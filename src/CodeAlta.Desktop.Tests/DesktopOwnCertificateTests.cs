using System.Collections.Frozen;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A server on the local network with a certificate of its own (<c>altaserver run --own-certificate</c>): the desktop that
/// sets it up from its address with <c>#pin=</c> keeps the pin in the link, every connection of the link checks it, and
/// the address it gives machines and people carries it.
/// </summary>
[TestClass]
public sealed class DesktopOwnCertificateTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ADesktopSetsUpAServerWithItsOwnCertificate_KeepsThePin_AndGoesOnlineThroughIt()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-own-certificate-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["https://127.0.0.1:0"], OwnCertificate = true, ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
            }, token);
            var given = FleetServerAddress.Format(server.Addresses.Single(), server.CertificatePin);
            Assert.IsTrue(FleetServerAddress.TryParse(given, out var address, out var pin), given);
            Assert.AreEqual(server.CertificatePin, pin);
            Assert.IsFalse(FleetServerAddress.TryParse(given.Replace("https://", "http://", StringComparison.Ordinal), out _, out _), "a pin is for https:// only");
            Assert.IsFalse(FleetServerAddress.TryParse(given[..^1], out _, out _), "a pin that is not one is refused, not dropped");

            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = At("global"), CurrentProjectPath = At("project"), DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            await using var servers = new DesktopFleetServers(host, "Desktop", "4.0.0");

            // Without the pin, the certificate of the server does not validate: nothing is set up.
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => servers.CreateServerAsync(address, server.SetupCode!.Code, "Alex", "Home", token));
            var link = await servers.CreateServerAsync(address, server.SetupCode!.Code, "Alex", "Home", pin, token);
            Assert.AreEqual(pin, new FleetServerLinkStore(host.CatalogOptions.FleetRoot).Find(link.Url)!.CertificatePin, "the link keeps it");
            Assert.AreEqual(given, link.ShareableUrl);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (servers.Connections.SingleOrDefault()?.Connector?.State != FleetConnectorState.Online && DateTime.UtcNow < deadline) await Task.Delay(50, token);
            Assert.AreEqual(FleetConnectorState.Online, servers.Connections.Single().Connector!.State, "the control connection checks the pin");
            Assert.AreEqual(given, FleetServerService.View(servers.Connections.Single(), []).ShareUrl, "altanode join and the invitations carry the pin");
            Assert.IsNotNull(await servers.CreateRecoveryKeyAsync(link.Id, token), "the calls of the link, its device list included, go through the pin");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
