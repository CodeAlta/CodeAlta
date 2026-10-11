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
}

internal sealed record SessionJournalRetirementSource(string SessionId, string JournalPath, string FileIdentity,
    long Length, DateTimeOffset LastWriteUtc);
internal sealed record SessionJournalRetirementResult(string Status, bool Deleted, bool ArtifactsRemaining)
{
    /// <summary>Gets the bytes of the journal and of the artifacts removed with it.</summary>
    internal long FreedBytes { get; init; }
}
