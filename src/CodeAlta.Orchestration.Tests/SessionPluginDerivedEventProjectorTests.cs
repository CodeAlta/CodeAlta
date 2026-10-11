using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SessionPluginDerivedEventProjectorTests
{
    [TestMethod]
    public async Task ProjectAsync_InvokesApplicableContributionForReplayAndLiveEvents()
    {
        PluginSessionEventProjectionContext? captured = null;
        var contribution = new PluginSessionEventProjectionContribution
        {
            Name = "stats",
            ProjectAsync = (context, _) =>
            {
                captured = context;
                return ValueTask.FromResult<IReadOnlyList<PluginDerivedSessionEvent>>(
                    [new PluginDerivedSessionEvent { EventId = $"{context.SessionId}:stats", Markdown = $"Events: {context.Events.Count}" }]);
            },
        };
        var projector = CreateProjector(contribution);
        var context = CreateContext();
        var events = new[] { CreateAgentEvent() };

        var result = await projector.ProjectAsync(context, events, isReplay: true);

        Assert.AreEqual(1, result.Events.Count);
        Assert.AreEqual("session-1:stats", result.Events[0].EventId);
        Assert.AreEqual("Events: 1", result.Events[0].Markdown);
        Assert.AreEqual(0, result.Diagnostics.Count);
        Assert.IsNotNull(captured);
        Assert.AreEqual("session-1", captured.SessionId);
        Assert.IsTrue(captured.IsReplay);
        Assert.AreEqual(PluginPoint.SessionEventProjection, captured.Handle.Point);
    }

    [TestMethod]
    public async Task ProjectAsync_ReturnsDiagnosticWhenContributionFails()
    {
        var contribution = new PluginSessionEventProjectionContribution
        {
            Name = "broken",
            ProjectAsync = static (_, _) => throw new InvalidOperationException("broken"),
        };
        var projector = CreateProjector(contribution);

        var result = await projector.ProjectAsync(CreateContext(), [CreateAgentEvent()], isReplay: false);

        Assert.AreEqual(0, result.Events.Count);
        Assert.AreEqual(1, result.Diagnostics.Count);
        StringAssert.Contains(result.Diagnostics[0].Message, "broken");
    }

    [TestMethod]
    public async Task ProjectAsync_ReturnsEmptyWhenNoEventsAreProvided()
    {
        var projector = CreateProjector(new PluginSessionEventProjectionContribution
        {
            Name = "stats",
            ProjectAsync = static (_, _) => ValueTask.FromResult<IReadOnlyList<PluginDerivedSessionEvent>>(
                [new PluginDerivedSessionEvent { EventId = "unexpected" }]),
        });

        var result = await projector.ProjectAsync(CreateContext(), [], isReplay: true);

        Assert.AreEqual(0, result.Events.Count);
        Assert.AreEqual(0, result.Diagnostics.Count);
    }

    [TestMethod]
    public async Task ProjectAsync_AllowsStatsPluginToComputeReplayablePerTurnDerivedEvent()
    {
        var contribution = new PluginSessionEventProjectionContribution
        {
            Name = "turn-stats",
            ProjectAsync = static (context, _) =>
            {
                var started = context.Events.OfType<AgentActivityEvent>()
                    .Where(static agentEvent => agentEvent.Kind == AgentActivityKind.Turn && agentEvent.Phase == AgentActivityPhase.Started)
                    .Select(static agentEvent => (DateTimeOffset?)agentEvent.Timestamp)
                    .FirstOrDefault();
                var completed = context.Events.OfType<AgentActivityEvent>()
                    .Where(static agentEvent => agentEvent.Kind == AgentActivityKind.Turn && agentEvent.Phase == AgentActivityPhase.Completed)
                    .Select(static agentEvent => (DateTimeOffset?)agentEvent.Timestamp)
                    .LastOrDefault();
                var assistantCharacters = context.Events.OfType<AgentContentDeltaEvent>()
                    .Where(static agentEvent => agentEvent.Kind == AgentContentKind.Assistant)
                    .Sum(static agentEvent => agentEvent.Delta.Length);
                var toolCalls = context.Events.OfType<AgentActivityEvent>()
                    .Where(static agentEvent => agentEvent.Kind == AgentActivityKind.ToolCall)
                    .ToArray();
                var toolPayloadCharacters = toolCalls.Sum(static agentEvent => agentEvent.Message?.Length ?? 0);
                var elapsed = started is not null && completed is not null
                    ? completed.Value - started.Value
                    : TimeSpan.Zero;

                return ValueTask.FromResult<IReadOnlyList<PluginDerivedSessionEvent>>(
                    [new PluginDerivedSessionEvent
                    {
                        EventId = $"{context.SessionId}:turn-stats:{context.Events[0].RunId}",
                        Markdown = $"Elapsed: {elapsed.TotalSeconds:0}s; assistant chars: {assistantCharacters}; tool calls: {toolCalls.Length}; tool payload chars: {toolPayloadCharacters}",
                    }]);
            },
        };
        var projector = CreateProjector(contribution);
        var runId = new AgentRunId("run-1");
        var startedAt = DateTimeOffset.Parse("2026-05-05T10:00:00Z");
        var liveEvents = CreateStatsEvents(runId, startedAt);
        var replayedEvents = CreateStatsEvents(runId, startedAt);

        var liveResult = await projector.ProjectAsync(CreateContext(), liveEvents, isReplay: false);
        var replayResult = await projector.ProjectAsync(CreateContext(), replayedEvents, isReplay: true);

        Assert.AreEqual(0, liveResult.Diagnostics.Count);
        Assert.AreEqual(0, replayResult.Diagnostics.Count);
        Assert.AreEqual(1, liveResult.Events.Count);
        Assert.AreEqual(liveResult.Events[0].EventId, replayResult.Events[0].EventId);
        Assert.AreEqual(liveResult.Events[0].Markdown, replayResult.Events[0].Markdown);
        Assert.AreEqual("Elapsed: 7s; assistant chars: 11; tool calls: 1; tool payload chars: 13", liveResult.Events[0].Markdown);
    }

    private static SessionPluginDerivedEventProjector CreateProjector(PluginSessionEventProjectionContribution contribution)
    {
        var registry = new PluginContributionRegistry();
        registry.Register(
            new PluginDescriptor
            {
                RuntimeKey = "plugin-1",
                TypeName = "Plugin",
                AssemblyName = "PluginAssembly",
            },
            PluginScope.Global,
            scopeProjectId: null,
            scopeProjectPath: null,
            PluginPoint.SessionEventProjection,
            [contribution],
            activationGeneration: 1);
        return new SessionPluginDerivedEventProjector(new PluginOrchestrationBridge(
            new PluginContributionAdapterService(registry),
            static () => []));
    }

    private static SessionCommandContext CreateContext()
        => new()
        {
            ProjectId = "project-1",
            ProjectPath = "C:/project",
            PromptSessionId = "prompt-session-1",
            ModelProviderId = "provider-1",
            SessionId = "session-1",
        };

    private static AgentEvent CreateAgentEvent()
        => new AgentErrorEvent(
            new ModelProviderId("provider-1"),
            "session-1",
            DateTimeOffset.UtcNow,
            "diagnostic", (Exception?)null);

    private static IReadOnlyList<AgentEvent> CreateStatsEvents(AgentRunId runId, DateTimeOffset startedAt)
        =>
        [
            new AgentActivityEvent(
                new ModelProviderId("provider-1"),
                "session-1",
                startedAt,
                runId,
                AgentActivityKind.Turn,
                AgentActivityPhase.Started,
                "turn-1",
                ParentActivityId: null,
                Name: "turn",
                Message: null),
            new AgentContentDeltaEvent(
                new ModelProviderId("provider-1"),
                "session-1",
                startedAt.AddSeconds(2),
                runId,
                AgentContentKind.Assistant,
                "content-1",
                ParentActivityId: "turn-1",
                Delta: "hello "),
            new AgentContentDeltaEvent(
                new ModelProviderId("provider-1"),
                "session-1",
                startedAt.AddSeconds(3),
                runId,
                AgentContentKind.Assistant,
                "content-1",
                ParentActivityId: "turn-1",
                Delta: "world"),
            new AgentActivityEvent(
                new ModelProviderId("provider-1"),
                "session-1",
                startedAt.AddSeconds(4),
                runId,
                AgentActivityKind.ToolCall,
                AgentActivityPhase.Completed,
                "tool-1",
                ParentActivityId: "turn-1",
                Name: "read_file",
                Message: "payload-bytes"),
            new AgentActivityEvent(
                new ModelProviderId("provider-1"),
                "session-1",
                startedAt.AddSeconds(7),
                runId,
                AgentActivityKind.Turn,
                AgentActivityPhase.Completed,
                "turn-1",
                ParentActivityId: null,
                Name: "turn",
                Message: null),
        ];
}
