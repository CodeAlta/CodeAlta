using CodeAlta.Catalog;
using CodeAlta.Catalog.Network;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Hosting;

/// <summary>
/// The proxy of an application of CodeAlta (desktop, TUI, <c>altacli</c>), applied from the <c>[network.proxy]</c> table of a
/// profile at the start, before the first HTTP request, and again when the settings change.
/// </summary>
public sealed class CodeAltaNetwork
{
    /// <summary>The variable the sandbox sets in the environment of a sandboxed session: its programs keep the proxy of the sandbox.</summary>
    public const string SandboxVariable = "CODEALTA_SANDBOX";

    private readonly string _globalRoot;
    private readonly Lock _gate = new();

    private CodeAltaNetwork(NetworkProxy proxy, string globalRoot, bool sandboxed)
    {
        Proxy = proxy;
        _globalRoot = globalRoot;
        Sandboxed = sandboxed;
    }

    /// <summary>Gets the proxy of the process.</summary>
    public NetworkProxy Proxy { get; }

    /// <summary>
    /// Gets whether the process runs in the sandbox of a session: it then keeps the proxy the sandbox gave its environment,
    /// whatever the configuration says.
    /// </summary>
    public bool Sandboxed { get; }

    /// <summary>Gets why the last application kept the system mode instead of the settings, or null when it applied them.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Installs the proxy of the process (<see cref="NetworkProxy.Install"/>) and applies the settings of a profile. A
    /// configuration that cannot be read, or a password that cannot be, keeps the system mode and says why in <see cref="Error"/>:
    /// the application starts whatever the file says.
    /// </summary>
    /// <param name="globalRoot">The root of the profile, <c>~/.alta</c>.</param>
    /// <returns>The network of the process.</returns>
    /// <exception cref="ArgumentException"><paramref name="globalRoot"/> is empty.</exception>
    public static CodeAltaNetwork Start(string globalRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRoot);
        var network = new CodeAltaNetwork(NetworkProxy.Install(), globalRoot, !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SandboxVariable)));
        network.Refresh();
        return network;
    }

    /// <summary>Reads the settings of the profile again and applies them: to the next requests, and to the programs started from now on.</summary>
    /// <returns>Why the settings could not be applied, or null when they were.</returns>
    public string? Refresh()
    {
        if (Sandboxed) return null;
        lock (_gate)
        {
            var (settings, password, error) = Read(_globalRoot);
            Proxy.Apply(settings, password);
            Error = error;
            return error;
        }
    }

    /// <summary>Reads the settings of a profile and the password of their secret.</summary>
    /// <param name="globalRoot">The root of the profile.</param>
    /// <returns>The settings to apply, the password, and why the settings of the file were not taken (the defaults then are).</returns>
    internal static (ProxySettings Settings, string? Password, string? Error) Read(string globalRoot)
    {
        return new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = globalRoot }).TryLoadGlobalProxySettings(out var settings, out var error)
            ? ReadPassword(globalRoot, settings)
            : (settings, null, "The proxy settings could not be read: " + error);
    }

    /// <summary>Reads the password of the settings from their secret, when they need one.</summary>
    /// <param name="globalRoot">The root of the profile, whose store keeps the secret.</param>
    /// <param name="settings">The settings.</param>
    /// <returns>The settings to apply, the password, and why the settings were not taken (the defaults then are).</returns>
    internal static (ProxySettings Settings, string? Password, string? Error) ReadPassword(string globalRoot, ProxySettings settings)
        => settings is { Mode: ProxyMode.Manual, Authentication: ProxyAuthentication.Password, PasswordSecret: not null }
            ? ReadPassword(new LocalSecretStore(globalRoot), settings)
            : (settings, null, null);

    /// <summary>Reads the password of the settings from a store, when they need one.</summary>
    /// <param name="store">The store that keeps the secret.</param>
    /// <param name="settings">The settings.</param>
    /// <returns>
    /// The settings to apply, the password, and what was wrong. A store that cannot be used (no Keychain, a key that is
    /// gone, a value protected by another key, a secret kept in a vault that is locked) keeps the manual proxy without its
    /// password: requests go through it and get its 407, never around it, and the error says what to do.
    /// </returns>
    internal static (ProxySettings Settings, string? Password, string? Error) ReadPassword(LocalSecretStore store, ProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (settings is not { Mode: ProxyMode.Manual, Authentication: ProxyAuthentication.Password, PasswordSecret: { } secret }) return (settings, null, null);
        try
        {
            if (store.TryGetSecret(secret, out var password)) return (settings, password, null);
        }
        catch (Exception exception) when (IsSecretFailure(exception))
        {
            return (settings, null, $"The proxy password could not be read from the secret {secret}: {exception.Message} "
                + "Check the store of the secrets in Settings > Secrets, then type the password again in Settings > Network.");
        }

        // The proxy is still used: it answers 407 to what it does not let through, which says what is wrong.
        return (settings, null, $"The secret {secret} that keeps the proxy password is not set.");
    }

    /// <summary>Gets whether an exception says that a secret could not be read or kept, rather than a defect.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>True for a store of secrets that cannot be used now.</returns>
    public static bool IsSecretFailure(Exception exception)
        => exception is SecretStoreUnavailableException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
            or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException;
}
