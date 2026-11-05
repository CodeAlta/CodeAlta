using System.Buffers.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
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
    public void AMachineGivenToAnotherPerson_TakesTheKeysValuesBudgetsAndGrantsOfItsNewOwner_AndNoneOfItsFormerOwner()
    {
        using var devices = new Devices();
        var list = devices.List();
        // The node of p1 (Desk) was given to p2 (Bea): the device list still names p1, the signed access document names p2.
        var access = FleetAccessState.Empty with { PersonalMachines = System.Collections.Immutable.ImmutableDictionary.CreateRange([KeyValuePair.Create(devices.Node.DeviceId, "p2")]) };
        Assert.AreEqual("p2", FleetAccessState.PersonOf(access, list.Members[devices.Node.DeviceId]));
        Assert.AreEqual("p1", FleetAccessState.PersonOf(access, list.Members[devices.Desk.DeviceId]), "a document gives machines only");

        // The keys of a space: the new owner's open on the machine, the former owner's no more.
        var node = PublicKey(devices.Node);
        FleetMetadataKey KeyOf(string person) => FleetMetadataKey.Generate(new FleetKeyGrant("org", person, 1, list.Sequence) { SpaceId = "space-" + person });
        var beaKey = KeyOf("p2");
        var byBea = FleetKeyWrap.Wrap(beaKey, node, beaKey.Grant!, devices.Bea);
        Assert.AreEqual(beaKey.Id, FleetKeyWrap.Unwrap(byBea, beaKey.Id, 1, "space-p2", devices.Node, list, access).Id);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(byBea, beaKey.Id, 1, "space-p2", devices.Node, list), "without the document, Bea is another person");
        Assert.IsTrue(FleetKeyWrap.IsGranted(byBea, beaKey.Id, 1, "space-p2", devices.Node.DeviceId, list, access));
        var deskKey = KeyOf("p1");
        var byDesk = FleetKeyWrap.Wrap(deskKey, node, deskKey.Grant!, devices.Desk);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(byDesk, deskKey.Id, 1, "space-p1", devices.Node, list, access), "the former owner gives the machine no key");
        Assert.IsFalse(FleetKeyWrap.IsGranted(byDesk, deskKey.Id, 1, "space-p1", devices.Node.DeviceId, list, access));

        // Values: the new owner seals them for the machine, which opens them; the former owner seals none for it.
        Assert.AreEqual(devices.Node.DeviceId, FleetSecretSealing.Recipients(list, access, "p2", [devices.Node.DeviceId]).Single(member => member.Kind == FleetDeviceKinds.Node).DeviceId);
        Assert.IsFalse(FleetSecretSealing.Recipients(list, access, "p1", [devices.Node.DeviceId]).Any(member => member.Kind == FleetDeviceKinds.Node));
        var binding = new FleetSecretBinding("v1", FleetSecretKinds.Secret, "TOKEN", FleetSecretScopeKinds.Person, null, 1, false);
        var copies = FleetSecretSealing.Seal("s3cret", binding, FleetSecretSealing.Recipients(list, access, "p2", [devices.Node.DeviceId]), list, access, devices.Bea);
        var copy = copies.Single(item => item.DeviceId == devices.Node.DeviceId).Sealed;
        Assert.IsTrue(FleetSecretSealing.TryOpen(copy, binding, devices.Node, list, access, out var value, out var reason), reason);
        Assert.AreEqual("s3cret", value);
        Assert.IsFalse(FleetSecretSealing.TryOpen(copy, binding, devices.Node, list, out _, out _), "without the document, Bea is another person");
        Assert.ThrowsExactly<ArgumentException>(() => FleetSecretSealing.Seal("s3cret", binding, [list.Members[devices.Node.DeviceId]], list, access, devices.Desk));

        // Budgets and environments: those of the new owner hold on the machine, those of the former owner no more.
        var time = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
        FleetSignedBudget Budget(DeviceIdentity signer, string person) => FleetBudgetsManager.SignBudget(signer, new FleetBudgetDefinition(FleetBudgetDefinition.CurrentVersion, "org",
            person, "b-" + person, "Budget", FleetBudgetScopeKinds.Person, null, FleetBudgetMeasures.Tokens, 1000, FleetBudgetPeriods.Day, "UTC", FleetBudgetModes.Advisory,
            FleetBudgetReachedActions.Stop, 1, false, signer.DeviceId, time));
        Assert.IsTrue(FleetBudgetDocuments.TryVerify(Budget(devices.Bea, "p2"), list, access, devices.Node.DeviceId, out _, out reason), reason);
        Assert.IsFalse(FleetBudgetDocuments.TryVerify(Budget(devices.Desk, "p1"), list, access, devices.Node.DeviceId, out _, out _));
        Assert.IsTrue(FleetBudgetDocuments.TryVerify(Budget(devices.Desk, "p1"), list, devices.Node.DeviceId, out _, out _), "without the document, the device list decides");
        FleetSignedEnvironment Environment(DeviceIdentity signer, string person) => FleetServerClient.SignEnvironment(signer, new FleetEnvironmentDefinition(
            FleetEnvironmentDefinition.CurrentVersion, "org", person, "e-" + person, "prod", 1, [], [], [], false, signer.DeviceId, time));
        Assert.IsTrue(FleetEnvironmentDocuments.TryVerify(Environment(devices.Bea, "p2"), list, access, devices.Node.DeviceId, out _, out reason), reason);
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerify(Environment(devices.Desk, "p1"), list, access, devices.Node.DeviceId, out _, out _));

        // A personal automation its new owner gives it opens there; one of its former owner no more.
        var content = new FleetAutomationContent("{\"steps\":[]}", null);
        var beaVersion = FleetAutomationSigning.Seal(content, "a1", 1, devices.Node.DeviceId, list, devices.Bea);
        Assert.IsTrue(FleetAutomationSigning.TryOpen(beaVersion, "a1", 1, devices.Node.DeviceId, devices.Node, list, access, out _, out reason), reason);
        Assert.IsFalse(FleetAutomationSigning.TryOpen(FleetAutomationSigning.Seal(content, "a1", 1, devices.Node.DeviceId, list, devices.Desk), "a1", 1, devices.Node.DeviceId,
            devices.Node, list, access, out _, out _));

        // Grants of the sessions of the machine: signed by a device of its new owner, never of its former owner.
        var metadata = new FleetSessionMetadata("Fix the parser");
        var (sealedByBea, beaWraps) = FleetGrantKeys.Seal("org", "g1", "s1", metadata, [PublicKey(devices.Cy)], list.Sequence, devices.Bea);
        Assert.AreEqual("Fix the parser", FleetGrantKeys.Open(sealedByBea, beaWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Cy, list, access).Title);
        var (sealedByDesk, deskWraps) = FleetGrantKeys.Seal("org", "g1", "s1", metadata, [PublicKey(devices.Cy)], list.Sequence, devices.Desk);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedByDesk, deskWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Cy, list, access));
    }

    [TestMethod]
    public void ATeamMachine_TakesValuesBudgetsAndEnvironmentsOfItsMaintainers_KeysOfItsPeople_AndNothingOfItsDeviceListPerson()
    {
        using var devices = new Devices();
        var list = devices.List();
        // The node p1 (Desk) enrolled is a machine of team t1: p2 (Bea) maintains it, p3 (Cy) is a member, p1 is not in it.
        var access = FleetAccessState.Empty with
        {
            Teams = System.Collections.Immutable.ImmutableDictionary.CreateRange([KeyValuePair.Create("t1", new FleetAccessTeam("t1", ["p2"], ["p3"], false))]),
            Machines = System.Collections.Immutable.ImmutableDictionary.CreateRange([KeyValuePair.Create(devices.Node.DeviceId, "t1")]),
        };
        var machine = list.Members[devices.Node.DeviceId];
        Assert.AreEqual((true, false, false), (FleetAccessState.HoldsValuesOf(access, machine, "p2"), FleetAccessState.HoldsValuesOf(access, machine, "p3"),
            FleetAccessState.HoldsValuesOf(access, machine, "p1")), "maintainers manage the team machine; members and the device-list person do not");
        Assert.AreEqual((true, true, false), (FleetAccessState.HoldsKeysOf(access, machine, "p2"), FleetAccessState.HoldsKeysOf(access, machine, "p3"),
            FleetAccessState.HoldsKeysOf(access, machine, "p1")), "maintainers and members seal their sessions there; the device-list person does not");
        Assert.IsFalse(FleetAccessState.HoldsValuesOf(access with { Suspended = access.Suspended.Add("p2") }, machine, "p2"), "a suspended maintainer gives nothing");
        Assert.IsTrue(FleetAccessState.HoldsValuesOf(access, list.Members[devices.Desk.DeviceId], "p1"), "a controller device stays of its person");

        // The keys of a space: those of its maintainers and members open on the machine, never those of the device-list person.
        var node = PublicKey(devices.Node);
        FleetMetadataKey KeyOf(string person, string scope) => FleetMetadataKey.Generate(new FleetKeyGrant("org", person, 1, list.Sequence) { SpaceId = scope });
        foreach (var (signer, person) in new[] { (devices.Bea, "p2"), (devices.Cy, "p3") })
        {
            var key = KeyOf(person, "space-" + person);
            var wrapped = FleetKeyWrap.Wrap(key, node, key.Grant!, signer);
            Assert.AreEqual(key.Id, FleetKeyWrap.Unwrap(wrapped, key.Id, 1, "space-" + person, devices.Node, list, access).Id, $"the key of {person} opens on the team machine");
            Assert.IsTrue(FleetKeyWrap.IsGranted(wrapped, key.Id, 1, "space-" + person, devices.Node.DeviceId, list, access));
        }

        var deskKey = KeyOf("p1", "space-p1");
        var byDesk = FleetKeyWrap.Wrap(deskKey, node, deskKey.Grant!, devices.Desk);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(byDesk, deskKey.Id, 1, "space-p1", devices.Node, list, access), "the device-list person gives it no key");
        Assert.IsFalse(FleetKeyWrap.IsGranted(byDesk, deskKey.Id, 1, "space-p1", devices.Node.DeviceId, list, access));
        Assert.AreEqual(deskKey.Id, FleetKeyWrap.Unwrap(byDesk, deskKey.Id, 1, "space-p1", devices.Node, list).Id, "without the document, the device list decides");

        // Values: a maintainer seals them for the machine, which opens them; a member and the device-list person seal none for it.
        Assert.AreEqual(devices.Node.DeviceId, FleetSecretSealing.Recipients(list, access, "p2", [devices.Node.DeviceId]).Single(member => member.Kind == FleetDeviceKinds.Node).DeviceId);
        Assert.IsFalse(FleetSecretSealing.Recipients(list, access, "p3", [devices.Node.DeviceId]).Any(member => member.Kind == FleetDeviceKinds.Node));
        Assert.IsFalse(FleetSecretSealing.Recipients(list, access, "p1", [devices.Node.DeviceId]).Any(member => member.Kind == FleetDeviceKinds.Node));
        var binding = new FleetSecretBinding("v1", FleetSecretKinds.Secret, "TOKEN", FleetSecretScopeKinds.Person, null, 1, false);
        var copies = FleetSecretSealing.Seal("s3cret", binding, FleetSecretSealing.Recipients(list, access, "p2", [devices.Node.DeviceId]), list, access, devices.Bea);
        Assert.IsTrue(FleetSecretSealing.TryOpen(copies.Single(item => item.DeviceId == devices.Node.DeviceId).Sealed, binding, devices.Node, list, access, out var value, out var reason), reason);
        Assert.AreEqual("s3cret", value);
        Assert.ThrowsExactly<ArgumentException>(() => FleetSecretSealing.Seal("s3cret", binding, [machine], list, access, devices.Cy), "a member seals no value for the team machine");
        Assert.ThrowsExactly<ArgumentException>(() => FleetSecretSealing.Seal("s3cret", binding, [machine], list, access, devices.Desk), "nor does the device-list person");
        // A copy of a value a member wrapped to the machine anyway does not open there: values come from its maintainers only.
        var memberValue = KeyOf("p3", "secret/v1");
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(FleetKeyWrap.Wrap(memberValue, node, memberValue.Grant!, devices.Cy), memberValue.Id, 1, "secret/v1",
            devices.Node, list, access));

        // Budgets and environments: those of its maintainers hold on the machine; those of a member or of the device-list person do not.
        var time = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
        FleetSignedBudget Budget(DeviceIdentity signer, string person) => FleetBudgetsManager.SignBudget(signer, new FleetBudgetDefinition(FleetBudgetDefinition.CurrentVersion, "org",
            person, "b-" + person, "Budget", FleetBudgetScopeKinds.Machine, devices.Node.DeviceId, FleetBudgetMeasures.Tokens, 1000, FleetBudgetPeriods.Day, "UTC",
            FleetBudgetModes.Advisory, FleetBudgetReachedActions.Stop, 1, false, signer.DeviceId, time));
        Assert.IsTrue(FleetBudgetDocuments.TryVerify(Budget(devices.Bea, "p2"), list, access, devices.Node.DeviceId, out _, out reason), reason);
        Assert.IsFalse(FleetBudgetDocuments.TryVerify(Budget(devices.Cy, "p3"), list, access, devices.Node.DeviceId, out _, out _));
        Assert.IsFalse(FleetBudgetDocuments.TryVerify(Budget(devices.Desk, "p1"), list, access, devices.Node.DeviceId, out _, out _));
        FleetSignedEnvironment Environment(DeviceIdentity signer, string person) => FleetServerClient.SignEnvironment(signer, new FleetEnvironmentDefinition(
            FleetEnvironmentDefinition.CurrentVersion, "org", person, "e-" + person, "prod", 1, [], [], [], false, signer.DeviceId, time));
        Assert.IsTrue(FleetEnvironmentDocuments.TryVerify(Environment(devices.Bea, "p2"), list, access, devices.Node.DeviceId, out _, out reason), reason);
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerify(Environment(devices.Cy, "p3"), list, access, devices.Node.DeviceId, out _, out _));
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerify(Environment(devices.Desk, "p1"), list, access, devices.Node.DeviceId, out _, out _));

        // Grants of a session of the machine: signed by a device of the person who started it there, never of another person.
        var metadata = new FleetSessionMetadata("Fix the parser");
        var (sealedByCy, cyWraps) = FleetGrantKeys.Seal("org", "g1", "s1", metadata, [PublicKey(devices.Laptop)], list.Sequence, devices.Cy);
        Assert.AreEqual("Fix the parser", FleetGrantKeys.Open(sealedByCy, cyWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Laptop, list, access, "p3",
            devices.StartedBy("s1", "p3")).Title);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedByCy, cyWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Laptop, list,
            access, "p2", devices.StartedBy("s1", "p2")), "a copy of another person than the one of the session");
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedByCy, cyWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Laptop, list,
            access), "a session of a team machine without its person");
        var (sealedByDesk, deskWraps) = FleetGrantKeys.Seal("org", "g1", "s1", metadata, [PublicKey(devices.Laptop)], list.Sequence, devices.Desk);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedByDesk, deskWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Laptop, list,
            access, "p1"), "the device-list person started no session there as a person of the team");
    }

    [TestMethod]
    public void AMachineOfTheOrganization_TakesValuesBudgetsEnvironmentsAndKeysOfItsSigners_AndNothingOfItsDeviceListPerson()
    {
        using var devices = new Devices();
        var list = devices.List();
        // The node p1 (Desk) enrolled is the organization's: p2 (Bea) owns the organization, p3 (Cy) signs as an admin, p1 has no role.
        var access = FleetAccessState.Empty with { OrganizationId = "org", Owners = ["p2"], Signers = ["p3"], OrganizationMachines = [devices.Node.DeviceId] };
        var machine = list.Members[devices.Node.DeviceId];
        Assert.AreEqual((true, true, false), (FleetAccessState.HoldsValuesOf(access, machine, "p2"), FleetAccessState.HoldsValuesOf(access, machine, "p3"),
            FleetAccessState.HoldsValuesOf(access, machine, "p1")), "its owners and admins who sign manage it; the device-list person does not");
        Assert.AreEqual((true, true, false), (FleetAccessState.HoldsKeysOf(access, machine, "p2"), FleetAccessState.HoldsKeysOf(access, machine, "p3"),
            FleetAccessState.HoldsKeysOf(access, machine, "p1")), "its owners and admins who sign seal their sessions there; the device-list person does not");
        Assert.IsFalse(FleetAccessState.HoldsKeysOf(access with { Suspended = access.Suspended.Add("p3") }, machine, "p3"), "a suspended signer gives nothing");
        Assert.IsTrue(FleetAccessState.HoldsValuesOf(access with { Owners = ["p1"] }, machine, "p1"), "the device-list person holds it as an owner, when one");

        // Keys of a space: those of its signers open on the machine, never those of the device-list person.
        var node = PublicKey(devices.Node);
        FleetMetadataKey KeyOf(string person, string scope) => FleetMetadataKey.Generate(new FleetKeyGrant("org", person, 1, list.Sequence) { SpaceId = scope });
        foreach (var (signer, person) in new[] { (devices.Bea, "p2"), (devices.Cy, "p3") })
        {
            var key = KeyOf(person, "space-" + person);
            Assert.AreEqual(key.Id, FleetKeyWrap.Unwrap(FleetKeyWrap.Wrap(key, node, key.Grant!, signer), key.Id, 1, "space-" + person, devices.Node, list, access).Id,
                $"the key of {person} opens on the machine of the organization");
        }

        var deskKey = KeyOf("p1", "space-p1");
        var byDesk = FleetKeyWrap.Wrap(deskKey, node, deskKey.Grant!, devices.Desk);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(byDesk, deskKey.Id, 1, "space-p1", devices.Node, list, access), "the device-list person gives it no key");
        Assert.IsFalse(FleetKeyWrap.IsGranted(byDesk, deskKey.Id, 1, "space-p1", devices.Node.DeviceId, list, access));

        // Values: a signer seals them for the machine, which opens them; the device-list person seals none for it.
        Assert.IsTrue(FleetSecretSealing.Recipients(list, access, "p3", [devices.Node.DeviceId]).Any(member => member.DeviceId == devices.Node.DeviceId));
        Assert.IsFalse(FleetSecretSealing.Recipients(list, access, "p1", [devices.Node.DeviceId]).Any(member => member.Kind == FleetDeviceKinds.Node));
        var binding = new FleetSecretBinding("v1", FleetSecretKinds.Secret, "TOKEN", FleetSecretScopeKinds.Person, null, 1, false);
        var copies = FleetSecretSealing.Seal("s3cret", binding, FleetSecretSealing.Recipients(list, access, "p3", [devices.Node.DeviceId]), list, access, devices.Cy);
        Assert.IsTrue(FleetSecretSealing.TryOpen(copies.Single(item => item.DeviceId == devices.Node.DeviceId).Sealed, binding, devices.Node, list, access, out var value, out var reason), reason);
        Assert.AreEqual("s3cret", value);
        Assert.ThrowsExactly<ArgumentException>(() => FleetSecretSealing.Seal("s3cret", binding, [machine], list, access, devices.Desk), "the device-list person seals no value for it");

        // Budgets and environments: those of its signers hold on the machine; those of the device-list person do not.
        var time = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
        FleetSignedBudget Budget(DeviceIdentity signer, string person) => FleetBudgetsManager.SignBudget(signer, new FleetBudgetDefinition(FleetBudgetDefinition.CurrentVersion, "org",
            person, "b-" + person, "Budget", FleetBudgetScopeKinds.Machine, devices.Node.DeviceId, FleetBudgetMeasures.Tokens, 1000, FleetBudgetPeriods.Day, "UTC",
            FleetBudgetModes.Advisory, FleetBudgetReachedActions.Stop, 1, false, signer.DeviceId, time));
        Assert.IsTrue(FleetBudgetDocuments.TryVerify(Budget(devices.Cy, "p3"), list, access, devices.Node.DeviceId, out _, out reason), reason);
        Assert.IsFalse(FleetBudgetDocuments.TryVerify(Budget(devices.Desk, "p1"), list, access, devices.Node.DeviceId, out _, out _));
        FleetSignedEnvironment Environment(DeviceIdentity signer, string person) => FleetServerClient.SignEnvironment(signer, new FleetEnvironmentDefinition(
            FleetEnvironmentDefinition.CurrentVersion, "org", person, "e-" + person, "prod", 1, [], [], [], false, signer.DeviceId, time));
        Assert.IsTrue(FleetEnvironmentDocuments.TryVerify(Environment(devices.Bea, "p2"), list, access, devices.Node.DeviceId, out _, out reason), reason);
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerify(Environment(devices.Desk, "p1"), list, access, devices.Node.DeviceId, out _, out _));

        // Grants of a session of the machine: signed by a device of the signer who started it there, never of another person.
        var metadata = new FleetSessionMetadata("Fix the parser");
        var (sealedByCy, cyWraps) = FleetGrantKeys.Seal("org", "g1", "s1", metadata, [PublicKey(devices.Laptop)], list.Sequence, devices.Cy);
        Assert.AreEqual("Fix the parser", FleetGrantKeys.Open(sealedByCy, cyWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Laptop, list, access, "p3",
            devices.StartedBy("s1", "p3")).Title);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedByCy, cyWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Laptop, list,
            access), "a session of a machine of the organization without its person");
        var (sealedByDesk, deskWraps) = FleetGrantKeys.Seal("org", "g1", "s1", metadata, [PublicKey(devices.Laptop)], list.Sequence, devices.Desk);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(sealedByDesk, deskWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Laptop, list,
            access, "p1"), "the device-list person, without a role, gives no copy of a session there");
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

    [TestMethod]
    public void ACopyOfASessionOfATeamMachine_HoldsForThePersonTheMachineSignedStartedIt_NeverForTheOneTheServerSays()
    {
        using var devices = new Devices();
        var list = devices.List();
        // A machine of team t1: Bea (p2) maintains it, Cy (p3) is a member. Bea started the session s1 there; Cy started s2.
        var access = FleetAccessState.Empty with
        {
            Teams = System.Collections.Immutable.ImmutableDictionary.CreateRange([KeyValuePair.Create("t1", new FleetAccessTeam("t1", ["p2"], ["p3"], false))]),
            Machines = System.Collections.Immutable.ImmutableDictionary.CreateRange([KeyValuePair.Create(devices.Node.DeviceId, "t1")]),
        };
        var (byBea, beaWraps) = FleetGrantKeys.Seal("org", "g1", "s1", new FleetSessionMetadata("Fix the parser"), [PublicKey(devices.Laptop)], list.Sequence, devices.Bea);
        Assert.AreEqual("Fix the parser", FleetGrantKeys.Open(byBea, beaWraps.Single().Wrapped, "org", "g1", "s1", devices.Node.DeviceId, devices.Laptop, list, access, "p2",
            devices.StartedBy("s1", "p2")).Title, "the machine signed that Bea started it");

        // The coordinator, with Cy, says s1 is Cy's and relays a copy Cy signed for it, with what she wants its title to be.
        var (byCy, cyWraps) = FleetGrantKeys.Seal("org", "g1", "s1", new FleetSessionMetadata("Nothing to see"), [PublicKey(devices.Laptop)], list.Sequence, devices.Cy);
        var forged = cyWraps.Single().Wrapped;
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(byCy, forged, "org", "g1", "s1", devices.Node.DeviceId, devices.Laptop, list, access, "p3"),
            "the person the server says, without the machine's word, is not who started the session");
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(byCy, forged, "org", "g1", "s1", devices.Node.DeviceId, devices.Laptop, list, access, "p3",
            devices.StartedBy("s2", "p3")), "the machine's word for another session of hers");
        Assert.ThrowsExactly<InvalidDataException>(() => FleetGrantKeys.Open(byCy, forged, "org", "g1", "s1", devices.Node.DeviceId, devices.Laptop, list, access, "p3",
            System.Buffers.Text.Base64Url.EncodeToString(devices.Cy.SignData(FleetSessionPersons.GetSignedBytes("org", devices.Node.DeviceId, "s1", "p3")))),
            "her own word for it");
    }

    // The founder's laptop, the desk of the owner of the session (p1), their node, Bea (p2) and Cy (p3).
    private sealed class Devices : IDisposable
    {
        // The node's signature that a person started one of its sessions, as it publishes it.
        public string StartedBy(string sessionId, string personId)
            => System.Buffers.Text.Base64Url.EncodeToString(Node.SignData(FleetSessionPersons.GetSignedBytes("org", Node.DeviceId, sessionId, personId)));

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
