using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Server.Tests;

/// <summary>
/// What an approval signs: against a real server of this process.
/// </summary>
public sealed partial class FleetConnectorTests
{
    [TestMethod]
    public async Task AMachineThatSpendsItsTokenOnAController_GetsNoControllerApprovedByTheWordsItReturns()
    {
        await using var fleet = await FleetFixture.StartAsync(approve: false);
        // A compromised node enrolls a controller key of its own with the token the desktop made for it, and answers its words.
        using var attackerKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "attacker"));
        using (var attacker = new FleetServerClient(fleet.Server.BaseAddress, attackerKey))
            await attacker.EnrollAsync((await fleet.Desktop.CreateEnrollmentTokenAsync()).Token, "build-box", FleetDeviceKinds.Controller);

        // The desktop approves without a person only as a node, and only the key it pinned for the machine: neither the
        // words, nor the key, nor the id the machine answers bring the controller in.
        await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => fleet.Desktop.ApproveAsync(attackerKey.DeviceId, FleetDeviceKinds.Node, attackerKey.Fingerprint.ToShortWords()));
        await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => fleet.Desktop.ApproveAsync(attackerKey.DeviceId, FleetDeviceKinds.Node, attackerKey.Fingerprint));
        await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => fleet.Desktop.ApproveAsync(attackerKey.DeviceId, FleetDeviceKinds.Node, fleet.NodeKey.Fingerprint));
        Assert.AreEqual("pending", (await fleet.Desktop.GetDeviceAsync(attackerKey.DeviceId)).Status, "nothing was signed");
        Assert.IsFalse((await fleet.Desktop.ReadVerifiedDeviceListAsync()).Members.ContainsKey(attackerKey.DeviceId));

        // A machine is never signed in as a controller either, and the class signed is the one approved.
        await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => fleet.Desktop.ApproveAsync(fleet.NodeKey.DeviceId, FleetDeviceKinds.Controller, fleet.NodeKey.Fingerprint));
        Assert.AreEqual("approved", (await fleet.Desktop.ApproveAsync(fleet.NodeKey.DeviceId, FleetDeviceKinds.Node, fleet.NodeKey.Fingerprint)).Status);
        Assert.AreEqual(FleetDeviceKinds.Node, (await fleet.Desktop.ReadVerifiedDeviceListAsync()).Members[fleet.NodeKey.DeviceId].Kind);
        Assert.ThrowsExactly<ArgumentException>(() => fleet.Desktop.ApproveAsync(attackerKey.DeviceId, "otter river robin saddle salmon pearl"), "a class is always named");
    }
}
