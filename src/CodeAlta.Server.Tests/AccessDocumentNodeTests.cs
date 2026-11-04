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
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

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
