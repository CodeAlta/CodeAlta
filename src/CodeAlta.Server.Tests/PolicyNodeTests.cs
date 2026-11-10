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
    private static readonly TimeSpan Patience = TestTime.Seconds(20);

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

    [TestMethod]
    public async Task AnAdminNoLongerASigner_SignsNoNewVersion_EvenAgainstTheAccessDocumentTheyStillSignedIn()
    {
        await using var server = await ServerFixture.StartAsync();
        using var root = new TempDirectory();
        using var ownerKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "owner"));
        using var beaKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "bea"));
        using var owner = new FleetServerClient(server.BaseAddress, ownerKey);
        await owner.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Acme", CancellationToken.None);
        var invitation = await owner.InvitePersonAsync(new Fleet.Protocol.Api.InvitePersonRequest("Bea"));
        using (var bea = new FleetServerClient(server.BaseAddress, beaKey)) await bea.EnrollAsync(invitation.Token, "Bea laptop", FleetDeviceKinds.Controller, CancellationToken.None);
        await owner.ApproveAsync(beaKey.DeviceId, FleetDeviceKinds.Controller, CancellationToken.None);
        await owner.SetPersonRoleAsync(invitation.Person.Id, FleetOrganizationRoles.Admin);
        var access = new FleetOrganizationAccess(new FleetOrganizationDevices(server.BaseAddress, ownerKey, Path.Combine(root.Path, "owner-link", FleetOrganizationDevices.PinFileName)),
            Path.Combine(root.Path, "owner-link", FleetOrganizationAccess.PinFileName));
        await access.SignAsync((await access.PrepareAsync(CancellationToken.None))!, CancellationToken.None);

        // Bea, an admin who signs, stops the fleet; the machine verifies it.
        var beaConsole = Console(server.BaseAddress, beaKey, Path.Combine(root.Path, "bea-link"));
        var stopped = await beaConsole.SignAsync(await beaConsole.PrepareAsync(FleetPolicyRules.None, new FleetPolicyStop(FleetPolicyStopScopes.Fleet, [], [], "Incident"), null,
            CancellationToken.None), CancellationToken.None);
        using var forging = new AppendingHandler { InnerHandler = new HttpClientHandler() };
        var machine = Console(server.BaseAddress, ownerKey, Path.Combine(root.Path, "machine-link"), forging);
        Assert.AreEqual("Incident", (await machine.RefreshAsync(CancellationToken.None)).Stop?.Reason);

        // She is no longer an admin, and the owner signs it. With the server, she then signs a version that lifts the stop,
        // against the access document she still signed in, after the version before it.
        await owner.SetPersonRoleAsync(invitation.Person.Id, FleetOrganizationRoles.Member);
        await access.SignAsync((await access.PrepareAsync(CancellationToken.None))!, CancellationToken.None);
        Assert.IsFalse(access.Current!.IsSigner(invitation.Person.Id));
        var lifted = new FleetPolicyDocument(FleetPolicyDocument.CurrentVersion, stopped.OrganizationId!, stopped.Number + 1, stopped.LastHash, stopped.DeviceListSequence,
            stopped.AccessNumber, FleetPolicyRules.None, null, beaKey.DeviceId, DateTimeOffset.UtcNow);
        var text = FleetPolicyDocuments.Serialize(lifted);
        var signed = new FleetSignedPolicyDocument(text, System.Buffers.Text.Base64Url.EncodeToString(beaKey.SignData(FleetPolicyDocuments.GetSignedBytes(text))));
        forging.Appended = new PolicyDocumentDto(lifted.Number, FleetPolicyDocuments.Hash(signed), signed, DateTimeOffset.UtcNow);

        // The machine that verified the stop keeps it, in this process and after a restart.
        var refused = await Assert.ThrowsExactlyAsync<FleetPolicyDocumentException>(() => machine.RefreshAsync(CancellationToken.None));
        StringAssert.Contains(refused.Message, "signs no more");
        Assert.AreEqual("Incident", machine.Current?.Stop?.Reason, "a version signed by someone who signs no more is not taken");
        var restarted = Console(server.BaseAddress, ownerKey, Path.Combine(root.Path, "machine-link"), forging);
        await Assert.ThrowsExactlyAsync<FleetPolicyDocumentException>(() => restarted.RefreshAsync(CancellationToken.None));
        Assert.AreEqual("Incident", restarted.Current?.Stop?.Reason);

        // The owner signs the next version: it holds, and the stop is lifted only by it.
        forging.Appended = null;
        var owners = Console(server.BaseAddress, ownerKey, Path.Combine(root.Path, "owner-link"));
        await owners.SignAsync(await owners.PrepareAsync(FleetPolicyRules.None, null, null, CancellationToken.None), CancellationToken.None);
        Assert.IsNull((await machine.RefreshAsync(CancellationToken.None)).Stop);
    }

    [TestMethod]
    public async Task AnAdminNoLongerASigner_SignsNoNewVersion_ForAMachineThatReadsThePolicyForTheFirstTime()
    {
        await using var server = await ServerFixture.StartAsync();
        using var root = new TempDirectory();
        using var ownerKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "owner"));
        using var beaKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "bea"));
        using var owner = new FleetServerClient(server.BaseAddress, ownerKey);
        await owner.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Acme", CancellationToken.None);
        var invitation = await owner.InvitePersonAsync(new Fleet.Protocol.Api.InvitePersonRequest("Bea"));
        using (var bea = new FleetServerClient(server.BaseAddress, beaKey)) await bea.EnrollAsync(invitation.Token, "Bea laptop", FleetDeviceKinds.Controller, CancellationToken.None);
        await owner.ApproveAsync(beaKey.DeviceId, FleetDeviceKinds.Controller, CancellationToken.None);
        await owner.SetPersonRoleAsync(invitation.Person.Id, FleetOrganizationRoles.Admin);
        var access = new FleetOrganizationAccess(new FleetOrganizationDevices(server.BaseAddress, ownerKey, Path.Combine(root.Path, "owner-link", FleetOrganizationDevices.PinFileName)),
            Path.Combine(root.Path, "owner-link", FleetOrganizationAccess.PinFileName));
        await access.SignAsync((await access.PrepareAsync(CancellationToken.None))!, CancellationToken.None);

        // The owner stops the fleet; then Bea is no longer an admin, and with the server she lifts the stop against the
        // authority she held before.
        var owners = Console(server.BaseAddress, ownerKey, Path.Combine(root.Path, "owner-link"));
        var stopped = await owners.SignAsync(await owners.PrepareAsync(FleetPolicyRules.None, new FleetPolicyStop(FleetPolicyStopScopes.Fleet, [], [], "Incident"), null,
            CancellationToken.None), CancellationToken.None);
        await owner.SetPersonRoleAsync(invitation.Person.Id, FleetOrganizationRoles.Member);
        await access.SignAsync((await access.PrepareAsync(CancellationToken.None))!, CancellationToken.None);
        var lifted = new FleetPolicyDocument(FleetPolicyDocument.CurrentVersion, stopped.OrganizationId!, stopped.Number + 1, stopped.LastHash, stopped.DeviceListSequence,
            stopped.AccessNumber, FleetPolicyRules.None, null, beaKey.DeviceId, DateTimeOffset.UtcNow);
        var text = FleetPolicyDocuments.Serialize(lifted);
        var signed = new FleetSignedPolicyDocument(text, System.Buffers.Text.Base64Url.EncodeToString(beaKey.SignData(FleetPolicyDocuments.GetSignedBytes(text))));
        using var forging = new AppendingHandler { InnerHandler = new HttpClientHandler() };
        forging.Appended = new PolicyDocumentDto(lifted.Number, FleetPolicyDocuments.Hash(signed), signed, DateTimeOffset.UtcNow);

        // A machine that reads the policy for the first time does not take her version: its work stays stopped, and nothing
        // is kept that a restart would trust.
        var machine = Console(server.BaseAddress, ownerKey, Path.Combine(root.Path, "machine-link"), forging);
        var refused = await Assert.ThrowsExactlyAsync<FleetPolicyDocumentException>(() => machine.RefreshAsync(CancellationToken.None));
        StringAssert.Contains(refused.Message, "signs no more");
        Assert.IsNotNull(machine.Current?.Stop, "the work of the fleet does not go on on a version its signer could no longer sign");
        Assert.IsTrue(machine.Current!.Stops(ownerKey.DeviceId, null));
        var restarted = Console(server.BaseAddress, ownerKey, Path.Combine(root.Path, "machine-link"), forging);
        await Assert.ThrowsExactlyAsync<FleetPolicyDocumentException>(() => restarted.RefreshAsync(CancellationToken.None));
        Assert.IsNotNull(restarted.Current?.Stop);
    }

    [TestMethod]
    public async Task AKeptPolicyThatDoesNotRead_StopsTheWorkOfTheFleet_AndIsNotReplacedByWhatTheServerSays()
    {
        using var root = new TempDirectory();
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        var path = Path.Combine(root.Path, "node-link", FleetOrganizationPolicy.PinFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"root\": \"abc\", \"number\": 3, \"rules\": ");

        // The rules and the stop it held, and the checkpoint of the chain, are lost: the work of the fleet stops, and says how to repair it.
        var policy = new FleetOrganizationPolicy(new FleetOrganizationAccess(new FleetOrganizationDevices(new Uri("http://127.0.0.1:9"), nodeKey)), path);
        var halt = new FleetPolicyGuard(nodeKey.DeviceId, () => [new FleetPolicySource("server", policy.Current, null, static _ => true)]).Halt;
        Assert.IsNotNull(halt, "an established policy that does not read is no reason to run without one");
        StringAssert.Contains(halt.Stop.Reason, path);

        // The chain of the server would be trusted without the lost checkpoint: it is not read until the file is repaired.
        var refused = await Assert.ThrowsExactlyAsync<FleetPolicyDocumentException>(() => policy.RefreshAsync(CancellationToken.None));
        StringAssert.Contains(refused.Message, path);
        Assert.AreEqual("{ \"root\": \"abc\", \"number\": 3, \"rules\": ", File.ReadAllText(path), "the file stays as it is, for its repair");
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

    // Answers the list of the policy with a version of its own after the ones the server holds, as a server that takes it would.
    private sealed class AppendingHandler : DelegatingHandler
    {
        public PolicyDocumentDto? Appended { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (Appended is not { } appended || request.Method != HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/api/v1/policies", StringComparison.Ordinal))
                return response;
            var list = await response.Content.ReadFromJsonAsync(FleetPolicyDocumentJsonContext.Default.PolicyDocumentListDto, cancellationToken);
            return new HttpResponseMessage(response.StatusCode)
            {
                Content = JsonContent.Create(new PolicyDocumentListDto([.. list!.Items, appended]), FleetPolicyDocumentJsonContext.Default.PolicyDocumentListDto),
            };
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
