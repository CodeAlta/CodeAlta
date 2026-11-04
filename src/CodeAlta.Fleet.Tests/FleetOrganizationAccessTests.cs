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
/// What a node decides from the signed access document (<c>doc/fleet/access-control.md</c>, what the node decides): the chain
/// verified against the device list it anchored and pinned per link; on a team machine the team's members use it for their own
/// sessions and its maintainers manage it, whoever the device list names as its owner; a suspended person's devices are refused
/// and the automations they signed wait; a server that answers a forged or shortened chain changes nothing.
/// </summary>
[TestClass]
public sealed class FleetOrganizationAccessTests
{
    private static readonly DateTimeOffset Time = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task OnATeamMachine_TheTeamUsesIt_ItsMaintainersManageIt_AndASuspendedPersonIsRefused()
    {
        using var world = new World();
        var server = world.Server();
        var devices = new FleetOrganizationDevices(new Uri("http://server.test/"), world.Node, null, server);
        var access = new FleetOrganizationAccess(devices);
        var trust = new FleetOrganizationTrust(() => devices.Current, world.Node.DeviceId, () => access.Current);

        await devices.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(FleetPairingCoordinator.DefaultPermissions, trust.FindTrusted(world.Desktop.Fingerprint)?.Permissions,
            "without a document, the machine is its owner's personal machine");
        Assert.AreEqual(0, (await access.RefreshAsync(CancellationToken.None)).Number, "an organization that signed nothing has no team machine");

        server.Documents.Add(world.Sign(world.Desktop, 1, null, owners: ["p1"], teams: [new FleetAccessTeam("t1", ["p2"], ["p3"], false)],
            machines: [new FleetAccessMachine(world.Node.DeviceId, "t1")]));
        var changed = 0;
        access.Changed += _ => changed++;
        await access.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(1, changed);
        var mia = trust.FindTrusted(world.Mia.Fingerprint);
        Assert.AreEqual((FleetOrganizationTrust.TeamMaintainerPermissions, "p2", true), (mia?.Permissions, mia?.PersonId, mia?.OwnSessionsOnly));
        var max = trust.FindTrusted(world.Max.Fingerprint);
        Assert.AreEqual((FleetOrganizationTrust.TeamMemberPermissions, "p3", true), (max?.Permissions, max?.PersonId, max?.OwnSessionsOnly));
        Assert.AreEqual(DevicePermissions.None, trust.FindTrusted(world.Desktop.Fingerprint)?.Permissions,
            "the person the device list names gets nothing from it once the machine is the team's");

        server.Documents.Add(world.Sign(world.Desktop, 2, server.Documents[^1], owners: ["p1"], teams: [new FleetAccessTeam("t1", ["p2"], [], false)],
            machines: [new FleetAccessMachine(world.Node.DeviceId, "t1")], suspended: ["p3"]));
        await access.RefreshAsync(CancellationToken.None);
        Assert.IsNull(trust.FindTrusted(world.Max.Fingerprint), "a suspended person's devices are refused");
        Assert.IsTrue(trust.IsRevoked(world.Max.Fingerprint), "and their channels closed");
    }

    [TestMethod]
    public async Task AForgedOrShortenedChain_ChangesNothing_AndThePinOutlivesARestart()
    {
        using var world = new World();
        using var folder = new TestFolder();
        var pin = Path.Combine(folder.Path, "link", FleetOrganizationAccess.PinFileName);
        var server = world.Server();
        var devices = new FleetOrganizationDevices(new Uri("http://server.test/"), world.Node, null, server);
        var access = new FleetOrganizationAccess(devices, pin);
        server.Documents.Add(world.Sign(world.Desktop, 1, null, owners: ["p1"], signers: ["p2"]));
        server.Documents.Add(world.Sign(world.Mia, 2, server.Documents[^1], owners: ["p1"], signers: ["p2"], suspended: ["p3"]));
        var verified = await access.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(verified.IsSuspended("p3"));

        // The server takes the suspension back by answering the chain without it.
        server.Documents.RemoveAt(1);
        await Assert.ThrowsExactlyAsync<FleetAccessDocumentException>(() => access.RefreshAsync(CancellationToken.None));
        Assert.AreSame(verified, access.Current, "the version verified before still decides");
        var restarted = new FleetOrganizationAccess(new FleetOrganizationDevices(new Uri("http://server.test/"), world.Node, null, server), pin);
        await Assert.ThrowsExactlyAsync<FleetAccessDocumentException>(() => restarted.RefreshAsync(CancellationToken.None), "the pin outlives the instance");

        // A member signs a version that puts them in a team: not a signer, refused.
        server.Documents.Add(world.Sign(world.Max, 2, server.Documents[^1], owners: ["p1"], signers: ["p2"], teams: [new FleetAccessTeam("t1", ["p3"], [], false)],
            machines: [new FleetAccessMachine(world.Node.DeviceId, "t1")]));
        var refused = await Assert.ThrowsExactlyAsync<FleetAccessDocumentException>(() => access.RefreshAsync(CancellationToken.None));
        StringAssert.Contains(refused.Message, "not an owner or an admin who signs");
    }

    [TestMethod]
    public void ATeamMachineRunsTheAutomationsOfItsMaintainers_ThoseOfItsMembersWhenAllowed_AndNoneOfASuspendedPerson()
    {
        using var world = new World();
        var devices = FleetDeviceListVerifier.VerifyOrThrow(world.Chain().Select(static entry => entry.Entry), "org");
        var team = new FleetAccessTeam("t1", ["p2"], ["p3"], false);
        FleetAccessState Access(FleetAccessTeam value, params string[] suspended) => FleetAccessDocuments.Verify([world.Sign(world.Desktop, 1, null, owners: ["p1"], teams: [value],
            machines: [new FleetAccessMachine(world.Node.DeviceId, "t1")], suspended: suspended).Document], devices, "org").State;

        Assert.IsNull(FleetAutomationVersionDocuments.CheckAccess("p1", world.Node.DeviceId, devices, null), "a personal machine runs its owner's automations");
        Assert.IsNotNull(FleetAutomationVersionDocuments.CheckAccess("p2", world.Node.DeviceId, devices, null), "and nobody else's");
        Assert.IsNull(FleetAutomationVersionDocuments.CheckAccess("p2", world.Node.DeviceId, devices, Access(team)));
        Assert.IsNotNull(FleetAutomationVersionDocuments.CheckAccess("p3", world.Node.DeviceId, devices, Access(team)), "a member's, not unless the team allows it");
        Assert.IsNull(FleetAutomationVersionDocuments.CheckAccess("p3", world.Node.DeviceId, devices, Access(team with { RunsMemberAutomations = true })));
        Assert.IsNotNull(FleetAutomationVersionDocuments.CheckAccess("p1", world.Node.DeviceId, devices, Access(team)), "the device list's owner is not the team");
        Assert.AreEqual("the version is signed by a device of a person the organization suspended",
            FleetAutomationVersionDocuments.CheckAccess("p3", world.Node.DeviceId, devices, Access(team with { Members = [] }, "p3")));
    }

    // A server that answers the device list and the access document chain it holds.
    private sealed class AccessServer(IReadOnlyList<DeviceListEntryDto> chain) : HttpMessageHandler
    {
        public List<AccessDocumentDto> Documents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/organization" => JsonSerializer.SerializeToUtf8Bytes(new OrganizationDto("org", "Org", Time, []), ServerApiJsonContext.Default.OrganizationDto),
                "/api/v1/device-list" => JsonSerializer.SerializeToUtf8Bytes(new ListResponse<DeviceListEntryDto>(chain), ServerApiJsonContext.Default.ListResponseDeviceListEntryDto),
                "/api/v1/access/documents" => JsonSerializer.SerializeToUtf8Bytes(new AccessDocumentListDto([.. Documents]), FleetAccessDocumentJsonContext.Default.AccessDocumentListDto),
                var path => throw new InvalidOperationException($"Unexpected call to {path}."),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    // Alex's desktop (the first controller, p1) adds his node, then Mia's (p2) and Max's (p3) first devices.
    private sealed class World : IDisposable
    {
        private readonly TestFolder _folder = new();

        public World()
        {
            Desktop = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "desktop"));
            Node = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "node"));
            Mia = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "mia"));
            Max = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "max"));
        }

        public DeviceIdentity Desktop { get; }

        public DeviceIdentity Node { get; }

        public DeviceIdentity Mia { get; }

        public DeviceIdentity Max { get; }

        public AccessServer Server() => new([.. Chain().Select(static item => item.Dto)]);

        public IReadOnlyList<(FleetSignedDeviceListEntry Entry, DeviceListEntryDto Dto)> Chain()
        {
            var list = new List<(FleetSignedDeviceListEntry, DeviceListEntryDto)>();
            void Add(string? organization, string operation, DeviceIdentity device, string kind, string? owner)
            {
                var previous = list.Count == 0 ? null : list[^1].Item2.Hash;
                var payload = new FleetDeviceListEntryPayload(1, organization, list.Count + 1, previous, operation, device.DeviceId,
                    Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()), kind, owner, Desktop.DeviceId, Time);
                var text = FleetDeviceListEntries.SerializePayload(payload);
                var signed = new FleetSignedDeviceListEntry(text, Base64Url.EncodeToString(Desktop.SignData(FleetDeviceListEntries.GetSignedBytes(text))));
                list.Add((signed, new DeviceListEntryDto(list.Count + 1, FleetDeviceListEntries.Hash(signed), signed, Time)));
            }

            Add(null, FleetDeviceListOperations.Add, Desktop, FleetDeviceKinds.Controller, null);
            Add("org", FleetDeviceListOperations.Add, Node, FleetDeviceKinds.Node, "p1");
            Add("org", FleetDeviceListOperations.Add, Mia, FleetDeviceKinds.Controller, "p2");
            Add("org", FleetDeviceListOperations.Add, Max, FleetDeviceKinds.Controller, "p3");
            return list;
        }

        public AccessDocumentDto Sign(DeviceIdentity signer, long number, AccessDocumentDto? previous, IReadOnlyList<string> owners, IReadOnlyList<string>? signers = null,
            IReadOnlyList<FleetAccessTeam>? teams = null, IReadOnlyList<FleetAccessMachine>? machines = null, IReadOnlyList<string>? suspended = null)
        {
            var document = new FleetAccessDocument(1, "org", number, previous?.Hash, 4, owners, signers ?? [], teams ?? [], machines ?? [], suspended ?? [], signer.DeviceId, Time);
            var text = FleetAccessDocuments.Serialize(document);
            var signed = new FleetSignedAccessDocument(text, Base64Url.EncodeToString(signer.SignData(FleetAccessDocuments.GetSignedBytes(text))));
            return new AccessDocumentDto(number, FleetAccessDocuments.Hash(signed), signed, Time);
        }

        public void Dispose()
        {
            Desktop.Dispose();
            Node.Dispose();
            Mia.Dispose();
            Max.Dispose();
            _folder.Dispose();
        }
    }
}
