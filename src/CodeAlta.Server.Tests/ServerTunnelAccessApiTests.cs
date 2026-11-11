using System.Net;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;
using Microsoft.AspNetCore.Http;

namespace CodeAlta.Server.Tests;

[TestClass]
public sealed class ServerTunnelAccessApiTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OnlyAnApprovedSignedDevice_CanRenewTransportAccess()
    {
        using var folder = new TempDirectory();
        var publicAddress = new Uri("https://personal-2586.euw.devtunnels.ms");
        var reads = 0;
        var fresh = new FleetTunnelAccessDto(publicAddress.ToString(), "new.connect.token", DateTimeOffset.UtcNow.AddHours(24));
        await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(folder.Path, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
            TunnelAccess = _ => { Interlocked.Increment(ref reads); return Task.FromResult<FleetTunnelAccessDto?>(fresh); },
        }, TestContext.CancellationToken);
        var address = server.Addresses.Single();
        using var owner = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "owner"));
        using var localOwner = new FleetServerClient(address, owner);
        await localOwner.SetUpAsync(server.SetupCode!.Code, "Owner", "Desktop", null, TestContext.CancellationToken);
        using var anonymous = new HttpClient();
        using var refused = await anonymous.GetAsync(new Uri(address, "api/v1/server/tunnel-access"), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.AreEqual(0, reads);

        using var node = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "node"));
        using var localNode = new FleetServerClient(address, node);
        var invite = await localOwner.CreateEnrollmentTokenAsync(cancellationToken: TestContext.CancellationToken);
        await localNode.EnrollAsync(invite.Token, "Node", TestContext.CancellationToken);
        new FleetTunnelAccessStore(node).Save(publicAddress, new(publicAddress.ToString(), "old.connect.token", DateTimeOffset.UtcNow.AddHours(1)));
        // Only the test handler rewrites the destination; every socket of the fixture stays on loopback.
        using var route = new LoopbackTunnel(address);
        using var remoteNode = new FleetServerClient(publicAddress, node, route);
        await Assert.ThrowsExactlyAsync<FleetServerException>(() => remoteNode.RenewTunnelAccessAsync(TestContext.CancellationToken));
        Assert.AreEqual(0, reads, "a pending device with transport access still has no approved-device rights");
        await localOwner.ApproveAsync(node.DeviceId, FleetDeviceKinds.Node, node.Fingerprint, TestContext.CancellationToken);
        await remoteNode.RenewTunnelAccessAsync(TestContext.CancellationToken);
        Assert.AreEqual(1, reads);
        Assert.AreEqual(fresh, new FleetTunnelAccessStore(node).Read(publicAddress));
        Assert.AreEqual("no-store", route.CacheControl);
    }

    [TestMethod]
    public void AForwardedHost_IsBelievedOnlyFromATrustedProxy_AndCannotContainAnotherRequestTarget()
    {
        var proxies = new TrustedProxies(["127.0.0.1", "::1"]);
        var raw = new HostString("localhost", 2586);
        var headers = new HeaderDictionary { ["X-Forwarded-Host"] = "personal-2586.euw.devtunnels.ms", ["X-Forwarded-Proto"] = "https", ["X-Forwarded-For"] = "10.1.2.3" };
        Assert.AreEqual("personal-2586.euw.devtunnels.ms", proxies.HostOf(IPAddress.Loopback, headers, raw).Value);
        Assert.AreEqual(raw, proxies.HostOf(IPAddress.Parse("192.0.2.4"), headers, raw));
        Assert.AreEqual(IPAddress.Parse("10.1.2.3"), proxies.ClientOf(IPAddress.Loopback, headers),
            "Dev Tunnels forwards its internal address, not a real caller IP; do not claim otherwise");
        foreach (var malformed in new[] { "user@host.example", "host.example/path", "host.example,another.example", "host.example?query", "host.example#fragment", "host.example\\path", "host.example:99999" })
        {
            headers["X-Forwarded-Host"] = malformed;
            Assert.AreEqual(raw, proxies.HostOf(IPAddress.Loopback, headers, raw), malformed);
        }
    }

    private sealed class LoopbackTunnel(Uri destination) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        public string? CacheControl { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("tunnel old.connect.token", request.Headers.GetValues(FleetTunnelAccessStore.AuthorizationHeader).Single());
            request.RequestUri = new Uri(destination, request.RequestUri!.PathAndQuery);
            var response = await base.SendAsync(request, cancellationToken);
            CacheControl = response.Headers.CacheControl?.ToString();
            return response;
        }
    }
}
