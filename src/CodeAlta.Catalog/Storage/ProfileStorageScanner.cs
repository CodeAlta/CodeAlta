namespace CodeAlta.Catalog.Storage;

/// <summary>One informational category of measured file lengths; it grants no deletion authority.</summary>
/// <param name="Id">The stable category identifier.</param>
/// <param name="Bytes">The sum of logical file lengths.</param>
/// <param name="FileCount">The number of measured file paths, including hidden files.</param>
public sealed record ProfileStorageCategory(string Id, long Bytes, long FileCount);

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
}

/// <summary>Measures file metadata under a host-owned global profile; never deletes or opens file contents.</summary>
public sealed class ProfileStorageScanner
{
    private readonly string _root;

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
        => Task.Run(() => Scan(cancellationToken), cancellationToken);

    private ProfileStorageSnapshot Scan(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var totals = new Dictionary<string, (long Bytes, long Files)>(StringComparer.Ordinal)
        {
            ["sessions"] = default, ["scratchpad"] = default, ["savedStatistics"] = default,
            ["database"] = default, ["configuration"] = default, ["extensions"] = default,
            ["projects"] = default, ["cacheLogs"] = default, ["other"] = default, ["developer"] = default,
        };
        long skippedLinks = 0, skippedEntries = 0, totalBytes = 0;
        var directories = new Stack<DirectoryInfo>();
        var enumeration = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var root = new DirectoryInfo(_root);
        try
        {
            if (HasLinkedAncestor(root)) skippedLinks++;
            else directories.Push(root);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A missing profile is a useful empty state, not a reason to create it.
        }
        catch (Exception ex) when (IsEnumerationFailure(ex)) { skippedEntries++; }

        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Recheck on use, not just when the parent enumerated it: profiles can change during a scan.
                if (HasLinkedAncestor(directory)) { skippedLinks++; continue; }
                foreach (var entry in directory.EnumerateFileSystemInfos("*", enumeration))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        entry.Refresh();
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
                        {
                            skippedLinks++;
                            continue;
                        }
                        if (entry is DirectoryInfo child) { directories.Push(child); continue; }
                        // Check the ancestry again before measuring a file; never traverse a discovered link.
                        if (HasLinkedAncestor(directory)) { skippedLinks++; break; }
                        var file = (FileInfo)entry;
                        var bytes = file.Length;
                        var category = Classify(Path.GetRelativePath(_root, entry.FullName));
                        var prior = totals[category];
                        var nextTotal = checked(totalBytes + bytes);
                        totals[category] = (checked(prior.Bytes + bytes), checked(prior.Files + 1));
                        totalBytes = nextTotal;
                    }
                    catch (Exception ex) when (IsEnumerationFailure(ex) || ex is OverflowException) { skippedEntries++; }
                }
            }
            catch (Exception ex) when (IsEnumerationFailure(ex)) { skippedEntries++; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(_root, started, DateTimeOffset.UtcNow, totalBytes,
            totals.Select(pair => new ProfileStorageCategory(pair.Key, pair.Value.Bytes, pair.Value.Files)).ToArray(),
            skippedLinks, skippedEntries);
    }

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

    private static string Classify(string relative)
    {
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var first = (separator < 0 ? relative : relative[..separator]).ToLowerInvariant();
        return first switch
        {
            "dev" => "developer", // Own the whole subtree once, never a second pass through its sessions/data.
            "sessions" or "threads" => "sessions",
            "scratchpad" => "scratchpad",
            "saved_statistics_after_deleted_sessions" => "savedStatistics",
            "data" => "database",
            "auth" or "providers" or "color-schemes" => "configuration",
            "prompts" or "skills" or "plugins" or "plugin-data" => "extensions",
            "projects" or "worktrees" or "spaces" => "projects",
            "cache" or "logs" => "cacheLogs",
            "config.toml" or "mcp.json" or "agents.md" => "configuration",
            _ => "other",
        };
    }
}
