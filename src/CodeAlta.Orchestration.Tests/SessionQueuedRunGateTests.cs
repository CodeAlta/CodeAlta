using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A prompt that waits in the queue of a session starts a turn later, when the session is free: the gate of the runtime (the
/// budgets of a node) admits it then, not when it was queued. One the gate holds starts once it lets it; one it refuses fails
/// without calling the model, and the queue goes on.
/// </summary>
[TestClass]
public sealed class SessionQueuedRunGateTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    [TestMethod]
    public async Task AQueuedPrompt_IsAdmittedWhenItsTurnStarts_HeldUntilTheGateLetsIt()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Session");
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Script(session.SessionId, ScriptedTurnProvider.WaitThenReply(running.Task, "First.", started), ScriptedTurnProvider.Reply("Queued."));
        var asked = new List<QueuedRunAdmission>();
        var asking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letIt = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RuntimeService.QueuedRunGate = (admission, _) =>
        {
            lock (asked) asked.Add(admission);
            asking.TrySetResult();
            return letIt.Task;
        };

        var first = host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("first") });
        await started.Task.WithinAsync(Hang);
        var queued = await host.RuntimeService.QueuePromptAsync(session, "queued follow-up", "queue", submittedBy: null);
        Assert.AreEqual(0, asked.Count, "a prompt is admitted when its turn starts, not when it is queued");

        // The first turn ends: the queued prompt is asked for, and waits.
        running.SetResult();
        await asking.Task.WithinAsync(Hang);
        Assert.AreEqual((session.SessionId, queued.QueueItemId), (asked[0].SessionId, asked[0].QueueItemId));
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.AreEqual(1, provider.RequestsOf(session.SessionId).Length, "nothing of the queued prompt runs while it waits");

        letIt.SetResult(null);
        await first.WithinAsync(Hang);
        await WaitUntilAsync(() => provider.RequestsOf(session.SessionId).Length == 2);
        Assert.AreEqual(1, asked.Count, "a queued prompt is admitted once");
    }

    [TestMethod]
    public async Task AQueuedPromptTheGateRefuses_Fails_WithoutCallingTheModel_AndTheQueueGoesOn()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Session");
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Script(session.SessionId, ScriptedTurnProvider.WaitThenReply(running.Task, "First.", started), ScriptedTurnProvider.Reply("Second queued."));
        string? refusedId = null;
        host.RuntimeService.QueuedRunGate = (admission, _) => Task.FromResult(admission.QueueItemId == refusedId ? "The budget 'Home' is reached: the run does not start." : null);

        var first = host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("first") });
        await started.Task.WithinAsync(Hang);
        var refused = await host.RuntimeService.QueuePromptAsync(session, "refused follow-up", "queue", submittedBy: null);
        refusedId = refused.QueueItemId;
        await host.RuntimeService.QueuePromptAsync(session, "admitted follow-up", "queue", submittedBy: null);
        running.SetResult();
        await first.WithinAsync(Hang);

        await WaitUntilAsync(() => provider.RequestsOf(session.SessionId).Length == 2);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        var requests = provider.RequestsOf(session.SessionId);
        Assert.AreEqual(2, requests.Length, "the refused prompt never reaches the model");
        Assert.IsFalse(requests.Any(static request => LastPrompt(request).Contains("refused follow-up", StringComparison.Ordinal)));
        Assert.IsTrue(LastPrompt(requests[1]).Contains("admitted follow-up", StringComparison.Ordinal), "the queue goes on with the next prompt");
    }

    private static string LastPrompt(CodeAlta.Agent.Runtime.AgentTurnRequest request)
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
