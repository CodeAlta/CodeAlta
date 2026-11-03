using System.Collections.Concurrent;
using System.Net;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The server client and the connector of CodeAlta.Fleet against a real server of this process: setup, enrollment and
/// approval with entries signed by the client's own device key, then the control connection, the publication of facts
/// with their versions, its resumption after a dropped connection, and what is never published.
/// </summary>
[TestClass]
public sealed class FleetConnectorTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TheClientSetsUpEnrollsApprovesAndRevokesWithItsOwnKeys()
    {
        await using var server = await ServerFixture.StartAsync();
        using var root = new TempDirectory();
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        using var desktop = new FleetServerClient(server.BaseAddress, desktopKey);
        using var node = new FleetServerClient(server.BaseAddress, nodeKey);

        Assert.IsFalse((await desktop.GetHealthAsync()).SetUp);
        var wrong = await Assert.ThrowsExactlyAsync<FleetServerException>(() => desktop.SetUpAsync("WRONG-CODE", "Alex", "Desktop"));
        Assert.AreEqual((HttpStatusCode.Unauthorized, CoordinatorErrorCodes.SetupCodeInvalid), (wrong.Status, wrong.Code));
        var setup = await desktop.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Home");
        Assert.AreEqual(("Home", desktopKey.DeviceId, FleetDeviceKinds.Controller), (setup.Organization.Name, setup.Device.Id, setup.Device.Kind));

        var token = await desktop.CreateEnrollmentTokenAsync(TimeSpan.FromMinutes(30), "Mac mini");
        var pending = await node.EnrollAsync(token.Token, "Mac mini");
        Assert.AreEqual((nodeKey.DeviceId, "pending", FleetDeviceKinds.Node), (pending.Id, pending.Status, pending.Kind));
        var used = await Assert.ThrowsExactlyAsync<FleetServerException>(() => node.EnrollAsync(token.Token, "Mac mini"));
        Assert.AreEqual(CoordinatorErrorCodes.EnrollmentTokenUsed, used.Code);
        // A pending device cannot call the API: it is told to wait.
        var waiting = await Assert.ThrowsExactlyAsync<FleetServerException>(() => node.GetDeviceAsync());
        Assert.AreEqual((FleetErrorCodes.PendingApproval, FleetErrorHints.WaitForApproval), (waiting.Code, waiting.Hint));
        // A node cannot make tokens; only a controller device can.
        Assert.IsTrue((await desktop.ListDevicesAsync()).Any(device => device.Id == nodeKey.DeviceId && device.Status == "pending"));

        var approved = await desktop.ApproveAsync(nodeKey.DeviceId);
        Assert.AreEqual(("approved", setup.Owner.Id), (approved.Status, approved.OwnerId));
        Assert.AreEqual(2, (await desktop.GetDeviceListAsync()).Count);
        Assert.AreEqual("approved", (await node.GetDeviceAsync()).Status);
        var forbidden = await Assert.ThrowsExactlyAsync<FleetServerException>(() => node.CreateEnrollmentTokenAsync());
        Assert.AreEqual(FleetErrorCodes.Forbidden, forbidden.Code);

        var revoked = await desktop.RevokeAsync(nodeKey.DeviceId);
        Assert.AreEqual("revoked", revoked.Status);
        Assert.AreEqual(3, (await desktop.GetDeviceListAsync()).Count);
    }

    [TestMethod]
    public async Task OnlyTheSessionsOfLinkedSpacesThatAreNotPrivateArePublished()
    {
        await using var fleet = await FleetFixture.StartAsync();
        var source = new FakeSource();
        source.Set(Session("s-work", "work"), Session("s-private", "work", isPrivate: true), Session("s-home", "home"), Session("s-default", null));
        await using var connector = fleet.NodeConnector(source, space => space == "work");
        connector.Start();

        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s-work"));
        var machines = await fleet.Desktop.ListMachinesAsync();
        Assert.AreEqual(("Mac mini", "macos", true), (machines.Single().Inventory.Name, machines.Single().Inventory.Os, machines.Single().Online));

        // A change is published; still nothing of the other sessions.
        source.Set(Session("s-work", "work", FleetSessionStates.Running), Session("s-private", "work", FleetSessionStates.Running, isPrivate: true),
            Session("s-home", "home", FleetSessionStates.Running), Session("s-default", null, FleetSessionStates.Running));
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s-work" && session.State == FleetSessionStates.Running));
        var sessions = await fleet.Desktop.ListSessionsAsync();
        CollectionAssert.AreEqual(new[] { "s-work" }, sessions.Select(static session => session.Id).ToArray());
        Assert.AreEqual((fleet.NodeKey.DeviceId, 2L, "anthropic", "claude-opus-5-5"), (sessions[0].MachineId, sessions[0].Version, sessions[0].ProviderType, sessions[0].Model));
        Assert.IsNull(sessions[0].SealedMetadata, "titles are not published");
    }

    [TestMethod]
    public async Task PublicationResumesFromTheCursorsAfterADroppedConnection()
    {
        await using var fleet = await FleetFixture.StartAsync();
        var source = new FakeSource();
        source.Set(Session("s1", "work"));
        var transports = new ConcurrentQueue<FleetWebSocketTransport>();
        var results = new ConcurrentQueue<FleetFactsPublishResult>();
        var outbox = Path.Combine(fleet.Root.Path, "outbox.json");
        await using (var connector = fleet.NodeConnector(source, static _ => true, outbox, async (address, token) =>
               {
                   var transport = await FleetWebSocketTransport.ConnectAsync(address, null, token);
                   transports.Enqueue(transport);
                   return transport;
               }))
        {
            connector.Published += (_, result) => results.Enqueue(result);
            connector.Start();
            await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s1"));

            // The connection drops under the node; the session changes meanwhile.
            transports.Last().Abort();
            await WaitForAsync(() => Task.FromResult(connector.State != FleetConnectorState.Online || transports.Count > 1));
            source.Set(Session("s1", "work", FleetSessionStates.Running), Session("s2", "work"));
            await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()) is var listed
                && listed.Any(session => session.Id == "s1" && session.Version == 2) && listed.Any(session => session.Id == "s2"));
            Assert.IsTrue(transports.Count >= 2, "it reconnected");
            // Each batch went on from the cursor the server gave back, never repeating a sequence.
            var acks = results.Select(static result => result.Ack).ToArray();
            CollectionAssert.AreEqual(acks.Order().Distinct().ToArray(), acks);
            Assert.AreEqual(connector.Cursors.Facts, acks[^1]);
        }

        // Started again on its outbox, it publishes nothing the server already holds.
        results.Clear();
        await using (var again = fleet.NodeConnector(source, static _ => true, outbox))
        {
            again.Published += (_, result) => results.Enqueue(result);
            again.Start();
            await WaitForAsync(() => Task.FromResult(again.State == FleetConnectorState.Online));
            source.Set(Session("s1", "work", FleetSessionStates.Running), Session("s2", "work"), Session("s3", "work"));
            again.Nudge();
            await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s3"));
            Assert.AreEqual(0, results.Sum(static result => result.Unchanged), "the versions the server held were not sent again");
            Assert.AreEqual(1L, (await fleet.Desktop.ListSessionsAsync()).Single(session => session.Id == "s3").Version);
        }
    }

    [TestMethod]
    public async Task APendingNodeWaitsForItsApprovalThenConnectsAndARevokedOneStops()
    {
        await using var fleet = await FleetFixture.StartAsync(approve: false);
        var source = new FakeSource();
        source.Set(Session("s1", "work"));
        await using var connector = fleet.NodeConnector(source, static _ => true);
        connector.Start();
        await WaitForAsync(() => Task.FromResult(connector.State == FleetConnectorState.PendingApproval));
        Assert.AreEqual(FleetErrorCodes.PendingApproval, connector.LastError?.Code);

        await fleet.Desktop.ApproveAsync(fleet.NodeKey.DeviceId);
        await WaitForAsync(() => Task.FromResult(connector.State == FleetConnectorState.Online));
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Count == 1);

        await fleet.Desktop.RevokeAsync(fleet.NodeKey.DeviceId);
        await WaitForAsync(() => Task.FromResult(connector.State == FleetConnectorState.Revoked));
        Assert.AreEqual(FleetErrorCodes.Revoked, connector.LastError?.Code);
    }

    [TestMethod]
    public async Task ADesktopFollowsTheMachinesSessionsAndPresenceOfItsOrganization()
    {
        await using var fleet = await FleetFixture.StartAsync();
        await using var desktop = new FleetConnector(new FleetConnectorOptions
        {
            ServerAddress = fleet.Server.BaseAddress, Identity = fleet.DesktopKey, OutboxPath = Path.Combine(fleet.Root.Path, "desktop-outbox.json"),
            Client = new FleetClientInfo("desktop", "4.0.0", "windows"), WatchDirectory = true, MinimumBackoff = TimeSpan.FromMilliseconds(100),
        });
        var changed = 0;
        desktop.DirectoryChanged += _ => Interlocked.Increment(ref changed);
        desktop.Start();
        await WaitForAsync(() => Task.FromResult(desktop.State == FleetConnectorState.Online));

        var source = new FakeSource();
        source.Set(Session("s1", "work", FleetSessionStates.RequiresAction));
        await using (var node = fleet.NodeConnector(source, static _ => true))
        {
            node.Start();
            await WaitForAsync(() => Task.FromResult(desktop.Directory.Sessions.ContainsKey("s1") && desktop.Directory.Machines.ContainsKey(fleet.NodeKey.DeviceId)
                && desktop.Directory.IsOnline(fleet.NodeKey.DeviceId)));
            var session = desktop.Directory.Sessions["s1"];
            Assert.AreEqual((fleet.NodeKey.DeviceId, FleetSessionStates.RequiresAction), (session.MachineId, session.Fact.State));
            Assert.AreEqual("Mac mini", desktop.Directory.Machines[fleet.NodeKey.DeviceId].Inventory.Name);
        }

        await WaitForAsync(() => Task.FromResult(!desktop.Directory.IsOnline(fleet.NodeKey.DeviceId)));
        Assert.IsTrue(Volatile.Read(ref changed) > 0);
    }

    private static FleetLocalSession Session(string id, string? space, string state = FleetSessionStates.Idle, bool isPrivate = false)
        => new(id, space, isPrivate, 0, state, Created, Created.AddMinutes(state == FleetSessionStates.Idle ? 1 : 2), "anthropic", "claude-opus-5-5",
            new FleetSessionCounts(1, 2, 300, 40));

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Waits.Hang;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The condition did not hold in time.");
            await Task.Delay(50);
        }
    }

    /// <summary>A server set up by a desktop key, with a node key enrolled (and approved unless asked otherwise).</summary>
    private sealed class FleetFixture : IAsyncDisposable
    {
        private FleetFixture(ServerFixture server, TempDirectory root, DeviceIdentity desktopKey, DeviceIdentity nodeKey, FleetServerClient desktop)
        {
            Server = server;
            Root = root;
            DesktopKey = desktopKey;
            NodeKey = nodeKey;
            Desktop = desktop;
        }

        public ServerFixture Server { get; }

        public TempDirectory Root { get; }

        public DeviceIdentity DesktopKey { get; }

        public DeviceIdentity NodeKey { get; }

        public FleetServerClient Desktop { get; }

        public static async Task<FleetFixture> StartAsync(bool approve = true)
        {
            var server = await ServerFixture.StartAsync();
            var root = new TempDirectory();
            var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
            var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
            var desktop = new FleetServerClient(server.BaseAddress, desktopKey);
            await desktop.SetUpAsync(server.SetupCode, "Alex", "Desktop");
            using var node = new FleetServerClient(server.BaseAddress, nodeKey);
            await node.EnrollAsync((await desktop.CreateEnrollmentTokenAsync()).Token, "Mac mini");
            if (approve) await desktop.ApproveAsync(nodeKey.DeviceId);
            return new FleetFixture(server, root, desktopKey, nodeKey, desktop);
        }

        public FleetConnector NodeConnector(IFleetPublicationSource source, Func<string?, bool> publishes, string? outbox = null,
            Func<Uri, CancellationToken, Task<IFleetTransport>>? connect = null)
            => new(new FleetConnectorOptions
            {
                ServerAddress = Server.BaseAddress, Identity = NodeKey, OutboxPath = outbox ?? Path.Combine(Root.Path, $"outbox-{Guid.NewGuid():N}.json"),
                Client = new FleetClientInfo("node", "4.0.0", "macos"), Source = source, PublishesSpace = publishes,
                MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromMilliseconds(500), PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
                PublishDelay = TimeSpan.FromMilliseconds(20), PollInterval = TimeSpan.FromMilliseconds(200),
                ConnectTransport = connect is null ? null : async (address, token) => await connect(address, token),
            });

        public async ValueTask DisposeAsync()
        {
            Desktop.Dispose();
            DesktopKey.Dispose();
            NodeKey.Dispose();
            await Server.DisposeAsync();
            Root.Dispose();
        }
    }

    /// <summary>Sessions a test sets; each change says so.</summary>
    private sealed class FakeSource : IFleetPublicationSource
    {
        private IReadOnlyList<FleetLocalSession> _sessions = [];

        public event Action? Changed;

        public void Set(params FleetLocalSession[] sessions)
        {
            Volatile.Write(ref _sessions, sessions);
            Changed?.Invoke();
        }

        public ValueTask<FleetMachineInventory> ReadInventoryAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new FleetMachineInventory("Mac mini", "macos", "arm64", "4.0.0", ["moves"], null, ["anthropic"]));

        public ValueTask<IReadOnlyList<FleetLocalSession>> ReadSessionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Volatile.Read(ref _sessions));
    }
}
