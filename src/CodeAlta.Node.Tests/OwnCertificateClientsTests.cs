using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Node.Tests;

/// <summary>
/// A server with a certificate of its own (<c>altaserver run --own-certificate</c>, <c>doc/fleet/security-and-privacy.md</c>,
/// channels) is reached with the pin of its link only: every read a machine or a desktop makes of it, beyond the device list,
/// carries that pin (the access document, the policy and its kill switch, the values, the budgets, the providers of a scope).
/// A client made without it fails on the certificate.
/// </summary>
[TestClass]
public sealed class OwnCertificateClientsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task EveryReadOfALinkCarriesThePinOfItsCertificate()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(root.Root, "server"), Urls = ["https://127.0.0.1:0"], OwnCertificate = true, ConfigureLogging = static _ => { },
            EnvironmentVariable = static _ => null,
        }, token);
        var pin = server.CertificatePin!;
        var address = server.Addresses.Single();
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "node"));
        using (var desktop = new FleetServerClient(address, desktopKey, null, null, pin))
        {
            await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
            var enrollment = await desktop.CreateEnrollmentTokenAsync(TimeSpan.FromMinutes(30), "Mac mini", token);
            using (var node = new FleetServerClient(address, nodeKey, null, null, pin)) await node.EnrollAsync(enrollment.Token, "Mac mini", token);
            await desktop.ApproveAsync(nodeKey.DeviceId, FleetDeviceKinds.Node, token);
        }

        // The desktop: the access document, the policy (whose kill switch it signs), its values and its budgets.
        var desktopDevices = new FleetOrganizationDevices(address, desktopKey, Path.Combine(root.Root, "desktop-link", FleetOrganizationDevices.PinFileName)) { CertificatePin = pin };
        var access = new FleetOrganizationAccess(desktopDevices);
        await access.RefreshAsync(token);
        Assert.AreEqual(0, (await new FleetOrganizationPolicy(access).RefreshAsync(token)).Number, "the policy is read through the pin");
        Assert.AreEqual(0, (await new FleetSecretsManager(desktopDevices).ListAsync(token)).Secrets.Count, "the values are read through the pin");
        Assert.AreEqual(0, (await new FleetBudgetsManager(desktopDevices).ListAsync(token)).Budgets.Count, "the budgets are read through the pin");

        // The machine: the providers of its scope, from the connector of its link.
        await using var connector = new FleetConnector(new FleetConnectorOptions
        {
            ServerAddress = address, Identity = nodeKey, OutboxPath = Path.Combine(root.Root, "outbox.json"), CertificatePin = pin,
            Client = new FleetClientInfo("node", "4.0.0", "linux"),
        });
        await connector.Access.RefreshAsync(token);
        await using var host = await NodeHost.StartAsync(root.Options(), token);
        var providers = new NodeScopeProviders(host.Host.CatalogOptions, new LocalSecretStore(Path.Combine(root.Root, "secrets")), nodeKey);
        Assert.AreEqual(0, (await providers.RefreshAsync("link-1", connector, token)).Count, "the providers of the scope are read through the pin");
    }
}
