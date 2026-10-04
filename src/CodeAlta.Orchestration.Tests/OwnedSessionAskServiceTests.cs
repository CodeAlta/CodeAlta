using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Actual private queue and action ownership with inert command admission.</summary>
[TestClass]
public sealed class OwnedSessionAskServiceTests
{
    [TestMethod]
    public async Task Answer_ClaimsOnceAndRetainsPositiveEvidenceAfterReceiptFailure()
    {
        var receipt = new OwnedSessionCommandReceipt("answer", OwnedSessionCommandKind.Send, "session");
        OwnedAskSubmission? submission = null;
        OwnedTextSendRequest? sent = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new OwnedSessionAskService(true, (request, context) =>
        {
            sent = request;
            submission = context;
            entered.TrySetResult();
            return new(OwnedSessionCommandAdmissionKind.Accepted, receipt);
        });
        var execution = await Produce(owner);
        var handle = owner.List("session").Head!.Handle;
        var action = new OwnedAskAction(Guid.NewGuid(), handle, [new AltaAskAnswer { QuestionIndex = 0, FreeformText = "answer" }]);
        var first = owner.AnswerAsync(action, default);
        Task<OwnedAskDisposition>? duplicate = null;
        try
        {
            duplicate = owner.AnswerAsync(action, default);
            Assert.AreSame(first, duplicate);
            Assert.AreEqual("submitting", owner.Observe(action.ActionId, handle)!.Status);
            // The launch gate is released by AnswerAsync, but readiness must not assume a scheduling race.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(submission);
            Assert.AreEqual(handle.AskId, submission.AskId);
            Assert.AreEqual("session", sent!.SessionId);
            submission.RecordRunReturned(new("real-run"));
            receipt.Complete(new(OwnedSessionCommandOutcome.Failed, Code: "publication_failed"));
            var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("admitted", result.Status);
            Assert.AreEqual("real-run", result.RunId);
            Assert.IsNull(owner.List("session").Head);
        }
        finally
        {
            receipt.Complete(new(OwnedSessionCommandOutcome.Cancelled));
            execution.Close();
            owner.CloseAdmission();
            await Join(owner, first, duplicate ?? Task.CompletedTask);
        }
    }

    [TestMethod]
    public async Task NonAdmission_RotatesHandleAndCancelCannotUseOldGeneration()
    {
        var owner = new OwnedSessionAskService(true, static (_, _) => new(OwnedSessionCommandAdmissionKind.Busy));
        var execution = await Produce(owner);
        Task? answer = null;
        Task? cancel = null;
        try
        {
            var old = owner.List("session").Head!.Handle;
            var pending = owner.AnswerAsync(new(Guid.NewGuid(), old, [new() { QuestionIndex = 0, FreeformText = "a" }]), default);
            answer = pending;
            Assert.AreEqual("not_admitted", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            var fresh = owner.List("session").Head!.Handle;
            Assert.AreEqual(old.ResponseGeneration + 1, fresh.ResponseGeneration);
            var rejected = owner.CancelAsync(new(Guid.NewGuid(), old, []), default);
            cancel = rejected;
            Assert.AreEqual("rejected", (await rejected.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            var accepted = owner.CancelAsync(new(Guid.NewGuid(), fresh, []), default);
            cancel = accepted;
            Assert.AreEqual("cancelled", (await accepted.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally { execution.Close(); owner.CloseAdmission(); await Join(owner, answer ?? Task.CompletedTask, cancel ?? Task.CompletedTask); }
    }

    [TestMethod]
    public async Task Producer_RequiresLifecycleAndClosedHandlerNeverRebinds()
    {
        var owner = new OwnedSessionAskService(true, static (_, _) => throw new AssertFailedException("No send expected."));
        var a = owner.CreateExecution(Guid.NewGuid(), "session", default);
        var invocation = Invocation();
        Assert.IsFalse((await a.Tool.Handler(invocation, default)).Success);
        a.Bind(Guid.NewGuid(), 1, new("fixture"));
        Assert.IsFalse((await a.Tool.Handler(invocation, default)).Success); // Provider ignored StartedAsync.
        Assert.IsNull(owner.List("session").Head);
        await a.StartedAsync(new("run-a"), default);
        Assert.IsTrue((await a.Tool.Handler(invocation, default)).Success);
        Assert.IsFalse((await a.Tool.Handler(invocation, default)).Success);
        a.Close();
        var b = owner.CreateExecution(Guid.NewGuid(), "session", default);
        b.Bind(Guid.NewGuid(), 1, new("fixture"));
        await b.StartedAsync(new("run-b"), default);
        try
        {
            Assert.IsFalse((await a.Tool.Handler(invocation, default)).Success);
            Assert.IsTrue((await b.Tool.Handler(invocation, default)).Success);
        }
        finally { a.Close(); b.Close(); owner.CloseAdmission(); await Join(owner); }
    }

    [TestMethod]
    public async Task SessionTool_AsksThroughTheOpenSendOfItsSessionOncePerSend()
    {
        var owner = new OwnedSessionAskService(true, static (_, _) => throw new AssertFailedException("No send expected."));
        var adapter = new OwnedSessionAltaAskService(owner);
        var caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session" };
        var request = new AltaAskRequest { Questions = [new() { Title = "Decision", Question = "How?", Freeform = new() }] };
        // No send of the session is running yet, then one that the provider has not started.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => adapter.QueueAsync(request, "session", caller));
        var send = owner.CreateExecution(Guid.NewGuid(), "session", default);
        send.Bind(Guid.NewGuid(), 1, new("fixture"));
        Assert.IsNull(owner.QueueFromSession("session", request));
        // A session with its own alta tool gets no second tool of that name; the lifecycle is still joined.
        var composed = send.Compose(new AgentSendOptions { Input = new AgentInput([]) }, includeTool: false);
        Assert.IsNull(composed.AdditionalTools);
        Assert.IsNotNull(composed.RunLifecycle);
        await send.StartedAsync(new("run"), default);
        try
        {
            Assert.IsNull(owner.QueueFromSession("other", request), "Another session has no open send.");
            var queued = await adapter.QueueAsync(request, " session ", caller);
            Assert.AreEqual("session", queued.SessionId);
            Assert.AreEqual(queued.AskId, owner.List("session").Head!.Handle.AskId);
            Assert.IsNull(owner.QueueFromSession("session", request), "One ask per send.");
            Assert.IsFalse((await send.Tool.Handler(Invocation(), default)).Success, "The per-send tool shares that one ask.");
            Assert.Throws<ArgumentException>(() => owner.QueueFromSession("session", request with { File = new() { Path = "plan.md" } }));
            Assert.Throws<ArgumentException>(() => owner.QueueFromSession("session", new AltaAskRequest()));
            Assert.AreEqual(0, adapter.GetPending("session").Count);
            Assert.IsNull(adapter.Peek("session"));
        }
        finally { send.Close(); owner.CloseAdmission(); await Join(owner); }
        Assert.IsNull(owner.QueueFromSession("session", request), "A closed owner admits nothing.");
    }

    [TestMethod]
    public async Task AskCapacity_DoesNotPreventLaterSendLifecycleBinding()
    {
        var owner = new OwnedSessionAskService(true, static (_, _) => throw new AssertFailedException("No dispatch."));
        OwnedSessionAskExecution? extra = null;
        try
        {
            for (var i = 0; i < 256; i++) await Produce(owner);
            extra = owner.CreateExecution(Guid.NewGuid(), "session", default);
            extra.Bind(Guid.NewGuid(), 1, new("fixture"));
            await extra.StartedAsync(new("later-send"), default);
            Assert.IsFalse((await extra.Tool.Handler(Invocation(), default)).Success);
            Assert.IsTrue(owner.List("session").HasMore);
        }
        finally { extra?.Close(); owner.CloseAdmission(); await Join(owner); }
    }

    [TestMethod]
    public async Task ActionCapacity_RetainsReplayAndRejectsBeforeQueueMutation()
    {
        var owner = new OwnedSessionAskService(true, static (_, _) => new(OwnedSessionCommandAdmissionKind.Busy));
        var originals = new List<Task>();
        OwnedAskAction? first = null;
        try
        {
            await Produce(owner);
            for (var i = 0; i < 256; i++)
            {
                var action = new OwnedAskAction(Guid.NewGuid(), owner.List("session").Head!.Handle,
                    [new() { QuestionIndex = 0, FreeformText = "answer" }]);
                first ??= action;
                var original = owner.AnswerAsync(action, default);
                originals.Add(original);
                Assert.AreEqual("not_admitted", (await original.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            }
            var retained = owner.AnswerAsync(first!, default);
            Assert.AreSame(originals[0], retained);
            var head = owner.List("session").Head!;
            var rejected = owner.CancelAsync(new(Guid.NewGuid(), head.Handle, []), default);
            originals.Add(rejected);
            Assert.AreEqual("capacity", (await rejected).Status);
            Assert.AreEqual(head, owner.List("session").Head);
        }
        finally { owner.CloseAdmission(); await Join(owner, originals.ToArray()); }
    }

    [TestMethod]
    public async Task ClaimedAnswer_CannotCancelOrForgeOrigin_AndUncertaintySurvivesClose()
    {
        var receipt = new OwnedSessionCommandReceipt("answer", OwnedSessionCommandKind.Send, "session");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new OwnedSessionAskService(true, (_, _) => { entered.TrySetResult(); return new(OwnedSessionCommandAdmissionKind.Accepted, receipt); });
        var originals = new List<Task>();
        try
        {
            await Produce(owner);
            var handle = owner.List("session").Head!.Handle;
            Assert.Throws<OperationCanceledException>(() => owner.CancelAsync(new(Guid.NewGuid(), handle, []), new CancellationToken(canceled: true)));
            Assert.Throws<ArgumentException>(() => owner.AnswerAsync(new(Guid.NewGuid(), handle,
                [new() { QuestionIndex = 0, FreeformText = new string('x', 8193) }]), default));
            var forged = owner.CancelAsync(new(Guid.NewGuid(), handle with { RuntimeInstanceId = Guid.NewGuid() }, []), default);
            originals.Add(forged);
            Assert.AreEqual("rejected", (await forged).Status);
            var action = new OwnedAskAction(Guid.NewGuid(), handle, [new() { QuestionIndex = 0, FreeformText = "answer" }]);
            var answer = owner.AnswerAsync(action, default);
            originals.Add(answer);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancel = owner.CancelAsync(new(Guid.NewGuid(), handle, []), default);
            originals.Add(cancel);
            Assert.AreEqual("rejected", (await cancel.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            owner.CloseAdmission(); // Must not wait for this answer's dependent receipt.
            Assert.IsFalse(answer.IsCompleted);
            receipt.Complete(new(OwnedSessionCommandOutcome.Failed));
            Assert.AreEqual("indeterminate", (await answer.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.AreEqual("indeterminate", owner.List("session").Head!.State);
            Assert.AreSame(answer, owner.AnswerAsync(action, default));
        }
        finally
        {
            receipt.Complete(new(OwnedSessionCommandOutcome.Cancelled));
            owner.CloseAdmission();
            await Join(owner, originals.ToArray());
        }
    }

    [TestMethod]
    public void ProducerValidation_RejectsUnknownFileTargetMalformedAndExcessiveInput()
    {
        foreach (var json in new[]
        {
            "{\"file\":null,\"questions\":[]}", "{\"sessionId\":\"other\",\"questions\":[]}",
            "{\"questions\":[{\"title\":\"t\",\"question\":\"q\",\"freeform\":{},\"unknown\":1}]}",
            new string(' ', 65537), "{\"questions\":[{\"title\":\"\\ud800\",\"question\":\"q\",\"freeform\":{}}]}"
        }) Assert.Throws<ArgumentException>(() => OwnedSessionAskTool.ParseRequest(json));

        foreach (var outer in new[]
        {
            "{\"args\":[\"send\",\"--stdin\"],\"stdin\":\"{}\"}",
            "{\"args\":[\"ask\",\"--stdin\"],\"stdin\":\"{}\",\"cwd\":\"other\"}",
            "{\"args\":[\"ask\",\"--session\",\"other\",\"--stdin\"],\"stdin\":\"{}\"}",
        })
        {
            using var document = JsonDocument.Parse(outer);
            Assert.Throws<ArgumentException>(() => OwnedSessionAskTool.ParseInvocation(new(new("fixture"), "session", "call", "alta", document.RootElement)));
        }
    }

    internal static AgentToolInvocation Invocation()
    {
        using var json = JsonDocument.Parse("{\"args\":[\"ask\",\"--stdin\"],\"stdin\":\"{\\\"questions\\\":[{\\\"title\\\":\\\"Question\\\",\\\"question\\\":\\\"Continue?\\\",\\\"freeform\\\":{}}]}\"}");
        return new(new("fixture"), "session", "call", "alta", json.RootElement.Clone());
    }

    internal static async Task<OwnedSessionAskExecution> Produce(OwnedSessionAskService owner)
    {
        var execution = owner.CreateExecution(Guid.NewGuid(), "session", default);
        execution.Bind(Guid.NewGuid(), 1, new("fixture"));
        await execution.StartedAsync(new("producer-run"), default);
        Assert.IsTrue((await execution.Tool.Handler(Invocation(), default)).Success);
        execution.Close();
        return execution;
    }

    private static async Task Join(OwnedSessionAskService owner, params Task[] originals)
    {
        var drain = owner.DrainAsync();
        var all = Task.WhenAll(originals.Append(drain));
        try { await all.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { ex.Data["RetainedAskOwner"] = owner; ex.Data["OriginalJoin"] = all; throw; }
    }
}
