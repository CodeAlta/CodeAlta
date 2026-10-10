using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// The session filter of the questions: the numbers of one session and of its sub-agents, on every page, beside the other filters,
/// and what a session that is not known gives.
/// </summary>
[TestClass]
public sealed class StatisticsSessionFilterTests
{
    private const string Year = "2026-01-01..2026-12-31";
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 12, 31);

    // session-1 created session-2 (QueryHarness); here session-2 created a session of its own, so the tree has two levels.
    internal static async Task<QueryHarness> TreeAsync()
    {
        var harness = await QueryHarness.CreateAsync("UTC");
        var grandchild = SyntheticFacts.Batch(new Random(77), "grandchild", SyntheticFacts.SpringStart + 50, 100 * 96, 60);
        grandchild.Session!.ParentSessionId = "session-2";
        await harness.AddAsync(grandchild);
        return harness;
    }

    private static StatisticsRequest Scoped(string session, bool withChildren = true, StatisticsFrequency frequency = StatisticsFrequency.Month)
        => new() { Period = Year, Frequency = frequency, Limit = 500, Filter = new StatisticsFilter { Session = session, WithChildren = withChildren } };

    private static double Sum(QueryHarness harness, Func<FactBatch, IEnumerable<(QuarterHour Quarter, double Value)>> values, IReadOnlySet<string> sessions)
        => harness.Naive(values, StatisticsFrequency.Year, From, To, DayOfWeek.Monday, batch => sessions.Contains(batch.SessionId)).Values.Sum();

    [TestMethod]
    public async Task ASessionFilter_LimitsTheSeriesToTheSessionAndItsSubAgents_AtAnyDepth()
    {
        await using var harness = await TreeAsync();
        var tree = new HashSet<string>(StringComparer.Ordinal) { "session-1", "session-2", "grandchild" };
        var alone = new HashSet<string>(StringComparer.Ordinal) { "session-1" };

        foreach (var (metric, values) in new (string, Func<FactBatch, IEnumerable<(QuarterHour, double)>>)[]
                 {
                     ("active-time", QueryHarness.ActiveMs), ("runs", QueryHarness.Runs), ("tokens", QueryHarness.Tokens), ("tool-calls", QueryHarness.ToolCalls), ("your-prompts", QueryHarness.YourPrompts),
                 })
        {
            var withChildren = await harness.Queries.SeriesAsync(Scoped("session-1"), metric);
            var single = await harness.Queries.SeriesAsync(Scoped("session-1", withChildren: false), metric);

            var naive = harness.Naive(values, StatisticsFrequency.Month, From, To, DayOfWeek.Monday, batch => tree.Contains(batch.SessionId));
            CollectionAssert.AreEqual(QueryHarness.Expected(withChildren.Buckets, naive, StatisticsFrequency.Month), withChildren.Series.Single().Values.ToArray(), metric);
            Assert.AreEqual(Sum(harness, values, alone), single.Series.Single().Total, 0.001, metric + " of the session alone");
            Assert.IsTrue(withChildren.Series.Single().Total > single.Series.Single().Total, metric + ": the sub-agents add to the session");
            Assert.IsFalse(withChildren.Query.Notes.Contains("session-not-found"));
        }

        // The start of an identifier names the session when it is the only one that starts so.
        var byStart = await harness.Queries.SeriesAsync(Scoped("grandch", withChildren: false), "tokens");
        Assert.AreEqual(Sum(harness, QueryHarness.Tokens, new HashSet<string> { "grandchild" }), byStart.Series.Single().Total, 0.001);
        // A sub-agent is the top of its own tree: its parent is not in it.
        var child = await harness.Queries.SeriesAsync(Scoped("session-2"), "tokens");
        Assert.AreEqual(Sum(harness, QueryHarness.Tokens, new HashSet<string> { "session-2", "grandchild" }), child.Series.Single().Total, 0.001);
    }

    [TestMethod]
    public async Task EveryPage_HonorsTheSessionFilter()
    {
        await using var harness = await TreeAsync();
        var tree = new HashSet<string>(StringComparer.Ordinal) { "session-1", "session-2", "grandchild" };
        var request = Scoped("session-1");
        var batches = harness.Batches.Where(batch => tree.Contains(batch.SessionId)).ToList();
        var tokens = Sum(harness, QueryHarness.Tokens, tree);
        var time = Sum(harness, QueryHarness.ActiveMs, tree);
        var calls = Sum(harness, QueryHarness.ToolCalls, tree);
        var runs = Sum(harness, QueryHarness.Runs, tree);
        Assert.IsTrue(tokens > 0 && time > 0 && calls > 0 && runs > 0, "the tree has facts in the test");
        var everything = await harness.Queries.SummaryAsync(request with { Filter = new StatisticsFilter() });

        // Overview.
        var summary = await harness.Queries.SummaryAsync(request);
        double Tile(string id) => summary.Tiles.Single(tile => tile.Id == id).Value;
        Assert.AreEqual(3, Tile("sessions"), "the session, its sub-agent and the sub-agent of that one");
        Assert.AreEqual(tokens, Tile("tokens"), 0.001);
        Assert.AreEqual(time, Tile("active-time"), 0.001);
        Assert.AreEqual(calls, Tile("tool-calls"), 0.001);
        Assert.AreEqual(runs, Tile("runs"), 0.001);
        Assert.IsTrue(Tile("tokens") < everything.Tiles.Single(static tile => tile.Id == "tokens").Value, "the numbers of the tree are not those of every session");
        var records = await harness.Queries.RecordsAsync(request);
        Assert.IsNotEmpty(records.Records);
        Assert.IsTrue(records.Records.Where(static record => record.SessionId is not null).All(record => tree.Contains(record.SessionId!)), "a record of the tree is a run or a call of the tree");
        var longest = batches.SelectMany(static batch => batch.Extremes).Where(static pair => pair.Key.Measure == ExtremeMeasure.LongestRunMs).Max(static pair => pair.Value.Value);
        Assert.AreEqual(longest, records.Records.Single(static record => record.Measure == "longestRun").Value);

        // Activity.
        var active = await harness.Queries.SeriesAsync(request with { Frequency = StatisticsFrequency.Year }, "sessions-active");
        Assert.AreEqual(3, active.Series.Single().Total);
        var started = await harness.Queries.SeriesAsync(request with { Frequency = StatisticsFrequency.Year }, "sessions-started", "delegated");
        Assert.AreEqual(1, started.Series.Single(static line => line.Key == "direct").Total);
        Assert.AreEqual(2, started.Series.Single(static line => line.Key == "sub-agent").Total);
        var calendar = await harness.Queries.CalendarAsync(request);
        Assert.AreEqual(time, calendar.Days.Sum(static day => day.ActiveMs), 0.001);
        var week = await harness.Queries.WeekHourAsync(request);
        Assert.AreEqual(time, week.ActiveMs.Sum(static row => row.Sum()), 0.001);
        var distribution = await harness.Queries.DistributionAsync(request, "run-duration");
        var observed = batches.SelectMany(static batch => batch.Histograms).Where(static pair => pair.Key.Measure == HistogramMeasure.RunDurationMs).Sum(static pair => pair.Value);
        Assert.AreEqual(observed, distribution.Count);

        // Models, Cost, Tools, Code.
        var models = await harness.Queries.ModelsAsync(request);
        Assert.AreEqual(tokens, models.Rows.Sum(static row => (double)(row.InputTokens + row.OutputTokens)), 0.001);
        var cost = await harness.Queries.SeriesAsync(request with { Frequency = StatisticsFrequency.Year }, "cost");
        foreach (var line in cost.Series.Where(static line => line.Key.Length > 0))
        {
            Assert.AreEqual(Sum(harness, batch => QueryHarness.Cost(batch, line.Key), tree), line.Total, 0.001, "cost in " + line.Key);
        }

        var tools = await harness.Queries.ToolsAsync(request);
        Assert.AreEqual(calls, tools.Rows.Sum(static row => (double)row.Calls), 0.001);
        var top = await harness.Queries.TopAsync(request, "tools", "calls");
        Assert.AreEqual(calls, top.Rows.Sum(static row => row.Calls), 0.001);
        var lines = await harness.Queries.SeriesAsync(request with { Frequency = StatisticsFrequency.Year }, "lines-added");
        Assert.AreEqual(batches.SelectMany(static batch => batch.Tools.Values).Sum(static tool => (double)tool.LinesAdded), lines.Series.Single().Total, 0.001);
        var details = await harness.Queries.DetailsAsync(request, "shell-program");
        Assert.AreEqual(batches.SelectMany(static batch => batch.Details).Where(static pair => pair.Key.List == DetailList.ShellProgram).Sum(static pair => pair.Value), details.Total);

        // Prompts, Agents.
        var prompts = await harness.Queries.RunsAsync(request, "recent");
        Assert.AreEqual(batches.Sum(static batch => batch.Runs.Count), prompts.TotalRows);
        Assert.IsTrue(prompts.Runs.All(run => tree.Contains(run.SessionId)));
        var delegated = await harness.Queries.SeriesAsync(request with { Frequency = StatisticsFrequency.Year }, "tokens", "delegated");
        Assert.AreEqual(Sum(harness, QueryHarness.Tokens, new HashSet<string> { "session-1" }), delegated.Series.Single(static line => line.Key == "direct").Total, 0.001);
        Assert.AreEqual(Sum(harness, QueryHarness.Tokens, new HashSet<string> { "session-2", "grandchild" }), delegated.Series.Single(static line => line.Key == "sub-agent").Total, 0.001);

        // Projects, Sessions.
        var projects = await harness.Queries.ProjectsAsync(request);
        Assert.AreEqual(tokens, projects.Rows.Sum(static row => row.Tokens), 0.001);
        Assert.AreEqual(3, projects.Rows.Sum(static row => row.Sessions));
        var sessions = await harness.Queries.SessionsAsync(request, "tokens");
        CollectionAssert.AreEquivalent(tree.ToArray(), sessions.Rows.Select(static row => row.SessionId).ToArray(), "the table lists the session and its sub-agents, each one click from its chat");
        var ranked = await harness.Queries.TopAsync(request, "sessions", "tokens");
        CollectionAssert.AreEquivalent(tree.ToArray(), ranked.Rows.Select(static row => row.Key).ToArray());

        // Health.
        var health = await harness.Queries.HealthAsync(request);
        Assert.AreEqual(batches.SelectMany(static batch => batch.Activity.Values).Sum(static value => (double)value.Errors), health.Errors.Sum(), 0.001);
        Assert.AreEqual((long)runs, health.Runs);
    }

    [TestMethod]
    public async Task ASessionFilter_AddsToTheOthers_AndASpaceKeepsItsChatsAndProjects()
    {
        await using var harness = await TreeAsync();
        harness.Directory.Projects.AddRange(Enumerable.Range(0, 3).Select(index => new ProjectInfo("project-" + index, "p" + index, "Project " + index, index == 0 ? new[] { "work" } : [])));
        harness.Directory.Spaces.Add(new SpaceInfo("work", "Work", false));
        var tree = new HashSet<string>(StringComparer.Ordinal) { "session-1", "session-2", "grandchild" };
        var request = Scoped("session-1", frequency: StatisticsFrequency.Year);
        var projectOf = harness.Batches.ToDictionary(static batch => batch.SessionId, static batch => batch.Session!.ProjectRef!);

        // A project: the sessions of the tree that are of the project, and no other session of the project.
        foreach (var project in new[] { "project-0", "project-1", "project-2" })
        {
            var inProject = new HashSet<string>(tree.Where(id => projectOf[id] == project), StringComparer.Ordinal);
            var result = await harness.Queries.SeriesAsync(request with { Filter = request.Filter with { Project = project } }, "tokens");
            Assert.AreEqual(Sum(harness, QueryHarness.Tokens, inProject), result.Series.Single().Total, 0.001, project);
        }

        // A named space: its projects today, as without a session.
        var inWork = new HashSet<string>(tree.Where(id => projectOf[id] == "project-0"), StringComparer.Ordinal);
        var work = await harness.Queries.SeriesAsync(request with { Filter = request.Filter with { Space = "work" } }, "tokens");
        Assert.AreEqual(Sum(harness, QueryHarness.Tokens, inWork), work.Series.Single().Total, 0.001);
        CollectionAssert.Contains(work.Query.Notes.ToArray(), "space-membership-is-current");

        // The space that has every project changes nothing.
        var everywhere = await harness.Queries.SeriesAsync(request with { Filter = request.Filter with { Space = "default" } }, "tokens");
        Assert.AreEqual(Sum(harness, QueryHarness.Tokens, tree), everywhere.Series.Single().Total, 0.001);

        // A chat (a session of no project) is in every space, alone or as the top of a tree.
        var chat = SyntheticFacts.Batch(new Random(5), "chat", SyntheticFacts.SpringStart + 10, 50 * 96, 40);
        chat.Session!.ProjectRef = null;
        await harness.AddAsync(chat);
        var chatInWork = await harness.Queries.SeriesAsync(Scoped("chat", frequency: StatisticsFrequency.Year) with { Filter = new StatisticsFilter { Session = "chat", WithChildren = true, Space = "work" } }, "tokens");
        Assert.AreEqual(Sum(harness, QueryHarness.Tokens, new HashSet<string> { "chat" }), chatInWork.Series.Single().Total, 0.001);

        // A provider narrows the tree, and never widens it.
        var codex = await harness.Queries.SeriesAsync(request with { Filter = request.Filter with { Provider = "codex" } }, "tokens");
        var expected = harness.Batches.Where(batch => tree.Contains(batch.SessionId)).SelectMany(static batch => batch.Usage).Where(static pair => pair.Key.Provider == "codex").Sum(static pair => (double)(pair.Value.InputTokens + pair.Value.OutputTokens));
        Assert.AreEqual(expected, codex.Series.Single().Total, 0.001);
    }

    [TestMethod]
    public async Task TheWholeHistoryOfASession_IsItsLife_FromItsFirstDayToItsLast()
    {
        // Today is long after the sessions: "all" of everything ends today, "all" of a session ends with the session.
        await using var harness = await QueryHarness.CreateAsync("Europe/Paris", now: new DateTimeOffset(2027, 2, 1, 12, 0, 0, TimeSpan.Zero));
        var short1 = SyntheticFacts.Batch(new Random(21), "short", QuarterHour.Of(new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero)).Index, 2 * 96, 40);
        short1.Session!.FirstRecord = new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);
        short1.Session.LastRecord = new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero);
        var child = SyntheticFacts.Batch(new Random(22), "short-child", QuarterHour.Of(new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero)).Index, 3 * 96, 40);
        child.Session!.ParentSessionId = "short";
        child.Session.FirstRecord = new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero);
        child.Session.LastRecord = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        await harness.AddAsync(short1);
        await harness.AddAsync(child);
        static StatisticsRequest All(StatisticsFilter filter) => new() { Period = "all", Filter = filter };

        var everything = await harness.Queries.SeriesAsync(All(new StatisticsFilter()), "tokens");
        var alone = await harness.Queries.SeriesAsync(All(new StatisticsFilter { Session = "short" }), "tokens");
        var tree = await harness.Queries.SeriesAsync(All(new StatisticsFilter { Session = "short", WithChildren = true }), "tokens");
        var unknown = await harness.Queries.SeriesAsync(All(new StatisticsFilter { Session = "nobody" }), "tokens");
        var month = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-07-01..2026-07-31", Filter = new StatisticsFilter { Session = "short" } }, "tokens");

        Assert.AreEqual("2027-02-01", everything.Query.To, "every session: to today");
        Assert.AreEqual(("2026-07-10", "2026-07-12"), (alone.Query.From, alone.Query.To));
        Assert.AreEqual(("2026-07-10", "2026-07-15"), (tree.Query.From, tree.Query.To), "with its sub-agents: to the last day of the last of them");
        Assert.AreEqual("day", alone.Query.Frequency, "a few days are cut in days, where the months of every session are cut in weeks or months");
        Assert.AreEqual("all", alone.Query.Period, "the period is echoed as it was asked");
        Assert.AreEqual((everything.Query.From, everything.Query.To), (unknown.Query.From, unknown.Query.To), "a session that is not known has no life to show");
        Assert.AreEqual(("2026-07-01", "2026-07-31"), (month.Query.From, month.Query.To), "a period that names its days keeps them");
        // Nothing of the session is outside its life: the totals are those of its facts.
        Assert.AreEqual(short1.Usage.Values.Sum(static value => (double)(value.InputTokens + value.OutputTokens)), alone.Series.Single().Total, 0.001);
        Assert.AreEqual(short1.Usage.Values.Concat(child.Usage.Values).Sum(static value => (double)(value.InputTokens + value.OutputTokens)), tree.Series.Single().Total, 0.001);
        // The session by its own command is as it was: the whole history of the statistics.
        Assert.AreEqual("2027-02-01", (await harness.Queries.SessionAsync("short"))!.Query.To);
    }

    [TestMethod]
    public async Task ASessionTheStatisticsDoNotKnow_IsNothing_NeverEverySession()
    {
        await using var harness = await TreeAsync();
        var unknown = Scoped("01a00000-0000-7000-8000-000000000000");

        var summary = await harness.Queries.SummaryAsync(unknown);
        Assert.IsTrue(summary.Tiles.All(static tile => tile.Value == 0), "no number of another session");
        Assert.IsEmpty(summary.Costs);
        CollectionAssert.Contains(summary.Query.Notes.ToArray(), "session-not-found");
        Assert.AreEqual(0, (await harness.Queries.SeriesAsync(unknown, "tokens")).Series.Single().Total);
        Assert.IsEmpty((await harness.Queries.SessionsAsync(unknown, "recent")).Rows);
        Assert.IsEmpty((await harness.Queries.ModelsAsync(unknown)).Rows);
        Assert.IsEmpty((await harness.Queries.ToolsAsync(unknown)).Rows);
        Assert.IsEmpty((await harness.Queries.ProjectsAsync(unknown)).Rows);
        Assert.IsEmpty((await harness.Queries.RunsAsync(unknown, "recent")).Runs);
        Assert.IsEmpty((await harness.Queries.RecordsAsync(unknown)).Records);
        Assert.AreEqual(0, (await harness.Queries.HealthAsync(unknown)).Runs);
        Assert.IsEmpty((await harness.Queries.CalendarAsync(unknown)).Days);
        Assert.AreEqual(0, (await harness.Queries.DistributionAsync(unknown, "run-duration")).Count);
        Assert.IsEmpty((await harness.Queries.TopAsync(unknown, "sessions", "tokens")).Rows);
        Assert.IsEmpty((await harness.Queries.DetailsAsync(unknown, "shell-program")).Rows);

        // A session that is named and empty is a request that is not valid, as the start of several sessions is.
        foreach (var blank in new[] { string.Empty, "   " })
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.SummaryAsync(Scoped(blank)));
        }

        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.SummaryAsync(Scoped("session-")));

        // Sub-agents of no session are every session: the switch alone is no filter.
        var everything = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = Year, Filter = new StatisticsFilter { WithChildren = true } }, "tokens");
        Assert.AreEqual(harness.Batches.SelectMany(static batch => batch.Usage.Values).Sum(static value => (double)(value.InputTokens + value.OutputTokens)), everything.Series.Single().Total, 0.001);
    }

    [TestMethod]
    public async Task SessionsThatNameEachOtherAsParents_EndTheWalk()
    {
        await using var harness = await QueryHarness.CreateAsync("UTC", sessions: 2);
        foreach (var (id, parent, seed) in new[] { ("loop-a", "loop-b", 11), ("loop-b", "loop-c", 12), ("loop-c", "loop-a", 13) })
        {
            var batch = SyntheticFacts.Batch(new Random(seed), id, SyntheticFacts.SpringStart, 30 * 96, 30);
            batch.Session!.ParentSessionId = parent;
            await harness.AddAsync(batch);
        }

        var loop = new HashSet<string>(StringComparer.Ordinal) { "loop-a", "loop-b", "loop-c" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var series = await harness.Queries.SeriesAsync(Scoped("loop-b"), "tokens", cancellationToken: timeout.Token);
        var sessions = await harness.Queries.SessionsAsync(Scoped("loop-b"), "tokens", timeout.Token);
        var detail = await harness.Queries.SessionAsync("loop-b", withChildren: true, timeout.Token);

        Assert.AreEqual(Sum(harness, QueryHarness.Tokens, loop), series.Series.Single().Total, 0.001, "each session of the circle once");
        CollectionAssert.AreEquivalent(loop.ToArray(), sessions.Rows.Select(static row => row.SessionId).ToArray());
        Assert.IsNotNull(detail);
        Assert.AreEqual("loop-b", detail.Session.SessionId);
        CollectionAssert.AreEquivalent(new[] { "loop-a", "loop-c" }, detail.Children.Select(static child => child.SessionId).ToArray());
    }

    [TestMethod]
    public async Task ASessionWhoseIdentifierStartsOthers_IsFoundByItsWholeIdentifier()
    {
        await using var harness = await QueryHarness.CreateAsync("UTC", sessions: 2);
        // Many sessions start with the identifier of one: the one of that exact identifier is still the one that is named.
        foreach (var id in new[] { "session-10", "session-11", "session-12", "session-13" })
        {
            await harness.AddAsync(SyntheticFacts.Batch(new Random(3), id, SyntheticFacts.SpringStart, 30 * 96, 20));
        }

        var exact = await harness.Queries.SeriesAsync(Scoped("session-1", withChildren: false), "tokens");

        Assert.AreEqual(Sum(harness, QueryHarness.Tokens, new HashSet<string> { "session-1" }), exact.Series.Single().Total, 0.001);
        Assert.AreEqual("session-1", (await harness.Queries.SessionAsync("session-1"))!.Session.SessionId);
    }

    [TestMethod]
    public void TheSessionOfARequest_IsReadFromTheJsonOfAPage()
    {
        var request = StatisticsJson.ParseRequest("""{"period":"all","filter":{"session":"01a124b3-fcbd-7fa9-917c-85525ea5444e","withChildren":true,"provider":"codex"}}""");

        Assert.AreEqual("01a124b3-fcbd-7fa9-917c-85525ea5444e", request.Filter.Session);
        Assert.IsTrue(request.Filter.WithChildren);
        Assert.AreEqual("codex", request.Filter.Provider);
        Assert.IsFalse(request.Filter.IsEmpty);
        Assert.IsFalse(new StatisticsFilter { Session = "x" }.IsEmpty);
        var plain = StatisticsJson.ParseRequest("""{"filter":{"session":"abc"}}""");
        Assert.IsFalse(plain.Filter.WithChildren, "the session alone, unless its sub-agents are asked for");
        Assert.IsNull(StatisticsJson.ParseRequest("""{"filter":{"project":"p"}}""").Filter.Session);
    }
}
