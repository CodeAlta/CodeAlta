using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The rules of access control (<c>doc/fleet/access-control.md</c>, roles and grants): denied unless granted; management flows
/// down the scopes, content never does; an admin is not a reader of content; teams own what their maintainers manage and their
/// members use; grants of content apply to the resource itself only; a suspended person holds nothing.
/// </summary>
[TestClass]
public sealed class AccessPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AnAdminManagesEverything_ButReadsNoContentItDoesNotOwn()
    {
        var access = Snapshot(people: [Person("owner", FleetOrganizationRoles.Owner), Person("admin", FleetOrganizationRoles.Admin), Person("bea", FleetOrganizationRoles.Member)]);
        var session = AccessTarget.Resource(FleetResourceKinds.Session, "s1", FleetPrincipalKinds.Person, "bea");
        var machine = AccessTarget.Resource(FleetResourceKinds.Machine, "m1", FleetPrincipalKinds.Person, "bea");

        Assert.IsTrue(AccessPolicy.Allows(access, "admin", "session.view-facts", session, Now), "an admin sees that a session exists and what it costs");
        Assert.IsFalse(AccessPolicy.Allows(access, "admin", "session.view-content", session, Now), "never what it says");
        Assert.IsFalse(AccessPolicy.Allows(access, "owner", "session.interact", session, Now), "not even an owner drives the session of another person");
        Assert.IsFalse(AccessPolicy.Allows(access, "admin", "machine.logs", machine, Now), "the log of a machine is its owner's");
        Assert.IsFalse(AccessPolicy.Allows(access, "admin", "machine.start", machine, Now), "a personal machine is not an admin's to start sessions on");
        Assert.IsTrue(AccessPolicy.Allows(access, "admin", "machine.manage", machine, Now));
        Assert.IsTrue(AccessPolicy.Allows(access, "bea", "session.view-content", session, Now), "the owner of a session reads it");
        Assert.IsTrue(AccessPolicy.Allows(access, "admin", "access.manage", AccessTarget.Organization, Now));
        Assert.IsFalse(AccessPolicy.Allows(access, "bea", "access.manage", AccessTarget.Organization, Now), "a member does not administer");
        Assert.IsTrue(AccessPolicy.Allows(access, "bea", "access.view", AccessTarget.Organization, Now));
    }

    [TestMethod]
    public void ATeamsMaintainersManageWhatItOwns_ItsMembersUseIt_OthersDoNot()
    {
        var access = Snapshot(
            people: [Person("owner", FleetOrganizationRoles.Owner), Person("mia", FleetOrganizationRoles.Member), Person("max", FleetOrganizationRoles.Member),
                Person("out", FleetOrganizationRoles.Member)],
            teams: [Team("infra")],
            members: [Member("infra", "mia", FleetTeamRoles.Maintainer), Member("infra", "max", FleetTeamRoles.Member)]);
        var machine = AccessTarget.Resource(FleetResourceKinds.Machine, "m1", FleetPrincipalKinds.Team, "infra");
        var run = AccessTarget.Resource(FleetResourceKinds.Run, "r1", FleetPrincipalKinds.Team, "infra");

        Assert.IsTrue(AccessPolicy.Allows(access, "mia", "machine.manage", machine, Now));
        Assert.IsTrue(AccessPolicy.Allows(access, "mia", "access.manage", AccessTarget.Team("infra"), Now), "a maintainer manages the members of the team");
        Assert.IsFalse(AccessPolicy.Allows(access, "max", "machine.manage", machine, Now));
        Assert.IsTrue(AccessPolicy.Allows(access, "max", "machine.start", machine, Now), "a member uses the team's machines");
        Assert.IsTrue(AccessPolicy.Allows(access, "max", "run.view-content", run, Now), "and sees the content of the team's work");
        Assert.IsFalse(AccessPolicy.Allows(access, "max", "access.manage", AccessTarget.Team("infra"), Now));
        Assert.IsFalse(AccessPolicy.Allows(access, "out", "machine.start", machine, Now), "someone outside the team does not");
        Assert.IsFalse(AccessPolicy.Allows(access, "owner", "run.view-content", run, Now), "nor an owner of the organization, for content");
        Assert.IsTrue(AccessPolicy.Allows(access, "owner", "machine.manage", machine, Now), "who manages it all the same");

        var dissolved = access with { Teams = [Team("infra") with { DissolvedAt = Now }] };
        Assert.IsFalse(AccessPolicy.Allows(dissolved, "mia", "machine.manage", machine, Now), "a dissolved team gives nothing");
    }

    [TestMethod]
    public void GrantsFlowDownTheScopes_ButContentIsGrantedOnTheResourceItself()
    {
        var access = Snapshot(
            people: [Person("owner", FleetOrganizationRoles.Owner), Person("gil", FleetOrganizationRoles.Guest), Person("ana", FleetOrganizationRoles.Member)],
            teams: [Team("web")],
            members: [Member("web", "ana", FleetTeamRoles.Member)],
            grants:
            [
                Grant("g1", FleetPrincipalKinds.Person, "gil", "automation.run", FleetScopeKinds.Team, "web"),
                Grant("g2", FleetPrincipalKinds.Person, "gil", "session.view-content", FleetScopeKinds.Team, "web"),
                Grant("g3", FleetPrincipalKinds.Team, "web", "session.view-content", FleetScopeKinds.Resource, "session:s9"),
                Grant("g4", FleetPrincipalKinds.Person, "gil", "automation.edit", FleetScopeKinds.Project, "github.com/acme/site", expires: Now.AddMinutes(-1)),
                Grant("g5", FleetPrincipalKinds.Person, "gil", "automation.view", FleetScopeKinds.Project, "github.com/acme/site"),
            ]);
        var teamAutomation = AccessTarget.Resource(FleetResourceKinds.Automation, "a1", FleetPrincipalKinds.Team, "web");
        var teamSession = AccessTarget.Resource(FleetResourceKinds.Session, "s1", FleetPrincipalKinds.Service, "svc");
        var sharedSession = AccessTarget.Resource(FleetResourceKinds.Session, "s9", FleetPrincipalKinds.Person, "owner");
        var projectAutomation = AccessTarget.Resource(FleetResourceKinds.Automation, "a2", FleetPrincipalKinds.Person, "owner", "github.com/acme/site");

        Assert.IsTrue(AccessPolicy.Decide(access, "gil", "automation.run", teamAutomation, Now) is { Allowed: true, Source: "grant:g1" }, "a grant on a team covers what it owns");
        Assert.IsFalse(AccessPolicy.Allows(access, "gil", "session.view-content", teamSession with { TeamId = "web" }, Now), "content is never granted down a scope");
        Assert.IsTrue(AccessPolicy.Allows(access, "ana", "session.view-content", sharedSession, Now), "a grant to a team reaches its members, on that session");
        Assert.IsFalse(AccessPolicy.Allows(access, "gil", "automation.edit", projectAutomation, Now), "an expired grant gives nothing");
        Assert.IsTrue(AccessPolicy.Allows(access, "gil", "automation.view", projectAutomation, Now), "a grant on a project covers what lives in it");
        Assert.IsFalse(AccessPolicy.Allows(access, "gil", "access.view", AccessTarget.Organization, Now), "a guest has what was granted, nothing else");
    }

    [TestMethod]
    public void ASuspendedPersonHoldsNothing_AndAnAuditorReadsWithoutChanging()
    {
        var access = Snapshot(people: [Person("owner", FleetOrganizationRoles.Owner), Person("old", FleetOrganizationRoles.Admin) with { Status = FleetPersonStatuses.Suspended },
            Person("aud", FleetOrganizationRoles.Auditor)]);
        var own = AccessTarget.Resource(FleetResourceKinds.Machine, "m1", FleetPrincipalKinds.Person, "old");

        Assert.IsTrue(AccessPolicy.Decide(access, "old", "machine.manage", own, Now) is { Allowed: false, Source: "suspended" });
        Assert.IsFalse(AccessPolicy.IsSigner(access, "old"), "a suspended admin signs nothing");
        Assert.IsTrue(AccessPolicy.IsSigner(access, "owner"));
        Assert.IsTrue(AccessPolicy.Allows(access, "aud", "audit.read", AccessTarget.Organization, Now));
        Assert.IsTrue(AccessPolicy.Allows(access, "aud", "access.review", AccessTarget.Organization, Now));
        Assert.IsTrue(AccessPolicy.Allows(access, "aud", "machine.view", own, Now), "facts");
        Assert.IsFalse(AccessPolicy.Allows(access, "aud", "machine.manage", own, Now), "no change");
        Assert.IsFalse(AccessPolicy.Allows(access, "aud", "access.manage", AccessTarget.Organization, Now));
        CollectionAssert.Contains(AccessPolicy.OrganizationCapabilities(access, "aud", Now).ToList(), "audit.export");
        CollectionAssert.DoesNotContain(AccessPolicy.OrganizationCapabilities(access, "aud", Now).ToList(), "access.sign");
        Assert.ThrowsExactly<ArgumentException>(() => AccessPolicy.Decide(access, "owner", "machine.fly", own, Now));
    }

    [TestMethod]
    public void TheApprovalsOfAnAutomationGoToItsOwnerItsTeamAndItsGrants_NotToEveryAdmin()
    {
        var access = Snapshot(
            people: [Person("owner", FleetOrganizationRoles.Owner), Person("admin", FleetOrganizationRoles.Admin), Person("mia", FleetOrganizationRoles.Member),
                Person("max", FleetOrganizationRoles.Member), Person("oncall", FleetOrganizationRoles.Member), Person("bea", FleetOrganizationRoles.Member),
                Person("old", FleetOrganizationRoles.Member) with { Status = FleetPersonStatuses.Suspended }],
            teams: [Team("infra")],
            members: [Member("infra", "mia", FleetTeamRoles.Maintainer), Member("infra", "max", FleetTeamRoles.Member), Member("infra", "old", FleetTeamRoles.Member)],
            grants: [Grant("g1", FleetPrincipalKinds.Person, "oncall", "automation.approve", FleetScopeKinds.Resource, "automation:a1"),
                Grant("g2", FleetPrincipalKinds.Person, "bea", "automation.approve", FleetScopeKinds.Resource, "automation:other")]);

        CollectionAssert.AreEqual(new[] { "mia", "max", "oncall" },
            AccessPolicy.Approvers(access, AccessTarget.Resource(FleetResourceKinds.Automation, "a1", FleetPrincipalKinds.Team, "infra"), Now).ToArray(),
            "the team that owns it and who is granted it; a suspended member, the admins and a grant on another automation do not");
        CollectionAssert.AreEqual(new[] { "bea" },
            AccessPolicy.Approvers(access, AccessTarget.Resource(FleetResourceKinds.Automation, "a2", FleetPrincipalKinds.Person, "bea"), Now).ToArray(), "a person's own");
        CollectionAssert.AreEqual(new[] { "owner", "admin" },
            AccessPolicy.Approvers(access, AccessTarget.Resource(FleetResourceKinds.Automation, "a3", FleetPrincipalKinds.Organization, "org"), Now).ToArray(),
            "the organization's, by the roles that run it");
    }

    [TestMethod]
    public void AnApproverByGrantsWithAnEnd_IsListedWithTheLatestEnd_AndNotOnceItPassed()
    {
        var access = Snapshot(
            people: [Person("bea", FleetOrganizationRoles.Member), Person("cy", FleetOrganizationRoles.Member), Person("dan", FleetOrganizationRoles.Member)],
            grants: [Grant("g1", FleetPrincipalKinds.Person, "cy", "automation.approve", FleetScopeKinds.Resource, "automation:a1", expires: Now.AddDays(1)),
                Grant("g2", FleetPrincipalKinds.Person, "cy", "automation.approve", FleetScopeKinds.Resource, "automation:a1", expires: Now.AddDays(3)),
                Grant("g3", FleetPrincipalKinds.Person, "dan", "automation.approve", FleetScopeKinds.Resource, "automation:a1"),
                Grant("g4", FleetPrincipalKinds.Person, "dan", "automation.approve", FleetScopeKinds.Resource, "automation:a1", expires: Now.AddDays(1))]);
        var automation = AccessTarget.Resource(FleetResourceKinds.Automation, "a1", FleetPrincipalKinds.Person, "bea");

        CollectionAssert.AreEqual(new (string, DateTimeOffset?)[] { ("bea", null), ("cy", Now.AddDays(3)), ("dan", null) },
            AccessPolicy.ApproversWithEnds(access, automation, Now).ToArray(), "its owner and a grant without an end have none; the latest end of the grants");
        CollectionAssert.AreEqual(new (string, DateTimeOffset?)[] { ("bea", null), ("dan", null) }, AccessPolicy.ApproversWithEnds(access, automation, Now.AddDays(4)).ToArray());
    }

    [TestMethod]
    public void WhoIsGrantedToRunAnAutomation_IsARunner_AnAdminToo()
    {
        var access = Snapshot(
            people: [Person("owner", FleetOrganizationRoles.Owner), Person("admin", FleetOrganizationRoles.Admin), Person("cy", FleetOrganizationRoles.Member),
                Person("old", FleetOrganizationRoles.Admin) with { Status = FleetPersonStatuses.Suspended }],
            teams: [Team("infra")],
            grants: [Grant("g1", FleetPrincipalKinds.Person, "admin", "automation.run", FleetScopeKinds.Resource, "automation:a1", expires: Now.AddDays(2)),
                Grant("g2", FleetPrincipalKinds.Person, "cy", "automation.run", FleetScopeKinds.Team, "infra"),
                Grant("g3", FleetPrincipalKinds.Person, "old", "automation.run", FleetScopeKinds.Resource, "automation:a1"),
                Grant("g4", FleetPrincipalKinds.Person, "owner", "automation.run", FleetScopeKinds.Resource, "automation:other")]);
        var automation = AccessTarget.Resource(FleetResourceKinds.Automation, "a1", FleetPrincipalKinds.Team, "infra");

        // The admin maintains no team: her role does not make her a runner on the machines, her grant does.
        CollectionAssert.AreEqual(new (string, DateTimeOffset?)[] { ("admin", Now.AddDays(2)), ("cy", null) }, AccessPolicy.RunnersWithEnds(access, automation, Now).ToArray(),
            "a grant to an admin counts as any other; a suspended person and a grant on another automation do not");
        CollectionAssert.AreEqual(new (string, DateTimeOffset?)[] { ("cy", null) }, AccessPolicy.RunnersWithEnds(access, automation, Now.AddDays(3)).ToArray(), "once it ended, it does not");
    }

    [TestMethod]
    public void TheOnCallOfARotation_IsTheTurnOfTheTime_SkippingWhoLeftTheTeamOrIsSuspended()
    {
        var access = Snapshot(
            people: [Person("mia", FleetOrganizationRoles.Member), Person("max", FleetOrganizationRoles.Member), Person("old", FleetOrganizationRoles.Member) with { Status = FleetPersonStatuses.Suspended },
                Person("gone", FleetOrganizationRoles.Member)],
            teams: [Team("infra")],
            members: [Member("infra", "mia", FleetTeamRoles.Maintainer), Member("infra", "max", FleetTeamRoles.Member), Member("infra", "old", FleetTeamRoles.Member)]);
        var rotation = new OnCallRotationRecord("org", "infra", ["mia", "max", "old", "gone"], Now, 12, Now, "mia");

        Assert.AreEqual(("mia", Now.AddHours(12)), AccessService.OnCallNow(rotation, access, Now.AddHours(1)));
        Assert.AreEqual(("max", Now.AddHours(24)), AccessService.OnCallNow(rotation, access, Now.AddHours(13)));
        Assert.AreEqual("mia", AccessService.OnCallNow(rotation, access, Now.AddHours(25)).PersonId, "a suspended person's turn, and that of who left the team, go to the next in turn");
        Assert.AreEqual("mia", AccessService.OnCallNow(rotation, access, Now.AddHours(48)).PersonId, "the turns go round");
        Assert.AreEqual(("mia", Now.AddHours(12)), AccessService.OnCallNow(rotation, access, Now.AddHours(-3)), "before its start, its first turn");
        Assert.AreEqual(((string?)null, (DateTimeOffset?)null), AccessService.OnCallNow(rotation with { People = ["old", "gone"] }, access, Now));
    }

    private static AccessSnapshot Snapshot(IReadOnlyList<PersonRecord>? people = null, IReadOnlyList<TeamRecord>? teams = null, IReadOnlyList<TeamMemberRecord>? members = null,
        IReadOnlyList<GrantRecord>? grants = null)
        => new(people ?? [], teams ?? [], members ?? [], grants ?? [], [], []);

    private static PersonRecord Person(string id, string role) => new(id, "org", id, role, Now);

    private static TeamRecord Team(string id) => new(id, "org", id, false, Now, "owner", null, 1);

    private static TeamMemberRecord Member(string team, string person, string role) => new(team, person, role, Now);

    private static GrantRecord Grant(string id, string subjectKind, string subjectId, string capability, string scopeKind, string? scopeId, DateTimeOffset? expires = null)
        => new(id, "org", subjectKind, subjectId, capability, scopeKind, scopeId, FleetGrantStatuses.Active, Now, "owner", expires, null);
}
