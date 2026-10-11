using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

public sealed partial class FileSystemAgentSessionStore
{
    // Unlike normal listing, an incomplete projection is not silently omitted: a hidden child could protect
    // any parent. Read the physical graph without the best-effort SQLite listing, and reject links/duplicates.
    internal async Task<IReadOnlySet<string>?> ReadRetentionFamilyProtectionAsync(CancellationToken cancellationToken)
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
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
                    var listed = await ProjectSessionFileForListingAsync(path, cancellationToken).ConfigureAwait(false);
                    if (listed?.Projection.Summary is not { } summary || !ids.Add(summary.SessionId)
                        || !string.Equals(path, _layout.GetSessionFilePath(summary.SessionId, summary.CreatedAt), StringComparison.OrdinalIgnoreCase)) return null;
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
                }
            }
            return result;

            void AddRelation(string sessionId, string? related)
            {
                if (related is null) return;
                if (string.IsNullOrWhiteSpace(related) || related != related.Trim()) throw new InvalidDataException("Ambiguous lineage identifier.");
                result.Add(sessionId);
                result.Add(related);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        {
            return null;
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
                    result = result with { ArtifactsRemaining = artifacts.DeleteCaptured() };
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
internal sealed record SessionJournalRetirementResult(string Status, bool Deleted, bool ArtifactsRemaining);
