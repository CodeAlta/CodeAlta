using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    // This body is retained by forwarding admission through insertion, deferred execution and cleanup.
    // Durable queue records never construct an OwnedQueuedExecution or carry its authority.
    internal Task<OwnedSessionCommandResult> QueueOwnedCommandAsync(OwnedTextQueueRequest request,
        OwnedSessionCommandReceipt receipt, bool reviewPermissions, CancellationToken cancellationToken)
        => QueueOwnedCommandAsync(request, receipt, reviewPermissions, cancellationToken, false);

    internal Task<OwnedSessionCommandResult> QueueOwnedCommandAsync(OwnedTextQueueRequest request,
        OwnedSessionCommandReceipt receipt, bool reviewPermissions, CancellationToken cancellationToken, bool enableUserInput)
        => AdmitAsync(() => QueueOwnedBodyAsync(request, receipt, reviewPermissions, cancellationToken, enableUserInput), CancellationToken.None);

    private async Task<OwnedSessionCommandResult> QueueOwnedBodyAsync(OwnedTextQueueRequest request,
        OwnedSessionCommandReceipt receipt, bool reviewPermissions, CancellationToken cancellationToken, bool enableUserInput)
    {
        var item = new OwnedQueuedExecution(request, cancellationToken);
        item.CancellationWorker.Launch(item.CancelExecutionAsync);
        RuntimeSessionEntry? captured = null;
        Actors.SessionActor? capturedActor = null;
        OwnedProviderEventForwarding.Use? registrationUse = null;
        var inserted = false;
        var preDispatchCancellationRefused = false;
        QueuedPromptDrainWork? work = null;
        SessionPermissionService.OwnedPermissionExecution? permission = null;
        var result = new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Failed, Code: "queue_target_unavailable");
        try
        {
            item.CallerRegistration = cancellationToken.UnsafeRegister(static state => ((OwnedQueuedExecution)state!).Stop.TrySetResult(), item);
            item.CallerRegistered = true;
            if (!_disposed && request.ExpectedRuntimeInstanceId == _runtimeInstanceId && _sessionActors.TryGet(request.SessionId, out var actor))
            {
                capturedActor = actor;
                captured = await actor.QueryAsync(_ =>
                {
                    if (!_entries.TryGetValue(request.SessionId, out var entry) || !CanUseOwnedQueue(entry, item)
                        || entry.OwnedQueue is not null) return ValueTask.FromResult<RuntimeSessionEntry?>(null);
                    registrationUse = entry.Attachment.TryAcquireHandleUse();
                    if (registrationUse is null) return ValueTask.FromResult<RuntimeSessionEntry?>(null);
                    return ValueTask.FromResult<RuntimeSessionEntry?>(entry);
                }, CancellationToken.None).ConfigureAwait(false);
                if (captured is not null)
                {
                    // Register outside the mailbox: an already-cancelled token may invoke inline.
                    // The short setup use protects the source until publication or refused cleanup.
                    item.AttachmentRegistration = captured.Attachment.Cancellation.Token.UnsafeRegister(
                        static state => ((OwnedQueuedExecution)state!).Stop.TrySetResult(), item);
                    item.AttachmentRegistered = true;
                    inserted = await actor.QueryAsync(_ =>
                    {
                        if (!_entries.TryGetValue(request.SessionId, out var current) || !ReferenceEquals(current, captured)
                            || !CanUseOwnedQueue(captured, item) || captured.OwnedQueue is not null) return ValueTask.FromResult(false);
                        captured.OwnedQueue = item;
                        return ValueTask.FromResult(true);
                    }, CancellationToken.None).ConfigureAwait(false);
                    if (inserted) { registrationUse!.Dispose(); registrationUse = null; }
                }
            }
            if (!inserted)
            {
                if (item.IsCancellationRequested) result = new(OwnedSessionCommandOutcome.Cancelled, Code: "queue_cancelled");
                receipt.CompleteQueueInsertion(new(false, result.Code!));
            }
            else
            {
                if (captured is null) throw new InvalidOperationException("Queue insertion lost its captured entry.");
                receipt.CompleteQueueInsertion(new(true, "queue_accepted"));
                // Retain the actual immediate attempt. It may wait for this item to drain, so never
                // join it from this body before publishing Drained. Recursive tails remain runtime-owned.
                _ = _forwarding.RunAsync(async () =>
                {
                    try { await TryDrainNextQueuedPromptAsync(request.SessionId, capturedActor, item).ConfigureAwait(false); }
                    catch (ObjectDisposedException) when (_disposed || captured.Attachment.IsRetiring) { item.Stop.TrySetResult(); }
                    catch
                    {
                        item.Stop.TrySetResult();
                        throw;
                    }
                }, external: false, reportFailure: true);
                await Task.WhenAny(item.Claim.Task, item.Stop.Task).ConfigureAwait(false);
                // Cancellation and claim race in the same mailbox. A claimed item must keep its
                // exact use and run cleanup; it cannot be removed as though it were still waiting.
                work = await capturedActor!.QueryAsync(_ =>
                {
                    if (item.Claim.Task.IsCompleted) return ValueTask.FromResult(item.Claim.Task.GetAwaiter().GetResult());
                    item.Claim.TrySetResult(null);
                    return ValueTask.FromResult<QueuedPromptDrainWork?>(null);
                }, CancellationToken.None).ConfigureAwait(false);
                if (work is null)
                    result = item.IsCancellationRequested
                        ? new(OwnedSessionCommandOutcome.Cancelled, Code: "queue_cancelled")
                        : new(OwnedSessionCommandOutcome.Failed, Code: "queue_target_unavailable");
                else
                {
                    // Item cancellation may have won after claim but before provider admission.
                    if (item.IsCancellationRequested || captured.Attachment.IsRetiring)
                    {
                        // Retirement can precede its cancellation callback; retain this explicit refusal.
                        preDispatchCancellationRefused = true;
                        throw new OperationCanceledException("The original queue execution was cancelled.");
                    }
                    item.Execution.Token.ThrowIfCancellationRequested();
                    QueueRunLifecycle? lifecycle = null;
                    var send = new AgentSendOptions { Input = AgentInput.Text(request.Text) };
                    if (reviewPermissions || enableUserInput)
                    {
                        permission = await Permissions.CreateOwnedExecutionAsync(receipt.OperationId, request.SessionId, item.Execution.Token, reviewPermissions, enableUserInput).ConfigureAwait(false);
                        if (permission is null || !await Permissions.BindOwnedExecutionAsync(permission, _runtimeInstanceId,
                            captured.Attachment, captured.ProviderId).ConfigureAwait(false))
                            throw new QueueBindingException();
                        lifecycle = new(Permissions.CreateOwnedRunLifecycle(permission), Permissions.CreateOwnedCommandHandler(permission));
                        send = new() { Input = send.Input, OnPermissionRequest = lifecycle.HandleAsync, RunLifecycle = lifecycle,
                            OnUserInputRequest = Permissions.CreateOwnedUserInputHandler(permission), EnableUserInputTool = permission.EnableUserInput };
                    }
                    var startedAt = DateTimeOffset.UtcNow;
                    Task<AgentRunId>? runOriginal = null;
                    item.Run.Launch(() => runOriginal = RunCapturedAsync(work.SessionHandleId, send, item.Execution.Token));
                    if (await item.Run.Outcome.ConfigureAwait(false) is { } runFailure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(runFailure);
                    var runId = await runOriginal!.ConfigureAwait(false);
                    if (lifecycle is not null && !lifecycle.WasBound) throw new QueueBindingException();
                    await PublishRunSubmittedIfStillInFlightAsync(captured.ToDescriptor(), runId, startedAt, CancellationToken.None, captured).ConfigureAwait(false);
                    result = new(OwnedSessionCommandOutcome.Completed, runId, "queue_dispatched");
                }
            }
        }
        catch (QueueBindingException)
        {
            result = new(OwnedSessionCommandOutcome.Failed, Code: "queue_binding_unavailable");
        }
        catch (OperationCanceledException failure) when (preDispatchCancellationRefused || item.IsCancellationRequested || item.Execution.IsCancellationRequested)
        {
            item.ReleaseDecision.Observe(failure);
            result = new(OwnedSessionCommandOutcome.Cancelled, Code: "queue_cancelled");
        }
        catch (Exception failure)
        {
            item.ReleaseDecision.Observe(failure);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "queue_failed");
        }
        finally
        {
            // Begin permission closure independently of the already-running cancellation worker.
            // Neither may be substituted for the original provider send or registration joins.
            if (permission is not null) item.PermissionClose.Launch(() => Permissions.CloseOwnedExecutionAsync(permission));
            if (item.CallerRegistered) item.CallerClose.Launch(() => item.CallerRegistration.DisposeAsync().AsTask());
            if (item.AttachmentRegistered) item.AttachmentClose.Launch(() => item.AttachmentRegistration.DisposeAsync().AsTask());
            item.ExecutionSettled.TrySetResult();
            if (permission is not null) await Join(item.PermissionClose, "permission close", false).ConfigureAwait(false);
            if (item.CallerRegistered) await Join(item.CallerClose, "caller registration", true).ConfigureAwait(false);
            if (item.AttachmentRegistered) await Join(item.AttachmentClose, "attachment registration", true).ConfigureAwait(false);
            await Join(item.CancellationWorker, "cancellation worker", false).ConfigureAwait(false);
            if (item.ReleaseDecision.Retained)
            {
                var retained = new AgentDependencyRetentionException("owned queue", "retained terminal prerequisites", item.ReleaseDecision.Failures,
                    new { Runtime = this, Item = item, Entry = captured, Permission = permission, RegistrationUse = registrationUse, Work = work });
                _forwarding.RetainDependencies(retained, item);
                registrationUse?.Retain(retained);
                work?.Use.Retain(retained);
                item.ReleaseDecision.Complete();
                receipt.CompleteQueueInsertion(new(false, "queue_cleanup_failed"));
                item.Drained.TrySetException(retained);
                throw retained;
            }
            try
            {
                if (captured is not null)
                    await capturedActor!.QueryAsync(_ =>
                    {
                        if (ReferenceEquals(captured.OwnedQueue, item)) captured.OwnedQueue = null;
                        if (work is not null) captured.CompleteQueueDrain();
                        return ValueTask.FromResult(true);
                    }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                var retained = new AgentDependencyRetentionException("owned queue", "slot cleanup", [failure],
                    new { Runtime = this, Item = item, RegistrationUse = registrationUse, Work = work });
                item.ReleaseDecision.Observe(retained);
                _forwarding.RetainDependencies(retained, item);
                registrationUse?.Retain(retained);
                work?.Use.Retain(retained);
                item.Drained.TrySetException(retained);
                throw retained;
            }
            finally
            {
                if (!item.ReleaseDecision.Retained)
                {
                    try
                    {
                        item.Execution.Dispose();
                        registrationUse?.Dispose();
                        work?.Use.Dispose();
                    }
                    catch (Exception failure)
                    {
                        var retained = new AgentDependencyRetentionException("owned queue", "dependency release", [failure],
                            new { Runtime = this, Item = item, RegistrationUse = registrationUse, Work = work });
                        item.ReleaseDecision.Observe(retained);
                        registrationUse?.Retain(retained);
                        work?.Use.Retain(retained);
                        _forwarding.RetainDependencies(retained, item);
                        item.ReleaseDecision.Complete();
                        receipt.CompleteQueueInsertion(new(false, "queue_cleanup_failed"));
                        item.Drained.TrySetException(retained);
                        throw retained;
                    }
                }
                item.ReleaseDecision.Complete();
                receipt.CompleteQueueInsertion(new(false, result.Code ?? "queue_failed"));
                if (item.ReleaseDecision.Released) item.Drained.TrySetResult();
            }
        }
        return result;

        async Task Join(OwnedSessionCommandService.OriginalInvocation invocation, string stage, bool requiredRelease)
        {
            if (await invocation.Outcome.ConfigureAwait(false) is not { } failure) return;
            result = new(OwnedSessionCommandOutcome.Failed, Code: "queue_cleanup_failed");
            item.ReleaseDecision.Observe(requiredRelease || invocation.Original is null
                ? new AgentDependencyRetentionException("owned queue", stage, [failure], item) : failure);
        }
    }

    // All entry reads/mutations below execute in the existing session mailbox.
    private bool CanUseOwnedQueue(RuntimeSessionEntry entry, OwnedQueuedExecution item)
        => !_disposed && !_transitions.ContainsKey(item.Request.SessionId) && !entry.IsTerminated
            && string.Equals(entry.SessionId, item.Request.SessionId, StringComparison.Ordinal)
            && item.Request.ExpectedRuntimeInstanceId == _runtimeInstanceId
            && entry.Attachment.Ordinal == item.Request.ExpectedAttachmentGeneration
            && !entry.Attachment.IsRetiring && HasOwnedCommandDefaults(entry)
            && string.IsNullOrWhiteSpace(entry.PendingAgentPromptId) && !item.IsCancellationRequested;

    private void RefuseUnavailableOwnedQueue(string sessionId)
    {
        if (_entries.TryGetValue(sessionId, out var entry) && entry.OwnedQueue is { } item
            && !item.Claim.Task.IsCompleted && !CanUseOwnedQueue(entry, item))
            item.Claim.TrySetResult(null);
    }

    private QueuedPromptDrainWork? TryClaimOwnedQueue(RuntimeSessionEntry entry)
    {
        if (entry.OwnedQueue is not { } item || item.Claim.Task.IsCompleted) return null;
        if (!CanUseOwnedQueue(entry, item)) { item.Claim.TrySetResult(null); return null; }
        var use = entry.Attachment.TryAcquireHandleUse();
        if (use is null) { item.Claim.TrySetResult(null); return null; }
        entry.BeginQueueDrain();
        var work = new QueuedPromptDrainWork(entry.SessionHandleId, null, entry, use, item);
        item.Claim.TrySetResult(work);
        return work;
    }

    private Task CloseOwnedQueueAttachmentAsync(OwnedProviderEventForwarding.Attachment attachment)
    {
        var closure = new AttachmentClosureJoin(new { Runtime = this, Attachment = attachment });
        return closure.RunAsync(() => Permissions.InvalidateOwnedAttachmentAsync(attachment), () =>
        {
            if (!_sessionActors.TryGet(attachment.Identity.SessionId, out var actor)) return null;
            return actor.QueryAsync<Task?>(_ =>
                {
                    if (_entries.TryGetValue(attachment.Identity.SessionId, out var entry)
                        && ReferenceEquals(entry.Attachment, attachment) && entry.OwnedQueue is { } item)
                    {
                        item.Stop.TrySetResult();
                        return ValueTask.FromResult<Task?>(item.Drained.Task);
                    }
                    return ValueTask.FromResult<Task?>(null);
                }, CancellationToken.None).AsTask();
        });
    }

    private sealed class OwnedQueuedExecution(OwnedTextQueueRequest request, CancellationToken callerCancellation)
    {
        internal OwnedSessionCommandService.DependencyReleaseDecision ReleaseDecision { get; } = new();
        internal OwnedSessionCommandService.OriginalInvocation Run { get; } = new();
        internal OwnedSessionCommandService.OriginalInvocation CancellationWorker { get; } = new();
        internal OwnedSessionCommandService.OriginalInvocation CancellationTraversal { get; } = new();
        internal OwnedSessionCommandService.OriginalInvocation PermissionClose { get; } = new();
        internal OwnedSessionCommandService.OriginalInvocation CallerClose { get; } = new();
        internal OwnedSessionCommandService.OriginalInvocation AttachmentClose { get; } = new();
        internal CancellationTokenRegistration CallerRegistration { get; set; }
        internal bool CallerRegistered { get; set; }
        internal bool AttachmentRegistered { get; set; }
        internal OwnedTextQueueRequest Request { get; } = request;
        internal bool IsCancellationRequested => callerCancellation.IsCancellationRequested || Stop.Task.IsCompleted;
        internal CancellationTokenSource Execution { get; } = new();
        internal TaskCompletionSource Stop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ExecutionSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<QueuedPromptDrainWork?> Claim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration AttachmentRegistration { get; set; }
        internal async Task CancelExecutionAsync()
        {
            await Task.WhenAny(Stop.Task, ExecutionSettled.Task).ConfigureAwait(false);
            if (Stop.Task.IsCompleted)
            {
                CancellationTraversal.Launch(Execution.CancelAsync);
                if (await CancellationTraversal.Outcome.ConfigureAwait(false) is { } failure)
                {
                    if (CancellationTraversal.Original is null)
                        throw new AgentDependencyRetentionException("owned queue", "cancellation launch", [failure], this);
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
                }
            }
        }
    }

    private sealed class QueueBindingException : Exception { }

    private sealed class QueueRunLifecycle(AgentRunLifecycle inner, AgentPermissionRequestHandler handler) : AgentRunLifecycle
    {
        private int _bound;
        private int _open;
        internal bool WasBound => Volatile.Read(ref _bound) != 0;
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            try { await inner.StartedAsync(runId, executionToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception failure) when (!OwnedProviderEventForwarding.HasRetention(failure)) { throw new QueueBindingException(); }
            Volatile.Write(ref _bound, 1);
            Volatile.Write(ref _open, 1);
        }
        public override Task ClosingAsync(AgentRunId runId)
        {
            Volatile.Write(ref _open, 0);
            return inner.ClosingAsync(runId);
        }
        internal Task<AgentPermissionDecision> HandleAsync(AgentPermissionRequest request, CancellationToken token)
            => Volatile.Read(ref _open) == 0 ? Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)) : handler(request, token);
    }
}
