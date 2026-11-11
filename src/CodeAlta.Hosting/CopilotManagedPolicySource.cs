using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Copilot;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting;

/// <summary>Where the reading of the managed settings of the Copilot providers stands, for the page of a provider.</summary>
/// <param name="State">
/// <c>none</c> (no Copilot provider, nothing is downloaded), <c>installing</c> (the runtime is being downloaded),
/// <c>reading</c>, <c>ready</c> or <c>failed</c>.
/// </param>
/// <param name="Message">What it does or what failed, as a sentence; null when there is nothing to say.</param>
/// <param name="Received">The bytes of the runtime downloaded so far, while it is installed.</param>
/// <param name="Total">The bytes of the runtime, while it is installed.</param>
/// <param name="ReadAt">When the settings were read last; null when they never were.</param>
public sealed record CopilotManagedPolicyStatus(string State, string? Message = null, long? Received = null, long? Total = null, DateTimeOffset? ReadAt = null)
{
    /// <summary>Gets the status of a host without a Copilot provider.</summary>
    public static CopilotManagedPolicyStatus None { get; } = new("none");
}

/// <summary>
/// Reads the enterprise managed settings that apply to the GitHub accounts of the Copilot providers of this computer,
/// and gives each its policy (<see cref="ManagedPolicyState"/>), which then applies to every session whatever its
/// provider. The Copilot runtime is downloaded the first time a Copilot provider exists, never before; the settings are
/// read again about every hour, when the providers change, and on <see cref="RefreshAsync"/>. The last policy of each
/// provider is kept on disk and applies from the next start, offline included, until a newer one replaces it.
/// </summary>
public sealed class CopilotManagedPolicySource : IAsyncDisposable
{
    /// <summary>The source the policies name, for people.</summary>
    public const string SourceName = "GitHub Copilot";

    /// <summary>How often the settings are read again.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private const string KeyPrefix = "copilot:";
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);

    private readonly ManagedPolicyState _state;
    private readonly string _globalRoot;
    private readonly Func<IReadOnlyList<CodeAltaProviderDocument>> _providers;
    private readonly CopilotRuntimeInstaller _installer;
    private readonly Func<string, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<CopilotManagedSettings>>> _read;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _statusGate = new();
    private readonly HashSet<string> _freshThisStart = new(StringComparer.Ordinal);
    private CopilotManagedPolicyStatus _status = CopilotManagedPolicyStatus.None;
    private Task? _loop;
    private TaskCompletionSource _soon = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Initializes a new instance of the <see cref="CopilotManagedPolicySource"/> class, on the network.</summary>
    /// <param name="state">The policies of the host, where each provider's goes.</param>
    /// <param name="globalRoot">The global root of CodeAlta (<c>~/.alta</c>), which holds the sign-ins and the cache.</param>
    /// <param name="providers">Gives the providers configured now.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="globalRoot"/> is blank.</exception>
    public CopilotManagedPolicySource(ManagedPolicyState state, string globalRoot, Func<IReadOnlyList<CodeAltaProviderDocument>> providers)
        : this(state, globalRoot, providers, new CopilotRuntimeInstaller(Path.Combine(globalRoot, "cache", "copilot-runtime")),
            static (executable, home, tokens, cancellationToken) => new CopilotManagedSettingsReader(executable, home).ReadAsync(tokens, cancellationToken), TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CopilotManagedPolicySource"/> class, for tests.</summary>
    /// <param name="state">The policies of the host.</param>
    /// <param name="globalRoot">The global root of CodeAlta.</param>
    /// <param name="providers">Gives the providers configured now.</param>
    /// <param name="installer">Downloads the runtime.</param>
    /// <param name="read">Reads the settings of tokens with the runtime: its entry point, its home, the tokens.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="globalRoot"/> is blank.</exception>
    public CopilotManagedPolicySource(ManagedPolicyState state, string globalRoot, Func<IReadOnlyList<CodeAltaProviderDocument>> providers,
        CopilotRuntimeInstaller installer, Func<string, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<CopilotManagedSettings>>> read, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRoot);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(time);
        _state = state;
        _globalRoot = Path.GetFullPath(globalRoot);
        _providers = providers;
        _installer = installer;
        _read = read;
        _time = time;
    }

    /// <summary>Raised when <see cref="Status"/> changed.</summary>
    public event Action<CopilotManagedPolicyStatus>? StatusChanged;

    /// <summary>Gets where the reading stands.</summary>
    public CopilotManagedPolicyStatus Status
    {
        get
        {
            lock (_statusGate) return _status;
        }
    }

    /// <summary>
    /// Applies the policies kept from the last start, then reads the settings in the background: after
    /// <paramref name="firstDelay"/>, then every <see cref="RefreshInterval"/> and after each <see cref="RefreshSoon"/>.
    /// </summary>
    /// <param name="firstDelay">How long after the start the first reading begins, off the start-up of the host.</param>
    /// <exception cref="InvalidOperationException">It was started already.</exception>
    public void Start(TimeSpan firstDelay)
    {
        if (_loop is not null) throw new InvalidOperationException("The source of the Copilot policies was started already.");
        ApplyKept();
        _loop = Task.Run(() => LoopAsync(firstDelay, _stop.Token));
    }

    /// <summary>Asks for a reading soon, as when a provider was added, signed in or removed.</summary>
    public void RefreshSoon() => Volatile.Read(ref _soon).TrySetResult();

    /// <summary>Reads the settings now, one reading at a time.</summary>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The status once it ended; a failure is in it, not thrown.</returns>
    /// <exception cref="OperationCanceledException">The reading was canceled.</exception>
    public async Task<CopilotManagedPolicyStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetStatus(Status with { State = "failed", Message = Describe(exception), Received = null, Total = null });
            MarkMissingFresh();
        }
        finally
        {
            _gate.Release();
        }

        return Status;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped with the host.
            }
        }

        _installer.Dispose();
        _stop.Dispose();
    }

    /// <summary>
    /// Maps the settings of a provider's account to the policy CodeAlta enforces.
    /// </summary>
    /// <param name="settings">The settings the runtime resolved.</param>
    /// <param name="account">The provider, for people.</param>
    /// <param name="readAt">When they were read.</param>
    /// <returns>The policy; empty when the settings ask nothing CodeAlta enforces.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public static ManagedPolicy Map(CopilotManagedSettings settings, string? account, DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var policy = new ManagedPolicy
        {
            Source = SourceName,
            Account = account,
            BypassPermissionsDisabled = settings.BypassPermissionsDisabled,
            AssistedPermissionsDisabled = settings.AssistedPermissionsDisabled,
            // Copilot's assisted mode has no counterpart in every provider: a new session asks.
            DefaultPermissionMode = settings.DefaultMode switch
            {
                "manual" or "assisted" => "default",
                "allow-all" when !settings.BypassPermissionsDisabled => "bypassPermissions",
                _ => null,
            },
            SandboxRequired = settings.SandboxEnabled,
            RunOutsideSandboxDisallowed = !settings.SandboxBypassAllowed,
            SupportUrl = settings.SupportUrl,
            ManagedKeys = settings.ManagedKeys,
            ReadAt = readAt,
        };
        return policy.IsEmpty ? ManagedPolicy.None : policy;
    }

    private async Task LoopAsync(TimeSpan firstDelay, CancellationToken cancellationToken)
    {
        var wait = firstDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var soon = Volatile.Read(ref _soon);
            await Task.WhenAny(Task.Delay(wait, _time, cancellationToken), soon.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (soon.Task.IsCompleted) Interlocked.CompareExchange(ref _soon, new(TaskCreationOptions.RunContinuationsAsynchronously), soon);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            wait = RefreshInterval;
        }
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var providers = _providers().Where(static provider => provider.ProviderType == "copilot" && !string.IsNullOrWhiteSpace(provider.ProviderKey)).ToArray();
        foreach (var key in _state.Policies.Keys.Where(static key => key.StartsWith(KeyPrefix, StringComparison.Ordinal)))
        {
            // A provider removed since takes its policy with it; a computer without a Copilot provider reads nothing.
            if (!providers.Any(provider => KeyPrefix + provider.ProviderKey == key)) _state.Remove(key);
        }

        if (providers.Length == 0)
        {
            SetStatus(CopilotManagedPolicyStatus.None);
            return;
        }

        var signedIn = new List<(string Key, string Token)>();
        foreach (var provider in providers)
        {
            if (await GitHubTokenAsync(provider, cancellationToken).ConfigureAwait(false) is { } token) signedIn.Add((provider.ProviderKey, token));
        }

        var progress = new Progress(this);
        var installation = await _installer.EnsureInstalledAsync(progress, cancellationToken).ConfigureAwait(false);
        SetStatus(Status with { State = "reading", Message = "Reading the policy of your organization…", Received = null, Total = null });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        var settings = await _read(installation.Executable, Path.Combine(_installer.Root, "home"), [.. signedIn.Select(static item => item.Token)], timeout.Token).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        if (signedIn.Count == 0)
        {
            // No provider is signed in: the policy of the computer applies to them all.
            Apply(providers.Select(static provider => provider.ProviderKey), settings.Count > 0 ? settings[0] : new CopilotManagedSettings(), now);
        }
        else
        {
            for (var index = 0; index < signedIn.Count && index < settings.Count; index++)
                Apply([signedIn[index].Key], settings[index], now);
        }

        var diagnostics = settings.SelectMany(static item => item.Diagnostics).Distinct(StringComparer.Ordinal).ToArray();
        SetStatus(new CopilotManagedPolicyStatus("ready", diagnostics.Length == 0 ? null : string.Join(" ", diagnostics), ReadAt: now));
    }

    private void Apply(IEnumerable<string> providerKeys, CopilotManagedSettings settings, DateTimeOffset now)
    {
        foreach (var providerKey in providerKeys)
        {
            var policy = Map(settings, providerKey, now);
            lock (_freshThisStart) _freshThisStart.Add(providerKey);
            _state.Set(KeyPrefix + providerKey, policy);
            Keep(providerKey, policy, settings.ForceRemoteSettingsRefresh);
        }
    }

    // The sign-in of a provider: the token of its environment variable, else the GitHub token CodeAlta keeps for it.
    // A provider signed in with a Copilot token has no GitHub account to read a policy for.
    private async Task<string?> GitHubTokenAsync(CodeAltaProviderDocument provider, CancellationToken cancellationToken)
    {
        var source = Text(provider.AuthSource) ?? CopilotDirectAuthSources.GitHubDeviceFlow;
        if (source == CopilotDirectAuthSources.CopilotTokenEnvironment) return null;
        if (source == CopilotDirectAuthSources.GitHubTokenEnvironment)
            return Text(provider.GitHubTokenEnv) is { } variable ? Text(Environment.GetEnvironmentVariable(variable)) : null;
        var cache = await new CopilotDirectCredentialStore(_globalRoot, provider.ProviderKey).ReadAsync(cancellationToken).ConfigureAwait(false);
        return Text(cache?.GitHubToken);
    }

    // The policies of the last start apply until a reading replaces them; one whose source requires a reading at each
    // start keeps sessions from sending until it is read again.
    private void ApplyKept()
    {
        var folder = KeptFolder;
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (ReadKept(path) is not { } kept) continue;
            _state.Set(KeyPrefix + kept.ProviderKey, kept.ForceRefresh ? kept.Policy with { FreshPolicyMissing = true } : kept.Policy);
        }
    }

    // After a failed reading, the policies that require a reading at each start and were not read since keep blocking.
    private void MarkMissingFresh()
    {
        var folder = KeptFolder;
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (ReadKept(path) is not { ForceRefresh: true } kept) continue;
            lock (_freshThisStart)
            {
                if (_freshThisStart.Contains(kept.ProviderKey)) continue;
            }

            _state.Set(KeyPrefix + kept.ProviderKey, kept.Policy with { FreshPolicyMissing = true });
        }
    }

    private string KeptFolder => Path.Combine(_globalRoot, "auth", "copilot-policy");

    private void Keep(string providerKey, ManagedPolicy policy, bool forceRefresh)
    {
        var path = Path.Combine(KeptFolder, FileName(providerKey));
        if (policy.IsEmpty && !forceRefresh)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(KeptFolder);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("provider", providerKey);
            writer.WriteBoolean("forceRemoteSettingsRefresh", forceRefresh);
            writer.WriteBoolean("bypassPermissionsDisabled", policy.BypassPermissionsDisabled);
            writer.WriteBoolean("assistedPermissionsDisabled", policy.AssistedPermissionsDisabled);
            if (policy.DefaultPermissionMode is { } mode) writer.WriteString("defaultPermissionMode", mode);
            writer.WriteBoolean("sandboxRequired", policy.SandboxRequired);
            writer.WriteBoolean("runOutsideSandboxDisallowed", policy.RunOutsideSandboxDisallowed);
            if (policy.SupportUrl is { } url) writer.WriteString("supportUrl", url);
            writer.WriteStartArray("managedKeys");
            foreach (var key in policy.ManagedKeys) writer.WriteStringValue(key);
            writer.WriteEndArray();
            if (policy.ReadAt is { } at) writer.WriteString("readAt", at);
            writer.WriteEndObject();
        }

        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, stream.ToArray());
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Kept(string ProviderKey, bool ForceRefresh, ManagedPolicy Policy);

    private static Kept? ReadKept(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            string? String(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            bool Bool(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            if (Text(String("provider")) is not { } provider) return null;
            return new Kept(provider, Bool("forceRemoteSettingsRefresh"), new ManagedPolicy
            {
                Source = SourceName,
                Account = provider,
                BypassPermissionsDisabled = Bool("bypassPermissionsDisabled"),
                AssistedPermissionsDisabled = Bool("assistedPermissionsDisabled"),
                DefaultPermissionMode = Text(String("defaultPermissionMode")),
                SandboxRequired = Bool("sandboxRequired"),
                RunOutsideSandboxDisallowed = Bool("runOutsideSandboxDisallowed"),
                SupportUrl = Text(String("supportUrl")),
                ManagedKeys = root.TryGetProperty("managedKeys", out var keys) && keys.ValueKind == JsonValueKind.Array
                    ? [.. keys.EnumerateArray().Where(static key => key.ValueKind == JsonValueKind.String).Select(static key => key.GetString()!)]
                    : [],
                ReadAt = root.TryGetProperty("readAt", out var at) && at.TryGetDateTimeOffset(out var read) ? read : null,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // A provider key may hold characters a file name cannot: they become '_'.
    private static string FileName(string providerKey)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(providerKey.Length + 5);
        foreach (var character in providerKey) builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        return builder.Append(".json").ToString();
    }

    private void SetStatus(CopilotManagedPolicyStatus status)
    {
        lock (_statusGate)
        {
            if (_status == status) return;
            _status = status;
        }

        StatusChanged?.Invoke(status);
    }

    private static string Describe(Exception exception)
        => exception switch
        {
            CopilotRuntimeInstallException install => install.Message,
            OperationCanceledException => "The Copilot runtime did not answer in time.",
            _ => $"The policy of your organization could not be read: {exception.Message}",
        };

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class Progress(CopilotManagedPolicySource source) : IProgress<CopilotRuntimeInstallProgress>
    {
        public void Report(CopilotRuntimeInstallProgress value)
            => source.SetStatus(source.Status with { State = "installing", Message = value.Message, Received = value.Received, Total = value.Total });
    }
}
