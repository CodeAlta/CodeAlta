using CodeAlta.Agent;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.CopilotCli;
using GitHub.Copilot.SDK;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CopilotAgentSessionTests
{
    [TestMethod]
    public void ProjectSessionEvents_SuppressesPrematureIdleUntilFinalAnswerTurnEnds()
    {
        var tracker = new CopilotAgentSession.CopilotInteractionTracker();
        const string sessionId = "session-1";
        const string interactionId = "interaction-1";

        var userEvents = CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new UserMessageEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-14T13:50:00Z"),
                Data = new UserMessageData
                {
                    Content = "Tell me about Tomlyn.",
                    InteractionId = interactionId
                }
            },
            tracker);

        Assert.AreEqual(1, userEvents.Count);
        Assert.IsInstanceOfType<AgentContentCompletedEvent>(userEvents[0]);

        var turnStartEvents = CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new AssistantTurnStartEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-14T13:50:01Z"),
                Data = new AssistantTurnStartData
                {
                    TurnId = "0",
                    InteractionId = interactionId
                }
            },
            tracker);

        Assert.AreEqual(1, turnStartEvents.Count);
        Assert.IsInstanceOfType<AgentActivityEvent>(turnStartEvents[0]);

        var prematureIdleEvents = CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new SessionIdleEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-14T13:50:10Z"),
                Data = new SessionIdleData()
            },
            tracker);

        Assert.AreEqual(0, prematureIdleEvents.Count);

        var intermediateTurnEndEvents = CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new AssistantTurnEndEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-14T13:50:10Z"),
                Data = new AssistantTurnEndData
                {
                    TurnId = "0"
                }
            },
            tracker);

        Assert.AreEqual(1, intermediateTurnEndEvents.Count);
        Assert.IsInstanceOfType<AgentActivityEvent>(intermediateTurnEndEvents[0]);

        var finalAnswerEvents = CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new AssistantMessageEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-14T13:50:17Z"),
                Data = new AssistantMessageData
                {
                    MessageId = "message-1",
                    InteractionId = interactionId,
                    Phase = "final_answer",
                    Content = "Tomlyn is a .NET TOML library.",
                    ReasoningText = "Considering project structure"
                }
            },
            tracker);

        Assert.AreEqual(2, finalAnswerEvents.Count);
        Assert.AreEqual(AgentContentKind.Reasoning, ((AgentContentCompletedEvent)finalAnswerEvents[0]).Kind);
        Assert.AreEqual(AgentContentKind.Assistant, ((AgentContentCompletedEvent)finalAnswerEvents[1]).Kind);

        var completionEvents = CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new AssistantTurnEndEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-14T13:50:17Z"),
                Data = new AssistantTurnEndData
                {
                    TurnId = "2"
                }
            },
            tracker);

        Assert.AreEqual(2, completionEvents.Count);
        Assert.IsInstanceOfType<AgentActivityEvent>(completionEvents[0]);
        Assert.IsInstanceOfType<AgentSessionUpdateEvent>(completionEvents[1]);
        Assert.AreEqual(AgentSessionUpdateKind.Idle, ((AgentSessionUpdateEvent)completionEvents[1]).Kind);
    }

    [TestMethod]
    public void ProjectSessionEvents_SuppressesEmbeddedReasoningWhenExplicitReasoningAlreadyExists()
    {
        var tracker = new CopilotAgentSession.CopilotInteractionTracker();
        const string sessionId = "session-1";
        const string interactionId = "interaction-1";

        CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new UserMessageEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-14T13:50:00Z"),
                Data = new UserMessageData
                {
                    Content = "Tell me about Tomlyn.",
                    InteractionId = interactionId
                }
            },
            tracker);

        CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new AssistantTurnStartEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-14T13:50:01Z"),
                Data = new AssistantTurnStartData
                {
                    TurnId = "0",
                    InteractionId = interactionId
                }
            },
            tracker);

        CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new AssistantReasoningEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-14T13:50:02Z"),
                Data = new AssistantReasoningData
                {
                    ReasoningId = "reasoning-1",
                    Content = "Explicit reasoning"
                }
            },
            tracker);

        var finalAnswerEvents = CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new AssistantMessageEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-14T13:50:17Z"),
                Data = new AssistantMessageData
                {
                    MessageId = "message-1",
                    InteractionId = interactionId,
                    Phase = "final_answer",
                    Content = "Tomlyn is a .NET TOML library.",
                    ReasoningText = "Embedded reasoning"
                }
            },
            tracker);

        Assert.AreEqual(1, finalAnswerEvents.Count);
        Assert.AreEqual(AgentContentKind.Assistant, ((AgentContentCompletedEvent)finalAnswerEvents[0]).Kind);
    }

    [TestMethod]
    public void ProjectSessionEvents_SynthesizesIdleWhenPhaseLessAssistantMessageEndsTurn()
    {
        var tracker = new CopilotAgentSession.CopilotInteractionTracker();
        const string sessionId = "session-1";
        const string interactionId = "interaction-1";

        CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new UserMessageEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-20T06:20:50Z"),
                Data = new UserMessageData
                {
                    Content = "Write the final summary.",
                    InteractionId = interactionId
                }
            },
            tracker);

        CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new AssistantTurnStartEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-20T06:20:51Z"),
                Data = new AssistantTurnStartData
                {
                    TurnId = "29",
                    InteractionId = interactionId
                }
            },
            tracker);

        var finalMessageEvents = CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new AssistantMessageEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-20T06:20:59Z"),
                Data = new AssistantMessageData
                {
                    MessageId = "message-29",
                    InteractionId = interactionId,
                    Content = "The report is written to report_codealta_app_architecture.md."
                }
            },
            tracker);

        Assert.AreEqual(1, finalMessageEvents.Count);
        Assert.AreEqual(AgentContentKind.Assistant, ((AgentContentCompletedEvent)finalMessageEvents[0]).Kind);

        var completionEvents = CopilotAgentSession.ProjectSessionEvents(
            sessionId,
            new AssistantTurnEndEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-20T06:20:59Z"),
                Data = new AssistantTurnEndData
                {
                    TurnId = "29"
                }
            },
            tracker);

        Assert.AreEqual(2, completionEvents.Count);
        Assert.IsInstanceOfType<AgentActivityEvent>(completionEvents[0]);
        Assert.IsInstanceOfType<AgentSessionUpdateEvent>(completionEvents[1]);
        Assert.AreEqual(AgentSessionUpdateKind.Idle, ((AgentSessionUpdateEvent)completionEvents[1]).Kind);
    }

    [TestMethod]
    public void ShouldRefreshQuotaForEvent_TriggersOnTurnBoundariesAndPromptReady()
    {
        Assert.IsTrue(CopilotAgentSession.ShouldRefreshQuotaForEvent(
            new AssistantTurnStartEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-19T07:00:00Z"),
                Data = new AssistantTurnStartData
                {
                    TurnId = "1",
                    InteractionId = "interaction-1"
                }
            }));
        Assert.IsTrue(CopilotAgentSession.ShouldRefreshQuotaForEvent(
            new AssistantTurnEndEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-19T07:00:01Z"),
                Data = new AssistantTurnEndData
                {
                    TurnId = "1"
                }
            }));
        Assert.IsTrue(CopilotAgentSession.ShouldRefreshQuotaForEvent(
            new SessionIdleEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-19T07:00:02Z"),
                Data = new SessionIdleData()
            }));
        Assert.IsFalse(CopilotAgentSession.ShouldRefreshQuotaForEvent(
            new UserMessageEvent
            {
                Timestamp = DateTimeOffset.Parse("2026-03-19T07:00:03Z"),
                Data = new UserMessageData
                {
                    Content = "hello"
                }
            }));
    }
}
