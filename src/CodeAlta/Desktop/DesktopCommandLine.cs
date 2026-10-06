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

    /// <summary>The token of the start that waits for this application's window; null when none does.</summary>
    internal string? StartToken { get; init; }
    internal bool ReviewOwnedCommandPermissions { get; init; }
    internal bool EnableOwnedUserInput { get; init; }
}
internal sealed record OwnedDesktopRoots(string Project, string? Home, string? Instructions, string? Builtin);

internal static class DesktopCommandLine
{
    /// <summary>Asks the running instance to exit instead of starting one.</summary>
    internal const string ExitOption = "--exit";

    /// <summary>Keeps the terminal until the application exits.</summary>
    internal const string WaitOption = "--wait";

    private const string HelpText = """
        CodeAlta Desktop

        Usage:
          alta                Start CodeAlta Desktop for the current folder.
          alta --dev          Start the developer instance beside the normal one.
          alta --wait         Start it and keep the terminal until it exits.
          alta --exit         Ask the running CodeAlta Desktop to exit.
          alta --version      Print the version.
          alta --help, -h     Print this help.

        alta opens the current folder as a project and uses the ~/.alta profile, which it shares
        with CodeAlta TUI (altatui). It runs the built-in plugins (MCP, Git, Statistics) and the
        source plugins of ~/.alta/plugins and of the current project. Set CODEALTA_DISABLE_PLUGINS=1
        to start without plugins. When CodeAlta Desktop is already running, alta shows its window.

        In a terminal, alta gives the prompt back once the window is shown. --wait keeps the
        terminal instead, alone or with --dev.

        --dev runs a second instance on the same profile. It shares configuration, providers,
        credentials, prompts, skills and projects, and keeps its own sessions under ~/.alta/dev.

        --exit asks first when files have unsaved edits or sessions are running. Use
        alta --dev --exit for the developer instance. Exit before updating with
        dotnet tool update -g CodeAlta.

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

            // Admission grants only desktop-owned browser storage and opt-in cache writes in a trusted copy.
            return startNative(options!);
        }
        catch (Exception exception)
        {
            error.WriteLine(exception.Message);
            return 1;
        }
    }

    // Mandatory CLI seam. Tests supply existence facts without opening any storage or native services.
    internal static bool TryParse(string[] args, Func<string, bool> directoryExists, Func<string, bool> fileExists,
        out DesktopLaunchOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(fileExists);
        options = null;
        error = "Unknown or invalid options. Run alta without options to start CodeAlta Desktop, or alta --help to list the options.";
        if (args.Length == 0 || args is [CodeAltaInstanceProfile.DeveloperOption])
        {
            options = CreateDefaultOptions(developer: args.Length == 1);
            error = null;
            return true;
        }
        if (args is [ExitOption] or [CodeAltaInstanceProfile.DeveloperOption, ExitOption] or [ExitOption, CodeAltaInstanceProfile.DeveloperOption])
        {
            options = CreateDefaultOptions(developer: args.Length == 2) with { ExitRunning = true };
            error = null;
            return true;
        }
        if (args is [WaitOption] or [CodeAltaInstanceProfile.DeveloperOption, WaitOption] or [WaitOption, CodeAltaInstanceProfile.DeveloperOption])
        {
            options = CreateDefaultOptions(developer: args.Length == 2) with { Wait = true };
            error = null;
            return true;
        }
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

    internal static DesktopLaunchOptions CreateDefaultOptions() => CreateDefaultOptions(developer: false);

    internal static DesktopLaunchOptions CreateDefaultOptions(bool developer)
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
            Owned = new(Path.GetFullPath(Environment.CurrentDirectory), null, null, null),
            StateRoot = developer ? profile.StateRoot : null,
            Developer = developer,
        };
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
