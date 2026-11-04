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
        var listed = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/draft")).Read<AccessDocumentDraftDto>().Document.Automations!.Single();
        Assert.AreEqual(("nightly", FleetPrincipalKinds.Person, beaId, true), (listed.Id, listed.OwnerKind, listed.OwnerId, listed.Paused),
            "machines learn from the signed document that it is hers, and waits for her");

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
        PutAutomationRequest Version(TestDevice signer, string id, string? scope, long number = 1)
        {
            var text = FleetAutomationVersionDocuments.Serialize(new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, setup.Organization.Id, id, number, machine.Id, false,
                true, ["hourly"], null, null, "{\"sealed\":\"for the machine\"}", signer.DeviceId, signer.PublicKey, server.Clock.Now) { Scope = scope, Provider = scope is null ? null : "gateway" });
            return new PutAutomationRequest(new FleetSignedAutomationVersion(text, signer.Sign(FleetAutomationVersionDocuments.GetSignedBytes(text))));
        }

        var put = await server.SendAsync(mia, HttpMethod.Put, "/api/v1/automations/deploy", Version(mia, "deploy", "team:" + team.Id));
        Assert.AreEqual(HttpStatusCode.OK, put.Status, put.Body.ToString());
        var owned = (await server.SendAsync(mia, HttpMethod.Get, "/api/v1/owners")).Read<AccessListDto<ResourceOwnerDto>>().Items.Single(resource => resource.ResourceId == "deploy");
        Assert.AreEqual((FleetPrincipalKinds.Team, team.Id, miaId), (owned.OwnerKind, owned.OwnerId, owned.AuthorId), "the team owns it from its first version");
        // The machine says which of its providers run with the key of an account: the server takes no automation of the team that runs with another.
        var cursors = await server.GetService<ICoordinatorStore>().GetPublicationCursorsAsync(node.DeviceId);
        await server.GetService<CodeAlta.Server.Services.PublicationService>().PublishInventoryAsync((await server.GetService<ICoordinatorStore>().GetDeviceAsync(node.DeviceId))!,
            new FleetInventoryPublish(Math.Max(cursors.Facts, cursors.Inventory) + 1, new FleetMachineInventory("Build box", "linux", Providers: ["openai-chat", "codex"],
                AccountProviders: ["gateway"])), CancellationToken.None);
        var plan = Version(mia, "deploy-plan", "team:" + team.Id);
        var planText = FleetAutomationVersionDocuments.Serialize(FleetAutomationVersionDocuments.TryParse(plan.Version.Payload, out var parsed) ? parsed with { Provider = "codex" } : null!);
        var onPlan = await server.SendAsync(mia, HttpMethod.Put, "/api/v1/automations/deploy-plan",
            new PutAutomationRequest(new FleetSignedAutomationVersion(planText, mia.Sign(FleetAutomationVersionDocuments.GetSignedBytes(planText)))));
        Assert.AreEqual(HttpStatusCode.Conflict, onPlan.Status, "a personal plan is refused by the server too, before the machine refuses it");
        StringAssert.Contains(onPlan.Body.ToString(), "account credentials only");

        // Another maintainer lists it and runs it by hand; a member does neither.
        Assert.IsTrue((await server.SendAsync(ada, HttpMethod.Get, "/api/v1/automations")).Read<AutomationListDto>().Automations.Any(static item => item.Id == "deploy"));
        Assert.IsFalse((await server.SendAsync(max, HttpMethod.Get, "/api/v1/automations")).Read<AutomationListDto>().Automations.Any(static item => item.Id == "deploy"));

        // Ada opens it in her editor and saves it: her version follows Mia's, the team still owns it, and Mia still reads it.
        var edited = await server.SendAsync(ada, HttpMethod.Put, "/api/v1/automations/deploy", Version(ada, "deploy", "team:" + team.Id, 2));
        Assert.AreEqual(HttpStatusCode.OK, edited.Status, edited.Body.ToString());
        Assert.AreEqual((2L, ada.DeviceId), (edited.Read<AutomationDto>().Number, edited.Read<AutomationDto>().SignerDeviceId));
        Assert.AreEqual(2L, (await server.SendAsync(mia, HttpMethod.Get, "/api/v1/automations")).Read<AutomationListDto>().Automations.Single(static item => item.Id == "deploy").Number);
        var still = (await server.SendAsync(mia, HttpMethod.Get, "/api/v1/owners")).Read<AccessListDto<ResourceOwnerDto>>().Items.Single(resource => resource.ResourceId == "deploy");
        Assert.AreEqual((FleetPrincipalKinds.Team, team.Id), (still.OwnerKind, still.OwnerId));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Put, "/api/v1/automations/deploy", Version(max, "deploy", "team:" + team.Id, 3))).Status,
            "a member gives it no version");
        RunAutomationRequest Run(TestDevice device)
        {
            var text = FleetAutomationRunRequestDocuments.Serialize(new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, setup.Organization.Id, "deploy",
                machine.Id, "run-1", device.DeviceId, device.PublicKey, server.Clock.Now));
            return new RunAutomationRequest(new FleetSignedAutomationRunRequest(text, device.Sign(FleetAutomationRunRequestDocuments.GetSignedBytes(text))));
        }

        Assert.AreEqual(HttpStatusCode.Conflict, (await server.SendAsync(ada, HttpMethod.Post, "/api/v1/automations/deploy/run", Run(ada))).Status,
            "a maintainer may run it: the machine is offline here");
        // A member of the team sees its runs, which their team role gives at the endpoints too; running it by hand stays its maintainers'.
        Assert.AreEqual(HttpStatusCode.NotFound, (await server.SendAsync(max, HttpMethod.Post, "/api/v1/automations/deploy/run", Run(max))).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(max, HttpMethod.Get, "/api/v1/automations/deploy/runs")).Status, "a member sees the runs of the team");
        var (cy, _) = await AddPersonAsync(server, owner, setup, "Cy");
        Assert.AreEqual(HttpStatusCode.NotFound, (await server.SendAsync(cy, HttpMethod.Get, "/api/v1/automations/deploy/runs")).Status, "not someone of no team");
        Assert.AreEqual(HttpStatusCode.NotFound, (await server.SendAsync(cy, HttpMethod.Post, "/api/v1/automations/deploy/run", Run(cy))).Status);
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

        // A run of the team's automation says which identity it acted as: kept on the run and in the audit when the team has it on.
        var automations = server.GetService<CodeAlta.Server.Services.FleetAutomationService>();
        var box = (await server.GetService<ICoordinatorStore>().GetDeviceAsync(node.DeviceId))!;
        await automations.RecordRunsAsync(box, new FleetAutomationRunFacts([
            new FleetAutomationRunFact("run-a", "deploy", 1, "schedule", "succeeded", server.Clock.Now, server.Clock.Now) { ActingAs = identity.Id },
            new FleetAutomationRunFact("run-b", "deploy", 1, "schedule", "succeeded", server.Clock.Now, server.Clock.Now) { ActingAs = "svc-unknown" }]), CancellationToken.None);
        var runs = (await server.SendAsync(ada, HttpMethod.Get, "/api/v1/automations/deploy/runs")).Read<AutomationRunListDto>().Runs.ToDictionary(static run => run.RunId);
        Assert.AreEqual((identity.Id, null), (runs["run-a"].ActingAs, runs["run-b"].ActingAs));
        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items;
        Assert.AreEqual(1, audit.Count(record => record.Action == "automation.run_as" && record.Details?.Contains(identity.Id, StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task APersonalMachineGivenToAnotherPerson_IsTheirsInTheSignedDocument_WhichMachinesVerify()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (mia, miaId) = await AddPersonAsync(server, owner, setup, "Mia");
        var offer = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Person,
            miaId))).Read<TransferDto>();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/transfers/{offer.Id}/accept")).Status);

        // The draft says it; the owner signs it; a machine that replays the chain against the device list takes Mia as the owner.
        var draft = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/draft")).Read<AccessDocumentDraftDto>();
        Assert.AreEqual((machine.Id, miaId), (draft.Document.PersonalMachines!.Single().DeviceId, draft.Document.PersonalMachines!.Single().PersonId));
        var signed = await SignDraftAsync(server, owner);
        Assert.AreEqual(HttpStatusCode.Created, signed.Status, signed.Body.ToString());
        var entries =(await server.SendAsync(node, HttpMethod.Get, "/api/v1/device-list")).Read<ListResponse<DeviceListEntryDto>>().Items;
        var devices = FleetDeviceListVerifier.VerifyOrThrow(entries.Select(static entry => entry.Entry), setup.Organization.Id);
        var documents = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/access/documents")).Read<AccessDocumentListDto>().Items;
        var state = FleetAccessDocuments.Verify(documents.Select(static item => item.Document), devices, setup.Organization.Id);
        Assert.IsTrue(state.IsValid, state.Reason);
        Assert.AreEqual((setup.Owner.Id, miaId), (devices.Members[machine.Id].OwnerId, state.State.MachineOwner(machine.Id, devices)),
            "the device list still names Alex; the signed document names Mia");

        // The server gives the machine the versions of its new owner, and no more those of its former owner.
        PutAutomationRequest Version(TestDevice signer, string id)
        {
            var text = FleetAutomationVersionDocuments.Serialize(new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, setup.Organization.Id, id, 1, machine.Id, false,
                true, ["hourly"], null, null, "{\"sealed\":\"for the machine\"}", signer.DeviceId, signer.PublicKey, server.Clock.Now));
            return new PutAutomationRequest(new FleetSignedAutomationVersion(text, signer.Sign(FleetAutomationVersionDocuments.GetSignedBytes(text))));
        }

        var byMia = await server.SendAsync(mia, HttpMethod.Put, "/api/v1/automations/mine", Version(mia, "mine"));
        Assert.AreEqual(HttpStatusCode.OK, byMia.Status, byMia.Body.ToString());
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/automations/his", Version(owner, "his"))).Status);

        // Given back to Alex: the next draft leaves the machine out, and the signer reads that it goes.
        var back = (await server.SendAsync(mia, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Person,
            setup.Owner.Id))).Read<TransferDto>();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/transfers/{back.Id}/accept")).Status);
        var again = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/draft")).Read<AccessDocumentDraftDto>();
        Assert.IsTrue(again.Changes);
        Assert.IsNull(again.Document.PersonalMachines);
    }

    [TestMethod]
    public async Task AnAdminGivesAMachineOfTheOrganizationAnAutomationOfTheOrganization_AndNobodyElseGivesItAnything()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (ada, _) = await AddPersonAsync(server, owner, setup, "Ada", FleetOrganizationRoles.Admin);
        var (max, _) = await AddPersonAsync(server, owner, setup, "Max");
        var offer = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id,
            FleetPrincipalKinds.Organization, setup.Organization.Id))).Read<TransferDto>();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(ada, HttpMethod.Post, $"/api/v1/transfers/{offer.Id}/accept")).Status);
        PutAutomationRequest Version(TestDevice signer, string id, string? scope)
        {
            var text = FleetAutomationVersionDocuments.Serialize(new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, setup.Organization.Id, id, 1, machine.Id, false,
                true, ["hourly"], null, null, "{\"sealed\":\"for the machine\"}", signer.DeviceId, signer.PublicKey, server.Clock.Now) { Scope = scope, Provider = scope is null ? null : "gateway" });
            return new PutAutomationRequest(new FleetSignedAutomationVersion(text, signer.Sign(FleetAutomationVersionDocuments.GetSignedBytes(text))));
        }

        var put = await server.SendAsync(ada, HttpMethod.Put, "/api/v1/automations/report", Version(ada, "report", FleetAutomationVersion.OrganizationScope));
        Assert.AreEqual(HttpStatusCode.OK, put.Status, put.Body.ToString());
        var owned = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/owners")).Read<AccessListDto<ResourceOwnerDto>>().Items.Single(resource => resource.ResourceId == "report");
        Assert.AreEqual(FleetPrincipalKinds.Organization, owned.OwnerKind);
        Assert.IsTrue((await server.SendAsync(owner, HttpMethod.Get, "/api/v1/automations")).Read<AutomationListDto>().Automations.Any(static item => item.Id == "report"),
            "the owner lists what an admin gave the organization");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Put, "/api/v1/automations/mine",
            Version(max, "mine", FleetAutomationVersion.OrganizationScope))).Status, "a member gives the organization nothing");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/automations/mine", Version(owner, "mine", null))).Status,
            "a machine of the organization runs no personal automation");

        var identity = (await server.SendAsync(ada, HttpMethod.Post, "/api/v1/service-identities",
            new CreateServiceIdentityRequest("org-bot", FleetPrincipalKinds.Organization, setup.Organization.Id))).Read<ServiceIdentityDto>();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(ada, HttpMethod.Post, $"/api/v1/service-identities/{identity.Id}/enable")).Status);
        var draft = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/draft")).Read<AccessDocumentDraftDto>();
        CollectionAssert.AreEqual(new[] { machine.Id }, draft.Document.OrganizationMachines!.ToArray());
        CollectionAssert.AreEqual(new[] { identity.Id }, draft.Document.OrganizationServices!.ToArray());
    }

    [TestMethod]
    public async Task AMaintainerSetsAProviderOfTheTeam_ItsKeySealedForTheMachinesOfTheTeamOnly_WhichReadTheirCopies()
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
        var offer = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Team,
            team.Id))).Read<TransferDto>();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/transfers/{offer.Id}/accept")).Status);
        PutScopeProviderRequest Provider(long version, string device, string type = "anthropic") => new(FleetPrincipalKinds.Team, team.Id, "Infra gateway", type,
            "https://gateway.example/v1", ["claude-opus-5-5"], version, "sealed-key", [new GrantKeyWrapDto(device, "wrapped")]);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Put, "/api/v1/providers/infra-gateway", Provider(1, machine.Id))).Status,
            "a member of the team sets no provider of it");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(mia, HttpMethod.Put, "/api/v1/providers/infra-gateway", Provider(1, mia.DeviceId))).Status,
            "its key goes to the machines of the team only");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(mia, HttpMethod.Put, "/api/v1/providers/infra-gateway", Provider(1, machine.Id, "codex"))).Status,
            "a personal plan is no provider of a scope");
        var put = await server.SendAsync(mia, HttpMethod.Put, "/api/v1/providers/infra-gateway", Provider(1, machine.Id));
        Assert.AreEqual(HttpStatusCode.OK, put.Status, put.Body.ToString());
        Assert.AreEqual(HttpStatusCode.Conflict, (await server.SendAsync(mia, HttpMethod.Put, "/api/v1/providers/infra-gateway", Provider(1, machine.Id))).Status,
            "a version is never replaced by one that is not newer");
        var listed = (await server.SendAsync(max, HttpMethod.Get, "/api/v1/providers")).Read<AccessListDto<ScopeProviderDto>>().Items.Single();
        Assert.AreEqual(("Infra gateway", machine.Id), (listed.Name, listed.Machines.Single()), "the members see what the team runs with, never its key");
        Assert.IsFalse((await server.SendAsync(max, HttpMethod.Get, "/api/v1/providers")).Body.ToString().Contains("sealed-key", StringComparison.Ordinal));

        var copies = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/provider-keys")).Read<AccessListDto<ScopeProviderKeyDto>>().Items.Single();
        Assert.AreEqual(("infra-gateway", "sealed-key", "wrapped"), (copies.Provider.Id, copies.Sealed, copies.Wrapped));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(mia, HttpMethod.Get, "/api/v1/provider-keys")).Status, "the keys go to machines");
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mia, HttpMethod.Delete, "/api/v1/providers/infra-gateway")).Status);
        Assert.AreEqual(0, (await server.SendAsync(node, HttpMethod.Get, "/api/v1/provider-keys")).Read<AccessListDto<ScopeProviderKeyDto>>().Items.Count);
    }

    [TestMethod]
    public async Task WhoAnswersForTheContentOfAGrant_ListsIt_AndConfirmsItsKeys_WithoutManagingAccess()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (mia, miaId) = await AddPersonAsync(server, owner, setup, "Mia");
        var (max, maxId) = await AddPersonAsync(server, owner, setup, "Max");
        var (cy, _) = await AddPersonAsync(server, owner, setup, "Cy");
        var team = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("Infra"))).Read<TeamDto>();
        await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{miaId}", new SetTeamMemberRequest(FleetTeamRoles.Maintainer));
        await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/teams/{team.Id}/members/{setup.Owner.Id}");
        var offer = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Team,
            team.Id))).Read<TransferDto>();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/transfers/{offer.Id}/accept")).Status);
        await PublishSessionAsync(server, node, "s1");

        // Mia, a maintainer of the team whose machine runs the session, shares it with Max: she sees the grant that waits for her keys.
        var made = await server.SendAsync(mia, HttpMethod.Post, "/api/v1/grants",
            new CreateGrantRequest(FleetPrincipalKinds.Person, maxId, "session.view-content", FleetScopeKinds.Resource, "session:s1", Sealed: true));
        Assert.AreEqual(HttpStatusCode.Created, made.Status, made.Body.ToString());
        var grant = made.Read<GrantDto>();
        Assert.IsTrue((await server.SendAsync(mia, HttpMethod.Get, "/api/v1/grants")).Read<AccessListDto<GrantDto>>().Items.Any(item => item.Id == grant.Id),
            "who answers for the content lists the grants on it, to confirm their keys");
        Assert.IsFalse((await server.SendAsync(cy, HttpMethod.Get, "/api/v1/grants")).Read<AccessListDto<GrantDto>>().Items.Any(item => item.Id == grant.Id),
            "nobody else does");
        var keyed = await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/grants/{grant.Id}/keys", new ConfirmGrantKeysRequest("sealed", [new GrantKeyWrapDto(max.DeviceId, "wrapped")]));
        Assert.AreEqual(HttpStatusCode.OK, keyed.Status, keyed.Body.ToString());
    }

    [TestMethod]
    public async Task AMaintainerOfAnotherTeam_NeitherReplacesNorTakesOverAProviderTheyDoNotAnswerFor()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (mia, miaId) = await AddPersonAsync(server, owner, setup, "Mia");
        var (max, maxId) = await AddPersonAsync(server, owner, setup, "Max");
        var infra = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("Infra"))).Read<TeamDto>();
        var web = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("Web"))).Read<TeamDto>();
        await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{infra.Id}/members/{miaId}", new SetTeamMemberRequest(FleetTeamRoles.Maintainer));
        await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{web.Id}/members/{maxId}", new SetTeamMemberRequest(FleetTeamRoles.Maintainer));
        await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/teams/{infra.Id}/members/{setup.Owner.Id}");
        await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/teams/{web.Id}/members/{setup.Owner.Id}");
        var offer = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Team,
            infra.Id))).Read<TransferDto>();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/transfers/{offer.Id}/accept")).Status);
        PutScopeProviderRequest Provider(TeamDto team, long version) => new(FleetPrincipalKinds.Team, team.Id, "Gateway", "anthropic", "https://gateway.example/v1",
            ["claude-opus-5-5"], version, "sealed-key", [new GrantKeyWrapDto(machine.Id, "wrapped")]);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mia, HttpMethod.Put, "/api/v1/providers/gateway", Provider(infra, 1))).Status);

        // The machine goes to Web; its maintainer sends the id of Infra's provider, a newer version, and Web as its owner.
        var moved = (await server.SendAsync(mia, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Team,
            web.Id))).Read<TransferDto>();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(max, HttpMethod.Post, $"/api/v1/transfers/{moved.Id}/accept")).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Put, "/api/v1/providers/gateway", Provider(web, 2))).Status,
            "a provider is changed by who answers for the scope it belongs to");
        var held = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/providers")).Read<AccessListDto<ScopeProviderDto>>().Items.Single();
        Assert.AreEqual((infra.Id, 1L), (held.OwnerId, held.Version), "Infra's provider stays as it was");

        // Who answers for both scopes does not move it to the other either: a provider keeps the owner it was made for.
        await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{web.Id}/members/{miaId}", new SetTeamMemberRequest(FleetTeamRoles.Maintainer));
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(mia, HttpMethod.Put, "/api/v1/providers/gateway", Provider(web, 2))).Status);
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

        // The team answers its approvals: the access document names who else than Max takes its runs over, for its machine.
        var draft = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/draft")).Read<AccessDocumentDraftDto>();
        Assert.AreEqual(("nightly", miaId), (draft.Document.Approvers!.Single().AutomationId, draft.Document.Approvers!.Single().People.Single()),
            "the maintainer of the team; Max placed it, the admins are not asked");
        Assert.IsTrue((await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/self")).Read<AccessSelfDto>().Document.Pending
            .Any(line => line.Contains("answers the approvals of", StringComparison.Ordinal)));
        Task<ApiAnswer> TakeOverAsync(TestDevice device)
        {
            var text = FleetAutomationRunRequestDocuments.Serialize(new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, setup.Organization.Id, "nightly", machine.Id,
                "run-1", device.DeviceId, device.PublicKey, server.Clock.Now) { Action = FleetAutomationRunRequest.TakeOverAction });
            return server.SendAsync(device, HttpMethod.Post, "/api/v1/automations/nightly/runs/run-1/takeover",
                new RunAutomationRequest(new FleetSignedAutomationRunRequest(text, device.Sign(FleetAutomationRunRequestDocuments.GetSignedBytes(text)))));
        }

        // Its runs that ask the person on call go to whoever of the team is on call now, by the rotation its maintainer sets.
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Put, $"/api/v1/teams/{team.Id}/oncall", new PutOnCallRotationRequest([maxId, miaId], 12))).Status,
            "a member does not set it");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(mia, HttpMethod.Put, $"/api/v1/teams/{team.Id}/oncall",
            new PutOnCallRotationRequest([maxId, setup.Owner.Id], 12))).Status, "only people of the team");
        var set = await server.SendAsync(mia, HttpMethod.Put, $"/api/v1/teams/{team.Id}/oncall", new PutOnCallRotationRequest([maxId, miaId], 12));
        Assert.AreEqual(HttpStatusCode.OK, set.Status, set.Body.ToString());
        var listedTeam = (await server.SendAsync(max, HttpMethod.Get, "/api/v1/teams")).Read<AccessListDto<TeamDto>>().Items.Single();
        Assert.AreEqual((maxId, 12), (listedTeam.OnCall!.OnCallNow, listedTeam.OnCall.ShiftHours));
        var access = server.GetService<CodeAlta.Server.Services.AccessService>();
        CollectionAssert.AreEqual(new[] { maxId }, (await access.ApproversOfAsync(setup.Organization.Id, "nightly", maxId, "oncall", default)).ToArray());
        // Its on-call tool names who is on call: they are asked when they answer its approvals; anyone else falls back to the rotation.
        CollectionAssert.AreEqual(new[] { miaId }, (await access.ApproversOfAsync(setup.Organization.Id, "nightly", maxId, "oncall", ["mia@acme.test", "Mia"], default)).ToArray());
        var ownerSelf = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/self")).Read<AccessSelfDto>();
        CollectionAssert.AreEqual(new[] { maxId }, (await access.ApproversOfAsync(setup.Organization.Id, "nightly", maxId, "oncall", [ownerSelf.Person.DisplayName, "zed@acme.test"], default)).ToArray(),
            "an owner of the organization answers none of its approvals: the tool does not make them");
        CollectionAssert.AreEqual(new[] { miaId, maxId }, (await access.ApproversOfAsync(setup.Organization.Id, "nightly", maxId, null, default)).ToArray(), "its owner team otherwise");
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mia, HttpMethod.Delete, $"/api/v1/teams/{team.Id}/oncall")).Status);
        CollectionAssert.AreEqual(new[] { miaId, maxId }, (await access.ApproversOfAsync(setup.Organization.Id, "nightly", maxId, "oncall", default)).ToArray(), "without a rotation, its team");

        // The machine reported the run that waits: a take-over is passed on for a run of this automation only.
        var box = (await server.GetService<ICoordinatorStore>().GetDeviceAsync(machine.Id))!;
        await server.GetService<CodeAlta.Server.Services.FleetAutomationService>().RecordRunsAsync(box, new FleetAutomationRunFacts([
            new FleetAutomationRunFact("run-1", "nightly", 1, "schedule", "waiting", server.Clock.Now, null)]), CancellationToken.None);
        var byMia = await TakeOverAsync(mia);
        Assert.AreEqual(HttpStatusCode.Conflict, byMia.Status, byMia.Body.ToString());
        StringAssert.Contains(byMia.Body.ToString(), "offline", "Mia may: the machine is offline, so it is not passed on");
        Assert.AreEqual(HttpStatusCode.NotFound, (await TakeOverAsync(owner)).Status, "an owner of the organization manages it, but answers none of its approvals");
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
