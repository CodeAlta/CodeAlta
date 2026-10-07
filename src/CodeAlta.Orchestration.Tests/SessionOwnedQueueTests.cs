using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Explicit-root, inert-provider fixtures. Source/body audit is required before execution.</summary>
[TestClass]
public sealed class SessionOwnedQueueTests
{
    [TestMethod]
    public Task Queue_ReservationInsertionAndExecutionAreDistinctAndVolatile() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        await f.State(AgentSessionUpdateKind.Info, new("earlier"));
        var held = f.HoldTools();
        var ensure = f.Keep(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options(tools: held)));
        await f.Ready(held.Entered.Task);
        var request = f.Request(target);
        var receipt = f.Accept(request);
        Assert.IsFalse(receipt.QueueInsertion!.IsCompleted);
        Assert.IsFalse(receipt.Completion.IsCompleted);
        held.Release.TrySetResult();
        await f.Wait(ensure);
        Assert.AreEqual("queue_accepted", (await f.Wait(receipt.QueueInsertion!)).Code);
        Assert.IsFalse(receipt.Completion.IsCompleted);
        var state = await f.Wait(f.Journal.ReadLatestStateAsync(f.Session.SessionId, f.Session.CreatedAt));
        Assert.IsNotNull(state);
        Assert.IsFalse(state.QueuedPrompts.Any(p => p.Prompt == request.Text));
        var script = f.AddScript();
        await f.State(AgentSessionUpdateKind.Idle);
        await f.Ready(script.Started.Task);
        Assert.IsFalse(receipt.Completion.IsCompleted);
        Assert.AreEqual(AgentInput.Text(request.Text).Items[0], script.Options!.Input.Items[0]);
        script.Release.TrySetResult();
        Assert.AreEqual("queue_dispatched", (await f.Wait(receipt.Completion)).Code);
    });

    [TestMethod]
    public Task Queue_ReplayConflictsCapacityAndOutstandingSlot() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        await f.State(AgentSessionUpdateKind.Info, new("earlier"));
        var request = f.Request(target);
        var receipt = f.Accept(request);
        await f.Wait(receipt.QueueInsertion!);
        Assert.AreSame(receipt, f.Commands.AdmitQueue(request).Receipt);
        foreach (var changed in new[] { request with { Text = request.Text + "!" }, request with { SessionId = request.SessionId + "-different" },
            request with { ExpectedAttachmentGeneration = request.ExpectedAttachmentGeneration + 1 },
            request with { ExpectedRuntimeInstanceId = new Guid("00000000-0000-0000-0000-000000000001") } })
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.Commands.AdmitQueue(changed).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.Commands.AdmitSend(new(request.ClientRequestId, request.SessionId, request.Text)).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy, f.Commands.AdmitQueue(request with { ClientRequestId = "busy" }).Kind);
        var cancel = f.Cancel(receipt, "cancel");
        Assert.AreSame(cancel, f.Commands.AdmitCancelQueue(new("cancel", receipt.OperationId)).Receipt);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.Commands.AdmitCancelQueue(new("cancel", new Guid("00000000-0000-0000-0000-000000000001"))).Kind);
        await f.Wait(cancel.Completion);
        await f.Wait(receipt.Completion);
        // Both receipts have settled. A cancellation of the settled queue operation takes the place of the older one,
        // the queue operation itself, whose key is not run again and which is no target any more.
        Assert.IsTrue(f.Commands.HasCapacity);
        var late = f.Commands.AdmitCancelQueue(new("late", receipt.OperationId));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, late.Kind);
        Assert.AreEqual("already_terminal", (await f.Wait(late.Receipt!.Completion)).Code);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.Commands.AdmitQueue(request).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.UnknownTarget, f.Commands.AdmitCancelQueue(new("later", receipt.OperationId)).Kind);
        await f.Wait(f.Commands.DisposeAsync().AsTask());
        Assert.AreSame(cancel, f.Commands.AdmitCancelQueue(new("cancel", receipt.OperationId)).Receipt);
        Assert.AreSame(late.Receipt, f.Commands.AdmitCancelQueue(new("late", receipt.OperationId)).Receipt);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.Commands.AdmitQueue(request).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Closed, f.Commands.AdmitQueue(request with { ClientRequestId = "closed" }).Kind);
    }, capacity: 2);

    [TestMethod]
    public Task Queue_ValidationAndCallerWaitCancellationDoNotChangeAcceptedWork() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        var request = f.Request(target);
        foreach (var invalid in new[] { request with { SessionId = " padded " }, request with { Text = "\ud800" },
            request with { Text = new string('x', 32769) }, request with { ExpectedAttachmentGeneration = 0 },
            request with { ExpectedRuntimeInstanceId = Guid.Empty }, request with { ClientRequestId = new string('x', 257) } })
            Assert.ThrowsExactly<ArgumentException>(() => f.Commands.AdmitQueue(invalid));
        Assert.ThrowsExactly<ArgumentException>(() => f.Commands.AdmitCancelQueue(new(" padded ", Guid.NewGuid())));
        Assert.ThrowsExactly<ArgumentException>(() => f.Commands.AdmitCancelQueue(new("empty-target", Guid.Empty)));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.UnknownTarget, f.Commands.AdmitCancelQueue(new("unknown", Guid.NewGuid())).Kind);
        var caller = f.Source();
        await f.Wait(f.Keep(caller.CancelAsync()));
        Assert.ThrowsExactly<OperationCanceledException>(() => f.Commands.AdmitQueue(request, caller.Token));
        var script = f.AddScript();
        var receipt = f.Accept(request);
        await f.Wait(receipt.QueueInsertion!);
        await f.Ready(script.Started.Task);
        var wait = f.Keep(receipt.Completion.WaitAsync(caller.Token));
        await f.Cancelled(wait);
        Assert.IsFalse(script.Token.IsCancellationRequested);
        script.Release.TrySetResult();
        Assert.AreEqual("queue_dispatched", (await f.Wait(receipt.Completion)).Code);
    });

    [TestMethod]
    public async Task Queue_ExactInsertionRefusesMissingStaleNonownedPendingAndTerminated()
    {
        await Fixture.Run(async f =>
        {
            var absent = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
            await f.Refused(f.Request(absent) with { ExpectedAttachmentGeneration = 1 });
            Assert.AreEqual(0, f.Provider.Attachments);
            var unowned = await f.Prepare(owned: false);
            await f.Refused(f.Request(unowned));
            Assert.AreEqual(1, f.Provider.Attachments);
        });
        await Fixture.Run(async f =>
        {
            var target = await f.Prepare();
            var otherRuntime = target.RuntimeInstanceId == new Guid("00000000-0000-0000-0000-000000000001")
                ? new Guid("00000000-0000-0000-0000-000000000002") : new Guid("00000000-0000-0000-0000-000000000001");
            await f.Refused(f.Request(target) with { ExpectedRuntimeInstanceId = otherRuntime });
            await f.Refused(f.Request(target) with { ExpectedAttachmentGeneration = target.Entry!.AttachmentGeneration + 1 });
            await f.Wait(f.Runtime.SetActiveSessionAgentPromptIdAsync(f.Session.SessionId, "plan"));
            await f.Refused(f.Request(target));
            Assert.AreEqual(1, f.Provider.Attachments);
        });
        await Fixture.Run(async f =>
        {
            var target = await f.Prepare();
            await f.State(AgentSessionUpdateKind.Shutdown);
            await f.Refused(f.Request(target));
            Assert.AreEqual(1, f.Provider.Attachments);
        });
    }

    [TestMethod]
    public Task Queue_ClaimRevalidatesPendingPromptWithoutReplacement() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        await f.State(AgentSessionUpdateKind.Info, new("earlier"));
        var receipt = f.Accept(f.Request(target));
        await f.Wait(receipt.QueueInsertion!);
        await f.Wait(f.Runtime.SetActiveSessionAgentPromptIdAsync(f.Session.SessionId, "plan"));
        await f.State(AgentSessionUpdateKind.Idle);
        Assert.AreEqual("queue_target_unavailable", (await f.Wait(receipt.Completion)).Code);
        Assert.AreEqual(0, f.Provider.Sends);
        Assert.AreEqual(1, f.Provider.Attachments);
    });

    [TestMethod]
    public Task Queue_LegacyPrecedenceFastIdleTailAndRecoveredRecordsNeverGainReview() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        await f.State(AgentSessionUpdateKind.Info, new("earlier"));
        // Simulate a recovered durable record; no owned admission creates or imports this record.
        var local = await f.Wait(f.Journal.ReadLatestStateAsync(f.Session.SessionId, f.Session.CreatedAt));
        Assert.IsNotNull(local);
        local.QueuedPrompts.Add(new() { QueueItemId = "owned-looking", Kind = "owned-queue", Prompt = "legacy", State = "queued", CreatedAt = f.Session.CreatedAt });
        await f.Wait(f.Journal.AppendStateAsync(f.Session, local));
        var legacy = f.AddScript();
        var owned = f.AddScript();
        var receipt = f.Accept(f.Request(target));
        await f.Wait(receipt.QueueInsertion!);
        await f.State(AgentSessionUpdateKind.Idle);
        await f.Ready(legacy.Started.Task);
        Assert.IsNull(legacy.Options!.RunLifecycle);
        Assert.IsNull(legacy.Options!.OnPermissionRequest);
        Assert.IsFalse(owned.Started.Task.IsCompleted);
        // Idle is committed while RunAsync is still held; only the completion tail can drain next.
        await f.State(AgentSessionUpdateKind.Idle);
        legacy.Release.TrySetResult();
        await f.Ready(owned.Started.Task);
        Assert.IsNotNull(owned.Options!.RunLifecycle);
        owned.Release.TrySetResult();
        await f.Wait(receipt.Completion);
        Assert.AreEqual(2, f.Provider.Sends);
    }, review: true);

    [TestMethod]
    public Task Queue_WaitingCancelIsOperationTargetedAndDoesNotTouchActiveRun() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        await f.State(AgentSessionUpdateKind.Info, new("unrelated"));
        var receipt = f.Accept(f.Request(target));
        await f.Wait(receipt.QueueInsertion!);
        var cancel = f.Cancel(receipt, "cancel");
        Assert.AreEqual("queue_cancelled", (await f.Wait(receipt.Completion)).Code);
        Assert.AreEqual("queue_cancellation_signalled", (await f.Wait(cancel.Completion)).Code);
        Assert.AreEqual(0, f.Provider.Aborts);
        var state = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.AreEqual("unrelated", state.Entry!.ActiveRunId);
        var second = f.Accept(f.Request(target));
        await f.Wait(second.QueueInsertion!);
        Assert.AreSame(cancel, f.Commands.AdmitCancelQueue(new("cancel", receipt.OperationId)).Receipt);
        Assert.IsFalse(second.Completion.IsCompleted);
        await f.Wait(f.Cancel(second, "second-cancel").Completion);
    });

    [TestMethod]
    public Task Queue_ClaimedCancelJoinsOriginalTraversalAndPreservesAcceptedPermission() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        var script = f.AddScript(holdCancellation: true);
        var receipt = f.Accept(f.Request(target));
        await f.Wait(receipt.QueueInsertion!);
        await f.Ready(script.Started.Task);
        var handler = script.Options!.OnPermissionRequest!;
        var mismatch = f.Keep(handler(f.Permission("wrong-run", new("definitely-not-this-run")), CancellationToken.None));
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Wait(mismatch)).Kind);
        var accepted = f.Keep(handler(f.Permission("accepted", script.RunId), CancellationToken.None));
        var page = await f.Wait(f.Runtime.Permissions.ListOwnedCommandsAsync(f.Session.SessionId, CancellationToken.None).AsTask());
        var handle = page.Entries.Single().Handle;
        Assert.AreEqual(receipt.OperationId, handle.OperationId);
        Assert.IsTrue(await f.Wait(f.Runtime.Permissions.ResolveOwnedCommandAsync(handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Wait(accepted)).Kind);
        var pending = f.Keep(handler(f.Permission("pending", null), CancellationToken.None));
        var pendingPage = await f.Wait(f.Runtime.Permissions.ListOwnedCommandsAsync(f.Session.SessionId, CancellationToken.None).AsTask());
        var pendingHandle = pendingPage.Entries.Single().Handle;
        var cancel = f.Cancel(receipt, "cancel");
        await f.Ready(script.CancellationEntered.Task);
        Assert.IsFalse(receipt.Completion.IsCompleted);
        Assert.IsFalse(cancel.Completion.IsCompleted);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy, f.Commands.AdmitQueue(f.Request(target)).Kind);
        Assert.IsFalse(await f.Wait(f.Runtime.Permissions.ResolveOwnedCommandAsync(pendingHandle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Wait(pending)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Wait(accepted)).Kind);
        Assert.AreEqual(0, f.Provider.Aborts);
        script.Release.TrySetResult();
        Assert.IsFalse(receipt.Completion.IsCompleted);
        script.ReleaseCancellation.TrySetResult();
        await f.Wait(receipt.Completion);
        await f.Wait(cancel.Completion);
        Assert.IsTrue(script.RegistrationDisposed);
        Assert.IsTrue(script.ForwardingDisposed);
        var terminalCancel = f.Cancel(receipt, "terminal-cancel");
        Assert.AreEqual("already_terminal", (await f.Wait(terminalCancel.Completion)).Code);
    }, review: true);

    [TestMethod]
    public async Task Queue_DefaultDenialAndUnsupportedLifecycleDoNotCreateReview()
    {
        await Fixture.Run(async f =>
        {
            var target = await f.Prepare();
            var script = f.AddScript();
            var receipt = f.Accept(f.Request(target));
            await f.Wait(receipt.QueueInsertion!);
            await f.Ready(script.Started.Task);
            var decision = f.Keep(f.Provider.Latest.Options.OnPermissionRequest!(f.Permission("default", script.RunId), CancellationToken.None));
            Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Wait(decision)).Kind);
            var input = f.Keep(f.Provider.Latest.Options.OnUserInputRequest!(new AgentUserInputRequest(f.Provider.Descriptor.ProviderId,
                f.Session.SessionId, DateTimeOffset.UtcNow, null, "input", new AgentUserInputForm([])), CancellationToken.None));
            await f.Cancelled(input);
            script.Release.TrySetResult();
            await f.Wait(receipt.Completion);
        });
        await Fixture.Run(async f =>
        {
            var target = await f.Prepare();
            var script = f.AddScript(lifecycle: false);
            var receipt = f.Accept(f.Request(target));
            await f.Wait(receipt.QueueInsertion!);
            await f.Ready(script.Started.Task);
            var decision = f.Keep(script.Options!.OnPermissionRequest!(f.Permission("unsupported", script.RunId), CancellationToken.None));
            Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Wait(decision)).Kind);
            script.Release.TrySetResult();
            Assert.AreEqual("queue_binding_unavailable", (await f.Wait(receipt.Completion)).Code);
        }, review: true);
    }

    [TestMethod]
    [DataRow("retire")]
    [DataRow("owner")]
    [DataRow("runtime")]
    public Task Queue_WaitingLifetimeClosureSettlesWithoutAcquisition(string closure) => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        await f.State(AgentSessionUpdateKind.Info, new("earlier"));
        var receipt = f.Accept(f.Request(target));
        await f.Wait(receipt.QueueInsertion!);
        var close = f.Keep(f.Close(closure));
        await f.Wait(receipt.Completion);
        await f.Wait(close);
        Assert.AreEqual(0, f.Provider.Sends);
        Assert.AreEqual(1, f.Provider.Attachments);
    });

    [TestMethod]
    [DataRow("retire")]
    [DataRow("owner")]
    [DataRow("runtime")]
    public Task Queue_ClaimedLifetimeClosureJoinsOriginalCallbackAndRegistration(string closure) => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        var script = f.AddScript(holdCancellation: true);
        var receipt = f.Accept(f.Request(target));
        await f.Wait(receipt.QueueInsertion!);
        await f.Ready(script.Started.Task);
        var close = f.Keep(f.Close(closure));
        await f.Ready(script.CancellationEntered.Task);
        Assert.IsFalse(close.IsCompleted);
        Assert.IsFalse(receipt.Completion.IsCompleted);
        script.Release.TrySetResult();
        script.ReleaseCancellation.TrySetResult();
        await f.Wait(receipt.Completion);
        await f.Wait(close);
        Assert.IsTrue(script.RegistrationDisposed);
        Assert.IsTrue(script.ForwardingDisposed);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    }, review: true);

    [TestMethod]
    public Task Queue_RetiringAttachmentRefusesInsertionWithoutRecapture() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        f.Provider.HoldAbort = true;
        var detach = f.Keep(f.Close("retire"));
        await f.Ready(f.Provider.AbortEntered.Task);
        await f.Refused(f.Request(target));
        Assert.AreEqual(1, f.Provider.Attachments);
        Assert.AreEqual(0, f.Provider.Sends);
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Wait(detach);
    });

    [TestMethod]
    public Task Queue_ExistingOwnedSendAndEarlyIdleKeepDeferredExecutionDistinct() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        var original = f.AddScript();
        var deferred = f.AddScript();
        var sendAdmission = f.Commands.AdmitSend(new("original-send", f.Session.SessionId, "original"));
        if (sendAdmission.Receipt is { } retained) _ = f.Keep(retained.Completion);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, sendAdmission.Kind);
        await f.Ready(original.Started.Task);
        await f.State(AgentSessionUpdateKind.Info, original.RunId);
        var receipt = f.Accept(f.Request(target));
        await f.Wait(receipt.QueueInsertion!);
        Assert.IsFalse(deferred.Started.Task.IsCompleted);
        Assert.IsFalse(receipt.Completion.IsCompleted);
        await f.State(AgentSessionUpdateKind.Idle);
        Assert.IsFalse(sendAdmission.Receipt!.Completion.IsCompleted);
        original.Release.TrySetResult();
        await f.Ready(deferred.Started.Task);
        await f.Wait(sendAdmission.Receipt.Completion);
        deferred.Release.TrySetResult();
        Assert.AreEqual(deferred.RunId, (await f.Wait(receipt.Completion)).RunId);
        Assert.AreEqual(1, f.Provider.Attachments);
    }, review: true);

    [TestMethod]
    public Task Queue_OwnedFastCompletionTailDrainsLegacyInsertedWhileClaimed() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        var owned = f.AddScript();
        var legacy = f.AddScript();
        var receipt = f.Accept(f.Request(target));
        await f.Wait(receipt.QueueInsertion!);
        await f.Ready(owned.Started.Task);
        await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "later-legacy", "send", null));
        await f.State(AgentSessionUpdateKind.Idle);
        owned.Release.TrySetResult();
        await f.Ready(legacy.Started.Task);
        Assert.IsNull(legacy.Options!.RunLifecycle);
        Assert.AreEqual("queue_dispatched", (await f.Wait(receipt.Completion)).Code);
        legacy.Release.TrySetResult();
        await f.Wait(f.Close("retire")); // Joins the original legacy send and its completion tail.
    }, review: true);

    [TestMethod]
    public Task Queue_CancelBeforeInsertionSettlesBothResultsAfterOriginalActorWork() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        var held = f.HoldTools();
        var ensure = f.Keep(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options(tools: held)));
        await f.Ready(held.Entered.Task);
        var receipt = f.Accept(f.Request(target));
        var cancel = f.Cancel(receipt, "cancel-reservation");
        Assert.IsFalse(receipt.QueueInsertion!.IsCompleted);
        Assert.IsFalse(cancel.Completion.IsCompleted);
        held.Release.TrySetResult();
        await f.Wait(ensure);
        await f.Wait(cancel.Completion);
        Assert.IsFalse((await f.Wait(receipt.QueueInsertion!)).Accepted);
        Assert.AreEqual("queue_cancelled", (await f.Wait(receipt.Completion)).Code);
        Assert.AreEqual(0, f.Provider.Sends);
    });

    [TestMethod]
    public Task Queue_InvalidRunBindingFailsBeforePermissionCapableScript() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        var script = f.AddScript();
        script.InvalidBinding = true;
        var receipt = f.Accept(f.Request(target));
        await f.Wait(receipt.QueueInsertion!);
        Assert.AreEqual("queue_binding_unavailable", (await f.Wait(receipt.Completion)).Code);
        Assert.IsFalse(script.Started.Task.IsCompleted);
        Assert.IsNotNull(script.Options!.RunLifecycle);
        Assert.IsTrue(script.RegistrationDisposed);
        Assert.AreEqual(0, (await f.Wait(f.Runtime.Permissions.ListOwnedCommandsAsync(f.Session.SessionId, CancellationToken.None).AsTask())).Entries.Count);
    }, review: true);

    [TestMethod]
    public Task Queue_ProviderFailureIsBoundedAndReplayDoesNotDispatchAgain() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        var script = f.AddScript();
        script.Failure = new InvalidOperationException("inert provider detail must not enter receipt");
        var request = f.Request(target);
        var receipt = f.Accept(request);
        await f.Wait(receipt.QueueInsertion!);
        await f.Ready(script.Started.Task);
        script.Release.TrySetResult();
        Assert.AreEqual("queue_failed", (await f.Wait(receipt.Completion)).Code);
        Assert.AreSame(receipt, f.Commands.AdmitQueue(request).Receipt);
        Assert.AreEqual(1, f.Provider.Sends);
    });

    [TestMethod]
    public Task Queue_UnsolicitedProviderCancellationIsFailureAndReplayDoesNotDispatchAgain() => Fixture.Run(async f =>
    {
        var target = await f.Prepare();
        var script = f.AddScript();
        script.Failure = new OperationCanceledException("unsolicited inert provider detail must not enter receipt");
        try
        {
            var request = f.Request(target);
            var receipt = f.Accept(request);
            var insertion = await f.Wait(receipt.QueueInsertion!);
            Assert.AreEqual(new OwnedQueueInsertionResult(true, "queue_accepted"), insertion);
            await f.Ready(script.Started.Task);
            Assert.IsFalse(script.Token.IsCancellationRequested);
            Assert.IsFalse(receipt.Completion.IsCompleted);
            script.Release.TrySetResult();
            var result = await f.Wait(receipt.Completion);
            // The whole public result is bounded; the provider exception must not fault the receipt.
            Assert.IsTrue(receipt.Completion.IsCompletedSuccessfully);
            Assert.AreEqual(new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Failed, Code: "queue_failed"), result);
            Assert.IsFalse(script.Token.IsCancellationRequested);
            Assert.IsFalse(script.CancellationEntered.Task.IsCompleted);
            Assert.IsTrue(script.RegistrationDisposed);
            Assert.IsTrue(script.ForwardingDisposed);
            Assert.AreEqual(0, (await f.Wait(f.Runtime.Permissions.ListOwnedCommandsAsync(f.Session.SessionId, CancellationToken.None).AsTask())).Entries.Count);

            var replay = f.Commands.AdmitQueue(request);
            if (replay.Receipt is { } retained)
            {
                _ = f.Keep(retained.Completion);
                if (retained.QueueInsertion is { } retainedInsertion) _ = f.Keep(retainedInsertion);
            }
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, replay.Kind);
            Assert.AreSame(receipt, replay.Receipt);
            Assert.AreEqual(receipt.OperationId, replay.Receipt!.OperationId);
            Assert.AreSame(insertion, await f.Wait(replay.Receipt.QueueInsertion!));
            Assert.AreSame(result, await f.Wait(replay.Receipt.Completion));
            await f.Wait(f.Commands.DisposeAsync().AsTask());
            Assert.AreEqual(1, f.Provider.Sends);
            Assert.AreEqual(0, f.Provider.Aborts);
            Assert.AreEqual(0, f.Provider.EarlyDisposals);
        }
        finally
        {
            script.Release.TrySetResult();
            script.ReleaseCancellation.TrySetResult();
        }
    }, review: true);

    private sealed class HeldTools : IReadOnlyList<AgentToolDefinition>
    {
        internal TaskCompletionSource Entered { get; } = Gate();
        internal TaskCompletionSource Release { get; } = Gate();
        public int Count { get { Entered.TrySetResult(); Release.Task.GetAwaiter().GetResult(); return 0; } }
        public AgentToolDefinition this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<AgentToolDefinition> GetEnumerator() => Enumerable.Empty<AgentToolDefinition>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _work = [];
        private readonly HashSet<Task> _expectedCancellation = [];
        private readonly List<Exception> _failures = [];
        private readonly List<CancellationTokenSource> _sources = [];
        private readonly List<HeldTools> _tools = [];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-owned-queue-" + Guid.NewGuid().ToString("N"));
        private Task? _lifetime;
        private CodeAltaHost? _host;
        private Task? _hostClose;
        private bool _cleaning;
        private readonly TaskCompletionSource _cleanupStarted = Gate();
        internal Provider Provider { get; } = new();
        internal SessionViewDescriptor Session { get; private set; } = null!;
        internal SessionViewJournalStore Journal { get; private set; } = null!;
        internal SessionRuntimeService Runtime => _host!.RuntimeService;
        internal OwnedSessionCommandService Commands => _host!.Commands;
        internal static async Task Run(Func<Fixture, Task> body, int capacity = 256, bool review = false)
        {
            var f = new Fixture();
            f._lifetime = f.RunOwned(body, capacity, review);
            try { await f._lifetime.WaitAsync(TimeSpan.FromSeconds(40)); }
            catch (Exception ex)
            {
                lock (f._gate) f._failures.Add(ex);
                f.BeginCleanup(); // Independently release/cancel even when the original lifetime is still held.
            }
            Exception[] failures;
            lock (f._gate) failures = [.. f._failures];
            if (failures.Length != 0)
            {
                var error = new AggregateException("Queue fixture/root retained at " + f._root, failures);
                error.Data["RetainedFixture"] = f;
                throw error;
            }
        }
        private async Task RunOwned(Func<Fixture, Task> body, int capacity, bool review)
        {
            try
            {
                await Setup(capacity, review);
                bool run;
                lock (_gate) run = !_cleaning;
                if (run) await body(this);
            }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
            finally
            {
                BeginCleanup();
                await _cleanupStarted.Task;
                Task[] tasks;
                lock (_gate) tasks = [.. _work];
                // Originals, not bounded waiters, determine source release. The outer timeout
                // permanently fails and retains this entire lifetime if any join remains uncertain.
                await Task.WhenAll(tasks.Distinct().Select(Join));
                Task[] late;
                lock (_gate) late = _work.Except(tasks).ToArray();
                await Task.WhenAll(late.Select(Join));
                CancellationTokenSource[] sources;
                lock (_gate) sources = [.. _sources];
                foreach (var source in sources) source.Dispose();
                // Never delete roots: parent may inspect successful and failed fixtures alike.
            }
        }
        private void BeginCleanup()
        {
            HeldTools[] tools;
            CancellationTokenSource[] sources;
            bool closeHost;
            lock (_gate)
            {
                if (_cleaning) return;
                _cleaning = true;
                tools = [.. _tools]; sources = [.. _sources]; closeHost = _host is not null;
            }
            try
            {
                foreach (var hold in tools) hold.Release.TrySetResult();
                Provider.ReleaseAll();
                foreach (var source in sources) Initiate(source.CancelAsync);
                if (closeHost) Initiate(CloseHost);
            }
            finally { _cleanupStarted.TrySetResult(); }
        }
        private Task CloseHost()
        {
            var launch = Gate();
            Task task;
            lock (_gate)
            {
                if (_hostClose is not null) return _hostClose;
                var host = _host!;
                task = Keep(Dispose());
                _hostClose = task;
                async Task Dispose() { await launch.Task; await host.DisposeAsync(); }
            }
            launch.TrySetResult();
            return task;
        }
        private void CheckOpen()
        {
            lock (_gate) if (_cleaning) throw new InvalidOperationException("The failed/closing fixture cannot admit more work.");
        }
        private async Task Join(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (task.IsCanceled && _expectedCancellation.Contains(task)) { }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
        }
        private void Initiate(Func<Task> start)
        {
            try { _ = Keep(start()); }
            catch (Exception ex) { _ = Keep(Task.FromException(ex)); }
        }
        internal Task Keep(Task task) { lock (_gate) _work.Add(task); return task; }
        internal Task<T> Keep<T>(Task<T> task) { Keep((Task)task); return task; }
        internal Task Wait(Task task) => Keep(Keep(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<T> Wait<T>(Task<T> task) => Keep(Keep(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task Ready(Task signal) => Keep(signal.WaitAsync(TimeSpan.FromSeconds(5)));
        internal async Task Cancelled(Task task)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => task);
            _expectedCancellation.Add(task);
        }
        internal CancellationTokenSource Source()
        {
            lock (_gate) { CheckOpen(); var source = new CancellationTokenSource(); _sources.Add(source); return source; }
        }
        internal HeldTools HoldTools()
        {
            lock (_gate) { CheckOpen(); var tools = new HeldTools(); _tools.Add(tools); return tools; }
        }
        internal Script AddScript(bool holdCancellation = false, bool lifecycle = true)
        {
            CheckOpen();
            return Provider.Add(holdCancellation, lifecycle);
        }
        internal SessionExecutionOptions Options(bool owned = true, IReadOnlyList<AgentToolDefinition>? tools = null) => new()
        {
            ProviderId = Provider.Descriptor.ProviderId, ProviderKey = Provider.Descriptor.ProviderId.Value,
            WorkingDirectory = Session.WorkingDirectory, ProjectRoots = [Session.WorkingDirectory!], Model = "fixture-model", Tools = tools,
            OnPermissionRequest = owned ? Runtime.Permissions.OwnedDefaultPermissionHandler : static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
            OnUserInputRequest = owned ? Runtime.Permissions.OwnedDefaultUserInputHandler : static (_, _) => Task.FromCanceled<AgentUserInputResponse>(new CancellationToken(true)),
        };
        internal async Task<SessionRuntimeCurrentState> Prepare(bool owned = true)
        {
            await Wait(Runtime.EnsureCoordinatorSessionAsync(Session, Options(owned)));
            return await Wait(Runtime.GetCurrentStateAsync(Session.SessionId));
        }
        internal OwnedTextQueueRequest Request(SessionRuntimeCurrentState state) => new(Guid.NewGuid().ToString("N"), Session.SessionId,
            state.RuntimeInstanceId, state.Entry?.AttachmentGeneration ?? 1, " exact volatile text \n");
        internal OwnedSessionCommandReceipt Accept(OwnedTextQueueRequest request)
        {
            CheckOpen();
            var admission = Commands.AdmitQueue(request);
            if (admission.Receipt is { } retained) { _ = Keep(retained.Completion); if (retained.QueueInsertion is { } insertion) _ = Keep(insertion); }
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
            return admission.Receipt!;
        }
        internal OwnedSessionCommandReceipt Cancel(OwnedSessionCommandReceipt target, string key)
        {
            var admission = Commands.AdmitCancelQueue(new(key, target.OperationId));
            if (admission.Receipt is { } retained) _ = Keep(retained.Completion);
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
            return admission.Receipt!;
        }
        internal async Task Refused(OwnedTextQueueRequest request)
        {
            var receipt = Accept(request);
            Assert.AreEqual("queue_target_unavailable", (await Wait(receipt.QueueInsertion!)).Code);
            Assert.AreEqual("queue_target_unavailable", (await Wait(receipt.Completion)).Code);
        }
        internal Task Close(string kind) => kind switch
        {
            "owner" => Commands.DisposeAsync().AsTask(),
            "runtime" => Runtime.DisposeAsync().AsTask(),
            _ => Runtime.DetachRuntimeSessionAsync(Session.SessionId),
        };
        internal AgentCommandPermissionRequest Permission(string id, AgentRunId? run) => new(Provider.Descriptor.ProviderId, Session.SessionId,
            DateTimeOffset.UtcNow, run, id, null, "inert text", Session.WorkingDirectory!, null, "fixture", null, null, null);
        internal async Task State(AgentSessionUpdateKind kind, AgentRunId? run = null)
        {
            CheckOpen();
            using var source = new CancellationTokenSource();
            var marker = Guid.NewGuid().ToString("N");
            var observation = Keep(Observe());
            try { Provider.Latest.Emit(kind, run, marker); await Wait(observation); }
            finally
            {
                var cancel = Keep(source.CancelAsync());
                await Keep(Task.WhenAll(observation, cancel));
            }
            async Task Observe()
            {
                await foreach (var item in Runtime.StreamEventsAsync(source.Token))
                    if (item is SessionAgentEvent { Event: AgentSessionUpdateEvent update } && update.Message == marker) return;
                Assert.Fail("Missing uniquely tagged committed state.");
            }
        }
        private async Task Setup(int capacity, bool review)
        {
            for (var node = new DirectoryInfo(Path.GetDirectoryName(_root)!); node is not null; node = node.Parent)
                if ((node.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse fixture ancestry.");
            if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root already exists.");
            var home = Path.Combine(_root, "home"); var global = Path.Combine(_root, "global");
            var project = Path.Combine(_root, "project"); var builtin = Path.Combine(_root, "builtin");
            foreach (var path in new[] { home, global, project, builtin }) Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(builtin, ".git"));
            File.WriteAllText(Path.Combine(builtin, ".git", "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
            File.WriteAllText(Path.Combine(builtin, ".git", "fixture.ignore"), "");
            var catalog = new CatalogOptions { GlobalRoot = global };
            var projectId = (await Keep(new ProjectCatalog(catalog).UpsertFromPathAsync(project))).Id;
            Journal = new(catalog);
            Session = new() { SessionId = Guid.CreateVersion7().ToString(), Kind = SessionViewKind.ProjectSession, ProjectRef = projectId,
                ProviderId = Provider.Descriptor.ProviderId.Value, ProviderKey = Provider.Descriptor.ProviderId.Value,
                WorkingDirectory = project, Title = "Owned queue fixture", ModelId = "fixture-model", AgentPromptId = "default", CreatedAt = DateTimeOffset.UtcNow };
            await Keep(Journal.EnsureHeaderAsync(Session));
            var store = Journal.CreateSessionStore();
            await Keep(store.UpsertSessionAsync(new AgentSessionSummary { SessionId = Session.SessionId, ProviderId = Provider.Descriptor.ProviderId,
                ProviderKey = Provider.Descriptor.ProviderId.Value, WorkingDirectory = project, Title = Session.Title,
                ModelId = Session.ModelId, AgentPromptId = "default", CreatedAt = Session.CreatedAt, UpdatedAt = Session.CreatedAt }));
            await Keep(Journal.AppendStateAsync(Session, new() { ProviderKey = Session.ProviderKey, ModelId = Session.ModelId, AgentPromptId = "default" }));
            var readback = await Keep(store.GetSessionAsync(Session.SessionId));
            Assert.IsNotNull(readback); Assert.AreEqual(Session.SessionId, readback.SessionId);
            var host = await Keep(CodeAltaHost.CreateAsync(new() { GlobalRoot = global, CurrentProjectPath = project,
                DiscoveryScope = new(home, _root), BuiltInSkillRoot = builtin, PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                StartPlugins = false, OwnsLogging = false, IsHeadless = true, ReviewOwnedCommandPermissions = review,
                OwnedCommandReceiptCapacity = capacity, ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider.Descriptor, Provider.CreateRuntime) }));
            bool close;
            lock (_gate) { _host = host; close = _cleaning; }
            if (close) await Keep(CloseHost());
        }
    }

    private sealed class Script(bool lifecycle)
    {
        internal bool Lifecycle => lifecycle;
        internal bool InvalidBinding { get; set; }
        internal Exception? Failure { get; set; }
        internal TaskCompletionSource Started { get; } = Gate();
        internal TaskCompletionSource Release { get; } = Gate();
        internal TaskCompletionSource CancellationEntered { get; } = Gate();
        internal TaskCompletionSource ReleaseCancellation { get; } = Gate();
        internal AgentSendOptions? Options { get; set; }
        internal CancellationToken Token { get; set; }
        internal AgentRunId RunId { get; } = new(Guid.NewGuid().ToString("N"));
        internal bool RegistrationDisposed { get; set; }
        internal bool ForwardingDisposed { get; set; }
    }

    private sealed class Provider
    {
        private readonly object _gate = new();
        private readonly List<Script> _scripts = [];
        private readonly List<Session> _sessions = [];
        private int _sends, _aborts, _active, _earlyDisposals;
        private bool _cleaning;
        internal int Sends => Volatile.Read(ref _sends);
        internal int Aborts => Volatile.Read(ref _aborts);
        internal int EarlyDisposals => Volatile.Read(ref _earlyDisposals);
        internal int Attachments { get { lock (_gate) return _sessions.Count; } }
        internal Session Latest { get { lock (_gate) return _sessions[^1]; } }
        internal bool HoldAbort { get; set; }
        internal TaskCompletionSource AbortEntered { get; } = Gate();
        internal TaskCompletionSource ReleaseAbort { get; } = Gate();
        internal ModelProviderDescriptor Descriptor { get; } = new(new("owned-queue-fixture"), "Owned queue fixture") { DefaultModelId = "fixture-model" };
        internal IModelProviderRuntime CreateRuntime() => new Runtime(this);
        internal Script Add(bool holdCancellation, bool lifecycle)
        {
            var script = new Script(lifecycle);
            if (!holdCancellation) script.ReleaseCancellation.TrySetResult();
            lock (_gate)
            {
                _scripts.Add(script);
                if (_cleaning) { script.Release.TrySetResult(); script.ReleaseCancellation.TrySetResult(); }
            }
            return script;
        }
        internal void ReleaseAll()
        {
            ReleaseAbort.TrySetResult();
            lock (_gate)
            {
                _cleaning = true;
                foreach (var script in _scripts) { script.Release.TrySetResult(); script.ReleaseCancellation.TrySetResult(); }
            }
        }
        private sealed class Runtime(Provider owner) : IModelProviderSessionRuntime
        {
            public ModelProviderDescriptor Descriptor => owner.Descriptor;
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Probe forbidden.");
            public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("Executor forbidden.");
            public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default) => Create(options.SessionId!, options);
            public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default) => Create(sessionId, options);
            private Task<IAgentSession> Create(string id, AgentSessionCreateOptions options)
            {
                var session = new Session(owner, id, options);
                lock (owner._gate) owner._sessions.Add(session);
                return Task.FromResult<IAgentSession>(session);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        internal sealed class Session(Provider owner, string id, AgentSessionCreateOptions options) : IAgentSession
        {
            private Action<AgentEvent>? _handler;
            internal AgentSessionCreateOptions Options => options;
            public ModelProviderId ProviderId => owner.Descriptor.ProviderId;
            public string SessionId => id;
            public string? WorkspacePath => options.WorkingDirectory;
            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            { await Task.CompletedTask; yield break; }
            public IDisposable Subscribe(Action<AgentEvent> handler) { _handler = handler; return new Subscription(this); }
            internal void Emit(AgentSessionUpdateKind kind, AgentRunId? run, string marker) => _handler?.Invoke(new AgentSessionUpdateEvent(ProviderId, id, DateTimeOffset.UtcNow, run, kind, marker));
            public async Task<AgentRunId> SendAsync(AgentSendOptions send, CancellationToken cancellationToken = default)
            {
                Script script;
                lock (owner._gate) script = owner._scripts[owner._sends++];
                Interlocked.Increment(ref owner._active);
                using var source = new CancellationTokenSource();
                // Retain and join the original forwarding registration before destination disposal.
                var forwarding = cancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), source);
                var registration = source.Token.Register(() => { script.CancellationEntered.TrySetResult(); script.ReleaseCancellation.Task.GetAwaiter().GetResult(); });
                try
                {
                    script.Options = send; script.Token = source.Token;
                    if (script.Lifecycle && send.RunLifecycle is { } lifecycle)
                        await lifecycle.StartedAsync(script.RunId, script.InvalidBinding ? CancellationToken.None : source.Token);
                    script.Started.TrySetResult();
                    await script.Release.Task;
                    Emit(AgentSessionUpdateKind.Idle, null, "script-complete");
                    source.Token.ThrowIfCancellationRequested();
                    if (script.Failure is { } failure) throw failure;
                    return script.RunId;
                }
                finally
                {
                    try
                    {
                        if (script.Lifecycle && send.RunLifecycle is { } lifecycle) await lifecycle.ClosingAsync(script.RunId);
                    }
                    finally
                    {
                        var registrationDisposal = registration.DisposeAsync().AsTask();
                        var forwardingDisposal = forwarding.DisposeAsync().AsTask();
                        await Task.WhenAll(registrationDisposal, forwardingDisposal);
                        script.RegistrationDisposed = true;
                        script.ForwardingDisposed = true;
                        Interlocked.Decrement(ref owner._active);
                    }
                }
            }
            public Task AbortAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._aborts);
                owner.AbortEntered.TrySetResult();
                return owner.HoldAbort ? owner.ReleaseAbort.Task : Task.CompletedTask;
            }
            public Task<AgentRunId> SteerAsync(AgentSteerOptions steer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Steer forbidden.");
            public Task CompactAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Compact forbidden.");
            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
            public ValueTask DisposeAsync() { if (Volatile.Read(ref owner._active) != 0) Interlocked.Increment(ref owner._earlyDisposals); return ValueTask.CompletedTask; }
            private sealed class Subscription(Session session) : IDisposable { public void Dispose() => session._handler = null; }
        }
    }
}
