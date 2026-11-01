using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAlta.Browser.Discovery;

/// <summary>
/// Finds the browsers installed on this computer, with their versions and the user's profiles, without starting any:
/// the standard places of each system, the programs Windows registers (<c>App Paths</c>) and the <c>PATH</c>.
/// </summary>
internal static partial class BrowserCatalog
{
    // Where each browser is installed and keeps its data, per system. Paths start with a variable ("%LOCALAPPDATA%"),
    // "~" for the home folder, or "/".
    private sealed record Known(string Id, string Name, BrowserEngine Engine, ImmutableArray<string> Programs, string? Data, ImmutableArray<string>? AppPaths = null, ImmutableArray<string>? Commands = null);

    private static readonly ImmutableArray<Known> Windows =
    [
        new("chrome", "Google Chrome", BrowserEngine.Chromium, Programs(@"Google\Chrome\Application\chrome.exe"), @"%LOCALAPPDATA%\Google\Chrome\User Data", ["chrome.exe"]),
        new("chrome-beta", "Google Chrome Beta", BrowserEngine.Chromium, Programs(@"Google\Chrome Beta\Application\chrome.exe"), @"%LOCALAPPDATA%\Google\Chrome Beta\User Data"),
        new("chrome-dev", "Google Chrome Dev", BrowserEngine.Chromium, Programs(@"Google\Chrome Dev\Application\chrome.exe"), @"%LOCALAPPDATA%\Google\Chrome Dev\User Data"),
        new("chrome-canary", "Google Chrome Canary", BrowserEngine.Chromium, [@"%LOCALAPPDATA%\Google\Chrome SxS\Application\chrome.exe"], @"%LOCALAPPDATA%\Google\Chrome SxS\User Data"),
        new("edge", "Microsoft Edge", BrowserEngine.Chromium, Programs(@"Microsoft\Edge\Application\msedge.exe"), @"%LOCALAPPDATA%\Microsoft\Edge\User Data", ["msedge.exe"]),
        new("edge-beta", "Microsoft Edge Beta", BrowserEngine.Chromium, Programs(@"Microsoft\Edge Beta\Application\msedge.exe"), @"%LOCALAPPDATA%\Microsoft\Edge Beta\User Data"),
        new("edge-dev", "Microsoft Edge Dev", BrowserEngine.Chromium, Programs(@"Microsoft\Edge Dev\Application\msedge.exe"), @"%LOCALAPPDATA%\Microsoft\Edge Dev\User Data"),
        new("edge-canary", "Microsoft Edge Canary", BrowserEngine.Chromium, [@"%LOCALAPPDATA%\Microsoft\Edge SxS\Application\msedge.exe"], @"%LOCALAPPDATA%\Microsoft\Edge SxS\User Data"),
        new("brave", "Brave", BrowserEngine.Chromium, Programs(@"BraveSoftware\Brave-Browser\Application\brave.exe"), @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data", ["brave.exe"]),
        new("vivaldi", "Vivaldi", BrowserEngine.Chromium, Programs(@"Vivaldi\Application\vivaldi.exe"), @"%LOCALAPPDATA%\Vivaldi\User Data", ["vivaldi.exe"]),
        new("chromium", "Chromium", BrowserEngine.Chromium, [@"%LOCALAPPDATA%\Chromium\Application\chrome.exe"], @"%LOCALAPPDATA%\Chromium\User Data"),
        new("firefox", "Firefox", BrowserEngine.Firefox, Programs(@"Mozilla Firefox\firefox.exe"), @"%APPDATA%\Mozilla\Firefox", ["firefox.exe"]),
        new("firefox-developer", "Firefox Developer Edition", BrowserEngine.Firefox, Programs(@"Firefox Developer Edition\firefox.exe"), @"%APPDATA%\Mozilla\Firefox"),
        new("firefox-nightly", "Firefox Nightly", BrowserEngine.Firefox, Programs(@"Firefox Nightly\firefox.exe"), @"%APPDATA%\Mozilla\Firefox"),
    ];

    private static readonly ImmutableArray<Known> Mac =
    [
        new("chrome", "Google Chrome", BrowserEngine.Chromium, App("Google Chrome"), "~/Library/Application Support/Google/Chrome"),
        new("chrome-beta", "Google Chrome Beta", BrowserEngine.Chromium, App("Google Chrome Beta"), "~/Library/Application Support/Google/Chrome Beta"),
        new("chrome-dev", "Google Chrome Dev", BrowserEngine.Chromium, App("Google Chrome Dev"), "~/Library/Application Support/Google/Chrome Dev"),
        new("chrome-canary", "Google Chrome Canary", BrowserEngine.Chromium, App("Google Chrome Canary"), "~/Library/Application Support/Google/Chrome Canary"),
        new("edge", "Microsoft Edge", BrowserEngine.Chromium, App("Microsoft Edge"), "~/Library/Application Support/Microsoft Edge"),
        new("edge-beta", "Microsoft Edge Beta", BrowserEngine.Chromium, App("Microsoft Edge Beta"), "~/Library/Application Support/Microsoft Edge Beta"),
        new("edge-dev", "Microsoft Edge Dev", BrowserEngine.Chromium, App("Microsoft Edge Dev"), "~/Library/Application Support/Microsoft Edge Dev"),
        new("edge-canary", "Microsoft Edge Canary", BrowserEngine.Chromium, App("Microsoft Edge Canary"), "~/Library/Application Support/Microsoft Edge Canary"),
        new("brave", "Brave", BrowserEngine.Chromium, App("Brave Browser"), "~/Library/Application Support/BraveSoftware/Brave-Browser"),
        new("vivaldi", "Vivaldi", BrowserEngine.Chromium, App("Vivaldi"), "~/Library/Application Support/Vivaldi"),
        new("chromium", "Chromium", BrowserEngine.Chromium, App("Chromium"), "~/Library/Application Support/Chromium"),
        new("firefox", "Firefox", BrowserEngine.Firefox, App("Firefox", "firefox"), "~/Library/Application Support/Firefox"),
        new("firefox-developer", "Firefox Developer Edition", BrowserEngine.Firefox, App("Firefox Developer Edition", "firefox"), "~/Library/Application Support/Firefox"),
        new("firefox-nightly", "Firefox Nightly", BrowserEngine.Firefox, App("Firefox Nightly", "firefox"), "~/Library/Application Support/Firefox"),
    ];

    private static readonly ImmutableArray<Known> Linux =
    [
        new("chrome", "Google Chrome", BrowserEngine.Chromium, ["/opt/google/chrome/chrome", "/var/lib/flatpak/exports/bin/com.google.Chrome", "~/.local/share/flatpak/exports/bin/com.google.Chrome"], "~/.config/google-chrome", Commands: ["google-chrome-stable", "google-chrome"]),
        new("chrome-beta", "Google Chrome Beta", BrowserEngine.Chromium, ["/opt/google/chrome-beta/chrome"], "~/.config/google-chrome-beta", Commands: ["google-chrome-beta"]),
        new("chrome-dev", "Google Chrome Dev", BrowserEngine.Chromium, ["/opt/google/chrome-unstable/chrome"], "~/.config/google-chrome-unstable", Commands: ["google-chrome-unstable"]),
        new("chrome-canary", "Google Chrome Canary", BrowserEngine.Chromium, ["/opt/google/chrome-canary/chrome"], "~/.config/google-chrome-canary", Commands: ["google-chrome-canary"]),
        new("edge", "Microsoft Edge", BrowserEngine.Chromium, ["/opt/microsoft/msedge/msedge"], "~/.config/microsoft-edge", Commands: ["microsoft-edge-stable", "microsoft-edge"]),
        new("edge-beta", "Microsoft Edge Beta", BrowserEngine.Chromium, ["/opt/microsoft/msedge-beta/msedge"], "~/.config/microsoft-edge-beta", Commands: ["microsoft-edge-beta"]),
        new("edge-dev", "Microsoft Edge Dev", BrowserEngine.Chromium, ["/opt/microsoft/msedge-dev/msedge"], "~/.config/microsoft-edge-dev", Commands: ["microsoft-edge-dev"]),
        new("brave", "Brave", BrowserEngine.Chromium, ["/opt/brave.com/brave/brave"], "~/.config/BraveSoftware/Brave-Browser", Commands: ["brave-browser", "brave"]),
        new("vivaldi", "Vivaldi", BrowserEngine.Chromium, ["/opt/vivaldi/vivaldi"], "~/.config/vivaldi", Commands: ["vivaldi-stable", "vivaldi"]),
        new("chromium", "Chromium", BrowserEngine.Chromium, ["/usr/lib/chromium/chromium", "/usr/lib/chromium-browser/chromium-browser", "/snap/bin/chromium"], "~/.config/chromium", Commands: ["chromium", "chromium-browser"]),
        new("firefox", "Firefox", BrowserEngine.Firefox, ["/usr/lib/firefox/firefox", "/usr/lib64/firefox/firefox", "/opt/firefox/firefox", "/snap/bin/firefox", "/var/lib/flatpak/exports/bin/org.mozilla.firefox"], "~/.mozilla/firefox", Commands: ["firefox"]),
        new("firefox-developer", "Firefox Developer Edition", BrowserEngine.Firefox, ["/opt/firefox-developer-edition/firefox"], "~/.mozilla/firefox", Commands: ["firefox-developer-edition"]),
        new("firefox-nightly", "Firefox Nightly", BrowserEngine.Firefox, ["/opt/firefox-nightly/firefox"], "~/.mozilla/firefox", Commands: ["firefox-nightly"]),
    ];

    // Other places Linux packages keep the profiles of a browser: Snap and Flatpak, and the XDG folder of newer Firefox.
    private static readonly FrozenDictionary<string, ImmutableArray<string>> LinuxDataAlternatives = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
    {
        ["chrome"] = ["~/.var/app/com.google.Chrome/config/google-chrome"],
        ["chromium"] = ["~/snap/chromium/common/chromium", "~/.var/app/org.chromium.Chromium/config/chromium"],
        ["firefox"] = ["~/.config/mozilla/firefox", "~/snap/firefox/common/.mozilla/firefox", "~/.var/app/org.mozilla.firefox/.mozilla/firefox"],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The order in which the first browser found is taken when nothing else decides.</summary>
    public static readonly ImmutableArray<string> FallbackOrder = ["chrome", "edge", "firefox", "safari"];

    private static ImmutableArray<string> Programs(string relative)
        => [$@"%ProgramFiles%\{relative}", $@"%ProgramFiles(x86)%\{relative}", $@"%LOCALAPPDATA%\{relative}"];

    private static ImmutableArray<string> App(string bundle, string? program = null)
        => [$"/Applications/{bundle}.app/Contents/MacOS/{program ?? bundle}", $"~/Applications/{bundle}.app/Contents/MacOS/{program ?? bundle}"];

    /// <summary>Finds the browsers of this computer.</summary>
    public static IReadOnlyList<InstalledBrowser> Find(BrowserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var known = environment.Platform switch { BrowserPlatform.Windows => Windows, BrowserPlatform.MacOS => Mac, _ => Linux };
        var found = new List<InstalledBrowser>();
        var seen = new HashSet<string>(environment.Platform == BrowserPlatform.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var browser in known)
        {
            var program = Locate(environment, browser);
            if (program is null || !seen.Add(program)) continue;
            var data = DataDirectory(environment, browser);
            found.Add(new InstalledBrowser(browser.Id, browser.Name, browser.Engine, program, ReadVersion(environment, browser, program, data), data,
                data is null ? [] : browser.Engine == BrowserEngine.Firefox ? FirefoxProfiles(data) : ChromiumProfiles(data)));
        }

        if (environment.Platform == BrowserPlatform.MacOS && Resolve(environment, "/Applications/Safari.app") is { } safari && Directory.Exists(safari))
        {
            found.Add(new InstalledBrowser("safari", "Safari", BrowserEngine.Safari, "/usr/bin/safaridriver", ReadPlistVersion(Path.Combine(safari, "Contents", "Info.plist")), null, []));
        }

        return found;
    }

    private static string? Locate(BrowserEnvironment environment, Known browser)
    {
        foreach (var candidate in browser.Programs)
        {
            if (Resolve(environment, candidate) is { } path && File.Exists(path)) return path;
        }

        foreach (var program in browser.AppPaths ?? [])
        {
            if (environment.AppPath(program) is { } path && File.Exists(path)) return path;
        }

        foreach (var command in browser.Commands ?? [])
        {
            if (FindOnPath(environment, command) is { } path) return path;
        }

        return null;
    }

    private static string? FindOnPath(BrowserEnvironment environment, string command)
    {
        foreach (var directory in (environment.Variable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(directory, command);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string? DataDirectory(BrowserEnvironment environment, Known browser)
    {
        if (browser.Data is null) return null;
        var candidates = new List<string?> { Resolve(environment, browser.Data) };
        if (environment.Platform == BrowserPlatform.Linux && LinuxDataAlternatives.TryGetValue(browser.Id, out var alternatives))
        {
            candidates.AddRange(alternatives.Select(path => Resolve(environment, path)));
        }

        // The folder the browser wrote last is the one it uses.
        return candidates.Where(static path => path is not null && Directory.Exists(path))
            .OrderByDescending(static path => Directory.GetLastWriteTimeUtc(path!))
            .FirstOrDefault() ?? candidates[0];
    }

    /// <summary>Expands the variable, or the home folder, a known path starts with; null when the variable is not set.</summary>
    internal static string? Resolve(BrowserEnvironment environment, string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal)) return Path.Combine(environment.Home, path[2..]);
        if (!path.StartsWith('%')) return path;
        var end = path.IndexOf('%', 1);
        if (end < 0) return path;
        var value = environment.Variable(path[1..end]);
        return string.IsNullOrEmpty(value) ? null : value + path[(end + 1)..];
    }

    private static string? ReadVersion(BrowserEnvironment environment, Known browser, string program, string? data)
    {
        try
        {
            if (environment.Platform == BrowserPlatform.Windows && OperatingSystem.IsWindows())
            {
                if (FileVersionInfo.GetVersionInfo(program).ProductVersion is { Length: > 0 } version) return version.Trim();
            }
            else if (environment.Platform == BrowserPlatform.MacOS && program.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal) is var index and > 0)
            {
                if (ReadPlistVersion(Path.Combine(program[..(index + 4)], "Contents", "Info.plist")) is { } version) return version;
            }

            if (browser.Engine == BrowserEngine.Firefox)
            {
                // application.ini sits beside the program; a link in /usr/bin points into the installation.
                var real = new FileInfo(program).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? program;
                foreach (var directory in new[] { Path.GetDirectoryName(real), Path.Combine(Path.GetDirectoryName(real) ?? string.Empty, "..", "Resources") })
                {
                    var ini = directory is null ? null : Path.Combine(directory, "application.ini");
                    if (ini is not null && File.Exists(ini) && IniValue(File.ReadAllLines(ini), "App", "Version") is { } version) return version;
                }
            }
            else if (data is not null && Path.Combine(data, "Last Version") is var last && File.Exists(last))
            {
                return File.ReadAllText(last).Trim();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return null;
    }

    [GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>([^<]+)</string>")]
    private static partial Regex PlistVersion();

    private static string? ReadPlistVersion(string plist)
    {
        try
        {
            return File.Exists(plist) && PlistVersion().Match(File.ReadAllText(plist)) is { Success: true } match ? match.Groups[1].Value.Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The profiles <c>Local State</c> lists, in <c>profile.info_cache</c>.</summary>
    internal static IReadOnlyList<BrowserProfile> ChromiumProfiles(string userData)
    {
        var profiles = new List<BrowserProfile>();
        if (ReadLocalState(userData) is not { } state) return profiles;
        using (state)
        {
            var root = state.RootElement;
            if (!root.TryGetProperty("profile", out var profile) || !profile.TryGetProperty("info_cache", out var cache) || cache.ValueKind != JsonValueKind.Object) return profiles;
            var lastUsed = profile.TryGetProperty("last_used", out var last) && last.ValueKind == JsonValueKind.String ? last.GetString() : "Default";
            foreach (var entry in cache.EnumerateObject())
            {
                var name = entry.Value.TryGetProperty("name", out var shown) && shown.ValueKind == JsonValueKind.String ? shown.GetString()! : entry.Name;
                profiles.Add(new BrowserProfile(name, Path.Combine(userData, entry.Name), entry.Name == lastUsed));
            }
        }

        return profiles;
    }

    /// <summary>Reads <c>Local State</c>, which Chromium replaces whole when it writes it; null when it is missing or unreadable.</summary>
    internal static JsonDocument? ReadLocalState(string userData)
    {
        var path = Path.Combine(userData, "Local State");
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonDocument.Parse(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The profiles <c>profiles.ini</c> lists; the default one is the default of an installation, else the one marked <c>Default=1</c>.</summary>
    internal static IReadOnlyList<BrowserProfile> FirefoxProfiles(string root)
    {
        var ini = Path.Combine(root, "profiles.ini");
        string[] lines;
        try
        {
            if (!File.Exists(ini)) return [];
            lines = File.ReadAllLines(ini);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var sections = new List<(string Name, Dictionary<string, string> Values)>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) sections.Add((line[1..^1], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
            else if (sections.Count != 0 && line.IndexOf('=') is var equal and > 0) sections[^1].Values[line[..equal].Trim()] = line[(equal + 1)..].Trim();
        }

        var installDefaults = sections.Where(static section => section.Name.StartsWith("Install", StringComparison.Ordinal) && section.Values.ContainsKey("Default"))
            .Select(static section => section.Values["Default"]).ToHashSet(StringComparer.Ordinal);
        var profiles = new List<BrowserProfile>();
        foreach (var (name, values) in sections)
        {
            if (!name.StartsWith("Profile", StringComparison.Ordinal) || !values.TryGetValue("Path", out var path)) continue;
            var relative = !values.TryGetValue("IsRelative", out var isRelative) || isRelative == "1";
            var directory = relative ? Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))) : path;
            var isDefault = installDefaults.Count != 0 ? installDefaults.Contains(path) : values.TryGetValue("Default", out var flag) && flag == "1";
            profiles.Add(new BrowserProfile(values.TryGetValue("Name", out var shown) ? shown : path, directory, isDefault));
        }

        return profiles;
    }

    private static string? IniValue(string[] lines, string section, string key)
    {
        var inside = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) inside = line == $"[{section}]";
            else if (inside && line.StartsWith(key + "=", StringComparison.Ordinal)) return line[(key.Length + 1)..].Trim();
        }

        return null;
    }
}
