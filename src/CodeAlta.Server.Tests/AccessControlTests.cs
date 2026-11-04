using System.Net;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// Access control on the REST API (<c>doc/fleet/access-control.md</c>): people invited and their roles, the last owner kept,
/// teams run by their maintainers, grants with content granted on one resource only and sealed content waiting for its keys,
/// transfers accepted by the new owner, the signed access document chained and signed by signers only, deprovisioning, and
/// every change audited, refused ones too.
/// </summary>
[TestClass]
public sealed class AccessControlTests
{
    [TestMethod]
    public async Task AnInvitedPersonJoinsAsAMember_WhoReadsButDoesNotAdminister_AndTheAuditIsForOwners()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Home server");
        // The device of the owner signs an entry of their own person first (a machine, as setting a server up does with its recovery key).
        var (setup, _) = await server.SetUpWithNodeAsync(owner, node);
        var (bea, beaId) = await AddPersonAsync(server, owner, setup, "Bea");

        var self = (await server.SendAsync(bea, HttpMethod.Get, "/api/v1/access/self")).Read<AccessSelfDto>();
        Assert.AreEqual((FleetOrganizationRoles.Member, beaId, 1), (self.Person.Role, self.Person.Id, self.Person.Devices));
        Assert.IsFalse(self.Advanced, "a household of an owner and members is not advanced");
        Assert.IsFalse(self.Signer);
        CollectionAssert.Contains(self.Capabilities.ToList(), "access.view");
        CollectionAssert.DoesNotContain(self.Capabilities.ToList(), "access.manage");

        var people = (await server.SendAsync(bea, HttpMethod.Get, "/api/v1/people")).Read<AccessListDto<PersonAccessDto>>();
        CollectionAssert.AreEquivalent(new[] { "Alex", "Bea" }, people.Items.Select(static person => person.DisplayName).ToArray());

        var refused = await server.SendAsync(bea, HttpMethod.Post, "/api/v1/people", new InvitePersonRequest("Cy"));
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.Status, "a member invites nobody");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(bea, HttpMethod.Get, "/api/v1/audit")).Status, "the audit log is for owners, admins and auditors");
        var audit = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500");
        Assert.AreEqual(HttpStatusCode.OK, audit.Status, audit.Body.ToString());
        var actions = audit.Read<ListResponse<AuditRecordDto>>().Items.Select(static record => (record.Action, record.Result)).ToList();
        CollectionAssert.Contains(actions, ("person.invite", AuditEntry.Success));
        CollectionAssert.Contains(actions, ("person.invite", FleetErrorCodes.Forbidden), "a refused change is audited too");
    }

    [TestMethod]
    public async Task OnlyAnOwnerMakesAdmins_AndTheLastOwnerKeepsTheRole()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Home server");
        // The device of the owner signs an entry of their own person first (a machine, as setting a server up does with its recovery key).
        var (setup, _) = await server.SetUpWithNodeAsync(owner, node);
        var (admin, adminId) = await AddPersonAsync(server, owner, setup, "Ada", FleetOrganizationRoles.Admin);
        var (_, beaId) = await AddPersonAsync(server, owner, setup, "Bea");

        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(admin, HttpMethod.Put, $"/api/v1/people/{beaId}/role", new SetRoleRequest(FleetOrganizationRoles.Admin))).Status,
            "an admin makes no admin");
        var auditor = await server.SendAsync(admin, HttpMethod.Put, $"/api/v1/people/{beaId}/role", new SetRoleRequest(FleetOrganizationRoles.Auditor));
        Assert.AreEqual(HttpStatusCode.OK, auditor.Status, auditor.Body.ToString());
        Assert.AreEqual(FleetOrganizationRoles.Auditor, auditor.Read<PersonAccessDto>().Role);

        var last = await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/people/{setup.Owner.Id}/role", new SetRoleRequest(FleetOrganizationRoles.Member));
        Assert.AreEqual((HttpStatusCode.Conflict, CoordinatorErrorCodes.LastOwner), (last.Status, last.Code));
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/people/{adminId}/role", new SetRoleRequest(FleetOrganizationRoles.Owner))).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(admin, HttpMethod.Put, $"/api/v1/people/{setup.Owner.Id}/role", new SetRoleRequest(FleetOrganizationRoles.Member))).Status,
            "with another owner, the first one may step down");
        Assert.IsTrue((await server.SendAsync(admin, HttpMethod.Get, "/api/v1/access/self")).Read<AccessSelfDto>().Advanced, "an auditor makes the organization advanced");
    }

    [TestMethod]
    public async Task ATeamIsRunByItsMaintainers_AndGrantsOfContentAreOnOneResource_SealedOnesWaitingForTheirKeys()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (mia, miaId) = await AddPersonAsync(server, owner, setup, "Mia");
        var (max, maxId) = await AddPersonAsync(server, owner, setup, "Max");

        var team = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("Infra"))).Read<TeamDto>();
        Assert.AreEqual(HttpStatusCode.Conflict, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("infra"))).Status, "names are unique");
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{miaId}", new SetTeamMemberRequest(FleetTeamRoles.Maintainer))).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{maxId}", new SetTeamMemberRequest(FleetTeamRoles.Member))).Status,
            "nobody adds themselves to a team");
        var added = await server.SendAsync(mia, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{maxId}", new SetTeamMemberRequest(FleetTeamRoles.Member));
        Assert.AreEqual(HttpStatusCode.OK, added.Status, added.Body.ToString());
        Assert.AreEqual(3, added.Read<TeamDto>().Members.Count, "its maker, its maintainer, its member");

        var down = await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/teams/{team.Id}/members/{setup.Owner.Id}");
        Assert.AreEqual(HttpStatusCode.OK, down.Status, down.Body.ToString());
        var lastMaintainer = await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{miaId}", new SetTeamMemberRequest(FleetTeamRoles.Member));
        Assert.AreEqual(HttpStatusCode.Conflict, lastMaintainer.Status, "a team with members keeps a maintainer");

        var scoped = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/grants",
            new CreateGrantRequest(FleetPrincipalKinds.Person, maxId, "session.view-content", FleetScopeKinds.Team, team.Id));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.AccessInvalid), (scoped.Status, scoped.Code), "content never flows down a scope");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/grants",
            new CreateGrantRequest(FleetPrincipalKinds.Person, maxId, "machine.logs", FleetScopeKinds.Resource, "machine:" + machine.Id, Sealed: true))).Status,
            "what is sealed for a grant is the content of a session");
        await PublishSessionAsync(server, node, "s1");
        var byOwner = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/grants",
            new CreateGrantRequest(FleetPrincipalKinds.Person, maxId, "session.view-content", FleetScopeKinds.Resource, "session:s1", Sealed: true));
        Assert.AreEqual(HttpStatusCode.Created, byOwner.Status, byOwner.Body.ToString());
        var pending = byOwner.Read<GrantDto>();
        Assert.AreEqual(FleetGrantStatuses.PendingKeys, pending.Status, "sealed content waits for a device that holds its keys");
        var keys = new ConfirmGrantKeysRequest("sealed", [new GrantKeyWrapDto(max.DeviceId, "wrapped")]);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/grants/{pending.Id}/keys", keys)).Status,
            "only a device of the owner of the content gives its keys");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/grants/{pending.Id}/keys")).Status,
            "the keys of a session are given, not only said to be");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/grants/{pending.Id}/keys",
            new ConfirmGrantKeysRequest("sealed", [new GrantKeyWrapDto(mia.DeviceId, "wrapped")]))).Status, "a copy goes to the devices of the grantee only");
        var active = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/grants/{pending.Id}/keys", keys);
        Assert.AreEqual(HttpStatusCode.OK, active.Status, active.Body.ToString());
        Assert.AreEqual(FleetGrantStatuses.Active, active.Read<GrantDto>().Status);
        var given = (await server.SendAsync(max, HttpMethod.Get, "/api/v1/grant-keys")).Read<AccessListDto<GrantKeyDto>>().Items.Single();
        Assert.AreEqual((pending.Id, "s1", machine.Id, "sealed", "wrapped"), (given.GrantId, given.SessionId, given.MachineId, given.Sealed, given.Wrapped));
        Assert.AreEqual(0, (await server.SendAsync(mia, HttpMethod.Get, "/api/v1/grant-keys")).Read<AccessListDto<GrantKeyDto>>().Items.Count, "nothing for another person");
        var listed = (await server.SendAsync(max, HttpMethod.Get, "/api/v1/sessions")).Read<ListResponse<SessionDto>>().Items;
        Assert.AreEqual("s1", listed.Single().Id, "the grantee sees the session whose content they were given");
        var teamGrant = await server.SendAsync(mia, HttpMethod.Post, "/api/v1/grants",
            new CreateGrantRequest(FleetPrincipalKinds.Person, maxId, "automation.edit", FleetScopeKinds.Team, team.Id));
        Assert.AreEqual(HttpStatusCode.Created, teamGrant.Status, "a maintainer grants on the team");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(mia, HttpMethod.Post, "/api/v1/grants",
            new CreateGrantRequest(FleetPrincipalKinds.Person, maxId, "automation.edit", FleetScopeKinds.Organization))).Status, "not on the organization");
        var maxGrants = (await server.SendAsync(max, HttpMethod.Get, "/api/v1/grants")).Read<AccessListDto<GrantDto>>();
        Assert.AreEqual(2, maxGrants.Items.Count, "a person sees the grants made to them");
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(max, HttpMethod.Delete, $"/api/v1/grants/{pending.Id}")).Status, "and may give one back");
        Assert.AreEqual(0, (await server.SendAsync(max, HttpMethod.Get, "/api/v1/grant-keys")).Read<AccessListDto<GrantKeyDto>>().Items.Count, "its keys go with it");

        var review = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/review");
        Assert.AreEqual(HttpStatusCode.OK, review.Status, review.Body.ToString());
        var rows = review.Read<AccessReviewDto>().Rows;
        Assert.IsTrue(rows.Any(row => row.PrincipalId == miaId && row.Source == $"team:{team.Id}:maintainer"));
        Assert.IsTrue(rows.Any(row => row.PrincipalId == maxId && row.Capability == "automation.edit" && row.Source.StartsWith("grant:", StringComparison.Ordinal)));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Get, "/api/v1/access/review")).Status);
    }

    [TestMethod]
    public async Task AnAutomationGivenToAPersonCarriesItsHandOverForTheirDevicesOnly_AndItsFormerOwnerGivesItNoMoreVersions()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Mac mini");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (bea, beaId) = await AddPersonAsync(server, owner, setup, "Bea");
        var (mia, _) = await AddPersonAsync(server, owner, setup, "Mia");
        FleetSignedAutomationVersion Version(long number, bool removed = false)
        {
            var text = FleetAutomationVersionDocuments.Serialize(new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, setup.Organization.Id, "nightly", number,
                machine.Id, removed, !removed, removed ? [] : ["hourly"], null, null, removed ? null : "{\"sealed\":\"for the machine\"}", owner.DeviceId, owner.PublicKey,
                server.Clock.Now));
            return new FleetSignedAutomationVersion(text, owner.Sign(FleetAutomationVersionDocuments.GetSignedBytes(text)));
        }

        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/automations/nightly", new PutAutomationRequest(Version(1)))).Status);
        var toMia = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Automation, "nightly", FleetPrincipalKinds.Person,
            beaId, new TransferHandoverDto("sealed", [new GrantKeyWrapDto(mia.DeviceId, "wrapped")])));
        Assert.AreEqual(HttpStatusCode.BadRequest, toMia.Status, "a hand-over goes to the devices of who takes it only");
        var offer = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Automation, "nightly", FleetPrincipalKinds.Person,
            beaId, new TransferHandoverDto("sealed", [new GrantKeyWrapDto(bea.DeviceId, "wrapped")])));
        Assert.AreEqual(HttpStatusCode.Created, offer.Status, offer.Body.ToString());
        var transfer = offer.Read<TransferDto>();
        Assert.IsTrue(transfer.HasHandover);
        var copy = await server.SendAsync(bea, HttpMethod.Get, $"/api/v1/transfers/{transfer.Id}/handover");
        Assert.AreEqual(HttpStatusCode.OK, copy.Status, copy.Body.ToString());
        Assert.AreEqual(("nightly", setup.Owner.Id, "sealed", "wrapped"), (copy.Read<TransferHandoverCopyDto>().ResourceId, copy.Read<TransferHandoverCopyDto>().FromPersonId,
            copy.Read<TransferHandoverCopyDto>().Sealed, copy.Read<TransferHandoverCopyDto>().Wrapped));
        Assert.AreEqual(HttpStatusCode.NotFound, (await server.SendAsync(mia, HttpMethod.Get, $"/api/v1/transfers/{transfer.Id}/handover")).Status, "nothing for another device");

        // Before Bea takes it, its owner still changes it; once she did, the automation is hers: no new version from him, only its removal.
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/automations/nightly", new PutAutomationRequest(Version(2)))).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(bea, HttpMethod.Post, $"/api/v1/transfers/{transfer.Id}/accept")).Status);
        var refused = await server.SendAsync(owner, HttpMethod.Put, "/api/v1/automations/nightly", new PutAutomationRequest(Version(3)));
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.Status, "an automation given away gets no new version from its former owner");
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/automations/nightly", new PutAutomationRequest(Version(4, removed: true)))).Status,
            "its former owner takes it off his machines");
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(bea, HttpMethod.Get, $"/api/v1/transfers/{transfer.Id}/handover")).Status, "the hand-over stays for her devices");

        // A declined offer takes its hand-over with it.
        var back = await server.SendAsync(bea, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Automation, "nightly", FleetPrincipalKinds.Person,
            setup.Owner.Id, new TransferHandoverDto("sealed", [new GrantKeyWrapDto(owner.DeviceId, "wrapped")])));
        Assert.AreEqual(HttpStatusCode.Created, back.Status, back.Body.ToString());
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/transfers/{back.Read<TransferDto>().Id}/decline")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await server.SendAsync(owner, HttpMethod.Get, $"/api/v1/transfers/{back.Read<TransferDto>().Id}/handover")).Status);
    }

    [TestMethod]
    public async Task AMaintainerGivesATeamMachineAnAutomationOfTheTeam_WhichTheTeamOwns_AndTheDocumentNamesTheIdentityItRunsAs()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (mia, miaId) = await AddPersonAsync(server, owner, setup, "Mia");
        var (max, maxId) = await AddPersonAsync(server, owner, setup, "Max");
        var (ada, adaId) = await AddPersonAsync(server, owner, setup, "Ada");
        var team = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("Infra"))).Read<TeamDto>();
        await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{miaId}", new SetTeamMemberRequest(FleetTeamRoles.Maintainer));
        await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{adaId}", new SetTeamMemberRequest(FleetTeamRoles.Maintainer));
        await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{maxId}", new SetTeamMemberRequest(FleetTeamRoles.Member));
        await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/teams/{team.Id}/members/{setup.Owner.Id}");
        var offer = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Team,
            team.Id))).Read<TransferDto>();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/transfers/{offer.Id}/accept")).Status);
        PutAutomationRequest Version(TestDevice signer, string id, string? scope)
        {
            var text = FleetAutomationVersionDocuments.Serialize(new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, setup.Organization.Id, id, 1, machine.Id, false,
                true, ["hourly"], null, null, "{\"sealed\":\"for the machine\"}", signer.DeviceId, signer.PublicKey, server.Clock.Now) { Scope = scope });
            return new PutAutomationRequest(new FleetSignedAutomationVersion(text, signer.Sign(FleetAutomationVersionDocuments.GetSignedBytes(text))));
        }

        var put = await server.SendAsync(mia, HttpMethod.Put, "/api/v1/automations/deploy", Version(mia, "deploy", "team:" + team.Id));
        Assert.AreEqual(HttpStatusCode.OK, put.Status, put.Body.ToString());
        var owned = (await server.SendAsync(mia, HttpMethod.Get, "/api/v1/owners")).Read<AccessListDto<ResourceOwnerDto>>().Items.Single(resource => resource.ResourceId == "deploy");
        Assert.AreEqual((FleetPrincipalKinds.Team, team.Id, miaId), (owned.OwnerKind, owned.OwnerId, owned.AuthorId), "the team owns it from its first version");
        // Another maintainer lists it and runs it by hand; a member does neither.
        Assert.IsTrue((await server.SendAsync(ada, HttpMethod.Get, "/api/v1/automations")).Read<AutomationListDto>().Automations.Any(static item => item.Id == "deploy"));
        Assert.IsFalse((await server.SendAsync(max, HttpMethod.Get, "/api/v1/automations")).Read<AutomationListDto>().Automations.Any(static item => item.Id == "deploy"));
        RunAutomationRequest Run(TestDevice device)
        {
            var text = FleetAutomationRunRequestDocuments.Serialize(new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, setup.Organization.Id, "deploy",
                machine.Id, "run-1", device.DeviceId, device.PublicKey, server.Clock.Now));
            return new RunAutomationRequest(new FleetSignedAutomationRunRequest(text, device.Sign(FleetAutomationRunRequestDocuments.GetSignedBytes(text))));
        }

        Assert.AreEqual(HttpStatusCode.Conflict, (await server.SendAsync(ada, HttpMethod.Post, "/api/v1/automations/deploy/run", Run(ada))).Status,
            "a maintainer may run it: the machine is offline here");
        Assert.AreEqual(HttpStatusCode.NotFound, (await server.SendAsync(max, HttpMethod.Post, "/api/v1/automations/deploy/run", Run(max))).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Put, "/api/v1/automations/other", Version(max, "other", "team:" + team.Id))).Status,
            "a member gives the team none of its automations");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Put, "/api/v1/automations/mine", Version(max, "mine", null))).Status,
            "nor its own, while the team does not run those of its members");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/automations/mine", Version(owner, "mine", null))).Status,
            "the machine is the team's now, not its former owner's");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(mia, HttpMethod.Put, "/api/v1/automations/web", Version(mia, "web", "team:web"))).Status,
            "an automation of a team goes to a machine of that team");

        // The service identity of the team, once on, is in the next version of the document that machines follow.
        var identity = (await server.SendAsync(mia, HttpMethod.Post, "/api/v1/service-identities",
            new CreateServiceIdentityRequest("infra-bot", FleetPrincipalKinds.Team, team.Id))).Read<ServiceIdentityDto>();
        var enabled = await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/service-identities/{identity.Id}/enable");
        Assert.AreEqual(HttpStatusCode.OK, enabled.Status, enabled.Body.ToString());
        var draft = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/draft")).Read<AccessDocumentDraftDto>();
        CollectionAssert.AreEqual(new[] { identity.Id }, draft.Document.Teams.Single().Services!.ToArray());
    }

    [TestMethod]
    public async Task AMachineGoesToATeamOnceAMaintainerAcceptsIt_AndAnAutomationTakenWaitsPaused()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (mia, miaId) = await AddPersonAsync(server, owner, setup, "Mia");
        var (max, maxId) = await AddPersonAsync(server, owner, setup, "Max");
        var team = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("Infra"))).Read<TeamDto>();
        await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{miaId}", new SetTeamMemberRequest(FleetTeamRoles.Maintainer));
        await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{maxId}", new SetTeamMemberRequest(FleetTeamRoles.Member));
        await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/teams/{team.Id}/members/{setup.Owner.Id}");

        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Post, "/api/v1/transfers",
            new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Team, team.Id))).Status, "only who manages a machine gives it");
        var offer = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers",
            new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Team, team.Id));
        Assert.AreEqual(HttpStatusCode.Created, offer.Status, offer.Body.ToString());
        var transfer = offer.Read<TransferDto>();
        Assert.AreEqual(("Build box", FleetTransferStates.Offered), (transfer.ResourceName, transfer.State));
        Assert.AreEqual(HttpStatusCode.Conflict, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers",
            new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Person, maxId))).Status, "one offer at a time");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Post, $"/api/v1/transfers/{transfer.Id}/accept")).Status,
            "a member of the team does not accept for it");
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/transfers/{transfer.Id}/accept")).Status);

        var owners = (await server.SendAsync(max, HttpMethod.Get, "/api/v1/owners")).Read<AccessListDto<ResourceOwnerDto>>();
        var held = owners.Items.Single(resource => resource.ResourceId == machine.Id);
        Assert.AreEqual((FleetPrincipalKinds.Team, team.Id, "Infra", false), (held.OwnerKind, held.OwnerId, held.OwnerName, held.Paused), "a member sees the machines of the team");
        var teams = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/teams")).Read<AccessListDto<TeamDto>>();
        CollectionAssert.AreEqual(new[] { machine.Id }, teams.Items.Single().Machines.ToArray());

        // An automation of Max, given to the team: it waits for the team to resume it.
        var store = (IAutomationStore)server.GetService<ICoordinatorStore>();
        await store.PutAutomationAsync(new AutomationRecord(setup.Organization.Id, "nightly", machine.Id, maxId, 1, false, true, "[]", "{}", "sig", max.DeviceId,
            server.Clock.Now, "delivered", null), new AuditEntry("automation.put", AuditEntry.Success, maxId, max.DeviceId, "nightly", null, null));
        var give = await server.SendAsync(max, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Automation, "nightly", FleetPrincipalKinds.Team, team.Id));
        Assert.AreEqual(HttpStatusCode.Created, give.Status, give.Body.ToString());
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/transfers/{give.Read<TransferDto>().Id}/accept")).Status);
        var automation = (await server.SendAsync(mia, HttpMethod.Get, "/api/v1/owners")).Read<AccessListDto<ResourceOwnerDto>>().Items.Single(resource => resource.ResourceId == "nightly");
        Assert.AreEqual((FleetPrincipalKinds.Team, true, maxId), (automation.OwnerKind, automation.Paused, automation.AuthorId), "paused, its author kept");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Post, "/api/v1/owners/automation/nightly/resume")).Status);
        var resumed = await server.SendAsync(mia, HttpMethod.Post, "/api/v1/owners/automation/nightly/resume");
        Assert.AreEqual(HttpStatusCode.OK, resumed.Status, resumed.Body.ToString());
        Assert.IsFalse(resumed.Read<ResourceOwnerDto>().Paused);
    }

    [TestMethod]
    public async Task TheAccessDocumentIsSignedBySignersOnly_FollowsTheModel_AndOnlyAnOwnerChangesWhoSigns()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (bea, beaId) = await AddPersonAsync(server, owner, setup, "Bea");
        var team = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("Infra"))).Read<TeamDto>();
        var transfer = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers",
            new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Team, team.Id))).Read<TransferDto>();
        await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/transfers/{transfer.Id}/accept");

        var state = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/self")).Read<AccessSelfDto>().Document;
        Assert.AreEqual(0, state.Number);
        Assert.IsTrue(state.Pending.Any(line => line.Contains("Build box becomes a machine of Infra", StringComparison.Ordinal)), string.Join("; ", state.Pending));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(bea, HttpMethod.Get, "/api/v1/access/draft")).Status, "a member signs nothing");

        var first = await SignDraftAsync(server, owner);
        Assert.AreEqual(HttpStatusCode.Created, first.Status, first.Body.ToString());
        var listed = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/access/documents")).Read<AccessDocumentListDto>();
        Assert.AreEqual(1, listed.Items.Count, "a node reads the chain");
        Assert.AreEqual(0, (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/self")).Read<AccessSelfDto>().Document.Pending.Count);

        // A version the model does not say is refused, even signed by a signer; one signed by a member too.
        var draft = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/draft")).Read<AccessDocumentDraftDto>();
        Assert.IsFalse(draft.Changes);
        var forged = draft.Document with { Suspended = [beaId], SignerDeviceId = owner.DeviceId, Time = server.Clock.Now };
        var refused = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/access/documents", new AppendAccessDocumentRequest(Sign(owner, forged)));
        Assert.AreEqual(HttpStatusCode.Conflict, refused.Status, "the server keeps the document and its model together");
        var byMember = await server.SendAsync(bea, HttpMethod.Post, "/api/v1/access/documents",
            new AppendAccessDocumentRequest(Sign(bea, draft.Document with { SignerDeviceId = bea.DeviceId, Time = server.Clock.Now })));
        Assert.AreEqual(HttpStatusCode.Forbidden, byMember.Status);

        // An admin signs what changes in teams; who signs changes only by an owner's device.
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/people/{beaId}/role", new SetRoleRequest(FleetOrganizationRoles.Admin))).Status);
        Assert.IsFalse((await server.SendAsync(bea, HttpMethod.Get, "/api/v1/access/self")).Read<AccessSelfDto>().Signer, "an admin signs once an owner signed them in");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(bea, HttpMethod.Get, "/api/v1/access/draft")).Status);
        var forgedByAdmin = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/draft");
        var adminSigns = await server.SendAsync(bea, HttpMethod.Post, "/api/v1/access/documents", new AppendAccessDocumentRequest(Sign(bea,
            forgedByAdmin.Read<AccessDocumentDraftDto>().Document with { SignerDeviceId = bea.DeviceId, Time = server.Clock.Now })));
        Assert.AreEqual(HttpStatusCode.Forbidden, adminSigns.Status, adminSigns.Body.ToString());
        Assert.AreEqual(HttpStatusCode.Created, (await SignDraftAsync(server, owner)).Status);
        await server.SendAsync(bea, HttpMethod.Patch, $"/api/v1/teams/{team.Id}", new UpdateTeamRequest(RunsMemberAutomations: true));
        var second = await SignDraftAsync(server, bea);
        Assert.AreEqual(HttpStatusCode.Created, second.Status, second.Body.ToString());
        Assert.AreEqual(3, second.Read<AccessDocumentDto>().Number);
    }

    [TestMethod]
    public async Task DeprovisioningSuspendsAPerson_GivesWhatTheyOwnedToTheSuccessorPaused_AndRefusesTheirDevices()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Home server");
        // The device of the owner signs an entry of their own person first (a machine, as setting a server up does with its recovery key).
        var (setup, _) = await server.SetUpWithNodeAsync(owner, node);
        var (sam, samId) = await AddPersonAsync(server, owner, setup, "Sam");
        var (mia, miaId) = await AddPersonAsync(server, owner, setup, "Mia");
        using var samNode = new TestDevice("Sam laptop node");
        var pending = await server.EnrollAsync(sam, samNode);
        var entry = await server.NextEntryAsync(sam, FleetDeviceListOperations.Add, pending.Id, samNode.PublicKey, FleetDeviceKinds.Node, samId, setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(sam, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(entry))).Status);
        var team = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("Web"))).Read<TeamDto>();
        await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{samId}", new SetTeamMemberRequest(FleetTeamRoles.Member));
        await ((IAutomationStore)server.GetService<ICoordinatorStore>()).PutAutomationAsync(new AutomationRecord(setup.Organization.Id, "report", "elsewhere", samId, 1, false, true, "[]", "{}", "sig",
            sam.DeviceId, server.Clock.Now, "delivered", null), new AuditEntry("automation.put", AuditEntry.Success, samId, sam.DeviceId, "report", null, null));

        var revokeOther = await server.NextEntryAsync(mia, FleetDeviceListOperations.Remove, samNode.DeviceId, null, null, null, setup.Organization.Id);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/devices/{samNode.DeviceId}/revoke",
            new DeviceListChangeRequest(revokeOther))).Status, "a member revokes no device of another person");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/people/{setup.Owner.Id}/deprovision", new DeprovisionRequest())).Status,
            "nobody deprovisions themselves");

        var answer = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/people/{samId}/deprovision", new DeprovisionRequest(FleetPrincipalKinds.Person, miaId));
        Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
        var result = answer.Read<DeprovisionResultDto>();
        Assert.AreEqual(FleetPersonStatuses.Suspended, result.Person.Status);
        Assert.AreEqual(0, result.Person.Teams.Count, "their teams are taken back");
        CollectionAssert.AreEquivalent(new[] { sam.DeviceId, samNode.DeviceId }, result.DevicesToRevoke.ToArray());
        CollectionAssert.AreEqual(new[] { samNode.DeviceId }, result.MachinesLeaving.ToArray(), "their personal machine leaves the fleet");
        var report = result.Transfers.Single();
        Assert.AreEqual(("report", FleetPrincipalKinds.Person, miaId, FleetTransferReasons.Deprovision), (report.ResourceId, report.ToKind, report.ToId, report.Reason));

        var refused = await server.SendAsync(sam, HttpMethod.Get, "/api/v1/access/self");
        Assert.AreEqual((HttpStatusCode.Forbidden, FleetErrorCodes.Revoked), (refused.Status, refused.Code), "their devices are refused at once");
        var owned = (await server.SendAsync(mia, HttpMethod.Get, "/api/v1/owners")).Read<AccessListDto<ResourceOwnerDto>>().Items.Single(resource => resource.ResourceId == "report");
        Assert.AreEqual((miaId, true, samId), (owned.OwnerId, owned.Paused, owned.AuthorId));
        var signed = await SignDraftAsync(server, owner);
        Assert.AreEqual(HttpStatusCode.Created, signed.Status, signed.Body.ToString());
        FleetAccessDocuments.TryParse(signed.Read<AccessDocumentDto>().Document.Payload, out var document);
        CollectionAssert.AreEqual(new[] { samId }, document!.Suspended.ToArray(), "the signed document tells the machines");

        // The owner signs their devices out of the list.
        var removal = await server.NextEntryAsync(owner, FleetDeviceListOperations.Remove, samNode.DeviceId, null, null, null, setup.Organization.Id);
        var revoked = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{samNode.DeviceId}/revoke", new DeviceListChangeRequest(removal));
        Assert.AreEqual(HttpStatusCode.OK, revoked.Status, revoked.Body.ToString());
    }

    internal static async Task<(TestDevice Device, string PersonId)> AddPersonAsync(ServerFixture server, TestDevice owner, SetupResponse setup, string name,
        string role = FleetOrganizationRoles.Member)
    {
        var invited = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/people", new InvitePersonRequest(name, role));
        Assert.AreEqual(HttpStatusCode.Created, invited.Status, invited.Body.ToString());
        var invitation = invited.Read<InvitationDto>();
        Assert.AreEqual((role, 0), (invitation.Person.Role, invitation.Person.Devices));
        var device = new TestDevice(name + " desktop");
        var enrolled = await server.SendAsync(device, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(invitation.Token, device.PublicKey, device.Name, FleetDeviceKinds.Controller));
        Assert.AreEqual(HttpStatusCode.Created, enrolled.Status, enrolled.Body.ToString());
        var entry = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, device.DeviceId, device.PublicKey, FleetDeviceKinds.Controller, invitation.Person.Id,
            setup.Organization.Id);
        var approved = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{device.DeviceId}/approve", new DeviceListChangeRequest(entry));
        Assert.AreEqual(HttpStatusCode.OK, approved.Status, approved.Body.ToString());
        return (device, invitation.Person.Id);
    }

    // A machine publishes the fact of one session, as its control connection does.
    private static async Task PublishSessionAsync(ServerFixture server, TestDevice machine, string sessionId)
    {
        var cursors = await server.GetService<ICoordinatorStore>().GetPublicationCursorsAsync(machine.DeviceId);
        var device = (await server.GetService<ICoordinatorStore>().GetDeviceAsync(machine.DeviceId))!;
        await server.GetService<CodeAlta.Server.Services.PublicationService>().PublishFactsAsync(device, new FleetFactsPublish(Math.Max(cursors.Facts, cursors.Inventory) + 1,
            [new FleetSessionFact(sessionId, 1, 1, "idle", server.Clock.Now, server.Clock.Now, "anthropic", "claude", new FleetSessionCounts(1, 0, 10, 5))]), CancellationToken.None);
    }

    internal static async Task<ApiAnswer> SignDraftAsync(ServerFixture server, TestDevice signer)
    {
        var draft = await server.SendAsync(signer, HttpMethod.Get, "/api/v1/access/draft");
        Assert.AreEqual(HttpStatusCode.OK, draft.Status, draft.Body.ToString());
        var document = draft.Read<AccessDocumentDraftDto>().Document with { SignerDeviceId = signer.DeviceId, Time = server.Clock.Now };
        return await server.SendAsync(signer, HttpMethod.Post, "/api/v1/access/documents", new AppendAccessDocumentRequest(Sign(signer, document)));
    }

    internal static FleetSignedAccessDocument Sign(TestDevice signer, FleetAccessDocument document)
    {
        var text = FleetAccessDocuments.Serialize(document);
        return new FleetSignedAccessDocument(text, signer.Sign(FleetAccessDocuments.GetSignedBytes(text)));
    }
}
