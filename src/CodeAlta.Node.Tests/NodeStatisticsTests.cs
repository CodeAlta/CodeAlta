using CodeAlta.Plugin.Statistics;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Node.Tests;

/// <summary>
/// CodeAlta Node runs the statistics engine, as CodeAlta Desktop does (<c>doc/fleet/node.md</c>): the Statistics plugin of
/// the node keeps the facts of its sessions in its application database, counting from its first start without asking anyone.
/// </summary>
[TestClass]
public sealed class NodeStatisticsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ANode_CountsTheStatisticsOfItsSessions()
    {
        using var root = new NodeTestRoot();
        var token = TestContext.CancellationToken;
        // The journals are written first: the engine finds them at its first look, without a signal of the runtime.
        await using (var writer = await NodeHost.StartAsync(root.Options(), token))
        {
            await NodeSessionJournals.AddSessionAsync(writer.Host, "chat-1", root.ProjectRoot, token);
            await NodeSessionJournals.AddTurnAsync(writer.Host, "chat-1", "run-1", 1200, 300, token);
        }

        await using var node = await NodeHost.StartAsync(root.Options() with { StartPlugins = true }, token);
        var plugin = node.Host.PluginRuntime.ActivePlugins.Select(static active => active.Instance).OfType<StatisticsPlugin>().SingleOrDefault();
        Assert.IsNotNull(plugin, "The node starts the Statistics plugin.");
        var service = plugin.Statistics;
        Assert.IsNotNull(service, "Its engine runs in the node.");
        var deadline = DateTime.UtcNow + Patience;
        // The engine starts a moment after the node, so that a start is never slowed down.
        while (service.Status.State is not (HistoryState.Reading or HistoryState.Done))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"The engine of the node did not start: {service.Status.State} {service.Status.Error}.");
            await Task.Delay(200, token);
        }

        double runs;
        while ((runs = (await service.Queries.SummaryAsync(new StatisticsRequest { Period = "2d" }, token)).Tiles.Single(static tile => tile.Id == "runs").Value) < 1)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"The node did not count its session: {service.Status.State}.");
            await Task.Delay(200, token);
        }

        Assert.AreEqual(1, runs);
        Assert.AreEqual("from-today", service.Status.Choice, "Nobody is asked on a node: the statistics start with its first day.");
    }
}
