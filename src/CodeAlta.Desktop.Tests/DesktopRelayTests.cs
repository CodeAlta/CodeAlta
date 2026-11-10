using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The desktop's way to a machine when a server is there: a node of the organization this computer never paired with gets
/// a host that reaches it through the relay, says so in its rows, connects again when the relay drops, and leaves when
/// the server lists it no more; a paired machine whose addresses do not answer goes through the relay too.
/// </summary>
[TestClass]
public sealed class DesktopRelayTests
{
    private static readonly TimeSpan Patience = TestTime.Seconds(30);
    private static readonly RemoteSessionHostOptions Quick = new() { MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromMilliseconds(200), ListInterval = TimeSpan.FromMilliseconds(100) };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AMachineOfTheOrganizationIsReachedThroughTheRelay_AndAPairedOneWhoseAddressesFailToo()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(fixture.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { },
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        var nodeId = fixture.NodeIdentity.DeviceId;
        using (var desktop = new FleetServerClient(address, fixture.ViewerIdentity))
        {
            await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
            var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);
            using (var node = new FleetServerClient(address, fixture.NodeIdentity)) await node.EnrollAsync(enrollment.Token, "test-node", token);
            await desktop.ApproveAsync(nodeId, FleetDeviceKinds.Node, token);
        }

        // The node's control connection serves the relays offered to it with its node API.
        FleetConnector? nodeConnector = null;
        nodeConnector = new FleetConnector(Fast(new FleetConnectorOptions
        {
            ServerAddress = address, Identity = fixture.NodeIdentity, OutboxPath = Path.Combine(fixture.Root, "node-outbox.json"), Client = new FleetClientInfo("node", "1.0.0", "windows"),
            AcceptRelay = async (_, stream, cancellation) =>
            {
                await nodeConnector!.Devices.RefreshAsync(cancellation);
                await fixture.Service.ServeRelayedAsync(stream, nodeConnector.Devices.Trust, cancellation);
            },
        }));
        await using var nodeLife = nodeConnector;
        var relays = 0;
        Stream? lastRelay = null;
        await using var viewer = new FleetConnector(Fast(new FleetConnectorOptions
        {
            ServerAddress = address, Identity = fixture.ViewerIdentity, OutboxPath = Path.Combine(fixture.Root, "viewer-outbox.json"), Client = new FleetClientInfo("desktop", "1.0.0", "windows"),
            WatchDirectory = true,
            WrapRelayStream = stream =>
            {
                Interlocked.Increment(ref relays);
                Volatile.Write(ref lastRelay, stream);
                return stream;
            },
        }));
        nodeConnector.Start();
        viewer.Start();
        await WaitForAsync(() => viewer.Directory.IsOnline(nodeId), "the node to be online at the server", token);

        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        // The fleet folder of the profile: the same device key as the control connection.
        var fleetRoot = fixture.ViewerFleetRoot;
        await using (var fleet = new DesktopFleet(fleetRoot, registry.TakeMembership(), "desktop", null, Quick) { RelayRoute = _ => viewer })
        {
            await fleet.SyncOrganizationMachinesAsync([(nodeId, "test-node")], token);
            var host = fleet.Find(nodeId);
            Assert.IsNotNull(host);
            Assert.IsFalse(host.IsPaired, "The machine is reached through the server, never paired.");
            await WaitForAsync(() => host.State == RemoteMachineState.Online, () => $"the machine to be online through the relay ({host.Problem})", token);
            Assert.AreEqual(FleetChannelPath.Relayed, host.ChannelPath);
            var view = MachinesService.View(host);
            Assert.AreEqual((false, "relayed"), (view.Paired, view.Channel));
            CollectionAssert.AreEquivalent(new[] { "view", "drive", "start", "models" }, view.Permissions, "The owner's own node gives this controller device the paired defaults.");

            var created = await host.CurrentClient!.CreateSessionAsync(new FleetCreateSessionParams(Title: "Through the server"), "create-desktop-relay", token);
            Assert.IsTrue(await host.WaitListedAsync(created.SessionId, Patience, token));
            var row = WorkspaceService.WithRemoteSessions(new WorkspaceSnapshot(true, [], [], false, false, false), [host]).Sessions.Single(session => session.Id == created.SessionId);
            Assert.AreEqual((nodeId, "relayed"), (row.MachineId, row.MachineChannel), "The row of the session says where its channel goes.");

            // The relay drops: the host connects again by itself, through a new relay.
            Volatile.Read(ref lastRelay)!.Dispose();
            await WaitForAsync(() => Volatile.Read(ref relays) >= 2 && host.State == RemoteMachineState.Online, "the machine to be reached again", token);
            Assert.IsTrue(host.Lists(created.SessionId));

            await fleet.SyncOrganizationMachinesAsync([], token);
            Assert.IsNull(fleet.Find(nodeId), "A machine no server lists leaves.");
        }

        // A paired machine whose addresses do not answer (another network) is reached through the relay, with its pinned key.
        var paired = await fixture.PairViewerAsync(token);
        new PairedMachineStore(fleetRoot).Save(paired with { Addresses = ["127.0.0.1:1"] });
        var again = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        await using (var fleet = new DesktopFleet(fleetRoot, again.TakeMembership(), "desktop", null, Quick) { RelayRoute = _ => viewer })
        {
            await fleet.StartAsync(token);
            var host = fleet.Find(nodeId)!;
            Assert.IsTrue(host.IsPaired);
            await WaitForAsync(() => host.State == RemoteMachineState.Online, "the paired machine to be reached through the relay", token);
            Assert.AreEqual(FleetChannelPath.Relayed, host.ChannelPath);
            await fleet.SyncOrganizationMachinesAsync([(nodeId, "test-node")], token);
            Assert.AreSame(host, fleet.Find(nodeId), "A paired machine keeps its own host.");
        }
    }

    private static FleetConnectorOptions Fast(FleetConnectorOptions options) => new()
    {
        ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, WatchDirectory = options.WatchDirectory,
        AcceptRelay = options.AcceptRelay, WrapRelayStream = options.WrapRelayStream, MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1),
        PollInterval = TimeSpan.FromMilliseconds(500), PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
    };

    private static Task WaitForAsync(Func<bool> condition, string what, CancellationToken cancellationToken) => WaitForAsync(condition, () => what, cancellationToken);

    private static async Task WaitForAsync(Func<bool> condition, Func<string> what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what()}.");
            await Task.Delay(50, cancellationToken);
        }
    }
}
