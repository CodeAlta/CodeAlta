using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime.Actors;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Identifies one permission attempt in a trusted application callback association.</summary>
/// <param name="SessionId">Captured session identity, not a renderer authorization grant.</param>
/// <param name="RunId">Provider run identity, or null when the provider supplies none.</param>
/// <param name="InteractionId">Provider interaction identity.</param>
/// <param name="AttemptId">Fresh application identity; reused provider IDs never reuse this value.</param>
public sealed record SessionPermissionHandle(string SessionId, string? RunId, string InteractionId, Guid AttemptId);

/// <summary>
/// Immutable pending summary, independent of open tabs and lossy timeline streams.
/// Only scalar preview data is retained; raw payloads and mutable provider collections are not retained.
/// This trusted application contract is neither renderer authorization nor durable restart recovery.
/// </summary>
/// <param name="Handle">Attempt identity.</param>
/// <param name="ProviderId">Provider identity.</param>
/// <param name="Timestamp">Request timestamp.</param>
/// <param name="Kind">Request kind.</param>
/// <param name="Command">Optional command preview.</param>
/// <param name="WorkingDirectory">Optional command directory.</param>
/// <param name="Reason">Optional reason.</param>
/// <param name="GrantRoot">Optional file-change grant root.</param>
public sealed record SessionPermissionSnapshot(
    SessionPermissionHandle Handle, ModelProviderId ProviderId, DateTimeOffset Timestamp, string Kind,
    string? Command, string? WorkingDirectory, string? Reason, string? GrantRoot);

/// <summary>A permission registration with a read-only completion; no frontend owns its completion source.</summary>
public sealed class SessionPermissionRegistration
{
    private readonly Task<AgentPermissionDecision> _decision;
    private readonly CancellationToken _cancellationToken;

    internal SessionPermissionRegistration(SessionPermissionSnapshot snapshot, Task<AgentPermissionDecision> completion,
        Task<AgentPermissionDecision> decision, CancellationToken cancellationToken)
    {
        Snapshot = snapshot;
        Completion = completion;
        _decision = decision;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Gets the immutable request summary and attempt identity.</summary>
    public SessionPermissionSnapshot Snapshot { get; }

    /// <summary>Gets the exactly-once decision, including Cancel on caller cancellation or owner shutdown.</summary>
    public Task<AgentPermissionDecision> Completion { get; }

    /// <summary>Gets a presentation guard; resolution still validates the full handle under the owner.</summary>
    public bool IsPending => !_decision.IsCompleted && !_cancellationToken.IsCancellationRequested;
}

/// <summary>
/// Application-owned permission policy and pending completions. The mailbox serializes register/list/resolve/cancel,
/// never user waits. Completed attempts are removed, not retained as tombstones. Provider runtime choice enforcement
/// remains authoritative. Disposing presentation alone must not cancel this service or its requests.
/// </summary>
public sealed class SessionPermissionService : IAsyncDisposable
{
    private readonly OrchestrationMailboxActor _actor = new(128);
    private readonly Dictionary<SessionPermissionHandle, PendingPermission> _pending = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopped;
    private int _disposeStarted;

    /// <summary>
    /// Registers a trusted callback association. AutoApprove retains the existing AllowOnce policy.
    /// Cancellation (including before registration) and a disposed owner return Cancel, never approval.
    /// An explicit provider session ID is selected by the application adapter before calling this method.
    /// </summary>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">The session or interaction identity is blank.</exception>
    public async Task<SessionPermissionRegistration> RegisterAsync(
        string sessionId, AgentPermissionRequest request, bool autoApprove, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InteractionId);
        var handle = new SessionPermissionHandle(sessionId, request.RunId?.Value, request.InteractionId, Guid.NewGuid());
        var command = request as AgentCommandPermissionRequest;
        var file = request as AgentFileChangePermissionRequest;
        var snapshot = new SessionPermissionSnapshot(handle, request.ProviderId, request.Timestamp, request.Kind,
            command?.Command, command?.WorkingDirectory, command?.Reason ?? file?.Reason, file?.GrantRoot);
        var completion = new TaskCompletionSource<AgentPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = await ExecuteAsync(() =>
        {
            if (_stopped || cancellationToken.IsCancellationRequested)
            {
                completion.SetResult(new(AgentPermissionDecisionKind.Cancel));
            }
            else if (autoApprove)
            {
                completion.SetResult(new(AgentPermissionDecisionKind.AllowOnce));
            }
            else
            {
                _pending.Add(handle, new(snapshot, completion, cancellationToken));
            }

            return true;
        }, false).ConfigureAwait(false);
        if (!registered)
        {
            completion.TrySetResult(new(AgentPermissionDecisionKind.Cancel));
        }

        // This wait is outside the mailbox. Cancellation cleanup is part of the returned, observed task.
        return new(snapshot, AwaitDecisionAsync(handle, completion.Task, cancellationToken), completion.Task, cancellationToken);
    }

    /// <summary>
    /// Lists independent, immutable summaries; excludes caller-canceled attempts even before cleanup runs.
    /// Each entry's cancellation check occurs in the mailbox; later cancellation can invalidate an already returned summary.
    /// Returns an empty list after disposal.
    /// </summary>
    public ValueTask<IReadOnlyList<SessionPermissionSnapshot>> ListAsync()
        => ExecuteAsync<IReadOnlyList<SessionPermissionSnapshot>>(
            () => Array.AsReadOnly(_pending.Values
                .Where(static entry => !entry.CancellationToken.IsCancellationRequested)
                .Select(static entry => entry.Snapshot).ToArray()),
            Array.Empty<SessionPermissionSnapshot>());

    /// <summary>
    /// Checks the entire scoped handle and caller cancellation in the mailbox; canceled, stale or wrong identities
    /// return false even before cancellation cleanup. This observation does not reserve a later resolution.
    /// </summary>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    public ValueTask<bool> IsPendingAsync(SessionPermissionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return ExecuteAsync(() => _pending.TryGetValue(handle, out var entry) && !entry.CancellationToken.IsCancellationRequested, false);
    }

    /// <summary>
    /// Resolves the exact attempt. Invalid decision values, wrong identities and stale/replayed responses return false.
    /// Resolution checks the caller token in the mailbox: cancellation already signaled at that check completes Cancel
    /// and rejects a non-Cancel response regardless of cleanup-message ordering. Cancellation after that check does not
    /// revoke an accepted decision. The mailbox serializes competing responses and entry removal.
    /// This narrow use case preserves the four existing TUI choices; provider policy amendments remain provider-owned.
    /// </summary>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    public ValueTask<bool> ResolveAsync(SessionPermissionHandle handle, AgentPermissionDecisionKind decision)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!Enum.IsDefined(decision))
        {
            return ValueTask.FromResult(false);
        }

        return ExecuteAsync(() => Complete(handle, decision), false);
    }

    /// <summary>Cancels exactly one pending attempt; returns false for stale or mismatched handles.</summary>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    public ValueTask<bool> CancelAsync(SessionPermissionHandle handle)
        => ResolveAsync(handle, AgentPermissionDecisionKind.Cancel);

    /// <summary>Cancels current pending attempts for exactly the given session/run, including an explicit null run.</summary>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    public ValueTask<int> CancelRunAsync(string sessionId, string? runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return ExecuteAsync(() =>
        {
            var handles = _pending.Keys.Where(handle => handle.SessionId == sessionId && handle.RunId == runId).ToArray();
            foreach (var handle in handles)
            {
                Complete(handle, AgentPermissionDecisionKind.Cancel);
            }

            return handles.Length;
        }, 0);
    }

    /// <summary>Cancels all pending waits before stopping the mailbox. Concurrent disposals join the same shutdown.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _shutdown.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await _actor.AskAsync(_ =>
            {
                _stopped = true;
                foreach (var entry in _pending.Values)
                {
                    entry.Completion.SetResult(new(AgentPermissionDecisionKind.Cancel));
                }

                _pending.Clear();
                return ValueTask.FromResult(true);
            }).ConfigureAwait(false);
            await _actor.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _shutdown.TrySetResult();
        }
    }

    private async Task<AgentPermissionDecision> AwaitDecisionAsync(
        SessionPermissionHandle handle, Task<AgentPermissionDecision> completion, CancellationToken cancellationToken)
    {
        try
        {
            return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelAsync(handle).ConfigureAwait(false);
            return await completion.ConfigureAwait(false);
        }
    }

    private bool Complete(SessionPermissionHandle handle, AgentPermissionDecisionKind decision)
    {
        if (!_pending.Remove(handle, out var entry))
        {
            return false;
        }

        var canceled = entry.CancellationToken.IsCancellationRequested;
        entry.Completion.SetResult(new(canceled ? AgentPermissionDecisionKind.Cancel : decision));
        return !canceled || decision == AgentPermissionDecisionKind.Cancel;
    }

    private async ValueTask<T> ExecuteAsync<T>(Func<T> command, T stoppedResult)
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            return stoppedResult;
        }

        try
        {
            return await _actor.AskAsync(_ => ValueTask.FromResult(command())).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposeStarted) != 0)
        {
            return stoppedResult;
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _disposeStarted) != 0)
        {
            return stoppedResult;
        }
    }

    private sealed record PendingPermission(
        SessionPermissionSnapshot Snapshot, TaskCompletionSource<AgentPermissionDecision> Completion, CancellationToken CancellationToken);
}
