using System.Diagnostics;
using System.IO.Enumeration;

namespace CodeAlta.Catalog.Storage;

/// <summary>One informational category of measured file lengths; it grants no deletion authority.</summary>
/// <param name="Id">The stable category identifier.</param>
/// <param name="Bytes">The sum of logical file lengths.</param>
/// <param name="FileCount">The number of measured file paths, including hidden files.</param>
public sealed record ProfileStorageCategory(string Id, long Bytes, long FileCount);

/// <summary>Counts of entries examined so far, including skipped entries whose type was known; not an estimated total.</summary>
/// <param name="ProcessedFiles">File entries examined.</param>
/// <param name="ProcessedDirectories">Child directory entries examined, excluding the profile root.</param>
public sealed record ProfileStorageProgress(long ProcessedFiles, long ProcessedDirectories);

/// <summary>A metadata-only, non-atomic measurement of the profile. No file contents are read.</summary>
/// <param name="RootPath">The host-captured profile root.</param>
/// <param name="StartedAt">When enumeration started.</param>
/// <param name="CompletedAt">When enumeration finished.</param>
/// <param name="TotalBytes">The measured total, not allocated disk space or reclaimable space.</param>
/// <param name="Categories">Disjoint categories whose bytes sum to the measured total.</param>
/// <param name="SkippedLinks">Skipped symbolic links or reparse points.</param>
/// <param name="SkippedEntries">Inaccessible, missing-during-scan or otherwise unreadable entries/subtrees.</param>
public sealed record ProfileStorageSnapshot(string RootPath, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    long TotalBytes, IReadOnlyList<ProfileStorageCategory> Categories, long SkippedLinks, long SkippedEntries)
{
    /// <summary>Gets whether some entries could not be measured. Percentages still refer to the measured total.</summary>
    public bool IsPartial => SkippedLinks != 0 || SkippedEntries != 0;

    /// <summary>Gets the file entries examined, including known files that were skipped.</summary>
    public long ProcessedFiles { get; init; }

    /// <summary>Gets the child directory entries examined, excluding the profile root.</summary>
    public long ProcessedDirectories { get; init; }
}

/// <summary>Measures file metadata under a host-owned global profile; never deletes or opens file contents.</summary>
public sealed class ProfileStorageScanner
{
    private readonly string _root;

    internal Action<string, bool>? EntryEnumerated { get; init; }

    /// <summary>Captures the profile root. Callers cannot select another path per scan.</summary>
    /// <exception cref="ArgumentNullException">Options is null.</exception>
    /// <exception cref="ArgumentException">The global root is empty or invalid.</exception>
    public ProfileStorageScanner(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GlobalRoot);
        _root = Path.GetFullPath(options.GlobalRoot);
    }

    /// <summary>Enumerates metadata on a worker thread, with cancellation independent of maintenance.</summary>
    /// <exception cref="OperationCanceledException">The caller canceled the scan.</exception>
    public Task<ProfileStorageSnapshot> ScanAsync(CancellationToken cancellationToken)
        => ScanAsync(null, cancellationToken);

    /// <summary>Enumerates metadata off-thread, reporting bounded progress and exact final counts.</summary>
    /// <remarks>Progress runs on the worker; observers must return promptly. Intermediate reports are throttled to four per second.</remarks>
    /// <exception cref="OperationCanceledException">The caller canceled the scan.</exception>
    public Task<ProfileStorageSnapshot> ScanAsync(IProgress<ProfileStorageProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Scan(progress, cancellationToken), cancellationToken);

    private ProfileStorageSnapshot Scan(IProgress<ProfileStorageProgress>? progress, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var totals = new Dictionary<string, (long Bytes, long Files)>(StringComparer.Ordinal)
        {
            ["sessions"] = default, ["scratchpad"] = default, ["savedStatistics"] = default,
            ["database"] = default, ["configuration"] = default, ["extensions"] = default,
            ["projects"] = default, ["cacheLogs"] = default, ["other"] = default, ["developer"] = default,
        };
        long skippedLinks = 0, skippedEntries = 0, totalBytes = 0;
        long processedFiles = 0, processedDirectories = 0, lastProgress = 0;
        progress?.Report(new(0, 0));
        var directories = new Stack<(DirectoryInfo Directory, string? Category)>();
        var enumeration = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var root = new DirectoryInfo(_root);
        try
        {
            if (HasLinkedAncestor(root)) skippedLinks++;
            else directories.Push((root, null));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A missing profile is a useful empty state, not a reason to create it.
        }
        catch (Exception ex) when (IsEnumerationFailure(ex)) { skippedEntries++; }

        while (directories.TryPop(out var pending))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, category) = pending;
            try
            {
                // Recheck on use, not just when the parent enumerated it: profiles can change during a scan.
                if (HasLinkedAncestor(directory)) { skippedLinks++; continue; }
                var entries = new FileSystemEnumerable<StorageEntry>(directory.FullName, (ref FileSystemEntry entry) =>
                {
                    var attributes = entry.Attributes;
                    var isDirectory = entry.IsDirectory;
                    var value = new StorageEntry(isDirectory ? entry.ToFullPath() : null,
                        category ?? Classify(entry.FileName),
                        isDirectory || (attributes & FileAttributes.ReparsePoint) != 0 ? 0 : entry.Length, attributes);
                    EntryEnumerated?.Invoke(value.DirectoryPath ?? entry.ToFullPath(), isDirectory);
                    return value;
                }, enumeration);
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (entry.DirectoryPath is not null) processedDirectories++;
                        else processedFiles++;
                        // Enumeration supplies attributes and file length. Refresh/LinkTarget would reopen
                        // every path; walking all ancestors per file multiplies I/O on large scratchpads.
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            skippedLinks++;
                            continue;
                        }
                        if (entry.DirectoryPath is { } child) { directories.Push((new DirectoryInfo(child), entry.Category)); continue; }
                        // No path traversal here: use the enumerated file metadata, not another stat/open.
                        // Directories (including their ancestors) are still rechecked before enumeration.
                        var bytes = entry.Length;
                        var prior = totals[entry.Category];
                        var nextTotal = checked(totalBytes + bytes);
                        totals[entry.Category] = (checked(prior.Bytes + bytes), checked(prior.Files + 1));
                        totalBytes = nextTotal;
                    }
                    catch (Exception ex) when (IsEnumerationFailure(ex) || ex is OverflowException) { skippedEntries++; }
                    finally
                    {
                        if (progress is not null && (lastProgress == 0 || Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds >= 250))
                        {
                            progress.Report(new(processedFiles, processedDirectories));
                            lastProgress = Stopwatch.GetTimestamp();
                        }
                    }
                }
            }
            catch (Exception ex) when (IsEnumerationFailure(ex)) { skippedEntries++; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(processedFiles, processedDirectories));
        return new(_root, started, DateTimeOffset.UtcNow, totalBytes,
            totals.Select(pair => new ProfileStorageCategory(pair.Key, pair.Value.Bytes, pair.Value.Files)).ToArray(),
            skippedLinks, skippedEntries) { ProcessedFiles = processedFiles, ProcessedDirectories = processedDirectories };
    }

    private readonly record struct StorageEntry(string? DirectoryPath, string Category, long Length, FileAttributes Attributes);

    private static bool HasLinkedAncestor(DirectoryInfo directory)
    {
        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
        {
            var attributes = File.GetAttributes(current.FullName);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null) return true;
        }
        return false;
    }

    private static bool IsEnumerationFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static string Classify(ReadOnlySpan<char> name)
    {
        // Classify only root entries. Descendants inherit their category without allocating relative paths/names.
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        if (name.Equals("dev", comparison)) return "developer";
        if (name.Equals("sessions", comparison) || name.Equals("threads", comparison)) return "sessions";
        if (name.Equals("scratchpad", comparison)) return "scratchpad";
        if (name.Equals("saved_statistics_after_deleted_sessions", comparison)) return "savedStatistics";
        if (name.Equals("data", comparison)) return "database";
        if (name.Equals("auth", comparison) || name.Equals("providers", comparison) || name.Equals("color-schemes", comparison)
            || name.Equals("config.toml", comparison) || name.Equals("mcp.json", comparison) || name.Equals("agents.md", comparison)) return "configuration";
        if (name.Equals("prompts", comparison) || name.Equals("skills", comparison) || name.Equals("plugins", comparison) || name.Equals("plugin-data", comparison)) return "extensions";
        if (name.Equals("projects", comparison) || name.Equals("worktrees", comparison) || name.Equals("spaces", comparison)) return "projects";
        if (name.Equals("cache", comparison) || name.Equals("logs", comparison)) return "cacheLogs";
        return "other";
    }
}
