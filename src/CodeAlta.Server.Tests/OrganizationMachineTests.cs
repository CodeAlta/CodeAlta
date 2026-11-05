using System.Net;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Server.Tests;

/// <summary>
/// A machine of the organization (<c>doc/fleet/access-control.md</c>, team roles): its owners and the admins who sign manage it,
/// so its budgets, hooks, values and environments are theirs; they use it for their own sessions, so their devices give it the keys
/// of their spaces, and it seals each session with the key of the person who started it there. The person the device list names
/// for it, who enrolled it, gets nothing of it unless they are an owner or an admin.
/// </summary>
[TestClass]
public sealed class OrganizationMachineTests
{
    [TestMethod]
    public async Task AMachineOfTheOrganization_TakesTheBudgetsHooksValuesEnvironmentsAndKeysOfItsSigners_AndNothingOfItsDeviceListPerson()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (ola, olaId) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Ola", FleetOrganizationRoles.Admin);
        var (ada, adaId) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Ada", FleetOrganizationRoles.Admin);
        var (max, maxId) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Max");
        using var olaDevice = ola;
        using var adaDevice = ada;
        using var maxDevice = max;
        var organization = setup.Organization.Id;
        var alexId = setup.Owner.Id;

        // Alex's budget of every machine of his, made while he holds the machine he enrolled.
        Assert.AreEqual(HttpStatusCode.OK, (await PutBudgetAsync(owner, alexId, "alex-all", FleetBudgetScopeKinds.Person, null)).Status);

        // The machine Alex enrolled becomes the organization's; Ola becomes an owner, and Alex a member who signs nothing.
        var offer = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id,
            FleetPrincipalKinds.Organization, organization))).Read<TransferDto>();
        var accepted = await server.SendAsync(ada, HttpMethod.Post, $"/api/v1/transfers/{offer.Id}/accept");
        Assert.AreEqual(HttpStatusCode.OK, accepted.Status, accepted.Body.ToString());
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, $"/api/v1/people/{olaId}/role", new SetRoleRequest(FleetOrganizationRoles.Owner))).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(ola, HttpMethod.Put, $"/api/v1/people/{alexId}/role", new SetRoleRequest(FleetOrganizationRoles.Member))).Status);

        // Hooks: an owner or an admin makes them for the machine; a member and the device-list person do not.
        foreach (var maker in new[] { ola, ada })
        {
            var hook = await server.SendAsync(maker, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(machine.Id, HookSources.Webhook, null));
            Assert.IsTrue(hook.Status is HttpStatusCode.OK or HttpStatusCode.Created, hook.Body.ToString());
        }

        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(max, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(machine.Id, HookSources.Webhook, null))).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(machine.Id, HookSources.Webhook, null))).Status);

        // Budgets: an admin limits the machine, which reads the budgets of its owners and admins only.
        var byAda = await PutBudgetAsync(ada, adaId, "ada-box", FleetBudgetScopeKinds.Machine, machine.Id);
        Assert.AreEqual(HttpStatusCode.OK, byAda.Status, byAda.Body.ToString());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PutBudgetAsync(max, maxId, "max-box", FleetBudgetScopeKinds.Machine, machine.Id)).Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PutBudgetAsync(owner, alexId, "alex-box", FleetBudgetScopeKinds.Machine, machine.Id)).Status);
        var budgets = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/machine-budgets")).Read<FleetMachineBudgetListDto>().Budgets;
        CollectionAssert.AreEqual(new[] { "ada-box" }, budgets.Select(static budget => budget.Id).ToArray());

        // Metadata keys: the devices of its owners and admins give it theirs, and it reads them; those of others no more.
        foreach (var (device, person) in new[] { (ola, "ola"), (ada, "ada") })
        {
            var key = await server.SendAsync(device, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest(person + "-key", 1,
                [new MetadataKeyWrapDto(device.DeviceId, $"{{\"for\":\"{person}\"}}"), new MetadataKeyWrapDto(node.DeviceId, $"{{\"for\":\"box-{person}\"}}")], person + "-space",
                "{\"sealed\":\"space\"}"));
            Assert.IsTrue(key.Status is HttpStatusCode.OK or HttpStatusCode.Created, key.Body.ToString());
        }

        foreach (var (device, person) in new[] { (max, "max"), (owner, "alex") })
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(device, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest(person + "-key", 1,
                [new MetadataKeyWrapDto(device.DeviceId, "{\"for\":\"self\"}"), new MetadataKeyWrapDto(node.DeviceId, "{\"for\":\"box\"}")], person + "-space",
                "{\"sealed\":\"space\"}"))).Status, person);
        }

        var keys = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/metadata-keys")).Read<ListResponse<MetadataKeyDto>>().Items;
        CollectionAssert.AreEquivalent(new[] { ("ada-key", "{\"for\":\"box-ada\"}"), ("ola-key", "{\"for\":\"box-ola\"}") }, keys.Select(static key => (key.Id, key.Wrapped)).ToArray());

        // Values: an admin seals them for the machine, which holds them; a member and the device-list person seal none for it.
        Task<ApiAnswer> PutValueAsync(TestDevice signer, string id) => server.SendAsync(signer, HttpMethod.Put, $"/api/v1/secrets/{id}", new SetFleetSecretRequest(FleetSecretKinds.Secret,
            "TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), 1, false, [new FleetSecretCopyDto(signer.DeviceId, "{\"for\":\"self\"}"),
                new FleetSecretCopyDto(node.DeviceId, $"{{\"for\":\"{id}\"}}")]));
        var value = await PutValueAsync(ada, "adaToken");
        Assert.AreEqual(HttpStatusCode.OK, value.Status, value.Body.ToString());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PutValueAsync(max, "maxToken")).Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PutValueAsync(owner, "alexToken")).Status);
        var held = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/secrets/held")).Read<FleetSecretListDto>().Secrets;
        Assert.AreEqual(("adaToken", "{\"for\":\"adaToken\"}"), (held.Single().Id, held.Single().Copy));

        // Environments: the machine reads those of its owners and admins only.
        foreach (var (device, person) in new[] { (ada, adaId), (max, maxId), (owner, alexId) })
        {
            var id = "env-" + device.DeviceId[..6];
            var environment = await server.SendAsync(device, HttpMethod.Put, $"/api/v1/environments/{id}", new PutFleetEnvironmentRequest(SignEnvironment(device,
                new FleetEnvironmentDefinition(FleetEnvironmentDefinition.CurrentVersion, organization, person, id, "prod", 1, [], [], [], false, device.DeviceId, server.Clock.Now))));
            Assert.IsTrue(environment.Status is HttpStatusCode.OK or HttpStatusCode.Created, environment.Body.ToString());
        }

        var environments = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/environments")).Read<FleetEnvironmentListDto>().Environments;
        CollectionAssert.AreEqual(new[] { "env-" + ada.DeviceId[..6] }, environments.Select(static item => item.Id).ToArray());

        // Grants of a session of the machine: the owner or admin who started it there holds its key and confirms it; nobody else.
        var store = server.GetService<CodeAlta.Server.Storage.ICoordinatorStore>();
        var cursors = await store.GetPublicationCursorsAsync(node.DeviceId);
        await server.GetService<CodeAlta.Server.Services.PublicationService>().PublishFactsAsync((await store.GetDeviceAsync(node.DeviceId))!, new FleetFactsPublish(
            Math.Max(cursors.Facts, cursors.Inventory) + 1, [new FleetSessionFact("s-ada", 1, 1, "idle", server.Clock.Now, server.Clock.Now, PersonId: adaId),
                new FleetSessionFact("s-alex", 1, 1, "idle", server.Clock.Now, server.Clock.Now, PersonId: alexId)]), CancellationToken.None);
        async Task<string> GrantAsync(string session)
        {
            var made = await server.SendAsync(ola, HttpMethod.Post, "/api/v1/grants",
                new CreateGrantRequest(FleetPrincipalKinds.Person, maxId, "session.view-content", FleetScopeKinds.Resource, "session:" + session, Sealed: true));
            Assert.AreEqual(HttpStatusCode.Created, made.Status, made.Body.ToString());
            return made.Read<GrantDto>().Id;
        }

        var ofAda = await GrantAsync("s-ada");
        var ofAlex = await GrantAsync("s-alex");
        var copy = new ConfirmGrantKeysRequest("sealed", [new GrantKeyWrapDto(max.DeviceId, "wrapped")]);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(ola, HttpMethod.Post, $"/api/v1/grants/{ofAda}/keys", copy)).Status, "another signer holds no key of it");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/grants/{ofAlex}/keys", copy)).Status,
            "the device-list person, who no longer signs, gives no key there");
        var confirmed = await server.SendAsync(ada, HttpMethod.Post, $"/api/v1/grants/{ofAda}/keys", copy);
        Assert.AreEqual(HttpStatusCode.OK, confirmed.Status, confirmed.Body.ToString());
        Assert.AreEqual(adaId, (await server.SendAsync(max, HttpMethod.Get, "/api/v1/grant-keys")).Read<AccessListDto<GrantKeyDto>>().Items.Single().PersonId);

        // What the machine spends counts for the budget its admin set for it, never for the budget of the device-list person's machines.
        var box = await server.GetService<CodeAlta.Server.Storage.ICoordinatorStore>().GetDeviceAsync(node.DeviceId, CancellationToken.None);
        var row = new FleetStatisticsRow(FleetStatisticsFamilies.Usage, FleetStatisticsFamilies.QuarterOf(server.Clock.Now), "anthropic", "claude", [""],
            new Dictionary<string, long> { ["input_tokens"] = 20, ["output_tokens"] = 10, ["requests"] = 1 });
        await server.GetService<CodeAlta.Server.Services.StatisticsService>().PublishAsync(box!, new FleetStatisticsPublish(1, "generation-box",
            [new FleetStatisticsSet("session-of-box", 0, 1, null, [row])]), CancellationToken.None);
        async Task<long> UsedOfAsync(TestDevice signer, string id) => (await server.SendAsync(signer, HttpMethod.Get, "/api/v1/budgets")).Read<FleetBudgetListDto>().Budgets
            .Single(budget => budget.Id == id).Usage!.Used;
        Assert.AreEqual((30L, 0L), (await UsedOfAsync(ada, "ada-box"), await UsedOfAsync(owner, "alex-all")));

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
    public async Task AMachineOfTheOrganization_SealsEachSessionWithTheKeyOfItsSigner_NoneWithoutOne_AndAnAdminWhoStopsSigningSealsWithANewEpoch()
    {
        await using var server = await ServerFixture.StartAsync();
        using var root = new TempDirectory();
        using var alexKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "alex"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        using var miaKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "mia"));
        using var maxKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "max"));
        using var alex = new FleetServerClient(server.BaseAddress, alexKey);
        var setup = await alex.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Acme", CancellationToken.None);
        var alexId = setup.Owner.Id;
        var enrollment = await alex.CreateEnrollmentTokenAsync(null, "build-box", CancellationToken.None);
        using (var node = new FleetServerClient(server.BaseAddress, nodeKey)) await node.EnrollAsync(enrollment.Token, "Build box", CancellationToken.None);
        await alex.ApproveAsync(nodeKey.DeviceId, FleetDeviceKinds.Node, CancellationToken.None);
        async Task<(FleetServerClient Client, string PersonId)> InviteAsync(string name, string role, DeviceIdentity key)
        {
            var invitation = await alex.InvitePersonAsync(new InvitePersonRequest(name, role));
            var client = new FleetServerClient(server.BaseAddress, key);
            await client.EnrollAsync(invitation.Token, name + " laptop", FleetDeviceKinds.Controller, CancellationToken.None);
            await alex.ApproveAsync(key.DeviceId, FleetDeviceKinds.Controller, CancellationToken.None);
            return (client, invitation.Person.Id);
        }

        var (mia, miaId) = await InviteAsync("Mia", FleetOrganizationRoles.Admin, miaKey);
        var (max, maxId) = await InviteAsync("Max", FleetOrganizationRoles.Member, maxKey);
        using var miaClient = mia;
        using var maxClient = max;

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
        var maxKeys = Keyring(maxKey, out var maxAccess);
        var first = (await alexKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreEqual(first.Id, (await nodeKeys.RefreshAsync("work", CancellationToken.None))?.Id, "the machine of Alex seals with his key");

        // The machine becomes the organization's: Alex, its owner, and Mia, an admin, sign; Max is a member.
        var transfer = await alex.OfferTransferAsync(new OfferTransferRequest(FleetResourceKinds.Machine, nodeKey.DeviceId, FleetPrincipalKinds.Organization, setup.Organization.Id));
        await mia.DecideTransferAsync(transfer.Id, "accept");
        var console = new FleetOrganizationAccess(new FleetOrganizationDevices(server.BaseAddress, alexKey));
        async Task SignAsync()
        {
            await console.SignAsync((await console.PrepareAsync(CancellationToken.None))!, CancellationToken.None);
            foreach (var access in new[] { alexAccess, nodeAccess, miaAccess, maxAccess }) await access.RefreshAsync(CancellationToken.None);
        }

        await SignAsync();
        Assert.IsTrue(nodeAccess.Current?.IsOrganizationMachine(nodeKey.DeviceId) == true, "the machine knows it is the organization's");
        Assert.IsTrue(nodeAccess.Current!.IsSigner(miaId), "Mia signs");

        // A session without a signer (local work, an automation of the organization) is sealed with no key: it publishes no title.
        Assert.IsNull(nodeKeys.CurrentFor("work"), "no key seals on the machine of the organization by itself");
        Assert.IsNull(await nodeKeys.RefreshAsync("work", CancellationToken.None), "and nothing seals a session without its person");
        Assert.AreEqual(first.Id, nodeKeys.CurrentFor("work", alexId)?.Id, "a session of Alex, an owner, seals with his key");
        Assert.AreEqual(first.Id, (await alexKeys.RefreshAsync("work", CancellationToken.None))?.Id, "Alex signs: his key stays");

        // The keys of the admin reach the machine, which seals her sessions with them; a member's do not.
        var miasKey = (await miaKeys.RefreshAsync("work", CancellationToken.None))!;
        var maxsKey = (await maxKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.IsTrue((await mia.ListMetadataKeysAsync()).Single(key => key.Id == miasKey.Id).WrappedTo.Contains(nodeKey.DeviceId));
        Assert.IsFalse((await max.ListMetadataKeysAsync()).Single(key => key.Id == maxsKey.Id).WrappedTo.Contains(nodeKey.DeviceId));
        await nodeKeys.RefreshAsync("work", CancellationToken.None);
        Assert.AreEqual((miasKey.Id, miaId), (nodeKeys.CurrentFor("work", miaId)?.Id, nodeKeys.CurrentFor("work", miaId)?.Grant?.PersonId));
        Assert.IsNull(nodeKeys.CurrentFor("work", maxId), "a member's session there publishes no title");

        // Mia is no more an admin: once signed, the machine seals nothing more of hers, and her devices seal with a new epoch it does not hold.
        await alex.SetPersonRoleAsync(miaId, FleetOrganizationRoles.Member);
        await SignAsync();
        Assert.IsFalse(nodeAccess.Current!.IsSigner(miaId));
        Assert.IsNull(nodeKeys.CurrentFor("work", miaId), "her key seals nothing more there, at once");
        var miasNext = (await miaKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreEqual((miasKey.Grant!.SpaceId, 2), (miasNext.Grant!.SpaceId, miasNext.Grant.Epoch));
        Assert.IsFalse((await mia.ListMetadataKeysAsync()).Single(key => key.Id == miasNext.Id).WrappedTo.Contains(nodeKey.DeviceId));
        await nodeKeys.RefreshAsync("work", CancellationToken.None);
        Assert.IsNull(nodeKeys.CurrentFor("work", miaId));
        Assert.AreEqual(first.Id, nodeKeys.CurrentFor("work", alexId)?.Id, "the owner's sessions still seal with his key");
    }

    private static FleetSignedEnvironment SignEnvironment(TestDevice signer, FleetEnvironmentDefinition definition)
    {
        var text = FleetEnvironmentDocuments.Serialize(definition);
        return new FleetSignedEnvironment(text, signer.Sign(FleetEnvironmentDocuments.GetSignedBytes(text)));
    }
}
