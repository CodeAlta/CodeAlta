using System.Buffers.Text;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The signed versions of automations: signed by a controller device of the person who owns the machine, for that machine
/// and organization, and what they do sealed for the machine and the person's controller devices only, so that a server
/// neither changes a version nor reads it.
/// </summary>
[TestClass]
public sealed class FleetAutomationSigningTests
{
    private const string Definition = "[automations.daily-1]\nname = \"Daily\"\nprompt = \"Summarize the day.\"\n";

    [TestMethod]
    public void AVersionSignedByAControllerOfTheOwnerVerifiesForItsMachineOnly()
    {
        using var devices = new Devices();
        var list = devices.List();
        var signed = FleetAutomationSigning.Sign(devices.Laptop, Version(devices.Node.DeviceId, 3));

        Assert.IsTrue(FleetAutomationVersionDocuments.TryVerify(signed, list, "org", devices.Node.DeviceId, out var version, out var reason), reason);
        Assert.AreEqual(("daily-1", 3L, devices.Laptop.DeviceId), (version.AutomationId, version.Number, version.SignerDeviceId));

        AssertRefused(signed, list, "org", devices.Other.DeviceId, "another machine");
        AssertRefused(signed, list, "org-2", devices.Node.DeviceId, "another organization");
        // The server changes what it holds, or drops the signature.
        AssertRefused(signed with { Payload = signed.Payload.Replace("\"enabled\":true", "\"enabled\":false", StringComparison.Ordinal) }, list, "org", devices.Node.DeviceId,
            "signature does not verify");
        AssertRefused(signed with { Signature = string.Empty }, list, "org", devices.Node.DeviceId, "does not read");
        AssertRefused(null, list, "org", devices.Node.DeviceId, "does not read");
        // A device outside the list, a node of the list, or a controller the list removed signs nothing.
        AssertRefused(FleetAutomationSigning.Sign(devices.Stranger, Version(devices.Node.DeviceId, 3)), list, "org", devices.Node.DeviceId, "not signed by a controller device");
        AssertRefused(FleetAutomationSigning.Sign(devices.Other, Version(devices.Node.DeviceId, 3)), list, "org", devices.Node.DeviceId, "not signed by a controller device");
        var tablet = FleetAutomationSigning.Sign(devices.Tablet, Version(devices.Node.DeviceId, 4));
        Assert.IsTrue(FleetAutomationVersionDocuments.TryVerify(tablet, list, "org", devices.Node.DeviceId, out _, out reason), reason);
        AssertRefused(tablet, devices.List(removeTablet: true), "org", devices.Node.DeviceId, "not signed by a controller device");
        // A stranger that claims the laptop as its signer, with its own key.
        var claimed = FleetAutomationSigning.Sign(devices.Stranger, Version(devices.Node.DeviceId, 3));
        var forged = claimed with { Payload = claimed.Payload.Replace(devices.Stranger.DeviceId, devices.Laptop.DeviceId, StringComparison.Ordinal) };
        AssertRefused(forged, list, "org", devices.Node.DeviceId, "key the device list holds");
    }

    [TestMethod]
    public void ARunRequestVerifiesForItsMachine_SignedByAControllerOfTheOwner_WithinMinutes()
    {
        using var devices = new Devices();
        var list = devices.List();
        var now = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
        FleetSignedAutomationRunRequest Sign(DeviceIdentity signer, string machine, DateTimeOffset time)
            => FleetAutomationSigning.SignRun(signer, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, "org", "daily-1", machine, "run-1", string.Empty,
                string.Empty, time));
        string Refused(FleetSignedAutomationRunRequest? signed, FleetDeviceListState? other = null, string machine = "")
        {
            Assert.IsFalse(FleetAutomationRunRequestDocuments.TryVerify(signed, other ?? list, "org", machine.Length > 0 ? machine : devices.Node.DeviceId, now, out _, out var reason));
            return reason;
        }

        var signed = Sign(devices.Laptop, devices.Node.DeviceId, now.AddMinutes(-2));
        Assert.IsTrue(FleetAutomationRunRequestDocuments.TryVerify(signed, list, "org", devices.Node.DeviceId, now, out var request, out var why), why);
        Assert.AreEqual(("daily-1", "run-1", devices.Laptop.DeviceId), (request.AutomationId, request.RunId, request.SignerDeviceId));

        StringAssert.Contains(Refused(signed, machine: devices.Other.DeviceId), "another machine");
        StringAssert.Contains(Refused(Sign(devices.Laptop, devices.Node.DeviceId, now.AddHours(-1))), "too long ago");
        StringAssert.Contains(Refused(Sign(devices.Laptop, devices.Node.DeviceId, now.AddHours(1))), "too long ago");
        StringAssert.Contains(Refused(Sign(devices.Stranger, devices.Node.DeviceId, now)), "not signed by a controller device");
        StringAssert.Contains(Refused(Sign(devices.Other, devices.Node.DeviceId, now)), "not signed by a controller device");
        StringAssert.Contains(Refused(Sign(devices.Tablet, devices.Node.DeviceId, now), devices.List(removeTablet: true)), "not signed by a controller device");
        StringAssert.Contains(Refused(signed with { Payload = signed.Payload.Replace("run-1", "run-2", StringComparison.Ordinal) }), "signature does not verify");
        StringAssert.Contains(Refused(null), "does not read");
    }

    // What a machine must obey for safety is never a field an older machine ignores (doc/fleet/protocol.md, "Versions"): a try,
    // and a version whose definition decides what its runs do without a person, are of a format an older machine refuses
    // (it reads version 1 only), so that it neither runs a try for real nor runs an automation without its autonomy.
    [TestMethod]
    public void ATry_AndAVersionWithAutonomy_AreOfAFormatAnOlderMachineRefuses()
    {
        using var devices = new Devices();
        var list = devices.List();
        var now = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
        FleetSignedAutomationRunRequest Sign(int version, string? action)
            => FleetAutomationSigning.SignRun(devices.Laptop, new FleetAutomationRunRequest(version, "org", "daily-1", devices.Node.DeviceId, "run-1", string.Empty,
                string.Empty, now) { Action = action });

        // An older machine reads version 1 only.
        Assert.AreEqual(1, FleetAutomationRunRequest.VersionFor(null));
        Assert.AreEqual(1, FleetAutomationRunRequest.VersionFor(FleetAutomationRunRequest.TakeOverAction));
        Assert.AreEqual(2, FleetAutomationRunRequest.VersionFor(FleetAutomationRunRequest.TryAction));
        var tried = Sign(FleetAutomationRunRequest.VersionFor(FleetAutomationRunRequest.TryAction), FleetAutomationRunRequest.TryAction);
        Assert.IsTrue(FleetAutomationRunRequestDocuments.TryVerify(tried, list, "org", devices.Node.DeviceId, now, out var request, out var reason), reason);
        Assert.AreEqual(FleetAutomationRunRequest.TryAction, request.Action);
        // A try in the format of a run, or a run in the format of a try, does not read: one is never taken for the other.
        Assert.ThrowsExactly<ArgumentException>(() => Sign(1, FleetAutomationRunRequest.TryAction));
        Assert.ThrowsExactly<ArgumentException>(() => Sign(2, null));
        Assert.IsFalse(FleetAutomationRunRequestDocuments.TryRead(tried.Payload.Replace("\"v\":2", "\"v\":1", StringComparison.Ordinal), out _, out reason));
        StringAssert.Contains(reason, "try");

        Assert.AreEqual(1, FleetAutomationVersion.VersionFor(autonomy: false));
        Assert.AreEqual(2, FleetAutomationVersion.VersionFor(autonomy: true));
        var guarded = FleetAutomationSigning.Sign(devices.Laptop, Version(devices.Node.DeviceId, 5) with { Version = FleetAutomationVersion.VersionFor(autonomy: true) });
        Assert.IsTrue(FleetAutomationVersionDocuments.TryVerify(guarded, list, "org", devices.Node.DeviceId, out var version, out reason), reason);
        Assert.AreEqual(2, version.Version);
        Assert.ThrowsExactly<ArgumentException>(() => FleetAutomationSigning.Sign(devices.Laptop, Version(devices.Node.DeviceId, 6) with { Version = 3 }));
    }

    [TestMethod]
    public void OnADirectChannelTheVersionVerifiesWithTheKeyItNames()
    {
        using var devices = new Devices();
        var signed = FleetAutomationSigning.Sign(devices.Laptop, Version(devices.Node.DeviceId, 1) with { OrganizationId = null });
        Assert.IsTrue(FleetAutomationVersionDocuments.TryVerify(signed, devices.Node.DeviceId, out var version, out var key, out var reason), reason);
        Assert.AreEqual((devices.Laptop.DeviceId, Definition), (key.DeviceId, version.Definition));

        Assert.IsFalse(FleetAutomationVersionDocuments.TryVerify(signed, devices.Other.DeviceId, out _, out _, out reason));
        StringAssert.Contains(reason, "another machine");
        // The key of another device under the name of the laptop.
        var stranger = FleetAutomationSigning.Sign(devices.Stranger, Version(devices.Node.DeviceId, 1));
        var renamed = stranger with { Payload = stranger.Payload.Replace(devices.Stranger.DeviceId, devices.Laptop.DeviceId, StringComparison.Ordinal) };
        Assert.IsFalse(FleetAutomationVersionDocuments.TryVerify(renamed, devices.Node.DeviceId, out _, out _, out reason));
        StringAssert.Contains(reason, "not the key of its signer");
    }

    [TestMethod]
    public void AVersionHoldsItsContentInClearOrSealedAndARemovalHoldsNone()
    {
        Assert.IsNull(FleetAutomationVersionDocuments.Check(Version("node", 1)));
        StringAssert.Contains(FleetAutomationVersionDocuments.Check(Version("node", 0)), "starts at 1");
        StringAssert.Contains(FleetAutomationVersionDocuments.Check(Version("node", 1) with { Sealed = "x" }), "either in clear or sealed");
        StringAssert.Contains(FleetAutomationVersionDocuments.Check(Version("node", 1) with { Definition = null }), "either in clear or sealed");
        StringAssert.Contains(FleetAutomationVersionDocuments.Check(Version("node", 1) with { Removed = true }), "holds nothing else");
        Assert.IsNull(FleetAutomationVersionDocuments.Check(Version("node", 1) with { Removed = true, Definition = null }));
        StringAssert.Contains(FleetAutomationVersionDocuments.Check(Version("node", 1) with { Triggers = ["Hourly Now"] }), "short word");
        StringAssert.Contains(FleetAutomationVersionDocuments.Check(Version("node", 1) with { Version = 3 }), "not known");
        StringAssert.Contains(FleetAutomationVersionDocuments.Check(Version("node", 1) with { Definition = null, Sealed = "x", Repository = "github.com/a/b" }), "in clear beside");
    }

    [TestMethod]
    public void TheSealedContentOpensForTheMachineAndTheControllersOfItsPersonOnly()
    {
        using var devices = new Devices();
        var list = devices.List();
        var content = new FleetAutomationContent(Definition, "github.com/owner/repo");
        var sealedText = FleetAutomationSigning.Seal(content, "daily-1", 5, devices.Node.DeviceId, list, devices.Laptop);
        Assert.IsFalse(sealedText.Contains("Summarize", StringComparison.Ordinal), "the definition is not in clear");
        Assert.IsFalse(sealedText.Contains("owner/repo", StringComparison.Ordinal), "the repository is not in clear");

        Assert.IsTrue(FleetAutomationSigning.TryOpen(sealedText, "daily-1", 5, devices.Node.DeviceId, devices.Node, list, out var opened, out var reason), reason);
        Assert.AreEqual(content, opened);
        Assert.IsTrue(FleetAutomationSigning.TryOpen(sealedText, "daily-1", 5, devices.Node.DeviceId, devices.Tablet, list, out _, out reason), "the other controller of the person: " + reason);

        // Another node of the person, a stranger, or the content moved to another version, automation or machine: it does not open.
        Assert.IsFalse(FleetAutomationSigning.TryOpen(sealedText, "daily-1", 5, devices.Node.DeviceId, devices.Other, list, out _, out reason));
        StringAssert.Contains(reason, "not sealed for this machine");
        Assert.IsFalse(FleetAutomationSigning.TryOpen(sealedText, "daily-1", 5, devices.Node.DeviceId, devices.Stranger, list, out _, out _));
        Assert.IsFalse(FleetAutomationSigning.TryOpen(sealedText, "daily-1", 6, devices.Node.DeviceId, devices.Node, list, out _, out reason));
        StringAssert.Contains(reason, "does not open for this automation");
        Assert.IsFalse(FleetAutomationSigning.TryOpen(sealedText, "daily-2", 5, devices.Node.DeviceId, devices.Node, list, out _, out _));
        Assert.IsFalse(FleetAutomationSigning.TryOpen(sealedText, "daily-1", 5, devices.Other.DeviceId, devices.Node, list, out _, out _));
        Assert.IsFalse(FleetAutomationSigning.TryOpen(null, "daily-1", 5, devices.Node.DeviceId, devices.Node, list, out _, out _));

        // Sealed by a node, which gives no key: refused when it seals, and a key it wraps is refused when it opens.
        Assert.ThrowsExactly<ArgumentException>(() => FleetAutomationSigning.Seal(content, "daily-1", 5, devices.Node.DeviceId, list, devices.Other));
        Assert.ThrowsExactly<ArgumentException>(() => FleetAutomationSigning.Seal(content, "daily-1", 5, devices.Stranger.DeviceId, list, devices.Laptop));
    }

    private static FleetAutomationVersion Version(string machineId, long number)
        => new(FleetAutomationVersion.CurrentVersion, "org", "daily-1", number, machineId, false, true, ["hourly"], Definition, null, null, string.Empty, string.Empty,
            new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

    private static void AssertRefused(FleetSignedAutomationVersion? signed, FleetDeviceListState list, string organization, string machine, string expected)
    {
        Assert.IsFalse(FleetAutomationVersionDocuments.TryVerify(signed, list, organization, machine, out _, out var reason));
        StringAssert.Contains(reason, expected);
    }

    /// <summary>A laptop (the first controller device, of person p1), a tablet (another controller of p1), a node and another node of p1, and a stranger outside the list.</summary>
    private sealed class Devices : IDisposable
    {
        private readonly TestFolder _folder = new();

        public Devices()
        {
            Laptop = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "laptop"));
            Tablet = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "tablet"));
            Node = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "node"));
            Other = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "other"));
            Stranger = DeviceIdentity.LoadOrCreate(Path.Combine(_folder.Path, "stranger"));
        }

        public DeviceIdentity Laptop { get; }

        public DeviceIdentity Tablet { get; }

        public DeviceIdentity Node { get; }

        public DeviceIdentity Other { get; }

        public DeviceIdentity Stranger { get; }

        public FleetDeviceListState List(bool removeTablet = false)
        {
            var entries = new List<FleetSignedDeviceListEntry> { Entry(1, null, FleetDeviceListOperations.Add, Laptop, FleetDeviceKinds.Controller, null, null) };
            entries.Add(Entry(2, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add, Node, FleetDeviceKinds.Node, "org", "p1"));
            entries.Add(Entry(3, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add, Other, FleetDeviceKinds.Node, "org", "p1"));
            entries.Add(Entry(4, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add, Tablet, FleetDeviceKinds.Controller, "org", "p1"));
            if (removeTablet) entries.Add(Entry(5, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Remove, Tablet, null, "org", null));
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
            Tablet.Dispose();
            Node.Dispose();
            Other.Dispose();
            Stranger.Dispose();
            _folder.Dispose();
        }
    }
}
