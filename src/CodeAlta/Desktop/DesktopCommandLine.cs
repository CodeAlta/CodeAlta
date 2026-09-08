using System.Reflection;

namespace CodeAlta.Desktop;

internal sealed record DesktopLaunchOptions(string DataRoot, string? CatalogRoot);

internal static class DesktopCommandLine
{
    internal static string Version => typeof(DesktopCommandLine).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";

    internal static int Run(string[] args, TextWriter output, TextWriter error, Func<DesktopLaunchOptions, int> startNative)
    {
        if (args is ["--help"] or ["-h"])
        {
            output.WriteLine("alta --data-root <new absolute directory> [--catalog-root <existing absolute trusted task-owned COPY> --allow-catalog-cache]\nCodeAlta desktop is in development; use altatui for agent functionality.\nOptional persisted workspace browsing can write cache/cache.sqlite3 and SQLite sidecars in the supplied COPY. No production/default profile, providers or plugins are opened.\nPaths do not prove task ownership or isolate reparse points: supply only a trusted task-owned copy. Browser and catalog roots must be separate and not beneath .alta.\n--help / --version must be used alone and do not initialize native services or storage.");
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

            // Admission grants only isolated browser storage and opt-in cache writes in a trusted copy.
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
        error = "Supply a new absolute --data-root; optional --catalog-root requires an existing absolute trusted task-owned COPY and --allow-catalog-cache. No default profiles, .alta paths or overlapping roots are allowed. Use --help.";
        string? data = null;
        string? catalog = null;
        var allowCache = false;
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
        error = null;
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
