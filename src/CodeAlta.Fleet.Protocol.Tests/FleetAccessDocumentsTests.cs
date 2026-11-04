using System.Buffers.Text;
using System.Security.Cryptography;

namespace CodeAlta.Fleet.Protocol.Tests;

/// <summary>
/// The chain of the signed access document (<c>doc/fleet/access-control.md</c>, what the node decides), as every machine replays
/// it against the device list it anchored: the first version by the owner who set the server up, the next ones by the devices
/// of signers; only an owner changes who signs or takes a suspension back; a version follows the one before; a device that
/// left the list signs nothing after it left.
/// </summary>
[TestClass]
public sealed class FleetAccessDocumentsTests
{
    private const string Organization = "org1";
    private static readonly DateTimeOffset Time = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void TheOwnerSignsTheFirstVersion_ASignerTheNext_AndMachinesReadTeamsAndSuspensions()
    {
        using var world = new World();
        var chain = new List<FleetSignedAccessDocument>();
        chain.Add(world.Founder.Sign(Document(1, null, 4, owners: ["alex"], signers: ["ada"])));
        chain.Add(world.Ada.Sign(Document(2, Hash(chain), 4, owners: ["alex"], signers: ["ada"], teams: [Team("infra", ["ada"], ["bea"])],
            machines: [new FleetAccessMachine(world.Node.Id, "infra")])));
        chain.Add(world.Ada.Sign(Document(3, Hash(chain), 4, owners: ["alex"], signers: ["ada"], teams: [Team("infra", ["ada"], [])], machines: [new FleetAccessMachine(world.Node.Id, "infra")],
            suspended: ["bea"])));

        var verification = FleetAccessDocuments.Verify(chain, world.Devices, Organization);
        Assert.IsTrue(verification.IsValid, verification.Reason);
        var state = verification.State;
        Assert.AreEqual(3, state.Number);
        Assert.AreEqual("infra", state.TeamOf(world.Node.Id)?.Id);
        Assert.AreEqual(FleetTeamRoles.Maintainer, FleetAccessState.RoleIn(state.TeamOf(world.Node.Id)!, "ada"));
        Assert.IsNull(FleetAccessState.RoleIn(state.TeamOf(world.Node.Id)!, "bea"), "Bea left the team");
        Assert.IsTrue(state.IsSuspended("bea"));
        Assert.IsTrue(state.IsSigner("ada"));
        Assert.IsNull(state.TeamOf(world.Founder.Id), "a machine the document does not name is personal");
        Assert.AreEqual(FleetAccessDocuments.Hash(chain[0]), state.FirstHash);
    }

    [TestMethod]
    public void AVersionNotSignedByASigner_OrOutOfTheChain_IsRefused()
    {
        using var world = new World();
        var first = world.Founder.Sign(Document(1, null, 4, owners: ["alex"]));

        AssertBroken([world.Ada.Sign(Document(1, null, 4, owners: ["alex"]))], world, 1, "the first version is signed by a device of the person who set the server up");
        AssertBroken([world.Founder.Sign(Document(1, null, 4, owners: ["ada"]))], world, 1, "the first version names the person who set the server up as an owner");
        AssertBroken([first, world.Bea.Sign(Document(2, Hash([first]), 4, owners: ["alex"]))], world, 2, "its signer is not an owner or an admin who signs, or is suspended");
        AssertBroken([first, world.Founder.Sign(Document(3, Hash([first]), 4, owners: ["alex"]))], world, 2, "its number is 3, not 2");
        AssertBroken([first, world.Founder.Sign(Document(2, "elsewhere", 4, owners: ["alex"]))], world, 2, "it does not name the hash of the version before it");
        AssertBroken([first, world.Founder.Sign(Document(2, Hash([first]), 3, owners: ["alex"]))], world, 2,
            "it names an entry of the device list before the one of the version before, or one this device does not hold");
        AssertBroken([world.Node.Sign(Document(1, null, 4, owners: ["alex"]))], world, 1, "its signer was not a controller device of the device list at the entry it names");
        AssertBroken([world.Founder.Sign(Document(1, null, 4, owners: ["alex"], teams: [Team("t", ["bea"], [])], suspended: ["bea"]))], world, 1, "a suspended person is in a team");
        AssertBroken([world.Founder.Sign(Document(1, null, 4, owners: ["alex"], machines: [new FleetAccessMachine(world.Node.Id, "ghost")]))], world, 1,
            "a team machine does not name a team of the version, or is named twice");

        var tampered = first with { Payload = first.Payload.Replace("\"suspended\":[]", "\"suspended\":[\"bea\"]", StringComparison.Ordinal) };
        AssertBroken([tampered], world, 1, "its signature does not verify with the key of its signer");
        AssertBroken([first], world, 1, "it names another organization", organization: "org2");
    }

    [TestMethod]
    public void OnlyAnOwnerChangesWhoSigns_OrTakesASuspensionBack_AndADeviceThatLeftSignsNothingAfter()
    {
        using var world = new World();
        var chain = new List<FleetSignedAccessDocument> { world.Founder.Sign(Document(1, null, 4, owners: ["alex"], signers: ["ada"], suspended: ["bea"])) };

        AssertBroken([.. chain, world.Ada.Sign(Document(2, Hash(chain), 4, owners: ["alex"], signers: ["ada", "cy"], suspended: ["bea"]))], world, 2,
            "only an owner changes who owns and signs, or takes a suspension back");
        AssertBroken([.. chain, world.Ada.Sign(Document(2, Hash(chain), 4, owners: ["alex"], signers: ["ada"]))], world, 2,
            "only an owner changes who owns and signs, or takes a suspension back");
        var byOwner = world.Founder.Sign(Document(2, Hash(chain), 4, owners: ["alex"], signers: [], suspended: []));
        Assert.IsTrue(FleetAccessDocuments.Verify([.. chain, byOwner], world.Devices, Organization).IsValid, "the owner takes the suspension back and Ada's signature");

        // Ada's device left the list at entry 5: it signed at entry 4 before, never at 5 or later.
        AssertBroken([.. chain, world.Ada.Sign(Document(2, Hash(chain), 5, owners: ["alex"], signers: ["ada"], suspended: ["bea"]))], world, 2,
            "its signer was not a controller device of the device list at the entry it names", devices: world.WithAdaRemoved);
        Assert.IsTrue(FleetAccessDocuments.Verify([.. chain, world.Ada.Sign(Document(2, Hash(chain), 4, owners: ["alex"], signers: ["ada"], suspended: ["bea"]))],
            world.WithAdaRemoved, Organization).IsValid, "what it signed before it left still holds");
    }

    [TestMethod]
    public void DescribeSaysWhatChanges_AndSaySameIgnoresTheOrder()
    {
        using var world = new World();
        var before = FleetAccessDocuments.Verify([world.Founder.Sign(Document(1, null, 4, owners: ["alex"], teams: [Team("infra", ["ada"], ["bea"])]))], world.Devices, Organization).State;
        var after = Document(2, null, 4, owners: ["alex"], signers: ["ada"], teams: [Team("infra", ["ada"], [])], machines: [new FleetAccessMachine("m1", "infra")], suspended: ["bea"]);
        var names = new Dictionary<string, string> { ["ada"] = "Ada", ["bea"] = "Bea", ["infra"] = "Infra", ["m1"] = "Build box" };

        CollectionAssert.AreEqual(new[]
        {
            "Ada becomes an admin who signs", "Bea leaves Infra", "Build box becomes a machine of Infra", "Bea is suspended: their work stops on team machines",
        }, FleetAccessDocuments.Describe(before, after, id => names.GetValueOrDefault(id)).ToArray());
        Assert.IsTrue(FleetAccessDocuments.SaySame(after, after with { Owners = ["alex"], Teams = [Team("infra", ["ada"], [])], Suspended = ["bea"], Number = 9 }));
        Assert.IsFalse(FleetAccessDocuments.SaySame(after, after with { Suspended = [] }));
    }

    [TestMethod]
    public void ATeamNamesTheServiceIdentitiesItRunsAs_AndATeamAutomationVerifiesOnlyFromItsMaintainersOnItsMachines()
    {
        using var world = new World();
        // A team with no service identity writes the format as before: the hash of a version signed earlier does not move.
        Assert.IsFalse(FleetAccessDocuments.Serialize(Document(1, null, 4, owners: ["alex"], teams: [Team("infra", ["ada"], [])])).Contains("services", StringComparison.Ordinal));
        var chain = new List<FleetSignedAccessDocument> { world.Founder.Sign(Document(1, null, 4, owners: ["alex"], signers: ["ada"], teams: [Team("infra", ["ada"], ["bea"])],
            machines: [new FleetAccessMachine(world.Node.Id, "infra")])) };
        var before = FleetAccessDocuments.Verify(chain, world.Devices, Organization).State;
        Assert.IsNull(before.TeamOf(world.Node.Id)!.Services);
        var after = Document(2, Hash(chain), 4, owners: ["alex"], signers: ["ada"], teams: [Team("infra", ["ada"], ["bea"]) with { Services = ["svc-infra"] }],
            machines: [new FleetAccessMachine(world.Node.Id, "infra")]);
        CollectionAssert.AreEqual(new[] { "Infra runs its automations as infra-bot" },
            FleetAccessDocuments.Describe(before, after, id => id switch { "infra" => "Infra", "svc-infra" => "infra-bot", _ => null }).ToArray());
        Assert.IsFalse(FleetAccessDocuments.SaySame(before, after), "a service identity turned on is a change machines follow");
        chain.Add(world.Ada.Sign(after));
        var state = FleetAccessDocuments.Verify(chain, world.Devices, Organization).State;
        CollectionAssert.AreEqual(new[] { "svc-infra" }, state.TeamOf(world.Node.Id)!.Services!.ToArray());

        // A version for the team: from its maintainer, for a machine of that team; never from a member, nor for another team or a personal machine.
        FleetSignedAutomationVersion Version(Key signer, string machine, string? scope)
        {
            var text = FleetAutomationVersionDocuments.Serialize(new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, Organization, "nightly", 1, machine, false, true, [],
                null, null, "{\"sealed\":1}", signer.Id, signer.PublicKey, Time) { Scope = scope, Provider = scope is null ? null : "gateway" });
            return new FleetSignedAutomationVersion(text, signer.SignVersion(text));
        }

        Assert.IsTrue(FleetAutomationVersionDocuments.TryVerify(Version(world.Ada, world.Node.Id, "team:infra"), world.Devices, state, Organization, world.Node.Id, out var version,
            out var reason), reason);
        Assert.AreEqual("team:infra", version.Scope);
        Assert.IsFalse(FleetAutomationVersionDocuments.TryVerify(Version(world.Bea, world.Node.Id, "team:infra"), world.Devices, state with { Teams = state.Teams.SetItem("infra",
            state.Teams["infra"] with { RunsMemberAutomations = true }) }, Organization, world.Node.Id, out _, out reason), "a member gives the team none of its automations");
        Assert.IsFalse(FleetAutomationVersionDocuments.TryVerify(Version(world.Ada, world.Node.Id, "team:web"), world.Devices, state, Organization, world.Node.Id, out _, out _));
        Assert.IsFalse(FleetAutomationVersionDocuments.TryVerify(Version(world.Ada, world.Node.Id, "organization"), world.Devices, state, Organization, world.Node.Id, out _, out _),
            "a scope this version does not know");
        Assert.IsTrue(FleetAutomationVersionDocuments.TryVerify(Version(world.Ada, world.Node.Id, null), world.Devices, state, Organization, world.Node.Id, out var personal, out _));
        Assert.IsNull(personal.Scope);
        Assert.IsFalse(FleetAutomationVersionDocuments.Serialize(personal).Contains("scope", StringComparison.Ordinal), "a personal version writes the format as before");
    }

    [TestMethod]
    public void TheMachinesOfTheOrganizationRunItsAutomations_FromItsOwnersAndSigners_AndNoOther()
    {
        using var world = new World();
        Assert.IsFalse(FleetAccessDocuments.Serialize(Document(1, null, 4, owners: ["alex"])).Contains("orgMachines", StringComparison.Ordinal), "nothing of the organization: the format as before");
        var chain = new List<FleetSignedAccessDocument> { world.Founder.Sign(Document(1, null, 4, owners: ["alex"], signers: ["ada"])) };
        var before = FleetAccessDocuments.Verify(chain, world.Devices, Organization).State;
        var after = Document(2, Hash(chain), 4, owners: ["alex"], signers: ["ada"]) with { OrganizationMachines = [world.Node.Id], OrganizationServices = ["svc-org"] };
        CollectionAssert.AreEqual(new[] { "Build box becomes a machine of the organization", "The organization runs its automations as org-bot" },
            FleetAccessDocuments.Describe(before, after, id => id == world.Node.Id ? "Build box" : id == "svc-org" ? "org-bot" : null).ToArray());
        Assert.IsFalse(FleetAccessDocuments.SaySame(before, after));
        chain.Add(world.Ada.Sign(after));
        var state = FleetAccessDocuments.Verify(chain, world.Devices, Organization).State;
        Assert.IsTrue(state.IsOrganizationMachine(world.Node.Id));
        CollectionAssert.AreEqual(new[] { "svc-org" }, state.OrganizationServices.ToArray());

        FleetSignedAutomationVersion Version(Key signer, string? scope)
        {
            var text = FleetAutomationVersionDocuments.Serialize(new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, Organization, "report", 1, world.Node.Id, false,
                true, [], null, null, "{\"sealed\":1}", signer.Id, signer.PublicKey, Time) { Scope = scope, Provider = scope is null ? null : "gateway" });
            return new FleetSignedAutomationVersion(text, signer.SignVersion(text));
        }

        Assert.IsTrue(FleetAutomationVersionDocuments.TryVerify(Version(world.Ada, "organization"), world.Devices, state, Organization, world.Node.Id, out _, out var reason), reason);
        Assert.IsFalse(FleetAutomationVersionDocuments.TryVerify(Version(world.Bea, "organization"), world.Devices, state, Organization, world.Node.Id, out _, out _),
            "a member gives the organization no automation");
        Assert.IsFalse(FleetAutomationVersionDocuments.TryVerify(Version(world.Ada, null), world.Devices, state, Organization, world.Node.Id, out _, out _),
            "a machine of the organization runs no personal automation");
        Assert.IsFalse(FleetAutomationVersionDocuments.TryVerify(Version(world.Ada, "organization"), world.Devices, before, Organization, world.Node.Id, out _, out _),
            "an automation of the organization runs on a machine of the organization only");
    }

    [TestMethod]
    public void APersonalMachineGivenToAnotherPerson_IsSignedInTheDocument_AndItsNewOwnerDecidesThere()
    {
        using var world = new World();
        Assert.IsFalse(FleetAccessDocuments.Serialize(Document(1, null, 4, owners: ["alex"])).Contains("personalMachines", StringComparison.Ordinal),
            "no machine given away: the format as before");
        var chain = new List<FleetSignedAccessDocument> { world.Founder.Sign(Document(1, null, 4, owners: ["alex"], signers: ["ada"])) };
        var before = FleetAccessDocuments.Verify(chain, world.Devices, Organization).State;
        Assert.AreEqual("alex", before.MachineOwner(world.Node.Id, world.Devices), "the device list names the owner until a version says another");
        Assert.AreEqual("alex", FleetAccessState.OwnerOfMachine(null, world.Node.Id, world.Devices), "and before any version");

        var after = Document(2, Hash(chain), 4, owners: ["alex"], signers: ["ada"]) with { PersonalMachines = [new FleetAccessPersonalMachine(world.Node.Id, "bea")] };
        CollectionAssert.AreEqual(new[] { "Build box is Bea's: their devices manage it, and it runs their automations" },
            FleetAccessDocuments.Describe(before, after, id => id == world.Node.Id ? "Build box" : id == "bea" ? "Bea" : null).ToArray());
        Assert.IsFalse(FleetAccessDocuments.SaySame(before, after), "a signer is never told nothing changes");
        chain.Add(world.Ada.Sign(after));
        var state = FleetAccessDocuments.Verify(chain, world.Devices, Organization).State;
        Assert.AreEqual("bea", state.MachineOwner(world.Node.Id, world.Devices));
        Assert.IsTrue(FleetAccessDocuments.SaySame(state, after));
        Assert.IsNull(FleetAutomationVersionDocuments.CheckAccess("bea", world.Node.Id, world.Devices, state), "the machine runs the automations of its new owner");
        Assert.IsNotNull(FleetAutomationVersionDocuments.CheckAccess("alex", world.Node.Id, world.Devices, state), "and no more those of the person the device list names");

        // Given back: the signer reads that the mapping goes.
        CollectionAssert.AreEqual(new[] { "Build box is no longer Bea's: the person the device list names for it owns it again" },
            FleetAccessDocuments.Describe(state, Document(3, Hash(chain), 4, owners: ["alex"], signers: ["ada"]), id => id == world.Node.Id ? "Build box" : id == "bea" ? "Bea" : null)
                .ToArray());

        // What a version says of personal machines holds: each once, with its person, never also a team's or the organization's.
        Assert.IsTrue(FleetDevicePublicKey.TryParse(world.Ada.PublicKey, out var adaKey));
        foreach (var (wrong, why) in new (FleetAccessDocument, string)[]
        {
            (after with { PersonalMachines = [new(world.Node.Id, "bea"), new(world.Node.Id, "ada")] }, "a personal machine is named twice, or does not name its person"),
            (after with { PersonalMachines = [new(world.Node.Id, "")] }, "a personal machine is named twice, or does not name its person"),
            (after with { PersonalMachines = [new(world.Node.Id, "bea")], OrganizationMachines = [world.Node.Id] }, "a machine is both a person's and a team's or the organization's"),
            (after with { PersonalMachines = [new(world.Node.Id, "bea")], Teams = [Team("t1", ["ada"], [])], Machines = [new FleetAccessMachine(world.Node.Id, "t1")] },
                "a machine is both a person's and a team's or the organization's"),
        })
        {
            Assert.IsFalse(FleetAccessDocuments.TryReadSigned(world.Ada.Sign(wrong), adaKey, out _, out var reason));
            Assert.AreEqual(why, reason);
        }
    }

    [TestMethod]
    public void TheDocumentSaysWhoOwnsAnAutomationGivenAway_AndItsFormerOwnerGivesItNothingButItsRemoval()
    {
        using var world = new World();
        var chain = new List<FleetSignedAccessDocument> { world.Founder.Sign(Document(1, null, 4, owners: ["alex"], signers: ["ada"])) };
        var before = FleetAccessDocuments.Verify(chain, world.Devices, Organization).State;
        var after = Document(2, Hash(chain), 4, owners: ["alex"], signers: ["ada"]) with { Automations = [new FleetAccessAutomation("nightly", FleetPrincipalKinds.Person, "bea", true)] };
        CollectionAssert.AreEqual(new[] { "The automation nightly is Bea's", "The automation nightly waits for its owner to resume it" },
            FleetAccessDocuments.Describe(before, after, id => id == "bea" ? "Bea" : null).ToArray());
        chain.Add(world.Ada.Sign(after));
        var state = FleetAccessDocuments.Verify(chain, world.Devices, Organization).State;
        Assert.AreEqual("bea", state.Automations["nightly"].OwnerId);

        // A draft that leaves the automation out gives it back to whoever gives it versions: the signer reads it, alone or among other changes.
        CollectionAssert.AreEqual(new[] { "The automation nightly is no longer Bea's: it goes back to who gives it versions, and is no longer paused" },
            FleetAccessDocuments.Describe(state, Document(3, Hash(chain), 4, owners: ["alex"], signers: ["ada"]), id => id == "bea" ? "Bea" : null).ToArray());
        CollectionAssert.AreEqual(new[] { "Bea becomes an admin who signs", "The automation nightly is no longer Bea's: it goes back to who gives it versions, and is no longer paused" },
            FleetAccessDocuments.Describe(state, Document(3, Hash(chain), 4, owners: ["alex"], signers: ["ada", "bea"]), id => id == "bea" ? "Bea" : null).ToArray());

        // The node of Alex: his own version of it no longer runs; Bea's does not either while it waits.
        StringAssert.Contains(FleetAutomationVersionDocuments.CheckAccess("alex", world.Node.Id, world.Devices, state, "nightly"), "another owner");
        Assert.AreEqual(FleetAutomationVersionDocuments.WaitsForOwner, FleetAutomationVersionDocuments.CheckAccess("bea", world.Node.Id, world.Devices, state, "nightly"));
        Assert.IsNull(FleetAutomationVersionDocuments.CheckAccess("alex", world.Node.Id, world.Devices, state, "other"), "an automation the document does not name stays his");
    }

    [TestMethod]
    public void TheApproversOfAnAutomation_AreSignedWithTheDocument_AndLetTheMachineTakeASignedTakeOverFromThem()
    {
        using var world = new World();
        var first = world.Founder.Sign(Document(1, null, 4, owners: ["alex"]));
        Assert.IsFalse(first.Payload.Contains("approvers", StringComparison.Ordinal), "a version without approvers reads as before");
        var chain = new List<FleetSignedAccessDocument> { first };
        chain.Add(world.Founder.Sign(Document(2, Hash(chain), 4, owners: ["alex"]) with { Approvers = [new FleetAccessApprovers("a1", ["bea"])] }));
        var state = FleetAccessDocuments.Verify(chain, world.Devices, Organization).State;
        Assert.AreEqual(2, state.Number);
        Assert.IsTrue(state.Approves("a1", "bea", Time));
        Assert.IsFalse(state.Approves("a2", "bea", Time), "for that automation only");
        Assert.IsFalse((state with { Suspended = ["bea"] }).Approves("a1", "bea", Time), "a suspended person approves nothing");

        AssertBroken([first, world.Founder.Sign(Document(2, Hash([first]), 4, owners: ["alex"], suspended: ["bea"]) with { Approvers = [new FleetAccessApprovers("a1", ["bea"])] })],
            world, 2, "a suspended person answers approvals");
        AssertBroken([first, world.Founder.Sign(Document(2, Hash([first]), 4, owners: ["alex"]) with { Approvers = [new FleetAccessApprovers("a1", ["bea", "bea"])] })],
            world, 2, "the approvers of an automation do not hold");
        var names = new Dictionary<string, string> { ["bea"] = "Bea", ["a1"] = "Nightly" };
        CollectionAssert.AreEqual(new[] { "Bea answers the approvals of Nightly and takes its runs over" },
            FleetAccessDocuments.Describe(FleetAccessDocuments.Verify([first], world.Devices, Organization).State, Document(2, null, 4, owners: ["alex"]) with
            {
                Approvers = [new FleetAccessApprovers("a1", ["bea"])],
            }, id => names.GetValueOrDefault(id)).ToArray());
        CollectionAssert.AreEqual(new[] { "Bea no longer answers the approvals of Nightly" }, FleetAccessDocuments.Describe(state, Document(3, null, 4, owners: ["alex"]), id => names.GetValueOrDefault(id)).ToArray());
        Assert.IsTrue(FleetAccessDocuments.SaySame(state, Document(3, null, 4, owners: ["alex"]) with { Approvers = [new FleetAccessApprovers("a1", ["bea"])] }));
        Assert.IsFalse(FleetAccessDocuments.SaySame(state, Document(3, null, 4, owners: ["alex"])));

        // A take-over Bea signs for Alex's node: refused as before, unless the machine says Bea may make it.
        var text = FleetAutomationRunRequestDocuments.Serialize(new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, Organization, "a1", world.Node.Id, "run-1",
            world.Bea.Id, world.Bea.PublicKey, Time) { Action = FleetAutomationRunRequest.TakeOverAction });
        var signed = new FleetSignedAutomationRunRequest(text, world.Bea.SignBytes(FleetAutomationRunRequestDocuments.GetSignedBytes(text)));
        Assert.IsFalse(FleetAutomationRunRequestDocuments.TryVerify(signed, world.Devices, Organization, world.Node.Id, Time, out _, out var refused));
        Assert.AreEqual("the run request is signed by a device of another person than the owner of this machine", refused);
        Assert.IsTrue(FleetAutomationRunRequestDocuments.TryVerify(signed, world.Devices, Organization, world.Node.Id, Time,
            (request, person) => state.Approves(request.AutomationId, person, Time), out var taken, out var reason), reason);
        Assert.AreEqual("run-1", taken.RunId);
        Assert.IsFalse(FleetAutomationRunRequestDocuments.TryVerify(signed with { Signature = first.Signature }, world.Devices, Organization, world.Node.Id, Time,
            (request, person) => state.Approves(request.AutomationId, person, Time), out _, out _), "the signature is still checked");
    }

    [TestMethod]
    public void AnApproverGrantedUntilATime_IsSignedWithItsEnd_AndApprovesNothingAfterIt()
    {
        using var world = new World();
        var first = world.Founder.Sign(Document(1, null, 4, owners: ["alex"]));
        var end = Time.AddDays(2);
        var granted = new FleetAccessApprovers("a1", ["bea", "cy"]) { Until = new Dictionary<string, DateTimeOffset> { ["cy"] = end } };
        var second = world.Founder.Sign(Document(2, FleetAccessDocuments.Hash(first), 4, owners: ["alex"]) with { Approvers = [granted] });
        Assert.IsFalse(world.Founder.Sign(Document(1, null, 4, owners: ["alex"]) with { Approvers = [new FleetAccessApprovers("a1", ["bea"])] }).Payload.Contains("until", StringComparison.Ordinal),
            "an approver without an end writes none");
        var state = FleetAccessDocuments.Verify([first, second], world.Devices, Organization).State;

        Assert.IsTrue(state.Approves("a1", "cy", end.AddMinutes(-1)));
        Assert.IsFalse(state.Approves("a1", "cy", end), "at its end, and after it, it approves nothing");
        Assert.IsTrue(state.Approves("a1", "bea", end.AddYears(1)), "without an end, as long as the document names them");
        var names = new Dictionary<string, string> { ["cy"] = "Cy", ["a1"] = "Nightly" };
        StringAssert.Contains(FleetAccessDocuments.Describe(FleetAccessDocuments.Verify([first], world.Devices, Organization).State, Document(2, null, 4, owners: ["alex"]) with { Approvers = [granted] },
            id => names.GetValueOrDefault(id)).Single(line => line.StartsWith("Cy", StringComparison.Ordinal)), "until");
        Assert.IsFalse(FleetAccessDocuments.SaySame(state, Document(3, null, 4, owners: ["alex"]) with
        {
            Approvers = [granted with { Until = new Dictionary<string, DateTimeOffset> { ["cy"] = end.AddDays(1) } }],
        }), "another end is another version to sign");
        Assert.IsTrue(FleetAccessDocuments.SaySame(state, Document(3, null, 4, owners: ["alex"]) with { Approvers = [granted] }));
        AssertBroken([first, world.Founder.Sign(Document(2, FleetAccessDocuments.Hash(first), 4, owners: ["alex"]) with
        {
            Approvers = [new FleetAccessApprovers("a1", ["bea"]) { Until = new Dictionary<string, DateTimeOffset> { ["cy"] = end } }],
        })], world, 2, "the approvers of an automation do not hold");
    }

    private static void AssertBroken(IReadOnlyList<FleetSignedAccessDocument> chain, World world, long at, string reason, string organization = Organization,
        FleetDeviceListState? devices = null)
    {
        var verification = FleetAccessDocuments.Verify(chain, devices ?? world.Devices, organization);
        Assert.IsFalse(verification.IsValid);
        Assert.AreEqual((at, reason), (verification.BrokenAt, verification.Reason));
        Assert.AreEqual(at - 1, verification.State.Number, "the state is the document up to the last version that holds");
    }

    [TestMethod]
    public void TheDocumentNamesWhoRunsAnAutomationByHand_UntilTheirGrantEnds_AndTheSignerReadsEachChange()
    {
        using var world = new World();
        var chain = new List<FleetSignedAccessDocument> { world.Founder.Sign(Document(1, null, 4, owners: ["alex"], signers: ["ada"])) };
        var before = FleetAccessDocuments.Verify(chain, world.Devices, Organization).State;
        var until = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var after = Document(2, Hash(chain), 4, owners: ["alex"], signers: ["ada"]) with
        {
            Runners = [new FleetAccessApprovers("nightly", ["bea"]) { Until = new Dictionary<string, DateTimeOffset> { ["bea"] = until } }],
        };
        CollectionAssert.AreEqual(new[] { "Bea runs nightly by hand until 2026-12-01 00:00:00Z" },
            FleetAccessDocuments.Describe(before, after, id => id == "bea" ? "Bea" : null).ToArray());
        chain.Add(world.Ada.Sign(after));
        var state = FleetAccessDocuments.Verify(chain, world.Devices, Organization).State;
        Assert.IsTrue(state.Runs("nightly", "bea", until.AddDays(-1)));
        Assert.IsFalse(state.Runs("nightly", "bea", until), "not from the end of the grant, by the clock of the machine");
        Assert.IsFalse(state.Runs("other", "bea", until.AddDays(-1)), "that automation only");
        Assert.IsFalse(FleetAccessDocuments.SaySame(state, Document(3, Hash(chain), 4, owners: ["alex"], signers: ["ada"])));
        CollectionAssert.AreEqual(new[] { "Bea no longer runs nightly by hand" },
            FleetAccessDocuments.Describe(state, Document(3, Hash(chain), 4, owners: ["alex"], signers: ["ada"]), id => id == "bea" ? "Bea" : null).ToArray());

        // A suspended person runs nothing: a version that names one does not hold.
        var suspended = Document(3, Hash(chain), 4, owners: ["alex"], signers: ["ada"], suspended: ["bea"]) with { Runners = [new FleetAccessApprovers("nightly", ["bea"])] };
        Assert.IsFalse(FleetAccessDocuments.Verify([.. chain, world.Ada.Sign(suspended)], world.Devices, Organization).IsValid);
    }

    private static string Hash(IReadOnlyList<FleetSignedAccessDocument> chain) => FleetAccessDocuments.Hash(chain[^1]);

    private static FleetAccessTeam Team(string id, IReadOnlyList<string> maintainers, IReadOnlyList<string> members) => new(id, maintainers, members, false);

    private static FleetAccessDocument Document(long number, string? previous, long list, IReadOnlyList<string> owners, IReadOnlyList<string>? signers = null,
        IReadOnlyList<FleetAccessTeam>? teams = null, IReadOnlyList<FleetAccessMachine>? machines = null, IReadOnlyList<string>? suspended = null)
        => new(1, Organization, number, previous, list, owners, signers ?? [], teams ?? [], machines ?? [], suspended ?? [], string.Empty, Time);

    // A device list: Alex's first device, his node, then Ada's and Bea's first devices signed by Alex; Ada's later removed.
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
            entries.Add(Founder.Entry(new FleetDeviceListEntryPayload(1, Organization, 5, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Remove, Ada.Id, null,
                null, null, Founder.Id, Time)));
            WithAdaRemoved = FleetDeviceListVerifier.VerifyOrThrow(entries, Organization);
        }

        public Key Founder { get; } = new();

        public Key Node { get; } = new();

        public Key Ada { get; } = new();

        public Key Bea { get; } = new();

        public FleetDeviceListState Devices { get; }

        public FleetDeviceListState WithAdaRemoved { get; }

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

        public FleetSignedAccessDocument Sign(FleetAccessDocument document)
        {
            var text = FleetAccessDocuments.Serialize(document with { SignerDeviceId = Id });
            return new FleetSignedAccessDocument(text, Base64Url.EncodeToString(_key.SignData(FleetAccessDocuments.GetSignedBytes(text), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        }

        public string SignVersion(string text)
            => Base64Url.EncodeToString(_key.SignData(FleetAutomationVersionDocuments.GetSignedBytes(text), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public string SignBytes(byte[] bytes)
            => Base64Url.EncodeToString(_key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public void Dispose() => _key.Dispose();
    }
}
