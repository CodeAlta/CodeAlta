using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Control;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The relay of session channels at the level of the server: <c>relay.open</c>, <c>relay.incoming</c>,
/// <c>relay.accept</c> and the two WebSockets with their one-time tickets, the bytes piped as they are, the audit of who
/// reached whom with the bytes and never the content, the refusals, the revocation and the limits.
/// </summary>
[TestClass]
public sealed class RelayTests
{
    [TestMethod]
    public async Task TwoDevicesOfTheOrganizationAreSplicedAndTheAuditCountsBytesNotContent()
    {
        await using var rig = await RelayRig.StartAsync();
        var (viewerTicket, targetTicket) = await rig.OpenAcceptedAsync();
        Assert.AreEqual((rig.Mac.DeviceId, rig.Owner.DeviceId), (viewerTicket.Peer, targetTicket.Peer));

        using var viewer = await rig.OpenSideAsync(rig.Owner, viewerTicket.Ticket);
        using var target = await rig.OpenSideAsync(rig.Mac, targetTicket.Ticket);
        var secret = TestText.Utf8("secret-content");
        await viewer.SendAsync(secret, WebSocketMessageType.Binary, true, CancellationToken.None);
        CollectionAssert.AreEqual(secret, await ReceiveAsync(target, secret.Length));
        var reply = new byte[1000];
        Random.Shared.NextBytes(reply);
        await target.SendAsync(reply, WebSocketMessageType.Binary, true, CancellationToken.None);
        CollectionAssert.AreEqual(reply, await ReceiveAsync(viewer, reply.Length));

        await viewer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        Assert.IsTrue(await EndsAsync(target), "The close of one side reaches the other.");
        await target.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        var record = await rig.WaitAuditAsync(static record => record.Action == "relay");
        Assert.AreEqual((AuditEntry.Success, rig.Owner.DeviceId, rig.Mac.DeviceId), (record.Result, record.ActorDeviceId, record.Target));
        using var details = JsonDocument.Parse(record.Details!);
        Assert.AreEqual((secret.Length, reply.Length), (details.RootElement.GetProperty("up").GetInt32(), details.RootElement.GetProperty("down").GetInt32()));
        Assert.AreEqual(viewerTicket.RelayId, details.RootElement.GetProperty("relay").GetString());
        Assert.IsFalse(record.Details!.Contains("secret", StringComparison.Ordinal), "The audit never holds content.");
    }

    [TestMethod]
    public async Task RelaysToUnknownOfflineOrDecliningDevicesAreRefused()
    {
        await using var rig = await RelayRig.StartAsync();
        using var pc = new TestDevice("PC");
        await rig.ApproveNodeAsync(pc);

        Assert.AreEqual(CoordinatorErrorCodes.DeviceNotFound, (await rig.RefusedOpenAsync("aaaaaaaaaaaaaaaaaaaaaaaaaa")).Code);
        Assert.AreEqual(FleetErrorCodes.InvalidParams, (await rig.RefusedOpenAsync(rig.Owner.DeviceId)).Code, "A device does not relay to itself.");
        Assert.AreEqual(FleetRelayErrorCodes.TargetOffline, (await rig.RefusedOpenAsync(pc.DeviceId)).Code, "An approved device without a control connection is offline.");

        var open = rig.OpenAsync(rig.Mac.DeviceId);
        var incoming = await rig.ReadIncomingAsync();
        Assert.AreEqual(rig.Owner.DeviceId, incoming.From);
        await rig.Target.SendRequestAsync(FleetServerMethods.RelayClose, JsonSerializer.SerializeToElement(new FleetRelayClose(incoming.RelayId, "declined"),
            FleetServerJsonContext.Default.FleetRelayClose));
        var declined = await Assert.ThrowsExactlyAsync<FleetException>(() => open);
        Assert.AreEqual(FleetRelayErrorCodes.Declined, declined.Error.Code);
        var refusal = await rig.WaitAuditAsync(static record => record.Action == "relay.open" && record.Result == FleetRelayErrorCodes.Declined);
        Assert.AreEqual((rig.Owner.DeviceId, rig.Mac.DeviceId), (refusal.ActorDeviceId, refusal.Target));
        Assert.AreEqual(0, rig.Hub.Count(rig.Mac.DeviceId), "A declined relay is gone.");
    }

    [TestMethod]
    public async Task ATicketWorksOnceForItsOwnDeviceWithASignedRequest()
    {
        await using var rig = await RelayRig.StartAsync();
        var (viewerTicket, targetTicket) = await rig.OpenAcceptedAsync();

        Assert.AreEqual(HttpStatusCode.Unauthorized, await rig.RefusedSideAsync(null, viewerTicket.Ticket), "A side is signed by its device key.");
        Assert.AreEqual(HttpStatusCode.Forbidden, await rig.RefusedSideAsync(rig.Mac, viewerTicket.Ticket), "The ticket of the viewer is the viewer's only.");
        using var viewer = await rig.OpenSideAsync(rig.Owner, viewerTicket.Ticket);
        Assert.AreEqual(HttpStatusCode.Forbidden, await rig.RefusedSideAsync(rig.Owner, viewerTicket.Ticket), "A ticket works once.");
        using var target = await rig.OpenSideAsync(rig.Mac, targetTicket.Ticket);
        await viewer.SendAsync(TestText.Utf8("ok"), WebSocketMessageType.Binary, true, CancellationToken.None);
        CollectionAssert.AreEqual(TestText.Utf8("ok"), await ReceiveAsync(target, 2));
    }

    [TestMethod]
    public async Task ARevokedDeviceLosesItsRelaysAndIsRefusedOthers()
    {
        await using var rig = await RelayRig.StartAsync();
        var (viewerTicket, targetTicket) = await rig.OpenAcceptedAsync();
        using var viewer = await rig.OpenSideAsync(rig.Owner, viewerTicket.Ticket);
        using var target = await rig.OpenSideAsync(rig.Mac, targetTicket.Ticket);

        await rig.RevokeAsync(rig.Mac);

        Assert.IsTrue(await EndsAsync(viewer), "The relay of a revoked device is cut.");
        var record = await rig.WaitAuditAsync(static record => record.Action == "relay");
        Assert.AreEqual("relay.revoked", record.Result);
        var refused = await rig.RefusedOpenAsync(rig.Mac.DeviceId);
        Assert.AreEqual(FleetErrorCodes.Forbidden, refused.Code, "A revoked device is no member to reach.");
    }

    [TestMethod]
    public async Task ARelayPastItsBytesIsClosedAndAuditedSo()
    {
        await using var rig = await RelayRig.StartAsync(new RelayOptions { MaxBytes = 1000 });
        var (viewerTicket, targetTicket) = await rig.OpenAcceptedAsync();
        using var viewer = await rig.OpenSideAsync(rig.Owner, viewerTicket.Ticket);
        using var target = await rig.OpenSideAsync(rig.Mac, targetTicket.Ticket);

        await viewer.SendAsync(new byte[600], WebSocketMessageType.Binary, true, CancellationToken.None);
        await ReceiveAsync(target, 600);
        await viewer.SendAsync(new byte[600], WebSocketMessageType.Binary, true, CancellationToken.None);

        Assert.IsTrue(await EndsAsync(target));
        var record = await rig.WaitAuditAsync(static record => record.Action == "relay");
        Assert.AreEqual("relay.limit", record.Result);
    }

    [TestMethod]
    public async Task AnIdleRelayIsClosed()
    {
        await using var rig = await RelayRig.StartAsync(new RelayOptions { IdleTimeout = TimeSpan.FromMilliseconds(300) });
        var (viewerTicket, targetTicket) = await rig.OpenAcceptedAsync();
        using var viewer = await rig.OpenSideAsync(rig.Owner, viewerTicket.Ticket);
        using var target = await rig.OpenSideAsync(rig.Mac, targetTicket.Ticket);

        Assert.IsTrue(await EndsAsync(viewer));
        Assert.AreEqual("relay.idle", (await rig.WaitAuditAsync(static record => record.Action == "relay")).Result);
    }

    [TestMethod]
    public async Task AServerThatStopsEndsItsRelaysAtOnce()
    {
        await using var rig = await RelayRig.StartAsync();
        var (viewerTicket, targetTicket) = await rig.OpenAcceptedAsync();
        using var viewer = await rig.OpenSideAsync(rig.Owner, viewerTicket.Ticket);
        using var target = await rig.OpenSideAsync(rig.Mac, targetTicket.Ticket);
        var hello = TestText.Utf8("hello");
        await viewer.SendAsync(hello, WebSocketMessageType.Binary, true, CancellationToken.None);
        CollectionAssert.AreEqual(hello, await ReceiveAsync(target, hello.Length));

        // A switch of versions does not wait for the shutdown timeout of the host (30 s) to cut the session channels.
        var stopping = System.Diagnostics.Stopwatch.StartNew();
        await rig.Server.Server.DisposeAsync().AsTask().WaitAsync(Waits.Hang);
        Assert.IsLessThan(TimeSpan.FromSeconds(15), stopping.Elapsed, $"The stop took {stopping.Elapsed}.");
        Assert.IsTrue(await EndsAsync(viewer));
        Assert.IsTrue(await EndsAsync(target));
    }

    [TestMethod]
    public async Task ADeviceHasAtMostItsShareOfRelays()
    {
        await using var rig = await RelayRig.StartAsync(new RelayOptions { MaxRelaysPerDevice = 1 });
        await rig.OpenAcceptedAsync();

        var refused = await rig.RefusedOpenAsync(rig.Mac.DeviceId);
        Assert.AreEqual((FleetRelayErrorCodes.TooMany, FleetErrorHints.Retry), (refused.Code, refused.Hint));
    }

    private static async Task<byte[]> ReceiveAsync(WebSocket socket, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        using var timeout = new CancellationTokenSource(Waits.Hang);
        while (read < count)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(read), timeout.Token);
            Assert.AreEqual(WebSocketMessageType.Binary, result.MessageType);
            read += result.Count;
        }

        return buffer;
    }

    // Whether the server ended the socket: a close frame, or a cut.
    private static async Task<bool> EndsAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(Waits.Hang);
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return true;
            }
        }
        catch (WebSocketException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>A server, its owner's controller device as viewer, and a node with a control connection that hears the relays offered to it.</summary>
    private sealed class RelayRig : IAsyncDisposable
    {
        private readonly Channel<FleetRelayIncoming> _incoming = Channel.CreateUnbounded<FleetRelayIncoming>();

        private RelayRig(ServerFixture server, TestDevice owner, TestDevice mac, string organizationId, string ownerPersonId)
        {
            Server = server;
            Owner = owner;
            Mac = mac;
            OrganizationId = organizationId;
            OwnerPersonId = ownerPersonId;
        }

        public string OwnerPersonId { get; }

        public ServerFixture Server { get; }

        public TestDevice Owner { get; }

        public TestDevice Mac { get; }

        public string OrganizationId { get; }

        public FleetConnection Viewer { get; private set; } = null!;

        public FleetConnection Target { get; private set; } = null!;

        public RelayHub Hub => Server.GetService<RelayHub>();

        public static async Task<RelayRig> StartAsync(RelayOptions? options = null)
        {
            var server = await ServerFixture.StartAsync(relay: options);
            var owner = new TestDevice("Desktop");
            var mac = new TestDevice("Mac mini");
            var (setup, _) = await server.SetUpWithNodeAsync(owner, mac);
            var rig = new RelayRig(server, owner, mac, setup.Organization.Id, setup.Owner.Id);
            rig.Viewer = (await server.ConnectAsync(owner)).Connection;
            rig.Target = (await server.ConnectAsync(mac, connection => connection.SetEventHandler((fleetEvent, _) =>
            {
                if (fleetEvent.Event == FleetServerMethods.RelayIncoming)
                    rig._incoming.Writer.TryWrite(fleetEvent.Payload!.Value.Deserialize(FleetServerJsonContext.Default.FleetRelayIncoming)!);
                return ValueTask.CompletedTask;
            }))).Connection;
            return rig;
        }

        public Task<FleetRelayTicket?> OpenAsync(string target)
            => Viewer.SendRequestAsync(FleetServerMethods.RelayOpen, new FleetRelayOpen(target), FleetServerJsonContext.Default.FleetRelayOpen,
                FleetServerJsonContext.Default.FleetRelayTicket, null, CancellationToken.None);

        public async Task<FleetRelayIncoming> ReadIncomingAsync()
        {
            using var timeout = new CancellationTokenSource(Waits.Hang);
            return await _incoming.Reader.ReadAsync(timeout.Token);
        }

        public async Task<(FleetRelayTicket Viewer, FleetRelayTicket Target)> OpenAcceptedAsync()
        {
            var open = OpenAsync(Mac.DeviceId);
            var incoming = await ReadIncomingAsync();
            var target = await Target.SendRequestAsync(FleetServerMethods.RelayAccept, new FleetRelayRef(incoming.RelayId), FleetServerJsonContext.Default.FleetRelayRef,
                FleetServerJsonContext.Default.FleetRelayTicket, null, CancellationToken.None);
            var viewer = await open;
            Assert.AreEqual(incoming.RelayId, viewer!.RelayId);
            return (viewer, target!);
        }

        public async Task<FleetError> RefusedOpenAsync(string target)
            => (await Assert.ThrowsExactlyAsync<FleetException>(() => OpenAsync(target))).Error;

        public async Task<ClientWebSocket> OpenSideAsync(TestDevice device, string ticket)
        {
            var socket = Side(device, ticket, out var address);
            await socket.ConnectAsync(address, CancellationToken.None);
            return socket;
        }

        public async Task<HttpStatusCode> RefusedSideAsync(TestDevice? device, string ticket)
        {
            using var socket = Side(device, ticket, out var address);
            socket.Options.CollectHttpResponseDetails = true;
            await Assert.ThrowsExactlyAsync<WebSocketException>(() => socket.ConnectAsync(address, CancellationToken.None));
            return socket.HttpStatusCode;
        }

        public async Task ApproveNodeAsync(TestDevice node)
        {
            var pending = await Server.EnrollAsync(Owner, node);
            var entry = await Server.NextEntryAsync(Owner, FleetDeviceListOperations.Add, pending.Id, node.PublicKey, FleetDeviceKinds.Node, OwnerPersonId, OrganizationId);
            Assert.AreEqual(HttpStatusCode.OK, (await Server.SendAsync(Owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(entry))).Status);
        }

        public async Task RevokeAsync(TestDevice device)
        {
            var entry = await Server.NextEntryAsync(Owner, FleetDeviceListOperations.Remove, device.DeviceId, null, null, null, OrganizationId);
            var answer = await Server.SendAsync(Owner, HttpMethod.Post, $"/api/v1/devices/{device.DeviceId}/revoke", new DeviceListChangeRequest(entry));
            Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
        }

        public async Task<AuditRecordDto> WaitAuditAsync(Func<AuditRecordDto, bool> match)
        {
            var deadline = DateTime.UtcNow + Waits.Hang;
            while (true)
            {
                var page = (await Server.SendAsync(Owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>();
                if (page.Items.LastOrDefault(match) is { } record) return record;
                if (DateTime.UtcNow > deadline) Assert.Fail("The audit log never held the record.");
                await Task.Delay(50);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Viewer.DisposeAsync();
            await Target.DisposeAsync();
            await Server.DisposeAsync();
            Owner.Dispose();
            Mac.Dispose();
        }

        private ClientWebSocket Side(TestDevice? device, string ticket, out Uri address)
        {
            address = new Uri(Server.BaseAddress.ToString().Replace("http://", "ws://", StringComparison.Ordinal).TrimEnd('/') + FleetServerMethods.RelayPath + "/" + ticket);
            var socket = new ClientWebSocket();
            if (device is null) return socket;
            var time = Server.Clock.Now.ToUnixTimeMilliseconds();
            var nonce = FleetSignedRequest.CreateNonce();
            var signed = FleetSignedRequest.GetSignedBytes("GET", address.PathAndQuery, time, nonce, FleetSignedRequest.HashBody([]), device.DeviceId);
            socket.Options.SetRequestHeader(FleetSignedRequest.DeviceHeader, device.DeviceId);
            socket.Options.SetRequestHeader(FleetSignedRequest.TimestampHeader, time.ToString(System.Globalization.CultureInfo.InvariantCulture));
            socket.Options.SetRequestHeader(FleetSignedRequest.NonceHeader, nonce);
            socket.Options.SetRequestHeader(FleetSignedRequest.SignatureHeader, device.Sign(signed));
            return socket;
        }
    }
}
