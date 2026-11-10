using CodeAlta.Catalog;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Node.Tests;

/// <summary>
/// <c>altanode join</c> against a CodeAlta Server of the test process: the node enrolls, waits for a controller device,
/// and once approved the running node keeps a control connection that publishes its machine; <c>status</c> says so.
/// </summary>
[TestClass]
public sealed class NodeJoinTests
{
    private static readonly TimeSpan Patience = TestTime.Seconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AJoinedNodeIsApprovedThenConnectsAndStatusSaysSo()
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

        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var node = new NodeApplication(TextWriter.Null, TextWriter.Null)
        {
            InitializeLogging = false,
            ConfigureHost = root.Isolate,
            Started = (_, stop) => started.TrySetResult(stop),
            ConfigureConnector = static options => new FleetConnectorOptions
            {
                ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
                PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory, PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
                MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(500),
            },
            ServiceEnvironment = static () => null,
        };
        var running = node.RunAsync(["run", "--data-root", root.DataRoot, "--no-listen", "--name", "test-node"]);
        var stop = await started.Task.WithinAsync(Patience, token);
        try
        {
            var (code, output, error) = await RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "is enrolled in");
            var links = new FleetServerLinkStore(new CatalogOptions { GlobalRoot = root.DataRoot, StateRoot = root.DataRoot }.FleetRoot).List();
            Assert.AreEqual((FleetServerLinkStates.Pending, "node"), (links.Single().State, links.Single().Kind));
            Assert.AreEqual(FleetServerAddress.Format(address), (await SpaceOfAsync(root)).Settings.Fleet?.Server, "the default space publishes to the server");

            // Joining again with the same token is not a second enrollment: the link is there.
            (code, output, _) = await RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--no-wait"]);
            Assert.AreEqual(NodeExitCodes.Success, code);
            StringAssert.Contains(output, "linked to");

            var pending = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending");
            Assert.AreEqual("test-node", pending.Name);
            await desktop.ApproveAsync(pending.Id, FleetDeviceKinds.Node, token);

            var deadline = DateTime.UtcNow + Patience;
            while (!(await desktop.ListMachinesAsync(token)).Any(static machine => machine.Online && machine.Inventory.Name == "test-node"))
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("The node did not publish its machine.");
                await Task.Delay(100, token);
            }

            // The link learns its organization once connected.
            var expected = $"Server: {FleetServerAddress.Format(address)} (Home), approved, connected since";
            do
            {
                (code, output, error) = await RunAsync(["status", "--data-root", root.DataRoot]);
                Assert.AreEqual(NodeExitCodes.Success, code, error);
                if (output.Contains(expected, StringComparison.Ordinal)) break;
                if (DateTime.UtcNow > deadline) Assert.Fail(output);
                await Task.Delay(100, token);
            }
            while (true);
        }
        finally
        {
            stop();
            Assert.AreEqual(NodeExitCodes.Success, await running.WithinAsync(Patience, token));
        }
    }

    private static async Task<SpaceDescriptor> SpaceOfAsync(NodeTestRoot root)
    {
        var catalog = new CatalogOptions { GlobalRoot = root.DataRoot, StateRoot = root.DataRoot };
        return (await new SpaceCatalog(new ProjectCatalog(catalog)).LoadAsync()).Single(static space => space.IsDefault);
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await new NodeApplication(TextWriter.Synchronized(output), TextWriter.Synchronized(error)) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync(args);
        return (code, output.ToString(), error.ToString());
    }
}
