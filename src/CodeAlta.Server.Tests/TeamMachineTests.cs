using System.Net;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Server.Tests;

/// <summary>
/// A machine of a team (<c>doc/fleet/access-control.md</c>, team roles): its maintainers manage it, so its budgets, hooks,
/// values and environments are theirs; its maintainers and members use it for their own sessions, so their devices give it the
/// keys of their spaces, and it seals each session with the key of the person who started it there. The person the device
/// list names for it, who enrolled it, gets nothing of it unless the team gives them a role.
/// </summary>
[TestClass]
public sealed class TeamMachineTests
{
    [TestMethod]
    public async Task ATeamMachine_TakesTheBudgetsHooksValuesAndEnvironmentsOfItsMaintainers_TheKeysOfItsPeople_AndNothingOfItsDeviceListPerson()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (mia, miaId) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Mia");
        var (bea, beaId) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Bea");
        var organization = setup.Organization.Id;
        var alexId = setup.Owner.Id;

        // Alex's budget of every machine of his, made while he holds the machine he enrolled.
        Assert.AreEqual(HttpStatusCode.OK, (await PutBudgetAsync(owner, alexId, "alex-all", FleetBudgetScopeKinds.Person, null)).Status);

        // Mia maintains the team, Bea is a member; Alex, who made it, leaves it, and the machine he enrolled becomes the team's.
        var team = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("Infra"))).Read<TeamDto>();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{miaId}", new SetTeamMemberRequest(FleetTeamRoles.Maintainer))).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/teams/{team.Id}/members/{beaId}", new SetTeamMemberRequest(FleetTeamRoles.Member))).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Delete, $"/api/v1/teams/{team.Id}/members/{alexId}")).Status);
        var offer = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Team,
            team.Id))).Read<TransferDto>();
        var accepted = await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/transfers/{offer.Id}/accept");
        Assert.AreEqual(HttpStatusCode.OK, accepted.Status, accepted.Body.ToString());

        // Hooks: a maintainer makes them for the machine; a member and the device-list person do not.
        var hook = await server.SendAsync(mia, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(machine.Id, HookSources.Webhook, null));
        Assert.IsTrue(hook.Status is HttpStatusCode.OK or HttpStatusCode.Created, hook.Body.ToString());
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(bea, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(machine.Id, HookSources.Webhook, null))).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(machine.Id, HookSources.Webhook, null))).Status);

        // Budgets: a maintainer limits the machine, which reads the budgets of its maintainers only.
        var byMia = await PutBudgetAsync(mia, miaId, "mia-box", FleetBudgetScopeKinds.Machine, machine.Id);
        Assert.AreEqual(HttpStatusCode.OK, byMia.Status, byMia.Body.ToString());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PutBudgetAsync(bea, beaId, "bea-box", FleetBudgetScopeKinds.Machine, machine.Id)).Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PutBudgetAsync(owner, alexId, "alex-box", FleetBudgetScopeKinds.Machine, machine.Id)).Status);
        var budgets = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/machine-budgets")).Read<FleetMachineBudgetListDto>().Budgets;
        CollectionAssert.AreEqual(new[] { "mia-box" }, budgets.Select(static budget => budget.Id).ToArray());

        // Metadata keys: the devices of its maintainers and members give it theirs, and it reads them; the device-list person's no more.
        foreach (var (device, person) in new[] { (mia, "mia"), (bea, "bea") })
        {
            var key = await server.SendAsync(device, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest(person + "-key", 1,
                [new MetadataKeyWrapDto(device.DeviceId, $"{{\"for\":\"{person}\"}}"), new MetadataKeyWrapDto(node.DeviceId, $"{{\"for\":\"box-{person}\"}}")], person + "-space",
                "{\"sealed\":\"space\"}"));
            Assert.IsTrue(key.Status is HttpStatusCode.OK or HttpStatusCode.Created, key.Body.ToString());
        }

        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("alex-key", 1,
            [new MetadataKeyWrapDto(owner.DeviceId, "{\"for\":\"alex\"}"), new MetadataKeyWrapDto(node.DeviceId, "{\"for\":\"box\"}")], "alex-space", "{\"sealed\":\"space\"}"))).Status);
        var keys = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/metadata-keys")).Read<ListResponse<MetadataKeyDto>>().Items;
        CollectionAssert.AreEquivalent(new[] { ("bea-key", "{\"for\":\"box-bea\"}"), ("mia-key", "{\"for\":\"box-mia\"}") }, keys.Select(static key => (key.Id, key.Wrapped)).ToArray());

        // Values: a maintainer seals them for the machine, which holds them; a member and the device-list person seal none for it.
        Task<ApiAnswer> PutValueAsync(TestDevice signer, string id) => server.SendAsync(signer, HttpMethod.Put, $"/api/v1/secrets/{id}", new SetFleetSecretRequest(FleetSecretKinds.Secret,
            "TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), 1, false, [new FleetSecretCopyDto(signer.DeviceId, "{\"for\":\"self\"}"),
                new FleetSecretCopyDto(node.DeviceId, $"{{\"for\":\"{id}\"}}")]));
        var value = await PutValueAsync(mia, "miaToken");
        Assert.AreEqual(HttpStatusCode.OK, value.Status, value.Body.ToString());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PutValueAsync(bea, "beaToken")).Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PutValueAsync(owner, "alexToken")).Status);
        var held = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/secrets/held")).Read<FleetSecretListDto>().Secrets;
        Assert.AreEqual(("miaToken", "{\"for\":\"miaToken\"}"), (held.Single().Id, held.Single().Copy));

        // Environments: the machine reads those of its maintainers only.
        foreach (var (device, person) in new[] { (mia, miaId), (bea, beaId), (owner, alexId) })
        {
            var id = "env-" + device.DeviceId[..6];
            var environment = await server.SendAsync(device, HttpMethod.Put, $"/api/v1/environments/{id}", new PutFleetEnvironmentRequest(SignEnvironment(device,
                new FleetEnvironmentDefinition(FleetEnvironmentDefinition.CurrentVersion, organization, person, id, "prod", 1, [], [], [], false, device.DeviceId, server.Clock.Now))));
            Assert.IsTrue(environment.Status is HttpStatusCode.OK or HttpStatusCode.Created, environment.Body.ToString());
        }

        var environments = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/environments")).Read<FleetEnvironmentListDto>().Environments;
        CollectionAssert.AreEqual(new[] { "env-" + mia.DeviceId[..6] }, environments.Select(static item => item.Id).ToArray());

        // What the machine spends counts for the budget its maintainer set for it, never for the budget of the device-list person's machines.
        var box = await server.GetService<CodeAlta.Server.Storage.ICoordinatorStore>().GetDeviceAsync(node.DeviceId, CancellationToken.None);
        var row = new FleetStatisticsRow(FleetStatisticsFamilies.Usage, FleetStatisticsFamilies.QuarterOf(server.Clock.Now), "anthropic", "claude", [""],
            new Dictionary<string, long> { ["input_tokens"] = 20, ["output_tokens"] = 10, ["requests"] = 1 });
        await server.GetService<CodeAlta.Server.Services.StatisticsService>().PublishAsync(box!, new FleetStatisticsPublish(1, "generation-box",
            [new FleetStatisticsSet("session-of-box", 0, 1, null, [row])]), CancellationToken.None);
        async Task<long> UsedOfAsync(TestDevice signer, string id) => (await server.SendAsync(signer, HttpMethod.Get, "/api/v1/budgets")).Read<FleetBudgetListDto>().Budgets
            .Single(budget => budget.Id == id).Usage!.Used;
        Assert.AreEqual((30L, 0L), (await UsedOfAsync(mia, "mia-box"), await UsedOfAsync(owner, "alex-all")));

        Task<ApiAnswer> PutBudgetAsync(TestDevice signer, string person, string id, string scope, string? scopeId)
        {
            var text = FleetBudgetDocuments.Serialize(new FleetBudgetDefinition(FleetBudgetDefinition.CurrentVersion, organization, person, id, id, scope, scopeId,
                FleetBudgetMeasures.Tokens, 1000, FleetBudgetPeriods.Day, "UTC", FleetBudgetModes.Advisory, FleetBudgetReachedActions.Stop, 1, false, signer.DeviceId,
                server.Clock.Now));
            return server.SendAsync(signer, HttpMethod.Put, $"/api/v1/budgets/{id}",
                new PutFleetBudgetRequest(new FleetSignedBudget(text, signer.Sign(FleetBudgetDocuments.GetSignedBytes(text)))));
        }
    }

    [TestMethod]
    public async Task ATeamMachine_SealsEachSessionWithTheKeyOfItsPerson_NoneWithoutOne_AndAPersonWhoLeavesTheTeamSealsWithANewEpoch()
    {
        await using var server = await ServerFixture.StartAsync();
        using var root = new TempDirectory();
        using var alexKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "alex"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        using var miaKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "mia"));
        using var beaKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "bea"));
        using var alex = new FleetServerClient(server.BaseAddress, alexKey);
        var alexId = (await alex.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Acme", CancellationToken.None)).Owner.Id;
        var enrollment = await alex.CreateEnrollmentTokenAsync(null, "build-box", CancellationToken.None);
        using (var node = new FleetServerClient(server.BaseAddress, nodeKey)) await node.EnrollAsync(enrollment.Token, "Build box", CancellationToken.None);
        await alex.ApproveAsync(nodeKey.DeviceId, FleetDeviceKinds.Node, CancellationToken.None);
        async Task<(FleetServerClient Client, string PersonId)> InviteAsync(string name, DeviceIdentity key)
        {
            var invitation = await alex.InvitePersonAsync(new InvitePersonRequest(name));
            var client = new FleetServerClient(server.BaseAddress, key);
            await client.EnrollAsync(invitation.Token, name + " laptop", FleetDeviceKinds.Controller, CancellationToken.None);
            await alex.ApproveAsync(key.DeviceId, FleetDeviceKinds.Controller, CancellationToken.None);
            return (client, invitation.Person.Id);
        }

        var (mia, miaId) = await InviteAsync("Mia", miaKey);
        var (bea, beaId) = await InviteAsync("Bea", beaKey);
        using var miaClient = mia;
        using var beaClient = bea;

        // Each device reads its keys with the access document it verified, as the connector of its link gives it.
        FleetMetadataKeyring Keyring(DeviceIdentity identity, out FleetOrganizationAccess access)
        {
            var devices = new FleetOrganizationDevices(server.BaseAddress, identity);
            var held = access = new FleetOrganizationAccess(devices);
            return new FleetMetadataKeyring(devices) { Access = () => held.Current };
        }

        var alexKeys = Keyring(alexKey, out var alexAccess);
        var nodeKeys = Keyring(nodeKey, out var nodeAccess);
        var miaKeys = Keyring(miaKey, out var miaAccess);
        var beaKeys = Keyring(beaKey, out var beaAccess);
        var first = (await alexKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreEqual(first.Id, (await nodeKeys.RefreshAsync("work", CancellationToken.None))?.Id, "the machine of Alex seals with his key");

        // The machine becomes the team's: Mia maintains the team, Bea is a member, Alex leaves it; Alex signs the version that says so.
        var team = await alex.CreateTeamAsync("Infra");
        await alex.SetTeamMemberAsync(team.Id, miaId, FleetTeamRoles.Maintainer);
        await alex.SetTeamMemberAsync(team.Id, beaId, FleetTeamRoles.Member);
        await alex.SetTeamMemberAsync(team.Id, alexId, null);
        var transfer = await alex.OfferTransferAsync(new OfferTransferRequest(FleetResourceKinds.Machine, nodeKey.DeviceId, FleetPrincipalKinds.Team, team.Id));
        await mia.DecideTransferAsync(transfer.Id, "accept");
        var console = new FleetOrganizationAccess(new FleetOrganizationDevices(server.BaseAddress, alexKey));
        async Task SignAsync()
        {
            await console.SignAsync((await console.PrepareAsync(CancellationToken.None))!, CancellationToken.None);
            foreach (var access in new[] { alexAccess, nodeAccess, miaAccess, beaAccess }) await access.RefreshAsync(CancellationToken.None);
        }

        await SignAsync();
        Assert.IsNotNull(nodeAccess.Current?.TeamOf(nodeKey.DeviceId), "the machine knows it is the team's");

        // A session without a person of the team (local work, an automation of the team) is sealed with no key: it publishes no title.
        Assert.IsNull(nodeKeys.CurrentFor("work"), "the key of Alex seals nothing more on the team machine");
        Assert.IsNull(await nodeKeys.RefreshAsync("work", CancellationToken.None), "and nothing seals a session without its person");
        Assert.IsNull(nodeKeys.CurrentFor("work", alexId), "nor a session of Alex, who is not in the team");

        // Alex seals from now on with a new epoch the machine does not hold.
        var second = (await alexKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreEqual((first.Grant!.SpaceId, 2), (second.Grant!.SpaceId, second.Grant.Epoch));
        Assert.IsFalse((await alex.ListMetadataKeysAsync()).Single(key => key.Id == second.Id).WrappedTo.Contains(nodeKey.DeviceId));

        // The keys of its maintainer and its member reach the machine, which seals the sessions of each with their own key.
        var miasKey = (await miaKeys.RefreshAsync("work", CancellationToken.None))!;
        var beasKey = (await beaKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.IsTrue((await mia.ListMetadataKeysAsync()).Single(key => key.Id == miasKey.Id).WrappedTo.Contains(nodeKey.DeviceId));
        Assert.IsTrue((await bea.ListMetadataKeysAsync()).Single(key => key.Id == beasKey.Id).WrappedTo.Contains(nodeKey.DeviceId));
        Assert.IsNull(await nodeKeys.RefreshAsync("work", CancellationToken.None), "a session without its person still publishes no title");
        Assert.AreEqual((miasKey.Id, miaId), (nodeKeys.CurrentFor("work", miaId)?.Id, nodeKeys.CurrentFor("work", miaId)?.Grant?.PersonId));
        Assert.AreEqual((beasKey.Id, beaId), (nodeKeys.CurrentFor("work", beaId)?.Id, nodeKeys.CurrentFor("work", beaId)?.Grant?.PersonId));
        Assert.IsNull(nodeKeys.CurrentFor("work", alexId));

        // Bea leaves the team: once signed, the machine seals nothing more of hers, and her devices seal with a new epoch it does not hold.
        await alex.SetTeamMemberAsync(team.Id, beaId, null);
        await SignAsync();
        Assert.IsNull(nodeKeys.CurrentFor("work", beaId), "her key seals nothing more there, at once");
        var beasNext = (await beaKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreEqual((beasKey.Grant!.SpaceId, 2), (beasNext.Grant!.SpaceId, beasNext.Grant.Epoch));
        Assert.IsFalse((await bea.ListMetadataKeysAsync()).Single(key => key.Id == beasNext.Id).WrappedTo.Contains(nodeKey.DeviceId));
        await nodeKeys.RefreshAsync("work", CancellationToken.None);
        Assert.IsNull(nodeKeys.CurrentFor("work", beaId));
        Assert.AreEqual(miasKey.Id, nodeKeys.CurrentFor("work", miaId)?.Id, "the maintainer's sessions still seal with her key");
    }

    private static FleetSignedEnvironment SignEnvironment(TestDevice signer, FleetEnvironmentDefinition definition)
    {
        var text = FleetEnvironmentDocuments.Serialize(definition);
        return new FleetSignedEnvironment(text, signer.Sign(FleetEnvironmentDocuments.GetSignedBytes(text)));
    }
}
