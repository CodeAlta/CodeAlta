using System.Diagnostics;
using CodeAlta.Agent.Runtime;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Core.IO;

namespace CodeAlta.Catalog.Secrets;

/// <summary>
/// Keeps the key of a <see cref="MasterKeySecretProtector"/>: in the protected store of the machine, or in a file only
/// the user can read.
/// </summary>
public interface ISecretKeyStore
{
    /// <summary>Gets the name of the protection, one of <see cref="SecretProtectionKinds"/>.</summary>
    string Kind { get; }

    /// <summary>Reads the key.</summary>
    /// <returns>The key, or <see langword="null"/> when none is kept yet.</returns>
    /// <exception cref="SecretStoreUnavailableException">The store of the key cannot be read.</exception>
    byte[]? Load();

    /// <summary>Keeps a key, in the place of the one kept before.</summary>
    /// <param name="key">The key.</param>
    /// <exception cref="SecretStoreUnavailableException">The store of the key cannot be written.</exception>
    void Save(byte[] key);

    /// <summary>Removes the key, when the store it protected is removed.</summary>
    void Delete();
}

/// <summary>
/// Protects values with AES-GCM under a random key of 256 bits kept by an <see cref="ISecretKeyStore"/>: the Keychain,
/// libsecret, or a file only the user can read. The key is made the first time a value is protected.
/// </summary>
public sealed class MasterKeySecretProtector : ISecretProtector
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly ISecretKeyStore _keys;
    private readonly Lock _gate = new();
    private byte[]? _key;

    /// <summary>Initializes a new instance of the <see cref="MasterKeySecretProtector"/> class.</summary>
    /// <param name="keys">Where the key is kept.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <see langword="null"/>.</exception>
    public MasterKeySecretProtector(ISecretKeyStore keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
    }

    /// <inheritdoc />
    public string Kind => _keys.Kind;

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> value)
    {
        var key = Key(create: true)!;
        var result = new byte[NonceSize + TagSize + value.Length];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, value, result.AsSpan(NonceSize + TagSize), result.AsSpan(NonceSize, TagSize));
        return result;
    }

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedValue)
    {
        if (protectedValue.Length < NonceSize + TagSize) throw new SecretStoreUnavailableException("A protected value is damaged.");
        var key = Key(create: false)
            ?? throw new SecretStoreUnavailableException($"The key of the secrets of this machine is missing from its {Describe(Kind)}: the values cannot be read back. Set them again.");
        var result = new byte[protectedValue.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(protectedValue[..NonceSize], protectedValue[(NonceSize + TagSize)..], protectedValue.Slice(NonceSize, TagSize), result);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new SecretStoreUnavailableException($"A value was protected by another key than the one of the {Describe(Kind)}: set it again.", exception);
        }

        return result;
    }

    private byte[]? Key(bool create)
    {
        lock (_gate)
        {
            if (_key is not null) return _key;
            var key = _keys.Load();
            if (key is { Length: KeySize })
            {
                return _key = key;
            }

            if (key is not null) throw new SecretStoreUnavailableException($"The key of the secrets in the {Describe(Kind)} is damaged.");
            if (!create) return null;
            key = RandomNumberGenerator.GetBytes(KeySize);
            _keys.Save(key);
            return _key = key;
        }
    }

    internal static string Describe(string kind) => kind switch
    {
        SecretProtectionKinds.Keychain => "Keychain",
        SecretProtectionKinds.Libsecret => "keyring (libsecret)",
        SecretProtectionKinds.Dpapi => "Windows data protection",
        _ => "key file",
    };
}

/// <summary>Keeps the key in a file only the user can read, where the machine has no protected store.</summary>
public sealed class FileSecretKeyStore : ISecretKeyStore
{
    private readonly string _path;

    /// <summary>Initializes a new instance of the <see cref="FileSecretKeyStore"/> class.</summary>
    /// <param name="path">The path of the file of the key.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public FileSecretKeyStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public string Kind => SecretProtectionKinds.File;

    /// <inheritdoc />
    public byte[]? Load()
    {
        try
        {
            return File.Exists(_path) ? Convert.FromHexString(File.ReadAllText(_path).Trim()) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            throw new SecretStoreUnavailableException($"The key file of the secrets cannot be read: {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public void Save(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            var temporary = _path + ".tmp";
            LocalSecretStore.WritePrivateFile(temporary, Convert.ToHexString(key));
            StagedFileReplacement.MoveOverWithoutGap(temporary, _path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreUnavailableException($"The key file of the secrets cannot be written: {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public void Delete() => File.Delete(_path);
}

/// <summary>
/// Keeps the key in the protected store of the user through its command line tool: <c>security</c> and the login
/// Keychain on macOS, <c>secret-tool</c> and the Secret Service (libsecret) on Linux. The key never appears in the
/// arguments of a process: it is given on the standard input.
/// </summary>
public sealed class CommandSecretKeyStore : ISecretKeyStore
{
    private const string Service = "CodeAlta secrets";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _account;

    /// <summary>Initializes a new instance of the <see cref="CommandSecretKeyStore"/> class.</summary>
    /// <param name="kind"><see cref="SecretProtectionKinds.Keychain"/> or <see cref="SecretProtectionKinds.Libsecret"/>.</param>
    /// <param name="account">The name the key is kept under, one per store folder.</param>
    /// <exception cref="ArgumentException">The kind is neither, or the account is empty.</exception>
    public CommandSecretKeyStore(string kind, string account)
    {
        if (kind is not (SecretProtectionKinds.Keychain or SecretProtectionKinds.Libsecret)) throw new ArgumentException($"'{kind}' is kept by no command.", nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        Kind = kind;
        _account = account;
    }

    /// <inheritdoc />
    public string Kind { get; }

    /// <summary>Gets the name a store folder keeps its key under: one per folder, so that two profiles never share a key.</summary>
    /// <param name="folder">The folder of the store.</param>
    /// <returns>The name.</returns>
    public static string AccountOf(string folder)
        => "store-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder))), 0, 8).ToLowerInvariant();

    /// <summary>Tells whether the protected store of this user can be used now, as the store of a new key.</summary>
    /// <param name="kind"><see cref="SecretProtectionKinds.Keychain"/> or <see cref="SecretProtectionKinds.Libsecret"/>.</param>
    /// <returns><see langword="true"/> when a key can be written there and read back now.</returns>
    /// <remarks>
    /// A read alone is not enough: over SSH, or in a service of macOS, the login Keychain answers a read and refuses a
    /// write ("User interaction is not allowed"), and such a session keeps its key in a file instead.
    /// </remarks>
    public static bool IsAvailable(string kind)
    {
        try
        {
            if (kind == SecretProtectionKinds.Libsecret && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return false;
            var probe = new CommandSecretKeyStore(kind, "probe-" + Guid.NewGuid().ToString("N"));
            var written = RandomNumberGenerator.GetBytes(8);
            try
            {
                probe.Save(written);
                return probe.Load() is { } read && read.AsSpan().SequenceEqual(written);
            }
            finally
            {
                probe.Delete();
            }
        }
        catch (Exception exception) when (exception is SecretStoreUnavailableException or ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public byte[]? Load()
    {
        var (exit, output, error) = Kind == SecretProtectionKinds.Keychain
            ? Run("security", ["find-generic-password", "-s", Service, "-a", _account, "-w"], input: null)
            : Run("secret-tool", ["lookup", "service", Service, "account", _account], input: null);
        var text = output.Trim();
        if (exit == 0 && text.Length > 0)
        {
            try
            {
                return Convert.FromHexString(text);
            }
            catch (FormatException exception)
            {
                throw new SecretStoreUnavailableException($"The key of the secrets in the {MasterKeySecretProtector.Describe(Kind)} is damaged.", exception);
            }
        }

        // Not found: `security` exits with 44, `secret-tool` with 1 and nothing on its error output.
        if (Kind == SecretProtectionKinds.Keychain ? exit == 44 : exit is 0 or 1 && string.IsNullOrWhiteSpace(error)) return null;
        throw new SecretStoreUnavailableException($"The {MasterKeySecretProtector.Describe(Kind)} cannot be read: {Short(error)}");
    }

    /// <inheritdoc />
    public void Save(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var hex = Convert.ToHexString(key);
        // `security -i` reads its commands on the standard input, so the key is in no argument of a process.
        var (exit, _, error) = Kind == SecretProtectionKinds.Keychain
            ? Run("security", ["-i"], $"add-generic-password -U -s \"{Service}\" -a \"{_account}\" -w \"{hex}\"\n")
            : Run("secret-tool", ["store", "--label=" + Service, "service", Service, "account", _account], hex);
        if (exit != 0 || Kind == SecretProtectionKinds.Keychain && error.Contains("error", StringComparison.OrdinalIgnoreCase))
            throw new SecretStoreUnavailableException($"The {MasterKeySecretProtector.Describe(Kind)} cannot be written: {Short(error)}");
    }

    /// <inheritdoc />
    public void Delete()
    {
        _ = Kind == SecretProtectionKinds.Keychain
            ? Run("security", ["delete-generic-password", "-s", Service, "-a", _account], input: null)
            : Run("secret-tool", ["clear", "service", Service, "account", _account], input: null);
    }

    private static string Short(string error) => error.Trim() is { Length: > 0 } text ? text.Length > 300 ? text[..300] : text : "no answer.";

    private static (int Exit, string Output, string Error) Run(string command, IReadOnlyList<string> arguments, string? input)
    {
        var start = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new SecretStoreUnavailableException($"{command} did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SecretStoreUnavailableException($"{command} cannot be run: {exception.Message}", exception);
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                if (input is not null) process.StandardInput.Write(input);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It ended without reading: its exit code says why.
            }

            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new SecretStoreUnavailableException($"{command} did not answer within {Timeout.TotalSeconds:0} seconds: the store may be locked.");
            }

            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
    }
}
