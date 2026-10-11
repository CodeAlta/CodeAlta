using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    private readonly Dictionary<string, int> _retentionWork = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _retentionClaims = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _retiredSessionIds = new(StringComparer.OrdinalIgnoreCase);

    // Shared by lineage publication and direct runtime mutation, not just the owned-command entry point.
    // A queued actor command cannot become new work after deletion: its original admission remains owned.
    private IDisposable ClaimRetentionWork(params string?[] sessionIds)
    {
        var ids = sessionIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_identityGate)
        {
            if (ids.Any(id => _retentionClaims.Contains(id) || _retiredSessionIds.Contains(id)))
                throw new InvalidOperationException("The session is being retired or has been retired.");
            foreach (var id in ids) _retentionWork[id] = _retentionWork.GetValueOrDefault(id) + 1;
            return new RuntimeRetentionLease(this, ids, false);
        }
    }

    private IDisposable? TryClaimRuntimeRetirement(string sessionId)
    {
        lock (_identityGate)
        {
            if (_retentionWork.ContainsKey(sessionId) || _retiredSessionIds.Contains(sessionId) || !_retentionClaims.Add(sessionId)) return null;
            return new RuntimeRetentionLease(this, [sessionId], true);
        }
    }

    private sealed class RuntimeRetentionLease(SessionRuntimeService owner, string[] ids, bool retirement) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._identityGate)
                foreach (var id in ids)
                    if (retirement) owner._retentionClaims.Remove(id);
                    else if (--owner._retentionWork[id] == 0) owner._retentionWork.Remove(id);
        }
    }

    internal IAsyncEnumerable<AgentSessionMetadata> ListRetentionCandidatesAsync(CancellationToken cancellationToken)
        => _sessionViewCatalog.JournalStore.CreateSessionStore().ListSessionsAsync(cancellationToken: cancellationToken);

    // Claim-free and read-free: a file stat. Retirement reads the age again from the handle it holds.
    internal DateTimeOffset? GetRetentionJournalLastWriteUtc(AgentSessionMetadata candidate)
        => _sessionViewCatalog.JournalStore.CreateSessionStore().GetRetentionJournalLastWriteUtc(candidate.SessionId, candidate.CreatedAt);

    internal Task<string> RetireOwnedCatalogSessionAsync(AgentSessionMetadata candidate, DateTimeOffset cutoff,
        SessionRetentionProtection protection, ISessionStatisticsRetirementArchive? archive, SessionRetentionPass? pass,
        CancellationToken cancellationToken)
        => AdmitAsync(() => RetireOwnedCatalogSessionBodyAsync(candidate, cutoff, protection, archive, pass, cancellationToken), cancellationToken);

    private async Task<string> RetireOwnedCatalogSessionBodyAsync(AgentSessionMetadata candidate, DateTimeOffset cutoff,
        SessionRetentionProtection protection, ISessionStatisticsRetirementArchive? archive, SessionRetentionPass? pass,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var hostClaim = protection.TryReserveRetirement(candidate.SessionId);
        if (hostClaim is null) return "protection_unavailable";
        using var jobClaim = Jobs.TryReserveRetirement(candidate.SessionId);
        if (jobClaim is null) return "jobs_or_delivery";
        using var runtimeClaim = TryClaimRuntimeRetirement(candidate.SessionId);
        if (runtimeClaim is null) return "runtime_admission_or_lineage";
        var actor = GetActorForWork(candidate.SessionId);
        // Holding this original query serializes direct hub/runtime admissions, not merely UI/owned sends.
        // Never detach an idle-looking provider: any attachment (remote/background work included) protects it.
        return await actor.QueryAsync(async actorToken =>
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(actorToken, cancellationToken);
            var token = lifetime.Token;
            token.ThrowIfCancellationRequested();
            if (_entries.ContainsKey(candidate.SessionId) || _transitions.ContainsKey(candidate.SessionId)
                || _remoteControlWanted.ContainsKey(candidate.SessionId)) return "session_in_use";
            if ((await Permissions.ListWaitingSessionsAsync().ConfigureAwait(false)).Contains(candidate.SessionId, StringComparer.OrdinalIgnoreCase)
                || (await Permissions.ListAsync().ConfigureAwait(false)).Any(item => string.Equals(item.Handle.SessionId, candidate.SessionId, StringComparison.OrdinalIgnoreCase)))
                return "interaction_pending";
            var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
            SessionStatisticsRetirementReceipt? receipt = null;
            var result = await store.RetireSessionAsync(candidate.SessionId, candidate.CreatedAt, cutoff,
                async (journal, saveToken) => receipt = await TrySaveStatisticsAsync(archive, journal, pass, saveToken).ConfigureAwait(false),
                delete => protection.DeleteIfReady(candidate.SessionId, delete), token).ConfigureAwait(false);
            if (!result.Deleted) return result.Status;
            pass?.AddFreedBytes(result.FreedBytes);
            lock (_identityGate) _retiredSessionIds.Add(candidate.SessionId);
            try { await _agentSessionCatalog.NotifySessionDeletedAsync(candidate.SessionId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { return "deleted_reconciliation_pending"; }
            if (receipt is not null)
            {
                // Statistics marks the session deleted itself when it next finds its journal gone: a failure here loses nothing.
                try { await archive!.ReconcileDeletedAsync(receipt, result.ArtifactsRemaining, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* Best effort. */ }
            }
            if (result.Status == "reconciliation_pending") return "deleted_reconciliation_pending";
            return result.ArtifactsRemaining ? "deleted_partial_artifacts" : "deleted";
        // Query cancellation cancels only its reply wait, not accepted actor work. Hold every claim until the
        // original callback finishes; propagate shutdown to its body instead of canceling that ownership wait.
        }, CancellationToken.None).ConfigureAwait(false);
    }

    // Saved Statistics are best effort: whatever Statistics answers, or fails to answer, the expired session is deleted.
    // Statistics keeps the facts it has already read of a deleted session; the saved copy only survives a reset. A
    // participant that does not answer in time is not asked again during the pass, so it can never hold the cleanup back.
    private static async Task<SessionStatisticsRetirementReceipt?> TrySaveStatisticsAsync(ISessionStatisticsRetirementArchive? archive,
        SessionJournalRetirementSource journal, SessionRetentionPass? pass, CancellationToken cancellationToken)
    {
        if (archive is null || pass?.StatisticsUnavailable == true) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatisticsSaveTimeout);
        try
        {
            var receipt = (await archive.PrepareAsync(journal.SessionId, timeout.Token).ConfigureAwait(false)).Receipt;
            return receipt is not null && string.Equals(receipt.Source.SessionId, journal.SessionId, StringComparison.Ordinal)
                && string.Equals(receipt.Source.Path, journal.JournalPath, StringComparison.OrdinalIgnoreCase)
                && receipt.Source.Length == journal.Length && receipt.Source.LastWriteUtc == journal.LastWriteUtc ? receipt : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            pass?.MarkStatisticsUnavailable();
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static readonly TimeSpan StatisticsSaveTimeout = TimeSpan.FromSeconds(60);
}
