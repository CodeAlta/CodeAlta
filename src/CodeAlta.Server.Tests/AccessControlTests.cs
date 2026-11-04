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
        var byAdmin = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/grants",
            new CreateGrantRequest(FleetPrincipalKinds.Person, maxId, "machine.logs", FleetScopeKinds.Resource, "machine:" + machine.Id, Sealed: true));
        Assert.AreEqual(HttpStatusCode.Created, byAdmin.Status, "the owner of the machine grants its log");
        var pending = byAdmin.Read<GrantDto>();
        Assert.AreEqual(FleetGrantStatuses.PendingKeys, pending.Status, "sealed content waits for a device that holds its keys");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/grants/{pending.Id}/keys")).Status,
            "only a device of the owner of the content confirms its keys");
        var active = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/grants/{pending.Id}/keys");
        Assert.AreEqual(FleetGrantStatuses.Active, active.Read<GrantDto>().Status);
        var teamGrant = await server.SendAsync(mia, HttpMethod.Post, "/api/v1/grants",
            new CreateGrantRequest(FleetPrincipalKinds.Person, maxId, "automation.edit", FleetScopeKinds.Team, team.Id));
        Assert.AreEqual(HttpStatusCode.Created, teamGrant.Status, "a maintainer grants on the team");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(mia, HttpMethod.Post, "/api/v1/grants",
            new CreateGrantRequest(FleetPrincipalKinds.Person, maxId, "automation.edit", FleetScopeKinds.Organization))).Status, "not on the organization");
        var maxGrants = (await server.SendAsync(max, HttpMethod.Get, "/api/v1/grants")).Read<AccessListDto<GrantDto>>();
        Assert.AreEqual(2, maxGrants.Items.Count, "a person sees the grants made to them");
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(max, HttpMethod.Delete, $"/api/v1/grants/{pending.Id}")).Status, "and may give one back");

        var review = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/access/review");
        Assert.AreEqual(HttpStatusCode.OK, review.Status, review.Body.ToString());
        var rows = review.Read<AccessReviewDto>().Rows;
        Assert.IsTrue(rows.Any(row => row.PrincipalId == miaId && row.Source == $"team:{team.Id}:maintainer"));
        Assert.IsTrue(rows.Any(row => row.PrincipalId == maxId && row.Capability == "automation.edit" && row.Source.StartsWith("grant:", StringComparison.Ordinal)));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Get, "/api/v1/access/review")).Status);
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
        var adminSigns = await SignDraftAsync(server, bea);
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.AccessDocumentInvalid), (adminSigns.Status, adminSigns.Code), adminSigns.Body.ToString());
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
