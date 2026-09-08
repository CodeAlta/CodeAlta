using CodeAlta.Agent;
using CodeAlta.Plugin.Statistics;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;

namespace CodeAlta.Tests;

/// <summary>Literal neutral events through the real synchronous contribution; no lifecycle, native or text callbacks.</summary>
[TestClass]
public sealed class PluginStatisticsPresentationTests
{
    [TestMethod]
    public void Construction_BackendAndInjectedCompositionAreDeferred()
    {
        var calls = 0;
        var backend = new StatisticsPlugin();
        var injected = new StatisticsPlugin((projection, _) => { calls++; return projection; });
        foreach (var plugin in new[] { backend, injected })
        {
            Assert.AreEqual("statistics", plugin.GetSessionEventProjections().Single().Name);
            Assert.IsNotNull(plugin.GetSessionEventProjections().Single().ProjectAsync);
        }
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void Construction_RejectsNullDecorator()
    {
        var error = Assert.ThrowsExactly<ArgumentNullException>(() => new StatisticsPlugin(null!));
        Assert.AreEqual("decorateProjection", error.ParamName);
    }

    [TestMethod]
    public void Projection_EmptyAndIncompleteBatchesDoNotDecorate()
    {
        var calls = 0;
        var plugin = new StatisticsPlugin((projection, _) => { calls++; return projection; });
        Assert.AreEqual(0, Project(plugin, Context([])).Count);
        AgentEvent started = new AgentActivityEvent(new ModelProviderId("literal-provider"), "runtime-session", At(),
            new AgentRunId("literal-run"), AgentActivityKind.Turn, AgentActivityPhase.Started,
            "literal-activity", null, null, null, Details: null);
        Assert.AreEqual(0, Project(plugin, Context([started])).Count);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void Projection_NullContextAndCancellationRetainOrder()
    {
        var calls = 0;
        var route = new StatisticsPlugin((projection, _) => { calls++; return projection; }).GetSessionEventProjections().Single();
        var token = new CancellationToken(canceled: true);
        Assert.AreEqual("context", Assert.ThrowsExactly<ArgumentNullException>(() => { _ = route.ProjectAsync(null!, token); }).ParamName);
        Assert.AreEqual(token, Assert.ThrowsExactly<OperationCanceledException>(() => { _ = route.ProjectAsync(Context([]), token); }).CancellationToken);
        Assert.AreEqual(token, Assert.ThrowsExactly<OperationCanceledException>(() => { _ = route.ProjectAsync(Context([Idle()]), token); }).CancellationToken);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void Projection_PlainBackendEmitsPortableReadyEvent()
    {
        var projection = Project(new StatisticsPlugin(), Context([Idle()])).Single();
        AssertPortable(projection);
        Assert.AreEqual("statistics:literal-session:run-literal-run", projection.EventId);
        Assert.AreEqual(At(), projection.Timestamp);
        Assert.IsNotNull(projection.Payload);
        Assert.IsNull(projection.DynamicContent);
        Assert.IsFalse(projection.Remove);
    }

    [TestMethod]
    public void Projection_DecoratesCandidateWithoutNativeFormatting()
    {
        PluginDerivedSessionEvent? candidate = null;
        StatisticsPresentation? presentation = null;
        var result = new PluginDerivedSessionEvent { EventId = "decorated" };
        var plugin = new StatisticsPlugin((projection, turn) =>
        {
            candidate = projection;
            presentation = turn;
            AssertPortable(projection);
            return result;
        });
        Assert.AreSame(result, Project(plugin, Context([Idle()])).Single());
        Assert.IsNotNull(candidate);
        Assert.IsNotNull(presentation);
        Assert.AreEqual("Turn statistics", presentation.Title);
        Assert.IsNotNull(presentation.RenderTurnSummarySuffix);
        Assert.IsNotNull(presentation.RenderMetricTable);
        Assert.IsNotNull(presentation.RenderUsageTable);
        Assert.IsNotNull(presentation.RenderToolBucketTable);
    }

    [TestMethod]
    public void Projection_ReusesCachedDecoratedEventForUnchangedTurn()
    {
        var calls = 0;
        var plugin = new StatisticsPlugin((projection, _) => { calls++; return projection with { EventId = "decorated" }; });
        var first = Project(plugin, Context([Idle()])).Single();
        var second = Project(plugin, Context([Idle()])).Single();
        Assert.AreSame(first, second);
        Assert.AreEqual("decorated", second.EventId);
        Assert.AreEqual(1, calls); // Sequential reuse only; racing GetOrAdd candidates may each decorate.
    }

    [TestMethod]
    public void Projection_SeparatesSessionAndFingerprintCacheKeys()
    {
        var calls = 0;
        var plugin = new StatisticsPlugin((projection, _) => { calls++; return projection; });
        var context = Context([Idle()]);
        var first = Project(plugin, context).Single();
        var otherSession = Project(plugin, context with { SessionId = "other-session" }).Single();
        var changed = Project(plugin, Context([Idle() with { Timestamp = At().AddSeconds(1) }])).Single();
        Assert.AreNotSame(first, otherSession);
        Assert.AreNotSame(first, changed);
        Assert.AreEqual(first.EventId, changed.EventId);
        Assert.AreNotEqual(first.EventId, otherSession.EventId);
        Assert.AreSame(first, Project(plugin, context).Single());
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public void Projection_DecoratorFailureAndCancellationPropagateWithoutCaching()
    {
        var failure = new ApplicationException("literal-decorator-failure");
        var cancellation = new OperationCanceledException(new CancellationToken(canceled: true));
        var calls = 0;
        var plugin = new StatisticsPlugin((projection, _) =>
        {
            calls++;
            if (calls == 1) throw failure;
            if (calls == 2) throw cancellation;
            return projection;
        });
        var context = Context([Idle()]);
        Assert.AreSame(failure, Assert.ThrowsExactly<ApplicationException>(() => Project(plugin, context)));
        Assert.AreSame(cancellation, Assert.ThrowsExactly<OperationCanceledException>(() => Project(plugin, context)));
        var result = Project(plugin, context).Single();
        Assert.AreSame(result, Project(plugin, context).Single());
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public void Projection_NullDecoratorResultHasNoPortableFallback()
    {
        var calls = 0;
        var plugin = new StatisticsPlugin((_, _) => { calls++; return null!; });
        var result = Project(plugin, Context([Idle()]));
        Assert.AreEqual(1, result.Count);
        Assert.IsNull(result[0]);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void TerminalDecoration_PreservesEventPayloadAndDetailMetadata()
    {
        var payload = new object();
        var dynamic = new UnreadDynamicContent();
        var source = new PluginDerivedSessionEvent
        {
            EventId = "literal-event", Timestamp = At(), Markdown = "portable", RenderTarget = "opaque-target",
            Payload = payload, DynamicContent = dynamic, Remove = true,
            DetailSections =
            [
                new PluginDerivedSessionEventDetailSection { Header = "first", Markdown = "one" },
                new PluginDerivedSessionEventDetailSection { Header = "second", Markdown = "two" },
            ],
        };
        var decorated = Assert.IsInstanceOfType<PluginTerminalDerivedSessionEvent>(
            StatisticsTerminalContributions.DecorateProjection(source, UnreadPresentation()));
        Assert.AreEqual(source.EventId, decorated.EventId);
        Assert.AreEqual(source.Timestamp, decorated.Timestamp);
        Assert.AreEqual(source.Markdown, decorated.Markdown);
        Assert.AreEqual(source.RenderTarget, decorated.RenderTarget);
        Assert.AreSame(payload, decorated.Payload);
        Assert.AreSame(dynamic, decorated.DynamicContent);
        Assert.AreEqual(source.Remove, decorated.Remove);
        Assert.AreEqual(2, decorated.DetailSections.Count);
        for (var i = 0; i < source.DetailSections.Count; i++)
        {
            var detail = Assert.IsInstanceOfType<PluginTerminalDerivedSessionEventDetailSection>(decorated.DetailSections[i]);
            Assert.AreEqual(source.DetailSections[i].Header, detail.Header);
            Assert.AreEqual(source.DetailSections[i].Markdown, detail.Markdown);
            Assert.IsNotNull(detail.VisualFactory);
            Assert.IsNull(detail.HeaderVisualFactory);
        }
    }

    [TestMethod]
    public void TerminalDecoration_DefersCardAndDetailCallbacks()
    {
        var source = new PluginDerivedSessionEvent
        {
            EventId = "literal-event",
            DetailSections = [new PluginDerivedSessionEventDetailSection { Header = "header", Markdown = "detail" }],
        };
        var decorated = Assert.IsInstanceOfType<PluginTerminalDerivedSessionEvent>(
            StatisticsTerminalContributions.DecorateProjection(source, UnreadPresentation()));
        Assert.IsNotNull(decorated.VisualFactory);
        Assert.IsNotNull(Assert.IsInstanceOfType<PluginTerminalDerivedSessionEventDetailSection>(decorated.DetailSections.Single()).VisualFactory);
        var empty = Assert.IsInstanceOfType<PluginTerminalDerivedSessionEvent>(
            StatisticsTerminalContributions.DecorateProjection(source with { DetailSections = [] }, UnreadPresentation()));
        Assert.IsNotNull(empty.VisualFactory);
        Assert.AreEqual(0, empty.DetailSections.Count);
        Assert.IsNull(empty.Markdown);
        Assert.IsNull(empty.Payload);
        Assert.IsNull(empty.DynamicContent);
    }

    private static DateTimeOffset At() => new(2026, 5, 8, 10, 0, 0, TimeSpan.Zero);

    private static AgentSessionUpdateEvent Idle() => new(new ModelProviderId("literal-provider"), "runtime-session",
        At(), new AgentRunId("literal-run"), AgentSessionUpdateKind.Idle, null, Details: null, Usage: null);

    private static PluginSessionEventProjectionContext Context(IReadOnlyList<AgentEvent> events) => new()
    {
        Handle = new PluginContributionHandle
        {
            PluginRuntimeKey = "statistics", PluginTypeName = "CodeAlta.Plugin.Statistics.StatisticsPlugin",
            Point = PluginPoint.SessionEventProjection, RuntimeContributionKey = "literal-contribution", NaturalName = "statistics",
        },
        SessionId = "literal-session", Events = events,
    };

    private static IReadOnlyList<PluginDerivedSessionEvent> Project(StatisticsPlugin plugin, PluginSessionEventProjectionContext context)
    {
        var pending = plugin.GetSessionEventProjections().Single().ProjectAsync(context, CancellationToken.None);
        Assert.IsTrue(pending.IsCompletedSuccessfully);
        return pending.Result;
    }

    private static void AssertPortable(PluginDerivedSessionEvent projection)
    {
        Assert.AreEqual(typeof(PluginDerivedSessionEvent), projection.GetType());
        StringAssert.StartsWith(projection.Markdown, "**Turn statistics**");
        StringAssert.Contains(projection.Markdown, "estimated heuristic");
        Assert.AreEqual("codealta.statistics.turn.v1", projection.RenderTarget);
        Assert.AreEqual(1, projection.DetailSections.Count);
        var detail = projection.DetailSections[0];
        Assert.AreEqual(typeof(PluginDerivedSessionEventDetailSection), detail.GetType());
        Assert.AreEqual("Detailed statistics", detail.Header);
        Assert.IsFalse(string.IsNullOrWhiteSpace(detail.Markdown));
    }

    private static StatisticsPresentation UnreadPresentation() => new("literal-title", UnreadText, UnreadText, UnreadText, UnreadText);
    private static string UnreadText() => throw new AssertFailedException("Decoration must not invoke any text callback.");

    private sealed class UnreadDynamicContent : PluginDynamicDerivedSessionEventContent
    {
        public override string Markdown => throw new AssertFailedException("Decoration must not read dynamic content.");
        public override IReadOnlyList<PluginDerivedSessionEventDetailSection> DetailSections => throw new AssertFailedException("Decoration must not read dynamic details.");
    }
}
