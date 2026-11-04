using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Fleet.Protocol.Tests;

/// <summary>
/// Versions of the signed documents (<c>doc/fleet/protocol.md</c>, versions): within 4.x, fields are only added, and a
/// verifier of a previous minor keeps verifying what a newer one signs. A device replays the device list tolerantly: an
/// authentic entry its rules do not understand stays in the chain without effect, never breaks it; the access document
/// ignores the fields it does not know. What a device signs and a server keeps stays strict.
/// </summary>
[TestClass]
public sealed class FleetVersionSkewTests
{
    private const string GoldenFile = "fleet-documents-newer-minor.json";
    private const string WriteVariable = "CODEALTA_WRITE_FLEET_GOLDEN";

    [TestMethod]
    public void ADeviceListAndAnAccessDocumentMadeByANewerMinorVerifyWithTheseRules()
    {
        var golden = Golden.Read(Path.Combine(AppContext.BaseDirectory, "Golden", GoldenFile));

        // The newer list: additive fields, a device of a class these rules do not know (and its removal), an operation they
        // do not know, the owner signing another person's first device, and that person's own machine after it.
        var tolerant = FleetDeviceListVerifier.Verify(golden.DeviceList, golden.Organization, FleetDeviceListMode.Tolerant);
        Assert.IsTrue(tolerant.IsValid, tolerant.Reason);
        var devices = tolerant.State;
        Assert.AreEqual((7L, 6L), (devices.Sequence, devices.LastRemovalSequence), "the removal of a device these rules never knew still counts");
        CollectionAssert.AreEquivalent(new[] { golden.Ids["founder"], golden.Ids["node"], golden.Ids["ada"], golden.Ids["adaNode"] }, devices.Members.Keys.ToArray());
        Assert.AreEqual(("ada", FleetDeviceKinds.Controller), (devices.Members[golden.Ids["ada"]].OwnerId, devices.Members[golden.Ids["ada"]].Kind));
        CollectionAssert.AreEqual(new[] { 3L, 4L }, devices.Skipped.Select(static entry => entry.Sequence).ToArray());
        Assert.IsFalse(devices.Members.ContainsKey(golden.Ids["teamMachine"]), "a device these rules do not understand is never trusted");
        Assert.AreEqual(golden.Keys["node"], devices.Members[golden.Ids["node"]].Key.Text, "an unknown operation changed no key");

        // What a device signs or a server keeps must be understood: the strict rules stop at the first entry they do not know.
        var strict = FleetDeviceListVerifier.Verify(golden.DeviceList, golden.Organization, FleetDeviceListMode.Strict);
        Assert.AreEqual((false, 3L), (strict.IsValid, strict.BrokenAt));

        // The access document: fields these rules do not know are read past; the chain, its signers and what it says verify.
        var access = FleetAccessDocuments.Verify(golden.AccessDocuments, devices, golden.Organization);
        Assert.IsTrue(access.IsValid, access.Reason);
        Assert.AreEqual(2L, access.State.Number);
        CollectionAssert.AreEquivalent(new[] { "alex" }, access.State.Owners.ToArray());
        CollectionAssert.AreEquivalent(new[] { "ada" }, access.State.Signers.ToArray());
        CollectionAssert.AreEquivalent(new[] { "eve" }, access.State.Suspended.ToArray());
        Assert.AreEqual(FleetTeamRoles.Maintainer, FleetAccessState.RoleIn(access.State.Teams["team-build"], "ada"));
    }

    [TestMethod]
    public void ATolerantReplayKeepsAnEntryItDoesNotUnderstandWithoutEffect_ButNeverAForgedOne()
    {
        using var founder = new Key();
        using var stranger = new Key();
        using var unknown = new Key();
        using var laptop = new Key();
        var list = new List<FleetSignedDeviceListEntry> { founder.Sign(founder.FirstPayload()) };
        list.Add(founder.Sign(Entry(2, list, "add", unknown, "future-kind", "alex", founder)));
        list.Add(founder.Sign(Entry(3, list, "add", laptop, FleetDeviceKinds.Controller, "alex", founder)));
        var state = FleetDeviceListVerifier.VerifyOrThrow(list, "org", FleetDeviceListMode.Tolerant);
        Assert.IsTrue(state.IsController(laptop.Id));
        Assert.IsTrue(state.Unrecognized.Contains(unknown.Id));

        // The same device cannot come back as a known one: it was in the list.
        var again = list.ToList();
        again.Add(founder.Sign(Entry(4, again, "add", unknown, FleetDeviceKinds.Controller, "alex", founder)));
        Assert.IsFalse(FleetDeviceListVerifier.Verify(again, "org", FleetDeviceListMode.Tolerant).IsValid);

        // Authenticity is never relaxed: an unknown operation signed by a device that is not a member breaks the chain.
        var forged = list.ToList();
        forged.Add(stranger.Sign(Entry(4, forged, "rotate", laptop, null, null, stranger)));
        Assert.AreEqual((false, "its signer is not a member of the list"), (FleetDeviceListVerifier.Verify(forged, "org", FleetDeviceListMode.Tolerant).IsValid,
            FleetDeviceListVerifier.Verify(forged, "org", FleetDeviceListMode.Tolerant).Reason));
    }

    /// <summary>
    /// Writes the golden file as a newer minor would sign it. Run by hand with <c>CODEALTA_WRITE_FLEET_GOLDEN=1</c> only when
    /// the file must change: what an older verifier accepts must not change after a release.
    /// </summary>
    [TestMethod]
    public void WriteTheGoldenFile()
    {
        if (Environment.GetEnvironmentVariable(WriteVariable) != "1") Assert.Inconclusive($"Set {WriteVariable}=1 to write {GoldenFile}.");
        using var founder = new Key();
        using var node = new Key();
        using var teamMachine = new Key();
        using var rotated = new Key();
        using var ada = new Key();
        using var adaNode = new Key();
        const string Organization = "org-golden";
        var list = new List<FleetSignedDeviceListEntry> { founder.Sign(founder.FirstPayload()) };
        list.Add(founder.Sign(Entry(2, list, "add", node, FleetDeviceKinds.Node, "alex", founder, Organization,
            writer => { writer.WriteStartArray("labels"); writer.WriteStringValue("gpu"); writer.WriteEndArray(); writer.WriteBoolean("sandboxRequired", true); })));
        list.Add(founder.Sign(Entry(3, list, "add", teamMachine, "team-machine", null, founder, Organization, writer => writer.WriteString("team", "team-build"))));
        list.Add(founder.Sign(Entry(4, list, "rotate", node, null, "alex", founder, Organization, writer => writer.WriteString("newKey", rotated.PublicKey))));
        list.Add(founder.Sign(Entry(5, list, "add", ada, FleetDeviceKinds.Controller, "ada", founder, Organization, writer => writer.WriteString("invitedBy", "alex"))));
        list.Add(founder.Sign(Entry(6, list, "remove", teamMachine, null, null, founder, Organization, writer => writer.WriteString("why", "retired"))));
        list.Add(ada.Sign(Entry(7, list, "add", adaNode, FleetDeviceKinds.Node, "ada", ada, Organization)));

        var first = founder.SignAccess(Access(Organization, 1, null, founder, suspended: [], writer =>
        {
            writer.WriteStartObject("roles");
            writer.WriteString("ada", "admin");
            writer.WriteEndObject();
            writer.WriteStartObject("retention");
            writer.WriteNumber("days", 30);
            writer.WriteEndObject();
        }));
        var second = ada.SignAccess(Access(Organization, 2, FleetAccessDocuments.Hash(first), ada, suspended: ["eve"], writer => writer.WriteString("note", "Eve left")));

        var path = Path.Combine(SourceFolder(), "Golden", GoldenFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("organization", Organization);
        json.WriteStartObject("ids");
        foreach (var (name, key) in new[] { ("founder", founder), ("node", node), ("teamMachine", teamMachine), ("ada", ada), ("adaNode", adaNode) }) json.WriteString(name, key.Id);
        json.WriteEndObject();
        json.WriteStartObject("keys");
        json.WriteString("node", node.PublicKey);
        json.WriteEndObject();
        json.WriteStartArray("deviceList");
        foreach (var entry in list) { json.WriteStartObject(); json.WriteString("payload", entry.Payload); json.WriteString("sig", entry.Signature); json.WriteEndObject(); }
        json.WriteEndArray();
        json.WriteStartArray("accessDocuments");
        foreach (var document in new[] { first, second }) { json.WriteStartObject(); json.WriteString("payload", document.Payload); json.WriteString("sig", document.Signature); json.WriteEndObject(); }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static string SourceFolder([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    // The text of an entry as its signer writes it, with the fields a newer version may add after the known ones.
    private static string Entry(long sequence, List<FleetSignedDeviceListEntry> before, string operation, Key device, string? kind, string? owner, Key signer,
        string organization = "org", Action<Utf8JsonWriter>? more = null)
        => Write(writer =>
        {
            writer.WriteNumber("v", 1);
            writer.WriteString("org", organization);
            writer.WriteNumber("seq", sequence);
            writer.WriteString("prev", FleetDeviceListEntries.Hash(before[^1]));
            writer.WriteString("op", operation);
            writer.WriteString("device", device.Id);
            if (operation == "add") writer.WriteString("key", device.PublicKey); else writer.WriteNull("key");
            if (kind is null) writer.WriteNull("kind"); else writer.WriteString("kind", kind);
            if (owner is null) writer.WriteNull("owner"); else writer.WriteString("owner", owner);
            writer.WriteString("signer", signer.Id);
            writer.WriteString("time", "2026-11-04T09:00:00+00:00");
            more?.Invoke(writer);
        });

    private static string Access(string organization, long number, string? previous, Key signer, string[] suspended, Action<Utf8JsonWriter> more)
        => Write(writer =>
        {
            writer.WriteNumber("v", 1);
            writer.WriteString("org", organization);
            writer.WriteNumber("number", number);
            if (previous is null) writer.WriteNull("prev"); else writer.WriteString("prev", previous);
            writer.WriteNumber("list", 7);
            writer.WriteStartArray("owners"); writer.WriteStringValue("alex"); writer.WriteEndArray();
            writer.WriteStartArray("signers"); writer.WriteStringValue("ada"); writer.WriteEndArray();
            writer.WriteStartArray("teams");
            writer.WriteStartObject();
            writer.WriteString("id", "team-build");
            writer.WriteStartArray("maintainers"); writer.WriteStringValue("ada"); writer.WriteEndArray();
            writer.WriteStartArray("members"); writer.WriteEndArray();
            writer.WriteBoolean("runsMemberAutomations", false);
            writer.WriteStartArray("labels"); writer.WriteStringValue("ci"); writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartArray("machines"); writer.WriteEndArray();
            writer.WriteStartArray("suspended"); foreach (var id in suspended) writer.WriteStringValue(id); writer.WriteEndArray();
            writer.WriteString("signer", signer.Id);
            writer.WriteString("time", "2026-11-04T09:00:00+00:00");
            more(writer);
        });

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record Golden(string Organization, IReadOnlyDictionary<string, string> Ids, IReadOnlyDictionary<string, string> Keys,
        IReadOnlyList<FleetSignedDeviceListEntry> DeviceList, IReadOnlyList<FleetSignedAccessDocument> AccessDocuments)
    {
        public static Golden Read(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            static Dictionary<string, string> Map(JsonElement element) => element.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value.GetString()!);
            return new Golden(root.GetProperty("organization").GetString()!, Map(root.GetProperty("ids")), Map(root.GetProperty("keys")),
                [.. root.GetProperty("deviceList").EnumerateArray().Select(static item => new FleetSignedDeviceListEntry(item.GetProperty("payload").GetString()!, item.GetProperty("sig").GetString()!))],
                [.. root.GetProperty("accessDocuments").EnumerateArray().Select(static item => new FleetSignedAccessDocument(item.GetProperty("payload").GetString()!, item.GetProperty("sig").GetString()!))]);
        }
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

        public string FirstPayload()
            => FleetDeviceListEntries.SerializePayload(new FleetDeviceListEntryPayload(1, null, 1, null, FleetDeviceListOperations.Add, Id, PublicKey, FleetDeviceKinds.Controller, null, Id,
                new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero)));

        public FleetSignedDeviceListEntry Sign(string payload) => new(payload, SignBytes(FleetDeviceListEntries.GetSignedBytes(payload)));

        public FleetSignedAccessDocument SignAccess(string payload) => new(payload, SignBytes(FleetAccessDocuments.GetSignedBytes(payload)));

        private string SignBytes(byte[] bytes) => Base64Url.EncodeToString(_key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public void Dispose() => _key.Dispose();
    }
}
