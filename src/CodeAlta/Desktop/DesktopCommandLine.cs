using System.Reflection;

namespace CodeAlta.Desktop;

internal sealed record DesktopLaunchOptions(string DataRoot, string? CatalogRoot)
{
    internal OwnedDesktopRoots? Owned { get; init; }
    internal bool ReviewOwnedCommandPermissions { get; init; }
    internal bool EnableOwnedUserInput { get; init; }
}
internal sealed record OwnedDesktopRoots(string Project, string Home, string Instructions, string Builtin);

internal static class DesktopCommandLine
{
    internal static string Version => typeof(DesktopCommandLine).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";

    internal static int Run(string[] args, TextWriter output, TextWriter error, Func<DesktopLaunchOptions, int> startNative)
    {
        if (args is ["--help"] or ["-h"])
        {
            output.WriteLine("alta\nalta --data-root <new absolute directory> [--catalog-root <existing absolute trusted task-owned COPY> --allow-catalog-cache]\nCodeAlta desktop is in development; use altatui for full agent functionality.\nWith no options, the desktop keeps its data under the platform-local application-data directory. It does not open ~/.alta or any other legacy profile, start providers or plugins, or acquire the terminal runtime lock.\nThe explicit-root form remains available for isolated browsing. It can write cache/cache.sqlite3 and SQLite sidecars in the supplied COPY. Browser and catalog roots must be separate and outside .alta.\n--help / --version must be used alone and do not initialize native services or storage.");
            output.WriteLine("Separate owned mode additionally requires --allow-owned-host --project-root <existing absolute directory> --discovery-home <existing absolute directory> --instruction-root <existing absolute project ancestor> --builtin-skill-root <existing absolute directory>. This consents to lock/project-catalog/journal/cache/provider-state writes, configured-provider registration (including declared credential environment names and shipped defaults), and provider authentication/storage/network on submission. Plugins and probes stay disabled; permissions denied and user input cancelled. No default-profile/HOME substitution; discovery roots do not sandbox providers, copied-cache external journal paths or reparse points. Only task-owned roots are admitted; this is not production/shared-profile qualification.");
            output.WriteLine("Owned mode only: --review-owned-command-permissions explicitly enables manual review of supported plain command requests (Allow once / Deny / Cancel). Default remains deny. Approval can execute commands with the host's privileges; roots are not a sandbox. Unsupported permissions remain denied; user input remains cancelled. Refresh pending commands manually; closing a review or losing an RPC response does not revoke an accepted decision.");
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
        error = "Unknown or invalid options. Run alta with no arguments for desktop-owned storage, or use --help for isolated-root options.";
        if (args.Length == 0)
        {
            options = CreateDefaultOptions();
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

    internal static DesktopLaunchOptions CreateDefaultOptions()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
                throw new InvalidOperationException("Unable to determine a local application-data directory for CodeAlta.");
            localData = Path.Combine(userProfile, ".local", "share");
        }
        return new DesktopLaunchOptions(Path.Combine(localData, "CodeAlta", "desktop"), CatalogRoot: null);
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
