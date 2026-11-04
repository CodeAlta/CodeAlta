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

    [TestMethod]
    public async Task APersonWhoJoinsTheTeamOfAGrantAfterItsKeys_GetsACopyOnceTheOwnersComputerKeysItAgain()
    {
        await using var fleet = await FleetFixture.StartAsync();
        using var beaKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "bea"));
        using var cyKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "cy"));
        var desktopKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey));
        var nodeKeys = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey));
        Assert.IsNotNull(await desktopKeys.RefreshAsync("work", CancellationToken.None));
        var source = new FakeSource();
        source.Set(Session("s1", "work") with { Title = "Fix the parser" });
        await using var connector = fleet.NodeConnector(source, static _ => true, keys: nodeKeys);
        connector.Start();
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(session => session.Id == "s1" && session.SealedMetadata is not null));

        async Task<string> JoinAsync(DeviceIdentity key, string name)
        {
            var invitation = await fleet.Desktop.InvitePersonAsync(new InvitePersonRequest(name));
            using (var client = new FleetServerClient(fleet.Server.BaseAddress, key)) await client.EnrollAsync(invitation.Token, name + " laptop", FleetDeviceKinds.Controller, CancellationToken.None);
            await fleet.Desktop.ApproveAsync(key.DeviceId, FleetDeviceKinds.Controller, CancellationToken.None);
            return invitation.Person.Id;
        }

        var beaId = await JoinAsync(beaKey, "Bea");
        var team = await fleet.Desktop.CreateTeamAsync("Infra");
        await fleet.Desktop.SetTeamMemberAsync(team.Id, beaId, FleetTeamRoles.Member);
        var grant = await fleet.Desktop.CreateGrantAsync(new CreateGrantRequest(FleetPrincipalKinds.Team, team.Id, "session.view-content", FleetScopeKinds.Resource, "session:s1",
            Sealed: true));
        await FleetGrantKeys.ConfirmAsync(fleet.Desktop, desktopKeys, grant.Id, CancellationToken.None);
        Assert.IsFalse((await fleet.Desktop.ListGrantsAsync()).Items.Single().MissingKeys);

        // Cy joins the team after the keys were given: the grant says it misses a copy, and the owner's computer keys it again.
        var cyId = await JoinAsync(cyKey, "Cy");
        await fleet.Desktop.SetTeamMemberAsync(team.Id, cyId, FleetTeamRoles.Member);
        var cyKeys = new FleetGrantKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, cyKey));
        Assert.AreEqual(0, await cyKeys.RefreshAsync(CancellationToken.None));
        Assert.IsTrue((await fleet.Desktop.ListGrantsAsync()).Items.Single().MissingKeys);
        Assert.AreEqual(FleetGrantStatuses.Active, (await FleetGrantKeys.ConfirmAsync(fleet.Desktop, desktopKeys, grant.Id, CancellationToken.None)).Status);
        Assert.IsFalse((await fleet.Desktop.ListGrantsAsync()).Items.Single().MissingKeys);
        Assert.AreEqual(1, await cyKeys.RefreshAsync(CancellationToken.None));
        Assert.AreEqual("Fix the parser", cyKeys.Find("s1")?.Title);
        var beaKeys = new FleetGrantKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, beaKey));
        Assert.AreEqual(1, await beaKeys.RefreshAsync(CancellationToken.None), "the copies of the others are given again with it");
    }
}
