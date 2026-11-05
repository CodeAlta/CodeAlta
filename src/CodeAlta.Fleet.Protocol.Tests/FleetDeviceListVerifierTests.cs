using System.Buffers.Text;
using System.Security.Cryptography;

namespace CodeAlta.Fleet.Protocol.Tests;

/// <summary>
/// The rules of the device list as every device replays it: a chain of entries, each after the hash of the one
/// before, signed by a controller device of the list that adds devices of its own person only.
/// </summary>
[TestClass]
public sealed class FleetDeviceListVerifierTests
{
    private const string Organization = "org-1";
    private const string Owner = "person-1";
    private static readonly DateTimeOffset Time = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AChainOfControllersAndNodesVerifies()
    {
        using var desktop = new Key();
        using var laptop = new Key();
        using var node = new Key();
        var chain = new Chain();
        chain.Add(desktop.First());
        chain.Add(desktop, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
        chain.Add(desktop, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, Owner);
        // A controller approved by the first one signs in turn.
        chain.Add(laptop, FleetDeviceListOperations.Remove, node, null, null);

        var state = FleetDeviceListVerifier.VerifyOrThrow(chain.Entries, Organization);
        Assert.AreEqual((4L, Organization), (state.Sequence, state.OrganizationId));
        Assert.IsTrue(state.IsController(desktop.Id) && state.IsController(laptop.Id));
        Assert.IsFalse(state.Members.ContainsKey(node.Id));
        Assert.AreEqual(Owner, state.Members[desktop.Id].OwnerId, "the first device's person is the one its first entry names");
        Assert.AreEqual(laptop.PublicKey, state.Members[laptop.Id].Key.Text);
        Assert.AreEqual(FleetDeviceListEntries.Hash(chain.Entries[^1]), state.LastHash);
        Assert.AreEqual(4L, state.LastRemovalSequence, "the list knows when it last removed a device");
        Assert.AreEqual(0L, FleetDeviceListVerifier.VerifyOrThrow(chain.Entries.Take(3), Organization).LastRemovalSequence);
    }

    [TestMethod]
    public void ANodeSignsNoEntry()
    {
        using var desktop = new Key();
        using var node = new Key();
        using var stranger = new Key();
        var chain = new Chain();
        chain.Add(desktop.First());
        chain.Add(desktop, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
        chain.Add(node, FleetDeviceListOperations.Add, stranger, FleetDeviceKinds.Controller, Owner);
        AssertBroken(chain, 3, "its signer is not a controller device");
    }

    [TestMethod]
    public void AnEntryOutOfTheChainIsRefused()
    {
        using var desktop = new Key();
        using var node = new Key();
        using var other = new Key();

        var wrongPrevious = new Chain();
        wrongPrevious.Add(desktop.First());
        wrongPrevious.Entries.Add(desktop.Sign(Payload(2, "not-the-hash", FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner, desktop)));
        AssertBroken(wrongPrevious, 2, "it does not name the hash of the entry before it");

        var skipped = new Chain();
        skipped.Add(desktop.First());
        skipped.Entries.Add(desktop.Sign(Payload(3, skipped.LastHash, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner, desktop)));
        AssertBroken(skipped, 2, "its sequence is 3, not 2");

        var forged = new Chain();
        forged.Add(desktop.First());
        var good = desktop.Sign(Payload(2, forged.LastHash, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner, desktop));
        forged.Entries.Add(good with { Signature = other.Sign(good with { }).Signature });
        AssertBroken(forged, 2, "its signature does not verify with the key of its signer");

        var notMember = new Chain();
        notMember.Add(desktop.First());
        notMember.Add(other, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
        AssertBroken(notMember, 2, "its signer is not a member of the list");

        var otherOrganization = new Chain();
        otherOrganization.Add(desktop.First());
        otherOrganization.Entries.Add(desktop.Sign(Payload(2, otherOrganization.LastHash, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner, desktop,
            organization: "org-2")));
        AssertBroken(otherOrganization, 2, "it names another organization");

        var notSelfSigned = new Chain();
        notSelfSigned.Entries.Add(other.Sign(Payload(1, null, FleetDeviceListOperations.Add, desktop, FleetDeviceKinds.Controller, null, desktop, organization: null)));
        AssertBroken(notSelfSigned, 1, "the signature of the first device does not verify");
    }

    [TestMethod]
    public void AControllerAddsDevicesOfItsOwnPersonOnlyAndTheLastControllerStays()
    {
        using var desktop = new Key();
        using var node = new Key();
        using var laptop = new Key();

        using var second = new Key();
        using var third = new Key();

        // The owner (the person of the first entry) signs in the first controller device of another person, and nothing more of theirs.
        var otherPerson = new Chain();
        otherPerson.Add(desktop.First());
        otherPerson.Add(desktop, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
        otherPerson.Add(desktop, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, "person-2");
        otherPerson.Add(desktop, FleetDeviceListOperations.Add, second, FleetDeviceKinds.Controller, "person-2");
        AssertBroken(otherPerson, 4, "a controller device adds devices of its own person only");

        var machineOfAnother = new Chain();
        machineOfAnother.Add(desktop.First());
        machineOfAnother.Add(desktop, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
        machineOfAnother.Add(desktop, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Node, "person-2");
        AssertBroken(machineOfAnother, 3, "a controller device adds devices of its own person only");

        // That person's device signs their next ones, never the first device of a third person: only the owner does.
        var notTheOwner = new Chain();
        notTheOwner.Add(desktop.First());
        notTheOwner.Add(desktop, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
        notTheOwner.Add(desktop, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, "person-2");
        notTheOwner.Add(laptop, FleetDeviceListOperations.Add, second, FleetDeviceKinds.Controller, "person-2");
        notTheOwner.Add(laptop, FleetDeviceListOperations.Add, third, FleetDeviceKinds.Controller, "person-3");
        AssertBroken(notTheOwner, 5, "a controller device adds devices of its own person only");

        var lastController = new Chain();
        lastController.Add(desktop.First());
        lastController.Add(desktop, FleetDeviceListOperations.Remove, desktop, null, null);
        AssertBroken(lastController, 2, "it removes the last controller device");

        var readded = new Chain();
        readded.Add(desktop.First());
        readded.Add(desktop, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
        readded.Add(desktop, FleetDeviceListOperations.Remove, node, null, null);
        readded.Add(desktop, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
        AssertBroken(readded, 4, "it adds a device that is or was in the list");
    }

    [TestMethod]
    public void TheOwnersOnlyDeviceNamesItsPerson_ToAddAnotherPersonsFirstDevice_AndANameOnceKnownDoesNotChange()
    {
        using var desktop = new Key();
        using var laptop = new Key();
        using var second = new Key();
        using var node = new Key();

        // Its first entry names no person: without "sowner", the device it adds would make the desktop that person's.
        var unnamed = new Chain();
        unnamed.Add(desktop.First());
        unnamed.Add(desktop, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, "person-2");
        Assert.AreEqual("person-2", FleetDeviceListVerifier.VerifyOrThrow(unnamed.Entries, Organization).Members[desktop.Id].OwnerId, "the rule an older list follows");

        // Named, the desktop is the owner's and adds the first device of the other person; that device adds its next ones.
        var named = new Chain();
        named.Add(desktop.First());
        named.Add(desktop.Sign(Payload(2, named.LastHash, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, "person-2", desktop) with { SignerOwnerId = Owner }));
        named.Add(laptop, FleetDeviceListOperations.Add, second, FleetDeviceKinds.Controller, "person-2");
        var state = FleetDeviceListVerifier.VerifyOrThrow(named.Entries, Organization);
        Assert.AreEqual((Owner, "person-2", "person-2"), (state.Members[desktop.Id].OwnerId, state.Members[laptop.Id].OwnerId, state.Members[second.Id].OwnerId));
        named.Add(desktop, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
        Assert.IsTrue(FleetDeviceListVerifier.Verify(named.Entries, Organization).IsValid, "the desktop goes on adding the owner's devices");

        // Once known, a signer's person stays: an entry that names another is refused.
        var renamed = new Chain();
        renamed.Add(desktop.First());
        renamed.Add(desktop, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
        renamed.Add(desktop.Sign(Payload(3, renamed.LastHash, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, "person-2", desktop) with { SignerOwnerId = "person-2" }));
        AssertBroken(renamed, 3, "it names another person for its signer");

        // An entry without the field reads as it always did: the payload text of an older device does not change.
        StringAssert.DoesNotMatch(FleetDeviceListEntries.SerializePayload(Payload(2, "h", FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, Owner, desktop)),
            new System.Text.RegularExpressions.Regex("sowner"));
    }

    [TestMethod]
    public void ARecoveryKeySignsInANewControllerOfItsPersonAndNothingElse()
    {
        using var desktop = new Key();
        using var recovery = new Key();
        using var node = new Key();
        using var laptop = new Key();
        using var other = new Key();
        Chain WithRecovery()
        {
            var chain = new Chain();
            chain.Add(desktop.First());
            chain.Add(desktop, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
            chain.Add(desktop, FleetDeviceListOperations.Add, recovery, FleetDeviceKinds.Recovery, Owner);
            return chain;
        }

        // Every device lost: the recovery key adds a new controller of its person, which may then remove the lost one.
        var recovered = WithRecovery();
        recovered.Add(recovery, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, Owner);
        recovered.Add(laptop, FleetDeviceListOperations.Remove, desktop, null, null);
        var state = FleetDeviceListVerifier.VerifyOrThrow(recovered.Entries, Organization);
        Assert.IsTrue(state.IsController(laptop.Id));
        Assert.IsFalse(state.IsController(recovery.Id), "a recovery key is no controller device");
        Assert.AreEqual(FleetDeviceKinds.Recovery, state.Members[recovery.Id].Kind);

        var node2 = WithRecovery();
        node2.Add(recovery, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Node, Owner);
        AssertBroken(node2, 4, "a recovery key adds controller devices only");
        var removes = WithRecovery();
        removes.Add(recovery, FleetDeviceListOperations.Remove, node, null, null);
        AssertBroken(removes, 4, "a recovery key removes no device");
        var forOther = WithRecovery();
        forOther.Add(recovery, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, "person-2");
        AssertBroken(forOther, 4, "a controller device adds devices of its own person only");
        var second = WithRecovery();
        second.Add(desktop, FleetDeviceListOperations.Add, other, FleetDeviceKinds.Recovery, Owner);
        AssertBroken(second, 4, "the person has a recovery key already: the old one leaves the list first");

        // A new recovery key once the old one left: the old one signs nothing more.
        var replaced = WithRecovery();
        replaced.Add(desktop, FleetDeviceListOperations.Remove, recovery, null, null);
        replaced.Add(desktop, FleetDeviceListOperations.Add, other, FleetDeviceKinds.Recovery, Owner);
        Assert.AreEqual(5L, FleetDeviceListVerifier.VerifyOrThrow(replaced.Entries, Organization).Sequence);
        replaced.Add(recovery, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, Owner);
        AssertBroken(replaced, 6, "its signer is not a member of the list");
    }

    [TestMethod]
    public void AnEmptyListDoesNotVerify()
    {
        var exception = Assert.ThrowsExactly<FleetDeviceListException>(() => FleetDeviceListVerifier.VerifyOrThrow([], Organization));
        Assert.AreEqual(1L, exception.BrokenAt);
    }

    private static void AssertBroken(Chain chain, long at, string reason)
    {
        var verification = FleetDeviceListVerifier.Verify(chain.Entries, Organization);
        Assert.IsFalse(verification.IsValid);
        Assert.AreEqual((at, reason), (verification.BrokenAt, verification.Reason));
        Assert.AreEqual(at - 1, verification.State.Sequence, "the state is the list up to the last entry that holds");
        var exception = Assert.ThrowsExactly<FleetDeviceListException>(() => FleetDeviceListVerifier.VerifyOrThrow(chain.Entries, Organization));
        StringAssert.Contains(exception.Message, reason);
    }

    private static FleetDeviceListEntryPayload Payload(long sequence, string? previous, string operation, Key device, string? kind, string? owner, Key signer,
        string? organization = Organization)
        => new(1, organization, sequence, previous, operation, device.Id, operation == FleetDeviceListOperations.Add ? device.PublicKey : null, kind, owner, signer.Id, Time);

    private sealed class Chain
    {
        public List<FleetSignedDeviceListEntry> Entries { get; } = [];

        public string? LastHash => Entries.Count == 0 ? null : FleetDeviceListEntries.Hash(Entries[^1]);

        public void Add(FleetSignedDeviceListEntry entry) => Entries.Add(entry);

        public void Add(Key signer, string operation, Key device, string? kind, string? owner)
            => Entries.Add(signer.Sign(Payload(Entries.Count + 1, LastHash, operation, device, kind, owner, signer)));
    }

    private sealed class Key : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public Key()
        {
            PublicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
            FleetDevicePublicKey.TryParse(PublicKey, out var parsed);
            Id = parsed!.DeviceId;
        }

        public string PublicKey { get; }

        public string Id { get; }

        public FleetSignedDeviceListEntry First()
            => Sign(new FleetDeviceListEntryPayload(1, null, 1, null, FleetDeviceListOperations.Add, Id, PublicKey, FleetDeviceKinds.Controller, null, Id, Time));

        public FleetSignedDeviceListEntry Sign(FleetDeviceListEntryPayload payload)
        {
            var text = FleetDeviceListEntries.SerializePayload(payload);
            return Sign(new FleetSignedDeviceListEntry(text, string.Empty));
        }

        public FleetSignedDeviceListEntry Sign(FleetSignedDeviceListEntry entry)
            => entry with
            {
                Signature = Base64Url.EncodeToString(_key.SignData(FleetDeviceListEntries.GetSignedBytes(entry.Payload), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            };

        public void Dispose() => _key.Dispose();
    }
}
