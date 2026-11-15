using System.IO.Enumeration;

namespace CodeAlta.Catalog.Storage;

/// <summary>
/// The files of a folder and of its folders: what removing it frees. A link (a symbolic link to a file or a folder, a
/// junction or another reparse point) is never followed and counts for nothing; what cannot be read is counted as
/// skipped. No file is opened.
/// </summary>
/// <param name="Bytes">The sum of the lengths of the files.</param>
/// <param name="Files">The number of files.</param>
/// <param name="Skipped">The folders and files that could not be read.</param>
public sealed record FolderUsage(long Bytes, long Files, long Skipped)
{
    /// <summary>Gets a usage of nothing.</summary>
    public static FolderUsage Empty { get; } = new(0, 0, 0);

    /// <summary>Gets the time of the most recent write of its files; null when it holds none.</summary>
    public DateTimeOffset? LastWrite { get; init; }

    /// <summary>
    /// Gets whether the path is or holds a repository: anything named <c>.git</c> (a file, a folder or a link, intact or
    /// not), a bare repository (a folder that holds <c>HEAD</c>, <c>objects</c> and <c>refs</c>), or one of the git
    /// directories the measure was given. Only the folders the walk could read are known.
    /// </summary>
    public bool HoldsRepository { get; init; }

    /// <summary>
    /// Measures a folder, or a single file. A path that is not there holds nothing; a path that is a link holds nothing.
    /// </summary>
    /// <param name="path">The folder or the file.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    /// <returns>What the path holds.</returns>
    /// <exception cref="ArgumentException">The path is null or empty.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static FolderUsage Measure(string path, CancellationToken cancellationToken)
        => Measure(path, excluded: null, gitDirectories: null, fileCounted: null, folderTaken: null, cancellationToken);

    /// <summary>Measures a path as <see cref="Measure(string, CancellationToken)"/> does.</summary>
    /// <param name="path">The folder or the file.</param>
    /// <param name="excluded">Full paths, as the walk builds them from <paramref name="path"/>, that are neither entered nor counted.</param>
    /// <param name="gitDirectories">Full physical paths of git directories that make a folder that is or holds one hold a repository.</param>
    /// <param name="fileCounted">Told each counted file, on the walking thread.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    internal static FolderUsage Measure(string path, IReadOnlySet<string>? excluded, IReadOnlySet<string>? gitDirectories, Action? fileCounted,
        CancellationToken cancellationToken)
        => Measure(path, excluded, gitDirectories, fileCounted, folderTaken: null, cancellationToken);

    /// <summary>Measures a path as <see cref="Measure(string, IReadOnlySet{string}, IReadOnlySet{string}, Action, CancellationToken)"/> does.</summary>
    /// <param name="folderTaken">Told each folder the walk takes from its stack, before it is checked again: the tests change it there.</param>
    internal static FolderUsage Measure(string path, IReadOnlySet<string>? excluded, IReadOnlySet<string>? gitDirectories, Action? fileCounted,
        Action<string>? folderTaken, CancellationToken cancellationToken)
        => Measure(path, excluded, gitDirectories, fileCounted, folderTaken, stopAtRepository: false, cancellationToken);

    /// <summary>
    /// Looks into a path for a repository, as a measure does, and stops at the first one it finds: its figures are then
    /// partial. <see cref="Skipped"/> above 0 says that the look was not complete.
    /// </summary>
    /// <param name="path">The folder or the file.</param>
    /// <param name="gitDirectories">Full physical paths of git directories that make a folder that is or holds one hold a repository.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    internal static FolderUsage FindRepository(string path, IReadOnlySet<string>? gitDirectories, CancellationToken cancellationToken)
        => Measure(path, excluded: null, gitDirectories, fileCounted: null, folderTaken: null, stopAtRepository: true, cancellationToken);

    private static FolderUsage Measure(string path, IReadOnlySet<string>? excluded, IReadOnlySet<string>? gitDirectories, Action? fileCounted,
        Action<string>? folderTaken, bool stopAtRepository, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        cancellationToken.ThrowIfCancellationRequested();
        var gitDirs = gitDirectories is { Count: > 0 } ? gitDirectories : null;
        var holds = IsGitName(Path.GetFileName(Path.TrimEndingDirectorySeparator(path))) || (gitDirs is not null && IsOrIsInside(path, gitDirs));
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).LinkTarget is not null) return Empty with { HoldsRepository = holds };
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Empty;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return new(0, 0, 1) { HoldsRepository = holds };
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            try
            {
                var file = new FileInfo(path);
                var length = file.Length;
                fileCounted?.Invoke();
                return new(length, 1, 0) { LastWrite = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), HoldsRepository = holds };
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return Empty;
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                return new(0, 0, 1) { HoldsRepository = holds };
            }
        }

        if (holds && stopAtRepository) return Empty with { HoldsRepository = true };
        var excludes = excluded is { Count: > 0 } ? excluded : null;
        long bytes = 0, files = 0, skipped = 0;
        DateTimeOffset? lastWrite = null;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var folders = new Stack<string>();
        folders.Push(path);
        while (folders.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            folderTaken?.Invoke(folder);
            try
            {
                // A folder may have become a link since its parent listed it: it is not entered then.
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || new DirectoryInfo(folder).LinkTarget is not null) continue;
                var entries = new FileSystemEnumerable<Entry>(folder, (ref FileSystemEntry entry) =>
                {
                    var link = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    var full = entry.IsDirectory || excludes is not null ? entry.ToFullPath() : null;
                    var kind = entry.FileName switch
                    {
                        var name when IsGitName(name) => Marker.Git,
                        "HEAD" when !entry.IsDirectory => Marker.Head,
                        "objects" when entry.IsDirectory => Marker.Objects,
                        "refs" when entry.IsDirectory => Marker.Refs,
                        _ => Marker.None,
                    };
                    return new Entry(full, entry.IsDirectory, link, entry.IsDirectory || link ? 0 : entry.Length,
                        entry.IsDirectory || link ? default : entry.LastWriteTimeUtc, kind);
                }, options);
                var bare = Marker.None;
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A name of git counts whatever it is, a link or a broken worktree included.
                    if (entry.Kind == Marker.Git) holds = true;
                    if (holds && stopAtRepository) return new(bytes, files, skipped) { LastWrite = lastWrite, HoldsRepository = true };
                    bare |= entry.Kind;
                    // The enumeration says what a link is without following it.
                    if (entry.Link) continue;
                    if (excludes is not null && excludes.Contains(entry.FullPath!)) continue;
                    if (entry.Directory)
                    {
                        if (gitDirs is not null && gitDirs.Contains(entry.FullPath!))
                        {
                            holds = true;
                            if (stopAtRepository) return new(bytes, files, skipped) { LastWrite = lastWrite, HoldsRepository = true };
                        }

                        folders.Push(entry.FullPath!);
                        continue;
                    }

                    // A sum past the range of a long is not wrapped: the file is not counted, and is skipped.
                    if (bytes > long.MaxValue - entry.Length)
                    {
                        skipped++;
                        continue;
                    }

                    bytes += entry.Length;
                    files++;
                    if (lastWrite is null || entry.LastWrite > lastWrite) lastWrite = entry.LastWrite;
                    fileCounted?.Invoke();
                }

                if ((bare & BareRepository) == BareRepository)
                {
                    holds = true;
                    if (stopAtRepository) return new(bytes, files, skipped) { LastWrite = lastWrite, HoldsRepository = true };
                }
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                // What was counted of this folder before the failure stays counted.
                skipped++;
            }
        }

        return new(bytes, files, skipped) { LastWrite = lastWrite, HoldsRepository = holds };
    }

    private const Marker BareRepository = Marker.Head | Marker.Objects | Marker.Refs;

    // Without case everywhere: on the side of a repository when unsure.
    private static bool IsGitName(ReadOnlySpan<char> name) => name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    private static bool IsOrIsInside(string path, IReadOnlySet<string> gitDirectories)
    {
        for (var current = Path.TrimEndingDirectorySeparator(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (gitDirectories.Contains(current)) return true;
        }

        return false;
    }

    private static bool IsReadFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    [Flags]
    private enum Marker
    {
        None = 0,
        Git = 1,
        Head = 2,
        Objects = 4,
        Refs = 8,
    }

    private readonly record struct Entry(string? FullPath, bool Directory, bool Link, long Length, DateTimeOffset LastWrite, Marker Kind);
}
