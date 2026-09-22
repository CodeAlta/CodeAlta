using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionPermissionService
{
    private readonly Dictionary<SessionOwnedUserInputHandle, PendingUserInput> _inputs = [];
    private readonly HashSet<PendingUserInput> _inputDeliveries = [];

    private bool HasOwnedDeliveryCapacity(OwnedPermissionExecution execution)
        => _ownedDeliveries.Count + _inputDeliveries.Count < OwnedPendingLimit
            && execution.Deliveries.Count + execution.InputDeliveries.Count < OwnedPendingPerExecutionLimit;

    private Task JoinOwnedDeliveries() => JoinDeliveryOriginalsAsync(_ownedDeliveries.Select(p => (Task)p.Delivery!)
        .Concat(_inputDeliveries.Select(p => (Task)p.Delivery!)));

    internal AgentUserInputRequestHandler CreateOwnedUserInputHandler(OwnedPermissionExecution execution)
        => (request, token) => HandleOwnedUserInputAsync(execution, request, token);

    private async Task<AgentUserInputResponse> HandleOwnedUserInputAsync(OwnedPermissionExecution execution,
        AgentUserInputRequest request, CancellationToken token)
    {
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = await ExecuteAsync<Task<AgentUserInputResponse?>?>(() =>
        {
            if (!execution.EnableUserInput || !execution.RunBound || !CanUse(execution) || token.IsCancellationRequested
                || !HasOwnedDeliveryCapacity(execution) || execution.AttachmentOrdinal is <= 0 or > 9007199254740991
                || request is null || request.SessionId != execution.SessionId || request.ProviderId.Value != execution.ProviderId
                || !OwnedUserInputValidation.Text(request.InteractionId, 128, identity: true)
                || (request.RunId is not null && request.RunId != execution.RunId)) return null;
            var form = OwnedUserInputValidation.Snapshot(request.Form);
            if (form is null) return null;
            var handle = new SessionOwnedUserInputHandle(execution.OperationId, execution.RuntimeId, execution.AttachmentOrdinal,
                execution.SessionId, request.RunId?.Value, request.InteractionId, Guid.NewGuid());
            var pending = new PendingUserInput(new(handle, execution.ProviderId!, form), execution, token);
            _inputs.Add(handle, pending); _inputDeliveries.Add(pending); execution.InputDeliveries.Add(pending);
            pending.Delivery = DeliverInputAsync(pending, execution.ExecutionToken, execution.AttachmentToken, execution.RunToken, launch.Task);
            return pending.Delivery;
        }, null).ConfigureAwait(false);
        launch.TrySetResult();
        var answer = delivery is null ? null : await delivery.ConfigureAwait(false);
        return answer ?? throw new OperationCanceledException("The original nonsecret input attempt is unavailable.", new CancellationToken(true));
    }

    private async Task<AgentUserInputResponse?> DeliverInputAsync(PendingUserInput pending, CancellationToken operationToken,
        CancellationToken attachmentToken, CancellationToken runToken, Task launch)
    {
        await launch.ConfigureAwait(false);
        pending.Lifetime = new OwnedDeliveryLifetime(pending);
        return await pending.Lifetime.RunAsync(async token =>
        {
            try { return await pending.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await ExecuteAsync(() => CompleteInput(pending.Snapshot.Handle, null), false).ConfigureAwait(false);
                return await pending.Completion.Task.ConfigureAwait(false);
            }
        },
        async () => { await _actor.AskAsync(_ => ValueTask.FromResult(CompleteInput(pending.Snapshot.Handle, null))).ConfigureAwait(false); },
        async () =>
        {
            await _actor.AskAsync(_ =>
            {
                pending.Execution.InputDeliveries.Remove(pending);
                _inputDeliveries.Remove(pending);
                return ValueTask.FromResult(true);
            }).ConfigureAwait(false);
        }, [operationToken, attachmentToken, runToken, pending.Token]).ConfigureAwait(false);
    }

    private bool LiveInput(PendingUserInput pending) => pending.Execution.EnableUserInput && pending.Execution.RunBound
        && CanUse(pending.Execution) && !pending.Token.IsCancellationRequested;

    private bool CompleteInput(SessionOwnedUserInputHandle handle, AgentUserInputResponse? answer)
    {
        if (!_inputs.Remove(handle, out var pending)) return false;
        var live = LiveInput(pending);
        pending.Completion.SetResult(live ? answer : null);
        return live;
    }

    /// <summary>Lists at most four live immutable forms. Detach does not cancel; absence cannot recover a lost decision.</summary>
    /// <exception cref="ArgumentException">The session identity is invalid.</exception>
    /// <exception cref="OperationCanceledException">The query was cancelled before its mailbox observation.</exception>
    public ValueTask<SessionOwnedUserInputPage> ListOwnedUserInputsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!OwnedUserInputValidation.Text(sessionId, 128, identity: true)) throw new ArgumentException("Invalid session identity.", nameof(sessionId));
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = _inputs.Values.Where(p => p.Snapshot.Handle.SessionId == sessionId && LiveInput(p)).Take(5).Select(p => p.Snapshot).ToArray();
            return new SessionOwnedUserInputPage(Array.AsReadOnly(entries.Take(4).ToArray()), entries.Length > 4);
        }, new SessionOwnedUserInputPage(Array.Empty<SessionOwnedUserInputSnapshot>(), false));
    }

    /// <summary>Accepts exactly one literal answer per original prompt, once, under mailbox authority. No provider-success guarantee.</summary>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation precedes the owner decision.</exception>
    public ValueTask<bool> ResolveOwnedUserInputAsync(SessionOwnedUserInputHandle handle,
        IReadOnlyList<SessionOwnedUserInputAnswer> answers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle); cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_inputs.TryGetValue(handle, out var pending) || !LiveInput(pending)) return false;
            var response = OwnedUserInputValidation.Answers(pending.Snapshot.Form, answers);
            return response is not null && CompleteInput(handle, response);
        }, false);
    }

    /// <summary>Cancels only the exact still-live attempt, never the send, run or attachment.</summary>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation precedes the owner decision.</exception>
    public ValueTask<bool> CancelOwnedUserInputAsync(SessionOwnedUserInputHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle); cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(() => { cancellationToken.ThrowIfCancellationRequested(); return CompleteInput(handle, null); }, false);
    }

    internal sealed class PendingUserInput(SessionOwnedUserInputSnapshot snapshot, OwnedPermissionExecution execution, CancellationToken token)
    {
        internal SessionOwnedUserInputSnapshot Snapshot { get; } = snapshot;
        internal OwnedPermissionExecution Execution { get; } = execution;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<AgentUserInputResponse?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<AgentUserInputResponse?>? Delivery { get; set; }
        internal OwnedDeliveryLifetime? Lifetime { get; set; }
    }
}
