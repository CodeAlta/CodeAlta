using System.Net;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Server.Tests;

/// <summary>
/// A server on the local network with a certificate of its own (<c>altaserver run --own-certificate</c>,
/// <c>doc/fleet/security-and-privacy.md</c>, channels): the server makes it once and keeps it sealed in its data root; a
/// client given its pin trusts that key only, so that the network sees TLS without a reverse proxy or a certificate
/// authority, and a client without the pin, or with another one, does not connect.
/// </summary>
[TestClass]
public sealed class OwnCertificateTests
{
    [TestMethod]
    public async Task AServerWithItsOwnCertificateIsReachedWithItsPinOnly_AndKeepsItAcrossRestarts()
    {
        using var root = new TempDirectory();
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        var data = Path.Combine(root.Path, "server");
        string pin;
        await using (var server = await StartAsync(data))
        {
            pin = server.CertificatePin!;
            Assert.IsTrue(FleetServerCertificates.IsPin(pin), pin);
            Assert.AreEqual(pin, CoordinatorServer.ReadState(server.Paths)!.CertificatePin, "altaserver status prints it");
            var address = server.Addresses.Single();
            Assert.AreEqual("https", address.Scheme);

            // Without the pin the certificate does not validate; another pin refuses it.
            using (var unpinned = new FleetServerClient(address, desktopKey))
                await Assert.ThrowsExactlyAsync<HttpRequestException>(() => unpinned.GetHealthAsync());
            using (var wrong = new FleetServerClient(address, desktopKey, null, null, FleetServerCertificates.PinPrefix + new string('A', 43)))
                await Assert.ThrowsExactlyAsync<HttpRequestException>(() => wrong.GetHealthAsync());

            // With it, the REST API, the device list and the control connection all go through.
            using var desktop = new FleetServerClient(address, desktopKey, null, null, pin);
            await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home");
            var token = await desktop.CreateEnrollmentTokenAsync(TimeSpan.FromMinutes(30), "Mac mini");
            using (var node = new FleetServerClient(address, nodeKey, null, null, pin)) await node.EnrollAsync(token.Token, "Mac mini");
            await desktop.ApproveAsync(nodeKey.DeviceId, FleetDeviceKinds.Node);
            Assert.AreEqual(2, (await new FleetOrganizationDevices(address, nodeKey) { CertificatePin = pin }.RefreshAsync(CancellationToken.None)).Members.Count);

            await using var connector = new FleetConnector(new FleetConnectorOptions
            {
                ServerAddress = address, Identity = nodeKey, OutboxPath = Path.Combine(root.Path, "outbox.json"), CertificatePin = pin,
                Client = new FleetClientInfo("node", "4.0.0", "linux"), MinimumBackoff = TimeSpan.FromMilliseconds(100),
            });
            connector.Start();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (connector.State != FleetConnectorState.Online && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.AreEqual(FleetConnectorState.Online, connector.State, "the control connection checks the pin too");
        }

        // The key stays sealed in the data root, never in clear, and the server keeps it: the pin of its clients holds.
        Assert.IsFalse(File.ReadAllText(new ServerPaths(data).OwnCertificatePath).Contains("PRIVATE KEY", StringComparison.Ordinal));
        await using (var again = await StartAsync(data)) Assert.AreEqual(pin, again.CertificatePin);
    }

    [TestMethod]
    public async Task AnHttpsAddressWithoutACertificateIsStillRefused()
    {
        using var root = new TempDirectory();
        var refused = await Assert.ThrowsExactlyAsync<ArgumentException>(() => CoordinatorServer.StartAsync(Options(root.Path, ownCertificate: false)));
        StringAssert.Contains(refused.Message, "--own-certificate");
    }

    private static Task<CoordinatorServer> StartAsync(string data) => CoordinatorServer.StartAsync(Options(data));

    private static CoordinatorServerOptions Options(string data, bool ownCertificate = true) => new()
    {
        DataRoot = data, Urls = ["https://127.0.0.1:0"], OwnCertificate = ownCertificate, ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
    };
}
