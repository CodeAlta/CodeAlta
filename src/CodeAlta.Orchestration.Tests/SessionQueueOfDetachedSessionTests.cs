using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A prompt queued for a session that is idle is sent at once, also when the runtime detached it (a session nobody shows
/// is detached once it has had nothing to do for a while): the owner attaches it again with what it runs with, and its
/// turn starts without waiting for another event of the session.
/// </summary>
[TestClass]
public sealed class SessionQueueOfDetachedSessionTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    [TestMethod]
    public async Task APromptQueuedForAnIdleDetachedSession_StartsItsTurnAtOnce()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var session = await DetachedAfterOneTurnAsync(host, provider);

        await host.RuntimeService.QueuePromptAsync(session, "queued follow-up", "queue", submittedBy: null);

        await WaitUntilAsync(() => provider.RequestsOf(session.SessionId).Length == 2);
        StringAssert.Contains(LastPrompt(provider.RequestsOf(session.SessionId)[1]), "queued follow-up");
        await host.RuntimeService.WhenSettledAsync(session.SessionId).WithinAsync(Hang);
        Assert.AreEqual(0, (await host.RuntimeService.GetQueuedPromptsAsync(session)).Count, "nothing waits in the queue any more");
    }

    // The answer of a child for a parent nobody shows: it is queued, as for any idle parent, and the parent takes it at once.
    [TestMethod]
    public async Task TheAnswerOfAChild_ForAnIdleDetachedParent_StartsItsTurnAtOnce()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var parent = await DetachedAfterOneTurnAsync(host, provider);

        Assert.IsTrue(await host.RuntimeService.DeliverRemoteChildNotificationAsync(new RemoteChildNotification("child-1", null, "mac", parent.SessionId,
            "answer", "The iOS tests pass.", null, "content-1")).WithinAsync(Hang));

        await WaitUntilAsync(() => provider.RequestsOf(parent.SessionId).Length == 2);
        StringAssert.Contains(LastPrompt(provider.RequestsOf(parent.SessionId)[1]), "The iOS tests pass.");
    }

    // A session that ran one turn through the owner, then was detached as a session nobody shows is once idle.
    private static async Task<SessionViewDescriptor> DetachedAfterOneTurnAsync(CodeAltaHost host, ScriptedTurnProvider provider)
    {
        var created = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Session");
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), created.SessionId, "first")).Receipt;
        Assert.IsNotNull(receipt);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await receipt.Completion.WithinAsync(Hang)).Outcome);
        await host.RuntimeService.WhenSettledAsync(created.SessionId).WithinAsync(Hang);
        Assert.IsTrue(await host.RuntimeService.DetachRuntimeSessionAsync(created.SessionId).WithinAsync(Hang), "the session was attached");
        return (await host.RuntimeService.ResolveOwnedSessionAsync(created.SessionId, CancellationToken.None))!;
    }

    private static string LastPrompt(AgentTurnRequest request)
        => request.Conversation.LastOrDefault(static message => message.Role == AgentConversationRole.User) is { } user
            ? string.Join("\n", user.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value)) : string.Empty;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Hang;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("It did not get there in time.");
            await Task.Delay(20);
        }
    }
}
