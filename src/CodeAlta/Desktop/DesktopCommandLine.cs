using System.Globalization;
using System.Net;
using System.Reflection;
using CodeAlta.Hosting;

namespace CodeAlta.Desktop;

internal sealed record DesktopLaunchOptions(string DataRoot, string? CatalogRoot)
{
    internal OwnedDesktopRoots? Owned { get; init; }

    /// <summary>The root of the state this instance alone writes; null keeps it in the catalog root.</summary>
    internal string? StateRoot { get; init; }

    /// <summary>Whether this is the developer instance, running beside the normal one on the same profile.</summary>
    internal bool Developer { get; init; }

    /// <summary>
    /// This start only asks the CodeAlta already running with this profile to exit (it may be running with
    /// its window closed), and starts nothing itself.
    /// </summary>
    internal bool ExitRunning { get; init; }

    /// <summary>
    /// Stay with what started this process until the application exits, instead of giving a terminal its
    /// prompt back (see <see cref="DesktopTerminalStart"/>).
    /// </summary>
    internal bool Wait { get; init; }

    /// <summary>
    /// The application was started by another program (<c>--automated</c>, <c>CODEALTA_AUTOMATED</c>), which also exits
    /// it: closing the window, <c>alta --exit</c> and SIGTERM ask nothing, and the application exits once no session is
    /// at work.
    /// </summary>
    internal bool Automated { get; init; }

    /// <summary>The token of the start that waits for this application's window; null when none does.</summary>
    internal string? StartToken { get; init; }
    internal bool ReviewOwnedCommandPermissions { get; init; }
    internal bool EnableOwnedUserInput { get; init; }

    /// <summary>
    /// The address the MCP server listens on: <c>localhost</c>, an IP address or a host name; null for the loopback
    /// address.
    /// </summary>
    internal string? McpHost { get; init; }

    /// <summary>The port the MCP server listens on, 0 for a free one; null for the port of this kind of instance.</summary>
    internal int? McpPort { get; init; }

    /// <summary>
    /// The folders this start adds as projects (<c>alta .</c>, <c>alta &lt;folder&gt;...</c>), as full paths, in their
    /// order: the running application adds them and shows the first one. Empty for none.
    /// </summary>
    internal IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary>The spaces the folders of <see cref="Projects"/> join (<c>--space</c>): ids, starts of ids or names. Empty for none.</summary>
    internal IReadOnlyList<string> Spaces { get; init; } = [];
}
internal sealed record OwnedDesktopRoots(string Project, string? Home, string? Instructions, string? Builtin);

internal static class DesktopCommandLine
{
    /// <summary>Asks the running instance to exit instead of starting one.</summary>
    internal const string ExitOption = "--exit";

    /// <summary>Keeps the terminal until the application exits.</summary>
    internal const string WaitOption = "--wait";

    /// <summary>Names the address the MCP server listens on.</summary>
    internal const string McpHostOption = "--mcp-host";

    /// <summary>Names the port the MCP server listens on.</summary>
    internal const string McpPortOption = "--mcp-port";

    /// <summary>Names a space the folders given join.</summary>
    internal const string SpaceOption = "--space";

    /// <summary>Says that another program started the application and exits it (see <see cref="DesktopLaunchOptions.Automated"/>).</summary>
    internal const string AutomatedOption = "--automated";

    /// <summary>The environment variable that does what <see cref="AutomatedOption"/> does.</summary>
    internal const string AutomatedVariable = "CODEALTA_AUTOMATED";

    /// <summary>Largest number of folders one start adds.</summary>
    internal const int MaximumProjects = 64;

    private const string InvalidOptions = "Unknown or invalid options. Run alta without options to start CodeAlta Desktop, or alta --help to list the options.";

    private const string HelpText = """
        CodeAlta Desktop

        Usage:
          alta                Start CodeAlta Desktop for the current folder.
          alta <folder>...    Add the folders as projects and show the first one.
          alta . --space <s>  Add the current folder as a project and put it in a space.
          alta --dev          Start the developer instance beside the normal one.
          alta --wait         Start it and keep the terminal until it exits.
          alta --exit         Ask the running CodeAlta Desktop to exit.
          alta --version      Print the version.
          alta --help, -h     Print this help.

        alta opens the current folder as a project and uses the ~/.alta profile, which it shares
        with CodeAlta TUI (altatui). It runs the built-in plugins (MCP, Git, Statistics) and the
        source plugins of ~/.alta/plugins and of the current project. Set CODEALTA_DISABLE_PLUGINS=1
        to start without plugins. When CodeAlta Desktop is already running, alta shows its window.

        alta . adds the current folder as a project, and alta <folder>... the folders named, whether
        CodeAlta Desktop is running or not; the window then shows the first one. A folder that is
        already a project is shown. --space <space> (an id, the start of an id or a name; repeat it
        for several) puts the folders in that space too. Every project is in the default space.

        In a terminal, alta gives the prompt back once the window is shown. --wait keeps the
        terminal instead, alone or with --dev.

        --dev runs a second instance on the same profile. It shares configuration, providers,
        credentials, prompts, skills and projects, and keeps its own sessions under ~/.alta/dev.

        --exit asks first when files have unsaved edits or sessions are running. Use
        alta --dev --exit for the developer instance. Exit before updating with
        dotnet tool update -g CodeAlta.

        --automated, or CODEALTA_AUTOMATED=1, is for a program that starts and exits CodeAlta
        Desktop by itself. Closing its window, alta --exit and SIGTERM then ask nothing: it
        exits once no session is at work, and unsaved edits are lost.

        CodeAlta Desktop runs an MCP server on this computer for the applications that drive it,
        at http://127.0.0.1:2582/mcp (port 2583 with --dev). Settings shows the address and turns
        the server off. Any start that opens the window can say where it listens:
          --mcp-port <port>      The port; 0 takes a free one.
          --mcp-host <address>   localhost, an IP address or a host name of this computer, such
                                 as its Tailscale name. An address other computers can reach
                                 asks for the access token that Settings shows.

        Isolated roots, for tests and development. The ~/.alta profile is not used, and every
        directory is an absolute path outside .alta:
          alta --data-root <new directory> --catalog-root <catalog copy> --allow-catalog-cache
              Browse a copy of a catalog. The cache of that copy can be written.
          ... --allow-owned-host --project-root <directory> --discovery-home <directory>
              --instruction-root <project ancestor> --builtin-skill-root <directory>
              Also run sessions on that copy. Plugins are not started.
          ... --review-owned-command-permissions
              Review each command request (Allow once / Deny / Cancel).
          ... --enable-owned-user-input
              Answer provider input forms. Do not enter secrets in them.

        Documentation: https://codealta.github.io
        """;

    internal static string Version => typeof(DesktopCommandLine).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";

    internal static int Run(string[] args, TextWriter output, TextWriter error, Func<DesktopLaunchOptions, int> startNative)
    {
        if (args is ["--help"] or ["-h"])
        {
            output.WriteLine(HelpText);
            return 0;
        }

        if (args is ["--version"])
        {
            output.WriteLine($"alta {Version}");
            return 0;
        }

        try
        {
            if (!TryParse(args, Directory.Exists, File.Exists, out var options, out var message))
            {
                error.WriteLine(message);
                return 2;
            }

            if (IsAutomated(Environment.GetEnvironmentVariable(AutomatedVariable))) options = options! with { Automated = true };
            // Admission grants only desktop-owned browser storage and opt-in cache writes in a trusted copy.
            return startNative(options!);
        }
        catch (Exception exception)
        {
            error.WriteLine(exception.Message);
            return 1;
        }
    }

    /// <summary>
    /// Whether the value of <see cref="AutomatedVariable"/> turns the automated start on: any value but an empty one,
    /// <c>0</c> or <c>false</c>.
    /// </summary>
    internal static bool IsAutomated(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed != "0" && !trimmed.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    // Mandatory CLI seam. Tests supply existence facts without opening any storage or native services.
    internal static bool TryParse(string[] args, Func<string, bool> directoryExists, Func<string, bool> fileExists,
        out DesktopLaunchOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(fileExists);
        options = null;
        error = InvalidOptions;
        // An automated start goes with any start, alta --exit included, where it changes nothing: the running
        // application decides how it exits.
        var end = Array.IndexOf(args, "--");
        var automated = args.Take(end < 0 ? args.Length : end).Count(static arg => arg == AutomatedOption);
        if (automated > 1) return false;
        if (automated == 1) args = [.. args.Where((arg, index) => arg != AutomatedOption || (end >= 0 && index > end))];
        if (!TryParseStart(args, directoryExists, fileExists, out options, out error)) return false;
        if (automated == 1) options = options! with { Automated = true };
        return true;
    }

    private static bool TryParseStart(string[] args, Func<string, bool> directoryExists, Func<string, bool> fileExists,
        out DesktopLaunchOptions? options, out string? error)
    {
        options = null;
        error = InvalidOptions;
        // Where the MCP server listens goes with any start that runs the application.
        if (!TryTakeMcpEndpoint(ref args, out var mcpHost, out var mcpPort)) return false;
        if (mcpHost is not null || mcpPort is not null)
        {
            if (!TryParseStart(args, directoryExists, fileExists, out options, out error)) return false;
            if (options!.ExitRunning)
            {
                options = null;
                error = InvalidOptions;
                return false;
            }

            options = options with { McpHost = mcpHost, McpPort = mcpPort };
            return true;
        }

        if (TryParseNormalStart(args, directoryExists, out options, out error) is { } normal) return normal;
        error = InvalidOptions;
        string? data = null;
        string? catalog = null;
        var allowCache = false;
        var allowOwned = false;
        var reviewCommands = false;
        var enableUserInput = false;
        string? project = null, home = null, instructions = null, builtin = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-root" when data is null && i + 1 < args.Length:
                    data = args[++i];
                    break;
                case "--catalog-root" when catalog is null && i + 1 < args.Length:
                    catalog = args[++i];
                    break;
                case "--allow-catalog-cache" when !allowCache:
                    allowCache = true;
                    break;
                case "--allow-owned-host" when !allowOwned: allowOwned = true; break;
                case "--review-owned-command-permissions" when !reviewCommands: reviewCommands = true; break;
                case "--enable-owned-user-input" when !enableUserInput: enableUserInput = true; break;
                case "--project-root" when project is null && i + 1 < args.Length: project = args[++i]; break;
                case "--discovery-home" when home is null && i + 1 < args.Length: home = args[++i]; break;
                case "--instruction-root" when instructions is null && i + 1 < args.Length: instructions = args[++i]; break;
                case "--builtin-skill-root" when builtin is null && i + 1 < args.Length: builtin = args[++i]; break;
                default: return false;
            }
        }
        if (string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data) || (catalog is not null) != allowCache) return false;
        if (catalog is not null && (string.IsNullOrWhiteSpace(catalog) || !Path.IsPathFullyQualified(catalog))) return false;
        if (ContainsAlta(data) || (catalog is not null && ContainsAlta(catalog))) return false;
        data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(data));
        catalog = catalog is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(catalog));
        if (ContainsAlta(data) || (catalog is not null && (ContainsAlta(catalog) || Overlap(data, catalog)))) return false;
        if (directoryExists(data) || fileExists(data)) return false;
        if (catalog is not null && (fileExists(catalog) || !directoryExists(catalog))) return false;
        options = new DesktopLaunchOptions(data, catalog);
        if (allowOwned || reviewCommands || enableUserInput || project is not null || home is not null || instructions is not null || builtin is not null)
        {
            options = null;
            if (!allowOwned || catalog is null || !allowCache) return false;
            var roots = new[] { project, home, instructions, builtin };
            if (roots.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || ContainsAlta(path))) return false;
            var normalized = roots.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!))).ToArray();
            if (normalized.Any(path => ContainsAlta(path) || !directoryExists(path) || fileExists(path))) return false;
            if (new[] { normalized[0], normalized[1], normalized[3] }.Any(path => Overlap(data, path))) return false;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var ancestor = Path.EndsInDirectorySeparator(normalized[2]) ? normalized[2] : normalized[2] + Path.DirectorySeparatorChar;
            if (!string.Equals(normalized[0], normalized[2], comparison) && !normalized[0].StartsWith(ancestor, comparison)) return false;
            options = new DesktopLaunchOptions(data, catalog) { Owned = new(normalized[0], normalized[1], normalized[2], normalized[3]), ReviewOwnedCommandPermissions = reviewCommands, EnableOwnedUserInput = enableUserInput };
        }
        error = null;
        return true;
    }

    // A start on the ~/.alta profile: --dev, --wait or --exit, and the folders it adds as projects with the spaces they
    // join. Null when the arguments are not those of such a start (the isolated roots, an unknown option).
    private static bool? TryParseNormalStart(string[] args, Func<string, bool> directoryExists, out DesktopLaunchOptions? options, out string? error)
    {
        options = null;
        error = InvalidOptions;
        var end = Array.IndexOf(args, "--");
        if (args.Take(end < 0 ? args.Length : end).Any(static arg => arg.StartsWith('-')
            && arg is not (CodeAltaInstanceProfile.DeveloperOption or ExitOption or WaitOption or SpaceOption)))
            return null;
        bool developer = false, exit = false, wait = false, folders = false;
        var projects = new List<string>();
        var spaces = new List<string>();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!folders)
            {
                switch (arg)
                {
                    case "--": folders = true; continue;
                    case CodeAltaInstanceProfile.DeveloperOption when !developer: developer = true; continue;
                    case ExitOption when !exit: exit = true; continue;
                    case WaitOption when !wait: wait = true; continue;
                    case SpaceOption when i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith('-'):
                        spaces.Add(args[++i].Trim());
                        continue;
                    case CodeAltaInstanceProfile.DeveloperOption or ExitOption or WaitOption or SpaceOption:
                        return false;
                }
            }

            // A folder, read from the folder this start runs in.
            string path;
            try
            {
                path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(arg));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = $"'{arg}' is not the path of a folder.";
                return false;
            }

            if (!directoryExists(path))
            {
                error = $"The folder '{arg}' does not exist.";
                return false;
            }

            if (!projects.Contains(path, comparer)) projects.Add(path);
        }

        if (exit && (wait || projects.Count > 0 || spaces.Count > 0)) return false;
        if (spaces.Count > 0 && projects.Count == 0)
        {
            error = "--space puts the folders given in a space: name a folder too, as in alta . --space work.";
            return false;
        }

        if (projects.Count > MaximumProjects)
        {
            error = $"alta adds at most {MaximumProjects} folders at once.";
            return false;
        }

        options = CreateDefaultOptions(developer, projects.FirstOrDefault()) with { ExitRunning = exit, Wait = wait, Projects = projects, Spaces = spaces };
        error = null;
        return true;
    }

    internal static DesktopLaunchOptions CreateDefaultOptions() => CreateDefaultOptions(developer: false);

    internal static DesktopLaunchOptions CreateDefaultOptions(bool developer) => CreateDefaultOptions(developer, null);

    // The folder the application starts on: the first folder given, else the current one.
    private static DesktopLaunchOptions CreateDefaultOptions(bool developer, string? project)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            throw new InvalidOperationException("Unable to determine the user profile directory for CodeAlta.");
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            localData = Path.Combine(userProfile, ".local", "share");
        var profile = CodeAltaInstanceProfile.Create(Path.Combine(userProfile, ".alta"), developer);
        return new DesktopLaunchOptions(
            // The developer instance has its own WebView profile: its own tabs and drafts, and its own
            // remote debugging port when one is requested.
            Path.Combine(localData, "CodeAlta", developer ? "desktop-dev" : "desktop"),
            profile.GlobalRoot)
        {
            // Match the TUI's normal startup: current project, standard profile, and the
            // built-in discovery defaults rather than the restricted explicit-root scope.
            Owned = new(project ?? Path.GetFullPath(Environment.CurrentDirectory), null, null, null),
            StateRoot = developer ? profile.StateRoot : null,
            Developer = developer,
        };
    }

    // Takes the two MCP options out of the arguments. False when one is given twice, without a value or with
    // a value that names no address or no port.
    private static bool TryTakeMcpEndpoint(ref string[] args, out string? host, out int? port)
    {
        host = null;
        port = null;
        if (Array.IndexOf(args, McpHostOption) < 0 && Array.IndexOf(args, McpPortOption) < 0) return true;
        var rest = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case McpHostOption when host is null && i + 1 < args.Length && IsMcpHost(args[i + 1]):
                    host = args[++i];
                    break;
                case McpPortOption when port is null && i + 1 < args.Length
                    && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value <= IPEndPoint.MaxPort:
                    port = value;
                    i++;
                    break;
                case McpHostOption or McpPortOption:
                    return false;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        args = [.. rest];
        return true;
    }

    /// <summary>
    /// Whether <paramref name="host"/> names an address a server can listen on: <c>localhost</c>, an IP address or
    /// the host name of this computer.
    /// </summary>
    internal static bool IsMcpHost(string? host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        // An IPv4 address is written with its four parts: a number alone also parses as one.
        (host is { Length: > 0 } && !host.Contains('%') && IPAddress.TryParse(host, out var address) &&
            (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 || host.Count(static c => c == '.') == 3)) ||
        IsHostName(host);

    // A DNS name, such as the name of the computer on a Tailscale network: labels of letters, digits and hyphens,
    // with a letter somewhere so that it is not an address written another way.
    private static bool IsHostName(string? host)
    {
        if (host is not { Length: > 0 and <= 253 } || !host.Any(char.IsAsciiLetter)) return false;
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            if (!label.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        }

        return true;
    }

    private static bool ContainsAlta(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.TrimEnd(' ', '.').Equals(".alta", StringComparison.OrdinalIgnoreCase));

    private static bool Overlap(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(first, second, comparison) ||
            first.StartsWith(Path.EndsInDirectorySeparator(second) ? second : second + Path.DirectorySeparatorChar, comparison) ||
            second.StartsWith(Path.EndsInDirectorySeparator(first) ? first : first + Path.DirectorySeparatorChar, comparison);
    }
}
