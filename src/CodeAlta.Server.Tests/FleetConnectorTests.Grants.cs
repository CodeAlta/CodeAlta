using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Server.Tests;

public sealed partial class FleetConnectorTests
{
    [TestMethod]
    public async Task ASealedGrantGetsItsKeysFromTheOwnersDesktop_AndTheGranteeOpensTheTitleUntilItIsTakenBack()
    {
        await using var fleet = await FleetFixture.StartAsync();
        using var beaKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "bea"));
        var desktopKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey));
        var nodeKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey));
        Assert.IsNotNull(await desktopKeys.RefreshAsync("work", CancellationToken.None));
        var source = new FakeSource();
        source.Set(Session("s1", "work") with { Title = "Fix the parser" });
        await using var connector = fleet.NodeConnector(source, static _ => true, keys: nodeKeys);
        connector.Start();
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s1" && session.SealedMetadata is not null));

        var invitation = await fleet.Desktop.InvitePersonAsync(new InvitePersonRequest("Bea"));
        using var bea = new FleetServerClient(fleet.Server.BaseAddress, beaKey);
        await bea.EnrollAsync(invitation.Token, "Bea laptop", FleetDeviceKinds.Controller, CancellationToken.None);
        await fleet.Desktop.ApproveAsync(beaKey.DeviceId, FleetDeviceKinds.Controller, CancellationToken.None);
        var grant = await fleet.Desktop.CreateGrantAsync(new CreateGrantRequest(FleetPrincipalKinds.Person, invitation.Person.Id, "session.view-content",
            FleetScopeKinds.Resource, "session:s1", Sealed: true));
        Assert.AreEqual(FleetGrantStatuses.PendingKeys, grant.Status);
        var beaKeys = new FleetGrantKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, beaKey));
        Assert.AreEqual(0, await beaKeys.RefreshAsync(CancellationToken.None), "nothing before the owner gives the keys");

        // Bea cannot give the keys to herself: the session is not even listed to her before. The owner's desktop opens the title and seals it for her.
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => FleetGrantKeys.ConfirmAsync(bea, new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, beaKey)),
            grant.Id, CancellationToken.None));
        Assert.AreEqual(FleetGrantStatuses.Active, (await FleetGrantKeys.ConfirmAsync(fleet.Desktop, desktopKeys, grant.Id, CancellationToken.None)).Status);
        Assert.AreEqual(1, await beaKeys.RefreshAsync(CancellationToken.None));
        Assert.AreEqual("Fix the parser", beaKeys.Find("s1")?.Title);
        Assert.AreEqual("s1", (await bea.ListSessionsAsync()).Single().Id, "the session is listed to her now");

        await fleet.Desktop.RevokeGrantAsync(grant.Id);
        Assert.AreEqual(0, await beaKeys.RefreshAsync(CancellationToken.None), "a grant taken back opens nothing more");
        Assert.IsNull(beaKeys.Find("s1"));
    }
}
