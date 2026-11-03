using System.Net;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Server.Tests;

/// <summary>
/// What an approval signs, the keys that seal once a device left the list, and the copies of keys a controller that left
/// the list gave before: against a real server of this process.
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

    [TestMethod]
    public async Task ARemovalTheListAccepted_StopsTheOldKeySealing_EvenWhenTheNextEpochCannotBeMade()
    {
        await using var fleet = await FleetFixture.StartAsync();
        using var pcKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "pc"));
        using (var pc = new FleetServerClient(fleet.Server.BaseAddress, pcKey)) await pc.EnrollAsync((await fleet.Desktop.CreateEnrollmentTokenAsync()).Token, "PC");
        await fleet.Desktop.ApproveAsync(pcKey.DeviceId, FleetDeviceKinds.Node);
        using var failing = new FailingKeysHandler { InnerHandler = new HttpClientHandler() };
        var devices = new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey, Path.Combine(fleet.Root.Path, "link", FleetOrganizationDevices.PinFileName), failing);
        var keys = new FleetMetadataKeyring(devices);
        var first = await keys.RefreshAsync("work", CancellationToken.None);
        Assert.IsNotNull(first);

        // The PC leaves the list; the next refresh accepts the removal, then the server fails the keys (or the next epoch).
        await fleet.Desktop.RevokeAsync(pcKey.DeviceId);
        failing.Fail = true;
        await Assert.ThrowsExactlyAsync<FleetServerException>(() => keys.RefreshAsync("work", CancellationToken.None));
        Assert.IsTrue(devices.Current!.LastRemovalSequence > first.Grant!.DeviceListSequence, "the removal was accepted");
        Assert.IsNull(keys.CurrentFor("work"), "the key the PC holds seals nothing more, whatever the server does next");
        Assert.IsTrue(keys.Held.Any(key => key.Id == first.Id), "it still opens what it sealed");

        // A list another caller of the same verifier accepted counts at once too.
        failing.Fail = false;
        var second = await keys.RefreshAsync("work", CancellationToken.None);
        Assert.AreNotEqual(first.Id, second?.Id);
        using var otherKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "other"));
        using (var other = new FleetServerClient(fleet.Server.BaseAddress, otherKey)) await other.EnrollAsync((await fleet.Desktop.CreateEnrollmentTokenAsync()).Token, "Other");
        await fleet.Desktop.ApproveAsync(otherKey.DeviceId, FleetDeviceKinds.Node);
        await keys.RefreshAsync("work", CancellationToken.None);
        await fleet.Desktop.RevokeAsync(otherKey.DeviceId);
        await devices.RefreshAsync(CancellationToken.None);
        Assert.IsNull(keys.CurrentFor("work"), "the removal is in the list the keyring wraps by: its keys stop sealing before it reads them again");
    }

    [TestMethod]
    public async Task TheCopiesAControllerGaveBeforeItLeft_StillOpenAfterARestart_AndSealNothingNew()
    {
        await using var fleet = await FleetFixture.StartAsync(approve: true);
        using var laptopKey = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "laptop"));
        using (var laptop = new FleetServerClient(fleet.Server.BaseAddress, laptopKey))
            await laptop.EnrollAsync((await fleet.Desktop.CreateEnrollmentTokenAsync()).Token, "Laptop", FleetDeviceKinds.Controller);
        await fleet.Desktop.ApproveAsync(laptopKey.DeviceId, FleetDeviceKinds.Controller);

        // The laptop makes the key of the space, wrapped to every device of the person; a title is sealed with it.
        var made = (await new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, laptopKey)).RefreshAsync("work", CancellationToken.None))!;
        var sealedTitle = made.Seal("s1", new FleetSessionMetadata("Fix the parser"));
        Assert.AreEqual(made.Id, (await new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey)).RefreshAsync("work", CancellationToken.None))?.Id);

        // The laptop leaves the list. The node starts again: the copy the laptop gave while it was a controller still opens.
        await fleet.Desktop.RevokeAsync(laptopKey.DeviceId);
        var restarted = new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey));
        Assert.IsNull(await restarted.RefreshAsync("work", CancellationToken.None), "the epoch the laptop made seals nothing more");
        Assert.IsTrue(restarted.TryOpen("s1", sealedTitle, out var opened), "what it sealed still opens");
        Assert.AreEqual("Fix the parser", opened!.Title);

        // The desktop makes the next epoch; the node seals with it, and still opens the older title.
        var next = (await new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey)).RefreshAsync("work", CancellationToken.None))!;
        Assert.AreNotEqual(made.Id, next.Id);
        Assert.AreEqual(next.Id, (await restarted.RefreshAsync("work", CancellationToken.None))?.Id);
        Assert.IsTrue(restarted.TryOpen("s1", sealedTitle, out _));
    }

    /// <summary>A server that fails the reads and writes of metadata keys while asked to, and answers everything else.</summary>
    private sealed class FailingKeysHandler : DelegatingHandler
    {
        private volatile bool _fail;

        public bool Fail
        {
            get => _fail;
            set => _fail = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Fail && request.RequestUri!.AbsolutePath.Contains("/api/v1/metadata-keys", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : base.SendAsync(request, cancellationToken);
    }
}
