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

    /// <summary>
    /// Gets the instant before which a file of the scratchpad of the instance counts as old: the start of the scan less
    /// <see cref="StorageSettings.ScratchpadMaxAge"/>, as the startup cleanup computes it from its own start.
    /// </summary>
    public DateTimeOffset ScratchpadCutoff { get; init; }

    /// <summary>
    /// Gets the sum of the lengths of the files of the scratchpad of the instance whose creation and last write are both
    /// before <see cref="ScratchpadCutoff"/>: the candidates of the startup cleanup by their age. It is not what a cleanup
    /// removes: the cleanup may not run on this system, keeps a file that is held, and on macOS and Linux also keeps a file
    /// whose status changed since (a copy that kept the times of its source), which a walk does not read.
    /// </summary>
    public long OldScratchpadBytes { get; init; }

    /// <summary>Gets the number of the files counted in <see cref="OldScratchpadBytes"/>.</summary>
    public long OldScratchpadFiles { get; init; }
}

/// <summary>Measures file metadata under a host-owned global profile; never deletes or opens file contents.</summary>
public sealed class ProfileStorageScanner
{
    private readonly string _root;
    private readonly string _scratchpad;

    internal Action<string, bool>? EntryEnumerated { get; init; }

    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Captures the profile root. Callers cannot select another path per scan.</summary>
    /// <exception cref="ArgumentNullException">Options is null.</exception>
    /// <exception cref="ArgumentException">The global root is empty or invalid.</exception>
    public ProfileStorageScanner(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GlobalRoot);
        _root = Path.GetFullPath(options.GlobalRoot);
        // The scratchpad the startup cleanup of this instance clears: the one of its state root, under the profile.
        _scratchpad = Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetFullPath(options.StateRoot), "scratchpad"));
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
        var started = Time.GetUtcNow();
        var scratchpadCutoff = started - StorageSettings.ScratchpadMaxAge;
        var totals = new Dictionary<string, (long Bytes, long Files)>(StringComparer.Ordinal)
        {
            ["sessions"] = default, ["scratchpad"] = default, ["savedStatistics"] = default, ["reviews"] = default,
            ["database"] = default, ["configuration"] = default, ["extensions"] = default,
            ["projects"] = default, ["cacheLogs"] = default, ["other"] = default, ["developer"] = default,
            ["browser"] = default, ["models"] = default,
        };
        long skippedLinks = 0, skippedEntries = 0, totalBytes = 0, oldScratchpadBytes = 0, oldScratchpadFiles = 0;
        long processedFiles = 0, processedDirectories = 0, lastProgress = 0;
        progress?.Report(new(0, 0));
        var directories = new Stack<(DirectoryInfo Directory, string? Category, bool Scratchpad)>();
        var enumeration = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var root = new DirectoryInfo(_root);
        try
        {
            if (StorageLinks.HasLinkedAncestor(root)) skippedLinks++;
            else directories.Push((root, null, false));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A missing profile is a useful empty state, not a reason to create it.
        }
        catch (Exception ex) when (IsEnumerationFailure(ex)) { skippedEntries++; }

        while (directories.TryPop(out var pending))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, category, scratchpad) = pending;
            try
            {
                // Recheck on use, not just when the parent enumerated it: profiles can change during a scan.
                if (StorageLinks.HasLinkedAncestor(directory)) { skippedLinks++; continue; }
                var entries = new FileSystemEnumerable<StorageEntry>(directory.FullName, (ref FileSystemEntry entry) =>
                {
                    var attributes = entry.Attributes;
                    var isDirectory = entry.IsDirectory;
                    var file = !isDirectory && (attributes & FileAttributes.ReparsePoint) == 0;
                    // The age of a file as the startup cleanup reads it, less the status change a walk does not read.
                    var changed = scratchpad && file
                        ? (entry.CreationTimeUtc > entry.LastWriteTimeUtc ? entry.CreationTimeUtc : entry.LastWriteTimeUtc)
                        : default;
                    var value = new StorageEntry(isDirectory ? entry.ToFullPath() : null,
                        category ?? Classify(entry.FileName), file ? entry.Length : 0, attributes, changed);
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
                        if (entry.DirectoryPath is { } child)
                        {
                            directories.Push((new DirectoryInfo(child), entry.Category, scratchpad || string.Equals(child, _scratchpad, PathComparison)));
                            continue;
                        }
                        // No path traversal here: use the enumerated file metadata, not another stat/open.
                        // Directories (including their ancestors) are still rechecked before enumeration.
                        var bytes = entry.Length;
                        var prior = totals[entry.Category];
                        var nextTotal = checked(totalBytes + bytes);
                        var old = scratchpad && entry.Changed < scratchpadCutoff;
                        var nextOld = old ? checked(oldScratchpadBytes + bytes) : oldScratchpadBytes;
                        totals[entry.Category] = (checked(prior.Bytes + bytes), checked(prior.Files + 1));
                        totalBytes = nextTotal;
                        oldScratchpadBytes = nextOld;
                        if (old) oldScratchpadFiles++;
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
        return new(_root, started, Time.GetUtcNow(), totalBytes,
            totals.Select(pair => new ProfileStorageCategory(pair.Key, pair.Value.Bytes, pair.Value.Files)).ToArray(),
            skippedLinks, skippedEntries)
        {
            ProcessedFiles = processedFiles, ProcessedDirectories = processedDirectories,
            ScratchpadCutoff = scratchpadCutoff, OldScratchpadBytes = oldScratchpadBytes, OldScratchpadFiles = oldScratchpadFiles,
        };
    }

    private readonly record struct StorageEntry(string? DirectoryPath, string Category, long Length, FileAttributes Attributes, DateTimeOffset Changed);

    // Windows and macOS compare names without case by default.
    private static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

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
        if (name.Equals("reviews", comparison)) return "reviews";
        if (name.Equals("data", comparison)) return "database";
        if (name.Equals("auth", comparison) || name.Equals("providers", comparison) || name.Equals("color-schemes", comparison)
            || name.Equals("config.toml", comparison) || name.Equals("mcp.json", comparison) || name.Equals("agents.md", comparison)) return "configuration";
        if (name.Equals("prompts", comparison) || name.Equals("skills", comparison) || name.Equals("plugins", comparison) || name.Equals("plugin-data", comparison)) return "extensions";
        if (name.Equals("projects", comparison) || name.Equals("worktrees", comparison) || name.Equals("spaces", comparison)) return "projects";
        if (name.Equals("cache", comparison) || name.Equals("logs", comparison)) return "cacheLogs";
        if (name.Equals("browser", comparison)) return "browser";
        if (name.Equals("llama", comparison)) return "models";
        return "other";
    }
}
