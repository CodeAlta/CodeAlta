using System.Diagnostics;
using System.IO.Enumeration;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog.Storage;

/// <summary>What the files of one session hold in the sessions folder.</summary>
/// <param name="SessionId">The session the files belong to, as their names say.</param>
/// <param name="JournalBytes">The sum of the lengths of its journals.</param>
/// <param name="JournalFiles">Its journals: more than one when its id is in several day folders.</param>
/// <param name="ArtifactBytes">The sum of the lengths of the files of its attachments folders and of its protocol trace.</param>
/// <param name="ArtifactFiles">The files of its attachments folders and its protocol trace.</param>
public sealed record SessionUsage(string SessionId, long JournalBytes, long JournalFiles, long ArtifactBytes, long ArtifactFiles)
{
    /// <summary>Gets the sum of its journals and its artifacts; <see cref="long.MaxValue"/> when the sum is past it.</summary>
    public long TotalBytes => JournalBytes > long.MaxValue - ArtifactBytes ? long.MaxValue : JournalBytes + ArtifactBytes;

    /// <summary>Gets the last write of its most recently written journal; null when it has attachments and no journal.</summary>
    public DateTimeOffset? JournalLastWrite { get; init; }
}

/// <summary>A metadata-only, non-atomic measurement of the sessions folder. No file contents are read.</summary>
/// <param name="SessionsRoot">The sessions folder of the instance.</param>
/// <param name="StartedAt">When enumeration started.</param>
/// <param name="CompletedAt">When enumeration finished.</param>
/// <param name="Sessions">
/// Every session that has a file there, once whatever the case of its id (as the catalog keys it), with the spelling
/// first seen, in ordinal order of its id. The list is not bounded, as the list of sessions is not.
/// </param>
/// <param name="UnownedBytes">The sum of the lengths of the files of no session: leases, markers and anything else.</param>
/// <param name="UnownedFiles">The files of no session.</param>
/// <param name="SkippedLinks">Skipped symbolic links or reparse points.</param>
/// <param name="SkippedEntries">Inaccessible, missing-during-scan or otherwise unreadable entries/subtrees.</param>
public sealed record SessionUsageSnapshot(string SessionsRoot, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    IReadOnlyList<SessionUsage> Sessions, long UnownedBytes, long UnownedFiles, long SkippedLinks, long SkippedEntries)
{
    /// <summary>Gets whether some entries could not be measured.</summary>
    public bool IsPartial => SkippedLinks != 0 || SkippedEntries != 0;

    /// <summary>Gets the file entries examined, including known files that were skipped.</summary>
    public long ProcessedFiles { get; init; }

    /// <summary>Gets the child directory entries examined, excluding the sessions folder.</summary>
    public long ProcessedDirectories { get; init; }
}

/// <summary>
/// Measures, in one walk of the sessions folder, the journal and the attachments of each session and what belongs to
/// none. A file belongs to a session by its name, as the session store writes it. Never deletes, never opens a file,
/// never follows a link: a folder that is a link, or is reached through one, counts as a skipped link.
/// </summary>
public sealed class SessionUsageScanner
{
    private readonly string _root;

    internal Action<string, bool>? EntryEnumerated { get; init; }

    /// <summary>Captures the sessions folder of the instance the options describe.</summary>
    /// <param name="options">The options whose <see cref="CatalogOptions.SessionsRoot"/> is measured.</param>
    /// <exception cref="ArgumentNullException">Options is null.</exception>
    /// <exception cref="ArgumentException">The state root is empty or invalid.</exception>
    public SessionUsageScanner(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateRoot);
        _root = Path.GetFullPath(options.SessionsRoot);
    }

    /// <summary>Enumerates metadata on a worker thread.</summary>
    /// <param name="cancellationToken">Stops the walk.</param>
    /// <returns>The measurement.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled the scan.</exception>
    public Task<SessionUsageSnapshot> ScanAsync(CancellationToken cancellationToken)
        => ScanAsync(null, cancellationToken);

    /// <summary>Enumerates metadata off-thread, reporting bounded progress and exact final counts.</summary>
    /// <param name="progress">Told the entries examined so far, at most four times a second, on the worker: it must return promptly.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    /// <returns>The measurement.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled the scan.</exception>
    public Task<SessionUsageSnapshot> ScanAsync(IProgress<ProfileStorageProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Scan(progress, cancellationToken), cancellationToken);

    private SessionUsageSnapshot Scan(IProgress<ProfileStorageProgress>? progress, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var sessions = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
        long unownedBytes = 0, unownedFiles = 0, skippedLinks = 0, skippedEntries = 0;
        long processedFiles = 0, processedDirectories = 0, lastProgress = 0;
        progress?.Report(new(0, 0));
        var folders = new Stack<string>();
        try
        {
            var attributes = File.GetAttributes(_root);
            if (StorageLinks.HasLinkedAncestor(new DirectoryInfo(_root))) skippedLinks++;
            else if ((attributes & FileAttributes.Directory) == 0) skippedEntries++;
            else folders.Push(_root);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // No sessions folder yet: nothing to measure, and nothing is created.
        }
        catch (Exception ex) when (IsReadFailure(ex)) { skippedEntries++; }

        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        while (folders.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The folder, or one above it, may have become a link since it was listed: it is not entered then.
                if (StorageLinks.HasLinkedAncestor(new DirectoryInfo(folder)))
                {
                    skippedLinks++;
                    continue;
                }

                var entries = new FileSystemEnumerable<Entry>(folder, (ref FileSystemEntry entry) =>
                {
                    var link = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    var value = new Entry(entry.ToFullPath(), entry.IsDirectory, link, entry.IsDirectory || link ? 0 : entry.Length,
                        entry.IsDirectory || link ? default : entry.LastWriteTimeUtc);
                    EntryEnumerated?.Invoke(value.Path, value.Directory);
                    return value;
                }, options);
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (entry.Directory) processedDirectories++;
                        else processedFiles++;
                        // The enumeration says what a link is without following it.
                        if (entry.Link) { skippedLinks++; continue; }
                        if (entry.Directory) { folders.Push(entry.Path); continue; }
                        var (owner, kind) = AgentSessionFolderFiles.IsOwnedElsewhere(_root, entry.Path)
                            ? (null, SessionFolderFileKind.Other)
                            : AgentSessionFolderFiles.OwnerOf(_root, entry.Path);
                        if (owner is null)
                        {
                            unownedBytes = checked(unownedBytes + entry.Length);
                            unownedFiles++;
                            continue;
                        }

                        if (!sessions.TryGetValue(owner, out var totals)) sessions[owner] = totals = new();
                        totals.Add(kind == SessionFolderFileKind.Journal, entry.Length, entry.LastWrite);
                    }
                    catch (OverflowException) { skippedEntries++; }
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
            catch (Exception ex) when (IsReadFailure(ex))
            {
                // What was counted of this folder before the failure stays counted.
                skippedEntries++;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(processedFiles, processedDirectories));
        cancellationToken.ThrowIfCancellationRequested();
        var usages = sessions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new SessionUsage(pair.Key, pair.Value.JournalBytes, pair.Value.JournalFiles, pair.Value.ArtifactBytes, pair.Value.ArtifactFiles)
            { JournalLastWrite = pair.Value.JournalLastWrite })
            .ToArray();
        return new(_root, started, DateTimeOffset.UtcNow, usages, unownedBytes, unownedFiles, skippedLinks, skippedEntries)
        { ProcessedFiles = processedFiles, ProcessedDirectories = processedDirectories };
    }

    private static bool IsReadFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private readonly record struct Entry(string Path, bool Directory, bool Link, long Length, DateTimeOffset LastWrite);

    private sealed class Totals
    {
        internal long JournalBytes;
        internal long JournalFiles;
        internal long ArtifactBytes;
        internal long ArtifactFiles;
        internal DateTimeOffset? JournalLastWrite;

        // A sum past the range of a long throws before anything changes: the file is skipped, not wrapped.
        internal void Add(bool journal, long length, DateTimeOffset lastWrite)
        {
            if (journal)
            {
                JournalBytes = checked(JournalBytes + length);
                JournalFiles++;
                if (JournalLastWrite is null || lastWrite > JournalLastWrite) JournalLastWrite = lastWrite;
            }
            else
            {
                ArtifactBytes = checked(ArtifactBytes + length);
                ArtifactFiles++;
            }
        }
    }
}
