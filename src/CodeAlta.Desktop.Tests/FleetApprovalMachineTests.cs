using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The machine of a run that waits for an approval, as the person confirms it (<c>doc/fleet/secrets.md</c>, environments protect
/// themselves): named with the identity of its key in the device list this computer verified, beside the name the server gives,
/// which the server alone chooses.
/// </summary>
[TestClass]
public sealed class FleetApprovalMachineTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheMachineOfAnApproval_IsShownByItsVerifiedKey_WhateverNameTheServerGivesIt()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-approval-machine-").FullName;
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
            using var names = new DeviceNameHandler { InnerHandler = new HttpClientHandler() };
            await using var servers = new DesktopFleetServers(host, "Desktop", "4.0.0") { Handler = names };
            var epoch = Guid.NewGuid().ToString("D");
            var service = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog };
            var created = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;
            Assert.AreEqual("ok", (await service.CreateRecoveryKeyAsync(new FleetRecoveryKeyRequest(epoch, linkId), token)).Status);

            // Two machines of the person: the build box and the Mac mini.
            using var desktop = new FleetServerClient(address, DeviceIdentity.LoadOrCreate(host.CatalogOptions.FleetRoot));
            using var buildBox = DeviceIdentity.LoadOrCreate(At("build-box"));
            using var macMini = DeviceIdentity.LoadOrCreate(At("mac-mini"));
            foreach (var (key, name) in new[] { (buildBox, "Build box"), (macMini, "Mac mini") })
            {
                using var machine = new FleetServerClient(address, key);
                await machine.EnrollAsync((await desktop.CreateEnrollmentTokenAsync()).Token, name);
                await desktop.ApproveAsync(key.DeviceId, FleetDeviceKinds.Node);
            }

            var owner = (await service.SecretsAsync(new FleetServerLinkRequest(epoch, linkId), token)).People.Single().Id;
            Assert.AreEqual("ok", (await service.SetEnvironmentAsync(new FleetEnvironmentSetRequest(epoch, linkId, "production", [owner], [], [], false), token)).Status);
            var environment = (await service.SecretsAsync(new FleetServerLinkRequest(epoch, linkId), token)).Environments.Single();

            // The Mac mini asks the approval of a run of production.
            var list = await new FleetOrganizationDevices(address, macMini).RefreshAsync(token);
            using (var asking = new FleetServerClient(address, macMini))
            {
                await asking.RequestApprovalAsync(environment.Id, new RequestFleetApprovalRequest("run-1",
                    FleetApprovalRequests.Nonce(macMini, list.OrganizationId!, environment.Id, 1, "run-1", null), 1, null, null), token);
            }

            // The server names the Mac mini "Build box", and the build box "Mac mini".
            names.Names[macMini.DeviceId] = "Build box";
            names.Names[buildBox.DeviceId] = "Mac mini";
            var approval = (await service.SecretsAsync(new FleetServerLinkRequest(epoch, linkId), token)).Approvals.Single();
            StringAssert.Contains(approval.Machine, macMini.Fingerprint.ToShortWords(), "the person confirms the machine by the key the device list holds for it");
            Assert.IsFalse(approval.Machine.Contains(buildBox.Fingerprint.ToShortWords(), StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Answers the devices of the organization under the names a server chooses.
    private sealed class DeviceNameHandler : DelegatingHandler
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (Names.IsEmpty || request.Method != HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/api/v1/devices", StringComparison.Ordinal)) return response;
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            Rename(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body!.ToJsonString(), Encoding.UTF8, "application/json") };
        }

        private void Rename(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject value:
                    if (value["id"]?.GetValue<string>() is { } id && Names.TryGetValue(id, out var name) && value.ContainsKey("name")) value["name"] = name;
                    foreach (var (_, child) in value.ToArray()) Rename(child);
                    break;
                case JsonArray items:
                    foreach (var child in items.ToArray()) Rename(child);
                    break;
            }
        }
    }
}
