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
    public void AKeyWrappedToADeviceOpensWithThatDeviceOnly()
    {
        using var folder = new TestFolder();
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "laptop"));
        using var node = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "node"));
        var key = FleetMetadataKey.Generate();
        var wrapped = FleetKeyWrap.Wrap(key, PublicKey(node));
        using (var document = JsonDocument.Parse(wrapped))
        {
            Assert.AreEqual((FleetKeyWrap.Algorithm, key.Id, node.DeviceId),
                (document.RootElement.GetProperty("alg").GetString(), document.RootElement.GetProperty("kid").GetString(), document.RootElement.GetProperty("to").GetString()));
        }

        var opened = FleetKeyWrap.Unwrap(wrapped, key.Id, node);
        Assert.AreEqual(key.Id, opened.Id);
        Assert.IsTrue(opened.TryOpen("s1", key.Seal("s1", new FleetSessionMetadata("Title")), out var metadata));
        Assert.AreEqual("Title", metadata!.Title);

        // Another device, another key id, a changed copy: refused.
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(wrapped, key.Id, laptop));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(wrapped, "other", node));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(Tamper(wrapped, "ct"), key.Id, node));
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(Tamper(wrapped, "epk"), key.Id, node));
        // A copy made for the laptop and relabelled for the node does not open: the device is bound into the key derivation.
        var forLaptop = FleetKeyWrap.Wrap(key, PublicKey(laptop)).Replace(laptop.DeviceId, node.DeviceId, StringComparison.Ordinal);
        Assert.ThrowsExactly<InvalidDataException>(() => FleetKeyWrap.Unwrap(forLaptop, key.Id, node));
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
