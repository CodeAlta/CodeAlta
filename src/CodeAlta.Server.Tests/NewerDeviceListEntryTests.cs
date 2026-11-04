using System.Buffers.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// A device list a newer minor extended (<c>doc/fleet/protocol.md</c>, versions), live: a desktop reads it, counts the entry
/// it does not understand as skipped (which its card shows), trusts nothing of it, and signs the next entries after it.
/// </summary>
[TestClass]
public sealed class NewerDeviceListEntryTests
{
    [TestMethod]
    public async Task ADesktopCountsTheEntryOfANewerVersionAsSkipped_TrustsNothingOfIt_AndSignsAfterIt()
    {
        await using var server = await ServerFixture.StartAsync();
        using var root = new TempDirectory();
        using var ownerKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "owner"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        using var futureKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "future"));
        using var owner = new FleetServerClient(server.BaseAddress, ownerKey);
        var setup = await owner.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Acme", CancellationToken.None);
        var organization = setup.Organization.Id;
        Assert.AreEqual(0, (await owner.ReadVerifiedDeviceListAsync(cancellationToken: CancellationToken.None)).Skipped.Count);

        // What a newer server kept: the owner's desktop added a device of a class this version does not know.
        var list = await owner.GetDeviceListAsync(CancellationToken.None);
        var futureId = futureKey.DeviceId;
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["v"] = 1, ["org"] = organization, ["seq"] = 2, ["prev"] = list[^1].Hash, ["op"] = "add", ["device"] = futureId,
            ["key"] = Convert.ToBase64String(futureKey.ExportSubjectPublicKeyInfo()), ["kind"] = "signer-of-the-future", ["owner"] = setup.Owner.Id,
            ["signer"] = ownerKey.DeviceId, ["time"] = "2026-11-04T12:00:00+00:00", ["scope"] = "team-1",
        });
        var entry = new FleetSignedDeviceListEntry(payload, Base64Url.EncodeToString(ownerKey.SignData(FleetDeviceListEntries.GetSignedBytes(payload))));
        var now = server.Clock.Now;
        var device = new DeviceRecord(futureId, organization, "Future", "signer-of-the-future", Convert.ToBase64String(futureKey.ExportSubjectPublicKeyInfo()), "fp-future",
            setup.Owner.Id, DeviceStatuses.Approved, ownerKey.DeviceId, null, now, now, null, 1);
        var record = new DeviceListEntryRecord(organization, 2, futureId, "add", ownerKey.DeviceId, entry.Payload, entry.Signature, FleetDeviceListEntries.Hash(entry), now);
        var (outcome, _) = await server.Server.Store.AddDeviceWithEntryAsync(device, record, null, new AuditEntry("device.add", AuditEntry.Success, null, ownerKey.DeviceId, futureId,
            "127.0.0.1", "test"));
        Assert.AreEqual(DeviceListAppendOutcome.Appended, outcome);

        var verified = await owner.ReadVerifiedDeviceListAsync(cancellationToken: CancellationToken.None);
        Assert.AreEqual((2L, 1), (verified.Sequence, verified.Skipped.Count), "the list verifies, with one entry skipped");
        Assert.IsFalse(verified.Members.ContainsKey(futureId), "a device of a class this version does not know is never trusted");

        // The owner's desktop still signs the next entries, after the one it skipped.
        var enrollment = await owner.CreateEnrollmentTokenAsync(null, "build-box", CancellationToken.None);
        using (var node = new FleetServerClient(server.BaseAddress, nodeKey)) await node.EnrollAsync(enrollment.Token, "Build box", CancellationToken.None);
        await owner.ApproveAsync(nodeKey.DeviceId, FleetDeviceKinds.Node, CancellationToken.None);
        Assert.IsTrue((await owner.ReadVerifiedDeviceListAsync(cancellationToken: CancellationToken.None)).Members.ContainsKey(nodeKey.DeviceId));
    }
}
