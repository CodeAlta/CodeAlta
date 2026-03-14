using CodeAlta.Agent;
using CodeAlta.Agent.Codex;
using CodeAlta.CodexSdk;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexAgentSessionTests
{
    [TestMethod]
    public void IsHistoryUnavailableBeforeFirstMessage_MatchesExpectedCodexError()
    {
        var exception = new JsonRpcException(
            -32603,
            "thread 019ced98-d6cb-73c0-89db-af6942d10c78 is not materialized yet; includeTurns is unavailable before first user message");

        Assert.IsTrue(CodexAgentSession.IsHistoryUnavailableBeforeFirstMessage(exception));
    }

    [TestMethod]
    public async Task HandleNotification_MapsCommentaryAgentMessageDeltasToReasoning()
    {
        await using var backend = new CodexAgentBackend(new CodexAgentBackendOptions());
        var session = new CodexAgentSession(
            backend,
            "thread-1",
            workingDirectory: null,
            model: null,
            reasoningEffort: null,
            sandboxMode: null,
            permissionHandler: static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
            userInputHandler: null);

        var events = new List<AgentEvent>();
        using var subscription = session.Subscribe(events.Add);

        session.HandleNotification(
            new CodexNotification.ItemStarted(
                new ItemStartedNotification
                {
                    ThreadId = "thread-1",
                    TurnId = "turn-1",
                    Item = new ThreadItem.AgentMessageThreadItem
                    {
                        Id = "item-1",
                        Phase = MessagePhase.Commentary,
                        Text = string.Empty
                    }
                }));

        session.HandleNotification(
            new CodexNotification.AgentMessageDelta(
                new AgentMessageDeltaNotification
                {
                    ThreadId = "thread-1",
                    TurnId = "turn-1",
                    ItemId = "item-1",
                    Delta = "Inspecting the repo."
                }));

        var delta = events.OfType<AgentContentDeltaEvent>().Single();
        Assert.AreEqual(AgentContentKind.Reasoning, delta.Kind);
    }
}
