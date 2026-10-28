using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

// The leases of the sessions across processes (SessionLeaseStore): this runtime holds the lease of every session it has an
// attachment for, of the sessions a frontend shows, and of a session while it writes a change to it. A session whose lease
// another process holds (the command line, another instance on the same sessions folder) is neither attached nor changed.
public sealed partial class SessionRuntimeService
{
    private readonly object _shownGate = new();
    private readonly Dictionary<string, IDisposable?> _shown = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _idleSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _idleSweepStop = new();
    private Task? _idleSweep;

    // The leases of the sessions, when the host takes them; null takes none.
    internal SessionLeaseRegistry? Leases { get; init; }

    // How long a session nobody shows stays attached once it has nothing left to do, and how often that is looked at (set
    // before the first SetShownSessions).
    internal TimeSpan IdleReleaseDelay { get; set; } = TimeSpan.FromSeconds(15);

    internal TimeSpan IdleSweepInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The code of a command refused because another process holds the session (<see cref="SessionInUseException"/>).</summary>
    public const string SessionInUseCode = "session_in_use";

    /// <summary>Finds the refusal of a session another process holds in a failure, wrapped or not.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns>The refusal; <see langword="null"/> when the failure is another one.</returns>
    public static SessionInUseException? FindSessionInUse(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 8; depth++)
        {
            switch (exception)
            {
                case SessionInUseException inUse:
                    return inUse;
                case AggregateException aggregate:
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        if (FindSessionInUse(inner) is { } found) return found;
                    }

                    return null;
                default:
                    exception = exception.InnerException;
                    break;
            }
        }

        return null;
    }

    /// <summary>Gets whether this runtime takes the leases of the sessions it runs, so that no other process runs them too.</summary>
    public bool TakesSessionLeases => Leases is not null;

    /// <summary>
    /// Gets who holds the lease of a session when it is another process (the command line, another instance): that process
    /// runs the session or shows it, and this runtime neither attaches nor changes it until it releases it.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The holder; <see langword="null"/> when this runtime holds the lease, nobody does, or the runtime takes no leases.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    public SessionLeaseHolder? GetSessionLeaseHolderElsewhere(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Leases?.HeldElsewhere(sessionId.Trim());
    }

    /// <summary>Gets the sessions whose lease this runtime holds now.</summary>
    /// <returns>The session ids; empty for a runtime that takes no leases.</returns>
    public IReadOnlyCollection<string> GetHeldSessionLeases() => Leases?.Held ?? [];

    /// <summary>
    /// Holds the lease of a session until the returned handle is disposed, for a caller that opens a session before it runs it
    /// (a headless run): no other process can take the session meanwhile.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The handle; a handle that holds nothing for a runtime that takes no leases.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="SessionInUseException">Another process holds the session.</exception>
    /// <exception cref="SessionLeaseUnavailableException">The lease cannot be taken: the folder of the leases cannot be written.</exception>
    public IDisposable HoldSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Leases?.Hold(sessionId.Trim()) ?? NoLease.Instance;
    }

    /// <summary>
    /// Says which sessions the frontend shows (the session tabs of every window and space): this runtime holds their leases,
    /// so the command line cannot take them while they are open. From the first call on, a session the frontend does not show
    /// and that has nothing left to do (no run, queue, job, background task, pending prompt or remote control) is detached
    /// after a short while, and its lease released: another process can then continue it.
    /// </summary>
    /// <param name="sessionIds">Every session the frontend shows now.</param>
    /// <remarks>A session another process holds is not taken; it is taken once that process releases it (<see cref="RetryShownSessionLeases"/>).</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="sessionIds"/> is null.</exception>
    public void SetShownSessions(IEnumerable<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        var wanted = new HashSet<string>(sessionIds.Where(static id => !string.IsNullOrWhiteSpace(id)).Select(static id => id.Trim()), StringComparer.OrdinalIgnoreCase);
        var released = new List<IDisposable>();
        lock (_shownGate)
        {
            if (_disposed) return;
            foreach (var (id, hold) in _shown.ToArray())
            {
                if (wanted.Contains(id)) continue;
                _shown.Remove(id);
                if (hold is not null) released.Add(hold);
            }

            foreach (var id in wanted) _shown.TryAdd(id, null);
            TakeShownLeasesLocked();
            _idleSweep ??= Leases is null ? null : Task.Run(SweepIdleSessionsAsync);
        }

        foreach (var hold in released) hold.Dispose();
    }

    /// <summary>Tries again to take the leases of the shown sessions another process held, once it released one.</summary>
    public void RetryShownSessionLeases()
    {
        lock (_shownGate)
        {
            if (!_disposed) TakeShownLeasesLocked();
        }
    }

    // Under _shownGate.
    private void TakeShownLeasesLocked()
    {
        if (Leases is null) return;
        foreach (var (id, hold) in _shown.ToArray())
        {
            if (hold is not null) continue;
            try
            {
                _shown[id] = Leases.Hold(id);
            }
            catch (SessionInUseException)
            {
                // Shown as used elsewhere; taken when it is released.
            }
            catch (SessionLeaseUnavailableException)
            {
                // Taken at a later retry; meanwhile its attachment and its changes are refused all the same.
            }
        }
    }

    private bool IsShown(string sessionId)
    {
        lock (_shownGate) return _shown.ContainsKey(sessionId);
    }

    // Holds a session while a change is written to it: a session another process runs is not changed, nor one whose lease
    // cannot be taken. Throws SessionInUseException, SessionLeaseUnavailableException.
    private IDisposable HoldForWrite(string sessionId)
        => Leases is null || string.IsNullOrWhiteSpace(sessionId) ? NoLease.Instance : Leases.Hold(sessionId.Trim());

    // Holds a session while a change that reports a status is written to it; false when another process holds it. A lease
    // that cannot be taken throws SessionLeaseUnavailableException.
    private bool TryHoldForWrite(string sessionId, out IDisposable lease)
    {
        try
        {
            lease = HoldForWrite(sessionId);
            return true;
        }
        catch (SessionInUseException)
        {
            lease = NoLease.Instance;
            return false;
        }
    }

    // The status of a change refused because another process holds the session, or null.
    private string? RefuseIfHeldElsewhere(string sessionId)
        => Leases?.HeldElsewhere(sessionId) is not null ? "session_in_use" : null;

    // The attachment of a session claims its lease before anything is written for it. Throws SessionInUseException,
    // SessionLeaseUnavailableException.
    private void ClaimEntryLease(string sessionId) => Leases?.ClaimEntry(sessionId);

    // Called where an attachment goes away for good (not where it is replaced). The sandbox worker of the session goes
    // with it, unless a background command still runs there.
    private void ReleaseEntryLease(string sessionId)
    {
        Leases?.ReleaseEntry(sessionId);
        StopUnusedSandboxWorker(sessionId);
    }

    // An attachment removed while it retires: the lease goes once the retirement ended, since the attachment may still write
    // until then, unless another attachment of the session was published meanwhile.
    private void ReleaseEntryLeaseAfter(string sessionId, Task retirement)
    {
        _ = retirement.ContinueWith(_ => ReleaseEntryLeaseWithoutEntry(sessionId), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // An attachment that could not be made keeps the lease only for the attachment it replaces.
    private void ReleaseEntryLeaseWithoutEntry(string sessionId)
    {
        if (!_entries.ContainsKey(sessionId)) ReleaseEntryLease(sessionId);
    }

    // Detaches the sessions nobody shows once they have had nothing to do for a while, so that their leases are released.
    private async Task SweepIdleSessionsAsync()
    {
        using var timer = new PeriodicTimer(IdleSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_idleSweepStop.Token).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var sessionId in _entries.Keys.ToArray())
                {
                    if (_idleSweepStop.IsCancellationRequested) return;
                    if (IsShown(sessionId) || !await IsIdleForReleaseAsync(sessionId).ConfigureAwait(false))
                    {
                        lock (_shownGate) _idleSince.Remove(sessionId);
                        continue;
                    }

                    DateTimeOffset since;
                    lock (_shownGate)
                    {
                        if (!_idleSince.TryGetValue(sessionId, out since)) _idleSince[sessionId] = since = now;
                    }

                    if (now - since < IdleReleaseDelay) continue;
                    await DetachIdleSessionAsync(sessionId).ConfigureAwait(false);
                    lock (_shownGate) _idleSince.Remove(sessionId);
                }

                lock (_shownGate)
                {
                    foreach (var id in _idleSince.Keys.ToArray())
                    {
                        if (!_entries.ContainsKey(id)) _idleSince.Remove(id);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_idleSweepStop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Whether a session has nothing left to do that its attachment would carry: what settlement waits for, and what only an
    // attachment keeps (remote control, an agent prompt chosen for the next prompt).
    private async Task<bool> IsIdleForReleaseAsync(string sessionId)
    {
        if (_remoteControlWanted.ContainsKey(sessionId)) return false;
        if (_entries.TryGetValue(sessionId, out var entry) && entry.PendingAgentPromptId is not null) return false;
        try
        {
            return !(await ProbeSessionAsync(sessionId, _idleSweepStop.Token).ConfigureAwait(false)).Busy;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private async Task DetachIdleSessionAsync(string sessionId)
    {
        try
        {
            await AdmitAsync(() => DetachOwnedBodyAsync(sessionId, onlyIfIdle: true), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or OperationCanceledException or IOException)
        {
            // A runtime that closes, or a session that went away meanwhile, has nothing to detach.
        }
    }

    // Under the actor of the session, just before it is detached: nothing started since the sweep looked at it.
    private async ValueTask<bool> IsStillIdleOnActorAsync(RuntimeSessionEntry entry, CancellationToken cancellationToken)
    {
        if (IsShown(entry.SessionId) || entry.Attachment.IsRetiring) return false;
        // An attachment whose session shut down has nothing to carry.
        if (entry.IsTerminated) return true;
        if (entry.HasActiveRun || entry.QueueDrainInProgress
            || entry.PendingAgentPromptId is not null || entry.BackgroundTasks.Any(static task => task.Outcome is null)
            || _remoteControlWanted.ContainsKey(entry.SessionId) || Jobs.HasUnsettled(entry.SessionId) || _forwarding.HasSessionWork(entry.SessionId)
            || OwnedWork?.Invoke(entry.SessionId) == true)
            return false;
        lock (_identityGate)
        {
            if (_retentionWork.ContainsKey(entry.SessionId)) return false;
        }

        return _abortHolds.ContainsKey(entry.SessionId)
            || (await ReadLatestLocalStateAsync(entry.SessionId, entry.CreatedAt, cancellationToken).ConfigureAwait(false))?.HasPendingQueuedPrompts() != true;
    }

    private void DisposeLeases()
    {
        _idleSweepStop.Cancel();
        List<IDisposable> holds;
        lock (_shownGate)
        {
            holds = [.. _shown.Values.OfType<IDisposable>()];
            _shown.Clear();
        }

        foreach (var hold in holds) hold.Dispose();
        Leases?.Dispose();
    }

    // The holds of a change of several sessions, released together.
    private sealed class LeaseList(List<IDisposable> leases) : IDisposable
    {
        public void Dispose()
        {
            foreach (var lease in leases) lease.Dispose();
            leases.Clear();
        }
    }

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }
}
