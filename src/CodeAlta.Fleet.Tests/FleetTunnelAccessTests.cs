using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

[TestClass]
public sealed class FleetTunnelAccessTests
{
    private static readonly Uri Address = new("https://personal-2586.euw.devtunnels.ms");
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Credentials_AreOriginBound_ProtectedAtRest_AndNotApplicationAuthorization()
    {
        using var folder = new TestFolder();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new FleetTunnelAccessStore(folder.Path, clock);
        var access = new FleetTunnelAccessDto(Address.ToString(), "private.synthetic.connect-token", clock.Now.AddHours(24));
        store.Save(Address, access);
        Assert.AreEqual(access, store.Read(Address));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Address, "/api/v1/organization"));
        request.Headers.Authorization = new("Bearer", "application");
        store.Authorize(Address, request);
        Assert.AreEqual("tunnel " + access.Token, request.Headers.GetValues(FleetTunnelAccessStore.AuthorizationHeader).Single());
        Assert.AreEqual("application", request.Headers.Authorization.Parameter);
        using var elsewhere = new HttpRequestMessage(HttpMethod.Get, "https://elsewhere.example");
        store.Authorize(Address, elsewhere);
        Assert.IsFalse(elsewhere.Headers.Contains(FleetTunnelAccessStore.AuthorizationHeader));
        Assert.ThrowsExactly<ArgumentException>(() => store.Save(new Uri("https://another-2586.euw.devtunnels.ms"), access));
        Assert.IsFalse(FleetTunnelAccessStore.IsTunnel(new Uri("https://devtunnels.ms.attacker.example")));
        Assert.IsFalse(FleetTunnelAccessStore.IsTunnel(new Uri("http://personal-2586.euw.devtunnels.ms")));
        Assert.IsFalse(access.ToString().Contains(access.Token, StringComparison.Ordinal));
        foreach (var file in Directory.EnumerateFiles(folder.Path, "*.json", SearchOption.AllDirectories))
            Assert.IsFalse(File.ReadAllText(file).Contains(access.Token, StringComparison.Ordinal), file);
        clock.Now = access.ExpiresAt;
        Assert.ThrowsExactly<HttpRequestException>(() => store.Authorize(Address, new HttpRequestMessage(HttpMethod.Get, Address)));
    }

    [TestMethod]
    public void APrivateInvitation_CarriesBothGates_ButCannotAuthorizeAnotherOrigin()
    {
        using var folder = new TestFolder();
        var store = new FleetTunnelAccessStore(folder.Path, TimeProvider.System);
        var access = new FleetTunnelAccessDto(Address.ToString(), "connect-only.synthetic.token", DateTimeOffset.UtcNow.AddHours(24));
        var invitation = FleetTunnelAccessStore.Invitation("ONE-TIME-ENROLL", access);
        Assert.IsTrue(FleetTunnelAccessStore.IsEnrollmentCode(invitation));
        Assert.IsTrue(FleetTunnelAccessStore.IsInvitation(invitation));
        Assert.AreEqual("ONE-TIME-ENROLL", store.ImportInvitation(Address, invitation));
        Assert.AreEqual(access, store.Read(Address));
        Assert.ThrowsExactly<ArgumentException>(() => store.ImportInvitation(new Uri("https://other-2586.euw.devtunnels.ms"), invitation));
        Assert.ThrowsExactly<ArgumentException>(() => store.ImportInvitation(Address, "ca-tunnel:not-valid-json"));
        Assert.AreEqual("plain-code", store.ImportInvitation(Address, "plain-code"));
    }

    [TestMethod]
    public async Task AnApprovedDevice_RenewsBeforeExpiry_AndInvitesAHeadlessDevice_WithoutItsAccountLogin()
    {
        using var folder = new TestFolder();
        using var identity = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "controller"));
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new FleetTunnelAccessStore(Path.GetDirectoryName(identity.KeyPath)!, clock);
        var held = new FleetTunnelAccessDto(Address.ToString(), "old.connect.token", clock.Now.AddHours(1));
        store.Save(Address, held);
        var renewal = new FleetTunnelAccessDto(Address.ToString(), "renewed.connect.token", clock.Now.AddHours(24));
        var renewals = 0;
        using var handler = new Handler(async request =>
        {
            Assert.IsTrue(request.Headers.Contains(FleetSignedRequest.SignatureHeader), "the tunnel gate never replaces the device signature");
            if (request.RequestUri!.AbsolutePath.EndsWith("/tunnel-access", StringComparison.Ordinal))
            {
                Assert.AreEqual("tunnel " + (renewals == 0 ? held.Token : renewal.Token), request.Headers.GetValues(FleetTunnelAccessStore.AuthorizationHeader).Single(), "renewal uses the still-valid credential");
                renewals++;
                return Json(renewal, ServerApiJsonContext.Default.FleetTunnelAccessDto);
            }
            Assert.AreEqual("tunnel renewed.connect.token", request.Headers.GetValues(FleetTunnelAccessStore.AuthorizationHeader).Single());
            await Task.CompletedTask;
            return Json(new EnrollmentTokenResponse("invite", "ONE-TIME", clock.Now.AddHours(1)), ServerApiJsonContext.Default.EnrollmentTokenResponse);
        });
        using var client = new FleetServerClient(Address, identity, handler, clock);
        await client.RenewTunnelAccessAsync(TestContext.CancellationToken);
        await client.RenewTunnelAccessAsync(TestContext.CancellationToken);
        Assert.AreEqual(1, renewals, "a current credential is not minted again on every request");
        Assert.AreEqual(renewal, store.Read(Address));
        var invitation = await client.CreateEnrollmentTokenAsync(cancellationToken: TestContext.CancellationToken);
        Assert.IsTrue(FleetTunnelAccessStore.IsInvitation(invitation.Token));

        // The invitation path also works without a browser or a GitHub login on the new device.
        using var node = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "node"));
        using var nodeHandler = new Handler(async request =>
        {
            Assert.AreEqual("tunnel renewed.connect.token", request.Headers.GetValues(FleetTunnelAccessStore.AuthorizationHeader).Single());
            var body = JsonSerializer.Deserialize(await request.Content!.ReadAsByteArrayAsync(TestContext.CancellationToken), ServerApiJsonContext.Default.EnrollRequest)!;
            Assert.AreEqual("ONE-TIME", body.Token, "only the fleet enrollment code reaches the application");
            return Json(new EnrollResponse(new DeviceDto(node.DeviceId, "Node", FleetDeviceKinds.Node, "", "", "pending", null, null, clock.Now, null, null, false)),
                ServerApiJsonContext.Default.EnrollResponse);
        });
        using var nodeClient = new FleetServerClient(Address, node, nodeHandler, clock);
        var device = await nodeClient.EnrollAsync(invitation.Token, "Node", TestContext.CancellationToken);
        Assert.AreEqual("pending", device.Status, "transport access does not approve the device");
    }

    [TestMethod]
    public async Task StreamedAuditExports_UseTheSameOriginBoundCredential()
    {
        using var folder = new TestFolder();
        using var identity = DeviceIdentity.LoadOrCreate(folder.Path);
        new FleetTunnelAccessStore(identity).Save(Address, new(Address.ToString(), "audit.connect.token", DateTimeOffset.UtcNow.AddHours(24)));
        using var handler = new Handler(request =>
        {
            Assert.AreEqual("tunnel audit.connect.token", request.Headers.GetValues(FleetTunnelAccessStore.AuthorizationHeader).Single());
            Assert.IsTrue(request.Headers.Contains(FleetSignedRequest.SignatureHeader));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("record\n") });
        });
        using var client = new FleetServerClient(Address, identity, handler);
        using var destination = new MemoryStream();
        await client.ExportAuditAsync(new FleetAuditQuery(), FleetAuditExportFormats.JsonLines, destination, TestContext.CancellationToken);
        Assert.AreEqual("record\n", Encoding.UTF8.GetString(destination.ToArray()));
    }

    [TestMethod]
    public async Task AUserManagedAddressWithoutAHeldCredential_KeepsOrdinaryEnrollmentCodes()
    {
        using var folder = new TestFolder();
        using var identity = DeviceIdentity.LoadOrCreate(folder.Path);
        using var handler = new Handler(request =>
        {
            Assert.AreEqual("/api/v1/enrollment-tokens", request.RequestUri!.AbsolutePath, "do not require a managed-tunnel API from an existing user-managed proxy");
            return Task.FromResult(Json(new EnrollmentTokenResponse("id", "ordinary-code", DateTimeOffset.UtcNow.AddHours(1)), ServerApiJsonContext.Default.EnrollmentTokenResponse));
        });
        using var client = new FleetServerClient(Address, identity, handler);
        Assert.AreEqual("ordinary-code", (await client.CreateEnrollmentTokenAsync(cancellationToken: TestContext.CancellationToken)).Token);
    }

    [TestMethod]
    public async Task WebSocketHandshakes_SendTheConnectHeader_KeepDeviceProof_AndRefuseRedirects()
    {
        using var folder = new TestFolder();
        var calls = 0;
        var redirect = false;
        using var handler = new Handler(request =>
        {
            calls++;
            Assert.AreEqual("tunnel websocket.connect.token", request.Headers.GetValues(FleetTunnelAccessStore.AuthorizationHeader).Single());
            Assert.AreEqual("device-proof", request.Headers.GetValues(FleetSignedRequest.SignatureHeader).Single());
            if (redirect) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://elsewhere.example") } });
            var key = request.Headers.GetValues("Sec-WebSocket-Key").Single();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols) { Content = new DuplexContent() };
            response.Headers.Add("Connection", "Upgrade");
            response.Headers.Add("Upgrade", "websocket");
            response.Headers.Add("Sec-WebSocket-Accept", accept);
            return Task.FromResult(response);
        });
        var store = new FleetTunnelAccessStore(folder.Path, TimeProvider.System)
        {
            HandshakeInvoker = () => new HttpMessageInvoker(handler, disposeHandler: false),
        };
        store.Save(Address, new(Address.ToString(), "websocket.connect.token", DateTimeOffset.UtcNow.AddHours(24)));
        var socketAddress = new UriBuilder(Address) { Scheme = "wss", Path = "/fleet/v1" }.Uri;
        using var socket = await store.ConnectAsync(Address, socketAddress, null,
            options => options.SetRequestHeader(FleetSignedRequest.SignatureHeader, "device-proof"), TestContext.CancellationToken).WithinAsync(TestTime.Seconds(10));
        Assert.AreEqual(WebSocketState.Open, socket.State);
        redirect = true;
        await Assert.ThrowsExactlyAsync<WebSocketException>(() => store.ConnectAsync(Address, socketAddress, null,
            options => options.SetRequestHeader(FleetSignedRequest.SignatureHeader, "device-proof"), TestContext.CancellationToken));
        Assert.AreEqual(2, calls, "a redirect is a failed handshake, not a forwarded credential");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.ConnectAsync(Address, new Uri("wss://other.devtunnels.ms/fleet/v1"),
            null, null, TestContext.CancellationToken));
        Assert.AreEqual(2, calls);
    }

    private static HttpResponseMessage Json<T>(T value, JsonTypeInfo<T> type) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, type), Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    // StreamContent returns a read-only wrapper; an upgraded WebSocket needs a genuinely duplex stream.
    private sealed class DuplexContent : HttpContent
    {
        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => new MemoryStream();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream());
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
