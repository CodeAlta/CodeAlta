using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary>
/// `alta fleet access` (<c>doc/fleet/access-control.md</c>, agents and access): people, teams, grants, owners and transfers are
/// read; who can do what for those who may review; a change is only proposed, for a person to apply, and never by a sandboxed session.
/// </summary>
public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public async Task FleetAccess_IsReadOnlyForAgents_WhoProposeChangesForAPersonToApply()
    {
        var access = new FakeFleetAccess();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add<IAltaFleet>(new FakeFleet()).Add<IAltaFleetAccess>(access));

        var show = await dispatcher.InvokeAsync(["fleet", "access", "show"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, show.ExitCode, show.Transcript);
        var me = Records(show, "alta.fleet.access").Single();
        Assert.AreEqual(("Mia", "member"), (me.GetProperty("person").GetString(), me.GetProperty("role").GetString()));
        StringAssert.Contains(me.GetProperty("note").GetString(), "signer");

        var people = await dispatcher.InvokeAsync(["fleet", "access", "people"], caller: FleetAgent);
        CollectionAssert.AreEqual(new[] { "Alex", "Mia" }, Records(people, "alta.fleet.access.person").Select(static line => line.GetProperty("name").GetString()).ToArray());
        var owners = await dispatcher.InvokeAsync(["fleet", "access", "owners"], caller: FleetAgent);
        var first = Records(owners, "alta.fleet.access.owner").First();
        Assert.AreEqual(("nightly", true), (first.GetProperty("id").GetString(), first.GetProperty("ownerSuspended").GetBoolean()), "what waits for someone comes first");

        // What machines decide by: the identities automations run as, and the signed document with the owners of automations.
        var identities = await dispatcher.InvokeAsync(["fleet", "access", "identities"], caller: FleetAgent);
        Assert.AreEqual(("infra-bot", "team:Infra", true), (Records(identities, "alta.fleet.access.identity").Single().GetProperty("name").GetString(),
            Records(identities, "alta.fleet.access.identity").Single().GetProperty("owner").GetString(), Records(identities, "alta.fleet.access.identity").Single().GetProperty("enabled").GetBoolean()));
        var document = await dispatcher.InvokeAsync(["fleet", "access", "document"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, document.ExitCode, document.Transcript);
        Assert.AreEqual(3, Records(document, "alta.fleet.access.document").Single().GetProperty("number").GetInt64());
        var signedAutomation = Records(document, "alta.fleet.access.automation").Single();
        Assert.AreEqual(("nightly", "person:p3", true), (signedAutomation.GetProperty("id").GetString(), signedAutomation.GetProperty("owner").GetString(), signedAutomation.GetProperty("paused").GetBoolean()));
        var grants = await dispatcher.InvokeAsync(["fleet", "access", "grants"], caller: FleetAgent);
        Assert.IsTrue(Records(grants, "alta.fleet.access.grant").Single().GetProperty("missingKeys").GetBoolean());

        var review = await dispatcher.InvokeAsync(["fleet", "access", "review"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, review.ExitCode, "a member reviews no access");

        var invalidKind = await dispatcher.InvokeAsync(["fleet", "access", "propose", "owner", "--summary", "x", "--stdin"], caller: FleetAgent, stdin: "{}");
        Assert.AreEqual(AltaExitCodes.Usage, invalidKind.ExitCode);
        var notJson = await dispatcher.InvokeAsync(["fleet", "access", "propose", "role", "--summary", "x", "--stdin"], caller: FleetAgent, stdin: "admin");
        Assert.AreEqual(AltaExitCodes.Usage, notJson.ExitCode);
        var proposed = await dispatcher.InvokeAsync(["fleet", "access", "propose", "team-member", "--summary", "Max builds the release", "--stdin"], caller: FleetAgent,
            stdin: "{\"teamId\":\"t1\",\"personId\":\"p3\",\"role\":\"member\"}");
        Assert.AreEqual(AltaExitCodes.Success, proposed.ExitCode, proposed.Transcript);
        Assert.AreEqual("open", Records(proposed, "alta.fleet.access.proposal").Single().GetProperty("state").GetString());
        Assert.AreEqual(("team-member", "session-a"), (access.Proposed.Single().Kind, access.Proposed.Single().SessionId), "the proposal names the session that made it");

        // No command applies a change: there is nothing to grant, transfer or sign with.
        foreach (var verb in new[] { "grant", "transfer", "sign", "role" })
            Assert.AreNotEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["fleet", "access", verb], caller: FleetAgent)).ExitCode, verb);
    }

    private sealed class FakeFleetAccess : IAltaFleetAccess
    {
        public List<(string Kind, string Summary, string Payload, string? SessionId)> Proposed { get; } = [];

        public Task<AltaFleetAccess> ReadAccessAsync(string? server, CancellationToken cancellationToken)
        {
            var mia = new AltaFleetPerson("p2", "Mia", "member", "active", ["Infra (member)"], 1);
            return Task.FromResult(new AltaFleetAccess("ok", null)
            {
                Server = "https://fleet.test",
                Me = mia,
                Capabilities = ["access.view", "statistics.view-own"],
                Pending = ["Max is a member of Infra"],
                People = [new AltaFleetPerson("p1", "Alex", "owner", "active", [], 2), mia],
                Owners = [new AltaFleetOwner("machine", "m1", "Build box", "team:Infra", false, false), new AltaFleetOwner("automation", "nightly", null, "person:Sam", true, true)],
                Identities = [new AltaFleetIdentity("svc1", "infra-bot", "team:Infra", true)],
                Grants = [new AltaFleetGrant("g1", "team:Infra", "session.view-content", "resource:session:s1", "active", null) { MissingKeys = true }],
                Signed = new AltaFleetSignedAccess(3, new Dictionary<string, IReadOnlyList<string>> { ["t1"] = ["svc1"] }, new Dictionary<string, string> { ["m1"] = "t1" }, [], [],
                    [new AltaFleetSignedAutomation("nightly", "person:p3", true)]),
            });
        }

        public Task<AltaFleetAccessProposal> ProposeAccessAsync(string? server, string kind, string summary, string payload, string? sessionId, CancellationToken cancellationToken)
        {
            Proposed.Add((kind, summary, payload, sessionId));
            return Task.FromResult(new AltaFleetAccessProposal("ok", null, "pr1", kind, summary, "open"));
        }
    }
}
