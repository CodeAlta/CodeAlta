using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugin.Statistics;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class StatisticsPluginTests
{
    [TestMethod]
    public void FormattingHelpers_UseCurrentTokenAndByteRules()
    {
        Assert.AreEqual(0, StatisticsPlugin.EstimateTokensFromCharacters(0));
        Assert.AreEqual(1, StatisticsPlugin.EstimateTokensFromCharacters(1));
        Assert.AreEqual(2, StatisticsPlugin.EstimateTokensFromCharacters(8));
        Assert.AreEqual(1, StatisticsPlugin.EstimateTokens("abcd"));
        Assert.IsTrue(StatisticsPlugin.EstimateTokens("{\"filePath\":\"/README.md\"}") > StatisticsPlugin.EstimateTokensFromCharacters(25));
        Assert.AreEqual("512 B", StatisticsPlugin.FormatBytes(512));
        Assert.AreEqual("1.5 KB", StatisticsPlugin.FormatBytes(1536));
        Assert.AreEqual("1.0s", StatisticsPlugin.FormatDuration(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task AltaCommand_Estimate_WritesJsonlEstimate()
    {
        var plugin = new StatisticsPlugin();
        var contribution = plugin.GetAltaCommands().Single();
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(new PluginAltaCommandContext
        {
            Plugin = new PluginDescriptor
            {
                RuntimeKey = "statistics",
                TypeName = typeof(StatisticsPlugin).FullName!,
                AssemblyName = typeof(StatisticsPlugin).Assembly.GetName().Name!,
                DisplayName = "Statistics",
            },
            Services = NoopPluginServices.Create(),
            Scope = PluginScope.Global,
            CorrelationId = "corr-1",
            Stdin = TextReader.Null,
            Stdout = stdout,
            Stderr = stderr,
        }) };

        var exitCode = await app.RunAsync(["statistics", "estimate", "abcd"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, stderr.ToString());
        using var document = JsonDocument.Parse(stdout.ToString());
        var root = document.RootElement;
        Assert.AreEqual("alta.statistics.estimate", root.GetProperty("type").GetString());
        Assert.AreEqual("statistics", root.GetProperty("pluginRuntimeKey").GetString());
        Assert.AreEqual(4, root.GetProperty("bytes").GetInt64());
        Assert.AreEqual(1, root.GetProperty("estimatedTokens").GetInt64());
    }

    [TestMethod]
    public async Task Projection_EmitsCompletedTurnCardWithEstimatedStatsAndShellBucket()
    {
        var plugin = new StatisticsPlugin();
        var contribution = plugin.GetThreadEventProjections().Single();
        var startedAt = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var events = CreateTurnEvents(startedAt, includeCompletedAssistant: true, includeUsage: false, runId: new AgentRunId("run-1"));

        var result = await contribution.ProjectAsync(CreateContext(events), CancellationToken.None);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("statistics:thread-1:run-run-1", result[0].EventId);
        StringAssert.Contains(result[0].Markdown, "computing");
        var completed = await WaitForDynamicProjectionAsync(result[0]);
        StringAssert.Contains(completed.Markdown, "**Turn statistics**");
        StringAssert.Contains(completed.Markdown, "estimated heuristic");
        Assert.AreEqual(1, completed.DetailSections.Count);
        StringAssert.Contains(completed.DetailSections[0].Markdown, "shell");
        StringAssert.Contains(completed.DetailSections[0].Markdown, "Assistant | 11 chars");
        var cardVisualFactory = result[0].DynamicContent?.VisualFactory;
        Assert.IsNotNull(cardVisualFactory);
        var cardVisual = cardVisualFactory(new PluginThreadEventVisualContext
        {
            EventId = result[0].EventId,
            Markdown = completed.Markdown,
        });
        var card = Assert.IsInstanceOfType<Collapsible>(cardVisual);
        var cardHeader = Assert.IsInstanceOfType<Markup>(card.Header);
        StringAssert.Contains(cardHeader.Text, "[bold]Turn statistics[/]");
        Assert.IsInstanceOfType<WrapHStack>(card.Content);
        var visualFactory = completed.DetailSections[0].VisualFactory;
        Assert.IsNotNull(visualFactory);
        var detailVisual = visualFactory(new PluginThreadEventVisualContext
        {
            EventId = result[0].EventId,
            Markdown = completed.DetailSections[0].Markdown,
            DetailHeader = completed.DetailSections[0].Header,
        });
        var detailWrap = Assert.IsInstanceOfType<WrapHStack>(detailVisual);
        Assert.AreEqual(2, detailWrap.Children.Count);
        var firstTable = Assert.IsInstanceOfType<MarkdownControl>(detailWrap.Children[0]);
        Assert.AreEqual(TableStyle.Minimal, firstTable.Options.TableStyle);
    }

    [TestMethod]
    public async Task Projection_PrefersCompletedContentOverDeltasForFinalSize()
    {
        var plugin = new StatisticsPlugin();
        var contribution = plugin.GetThreadEventProjections().Single();
        var startedAt = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var events = CreateTurnEvents(startedAt, includeCompletedAssistant: true, includeUsage: false, runId: new AgentRunId("run-2"));

        var result = await contribution.ProjectAsync(CreateContext(events), CancellationToken.None);

        var completed = await WaitForDynamicProjectionAsync(result.Single());
        StringAssert.Contains(completed.DetailSections.Single().Markdown, "Assistant | 11 chars");
        Assert.IsFalse(completed.DetailSections.Single().Markdown.Contains("22 chars", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Projection_UsesReportedUsageWhenAvailable()
    {
        var plugin = new StatisticsPlugin();
        var contribution = plugin.GetThreadEventProjections().Single();
        var startedAt = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var events = CreateTurnEvents(startedAt, includeCompletedAssistant: false, includeUsage: true, runId: new AgentRunId("run-3"));

        var result = await contribution.ProjectAsync(CreateContext(events), CancellationToken.None);

        var completed = await WaitForDynamicProjectionAsync(result.Single());
        StringAssert.Contains(completed.Markdown, "provider aggregate");
        StringAssert.Contains(completed.Markdown, "1,354 in (provider aggregate) / 567 out (provider aggregate; ≈7 observed generated)");
        StringAssert.Contains(completed.DetailSections.Single().Markdown, "Cached input (provider aggregate)");
    }

    [TestMethod]
    public async Task Projection_AggregatesProviderUsageAcrossTurnOperations()
    {
        var plugin = new StatisticsPlugin();
        var contribution = plugin.GetThreadEventProjections().Single();
        var backendId = new AgentBackendId("provider-1");
        var runId = new AgentRunId("run-provider-aggregate");
        var startedAt = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var events = new AgentEvent[]
        {
            new AgentActivityEvent(backendId, "session-1", startedAt, runId, AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1", null, "turn", null),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(100), runId, AgentContentKind.User, "user-1", "turn-1", "prompt"),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(150), runId, AgentContentKind.Assistant, "assistant-1", "turn-1", "response"),
            new AgentSessionUpdateEvent(
                backendId,
                "session-1",
                startedAt.AddMilliseconds(200),
                runId,
                AgentSessionUpdateKind.UsageUpdated,
                null,
                Usage: new AgentSessionUsage(
                    LastOperation: new AgentOperationUsageSnapshot(
                        InputTokens: 100,
                        OutputTokens: 50,
                        CachedInputTokens: 20,
                        ReasoningTokens: 15))),
            new AgentSessionUpdateEvent(
                backendId,
                "session-1",
                startedAt.AddMilliseconds(300),
                runId,
                AgentSessionUpdateKind.UsageUpdated,
                null,
                Usage: new AgentSessionUsage(
                    LastOperation: new AgentOperationUsageSnapshot(
                        InputTokens: 10,
                        OutputTokens: 30,
                        ReasoningTokens: 5))),
            new AgentActivityEvent(backendId, "session-1", startedAt.AddSeconds(1), runId, AgentActivityKind.Turn, AgentActivityPhase.Completed, "turn-1", null, "turn", null),
        };

        var result = await contribution.ProjectAsync(CreateContext(events), CancellationToken.None);

        var completed = await WaitForDynamicProjectionAsync(result.Single());
        StringAssert.Contains(completed.Markdown, "130 in (provider aggregate) / 80 out (provider aggregate");
        var detailsMarkdown = completed.DetailSections.Single().Markdown;
        StringAssert.Contains(detailsMarkdown, "Provider operations | 2");
        StringAssert.Contains(detailsMarkdown, "Input total (provider aggregate) | 130");
        StringAssert.Contains(detailsMarkdown, "Fresh input (provider aggregate) | 110");
        StringAssert.Contains(detailsMarkdown, "Cached input (provider aggregate) | 20");
        StringAssert.Contains(detailsMarkdown, "Output total (provider aggregate) | 80");
        StringAssert.Contains(detailsMarkdown, "Non-reasoning output (provider aggregate) | 60");
        StringAssert.Contains(detailsMarkdown, "Reasoning output (provider aggregate) | 20");
        StringAssert.Contains(detailsMarkdown, "Observed generated estimate | 2");
        StringAssert.Contains(detailsMarkdown, "Observed non-reasoning estimate | 2");
        StringAssert.Contains(detailsMarkdown, "Provider minus observed output | +78");
        StringAssert.Contains(detailsMarkdown, "Provider minus observed reasoning | +20");
    }

    [TestMethod]
    public async Task Projection_GroupsEventsWithoutRunIdByStableFallbackTurn()
    {
        var plugin = new StatisticsPlugin();
        var contribution = plugin.GetThreadEventProjections().Single();
        var startedAt = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var events = CreateTurnEvents(startedAt, includeCompletedAssistant: false, includeUsage: false, runId: null);

        var result = await contribution.ProjectAsync(CreateContext(events), CancellationToken.None);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("statistics:thread-1:session-session-1-turn-1", result[0].EventId);
    }

    [TestMethod]
    public async Task Projection_DoesNotEmitCardForIncompleteTurn()
    {
        var plugin = new StatisticsPlugin();
        var contribution = plugin.GetThreadEventProjections().Single();
        var backendId = new AgentBackendId("provider-1");
        var runId = new AgentRunId("run-open");
        var events = new AgentEvent[]
        {
            new AgentActivityEvent(backendId, "session-1", DateTimeOffset.UtcNow, runId, AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1", null, "turn", null),
            new AgentContentDeltaEvent(backendId, "session-1", DateTimeOffset.UtcNow.AddSeconds(1), runId, AgentContentKind.Assistant, "assistant-1", "turn-1", "still running"),
        };

        var result = await contribution.ProjectAsync(CreateContext(events), CancellationToken.None);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task Projection_CountsModelGeneratedToolInputAsOutputWithoutToolOutput()
    {
        var plugin = new StatisticsPlugin();
        var contribution = plugin.GetThreadEventProjections().Single();
        var backendId = new AgentBackendId("provider-1");
        var runId = new AgentRunId("run-output-accounting");
        var startedAt = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var details = ParseDetails("{\"command\":\"run\",\"arguments\":\"tool args\"}");
        var events = new AgentEvent[]
        {
            new AgentActivityEvent(backendId, "session-1", startedAt, runId, AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1", null, "turn", null),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(100), runId, AgentContentKind.User, "user-1", "turn-1", "prompt"),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(200), runId, AgentContentKind.Assistant, "assistant-1", "turn-1", "hi"),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(300), runId, AgentContentKind.Reasoning, "reasoning-1", "turn-1", "abc"),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(400), runId, AgentContentKind.ReasoningSummary, "summary-1", "turn-1", "sum"),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(500), runId, AgentContentKind.Plan, "plan-1", "turn-1", "plan"),
            new AgentActivityEvent(backendId, "session-1", startedAt.AddMilliseconds(600), runId, AgentActivityKind.ToolCall, AgentActivityPhase.Started, "tool-1", "turn-1", "tool", null, details),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(700), runId, AgentContentKind.ToolOutput, "tool-output-1", "tool-1", "tool output is not model output"),
            new AgentActivityEvent(backendId, "session-1", startedAt.AddMilliseconds(800), runId, AgentActivityKind.ToolCall, AgentActivityPhase.Completed, "tool-1", "turn-1", "tool", "done"),
            new AgentActivityEvent(backendId, "session-1", startedAt.AddSeconds(1), runId, AgentActivityKind.Turn, AgentActivityPhase.Completed, "turn-1", null, "turn", null),
        };

        var result = await contribution.ProjectAsync(CreateContext(events), CancellationToken.None);

        var card = await WaitForDynamicProjectionAsync(result.Single());
        StringAssert.Contains(card.Markdown, "2 in (estimated heuristic) / ≈8 out (estimated generated)");
        var detailsMarkdown = card.DetailSections.Single().Markdown;
        StringAssert.Contains(detailsMarkdown, "Tool input (observed model generated) | 16 chars");
        StringAssert.Contains(detailsMarkdown, "Observed generated output | 25 chars");
        StringAssert.Contains(detailsMarkdown, "Tool output | 31 chars");
    }

    [TestMethod]
    public async Task Projection_UsesReasoningSummaryForOutputOnlyWhenFullReasoningIsAbsent()
    {
        var plugin = new StatisticsPlugin();
        var contribution = plugin.GetThreadEventProjections().Single();
        var backendId = new AgentBackendId("provider-1");
        var runId = new AgentRunId("run-reasoning-summary-accounting");
        var startedAt = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var events = new AgentEvent[]
        {
            new AgentActivityEvent(backendId, "session-1", startedAt, runId, AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1", null, "turn", null),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(100), runId, AgentContentKind.User, "user-1", "turn-1", "prompt"),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(200), runId, AgentContentKind.Assistant, "assistant-1", "turn-1", "ok"),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(300), runId, AgentContentKind.Reasoning, "reasoning-1", "turn-1", "full reasoning"),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(400), runId, AgentContentKind.ReasoningSummary, "summary-1", "turn-1", "summary duplicate"),
            new AgentActivityEvent(backendId, "session-1", startedAt.AddSeconds(1), runId, AgentActivityKind.Turn, AgentActivityPhase.Completed, "turn-1", null, "turn", null),
        };

        var result = await contribution.ProjectAsync(CreateContext(events), CancellationToken.None);

        var card = await WaitForDynamicProjectionAsync(result.Single());
        var detailsMarkdown = card.DetailSections.Single().Markdown;
        StringAssert.Contains(detailsMarkdown, "Reasoning | 14 chars");
        StringAssert.Contains(detailsMarkdown, "Reasoning summary | 17 chars");
        StringAssert.Contains(detailsMarkdown, "Observed generated output | 16 chars");
    }

    [TestMethod]
    public async Task Projection_OmitsSpeedWhenOnlyCompletedContentTimestampsAreAvailable()
    {
        var plugin = new StatisticsPlugin();
        var contribution = plugin.GetThreadEventProjections().Single();
        var backendId = new AgentBackendId("provider-1");
        var runId = new AgentRunId("run-speed-unavailable");
        var startedAt = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var events = new AgentEvent[]
        {
            new AgentActivityEvent(backendId, "session-1", startedAt, runId, AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1", null, "turn", null),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(100), runId, AgentContentKind.User, "user-1", "turn-1", "prompt"),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddSeconds(2), runId, AgentContentKind.Assistant, "assistant-1", "turn-1", "completed assistant text"),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddSeconds(4), runId, AgentContentKind.Reasoning, "reasoning-1", "turn-1", "completed reasoning text"),
            new AgentActivityEvent(backendId, "session-1", startedAt.AddSeconds(10), runId, AgentActivityKind.Turn, AgentActivityPhase.Completed, "turn-1", null, "turn", null),
        };

        var result = await contribution.ProjectAsync(CreateContext(events), CancellationToken.None);

        var card = await WaitForDynamicProjectionAsync(result.Single());
        var detailsMarkdown = card.DetailSections.Single().Markdown;
        StringAssert.Contains(detailsMarkdown, "Assistant speed | n/a");
        StringAssert.Contains(detailsMarkdown, "Reasoning speed | n/a");
        StringAssert.Contains(detailsMarkdown, "Generated output speed | n/a");
    }

    [TestMethod]
    public async Task Projection_IncludesCompactionCountAndDurationInDetails()
    {
        var plugin = new StatisticsPlugin();
        var contribution = plugin.GetThreadEventProjections().Single();
        var backendId = new AgentBackendId("provider-1");
        var runId = new AgentRunId("run-compaction");
        var startedAt = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var events = new AgentEvent[]
        {
            new AgentActivityEvent(backendId, "session-1", startedAt, runId, AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1", null, "turn", null),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(100), runId, AgentContentKind.User, "user-1", "turn-1", "prompt"),
            new AgentSessionUpdateEvent(backendId, "session-1", startedAt.AddSeconds(1), runId, AgentSessionUpdateKind.CompactionStarted, "compacting"),
            new AgentSessionUpdateEvent(backendId, "session-1", startedAt.AddSeconds(3), runId, AgentSessionUpdateKind.CompactionCompleted, "compacted"),
            new AgentActivityEvent(backendId, "session-1", startedAt.AddSeconds(4), runId, AgentActivityKind.Turn, AgentActivityPhase.Completed, "turn-1", null, "turn", null),
        };

        var result = await contribution.ProjectAsync(CreateContext(events), CancellationToken.None);

        var card = await WaitForDynamicProjectionAsync(result.Single());
        StringAssert.Contains(card.Markdown, "compactions 1 / 2.0s");
        StringAssert.Contains(card.DetailSections.Single().Markdown, "Compactions | 1 / 2.0s");
    }

    private static PluginThreadEventProjectionContext CreateContext(IReadOnlyList<AgentEvent> events)
        => new()
        {
            Handle = PluginContributionHandle.Create("builtin:statistics", typeof(StatisticsPlugin).FullName!, PluginPoint.ThreadEventProjection, "statistics", 0, 1),
            ThreadId = "thread-1",
            ProjectId = "project-1",
            ProjectPath = "C:/project",
            BackendId = "provider-1",
            Model = "model-1",
            SessionId = events.LastOrDefault()?.SessionId,
            RunId = events.LastOrDefault(static item => item.RunId is not null)?.RunId?.Value,
            Events = events,
            IsCompleteBatch = true,
        };

    private static async Task<(string Markdown, IReadOnlyList<PluginDerivedThreadEventDetailSection> DetailSections)> WaitForDynamicProjectionAsync(PluginDerivedThreadEvent derivedEvent)
    {
        if (derivedEvent.DynamicContent is not { } dynamicContent)
        {
            return (derivedEvent.Markdown ?? string.Empty, derivedEvent.DetailSections);
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!dynamicContent.Markdown.Contains("computing", StringComparison.OrdinalIgnoreCase))
            {
                return (dynamicContent.Markdown, dynamicContent.DetailSections);
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? _, EventArgs __) => completion.TrySetResult();

            dynamicContent.Changed += OnChanged;
            try
            {
                if (!dynamicContent.Markdown.Contains("computing", StringComparison.OrdinalIgnoreCase))
                {
                    return (dynamicContent.Markdown, dynamicContent.DetailSections);
                }

                await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                dynamicContent.Changed -= OnChanged;
            }
        }

        Assert.Fail("Timed out waiting for dynamic statistics projection.");
        return (string.Empty, []);
    }

    private static JsonElement ParseDetails(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<AgentEvent> CreateTurnEvents(DateTimeOffset startedAt, bool includeCompletedAssistant, bool includeUsage, AgentRunId? runId)
    {
        var backendId = new AgentBackendId("provider-1");
        var events = new List<AgentEvent>
        {
            new AgentActivityEvent(backendId, "session-1", startedAt, runId, AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1", null, "turn", null),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddMilliseconds(100), runId, AgentContentKind.User, "user-1", "turn-1", "please help"),
            new AgentContentDeltaEvent(backendId, "session-1", startedAt.AddSeconds(1), runId, AgentContentKind.Assistant, "assistant-1", "turn-1", "hello "),
            new AgentContentDeltaEvent(backendId, "session-1", startedAt.AddSeconds(2), runId, AgentContentKind.Assistant, "assistant-1", "turn-1", "world"),
            new AgentActivityEvent(backendId, "session-1", startedAt.AddSeconds(3), runId, AgentActivityKind.CommandExecution, AgentActivityPhase.Started, "tool-1", "turn-1", "shell_command", "pwsh"),
            new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddSeconds(4), runId, AgentContentKind.CommandOutput, "tool-output-1", "tool-1", "file contents"),
            new AgentActivityEvent(backendId, "session-1", startedAt.AddSeconds(5), runId, AgentActivityKind.CommandExecution, AgentActivityPhase.Completed, "tool-1", "turn-1", "shell_command", "ok"),
        };

        if (includeCompletedAssistant)
        {
            events.Add(new AgentContentCompletedEvent(backendId, "session-1", startedAt.AddSeconds(6), runId, AgentContentKind.Assistant, "assistant-1", "turn-1", "hello world"));
        }

        if (includeUsage)
        {
            events.Add(new AgentSessionUpdateEvent(
                backendId,
                "session-1",
                startedAt.AddSeconds(6),
                runId,
                AgentSessionUpdateKind.UsageUpdated,
                null,
                Usage: new AgentSessionUsage(
                    LastOperation: new AgentOperationUsageSnapshot(
                        Model: "model-1",
                        InputTokens: 1234,
                        OutputTokens: 567,
                        CachedInputTokens: 120,
                        CacheReadTokens: 100,
                        CacheWriteTokens: 20,
                        ReasoningTokens: 89))));
        }

        events.Add(new AgentActivityEvent(backendId, "session-1", startedAt.AddSeconds(7), runId, AgentActivityKind.Turn, AgentActivityPhase.Completed, "turn-1", null, "turn", null));
        return events;
    }
}
