using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Api;

namespace CodeAlta.Server.Tests;

[TestClass]
public sealed class ControlConnectionTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AnApprovedDeviceIsWelcomedWithItsCursorsAndShowsOnline()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        await server.SetUpWithNodeAsync(owner, mac);

        var (connection, welcome) = await server.ConnectAsync(mac);
        await using (connection)
        {
            Assert.AreEqual(1, welcome.Protocol);
            CollectionAssert.IsSubsetOf(new[] { "presence", "facts", "directory" }, welcome.Features.ToArray());
            Assert.AreEqual(new FleetPublicationCursors(0, 0), welcome.Cursors!.Value.Deserialize(FleetServerJsonContext.Default.FleetPublicationCursors));
            // An answer on the connection implies the presence.
            await connection.SendRequestAsync(FleetServerMethods.PresenceUpdate,
                JsonSerializer.SerializeToElement(new FleetPresenceUpdate("active"), FleetServerJsonContext.Default.FleetPresenceUpdate));
            Assert.IsTrue((await server.SendAsync(owner, HttpMethod.Get, $"/api/v1/devices/{mac.DeviceId}")).Read<DeviceDto>().Online);

            await PublishAsync(connection, 3, Fact("s1", 1, 1));
            await connection.SendRequestAsync(FleetServerMethods.InventoryPublish,
                JsonSerializer.SerializeToElement(new FleetInventoryPublish(2, new FleetMachineInventory("Mac mini", "macos")), FleetServerJsonContext.Default.FleetInventoryPublish));
        }

        // Reconnected, it resumes from what the server acknowledged.
        var (again, resumed) = await server.ConnectAsync(mac);
        await using (again)
        {
            Assert.AreEqual(new FleetPublicationCursors(3, 2), resumed.Cursors!.Value.Deserialize(FleetServerJsonContext.Default.FleetPublicationCursors));
        }
    }

    [TestMethod]
    public async Task PendingRevokedAndUnknownDevicesAreRefusedWithWhatToDo()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        using var stranger = new TestDevice("Stranger");
        var setup = await server.SetUpAsync(owner);
        await server.EnrollAsync(owner, mac);

        var pending = await Assert.ThrowsExactlyAsync<FleetHandshakeException>(() => server.ConnectAsync(mac));
        Assert.AreEqual((FleetErrorCodes.PendingApproval, FleetErrorHints.WaitForApproval), (pending.Error.Code, pending.Error.Hint));
        var unknown = await Assert.ThrowsExactlyAsync<FleetHandshakeException>(() => server.ConnectAsync(stranger));
        Assert.AreEqual((FleetErrorCodes.Unauthenticated, FleetErrorHints.Reauthenticate), (unknown.Error.Code, unknown.Error.Hint));

        var add = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, mac.DeviceId, mac.PublicKey, FleetDeviceKinds.Node, setup.Owner.Id, setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{mac.DeviceId}/approve", new DeviceListChangeRequest(add))).Status);
        var closing = new TaskCompletionSource<FleetError>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (connection, _) = await server.ConnectAsync(mac, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetServerMethods.ConnectionClosing) closing.TrySetResult(fleetEvent.Payload!.Value.Deserialize(FleetServerJsonContext.Default.FleetError)!);
            return ValueTask.CompletedTask;
        }));
        await using (connection)
        {
            // Revoked while connected: the server says so, then closes its connection.
            var remove = await server.NextEntryAsync(owner, FleetDeviceListOperations.Remove, mac.DeviceId, null, null, null, setup.Organization.Id);
            Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{mac.DeviceId}/revoke", new DeviceListChangeRequest(remove))).Status);
            Assert.AreEqual(FleetErrorCodes.Revoked, (await closing.Task.WaitAsync(Waits.Hang)).Code);
            await connection.Completion.WaitAsync(Waits.Hang);
        }

        var revoked = await Assert.ThrowsExactlyAsync<FleetHandshakeException>(() => server.ConnectAsync(mac));
        Assert.AreEqual(FleetErrorCodes.Revoked, revoked.Error.Code);
        Assert.IsFalse((await server.SendAsync(owner, HttpMethod.Get, $"/api/v1/devices/{mac.DeviceId}")).Read<DeviceDto>().Online);
    }

    [TestMethod]
    public async Task FactsArePublishedIdempotentlyByVersion()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        await server.SetUpWithNodeAsync(owner, mac);
        var (connection, _) = await server.ConnectAsync(mac);
        await using var _1 = connection;

        var first = await PublishAsync(connection, 1, Fact("s1", 1, 2), Fact("s2", 1, 1));
        Assert.AreEqual(new FleetFactsPublishResult(1, 2, 0, []), first with { Stale = [] });
        var replay = await PublishAsync(connection, 1, Fact("s1", 1, 2), Fact("s2", 1, 1));
        Assert.AreEqual((1L, 0, 2), (replay.Ack, replay.Applied, replay.Unchanged));
        var mixed = await PublishAsync(connection, 2, Fact("s1", 1, 1, "idle"), Fact("s2", 1, 5, "requires_action"), Fact("s1", 0, 99));
        Assert.AreEqual((2L, 1, 1), (mixed.Ack, mixed.Applied, mixed.Unchanged));
        CollectionAssert.AreEqual(new[] { "s1" }, mixed.Stale.ToArray(), "A fact of an older epoch is refused.");

        var sessions = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sessions")).Read<ListResponse<SessionDto>>().Items.ToDictionary(static s => s.Id);
        Assert.AreEqual(("running", 2L), (sessions["s1"].State, sessions["s1"].Version));
        Assert.AreEqual(("requires_action", 5L, mac.DeviceId, "sealed-blob"), (sessions["s2"].State, sessions["s2"].Version, sessions["s2"].MachineId, sessions["s2"].SealedMetadata));
        Assert.AreEqual(new FleetSessionCounts(5, 10, 1000, 500), sessions["s2"].Counts);

        var invalid = await Assert.ThrowsExactlyAsync<FleetException>(() => PublishAsync(connection, 3, Fact("", 1, 1)));
        Assert.AreEqual(CoordinatorErrorCodes.PublicationInvalid, invalid.Error.Code);
        var unreadable = await Assert.ThrowsExactlyAsync<FleetException>(() =>
            connection.SendRequestAsync(FleetServerMethods.FactsPublish, JsonDocument.Parse("{\"sequence\":\"x\"}").RootElement));
        Assert.AreEqual(FleetErrorCodes.InvalidParams, unreadable.Error.Code);
    }

    [TestMethod]
    public async Task AWatcherSeesTheChangesOfItsOrganization()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        await server.SetUpWithNodeAsync(owner, mac);
        var events = Channel.CreateUnbounded<FleetDirectoryChange>();
        var (desktop, _) = await server.ConnectAsync(owner, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetServerMethods.DirectoryChanged)
            {
                events.Writer.TryWrite(fleetEvent.Payload!.Value.Deserialize(FleetServerJsonContext.Default.FleetDirectoryChange)!);
            }

            return ValueTask.CompletedTask;
        }));
        await using var _1 = desktop;

        var snapshot = (await desktop.SendRequestAsync(FleetServerMethods.DirectoryWatch))!.Value.Deserialize(FleetServerJsonContext.Default.FleetDirectorySnapshot)!;
        Assert.AreEqual(0, snapshot.Machines.Count);
        CollectionAssert.AreEqual(new[] { owner.DeviceId }, snapshot.Presence.Select(static p => p.DeviceId).ToArray());

        var (node, _) = await server.ConnectAsync(mac);
        await using (node)
        {
            var online = await NextAsync(events, FleetServerMethods.ChangeKinds.Presence);
            Assert.AreEqual((mac.DeviceId, true), (online.Id, online.Presence!.Online));

            await node.SendRequestAsync(FleetServerMethods.PresenceUpdate,
                JsonSerializer.SerializeToElement(new FleetPresenceUpdate("busy", 2), FleetServerJsonContext.Default.FleetPresenceUpdate));
            var busy = await NextAsync(events, FleetServerMethods.ChangeKinds.Presence);
            Assert.AreEqual(("busy", 2), (busy.Presence!.State, busy.Presence.ActiveSessions));
            // The same state again is no change: a heartbeat writes nothing and announces nothing.
            await node.SendRequestAsync(FleetServerMethods.PresenceUpdate,
                JsonSerializer.SerializeToElement(new FleetPresenceUpdate("busy", 2), FleetServerJsonContext.Default.FleetPresenceUpdate));

            await node.SendRequestAsync(FleetServerMethods.InventoryPublish, JsonSerializer.SerializeToElement(
                new FleetInventoryPublish(1, new FleetMachineInventory("Mac mini", "macos", "arm64", "4.0.0", ["sandbox"], ["macos"], ["anthropic"])),
                FleetServerJsonContext.Default.FleetInventoryPublish));
            var machine = await NextAsync(events, FleetServerMethods.ChangeKinds.Machine);
            Assert.AreEqual(("Mac mini", "arm64", FleetDeviceKinds.Node), (machine.Machine!.Inventory.Name, machine.Machine.Inventory.Architecture, machine.Machine.Kind));

            await PublishAsync(node, 1, Fact("s1", 1, 1));
            var session = await NextAsync(events, FleetServerMethods.ChangeKinds.Session);
            Assert.AreEqual(("s1", mac.DeviceId, 1L), (session.Id, session.Session!.MachineId, session.Session.Fact.Version));
            // An unchanged fact announces nothing; the next event is the newer version.
            await PublishAsync(node, 2, Fact("s1", 1, 1));
            await PublishAsync(node, 3, Fact("s1", 1, 2));
            Assert.AreEqual(2L, (await NextAsync(events, FleetServerMethods.ChangeKinds.Session)).Session!.Fact.Version);

            var machines = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/machines")).Read<ListResponse<MachineDto>>().Items;
            Assert.AreEqual((mac.DeviceId, "Mac mini", true), (machines.Single().DeviceId, machines.Single().Name, machines.Single().Online));
        }

        var offline = await NextAsync(events, FleetServerMethods.ChangeKinds.Presence);
        Assert.AreEqual((mac.DeviceId, false), (offline.Id, offline.Presence!.Online));
    }

    [TestMethod]
    public async Task ARevokedMachineLeavesTheDirectoryOfItsWatchers()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        var (setup, _) = await server.SetUpWithNodeAsync(owner, mac);
        var events = Channel.CreateUnbounded<FleetDirectoryChange>();
        var (desktop, _) = await server.ConnectAsync(owner, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetServerMethods.DirectoryChanged) events.Writer.TryWrite(fleetEvent.Payload!.Value.Deserialize(FleetServerJsonContext.Default.FleetDirectoryChange)!);
            return ValueTask.CompletedTask;
        }));
        await using var _1 = desktop;
        await desktop.SendRequestAsync(FleetServerMethods.DirectoryWatch);
        var (node, _) = await server.ConnectAsync(mac);
        await using (node)
        {
            await node.SendRequestAsync(FleetServerMethods.InventoryPublish, JsonSerializer.SerializeToElement(
                new FleetInventoryPublish(1, new FleetMachineInventory("Mac mini", "macos")), FleetServerJsonContext.Default.FleetInventoryPublish));
            await NextAsync(events, FleetServerMethods.ChangeKinds.Machine);
            await PublishAsync(node, 1, Fact("s1", 1, 1));
            await NextAsync(events, FleetServerMethods.ChangeKinds.Session);
        }

        // A handler of the machine still holds its record as it was before the revoke.
        var held = (await server.GetService<CodeAlta.Server.Storage.ICoordinatorStore>().GetDeviceAsync(mac.DeviceId))!;
        var remove = await server.NextEntryAsync(owner, FleetDeviceListOperations.Remove, mac.DeviceId, null, null, null, setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{mac.DeviceId}/revoke", new DeviceListChangeRequest(remove))).Status);

        // The watchers hear that it left, and a watch that starts later does not list it, its sessions or its presence.
        Assert.AreEqual(mac.DeviceId, (await NextAsync(events, FleetServerMethods.ChangeKinds.MachineRemoved)).Id);

        // What that handler publishes after the revoke is not announced: the next change the watcher hears is the desktop's own presence.
        var publications = server.GetService<CodeAlta.Server.Services.PublicationService>();
        await publications.PublishInventoryAsync(held, new FleetInventoryPublish(2, new FleetMachineInventory("Mac mini", "macos")), CancellationToken.None);
        await publications.PublishFactsAsync(held, new FleetFactsPublish(2, [Fact("s2", 1, 1)]), CancellationToken.None);
        await desktop.SendRequestAsync(FleetServerMethods.PresenceUpdate,
            JsonSerializer.SerializeToElement(new FleetPresenceUpdate("busy", 1), FleetServerJsonContext.Default.FleetPresenceUpdate));
        while (true)
        {
            var next = await events.Reader.ReadAsync().AsTask().WaitAsync(Waits.Hang);
            Assert.IsFalse(next.Kind is FleetServerMethods.ChangeKinds.Machine or FleetServerMethods.ChangeKinds.Session && (next.Machine?.DeviceId ?? next.Session?.MachineId) == mac.DeviceId,
                $"a late {next.Kind} of the revoked machine");
            if (next.Kind == FleetServerMethods.ChangeKinds.Presence && next.Id == owner.DeviceId && next.Presence?.State == "busy") break;
        }
        var snapshot = (await desktop.SendRequestAsync(FleetServerMethods.DirectoryWatch))!.Value.Deserialize(FleetServerJsonContext.Default.FleetDirectorySnapshot)!;
        Assert.AreEqual(0, snapshot.Machines.Count(machine => machine.DeviceId == mac.DeviceId));
        Assert.AreEqual(0, snapshot.Sessions.Count(session => session.MachineId == mac.DeviceId));
        Assert.AreEqual(0, snapshot.Presence.Count(presence => presence.DeviceId == mac.DeviceId));
    }

    [TestMethod]
    public async Task ANodeRenamedByItsInventoryIsListedByItsNewNameInTheDevices()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        await server.SetUpWithNodeAsync(owner, mac);
        var (node, _) = await server.ConnectAsync(mac);
        await using (node)
        {
            // altanode run --name: the node says its new name in its inventory.
            await node.SendRequestAsync(FleetServerMethods.InventoryPublish, JsonSerializer.SerializeToElement(
                new FleetInventoryPublish(1, new FleetMachineInventory("studio", "macos")), FleetServerJsonContext.Default.FleetInventoryPublish));
        }

        Assert.AreEqual("studio", (await server.SendAsync(owner, HttpMethod.Get, $"/api/v1/devices/{mac.DeviceId}")).Read<DeviceDto>().Name);
        Assert.AreEqual("studio", (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/machines")).Read<ListResponse<MachineDto>>().Items.Single().Name);
    }

    [TestMethod]
    public void ARemovedMachineLeavesTheDirectoryViewWithItsSessionsAndPresence()
    {
        var machine = new FleetDirectoryMachine("mac", FleetDeviceKinds.Node, new FleetMachineInventory("Mac mini", "macos"), Created);
        var view = CodeAlta.Fleet.Server.FleetDirectoryView.From(new FleetDirectorySnapshot([machine, machine with { DeviceId = "pc" }],
            [new FleetDirectorySession("mac", Fact("s1", 1, 1)), new FleetDirectorySession("pc", Fact("s2", 1, 1))],
            [new FleetPresence("mac", false, null, null, Created), new FleetPresence("pc", true, null, null, Created)]));

        var removed = view.Apply(new FleetDirectoryChange(FleetServerMethods.ChangeKinds.MachineRemoved, "mac"));

        CollectionAssert.AreEqual(new[] { "pc" }, removed.Machines.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { "s2" }, removed.Sessions.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { "pc" }, removed.Presence.Keys.ToArray());
        // Its connection closes after it left: the offline presence that follows does not list it again.
        var closed = removed.Apply(new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Presence, "mac", Presence: new FleetPresence("mac", false, null, null, Created)));
        CollectionAssert.AreEqual(new[] { "pc" }, closed.Presence.Keys.ToArray());
    }

    [TestMethod]
    public void ALateUpdateOfARemovedMachine_DoesNotListItAgain()
    {
        var machine = new FleetDirectoryMachine("mac", FleetDeviceKinds.Node, new FleetMachineInventory("Mac mini", "macos"), Created);
        var view = CodeAlta.Fleet.Server.FleetDirectoryView.From(new FleetDirectorySnapshot([machine], [new FleetDirectorySession("mac", Fact("s1", 1, 1))],
            [new FleetPresence("mac", true, null, null, Created)]));
        var removed = view.Apply(new FleetDirectoryChange(FleetServerMethods.ChangeKinds.MachineRemoved, "mac"));

        // An inventory, facts and a presence the server sent from handlers that were in flight when the machine was revoked.
        var late = removed.Apply(new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Machine, "mac", Machine: machine with { UpdatedAt = Created.AddMinutes(1) }))
            .Apply(new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Session, "s2", Session: new FleetDirectorySession("mac", Fact("s2", 1, 1))))
            .Apply(new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Presence, "mac", Presence: new FleetPresence("mac", true, null, null, Created)));

        Assert.AreEqual(0, late.Machines.Count);
        Assert.AreEqual(0, late.Sessions.Count);
        Assert.AreEqual(0, late.Presence.Count);
    }

    [TestMethod]
    public async Task AMachineWithdrawsTheFactsOfItsOwnSessionsOnlyAuditedAndAnnounced()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        using var pc = new TestDevice("PC");
        var (setup, _) = await server.SetUpWithNodeAsync(owner, mac);
        var pending = await server.EnrollAsync(owner, pc);
        var add = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, pending.Id, pc.PublicKey, FleetDeviceKinds.Node, setup.Owner.Id, setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(add))).Status);
        var events = Channel.CreateUnbounded<FleetDirectoryChange>();
        var (desktop, _) = await server.ConnectAsync(owner, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetServerMethods.DirectoryChanged) events.Writer.TryWrite(fleetEvent.Payload!.Value.Deserialize(FleetServerJsonContext.Default.FleetDirectoryChange)!);
            return ValueTask.CompletedTask;
        }));
        await using var _1 = desktop;
        var (node, welcome) = await server.ConnectAsync(mac);
        await using var _2 = node;
        var (other, _) = await server.ConnectAsync(pc);
        await using var _3 = other;
        CollectionAssert.Contains(welcome.Features.ToArray(), FleetServerMethods.FactsWithdraw);
        await desktop.SendRequestAsync(FleetServerMethods.DirectoryWatch);
        await PublishAsync(node, 1, Fact("s1", 1, 1), Fact("s2", 1, 1));

        // Another machine cannot take them back.
        Assert.AreEqual(0, (await WithdrawAsync(other, "s1", "s2")).Removed.Count);
        var result = await WithdrawAsync(node, "s1", "unknown", "s1");
        CollectionAssert.AreEqual(new[] { "s1" }, result.Removed.ToArray());
        var removed = await NextAsync(events, FleetServerMethods.ChangeKinds.SessionRemoved);
        Assert.AreEqual("s1", removed.Id);
        Assert.AreEqual((mac.DeviceId, 1L, 1L), (removed.Session?.MachineId, removed.Session?.Fact.Epoch, removed.Session?.Fact.Version),
            "a removal names the facts it removed, so that a watcher keeps newer ones");
        var sessions = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/sessions")).Read<ListResponse<SessionDto>>().Items;
        CollectionAssert.AreEqual(new[] { "s2" }, sessions.Select(static session => session.Id).ToArray());
        Assert.AreEqual(0, (await WithdrawAsync(node, "s1")).Removed.Count, "a withdrawal is idempotent");

        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit")).Read<ListResponse<AuditRecordDto>>().Items.Where(static record => record.Action == "session.withdraw").ToArray();
        Assert.AreEqual(1, audit.Length, "only a deletion is audited");
        Assert.AreEqual((mac.DeviceId, setup.Owner.Id, "{\"sessions\":[\"s1\"]}"), (audit[0].ActorDeviceId, audit[0].ActorPersonId, audit[0].Details));

        // Published again later, the session starts over at any version.
        Assert.AreEqual(1, (await PublishAsync(node, 2, Fact("s1", 1, 1))).Applied);
        var tooMany = await Assert.ThrowsExactlyAsync<FleetException>(() => WithdrawAsync(node, [.. Enumerable.Range(0, 501).Select(static index => $"s{index}")]));
        Assert.AreEqual(CoordinatorErrorCodes.PublicationInvalid, tooMany.Error.Code);
    }

    [TestMethod]
    public async Task AServerThatStopsClosesItsControlConnectionsAtOnceAndAsksForAReconnection()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        await server.SetUpWithNodeAsync(owner, mac);
        var closing = new TaskCompletionSource<FleetError>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (connection, _) = await server.ConnectAsync(mac, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            if (fleetEvent.Event == FleetServerMethods.ConnectionClosing) closing.TrySetResult(fleetEvent.Payload!.Value.Deserialize(FleetServerJsonContext.Default.FleetError)!);
            return ValueTask.CompletedTask;
        }));
        await using (connection)
        {
            await connection.SendRequestAsync(FleetServerMethods.PresenceUpdate,
                JsonSerializer.SerializeToElement(new FleetPresenceUpdate("active"), FleetServerJsonContext.Default.FleetPresenceUpdate));

            // A stop (a switch of versions, a service manager's) does not wait for the shutdown timeout of the host (30 s)
            // to abort the connection: the node is told at once, and reconnects to the next version.
            var stopping = System.Diagnostics.Stopwatch.StartNew();
            await server.Server.DisposeAsync().AsTask().WaitAsync(Waits.Hang);
            Assert.IsLessThan(TimeSpan.FromSeconds(15), stopping.Elapsed, $"The stop took {stopping.Elapsed}.");
            var error = await closing.Task.WaitAsync(Waits.Hang);
            Assert.AreEqual((FleetErrorCodes.ConnectionClosed, FleetErrorHints.Reconnect), (error.Code, error.Hint));
            await connection.Completion.WaitAsync(Waits.Hang);
        }
    }

    [TestMethod]
    public async Task ANewConnectionOfADeviceReplacesItsOldOne()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        await server.SetUpAsync(owner);
        var (first, _) = await server.ConnectAsync(owner);
        await using var _1 = first;
        var (second, _) = await server.ConnectAsync(owner);
        await using var _2 = second;
        await first.Completion.WaitAsync(Waits.Hang);
        Assert.AreEqual(FleetConnectionState.Open, second.State);
        Assert.IsTrue((await server.SendAsync(owner, HttpMethod.Get, $"/api/v1/devices/{owner.DeviceId}")).Read<DeviceDto>().Online);
    }

    [TestMethod]
    public async Task TheOpenApiDocumentDescribesTheApi()
    {
        await using var server = await ServerFixture.StartAsync();
        using var response = await server.Http.GetAsync("/openapi/v1.json");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        StringAssert.StartsWith(document.RootElement.GetProperty("openapi").GetString(), "3.");
        var paths = document.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/api/v1/setup", "/api/v1/organization", "/api/v1/devices", "/api/v1/devices/{id}/approve", "/api/v1/machines", "/api/v1/sessions", "/api/v1/audit" })
        {
            Assert.IsTrue(paths.TryGetProperty(path, out _), path);
        }

        Assert.IsFalse(paths.TryGetProperty(FleetServerMethods.ControlPath, out _));
    }

    private static FleetSessionFact Fact(string id, long epoch, long version, string state = "running")
        => new(id, epoch, version, state, Created, Created.AddMinutes(version), "anthropic", "claude-opus-5-5", new FleetSessionCounts(version, 2 * version, 200 * version, 100 * version),
            "sealed-blob");

    private static async Task<FleetFactsPublishResult> PublishAsync(FleetConnection connection, long sequence, params FleetSessionFact[] facts)
        => (await connection.SendRequestAsync(FleetServerMethods.FactsPublish, new FleetFactsPublish(sequence, facts), FleetServerJsonContext.Default.FleetFactsPublish,
            FleetServerJsonContext.Default.FleetFactsPublishResult))!;

    private static async Task<FleetFactsWithdrawResult> WithdrawAsync(FleetConnection connection, params string[] sessionIds)
        => (await connection.SendRequestAsync(FleetServerMethods.FactsWithdraw, new FleetFactsWithdraw(sessionIds), FleetServerJsonContext.Default.FleetFactsWithdraw,
            FleetServerJsonContext.Default.FleetFactsWithdrawResult))!;

    private static async Task<FleetDirectoryChange> NextAsync(Channel<FleetDirectoryChange> events, string kind)
    {
        while (true)
        {
            var change = await events.Reader.ReadAsync().AsTask().WaitAsync(Waits.Hang);
            if (change.Kind == kind) return change;
        }
    }
}
