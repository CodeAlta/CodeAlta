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
            });
        }

        public Task<AltaFleetAccessProposal> ProposeAccessAsync(string? server, string kind, string summary, string payload, string? sessionId, CancellationToken cancellationToken)
        {
            Proposed.Add((kind, summary, payload, sessionId));
            return Task.FromResult(new AltaFleetAccessProposal("ok", null, "pr1", kind, summary, "open"));
        }
    }
}
