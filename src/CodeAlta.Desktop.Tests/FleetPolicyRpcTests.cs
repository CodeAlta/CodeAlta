using System.Collections.Frozen;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The policy and the kill switch from the owner's computer (<c>doc/fleet/security-and-privacy.md</c>, policies and the kill
/// switch): the rules and a stop are signed only for the changes the person read, the stop shows in the state of the link for
/// the banner of the window, and lifting it is the next version.
/// </summary>
[TestClass]
public sealed class FleetPolicyRpcTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheOwnersComputerSignsRulesAndAStop_TheWindowSeesTheStop_AndLiftsIt()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-policy-rpc-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = At("global"), CurrentProjectPath = At("project"), DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            await using var servers = new DesktopFleetServers(host, "Desktop", "4.0.0");
            var epoch = Guid.NewGuid().ToString("D");
            var service = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog };
            var created = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;
            // The owner's first approval fixes his person in the device list, as a recovery key does.
            Assert.AreEqual("ok", (await service.CreateRecoveryKeyAsync(new FleetRecoveryKeyRequest(epoch, linkId), token)).Status);

            var home = await service.PolicyAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual(("ok", true, 0, null), (home.Status, home.CanSign, home.Number, home.Stop), home.Message);

            var rules = new FleetPolicyRulesView(["anthropic"], null, null, null, null, null, false, 120, null, null, null);
            var preview = await service.PreparePolicyAsync(new FleetPolicyChangeRequest(epoch, linkId, rules, null), token);
            Assert.AreEqual("ok", preview.Status, preview.Message);
            CollectionAssert.AreEqual(new[] { "Only these kinds of providers: anthropic", "Agents start no work on other machines", "A session of the fleet stops after 120 minutes" },
                preview.Changes.ToArray());
            Assert.AreEqual("changed", (await service.SignPolicyAsync(new FleetPolicySignRequest(epoch, linkId, rules, null, ["Only these kinds of providers: anthropic"]), token)).Status,
                "nothing is signed but what was read");
            Assert.AreEqual("ok", (await service.SignPolicyAsync(new FleetPolicySignRequest(epoch, linkId, rules, null, preview.Changes), token)).Status);

            var stop = new FleetPolicyStopView("fleet", [], [], "A leaked key", []);
            var stopping = await service.PreparePolicyAsync(new FleetPolicyChangeRequest(epoch, linkId, rules, stop), token);
            CollectionAssert.AreEqual(new[] { "STOP: the work of the fleet stops on every machine (A leaked key)" }, stopping.Changes.ToArray());
            Assert.AreEqual("ok", (await service.SignPolicyAsync(new FleetPolicySignRequest(epoch, linkId, rules, stop, stopping.Changes), token)).Status);
            var stopped = await service.PolicyAsync(new FleetServerLinkRequest(epoch, linkId), token);
            Assert.AreEqual((2, "fleet", "A leaked key"), (stopped.Number, stopped.Stop?.Scope, stopped.Stop?.Reason));
            Assert.AreEqual(2, stopped.Versions.Count);
            Assert.IsTrue(stopped.Versions[0].Stops, "the newest version comes first");
            var banner = (await service.StatusAsync(new FleetServerRequest(epoch), token)).Servers.Single().Stop;
            Assert.AreEqual(("fleet", "A leaked key"), (banner?.Scope, banner?.Reason), "the state of the link carries the stop for the banner");
            Assert.IsNotNull(servers.Policy.Halt, "a stop of every machine reaches this computer too");

            var lifting = await service.PreparePolicyAsync(new FleetPolicyChangeRequest(epoch, linkId, rules, null), token);
            Assert.AreEqual("ok", (await service.SignPolicyAsync(new FleetPolicySignRequest(epoch, linkId, rules, null, lifting.Changes), token)).Status);
            Assert.IsNull((await service.PolicyAsync(new FleetServerLinkRequest(epoch, linkId), token)).Stop);
            Assert.IsNull((await service.StatusAsync(new FleetServerRequest(epoch), token)).Servers.Single().Stop);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
