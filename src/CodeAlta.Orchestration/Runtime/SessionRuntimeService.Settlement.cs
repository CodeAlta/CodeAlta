using System.Collections.Concurrent;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

// When a session and the sessions it started have nothing left to do, and how to stop them all: what a headless run
// waits for before it reports its result, and what its timeout or a signal ends.
public sealed partial class SessionRuntimeService
{
    /// <summary>The name of the detail of an update event that records why a run was aborted (<see cref="AbortTreeAsync"/>).</summary>
    public const string AbortReasonDetail = "abortReason";

    // How long a wait for the sessions to settle goes without a signal before it looks at them again. The runtime signals
    // every change it makes (an event published, a claim released, a command or a fallback that ends, forwarded work); this
    // bound only limits what an edge missed by a provider or a plugin would cost, it decides nothing.
    private static readonly TimeSpan SettlementRecheck = TimeSpan.FromSeconds(1);

    private readonly SettlementSignal _settlement = new();

    // The sessions an abort of their tree holds: they start no run from their queue (a parent told of its aborted child,
    // the end of a job), until a prompt is sent to them again. The value is the reason of the abort.
    private readonly ConcurrentDictionary<string, string> _abortHolds = new(StringComparer.OrdinalIgnoreCase);

    // The order of the aborts: each abort of a tree takes the next number, and each session of the tree keeps the number
    // of the last abort that reached it. A send takes the current number when it is admitted, and does not start its run
    // when an abort of its session, or of a session it descends from, came after. Guarded by _abortGate, with the setting
    // of the holds an abort makes and the release of the hold by a send, so that a send and an abort see each other.
    private readonly object _abortGate = new();
    private readonly Dictionary<string, long> _abortSequences = new(StringComparer.OrdinalIgnoreCase);
    private long _abortSequence;

    // Whether the owner of the commands has work under way for a session, a fallback included; set by the owner.
    internal Func<string, bool>? OwnedWork { get; set; }

    // Cancels the send the owner of the commands admitted for a session, if any: one that has not started its run does
    // not start it, and one that has is aborted. The task completes when the send has ended; null when there is none.
    // Set by the owner.
    internal Func<string, Task?>? CancelOwnedSend { get; set; }

    // Tells what waits for sessions to settle that something changed: it looks at them again.
    internal void PulseSettlement() => _settlement.Pulse();

    /// <summary>
    /// Lists a session and the sessions it started that this runtime holds, transitively: the sessions whose parent, or
    /// whose creator, is one of them (the sessions <c>alta session create</c> created from it).
    /// </summary>
    /// <param name="rootSessionId">The session at the root.</param>
    /// <returns>The root first, then its descendants.</returns>
    /// <exception cref="ArgumentException"><paramref name="rootSessionId"/> is blank.</exception>
    public IReadOnlyList<string> GetSessionTree(string rootSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        var tree = new List<string> { rootSessionId };
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootSessionId };
        var entries = _entries.Values.ToArray();
        for (var index = 0; index < tree.Count; index++)
        {
            foreach (var entry in entries)
            {
                if (known.Contains(entry.SessionId)) continue;
                if (string.Equals(entry.ParentSessionId, tree[index], StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.CreatedBy?.SourceSessionId, tree[index], StringComparison.OrdinalIgnoreCase))
                {
                    known.Add(entry.SessionId);
                    tree.Add(entry.SessionId);
                }
            }
        }

        return tree;
    }

    /// <summary>
    /// Lists a session and every session below it: those this runtime holds (<see cref="GetSessionTree"/>), then those the
    /// stored sessions name as its descendants (<see cref="SessionLineage.Descendants"/>), which the runtime no longer holds
    /// once they ended and were detached.
    /// </summary>
    /// <param name="rootSessionId">The session at the root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The root first, then its descendants, each once.</returns>
    /// <exception cref="ArgumentException"><paramref name="rootSessionId"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The listing was canceled.</exception>
    public async Task<IReadOnlyList<string>> ListSessionTreeAsync(string rootSessionId, CancellationToken cancellationToken = default)
    {
        var tree = GetSessionTree(rootSessionId).ToList();
        var stored = await AdmitAsync(() => ListSessionRowsAsync(_sessionViewCatalog.JournalStore.CreateSessionStore()), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var known = new HashSet<string>(tree, StringComparer.OrdinalIgnoreCase);
        foreach (var descendant in SessionLineage.Descendants(rootSessionId, stored))
        {
            if (known.Add(descendant.Session.SessionId)) tree.Add(descendant.Session.SessionId);
        }

        return tree;
    }

    /// <summary>
    /// Waits until a session and every session it started have settled: no run, no queue drain and no prompt waiting in
    /// their queues, no background task of their provider and no job that runs or whose end is still being told, no
    /// parent notification or host prompt being delivered, no fallback that continues a failed run, no command of the
    /// owner under way, and no provider callback still being forwarded. A run that ends starts more work (the answer of a
    /// child is told to its parent, which runs again; a queued prompt is drained), so the first answer is not the end.
    /// </summary>
    /// <param name="rootSessionId">The session at the root.</param>
    /// <param name="cancellationToken">Stops waiting; the sessions go on.</param>
    /// <returns>A task that completes when the tree has been found settled twice in a row.</returns>
    /// <remarks>
    /// The wait follows the signals of the runtime (events, released claims, ended commands and forwarded work) and looks
    /// at each session on its actor. A queue that an abort holds (<see cref="AbortTreeAsync"/>) does not keep a session
    /// at work. A session the tree starts while the wait goes on is waited for too.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="rootSessionId"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    public async Task WhenSettledAsync(string rootSessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        var confirmed = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Taken before the look, so that a change during it is not missed.
            var changed = _settlement.Next;
            var forwarded = _forwarding.Changed;
            var (busy, wake) = await ProbeTreeAsync(rootSessionId, cancellationToken).ConfigureAwait(false);
            if (!busy)
            {
                // Settled twice in a row, the second look starting after the continuations of what ended last ran. The
                // signals are not asked to be quiet: they tell the changes of every session, and others may be at work.
                if (confirmed) return;
                confirmed = true;
                await Task.Yield();
                continue;
            }

            confirmed = false;
            Task[] waits = wake is null ? [changed, forwarded] : [changed, forwarded, wake];
            await Task.WhenAny(Task.WhenAny(waits), Task.Delay(SettlementRecheck, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Aborts a session and every session it started (<see cref="GetSessionTree"/>): their runs are stopped, the prompts
    /// admitted before the abort that have not started their run never start it, their jobs are
    /// cancelled, and their queues hold what they receive afterwards (the failures of their children, the ends of their
    /// jobs) until a prompt is sent to them again, so that nothing starts after the abort. Each session of the tree that was
    /// at work, and the root, records the abort and its reason in its journal, once its run has stopped.
    /// </summary>
    /// <param name="rootSessionId">The session at the root.</param>
    /// <param name="reason">Why the run is aborted, a short word such as <c>timeout</c> or <c>signal</c>.</param>
    /// <param name="cancellationToken">Stops waiting for the runs to stop; what was started goes on, and the records are not written.</param>
    /// <returns>The sessions of the tree.</returns>
    /// <exception cref="ArgumentException"><paramref name="rootSessionId"/> or <paramref name="reason"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    public async Task<IReadOnlyList<string>> AbortTreeAsync(string rootSessionId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        reason = reason.Trim();
        var tree = GetSessionTree(rootSessionId);
        lock (_abortGate)
        {
            var sequence = ++_abortSequence;
            foreach (var sessionId in tree)
            {
                _abortHolds[sessionId] = reason;
                _abortSequences[sessionId] = sequence;
            }
        }

        // The record of an abort belongs to the run it stops, when the session has one. The last run seen to start is kept
        // too: a run that started before the abort, whose first events had not reached the actor yet, is found once stopped.
        var atWork = new List<(string SessionId, AgentRunId? RunId, AgentRunId? LastRunId)>();
        foreach (var sessionId in tree)
        {
            if (string.Equals(sessionId, rootSessionId, StringComparison.OrdinalIgnoreCase)
                || (await ProbeSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)).Busy)
            {
                var entry = _entries.TryGetValue(sessionId, out var found) ? found : null;
                atWork.Add((sessionId, entry?.ActiveRunId, entry?.LastRunId));
            }
        }

        // Every stop is started before any is waited for: a parent that waits for its child is stopped too. A prompt admitted
        // before the abort whose run has not started yet is cancelled: it does not start after the abort.
        var stops = new List<Task>();
        foreach (var sessionId in tree)
        {
            stops.Add(CancelOwnedSendAsync(sessionId, reason));
            stops.Add(AbortQuietlyAsync(sessionId));
            foreach (var job in Jobs.List(sessionId).Where(static job => job.State == CodeAlta.Orchestration.Jobs.SessionJobState.Running))
                stops.Add(Jobs.CancelAsync(job.Id, byUser: false, CancellationToken.None));
            if (ScriptRuns is { } scripts) stops.Add(scripts.StopAsync(sessionId));
        }

        await Task.WhenAll(stops).WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (sessionId, runId, lastRunId) in atWork)
        {
            await WhenRunEndedAsync(sessionId, cancellationToken).ConfigureAwait(false);
            await RecordAbortAsync(sessionId, reason, runId ?? RunStartedSince(sessionId, lastRunId)).ConfigureAwait(false);
        }

        PulseSettlement();
        return tree;
    }

    /// <summary>Gets the reason of the abort that holds a session (<see cref="AbortTreeAsync"/>), until a prompt is sent to it again.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The reason, or <see langword="null"/> when no abort holds the session.</returns>
    public string? GetAbortReason(string sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && _abortHolds.TryGetValue(sessionId, out var reason) ? reason : null;

    // The number of the last abort, which a send takes when it is admitted (ClaimRunAfterAborts).
    private long CaptureAbortSequence()
    {
        lock (_abortGate) return _abortSequence;
    }

    // Whether a send admitted at an abort number may start its run: not when an abort of its session, or of a session it
    // descends from, came after its admission. One that may lifts the hold of an abort: the queue of its session drains
    // again after that run. A send to a child not attached yet when its tree was aborted is found through its parent.
    private bool ClaimRunAfterAborts(SessionViewDescriptor session, long admittedSequence)
    {
        lock (_abortGate)
        {
            if (_abortSequence != admittedSequence && AbortedSince(session, admittedSequence)) return false;
            _abortHolds.TryRemove(session.SessionId, out _);
            return true;
        }
    }

    private bool AbortedSince(SessionViewDescriptor session, long admittedSequence)
    {
        var lineage = new Stack<string?>([session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (lineage.TryPop(out var sessionId))
        {
            if (string.IsNullOrWhiteSpace(sessionId) || !seen.Add(sessionId)) continue;
            if (_abortSequences.TryGetValue(sessionId, out var aborted) && aborted > admittedSequence) return true;
            if (!_entries.TryGetValue(sessionId, out var entry)) continue;
            lineage.Push(entry.ParentSessionId);
            lineage.Push(entry.CreatedBy?.SourceSessionId);
        }

        return false;
    }

    private async Task CancelOwnedSendAsync(string sessionId, string reason)
    {
        if (CancelOwnedSend?.Invoke(sessionId) is not { } cancelled) return;
        await cancelled.ConfigureAwait(false);
        // A send admitted before the abort may have reached the runtime before it was cancelled, and lifted the hold.
        _abortHolds[sessionId] = reason;
    }

    private async Task AbortQuietlyAsync(string sessionId)
    {
        try
        {
            await AbortAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or OperationCanceledException or NotSupportedException)
        {
            // A session without a live attachment has nothing to stop.
        }
    }

    // Waits for the run of a session to end after it was stopped, as the settlement does for one session.
    private async Task WhenRunEndedAsync(string sessionId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var changed = _settlement.Next;
            var forwarded = _forwarding.Changed;
            if (!await HasActiveRunOrWorkAsync(sessionId, cancellationToken).ConfigureAwait(false)) return;
            await Task.WhenAny(Task.WhenAny(changed, forwarded), Task.Delay(SettlementRecheck, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<bool> HasActiveRunOrWorkAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_sessionActors.TryGet(sessionId, out var actor)) return false;
        var running = await actor.QueryAsync(_ => ValueTask.FromResult(
            _transitions.ContainsKey(sessionId)
            || _entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated && (entry.HasActiveRun || entry.QueueDrainInProgress)), cancellationToken).ConfigureAwait(false);
        return running || _forwarding.HasSessionWork(sessionId);
    }

    // The record of an abort in the journal of a session, which its history shows and the outcome of the run reads.
    private async Task RecordAbortAsync(string sessionId, string reason, AgentRunId? runId)
    {
        try
        {
            var session = await ResolveOwnedSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (session is null) return;
            using var details = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { [AbortReasonDetail] = reason }));
            await AppendSessionEventAsync(session, new AgentSessionUpdateEvent(new ModelProviderId(session.ResolvedProviderKey), sessionId, DateTimeOffset.UtcNow,
                runId, AgentSessionUpdateKind.Warning, $"The run was aborted: {reason}.", details.RootElement.Clone()), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException or UnauthorizedAccessException)
        {
            // A session that is being removed, or a runtime that closes, keeps no record.
        }
    }

    // The run a session started since the abort found it, its start not yet seen then: the actor has seen it by the end of the run.
    private AgentRunId? RunStartedSince(string sessionId, AgentRunId? lastRunIdAtAbort)
        => _entries.TryGetValue(sessionId, out var entry) && entry.LastRunId is { } last && last != lastRunIdAtAbort ? last : null;

    private async Task<(bool Busy, Task? Wake)> ProbeTreeAsync(string rootSessionId, CancellationToken cancellationToken)
    {
        List<Task>? wakes = null;
        var busy = false;
        foreach (var sessionId in GetSessionTree(rootSessionId))
        {
            var (sessionBusy, wake) = await ProbeSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            busy |= sessionBusy;
            if (wake is not null) (wakes ??= []).Add(wake);
        }

        return (busy, wakes is null ? null : Task.WhenAny(wakes));
    }

    // Whether one session has something left to do; a task that completes when part of it ends, for the work no signal of
    // the runtime tells the end of (a job).
    private async Task<(bool Busy, Task? Wake)> ProbeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (Jobs.HasUnsettled(sessionId)) return (true, Jobs.WhenSessionSettledAsync(sessionId));
        lock (_identityGate)
        {
            if (_retentionWork.ContainsKey(sessionId)) return (true, null);
        }

        if (_forwarding.HasSessionWork(sessionId) || OwnedWork?.Invoke(sessionId) == true) return (true, null);
        if (!_sessionActors.TryGet(sessionId, out var actor)) return (false, null);
        var held = _abortHolds.ContainsKey(sessionId);
        var busy = await actor.QueryAsync(async token =>
        {
            if (_transitions.ContainsKey(sessionId)) return true;
            if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated) return false;
            if (entry.HasActiveRun || entry.QueueDrainInProgress
                || entry.BackgroundTasks.Any(static task => task.Outcome is null)) return true;
            // What waits in the queue runs next, unless an abort holds it.
            return !held && (await ReadLatestLocalStateAsync(sessionId, entry.CreatedAt, token).ConfigureAwait(false))?.HasPendingQueuedPrompts() == true;
        }, cancellationToken).ConfigureAwait(false);
        return (busy, null);
    }

    // A signal that completes the task of its waiters at each pulse. A pulse without a waiter costs no allocation.
    private sealed class SettlementSignal
    {
        private TaskCompletionSource? _next;

        internal Task Next
        {
            get
            {
                var current = Volatile.Read(ref _next);
                if (current is not null) return current.Task;
                var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return (Interlocked.CompareExchange(ref _next, created, null) ?? created).Task;
            }
        }

        internal void Pulse() => Interlocked.Exchange(ref _next, null)?.TrySetResult();
    }
}
