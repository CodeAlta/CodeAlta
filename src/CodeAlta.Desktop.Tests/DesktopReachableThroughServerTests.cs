using System.Collections.Immutable;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A CodeAlta Desktop of a fleet with a server, as a machine of its person's other devices (<see cref="DesktopNode.ReachableThroughServers"/>):
/// off until the person turns it on, then reached through the relay of the server without pairing or a port, with what a
/// pairing gives; turned off, its relayed channels close and new ones are declined. The other desktops list it with their
/// machines once its inventory says so, and only when it is of their own person.
/// </summary>
[TestClass]
public sealed class DesktopReachableThroughServerTests
{
    private static readonly TimeSpan Patience = TestTime.Seconds(30);
    private static readonly RemoteSessionHostOptions Quick = new() { MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromMilliseconds(200), ListInterval = TimeSpan.FromMilliseconds(100) };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ADesktopOfThePerson_IsReachedThroughTheServerOnlyWhileThePersonLetsIt()
    {
        var token = TestContext.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "codealta-desktop-reach-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = Path.Combine(root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { },
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");
            await using var host = await FleetNodeFixture.CreateHostAsync(Path.Combine(root, "desk"), new ScriptedFleetProvider(), false, token);
            var deskRoot = Path.Combine(root, "desk-fleet");
            using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root, "laptop-fleet"));
            using var desk = DeviceIdentity.LoadOrCreate(deskRoot);

            // The laptop made the organization; the desk joined it as a controller device of the same person, approved by the laptop.
            using (var owner = new FleetServerClient(address, laptop))
            {
                await owner.SetUpAsync(server.SetupCode!.Code, "Alex", "Laptop", "Home", token);
                var enrollment = await owner.CreateEnrollmentTokenAsync(null, "desk", token);
                using (var joining = new FleetServerClient(address, desk)) await joining.EnrollAsync(enrollment.Token, "desk", FleetDeviceKinds.Controller, token);
                await owner.ApproveAsync(desk.DeviceId, FleetDeviceKinds.Controller, token);
            }

            await using var node = new DesktopNode(host, "desk", "1.0.0", deskRoot) { PortOverride = 0 };
            FleetConnector? deskConnector = null;
            deskConnector = new FleetConnector(Fast(new FleetConnectorOptions
            {
                ServerAddress = address, Identity = desk, OutboxPath = Path.Combine(root, "desk-outbox.json"), Client = new FleetClientInfo("desktop", "1.0.0", "windows"),
                AcceptRelay = (_, stream, cancellation) => node.ServeRelayedAsync(deskConnector!, stream, cancellation),
            }));
            await using var deskLife = deskConnector;
            await using var viewer = new FleetConnector(Fast(new FleetConnectorOptions
            {
                ServerAddress = address, Identity = laptop, OutboxPath = Path.Combine(root, "laptop-outbox.json"), Client = new FleetClientInfo("desktop", "1.0.0", "windows"),
                WatchDirectory = true,
            }));
            deskConnector.Start();
            viewer.Start();
            await WaitForAsync(() => viewer.Directory.IsOnline(desk.DeviceId), "the desk to be online at the server", token);

            Assert.IsFalse(node.ReachableThroughServers);
            Assert.IsFalse(await TryReachAsync(viewer, desk.DeviceId, laptop, token), "nothing is relayed to it before the person turns it on");

            await node.SetReachableThroughServersAsync(true, token);
            Assert.IsFalse(node.Listening, "the server reaches it: no port opens");
            Assert.AreEqual(0, node.Addresses.Count);
            Assert.ThrowsExactly<InvalidOperationException>(() => node.CreatePairing(), "no pairing link: nothing listens");

            await using (var client = await FleetNodeClient.ConnectRelayedAsync(viewer, desk.DeviceId, laptop, null, token))
            {
                Assert.AreEqual(FleetChannelPath.Relayed, client.Path);
                var info = await client.GetInfoAsync(token);
                Assert.AreEqual("desk", info.Name);
                CollectionAssert.AreEquivalent(new[] { "view", "drive", "start", "models" }, info.Permissions.ToArray(), "the person's own device gets what a pairing gives");
                Assert.IsNotNull(await client.ListSessionsAsync(cancellationToken: token), "it lists the sessions of the desk");
                Assert.AreEqual(0, node.Devices().Count, "reached through the server, nothing is paired");

                // Turned off: the channel under way closes at once, and the next relay is declined.
                await node.SetReachableThroughServersAsync(false, token);
                await client.Completion.WithinAsync(Patience, token);
            }

            Assert.IsFalse(await TryReachAsync(viewer, desk.DeviceId, laptop, token), "turned off, a new relay is declined");

            // The other desktop's host of the desk, while the desk declines: offline, and it goes on trying, so that it is
            // online again once the person turns it back on.
            var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
            await using (var fleet = new DesktopFleet(Path.Combine(root, "laptop-fleet"), registry.TakeMembership(), "laptop", null, Quick) { RelayRoute = _ => viewer })
            {
                await fleet.SyncOrganizationMachinesAsync([(desk.DeviceId, "desk")], token);
                var deskHost = fleet.Find(desk.DeviceId)!;
                await WaitForAsync(() => deskHost.State == RemoteMachineState.Offline && deskHost.Problem is not null, "the declined desk to be offline", token);
                await node.SetReachableThroughServersAsync(true, token);
                await WaitForAsync(() => deskHost.State == RemoteMachineState.Online, "the desk to be reached again once turned on", token);
                Assert.AreEqual(FleetChannelPath.Relayed, deskHost.ChannelPath);
            }

            // The choice is kept for the next start.
            await using var next = new DesktopNode(host, "desk", "1.0.0", deskRoot) { PortOverride = 0 };
            Assert.IsTrue(next.ReachableThroughServers);
            Assert.IsFalse(next.Visible, "the two ways are turned on apart");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task TheInventoryOfADesktopSaysItIsReachable_WhileItIs()
    {
        var token = TestContext.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "codealta-desktop-reach-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await FleetNodeFixture.CreateHostAsync(root, new ScriptedFleetProvider(), false, token);
            var reachable = false;
            await using var source = new FleetHostPublicationSource(host, "desk", "1.0.0", ["sessions", "moves", "console"])
            {
                MoreCapabilities = () => reachable ? [FleetMachineCapabilities.Reachable] : [],
            };
            CollectionAssert.AreEqual(new[] { "sessions", "moves", "console" }, (await source.ReadInventoryAsync(token)).Capabilities!.ToArray());
            reachable = true;
            CollectionAssert.AreEqual(new[] { "sessions", "moves", "console", FleetMachineCapabilities.Reachable }, (await source.ReadInventoryAsync(token)).Capabilities!.ToArray());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void AnotherDesktopIsAMachine_OnlyWhenItIsReachableAndOfThePerson()
    {
        var devices = FleetDeviceListState.Empty with
        {
            Members = ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
            {
                Member("laptop", FleetDeviceKinds.Controller, "alex"),
                Member("desk", FleetDeviceKinds.Controller, "alex"),
                Member("old-desk", FleetDeviceKinds.Controller, "alex"),
                Member("bea-desk", FleetDeviceKinds.Controller, "bea"),
                Member("mini", FleetDeviceKinds.Node, "alex"),
            }),
            Removed = ["old-desk"],
        };
        string[] reachable = ["sessions", "moves", "console", FleetMachineCapabilities.Reachable];

        Assert.IsTrue(DesktopFleetServers.IsMachine(Machine("mini", FleetDeviceKinds.Node, ["sessions", "moves"]), devices, "laptop"), "a node always is");
        Assert.IsTrue(DesktopFleetServers.IsMachine(Machine("desk", FleetDeviceKinds.Controller, reachable), devices, "laptop"), "a desktop of the person that lets it");
        Assert.IsFalse(DesktopFleetServers.IsMachine(Machine("desk", FleetDeviceKinds.Controller, ["sessions", "moves", "console"]), devices, "laptop"), "one that does not");
        Assert.IsFalse(DesktopFleetServers.IsMachine(Machine("bea-desk", FleetDeviceKinds.Controller, reachable), devices, "laptop"), "another person's desktop gives this one nothing");
        Assert.IsFalse(DesktopFleetServers.IsMachine(Machine("old-desk", FleetDeviceKinds.Controller, reachable), devices, "laptop"), "a desktop removed from the list");
        Assert.IsFalse(DesktopFleetServers.IsMachine(Machine("laptop", FleetDeviceKinds.Controller, reachable), devices, "laptop"), "never this computer");
        Assert.IsFalse(DesktopFleetServers.IsMachine(Machine("desk", FleetDeviceKinds.Controller, reachable), null, "laptop"), "not before the device list verified");
    }

    private static KeyValuePair<string, FleetDeviceListMember> Member(string id, string kind, string owner) => new(id, new FleetDeviceListMember(id, kind, null!, owner));

    private static FleetDirectoryMachine Machine(string id, string kind, IReadOnlyList<string> capabilities)
        => new(id, kind, new FleetMachineInventory(id, "windows", Capabilities: capabilities), DateTimeOffset.UnixEpoch);

    // Whether a relayed channel to the desk answers: a declined relay closes before the channel opens.
    private static async Task<bool> TryReachAsync(FleetConnector viewer, string deskId, DeviceIdentity laptop, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Patience);
            await using var client = await FleetNodeClient.ConnectRelayedAsync(viewer, deskId, laptop, null, timeout.Token);
            await client.GetInfoAsync(timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is not AssertFailedException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static FleetConnectorOptions Fast(FleetConnectorOptions options) => new()
    {
        ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, WatchDirectory = options.WatchDirectory,
        AcceptRelay = options.AcceptRelay, MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1),
        PollInterval = TimeSpan.FromMilliseconds(500), PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
    };

    private static async Task WaitForAsync(Func<bool> condition, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(50, cancellationToken);
        }
    }
}
