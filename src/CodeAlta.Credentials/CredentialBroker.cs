using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Credentials;

/// <summary>Whether a secret can be given now, as the window and the agents see it.</summary>
public enum SecretAvailabilityState
{
    /// <summary>It is given at once.</summary>
    Ready,

    /// <summary>Its vault must be unlocked.</summary>
    Locked,

    /// <summary>The program of its vault has no account.</summary>
    SignInNeeded,

    /// <summary>The program of its vault is not installed.</summary>
    NotInstalled,

    /// <summary>It asks before use, and a use waits for the user's confirmation.</summary>
    WaitsForYou,

    /// <summary>The item or the field is gone from the vault.</summary>
    NotFound,

    /// <summary>It cannot be read for another reason its message says.</summary>
    Unavailable,
}

/// <summary>Whether a secret can be given now.</summary>
/// <param name="Name">The name of the secret.</param>
/// <param name="State">The state.</param>
/// <param name="Message">What the user reads; null for nothing more.</param>
/// <param name="Provider">The credential provider of a secret kept in a vault; null for a secret of this computer.</param>
public sealed record SecretAvailability(string Name, SecretAvailabilityState State, string? Message, string? Provider)
{
    /// <summary>
    /// Gets what the program of its vault wrote about the failure, for the user only: the window shows it, an agent or a log never
    /// gets it, because it can hold a value the masking does not know yet. Null for nothing.
    /// </summary>
    public string? Detail { get; init; }
}

/// <summary>A credential provider as the window and the agents see it: never a value.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Type">Its type.</param>
/// <param name="DisplayName">Its name.</param>
/// <param name="Status">Its state.</param>
public sealed record CredentialProviderView(string Id, string Type, string DisplayName, CredentialProviderStatus Status)
{
    /// <summary>Gets the name of its type, such as <c>1Password</c>; null for a type no running plugin brings.</summary>
    public string? TypeName { get; init; }

    /// <summary>Gets its icon; null for a key.</summary>
    public string? Icon { get; init; }

    /// <summary>Gets how long its values stay in memory.</summary>
    public string Cache { get; init; } = CredentialCacheDurations.UntilClosed;

    /// <summary>Gets whether CodeAlta Desktop asks its vault to unlock when it starts.</summary>
    public bool UnlockAtStart { get; init; } = true;

    /// <summary>Gets whether its unlock takes a password typed in the window.</summary>
    public bool UnlockAsksPassword { get; init; }

    /// <summary>Gets its settings as written: a secret setting holds its reference, never its value.</summary>
    public IReadOnlyDictionary<string, object?> Settings { get; init; } = new Dictionary<string, object?>();

    /// <summary>Gets the number of secrets bound to it.</summary>
    public int Secrets { get; init; }

    /// <summary>Gets the number of its secrets held in memory now.</summary>
    public int Held { get; init; }

    /// <summary>
    /// Gets whether CodeAlta unlocked its vault since it was last locked, so that it may hold what opens it (a session key, the
    /// password of a database) even when it holds no value: the window offers to lock it.
    /// </summary>
    public bool Unlocked { get; init; }

    /// <summary>Gets whether the user locked it in CodeAlta: nothing is read from it until it is unlocked again.</summary>
    public bool LockedByUser { get; init; }
}

/// <summary>A use of a secret, as the recent uses list it: never a value.</summary>
/// <param name="Name">The secret.</param>
/// <param name="Use">What asked for it; null when nothing named it.</param>
/// <param name="At">When.</param>
/// <param name="Outcome"><c>given</c>, <c>waits</c> (for a confirmation), <c>confirmed</c>, <c>refused</c> or <c>unavailable</c>.</param>
public sealed record SecretUseRecord(string Name, string? Use, DateTimeOffset At, string Outcome);

/// <summary>A use of a secret that asks before use, waiting for the user's confirmation.</summary>
/// <param name="Name">The secret.</param>
/// <param name="Use">What asked for it; null when nothing named it.</param>
/// <param name="At">When it first asked.</param>
public sealed record PendingSecretUse(string Name, string? Use, DateTimeOffset At);

/// <summary>Tells which secrets may be given differently now.</summary>
public sealed class CredentialsChangedEventArgs(IReadOnlyCollection<string> names) : EventArgs
{
    /// <summary>Gets the names of the secrets whose availability may have changed; empty when only the providers changed.</summary>
    public IReadOnlyCollection<string> Names { get; } = names;
}

/// <summary>What a broker is made with.</summary>
public sealed class CredentialBrokerOptions
{
    /// <summary>Gets the root of the profile, <c>~/.alta</c>: its <c>config.toml</c> names the providers, its <c>secrets</c> folder binds the secrets.</summary>
    public required string GlobalRoot { get; init; }

    /// <summary>Gets the masking of the host, told each value before anything gets it.</summary>
    public Action<string> Mask { get; init; } = static _ => { };

    /// <summary>Gets the types of providers; null for the built-in ones.</summary>
    public CredentialProviderTypes? Types { get; init; }

    /// <summary>Gets what verifies the user for the secrets that ask before use; null where nobody can be asked.</summary>
    public IUserPresenceVerifier? Verifier { get; init; }

    /// <summary>
    /// Gets whether the host has a user in front of a window, who can answer the prompt of a vault: the normal instance of CodeAlta
    /// Desktop. The developer instance, the command line and the node never prompt.
    /// </summary>
    public bool CanPrompt { get; init; }

    /// <summary>
    /// Gets whether the host is the developer instance of CodeAlta Desktop: a user is in front of its window, and it asks a vault to
    /// unlock only when the user lets it (<c>developer_instance = "unlock"</c> under <c>[secrets]</c>), on a click, never at start, so
    /// that a developer instance an agent starts shows no prompt by itself.
    /// </summary>
    public bool DeveloperInstance { get; init; }

    /// <summary>Gets the clock.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Gets the logger; null for none. It never gets a value.</summary>
    public Logger? Logger { get; init; }
}

/// <summary>
/// The credential broker of a host: it reads the secrets kept in vaults through their providers, holds the values in memory,
/// masked from their first read, and answers the resolution of <c>${secret:NAME}</c> at once from that memory
/// (<see cref="IBoundSecretSource"/>). It also keeps the confirmations of the secrets that ask before use. One per host; it
/// never writes a value to disk.
/// </summary>
/// <remarks>
/// Values are read when the host starts (without a prompt), when the user unlocks a vault, and when a secret is bound. They stay
/// until CodeAlta closes, the user locks the provider, or the duration of its memory ends. What resolves a reference never waits
/// for a vault: a value that is not held is an error that names the secret and its vault.
/// </remarks>
public sealed class CredentialBroker : IBoundSecretSource, IDisposable
{
    private const int MaximumUses = 200;
    private const string LockedByUserMessage = "Locked in CodeAlta: its values were forgotten, and nothing is read from it until it is unlocked.";
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(25);

    private readonly CredentialBrokerOptions _options;
    private readonly CredentialProviderTypes _types;
    private readonly CredentialProviderSettings _settings;
    private readonly LocalSecretStore _store;
    private readonly SignIns _signIns;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ProviderEntry> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Held> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SecretAvailability> _problems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Name, string Use), DateTimeOffset?> _grants = [];
    private readonly List<PendingSecretUse> _pending = [];
    private readonly LinkedList<SecretUseRecord> _uses = new();
    private readonly HashSet<string> _unlockedAtStart = new(StringComparer.Ordinal);
    // When an agent last had an unlock of a provider dismissed: it does not ask again for a while.
    private readonly Dictionary<string, DateTimeOffset> _agentUnlocks = new(StringComparer.Ordinal);
    // The providers whose values expired and are to be read again: kept apart from the values, so that a resolver that sees a value
    // expire first (and forgets it) does not take away the only sign that it must be read again.
    private readonly HashSet<string> _stale = new(StringComparer.Ordinal);
    private readonly ITimer? _expiry;
    private bool _refreshQueued;
    private IReadOnlyList<string> _configurationProblems = [];
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="CredentialBroker"/> class.</summary>
    /// <param name="options">What it is made with.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public CredentialBroker(CredentialBrokerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GlobalRoot);
        _options = options;
        _types = options.Types ?? new CredentialProviderTypes(new VaultCommandRunner());
        _settings = new CredentialProviderSettings(Path.Combine(options.GlobalRoot, "config.toml"));
        _store = new LocalSecretStore(options.GlobalRoot);
        _signIns = new SignIns(_store.Bindings, options.Time);
        // The values that expire (a duration of the memory, a one-time code) are read again, without a prompt.
        _expiry = options.Time.CreateTimer(_ => _ = RefreshExpiredAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Raised when secrets may be given differently: a vault unlocked or locked, a secret bound, a use confirmed.</summary>
    public event EventHandler<CredentialsChangedEventArgs>? Changed;

    /// <summary>Gets the types of providers.</summary>
    public CredentialProviderTypes Types => _types;

    /// <summary>Gets the settings of the providers in the user's configuration.</summary>
    public CredentialProviderSettings Settings => _settings;

    /// <summary>Gets the secrets of this machine and their bindings.</summary>
    public LocalSecretStore Store => _store;

    /// <summary>Gets the sign-ins of websites of this machine and the sites the user allowed them on.</summary>
    public SignIns SignIns => _signIns;

    /// <summary>Gets whether this host can show the prompt of a vault.</summary>
    public bool CanPrompt => _options.CanPrompt || _options.DeveloperInstance && _settings.DeveloperInstanceUnlocks();

    /// <summary>Gets whether this host is the developer instance of CodeAlta Desktop.</summary>
    public bool DeveloperInstance => _options.DeveloperInstance;

    /// <summary>Gets what verifies the user; null where nobody can be asked.</summary>
    public IUserPresenceVerifier? Verifier => _options.Verifier;

    /// <summary>Gets what is wrong with the tables of the configuration that were left out.</summary>
    public IReadOnlyList<string> ConfigurationProblems { get { lock (_gate) return _configurationProblems; } }

    /// <summary>Lists the providers.</summary>
    /// <returns>The providers, in the order of the configuration.</returns>
    public IReadOnlyList<CredentialProviderView> ListProviders()
    {
        Synchronize();
        var bindings = SafeBindings();
        lock (_gate)
        {
            return [.. _providers.Values.OrderBy(static entry => entry.Order).Select(entry => View(entry, bindings))];
        }
    }

    /// <summary>Gets one provider.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>The provider, or null when the configuration does not name it.</returns>
    public CredentialProviderView? GetProvider(string id) => ListProviders().FirstOrDefault(provider => provider.Id == id);

    /// <summary>Gets whether a secret can be given now, for a use, without reading anything.</summary>
    /// <param name="name">The secret.</param>
    /// <returns>The availability; null for a name this machine does not have.</returns>
    public SecretAvailability? GetAvailability(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        SecretBinding? binding;
        try
        {
            if (!_store.Bindings.TryGet(name, out binding)) binding = null;
        }
        catch (InvalidDataException exception)
        {
            return new SecretAvailability(name, SecretAvailabilityState.Unavailable, exception.Message, null);
        }

        lock (_gate)
        {
            var waits = _pending.Any(pending => string.Equals(pending.Name, name, StringComparison.OrdinalIgnoreCase));
            if (binding is null)
            {
                return waits ? new SecretAvailability(name, SecretAvailabilityState.WaitsForYou, $"{name} waits for your confirmation.", null) : new SecretAvailability(name, SecretAvailabilityState.Ready, null, null);
            }

            if (Fresh(binding) is not null)
                return waits ? new SecretAvailability(binding.Name, SecretAvailabilityState.WaitsForYou, $"{binding.Name} waits for your confirmation.", binding.Provider)
                    : new SecretAvailability(binding.Name, SecretAvailabilityState.Ready, null, binding.Provider);
            if (_problems.TryGetValue(binding.Name, out var problem)) return problem;
            if (_providers.TryGetValue(binding.Provider, out var entry) && entry.Status.State != CredentialProviderState.Ready) return FromStatus(binding, entry);
            if (!_providers.ContainsKey(binding.Provider))
                return new SecretAvailability(binding.Name, SecretAvailabilityState.Unavailable, $"{binding.Name} is kept by the credential provider '{binding.Provider}', which the configuration no longer names.", binding.Provider);
            return new SecretAvailability(binding.Name, SecretAvailabilityState.Locked, $"{binding.Name} is not read yet from {DisplayName(binding.Provider)}.", binding.Provider);
        }
    }

    /// <summary>Lists the uses of a secret that ask before use and wait for the user's confirmation.</summary>
    /// <returns>The uses, the oldest first.</returns>
    public IReadOnlyList<PendingSecretUse> ListPending()
    {
        lock (_gate) return [.. _pending];
    }

    /// <summary>Lists the recent uses of the secrets, the newest first: never a value.</summary>
    /// <returns>The uses.</returns>
    public IReadOnlyList<SecretUseRecord> ListUses()
    {
        lock (_gate) return [.. _uses];
    }

    /// <summary>
    /// Reads again what the configuration names: the providers, their states, and the secrets bound to the providers that can be
    /// read without a prompt. A host calls it when it starts and after the user changes the providers.
    /// </summary>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>A task that ends once every provider answered or ran out of time.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Synchronize();
        ProviderEntry[] entries;
        lock (_gate) entries = [.. _providers.Values];
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(entries.Select(entry => RefreshProviderAsync(entry, interactive: false, changed, cancellationToken))).ConfigureAwait(false);
        Raise(changed, always: true);
    }

    /// <summary>
    /// Asks the locked vaults whose providers say so to unlock, once per run of CodeAlta, in a host where the user can answer
    /// their prompt (<see cref="CredentialBrokerOptions.CanPrompt"/>). A provider that takes a password is left for the user.
    /// </summary>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>A task that ends once each vault answered.</returns>
    public async Task UnlockAtStartAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.CanPrompt) return;
        ProviderEntry[] entries;
        lock (_gate)
        {
            entries = [.. _providers.Values.Where(entry => entry.Definition.UnlockAtStart && entry.Provider is not null && entry.Contribution?.UnlockAsksPassword != true
                && entry.Status.State == CredentialProviderState.Locked && _unlockedAtStart.Add(entry.Definition.Id))];
        }

        // One vault at a time: two prompts at once would hide each other.
        foreach (var entry in entries)
        {
            try
            {
                await UnlockAsync(entry.Definition.Id, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is CredentialProviderException or InvalidOperationException)
            {
                _options.Logger?.Info($"The credential provider {entry.Definition.Id} was not unlocked at start: {exception.Message}");
            }
        }
    }

    /// <summary>Unlocks the vault of a provider, while the user is present, then reads the secrets bound to it.</summary>
    /// <param name="id">The id of the provider.</param>
    /// <param name="password">The password the user typed, for a provider that takes one; null otherwise.</param>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>The provider after it.</returns>
    /// <exception cref="KeyNotFoundException">The configuration names no such provider.</exception>
    /// <exception cref="InvalidOperationException">This host cannot show the prompt of a vault.</exception>
    public async Task<CredentialProviderView> UnlockAsync(string id, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Synchronize();
        ProviderEntry entry;
        lock (_gate)
        {
            entry = _providers.TryGetValue(id, out var found) ? found : throw new KeyNotFoundException($"No credential provider is named '{id}'.");
        }

        if (entry.Provider is not { } provider) return GetProvider(id)!;
        // Masked before the program of the vault gets it: it could write it back in an error.
        if (!string.IsNullOrEmpty(password)) _options.Mask(password);
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var generation = 0;
        try
        {
            lock (_gate)
            {
                // The unlock the user asks ends a lock of the user; a lock while it runs wins over it.
                entry.LockedByUser = false;
                generation = entry.Generation;
            }

            var status = await provider.UnlockAsync(new CredentialUnlockContext { Password = password, UserPresent = CanPrompt }, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (entry.Generation == generation)
                {
                    entry.Status = status;
                    entry.Unlocked = status.State == CredentialProviderState.Ready;
                }
            }
        }
        catch (CredentialProviderException exception)
        {
            lock (_gate)
            {
                if (entry.Generation == generation) entry.Status = new CredentialProviderStatus(CredentialProviderState.Locked, exception.Message) { Detail = exception.Detail };
            }
        }
        finally
        {
            RelockIfStale(entry, generation);
            entry.Gate.Release();
        }

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await RefreshProviderAsync(entry, interactive: true, changed, cancellationToken, statusKnown: true).ConfigureAwait(false);
        Raise(changed, always: true);
        return GetProvider(id)!;
    }

    /// <summary>
    /// Says whether an agent may ask a vault to unlock now: not while another unlock it asked runs, nor for a while after the user
    /// dismissed one, so that an agent cannot keep showing the prompt of a vault.
    /// </summary>
    /// <param name="id">The id of the provider.</param>
    /// <param name="pause">How long a dismissed unlock is not asked again.</param>
    /// <returns>True when it may; it must then call <see cref="EndAgentUnlock"/>.</returns>
    public bool TryBeginAgentUnlock(string id, TimeSpan pause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            var now = _options.Time.GetUtcNow();
            if (_agentUnlocks.TryGetValue(id, out var until) && until > now) return false;
            // While it runs, nothing else of an agent asks.
            _agentUnlocks[id] = now + TimeSpan.FromMinutes(3);
            _agentUnlockPause = pause;
            return true;
        }
    }

    /// <summary>Ends an unlock an agent asked: a dismissed one pauses the next ones.</summary>
    /// <param name="id">The id of the provider.</param>
    /// <param name="dismissed">Whether the vault is still locked after it.</param>
    public void EndAgentUnlock(string id, bool dismissed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (dismissed) _agentUnlocks[id] = _options.Time.GetUtcNow() + _agentUnlockPause;
            else _agentUnlocks.Remove(id);
        }
    }

    private TimeSpan _agentUnlockPause = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Forgets the values a provider read, and what its vault holds to read them; null forgets every provider. Nothing is read from
    /// it again until the user unlocks it (<see cref="UnlockAsync"/>): neither a check nor a read that ran when it was locked, whose
    /// value is dropped.
    /// </summary>
    /// <param name="id">The id of the provider; null for all.</param>
    public void Lock(string? id)
    {
        var forgotten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ProviderEntry[] entries;
        lock (_gate)
        {
            entries = [.. _providers.Values.Where(entry => id is null || entry.Definition.Id == id)];
            foreach (var entry in entries)
            {
                // What runs on it now started before the lock: its result is dropped (see Generation).
                entry.Generation++;
                entry.Unlocked = false;
                _stale.Remove(entry.Definition.Id);
                foreach (var held in _held.Where(pair => pair.Value.Provider == entry.Definition.Id).Select(static pair => pair.Key).ToArray())
                {
                    _held.Remove(held);
                    forgotten.Add(held);
                }

                if (entry.Provider is null) continue;
                entry.LockedByUser = true;
                if (entry.Status.State == CredentialProviderState.Ready)
                    entry.Status = CredentialProviderStatus.Locked(LockedByUserMessage) with { Account = entry.Status.Account, ProgramPath = entry.Status.ProgramPath };
            }
        }

        foreach (var entry in entries)
        {
            try
            {
                entry.Provider?.Lock();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _options.Logger?.Warn($"The credential provider {entry.Definition.Id} failed to lock: {exception.Message}");
            }
        }

        Raise(forgotten, always: true);
    }

    /// <summary>Checks a provider: its state now, without a prompt.</summary>
    /// <param name="id">The id of the provider.</param>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>The provider after the check.</returns>
    /// <exception cref="KeyNotFoundException">The configuration names no such provider.</exception>
    public async Task<CredentialProviderView> TestProviderAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Synchronize();
        ProviderEntry entry;
        lock (_gate) entry = _providers.TryGetValue(id, out var found) ? found : throw new KeyNotFoundException($"No credential provider is named '{id}'.");
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await RefreshProviderAsync(entry, interactive: false, changed, cancellationToken).ConfigureAwait(false);
        Raise(changed, always: true);
        return GetProvider(id)!;
    }

    /// <summary>Lists entries of the vault of a provider, for the picker: names, never values.</summary>
    /// <param name="id">The id of the provider.</param>
    /// <param name="parent">The entry to open; null for the top.</param>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>The entries.</returns>
    /// <exception cref="KeyNotFoundException">The configuration names no such provider.</exception>
    /// <exception cref="CredentialProviderException">The vault cannot be listed.</exception>
    /// <exception cref="NotSupportedException">The provider lists nothing.</exception>
    public async Task<CredentialBrowsePage> BrowseAsync(string id, string? parent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Synchronize();
        ProviderEntry entry;
        lock (_gate) entry = _providers.TryGetValue(id, out var found) ? found : throw new KeyNotFoundException($"No credential provider is named '{id}'.");
        var provider = entry.Provider ?? throw new CredentialProviderException(CredentialFailure.Failed, entry.Status.Message ?? "This provider cannot be used.");
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            return await provider.BrowseAsync(new CredentialBrowseRequest(parent), timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Reads the user name, the password and the one-time code of a sign-in now, without a prompt, for one fill of a page.
    /// Each value is told to the masking of the host before it is given, and none is kept: the caller disposes the result as
    /// soon as the values are typed.
    /// </summary>
    /// <param name="id">The id of the sign-in.</param>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>The values, for the fill only.</returns>
    /// <exception cref="KeyNotFoundException">No sign-in has that id.</exception>
    /// <exception cref="CredentialProviderException">
    /// The provider cannot read them: the vault is locked (<see cref="CredentialFailure.Locked"/>), the item is missing, or the
    /// provider is not configured. The message names no value.
    /// </exception>
    public Task<SignInValues> ReadSignInAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_signIns.TryGet(id, out var signIn)) throw new KeyNotFoundException($"No sign-in is named '{id}'.");
        return ReadSignInFieldsAsync(signIn, codeOnly: false, cancellationToken);
    }

    /// <summary>
    /// Reads the values of the sign-in the user approved for a site, as the card showed it, without a prompt, for one fill of a
    /// page: it is refused when the saved sign-in changed since (its sites, its references, its vault) or no longer lists the
    /// site, and the references read are those of the approved sign-in. Each value is told to the masking of the host before it
    /// is given, and none is kept.
    /// </summary>
    /// <param name="approved">The sign-in as the user approved it.</param>
    /// <param name="origin">The site it is typed on.</param>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>The values, for the fill only.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="KeyNotFoundException">The sign-in was removed.</exception>
    /// <exception cref="InvalidOperationException">The sign-in changed since it was approved, or no longer lists the site.</exception>
    /// <exception cref="CredentialProviderException">The provider cannot read them; the message names no value.</exception>
    public Task<SignInValues> ReadSignInAsync(SignIn approved, string origin, CancellationToken cancellationToken = default)
    {
        Approved(approved, origin);
        return ReadSignInFieldsAsync(approved, codeOnly: false, cancellationToken);
    }

    /// <summary>
    /// Reads the one-time code of the sign-in the user approved for a site now, without a prompt, as its field is reached: a code
    /// read before the user name and the password were sent may have expired. The same checks as
    /// <see cref="ReadSignInAsync(SignIn, string, CancellationToken)"/> apply, and the code is masked before it is given.
    /// </summary>
    /// <param name="approved">The sign-in as the user approved it.</param>
    /// <param name="origin">The site it is typed on.</param>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>The code and when it expires.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The sign-in has no one-time code.</exception>
    /// <exception cref="KeyNotFoundException">The sign-in was removed.</exception>
    /// <exception cref="InvalidOperationException">The sign-in changed since it was approved, or no longer lists the site.</exception>
    /// <exception cref="CredentialProviderException">The provider cannot read it; the message names no value.</exception>
    public async Task<SignInCode> ReadSignInCodeAsync(SignIn approved, string origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (approved.Totp is null) throw new ArgumentException($"The sign-in {approved.Label} has no one-time code.", nameof(approved));
        Approved(approved, origin);
        using var values = await ReadSignInFieldsAsync(approved, codeOnly: true, cancellationToken).ConfigureAwait(false);
        return new SignInCode(values.Totp!, values.TotpExpiresAt);
    }

    // Checks that the sign-in saved now is the one the user approved, and that it still lists the site.
    private void Approved(SignIn approved, string origin)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(origin);
        if (!_signIns.TryGet(approved.Id, out var current)) throw new KeyNotFoundException($"No sign-in is named '{approved.Id}'.");
        if (!SignIns.IsSameDefinition(current, approved)) throw new InvalidOperationException($"The sign-in {approved.Label} changed since the user approved it.");
        if (SignIns.OriginOf(origin) is not { } site || !current.Sites.Contains(site, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"The sign-in {approved.Label} is not for {origin}.");
        }
    }

    // Reads the fields of a sign-in through its provider, never with a prompt: all of them, or its one-time code only.
    private async Task<SignInValues> ReadSignInFieldsAsync(SignIn signIn, bool codeOnly, CancellationToken cancellationToken)
    {
        Synchronize();
        ProviderEntry entry;
        lock (_gate)
        {
            entry = _providers.GetValueOrDefault(signIn.Provider)
                ?? throw new CredentialProviderException(CredentialFailure.Failed, $"The sign-in {signIn.Label} names the credential provider '{signIn.Provider}', which is not configured.");
        }

        var provider = entry.Provider ?? throw new CredentialProviderException(CredentialFailure.Failed, entry.Status.Message ?? "This provider cannot be used.");
        ThrowIfLockedByUser(entry);
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var username = codeOnly ? null : await ReadFieldAsync(provider, signIn.Username, timeout.Token).ConfigureAwait(false);
            var password = codeOnly ? null : await ReadFieldAsync(provider, signIn.Password, timeout.Token).ConfigureAwait(false);
            var totp = signIn.Totp is null ? null : await ReadFieldAsync(provider, signIn.Totp, timeout.Token).ConfigureAwait(false);
            return new SignInValues(username?.Value ?? string.Empty, password?.Value ?? string.Empty, totp?.Value, totp?.ExpiresAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CredentialProviderException(CredentialFailure.Failed, $"The vault did not give the sign-in {signIn.Label} in time.");
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    // Reads one field of a sign-in, never with a prompt, and masks it before anything else gets it.
    private async Task<CredentialValue> ReadFieldAsync(ICredentialProvider provider, string reference, CancellationToken cancellationToken)
    {
        var value = await provider.ReadAsync(reference, interactive: false, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(value.Value)) throw new CredentialProviderException(CredentialFailure.NotFound, "The vault gave an empty value.");
        _options.Mask(value.Value);
        return value;
    }

    /// <summary>
    /// Reads a secret bound to a vault now, without a prompt, so that it can be given: after it was bound, or to check it. A
    /// value of this computer needs no reading.
    /// </summary>
    /// <param name="name">The secret.</param>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>Its availability after the reading.</returns>
    public async Task<SecretAvailability> ReadSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        Synchronize();
        if (!_store.Bindings.TryGet(name, out var binding)) return GetAvailability(name) ?? new SecretAvailability(name, SecretAvailabilityState.Ready, null, null);
        ProviderEntry? entry;
        lock (_gate) entry = _providers.GetValueOrDefault(binding.Provider);
        if (entry is null) return GetAvailability(name)!;
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var generation = 0;
        try
        {
            lock (_gate) generation = entry.Generation;
            await ReadBindingAsync(entry, binding, interactive: false, changed, generation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RelockIfStale(entry, generation);
            entry.Gate.Release();
        }

        Raise(changed, always: false);
        return GetAvailability(name)!;
    }

    /// <summary>
    /// Confirms a use of a secret that asks before use: the system verifies the user, and the confirmation lasts as long as the
    /// secret says. A use nobody asked yet can be confirmed ahead.
    /// </summary>
    /// <param name="name">The secret.</param>
    /// <param name="use">What will use it; null for a use nobody names.</param>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>What the verification ended with.</returns>
    public async Task<UserPresenceResult> ConfirmAsync(string name, string? use, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_options.Verifier is not { } verifier || !CanPrompt) return UserPresenceResult.Unavailable;
        var duration = SafeAskBeforeUse(name) ?? SecretAskDurations.FifteenMinutes;
        var result = await verifier.VerifyAsync($"Use the secret {name}{(use is null ? string.Empty : $" for the {use}")}?", cancellationToken).ConfigureAwait(false);
        if (result == UserPresenceResult.Verified)
        {
            var now = _options.Time.GetUtcNow();
            lock (_gate)
            {
                var time = SecretAskDurations.TimeOf(duration);
                _grants[(name.ToUpperInvariant(), use ?? string.Empty)] = time is null ? null : time == TimeSpan.Zero ? DateTimeOffset.MinValue : now + time.Value;
                _pending.RemoveAll(pending => string.Equals(pending.Name, name, StringComparison.OrdinalIgnoreCase) && pending.Use == use);
                Record(name, use, "confirmed");
            }

            Raise([name], always: true);
        }

        return result;
    }

    /// <summary>Forgets the waiting uses of a secret: the user declined them.</summary>
    /// <param name="name">The secret.</param>
    /// <param name="use">The use; null for all of them.</param>
    public void Decline(string name, string? use)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            _pending.RemoveAll(pending => string.Equals(pending.Name, name, StringComparison.OrdinalIgnoreCase) && (use is null || pending.Use == use));
            Record(name, use, "refused");
        }

        Raise([name], always: true);
    }

    /// <summary>Forgets the confirmations given for a secret, so that its next use asks again.</summary>
    /// <param name="name">The secret.</param>
    public void ForgetConfirmations(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            foreach (var key in _grants.Keys.Where(key => key.Name == name.ToUpperInvariant()).ToArray()) _grants.Remove(key);
        }
    }

    /// <inheritdoc />
    public BoundSecretRead ReadBound(string name, string? use)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_store.Bindings.TryGet(name, out var binding))
            return BoundSecretRead.Unavailable($"{name} is not kept in a vault.");
        lock (_gate)
        {
            if (Fresh(binding) is not { } held)
            {
                var availability = GetAvailabilityUnlocked(binding);
                Record(binding.Name, use, "unavailable");
                return BoundSecretRead.Unavailable(availability.Message ?? $"{binding.Name} cannot be read from its vault now.");
            }

            if (SafeAskBeforeUse(binding.Name) is not null && Authorize(binding.Name, use) is { } waits) return BoundSecretRead.Unavailable(waits);
            Record(binding.Name, use, "given");
            return BoundSecretRead.Of(held.Value);
        }
    }

    /// <inheritdoc />
    public string? AuthorizeUse(string name, string? use)
    {
        ArgumentNullException.ThrowIfNull(name);
        string? problem;
        lock (_gate)
        {
            problem = Authorize(name, use);
            if (problem is null) Record(name, use, "given");
        }

        return problem;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ProviderEntry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = [.. _providers.Values];
            _providers.Clear();
            _held.Clear();
        }

        _expiry?.Dispose();
        foreach (var entry in entries) Release(entry);
    }

    // Under the gate: whether a use of a secret that asks before use is confirmed. A use that is not waits, and is recorded.
    private string? Authorize(string name, string? use)
    {
        var key = (name.ToUpperInvariant(), use ?? string.Empty);
        var now = _options.Time.GetUtcNow();
        if (_grants.TryGetValue(key, out var until))
        {
            if (until is null || until > now) return null;
            // A confirmation of each use is used once.
            if (until == DateTimeOffset.MinValue)
            {
                _grants.Remove(key);
                return null;
            }

            _grants.Remove(key);
        }

        var known = _pending.Any(pending => string.Equals(pending.Name, name, StringComparison.OrdinalIgnoreCase) && pending.Use == use);
        if (!known)
        {
            _pending.Add(new PendingSecretUse(name, use, now));
            if (_pending.Count > 100) _pending.RemoveAt(0);
        }

        Record(name, use, "waits");
        if (!known) ThreadPool.UnsafeQueueUserWorkItem(static state => state.broker.Raise([state.name], always: true), (broker: this, name), preferLocal: false);
        return CanPrompt && _options.Verifier is not null
            ? $"{name} asks before use: confirm its use{(use is null ? string.Empty : $" for the {use}")} in Settings › Secrets of CodeAlta."
            : $"{name} asks before use, and nobody can confirm it here.";
    }

    private void Record(string name, string? use, string outcome)
    {
        // A use already recorded with the same outcome a moment ago is not repeated: a resolver reads a secret several times.
        if (_uses.First?.Value is { } last && last.Name == name && last.Use == use && last.Outcome == outcome && _options.Time.GetUtcNow() - last.At < TimeSpan.FromSeconds(5)) return;
        _uses.AddFirst(new SecretUseRecord(name, use, _options.Time.GetUtcNow(), outcome));
        while (_uses.Count > MaximumUses) _uses.RemoveLast();
    }

    private string? SafeAskBeforeUse(string name)
    {
        try
        {
            return _store.Bindings.AskBeforeUse(name);
        }
        catch (InvalidDataException)
        {
            // A damaged file asks: a secret is not given without the confirmation it may have asked for.
            return SecretAskDurations.FifteenMinutes;
        }
    }

    private IReadOnlyList<SecretBinding> SafeBindings()
    {
        try
        {
            return _store.Bindings.List();
        }
        catch (InvalidDataException exception)
        {
            _options.Logger?.Warn(exception.Message);
            return [];
        }
    }

    // Under the gate: the value held for a binding, when it is the one of the binding as it is now and has not expired. An expired
    // value is forgotten here, and its provider is marked to be read again at once.
    private Held? Fresh(SecretBinding binding)
    {
        if (!_held.TryGetValue(binding.Name, out var held)) return null;
        var expired = held.ExpiresAt is { } end && end <= _options.Time.GetUtcNow();
        if (held.Provider != binding.Provider || held.Reference != binding.Reference || expired)
        {
            _held.Remove(binding.Name);
            if (expired) MarkStale(held.Provider);
            return null;
        }

        return held;
    }

    // Under the gate: a value of a provider expired; it is read again without a prompt, soon.
    private void MarkStale(string provider)
    {
        _stale.Add(provider);
        if (_refreshQueued || _disposed) return;
        _refreshQueued = true;
        ThreadPool.UnsafeQueueUserWorkItem(static broker => _ = broker.RefreshExpiredAsync(), this, preferLocal: false);
    }

    // Under the gate of a provider, at the end of what ran on it: when the user locked it meanwhile, what that put back in the
    // provider (a session key an unlock answered) is forgotten again.
    private void RelockIfStale(ProviderEntry entry, int generation)
    {
        bool stale;
        lock (_gate) stale = entry.Generation != generation;
        if (!stale) return;
        try
        {
            entry.Provider?.Lock();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _options.Logger?.Warn($"The credential provider {entry.Definition.Id} failed to lock: {exception.GetType().Name}");
        }
    }

    private void ThrowIfLockedByUser(ProviderEntry entry)
    {
        lock (_gate)
        {
            if (entry.LockedByUser) throw new CredentialProviderException(CredentialFailure.Locked, LockedByUserMessage);
        }
    }

    private SecretAvailability GetAvailabilityUnlocked(SecretBinding binding)
    {
        if (_problems.TryGetValue(binding.Name, out var problem)) return problem;
        if (_providers.TryGetValue(binding.Provider, out var entry))
            return entry.Status.State == CredentialProviderState.Ready
                ? new SecretAvailability(binding.Name, SecretAvailabilityState.Locked, $"{binding.Name} is not read yet from {DisplayName(binding.Provider)}.", binding.Provider)
                : FromStatus(binding, entry);
        return new SecretAvailability(binding.Name, SecretAvailabilityState.Unavailable, $"{binding.Name} is kept by the credential provider '{binding.Provider}', which the configuration does not name.", binding.Provider);
    }

    private SecretAvailability FromStatus(SecretBinding binding, ProviderEntry entry)
    {
        var name = DisplayName(entry);
        SecretAvailability availability = entry.Status.State switch
        {
            CredentialProviderState.Locked => new(binding.Name, SecretAvailabilityState.Locked, $"{binding.Name} is kept in {name}, which is locked: unlock it in Settings › Secrets of CodeAlta.", binding.Provider),
            CredentialProviderState.SignInNeeded => new(binding.Name, SecretAvailabilityState.SignInNeeded, $"{binding.Name} is kept in {name}, which needs a sign-in: {entry.Status.Message}", binding.Provider),
            CredentialProviderState.NotInstalled => new(binding.Name, SecretAvailabilityState.NotInstalled, $"{binding.Name} is kept in {name}, whose program is not installed.", binding.Provider),
            CredentialProviderState.Ready => new(binding.Name, SecretAvailabilityState.Ready, null, binding.Provider),
            _ => new(binding.Name, SecretAvailabilityState.Unavailable, $"{binding.Name} is kept in {name}: {entry.Status.Message}", binding.Provider),
        };
        return availability with { Detail = entry.Status.Detail };
    }

    private string DisplayName(string provider)
        => _providers.TryGetValue(provider, out var entry) ? DisplayName(entry) : provider;

    private static string DisplayName(ProviderEntry entry)
    {
        var type = entry.Contribution?.DisplayName ?? entry.Definition.Type;
        var name = entry.Definition.DisplayName ?? entry.Definition.Id;
        return string.Equals(name, type, StringComparison.OrdinalIgnoreCase) ? type : $"{type} ({name})";
    }

    private CredentialProviderView View(ProviderEntry entry, IReadOnlyList<SecretBinding> bindings)
    {
        var bound = bindings.Where(binding => binding.Provider == entry.Definition.Id).ToArray();
        return new CredentialProviderView(entry.Definition.Id, entry.Definition.Type, entry.Definition.DisplayName ?? entry.Contribution?.DisplayName ?? entry.Definition.Id, entry.Status)
        {
            TypeName = entry.Contribution?.DisplayName,
            Icon = entry.Contribution?.Icon,
            Cache = entry.Definition.Cache,
            UnlockAtStart = entry.Definition.UnlockAtStart,
            UnlockAsksPassword = entry.Contribution?.UnlockAsksPassword == true,
            Settings = entry.Definition.Values,
            Secrets = bound.Length,
            Held = bound.Count(binding => Fresh(binding) is not null),
            Unlocked = entry.Unlocked && entry.Status.State == CredentialProviderState.Ready,
            LockedByUser = entry.LockedByUser,
        };
    }

    // Makes the providers those the configuration names now: a new one is created, a changed one created again, a removed one released.
    // A provider is also created again when what opens its vault changes: a new version of a local secret a setting names (a token
    // rotated), or the confirmation its creation waited for.
    private void Synchronize()
    {
        var definitions = _settings.Load(out var problems);
        var versions = LocalVersions(definitions);
        List<ProviderEntry> released = [];
        List<ProviderEntry> reopened = [];
        lock (_gate)
        {
            if (_disposed) return;
            _configurationProblems = problems;
            var order = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                seen.Add(definition.Id);
                var contribution = _types.Find(definition.Type);
                var signature = definition.Signature + "\n" + (contribution is null ? "?" : RuntimeHelpersIdentity(contribution));
                var opener = Opener(definition, contribution, versions);
                if (_providers.TryGetValue(definition.Id, out var existing) && existing.Signature == signature && existing.Opener == opener
                    && !(existing.WaitsFor is { } waits && !IsPending(waits.Name, waits.Use)))
                {
                    existing.Order = order++;
                    existing.Definition = definition;
                    continue;
                }

                if (existing is not null)
                {
                    released.Add(existing);
                    // What still runs on the old provider is dropped.
                    existing.Generation++;
                }

                // A lock of the user stays when the provider is created again: only an unlock ends it.
                var entry = new ProviderEntry(definition, contribution, signature) { Order = order++, Opener = opener, LockedByUser = existing?.LockedByUser == true };
                Create(entry);
                _providers[definition.Id] = entry;
                _stale.Remove(definition.Id);
                foreach (var held in _held.Where(pair => pair.Value.Provider == definition.Id).Select(static pair => pair.Key).ToArray()) _held.Remove(held);
                // Its configuration is the same: nothing else checks it, so it is read again here.
                if (existing is not null && existing.Signature == signature && entry.Provider is not null) reopened.Add(entry);
            }

            foreach (var id in _providers.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                var gone = _providers[id];
                gone.Generation++;
                released.Add(gone);
                _providers.Remove(id);
                _stale.Remove(id);
                foreach (var held in _held.Where(pair => pair.Value.Provider == id).Select(static pair => pair.Key).ToArray()) _held.Remove(held);
            }
        }

        foreach (var entry in released) Release(entry);
        foreach (var entry in reopened) ThreadPool.UnsafeQueueUserWorkItem(static state => _ = state.broker.RefreshReopenedAsync(state.entry), (broker: this, entry), preferLocal: false);
    }

    private async Task RefreshReopenedAsync(ProviderEntry entry)
    {
        try
        {
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await RefreshProviderAsync(entry, interactive: false, changed, CancellationToken.None).ConfigureAwait(false);
            Raise(changed, always: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _options.Logger?.Warn($"The credential provider {entry.Definition.Id} could not be read again: {exception.GetType().Name}");
        }
    }

    // The versions of the local secrets the settings of the providers name, read outside the gate.
    private Dictionary<string, int> LocalVersions(IReadOnlyList<CredentialProviderDefinition> definitions)
    {
        var versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var named = definitions.SelectMany(static definition => definition.Values.Values).OfType<string>()
            .Any(static text => SecretReferences.TryParseLone(text.Trim(), out var parsed) && parsed.Kind == SecretReferenceKind.Secret);
        if (!named) return versions;
        try
        {
            foreach (var secret in _store.ListSecrets().Where(static secret => secret.Provider is null)) versions[secret.Name] = secret.Version;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or SecretStoreUnavailableException)
        {
            // The creation of the provider says why it cannot read them.
        }

        return versions;
    }

    // What opens the vault of a provider, beside its configuration: the versions of the local secrets its secret settings name.
    private static string Opener(CredentialProviderDefinition definition, PluginCredentialProviderContribution? contribution, Dictionary<string, int> versions)
    {
        if (contribution is null) return string.Empty;
        var text = new System.Text.StringBuilder();
        foreach (var setting in contribution.Settings.Where(static setting => setting.Kind == PluginCredentialProviderSettingKind.Secret))
        {
            if (!definition.Values.TryGetValue(setting.Key, out var written) || written is not string reference
                || !SecretReferences.TryParseLone(reference.Trim(), out var parsed) || parsed.Kind != SecretReferenceKind.Secret) continue;
            text.Append(parsed.Name).Append('=').Append(versions.TryGetValue(parsed.Name, out var version) ? version.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-").Append('\n');
        }

        return text.ToString();
    }

    // Under the gate: whether a use of a secret waits for the user's confirmation.
    private bool IsPending(string name, string use)
        => _pending.Any(pending => string.Equals(pending.Name, name, StringComparison.OrdinalIgnoreCase) && pending.Use == use);

    private static string RuntimeHelpersIdentity(PluginCredentialProviderContribution contribution)
        => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(contribution).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Under the gate: creates the provider of an entry, or says why it cannot be.
    private void Create(ProviderEntry entry)
    {
        if (entry.Contribution is not { } contribution)
        {
            entry.Status = CredentialProviderStatus.Unavailable($"No credential provider of type '{entry.Definition.Type}' is known: a plugin that brings it is not running.");
            return;
        }

        var values = new Dictionary<string, object?>(entry.Definition.Values, StringComparer.Ordinal);
        foreach (var setting in contribution.Settings)
        {
            if (setting.Required && (!values.TryGetValue(setting.Key, out var given) || given is null or "" || given is IEnumerable<object?> list && !list.Any()))
            {
                entry.Status = CredentialProviderStatus.Unavailable($"Its setting '{setting.Label}' is required.");
                return;
            }

            if (setting.Kind != PluginCredentialProviderSettingKind.Secret || !values.TryGetValue(setting.Key, out var written) || written is not string reference || string.IsNullOrWhiteSpace(reference)) continue;
            // A token of a vault is a secret of this computer, never one of a vault: a vault is not opened with a key it keeps.
            if (!SecretReferences.TryParseLone(reference.Trim(), out var parsed) || parsed.Kind != SecretReferenceKind.Secret)
            {
                entry.Status = CredentialProviderStatus.Unavailable($"Its setting '{setting.Label}' is a reference to a secret of this computer, such as ${{secret:NAME}}.");
                return;
            }

            try
            {
                if (_store.Bindings.TryGet(parsed.Name, out _))
                {
                    entry.Status = CredentialProviderStatus.Unavailable($"Its setting '{setting.Label}' names {parsed.Name}, which is kept in a vault: it must be a secret of this computer.");
                    return;
                }

                // A token that asks before use is given to the provider as to any use, which the user confirms in Settings; the
                // provider is created again once it is confirmed.
                var use = $"credential provider {entry.Definition.Id}";
                var asks = SafeAskBeforeUse(parsed.Name) is not null;
                if (asks && IsPending(parsed.Name, use))
                {
                    Waits(entry, setting, parsed.Name, use);
                    return;
                }

                var store = asks ? new LocalSecretStore(_options.GlobalRoot) { BoundSecrets = this, Use = use } : _store;
                string? value;
                try
                {
                    if (!store.TryGetSecret(parsed.Name, out value))
                    {
                        entry.Status = CredentialProviderStatus.Unavailable($"Its setting '{setting.Label}' names {parsed.Name}, which is not set on this computer.");
                        return;
                    }
                }
                catch (SecretStoreUnavailableException) when (asks && IsPending(parsed.Name, use))
                {
                    Waits(entry, setting, parsed.Name, use);
                    return;
                }

                _options.Mask(value);
                values[setting.Key] = value;
            }
            catch (Exception exception) when (exception is SecretStoreUnavailableException or InvalidDataException or IOException)
            {
                entry.Status = CredentialProviderStatus.Unavailable($"Its setting '{setting.Label}' cannot be read: {exception.Message}");
                return;
            }
        }

        try
        {
            entry.Provider = contribution.Create(new PluginCredentialProviderContext(entry.Definition.Id, values) { Logger = _options.Logger, Mask = _options.Mask });
            entry.Status = new CredentialProviderStatus(CredentialProviderState.Locked, "Not checked yet.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            entry.Status = CredentialProviderStatus.Unavailable($"It cannot be created: {exception.Message}");
        }
    }

    // Under the gate: the creation of a provider waits for the confirmation of a use of the local secret a setting names.
    private void Waits(ProviderEntry entry, PluginCredentialProviderSetting setting, string name, string use)
    {
        entry.WaitsFor = (name, use);
        entry.Status = CredentialProviderStatus.Unavailable(CanPrompt && _options.Verifier is not null
            ? $"Its setting '{setting.Label}' names {name}, which asks before use: confirm its use for the {use} in Settings › Secrets of CodeAlta."
            : $"Its setting '{setting.Label}' names {name}, which asks before use, and nobody can confirm it here.");
    }

    private void Release(ProviderEntry entry)
    {
        try
        {
            entry.Provider?.Lock();
            (entry.Provider as IDisposable)?.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _options.Logger?.Warn($"The credential provider {entry.Definition.Id} failed to stop: {exception.Message}");
        }
    }

    private async Task RefreshProviderAsync(ProviderEntry entry, bool interactive, HashSet<string> changed, CancellationToken cancellationToken, bool statusKnown = false)
    {
        if (entry.Provider is not { } provider) return;
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var generation = 0;
        try
        {
            lock (_gate)
            {
                // A provider the user locked is neither checked nor read until the user unlocks it: a check would find a vault that
                // needs nobody (a keychain, pass) ready again.
                if (entry.LockedByUser) return;
                generation = entry.Generation;
            }

            if (!statusKnown)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(StatusTimeout);
                CredentialProviderStatus status;
                try
                {
                    status = await provider.GetStatusAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    status = CredentialProviderStatus.Unavailable("It did not answer in time.");
                }
                catch (CredentialProviderException exception)
                {
                    status = CredentialProviderStatus.Unavailable(exception.Message) with { Detail = exception.Detail };
                }
                catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    status = CredentialProviderStatus.Unavailable(exception.Message);
                }

                lock (_gate)
                {
                    if (entry.Generation != generation) return;
                    entry.Status = status;
                    if (status.State != CredentialProviderState.Ready) entry.Unlocked = false;
                }
            }

            CredentialProviderStatus now;
            lock (_gate) now = entry.Status;
            var bindings = SafeBindings().Where(binding => binding.Provider == entry.Definition.Id).ToArray();
            foreach (var binding in bindings)
            {
                if (now.State != CredentialProviderState.Ready)
                {
                    lock (_gate)
                    {
                        _problems.Remove(binding.Name);
                        changed.Add(binding.Name);
                    }

                    continue;
                }

                await ReadBindingAsync(entry, binding, interactive, changed, generation, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            RelockIfStale(entry, generation);
            entry.Gate.Release();
        }
    }

    // Under the gate of the provider: reads one binding, unless its value is held already. What it read is kept only when the
    // provider was not locked (nor created again) since the generation the caller started with.
    private async Task ReadBindingAsync(ProviderEntry entry, SecretBinding binding, bool interactive, HashSet<string> changed, int generation, CancellationToken cancellationToken)
    {
        if (entry.Provider is not { } provider) return;
        lock (_gate)
        {
            if (entry.LockedByUser || entry.Generation != generation || Fresh(binding) is not null) return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(interactive ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(40));
        try
        {
            var value = await provider.ReadAsync(binding.Reference, interactive, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value.Value)) throw new CredentialProviderException(CredentialFailure.NotFound, "The vault gave an empty value.");
            // Masked before anything gets it, for as long as the host runs.
            _options.Mask(value.Value);
            var now = _options.Time.GetUtcNow();
            var memory = CredentialCacheDurations.TimeOf(entry.Definition.Cache);
            var expires = value.ExpiresAt;
            if (memory is { } duration && (expires is null || now + duration < expires)) expires = now + duration;
            lock (_gate)
            {
                // The user locked the provider while it read: the value is not kept (it stays masked).
                if (entry.Generation != generation || entry.LockedByUser) return;
                _held[binding.Name] = new Held(value.Value, binding.Provider, binding.Reference, expires);
                _problems.Remove(binding.Name);
                changed.Add(binding.Name);
            }
        }
        catch (CredentialProviderException exception)
        {
            var state = exception.Failure switch
            {
                CredentialFailure.Locked => SecretAvailabilityState.Locked,
                CredentialFailure.SignInNeeded => SecretAvailabilityState.SignInNeeded,
                CredentialFailure.NotInstalled => SecretAvailabilityState.NotInstalled,
                CredentialFailure.NotFound => SecretAvailabilityState.NotFound,
                _ => SecretAvailabilityState.Unavailable,
            };
            lock (_gate)
            {
                if (entry.Generation != generation) return;
                if (exception.Failure == CredentialFailure.Locked && entry.Status.State == CredentialProviderState.Ready)
                {
                    entry.Status = CredentialProviderStatus.Locked(exception.Message) with { Account = entry.Status.Account, ProgramPath = entry.Status.ProgramPath, Detail = exception.Detail };
                    entry.Unlocked = false;
                }

                _problems[binding.Name] = new SecretAvailability(binding.Name, state,
                    state == SecretAvailabilityState.NotFound ? $"{binding.Name} is not found in {DisplayName(entry)}: {exception.Message}" : $"{binding.Name}: {exception.Message}", binding.Provider)
                {
                    Detail = exception.Detail,
                };
                changed.Add(binding.Name);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (entry.Generation != generation) return;
                _problems[binding.Name] = new SecretAvailability(binding.Name, SecretAvailabilityState.Unavailable, $"{binding.Name}: {DisplayName(entry)} did not answer in time.", binding.Provider);
                changed.Add(binding.Name);
            }
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            lock (_gate)
            {
                if (entry.Generation != generation) return;
                _problems[binding.Name] = new SecretAvailability(binding.Name, SecretAvailabilityState.Unavailable, $"{binding.Name}: {exception.Message}", binding.Provider);
                changed.Add(binding.Name);
            }
        }
    }

    private async Task RefreshExpiredAsync()
    {
        try
        {
            var bindings = SafeBindings();
            ProviderEntry[] entries;
            lock (_gate)
            {
                _refreshQueued = false;
                if (_disposed) return;
                var now = _options.Time.GetUtcNow();
                // The values that expired since, and the providers a resolver or a check already saw expire (Fresh).
                foreach (var (name, held) in _held.Where(pair => pair.Value.ExpiresAt is { } end && end <= now).ToArray())
                {
                    _held.Remove(name);
                    _stale.Add(held.Provider);
                }

                entries = [.. _providers.Values.Where(entry => _stale.Contains(entry.Definition.Id) && !entry.LockedByUser)];
                _stale.Clear();
            }

            if (entries.Length == 0) return;
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries) await RefreshProviderAsync(entry, interactive: false, changed, CancellationToken.None).ConfigureAwait(false);
            Raise(changed, always: false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _options.Logger?.Warn($"The values of the vaults could not be read again: {exception.Message}");
        }
    }

    private void Raise(IReadOnlyCollection<string> names, bool always)
    {
        if (!always && names.Count == 0) return;
        try
        {
            Changed?.Invoke(this, new CredentialsChangedEventArgs([.. names]));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _options.Logger?.Warn($"A listener of the credentials failed: {exception.Message}");
        }
    }

    private sealed record Held(string Value, string Provider, string Reference, DateTimeOffset? ExpiresAt)
    {
        public override string ToString() => $"Held({Provider}, ***)";
    }

    private sealed class ProviderEntry(CredentialProviderDefinition definition, PluginCredentialProviderContribution? contribution, string signature)
    {
        public CredentialProviderDefinition Definition { get; set; } = definition;

        public PluginCredentialProviderContribution? Contribution { get; } = contribution;

        public string Signature { get; } = signature;

        public ICredentialProvider? Provider { get; set; }

        public CredentialProviderStatus Status { get; set; } = CredentialProviderStatus.Locked("Not checked yet.");

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Order { get; set; }

        // Changes when the user locks it, or when it is replaced: what started on an older generation drops its result.
        public int Generation { get; set; }

        // The user locked it: nothing is read from it until the user unlocks it.
        public bool LockedByUser { get; set; }

        // An unlock in CodeAlta made it ready: it may hold what opens its vault.
        public bool Unlocked { get; set; }

        // What opens its vault beside its configuration (the versions of the local secrets its settings name).
        public string Opener { get; init; } = string.Empty;

        // The use of a local secret its creation waits the confirmation of; null when it waits for none.
        public (string Name, string Use)? WaitsFor { get; set; }
    }
}
