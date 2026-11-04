using System.Net.Http.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The signed policy from the owner's console to a node, live (<c>doc/fleet/security-and-privacy.md</c>, policies and the kill
/// switch): the console says what it signs; the server's <c>policy.changed</c> makes the node read the chain; the node keeps
/// what it verified and obeys a stop while its server is gone, after a restart too; a version the server forged is refused and
/// changes nothing.
/// </summary>
[TestClass]
public sealed class PolicyNodeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task TheOwnerStopsTheFleet_TheNodeObeysOffline_AndAForgedVersionChangesNothing()
    {
        using var root = new TempDirectory();
        using var ownerKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "owner"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        var nodePolicy = Path.Combine(root.Path, "node-link", FleetOrganizationPolicy.PinFileName);
        var server = await ServerFixture.StartAsync();
        try
        {
            using var owner = new FleetServerClient(server.BaseAddress, ownerKey);
            await owner.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Acme", CancellationToken.None);
            var enrollment = await owner.CreateEnrollmentTokenAsync(null, "build-box", CancellationToken.None);
            using (var node = new FleetServerClient(server.BaseAddress, nodeKey)) await node.EnrollAsync(enrollment.Token, "Build box", CancellationToken.None);
            await owner.ApproveAsync(nodeKey.DeviceId, FleetDeviceKinds.Node, CancellationToken.None);

            var console = Console(server.BaseAddress, ownerKey, Path.Combine(root.Path, "owner-link"));
            var draft = await console.PrepareAsync(new FleetPolicyRules(Sandbox: "workspace"), new FleetPolicyStop(FleetPolicyStopScopes.Fleet, [], [], "A leaked key"),
                null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "The work of the fleet runs in the sandbox 'workspace' at least", "STOP: the work of the fleet stops on every machine (A leaked key)" },
                draft.Changes.ToArray(), "the console says what it signs, as it reads it");
            await console.SignAsync(draft, CancellationToken.None);

            var told = 0;
            var connector = new FleetConnector(new FleetConnectorOptions
            {
                ServerAddress = server.BaseAddress, Identity = nodeKey, OutboxPath = Path.Combine(root.Path, "node-outbox.json"), Client = new FleetClientInfo("node", "4.0.0", "linux"),
                AccessPinPath = Path.Combine(root.Path, "node-link", FleetOrganizationAccess.PinFileName), PolicyPath = nodePolicy, MinimumBackoff = TimeSpan.FromMilliseconds(100),
                PolicyChanged = _ => Interlocked.Increment(ref told),
            });
            await using (connector)
            {
                connector.Start();
                await WaitForAsync(() => connector.Policy.Current?.Number == 1, "the node reads the policy when it connects");
                var guard = new FleetPolicyGuard(nodeKey.DeviceId, () => [connector]);
                Assert.AreEqual("A leaked key", guard.Halt?.Stop.Reason, "every machine stops");

                // Lifted: policy.changed makes the node read the chain again.
                await console.SignAsync(await console.PrepareAsync(new FleetPolicyRules(Sandbox: "workspace"), null, null, CancellationToken.None), CancellationToken.None);
                await WaitForAsync(() => connector.Policy.Current?.Number == 2, "policy.changed makes the node read the chain again");
                Assert.IsNull(guard.Halt);

                // Stopped again, then the server goes away.
                await console.SignAsync(await console.PrepareAsync(FleetPolicyRules.None, new FleetPolicyStop(FleetPolicyStopScopes.Machines, [nodeKey.DeviceId], [], "Runaway job"), null,
                    CancellationToken.None), CancellationToken.None);
                await WaitForAsync(() => connector.Policy.Current?.Number == 3, "the stop of this machine");
                Assert.AreEqual("Runaway job", guard.Halt?.Stop.Reason);
                Assert.AreEqual(3, Volatile.Read(ref told), "the node was told of each new version");
            }

            // A forged version: the server takes the stop out of the third version. A console that reads the whole chain again
            // (after a restart) refuses it and keeps the stop it verified.
            using var forging = new ForgingHandler { InnerHandler = new HttpClientHandler() };
            Assert.AreEqual(3, (await Console(server.BaseAddress, ownerKey, Path.Combine(root.Path, "forged-link")).RefreshAsync(CancellationToken.None)).Number);
            forging.Forge = true;
            var restarted = Console(server.BaseAddress, ownerKey, Path.Combine(root.Path, "forged-link"), forging);
            Assert.AreEqual("Runaway job", restarted.Current?.Stop?.Reason, "what it kept applies before the server answers");
            await Assert.ThrowsExactlyAsync<FleetPolicyDocumentException>(() => restarted.RefreshAsync(CancellationToken.None));
            Assert.AreEqual("Runaway job", restarted.Current?.Stop?.Reason, "a version that does not verify changes nothing");
        }
        finally
        {
            await server.DisposeAsync();
        }

        // The server is gone; the node starts again from what it kept, and its work stays stopped.
        var offlineAccess = new FleetOrganizationAccess(new FleetOrganizationDevices(new Uri("http://127.0.0.1:9"), nodeKey));
        var kept = new FleetOrganizationPolicy(offlineAccess, nodePolicy);
        Assert.AreEqual((3L, "Runaway job"), (kept.Current?.Number, kept.Current?.Stop?.Reason));
        Assert.IsTrue(kept.Current!.Stops(nodeKey.DeviceId, null));
    }

    private static FleetOrganizationPolicy Console(Uri server, DeviceIdentity identity, string folder, HttpMessageHandler? handler = null)
    {
        var devices = new FleetOrganizationDevices(server, identity, Path.Combine(folder, FleetOrganizationDevices.PinFileName), handler);
        return new FleetOrganizationPolicy(new FleetOrganizationAccess(devices, Path.Combine(folder, FleetOrganizationAccess.PinFileName)),
            Path.Combine(folder, FleetOrganizationPolicy.PinFileName));
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (!condition())
        {
            if (timeout.IsCancellationRequested) Assert.Fail($"Timed out waiting: {what}.");
            await Task.Delay(50);
        }
    }

    // Answers the list of the policy with its third version changed: the stop taken out, its signature kept.
    private sealed class ForgingHandler : DelegatingHandler
    {
        public bool Forge { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!Forge || request.Method != HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/api/v1/policies", StringComparison.Ordinal)) return response;
            var list = await response.Content.ReadFromJsonAsync(FleetPolicyDocumentJsonContext.Default.PolicyDocumentListDto, cancellationToken);
            var items = list!.Items.Select(item => item.Number != 3 ? item : item with
            {
                Document = item.Document with { Payload = System.Text.RegularExpressions.Regex.Replace(item.Document.Payload, ",\"stop\":\\{[^}]*\\}", string.Empty) },
            }).ToArray();
            return new HttpResponseMessage(response.StatusCode) { Content = JsonContent.Create(new PolicyDocumentListDto(items), FleetPolicyDocumentJsonContext.Default.PolicyDocumentListDto) };
        }
    }
}
