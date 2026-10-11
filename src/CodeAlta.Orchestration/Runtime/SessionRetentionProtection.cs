using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Arbitrates host protection claims against automatic retirement. Unknown participants protect every session.
/// Hosts must publish all windows/spaces/drafts and external work, and claim before exposing new work or a tab.
/// </summary>
public sealed class SessionRetentionProtection : ISessionRetentionAdmission
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Participant> _participants = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retiring = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _retired = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _claims = new(StringComparer.OrdinalIgnoreCase);
    private int _unscopedClaims;
    private HashSet<string>? _required;
    private readonly TaskCompletionSource _initialReadiness = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _changed;

    /// <summary>Waits for the first complete host restoration without starting or retrying maintenance.</summary>
    /// <remarks>Readiness can subsequently be lost; retirement still rechecks every owner under the admission lock.</remarks>
    /// <exception cref="OperationCanceledException">The readiness observer was canceled; claims are unaffected.</exception>
    public Task WaitForReadyAsync(CancellationToken cancellationToken)
        => _initialReadiness.Task.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public IDisposable? TryProtectSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId != sessionId.Trim()) throw new ArgumentException("Session IDs must be canonical, without surrounding whitespace.", nameof(sessionId));
        lock (_gate)
        {
            if (_retiring.Contains(sessionId) || _retired.Contains(sessionId)) return null;
            _claims.TryGetValue(sessionId, out var count);
            _claims[sessionId] = checked(count + 1);
            return new Admission(this, sessionId);
        }
    }

    /// <inheritdoc />
    public IDisposable? TryProtectAll()
    {
        lock (_gate)
        {
            if (_retiring.Count != 0) return null;
            _unscopedClaims = checked(_unscopedClaims + 1);
            return new Admission(this, null);
        }
    }

    /// <summary>Names every required owner once restoration has discovered them. An empty set never authorizes deletion.</summary>
    /// <exception cref="ArgumentNullException">The participant list is null.</exception>
    /// <exception cref="InvalidOperationException">Retirement is underway.</exception>
    public void SealParticipants(IEnumerable<string> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        var required = participants.ToHashSet(StringComparer.Ordinal);
        foreach (var owner in required) ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
        {
            if (_retiring.Count != 0) throw new InvalidOperationException("Cannot replace retention owners during retirement.");
            _required = required;
            if (IsReady) _initialReadiness.TrySetResult();
        }
    }

    /// <summary>
    /// Atomically replaces one owner's complete protection set. False means retirement already owns an ID:
    /// the caller MUST NOT open it, edit its draft, create a reminder/job/delivery or start its work.
    /// Rejected publications leave the preceding snapshot intact.
    /// </summary>
    /// <exception cref="ArgumentException">The owner is blank or a session ID is blank or noncanonical.</exception>
    /// <exception cref="ArgumentNullException">The ID collection is null.</exception>
    public bool Publish(string owner, IEnumerable<string> sessionIds, bool ready)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sessionIds);
        var ids = sessionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (id != id.Trim()) throw new ArgumentException("Session IDs must be canonical, without surrounding whitespace.", nameof(sessionIds));
        }
        lock (_gate)
        {
            if (ids.Overlaps(_retiring) || ids.Overlaps(_retired)) return false;
            _participants[owner] = new(ids, ready);
            if (IsReady) _initialReadiness.TrySetResult();
            SignalChanged();
            return true;
        }
    }

    /// <summary>Marks restoration, a disconnected window or an external-work owner unknown. It does not release existing IDs.</summary>
    /// <exception cref="ArgumentException">The owner is blank.</exception>
    public void MarkUnknown(string owner)
        => MarkUnknown(owner, null);

    /// <summary>Marks an owner unknown with a bounded non-sensitive qualification reason for the startup summary.</summary>
    /// <exception cref="ArgumentException">The owner is blank.</exception>
    public void MarkUnknown(string owner, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
        {
            var ids = _participants.TryGetValue(owner, out var previous) ? previous.Ids : [];
            _participants[owner] = new(ids, false, reason);
            SignalChanged();
        }
    }

    /// <summary>Reserves a currently unprotected ID, or returns null. The lease must outlive archive and removal.</summary>
    /// <exception cref="ArgumentException">The session ID is blank.</exception>
    public IDisposable? TryReserveRetirement(string sessionId)
        => TryReserveRetirement(sessionId, out _);

    // The refusal tells why: "protection_unavailable" when an owner is not ready, "all_sessions_protected" while work
    // with no known session runs (a provider being prepared, a plugin task), which ends, and "session_protected" when
    // this session is open, claimed or already retired, which keeps it as the policy wants.
    internal IDisposable? TryReserveRetirement(string sessionId, out string? refusal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            refusal = !IsReady ? "protection_unavailable"
                : _participants.Values.Any(state => state.Ids.Contains(sessionId)) || _claims.ContainsKey(sessionId)
                    || _retired.Contains(sessionId) || _retiring.Contains(sessionId) ? "session_protected"
                : _unscopedClaims != 0 ? "all_sessions_protected"
                : null;
            if (refusal is not null) return null;
            _retiring.Add(sessionId);
            return new Reservation(this, sessionId);
        }
    }

    /// <summary>
    /// Waits until no work protects every session, for at most <paramref name="timeout"/>. Returns false at the timeout
    /// or at once when an owner is not ready, which waiting cannot change. It only observes: a reservation can still be
    /// refused right after.
    /// </summary>
    internal async Task<bool> WaitForAllSessionsReleasedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (!IsReady) return false;
                if (_unscopedClaims == 0) return true;
                changed = (_changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            try { await changed.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        }
    }

    // Called under the gate.
    private void SignalChanged()
    {
        var changed = _changed;
        _changed = null;
        changed?.TrySetResult();
    }

    // A loss of readiness after a claim must be observed immediately before authorizing deletion.
    internal bool IsReady
    {
        get
        {
            lock (_gate) return _required is { Count: > 0 }
                && _required.All(owner => _participants.TryGetValue(owner, out var state) && state.Ready)
                && _participants.Values.All(state => state.Ready);
        }
    }

    internal bool DeleteIfReady(string sessionId, Func<bool> delete)
    {
        lock (_gate)
        {
            if (!_retiring.Contains(sessionId) || !IsReady || !delete()) return false;
            // Remember the generation before releasing the reservation, including when later artifact/cache
            // reconciliation fails. Stale direct hub callers must never recreate a just-retired identity.
            _retired.Add(sessionId);
            return true;
        }
    }

    internal string? BlockedReason
    {
        get { lock (_gate) return _participants.Values.FirstOrDefault(state => !state.Ready && state.Reason is not null)?.Reason; }
    }

    private sealed record Participant(HashSet<string> Ids, bool Ready, string? Reason = null);
    private sealed class Admission(SessionRetentionProtection owner, string? sessionId) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._gate)
            {
                if (sessionId is null) { if (--owner._unscopedClaims == 0) owner.SignalChanged(); }
                else if (owner._claims[sessionId] == 1) owner._claims.Remove(sessionId);
                else owner._claims[sessionId]--;
            }
        }
    }
    private sealed class Reservation(SessionRetentionProtection owner, string sessionId) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._gate) owner._retiring.Remove(sessionId);
        }
    }
}
