using System.Threading.Channels;
using System.Runtime.ExceptionServices;
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
public sealed partial class SessionPermissionService : IAsyncDisposable
{
    private readonly bool _autoApproveOwnedPermissions;

    /// <summary>Creates a permission owner with deny-by-default owned callbacks.</summary>
    public SessionPermissionService() : this(false) { }

    /// <summary>Creates a permission owner with an explicit host-owned automatic approval policy.</summary>
    /// <remarks>Automatic approval grants AllowOnce, like the TUI, and is not a filesystem sandbox.
    /// Explicit per-operation command review still takes precedence.</remarks>
    public SessionPermissionService(bool autoApproveOwnedPermissions)
    {
        _autoApproveOwnedPermissions = autoApproveOwnedPermissions;
        OwnedDefaultPermissionHandler = (_, token) => Task.FromResult(new AgentPermissionDecision(
            token.IsCancellationRequested || Volatile.Read(ref _disposeStarted) != 0 ? AgentPermissionDecisionKind.Cancel
            : autoApproveOwnedPermissions ? AgentPermissionDecisionKind.AllowOnce : AgentPermissionDecisionKind.Deny));
    }
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

    // Immutable host-policy capabilities, not per-send or mutable latest-callback associations.
    // Owned sends reject a reused coordinator whose session defaults came from another caller.

    /// <summary>
    /// Gets the host's default permission decision for sessions it owns: allow once when the host approves
    /// automatically, otherwise deny. A session created with it can later be driven by the owner's commands.
    /// </summary>
    public AgentPermissionRequestHandler OwnedDefaultPermissionHandler { get; }

    /// <summary>Gets the host's default user-input answer for sessions it owns: the request is canceled.</summary>
    public AgentUserInputRequestHandler OwnedDefaultUserInputHandler { get; }
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
        internal AgentRunId? RunId { get; set; }
        internal CancellationToken RunToken { get; set; }
        internal bool RunBound { get; set; }
        internal OwnedProviderEventForwarding.Attachment? Attachment { get; set; }
        internal Guid RuntimeId { get; set; }
        internal long AttachmentOrdinal { get; set; }
        internal string? ProviderId { get; set; }
        internal bool Bound { get; set; }
        internal bool Closed { get; set; }
        internal bool ReviewCommands { get; init; }
        internal bool EnableUserInput { get; init; }
        internal HashSet<PendingUserInput> InputDeliveries { get; } = [];
        internal HashSet<PendingPermission> Deliveries { get; } = [];
        internal Task? Closure { get; set; }
    }

    internal ValueTask<OwnedPermissionExecution?> CreateOwnedExecutionAsync(Guid operationId, string sessionId, CancellationToken token)
        => CreateOwnedExecutionAsync(operationId, sessionId, token, true, false);

    internal ValueTask<OwnedPermissionExecution?> CreateOwnedExecutionAsync(Guid operationId, string sessionId,
        CancellationToken token, bool reviewCommands, bool enableUserInput)
        => ExecuteAsync<OwnedPermissionExecution?>(() =>
        {
            if (_stopped || _ownedAdmissionClosed || token.IsCancellationRequested || operationId == Guid.Empty
                || !ValidOwnedText(sessionId, OwnedIdentityLimit, required: true, identity: true)
                || _ownedExecutions.Count >= OwnedExecutionLimit || _ownedExecutions.ContainsKey(operationId)) return null;
            var execution = new OwnedPermissionExecution(this, operationId, sessionId, token)
            { ReviewCommands = reviewCommands, EnableUserInput = enableUserInput };
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

    // Only the provider's awaited Started hook supplies this identity/token. Legacy providers that
    // ignore the hook retain their prior unbound behavior; observations never manufacture a binding.
    internal ValueTask<bool> BindOwnedRunAsync(OwnedPermissionExecution execution, AgentRunId runId, CancellationToken runToken)
        => ExecuteAsync(() =>
        {
            if (!CanUse(execution) || execution.RunBound || execution.Deliveries.Count != 0 || execution.InputDeliveries.Count != 0
                || !ValidOwnedText(runId.Value, OwnedIdentityLimit, required: true, identity: true)
                || !runToken.CanBeCanceled || runToken.IsCancellationRequested) return false;
            execution.RunId = runId;
            execution.RunToken = runToken;
            execution.RunBound = true;
            return true;
        }, false);

    internal AgentRunLifecycle CreateOwnedRunLifecycle(OwnedPermissionExecution execution) => new OwnedRunLifecycle(this, execution);

    private sealed class OwnedRunLifecycle(SessionPermissionService owner, OwnedPermissionExecution execution) : AgentRunLifecycle
    {
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            if (await owner.BindOwnedRunAsync(execution, runId, executionToken).ConfigureAwait(false)) return;
            executionToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The owned execution cannot bind this provider run.");
        }

        // Captured execution object, never lookup by a supplied run/session or attachment-wide closure.
        public override Task ClosingAsync(AgentRunId runId) => owner.CloseOwnedExecutionAsync(execution);
    }

    internal async Task<AgentPermissionDecision> HandleOwnedCommandAsync(OwnedPermissionExecution execution,
        AgentPermissionRequest request, CancellationToken cancellationToken)
    {
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = await ExecuteAsync<Task<AgentPermissionDecision>?>(() =>
        {
            if (_autoApproveOwnedPermissions && !execution.ReviewCommands && CanUse(execution) && !cancellationToken.IsCancellationRequested
                && request.SessionId == execution.SessionId && request.ProviderId.Value == execution.ProviderId
                && (!execution.RunBound || request.RunId is null || request.RunId == execution.RunId))
                return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));
            if (!execution.ReviewCommands || !CanUse(execution) || cancellationToken.IsCancellationRequested || !Eligible(execution, request)
                || !HasOwnedDeliveryCapacity(execution)) return null;
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
            pending.Delivery = DeliverOwnedAsync(pending, execution.ExecutionToken, execution.AttachmentToken, execution.RunToken, launch.Task);
            return pending.Delivery;
        }, null).ConfigureAwait(false);
        launch.TrySetResult();
        return delivery is null ? new(AgentPermissionDecisionKind.Deny) : await delivery.ConfigureAwait(false);
    }

    // Keep retained provider delegates independent of a runtime send's compiler-generated closure
    // (which also owns captured entries, handle uses and linked cancellation sources).
    internal AgentPermissionRequestHandler CreateOwnedCommandHandler(OwnedPermissionExecution execution)
        => (request, token) => HandleOwnedCommandAsync(execution, request, token);

    private async Task<AgentPermissionDecision> DeliverOwnedAsync(PendingPermission pending,
        CancellationToken executionToken, CancellationToken attachmentToken, CancellationToken runToken, Task launch)
    {
        await launch.ConfigureAwait(false);
        pending.Lifetime = new OwnedDeliveryLifetime(pending);
        return await pending.Lifetime.RunAsync(
            token => AwaitDecisionAsync(pending.Snapshot.Handle, pending.Completion.Task, token),
            async () => { await _actor.AskAsync(_ => ValueTask.FromResult(Complete(pending.Snapshot.Handle, AgentPermissionDecisionKind.Cancel))).ConfigureAwait(false); },
            async () =>
            {
                await _actor.AskAsync(_ =>
                {
                    pending.OwnedExecution!.Deliveries.Remove(pending);
                    _ownedDeliveries.Remove(pending);
                    return ValueTask.FromResult(true);
                }).ConfigureAwait(false);
            }, [executionToken, attachmentToken, runToken, pending.CancellationToken]).ConfigureAwait(false);
    }

    internal Task CloseOwnedExecutionAsync(OwnedPermissionExecution execution)
        => JoinOwnedClosureAsync(() => ReferenceEquals(execution.Owner, this) ? CloseOwned(execution) : Task.CompletedTask);

    internal Task InvalidateOwnedOperationAsync(Guid operationId)
        => JoinOwnedClosureAsync(() => _ownedExecutions.TryGetValue(operationId, out var execution) ? CloseOwned(execution) : Task.CompletedTask);

    internal Task InvalidateOwnedAttachmentAsync(OwnedProviderEventForwarding.Attachment attachment)
        => JoinOwnedClosureAsync(() => JoinDeliveryOriginalsAsync(_ownedExecutions.Values
            .Where(execution => ReferenceEquals(execution.Attachment, attachment)).ToArray().Select(CloseOwned)));

    internal Task CloseOwnedAdmissionAsync()
        => JoinOwnedClosureAsync(() =>
        {
            _ownedAdmissionClosed = true;
            return JoinDeliveryOriginalsAsync(_ownedExecutions.Values.ToArray().Select(CloseOwned));
        });

    private async Task JoinOwnedClosureAsync(Func<Task> close)
    {
        var admission = new OwnedSessionCommandService.OriginalInvocation();
        Task? completion;
        try { completion = await admission.RunAsync(() => ExecuteAsync<Task?>(close, null).AsTask()).ConfigureAwait(false); }
        catch (Exception failure) { throw new AgentDependencyRetentionException("permission", "closure admission", [failure], new { Owner = this, Admission = admission }); }
        if (completion is null)
        {
            await JoinDeliveryOriginalsAsync([_shutdown.Task]).ConfigureAwait(false);
            return;
        }
        await JoinDeliveryOriginalsAsync([completion]).ConfigureAwait(false);
        // A concurrently disposed service owns all remaining completions. Do not release a caller's
        // source/handle on ExecuteAsync's stopped fallback before that shutdown has actually joined.
        if (Volatile.Read(ref _disposeStarted) != 0) await JoinDeliveryOriginalsAsync([_shutdown.Task]).ConfigureAwait(false);
    }

    private Task CloseOwned(OwnedPermissionExecution execution)
    {
        if (execution.Closure is not null) return execution.Closure;
        if (!Owns(execution)) return Task.CompletedTask;
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controls = new List<Exception>();
        var deliveries = execution.Deliveries.ToArray();
        var inputs = execution.InputDeliveries.ToArray();
        execution.Closure = CloseCoreAsync();
        execution.Closed = true;
        foreach (var pending in deliveries)
        {
            try { Complete(pending.Snapshot.Handle, AgentPermissionDecisionKind.Cancel); }
            catch (Exception failure) { controls.Add(failure); }
            finally { pending.Completion.TrySetResult(new(AgentPermissionDecisionKind.Cancel)); }
        }
        foreach (var pending in inputs)
        {
            try { CompleteInput(pending.Snapshot.Handle, null); }
            catch (Exception failure) { controls.Add(failure); }
            finally { pending.Completion.TrySetResult(null); }
        }
        launch.TrySetResult();
        return execution.Closure;

        async Task CloseCoreAsync()
        {
            await launch.Task.ConfigureAwait(false);
            var failures = new List<Exception>(controls);
            try
            {
                await JoinDeliveryOriginalsAsync(deliveries.Select(pending => (Task)pending.Delivery!)
                    .Concat(inputs.Select(pending => (Task)pending.Delivery!))).ConfigureAwait(false);
            }
            catch (Exception failure) { failures.Add(failure); }
            if (controls.Count != 0 || failures.Any(OwnedProviderEventForwarding.HasRetention))
                throw new AgentDependencyRetentionException(execution.SessionId, "permission closure", failures, new { Owner = this, Execution = execution, Deliveries = deliveries, Inputs = inputs });
            try
            {
                await _actor.AskAsync(_ =>
                {
                    _ownedExecutions.Remove(execution.OperationId);
                    execution.Attachment = null;
                    execution.ExecutionToken = default;
                    execution.AttachmentToken = default;
                    execution.RunToken = default;
                    return ValueTask.FromResult(true);
                }).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
                throw new AgentDependencyRetentionException(execution.SessionId, "permission closure index", failures, new { Owner = this, Execution = execution });
            }
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }

    private bool Owns(OwnedPermissionExecution execution)
        => ReferenceEquals(execution.Owner, this) && _ownedExecutions.TryGetValue(execution.OperationId, out var current)
            && ReferenceEquals(current, execution);

    private bool CanUse(OwnedPermissionExecution execution)
        => !_stopped && !_ownedAdmissionClosed && Owns(execution) && execution.Bound && !execution.Closed
            && !execution.ExecutionToken.IsCancellationRequested && !execution.AttachmentToken.IsCancellationRequested
            && (!execution.RunBound || !execution.RunToken.IsCancellationRequested)
            && execution.Attachment is { IsRetiring: false };

    private bool IsCanceled(PendingPermission pending)
        => pending.CancellationToken.IsCancellationRequested || (pending.OwnedExecution is { } execution && !CanUse(execution));

    private static bool Eligible(OwnedPermissionExecution execution, AgentPermissionRequest request)
        => request is AgentCommandPermissionRequest command
            && request.Kind == "commandExecution" && request.SessionId == execution.SessionId && request.ProviderId.Value == execution.ProviderId
            && ValidOwnedText(request.InteractionId, OwnedIdentityLimit, required: true, identity: true)
            && (request.RunId is null || ValidOwnedText(request.RunId.Value.Value, OwnedIdentityLimit, required: true, identity: true))
            && (!execution.RunBound || request.RunId is null || request.RunId == execution.RunId)
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

        var requiredCleanupConfirmed = false;
        try
        {
            var ownedCompletion = await _actor.AskAsync(actorCancellationToken =>
            {
                _stopped = true;
                _ownedAdmissionClosed = true;
                var owned = JoinDeliveryOriginalsAsync(_ownedExecutions.Values.ToArray().Select(CloseOwned));
                foreach (var entry in _pending.Values)
                {
                    entry.Completion.TrySetResult(new(AgentPermissionDecisionKind.Cancel));
                }

                _pending.Clear();
                return ValueTask.FromResult(owned);
            }).ConfigureAwait(false);
            Exception? ordinaryFailure = null;
            try { await ownedCompletion.ConfigureAwait(false); }
            catch (Exception failure)
            {
                if (OwnedProviderEventForwarding.HasRetention(failure)) throw;
                ordinaryFailure = failure;
            }
            try { await _actor.StopAsync().ConfigureAwait(false); }
            catch (Exception failure)
            {
                throw new AgentDependencyRetentionException("permission", "mailbox stop",
                    ordinaryFailure is null ? [failure] : [ordinaryFailure, failure], this);
            }
            requiredCleanupConfirmed = true;
            if (ordinaryFailure is not null) ExceptionDispatchInfo.Capture(ordinaryFailure).Throw();
            _shutdown.TrySetResult();
        }
        catch (Exception failure)
        {
            var reported = !requiredCleanupConfirmed && !OwnedProviderEventForwarding.HasRetention(failure)
                ? new AgentDependencyRetentionException("permission", "shutdown prerequisite", [failure], this)
                : failure;
            _shutdown.TrySetException(reported);
            ExceptionDispatchInfo.Capture(reported).Throw();
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
        internal OwnedDeliveryLifetime? Lifetime { get; set; }
    }

    internal static async Task JoinDeliveryOriginalsAsync(IEnumerable<Task> originals)
    {
        var stages = originals.Select(original => new DeliveryStage(() => original)).ToArray();
        foreach (var stage in stages) stage.Launch();
        var failures = new List<Exception>();
        foreach (var stage in stages)
            if (await stage.Outcome.ConfigureAwait(false) is not null) failures.Add(stage.Failure!);
        if (stages.Any(static stage => stage.Original is null) || failures.Any(OwnedProviderEventForwarding.HasRetention))
            throw new AgentDependencyRetentionException("permission", "closure originals", failures, stages);
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    // Owns actual delivery registrations and their destination, shared only by permission/input delivery.
    internal sealed class OwnedDeliveryLifetime(object dependencies)
    {
        internal CancellationTokenSource? Source { get; private set; }
        internal DeliveryStage? Body { get; private set; }
        internal DeliveryStage? CancelPending { get; private set; }
        internal DeliveryStage? IndexCleanup { get; private set; }
        internal CancellationTokenRegistration[] Registrations { get; private set; } = [];
        internal DeliveryStage[] RegistrationCleanup { get; private set; } = [];
        internal bool SourceReleased { get; private set; }
        internal bool IndexReleased { get; private set; }
        internal Exception? SourceFailure { get; private set; }
        internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> body,
            Func<Task> cancelPending, Func<Task> removeIndex, CancellationToken[] tokens)
        {
            Source = new CancellationTokenSource();
            Registrations = new CancellationTokenRegistration[tokens.Length];
            var registered = 0;
            var failures = new List<Exception>();
            var retained = false;
            try
            {
                for (; registered < tokens.Length; registered++)
                    Registrations[registered] = tokens[registered].UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), Source);
                Body = new DeliveryStage(() => body(Source.Token));
                Body.Launch();
                if (await Body.Outcome.ConfigureAwait(false) is not null) failures.Add(Body.Failure!);
            }
            catch (Exception failure) { failures.Add(failure); }
            if (failures.Count != 0) CancelPending = new DeliveryStage(cancelPending);
            RegistrationCleanup = Registrations.Take(registered)
                .Select(registration => new DeliveryStage(() => registration.DisposeAsync().AsTask())).ToArray();
            // Independent cancellation/index controls and every unregister start before a dependent join.
            CancelPending?.Launch();
            foreach (var cleanup in RegistrationCleanup) cleanup.Launch();
            if (CancelPending is not null && await CancelPending.Outcome.ConfigureAwait(false) is not null)
            {
                failures.Add(CancelPending.Failure!);
                retained = true;
            }
            foreach (var cleanup in RegistrationCleanup)
                if (await cleanup.Outcome.ConfigureAwait(false) is not null) { failures.Add(cleanup.Failure!); retained = true; }
            retained |= failures.Any(OwnedProviderEventForwarding.HasRetention);
            if (!retained)
            {
                try { Source.Dispose(); SourceReleased = true; }
                catch (Exception failure) { SourceFailure = failure; failures.Add(failure); retained = true; }
            }
            if (!retained)
            {
                IndexCleanup = new DeliveryStage(removeIndex);
                IndexCleanup.Launch();
                if (await IndexCleanup.Outcome.ConfigureAwait(false) is not null) { failures.Add(IndexCleanup.Failure!); retained = true; }
                else IndexReleased = true;
            }
            if (retained) throw new AgentDependencyRetentionException("permission delivery", "required cleanup", failures, new { Owner = dependencies, Lifetime = this });
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
            return ((Task<T>)Body!.Original!).GetAwaiter().GetResult();
        }
    }

    internal sealed class DeliveryStage
    {
        private readonly Func<Task> _invoke;
        private readonly TaskCompletionSource<Task> _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DeliveryStage(Func<Task> invoke) { _invoke = invoke; Outcome = ObserveAsync(); }
        internal Task? Original { get; private set; }
        internal AggregateException? OriginalFaults { get; private set; }
        internal Exception? Failure { get; private set; }
        internal Task<Exception?> Outcome { get; }
        internal void Launch()
        {
            try { Original = _invoke(); _launched.TrySetResult(Original); }
            catch (Exception failure) { _launched.TrySetException(failure); }
        }
        private async Task<Exception?> ObserveAsync()
        {
            try { await (await _launched.Task.ConfigureAwait(false)).ConfigureAwait(false); return null; }
            catch (Exception failure)
            {
                OriginalFaults = Original?.Exception;
                Failure = OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : failure;
                return failure;
            }
        }
    }
}
