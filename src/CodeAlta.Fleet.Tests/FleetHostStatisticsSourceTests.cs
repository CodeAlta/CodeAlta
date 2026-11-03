using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Plugin.Statistics.History;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The statistics an instance publishes, read from its Statistics plugin: the families a fleet sums, their provider and model
/// taken out of their keys, the measures that count, and names as the server takes them.
/// </summary>
[TestClass]
public sealed class FleetHostStatisticsSourceTests
{
    [TestMethod]
    public async Task TheRowsOfTheStore_BecomeTheRowsAFleetSums()
    {
        var publication = new FakePublication(
            Row("usage", [("provider", "anthropic"), ("model", "claude-opus-5-5"), ("effort", "high"), ("agent_prompt", ""), ("purpose", "0")],
                [("requests", 2), ("input_tokens", 1200), ("output_tokens", 0)]),
            Row("tool", [("provider", "anthropic"), ("kind", "4"), ("tool", "mcp__" + new string('x', 200))], [("calls", 3), ("duration_ms_max", 40)]),
            Row("content", [("kind", "1"), ("sender", "0"), ("prompt_kind", "0")], [("count", 1)]),
            Row("activity", [("provider", "bad\nname"), ("model", "m"), ("effort", "")], [("active_ms", 1000)]));
        using var source = new FleetHostStatisticsSource(() => (publication, null));

        var rows = await source.ReadRowsAsync("s1", 0, CancellationToken.None);

        Assert.AreEqual(3, rows.Count, "the content of prompts stays on the machine");
        var usage = rows[0];
        Assert.AreEqual(("anthropic", "claude-opus-5-5"), (usage.Provider, usage.Model));
        CollectionAssert.AreEqual(new[] { "high", "", "0" }, usage.Keys!.ToArray());
        CollectionAssert.AreEquivalent(new[] { "requests", "input_tokens" }, usage.Measures.Keys.ToArray(), "a measure at zero is not sent");
        var tool = rows[1];
        Assert.IsNull(tool.Model, "a tool call has no model of its own");
        Assert.AreEqual(128, tool.Keys![1].Length, "a long name is cut to what the server takes");
        Assert.IsNull(rows[2].Provider, "a name with a control character is not published");
        Assert.AreEqual(0, (await new FleetHostStatisticsSource(static () => (null, null)).ReadRevisionsAsync(CancellationToken.None)).Count, "nothing while the plugin does not run");
    }

    private static StatisticsFactRow Row(string family, (string, string)[] keys, (string, long)[] measures)
        => new(family, 42, [.. keys.Select(static key => new KeyValuePair<string, string>(key.Item1, key.Item2))],
            [.. measures.Select(static measure => new KeyValuePair<string, long>(measure.Item1, measure.Item2))]);

    private sealed class FakePublication(params StatisticsFactRow[] rows) : IStatisticsPublication
    {
        public ValueTask<IReadOnlyDictionary<string, string>> ReadRevisionsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["s1"] = "r1" });

        public ValueTask<IReadOnlyList<StatisticsFactRow>> ReadRowsAsync(string sessionId, long fromQuarter, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<StatisticsFactRow>>(rows);
    }
}
