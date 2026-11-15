using CodeAlta.Core.IO;

namespace CodeAlta.Catalog.Storage;

/// <summary>
/// Paths of checkouts and folders as the disk knows them: git names a checkout by its physical path, while a project or a
/// session may name the same folder through a link, and a volume may or may not tell case apart. Two paths are compared
/// physically, with the case rule of the volume they are on.
/// </summary>
public static class CheckoutPaths
{
    /// <summary>Gets the physical path of a folder: full, its links resolved, without a separator at the end.</summary>
    /// <param name="path">A path.</param>
    /// <returns>The physical path; the full path as it is written when its links cannot be followed.</returns>
    /// <exception cref="ArgumentException">The path is empty or not a path.</exception>
    public static string Physical(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            return Path.TrimEndingDirectorySeparator(PhysicalPath.Resolve(full));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    /// <summary>
    /// Gets how the volume of a path compares names, found out on the path itself rather than from the system (a volume
    /// of macOS can tell case apart): without case when the nearest entry of the path that has a letter in its name is
    /// also found under another case, and alone.
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <returns>Ordinal, unless the volume is known not to tell case apart.</returns>
    public static StringComparer ComparerOf(string path)
    {
        try
        {
            for (var current = Path.TrimEndingDirectorySeparator(path); Path.GetDirectoryName(current) is { } parent; current = parent)
            {
                var name = Path.GetFileName(current);
                if (!name.Any(char.IsLetter) || !Path.Exists(current)) continue;
                // Two entries side by side whose names differ only in case: the volume tells them apart.
                var names = Directory.EnumerateFileSystemEntries(parent, name, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 })
                    .Select(Path.GetFileName).ToArray();
                if (names.Length != 1) return StringComparer.Ordinal;
                var other = string.Concat(name.Select(static character => char.IsUpper(character) ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character)));
                return Path.Exists(Path.Combine(parent, other)) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Not known: case counts.
        }

        return StringComparer.Ordinal;
    }

    /// <summary>Gets whether a path is a folder or inside it, both full and physical, compared with <paramref name="comparer"/>.</summary>
    /// <param name="path">The path.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="comparer"><see cref="StringComparer.Ordinal"/> or <see cref="StringComparer.OrdinalIgnoreCase"/>, as <see cref="ComparerOf"/> gives it.</param>
    /// <returns>True when <paramref name="path"/> is <paramref name="folder"/> or below it.</returns>
    public static bool IsWithin(string path, string folder, StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(comparer);
        var (inner, outer) = (Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(folder));
        var comparison = ReferenceEquals(comparer, StringComparer.OrdinalIgnoreCase) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return inner.Equals(outer, comparison)
            || (inner.Length > outer.Length && inner.StartsWith(outer, comparison)
                && (Path.EndsInDirectorySeparator(outer) || inner[outer.Length] == Path.DirectorySeparatorChar));
    }
}
