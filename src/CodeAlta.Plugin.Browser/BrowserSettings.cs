using System.Globalization;
using System.Text.Json.Serialization;
using CodeAlta.Browser.Discovery;

namespace CodeAlta.Plugin.Browser;

/// <summary>
/// The settings of the browser, the user's own: kept by the plugin in <c>~/.alta/plugin-data/browser/settings.json</c>,
/// where a repository cannot change them.
/// </summary>
internal sealed record BrowserSettings
{
    /// <summary>Gets the preferred browser: <c>auto</c>, the id of an installed browser, or the path of a program.</summary>
    [JsonPropertyName("browser")]
    public string Browser { get; init; } = "auto";

    /// <summary>Gets the engine of the program of <see cref="Browser"/> when it is a path: <c>chromium</c> or <c>firefox</c>.</summary>
    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    /// <summary>Gets the profile of a session that names none: <c>isolated</c>, <c>persistent</c>, <c>persistent:&lt;name&gt;</c> or <c>user</c>.</summary>
    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "isolated";

    /// <summary>Gets whether the browsers CodeAlta starts run without a window.</summary>
    [JsonPropertyName("headless")]
    public bool Headless { get; init; }

    /// <summary>Gets whether sessions get <c>evaluate_script</c>.</summary>
    [JsonPropertyName("evaluateScript")]
    public bool EvaluateScript { get; init; } = true;

    /// <summary>Gets the settings of the user's own browser.</summary>
    [JsonPropertyName("userProfile")]
    public BrowserUserProfileSettings UserProfile { get; init; } = new();

    /// <summary>The settings with every value valid: what is not falls back to its default.</summary>
    public BrowserSettings Normalized() => this with
    {
        Browser = string.IsNullOrWhiteSpace(Browser) ? "auto" : Browser.Trim(),
        Engine = Engine is "chromium" or "firefox" ? Engine : null,
        Profile = BrowserProfileSpec.TryParse(Profile, out var profile) ? profile.ToString() : "isolated",
        UserProfile = (UserProfile ?? new()) with
        {
            AllowedSites = BrowserSites.NormalizeAll(UserProfile?.AllowedSites),
            BlockedSites = BrowserSites.NormalizeAll(UserProfile?.BlockedSites),
        },
    };
}

/// <summary>The settings of the user's own browser.</summary>
internal sealed record BrowserUserProfileSettings
{
    /// <summary>Gets whether sessions may ask for the user's own browser. Off until the user turns it on.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>Gets the sites the user allowed for every session, subdomains included.</summary>
    [JsonPropertyName("allowedSites")]
    public IReadOnlyList<string> AllowedSites { get; init; } = [];

    /// <summary>Gets the sites no session may load in the user's browser, subdomains included.</summary>
    [JsonPropertyName("blockedSites")]
    public IReadOnlyList<string> BlockedSites { get; init; } = [];
}

/// <summary>A profile a session browses in: its kind, and the name of a persistent one.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Name">The name of a persistent profile; null for the others.</param>
internal sealed record BrowserProfileSpec(BrowserProfileKind Kind, string? Name)
{
    /// <summary>The name of the persistent profile that names none.</summary>
    public const string DefaultName = "default";

    /// <summary>Gets the isolated profile.</summary>
    public static BrowserProfileSpec Isolated { get; } = new(BrowserProfileKind.Isolated, null);

    /// <summary>Reads <c>isolated</c>, <c>persistent</c>, <c>persistent:&lt;name&gt;</c> or <c>user</c>.</summary>
    public static bool TryParse(string? text, out BrowserProfileSpec profile)
    {
        profile = Isolated;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Equals("isolated", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            profile = new(BrowserProfileKind.User, null);
            return true;
        }

        if (value.Equals("persistent", StringComparison.OrdinalIgnoreCase))
        {
            profile = new(BrowserProfileKind.Persistent, DefaultName);
            return true;
        }

        if (value.StartsWith("persistent:", StringComparison.OrdinalIgnoreCase) && IsValidName(value["persistent:".Length..]))
        {
            profile = new(BrowserProfileKind.Persistent, value["persistent:".Length..].ToLowerInvariant());
            return true;
        }

        return false;
    }

    /// <summary>Whether a name can name a persistent profile: letters, digits, <c>-</c> and <c>_</c>, at most 64.</summary>
    public static bool IsValidName(string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= 64 && name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        BrowserProfileKind.Persistent => Name == DefaultName ? "persistent" : $"persistent:{Name}",
        BrowserProfileKind.User => "user",
        _ => "isolated",
    };
}

/// <summary>The sites of the user's own browser: a host without its <c>www.</c>, which also stands for its subdomains.</summary>
internal static class BrowserSites
{
    /// <summary>The site of a host or an address, or null when it has none (a <c>file:</c> address, <c>about:blank</c>).</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        string host;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.IdnHost)) return null;
            host = url.IdnHost;
        }
        else
        {
            host = text.TrimEnd('/');
            if (host.Contains('/') || host.Contains(' ')) return null;
            try
            {
                host = new IdnMapping().GetAscii(host);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4) host = host[4..];
        return host.Length is > 0 and <= 253 ? host : null;
    }

    /// <summary>The site an address loads, or null for an address of no site.</summary>
    public static string? Of(Uri url) => url.Scheme is "http" or "https" ? Normalize(url.IdnHost) : null;

    /// <summary>Whether a site is one of a list: the same site, or a subdomain of one.</summary>
    public static bool Matches(string site, IEnumerable<string> list)
        => list.Any(entry => site == entry || site.EndsWith("." + entry, StringComparison.Ordinal));

    /// <summary>A list of sites, each valid and once.</summary>
    public static IReadOnlyList<string> NormalizeAll(IEnumerable<string>? sites)
        => sites is null ? [] : [.. sites.Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
