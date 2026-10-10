using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// The numbers that are computed when they are asked (the sessions that run at once, the average fill of the context, the depth of
/// the sub-agents), limited to one session and its sub-agents: none of them counts a session of anyone else.
/// </summary>
[TestClass]
public sealed class StatisticsSessionDerivedMetricsTests
{
    private const string TheDay = "2026-06-10..2026-06-10";
    private static readonly DateTimeOffset Day = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);

    private static FactBatch Session(string id, string? parent = null, params (double StartMinute, double EndMinute)[] runs)
    {
        var batch = new FactBatch(id);
        var index = 0;
        foreach (var (start, end) in runs)
        {
            var runId = $"{id}-run{index++}";
            batch.Runs[runId] = new RunRow
            {
                SessionId = id, RunId = runId, Start = Day.AddMinutes(start), End = Day.AddMinutes(end), Outcome = RunOutcome.Completed,
                Sender = PromptSender.You, PromptKind = PromptKind.NewTurn, Provider = "codex", Model = "m",
            };
        }

        batch.Session = new SessionRow
        {
            SessionId = id, ProjectRef = "project-0", SessionKind = "ProjectSession", ParentSessionId = parent, Provider = "codex",
            FirstRecord = Day.AddHours(9), LastRecord = Day.AddHours(12),
        };
        return batch;
    }

    private static void Fill(FactBatch batch, string model, int requests, long fillPpmSum)
    {
        var usage = batch.UsageFor(new UsageKey(QuarterHour.Of(Day.AddHours(10)), "codex", model, "", "default", UsagePurpose.Turn));
        usage.Requests += requests;
        usage.ContextSamples += requests;
        usage.ContextFillPpmSum += fillPpmSum;
        usage.ContextFillPpmMax = Math.Max(usage.ContextFillPpmMax, fillPpmSum / requests);
    }

    private static StatisticsRequest Of(string? session, bool withChildren = true, string period = TheDay, StatisticsFrequency frequency = StatisticsFrequency.Hour)
        => new() { Period = period, Frequency = frequency, Filter = session is null ? new StatisticsFilter() : new StatisticsFilter { Session = session, WithChildren = withChildren } };

    private static double[] Values(SeriesResult result) => [.. result.Series.Single().Values];

    [TestMethod]
    public async Task SessionsAtOnce_OfASession_AreItAndItsSubAgents_NeverTheOthers()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        // 10:00 the session, 10:20 its sub-agent, 10:30 the sub-agent of that one: three at once until 10:40, and the session alone until 11:00.
        await harness.AddAsync(Session("root", runs: [(600, 660)]));
        await harness.AddAsync(Session("child", parent: "root", runs: [(620, 640)]));
        await harness.AddAsync(Session("grandchild", parent: "child", runs: [(630, 650)]));
        // Two sessions of someone else, going all along and beyond 11:00.
        await harness.AddAsync(Session("other", runs: [(600, 700)]));
        await harness.AddAsync(Session("another", runs: [(610, 690)]));

        var everything = await harness.Queries.SeriesAsync(Of(null), "sessions-at-once");
        var tree = await harness.Queries.SeriesAsync(Of("root"), "sessions-at-once");
        var alone = await harness.Queries.SeriesAsync(Of("root", withChildren: false), "sessions-at-once");
        var subAgent = await harness.Queries.SeriesAsync(Of("child"), "sessions-at-once");
        var unknown = await harness.Queries.SeriesAsync(Of("nobody"), "sessions-at-once");
        var life = await harness.Queries.SeriesAsync(Of("root", period: "all", frequency: StatisticsFrequency.Auto), "sessions-at-once");

        Assert.AreEqual(5d, Values(everything)[10], "every session: the five of them between 10:30 and 10:40");
        Assert.AreEqual(2d, Values(everything)[11], "the two others go on after 11:00");
        Assert.AreEqual(3d, Values(tree)[10], "the session, its sub-agent and the sub-agent of that one");
        Assert.AreEqual(0d, Values(tree)[11], "the runs of the others are not in the tree");
        Assert.AreEqual(3d, tree.Series.Single().Total);
        Assert.AreEqual(1d, Values(alone)[10]);
        Assert.AreEqual(1d, alone.Series.Single().Total);
        Assert.AreEqual(2d, subAgent.Series.Single().Total, "a sub-agent is the top of its own tree: its parent is not in it");
        Assert.IsTrue(Values(unknown).All(static value => value == 0), "a session that is not known: nobody, never everybody");
        CollectionAssert.Contains(unknown.Query.Notes.ToArray(), "session-not-found");
        // The whole history of the session is its day, cut in hours.
        Assert.AreEqual(("2026-06-10", "2026-06-10", "hour"), (life.Query.From, life.Query.To, life.Query.Frequency));
        Assert.AreEqual(3d, Values(life)[10]);
        // A filter of a run narrows the tree: nobody of the tree ran another model.
        var model = await harness.Queries.SeriesAsync(Of("root") with { Filter = new StatisticsFilter { Session = "root", WithChildren = true, Model = "elsewhere" } }, "sessions-at-once");
        Assert.IsTrue(Values(model).All(static value => value == 0));
    }

    [TestMethod]
    public async Task TheAverageFillOfTheContext_OfASession_IsOfItsRequestsAndThoseOfItsSubAgents()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        // The session: one request at 10% of the window. Its sub-agent: three at 90%. Someone else: four at 50%, on the model of the sub-agent.
        var root = Session("root");
        Fill(root, "small", 1, 100_000);
        var child = Session("child", parent: "root");
        Fill(child, "large", 3, 2_700_000);
        var other = Session("other");
        Fill(other, "large", 4, 2_000_000);
        await harness.AddAsync(root);
        await harness.AddAsync(child);
        await harness.AddAsync(other);
        var day = StatisticsFrequency.Day;

        var everything = await harness.Queries.SeriesAsync(Of(null, frequency: day), "context-fill");
        var tree = await harness.Queries.SeriesAsync(Of("root", frequency: day), "context-fill");
        var alone = await harness.Queries.SeriesAsync(Of("root", withChildren: false, frequency: day), "context-fill");
        var byModel = await harness.Queries.SeriesAsync(Of("root", frequency: day), "context-fill", "model");
        var samples = await harness.Queries.SeriesAsync(Of("root", frequency: day), "context-samples");
        var unknown = await harness.Queries.SeriesAsync(Of("nobody", frequency: day), "context-fill");

        Assert.AreEqual(0.6, everything.Series.Single().Total, 1e-9, "every session: 4.8 over 8 requests");
        Assert.AreEqual(0.7, tree.Series.Single().Total, 1e-9, "the tree: 2.8 over 4 requests, without the four of someone else");
        Assert.AreEqual(0.7, Values(tree)[0], 1e-9);
        Assert.AreEqual(0.1, alone.Series.Single().Total, 1e-9);
        Assert.AreEqual(4d, samples.Series.Single().Total);
        CollectionAssert.AreEqual(new[] { "large", "small" }, byModel.Series.Select(static line => line.Key).ToArray());
        Assert.AreEqual(0.9, byModel.Series[0].Total, 1e-9, "the model of the sub-agent, without the requests someone else made with it");
        Assert.AreEqual(0.1, byModel.Series[1].Total, 1e-9);
        Assert.AreEqual(0d, unknown.Series.Single().Total);
        CollectionAssert.Contains(unknown.Query.Notes.ToArray(), "session-not-found");

        // The tables that give the fill of each model say the same.
        var models = await harness.Queries.ModelsAsync(Of("root", frequency: day));
        Assert.AreEqual(0.9, models.Rows.Single(static row => row.Model == "large").AverageContextFill!.Value, 1e-9);
        Assert.AreEqual(3L, models.Rows.Single(static row => row.Model == "large").Requests);
        var health = await harness.Queries.HealthAsync(Of("root", frequency: day));
        Assert.AreEqual(0.9, health.ContextByModel.Single(static row => row.Model == "large").Average!.Value, 1e-9);
        Assert.AreEqual(3L, health.ContextByModel.Single(static row => row.Model == "large").Samples);
    }

    [TestMethod]
    public async Task TheDepthOfTheSubAgents_OfASession_CountsItsTree_FromTheTopOfTheWholeTree()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        await harness.AddAsync(Session("top"));
        await harness.AddAsync(Session("mid", parent: "top"));
        await harness.AddAsync(Session("sibling", parent: "top"));
        await harness.AddAsync(Session("kid", parent: "mid"));
        await harness.AddAsync(Session("grandkid", parent: "kid"));
        // The tree of someone else.
        await harness.AddAsync(Session("x"));
        await harness.AddAsync(Session("y", parent: "x"));
        static (string[] Names, long[] Counts, long Total) Rows(DetailsResult result)
            => ([.. result.Rows.Select(static row => row.Name)], [.. result.Rows.Select(static row => row.Count)], result.Total);

        var everything = Rows(await harness.Queries.DetailsAsync(Of(null), "sub-agent-depth"));
        var midTree = await harness.Queries.DetailsAsync(Of("mid"), "sub-agent-depth");
        var mid = Rows(midTree);
        var midAlone = Rows(await harness.Queries.DetailsAsync(Of("mid", withChildren: false), "sub-agent-depth"));
        var top = Rows(await harness.Queries.DetailsAsync(Of("top"), "sub-agent-depth"));
        var topAlone = Rows(await harness.Queries.DetailsAsync(Of("top", withChildren: false), "sub-agent-depth"));
        var unknown = await harness.Queries.DetailsAsync(Of("nobody"), "sub-agent-depth");

        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, everything.Names);
        CollectionAssert.AreEqual(new[] { 3L, 1L, 1L }, everything.Counts, "every session: mid, sibling and y at 1, kid at 2, grandkid at 3");
        // The sessions that are counted are those of the tree of the session; how deep each is, is from the top of the whole tree:
        // mid stays a sub-agent at 1 and its own sub-agent stays at 2, as on the page of every session.
        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, mid.Names);
        CollectionAssert.AreEqual(new[] { 1L, 1L, 1L }, mid.Counts, "mid, kid and grandkid: neither its sibling nor the tree of someone else");
        Assert.AreEqual(3L, mid.Total);
        Assert.AreEqual(0, midTree.Query.Notes.Count(static note => note == "some-parents-unknown"), "the parents above the session are followed though they are not counted");
        CollectionAssert.AreEqual(new[] { "1" }, midAlone.Names);
        Assert.AreEqual(1L, midAlone.Total);
        CollectionAssert.AreEqual(new[] { 2L, 1L, 1L }, top.Counts, "the whole tree of top: y is of another one");
        Assert.AreEqual(4L, top.Total);
        Assert.AreEqual(0L, topAlone.Total, "a session of its own is no sub-agent");
        Assert.AreEqual(0L, unknown.Total);
        Assert.IsEmpty(unknown.Rows);
        CollectionAssert.Contains(unknown.Query.Notes.ToArray(), "session-not-found");
    }
}
