namespace CodeAlta.Core.IO;

/// <summary>
/// The path the system reaches through a path: every link along it (a symbolic link, a junction) replaced by what it
/// points to, as far as the path exists. Two paths that name the same folder through links have the same physical path.
/// </summary>
public static class PhysicalPath
{
    // The most links one path is followed through.
    private const int MaximumLinks = 40;

    /// <summary>
    /// Resolves the links along a full path, as far as it exists: what follows is kept as it is written. The target of a
    /// link is resolved again, for the links above it.
    /// </summary>
    /// <param name="fullPath">A full path.</param>
    /// <returns>The path with its links resolved.</returns>
    /// <exception cref="IOException">The path goes through too many links, or a folder could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A folder could not be read.</exception>
    public static string Resolve(string fullPath) => Resolve(fullPath, 0);

    private static string Resolve(string fullPath, int links)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return fullPath;
        var current = root;
        var segments = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var next = Path.Combine(current, segments[index]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists && info.LinkTarget is null)
            {
                // Nothing further exists: the rest is created as written.
                return Path.Combine([next, .. segments[(index + 1)..]]);
            }

            // The short name Windows may give a folder (`ALTA~1` for `.alta`) is the folder under its own name.
            if (OperatingSystem.IsWindows() && segments[index].Contains('~')
                && new DirectoryInfo(current).EnumerateFileSystemInfos(segments[index]).FirstOrDefault() is { } named)
            {
                next = named.FullName;
                info = named;
            }

            if (info.LinkTarget is not null)
            {
                if (links >= MaximumLinks)
                {
                    throw new IOException("Too many links.");
                }

                // The target as the link names it, from the folder of the link, then resolved as a path of its own.
                var target = Path.GetFullPath(info.LinkTarget, current);
                return Resolve(Path.Combine([target, .. segments[(index + 1)..]]), links + 1);
            }

            current = next;
        }

        return current;
    }
}
