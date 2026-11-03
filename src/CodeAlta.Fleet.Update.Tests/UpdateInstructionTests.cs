using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Update.Tests;

/// <summary>
/// The trust of the fleet's updates (<c>doc/fleet/deployment.md</c>, updates): a machine installs only what a controller
/// device of the device list it anchored signed, never older than what runs unless a person chose a rollback, and only a
/// package of its own runtime whose hash the manifest signs. The server alone can make it install nothing.
/// </summary>
[TestClass]
public sealed class UpdateInstructionTests
{
    private const string Organization = "org-1";
    private const string Runtime = "win-x64";

    [TestMethod]
    public void AManifestAControllerOfTheAnchoredListSignedVerifies()
    {
        using var owner = new Signer();
        var context = Context(owner, running: "4.0.0");
        var signed = owner.Sign(Manifest(owner, "4.0.1", sequence: 3, campaign: "c3"));

        Assert.IsTrue(UpdateInstructions.TryVerify(new FleetUpdateInstruction("c3", signed), context, out var update, out var reason), reason);
        Assert.AreEqual(("c3", "4.0.1", "https://api.nuget.org/v3/index.json", 3L, false), (update.CampaignId, update.Version.ToString(), update.Feed, update.Sequence, update.Automatic));
        var checkedHash = update.ExpectedSha512;
        Assert.AreEqual(Hash('n'), checkedHash, "the hash signed for the package of this machine's runtime");
        var request = update.ToRequest();
        Assert.AreEqual((UpdateOrigin.Remote, "4.0.1", Hash('n')), (request.Origin, request.Version, request.ExpectedSha512));
    }

    [TestMethod]
    public void AnUnsignedOrTamperedManifestIsRefused()
    {
        using var owner = new Signer();
        var context = Context(owner, running: "4.0.0");
        var signed = owner.Sign(Manifest(owner, "4.0.1"));

        Refused(new FleetUpdateInstruction("c1", null), context, "no signed manifest");
        Refused(new FleetUpdateInstruction("c1", signed with { Signature = "" }), context, "signature");
        Refused(new FleetUpdateInstruction("c1", signed with { Payload = signed.Payload.Replace("4.0.1", "4.0.9", StringComparison.Ordinal) }), context, "signature");
        Refused(new FleetUpdateInstruction("another", signed), context, "another campaign");
    }

    [TestMethod]
    public void AManifestSignedByAnOutsiderANodeOrARemovedControllerIsRefused()
    {
        using var owner = new Signer();
        using var outsider = new Signer();
        using var node = new Signer();
        var context = Context(owner, running: "4.0.0");

        Refused(new FleetUpdateInstruction("c1", outsider.Sign(Manifest(outsider, "4.0.1"))), context, "not signed by a controller");
        // The outsider signs a manifest that names the owner as its signer: the key of the list says otherwise.
        Refused(new FleetUpdateInstruction("c1", outsider.Sign(Manifest(owner, "4.0.1"))), context, "signature does not verify");

        var withNode = context with { Devices = context.Devices with { Members = context.Devices.Members.Add(node.DeviceId, node.Member(FleetDeviceKinds.Node)) } };
        Refused(new FleetUpdateInstruction("c1", node.Sign(Manifest(node, "4.0.1"))), withNode, "not signed by a controller");

        var removed = context with { Devices = context.Devices with { Removed = context.Devices.Removed.Add(owner.DeviceId) } };
        Refused(new FleetUpdateInstruction("c1", owner.Sign(Manifest(owner, "4.0.1"))), removed, "not signed by a controller");

        Refused(new FleetUpdateInstruction("c1", owner.Sign(Manifest(owner, "4.0.1") with { OrganizationId = "org-2" })), context, "another organization");
    }

    [TestMethod]
    public void AReplayedManifestIsRefusedEvenTheOneAccepted()
    {
        using var owner = new Signer();
        var context = Context(owner, running: "4.0.0") with { LastSequence = 5, LastCampaignId = "c5" };

        Refused(new FleetUpdateInstruction("c4", owner.Sign(Manifest(owner, "4.0.1", sequence: 4, campaign: "c4"))), context, "older than campaign 5");
        Refused(new FleetUpdateInstruction("other", owner.Sign(Manifest(owner, "4.0.1", sequence: 5, campaign: "other"))), context, "older than campaign 5");
        // The campaign accepted never starts again (a cancelled one, or a rollback done before a later upgrade): the
        // machine answers it with what it kept of it.
        Refused(new FleetUpdateInstruction("c5", owner.Sign(Manifest(owner, "4.0.1", sequence: 5, campaign: "c5"))), context, "accepted already");
        Assert.IsTrue(UpdateInstructions.TryVerify(new FleetUpdateInstruction("c6", owner.Sign(Manifest(owner, "4.0.1", sequence: 6, campaign: "c6"))), context, out _, out var reason),
            reason);
    }

    [TestMethod]
    public void ADowngradeIsRefusedUnlessAPersonChoseARollback()
    {
        using var owner = new Signer();
        var context = Context(owner, running: "4.0.2");

        Refused(new FleetUpdateInstruction("c1", owner.Sign(Manifest(owner, "4.0.1"))), context, "rollback");
        Assert.IsTrue(UpdateInstructions.TryVerify(new FleetUpdateInstruction("c1", owner.Sign(Manifest(owner, "4.0.1") with { Rollback = true })), context, out var update,
            out var reason), reason);
        Assert.IsTrue(update.Rollback);
    }

    [TestMethod]
    public void AManifestWithoutTheHashOfThisRuntimeOrThatDoesNotNameThisMachineIsRefused()
    {
        using var owner = new Signer();
        var context = Context(owner, running: "4.0.0");
        var linuxOnly = Manifest(owner, "4.0.1") with
        {
            Packages = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["CodeAlta.Node"] = new Dictionary<string, string> { ["linux-x64"] = Hash('n') } },
        };
        Refused(new FleetUpdateInstruction("c1", owner.Sign(linuxOnly)), context, "CodeAlta.Node.win-x64");

        // The hash signed for the server is not one a node takes.
        Refused(new FleetUpdateInstruction("c1", owner.Sign(Manifest(owner, "4.0.1"))), context with { Product = UpdateProduct.Server, Runtime = "linux-arm64" },
            "CodeAlta.Server.linux-arm64");

        var others = Manifest(owner, "4.0.1") with { Machines = ["another-node"] };
        Refused(new FleetUpdateInstruction("c1", owner.Sign(others)), context, "does not name this machine");
        Assert.IsTrue(UpdateInstructions.TryVerify(new FleetUpdateInstruction("c1", owner.Sign(others with { Machines = ["another-node", "node-1"] })), context, out _, out var reason),
            reason);
    }

    [TestMethod]
    public async Task APackageThatDoesNotMatchTheSignedHashIsNotInstalled()
    {
        using var temp = new TempFolder();
        using var owner = new Signer();
        var feed = Directory.CreateDirectory(temp.Combine("feed")).FullName;
        Packages.Write(feed, "4.0.1");
        var context = Context(owner, running: "4.0.0");
        var manifest = Manifest(owner, "4.0.1") with { Feed = feed };
        Assert.IsTrue(UpdateInstructions.TryVerify(new FleetUpdateInstruction("c1", owner.Sign(manifest)), context, out var update, out var reason), reason);
        var updater = new SelfUpdater(new SelfUpdaterOptions
        {
            Product = Packages.Product, Store = new UpdateVersionStore(temp.Combine("versions")), Running = UpdateVersion.Parse("4.0.0"), Http = new HttpClient(),
            Policy = UpdateSourcePolicy.From(new UpdateSourceSettings { Source = feed }), Runtime = Runtime,
        });

        var request = update.ToRequest();
        var check = await updater.CheckAsync(request, CancellationToken.None);
        var refused = await Assert.ThrowsExactlyAsync<UpdateException>(() => updater.StageAsync(check, request, CancellationToken.None));
        Assert.AreEqual(UpdateErrorCodes.HashMismatch, refused.Code);
        Assert.IsFalse(Directory.Exists(temp.Combine("versions", "4.0.1")), "nothing is unpacked");

        // The hash of the very package, as the desktop reads it from the feed and signs it, installs it.
        var hashes = await UpdateReleases.ReadHashesAsync(UpdateFeeds.Open(feed, new HttpClient()), [Packages.Product], UpdateVersion.Parse("4.0.1"), CancellationToken.None);
        Assert.AreEqual(Packages.Sha512(Path.Combine(feed, "CodeAlta.Node.win-x64.4.0.1.nupkg")), hashes["CodeAlta.Node"]["win-x64"]);
        var good = manifest with { Packages = hashes };
        Assert.IsTrue(UpdateInstructions.TryVerify(new FleetUpdateInstruction("c1", owner.Sign(good)), context, out update, out reason), reason);
        request = update.ToRequest();
        var staged = await updater.StageAsync(await updater.CheckAsync(request, CancellationToken.None), request, CancellationToken.None);
        Assert.AreEqual("expected hash", staged.Checked, "the signed hash checked the package of a folder that publishes none");
        Assert.IsTrue(Directory.Exists(temp.Combine("versions", "4.0.1")));
    }

    [TestMethod]
    public void TheOwnersFileThatTurnsOffTheFleetWinsOverAnySignature()
    {
        var policy = UpdateSourcePolicy.From(new UpdateSourceSettings { Fleet = false });
        Assert.IsFalse(policy.AcceptsFleet);
        var refused = Assert.ThrowsExactly<UpdateException>(() => policy.Resolve(null, UpdateOrigin.Remote));
        Assert.AreEqual(UpdateErrorCodes.FleetRefused, refused.Code);
        Assert.AreEqual(UpdateFeeds.NuGetOrg, policy.Resolve(null, UpdateOrigin.Local), "the owner on the machine still updates it");
    }

    [TestMethod]
    public void AnAutomaticUpdateNeedsASignedAutomaticPolicyAndANewerStableVersion()
    {
        using var owner = new Signer();
        using var outsider = new Signer();
        var context = Context(owner, running: "4.0.0") with { LastPolicySequence = 2 };
        var automatic = new FleetUpdatePolicy(1, Organization, 2, FleetUpdateModes.Automatic, null, "https://feed.test/v3/index.json", "02:00", "Europe/Paris", owner.DeviceId,
            DateTimeOffset.UnixEpoch);

        Assert.IsTrue(UpdateInstructions.TryVerify(new FleetUpdateInstruction("auto-1", null, owner.Sign(automatic), "4.1.0"), context, out var update, out var reason), reason);
        Assert.AreEqual((true, null, "https://feed.test/v3/index.json", "4.1.0"), (update.Automatic, update.ExpectedSha512, update.Feed, update.Version.ToString()),
            "no person signed a hash: the feed's checks the package");

        Refused(new FleetUpdateInstruction("auto-1", null, owner.Sign(automatic), "4.1.0-beta.1"), context, "stable");
        Refused(new FleetUpdateInstruction("auto-1", null, owner.Sign(automatic), "3.9.0"), context, "older than 4.0.0");
        // The version that runs: nothing to install, the machine reports it updated once confirmed.
        Assert.IsTrue(UpdateInstructions.TryVerify(new FleetUpdateInstruction("auto-1", null, owner.Sign(automatic), "4.0.0"), context, out _, out reason), reason);
        Refused(new FleetUpdateInstruction("auto-1", null, owner.Sign(automatic with { Mode = FleetUpdateModes.Manual }), "4.1.0"), context, "not automatic");
        Refused(new FleetUpdateInstruction("auto-1", null, owner.Sign(automatic with { Sequence = 1 }), "4.1.0"), context, "older than policy 2");
        Refused(new FleetUpdateInstruction("auto-1", null, outsider.Sign(automatic with { SignerDeviceId = outsider.DeviceId }), "4.1.0"), context, "not signed by a controller");
    }

    [TestMethod]
    public void TheMaintenanceWindowIsTheNextStartInItsTimeZone()
    {
        var policy = new FleetUpdatePolicy(1, Organization, 1, FleetUpdateModes.Automatic, null, null, "02:30", null, "d", DateTimeOffset.UnixEpoch);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 4, 2, 30, 0, TimeSpan.Zero), FleetUpdateDocuments.NextWindow(policy, new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero)));
        Assert.AreEqual(new DateTimeOffset(2026, 11, 3, 2, 30, 0, TimeSpan.Zero), FleetUpdateDocuments.NextWindow(policy, new DateTimeOffset(2026, 11, 3, 1, 0, 0, TimeSpan.Zero)));
        var tokyo = policy with { TimeZone = "Asia/Tokyo" };
        // 02:30 in Tokyo (UTC+9) is 17:30 UTC the day before.
        Assert.AreEqual(new DateTimeOffset(2026, 11, 3, 17, 30, 0, TimeSpan.Zero), FleetUpdateDocuments.NextWindow(tokyo, new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero)));
        var now = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(now, FleetUpdateDocuments.NextWindow(policy with { Window = null }, now), "no window: at once");
    }

    [TestMethod]
    public void TheMaintenanceWindowStaysOpenForItsLengthAndFollowsDaylightSavingTime()
    {
        var paris = new FleetUpdatePolicy(1, Organization, 1, FleetUpdateModes.Automatic, null, null, "02:30", "Europe/Paris", "d", DateTimeOffset.UnixEpoch);
        // In November Paris is UTC+1: the window is 01:30 to 05:30 UTC.
        var noon = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 4, 1, 30, 0, TimeSpan.Zero), FleetUpdateDocuments.WindowOpening(paris, noon), "noon waits for the next window");
        var inside = new DateTimeOffset(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(inside, FleetUpdateDocuments.WindowOpening(paris, inside), "open: at once");
        Assert.AreEqual(new DateTimeOffset(2026, 11, 5, 1, 30, 0, TimeSpan.Zero), FleetUpdateDocuments.WindowOpening(paris, new DateTimeOffset(2026, 11, 4, 5, 30, 0, TimeSpan.Zero)),
            "closed once its length passed");
        // In July Paris is UTC+2.
        Assert.AreEqual(new DateTimeOffset(2026, 7, 2, 0, 30, 0, TimeSpan.Zero), FleetUpdateDocuments.WindowOpening(paris, new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)));
        // 02:30 does not exist on 29 March 2026 in Paris (02:00 becomes 03:00): the window opens an hour later, 03:30 CEST.
        Assert.AreEqual(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), FleetUpdateDocuments.WindowOpening(paris, new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero)));
        var now = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(now, FleetUpdateDocuments.WindowOpening(paris with { Window = null }, now), "no window: at once");
    }

    [TestMethod]
    public void AnAutomaticUpdateStartsAtTheWindowOfItsSignedPolicy()
    {
        using var owner = new Signer();
        var noon = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var context = Context(owner, running: "4.0.0") with { Now = noon };
        var automatic = new FleetUpdatePolicy(1, Organization, 1, FleetUpdateModes.Automatic, null, null, "02:00", "Europe/Paris", owner.DeviceId, DateTimeOffset.UnixEpoch);

        Assert.IsTrue(UpdateInstructions.TryVerify(new FleetUpdateInstruction("auto-1", null, owner.Sign(automatic), "4.1.0"), context, out var update, out var reason), reason);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.Zero), update.NotBefore, "not at noon, whatever the server says");
        Assert.AreEqual(automatic, update.Policy);
    }

    [TestMethod]
    public void ThePolicyAMachineKeptRefusesWhatItNoLongerAllows()
    {
        using var owner = new Signer();
        var pinned = new FleetUpdatePolicy(1, Organization, 3, FleetUpdateModes.Pinned, "4.0.1", null, null, null, owner.DeviceId, DateTimeOffset.UnixEpoch);
        var context = Context(owner, running: "4.0.0") with { Policy = pinned, LastPolicySequence = 3 };

        // A manifest signed before the fleet was pinned, delivered after it.
        Refused(new FleetUpdateInstruction("c1", owner.Sign(Manifest(owner, "4.0.2"))), context, "pinned to 4.0.1");
        Assert.IsTrue(UpdateInstructions.TryVerify(new FleetUpdateInstruction("c1", owner.Sign(Manifest(owner, "4.0.1"))), context, out _, out var reason), reason);

        // An automatic policy the pinned one replaced, and another one under the same number.
        var automatic = pinned with { Mode = FleetUpdateModes.Automatic, PinnedVersion = null, Sequence = 2 };
        Refused(new FleetUpdateInstruction("auto-1", null, owner.Sign(automatic), "4.1.0"), context, "older than policy 3");
        Refused(new FleetUpdateInstruction("auto-1", null, owner.Sign(automatic with { Sequence = 3 }), "4.1.0"), context, "another policy");
        Assert.IsTrue(UpdateInstructions.TryVerify(new FleetUpdateInstruction("auto-1", null, owner.Sign(automatic with { Sequence = 4 }), "4.1.0"), context, out _, out reason), reason);
    }

    private static void Refused(FleetUpdateInstruction instruction, UpdateInstructionContext context, string expected)
    {
        Assert.IsFalse(UpdateInstructions.TryVerify(instruction, context, out var update, out var reason), "refused");
        Assert.IsNull(update);
        StringAssert.Contains(reason, expected);
    }

    private static UpdateInstructionContext Context(Signer owner, string running) => new()
    {
        Devices = new FleetDeviceListState(Organization, 1, "hash", ImmutableDictionary.Create<string, FleetDeviceListMember>(StringComparer.Ordinal)
            .Add(owner.DeviceId, owner.Member(FleetDeviceKinds.Controller)), ImmutableHashSet.Create<string>(StringComparer.Ordinal)),
        OrganizationId = Organization,
        DeviceId = "node-1",
        Product = UpdateProduct.Node,
        Runtime = Runtime,
        Running = UpdateVersion.Parse(running),
    };

    private static FleetUpdateManifest Manifest(Signer signer, string version, long sequence = 1, string campaign = "c1")
        => new(FleetUpdateManifest.CurrentVersion, Organization, campaign, sequence, version, UpdateFeeds.NuGetOrg, false, null, null, null,
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["CodeAlta.Node"] = new Dictionary<string, string> { [Runtime] = Hash('n'), ["linux-x64"] = Hash('l') },
                ["CodeAlta.Server"] = new Dictionary<string, string> { [Runtime] = Hash('s') },
            }, signer.DeviceId, DateTimeOffset.UnixEpoch);

    private static string Hash(char seed) => Convert.ToBase64String(SHA512.HashData([(byte)seed]));

    private sealed class Signer : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public Signer()
        {
            FleetDevicePublicKey.TryParse(Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()), out var key);
            Key = key!;
        }

        public FleetDevicePublicKey Key { get; }

        public string DeviceId => Key.DeviceId;

        public FleetDeviceListMember Member(string kind) => new(DeviceId, kind, Key, "person-1");

        public FleetSignedUpdateDocument Sign(FleetUpdateManifest manifest)
        {
            var text = FleetUpdateDocuments.Serialize(manifest);
            return new FleetSignedUpdateDocument(text, Base64Url.EncodeToString(_key.SignData(FleetUpdateDocuments.GetManifestSignedBytes(text), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        }

        public FleetSignedUpdateDocument Sign(FleetUpdatePolicy policy)
        {
            var text = FleetUpdateDocuments.Serialize(policy);
            return new FleetSignedUpdateDocument(text, Base64Url.EncodeToString(_key.SignData(FleetUpdateDocuments.GetPolicySignedBytes(text), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        }

        public void Dispose() => _key.Dispose();
    }
}
