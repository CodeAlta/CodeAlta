using System.Net;
using System.Net.Http;

namespace CodeAlta.Catalog.Network;

/// <summary>
/// The proxy of the application, as <see cref="ProxySettings"/> says, in the two places it reaches: the HTTP clients of the
/// process that use the default proxy (this instance is <see cref="HttpClient.DefaultProxy"/>, see <see cref="Install"/>), and
/// the environment of the process, which the programs it starts inherit (<c>HTTP_PROXY</c>, <c>HTTPS_PROXY</c>,
/// <c>NO_PROXY</c>, in both cases).
/// </summary>
/// <remarks>
/// <para>
/// A change applies at once to the next request of every client that uses the default proxy, even one created before: the
/// clients ask this instance for each request. A connection already open stays as it is until it closes, and a program
/// already started keeps the environment it started with.
/// </para>
/// <para>
/// A client made with <c>UseProxy = false</c> (the loopback, the network of a sandbox) never asks: it stays as it is. The
/// environment of a sandboxed session is built by the sandbox from names it chooses, so it keeps the proxy of the sandbox.
/// </para>
/// </remarks>
public sealed partial class NetworkProxy : IWebProxy
{
    /// <summary>The variables of the environment the programs read their proxy from, in both cases.</summary>
    public static IReadOnlyList<string> Variables { get; } = ["HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "NO_PROXY", "no_proxy", "ALL_PROXY", "all_proxy"];

    private readonly IWebProxy? _system;
    private readonly Action<string, string?> _setVariable;
    private readonly KeyValuePair<string, string?>[] _original;
    private readonly Lock _gate = new();
    private readonly ProxyCredentials _credentials;
    private State _state;

    /// <summary>Initializes a proxy that does as the system says until <see cref="Apply"/> is called.</summary>
    /// <param name="system">The proxy of the system, read before the environment is changed; null for none.</param>
    /// <param name="getVariable">Reads a variable of the environment of the process.</param>
    /// <param name="setVariable">Sets a variable of the environment of the process; a null value removes it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="getVariable"/> or <paramref name="setVariable"/> is null.</exception>
    public NetworkProxy(IWebProxy? system, Func<string, string?> getVariable, Action<string, string?> setVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        ArgumentNullException.ThrowIfNull(setVariable);
        _system = system;
        _setVariable = setVariable;
        // What the system mode gives back: the variables as the process started with them.
        _original = [.. Variables.Select(name => new KeyValuePair<string, string?>(name, getVariable(name)))];
        _state = new State(ProxySettings.Default, null, null, null);
        _credentials = new ProxyCredentials(this);
        Upstream = new ManualUpstream(this);
    }

    /// <summary>Gets the settings applied.</summary>
    public ProxySettings Settings => Volatile.Read(ref _state).Settings;

    /// <summary>Gets the proxy of the system, which the system mode uses; null for none.</summary>
    public IWebProxy? SystemProxy => _system;

    /// <summary>
    /// Gets the proxy the proxy of a sandbox reaches the internet through: the manual proxy, while the manual mode applies,
    /// with its bypass list and its credentials; in the other modes it bypasses every host, so that a sandbox connects
    /// directly, as it always did.
    /// </summary>
    public IWebProxy Upstream { get; }

    /// <summary>Gets the <see cref="Upstream"/> of the proxy of the process (<see cref="Install"/>), or null when none is installed.</summary>
    public static IWebProxy? ProcessUpstream => HttpClient.DefaultProxy is NetworkProxy proxy ? proxy.Upstream : null;

    /// <summary>
    /// Gets the credentials given to a proxy that asks for them: those of the system proxy, of the account or of the user
    /// name and password of the settings. A client reads them when a proxy asks, so a change of the settings applies to them too.
    /// </summary>
    /// <remarks>
    /// The clients keep the instance they read first, so it stays: setting them changes those of the system proxy, which
    /// the system mode gives.
    /// </remarks>
    public ICredentials? Credentials
    {
        get => _credentials;
        set
        {
            if (_system is not null) _system.Credentials = value;
        }
    }

    /// <summary>
    /// Makes this instance the default proxy of the HTTP clients of the process (<see cref="HttpClient.DefaultProxy"/>), over
    /// the proxy of the system read first, or gives the instance already installed. Call it at the start, before the first
    /// request.
    /// </summary>
    /// <returns>The proxy of the process.</returns>
    public static NetworkProxy Install()
    {
        if (HttpClient.DefaultProxy is NetworkProxy installed) return installed;
        // The system proxy reads the variables of the environment as they are now, before a manual mode changes them.
        var proxy = new NetworkProxy(HttpClient.DefaultProxy, Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable);
        HttpClient.DefaultProxy = proxy;
        return proxy;
    }

    /// <summary>Applies settings: to the next requests of the clients, and to the environment of the programs started from now on.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="password">The password of <see cref="ProxyAuthentication.Password"/>, read from its secret; null when there is none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentException">The settings are not valid (<see cref="ProxySettings.GetError"/>).</exception>
    public void Apply(ProxySettings settings, string? password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.GetError() is { } error) throw new ArgumentException(error, nameof(settings));
        var state = new State(settings, settings.Mode == ProxyMode.Manual ? new ProxyBypassList(settings.Bypass.IsDefault ? [] : settings.Bypass) : null,
            CredentialOf(settings, password), password);
        lock (_gate)
        {
            Volatile.Write(ref _state, state);
            var variables = settings.Mode == ProxyMode.System ? _original : EnvironmentOf(settings, password);
            foreach (var (name, value) in variables) _setVariable(name, value);
        }
    }

    /// <summary>
    /// Sets the proxy variables of the settings applied in an environment built for a program, such as one that read the
    /// variables of the user again (a terminal on Windows): nothing in the system mode, the proxy or its absence otherwise.
    /// </summary>
    /// <param name="environment">The environment of the program.</param>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    public void ApplyTo(IDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var state = Volatile.Read(ref _state);
        if (state.Settings.Mode == ProxyMode.System) return;
        foreach (var (name, value) in EnvironmentOf(state.Settings, state.Password))
        {
            if (value is null) environment.Remove(name);
            else environment[name] = value;
        }
    }

    /// <summary>
    /// Gets the proxy variables of the programs CodeAlta starts in the none and manual modes: the address of the proxy (with
    /// the user name and password of <see cref="ProxyAuthentication.Password"/>, which the programs need to sign in) and the
    /// hosts reached directly, this computer always among them, or the absence of every proxy variable; nothing for the system mode.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="password">The password of <see cref="ProxyAuthentication.Password"/>, or null.</param>
    /// <returns>Each variable with its value; a null value removes the variable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public static IReadOnlyList<KeyValuePair<string, string?>> EnvironmentOf(ProxySettings settings, string? password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Mode == ProxyMode.System) return [];
        if (settings.Mode == ProxyMode.None || settings.ProxyUri is not { } uri) return [.. Variables.Select(static name => new KeyValuePair<string, string?>(name, null))];
        var address = AddressOf(uri, settings.Authentication == ProxyAuthentication.Password ? settings.User : null, password);
        var bypass = string.Join(',', (settings.Bypass.IsDefault ? [] : settings.Bypass).Select(ProxyBypassList.ToEnvironmentEntry).OfType<string>()
            .Concat(ProxyBypassList.LoopbackEntries).Distinct(StringComparer.OrdinalIgnoreCase));
        return
        [
            new("HTTP_PROXY", address), new("http_proxy", address), new("HTTPS_PROXY", address), new("https_proxy", address),
            new("NO_PROXY", bypass), new("no_proxy", bypass), new("ALL_PROXY", null), new("all_proxy", null),
        ];
    }

    /// <summary>Creates the proxy of settings not applied, such as the ones a check of the connection tries.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="password">The password of <see cref="ProxyAuthentication.Password"/>, or null.</param>
    /// <param name="system">The proxy of the system mode; null for none.</param>
    /// <returns>The proxy, or null to reach the network directly.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentException">The settings are not valid (<see cref="ProxySettings.GetError"/>).</exception>
    public static IWebProxy? Create(ProxySettings settings, string? password, IWebProxy? system)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.GetError() is { } error) throw new ArgumentException(error, nameof(settings));
        if (settings.Mode == ProxyMode.System) return system;
        if (settings.Mode == ProxyMode.None) return null;
        var bypass = new ProxyBypassList(settings.Bypass.IsDefault ? [] : settings.Bypass);
        return new ManualProxy(new WebProxy(settings.ProxyUri!) { Credentials = CredentialOf(settings, password) }, bypass);
    }

    /// <inheritdoc />
    public Uri? GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var state = Volatile.Read(ref _state);
        return state.Settings.Mode switch
        {
            ProxyMode.System => _system?.GetProxy(destination),
            ProxyMode.Manual when !state.Bypass!.Matches(destination.IdnHost) => state.Settings.ProxyUri,
            _ => null,
        };
    }

    /// <inheritdoc />
    public bool IsBypassed(Uri host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var state = Volatile.Read(ref _state);
        return state.Settings.Mode switch
        {
            ProxyMode.System => _system?.IsBypassed(host) ?? true,
            ProxyMode.Manual => state.Bypass!.Matches(host.IdnHost),
            _ => true,
        };
    }

    /// <summary>Gets the address of a proxy as it is shown: its scheme, host and port, never the user name and password it may hold.</summary>
    /// <param name="proxy">The address of the proxy, such as one the system mode gives from <c>HTTPS_PROXY=http://user:password@host:port</c>.</param>
    /// <returns>The address, such as <c>http://proxy.corp:8080</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="proxy"/> is null.</exception>
    public static string DisplayAddress(Uri proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        return proxy.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    /// <summary>Removes the user names and passwords of the addresses a text holds (<c>http://user:password@host</c>), and the values given.</summary>
    /// <param name="text">The text, such as the message of a failed request; null for none.</param>
    /// <param name="secrets">Values to remove as well, such as the password a check tried.</param>
    /// <returns>The text without them.</returns>
    public static string? RedactCredentials(string? text, params ReadOnlySpan<string?> secrets)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret)) continue;
            text = text.Replace(secret, "***", StringComparison.Ordinal);
            var escaped = Uri.EscapeDataString(secret);
            if (escaped != secret) text = text.Replace(escaped, "***", StringComparison.Ordinal);
        }

        return UserInfoPattern().Replace(text, "${scheme}://");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*)://[^\s/@'""]+@")]
    private static partial System.Text.RegularExpressions.Regex UserInfoPattern();

    // The address of the proxy for the programs, the user name and password escaped as a URL takes them.
    private static string AddressOf(Uri uri, string? user, string? password)
    {
        var address = uri.GetLeftPart(UriPartial.Authority);
        if (user is null) return address;
        var credentials = Uri.EscapeDataString(user) + (string.IsNullOrEmpty(password) ? "" : ":" + Uri.EscapeDataString(password));
        return $"{uri.Scheme}://{credentials}@{uri.Authority}";
    }

    private static NetworkCredential? CredentialOf(ProxySettings settings, string? password) => settings.Authentication switch
    {
        ProxyAuthentication.Account => CredentialCache.DefaultNetworkCredentials,
        // DOMAIN\user signs in to a domain, as Windows writes it.
        ProxyAuthentication.Password when settings.User is { } user => user.IndexOf('\\') is var slash and > 0
            ? new NetworkCredential(user[(slash + 1)..], password ?? "", user[..slash])
            : new NetworkCredential(user, password ?? ""),
        _ => null,
    };

    private sealed record State(ProxySettings Settings, ProxyBypassList? Bypass, NetworkCredential? Credential, string? Password);

    /// <summary>Gets whether two addresses name the same proxy: the same scheme, host and port.</summary>
    /// <param name="left">An address.</param>
    /// <param name="right">Another address, or null.</param>
    /// <returns>True when both name the same proxy.</returns>
    internal static bool SameProxy(Uri left, Uri? right)
        => right is not null && string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    // The credentials the clients keep: those of the settings applied when a proxy asks, for the proxy of those settings
    // alone. A request that went to the proxy of before, and gets its 407 after a change, is given nothing: the password of
    // one proxy never goes to another.
    private sealed class ProxyCredentials(NetworkProxy owner) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            ArgumentNullException.ThrowIfNull(uri);
            var state = Volatile.Read(ref owner._state);
            return state.Settings.Mode switch
            {
                ProxyMode.System => owner._system?.Credentials?.GetCredential(uri, authType),
                ProxyMode.Manual when SameProxy(uri, state.Settings.ProxyUri) => state.Credential,
                _ => null,
            };
        }
    }

    // The manual proxy while it applies, for the proxy of a sandbox; every host is reached directly in the other modes.
    private sealed class ManualUpstream(NetworkProxy owner) : IWebProxy
    {
        public ICredentials? Credentials { get => owner._credentials; set { } }

        public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : Volatile.Read(ref owner._state).Settings.ProxyUri;

        public bool IsBypassed(Uri host)
        {
            ArgumentNullException.ThrowIfNull(host);
            var state = Volatile.Read(ref owner._state);
            return state.Settings.Mode != ProxyMode.Manual || state.Bypass!.Matches(host.IdnHost);
        }
    }

    // A manual proxy with the bypass list of CodeAlta: names, subdomains, CIDR ranges and this computer.
    private sealed class ManualProxy(WebProxy proxy, ProxyBypassList bypass) : IWebProxy
    {
        public ICredentials? Credentials { get => new ScopedCredentials(proxy); set => proxy.Credentials = value; }

        public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : proxy.Address;

        public bool IsBypassed(Uri host) => bypass.Matches(host.IdnHost);
    }

    // The credentials of one proxy, given to that proxy alone.
    private sealed class ScopedCredentials(WebProxy proxy) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType)
            => SameProxy(uri, proxy.Address) ? proxy.Credentials?.GetCredential(uri, authType) : null;
    }
}
