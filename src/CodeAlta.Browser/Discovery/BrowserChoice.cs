using System.Globalization;
using System.Text.Json;

namespace CodeAlta.Browser.Discovery;

/// <summary>What says that a browser is used: when it was used last, and whether it is the system's default browser.</summary>
/// <param name="Browser">The browser.</param>
/// <param name="LastUsed">When the user browsed with it last, or null when no file says so.</param>
/// <param name="IsDefault">Whether the system opens web links with it.</param>
/// <param name="Measurable">Whether its use can be read at all: macOS keeps the files of Safari out of reach.</param>
internal sealed record BrowserUsage(InstalledBrowser Browser, DateTimeOffset? LastUsed, bool IsDefault, bool Measurable = true);

/// <summary>Where the browser of a session came from.</summary>
internal enum BrowserChoiceSource
{
    /// <summary>The session named it, because the user or the task did.</summary>
    User,

    /// <summary>The preferred browser of Settings.</summary>
    Settings,

    /// <summary>Automatic: the browser the user uses on this computer.</summary>
    Auto,
}

/// <summary>The browser chosen for a session, and why.</summary>
/// <param name="Browser">The browser.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Reason">Why, in a few words for the user and the agent.</param>
internal sealed record BrowserChoiceResult(InstalledBrowser Browser, BrowserChoiceSource Source, string Reason);

/// <summary>
/// Chooses the browser of a session: the one the session names, then the preferred browser of Settings, then the
/// browser the user uses, from the files each browser writes while the user browses and the system's default browser.
/// </summary>
/// <remarks>
/// Automatic reads file times and the profile entries of <c>Local State</c> only: never a history, a cookie or a page.
/// </remarks>
internal static class BrowserChoice
{
    /// <summary>How close the use of the default browser has to be to the one used last for the default to win.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);

    /// <summary>Reads the evidence of use of each browser.</summary>
    public static IReadOnlyList<BrowserUsage> ReadUsage(BrowserEnvironment environment, IReadOnlyList<InstalledBrowser> browsers)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(browsers);
        var defaultHint = SafeDefault(environment);
        var defaultId = defaultHint is null ? null : MatchDefault(environment.Platform, browsers, defaultHint)?.Id;
        return [.. browsers.Select(browser =>
        {
            var isDefault = browser.Id == defaultId;
            return browser.Engine switch
            {
                BrowserEngine.Safari => new BrowserUsage(browser, environment.IsRunning("Safari") ? environment.Time.GetUtcNow() : null, isDefault, Measurable: false),
                BrowserEngine.Firefox => new BrowserUsage(browser, FirefoxLastUse(browser), isDefault),
                _ => new BrowserUsage(browser, ChromiumLastUse(browser), isDefault),
            };
        })];
    }

    private static string? SafeDefault(BrowserEnvironment environment)
    {
        try
        {
            return environment.DefaultBrowser();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The browser the system's answer names: a program path, an application bundle, or a desktop file id.</summary>
    internal static InstalledBrowser? MatchDefault(BrowserPlatform platform, IReadOnlyList<InstalledBrowser> browsers, string hint)
    {
        switch (platform)
        {
            case BrowserPlatform.Windows:
                return browsers.FirstOrDefault(browser => string.Equals(Path.GetFullPath(browser.ExecutablePath), SafeFull(hint), StringComparison.OrdinalIgnoreCase));
            case BrowserPlatform.MacOS:
                var bundle = hint.TrimEnd('/') + "/";
                if (bundle.EndsWith("/Safari.app/", StringComparison.Ordinal)) return browsers.FirstOrDefault(static browser => browser.Engine == BrowserEngine.Safari);
                return browsers.FirstOrDefault(browser => browser.ExecutablePath.StartsWith(bundle, StringComparison.Ordinal));
            default:
                var id = Path.GetFileNameWithoutExtension(hint).ToLowerInvariant();
                string? browserId = id switch
                {
                    _ when id.Contains("chrome-beta", StringComparison.Ordinal) => "chrome-beta",
                    _ when id.Contains("chrome-unstable", StringComparison.Ordinal) => "chrome-dev",
                    _ when id.Contains("chrome-canary", StringComparison.Ordinal) => "chrome-canary",
                    _ when id.Contains("google-chrome", StringComparison.Ordinal) || id == "com.google.chrome" => "chrome",
                    _ when id.Contains("edge-beta", StringComparison.Ordinal) => "edge-beta",
                    _ when id.Contains("edge-dev", StringComparison.Ordinal) => "edge-dev",
                    _ when id.Contains("microsoft-edge", StringComparison.Ordinal) || id.Contains("msedge", StringComparison.Ordinal) => "edge",
                    _ when id.Contains("brave", StringComparison.Ordinal) => "brave",
                    _ when id.Contains("vivaldi", StringComparison.Ordinal) => "vivaldi",
                    _ when id.Contains("chromium", StringComparison.Ordinal) => "chromium",
                    _ when id.Contains("firefox-developer", StringComparison.Ordinal) => "firefox-developer",
                    _ when id.Contains("firefox-nightly", StringComparison.Ordinal) => "firefox-nightly",
                    _ when id.Contains("firefox", StringComparison.Ordinal) => "firefox",
                    _ => null,
                };
                return browserId is null ? null : browsers.FirstOrDefault(browser => browser.Id == browserId);
        }
    }

    private static string SafeFull(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>
    /// The last use of a Chromium browser: the newest <c>active_time</c> of its profiles in <c>Local State</c>, which
    /// Chromium refreshes at most once an hour while a window of the profile is used, and the times of the profiles'
    /// <c>History</c> and <c>Sessions/</c>. A background start (Edge's startup boost) writes none of them.
    /// </summary>
    internal static DateTimeOffset? ChromiumLastUse(InstalledBrowser browser)
    {
        if (browser.DataDirectory is not { } userData) return null;
        DateTimeOffset? latest = null;
        if (BrowserCatalog.ReadLocalState(userData) is { } state)
        {
            using (state)
            {
                if (state.RootElement.TryGetProperty("profile", out var profile) && profile.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in cache.EnumerateObject())
                    {
                        if (entry.Value.TryGetProperty("active_time", out var active) && active.TryGetDouble(out var seconds) && seconds > 0)
                        {
                            latest = Max(latest, DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)));
                        }
                    }
                }
            }
        }

        foreach (var profile in browser.Profiles)
        {
            latest = Max(latest, FileTime(Path.Combine(profile.Directory, "History")));
            latest = Max(latest, NewestIn(Path.Combine(profile.Directory, "Sessions")));
        }

        return latest;
    }

    /// <summary>The last use of Firefox: the times of the files of its profiles it writes while the user browses.</summary>
    internal static DateTimeOffset? FirefoxLastUse(InstalledBrowser browser)
    {
        DateTimeOffset? latest = null;
        foreach (var profile in browser.Profiles)
        {
            foreach (var file in new[] { "places.sqlite", "places.sqlite-wal", Path.Combine("sessionstore-backups", "recovery.jsonlz4"), "sessionstore.jsonlz4", "prefs.js" })
            {
                latest = Max(latest, FileTime(Path.Combine(profile.Directory, file)));
            }
        }

        return latest;
    }

    private static DateTimeOffset? FileTime(string path)
    {
        try
        {
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A system that keeps the folder out of reach (macOS privacy): that evidence is missing, nothing more.
            return null;
        }
    }

    private static DateTimeOffset? NewestIn(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;
            DateTimeOffset? newest = null;
            foreach (var file in Directory.EnumerateFiles(directory)) newest = Max(newest, FileTime(file));
            return newest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
        => left is null ? right : right is null ? left : left > right ? left : right;

    /// <summary>Chooses the browser of a session.</summary>
    /// <param name="usages">The browsers found, with their evidence.</param>
    /// <param name="requested">The browser the session names (an id of <see cref="InstalledBrowser.Id"/>, a name, or a program path), or null.</param>
    /// <param name="preferred">The preferred browser of Settings, or null or <c>auto</c>.</param>
    /// <param name="profile">The profile the session asks for: some browsers cannot browse in it.</param>
    /// <param name="now">The time now, for the reason.</param>
    /// <param name="refusal">Why no browser fits, for the agent.</param>
    /// <returns>The choice, or null with <paramref name="refusal"/>.</returns>
    public static BrowserChoiceResult? Choose(IReadOnlyList<BrowserUsage> usages, string? requested, string? preferred, BrowserProfileKind profile, DateTimeOffset now, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(usages);
        refusal = null;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (Find(usages, requested) is not { } named)
            {
                refusal = usages.Count == 0
                    ? $"No browser named \"{requested}\" is installed: CodeAlta found no browser on this computer."
                    : $"No browser named \"{requested}\" is installed. The browsers of this computer: {string.Join(", ", usages.Select(static usage => usage.Browser.Id))}.";
                return null;
            }

            if (named.Browser.Refusal(profile) is { } why)
            {
                refusal = why;
                return null;
            }

            return new BrowserChoiceResult(named.Browser, BrowserChoiceSource.User, "named by the session");
        }

        var preferredNote = string.Empty;
        if (!string.IsNullOrWhiteSpace(preferred) && !string.Equals(preferred, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (Find(usages, preferred) is { } chosen && chosen.Browser.Refusal(profile) is null)
            {
                return new BrowserChoiceResult(chosen.Browser, BrowserChoiceSource.Settings, "preferred in Settings");
            }

            preferredNote = Find(usages, preferred) is null ? $"; {preferred}, preferred in Settings, is not installed" : $"; {preferred}, preferred in Settings, cannot use this profile";
        }

        var candidates = usages.Where(usage => usage.Browser.Refusal(profile) is null).ToList();
        if (candidates.Count == 0)
        {
            refusal = usages.Count == 0
                ? "No browser was found on this computer: install Chrome, Edge or Firefox, or name the program of one in Settings > Extensions > Browser."
                : $"No browser of this computer can browse in the {profile.ToString().ToLowerInvariant()} profile: {string.Join(" ", usages.Select(static usage => usage.Browser.Refusal(BrowserProfileKind.User)).Where(static why => why is not null).Distinct())}";
            return null;
        }

        var latest = candidates.Where(static usage => usage.LastUsed is not null).MaxBy(static usage => usage.LastUsed);
        var defaultBrowser = candidates.FirstOrDefault(static usage => usage.IsDefault);
        if (defaultBrowser is not null)
        {
            // A browser whose use cannot be read counts by its default status alone.
            if (!defaultBrowser.Measurable || latest is null || ReferenceEquals(latest, defaultBrowser))
            {
                var used = defaultBrowser.LastUsed is { } time ? $", used {Ago(now, time)}" : string.Empty;
                return new BrowserChoiceResult(defaultBrowser.Browser, BrowserChoiceSource.Auto, $"the default browser{used}{preferredNote}");
            }

            if (defaultBrowser.LastUsed is { } defaultTime && latest.LastUsed - defaultTime < DefaultWindow)
            {
                return new BrowserChoiceResult(defaultBrowser.Browser, BrowserChoiceSource.Auto, $"the default browser, used within a day of {latest.Browser.Name}{preferredNote}");
            }
        }

        if (latest is not null)
        {
            return new BrowserChoiceResult(latest.Browser, BrowserChoiceSource.Auto, $"used most recently, {Ago(now, latest.LastUsed!.Value)}{preferredNote}");
        }

        var first = BrowserCatalog.FallbackOrder.Select(id => candidates.FirstOrDefault(usage => usage.Browser.Id == id)).FirstOrDefault(static usage => usage is not null) ?? candidates[0];
        return new BrowserChoiceResult(first.Browser, BrowserChoiceSource.Auto, $"the first browser found{preferredNote}");
    }

    private static BrowserUsage? Find(IReadOnlyList<BrowserUsage> usages, string name)
    {
        var trimmed = name.Trim();
        return usages.FirstOrDefault(usage => string.Equals(usage.Browser.Id, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? usages.FirstOrDefault(usage => string.Equals(usage.Browser.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? usages.FirstOrDefault(usage => string.Equals(usage.Browser.ExecutablePath, trimmed, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            // "Chrome" names Google Chrome, "Edge" Microsoft Edge.
            ?? usages.FirstOrDefault(usage => usage.Browser.Name.EndsWith(" " + trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A time before now, as a few words: "2 hours ago".</summary>
    internal static string Ago(DateTimeOffset now, DateTimeOffset then)
    {
        var elapsed = now - then;
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        static string Plural(int value, string unit) => string.Create(CultureInfo.InvariantCulture, $"{value} {unit}{(value == 1 ? string.Empty : "s")} ago");
        if (elapsed < TimeSpan.FromHours(1)) return Plural((int)elapsed.TotalMinutes, "minute");
        if (elapsed < TimeSpan.FromDays(1)) return Plural((int)elapsed.TotalHours, "hour");
        return Plural((int)elapsed.TotalDays, "day");
    }
}
