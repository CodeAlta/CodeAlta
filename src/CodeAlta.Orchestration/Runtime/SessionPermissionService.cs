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
    internal const int OwnedExecutionLimit = 64;
    internal const int OwnedPendingLimit = 128;
    internal const int OwnedPendingPerExecutionLimit = 4;
    internal const int OwnedIdentityLimit = 128;
    internal const int OwnedCommandLimit = 4096;
    internal const int OwnedDirectoryLimit = 1024;
    internal const int OwnedReasonLimit = 1024;
    private readonly Dictionary<Guid, OwnedPermissionExecution> _ownedExecutions = [];
    private readonly HashSet<PendingPermission> _ownedDeliveries = [];
    private bool _ownedAdmissionClosed;

    // Immutable denial-policy capabilities, not per-send or mutable latest-callback associations.
    // Owned sends reject a reused coordinator whose session defaults came from another caller.
    internal AgentPermissionRequestHandler OwnedDefaultPermissionHandler { get; }
        = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny));
    internal AgentUserInputRequestHandler OwnedDefaultUserInputHandler { get; }
        = static (_, _) => Task.FromCanceled<AgentUserInputResponse>(new CancellationToken(true));

    // Only the command owner creates one of these per immutable receipt. Closed records are removed
    // from the service; retained delegates hold their closed record, never a lookup of the latest send.
    internal sealed class OwnedPermissionExecution(SessionPermissionService owner, Guid operationId, string sessionId, CancellationToken token)
    {
        internal SessionPermissionService Owner { get; } = owner;
        internal Guid OperationId { get; } = operationId;
        internal string SessionId { get; } = sessionId;
        internal CancellationToken ExecutionToken { get; set; } = token;
        internal CancellationToken AttachmentToken { get; set; }
        internal OwnedProviderEventForwarding.Attachment? Attachment { get; set; }
        internal Guid RuntimeId { get; set; }
        internal long AttachmentOrdinal { get; set; }
        internal string? ProviderId { get; set; }
        internal bool Bound { get; set; }
        internal bool Closed { get; set; }
        internal HashSet<PendingPermission> Deliveries { get; } = [];
        internal Task? Closure { get; set; }
    }

    internal ValueTask<OwnedPermissionExecution?> CreateOwnedExecutionAsync(Guid operationId, string sessionId, CancellationToken token)
        => ExecuteAsync<OwnedPermissionExecution?>(() =>
        {
            if (_stopped || _ownedAdmissionClosed || token.IsCancellationRequested || operationId == Guid.Empty
                || !ValidOwnedText(sessionId, OwnedIdentityLimit, required: true, identity: true)
                || _ownedExecutions.Count >= OwnedExecutionLimit || _ownedExecutions.ContainsKey(operationId)) return null;
            var execution = new OwnedPermissionExecution(this, operationId, sessionId, token);
            _ownedExecutions.Add(operationId, execution);
            return execution;
        }, null);

    internal ValueTask<bool> BindOwnedExecutionAsync(OwnedPermissionExecution execution, Guid runtimeId,
        OwnedProviderEventForwarding.Attachment attachment, ModelProviderId providerId)
        => ExecuteAsync(() =>
        {
            if (!Owns(execution) || execution.Bound || execution.Closed || _ownedAdmissionClosed
                || execution.ExecutionToken.IsCancellationRequested || runtimeId == Guid.Empty || attachment.IsRetiring
                || !string.Equals(attachment.Identity.SessionId, execution.SessionId, StringComparison.Ordinal)
                || !ValidOwnedText(providerId.Value, OwnedIdentityLimit, required: true, identity: true)) return false;
            execution.Bound = true;
            execution.RuntimeId = runtimeId;
            execution.AttachmentOrdinal = attachment.Ordinal;
            execution.ProviderId = providerId.Value;
            execution.Attachment = attachment;
            execution.AttachmentToken = attachment.Cancellation.Token;
            return true;
        }, false);

    internal async Task<AgentPermissionDecision> HandleOwnedCommandAsync(OwnedPermissionExecution execution,
        AgentPermissionRequest request, CancellationToken cancellationToken)
    {
        var delivery = await ExecuteAsync<Task<AgentPermissionDecision>?>(() =>
        {
            if (!CanUse(execution) || cancellationToken.IsCancellationRequested || !Eligible(execution, request)
                || _ownedDeliveries.Count >= OwnedPendingLimit || execution.Deliveries.Count >= OwnedPendingPerExecutionLimit) return null;
            var command = (AgentCommandPermissionRequest)request;
            var handle = new SessionPermissionHandle(execution.SessionId, request.RunId?.Value, request.InteractionId, Guid.NewGuid());
            var snapshot = new SessionPermissionSnapshot(handle, request.ProviderId, request.Timestamp, request.Kind,
                command.Command, command.WorkingDirectory, command.Reason, null);
            var pending = new PendingPermission(snapshot,
                new(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken, execution);
            _pending.Add(handle, pending);
            execution.Deliveries.Add(pending);
            _ownedDeliveries.Add(pending);
            // Starts an asynchronous wait, never awaits a decision in the mailbox. The bounded delivery
            // remains owned until its linked source is disposed and its cleanup message is observed.
            pending.Delivery = DeliverOwnedAsync(pending, execution.ExecutionToken, execution.AttachmentToken);
            return pending.Delivery;
        }, null).ConfigureAwait(false);
        return delivery is null ? new(AgentPermissionDecisionKind.Deny) : await delivery.ConfigureAwait(false);
    }

    // Keep retained provider delegates independent of a runtime send's compiler-generated closure
    // (which also owns captured entries, handle uses and linked cancellation sources).
    internal AgentPermissionRequestHandler CreateOwnedCommandHandler(OwnedPermissionExecution execution)
        => (request, token) => HandleOwnedCommandAsync(execution, request, token);

    private async Task<AgentPermissionDecision> DeliverOwnedAsync(PendingPermission pending,
        CancellationToken executionToken, CancellationToken attachmentToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(executionToken, attachmentToken, pending.CancellationToken);
            return await AwaitDecisionAsync(pending.Snapshot.Handle, pending.Completion.Task, linked.Token).ConfigureAwait(false);
        }
        catch
        {
            // A failed cancellation registration must not leave an untracked pending entry.
            await CancelAsync(pending.Snapshot.Handle).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await ExecuteAsync(() =>
            {
                pending.OwnedExecution!.Deliveries.Remove(pending);
                _ownedDeliveries.Remove(pending);
                return true;
            }, false).ConfigureAwait(false);
        }
    }

    internal Task CloseOwnedExecutionAsync(OwnedPermissionExecution execution)
        => JoinOwnedClosureAsync(() => ReferenceEquals(execution.Owner, this) ? CloseOwned(execution) : Task.CompletedTask);

    internal Task InvalidateOwnedOperationAsync(Guid operationId)
        => JoinOwnedClosureAsync(() => _ownedExecutions.TryGetValue(operationId, out var execution) ? CloseOwned(execution) : Task.CompletedTask);

    internal Task InvalidateOwnedAttachmentAsync(OwnedProviderEventForwarding.Attachment attachment)
        => JoinOwnedClosureAsync(() => Task.WhenAll(_ownedExecutions.Values
            .Where(execution => ReferenceEquals(execution.Attachment, attachment)).ToArray().Select(CloseOwned)));

    internal Task CloseOwnedAdmissionAsync()
        => JoinOwnedClosureAsync(() =>
        {
            _ownedAdmissionClosed = true;
            foreach (var execution in _ownedExecutions.Values.ToArray()) _ = CloseOwned(execution);
            return Task.WhenAll(_ownedDeliveries.Select(pending => pending.Delivery!));
        });

    private async Task JoinOwnedClosureAsync(Func<Task> close)
    {
        var completion = await ExecuteAsync(close, Task.CompletedTask).ConfigureAwait(false);
        await completion.ConfigureAwait(false);
        // A concurrently disposed service owns all remaining completions. Do not release a caller's
        // source/handle on ExecuteAsync's stopped fallback before that shutdown has actually joined.
        if (Volatile.Read(ref _disposeStarted) != 0) await _shutdown.Task.ConfigureAwait(false);
    }

    private Task CloseOwned(OwnedPermissionExecution execution)
    {
        if (execution.Closure is not null) return execution.Closure;
        if (!Owns(execution)) return Task.CompletedTask;
        execution.Closed = true;
        _ownedExecutions.Remove(execution.OperationId);
        var deliveries = execution.Deliveries.ToArray();
        foreach (var pending in deliveries) Complete(pending.Snapshot.Handle, AgentPermissionDecisionKind.Cancel);
        execution.Attachment = null;
        execution.ExecutionToken = default;
        execution.AttachmentToken = default;
        return execution.Closure = Task.WhenAll(deliveries.Select(pending => pending.Delivery!));
    }

    private bool Owns(OwnedPermissionExecution execution)
        => ReferenceEquals(execution.Owner, this) && _ownedExecutions.TryGetValue(execution.OperationId, out var current)
            && ReferenceEquals(current, execution);

    private bool CanUse(OwnedPermissionExecution execution)
        => !_stopped && !_ownedAdmissionClosed && Owns(execution) && execution.Bound && !execution.Closed
            && !execution.ExecutionToken.IsCancellationRequested && !execution.AttachmentToken.IsCancellationRequested
            && execution.Attachment is { IsRetiring: false };

    private bool IsCanceled(PendingPermission pending)
        => pending.CancellationToken.IsCancellationRequested || (pending.OwnedExecution is { } execution && !CanUse(execution));

    private static bool Eligible(OwnedPermissionExecution execution, AgentPermissionRequest request)
        => request is AgentCommandPermissionRequest command
            && request.Kind == "commandExecution" && request.SessionId == execution.SessionId && request.ProviderId.Value == execution.ProviderId
            && ValidOwnedText(request.InteractionId, OwnedIdentityLimit, required: true, identity: true)
            && (request.RunId is null || ValidOwnedText(request.RunId.Value.Value, OwnedIdentityLimit, required: true, identity: true))
            && ValidOwnedText(command.Command, OwnedCommandLimit, required: true)
            && ValidOwnedText(command.WorkingDirectory, OwnedDirectoryLimit, required: true)
            && ValidOwnedText(command.Reason, OwnedReasonLimit, required: false)
            && command.ApprovalId is null && command.Actions is null && command.Network is null
            && command.ProposedExecPolicyAmendment is null && command.ProposedNetworkPolicyAmendments is null;

    private static bool ValidOwnedText(string? value, int limit, bool required, bool identity = false)
    {
        if (value is null) return !required;
        if (value.Length > limit || ((required || identity) && string.IsNullOrWhiteSpace(value))) return false;
        if (identity && !value.AsSpan().Trim().SequenceEqual(value.AsSpan())) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (character == '\0' || (identity && char.IsControl(character))) return false;
            if (!char.IsSurrogate(character)) continue;
            if (!char.IsHighSurrogate(character) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }

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
                .Where(entry => !IsCanceled(entry))
                .Select(static entry => entry.Snapshot).ToArray()),
            Array.Empty<SessionPermissionSnapshot>());

    /// <summary>
    /// Lists at most four complete owned plain-command attempts for an exact session under mailbox authority.
    /// Legacy trusted registrations are never included. Cancellation or closure invalidates observations, not accepted decisions.
    /// Returns an empty window after disposal. This does not create runtime state or wait for user decisions.
    /// </summary>
    /// <exception cref="ArgumentException">The session is not a bounded, canonical UTF-16 identity.</exception>
    /// <exception cref="OperationCanceledException">The caller token is canceled at the mailbox query.</exception>
    public ValueTask<SessionOwnedPermissionPage> ListOwnedCommandsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!ValidOwnedText(sessionId, OwnedIdentityLimit, required: true, identity: true))
            throw new ArgumentException("A bounded canonical session identity is required.", nameof(sessionId));
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = _ownedDeliveries.Where(pending => pending.Snapshot.Handle.SessionId == sessionId
                && _pending.ContainsKey(pending.Snapshot.Handle) && !IsCanceled(pending)).Take(5)
                .Select(pending => new SessionOwnedPermissionSnapshot(
                    new(pending.OwnedExecution!.OperationId, pending.OwnedExecution.RuntimeId,
                        pending.OwnedExecution.AttachmentOrdinal, pending.Snapshot.Handle), pending.Snapshot)).ToArray();
            return new SessionOwnedPermissionPage(Array.AsReadOnly(entries.Take(4).ToArray()), entries.Length > 4);
        }, new SessionOwnedPermissionPage(Array.Empty<SessionOwnedPermissionSnapshot>(), false));
    }

    /// <summary>
    /// Resolves only the exact still-live owned execution/attachment/attempt in the permission mailbox.
    /// Wrong identities, replay, closure and unsupported decisions return false, including legacy TUI handles.
    /// Caller cancellation observed before the mailbox decision throws without resolving; cancellation after acceptance
    /// cannot revoke the decision. An uncertain transport response must not be treated as an uncommitted decision.
    /// </summary>
    /// <exception cref="ArgumentNullException">The handle or its attempt is null.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation is observed before resolution.</exception>
    public ValueTask<bool> ResolveOwnedCommandAsync(SessionOwnedPermissionHandle handle,
        AgentPermissionDecisionKind decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(handle.Attempt);
        cancellationToken.ThrowIfCancellationRequested();
        if (decision is not (AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.Deny or AgentPermissionDecisionKind.Cancel))
            return ValueTask.FromResult(false);
        return ExecuteAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pending.TryGetValue(handle.Attempt, out var pending) || pending.OwnedExecution is not { } execution
                || execution.OperationId != handle.OperationId || execution.RuntimeId != handle.RuntimeInstanceId
                || execution.AttachmentOrdinal != handle.AttachmentGeneration || !CanUse(execution) || IsCanceled(pending)) return false;
            return Complete(handle.Attempt, decision);
        }, false);
    }

    /// <summary>
    /// Checks the entire scoped handle and caller cancellation in the mailbox; canceled, stale or wrong identities
    /// return false even before cancellation cleanup. This observation does not reserve a later resolution.
    /// </summary>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    public ValueTask<bool> IsPendingAsync(SessionPermissionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return ExecuteAsync(() => _pending.TryGetValue(handle, out var entry) && !IsCanceled(entry), false);
    }

    /// <summary>
    /// Resolves the exact attempt. Invalid decision values, wrong identities and stale/replayed responses return false.
    /// Resolution checks the caller token in the mailbox: cancellation already signaled at that check completes Cancel
    /// and rejects a non-Cancel response regardless of cleanup-message ordering. Cancellation after that check does not
    /// revoke an accepted decision. The mailbox serializes competing responses and entry removal.
    /// Preserves the four existing TUI choices. Opt-in owned command attempts allow only AllowOnce, Deny and Cancel;
    /// this trusted route cannot grant AllowForSession on them. Provider policy amendments remain provider-owned.
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
            var ownedCompletion = await _actor.AskAsync(actorCancellationToken =>
            {
                _stopped = true;
                _ownedAdmissionClosed = true;
                foreach (var execution in _ownedExecutions.Values.ToArray()) _ = CloseOwned(execution);
                var owned = Task.WhenAll(_ownedDeliveries.Select(pending => pending.Delivery!));
                foreach (var entry in _pending.Values)
                {
                    entry.Completion.SetResult(new(AgentPermissionDecisionKind.Cancel));
                }

                _pending.Clear();
                return ValueTask.FromResult(owned);
            }).ConfigureAwait(false);
            try { await ownedCompletion.ConfigureAwait(false); }
            finally
            {
                try
                {
                    // Delivery cleanup uses the stopped fallback during disposal; release its bounded indexes here.
                    await _actor.AskAsync(_ =>
                    {
                        foreach (var pending in _ownedDeliveries) pending.OwnedExecution!.Deliveries.Remove(pending);
                        _ownedDeliveries.Clear();
                        return ValueTask.FromResult(true);
                    }).ConfigureAwait(false);
                }
                finally { await _actor.StopAsync().ConfigureAwait(false); }
            }
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
        if (!_pending.TryGetValue(handle, out var entry))
        {
            return false;
        }

        var canceled = IsCanceled(entry);
        if (!canceled && entry.OwnedExecution is not null
            && decision is not (AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.Deny or AgentPermissionDecisionKind.Cancel)) return false;
        _pending.Remove(handle);
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

    internal sealed class PendingPermission(
        SessionPermissionSnapshot snapshot, TaskCompletionSource<AgentPermissionDecision> completion, CancellationToken cancellationToken,
        OwnedPermissionExecution? ownedExecution = null)
    {
        internal SessionPermissionSnapshot Snapshot { get; } = snapshot;
        internal TaskCompletionSource<AgentPermissionDecision> Completion { get; } = completion;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal OwnedPermissionExecution? OwnedExecution { get; } = ownedExecution;
        internal Task<AgentPermissionDecision>? Delivery { get; set; }
    }
}
