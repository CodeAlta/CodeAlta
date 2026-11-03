using System.Text;
using System.Text.Json;

namespace CodeAlta.Fleet.Protocol.Tests;

/// <summary>The formats devices and the server sign and chain: signed requests and entries of the device list.</summary>
[TestClass]
public sealed class FleetServerFormatsTests
{
    [TestMethod]
    public void TheSignedBytesOfARequestNameEachFieldOnItsLine()
    {
        var bytes = FleetSignedRequest.GetSignedBytes("post", "/api/v1/devices?limit=1", 1730000000000, "nonce", FleetSignedRequest.HashBody([]), "device");
        Assert.AreEqual("codealta-fleet-request/1\nPOST\n/api/v1/devices?limit=1\n1730000000000\nnonce\n47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU\ndevice",
            Encoding.UTF8.GetString(bytes));
        Assert.ThrowsExactly<ArgumentException>(() => FleetSignedRequest.GetSignedBytes("GET", "/a\n/b", 1, "nonce", "hash", "device"));
        Assert.ThrowsExactly<ArgumentException>(() => FleetSignedRequest.GetSignedBytes("GET", "/a", 1, "", "hash", "device"));
    }

    [TestMethod]
    public void NoncesAreRandomBase64Url()
    {
        var first = FleetSignedRequest.CreateNonce();
        Assert.IsTrue(FleetSignedRequest.IsValidNonce(first));
        Assert.AreNotEqual(first, FleetSignedRequest.CreateNonce());
        Assert.IsFalse(FleetSignedRequest.IsValidNonce("short"));
        Assert.IsFalse(FleetSignedRequest.IsValidNonce(new string('+', 30)));
    }

    [TestMethod]
    public void AnEntryOfTheDeviceListReadsBackAndChainsByItsSignature()
    {
        var payload = new FleetDeviceListEntryPayload(1, "org", 2, "prev", FleetDeviceListOperations.Add, "device", "key", FleetDeviceKinds.Node, "owner", "signer",
            new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero));
        var text = FleetDeviceListEntries.SerializePayload(payload);
        Assert.IsTrue(FleetDeviceListEntries.TryParsePayload(text, out var read));
        Assert.AreEqual(payload, read);
        using (var document = JsonDocument.Parse(text))
        {
            Assert.AreEqual("add", document.RootElement.GetProperty("op").GetString());
        }

        StringAssert.StartsWith(Encoding.UTF8.GetString(FleetDeviceListEntries.GetSignedBytes(text)), "codealta-device-list-entry/1\n{");
        var hash = FleetDeviceListEntries.Hash(new FleetSignedDeviceListEntry(text, "sig-a"));
        Assert.AreEqual(hash, FleetDeviceListEntries.Hash(new FleetSignedDeviceListEntry(text, "sig-a")));
        Assert.AreNotEqual(hash, FleetDeviceListEntries.Hash(new FleetSignedDeviceListEntry(text, "sig-b")));

        Assert.IsFalse(FleetDeviceListEntries.TryParsePayload("{\"v\":1}", out _), "An entry without its operation, device and signer does not read.");
        Assert.IsFalse(FleetDeviceListEntries.TryParsePayload("not json", out _));
    }
}
