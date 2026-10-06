using System.Diagnostics.CodeAnalysis;
using XenoAtom.Glob;

namespace CodeAlta.Catalog;

/// <summary>
/// Chooses files of a project from a list of names, paths and globs separated by commas, such as
/// <c>src, *.ts</c>.
/// </summary>
/// <remarks>
/// An item matches the file or the folder of that name or path anywhere in the project, and everything such a
/// folder holds: <c>bin</c> matches <c>bin/a.dll</c> and <c>src/app/bin/b.dll</c>. Names are compared without
/// case on Windows, where file names have none. An instance does not change and may be used from several threads.
/// </remarks>
public sealed class ProjectFilePathFilter
{
    private readonly GlobPattern[] _patterns;

    private ProjectFilePathFilter(GlobPattern[] patterns) => _patterns = patterns;

    /// <summary>Whether the filter has no item: it matches no path.</summary>
    public bool IsEmpty => _patterns.Length == 0;

    /// <summary>Reads a list of names, paths and globs separated by commas.</summary>
    /// <param name="list">The list. A null, empty or blank list gives a filter with no item.</param>
    /// <param name="filter">The filter, when every item of the list is a valid glob.</param>
    /// <returns>False when an item is not a valid glob.</returns>
    public static bool TryParse(string? list, [NotNullWhen(true)] out ProjectFilePathFilter? filter)
    {
        filter = null;
        var patterns = new List<GlobPattern>();
        foreach (var item in (list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var glob = Fold(item.Replace('\\', '/'));
            while (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob[2..];
            glob = glob.Trim('/');
            if (glob.Length == 0) continue;
            // The item itself, what it holds as a folder, and both anywhere below the project folder.
            foreach (var candidate in new[] { glob, $"{glob}/**", $"**/{glob}", $"**/{glob}/**" })
            {
                var result = GlobPattern.TryParse(candidate);
                if (!result.Success) return false;
                patterns.Add(result.Pattern!);
            }
        }

        filter = new([.. patterns]);
        return true;
    }

    /// <summary>Whether an item of the filter matches a path.</summary>
    /// <param name="relativePath">A path relative to the project folder, with forward slashes.</param>
    /// <returns>True when an item matches; always false for a filter with no item.</returns>
    public bool IsMatch(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (_patterns.Length == 0) return false;
        var path = Fold(relativePath);
        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(path)) return true;
        }

        return false;
    }

    // Globs compare case; file names on Windows do not.
    private static string Fold(string path) => OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;
}
