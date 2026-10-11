using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// One instance-owned startup pass. Policy validation/calendar arithmetic belong to Catalog; this service consumes
/// one captured UTC now/cutoff. Nothing runs until explicitly called after host restoration and protection readiness.
/// </summary>
public sealed class SessionRetentionService
{
    private readonly OwnedSessionCommandService _commands;
    private readonly SessionRuntimeService _runtime;
    private readonly SessionRetentionProtection _protection;
    private readonly ISessionStatisticsRetirementArchive? _archive;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private Task<SessionRetentionStatus>? _work;
    private SessionRetentionStatus _status = new("not_started", 0, 0, 0, null);

    /// <summary>Creates a coordinator without starting any work or opening a profile.</summary>
    /// <param name="commands">The owned command service that serializes each retirement with the commands of its session.</param>
    /// <param name="runtime">The runtime that owns the sessions.</param>
    /// <param name="protection">The registry of the sessions that are open or have work in progress.</param>
    /// <param name="archive">The Statistics participant that saves the statistics of a session before it is removed, or
    /// null: saving them is best effort, and never keeps an expired session.</param>
    /// <exception cref="ArgumentNullException">An ownership dependency is null.</exception>
    public SessionRetentionService(OwnedSessionCommandService commands, SessionRuntimeService runtime,
        SessionRetentionProtection protection, ISessionStatisticsRetirementArchive? archive)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(protection);
        _commands = commands;
        _runtime = runtime;
        _protection = protection;
        _archive = archive;
    }

    /// <summary>Gets the latest bounded immutable outcome for late subscribers.</summary>
    public SessionRetentionStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// Subscribes to bounded status changes and immediately replays the current value. Callbacks run in order,
    /// must not block, and cannot fail the retention worker. Dispose the returned lease to stop notifications.
    /// </summary>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    public IDisposable Subscribe(Action<SessionRetentionStatus> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            var subscription = new Subscription(this, callback);
            _subscriptions.Add(subscription);
            subscription.Notify(_status);
            return subscription;
        }
    }

    private sealed class Subscription(SessionRetentionService owner, Action<SessionRetentionStatus> callback) : IDisposable
    {
        private bool _disposed;
        internal void Notify(SessionRetentionStatus status)
        {
            if (_disposed) return;
            try { callback(status); } catch (Exception) { /* Presentation cannot fail retirement. */ }
        }
        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                owner._subscriptions.Remove(this);
            }
        }
    }

    /// <summary>
    /// Starts or joins the one original pass. Null cutoff means Never/disabled. The token belongs to host shutdown;
    /// hosts must join this task before releasing dependencies. Canceling a presentation must not cancel this worker.
    /// </summary>
    public Task<SessionRetentionStatus> RunOnceAsync(DateTimeOffset capturedNow, DateTimeOffset? cutoff,
        CancellationToken hostShutdown = default)
    {
        lock (_gate) return _work ??= Task.Run(() => RunBodyAsync(capturedNow, cutoff, hostShutdown), CancellationToken.None);
    }

    private async Task<SessionRetentionStatus> RunBodyAsync(DateTimeOffset now, DateTimeOffset? cutoff, CancellationToken token)
    {
        var examined = 0;
        var deleted = 0;
        var skipped = 0;
        var total = 0;
        var filesDeleted = 0;
        var pass = new SessionRetentionPass();
        string? partial = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (cutoff is null) return Set("skipped", "disabled");
            if (now.Offset != TimeSpan.Zero || cutoff.Value.Offset != TimeSpan.Zero || cutoff >= now)
                return Set("skipped", "invalid_cutoff");
            if (!_protection.IsReady) return Set("skipped", _protection.BlockedReason ?? "protection_unavailable");
            Set("running", null);
            // Listing is discovery, never age authority. The exact held journal is checked again under ownership.
            var candidates = new List<CodeAlta.Agent.AgentSessionMetadata>();
            await foreach (var candidate in _runtime.ListRetentionCandidatesAsync(token).ConfigureAwait(false)) candidates.Add(candidate);
            total = candidates.Count;
            Set("running", null);

            // The age of a session is the last write time of its journal: a file stat, not a read. A session is removed
            // only once no remaining session names it as its parent or creator, so a recent child keeps its family, and
            // an expired family goes leaves first, in this same pass.
            var lineages = new string[candidates.Count][];
            var references = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var expired = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                lineages[index] = Lineage(candidate);
                foreach (var related in lineages[index]) references[related] = references.GetValueOrDefault(related) + 1;
                if (_runtime.GetRetentionJournalLastWriteUtc(candidate) is { } lastWrite && lastWrite < cutoff.Value)
                    expired.TryAdd(candidate.SessionId, index);
                else
                {
                    // A recent session is kept as the policy wants: that does not make the pass partial.
                    examined++;
                    skipped++;
                }
            }
            Set("running", null);

            var ready = new Queue<int>(expired.Where(pair => references.GetValueOrDefault(pair.Key) == 0).Select(pair => pair.Value));
            var processed = new HashSet<int>();
            var retired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (ready.TryDequeue(out var index))
            {
                token.ThrowIfCancellationRequested();
                processed.Add(index);
                examined++;
                var result = await _commands.RetireCatalogSessionAsync(candidates[index], cutoff.Value, _protection, _archive, pass, token).ConfigureAwait(false);
                if (result.StartsWith("deleted", StringComparison.Ordinal))
                {
                    deleted++;
                    retired.Add(candidates[index].SessionId);
                    if (result != "deleted") partial = result;
                    foreach (var related in lineages[index])
                        if (--references[related] == 0 && expired.TryGetValue(related, out var parent) && !processed.Contains(parent))
                            ready.Enqueue(parent);
                }
                else { skipped++; if (result != "not_expired") partial ??= result; }
                Set("running", partial);
            }
            // Expired, but named by a session that stays: kept with its family, as the policy wants.
            var familyKept = expired.Count - processed.Count;
            examined += familyKept;
            skipped += familyKept;

            // Then every other file of the sessions folder older than the cutoff: what no listed session owns, and what a
            // removed session left. Only the files of a session that stays, or is in use, stay.
            var kept = candidates.Select(candidate => candidate.SessionId).Where(id => !retired.Contains(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            (filesDeleted, _) = await _runtime.SweepExpiredSessionFolderAsync(cutoff.Value, kept, retired, _protection, pass, token).ConfigureAwait(false);
            return Set(partial is null ? "completed" : "partial", partial);
        }
        catch (OperationCanceledException) { return Set("partial", "canceled"); }
        catch (Exception) { return Set("failed", "retention_unavailable"); }

        SessionRetentionStatus Set(string state, string? reason)
        {
            var status = new SessionRetentionStatus(state, examined, deleted, skipped, reason) { Total = total, FreedBytes = pass.FreedBytes, FilesDeleted = filesDeleted };
            lock (_gate)
            {
                Volatile.Write(ref _status, status);
                foreach (var subscription in _subscriptions.ToArray()) subscription.Notify(status);
            }
            return status;
        }
    }

    // The sessions a session names: the same relations manual deletion checks before removing a parent.
    private static string[] Lineage(CodeAlta.Agent.AgentSessionMetadata session)
        => new[] { session.ParentSessionId, session.CreatedBySessionId, session.ViewState?.ParentSessionId }
            .Where(id => !string.IsNullOrWhiteSpace(id) && !string.Equals(id.Trim(), session.SessionId, StringComparison.OrdinalIgnoreCase))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
