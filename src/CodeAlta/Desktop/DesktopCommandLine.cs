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
    internal bool ReviewOwnedCommandPermissions { get; init; }
    internal bool EnableOwnedUserInput { get; init; }
}
internal sealed record OwnedDesktopRoots(string Project, string? Home, string? Instructions, string? Builtin);

internal static class DesktopCommandLine
{
    /// <summary>Asks the running instance to exit instead of starting one.</summary>
    internal const string ExitOption = "--exit";

    internal static string Version => typeof(DesktopCommandLine).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";

    internal static int Run(string[] args, TextWriter output, TextWriter error, Func<DesktopLaunchOptions, int> startNative)
    {
        if (args is ["--help"] or ["-h"])
        {
            output.WriteLine("alta\nalta --data-root <new absolute directory> [--catalog-root <existing absolute trusted task-owned COPY> --allow-catalog-cache]\nCodeAlta desktop is in development; use altatui for the complete terminal experience.\nWith no options, the desktop starts the normal interactive host for the current directory and ~/.alta catalog, matching the TUI default. It acquires the runtime lock and may update project catalog, journal, cache, provider state, and configured provider authentication/storage when a prompt is submitted. It runs the built-in plugins (MCP, Git, Statistics) and the source plugins of ~/.alta/plugins and the current project; CODEALTA_DISABLE_PLUGINS=1 starts without them. WebView data remains under the platform-local application-data directory.\nThe explicit-root form remains available for isolated catalog-only browsing. Browser and catalog roots must be separate and outside .alta.\n--help / --version must be used alone and do not initialize native services or storage.");
            output.WriteLine("alta --dev\nStarts the developer instance for the current directory. It runs beside the normal instance on the same ~/.alta profile: configuration, providers, credentials, prompts, skills and the project catalog are shared, while sessions, the session cache and the runtime lock live under ~/.alta/dev and WebView data under its own application-data directory. Use it to work on CodeAlta with CodeAlta. --dev must be used alone.");
            output.WriteLine("alta --exit\nAsks the CodeAlta already running with this profile to exit, as Exit in its notification-area menu does: it may be running with its window closed. It asks first when files are unsaved or sessions are running. Nothing is started when none is running. Combine with --dev for the developer instance.");
            output.WriteLine("Explicit scoped owned mode requires --allow-owned-host --project-root <existing absolute directory> --discovery-home <existing absolute directory> --instruction-root <existing absolute project ancestor> --builtin-skill-root <existing absolute directory>. This consents to lock/project-catalog/journal/cache/provider-state writes, configured-provider registration (including declared credential environment names and shipped defaults), and provider authentication/storage/network on submission. Plugins and automatic probes stay disabled; explicit Models reads and Providers tests may probe; tools auto-approved and user input cancelled by default. No default-profile/HOME substitution; discovery roots do not sandbox providers, copied-cache external journal paths or reparse points. Only task-owned roots are admitted; this is not production/shared-profile qualification.");
            output.WriteLine("Owned mode automatically approves tool permissions by default, like TUI AutoApprove. Commands and file writes run with the host's privileges; roots are not a sandbox. --review-owned-command-permissions instead enables manual review of supported plain command requests (Allow once / Deny / Cancel), denying unsupported permissions. User input remains separately controlled. Closing a review or losing an RPC response does not revoke an accepted decision.");
            output.WriteLine("Owned mode only: --enable-owned-user-input independently enables manual nonsecret provider forms. Not credential entry or command approval; answers may persist in provider tool results/history. Default remains cancelled. Refresh manually; lost decisions cannot be recovered or replayed safely.");
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
        error = "Unknown or invalid options. Run alta with no arguments for the interactive current-project host, or use --help for isolated-root options.";
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
