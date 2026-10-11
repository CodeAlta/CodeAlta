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
        var pass = new SessionRetentionPass();
        var graphPrepared = false;
        string? partial = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (cutoff is null) return Set("skipped", "disabled");
            if (now.Offset != TimeSpan.Zero || cutoff.Value.Offset != TimeSpan.Zero || cutoff >= now)
                return Set("skipped", "invalid_cutoff");
            if (_archive is null) return Set("skipped", "archive_unavailable");
            if (!_protection.IsReady) return Set("skipped", _protection.BlockedReason ?? "protection_unavailable");
            Set("running", null);
            // Listing is discovery, never age authority. The exact held journal is streamed again under ownership.
            var candidates = new List<CodeAlta.Agent.AgentSessionMetadata>();
            await foreach (var candidate in _runtime.ListRetentionCandidatesAsync(token).ConfigureAwait(false)) candidates.Add(candidate);
            total = candidates.Count;
            Set("running", null);
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                examined++;
                // Most sessions are recent: their age is read without any claim, so they never hold back the
                // commands of the user. Only an expired session takes the exclusive path, which reads it again.
                string result;
                if (await _runtime.IsRetentionCandidateRecentAsync(candidate, now, cutoff.Value, token).ConfigureAwait(false))
                    result = "not_expired";
                else
                {
                    if (!graphPrepared)
                    {
                        await _runtime.PrepareRetentionFamilyGraphAsync(pass, token).ConfigureAwait(false);
                        graphPrepared = true;
                    }
                    result = await _commands.RetireCatalogSessionAsync(candidate, now, cutoff.Value, _protection, _archive, pass, token).ConfigureAwait(false);
                }
                if (result.StartsWith("deleted", StringComparison.Ordinal))
                {
                    deleted++;
                    if (result != "deleted") partial = result;
                }
                // A recent session is kept as the policy wants: that does not make the pass partial.
                else { skipped++; if (result != "not_expired") partial ??= result; }
                Set("running", partial);
            }
            return Set(partial is null ? "completed" : "partial", partial);
        }
        catch (OperationCanceledException) { return Set("partial", "canceled"); }
        catch (Exception) { return Set("failed", "retention_unavailable"); }

        SessionRetentionStatus Set(string state, string? reason)
        {
            var status = new SessionRetentionStatus(state, examined, deleted, skipped, reason) { Total = total, FreedBytes = pass.FreedBytes };
            lock (_gate)
            {
                Volatile.Write(ref _status, status);
                foreach (var subscription in _subscriptions.ToArray()) subscription.Notify(status);
            }
            return status;
        }
    }
}
