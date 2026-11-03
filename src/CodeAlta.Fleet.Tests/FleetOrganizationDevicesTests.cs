using System.Buffers.Text;
using System.Net;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The signed device list as a node reads it for a relayed channel, replayed with <see cref="FleetDeviceListVerifier"/>:
/// its members by key, the devices it removed, its first entry (the pin), and the trust a node gives the members (the
/// owner's controller devices as paired ones, any other member nothing, a removed device refused).
/// </summary>
[TestClass]
public sealed class FleetOrganizationDevicesTests
{
    private static readonly DateTimeOffset Time = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AVerifiedListFindsItsMembersByKeyAndKnowsTheKeysItRemoved()
    {
        using var keys = new Keys();
        var chain = keys.Chain();
        var list = Verify(chain);

        CollectionAssert.AreEquivalent(new[] { keys.Desktop.DeviceId, keys.Node.DeviceId }, list.Members.Keys.ToArray());
        Assert.AreEqual(("org", 4L), (list.OrganizationId, list.Sequence));
        Assert.AreEqual("p1", list.Members[keys.Desktop.DeviceId].OwnerId, "The first device is the person of the entries it signs.");
        Assert.AreEqual(keys.Node.Fingerprint, list.Members[keys.Node.DeviceId].Fingerprint());
        Assert.AreEqual(keys.Node.DeviceId, list.FindByFingerprint(keys.Node.Fingerprint)?.DeviceId);
        Assert.IsNull(list.FindByFingerprint(keys.Other.Fingerprint), "A removed device is no member.");
        Assert.IsTrue(list.IsRemoved(keys.Other.Fingerprint));
        Assert.IsFalse(list.IsRemoved(keys.Node.Fingerprint));
        Assert.AreEqual(chain[0].Hash, list.FirstHash, "The pin is the hash of the first entry.");
    }

    [TestMethod]
    public async Task TheFirstEntryIsPinnedPerLinkAndAListThatStartsWithAnotherDeviceIsRefused()
    {
        using var keys = new Keys();
        using var folder = new TestFolder();
        var pin = Path.Combine(folder.Path, "link", FleetOrganizationDevices.PinFileName);
        var chain = keys.Chain();
        var server = new DeviceListServer(chain);
        var devices = new FleetOrganizationDevices(new Uri("http://server.test/"), keys.Node, pin, server);

        var first = await devices.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(pin), "The first read pins the first entry in the folder of the link.");
        Assert.AreEqual(chain[0].Hash, first.FirstHash);

        // The same server now gives a list whose first controller is another device: a swapped organization.
        var swappedFirst = keys.Entry(keys.Other, 1, null, FleetDeviceListOperations.Add, keys.Other, FleetDeviceKinds.Controller, organization: null, owner: null);
        server.Chain = [swappedFirst, keys.Entry(keys.Other, 2, swappedFirst.Hash, FleetDeviceListOperations.Add, keys.Node, FleetDeviceKinds.Node)];
        await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => devices.RefreshAsync(CancellationToken.None));
        Assert.AreSame(first, devices.Current, "The list last verified is kept.");

        // The pin outlives the instance: a restart refuses the swapped list too.
        var restarted = new FleetOrganizationDevices(new Uri("http://server.test/"), keys.Node, pin, server);
        await Assert.ThrowsExactlyAsync<FleetDeviceListException>(() => restarted.RefreshAsync(CancellationToken.None));
        Assert.IsNull(restarted.Current);
    }

    [TestMethod]
    public void ANodeTrustsItsOwnersControllersAsPairedOthersForNothingAndRefusesRemovedKeys()
    {
        using var keys = new Keys();
        var list = Verify(keys.Chain());
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
        var list = Verify(keys.Chain());
        var trust = new FleetCombinedTrust(paired, new FleetOrganizationTrust(() => list, keys.Node.DeviceId));

        Assert.AreEqual(DevicePermissions.View, trust.FindTrusted(keys.Desktop.Fingerprint)?.Permissions, "A paired device keeps what the node gave it.");
        Assert.IsTrue(trust.IsRevoked(keys.Other.Fingerprint), "The organization removed it: refused on the relayed path.");
        Assert.IsNull(trust.FindTrusted(keys.Other.Fingerprint));
    }

    private static FleetDeviceListState Verify(IReadOnlyList<DeviceListEntryDto> chain) => FleetDeviceListVerifier.VerifyOrThrow(chain.Select(static item => item.Entry));

    /// <summary>A server that answers the organization and the device list it is given, for the reads of a device.</summary>
    private sealed class DeviceListServer(IReadOnlyList<DeviceListEntryDto> chain) : HttpMessageHandler
    {
        public IReadOnlyList<DeviceListEntryDto> Chain { get; set; } = chain;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organization" => JsonSerializer.SerializeToUtf8Bytes(new OrganizationDto("org", "Org", Time, []), ServerApiJsonContext.Default.OrganizationDto),
                "/api/v1/device-list" => JsonSerializer.SerializeToUtf8Bytes(new ListResponse<DeviceListEntryDto>(Chain), ServerApiJsonContext.Default.ListResponseDeviceListEntryDto),
                var path => throw new InvalidOperationException($"Unexpected call to {path}."),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
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
