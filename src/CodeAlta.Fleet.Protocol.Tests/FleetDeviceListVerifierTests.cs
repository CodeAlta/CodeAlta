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

        var otherPerson = new Chain();
        otherPerson.Add(desktop.First());
        otherPerson.Add(desktop, FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, Owner);
        otherPerson.Add(desktop, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, "person-2");
        AssertBroken(otherPerson, 3, "a controller device adds devices of its own person only");

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
