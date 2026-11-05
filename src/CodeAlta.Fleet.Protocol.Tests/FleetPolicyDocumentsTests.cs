using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace CodeAlta.Fleet.Protocol.Tests;

/// <summary>
/// The chain of the signed policy (<c>doc/fleet/security-and-privacy.md</c>, policies and the kill switch), as every machine
/// replays it against the device list and the access document it anchored: before any access document the person who set
/// the server up signs; then a signer of the access version a version names; a version follows the one before and names
/// no older access version; a stop names what it stops, and a machine reads whether it stops it.
/// </summary>
[TestClass]
public sealed class FleetPolicyDocumentsTests
{
    private const string Organization = "org1";
    private static readonly DateTimeOffset Time = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void TheFounderSignsAtHome_ASignerOfTheAccessDocumentLater_AndMachinesReadRulesAndTheStop()
    {
        using var world = new World();
        var chain = new List<FleetSignedPolicyDocument>
        {
            world.Founder.Sign(Policy(1, null, 0, new FleetPolicyRules(Providers: ["anthropic", "openai"], Sandbox: "workspace", PermissionMode: "acceptEdits"))),
        };
        chain.Add(world.Ada.Sign(Policy(2, Hash(chain), 1, new FleetPolicyRules(Providers: ["anthropic", "openai"]),
            new FleetPolicyStop(FleetPolicyStopScopes.Teams, [], ["infra"], "A runaway automation"))));

        var verification = FleetPolicyDocuments.Verify(chain, world.Devices, world.AccessAt, Organization);
        Assert.IsTrue(verification.IsValid, verification.Reason);
        var state = verification.State;
        Assert.AreEqual((2L, 1L), (state.Number, state.AccessNumber));
        Assert.IsTrue(state.Rules.AllowsProvider("OpenAI"));
        Assert.IsFalse(state.Rules.AllowsProvider("xai"));
        Assert.IsTrue(state.Stops(world.Node.Id, world.Access), "the node is a machine of the team the stop names");
        Assert.IsFalse(state.Stops(world.Founder.Id, world.Access), "a machine of no team named goes on");
        Assert.AreEqual(FleetPolicyDocuments.Hash(chain[0]), state.FirstHash);

        // Lifting the stop is the next version.
        chain.Add(world.Founder.Sign(Policy(3, Hash(chain), 1, state.Rules)));
        var lifted = FleetPolicyDocuments.Verify(chain, world.Devices, world.AccessAt, Organization).State;
        Assert.IsNull(lifted.Stop);
        Assert.IsFalse(lifted.Stops(world.Node.Id, world.Access));
    }

    [TestMethod]
    public void AVersionNotSignedByASigner_OutOfTheChain_OrThatDoesNotRead_IsRefused()
    {
        using var world = new World();
        var first = world.Founder.Sign(Policy(1, null, 0, FleetPolicyRules.None));

        AssertBroken([world.Ada.Sign(Policy(1, null, 0, FleetPolicyRules.None))], world, 1, "before any access document, a version is signed by a device of the person who set the server up");
        AssertBroken([first, world.Bea.Sign(Policy(2, Hash([first]), 1, FleetPolicyRules.None))], world, 2,
            "its signer is not an owner or an admin who signs in the access document it names, or is suspended there");
        AssertBroken([first, world.Ada.Sign(Policy(2, Hash([first]), 7, FleetPolicyRules.None))], world, 2,
            "it names version 7 of the access document, which this device has not verified");
        AssertBroken([first, world.Founder.Sign(Policy(3, Hash([first]), 0, FleetPolicyRules.None))], world, 2, "its number is 3, not 2");
        AssertBroken([first, world.Founder.Sign(Policy(2, "elsewhere", 0, FleetPolicyRules.None))], world, 2, "it does not name the hash of the version before it");
        var atOne = world.Ada.Sign(Policy(2, Hash([first]), 1, FleetPolicyRules.None));
        AssertBroken([first, atOne, world.Founder.Sign(Policy(3, Hash([first, atOne]), 0, FleetPolicyRules.None))], world, 3,
            "it names a version of the access document older than the one of the version before");
        AssertBroken([world.Node.Sign(Policy(1, null, 0, FleetPolicyRules.None))], world, 1, "its signer was not a controller device of the device list at the entry it names");
        AssertBroken([world.Founder.Sign(Policy(1, null, 0, new FleetPolicyRules(Network: "everywhere")))], world, 1, "its network is 'everywhere', not off, hosts or on");
        AssertBroken([world.Founder.Sign(Policy(1, null, 0, new FleetPolicyRules(PermissionMode: "yolo")))], world, 1, "its permission mode 'yolo' is not known");
        AssertBroken([world.Founder.Sign(Policy(1, null, 0, FleetPolicyRules.None, new FleetPolicyStop(FleetPolicyStopScopes.Machines, [], [])))], world, 1,
            "a stop of machines names machines, and only them");
        AssertBroken([world.Founder.Sign(Policy(1, null, 0, FleetPolicyRules.None, new FleetPolicyStop("galaxy", [], [])))], world, 1, "its stop reaches 'galaxy', which is not known");

        // A server that changes a rule, or takes the stop out of a signed version, breaks its signature.
        var stopped = world.Founder.Sign(Policy(1, null, 0, FleetPolicyRules.None, new FleetPolicyStop(FleetPolicyStopScopes.Fleet, [], [])));
        var tampered = stopped with { Payload = stopped.Payload.Replace(",\"stop\":{\"scope\":\"fleet\",\"machines\":[],\"teams\":[]}", string.Empty, StringComparison.Ordinal) };
        Assert.AreNotEqual(stopped.Payload, tampered.Payload);
        AssertBroken([tampered], world, 1, "its signature does not verify with the key of its signer");
        AssertBroken([first], world, 1, "it names another organization", organization: "org2");
    }

    [TestMethod]
    public void AStopReachesEveryMachine_TheMachinesItNames_OrTheMachinesOfItsTeams()
    {
        using var world = new World();
        FleetPolicyState With(FleetPolicyStop stop) => FleetPolicyState.Empty with { Stop = stop };
        Assert.IsTrue(With(new FleetPolicyStop(FleetPolicyStopScopes.Fleet, [], [])).Stops(world.Founder.Id, null));
        Assert.IsTrue(With(new FleetPolicyStop(FleetPolicyStopScopes.Machines, [world.Founder.Id], [])).Stops(world.Founder.Id, null));
        Assert.IsFalse(With(new FleetPolicyStop(FleetPolicyStopScopes.Machines, [world.Founder.Id], [])).Stops(world.Node.Id, null));
        Assert.IsTrue(With(new FleetPolicyStop(FleetPolicyStopScopes.Teams, [], ["infra"])).Stops(world.Node.Id, null),
            "an access document not known says no team: the strictest reading, as for a scope no version knows");
        Assert.IsFalse(With(new FleetPolicyStop(FleetPolicyStopScopes.Teams, [], ["infra"])).Stops(world.Node.Id, FleetAccessState.Empty), "an organization that signed none has no team machine");
        Assert.IsTrue(With(new FleetPolicyStop("future-scope", [], [])).Stops(world.Node.Id, null), "a scope no version knows reaches every machine");
        Assert.IsFalse(FleetPolicyState.Empty.Stops(world.Node.Id, world.Access));
    }

    [TestMethod]
    public void RulesAllowWhatTheyName_AndDescribeSaysWhatAVersionChanges()
    {
        var rules = new FleetPolicyRules(Models: ["claude-*", "gpt-6.1"]);
        Assert.IsTrue(rules.AllowsModel("claude-opus-5-5"));
        Assert.IsTrue(rules.AllowsModel("GPT-6.1"));
        Assert.IsFalse(rules.AllowsModel("gpt-6"));
        Assert.IsTrue(rules.AllowsModel(null), "the default model is decided once it is known");
        Assert.IsTrue(FleetPolicyRules.None.AllowsProvider("anything"));

        var before = FleetPolicyState.Empty with { Rules = new FleetPolicyRules(Sandbox: "workspace", AgentStarts: true) };
        var after = Policy(2, "h", 0, new FleetPolicyRules(Sandbox: "read-only", PermissionMode: "default", AgentStarts: false),
            new FleetPolicyStop(FleetPolicyStopScopes.Machines, ["d1"], [], "Leak"));
        CollectionAssert.AreEqual(new[]
        {
            "The work of the fleet runs in the sandbox 'read-only' at least", "Agents start no work on other machines", "Permission mode at most: default",
            "STOP: the work of the fleet stops on Build box (Leak)",
        }, FleetPolicyDocuments.Describe(before, after, id => id == "d1" ? "Build box" : null).ToArray());
        CollectionAssert.AreEqual(new[] { "No sandbox floor", "Agents may start work on other machines, where the machine allows it", "The work of the fleet goes on: the stop is lifted" },
            FleetPolicyDocuments.Describe(before with { Stop = after.Stop }, Policy(3, "h", 0, FleetPolicyRules.None), _ => null).ToArray());
    }

    [TestMethod]
    public void AServerKeepsOnlyAVersionSignedByTheDeviceThatSendsIt()
    {
        using var world = new World();
        var signed = world.Ada.Sign(Policy(1, null, 0, FleetPolicyRules.None));
        Assert.IsTrue(FleetDevicePublicKey.TryParse(world.Ada.PublicKey, out var ada));
        Assert.IsTrue(FleetDevicePublicKey.TryParse(world.Bea.PublicKey, out var bea));
        Assert.IsTrue(FleetPolicyDocuments.TryReadSigned(signed, ada!, out var document, out var reason), reason);
        Assert.AreEqual(world.Ada.Id, document.SignerDeviceId);
        Assert.IsFalse(FleetPolicyDocuments.TryReadSigned(signed, bea!, out _, out reason));
        Assert.AreEqual("it is not signed by the device that sends it", reason);
        Assert.IsNull(FleetPolicyDocuments.Validate(Policy(1, null, 0, FleetPolicyRules.None)));
    }

    private static void AssertBroken(IReadOnlyList<FleetSignedPolicyDocument> chain, World world, long at, string reason, string organization = Organization)
    {
        var verification = FleetPolicyDocuments.Verify(chain, world.Devices, world.AccessAt, organization);
        Assert.IsFalse(verification.IsValid);
        Assert.AreEqual((at, reason), (verification.BrokenAt, verification.Reason));
        Assert.AreEqual(at - 1, verification.State.Number, "the state is the policy up to the last version that holds");
    }

    private static string Hash(IReadOnlyList<FleetSignedPolicyDocument> chain) => FleetPolicyDocuments.Hash(chain[^1]);

    private static FleetPolicyDocument Policy(long number, string? previous, long access, FleetPolicyRules rules, FleetPolicyStop? stop = null)
        => new(1, Organization, number, previous, 4, access, rules, stop, string.Empty, Time);

    // A device list: Alex's first device, his node, Ada's and Bea's first devices; an access document where Ada signs and the
    // node is a machine of the infra team.
    private sealed class World : IDisposable
    {
        public World()
        {
            var entries = new List<FleetSignedDeviceListEntry>
            {
                Founder.Entry(new FleetDeviceListEntryPayload(1, null, 1, null, FleetDeviceListOperations.Add, Founder.Id, Founder.PublicKey, FleetDeviceKinds.Controller, null, Founder.Id, Time)),
            };
            void Add(Key device, string kind, string owner)
                => entries.Add(Founder.Entry(new FleetDeviceListEntryPayload(1, Organization, entries.Count + 1, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add,
                    device.Id, device.PublicKey, kind, owner, Founder.Id, Time)));
            Add(Node, FleetDeviceKinds.Node, "alex");
            Add(Ada, FleetDeviceKinds.Controller, "ada");
            Add(Bea, FleetDeviceKinds.Controller, "bea");
            Devices = FleetDeviceListVerifier.VerifyOrThrow(entries, Organization);
            var founderPerson = Devices.Members[Founder.Id].OwnerId;
            Access = FleetAccessState.Empty with
            {
                OrganizationId = Organization, Number = 1, Owners = [founderPerson!], Signers = ["ada"],
                Teams = new Dictionary<string, FleetAccessTeam> { ["infra"] = new("infra", ["ada"], ["bea"], false) }.ToImmutableDictionary(StringComparer.Ordinal),
                Machines = new Dictionary<string, string> { [Node.Id] = "infra" }.ToImmutableDictionary(StringComparer.Ordinal),
            };
        }

        public Key Founder { get; } = new();

        public Key Node { get; } = new();

        public Key Ada { get; } = new();

        public Key Bea { get; } = new();

        public FleetDeviceListState Devices { get; }

        public FleetAccessState Access { get; }

        public FleetAccessState? AccessAt(long number) => number == 1 ? Access : null;

        public void Dispose()
        {
            Founder.Dispose();
            Node.Dispose();
            Ada.Dispose();
            Bea.Dispose();
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

        public FleetSignedDeviceListEntry Entry(FleetDeviceListEntryPayload payload)
        {
            var text = FleetDeviceListEntries.SerializePayload(payload);
            return new FleetSignedDeviceListEntry(text, Base64Url.EncodeToString(_key.SignData(FleetDeviceListEntries.GetSignedBytes(text), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        }

        public FleetSignedPolicyDocument Sign(FleetPolicyDocument document)
        {
            var text = FleetPolicyDocuments.Serialize(document with { SignerDeviceId = Id });
            return new FleetSignedPolicyDocument(text, Base64Url.EncodeToString(_key.SignData(FleetPolicyDocuments.GetSignedBytes(text), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        }

        public void Dispose() => _key.Dispose();
    }
}
