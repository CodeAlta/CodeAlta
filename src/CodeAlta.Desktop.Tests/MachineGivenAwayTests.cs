using System.Buffers.Text;
using System.Collections.Immutable;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A personal machine given to another person (<c>doc/fleet/access-control.md</c>, ownership over time): once the signed access
/// document names its new owner, what its former owner gave it ends: the devices paired with it, but the new owner's own, and the
/// people it named. It happens once per change of owner, never for what the new owner pairs afterwards.
/// </summary>
[TestClass]
public sealed class MachineGivenAwayTests
{
    private static readonly DateTimeOffset Time = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheFormerOwnersPairingsAndPeopleEnd_OnceTheSignedDocumentNamesTheNewOwner()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        using var mia = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Root, "mia"));
        using var later = DeviceIdentity.LoadOrCreate(Path.Combine(fixture.Root, "later"));
        // Alex's desktop (the viewer) set the server up and added his node; Mia's desktop joined.
        var devices = DeviceList(fixture.ViewerIdentity, fixture.NodeIdentity, mia);
        await fixture.PairViewerAsync(token);
        await PairAsync(fixture, mia, "mia", token);
        fixture.Service.People.Set(new FleetNodePeopleChange("max", ["view", "start"]));
        var trusted = new TrustedDeviceStore(Path.Combine(fixture.Root, "node-fleet"), TimeProvider.System);

        var access = FleetAccessState.Empty with { OrganizationId = "org", Number = 1, Owners = ["alex"] };
        Assert.AreEqual(0, fixture.Service.ReconcileOwner(access, devices).Count, "the machine is still the one of the person the device list names");
        Assert.AreEqual(2, trusted.List().Count);

        var given = access with { Number = 2, PersonalMachines = ImmutableDictionary<string, string>.Empty.Add(fixture.NodeIdentity.DeviceId, "mia") };
        await fixture.Service.ApplyAccessAsync(given, devices, token);
        trusted = new TrustedDeviceStore(Path.Combine(fixture.Root, "node-fleet"), TimeProvider.System);
        CollectionAssert.AreEqual(new[] { mia.DeviceId }, trusted.List().Select(static device => device.DeviceId).ToArray(),
            "Alex's pairing ends; Mia's own controller device keeps hers");
        Assert.IsTrue(trusted.IsRevoked(fixture.ViewerIdentity.Fingerprint));
        Assert.AreEqual(0, fixture.Service.People.Read().People.Count, "the people Alex named are taken out");

        // Mia pairs another device and names someone: the next versions leave them, as long as the owner stays Mia.
        await PairAsync(fixture, later, "later", token);
        fixture.Service.People.Set(new FleetNodePeopleChange("max", ["view"]));
        Assert.AreEqual(0, fixture.Service.ReconcileOwner(given with { Number = 3 }, devices).Count);
        Assert.AreEqual(1, fixture.Service.People.Read().People.Count);

        // Given back to Alex: Mia's pairings end in turn, but Alex's own controller device would stay.
        CollectionAssert.AreEquivalent(new[] { mia.DeviceId, later.DeviceId }, fixture.Service.ReconcileOwner(access with { Number = 4 }, devices).ToArray());
    }

    private static async Task PairAsync(FleetNodeFixture fixture, DeviceIdentity identity, string name, CancellationToken token)
    {
        var (offer, _) = fixture.Service.Pairing.CreateOffer("test-node", fixture.Addresses, TimeSpan.FromMinutes(1));
        await FleetNodeClient.PairAsync(offer, identity, name, new PairedMachineStore(Path.Combine(fixture.Root, name + "-fleet")), token);
    }

    // A device list: Alex's first device, his node, then Mia's first device, all signed by Alex's.
    private static FleetDeviceListState DeviceList(DeviceIdentity founder, DeviceIdentity node, DeviceIdentity mia)
    {
        var entries = new List<FleetSignedDeviceListEntry>();
        void Add(string? organization, DeviceIdentity device, string kind, string? owner)
        {
            var payload = new FleetDeviceListEntryPayload(1, organization, entries.Count + 1, entries.Count == 0 ? null : FleetDeviceListEntries.Hash(entries[^1]),
                FleetDeviceListOperations.Add, device.DeviceId, Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()), kind, owner, founder.DeviceId, Time);
            var text = FleetDeviceListEntries.SerializePayload(payload);
            entries.Add(new FleetSignedDeviceListEntry(text, Base64Url.EncodeToString(founder.SignData(FleetDeviceListEntries.GetSignedBytes(text)))));
        }

        Add(null, founder, FleetDeviceKinds.Controller, null);
        Add("org", node, FleetDeviceKinds.Node, "alex");
        Add("org", mia, FleetDeviceKinds.Controller, "mia");
        return FleetDeviceListVerifier.VerifyOrThrow(entries, "org");
    }
}
