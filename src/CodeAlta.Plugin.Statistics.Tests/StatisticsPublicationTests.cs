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
    public void AnAliasRetargetedByALaterTurn_NeverRelabelsTheModelAnEarlierRequestReported()
    {
        // haiku stood for v1 in the first turn; a later turn, started under v1 (the alias as it resolved then), reported v2.
        static StatisticsFactRow Row(string family, long quarter, string model, string measure)
            => new(family, quarter, [new("provider", "claude-code"), new("model", model), new("effort", "high")], [new(measure, 1)]);
        var rows = new[]
        {
            Row("activity", 1, "haiku", "runs_started"), Row("usage", 1, "v1", "requests"), Row("cost", 1, "v1", "total_micro"), Row("activity", 1, "v1", "active_ms"),
            Row("activity", 5, "v1", "runs_started"), Row("usage", 5, "v2", "requests"),
        };

        var published = PublishedModels.Attribute(rows, new Dictionary<string, string> { ["haiku"] = "v1", ["v1"] = "v2" });

        string Model(string family, long quarter, string measure) => published.Single(row => row.Family == family && row.Quarter == quarter
            && row.Measures.Any(value => value.Key == measure)).Keys.Single(static key => key.Key == "model").Value;
        Assert.AreEqual("v1", Model("usage", 1, "requests"), "a model a provider reported is never relabelled");
        Assert.AreEqual("v1", Model("cost", 1, "total_micro"));
        Assert.AreEqual("v1", Model("activity", 1, "runs_started"), "the alias goes on the model it stood for in its turn, not on its latest");
        Assert.AreEqual("v1", Model("activity", 1, "active_ms"));
        Assert.AreEqual("v2", Model("activity", 5, "runs_started"), "the later turn started under v1 goes on the model its requests reported");
        Assert.AreEqual("v2", Model("usage", 5, "requests"));
    }
}
