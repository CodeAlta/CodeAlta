using System.Buffers.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The metadata key of a person: titles sealed with AES-256-GCM, bound to their session and key, and the key wrapped to
/// one device with ECDH-ES P-256, HKDF and AES-256-GCM, which only that device's key opens.
/// </summary>
[TestClass]
public sealed class FleetMetadataKeyTests
{
    [TestMethod]
    public void ATitleSealedForASessionOpensForItOnlyWithItsKey()
    {
        var key = FleetMetadataKey.Generate();
        var sealedText = key.Seal("s1", new FleetSessionMetadata("Fix the parser"));
        Assert.IsFalse(sealedText.Contains("parser", StringComparison.Ordinal), "the title is not in clear");
        using (var document = JsonDocument.Parse(sealedText))
        {
            Assert.AreEqual(("a256gcm", key.Id), (document.RootElement.GetProperty("alg").GetString(), document.RootElement.GetProperty("kid").GetString()));
        }

        Assert.IsTrue(key.TryOpen("s1", sealedText, out var opened));
        Assert.AreEqual("Fix the parser", opened!.Title);
        Assert.AreNotEqual(sealedText, key.Seal("s1", new FleetSessionMetadata("Fix the parser")), "a nonce of its own each time");

        // Moved to another session by the server, sealed by another key, changed, or of an unknown algorithm: it does not open.
        Assert.IsFalse(key.TryOpen("s2", sealedText, out _));
        Assert.IsFalse(FleetMetadataKey.Generate().TryOpen("s1", sealedText, out _));
        var other = new FleetMetadataKey(key.Id, new byte[FleetMetadataKey.KeyLength]);
        Assert.IsFalse(other.TryOpen("s1", sealedText, out _), "the same id with other bytes");
        Assert.IsFalse(key.TryOpen("s1", sealedText.Replace("a256gcm", "a128gcm", StringComparison.Ordinal), out _));
        Assert.IsFalse(key.TryOpen("s1", Tamper(sealedText, "ct"), out _));
        Assert.IsFalse(key.TryOpen("s1", "not json", out _));
        Assert.IsFalse(key.TryOpen("s1", null, out _));
    }

    [TestMethod]
    public void ABlindIndexIsTheSameOnEveryDeviceOfThePerson_AndRevealsNeitherTheValueNorAnotherIndex()
    {
        var bytes = new byte[FleetMetadataKey.KeyLength];
        bytes[0] = 7;
        var onDesktop = new FleetMetadataKey("k1", bytes);
        var onNode = new FleetMetadataKey("k1", bytes);
        var identity = "repo:github.com/codealta/codealta";

        var index = FleetProjectKeys.Of(onDesktop, identity);
        Assert.AreEqual(index, FleetProjectKeys.Of(onNode, identity), "two devices of the person give one key to one project");
        Assert.AreEqual(22, index.Length);
        Assert.IsFalse(index.Contains("codealta", StringComparison.OrdinalIgnoreCase));
        Assert.AreNotEqual(index, FleetProjectKeys.Of(onDesktop, "repo:github.com/codealta/other"));
        Assert.AreNotEqual(index, onDesktop.BlindIndex("session", identity), "another purpose gives another index");
        Assert.AreNotEqual(index, FleetProjectKeys.Of(FleetMetadataKey.Generate(), identity), "another person's key gives another index");
    }

    [TestMethod]
    public void AKeyWrappedToADeviceOpensWithThatDeviceOnly()
    {
        using var devices = new Devices();
        var list = devices.List();
        var key = FleetMetadataKey.Generate();
        var grant = new FleetKeyGrant("org", "p1", 1, list.Sequence);
        var wrapped = FleetKeyWrap.Wrap(key, PublicKey(devices.Node), grant, devices.Laptop);
        using (var document = JsonDocument.Parse(wrapped))
        {
            Assert.AreEqual((FleetKeyWrap.Algorithm, key.Id, devices.Node.DeviceId, devices.Laptop.DeviceId),
                (document.RootElement.GetProperty("alg").GetString(), document.RootElement.GetProperty("kid").GetString(), document.RootElement.GetProperty("to").GetString(),
                    document.RootElement.GetProperty("signer").GetString()));
        }

        var opened = FleetKeyWrap.Unwrap(wrapped, key.Id, 1, devices.Node, list);
        Assert.AreEqual((key.Id, grant), (opened.Id, opened.Grant));
        Assert.IsTrue(opened.TryOpen("s1", key.Seal("s1", new FleetSessionMetadata("Title")), out var metadata));
        Assert.AreEqual("Title", metadata!.Title);

        // Another device, another key id, a changed copy: refused.
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(wrapped, key.Id, 1, devices.Laptop, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(wrapped, "other", 1, devices.Node, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(Tamper(wrapped, "ct"), key.Id, 1, devices.Node, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(Tamper(wrapped, "epk"), key.Id, 1, devices.Node, list));
        // A copy made for the laptop and relabelled for the node does not open: the device is bound into the key derivation and the signature.
        var forLaptop = FleetKeyWrap.Wrap(key, PublicKey(devices.Laptop), grant, devices.Laptop).Replace(devices.Laptop.DeviceId + "\"", devices.Node.DeviceId + "\"", StringComparison.Ordinal);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(forLaptop, key.Id, 1, devices.Node, list));
    }

    [TestMethod]
    public void ACopyIsTakenOnlyWhenAControllerOfThePersonSignedItForThisEpochOrganizationAndList()
    {
        using var devices = new Devices();
        var list = devices.List();
        var key = FleetMetadataKey.Generate();
        var grant = new FleetKeyGrant("org", "p1", 2, list.Sequence);
        var node = PublicKey(devices.Node);

        // The server makes a key of its own and wraps it to the node: with no signature, or signed by a key of its own.
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(Without(FleetKeyWrap.Wrap(key, node, grant, devices.Laptop), "sig", "signer"), key.Id, 2, devices.Node, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(FleetKeyWrap.Wrap(key, node, grant, devices.Stranger), key.Id, 2, devices.Node, list));
        // A member that is not a controller device gives no key, nor does a controller that left the list.
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(FleetKeyWrap.Wrap(key, node, grant, devices.Other), key.Id, 2, devices.Node, list));
        // A copy given for another epoch than the one listed, relabelled, or for another person, organization or a list not verified here: refused.
        var genuine = FleetKeyWrap.Wrap(key, node, grant, devices.Laptop);
        Assert.AreEqual(2, FleetKeyWrap.Unwrap(genuine, key.Id, 2, devices.Node, list).Grant!.Epoch);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(genuine, key.Id, 3, devices.Node, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(genuine.Replace("\"epoch\":2", "\"epoch\":3", StringComparison.Ordinal), key.Id, 3, devices.Node, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(FleetKeyWrap.Wrap(key, node, grant with { PersonId = "p2" }, devices.Laptop), key.Id, 2, devices.Node, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(FleetKeyWrap.Wrap(key, node, grant with { OrganizationId = "other" }, devices.Laptop), key.Id, 2, devices.Node, list));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(FleetKeyWrap.Wrap(key, node, grant with { DeviceListSequence = list.Sequence + 1 }, devices.Laptop), key.Id, 2,
            devices.Node, list));
        // The fields of a grant are signed one per line: none holds a line break.
        Assert.ThrowsExactly<ArgumentException>(() => FleetKeyWrap.Wrap(key, node, grant with { PersonId = "p1\n2" }, devices.Laptop));
        // A removal after the key: the list knows when, so that the key seals nothing more.
        Assert.AreEqual(4L, devices.List(removeOther: true).LastRemovalSequence);
        Assert.AreEqual(0L, list.LastRemovalSequence);
    }

    // Removes fields of a JSON object.
    private static string Without(string json, params string[] fields)
    {
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject().Where(property => !fields.Contains(property.Name))) property.WriteTo(writer);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A laptop (the first controller device, of person p1), a node and another node of that person, and a stranger outside the list.</summary>
    private sealed class Devices : IDisposable
    {
        private readonly TestFolder _folder = new();

        public Devices()
        {
            Laptop = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "laptop"));
            Node = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "node"));
            Other = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "other"));
            Stranger = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "stranger"));
        }

        public DeviceIdentity Laptop { get; }

        public DeviceIdentity Node { get; }

        public DeviceIdentity Other { get; }

        public DeviceIdentity Stranger { get; }

        public FleetDeviceListState List(bool removeOther = false)
        {
            var entries = new List<FleetSignedDeviceListEntry> { Entry(1, null, FleetDeviceListOperations.Add, Laptop, FleetDeviceKinds.Controller, null, null) };
            entries.Add(Entry(2, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add, Node, FleetDeviceKinds.Node, "org", "p1"));
            entries.Add(Entry(3, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add, Other, FleetDeviceKinds.Node, "org", "p1"));
            if (removeOther) entries.Add(Entry(4, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Remove, Other, null, "org", null));
            return FleetDeviceListVerifier.VerifyOrThrow(entries, "org");
        }

        private FleetSignedDeviceListEntry Entry(long sequence, string? previous, string operation, DeviceIdentity device, string? kind, string? organization, string? owner)
        {
            var add = operation == FleetDeviceListOperations.Add;
            var payload = FleetDeviceListEntries.SerializePayload(new FleetDeviceListEntryPayload(1, organization, sequence, previous, operation, device.DeviceId,
                add ? Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()) : null, kind, owner, Laptop.DeviceId, new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero)));
            return new FleetSignedDeviceListEntry(payload, Base64Url.EncodeToString(Laptop.SignData(FleetDeviceListEntries.GetSignedBytes(payload))));
        }

        public void Dispose()
        {
            Laptop.Dispose();
            Node.Dispose();
            Other.Dispose();
            Stranger.Dispose();
            _folder.Dispose();
        }
    }

    private static FleetDevicePublicKey PublicKey(DeviceIdentity identity)
    {
        Assert.IsTrue(FleetDevicePublicKey.TryParse(Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo()), out var key));
        return key;
    }

    // Changes one character of a base64url field.
    private static string Tamper(string json, string field)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement.GetProperty(field).GetString()!;
        var changed = value[..10] + (value[10] == 'A' ? 'B' : 'A') + value[11..];
        return json.Replace(value, changed, StringComparison.Ordinal);
    }
}
