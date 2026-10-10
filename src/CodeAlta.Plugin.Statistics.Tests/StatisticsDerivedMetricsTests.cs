using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// The numbers that are computed from the rows the store already keeps, not kept as facts: the sessions that run at once (from
/// the runs), the depth of the trees of sub-agents (from the parents of the sessions) and the average fill of the context (a sum
/// over a count).
/// </summary>
[TestClass]
public sealed class StatisticsDerivedMetricsTests
{
    private static readonly DateTimeOffset Day = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);

    private static FactBatch Session(string id, string? parent = null, string? project = "project-0", DateTimeOffset? first = null, params (double StartMinute, double EndMinute)[] runs)
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
            SessionId = id, ProjectRef = project, SessionKind = "ProjectSession", ParentSessionId = parent, Provider = "codex",
            FirstRecord = first ?? Day.AddHours(1), LastRecord = (first ?? Day.AddHours(1)).AddHours(1),
        };
        return batch;
    }

    private static double[] Values(SeriesResult result) => [.. result.Series.Single().Values];

    [TestMethod]
    public async Task SessionsAtOnce_IsTheMostSessionsWithARunGoingAtTheSameMoment_InEachBucket()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        // 10:00 A, 10:20 B joins (two at once), 10:30 A ends as C starts (still two, never three), 10:50 nobody.
        await harness.AddAsync(Session("a", runs: [(600, 630), (720, 730)]));
        await harness.AddAsync(Session("b", runs: [(620, 640)]));
        await harness.AddAsync(Session("c", runs: [(630, 650)]));
        // Two runs of one session that overlap are one session.
        await harness.AddAsync(Session("d", runs: [(840, 850), (845, 855)]));
        // A run over midnight is going on both days; a run without a length is going at its moment.
        await harness.AddAsync(Session("e", runs: [(1430, 1450)]));
        await harness.AddAsync(Session("f", runs: [(1445, 1445)]));

        var days = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-06-10..2026-06-12", Frequency = StatisticsFrequency.Day }, "sessions-at-once");
        var hours = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-06-10..2026-06-10", Frequency = StatisticsFrequency.Hour }, "sessions-at-once");

        Assert.AreEqual("count", days.Unit);
        CollectionAssert.AreEqual(new[] { 2d, 2d, 0d }, Values(days));
        Assert.AreEqual(2d, days.Series.Single().Total, "The total of the line is the most of the period, not a sum.");
        var byHour = Values(hours);
        Assert.AreEqual(24, byHour.Length);
        Assert.AreEqual(2d, byHour[10]);
        Assert.AreEqual(1d, byHour[12]);
        Assert.AreEqual(1d, byHour[14], "Two runs of one session are one session.");
        Assert.AreEqual(1d, byHour[23]);
        Assert.AreEqual(0d, byHour[11]);
        Assert.AreEqual(0, days.Query.Notes.Count(static note => note == "runs-of-unknown-time-left-out"));
    }

    [TestMethod]
    public async Task SessionsAtOnce_LeavesOutARunWhoseTimesAreNotKnown_AndSaysSo()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        await harness.AddAsync(Session("a", runs: [(600, 630)]));
        // A run with a record ten days after the others: where it was going between its first and its last time is not known.
        var odd = Session("odd", runs: [(610, 620)]);
        var run = odd.Runs.Values.Single();
        run.End = run.Start.AddDays(10);
        run.SkippedMs = (long)TimeSpan.FromDays(10).TotalMilliseconds - 15_000;
        await harness.AddAsync(odd);

        var result = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-06-10..2026-06-14", Frequency = StatisticsFrequency.Day }, "sessions-at-once");

        CollectionAssert.AreEqual(new[] { 1d, 0d, 0d, 0d, 0d }, Values(result), "The run is not taken for ten days of work, nor placed where it may not have been.");
        CollectionAssert.Contains(result.Query.Notes.ToArray(), "runs-of-unknown-time-left-out");
    }

    [TestMethod]
    public async Task SessionsAtOnce_FollowsTheFiltersOfARun_AndTheComparedPeriod()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        harness.Directory.Projects.Add(new ProjectInfo("project-1", "beta", "Beta", []));
        await harness.AddAsync(Session("a", runs: [(600, 630)]));
        var claude = Session("b", project: "project-1", runs: [(620, 640)]);
        claude.Runs.Values.Single().Provider = "claude";
        await harness.AddAsync(claude);
        // The day before: one session.
        await harness.AddAsync(Session("y", runs: [(-800, -790)]));

        var all = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-06-10..2026-06-10", Frequency = StatisticsFrequency.Day, Comparison = StatisticsComparison.PreviousPeriod }, "sessions-at-once");
        var codex = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-06-10..2026-06-10", Frequency = StatisticsFrequency.Day, Filter = new StatisticsFilter { Provider = "codex" } }, "sessions-at-once");
        var beta = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-06-10..2026-06-10", Frequency = StatisticsFrequency.Day, Filter = new StatisticsFilter { Project = "Beta" } }, "sessions-at-once");
        var tools = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-06-10..2026-06-10", Frequency = StatisticsFrequency.Day, Filter = new StatisticsFilter { ToolKind = "shell" } }, "sessions-at-once");

        CollectionAssert.AreEqual(new[] { 2d }, Values(all));
        CollectionAssert.AreEqual(new[] { 1d }, all.Series.Single().Previous!.ToArray());
        Assert.AreEqual(1d, all.Series.Single().PreviousTotal);
        CollectionAssert.AreEqual(new[] { 1d }, Values(codex));
        CollectionAssert.AreEqual(new[] { 1d }, Values(beta));
        CollectionAssert.Contains(tools.Query.IgnoredFilters.ToArray(), "toolKind");
        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-06-10..2026-06-10" }, "sessions-at-once", "provider"));
    }

    [TestMethod]
    [DataRow("UTC", StatisticsFrequency.Day)]
    [DataRow("Asia/Kolkata", StatisticsFrequency.Week)]
    [DataRow("America/St_Johns", StatisticsFrequency.Month)]
    [DataRow("Europe/Paris", StatisticsFrequency.Day)]
    public async Task SessionsAtOnce_EqualsANaiveCountOverTheRuns(string zone, StatisticsFrequency frequency)
    {
        // Many sessions over the same days, so that their runs do meet.
        await using var harness = await QueryHarness.CreateAsync(zone, sessions: 0);
        var random = new Random(11);
        for (var session = 0; session < 12; session++)
        {
            await harness.AddAsync(SyntheticFacts.Batch(random, "s-" + session, SyntheticFacts.SpringStart + 2000, 20 * 96, 400));
        }

        var request = new StatisticsRequest { Period = "2026-04-08..2026-05-05", Frequency = frequency, WeekStart = DayOfWeek.Monday };
        var result = await harness.Queries.SeriesAsync(request, "sessions-at-once");

        // The most sessions at once is reached at the start of a run: every start is looked at, with the sessions going at that moment.
        var runs = harness.Batches.SelectMany(static batch => batch.Runs.Values).ToList();
        var naive = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            var local = TimeZoneInfo.ConvertTime(run.Start, harness.TimeZone).DateTime;
            var date = DateOnly.FromDateTime(local);
            if (date < new DateOnly(2026, 4, 8) || date > new DateOnly(2026, 5, 5))
            {
                continue;
            }

            var going = runs.Where(other => other.Start <= run.Start && run.Start < (other.End > other.Start ? other.End : other.Start.AddMilliseconds(1))).Select(static other => other.SessionId).Distinct().Count();
            var key = QueryHarness.BucketKey(local, frequency, DayOfWeek.Monday);
            naive[key] = Math.Max(naive.GetValueOrDefault(key), going);
        }

        var expected = QueryHarness.Expected(result.Buckets, naive, frequency);
        var actual = Values(result);
        Assert.IsTrue(expected.Max() >= 2, "The sessions of the test do run at once.");
        for (var index = 0; index < expected.Length; index++)
        {
            // A run that started before a bucket and goes on into it is going there too: the bucket has at least the naive count.
            Assert.IsTrue(actual[index] >= expected[index], $"{result.Buckets[index].Start}: {actual[index]} < {expected[index]}");
        }

        Assert.AreEqual(expected.Max(), actual.Max(), "The most of the period is reached at the start of a run.");
    }

    [TestMethod]
    public async Task TheAverageFillOfTheContext_IsASumOverACount_NotAnAverageOfAverages()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        var batch = Session("a");
        var morning = QuarterHour.Of(Day.AddHours(9));
        var evening = QuarterHour.Of(Day.AddHours(20));
        // One request at 10% of the window, three at 90%: the average of the day is 70%, not the 50% of the two rows.
        var one = batch.UsageFor(new UsageKey(morning, "codex", "small", "", "default", UsagePurpose.Turn));
        one.Requests = 1;
        one.ContextSamples = 1;
        one.ContextFillPpmSum = 100_000;
        var three = batch.UsageFor(new UsageKey(evening, "codex", "large", "", "default", UsagePurpose.Turn));
        three.Requests = 3;
        three.ContextSamples = 3;
        three.ContextFillPpmSum = 2_700_000;
        // Requests that reported no window count for nothing.
        var none = batch.UsageFor(new UsageKey(evening, "codex", "blind", "", "default", UsagePurpose.Turn));
        none.Requests = 5;
        await harness.AddAsync(batch);
        var request = new StatisticsRequest { Period = "2026-06-10..2026-06-11", Frequency = StatisticsFrequency.Day };

        var fill = await harness.Queries.SeriesAsync(request, "context-fill");
        var samples = await harness.Queries.SeriesAsync(request, "context-samples");
        var byModel = await harness.Queries.SeriesAsync(request, "context-fill", "model");
        var small = await harness.Queries.SeriesAsync(request with { Filter = new StatisticsFilter { Model = "small" } }, "context-fill");
        var hours = await harness.Queries.SeriesAsync(request with { Period = "2026-06-10..2026-06-10", Frequency = StatisticsFrequency.Hour }, "context-fill");

        Assert.AreEqual("ratio", fill.Unit);
        Assert.AreEqual(0.7, Values(fill)[0], 1e-9);
        Assert.AreEqual(0d, Values(fill)[1], "A bucket without a sample has no average: the samples say which.");
        Assert.AreEqual(0.7, fill.Series.Single().Total, 1e-9, "The total of the line is the average of the period.");
        CollectionAssert.AreEqual(new[] { 4d, 0d }, Values(samples));
        CollectionAssert.AreEqual(new[] { "large", "small" }, byModel.Series.Select(static line => line.Key).ToArray(), "The lines are those of the models that reported a window, the most samples first.");
        Assert.AreEqual(0.9, byModel.Series[0].Values[0], 1e-9);
        Assert.AreEqual(0.1, byModel.Series[1].Values[0], 1e-9);
        Assert.AreEqual(0.1, Values(small)[0], 1e-9);
        Assert.AreEqual(0.1, Values(hours)[9], 1e-9);
        Assert.AreEqual(0.9, Values(hours)[20], 1e-9);
    }

    [TestMethod]
    public async Task TheAverageFillOfTheContext_EqualsTheSumsOfTheFacts_AndAddsTheRestAsOneLine()
    {
        await using var harness = await QueryHarness.CreateAsync("Europe/Paris");
        var request = new StatisticsRequest { Period = "2026-04-03..2026-09-28", Frequency = StatisticsFrequency.Month, Comparison = StatisticsComparison.PreviousPeriod };

        var fill = await harness.Queries.SeriesAsync(request, "context-fill");
        var limited = await harness.Queries.SeriesAsync(request with { Limit = 2 }, "context-fill", "model");

        var from = new DateOnly(2026, 4, 3);
        var to = new DateOnly(2026, 9, 28);
        var sums = harness.Naive(static batch => batch.Usage.Select(static pair => (pair.Key.Quarter, (double)pair.Value.ContextFillPpmSum)), StatisticsFrequency.Month, from, to, DayOfWeek.Monday);
        var counts = harness.Naive(static batch => batch.Usage.Select(static pair => (pair.Key.Quarter, (double)pair.Value.ContextSamples)), StatisticsFrequency.Month, from, to, DayOfWeek.Monday);
        var expectedSums = QueryHarness.Expected(fill.Buckets, sums, StatisticsFrequency.Month);
        var expectedCounts = QueryHarness.Expected(fill.Buckets, counts, StatisticsFrequency.Month);
        var line = fill.Series.Single();
        for (var index = 0; index < expectedSums.Length; index++)
        {
            Assert.AreEqual(expectedCounts[index] > 0 ? expectedSums[index] / expectedCounts[index] / 1e6 : 0, line.Values[index], 1e-9, fill.Buckets[index].Start);
        }

        Assert.AreEqual(expectedSums.Sum() / expectedCounts.Sum() / 1e6, line.Total, 1e-9);
        Assert.IsNotNull(line.Previous);
        Assert.IsNotNull(line.PreviousTotal);

        // Two models and the rest: the rest is the average of all the others, their sums over their counts.
        Assert.AreEqual(3, limited.Series.Count);
        Assert.AreEqual("other", limited.Series[2].Key);
        var kept = limited.Series.Take(2).Select(static item => item.Key).ToHashSet(StringComparer.Ordinal);
        var restSums = harness.Naive(batch => batch.Usage.Where(pair => !kept.Contains(pair.Key.Model)).Select(static pair => (pair.Key.Quarter, (double)pair.Value.ContextFillPpmSum)), StatisticsFrequency.Month, from, to, DayOfWeek.Monday);
        var restCounts = harness.Naive(batch => batch.Usage.Where(pair => !kept.Contains(pair.Key.Model)).Select(static pair => (pair.Key.Quarter, (double)pair.Value.ContextSamples)), StatisticsFrequency.Month, from, to, DayOfWeek.Monday);
        Assert.AreEqual(restSums.Values.Sum() / restCounts.Values.Sum() / 1e6, limited.Series[2].Total, 1e-9);
    }

    [TestMethod]
    public async Task TheDepthOfTheSubAgents_FollowsTheParents_AsFarAsTheyAreKnown()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        await harness.AddAsync(Session("root"));
        await harness.AddAsync(Session("child-1", parent: "root"));
        await harness.AddAsync(Session("child-2", parent: "root"));
        await harness.AddAsync(Session("grandchild", parent: "child-1"));
        await harness.AddAsync(Session("great-grandchild", parent: "grandchild"));
        // Started in another month: not a session of the period, but a parent that is known.
        await harness.AddAsync(Session("old-root", first: Day.AddDays(-60)));
        await harness.AddAsync(Session("child-of-old", parent: "old-root"));
        var request = new StatisticsRequest { Period = "2026-06-01..2026-06-30" };

        var depth = await harness.Queries.DetailsAsync(request, "sub-agent-depth");

        Assert.AreEqual("sub-agent-depth", depth.List);
        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, depth.Rows.Select(static row => row.Name).ToArray(), "The rows are in the order of the depth.");
        CollectionAssert.AreEqual(new[] { 3L, 1L, 1L }, depth.Rows.Select(static row => row.Count).ToArray());
        Assert.AreEqual(5L, depth.Total, "The sessions with a parent that started in the period.");
        Assert.AreEqual(0.6, depth.Rows[0].Share, 1e-9);
        Assert.AreEqual(0, depth.Query.Notes.Count(static note => note == "some-parents-unknown"));
    }

    [TestMethod]
    public async Task TheDepthOfTheSubAgents_StopsAtAParentThatIsNotKnown_AndAtAParentThatComesBack()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        harness.Directory.Projects.Add(new ProjectInfo("project-1", "beta", "Beta", []));
        await harness.AddAsync(Session("root"));
        await harness.AddAsync(Session("child", parent: "root"));
        // The parent was never read (it is older than the history that was chosen), or its numbers were forgotten.
        await harness.AddAsync(Session("orphan", parent: "never-read"));
        await harness.AddAsync(Session("child-of-orphan", parent: "orphan", project: "project-1"));
        // Two sessions that name each other, and one that names itself: journals that were edited by hand.
        await harness.AddAsync(Session("loop-a", parent: "loop-b"));
        await harness.AddAsync(Session("loop-b", parent: "loop-a"));
        await harness.AddAsync(Session("self", parent: "self"));
        var request = new StatisticsRequest { Period = "2026-06-01..2026-06-30" };

        var depth = await harness.Queries.DetailsAsync(request, "sub-agent-depth").AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        var beta = await harness.Queries.DetailsAsync(request with { Filter = new StatisticsFilter { Project = "Beta", Model = "m" } }, "sub-agent-depth");

        // The parent that is not known is the top of what is known: orphan is at 1 and its child at 2. A loop stops where it comes
        // back: a session that names itself is at 1, two that name each other are at 2.
        Assert.AreEqual(6L, depth.Total);
        CollectionAssert.AreEqual(new[] { "1", "2" }, depth.Rows.Select(static row => row.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 3L, 3L }, depth.Rows.Select(static row => row.Count).ToArray());
        CollectionAssert.Contains(depth.Query.Notes.ToArray(), "some-parents-unknown");
        Assert.AreEqual(1L, beta.Total, "The project and the space filter the sessions that are counted, not their parents.");
        Assert.AreEqual("2", beta.Rows.Single().Name);
        CollectionAssert.Contains(beta.Query.IgnoredFilters.ToArray(), "model");
    }

    [TestMethod]
    public void TheNewMetricsAndTheNewList_AreNamed()
    {
        CollectionAssert.IsSubsetOf(new[] { "sessions-at-once", "context-fill", "context-samples" }, StatisticsQueries.MetricNames.ToArray());
        CollectionAssert.Contains(StatisticsQueries.DetailNames.ToArray(), "sub-agent-depth");
    }

    // ----- one session and its sub-agents -----
    //
    // The three numbers under a session filter (`StatisticsSessionFilterTests` has the facts): each is asked for a session with its
    // sub-agents in a store that also holds the sessions of someone else, and none counts them.

    private static void AddFill(FactBatch batch, string model, int requests, long fillPpmSum)
    {
        var usage = batch.UsageFor(new UsageKey(QuarterHour.Of(Day.AddHours(10)), "codex", model, "", "default", UsagePurpose.Turn));
        usage.Requests += requests;
        usage.ContextSamples += requests;
        usage.ContextFillPpmSum += fillPpmSum;
        usage.ContextFillPpmMax = Math.Max(usage.ContextFillPpmMax, fillPpmSum / requests);
    }

    private static StatisticsRequest Scoped(string? session, bool withChildren = true, string period = "2026-06-10..2026-06-10", StatisticsFrequency frequency = StatisticsFrequency.Hour)
        => new() { Period = period, Frequency = frequency, Filter = session is null ? new StatisticsFilter() : new StatisticsFilter { Session = session, WithChildren = withChildren } };

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

        var everything = await harness.Queries.SeriesAsync(Scoped(null), "sessions-at-once");
        var tree = await harness.Queries.SeriesAsync(Scoped("root"), "sessions-at-once");
        var alone = await harness.Queries.SeriesAsync(Scoped("root", withChildren: false), "sessions-at-once");
        var subAgent = await harness.Queries.SeriesAsync(Scoped("child"), "sessions-at-once");
        var unknown = await harness.Queries.SeriesAsync(Scoped("nobody"), "sessions-at-once");
        var life = await harness.Queries.SeriesAsync(Scoped("root", period: "all", frequency: StatisticsFrequency.Auto), "sessions-at-once");

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
        var model = await harness.Queries.SeriesAsync(Scoped("root") with { Filter = new StatisticsFilter { Session = "root", WithChildren = true, Model = "elsewhere" } }, "sessions-at-once");
        Assert.IsTrue(Values(model).All(static value => value == 0));
    }

    [TestMethod]
    public async Task TheAverageFillOfTheContext_OfASession_IsOfItsRequestsAndThoseOfItsSubAgents()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        // The session: one request at 10% of the window. Its sub-agent: three at 90%. Someone else: four at 50%, on the model of the sub-agent.
        var root = Session("root");
        AddFill(root, "small", 1, 100_000);
        var child = Session("child", parent: "root");
        AddFill(child, "large", 3, 2_700_000);
        var other = Session("other");
        AddFill(other, "large", 4, 2_000_000);
        await harness.AddAsync(root);
        await harness.AddAsync(child);
        await harness.AddAsync(other);
        var day = StatisticsFrequency.Day;

        var everything = await harness.Queries.SeriesAsync(Scoped(null, frequency: day), "context-fill");
        var tree = await harness.Queries.SeriesAsync(Scoped("root", frequency: day), "context-fill");
        var alone = await harness.Queries.SeriesAsync(Scoped("root", withChildren: false, frequency: day), "context-fill");
        var byModel = await harness.Queries.SeriesAsync(Scoped("root", frequency: day), "context-fill", "model");
        var samples = await harness.Queries.SeriesAsync(Scoped("root", frequency: day), "context-samples");
        var unknown = await harness.Queries.SeriesAsync(Scoped("nobody", frequency: day), "context-fill");

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
        var models = await harness.Queries.ModelsAsync(Scoped("root", frequency: day));
        Assert.AreEqual(0.9, models.Rows.Single(static row => row.Model == "large").AverageContextFill!.Value, 1e-9);
        Assert.AreEqual(3L, models.Rows.Single(static row => row.Model == "large").Requests);
        var health = await harness.Queries.HealthAsync(Scoped("root", frequency: day));
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

        var everything = Rows(await harness.Queries.DetailsAsync(Scoped(null), "sub-agent-depth"));
        var midTree = await harness.Queries.DetailsAsync(Scoped("mid"), "sub-agent-depth");
        var mid = Rows(midTree);
        var midAlone = Rows(await harness.Queries.DetailsAsync(Scoped("mid", withChildren: false), "sub-agent-depth"));
        var top = Rows(await harness.Queries.DetailsAsync(Scoped("top"), "sub-agent-depth"));
        var topAlone = Rows(await harness.Queries.DetailsAsync(Scoped("top", withChildren: false), "sub-agent-depth"));
        var unknown = await harness.Queries.DetailsAsync(Scoped("nobody"), "sub-agent-depth");

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
