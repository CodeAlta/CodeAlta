using CodeAlta.Plugin.Statistics.History;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// What a host that publishes its statistics to a fleet reads of the store (<c>doc/fleet/statistics.md</c>): a revision per
/// session that changes when a catch-up is saved, and the rows of the families a fleet sums, from a quarter hour on.
/// </summary>
[TestClass]
public sealed class StatisticsPublicationTests
{
    private static readonly DateTimeOffset Today9 = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TheRevisionOfASession_ChangesWithItsFacts_AndItsRowsAreThoseOfTheFamiliesAFleetSums()
    {
        await using var harness = await EngineHarness.CreateAsync();
        IStatisticsPublication publication = harness.Engine;
        Assert.AreEqual(0, (await publication.ReadRevisionsAsync()).Count, "nothing before the engine started");
        Assert.AreEqual(0, (await publication.ReadRowsAsync("a", 0)).Count);

        var session = EngineHarness.Session("a", Today9, runs: 2);
        harness.Journals.Set(session, EngineHarness.EndOf(session, Today9));
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var first = (await publication.ReadRevisionsAsync())["a"];
        var rows = await publication.ReadRowsAsync("a", long.MinValue);
        CollectionAssert.IsSubsetOf(rows.Select(static row => row.Family).Distinct().ToArray(), new[] { "activity", "usage", "cost", "tool" });
        var usage = rows.Where(static row => row.Family == "usage").ToArray();
        Assert.IsNotEmpty(usage);
        Assert.AreEqual("provider", usage[0].Keys[0].Key);
        Assert.AreEqual(2L, usage.Sum(static row => row.Measures.Single(static measure => measure.Key == "requests").Value), "the current value of the rows");

        // A quarter hour after the last row: nothing.
        var last = rows.Max(static row => row.Quarter);
        Assert.AreEqual(0, (await publication.ReadRowsAsync("a", last + 1)).Count);

        // A catch-up that saved more facts gives a new revision, and the rows as they are now.
        harness.Journals.Append("a", EngineHarness.More("a", Today9.AddHours(1)), Today9.AddHours(1).AddSeconds(20));
        harness.Engine.Signal("a");
        await harness.Engine.DrainAsync();
        Assert.AreNotEqual(first, (await publication.ReadRevisionsAsync())["a"]);
        Assert.AreEqual(3L, (await publication.ReadRowsAsync("a", long.MinValue)).Where(static row => row.Family == "usage")
            .Sum(static row => row.Measures.Single(static measure => measure.Key == "requests").Value));
    }

    [TestMethod]
    public async Task TheRowsOfARun_AreAllPublishedUnderTheModelItsProviderReported()
    {
        await using var harness = await EngineHarness.CreateAsync();
        IStatisticsPublication publication = harness.Engine;
        // Claude Code starts with the alias it was given, and its requests say the model it stands for; a tool call names no model.
        var session = new JournalBuilder("c", "claude-code").Header(Today9).State(Today9, model: "haiku")
            .User(Today9.AddSeconds(1), "c-run0", "a prompt of some words")
            .Usage(Today9.AddSeconds(5), "c-run0", model: "claude-haiku-4-5-20251001", cost: 0.01, costUnit: "usd")
            .ToolStarted(Today9.AddSeconds(6), "c-run0", "t1", "Read")
            .ToolDone(Today9.AddSeconds(7), "c-run0", "t1", "Read")
            .Assistant(Today9.AddSeconds(10), "c-run0")
            .Idle(Today9.AddSeconds(20), "c-run0");
        harness.Journals.Set(session, Today9.AddSeconds(20));
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var rows = await publication.ReadRowsAsync("c", long.MinValue);
        var models = rows.Select(static row => (row.Family, Model: row.Keys.FirstOrDefault(static key => key.Key == "model").Value)).Distinct().OrderBy(static row => row.Family).ToArray();
        CollectionAssert.AreEquivalent(new[] { "activity", "cost", "tool", "usage" }, models.Select(static row => row.Family).ToArray(), string.Join(", ", models));
        Assert.IsTrue(models.All(static row => row.Model == "claude-haiku-4-5-20251001"), $"one model for every row of the run: {string.Join(", ", models)}");
        Assert.AreEqual(1L, rows.Where(static row => row.Family == "activity").Sum(static row => row.Measures.FirstOrDefault(static measure => measure.Key == "runs_started").Value));
        Assert.AreEqual(1, rows.Count(static row => row.Family == "activity" && row.Measures.Any(static measure => measure.Key == "runs_started")),
            "the start of the run is on one row");
    }

    [TestMethod]
    public async Task TheTimeOfARun_StaysOnTheModelItsOwnRequestsReported_WhenALaterRunOfItsQuarterReportsAnother()
    {
        await using var harness = await EngineHarness.CreateAsync();
        IStatisticsPublication publication = harness.Engine;
        // The first run reports v1 before 9:15 and goes on until 9:16; the second, from 9:20, starts under v1 and its request reports v2.
        var session = new JournalBuilder("r", "claude-code").Header(Today9).State(Today9, model: "v1")
            .User(Today9.AddMinutes(10), "r-run0", "a prompt of some words")
            .Usage(Today9.AddMinutes(14).AddSeconds(50), "r-run0", model: "v1")
            .Assistant(Today9.AddMinutes(16), "r-run0")
            .Idle(Today9.AddMinutes(16).AddSeconds(10), "r-run0")
            .User(Today9.AddMinutes(20), "r-run1", "another prompt")
            .Usage(Today9.AddMinutes(21), "r-run1", model: "v2")
            .Assistant(Today9.AddMinutes(22), "r-run1")
            .Idle(Today9.AddMinutes(22).AddSeconds(10), "r-run1");
        harness.Journals.Set(session, Today9.AddMinutes(22).AddSeconds(10));
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var rows = await publication.ReadRowsAsync("r", long.MinValue);
        var second = rows.Where(static row => row.Family == "activity").GroupBy(static row => row.Quarter).OrderBy(static group => group.Key).Last();
        long Measure(string model, string measure) => second.Where(row => row.Keys.Any(key => key.Key == "model" && key.Value == model))
            .Sum(row => row.Measures.FirstOrDefault(value => value.Key == measure).Value);
        Assert.IsTrue(Measure("v1", "active_ms") > 0, "the first run's time after 9:15 stays on v1, the model its own request reported");
        Assert.AreEqual(0L, Measure("v1", "runs_started"), "the second run did not report v1");
        Assert.AreEqual(1L, Measure("v2", "runs_started"), "the second run is on the model its own request reported");
        Assert.AreEqual(130_000L, Measure("v2", "active_ms"), "the whole time of the second run, 9:20 to 9:22:10, is on v2");
    }

    [TestMethod]
    public async Task AnAliasRetargetedByALaterTurn_NeverRelabelsTheModelAnEarlierRequestReported()
    {
        await using var harness = await EngineHarness.CreateAsync();
        IStatisticsPublication publication = harness.Engine;
        // haiku stands for v1 in the first turn; the second turn starts under v1 (what haiku stood for) and its request reports v2.
        var session = new JournalBuilder("h", "claude-code").Header(Today9).State(Today9, model: "haiku")
            .User(Today9.AddSeconds(1), "h-run0", "a prompt of some words")
            .Usage(Today9.AddSeconds(5), "h-run0", model: "v1", cost: 0.01, costUnit: "usd")
            .Idle(Today9.AddSeconds(20), "h-run0")
            .User(Today9.AddMinutes(40), "h-run1", "another prompt")
            .Usage(Today9.AddMinutes(40).AddSeconds(5), "h-run1", model: "v2")
            .Idle(Today9.AddMinutes(40).AddSeconds(20), "h-run1");
        harness.Journals.Set(session, Today9.AddMinutes(40).AddSeconds(20));
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var rows = await publication.ReadRowsAsync("h", long.MinValue);
        string[] Models(string family, bool later) => [.. rows.Where(row => row.Family == family && (row.Quarter > rows.Min(static first => first.Quarter)) == later
            && row.Measures.Any(static value => value.Value != 0)).Select(static row => row.Keys.Single(static key => key.Key == "model").Value).Distinct()];
        CollectionAssert.AreEqual(new[] { "v1" }, Models("usage", later: false), "a model a provider reported is never relabelled");
        CollectionAssert.AreEqual(new[] { "v1" }, Models("cost", later: false));
        CollectionAssert.AreEqual(new[] { "v1" }, Models("activity", later: false), "the alias goes on the model it stood for in its turn, not on its latest");
        CollectionAssert.AreEqual(new[] { "v2" }, Models("activity", later: true), "the later turn started under v1 goes on the model its own request reported");
        CollectionAssert.AreEqual(new[] { "v2" }, Models("usage", later: true));
    }
}
