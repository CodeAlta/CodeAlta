using System.Buffers.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The keys of a grant of sealed content (<c>doc/fleet/access-control.md</c>, access and keys): a device of the owner of a session
/// seals its metadata with a key of the grant and wraps that key to each device of the grantee; only that device opens it, and only
/// when the copy holds for this grant and session and was signed by a controller device of the person who owns the session's machine.
/// </summary>
[TestClass]
public sealed class FleetGrantKeysTests
{
    [TestMethod]
    public void ASessionSharedByItsOwnerOpensOnTheGranteesDevice_AndOnNoOther()
    {
        using var devices = new Devices();
        var list = devices.List();
        var (sealedText, wraps) = FleetGrantKeys.Seal("org", "g1", "s1", new FleetSessionMetadata("Fix the parser"), [PublicKey(devices.Bea)], list.Sequence, devices.Desk);
        var wrapped = wraps.Single();
        Assert.AreEqual(devices.Bea.DeviceId, wrapped.DeviceId);

        Assert.AreEqual("Fix the parser", FleetGrantKeys.Open(sealedText, wrapped.Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Bea, list).Title);
        // Another device, grant, session or organization: refused.
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedText, wrapped.Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Cy, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedText, wrapped.Wrapped, "org", "g2", "s1", devices.Node.DeviceId, devices.Bea, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedText, wrapped.Wrapped, "org", "g1", "s2", devices.Node.DeviceId, devices.Bea, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedText, wrapped.Wrapped, "other", "g1", "s1", devices.Node.DeviceId, devices.Bea, list));
        // A changed copy, or a copy for Bea relabelled for Cy: the device is bound into the derivation and the signature.
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedText, Tamper(wrapped.Wrapped, "ct"), "org", "g1", "s1", devices.Node.DeviceId, devices.Bea, list));
        var relabelled = wrapped.Wrapped.Replace(devices.Bea.DeviceId + "\"", devices.Cy.DeviceId + "\"", StringComparison.Ordinal);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedText, relabelled, "org", "g1", "s1", devices.Node.DeviceId, devices.Cy, list));
        // The metadata of another session does not open with the key of this grant.
        var (otherSealed, _) = FleetGrantKeys.Seal("org", "g1", "s1", new FleetSessionMetadata("Other"), [PublicKey(devices.Bea)], list.Sequence, devices.Desk);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(otherSealed, wrapped.Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Bea, list));
    }

    [TestMethod]
    public void ACopyHoldsOnlyWhenAControllerOfTheOwnerOfTheMachineSignedIt()
    {
        using var devices = new Devices();
        var list = devices.List();
        var bea = PublicKey(devices.Bea);
        var metadata = new FleetSessionMetadata("Title");

        // Cy, another person, or Bea herself: they do not own the machine of the session, so what they give opens nothing.
        foreach (var signer in new[] { devices.Cy, devices.Bea })
        {
            var (sealedText, wraps) = FleetGrantKeys.Seal("org", "g1", "s1", metadata, [bea], list.Sequence, signer);
            Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedText, wraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Bea, list));
        }

        // The node of the owner is not a controller device: it gives no key either. A list this device has not verified neither.
        var (byNode, nodeWraps) = FleetGrantKeys.Seal("org", "g1", "s1", metadata, [bea], list.Sequence, devices.Node);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(byNode, nodeWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Bea, list));
        var (later, laterWraps) = FleetGrantKeys.Seal("org", "g1", "s1", metadata, [bea], list.Sequence + 1, devices.Desk);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(later, laterWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Bea, list));
        // A copy with no signature: refused.
        var (genuine, genuineWraps) = FleetGrantKeys.Seal("org", "g1", "s1", metadata, [bea], list.Sequence, devices.Desk);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(genuine, Tamper(genuineWraps.Single().Wrapped, "sig"), "org", "g1", "s1", devices.Node.DeviceId, devices.Bea, list));
        Assert.ThrowsExactly<ArgumentException>(() => FleetGrantKeys.Seal("org", "g1", "s1", metadata, [], list.Sequence, devices.Desk));
    }

    [TestMethod]
    public void AnAutomationHandedOverOpensOnTheDevicesOfWhoTakesIt_WhenItsFormerOwnerSignedIt()
    {
        using var devices = new Devices();
        var list = devices.List();
        var content = new FleetAutomationContent("[automations.nightly]\nname = \"Nightly\"\n", "github.com/codealta/codealta");
        var (sealedText, wraps) = FleetTransferHandover.Seal("org", "h1", "nightly", content, [PublicKey(devices.Bea)], list.Sequence, devices.Desk);
        Assert.IsFalse(sealedText.Contains("Nightly", StringComparison.Ordinal), "the server holds ciphertext only");
        var opened = FleetTransferHandover.Open(sealedText, wraps.Single().Wrapped, "org", "nightly", "p1", devices.Bea, list);
        Assert.AreEqual(content, opened);

        // Another device, automation, organization or former owner: refused; so is a copy signed by someone else than its former owner.
        Assert.ThrowsExactly<InvalidDataException>(() => FleetTransferHandover.Open(sealedText, wraps.Single().Wrapped, "org", "nightly", "p1", devices.Cy, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetTransferHandover.Open(sealedText, wraps.Single().Wrapped, "org", "other", "p1", devices.Bea, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetTransferHandover.Open(sealedText, wraps.Single().Wrapped, "else", "nightly", "p1", devices.Bea, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetTransferHandover.Open(sealedText, wraps.Single().Wrapped, "org", "nightly", "p3", devices.Bea, list));
        var (byCy, cyWraps) = FleetTransferHandover.Seal("org", "h1", "nightly", content, [PublicKey(devices.Bea)], list.Sequence, devices.Cy);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetTransferHandover.Open(byCy, cyWraps.Single().Wrapped, "org", "nightly", "p1", devices.Bea, list));
        // A copy of another hand-over does not open this one, nor does a grant's copy of a key.
        var (other, _) = FleetTransferHandover.Seal("org", "h2", "nightly", content, [PublicKey(devices.Bea)], list.Sequence, devices.Desk);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetTransferHandover.Open(other, wraps.Single().Wrapped, "org", "nightly", "p1", devices.Bea, list));
        var (_, grantWraps) = FleetGrantKeys.Seal("org", "h1", "automation:nightly", new FleetSessionMetadata("Title"), [PublicKey(devices.Bea)], list.Sequence, devices.Desk);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetTransferHandover.Open(sealedText, grantWraps.Single().Wrapped, "org", "nightly", "p1", devices.Bea, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetTransferHandover.Open(sealedText.Replace("\"ct\":\"", "\"ct\":\"A", StringComparison.Ordinal), wraps.Single().Wrapped,
            "org", "nightly", "p1", devices.Bea, list));
    }

    [TestMethod]
    public void AnAutomationOfATeamOpensOnItsMachineAndOnTheDevicesOfItsMaintainers_OfAnotherPersonThanTheMachine()
    {
        using var devices = new Devices();
        var list = devices.List();
        // The node was enrolled by p1; the team owns it now, and Bea (p2) and Cy (p3) maintain the team.
        var team = new FleetAccessTeam("infra", ["p2", "p3"], [], false, ["svc"]);
        var access = FleetAccessState.Empty with
        {
            OrganizationId = "org", Teams = FleetAccessState.Empty.Teams.Add("infra", team), Machines = FleetAccessState.Empty.Machines.Add(devices.Node.DeviceId, "infra"),
        };
        var content = new FleetAutomationContent("[automations.deploy]\n", "github.com/codealta/codealta");
        var sealedText = FleetAutomationSigning.SealForTeam(content, "deploy", 1, devices.Node.DeviceId, "infra", list, access, devices.Bea);
        Assert.IsFalse(sealedText.Contains("automations.deploy", StringComparison.Ordinal));
        foreach (var reader in new[] { devices.Node, devices.Bea, devices.Cy })
        {
            Assert.IsTrue(FleetAutomationSigning.TryOpen(sealedText, "deploy", 1, devices.Node.DeviceId, reader, list, access, out var opened, out var reason), reason);
            Assert.AreEqual(content, opened);
        }

        Assert.IsFalse(FleetAutomationSigning.TryOpen(sealedText, "deploy", 1, devices.Node.DeviceId, devices.Desk, list, access, out _, out _),
            "the person who enrolled the machine does not read what the team runs on it");
        Assert.IsFalse(FleetAutomationSigning.TryOpen(sealedText, "deploy", 1, devices.Node.DeviceId, devices.Node, list, null, out _, out var without));
        StringAssert.Contains(without, "access document");
        var notMaintainer = access with { Teams = access.Teams.SetItem("infra", team with { Maintainers = ["p3"] }) };
        Assert.IsFalse(FleetAutomationSigning.TryOpen(sealedText, "deploy", 1, devices.Node.DeviceId, devices.Node, list, notMaintainer, out _, out _),
            "a copy given by someone who no longer maintains the team");
        Assert.IsFalse(FleetAutomationSigning.TryOpen(sealedText, "deploy", 2, devices.Node.DeviceId, devices.Node, list, access, out _, out _), "another version");
        Assert.ThrowsExactly<ArgumentException>(() => FleetAutomationSigning.SealForTeam(content, "deploy", 1, devices.Node.DeviceId, "infra", list, access, devices.Desk),
            "only a maintainer seals for the team");
    }

    [TestMethod]
    public void AnAutomationOfTheOrganizationOpensOnItsMachineAndOnTheDevicesOfItsOwnersAndSigners()
    {
        using var devices = new Devices();
        var list = devices.List();
        // p1 owns the organization, Bea (p2) signs as an admin, Cy (p3) is a member; the node is the organization's.
        var access = FleetAccessState.Empty with
        {
            OrganizationId = "org", Owners = ["p1"], Signers = ["p2"], OrganizationMachines = [devices.Node.DeviceId], OrganizationServices = ["svc"],
        };
        var content = new FleetAutomationContent("[automations.report]\n", null);
        var sealedText = FleetAutomationSigning.SealForOrganization(content, "report", 1, devices.Node.DeviceId, list, access, devices.Bea);
        foreach (var reader in new[] { devices.Node, devices.Desk, devices.Bea })
            Assert.IsTrue(FleetAutomationSigning.TryOpen(sealedText, "report", 1, devices.Node.DeviceId, reader, list, access, out _, out var reason), reason);
        Assert.IsFalse(FleetAutomationSigning.TryOpen(sealedText, "report", 1, devices.Node.DeviceId, devices.Cy, list, access, out _, out _), "a member reads nothing of it");
        Assert.IsFalse(FleetAutomationSigning.TryOpen(sealedText, "report", 1, devices.Node.DeviceId, devices.Node, list, access with { Signers = [] }, out _, out _),
            "a copy given by someone who no longer signs");
        Assert.ThrowsExactly<ArgumentException>(() => FleetAutomationSigning.SealForOrganization(content, "report", 1, devices.Node.DeviceId, list, access, devices.Cy));
    }

    [TestMethod]
    public void TheKeyOfAProviderOfATeamOpensOnItsMachines_WhenAMaintainerSealedIt()
    {
        using var devices = new Devices();
        var list = devices.List();
        var team = new FleetAccessTeam("infra", ["p2"], ["p3"], false, ["svc"]);
        var access = FleetAccessState.Empty with
        {
            OrganizationId = "org", Teams = FleetAccessState.Empty.Teams.Add("infra", team), Machines = FleetAccessState.Empty.Machines.Add(devices.Node.DeviceId, "infra"),
        };
        var definition = new FleetProviderDefinition(FleetPrincipalKinds.Team, "infra", "Infra gateway", "openai-responses", "https://gateway.example/v1", ["gpt-6.1", "gpt-6.1-mini"]);
        var (sealedText, wraps) = FleetProviderKeys.Seal("org", "gateway", 2, definition, "sk-account-key", [PublicKey(devices.Node)], list.Sequence, devices.Bea);
        Assert.IsFalse(sealedText.Contains("sk-account", StringComparison.Ordinal), "the server holds ciphertext only");
        var copy = wraps.Single().Wrapped;
        Assert.AreEqual("sk-account-key", FleetProviderKeys.Open(sealedText, copy, "org", "gateway", 2, definition, devices.Node, list, access));
        // Another version or provider, or a signer who does not answer for the team: refused.
        Assert.ThrowsExactly<InvalidDataException>(() => FleetProviderKeys.Open(sealedText, copy, "org", "gateway", 3, definition, devices.Node, list, access));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetProviderKeys.Open(sealedText, copy, "org", "other", 2, definition, devices.Node, list, access));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetProviderKeys.Open(sealedText, copy, "org", "gateway", 2,
            definition with { OwnerKind = FleetPrincipalKinds.Organization, OwnerId = "org" }, devices.Node, list, access), "Bea does not sign for the organization");
        var (byMember, memberWraps) = FleetProviderKeys.Seal("org", "gateway", 2, definition, "sk-x", [PublicKey(devices.Node)], list.Sequence, devices.Cy);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetProviderKeys.Open(byMember, memberWraps.Single().Wrapped, "org", "gateway", 2, definition,
            devices.Node, list, access), "a member of the team gives it no key");

        // The server keeps the copy but changes what the machine installs with it: the endpoint, the type, the models, the name
        // or the owner. The key does not open: the machine never sends it, or a session, where its maintainer did not say.
        foreach (var altered in new[]
                 {
                     definition with { Endpoint = "https://coordinator.example/v1" }, definition with { Endpoint = null }, definition with { Type = "anthropic" },
                     definition with { Models = ["gpt-6.1-mini", "gpt-6.1"] }, definition with { Models = ["gpt-6.1"] }, definition with { Name = "Other" },
                     definition with { OwnerId = "other-team" },
                 })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => FleetProviderKeys.Open(sealedText, copy, "org", "gateway", 2, altered, devices.Node, list, access), altered.ToString());
        }

        Assert.AreEqual("sk-account-key", FleetProviderKeys.Open(sealedText, copy, "org", "gateway", 2, definition with { Models = ["gpt-6.1", "gpt-6.1-mini"] }, devices.Node,
            list, access), "the same definition, in another instance");
    }

    // The founder's laptop, the desk of the owner of the session (p1), their node, Bea (p2) and Cy (p3).
    private sealed class Devices : IDisposable
    {
        private readonly TestFolder _folder = new();

        public Devices()
        {
            Laptop = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "laptop"));
            Desk = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "desk"));
            Node = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "node"));
            Bea = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "bea"));
            Cy = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "cy"));
        }

        public DeviceIdentity Laptop { get; }

        public DeviceIdentity Desk { get; }

        public DeviceIdentity Node { get; }

        public DeviceIdentity Bea { get; }

        public DeviceIdentity Cy { get; }

        public FleetDeviceListState List()
        {
            var entries = new List<FleetSignedDeviceListEntry> { Entry(1, null, Laptop, FleetDeviceKinds.Controller, null, null) };
            entries.Add(Entry(2, FleetDeviceListEntries.Hash(entries[^1]), Desk, FleetDeviceKinds.Controller, "org", "p1"));
            entries.Add(Entry(3, FleetDeviceListEntries.Hash(entries[^1]), Node, FleetDeviceKinds.Node, "org", "p1"));
            entries.Add(Entry(4, FleetDeviceListEntries.Hash(entries[^1]), Bea, FleetDeviceKinds.Controller, "org", "p2"));
            entries.Add(Entry(5, FleetDeviceListEntries.Hash(entries[^1]), Cy, FleetDeviceKinds.Controller, "org", "p3"));
            return FleetDeviceListVerifier.VerifyOrThrow(entries, "org");
        }

        private FleetSignedDeviceListEntry Entry(long sequence, string? previous, DeviceIdentity device, string kind, string? organization, string? owner)
        {
            var payload = FleetDeviceListEntries.SerializePayload(new FleetDeviceListEntryPayload(1, organization, sequence, previous, FleetDeviceListOperations.Add, device.DeviceId,
                Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()), kind, owner, Laptop.DeviceId, new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero)));
            return new FleetSignedDeviceListEntry(payload, Base64Url.EncodeToString(Laptop.SignData(FleetDeviceListEntries.GetSignedBytes(payload))));
        }

        public void Dispose()
        {
            Laptop.Dispose();
            Desk.Dispose();
            Node.Dispose();
            Bea.Dispose();
            Cy.Dispose();
            _folder.Dispose();
        }
    }

    private static FleetDevicePublicKey PublicKey(DeviceIdentity identity)
    {
        Assert.IsTrue(FleetDevicePublicKey.TryParse(Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo()), out var key));
        return key;
    }

    // Changes one character of a base64url field of the copy.
    private static string Tamper(string json, string field)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement.GetProperty(field).GetString()!;
        var changed = value[..10] + (value[10] == 'A' ? 'B' : 'A') + value[11..];
        return json.Replace(value, changed, StringComparison.Ordinal);
    }
}
