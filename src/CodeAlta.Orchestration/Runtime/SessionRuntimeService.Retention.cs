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

    // Claim-free: a recent session never takes the exclusive retirement path. Retirement checks the age again.
    internal Task<bool> IsRetentionCandidateRecentAsync(AgentSessionMetadata candidate, DateTimeOffset now, DateTimeOffset cutoff,
        CancellationToken cancellationToken)
        => _sessionViewCatalog.JournalStore.CreateSessionStore().IsRetentionCandidateRecentAsync(candidate.SessionId, candidate.CreatedAt,
            now, cutoff, cancellationToken);

    // Reads the whole family graph once, outside any exclusive retirement, so that each retirement only re-reads
    // the journals that changed since. Its answer is not used: each retirement reads the graph again.
    internal Task PrepareRetentionFamilyGraphAsync(SessionRetentionPass pass, CancellationToken cancellationToken)
        => _sessionViewCatalog.JournalStore.CreateSessionStore().ReadRetentionFamilyProtectionAsync(pass, cancellationToken);

    internal Task<string> RetireOwnedCatalogSessionAsync(AgentSessionMetadata candidate, DateTimeOffset now,
        DateTimeOffset cutoff, SessionRetentionProtection protection, ISessionStatisticsRetirementArchive archive, SessionRetentionPass? pass,
        CancellationToken cancellationToken)
        => AdmitAsync(() => RetireOwnedCatalogSessionBodyAsync(candidate, now, cutoff, protection, archive, pass, cancellationToken), cancellationToken);

    private async Task<string> RetireOwnedCatalogSessionBodyAsync(AgentSessionMetadata candidate, DateTimeOffset now,
        DateTimeOffset cutoff, SessionRetentionProtection protection, ISessionStatisticsRetirementArchive archive, SessionRetentionPass? pass,
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
            var current = await store.GetSessionAsync(candidate.SessionId, token).ConfigureAwait(false);
            if (current is null || current.CreatedAt != candidate.CreatedAt || current.WorkspacePath != candidate.WorkspacePath)
                return "session_missing";
            // Family retirement needs a multi-actor reservation. Until that exists protect both parents and leaves,
            // including attribution lineage. A corrupt/ambiguous graph is never treated as independent sessions.
            var graph = await store.ReadRetentionFamilyProtectionAsync(pass, token).ConfigureAwait(false);
            if (graph is null) return "family_graph_unknown";
            if (graph.Contains(candidate.SessionId)) return "family_protected";
            var local = await _sessionViewCatalog.JournalStore.ReadLatestStateAsync(candidate.SessionId, candidate.CreatedAt, token).ConfigureAwait(false);
            if (local is not null && (local.QueuedPrompts.Any(item => item.State != "submitted")
                || !string.IsNullOrWhiteSpace(local.ParentSessionId) || !string.IsNullOrWhiteSpace(local.CreatedBy?.SourceSessionId)))
                return "queued_work_or_family";

            SessionStatisticsRetirementReceipt? receipt = null;
            var result = await store.RetireSessionAsync(candidate.SessionId, candidate.CreatedAt, now, cutoff, async (journal, archiveToken) =>
            {
                var prepared = await archive.PrepareAsync(journal.SessionId, archiveToken).ConfigureAwait(false);
                receipt = prepared.Receipt;
                return receipt is not null && !string.IsNullOrWhiteSpace(receipt.Generation)
                    && !string.IsNullOrWhiteSpace(receipt.PayloadSha256)
                    && string.Equals(receipt.Source.SessionId, journal.SessionId, StringComparison.Ordinal)
                    && string.Equals(receipt.Source.Path, journal.JournalPath, StringComparison.OrdinalIgnoreCase)
                    && receipt.Source.Length == journal.Length && receipt.Source.LastWriteUtc == journal.LastWriteUtc
                    && string.Equals(receipt.Source.Sha256, journal.Sha256, StringComparison.OrdinalIgnoreCase)
                    && await archive.ValidateAsync(receipt, archiveToken).ConfigureAwait(false);
            }, delete => protection.DeleteIfReady(candidate.SessionId, delete), token).ConfigureAwait(false);
            if (!result.Deleted) return result.Status;
            pass?.AddFreedBytes(result.FreedBytes);
            lock (_identityGate) _retiredSessionIds.Add(candidate.SessionId);
            // A reconciliation failure never retries artifact deletion. The durable participant rehydrates the
            // archive after restart and can reconcile its missing journal idempotently.
            try
            {
                await _agentSessionCatalog.NotifySessionDeletedAsync(candidate.SessionId, CancellationToken.None).ConfigureAwait(false);
                if (!await archive.ReconcileDeletedAsync(receipt!, result.ArtifactsRemaining, CancellationToken.None).ConfigureAwait(false))
                    return "deleted_reconciliation_pending";
            }
            catch (Exception) { return "deleted_reconciliation_pending"; }
            if (result.Status == "reconciliation_pending") return "deleted_reconciliation_pending";
            return result.ArtifactsRemaining ? "deleted_partial_artifacts" : "deleted";
        // Query cancellation cancels only its reply wait, not accepted actor work. Hold every claim until the
        // original callback finishes; propagate shutdown to its body instead of canceling that ownership wait.
        }, CancellationToken.None).ConfigureAwait(false);
    }
}
