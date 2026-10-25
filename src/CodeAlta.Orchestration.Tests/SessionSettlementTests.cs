using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A session and the sessions it started settle only when nothing is left to do: the answer of a child told to its
/// parent, a queued prompt, are part of the run. An abort of the tree stops them all and records why.
/// </summary>
[TestClass]
public sealed class SessionSettlementTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    [TestMethod]
    public async Task WhenSettled_WaitsForTheChild_TheParentNotification_AndTheQueuedPrompt()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var parent = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Parent");
        var childId = Guid.CreateVersion7().ToString();
        var childGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? childSend = null;
        provider.Script(parent.SessionId,
            ScriptedTurnProvider.DoThenReply(async () =>
            {
                // What `alta session create` and `alta session send` do from a turn of the parent.
                var child = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Child",
                    parentSessionId: parent.SessionId, createdBy: null, worktreeDirectory: null, spaceId: null, sessionId: childId);
                childSend = host.RuntimeService.SendAsync(child, options, new AgentSendOptions { Input = AgentInput.Text("child work") });
                await host.RuntimeService.QueuePromptAsync(parent, "queued follow-up", "send", submittedBy: null);
            }, "Started the child."),
            ScriptedTurnProvider.Reply("Did the follow-up."),
            ScriptedTurnProvider.Reply("Read the child."));
        provider.Script(childId, ScriptedTurnProvider.WaitThenReply(childGate.Task, "Child result.", childStarted));

        await host.RuntimeService.SendAsync(parent, options, new AgentSendOptions { Input = AgentInput.Text("start") });
        var settled = host.RuntimeService.WhenSettledAsync(parent.SessionId);
        await childStarted.Task.WaitAsync(Hang);
        await WaitUntilAsync(() => provider.RequestsOf(parent.SessionId).Length == 2 && settled.IsCompleted is false);
        CollectionAssert.AreEquivalent(new[] { parent.SessionId, childId }, host.RuntimeService.GetSessionTree(parent.SessionId).ToArray());

        // The parent answered twice, its queue is empty; its child still runs.
        await Task.WhenAny(settled, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.IsFalse(settled.IsCompleted, "The run is not over while a child it started runs.");

        childGate.SetResult();
        await settled.WaitAsync(Hang);

        Assert.IsNotNull(childSend);
        await childSend.WaitAsync(Hang);
        var parentRequests = provider.RequestsOf(parent.SessionId);
        Assert.AreEqual(3, parentRequests.Length, "The answer of the child started a third run of the parent before the tree settled.");
        StringAssert.Contains(TextOf(parentRequests[^1]), "Child result.");
        var history = await host.RuntimeService.TryReadStoredHistoryAsync(parent, null);
        Assert.AreEqual("Read the child.", history!.OfType<AgentContentCompletedEvent>().Last(static item => item.Kind == AgentContentKind.Assistant).Content);
    }

    [TestMethod]
    public async Task WhenSettled_OfAnIdleSession_CompletesAtOnce()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Idle");

        await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("hello") });

        await host.RuntimeService.WhenSettledAsync(session.SessionId).WaitAsync(Hang);
        await host.RuntimeService.WhenSettledAsync(Guid.NewGuid().ToString()).WaitAsync(Hang);
    }

    [TestMethod]
    public async Task AbortTree_StopsTheRootAndItsChild_StartsNothingAfter_AndRecordsTheReason()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var parent = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Parent");
        var child = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Child", parent.SessionId, createdBy: null);
        var never = new TaskCompletionSource();
        var parentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Script(parent.SessionId, ScriptedTurnProvider.WaitThenReply(never.Task, "never", parentStarted));
        provider.Script(child.SessionId, ScriptedTurnProvider.WaitThenReply(never.Task, "never", childStarted));
        var parentSend = host.RuntimeService.SendAsync(parent, options, new AgentSendOptions { Input = AgentInput.Text("work") });
        var childSend = host.RuntimeService.SendAsync(child, options, new AgentSendOptions { Input = AgentInput.Text("child work") });
        await Task.WhenAll(parentStarted.Task, childStarted.Task).WaitAsync(Hang);

        var tree = await host.RuntimeService.AbortTreeAsync(parent.SessionId, "timeout").WaitAsync(Hang);

        CollectionAssert.AreEquivalent(new[] { parent.SessionId, child.SessionId }, tree.ToArray());
        await Task.WhenAll(parentSend, childSend).ContinueWith(static _ => { }).WaitAsync(Hang);
        await host.RuntimeService.WhenSettledAsync(parent.SessionId).WaitAsync(Hang);
        Assert.AreEqual(1, provider.RequestsOf(parent.SessionId).Length, "The failure of the aborted child starts no run of its parent.");
        Assert.AreEqual("timeout", host.RuntimeService.GetAbortReason(parent.SessionId));
        foreach (var session in new[] { parent, child })
        {
            var history = await host.RuntimeService.TryReadStoredHistoryAsync(session, null);
            var record = history!.OfType<AgentSessionUpdateEvent>().LastOrDefault(static update => update.Details is { } details
                && details.TryGetProperty(SessionRuntimeService.AbortReasonDetail, out _));
            Assert.IsNotNull(record, "The journal of " + session.Title + " records the abort.");
            Assert.AreEqual("timeout", record.Details!.Value.GetProperty(SessionRuntimeService.AbortReasonDetail).GetString());
        }

        // A prompt sent again lifts the hold.
        await host.RuntimeService.SendAsync(parent, options, new AgentSendOptions { Input = AgentInput.Text("again") });
        Assert.IsNull(host.RuntimeService.GetAbortReason(parent.SessionId));
    }

    private static string TextOf(AgentTurnRequest request)
        => string.Join("\n", request.Conversation.SelectMany(static message => message.Parts).OfType<AgentMessagePart.Text>().Select(static part => part.Value));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Hang;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) Assert.Fail("The condition was never met.");
            await Task.Delay(20);
        }
    }
}
