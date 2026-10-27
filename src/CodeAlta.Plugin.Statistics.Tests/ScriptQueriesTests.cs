using CodeAlta.Agent;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>The questions about the runs of scripts: their arithmetic against a naive recount, and what joins them to their sessions.</summary>
[TestClass]
public sealed class ScriptQueriesTests
{
    private const string ParentId = "11111111-1111-1111-1111-111111111111";
    private const string ChildId = "55555555-5555-5555-5555-555555555555";
    private static readonly string[] Scripts = ["review-pr", "nightly", ScriptLabels.Inline, ScriptLabels.Unknown];

    // Runs of scripts spread over half a year: a session each, under random scripts, models and origins.
    private static async Task AddScriptsAsync(QueryHarness harness, int seed)
    {
        var random = new Random(seed);
        for (var session = 0; session < 4; session++)
        {
            var batch = new FactBatch("scripts-" + session);
            for (var index = 0; index < 60; index++)
            {
                var quarter = new QuarterHour(SyntheticFacts.SpringStart + random.Next(200 * 96));
                var measures = batch.ScriptFor(new ScriptKey(quarter, SyntheticFacts.Pick(random, ["codex", "claude"]), SyntheticFacts.Pick(random, ["gpt-6.1-sol", "claude-opus-5-5"]),
                    SyntheticFacts.Pick(random, Scripts), SyntheticFacts.Pick(random, ["you", "agent", "automation", "resume"])));
                measures.RunsStarted += random.Next(0, 3);
                measures.RunsCompleted += random.Next(0, 3);
                measures.RunsFailed += random.Next(0, 2);
                measures.RunsCancelled += random.Next(0, 2);
                measures.RunsInterrupted += random.Next(0, 2);
                measures.DurationCount += 1;
                measures.DurationMs += random.Next(1_000, 600_000);
                measures.WaitMs += random.Next(0, 60_000);
                measures.Calls += random.Next(0, 40);
                measures.Steps += random.Next(0, 6);
                measures.StepsFailed += random.Next(0, 2);
                measures.SessionsStarted += random.Next(0, 4);
                var tool = batch.ToolFor(new ToolKey(quarter, "codex", ToolKind.Files, "ToolCall:read_file"));
                tool.Calls += 5;
                tool.ScriptCalls += random.Next(0, 5);
            }

            batch.Session = new SessionRow { SessionId = batch.SessionId, SessionKind = "ProjectSession", Provider = "codex", FirstRecord = new QuarterHour(SyntheticFacts.SpringStart).Start };
            await harness.AddAsync(batch);
        }
    }

    private static Func<FactBatch, IEnumerable<(QuarterHour, double)>> Of(Func<ScriptMeasures, long> value)
        => batch => batch.Scripts.Select(pair => (pair.Key.Quarter, (double)value(pair.Value)));

    [TestMethod]
    [DataRow("UTC")]
    [DataRow("Europe/Paris")]
    [DataRow("America/St_Johns")]
    public async Task TheRunsOfScripts_EqualANaiveRecomputationFromTheFacts(string zone)
    {
        await using var harness = await QueryHarness.CreateAsync(zone, sessions: 2);
        await AddScriptsAsync(harness, 17);
        var from = new DateOnly(2026, 4, 3);
        var to = new DateOnly(2026, 9, 28);
        var request = new StatisticsRequest { Period = "2026-04-03..2026-09-28", Frequency = StatisticsFrequency.Week, WeekStart = DayOfWeek.Monday, Comparison = StatisticsComparison.None };

        foreach (var (metric, values) in new (string, Func<FactBatch, IEnumerable<(QuarterHour, double)>>)[]
                 {
                     ("script-runs", Of(static value => value.RunsStarted)),
                     ("script-runs-completed", Of(static value => value.RunsCompleted)),
                     ("script-runs-failed", Of(static value => value.RunsFailed)),
                     ("script-runs-cancelled", Of(static value => value.RunsCancelled)),
                     ("script-runs-interrupted", Of(static value => value.RunsInterrupted)),
                     ("script-time", Of(static value => value.DurationMs)),
                     ("script-wait-time", Of(static value => value.WaitMs)),
                     ("script-sessions", Of(static value => value.SessionsStarted)),
                     ("script-calls", static batch => batch.Tools.Select(static pair => (pair.Key.Quarter, (double)pair.Value.ScriptCalls))),
                 })
        {
            CollectionAssert.Contains(StatisticsQueries.MetricNames.ToList(), metric);
            var result = await harness.Queries.SeriesAsync(request, metric);

            var naive = harness.Naive(values, StatisticsFrequency.Week, from, to, DayOfWeek.Monday);
            Assert.IsTrue(naive.Values.Sum() > 0, metric + " has facts in the test");
            CollectionAssert.AreEqual(QueryHarness.Expected(result.Buckets, naive, StatisticsFrequency.Week), result.Series.Single().Values.ToArray(), $"{metric} {zone}");
        }

        var scripts = await harness.Queries.ScriptsAsync(request with { Limit = 10 });
        double Total(Func<ScriptMeasures, long> value, Func<ScriptKey, bool>? keep = null)
            => harness.Naive(batch => batch.Scripts.Where(pair => keep?.Invoke(pair.Key) ?? true).Select(pair => (pair.Key.Quarter, (double)value(pair.Value))), StatisticsFrequency.Week, from, to, DayOfWeek.Monday).Values.Sum();
        Assert.AreEqual(Total(static value => value.RunsStarted), scripts.Totals.Runs);
        Assert.AreEqual(Total(static value => value.RunsFailed), scripts.Totals.Failed);
        Assert.AreEqual(Total(static value => value.Calls), scripts.Totals.Calls);
        Assert.AreEqual(Total(static value => value.SessionsStarted), scripts.Totals.Sessions);
        Assert.AreEqual(Total(static value => value.WaitMs), scripts.Totals.WaitMs);
        var ended = Total(static value => value.RunsCompleted + value.RunsFailed + value.RunsCancelled + value.RunsInterrupted);
        Assert.AreEqual(Total(static value => value.RunsCompleted) / ended, scripts.Totals.SuccessRate!.Value, 1e-9);
        Assert.AreEqual(Scripts.Length, scripts.Scripts.Count);
        foreach (var row in scripts.Scripts)
        {
            Assert.AreEqual(Total(static value => value.RunsStarted, key => key.Script == row.Script), row.Runs, row.Script);
            Assert.AreEqual(Total(static value => value.RunsStarted, key => key.Script == row.Script), row.Spark.Sum(), row.Script);
        }

        // The kinds of scripts add up to the runs, and a filter on who started the turn keeps its runs only.
        var kinds = await harness.Queries.SeriesAsync(request, "script-runs", "script-kind");
        CollectionAssert.AreEquivalent(new[] { "library", "inline", "unknown" }, kinds.Series.Select(static line => line.Key).ToArray());
        Assert.AreEqual(scripts.Totals.Runs, kinds.Series.Sum(static line => line.Total));
        Assert.AreEqual(Total(static value => value.RunsStarted, static key => key.Script is not (ScriptLabels.Inline or ScriptLabels.Unknown)), kinds.Series.Single(static line => line.Key == "library").Total);
        var yours = await harness.Queries.SeriesAsync(request with { Filter = new StatisticsFilter { Origin = "you" } }, "script-runs");
        Assert.AreEqual(Total(static value => value.RunsStarted, static key => key.Origin == "you"), yours.Series.Single().Total);
        Assert.IsFalse(yours.Query.IgnoredFilters.Contains("origin"));
    }

    // A session of yours that ran a library script, and the session the script started: its tokens and cost are the script's.
    private static async Task<EngineHarness> SessionsAsync()
    {
        var harness = await EngineHarness.CreateAsync();
        harness.Journals.Set(ScriptFactsTests.ScriptJournal(), JournalBuilder.Time(2));
        var child = new JournalBuilder(ChildId);
        child.Header(JournalBuilder.Time(0, 10), parent: ParentId, createdByKind: "workflow", createdBySession: ParentId, correlation: "workflow:run-1/review")
            .State(JournalBuilder.Time(0, 10))
            .User(JournalBuilder.Time(0, 11), "c1", "do your part")
            .Usage(JournalBuilder.Time(0, 12), "c1", input: 4000, output: 500, cost: 0.5)
            .Idle(JournalBuilder.Time(0, 13), "c1");
        harness.Journals.Set(child, JournalBuilder.Time(2));
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        return harness;
    }

    [TestMethod]
    public async Task TheSessionsAScriptStarted_GiveTheTokensAndTheCostOfItsScript()
    {
        await using var harness = await SessionsAsync();
        var request = new StatisticsRequest { Period = "all", Comparison = StatisticsComparison.None };

        var scripts = await harness.Engine.Queries.ScriptsAsync(request);

        var row = scripts.Scripts.Single();
        Assert.AreEqual("review-pr", row.Script);
        Assert.AreEqual(1, row.Runs);
        Assert.AreEqual(4500, row.SessionTokens, "the tokens of the child session");
        Assert.AreEqual(0.5, row.SessionCosts.Single().Total, 1e-9);
        Assert.AreEqual(2, row.Sessions);
        Assert.IsNotNull(row.P50Ms);
        Assert.AreEqual("2026-10-09T10:00:05Z", row.LastRun);
        Assert.AreEqual(4500, scripts.Totals.SessionTokens);
        Assert.AreEqual(1, scripts.Totals.Completed);
        Assert.AreEqual(1d, scripts.Totals.SuccessRate);
        Assert.AreEqual(2, scripts.Totals.ToolCalls);
        Assert.AreEqual(30_000, scripts.Totals.WaitMs);

        var recent = scripts.Recent.Single();
        Assert.AreEqual(ParentId, recent.SessionId);
        Assert.AreEqual("run-1", recent.RunId);
        Assert.AreEqual("completed", recent.Status);
        Assert.AreEqual(85_000d, recent.DurationMs);

        // The turn of the parent sent a script; the turn of the child did not.
        var adoption = scripts.Adoption.Single(static item => item.Model == "gpt-6.1-sol");
        Assert.AreEqual(2, adoption.Turns);
        Assert.AreEqual(1, adoption.TurnsWithScript);
        Assert.AreEqual(0.5, adoption.Share);
        Assert.AreEqual(1, adoption.ScriptRuns);
        Assert.AreEqual(7d, adoption.CallsPerRun);
        Assert.AreEqual(1, scripts.Turns.Single(static item => item.WithScript).Turns);
        Assert.AreEqual(3d, scripts.Turns.Single(static item => item.WithScript).ToolCalls);
        Assert.AreEqual(1, scripts.Turns.Single(static item => !item.WithScript).Turns);
    }

    [TestMethod]
    public async Task ThePagesOfTheStatistics_SeeTheRunsOfScripts()
    {
        await using var harness = await SessionsAsync();
        var queries = harness.Engine.Queries;
        var request = new StatisticsRequest { Period = "all", Comparison = StatisticsComparison.None };

        var creators = await queries.SeriesAsync(request, "sessions-started", "creator");
        Assert.AreEqual(1, creators.Series.Single(static line => line.Key == "script").Total);
        Assert.AreEqual(1, creators.Series.Single(static line => line.Key == "you").Total);
        Assert.AreEqual(1, (await queries.SummaryAsync(request)).Tiles.Single(static tile => tile.Id == "script-runs").Value);
        Assert.AreEqual(1, (await queries.DistributionAsync(request, "script-run-duration", "review-pr")).Count);
        Assert.AreEqual(2, (await queries.DistributionAsync(request, "script-run-sessions")).Steps.Single().Lower);
        Assert.AreEqual("review-pr/merge", (await queries.DetailsAsync(request, "script-step-failed")).Rows.Single().Name);
        Assert.AreEqual("review-pr", (await queries.DetailsAsync(request, "script-called")).Rows.Single().Name);
        var records = await queries.RecordsAsync(request);
        Assert.AreEqual(85_000d, records.Records.Single(static record => record.Measure == "longestScriptRun").Value);
        Assert.AreEqual(ParentId, records.Records.Single(static record => record.Measure == "widestScriptRun").SessionId);
        var tools = await queries.ToolsAsync(request with { Limit = 50 });
        Assert.AreEqual("script", tools.Rows.Single(static row => row.Tool == "ToolCall:exec").Kind);
        Assert.AreEqual(2, tools.Rows.Sum(static row => row.ScriptCalls));

        // A filter narrows the runs of scripts, the session of the canvas limits them to it and its sub-agents.
        Assert.AreEqual(0, (await queries.ScriptsAsync(request with { Filter = new StatisticsFilter { Provider = "claude" } })).Totals.Runs);
        Assert.AreEqual(0, (await queries.ScriptsAsync(request with { Filter = new StatisticsFilter { Session = ChildId } })).Recent.Count);
        var parent = await queries.ScriptsAsync(request with { Filter = new StatisticsFilter { Session = ParentId, WithChildren = true } });
        Assert.AreEqual(1, parent.Totals.Runs);
        Assert.AreEqual(4500, parent.Totals.SessionTokens);
    }
}
