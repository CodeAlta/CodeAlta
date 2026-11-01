namespace CodeAlta.Browser.Discovery;

/// <summary>The engine of a browser, which says how CodeAlta drives it.</summary>
internal enum BrowserEngine
{
    /// <summary>Chrome, Edge and the other Chromium browsers: the Chrome DevTools Protocol.</summary>
    Chromium,

    /// <summary>Firefox: WebDriver BiDi on the endpoint Firefox opens itself.</summary>
    Firefox,

    /// <summary>Safari: its own MCP server, <c>safaridriver --mcp</c>.</summary>
    Safari,
}

/// <summary>The kind of profile a session browses with.</summary>
internal enum BrowserProfileKind
{
    /// <summary>A new private profile per session, deleted when the session ends.</summary>
    Isolated,

    /// <summary>A profile CodeAlta keeps for agents, where the user signs in once.</summary>
    Persistent,

    /// <summary>The user's own browser, profile and sign-ins.</summary>
    User,
}

/// <summary>A profile of a browser the user has, as the browser lists it.</summary>
/// <param name="Name">The name the browser shows, such as <c>Person 1</c> or <c>default-release</c>.</param>
/// <param name="Directory">The folder of the profile.</param>
/// <param name="IsDefault">Whether the browser opens this profile by default.</param>
internal sealed record BrowserProfile(string Name, string Directory, bool IsDefault);

/// <summary>A browser installed on this computer.</summary>
/// <param name="Id">The id sessions and settings name it by, such as <c>chrome</c>, <c>edge-beta</c> or <c>firefox</c>.</param>
/// <param name="Name">Its name, such as <c>Google Chrome</c>.</param>
/// <param name="Engine">Its engine.</param>
/// <param name="ExecutablePath">The program to start; for Safari, <c>safaridriver</c>.</param>
/// <param name="Version">Its version, or null when it could not be read without starting it.</param>
/// <param name="DataDirectory">Where it keeps its profiles: the user data folder of a Chromium browser, the folder of <c>profiles.ini</c> for Firefox; or null.</param>
/// <param name="Profiles">The user's profiles of it.</param>
internal sealed record InstalledBrowser(
    string Id,
    string Name,
    BrowserEngine Engine,
    string ExecutablePath,
    string? Version,
    string? DataDirectory,
    IReadOnlyList<BrowserProfile> Profiles)
{
    /// <summary>Gets the major version, or 0 when it is not known.</summary>
    public int MajorVersion => Version is { } version && int.TryParse(version.AsSpan(0, version.IndexOf('.') is var dot and > 0 ? dot : version.Length), out var major) ? major : 0;

    /// <summary>Whether a session can browse with this browser in a profile of a kind, and why not.</summary>
    /// <returns>Null when it can, else the reason, for the agent.</returns>
    public string? Refusal(BrowserProfileKind profile) => (Engine, profile) switch
    {
        (BrowserEngine.Safari, BrowserProfileKind.Persistent or BrowserProfileKind.User) =>
            "Safari's automation always runs in a private window: it never reaches the user's profile, and it keeps no profile of its own.",
        (BrowserEngine.Chromium, BrowserProfileKind.User) when !Id.StartsWith("chrome", StringComparison.Ordinal) && !Id.StartsWith("edge", StringComparison.Ordinal) =>
            $"{Name} cannot be driven in the user's own profile: only Chrome, Edge and Firefox can.",
        (BrowserEngine.Chromium, BrowserProfileKind.User) when MajorVersion is > 0 and < 144 =>
            $"{Name} {Version} cannot be driven in the user's own profile: that takes version 144 or later. The persistent profile works with it.",
        _ => null,
    };
}
