using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Plugin.Statistics;
using CodeAlta.Server.Hosting;
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

    [TestMethod]
    public async Task AJoinedNode_PublishesItsStatistics_AndTheServerSumsThemForItsOwner()
    {
        using var root = new NodeTestRoot();
        var token = TestContext.CancellationToken;
        await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { },
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop-fleet"));
        using var desktop = new FleetServerClient(address, desktopKey);
        await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);

        // A session in the journals of the node: one turn, a tool call, 1200 tokens in and 300 out. No model is called.
        await using (var writer = await NodeHost.StartAsync(root.Options(), token))
        {
            await NodeSessionJournals.AddSessionAsync(writer.Host, "chat-1", root.ProjectRoot, token);
            await NodeSessionJournals.AddTurnAsync(writer.Host, "chat-1", "run-1", 1200, 300, token);
        }

        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var node = new NodeApplication(TextWriter.Null, TextWriter.Null)
        {
            InitializeLogging = false,
            ConfigureHost = options => root.Isolate(options) with { StartPlugins = true },
            Started = (_, stop) => started.TrySetResult(stop),
            ConfigureConnector = static options => new FleetConnectorOptions
            {
                ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
                Statistics = options.Statistics, StatisticsFromQuarter = options.StatisticsFromQuarter, PublishesSpace = options.PublishesSpace,
                WatchDirectory = options.WatchDirectory, MetadataKeys = options.MetadataKeys, Devices = options.Devices, DeviceListPinPath = options.DeviceListPinPath,
                PendingApprovalRetry = TimeSpan.FromMilliseconds(200), MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1),
                PublishDelay = TimeSpan.FromMilliseconds(20), PollInterval = TimeSpan.FromMilliseconds(250),
            },
            ServiceEnvironment = static () => null,
        };
        var running = node.RunAsync(["run", "--data-root", root.DataRoot, "--no-listen", "--name", "test-node"]);
        var stop = await started.Task.WaitAsync(Patience, token);
        try
        {
            var joined = await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
                .RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]);
            Assert.AreEqual(NodeExitCodes.Success, joined);
            var pending = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending");
            await desktop.ApproveAsync(pending.Id, token);

            // The node counts its session from its journal and publishes it; its owner's device reads the sums.
            var days = new StatisticsQueryRequest(DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"), DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd"), null,
                [StatisticsGroupBy.Machine, StatisticsGroupBy.Provider]);
            var deadline = DateTime.UtcNow + Patience;
            StatisticsQueryResponse sums;
            while ((sums = await desktop.QueryStatisticsAsync(days, token)).Totals.InputTokens == 0)
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("The node did not publish its statistics.");
                await Task.Delay(250, token);
            }

            Assert.AreEqual((1200L, 300L, 1L, 1L), (sums.Totals.InputTokens, sums.Totals.OutputTokens, sums.Totals.Runs, sums.Totals.ToolCalls));
            var group = sums.Groups.Single(static group => group.Measures.InputTokens > 0);
            Assert.AreEqual((pending.Id, NodeSessionJournals.Provider.Value), (group.Machine, group.Provider));
            var machine = sums.Machines.Single(static machine => machine.Name == "test-node");
            Assert.AreEqual(1L, machine.Sessions);
        }
        finally
        {
            stop();
            Assert.AreEqual(NodeExitCodes.Success, await running.WaitAsync(Patience, token));
        }
    }
}
