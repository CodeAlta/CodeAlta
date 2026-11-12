using System.Collections.Immutable;

namespace CodeAlta.Catalog.Network;

/// <summary>How CodeAlta and the programs it starts reach the network.</summary>
public enum ProxyMode
{
    /// <summary>As the system says: the proxy variables of the environment, else the settings of the operating system (the default).</summary>
    System,

    /// <summary>Directly, without a proxy, whatever the environment and the system say.</summary>
    None,

    /// <summary>Through the proxy of <see cref="ProxySettings.Url"/>, but for the hosts of <see cref="ProxySettings.Bypass"/> and this computer.</summary>
    Manual,
}

/// <summary>How CodeAlta signs in to a manual proxy.</summary>
public enum ProxyAuthentication
{
    /// <summary>It does not: the proxy asks for nothing (the default).</summary>
    None,

    /// <summary>With the account the application runs under (Windows integrated authentication, Kerberos), when the proxy asks.</summary>
    Account,

    /// <summary>With <see cref="ProxySettings.User"/> and the password kept in the secret <see cref="ProxySettings.PasswordSecret"/>.</summary>
    Password,
}

/// <summary>
/// The proxy of CodeAlta, from the global <c>[network.proxy]</c> table: the way to the network of the application itself
/// (its HTTP clients that use the default proxy) and of the programs it starts (agent CLIs, git, npm, MCP servers, the shells
/// of sessions and the terminals), through the variables <c>HTTP_PROXY</c>, <c>HTTPS_PROXY</c> and <c>NO_PROXY</c>.
/// </summary>
/// <param name="Mode">How the network is reached.</param>
/// <param name="Url">The address of the proxy, <c>http://proxy.corp:8080</c>; kept in the other modes, so that a return to the manual one finds it.</param>
/// <param name="Bypass">The hosts reached without the proxy: names (a name also covers its subdomains, <c>*.</c> or <c>.</c> before it says the same), addresses and CIDR ranges.</param>
/// <param name="Authentication">How CodeAlta signs in to the proxy.</param>
/// <param name="User">The user name of <see cref="ProxyAuthentication.Password"/>.</param>
/// <param name="PasswordSecret">The name of the CodeAlta secret that keeps the password: never the password itself.</param>
public sealed record ProxySettings(ProxyMode Mode, string? Url, ImmutableArray<string> Bypass, ProxyAuthentication Authentication, string? User, string? PasswordSecret)
{
    /// <summary>The name of <see cref="ProxyMode.System"/> in the configuration.</summary>
    public const string SystemName = "system";

    /// <summary>The name of <see cref="ProxyMode.None"/> and of <see cref="ProxyAuthentication.None"/> in the configuration.</summary>
    public const string NoneName = "none";

    /// <summary>The name of <see cref="ProxyMode.Manual"/> in the configuration.</summary>
    public const string ManualName = "manual";

    /// <summary>The name of <see cref="ProxyAuthentication.Account"/> in the configuration.</summary>
    public const string AccountName = "account";

    /// <summary>The name of <see cref="ProxyAuthentication.Password"/> in the configuration.</summary>
    public const string PasswordName = "password";

    /// <summary>The secret the settings page keeps the password in.</summary>
    public const string DefaultPasswordSecret = "NETWORK_PROXY_PASSWORD";

    private const int MaximumUrlLength = 2048;
    private const int MaximumBypassEntries = 256;
    private const int MaximumUserLength = 256;

    /// <summary>Gets the settings when the configuration says nothing: as the system says.</summary>
    public static ProxySettings Default { get; } = new(ProxyMode.System, null, [], ProxyAuthentication.None, null, null);

    /// <summary>Gets the address of the proxy, when it is a valid one.</summary>
    public Uri? ProxyUri => TryParseUrl(Url, out var uri) ? uri : null;

    /// <summary>Reads the global proxy table.</summary>
    /// <param name="document">The table; null for none.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="InvalidDataException">A value is not one the table takes (<see cref="GetError"/>).</exception>
    public static ProxySettings Read(CodeAltaProxySettingsDocument? document)
    {
        if (document is null) return Default;
        if (ParseMode(document.Mode) is not { } mode) throw new InvalidDataException($"The proxy mode must be {SystemName}, {NoneName} or {ManualName}.");
        if (ParseAuthentication(document.Authentication) is not { } authentication)
            throw new InvalidDataException($"The proxy authentication must be {NoneName}, {AccountName} or {PasswordName}.");
        var settings = new ProxySettings(mode, Normalize(document.Url), [.. (document.NoProxy ?? []).Select(static entry => entry?.Trim() ?? "")],
            authentication, Normalize(document.User), Normalize(document.PasswordSecret));
        if (settings.GetError() is { } error) throw new InvalidDataException(error);
        return settings;
    }

    /// <summary>Gets the table that writes these settings, without what is the default; null when everything is.</summary>
    /// <returns>The table, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">The settings are not valid (<see cref="GetError"/>).</exception>
    public CodeAltaProxySettingsDocument? ToDocument()
    {
        if (GetError() is { } error) throw new ArgumentException(error);
        var document = new CodeAltaProxySettingsDocument
        {
            Mode = Mode == ProxyMode.System ? null : NameOf(Mode),
            Url = Normalize(Url),
            NoProxy = Bypass.IsDefaultOrEmpty ? null : [.. Bypass],
            Authentication = Authentication == ProxyAuthentication.None ? null : NameOf(Authentication),
            User = Normalize(User),
            PasswordSecret = Normalize(PasswordSecret),
        };
        return document is { Mode: null, Url: null, NoProxy: null, Authentication: null, User: null, PasswordSecret: null } ? null : document;
    }

    /// <summary>Gets why these settings cannot be used, or null when they can.</summary>
    /// <returns>The error, or <see langword="null"/>.</returns>
    public string? GetError()
    {
        if (!Enum.IsDefined(Mode)) return $"The proxy mode must be {SystemName}, {NoneName} or {ManualName}.";
        if (!Enum.IsDefined(Authentication)) return $"The proxy authentication must be {NoneName}, {AccountName} or {PasswordName}.";
        if (Url is not null && GetUrlError(Url) is { } urlError) return urlError;
        if (Mode == ProxyMode.Manual && Url is null) return "A manual proxy needs the address of the proxy, such as http://proxy.example.com:8080.";
        var bypass = Bypass.IsDefault ? [] : Bypass;
        if (bypass.Length > MaximumBypassEntries) return $"The proxy bypass list has more than {MaximumBypassEntries} entries.";
        foreach (var entry in bypass)
        {
            if (ProxyBypassList.GetEntryError(entry) is { } entryError) return entryError;
        }

        if (User is { Length: > MaximumUserLength } || User?.Any(char.IsControl) == true) return "The user name of the proxy is not valid.";
        if (PasswordSecret is not null && !Secrets.LocalSecretStore.IsName(PasswordSecret))
            return "The secret of the proxy password must be a secret name: letters, digits and _, not starting with a digit.";
        if (Authentication == ProxyAuthentication.Password && Mode == ProxyMode.Manual && (User is null || PasswordSecret is null))
            return "A proxy password needs a user name and the secret that keeps the password.";
        return null;
    }

    /// <summary>Gets why the address of a proxy is not one CodeAlta uses, or null when it is.</summary>
    /// <param name="url">The address, such as <c>http://proxy.example.com:8080</c>; one without a scheme is taken as <c>http://</c>.</param>
    /// <returns>The error, or <see langword="null"/>.</returns>
    public static string? GetUrlError(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaximumUrlLength) return "The address of the proxy must be http://host:port.";
        if (WithScheme(url.Trim()) is var text && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
            return "The address of the proxy must not hold a user name or a password: they go in its authentication, and the password in a secret.";
        return TryParseUrl(url, out _) ? null : "The address of the proxy must be http://host:port (or https://), without a path.";
    }

    /// <summary>Reads the address of a proxy: <c>http</c> or <c>https</c>, a host, an optional port, no path, query or user.</summary>
    /// <param name="url">The address; one without a scheme is taken as <c>http://</c>.</param>
    /// <param name="uri">The address read.</param>
    /// <returns>True when the address is one CodeAlta uses.</returns>
    public static bool TryParseUrl(string? url, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaximumUrlLength) return false;
        if (!Uri.TryCreate(WithScheme(url.Trim()), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (parsed.Host.Length == 0 || parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0) return false;
        if (parsed.AbsolutePath is not ("/" or "")) return false;
        uri = parsed;
        return true;
    }

    /// <summary>Reads a bypass list as a user types it: entries separated by commas, semicolons, spaces or lines.</summary>
    /// <param name="text">The text; null for none.</param>
    /// <returns>The entries, without the empty ones, each once.</returns>
    public static ImmutableArray<string> ParseBypass(string? text)
        => string.IsNullOrWhiteSpace(text) ? []
            : [.. text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Reads the name of a mode; null (absent) is <see cref="ProxyMode.System"/>.</summary>
    /// <param name="text">The name, in any case.</param>
    /// <returns>The mode, or null when the name is not one.</returns>
    public static ProxyMode? ParseMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or SystemName => ProxyMode.System,
        NoneName => ProxyMode.None,
        ManualName => ProxyMode.Manual,
        _ => null,
    };

    /// <summary>Reads the name of an authentication; null (absent) is <see cref="ProxyAuthentication.None"/>.</summary>
    /// <param name="text">The name, in any case.</param>
    /// <returns>The authentication, or null when the name is not one.</returns>
    public static ProxyAuthentication? ParseAuthentication(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or NoneName => ProxyAuthentication.None,
        AccountName => ProxyAuthentication.Account,
        PasswordName => ProxyAuthentication.Password,
        _ => null,
    };

    /// <summary>Gets the name of a mode in the configuration.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The name.</returns>
    public static string NameOf(ProxyMode mode) => mode switch
    {
        ProxyMode.None => NoneName,
        ProxyMode.Manual => ManualName,
        _ => SystemName,
    };

    /// <summary>Gets the name of an authentication in the configuration.</summary>
    /// <param name="authentication">The authentication.</param>
    /// <returns>The name.</returns>
    public static string NameOf(ProxyAuthentication authentication) => authentication switch
    {
        ProxyAuthentication.Account => AccountName,
        ProxyAuthentication.Password => PasswordName,
        _ => NoneName,
    };

    private static string WithScheme(string url) => url.Contains("://", StringComparison.Ordinal) ? url : "http://" + url;

    private static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
