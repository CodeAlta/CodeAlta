namespace CodeAlta.Catalog;

/// <summary>Outcome of an opt-in, top-level, local-directory suggestion read.</summary>
public enum DirectoryCompletionStatus
{
    /// <summary>The directory was fully enumerated without omissions within the budgets.</summary>
    Complete,
    /// <summary>An entry, result, or path budget was reached, or an unsafe entry was omitted.</summary>
    Incomplete,
    /// <summary>The directory or an ancestor was absent.</summary>
    Missing,
    /// <summary>The requested path or an ancestor is a file rather than a directory.</summary>
    NotDirectory,
    /// <summary>The input is not a bounded, canonical, fully qualified local directory and literal prefix.</summary>
    Invalid,
    /// <summary>Access was denied to the directory or an encountered entry.</summary>
    Denied,
    /// <summary>A linked ancestor, I/O error, or other untrustworthy read prevented completion.</summary>
    ReadError,
}

/// <summary>A bounded subset of direct child directory paths, never project or import authority.</summary>
/// <param name="Status">Whether enumeration was complete or why it could not be completed.</param>
/// <param name="Directories">Observed paths sorted within the returned subset only; never a global first-page guarantee.</param>
/// <param name="EntriesVisited">All encountered entries, including nonmatches and an over-budget sentinel.</param>
/// <param name="OmittedUnsafeEntries">Whether a hidden, linked, or oversized child was skipped.</param>
public sealed record DirectoryCompletionResult(DirectoryCompletionStatus Status, IReadOnlyList<string> Directories,
    int EntriesVisited, bool OmittedUnsafeEntries);

/// <summary>Opt-in bounded filesystem directory suggestions, independent of project catalog and TUI completion.</summary>
public static class DirectoryCompletionReader
{
    /// <summary>Maximum UTF-16 characters in the requested directory and each returned full path.</summary>
    public const int MaximumDirectoryLength = 1024;
    /// <summary>Maximum UTF-16 characters in the literal name prefix.</summary>
    public const int MaximumPrefixLength = 128;
    /// <summary>Maximum entries inspected before one additional sentinel makes the result incomplete.</summary>
    public const int MaximumEntries = 128;
    /// <summary>Maximum returned paths.</summary>
    public const int MaximumResults = 16;
    /// <summary>Maximum UTF-16 characters across returned paths.</summary>
    public const int MaximumTotalResultCharacters = MaximumDirectoryLength * MaximumResults;

    /// <summary>Reads only direct child directory names from one explicitly supplied absolute local directory.</summary>
    /// <remarks>Does not expand relative/home paths, scan a drive root, follow known linked/reparse components,
    /// read files, recurse or establish project ownership/importability. Hidden and linked children are omitted
    /// with Incomplete. Returned paths are sorted only within the bounded observed subset; no global ordering
    /// or absence can be inferred from Incomplete. Checks are point-in-time: an external writer may swap a path
    /// between attribute checks and enumeration. Filesystem calls and enumerator disposal can block despite
    /// cancellation; this is an entry/allocation budget, not a wall-clock deadline or atomic safety boundary.
    /// Mapped drives and Unix mounts cannot be reliably distinguished from local paths by lexical checks.</remarks>
    /// <param name="directoryPath">Canonical fully qualified existing local directory, not a drive root.</param>
    /// <param name="prefix">Literal partial child name, or empty to match all direct child directories.</param>
    /// <param name="cancellationToken">Checked before and between filesystem operations.</param>
    /// <returns>Explicit status and bounded observed suggestions with traversal evidence.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled before completion or between operations.</exception>
    public static DirectoryCompletionResult Read(string? directoryPath, string? prefix, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryCompletionResult invalid = new(DirectoryCompletionStatus.Invalid, [], 0, false);
        if (directoryPath is not { Length: > 0 and <= MaximumDirectoryLength } ||
            prefix is not { Length: <= MaximumPrefixLength }) return invalid;
        try
        {
            if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                prefix.Contains(Path.DirectorySeparatorChar) || prefix.Contains(Path.AltDirectorySeparatorChar) ||
                !Path.IsPathFullyQualified(directoryPath) || directoryPath.StartsWith("//", StringComparison.Ordinal) ||
                directoryPath.StartsWith(@"\\", StringComparison.Ordinal)) return invalid;
            var root = Path.GetPathRoot(directoryPath);
            if (root is null || string.Equals(directoryPath, root, StringComparison.Ordinal) ||
                !string.Equals(directoryPath, Path.GetFullPath(directoryPath), StringComparison.Ordinal)) return invalid;
            if (OperatingSystem.IsWindows() && (root.Length != 3 || !char.IsLetter(root[0]) || root[1] != ':' || root[2] != '\\'))
                return invalid;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return invalid; }

        var entries = 0;
        var omitted = false;
        var paths = new List<string>(MaximumResults);
        DirectoryCompletionResult result(DirectoryCompletionStatus status)
        {
            paths.Sort(static (left, right) =>
            {
                var comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
                return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
            });
            return new(status, status is DirectoryCompletionStatus.Complete or DirectoryCompletionStatus.Incomplete
                ? paths.ToArray() : [], entries, omitted);
        }
        try
        {
            // Check each component from the filesystem root before touching the requested directory.
            var ancestors = new Stack<DirectoryInfo>();
            for (var directory = new DirectoryInfo(directoryPath); directory is not null; directory = directory.Parent)
                ancestors.Push(directory);
            while (ancestors.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(ancestors.Pop().FullName);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return result(DirectoryCompletionStatus.ReadError);
                if (!attributes.HasFlag(FileAttributes.Directory)) return result(DirectoryCompletionStatus.NotDirectory);
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaximumEntries) return result(DirectoryCompletionStatus.Incomplete);
                if (entry.Length > MaximumDirectoryLength)
                {
                    omitted = true;
                    continue;
                }
                var attributes = File.GetAttributes(entry);
                var name = Path.GetFileName(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Hidden) ||
                    name.StartsWith(".", StringComparison.Ordinal))
                {
                    omitted = true;
                    continue;
                }
                if (!attributes.HasFlag(FileAttributes.Directory) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (paths.Count == MaximumResults) return result(DirectoryCompletionStatus.Incomplete);
                paths.Add(entry);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result(omitted ? DirectoryCompletionStatus.Incomplete : DirectoryCompletionStatus.Complete);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return result(DirectoryCompletionStatus.Denied); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return result(entries == 0 ? DirectoryCompletionStatus.Missing : DirectoryCompletionStatus.Incomplete); }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        { return result(DirectoryCompletionStatus.ReadError); }
    }
}
