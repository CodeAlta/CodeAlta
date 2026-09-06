using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.Tests;

// Pure owner + actual response-dispatch policy used by the TUI. No catalog/history/provider/plugin startup.
[TestClass]
public sealed class AltaAskResponseTests
{
    [TestMethod]
    public async Task BlockedDoubleSubmit_DispatchesOnceAndDoesNotExposeMutableSnapshots()
    {
        var (service, handle) = await CreateAsync();
        var before = service.GetPending("session");
        var release = new TaskCompletionSource<SessionPromptResponseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        Task<SessionPromptResponseResult> Dispatch() { count++; return release.Task; }
        var first = service.RespondAsync(handle, Dispatch);
        var second = await service.RespondAsync(handle, Dispatch);
        Assert.AreEqual(1, count);
        Assert.IsFalse(second.Claimed);
        Assert.AreEqual(AltaAskResponseState.Pending, before[0].ResponseState);
        Assert.AreEqual(AltaAskResponseState.Submitting, service.Peek("session")!.ResponseState);
        Assert.IsFalse(service.TryRemoveHead(handle.SessionId, handle.AskId).Accepted);
        release.SetResult(SessionPromptResponseResult.Admitted("run-1"));
        Assert.IsTrue((await first).Claimed);
        Assert.IsNull(service.Peek("session"));
    }

    [TestMethod]
    public async Task CancelBeforeClaimPreventsDispatch_ClaimBeforeCancelRejectsCancellation()
    {
        var (service, handle) = await CreateAsync();
        Assert.IsTrue(service.TryCancelResponse(handle).Accepted);
        Assert.IsFalse((await service.RespondAsync(handle, () => throw new AssertFailedException("stale dispatch"))).Claimed);

        (service, handle) = await CreateAsync();
        var release = new TaskCompletionSource<SessionPromptResponseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = service.RespondAsync(handle, () => release.Task);
        Assert.IsFalse(service.TryCancelResponse(handle).Accepted);
        release.SetResult(SessionPromptResponseResult.Indeterminate());
        await task;
        Assert.IsFalse(service.TryCancelResponse(handle).Accepted);
        Assert.IsFalse(service.TryRemoveHead(handle.SessionId, handle.AskId).Accepted);
    }

    [TestMethod]
    public async Task InterceptedNormalCompletion_RetainsHeadAndInvalidatesOldSubmitAndCancel()
    {
        var (service, handle) = await CreateAsync();
        var second = await service.QueueAsync(new(), "session", AltaCallerIdentity.Host);
        var result = await service.RespondAsync(handle, () => SessionPromptResponseDispatch.RunAsync(_ => Task.CompletedTask));
        Assert.AreEqual(SessionPromptResponseAdmission.DefinitelyNotAdmittedByThisRoute, result.DispatchResult!.Admission);
        var fresh = service.Peek("session")!.ResponseHandle!;
        Assert.AreEqual(handle.AskId, fresh.AskId);
        Assert.AreEqual(handle.Generation + 1, fresh.Generation);
        Assert.IsFalse(service.TryCancelResponse(handle).Accepted);
        Assert.IsFalse((await service.RespondAsync(handle, () => throw new AssertFailedException("obsolete attempt"))).Claimed);
        Assert.IsTrue(service.TryCancelResponse(fresh).Accepted);
        Assert.AreEqual(second.AskId, service.Peek("session")!.AskId);
    }

    [TestMethod]
    public async Task ForeignNonHeadAndRemovedHandlesNeverDispatchOrRemoveNextHead()
    {
        var (service, first) = await CreateAsync();
        await service.QueueAsync(new(), "session", AltaCallerIdentity.Host);
        var next = service.GetPending("session")[1].ResponseHandle!;
        var (_, foreign) = await CreateAsync();
        var other = await service.QueueAsync(new(), "other", AltaCallerIdentity.Host);
        var otherHandle = service.Peek(other.SessionId)!.ResponseHandle!;
        foreach (var invalid in new[] { foreign, next })
        {
            Assert.IsFalse(service.TryCancelResponse(invalid).Accepted);
            Assert.IsFalse((await service.RespondAsync(invalid, () => throw new AssertFailedException("invalid handle"))).Claimed);
        }
        Assert.IsTrue(service.TryCancelResponse(first).Accepted);
        Assert.IsFalse((await service.RespondAsync(first, () => throw new AssertFailedException("removed head"))).Claimed);
        Assert.AreSame(next, service.Peek("session")!.ResponseHandle);
        Assert.AreSame(otherHandle, service.Peek("other")!.ResponseHandle);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RuntimeExceptionOrCancellation_RetainsNonReplayableHead(bool cancel)
    {
        var (service, handle) = await CreateAsync();
        var result = await service.RespondAsync(handle, () => SessionPromptResponseDispatch.RunAsync(async dispatch =>
        {
            await dispatch.InvokeAsync(() => throw (cancel ? new OperationCanceledException("runtime canceled") : new IOException("runtime failed")));
        }));
        Assert.AreEqual(SessionPromptResponseAdmission.Indeterminate, result.DispatchResult!.Admission);
        Assert.AreEqual(AltaAskResponseState.Indeterminate, service.Peek("session")!.ResponseState);
        Assert.IsFalse((await service.RespondAsync(handle, () => throw new AssertFailedException("uncertain replay"))).Claimed);
        Assert.IsFalse(service.TryCancelResponse(handle).Accepted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreparationFailure_IsDefiniteButUnclassifiedDelegateFailureIsIndeterminate(bool cancel)
    {
        var (service, handle) = await CreateAsync();
        var result = await service.RespondAsync(handle, () => SessionPromptResponseDispatch.RunAsync(_ =>
            throw (cancel ? new OperationCanceledException("preparation canceled") : new IOException("history failed"))));
        Assert.AreEqual(SessionPromptResponseAdmission.DefinitelyNotAdmittedByThisRoute, result.DispatchResult!.Admission);
        var fresh = service.Peek("session")!.ResponseHandle!;
        result = await service.RespondAsync(fresh, () => throw new IOException("no phase evidence"));
        Assert.AreEqual(SessionPromptResponseAdmission.Indeterminate, result.DispatchResult!.Admission);
    }

    [TestMethod]
    public async Task PositiveAdmission_ThrowingProjectionAndNotificationsStillRemoveExactlyOnce()
    {
        var (service, handle) = await CreateAsync();
        var next = await service.QueueAsync(new(), "session", AltaCallerIdentity.Host);
        var observed = 0;
        service.QueueChanged += (_, _) => throw new IOException("notification failed");
        service.QueueChanged += (_, args) => { service.GetPending(args.SessionId); observed++; };
        var result = await service.RespondAsync(handle, () => SessionPromptResponseDispatch.RunAsync(async dispatch =>
        {
            await dispatch.InvokeAsync(() => ValueTask.FromResult(new SessionCommandResult { Outcome = SessionCommandOutcomeKind.Submitted, RunId = "run-1" }));
            throw new IOException("header projection failed");
        }));
        Assert.AreEqual(SessionPromptResponseAdmission.Admitted, result.DispatchResult!.Admission);
        Assert.AreEqual("run-1", result.DispatchResult.RunId);
        StringAssert.Contains(result.DispatchResult.Diagnostic, "header projection failed");
        Assert.AreEqual(2, observed);
        Assert.AreEqual(2, result.NotificationErrors.Count);
        Assert.IsFalse(result.NotificationErrors is string[]);
        Assert.AreEqual(next.AskId, service.Peek("session")!.AskId);
        Assert.IsFalse((await service.RespondAsync(handle, () => throw new AssertFailedException("already admitted"))).Claimed);
    }

    [TestMethod]
    public async Task ClaimAndSettlementNotifications_AreOutsideOwnershipAndRejectReentrantReplay()
    {
        var (service, handle) = await CreateAsync();
        var observations = 0;
        service.QueueChanged += (_, args) =>
        {
            var read = Task.Run(() => service.GetPending(args.SessionId));
            Assert.IsTrue(read.Wait(TimeSpan.FromSeconds(5)), "Response notifications must not hold the owner lock.");
            Assert.IsFalse(service.RespondAsync(handle, () => throw new AssertFailedException("reentrant replay")).GetAwaiter().GetResult().Claimed);
            observations++;
        };
        var result = await service.RespondAsync(handle, () => Task.FromResult(SessionPromptResponseResult.Admitted("run")));
        Assert.AreEqual(2, observations);
        Assert.AreEqual(0, result.NotificationErrors.Count);
    }

    [TestMethod]
    public async Task PositiveReturnIsLateEvidence_NotAnEarlyReceiptOrProviderSuccess()
    {
        var (service, handle) = await CreateAsync();
        var backend = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = service.RespondAsync(handle, () => SessionPromptResponseDispatch.RunAsync(async dispatch =>
        {
            await dispatch.InvokeAsync(() => new ValueTask<SessionCommandResult>(backend.Task));
        }));
        Assert.IsFalse(task.IsCompleted);
        Assert.AreEqual(AltaAskResponseState.Submitting, service.Peek("session")!.ResponseState);
        backend.SetResult(new SessionCommandResult { Outcome = SessionCommandOutcomeKind.Submitted, RunId = "late-run" });
        Assert.AreEqual(SessionPromptResponseAdmission.Admitted, (await task).DispatchResult!.Admission);
        Assert.IsNull(service.Peek("session"));
    }

    [TestMethod]
    public async Task NullRuntimeResultAndSecondInvocation_DoNotInventAdmissionOrDispatchAgain()
    {
        var count = 0;
        var result = await SessionPromptResponseDispatch.RunAsync(async dispatch =>
        {
            await dispatch.InvokeAsync(() => { count++; return ValueTask.FromResult<SessionCommandResult>(null!); });
            await dispatch.InvokeAsync(() => { count++; return ValueTask.FromResult(new SessionCommandResult { Outcome = SessionCommandOutcomeKind.Submitted, RunId = "run" }); });
        });
        Assert.AreEqual(1, count);
        Assert.AreEqual(SessionPromptResponseAdmission.Indeterminate, result.Admission);
    }

    [TestMethod]
    [DataRow(SessionCommandOutcomeKind.Completed, "run")]
    [DataRow(SessionCommandOutcomeKind.Cancelled, "run")]
    [DataRow(SessionCommandOutcomeKind.Rejected, null)]
    [DataRow(SessionCommandOutcomeKind.FailedWithRestoreRecommendation, null)]
    [DataRow(SessionCommandOutcomeKind.Queued, null)]
    [DataRow(SessionCommandOutcomeKind.Steered, "run")]
    [DataRow(SessionCommandOutcomeKind.Submitted, null)]
    [DataRow(SessionCommandOutcomeKind.Submitted, " ")]
    [DataRow((SessionCommandOutcomeKind)999, "run")]
    public async Task UnknownOrMalformedRuntimeResult_IsIndeterminate(SessionCommandOutcomeKind outcome, string? runId)
    {
        var result = await SessionPromptResponseDispatch.RunAsync(async dispatch =>
        {
            await dispatch.InvokeAsync(() => ValueTask.FromResult(new SessionCommandResult { Outcome = outcome, RunId = runId }));
        });
        Assert.AreEqual(SessionPromptResponseAdmission.Indeterminate, result.Admission);
    }

    private static async Task<(AltaAskService Service, AltaAskResponseHandle Handle)> CreateAsync()
    {
        var service = new AltaAskService();
        await service.QueueAsync(new(), "session", AltaCallerIdentity.Host);
        return (service, service.Peek("session")!.ResponseHandle!);
    }
}
