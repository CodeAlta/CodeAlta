using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

public sealed partial class FileSystemAgentSessionStore
{
    // Unlike normal listing, an incomplete projection is not silently omitted: a hidden child could protect
    // any parent. Read the physical graph without the best-effort SQLite listing, and reject links/duplicates.
    internal Task<IReadOnlySet<string>?> ReadRetentionFamilyProtectionAsync(CancellationToken cancellationToken)
        => ReadRetentionFamilyProtectionAsync(null, cancellationToken);

    // With a pass, a journal whose size and times did not change since that pass read it is not parsed again: without
    // it, every candidate of a pass parsed every journal of the profile, which never ended on large profiles.
    internal async Task<IReadOnlySet<string>?> ReadRetentionFamilyProtectionAsync(SessionRetentionPass? pass, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(_layout.SessionsRootPath)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(_layout.SessionsRootPath);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if (!string.Equals(child, _layout.SessionTracesRootPath, StringComparison.OrdinalIgnoreCase)
                        && !child.EndsWith(".attachments", StringComparison.OrdinalIgnoreCase)) pending.Push(child);
                foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl"))
                {
                    var file = new FileInfo(path);
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                    // Taken before the read: an append during the read changes it, and the next read parses again.
                    var stamp = new RetentionJournalStamp(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
                    if (pass?.TryGetFamily(path, stamp) is not { } family)
                    {
                        family = await ReadRetentionFamilyAsync(path, cancellationToken).ConfigureAwait(false);
                        if (family is null) return null;
                        pass?.SetFamily(path, stamp, family);
                    }
                    if (!ids.Add(family.SessionId)) return null;
                    result.UnionWith(family.Protected);
                }
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<RetentionJournalFamily?> ReadRetentionFamilyAsync(string path, CancellationToken cancellationToken)
    {
        var listed = await ProjectSessionFileForListingAsync(path, cancellationToken).ConfigureAwait(false);
        if (listed?.Projection.Summary is not { } summary
            || !string.Equals(path, _layout.GetSessionFilePath(summary.SessionId, summary.CreatedAt), StringComparison.OrdinalIgnoreCase)) return null;
        var protectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Projection readers may recover around a corrupt record. A retention graph cannot: that
        // lost record may contain the only parent/provenance edge. Reject incomplete tails as well.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0) return null;
        stream.Position = stream.Length - 1;
        if (stream.ReadByte() != '\n') return null;
        stream.Position = 0;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            ValidateObject(document.RootElement);
            if (document.RootElement.Deserialize(AgentJsonSerializerContext.Default.AgentEvent) is not { } item
                || !string.Equals(item.SessionId, summary.SessionId, StringComparison.Ordinal)) return null;
            if (item is not AgentRawEvent raw) continue;
            if (raw.BackendEventType == SessionSummaryEventType)
            {
                ValidateObject(raw.Raw);
                if (raw.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentSessionSummary) is not { } snapshot
                    || snapshot.SessionId != summary.SessionId) return null;
                AddRelation(snapshot.SessionId, snapshot.ParentSessionId);
                AddRelation(snapshot.SessionId, snapshot.CreatedBySessionId);
            }
            else if (raw.BackendEventType is CodeAltaSessionHeaderEventType or CodeAltaSessionStateEventType)
            {
                // Catalog is layered above Agent. Read its stable persisted wire fields here rather
                // than depending on its best-effort cached view projection. Keep every historical edge.
                ValidateObject(raw.Raw);
                if (ReadOptionalString(raw.Raw, "session_id") is { } id && id != summary.SessionId) return null;
                AddRelation(summary.SessionId, ReadOptionalString(raw.Raw, "parent_session_id"));
                if (raw.Raw.TryGetProperty("created_by", out var provenance) && provenance.ValueKind != JsonValueKind.Null)
                {
                    ValidateObject(provenance);
                    if (string.IsNullOrWhiteSpace(ReadOptionalString(provenance, "Kind"))) return null;
                    AddRelation(summary.SessionId, ReadOptionalString(provenance, "SourceSessionId"));
                }
            }
        }
        AddRelation(summary.SessionId, summary.ParentSessionId);
        AddRelation(summary.SessionId, summary.CreatedBySessionId);
        return new(summary.SessionId, [.. protectedIds]);

        void AddRelation(string sessionId, string? related)
        {
            if (related is null) return;
            if (string.IsNullOrWhiteSpace(related) || related != related.Trim()) throw new InvalidDataException("Ambiguous lineage identifier.");
            protectedIds.Add(sessionId);
            protectedIds.Add(related);
        }
    }

    // Reads the age of a journal without any claim, so that a recent session never takes the exclusive retirement
    // path. It can only keep a session: retirement reads the age again under its exclusive lease. A journal that
    // grows meanwhile only gains later activity, so a recent prefix means a recent journal.
    internal async Task<bool> IsRetentionCandidateRecentAsync(string sessionId, DateTimeOffset createdAt, DateTimeOffset now,
        DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(_layout.GetSessionFilePath(sessionId, createdAt), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            var activity = await SessionMeaningfulActivity.ReadAsync(stream, sessionId, createdAt, now, cancellationToken).ConfigureAwait(false);
            return activity.BlockedReason is null && activity.LastMeaningfulActivityUtc is { } last && last >= cutoff;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The exclusive path decides, and reports, what cannot be read here.
            return false;
        }
    }

    private static void ValidateObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Ambiguous lineage object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate lineage property.");
    }

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw new InvalidDataException("Ambiguous lineage field.");
        return property.GetString();
    }

    // Only the existing owned-command/runtime boundary calls this operation. Ordinary/manual deletion keeps its
    // separate semantics. The callback must return a verified source-bound durable archive receipt before true.
    internal async Task<SessionJournalRetirementResult> RetireSessionAsync(string sessionId, DateTimeOffset createdAt,
        DateTimeOffset now, DateTimeOffset cutoff,
        Func<SessionJournalRetirementSource, CancellationToken, Task<bool>> archive,
        Func<Func<bool>, bool> commitWhileProtected, CancellationToken cancellationToken)
    {
        var path = _layout.GetSessionFilePath(sessionId, createdAt);
        var result = new SessionJournalRetirementResult("session_missing", false, false);
        await _journalFile.WithPathLockAsync(path, async () =>
        {
            try
            {
                using (var journal = RetentionJournalLease.Open(_layout, sessionId, createdAt))
                {
                    var activity = await SessionMeaningfulActivity.ReadAsync(journal.Stream, sessionId, createdAt, now, cancellationToken).ConfigureAwait(false);
                    if (activity.BlockedReason is { } reason) { result = new(reason, false, false); return; }
                    if (activity.LastMeaningfulActivityUtc is not { } last || last >= cutoff)
                    { result = new("not_expired", false, false); return; }
                    journal.Stream.Position = 0;
                    var digest = Convert.ToHexString(await SHA256.HashDataAsync(journal.Stream, cancellationToken).ConfigureAwait(false));
                    var source = new SessionJournalRetirementSource(sessionId, journal.Path, journal.Identity, journal.Stream.Length, journal.LastWriteUtc, digest, last);
                    using var artifacts = journal.CaptureArtifacts(_layout, sessionId);
                    if (!await archive(source, cancellationToken).ConfigureAwait(false))
                    { result = new("archive_unavailable", false, false); return; }
                    cancellationToken.ThrowIfCancellationRequested();
                    // The stream and its ancestors have remained locked throughout preparation and verification.
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
    long Length, DateTimeOffset LastWriteUtc, string Sha256, DateTimeOffset LastMeaningfulActivityUtc);
internal sealed record SessionJournalRetirementResult(string Status, bool Deleted, bool ArtifactsRemaining)
{
    /// <summary>Gets the bytes of the journal and of the artifacts removed with it.</summary>
    internal long FreedBytes { get; init; }
}
