using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The signed access document from the owner's console to a node, live (<c>doc/fleet/access-control.md</c>, what the node
/// decides): a team machine trusts the team's members once the owner signed it; when a person is deprovisioned and the owner
/// signs again, the server's <c>access.changed</c> makes the node read the chain, and its trust refuses that person's device.
/// </summary>
[TestClass]
public sealed class AccessDocumentNodeTests
{
    private static readonly TimeSpan Patience = TestTime.Seconds(20);

    [TestMethod]
    public async Task ANodeFollowsTheSignedDocument_ATeamMachineTrustsItsMembers_AndADeprovisionedPersonIsRefused()
    {
        await using var server = await ServerFixture.StartAsync();
        using var root = new TempDirectory();
        using var ownerKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "owner"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        using var beaKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "bea"));
        using var owner = new FleetServerClient(server.BaseAddress, ownerKey);
        await owner.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Acme", CancellationToken.None);
        var enrollment = await owner.CreateEnrollmentTokenAsync(null, "build-box", CancellationToken.None);
        using (var node = new FleetServerClient(server.BaseAddress, nodeKey)) await node.EnrollAsync(enrollment.Token, "Build box", CancellationToken.None);
        await owner.ApproveAsync(nodeKey.DeviceId, FleetDeviceKinds.Node, CancellationToken.None);
        var invitation = await owner.InvitePersonAsync(new InvitePersonRequest("Bea"));
        using (var bea = new FleetServerClient(server.BaseAddress, beaKey)) await bea.EnrollAsync(invitation.Token, "Bea laptop", FleetDeviceKinds.Controller, CancellationToken.None);
        await owner.ApproveAsync(beaKey.DeviceId, FleetDeviceKinds.Controller, CancellationToken.None);
        var team = await owner.CreateTeamAsync("Infra");
        await owner.SetTeamMemberAsync(team.Id, invitation.Person.Id, FleetTeamRoles.Member);
        var transfer = await owner.OfferTransferAsync(new OfferTransferRequest(FleetResourceKinds.Machine, nodeKey.DeviceId, FleetPrincipalKinds.Team, team.Id));
        await owner.DecideTransferAsync(transfer.Id, "accept");

        var console = new FleetOrganizationAccess(new FleetOrganizationDevices(server.BaseAddress, ownerKey, Path.Combine(root.Path, "owner-link", FleetOrganizationDevices.PinFileName)),
            Path.Combine(root.Path, "owner-link", FleetOrganizationAccess.PinFileName));
        var draft = await console.PrepareAsync(CancellationToken.None);
        CollectionAssert.Contains(draft!.Changes.ToList(), "Build box becomes a machine of Infra", "the console says what it signs, as it reads it");
        await console.SignAsync(draft, CancellationToken.None);

        var told = 0;
        await using var connector = new FleetConnector(new FleetConnectorOptions
        {
            ServerAddress = server.BaseAddress, Identity = nodeKey, OutboxPath = Path.Combine(root.Path, "node-outbox.json"), Client = new FleetClientInfo("node", "4.0.0", "linux"),
            AccessPinPath = Path.Combine(root.Path, "node-link", FleetOrganizationAccess.PinFileName), MinimumBackoff = TimeSpan.FromMilliseconds(100),
            AccessChanged = _ => Interlocked.Increment(ref told),
        });
        connector.Start();
        await WaitForAsync(() => connector.Access.Current?.Number == 1, "the node reads the document when it connects");
        var member = connector.Trust.FindTrusted(DeviceFingerprint.Parse(beaKey.Fingerprint.ToString()));
        Assert.AreEqual((FleetOrganizationTrust.TeamMemberPermissions, true), (member?.Permissions, member?.OwnSessionsOnly), "the team machine trusts its member for her own sessions");
        Assert.AreEqual(FleetOrganizationTrust.TeamMaintainerPermissions, connector.Trust.FindTrusted(ownerKey.Fingerprint)?.Permissions,
            "the owner made the team, so he maintains it: he manages its machine as its maintainer, not as the person the device list names");

        // Bea leaves; her suspension reaches the node once the owner signs it.
        await owner.DeprovisionPersonAsync(invitation.Person.Id, new DeprovisionRequest());
        Assert.IsNotNull(connector.Trust.FindTrusted(beaKey.Fingerprint), "the server alone changes nothing on the node");
        await console.SignAsync((await console.PrepareAsync(CancellationToken.None))!, CancellationToken.None);
        await WaitForAsync(() => connector.Access.Current?.Number == 2, "access.changed makes the node read the chain again");
        Assert.IsNull(connector.Trust.FindTrusted(beaKey.Fingerprint));
        Assert.IsTrue(connector.Trust.IsRevoked(beaKey.Fingerprint), "her channels are closed");
        Assert.AreEqual(2, Volatile.Read(ref told), "the node was told of each new version");
    }

    [TestMethod]
    public async Task AReadAgainVerifiesOnlyTheVersionsAfterTheLastOneVerified_AndStillRefusesOneThatDoesNotFollow()
    {
        await using var server = await ServerFixture.StartAsync();
        using var root = new TempDirectory();
        using var ownerKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "owner"));
        using var owner = new FleetServerClient(server.BaseAddress, ownerKey);
        await owner.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Acme", CancellationToken.None);
        // The owner's first approval fixes his person in the device list, as a recovery key does when a server is set up.
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        var enrollment = await owner.CreateEnrollmentTokenAsync(null, "build-box", CancellationToken.None);
        using (var node = new FleetServerClient(server.BaseAddress, nodeKey)) await node.EnrollAsync(enrollment.Token, "Build box", CancellationToken.None);
        await owner.ApproveAsync(nodeKey.DeviceId, FleetDeviceKinds.Node, CancellationToken.None);
        using var recording = new RecordingHandler { InnerHandler = new HttpClientHandler() };
        var console = new FleetOrganizationAccess(new FleetOrganizationDevices(server.BaseAddress, ownerKey, Path.Combine(root.Path, "link", FleetOrganizationDevices.PinFileName),
            recording), Path.Combine(root.Path, "link", FleetOrganizationAccess.PinFileName));
        await console.SignAsync((await console.PrepareAsync(CancellationToken.None))!, CancellationToken.None);
        await owner.CreateTeamAsync("Infra");
        await console.SignAsync((await console.PrepareAsync(CancellationToken.None))!, CancellationToken.None);
        Assert.AreEqual(2, console.Current!.Number);
        recording.Reads.Clear();

        var held = console.Current;
        Assert.AreSame(held, await console.RefreshAsync(CancellationToken.None), "nothing new: the state verified before stays");
        CollectionAssert.AreEqual(new[] { "after=2" }, recording.Reads.ToArray(), "it asks only for what follows the version it verified");
        var web = await owner.CreateTeamAsync("Web");
        var third = await console.SignAsync((await console.PrepareAsync(CancellationToken.None))!, CancellationToken.None);
        Assert.AreEqual(3, third.Number);
        Assert.IsTrue(third.Teams.ContainsKey(web.Id));
        Assert.IsFalse(recording.Reads.Contains("after=0"), "never the whole chain again");

        // A server that answers a version which does not follow the one verified is refused, and the state stays.
        // The first version again, as if it came after the third: its number and its link do not follow.
        recording.Forged = new AccessDocumentListDto([(await owner.ListAccessDocumentsAsync(0)).Items[0]]);
        await Assert.ThrowsExactlyAsync<FleetAccessDocumentException>(() => console.RefreshAsync(CancellationToken.None));
        Assert.AreEqual(3, console.Current!.Number);
    }

    [TestMethod]
    public async Task AnAccessChangedThatArrivesWhileAReadRuns_IsReadAfterIt_NeverDropped()
    {
        await using var server = await ServerFixture.StartAsync();
        using var root = new TempDirectory();
        using var ownerKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "owner"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        using var owner = new FleetServerClient(server.BaseAddress, ownerKey);
        await owner.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Acme", CancellationToken.None);
        var enrollment = await owner.CreateEnrollmentTokenAsync(null, "build-box", CancellationToken.None);
        using (var node = new FleetServerClient(server.BaseAddress, nodeKey)) await node.EnrollAsync(enrollment.Token, "Build box", CancellationToken.None);
        await owner.ApproveAsync(nodeKey.DeviceId, FleetDeviceKinds.Node, CancellationToken.None);
        var console = new FleetOrganizationAccess(new FleetOrganizationDevices(server.BaseAddress, ownerKey, Path.Combine(root.Path, "owner-link", FleetOrganizationDevices.PinFileName)),
            Path.Combine(root.Path, "owner-link", FleetOrganizationAccess.PinFileName));
        await console.SignAsync((await console.PrepareAsync(CancellationToken.None))!, CancellationToken.None);

        // The reader of the connector tells what it read (a desktop follows its transfers there), with the version then held; once
        // armed, the next one waits there until released: the reader is still busy when the next access.changed arrives.
        var reads = new System.Collections.Concurrent.ConcurrentQueue<long>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var armed = 0;
        FleetConnector? self = null;
        await using var connector = new FleetConnector(new FleetConnectorOptions
        {
            ServerAddress = server.BaseAddress, Identity = nodeKey, OutboxPath = Path.Combine(root.Path, "node-outbox.json"), Client = new FleetClientInfo("node", "4.0.0", "linux"),
            AccessPinPath = Path.Combine(root.Path, "node-link", FleetOrganizationAccess.PinFileName), MinimumBackoff = TimeSpan.FromMilliseconds(100),
            AccessRead = () =>
            {
                reads.Enqueue(Volatile.Read(ref self)?.Access.Current?.Number ?? 0);
                if (Interlocked.Exchange(ref armed, 0) == 0) return;
                entered.Set();
                release.Wait(Patience);
            },
        });
        Volatile.Write(ref self, connector);
        connector.Start();
        await WaitForAsync(() => reads.LastOrDefault() == 1, "the node reads the document when it connects");

        Volatile.Write(ref armed, 1);
        await owner.CreateTeamAsync("Infra");
        await console.SignAsync((await console.PrepareAsync(CancellationToken.None))!, CancellationToken.None);
        Assert.IsTrue(entered.Wait(Patience), "access.changed makes the reader read");
        await owner.CreateTeamAsync("Web");
        Assert.AreEqual(3, (await console.SignAsync((await console.PrepareAsync(CancellationToken.None))!, CancellationToken.None)).Number);
        // The access.changed of version 3 reaches the node while the reader is still busy; give it time to arrive.
        await Task.Delay(500);
        Assert.AreNotEqual(3L, reads.Last(), "the reader was held before version 3 was signed");
        release.Set();

        var deadline = DateTime.UtcNow + Patience;
        while (reads.Last() != 3 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.AreEqual(3L, reads.Last(), $"reads {string.Join(",", reads)}, held {connector.Access.Current?.Number}");
    }

    // Records the reads of the access document, and answers a list of its own once given one.
    private sealed class RecordingHandler : DelegatingHandler
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Reads { get; } = new();

        public AccessDocumentListDto? Forged { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Method != HttpMethod.Get || !uri.AbsolutePath.EndsWith("/api/v1/access/documents", StringComparison.Ordinal))
                return await base.SendAsync(request, cancellationToken);
            Reads.Enqueue(uri.Query.TrimStart('?'));
            if (Forged is not { } forged) return await base.SendAsync(request, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(forged, FleetAccessDocumentJsonContext.Default.AccessDocumentListDto), System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out: {what}.");
            await Task.Delay(50);
        }
    }
}
