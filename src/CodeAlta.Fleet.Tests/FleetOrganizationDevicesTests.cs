using System.Buffers.Text;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The signed device list as a device replays it, without trusting the server: the chain from a self-signed first
/// controller, entries signed by controller members only, removals, and the trust a node gives the members on a relayed
/// channel (the owner's controller devices as paired ones, any other member nothing, a removed device refused).
/// </summary>
[TestClass]
public sealed class FleetOrganizationDevicesTests
{
    private static readonly DateTimeOffset Time = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AValidChainGivesItsMembersTheirOwnersAndTheKeysItRemoved()
    {
        using var keys = new Keys();
        var chain = keys.Chain();
        var list = FleetVerifiedDeviceList.Verify(chain);

        CollectionAssert.AreEquivalent(new[] { keys.Desktop.DeviceId, keys.Node.DeviceId }, list.Members.Keys.ToArray());
        Assert.AreEqual(("org", 4L), (list.OrganizationId, list.Count));
        Assert.AreEqual("p1", list.Members[keys.Desktop.DeviceId].OwnerId, "The first device is the person of the entries it signs.");
        Assert.AreEqual((FleetDeviceKinds.Node, keys.Node.Fingerprint), (list.Members[keys.Node.DeviceId].Kind, list.Members[keys.Node.DeviceId].Fingerprint));
        Assert.IsTrue(list.Removed.Contains(keys.Other.Fingerprint));
        Assert.AreEqual(chain[0].Hash, list.RootHash, "The root is the first entry.");
    }

    [TestMethod]
    public void AnEntryThatDoesNotHoldBreaksTheList()
    {
        using var keys = new Keys();
        var chain = keys.Chain();

        // A payload changed after it was signed.
        var tampered = chain.ToArray();
        tampered[1] = tampered[1] with { Entry = tampered[1].Entry with { Payload = tampered[1].Entry.Payload.Replace("\"node\"", "\"controller\"", StringComparison.Ordinal) } };
        Assert.ThrowsExactly<InvalidDataException>(() => FleetVerifiedDeviceList.Verify(tampered));

        // A node signs nobody in.
        var byNode = chain.Take(2).Append(keys.Entry(keys.Node, 3, chain[1].Hash, FleetDeviceListOperations.Add, keys.Other, FleetDeviceKinds.Node)).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() => FleetVerifiedDeviceList.Verify(byNode));

        // An entry that does not follow the one before.
        var unchained = chain.Take(2).Append(keys.Entry(keys.Desktop, 3, chain[0].Hash, FleetDeviceListOperations.Add, keys.Other, FleetDeviceKinds.Node)).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() => FleetVerifiedDeviceList.Verify(unchained));

        Assert.ThrowsExactly<InvalidDataException>(() => FleetVerifiedDeviceList.Verify([]));
    }

    [TestMethod]
    public void ANodeTrustsItsOwnersControllersAsPairedOthersForNothingAndRefusesRemovedKeys()
    {
        using var keys = new Keys();
        var list = FleetVerifiedDeviceList.Verify(keys.Chain());
        var trust = new FleetOrganizationTrust(() => list, keys.Node.DeviceId);

        Assert.AreEqual(FleetPairingCoordinator.DefaultPermissions, trust.FindTrusted(keys.Desktop.Fingerprint)?.Permissions);
        Assert.IsNull(trust.FindTrusted(keys.Node.Fingerprint), "The node never trusts its own key through the list.");
        Assert.IsTrue(trust.IsRevoked(keys.Other.Fingerprint));
        Assert.IsNull(trust.FindTrusted(keys.Other.Fingerprint));

        // Another node of the organization is known, and may do nothing.
        var desktopView = new FleetOrganizationTrust(() => list, keys.Desktop.DeviceId);
        Assert.AreEqual(DevicePermissions.None, desktopView.FindTrusted(keys.Node.Fingerprint)?.Permissions);
        Assert.IsNull(new FleetOrganizationTrust(() => null, keys.Node.DeviceId).FindTrusted(keys.Desktop.Fingerprint), "Without a verified list, nobody.");
    }

    [TestMethod]
    public void APairedDeviceKeepsItsPermissionsAndARemovalOfTheOrganizationRefusesIt()
    {
        using var keys = new Keys();
        using var folder = new TestFolder();
        var paired = new TrustedDeviceStore(folder.Path, TimeProvider.System);
        paired.Add("Desktop", keys.Desktop.Fingerprint, DevicePermissions.View);
        paired.Add("Other", keys.Other.Fingerprint, DevicePermissions.View | DevicePermissions.Drive);
        var list = FleetVerifiedDeviceList.Verify(keys.Chain());
        var trust = new FleetCombinedTrust(paired, new FleetOrganizationTrust(() => list, keys.Node.DeviceId));

        Assert.AreEqual(DevicePermissions.View, trust.FindTrusted(keys.Desktop.Fingerprint)?.Permissions, "A paired device keeps what the node gave it.");
        Assert.IsTrue(trust.IsRevoked(keys.Other.Fingerprint), "The organization removed it: refused on the relayed path.");
        Assert.IsNull(trust.FindTrusted(keys.Other.Fingerprint));
    }

    /// <summary>The keys of a desktop (the first controller), a node and another device, and the entries they sign.</summary>
    private sealed class Keys : IDisposable
    {
        private readonly TestFolder _folder = new();

        public Keys()
        {
            Desktop = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "desktop"));
            Node = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "node"));
            Other = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "other"));
        }

        public DeviceIdentity Desktop { get; }

        public DeviceIdentity Node { get; }

        public DeviceIdentity Other { get; }

        // The desktop adds itself, then the node and another device for its person, then removes the other device.
        public IReadOnlyList<DeviceListEntryDto> Chain()
        {
            var first = Entry(Desktop, 1, null, FleetDeviceListOperations.Add, Desktop, FleetDeviceKinds.Controller, organization: null, owner: null);
            var node = Entry(Desktop, 2, first.Hash, FleetDeviceListOperations.Add, Node, FleetDeviceKinds.Node);
            var other = Entry(Desktop, 3, node.Hash, FleetDeviceListOperations.Add, Other, FleetDeviceKinds.Node);
            var removed = Entry(Desktop, 4, other.Hash, FleetDeviceListOperations.Remove, Other, null);
            return [first, node, other, removed];
        }

        public DeviceListEntryDto Entry(DeviceIdentity signer, long sequence, string? previous, string operation, DeviceIdentity device, string? kind,
            string? organization = "org", string? owner = "p1")
        {
            var add = operation == FleetDeviceListOperations.Add;
            var payload = new FleetDeviceListEntryPayload(1, organization, sequence, previous, operation, device.DeviceId,
                add ? Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()) : null, kind, add ? owner : null, signer.DeviceId, Time);
            var text = FleetDeviceListEntries.SerializePayload(payload);
            var signed = new FleetSignedDeviceListEntry(text, Base64Url.EncodeToString(signer.SignData(FleetDeviceListEntries.GetSignedBytes(text))));
            return new DeviceListEntryDto(sequence, FleetDeviceListEntries.Hash(signed), signed, Time);
        }

        public void Dispose()
        {
            Desktop.Dispose();
            Node.Dispose();
            Other.Dispose();
            _folder.Dispose();
        }
    }
}
