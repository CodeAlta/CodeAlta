namespace CodeAlta.Agent.Runtime;

public sealed partial class FileSystemAgentSessionStore
{
    // The age of a session is the last write time of its journal: every record a session gains is appended to it, so a
    // journal that has not been written since the cutoff holds no later work. It is a file stat, never a read of the
    // journal, and it fails for no content: an old journal in a format this version no longer reads still has an age.
    // Null when the journal is not where the layout puts it.
    internal DateTimeOffset? GetRetentionJournalLastWriteUtc(string sessionId, DateTimeOffset createdAt)
    {
        try
        {
            var file = new FileInfo(_layout.GetSessionFilePath(sessionId, createdAt));
            return file.Exists ? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // Only the existing owned-command/runtime boundary calls this operation. Ordinary/manual deletion keeps its
    // separate semantics. The age is read again from the held handle, which denies writers until the deletion.
    // The statistics callback is best effort: it runs while the journal is held, and its outcome never keeps a session.
    internal async Task<SessionJournalRetirementResult> RetireSessionAsync(string sessionId, DateTimeOffset createdAt,
        DateTimeOffset cutoff,
        Func<SessionJournalRetirementSource, CancellationToken, Task> saveStatistics,
        Func<Func<bool>, bool> commitWhileProtected, CancellationToken cancellationToken)
    {
        var path = _layout.GetSessionFilePath(sessionId, createdAt);
        var result = new SessionJournalRetirementResult("session_missing", false, false);
        await _journalFile.WithPathLockAsync(path, async () =>
        {
            try
            {
                if (!File.Exists(path)) return;
                using (var journal = RetentionJournalLease.Open(_layout, sessionId, createdAt))
                {
                    if (journal.LastWriteUtc >= cutoff) { result = new("not_expired", false, false); return; }
                    var source = new SessionJournalRetirementSource(sessionId, journal.Path, journal.Identity, journal.Stream.Length, journal.LastWriteUtc);
                    using var artifacts = journal.CaptureArtifacts(_layout, sessionId);
                    await saveStatistics(source, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    // The stream and its ancestors have remained locked since the age was read.
                    if (!commitWhileProtected(() => { journal.Delete(); return true; }))
                    { result = new("protection_unavailable", false, false); return; }
                    result = new("ok", true, true);
                    var journalBytes = journal.Stream.Length;
                    result = result with { ArtifactsRemaining = artifacts.DeleteCaptured() };
                    result = result with { FreedBytes = journalBytes + artifacts.DeletedBytes };
                }
                // Close the delete-pending handles before reconciling projections. Never remove parent directories
                // by a stale path, and never sweep leftover artifacts on a subsequent absent-journal retry.
                _sessionFiles.TryRemove(sessionId, out _);
                InvalidateMetadataProjectionCache(path);
                _journalFile.ForgetLatestNotes(path);
                await RemoveCacheAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                await RemoveIndexAsync(path, CancellationToken.None).ConfigureAwait(false);
            }
            catch (PlatformNotSupportedException) { result = new("platform_unsupported", false, false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result = result.Deleted ? result with { Status = "reconciliation_pending", ArtifactsRemaining = true }
                    : new("unsafe_or_unavailable_source", false, false);
            }
            catch (Exception) when (result.Deleted)
            {
                result = result with { Status = "reconciliation_pending" };
            }
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    // Every file of the sessions folder last written before the cutoff, whether it belongs to a session or not: a stat of
    // each, never a read. Links are not followed. A file belongs to a session by its name: its journal, a file of its
    // attachments folder, its trace.
    internal IReadOnlyList<SessionFolderFile> ListExpiredSessionFolderFiles(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var root = _layout.SessionsRootPath;
        var files = new List<SessionFolderFile>();
        if (!Directory.Exists(root)) return files;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (lastWrite >= cutoff) continue;
            var (owner, kind) = OwnerOf(root, info.FullName);
            files.Add(new SessionFolderFile(info.FullName, owner, kind, info.Length, lastWrite));
        }

        return files;
    }

    private static (string? Owner, SessionFolderFileKind Kind) OwnerOf(string root, string path)
    {
        var parts = Path.GetRelativePath(root, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var name = parts[^1];
        if (parts.Length == 2 && string.Equals(parts[0], "traces", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".trace", StringComparison.OrdinalIgnoreCase))
            return (Owner(name[..^".trace".Length]), SessionFolderFileKind.Artifact);
        foreach (var part in parts.AsSpan(0, parts.Length - 1))
        {
            if (part.EndsWith(".attachments", StringComparison.OrdinalIgnoreCase))
                return (Owner(part[..^".attachments".Length]), SessionFolderFileKind.Artifact);
        }

        return name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? (Owner(name[..^".jsonl".Length]), SessionFolderFileKind.Journal) : (null, SessionFolderFileKind.Other);

        static string? Owner(string id) => string.IsNullOrWhiteSpace(id) ? null : id;
    }

    // Deletes one file of the sessions folder by its path, after a new stat: a file written since the listing, or a link,
    // stays. Returns the bytes freed, or -1 when the file stays. Serialized with the writers of this process by the path.
    internal async Task<long> DeleteExpiredSessionFolderFileAsync(SessionFolderFile file, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        long freed = -1;
        try
        {
            await _journalFile.WithPathLockAsync(file.Path, async () =>
            {
                var info = new FileInfo(file.Path);
                if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0
                    || new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) >= cutoff) return;
                var length = info.Length;
                if ((info.Attributes & FileAttributes.ReadOnly) != 0) info.Attributes &= ~FileAttributes.ReadOnly;
                info.Delete();
                freed = length;
                if (file.Kind == SessionFolderFileKind.Journal && file.OwnerSessionId is { } sessionId)
                {
                    if (_sessionFiles.TryGetValue(sessionId, out var known) && string.Equals(known, file.Path, StringComparison.OrdinalIgnoreCase))
                        _sessionFiles.TryRemove(sessionId, out _);
                    InvalidateMetadataProjectionCache(file.Path);
                    _journalFile.ForgetLatestNotes(file.Path);
                    await RemoveCacheAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    await RemoveIndexAsync(file.Path, CancellationToken.None).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file another process holds stays for the next pass.
        }

        return freed;
    }

    // Removes the folders the expired files leave empty: an attachments folder (or one of its folders) of a session that is
    // not kept, and a date
    // folder (year, month or day) that ends before the cutoff, which no new session can write to. Never the root or the
    // traces folder, never a link, never a folder that is not empty.
    internal void RemoveEmptyExpiredSessionFolders(DateTimeOffset cutoff, IReadOnlySet<string> keptSessionIds, CancellationToken cancellationToken)
    {
        var root = _layout.SessionsRootPath;
        if (!Directory.Exists(root)) return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        var folders = new DirectoryInfo(root).EnumerateDirectories("*", options).Select(folder => folder.FullName)
            .OrderByDescending(path => path.Length).ToList();
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = Path.GetRelativePath(root, folder).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var attachments = Array.FindLast(parts, part => part.EndsWith(".attachments", StringComparison.OrdinalIgnoreCase));
            var expired = attachments is not null
                ? !keptSessionIds.Contains(attachments[..^".attachments".Length])
                : DateFolderEnd(parts) is { } end && end <= cutoff;
            if (!expired) continue;
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0 && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Not empty any more, or held: it stays.
            }
        }

        // The first instant no session file of a yyyy[/MM[/dd]] folder can be dated at.
        static DateTimeOffset? DateFolderEnd(string[] parts)
        {
            if (parts.Length is < 1 or > 3) return null;
            var numbers = new int[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                if (!int.TryParse(parts[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[index])) return null;
            }

            try
            {
                var start = new DateTimeOffset(numbers[0], parts.Length > 1 ? numbers[1] : 1, parts.Length > 2 ? numbers[2] : 1, 0, 0, 0, TimeSpan.Zero);
                // A day of margin past the end of the UTC date the folder is named after.
                return (parts.Length switch { 1 => start.AddYears(1), 2 => start.AddMonths(1), _ => start.AddDays(1) }).AddDays(1);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}

internal sealed record SessionJournalRetirementSource(string SessionId, string JournalPath, string FileIdentity,
    long Length, DateTimeOffset LastWriteUtc);

internal enum SessionFolderFileKind
{
    Journal,
    Artifact,
    Other,
}

internal sealed record SessionFolderFile(string Path, string? OwnerSessionId, SessionFolderFileKind Kind, long Length, DateTimeOffset LastWriteUtc);

internal sealed record SessionJournalRetirementResult(string Status, bool Deleted, bool ArtifactsRemaining)
{
    /// <summary>Gets the bytes of the journal and of the artifacts removed with it.</summary>
    internal long FreedBytes { get; init; }
}
