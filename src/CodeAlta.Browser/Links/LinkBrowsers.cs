using CodeAlta.Browser.Discovery;

namespace CodeAlta.Browser.Links;

/// <summary>A browser a link the user follows can open in, with the profiles it can open it in.</summary>
/// <param name="Id">The id a setting names it by, such as <c>chrome</c>, <c>edge</c>, <c>firefox</c> or <c>safari</c>.</param>
/// <param name="Name">Its name, such as <c>Google Chrome</c>.</param>
/// <param name="SupportsProfiles">Whether a link can be opened in a profile of it chosen beforehand: Chromium browsers and Firefox, not Safari.</param>
/// <param name="Profiles">The names of its profiles, as it shows them; empty when it has none that can be chosen.</param>
/// <param name="IsDefault">Whether it is the default browser of the system.</param>
public sealed record LinkBrowser(string Id, string Name, bool SupportsProfiles, IReadOnlyList<string> Profiles, bool IsDefault);

/// <summary>The program to start to open a link in a browser, and its arguments.</summary>
/// <param name="FileName">The program.</param>
/// <param name="Arguments">Its arguments, each passed as it is, the address last.</param>
/// <param name="ProfileFound">False when a profile was asked for that the browser does not have: the link opens in the profile the browser opens.</param>
public sealed record LinkLaunch(string FileName, IReadOnlyList<string> Arguments, bool ProfileFound = true);

/// <summary>
/// Finds the browsers of this computer that links can open in, and says how to open a link in one of them, in a profile
/// of it: Chromium browsers take <c>--profile-directory</c> and Firefox <c>-P</c>, each handing the link to the browser
/// already running; Safari takes no profile. It starts nothing itself, and reads only the lists of profiles the browsers keep.
/// </summary>
public sealed class LinkBrowsers
{
    private readonly BrowserEnvironment _environment;

    /// <summary>Creates the browsers of this computer.</summary>
    public LinkBrowsers()
        : this(BrowserEnvironment.Current())
    {
    }

    internal LinkBrowsers(BrowserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <summary>Lists the browsers of this computer, the default one marked.</summary>
    /// <returns>The browsers, in the order they were found.</returns>
    public IReadOnlyList<LinkBrowser> Find()
    {
        var installed = BrowserCatalog.Find(_environment);
        var defaultId = DefaultOf(installed)?.Id;
        return [.. installed.Select(browser => new LinkBrowser(browser.Id, browser.Name, SupportsProfiles(browser),
            SupportsProfiles(browser)
                ? [.. browser.Profiles.Select(static profile => profile.Name.Trim()).Where(static name => name.Length > 0).Distinct(StringComparer.Ordinal)]
                : [],
            browser.Id == defaultId))];
    }

    /// <summary>Says how to open a link in a browser, in a profile of it.</summary>
    /// <param name="address">The address to open.</param>
    /// <param name="browser">The id of the browser (<see cref="LinkBrowser.Id"/>); null for the default browser of the system.</param>
    /// <param name="profile">The name of the profile; null for the profile the browser opens.</param>
    /// <returns>
    /// What to start; null when the system opens the link as it does any link: nothing asked for, a browser that is not
    /// installed, or a default browser that cannot be told.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="address"/> is null or empty.</exception>
    public LinkLaunch? Plan(string address, string? browser, string? profile)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        if (string.IsNullOrWhiteSpace(browser) && string.IsNullOrWhiteSpace(profile)) return null;
        var installed = BrowserCatalog.Find(_environment);
        return Plan(_environment.Platform, installed, DefaultOf(installed), address, browser, profile);
    }

    /// <summary>How to open a link, from the browsers found and the default one.</summary>
    internal static LinkLaunch? Plan(BrowserPlatform platform, IReadOnlyList<InstalledBrowser> installed, InstalledBrowser? defaultBrowser, string address,
        string? browser, string? profile)
    {
        var wanted = string.IsNullOrWhiteSpace(browser) ? null : browser.Trim();
        var target = wanted is null ? defaultBrowser : installed.FirstOrDefault(candidate => string.Equals(candidate.Id, wanted, StringComparison.OrdinalIgnoreCase));
        if (target is null) return null;
        var name = string.IsNullOrWhiteSpace(profile) ? null : profile.Trim();
        if (name is null || !SupportsProfiles(target))
        {
            // The default browser in the profile it opens is what the system does already.
            return wanted is null ? null : Plain(platform, target, address);
        }

        var chosen = target.Profiles.FirstOrDefault(candidate => candidate.Name.AsSpan().Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? (target.Engine == BrowserEngine.Chromium
                ? target.Profiles.FirstOrDefault(candidate => string.Equals(Path.GetFileName(candidate.Directory), name, StringComparison.OrdinalIgnoreCase))
                : null);
        if (chosen is null && target.Profiles.Count == 0)
        {
            // A browser always has a profile: none listed is a list the system kept out of reach (macOS privacy). The
            // name is then given as it is written: Firefox finds a profile by its name (unless written like a command-line
            // switch), and a Chromium browser takes the folder of one, which only a folder name such as "Profile 1" can be,
            // never a name that would make a new profile.
            if (target.Engine == BrowserEngine.Firefox && IsFirefoxProfileName(name)) return WithProfile(platform, target, ["-P", name], address);
            if (target.Engine == BrowserEngine.Chromium && IsChromiumProfileFolder(name)) return WithProfile(platform, target, ["--profile-directory=" + name], address);
        }

        if (chosen is null || target.Engine == BrowserEngine.Firefox && !IsFirefoxProfileName(chosen.Name))
        {
            var plain = wanted is null ? null : Plain(platform, target, address);
            return plain is null ? null : plain with { ProfileFound = false };
        }

        // Started with the profile, the program hands the link to the browser that runs, which opens it in that profile.
        return target.Engine == BrowserEngine.Firefox
            ? WithProfile(platform, target, ["-P", chosen.Name], address)
            : WithProfile(platform, target, ["--profile-directory=" + Path.GetFileName(chosen.Directory)], address);
    }

    // A browser with the arguments of a profile. On macOS through `open -n -a`: the system starts the program, which hands the
    // link to the browser that runs or becomes the browser, never a process of CodeAlta that would end with it; `-n`
    // because `--args` reaches an application only when it starts.
    private static LinkLaunch WithProfile(BrowserPlatform platform, InstalledBrowser browser, IReadOnlyList<string> profile, string address)
        => platform == BrowserPlatform.MacOS && BundleOf(browser.ExecutablePath) is { } bundle
            ? new LinkLaunch("/usr/bin/open", ["-n", "-a", bundle, "--args", .. profile, address])
            : new LinkLaunch(browser.ExecutablePath, [.. profile, address]);

    /// <summary>Whether a text is the folder of a profile of a Chromium browser: <c>Default</c> or <c>Profile 1</c>, <c>Profile 2</c>…</summary>
    internal static bool IsChromiumProfileFolder(string name)
        => string.Equals(name, "Default", StringComparison.Ordinal)
            || name.StartsWith("Profile ", StringComparison.Ordinal) && name.Length is > 8 and <= 12 && name.AsSpan(8).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>Whether Firefox's <c>-P</c> takes a text as the name of a profile rather than as a command-line switch.</summary>
    internal static bool IsFirefoxProfileName(string name)
        => name.Length > 0 && name[0] is not ('-' or '/');

    /// <summary>Whether a link can be opened in a profile of a browser chosen beforehand.</summary>
    internal static bool SupportsProfiles(InstalledBrowser browser) => browser.Engine is BrowserEngine.Chromium or BrowserEngine.Firefox;

    // A browser without a profile: on macOS, the application, as the system opens it; elsewhere its program.
    private static LinkLaunch Plain(BrowserPlatform platform, InstalledBrowser browser, string address)
    {
        if (platform == BrowserPlatform.MacOS)
        {
            var bundle = browser.Engine == BrowserEngine.Safari ? "/Applications/Safari.app" : BundleOf(browser.ExecutablePath);
            if (bundle is not null) return new LinkLaunch("/usr/bin/open", ["-a", bundle, address]);
        }

        return new LinkLaunch(browser.ExecutablePath, [address]);
    }

    private static string? BundleOf(string program)
        => program.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal) is var index and > 0 ? program[..(index + 4)] : null;

    private InstalledBrowser? DefaultOf(IReadOnlyList<InstalledBrowser> installed)
    {
        try
        {
            return _environment.DefaultBrowser() is { } hint ? BrowserChoice.MatchDefault(_environment.Platform, installed, hint) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return null;
        }
    }
}
