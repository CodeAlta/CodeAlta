using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Services;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The health a node reports in its heartbeat (<c>doc/fleet/observability.md</c>, health checks): its disk low where it keeps its
/// state reaches its owner as one notification once the problem lasts, however many heartbeats repeat it.
/// </summary>
[TestClass]
public sealed class NodeHealthTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ALowDisk_ReportedInTheHeartbeat_IsToldToTheOwnerOnce()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
            ConnectionPolicy = new FleetConnectionPolicy(FleetConnectionPolicy.DefaultMaxFrameBytes, 1000),
            Health = new HealthOptions { LastingAfter = TimeSpan.FromSeconds(2), RunLoop = false },
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop-fleet"));
        using var desktop = new FleetServerClient(address, desktopKey);
        await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);
        Assert.AreEqual(NodeExitCodes.Success, await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]));
        var nodeId = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending").Id;
        await desktop.ApproveAsync(nodeId, FleetDeviceKinds.Node, token);

        var node = await NodeHost.StartAsync(root.Options(), token);
        var servers = NodeServers.Start(node, null, "test-node", "1.0.0", Fast);
        try
        {
            // Any disk is low for this node: the problem is in every heartbeat.
            servers.Health.MinimumFreeBytes = long.MaxValue;
            Assert.AreEqual(FleetHealthKinds.DiskLow, servers.Health.Read().Single().Kind);
            var manager = new FleetBudgetsManager(new FleetOrganizationDevices(address, desktopKey, Path.Combine(root.Root, "desktop-link", FleetOrganizationDevices.PinFileName)));
            var told = await WaitForAsync(async () => (await manager.ListNotificationsAsync(token)).Notifications
                .FirstOrDefault(static notification => notification.Problem == FleetHealthKinds.DiskLow), token);
            Assert.AreEqual((FleetNotificationKinds.Health, nodeId), (told.Kind, told.MachineId));

            // More heartbeats say the same: still one.
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            Assert.AreEqual(1, (await manager.ListNotificationsAsync(token)).Notifications.Count(static notification => notification.Problem == FleetHealthKinds.DiskLow));

            // Room again: the next heartbeats end it.
            servers.Health.MinimumFreeBytes = 0;
            await WaitForAsync(async () => (await manager.ListNotificationsAsync(token)).Notifications
                .FirstOrDefault(static notification => notification.Problem == FleetHealthKinds.DiskLow && notification.ResolvedAt is not null), token);
        }
        finally
        {
            await servers.DisposeAsync();
            await node.DisposeAsync();
        }
    }

    // A connector that polls often and keeps the callbacks of the node, its health included.
    private static FleetConnectorOptions Fast(FleetConnectorOptions options) => new()
    {
        ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
        PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory, PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
        MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(250),
        DeviceListPinPath = options.DeviceListPinPath, Devices = options.Devices, MetadataKeys = options.MetadataKeys, AcceptRelay = options.AcceptRelay,
        SecretsChanged = options.SecretsChanged, BudgetsChanged = options.BudgetsChanged, Health = options.Health,
    };

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("It did not get there in time.");
            await Task.Delay(200, cancellationToken);
        }
    }
}
