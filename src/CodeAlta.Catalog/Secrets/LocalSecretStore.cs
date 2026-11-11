using System.Diagnostics.CodeAnalysis;
using CodeAlta.Agent.Runtime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Core.IO;

namespace CodeAlta.Catalog.Secrets;

/// <summary>
/// Gives the values of secrets and variables by name, to resolve the references of the configuration
/// (<c>${secret:NAME}</c>, <c>${var:NAME}</c>, <c>api_key_secret</c>). A store of a machine is one source; the stores of
/// a project, a space, a team or an organization are others, asked before it when they apply.
/// </summary>
public interface ISecretLookup
{
    /// <summary>Gets the scope of the values, such as <see cref="LocalSecretStore.MachineScope"/>.</summary>
    string Scope { get; }

    /// <summary>Gets the value of a secret, for a use of it: never to show it.</summary>
    /// <param name="name">The name of the secret.</param>
    /// <param name="value">Its value when it is set.</param>
    /// <returns><see langword="true"/> when the secret is set.</returns>
    /// <exception cref="SecretStoreUnavailableException">The value cannot be read back.</exception>
    bool TryGetSecret(string name, [NotNullWhen(true)] out string? value);

    /// <summary>Gets the value of a variable.</summary>
    /// <param name="name">The name of the variable.</param>
    /// <param name="value">Its value when it is set.</param>
    /// <returns><see langword="true"/> when the variable is set.</returns>
    bool TryGetVariable(string name, [NotNullWhen(true)] out string? value);

    /// <summary>Gets the values of all its secrets, which are masked wherever they would be written or shown.</summary>
    /// <returns>The values.</returns>
    IReadOnlyCollection<string> GetSecretValues();
}

/// <summary>What is known of a secret, never its value.</summary>
/// <param name="Name">Its name, such as <c>GRAFANA_TOKEN</c>.</param>
/// <param name="Version">Its version: 1 when set, one more at each rotation.</param>
/// <param name="SetBy">Who set its current value.</param>
/// <param name="SetAt">When its current value was set.</param>
/// <param name="Scope">Where it is kept, <see cref="LocalSecretStore.MachineScope"/> for this machine.</param>
public sealed record SecretInfo(string Name, int Version, string SetBy, DateTimeOffset SetAt, string Scope)
{
    /// <summary>Gets the id of the credential provider whose vault keeps the value; null for a value kept on this computer.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets what the user picked in the vault, as the list shows it; null for a value kept on this computer.</summary>
    public string? Label { get; init; }

    /// <summary>Gets what the provider reads, such as <c>op://Private/Anthropic/credential</c>; never shown to agents. Null for a value kept on this computer.</summary>
    public string? VaultReference { get; init; }

    /// <summary>Gets how long a confirmation of the secret lasts when it asks before use (<see cref="SecretAskDurations"/>); null when it does not ask.</summary>
    public string? AskBeforeUse { get; init; }
}

/// <summary>A variable: a named value that is not secret.</summary>
/// <param name="Name">Its name, such as <c>GRAFANA_URL</c>.</param>
/// <param name="Value">Its value.</param>
/// <param name="Version">Its version: 1 when set, one more at each change.</param>
/// <param name="SetBy">Who set its current value.</param>
/// <param name="SetAt">When its current value was set.</param>
/// <param name="Scope">Where it is kept, <see cref="LocalSecretStore.MachineScope"/> for this machine.</param>
public sealed record VariableInfo(string Name, string Value, int Version, string SetBy, DateTimeOffset SetAt, string Scope);

/// <summary>
/// The secrets and variables of this machine, kept under the root of the profile (<c>~/.alta/secrets</c>): the names
/// and what is known of them in <c>store.json</c>, the values of secrets protected by the protected store of the
/// machine (<see cref="ISecretProtector"/>). Secrets are write-only for whoever manages them: they are set, rotated,
/// deleted and listed by name; their values are only read to be used (<see cref="ISecretLookup"/>).
/// </summary>
/// <remarks>
/// The desktop, the terminal application, the command line and the node of one profile share the store: each change
/// reads the file again and writes it back while it holds the lock of the file, and each read follows the file.
/// </remarks>
public sealed partial class LocalSecretStore : ISecretLookup
{
    /// <summary>The scope of the values of a machine.</summary>
    public const string MachineScope = "machine";

    /// <summary>The longest value of a secret or a variable.</summary>
    public const int MaximumValueLength = 64 * 1024;

    /// <summary>The most secrets, and the most variables, of one store.</summary>
    public const int MaximumEntries = 512;

    private const string StoreFileName = "store.json";
    private const string KeyFileName = "key";

    private readonly ISecretProtector? _givenProtector;
    private readonly Lock _gate = new();
    private SecretBindings? _bindings;
    private ISecretProtector? _protector;
    private Snapshot? _snapshot;

    /// <summary>Initializes a new instance of the <see cref="LocalSecretStore"/> class for the profile of a root.</summary>
    /// <param name="root">The root of the profile, <c>~/.alta</c>: the store is its <c>secrets</c> folder.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is empty.</exception>
    public LocalSecretStore(string root)
        : this(root, protector: null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LocalSecretStore"/> class with a protection of its own.</summary>
    /// <param name="root">The root of the profile, <c>~/.alta</c>: the store is its <c>secrets</c> folder.</param>
    /// <param name="protector">
    /// What protects the values; <see langword="null"/> for the one the store was made with, or the protected store of
    /// this machine for a new store (<see cref="ForThisMachine"/>).
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is empty.</exception>
    public LocalSecretStore(string root, ISecretProtector? protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        FolderPath = Path.Combine(root, "secrets");
        _givenProtector = protector;
    }

    /// <summary>Gets the folder of the store.</summary>
    public string FolderPath { get; }

    /// <summary>Gets the secrets of this machine kept in vaults, and those that ask before use (<c>bindings.json</c>).</summary>
    public SecretBindings Bindings => _bindings ??= new SecretBindings(FolderPath);

    /// <summary>
    /// Gets the values of the secrets kept in vaults that the host holds, and its confirmations; null in a host without a
    /// credential broker, where a secret kept in a vault, or one that asks before use, cannot be read.
    /// </summary>
    public IBoundSecretSource? BoundSecrets { get; init; }

    /// <summary>Gets what the values read through this instance are for, such as <c>provider anthropic</c>; null for a use nobody names.</summary>
    public string? Use { get; init; }

    /// <inheritdoc />
    public string Scope => MachineScope;

    /// <summary>Gets the protection of the values, one of <see cref="SecretProtectionKinds"/>, or <see langword="null"/> while the store holds no secret.</summary>
    public string? Protection => Read().Document.Protection;

    /// <summary>
    /// Gets a number that changes each time the store changes, in this process or another: what keeps a copy of its
    /// values knows when to read them again.
    /// </summary>
    public long ChangeStamp => Read().Stamp;

    private string StorePath => Path.Combine(FolderPath, StoreFileName);

    /// <summary>Tells whether a text can be the name of a secret or a variable: the name of an environment variable.</summary>
    /// <param name="name">The text.</param>
    /// <returns><see langword="true"/> for letters, digits and <c>_</c>, not starting with a digit, at most 128.</returns>
    public static bool IsName([NotNullWhen(true)] string? name)
        => name is { Length: > 0 and <= 128 } && !char.IsAsciiDigit(name[0])
            && name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    /// <summary>
    /// Makes the protection of a new store on this machine: DPAPI on Windows, the Keychain on macOS, libsecret on Linux
    /// when its session runs, otherwise a key in a file only the user can read.
    /// </summary>
    /// <param name="folder">The folder of the store.</param>
    /// <returns>The protection.</returns>
    public static ISecretProtector ForThisMachine(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (OperatingSystem.IsWindows()) return new DpapiSecretProtector();
        var kind = OperatingSystem.IsMacOS() ? SecretProtectionKinds.Keychain : SecretProtectionKinds.Libsecret;
        return CommandSecretKeyStore.IsAvailable(kind)
            ? new MasterKeySecretProtector(new CommandSecretKeyStore(kind, CommandSecretKeyStore.AccountOf(folder)))
            : new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(folder, KeyFileName)));
    }

    /// <summary>Lists the secrets, by name, without their values.</summary>
    /// <returns>The secrets, in the order of their names.</returns>
    /// <exception cref="InvalidDataException">The file of the store is damaged.</exception>
    /// <remarks>A secret kept in a vault is listed with its provider; a binding wins over a value an older build kept here under its name.</remarks>
    public IReadOnlyList<SecretInfo> ListSecrets()
    {
        var bindings = Bindings.List();
        var asks = Bindings.ListAskBeforeUse();
        var bound = bindings.Select(static binding => binding.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. Read().Document.Secrets.Where(entry => !bound.Contains(entry.Key))
                .Select(entry => new SecretInfo(entry.Key, entry.Value.Version, entry.Value.SetBy ?? string.Empty, entry.Value.SetAt, MachineScope) { AskBeforeUse = asks.GetValueOrDefault(entry.Key) })
            .Concat(bindings.Select(binding => new SecretInfo(binding.Name, binding.Version, binding.SetBy, binding.SetAt, MachineScope)
            {
                Provider = binding.Provider, Label = binding.Label, VaultReference = binding.Reference, AskBeforeUse = asks.GetValueOrDefault(binding.Name),
            }))
            .OrderBy(static secret => secret.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Keeps the value of a secret in a vault: binds its name to what a credential provider reads. A value this machine kept
    /// under the name is taken away (it stays masked, as after a rotation).
    /// </summary>
    /// <param name="name">The name of the secret.</param>
    /// <param name="provider">The id of the credential provider.</param>
    /// <param name="reference">What the provider reads.</param>
    /// <param name="label">What the list shows.</param>
    /// <param name="setBy">Who binds it.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="ArgumentException">A value is not valid.</exception>
    /// <exception cref="InvalidOperationException">The store holds the most secrets kept in vaults already.</exception>
    public SecretBinding Bind(string name, string provider, string reference, string label, string setBy)
    {
        var binding = Bindings.Bind(name, provider, reference, label, setBy);
        RemoveLocal(name);
        return binding;
    }

    /// <summary>Lists the variables, with their values.</summary>
    /// <returns>The variables, in the order of their names.</returns>
    /// <exception cref="InvalidDataException">The file of the store is damaged.</exception>
    public IReadOnlyList<VariableInfo> ListVariables()
        => [.. Read().Document.Variables.Select(static entry => new VariableInfo(entry.Key, entry.Value.Value ?? string.Empty, entry.Value.Version, entry.Value.SetBy ?? string.Empty, entry.Value.SetAt, MachineScope))
            .OrderBy(static variable => variable.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Sets a secret: adds it, or gives it a new value and a new version.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="value">Its value.</param>
    /// <param name="setBy">Who sets it, such as <c>alice (CodeAlta Desktop)</c>.</param>
    /// <returns>What is known of it now.</returns>
    /// <exception cref="ArgumentException">The name or the value is not valid.</exception>
    /// <exception cref="InvalidOperationException">The store holds the most secrets already.</exception>
    /// <exception cref="SecretStoreUnavailableException">The value cannot be protected.</exception>
    /// <remarks>A secret kept in a vault under the name is then kept on this computer: its binding is removed.</remarks>
    public SecretInfo SetSecret(string name, string value, string setBy)
    {
        var info = WriteSecret(name, value, setBy, mustExist: false);
        Bindings.Unbind(name);
        return info;
    }

    /// <summary>Rotates a secret: gives it a new value and a new version.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="value">Its new value.</param>
    /// <param name="setBy">Who rotates it.</param>
    /// <returns>What is known of it now.</returns>
    /// <exception cref="ArgumentException">The name or the value is not valid.</exception>
    /// <exception cref="KeyNotFoundException">No secret has this name.</exception>
    /// <exception cref="SecretStoreUnavailableException">The value cannot be protected.</exception>
    public SecretInfo RotateSecret(string name, string value, string setBy) => WriteSecret(name, value, setBy, mustExist: true);

    /// <summary>Deletes a secret.</summary>
    /// <param name="name">Its name.</param>
    /// <returns><see langword="true"/> when it was set.</returns>
    /// <remarks>A secret kept in a vault loses its binding, and a secret that asks before use stops asking; nothing is changed in the vault.</remarks>
    public bool DeleteSecret(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var removed = RemoveLocal(name);
        removed |= Bindings.Unbind(name);
        if (IsName(name)) Bindings.SetAskBeforeUse(name, null);
        return removed;
    }

    private bool RemoveLocal(string name)
    {
        return Change(document =>
        {
            if (!document.Secrets.Remove(name, out var removed)) return false;
            // A run may still hold the value: it stays masked.
            Retire(document, [removed.Value]);
            return true;
        });
    }

    /// <summary>Sets a variable: adds it, or gives it a new value and a new version.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="value">Its value.</param>
    /// <param name="setBy">Who sets it.</param>
    /// <returns>The variable now.</returns>
    /// <exception cref="ArgumentException">The name or the value is not valid.</exception>
    /// <exception cref="InvalidOperationException">The store holds the most variables already.</exception>
    public VariableInfo SetVariable(string name, string value, string setBy)
    {
        Validate(name, value, setBy);
        VariableInfo? result = null;
        Change(document =>
        {
            var version = 1;
            if (document.Variables.TryGetValue(name, out var existing))
            {
                if (string.Equals(existing.Value, value, StringComparison.Ordinal))
                {
                    result = new(name, value, existing.Version, existing.SetBy ?? string.Empty, existing.SetAt, MachineScope);
                    return false;
                }

                version = existing.Version + 1;
                document.Variables.Remove(name);
            }
            else if (document.Variables.Count >= MaximumEntries)
            {
                throw new InvalidOperationException($"This machine holds at most {MaximumEntries} variables.");
            }

            var entry = new VariableDocument { Value = value, Version = version, SetBy = setBy, SetAt = DateTimeOffset.UtcNow };
            document.Variables[name] = entry;
            result = new(name, value, version, setBy, entry.SetAt, MachineScope);
            return true;
        });
        return result!;
    }

    /// <summary>Deletes a variable.</summary>
    /// <param name="name">Its name.</param>
    /// <returns><see langword="true"/> when it was set.</returns>
    public bool DeleteVariable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Change(document => document.Variables.Remove(name));
    }

    /// <inheritdoc />
    /// <remarks>
    /// A secret kept in a vault is given from what the host holds (<see cref="BoundSecrets"/>), and a secret that asks before use only
    /// for a use the user confirmed; otherwise the exception says why, naming the secret and its vault.
    /// </remarks>
    public bool TryGetSecret(string name, [NotNullWhen(true)] out string? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Bindings.TryGet(name, out var binding))
        {
            var read = BoundSecrets?.ReadBound(binding.Name, Use)
                ?? throw new SecretStoreUnavailableException($"{binding.Name} is kept in a vault (the credential provider '{binding.Provider}'), which this application does not read.");
            value = read.Value ?? throw new SecretStoreUnavailableException(read.Problem ?? $"{binding.Name} cannot be read from its vault now.");
            return true;
        }

        var snapshot = Read();
        if (!snapshot.Document.Secrets.ContainsKey(name))
        {
            value = null;
            return false;
        }

        if (Bindings.AskBeforeUse(name) is not null)
        {
            var problem = BoundSecrets is null
                ? $"{name} asks before use: this application cannot ask for a confirmation."
                : BoundSecrets.AuthorizeUse(name, Use);
            if (problem is not null) throw new SecretStoreUnavailableException(problem);
        }

        value = Values(snapshot)[name];
        return true;
    }

    /// <inheritdoc />
    public bool TryGetVariable(string name, [NotNullWhen(true)] out string? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        value = Read().Document.Variables.TryGetValue(name, out var entry) ? entry.Value : null;
        return value is not null;
    }

    /// <inheritdoc />
    /// <remarks>A value that cannot be read back is left out: what is not read cannot leak.</remarks>
    public IReadOnlyCollection<string> GetSecretValues()
    {
        var snapshot = Read();
        if (snapshot.Document.Secrets.Count == 0 && snapshot.Document.Fleet.Count == 0 && snapshot.Document.Retired.Count == 0) return [];
        List<string> values = [];
        try
        {
            values.AddRange(Values(snapshot).Values);
        }
        catch (SecretStoreUnavailableException)
        {
            // What is not read cannot leak.
        }

        // The secrets the fleet gave this machine are masked as its own are, and so are the values taken away a run may still hold.
        values.AddRange(FleetSecretValues(snapshot));
        values.AddRange(RetiredValues(snapshot));
        return values;
    }

    /// <summary>
    /// Moves the key of the store from the Keychain or libsecret into a file only the user can read, for a service that
    /// runs without a login session (a LaunchDaemon, a system unit, a lingering user unit), where neither answers. The
    /// values keep their key: nothing is protected again. Run as the user, where the Keychain or libsecret answers.
    /// </summary>
    /// <returns>The protection the key left; null when the store had none, or a key file or DPAPI already.</returns>
    /// <exception cref="SecretStoreUnavailableException">The key cannot be read where it is, or written to its file.</exception>
    public string? UseKeyFile() => UseKeyFile(KeyStoreOf);

    internal string? UseKeyFile(Func<string, ISecretKeyStore> keyStoreOf)
    {
        ArgumentNullException.ThrowIfNull(keyStoreOf);
        string? moved = null;
        Change(document =>
        {
            if (document.Protection is not { } kind || kind is not (SecretProtectionKinds.Keychain or SecretProtectionKinds.Libsecret)) return false;
            var key = keyStoreOf(kind).Load()
                ?? throw new SecretStoreUnavailableException($"The key of the secrets of this machine is missing from its {MasterKeySecretProtector.Describe(kind)}.");
            try
            {
                // The file first: the store names it only once it holds the key.
                KeyStoreOf(SecretProtectionKinds.File).Save(key);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            }

            document.Protection = SecretProtectionKinds.File;
            moved = kind;
            return true;
        });
        if (moved is null) return null;
        _protector = null;
        try
        {
            keyStoreOf(moved).Delete();
        }
        catch (Exception exception) when (exception is SecretStoreUnavailableException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A copy left there protects nothing the store still names.
        }

        return moved;
    }

    /// <summary>Removes the store: its file, and the key of its protection.</summary>
    public void Delete()
    {
        lock (_gate)
        {
            using var held = ConfigFileLock.Acquire(StorePath);
            if (Directory.Exists(FolderPath))
            {
                if (Read().Document.Protection is { } kind && kind != SecretProtectionKinds.Dpapi)
                {
                    KeyStoreOf(kind).Delete();
                }

                Directory.Delete(FolderPath, recursive: true);
            }

            _snapshot = null;
            _protector = null;
        }
    }

    // Writes a file only the user can read and write, in a folder only the user can open.
    internal static void WritePrivateFile(string path, string content)
    {
        var folder = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        {
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void Validate(string name, string value, string setBy)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(setBy);
        if (!IsName(name)) throw new ArgumentException($"'{name}' is not a valid name: letters, digits and _, not starting with a digit.", nameof(name));
        if (value.Length == 0) throw new ArgumentException($"The value of {name} is empty.", nameof(value));
        if (value.Length > MaximumValueLength) throw new ArgumentException($"The value of {name} is longer than {MaximumValueLength} characters.", nameof(value));
    }

    private SecretInfo WriteSecret(string name, string value, string setBy, bool mustExist)
    {
        Validate(name, value, setBy);
        SecretInfo? result = null;
        Change(document =>
        {
            var protector = ProtectorFor(document);
            var version = 1;
            if (document.Secrets.TryGetValue(name, out var existing))
            {
                version = existing.Version + 1;
                document.Secrets.Remove(name);
                // A run may still hold the value it replaces: it stays masked.
                Retire(document, [existing.Value]);
            }
            else if (mustExist)
            {
                throw new KeyNotFoundException($"No secret is named {name} on this machine.");
            }
            else if (document.Secrets.Count >= MaximumEntries)
            {
                throw new InvalidOperationException($"This machine holds at most {MaximumEntries} secrets.");
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            byte[] protectedValue;
            try
            {
                protectedValue = protector.Protect(bytes);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }

            var entry = new SecretDocument { Version = version, SetBy = setBy, SetAt = DateTimeOffset.UtcNow, Value = Convert.ToBase64String(protectedValue) };
            document.Secrets[name] = entry;
            result = new(name, version, setBy, entry.SetAt, MachineScope);
            return true;
        });
        return result!;
    }

    // The protection of the store: the one it was made with, or the one of this machine for its first secret.
    private ISecretProtector ProtectorFor(StoreDocument document)
    {
        if (_protector is { } known && (document.Protection is null || document.Protection == known.Kind)) return SetKind(document, known);
        if (_givenProtector is { } given)
        {
            if (document.Protection is { } written && written != given.Kind && document.Secrets.Count > 0)
                throw new SecretStoreUnavailableException($"The secrets of this machine are protected by the {MasterKeySecretProtector.Describe(written)}, not the {MasterKeySecretProtector.Describe(given.Kind)}.");
            return SetKind(document, _protector = given);
        }

        if (document.Protection is { } kind)
        {
            return SetKind(document, _protector = kind == SecretProtectionKinds.Dpapi
                ? (OperatingSystem.IsWindows() ? new DpapiSecretProtector() : throw new SecretStoreUnavailableException("The secrets of this profile were protected by Windows: they cannot be read on this system."))
                : new MasterKeySecretProtector(KeyStoreOf(kind)));
        }

        return SetKind(document, _protector = ForThisMachine(FolderPath));
    }

    private static ISecretProtector SetKind(StoreDocument document, ISecretProtector protector)
    {
        document.Protection ??= protector.Kind;
        return protector;
    }

    private ISecretKeyStore KeyStoreOf(string kind) => kind == SecretProtectionKinds.File
        ? new FileSecretKeyStore(Path.Combine(FolderPath, KeyFileName))
        : new CommandSecretKeyStore(kind, CommandSecretKeyStore.AccountOf(FolderPath));

    // The values of the secrets of a reading of the file, read back once for that reading.
    private Dictionary<string, string> Values(Snapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot.Values is { } known) return known;
            var protector = ProtectorFor(snapshot.Document);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, entry) in snapshot.Document.Secrets)
            {
                byte[] bytes;
                try
                {
                    bytes = protector.Unprotect(Convert.FromBase64String(entry.Value ?? string.Empty));
                }
                catch (FormatException exception)
                {
                    throw new SecretStoreUnavailableException($"The protected value of {name} is damaged: set it again.", exception);
                }

                values[name] = Encoding.UTF8.GetString(bytes);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }

            return snapshot.Values = values;
        }
    }

    // Reads the file again when it changed since the last reading.
    private Snapshot Read()
    {
        lock (_gate)
        {
            var info = new FileInfo(StorePath);
            var stamp = info.Exists ? info.LastWriteTimeUtc.Ticks ^ (info.Length << 1) : 0;
            if (_snapshot is { } known && known.Stamp == stamp) return known;
            return _snapshot = new Snapshot(stamp, info.Exists ? Load(StorePath) : new StoreDocument());
        }
    }

    private static StoreDocument Load(string path)
    {
        try
        {
            var document = JsonSerializer.Deserialize(File.ReadAllBytes(path), SecretStoreJsonContext.Default.StoreDocument) ?? new StoreDocument();
            // Names are those of environment variables: one name whatever its case, as on Windows.
            document.Secrets = new(document.Secrets.Where(static entry => IsName(entry.Key)), StringComparer.OrdinalIgnoreCase);
            document.Variables = new(document.Variables.Where(static entry => IsName(entry.Key) && entry.Value.Value is not null), StringComparer.OrdinalIgnoreCase);
            document.Fleet = [.. (document.Fleet ?? []).Where(static copy => copy is not null && IsName(copy.Name) && copy.Source is { Length: > 0 } && copy.Value is not null)];
            document.Retired = [.. (document.Retired ?? []).Where(static retired => retired?.Value is not null)];
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The store of secrets '{path}' is damaged: {exception.Message}", exception);
        }
        catch (FileNotFoundException)
        {
            return new StoreDocument();
        }
    }

    // Reads the file under its lock, changes it and writes it back when the change says it changed something.
    private bool Change(Func<StoreDocument, bool> change)
    {
        lock (_gate)
        {
            using var held = ConfigFileLock.Acquire(StorePath);
            var document = File.Exists(StorePath) ? Load(StorePath) : new StoreDocument();
            if (!change(document)) return false;
            var temporary = StorePath + ".tmp";
            WritePrivateFile(temporary, JsonSerializer.Serialize(document, SecretStoreJsonContext.Default.StoreDocument));
            // A rename that replaces the file while a host reads it (its masker follows the store), never leaving a moment without
            // it: a missing store reads as one without secrets.
            StagedFileReplacement.MoveOverWithoutGap(temporary, StorePath);
            _snapshot = null;
            return true;
        }
    }

    private sealed class Snapshot(long stamp, StoreDocument document)
    {
        public long Stamp { get; } = stamp;

        public StoreDocument Document { get; } = document;

        public Dictionary<string, string>? Values { get; set; }

        // The values of the copies the fleet gave, by their place in the document.
        public string?[]? FleetValues { get; set; }

        // The values taken away, kept for masking.
        public IReadOnlyList<string>? RetiredValues { get; set; }
    }
}

internal sealed class StoreDocument
{
    [JsonPropertyName("format")]
    public int Format { get; set; } = 1;

    [JsonPropertyName("protection")]
    public string? Protection { get; set; }

    [JsonPropertyName("secrets")]
    public Dictionary<string, SecretDocument> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("variables")]
    public Dictionary<string, VariableDocument> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // The copies the fleet gave this machine, their values protected as the values of the machine are.
    [JsonPropertyName("fleet")]
    public List<FleetCopyDocument> Fleet { get; set; } = [];

    // The values taken away that a run may still hold: masked, never given again.
    [JsonPropertyName("retired")]
    public List<RetiredDocument> Retired { get; set; } = [];
}

internal sealed class SecretDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("set_by")]
    public string? SetBy { get; set; }

    [JsonPropertyName("set_at")]
    public DateTimeOffset SetAt { get; set; }

    // The protected value, in base 64.
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

internal sealed class VariableDocument
{
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("set_by")]
    public string? SetBy { get; set; }

    [JsonPropertyName("set_at")]
    public DateTimeOffset SetAt { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoreDocument))]
internal sealed partial class SecretStoreJsonContext : JsonSerializerContext;
