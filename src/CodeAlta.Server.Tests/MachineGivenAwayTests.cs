using System.Net;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Server.Tests;

/// <summary>
/// A personal machine given to another person at the server (<c>doc/fleet/access-control.md</c>, ownership over time): once the
/// new owner accepted it, its budgets, hooks, metadata keys, values and environments are the new owner's, as its owner record
/// says, and no more those of the person the device list names for it.
/// </summary>
[TestClass]
public sealed class MachineGivenAwayTests
{
    [TestMethod]
    public async Task APersonalMachineGivenToAnotherPerson_TakesTheBudgetsHooksKeysAndValuesOfItsNewOwner_AndNoneOfItsFormerOwner()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, machine) = await server.SetUpWithNodeAsync(owner, node);
        var (mia, miaId) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Mia");
        var organization = setup.Organization.Id;
        var alexId = setup.Owner.Id;

        // Alex's own budget of every machine of his, made before he gives the machine away.
        Assert.AreEqual(HttpStatusCode.OK, (await PutBudgetAsync(owner, alexId, "alex-all", FleetBudgetScopeKinds.Person, null)).Status);

        var offer = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/transfers", new OfferTransferRequest(FleetResourceKinds.Machine, machine.Id, FleetPrincipalKinds.Person,
            miaId))).Read<TransferDto>();
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mia, HttpMethod.Post, $"/api/v1/transfers/{offer.Id}/accept")).Status);

        // Hooks: the new owner makes them for the machine, the former owner no more.
        var hook = await server.SendAsync(mia, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(machine.Id, HookSources.Webhook, null));
        Assert.IsTrue(hook.Status is HttpStatusCode.OK or HttpStatusCode.Created, hook.Body.ToString());
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/hooks", new CreateHookRequest(machine.Id, HookSources.Webhook, null))).Status);

        // Budgets: the new owner limits the machine; the machine reads its new owner's budgets, not its former owner's.
        var byMia = await PutBudgetAsync(mia, miaId, "mia-box", FleetBudgetScopeKinds.Machine, machine.Id);
        Assert.AreEqual(HttpStatusCode.OK, byMia.Status, byMia.Body.ToString());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PutBudgetAsync(owner, alexId, "alex-box", FleetBudgetScopeKinds.Machine, machine.Id)).Status);
        var budgets = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/machine-budgets")).Read<FleetMachineBudgetListDto>().Budgets;
        CollectionAssert.AreEqual(new[] { "mia-box" }, budgets.Select(static budget => budget.Id).ToArray());

        // Metadata keys: the new owner's devices give theirs to the machine, which reads them; the former owner's give it nothing.
        var key = await server.SendAsync(mia, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("mia-key", 1,
            [new MetadataKeyWrapDto(mia.DeviceId, "{\"for\":\"mia\"}"), new MetadataKeyWrapDto(node.DeviceId, "{\"for\":\"box\"}")], "mia-space", "{\"sealed\":\"space\"}"));
        Assert.IsTrue(key.Status is HttpStatusCode.OK or HttpStatusCode.Created, key.Body.ToString());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("alex-key", 1,
            [new MetadataKeyWrapDto(owner.DeviceId, "{\"for\":\"alex\"}"), new MetadataKeyWrapDto(node.DeviceId, "{\"for\":\"box\"}")], "alex-space", "{\"sealed\":\"space\"}"))).Status);
        var keys = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/metadata-keys")).Read<ListResponse<MetadataKeyDto>>().Items;
        Assert.AreEqual(("mia-key", "{\"for\":\"box\"}"), (keys.Single().Id, keys.Single().Wrapped));

        // Values and environments: the new owner seals values for the machine, which holds them; the former owner seals none for it.
        var value = await server.SendAsync(mia, HttpMethod.Put, "/api/v1/secrets/miaToken", new SetFleetSecretRequest(FleetSecretKinds.Secret, "TOKEN",
            new FleetSecretScopeDto(FleetSecretScopeKinds.Person), 1, false, [new FleetSecretCopyDto(mia.DeviceId, "{\"for\":\"mia\"}"), new FleetSecretCopyDto(node.DeviceId, "{\"for\":\"box\"}")]));
        Assert.AreEqual(HttpStatusCode.OK, value.Status, value.Body.ToString());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/secrets/alexToken", new SetFleetSecretRequest(FleetSecretKinds.Secret, "TOKEN",
            new FleetSecretScopeDto(FleetSecretScopeKinds.Person), 1, false, [new FleetSecretCopyDto(owner.DeviceId, "{\"for\":\"alex\"}"), new FleetSecretCopyDto(node.DeviceId, "{\"for\":\"box\"}")]))).Status);
        var held = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/secrets/held")).Read<FleetSecretListDto>().Secrets;
        Assert.AreEqual(("miaToken", "{\"for\":\"box\"}"), (held.Single().Id, held.Single().Copy));

        var environment = await server.SendAsync(mia, HttpMethod.Put, "/api/v1/environments/mia-env", new PutFleetEnvironmentRequest(SignEnvironment(mia,
            new FleetEnvironmentDefinition(FleetEnvironmentDefinition.CurrentVersion, organization, miaId, "mia-env", "prod", 1, [], [], [], false, mia.DeviceId, server.Clock.Now))));
        Assert.IsTrue(environment.Status is HttpStatusCode.OK or HttpStatusCode.Created, environment.Body.ToString());
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Put, "/api/v1/environments/alex-env", new PutFleetEnvironmentRequest(SignEnvironment(owner,
            new FleetEnvironmentDefinition(FleetEnvironmentDefinition.CurrentVersion, organization, alexId, "alex-env", "prod", 1, [], [], [], false, owner.DeviceId, server.Clock.Now))))).Status);
        var environments = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/environments")).Read<FleetEnvironmentListDto>().Environments;
        CollectionAssert.AreEqual(new[] { "mia-env" }, environments.Select(static item => item.Id).ToArray());

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
    public async Task AMachineGivenToAnotherPerson_SealsWithTheKeysOfItsNewOwner_AndItsFormerOwnerSealsWithANewEpochItDoesNotHold()
    {
        await using var server = await ServerFixture.StartAsync();
        using var root = new TempDirectory();
        using var alexKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "alex"));
        using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
        using var miaKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "mia"));
        using var alex = new FleetServerClient(server.BaseAddress, alexKey);
        await alex.SetUpAsync(server.SetupCode, "Alex", "Desktop", "Acme", CancellationToken.None);
        var enrollment = await alex.CreateEnrollmentTokenAsync(null, "build-box", CancellationToken.None);
        using (var node = new FleetServerClient(server.BaseAddress, nodeKey)) await node.EnrollAsync(enrollment.Token, "Build box", CancellationToken.None);
        await alex.ApproveAsync(nodeKey.DeviceId, FleetDeviceKinds.Node, CancellationToken.None);
        var invitation = await alex.InvitePersonAsync(new InvitePersonRequest("Mia"));
        using var mia = new FleetServerClient(server.BaseAddress, miaKey);
        await mia.EnrollAsync(invitation.Token, "Mia laptop", FleetDeviceKinds.Controller, CancellationToken.None);
        await alex.ApproveAsync(miaKey.DeviceId, FleetDeviceKinds.Controller, CancellationToken.None);

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
        var first = (await alexKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreEqual(first.Id, (await nodeKeys.RefreshAsync("work", CancellationToken.None))?.Id, "the machine of Alex seals with his key");

        // Alex gives the machine to Mia, who accepts; Alex signs the version that says so.
        var transfer = await alex.OfferTransferAsync(new OfferTransferRequest(FleetResourceKinds.Machine, nodeKey.DeviceId, FleetPrincipalKinds.Person, invitation.Person.Id));
        await mia.DecideTransferAsync(transfer.Id, "accept");
        var console = new FleetOrganizationAccess(new FleetOrganizationDevices(server.BaseAddress, alexKey));
        await console.SignAsync((await console.PrepareAsync(CancellationToken.None))!, CancellationToken.None);
        foreach (var access in new[] { alexAccess, nodeAccess, miaAccess }) await access.RefreshAsync(CancellationToken.None);

        // The machine no longer seals with the key of its former owner, at once and once it reads its keys again.
        Assert.IsNull(nodeKeys.CurrentFor("work"), "the key of Alex seals nothing more on Mia's machine");
        Assert.IsNull(await nodeKeys.RefreshAsync("work", CancellationToken.None), "and his copies do not open there any more");

        // Alex seals from now on with a new epoch the machine does not hold: Mia reads nothing new of his.
        var second = (await alexKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.AreEqual((first.Grant!.SpaceId, 2), (second.Grant!.SpaceId, second.Grant.Epoch));
        Assert.IsFalse((await alex.ListMetadataKeysAsync()).Single(key => key.Id == second.Id).WrappedTo.Contains(nodeKey.DeviceId));

        // Mia's key reaches her machine, which seals with it.
        var hers = (await miaKeys.RefreshAsync("work", CancellationToken.None))!;
        Assert.IsTrue((await mia.ListMetadataKeysAsync()).Single(key => key.Id == hers.Id).WrappedTo.Contains(nodeKey.DeviceId));
        var sealing = await nodeKeys.RefreshAsync("work", CancellationToken.None);
        Assert.AreEqual((hers.Id, invitation.Person.Id), (sealing?.Id, sealing?.Grant?.PersonId));
    }

    private static FleetSignedEnvironment SignEnvironment(TestDevice signer, FleetEnvironmentDefinition definition)
    {
        var text = FleetEnvironmentDocuments.Serialize(definition);
        return new FleetSignedEnvironment(text, signer.Sign(FleetEnvironmentDocuments.GetSignedBytes(text)));
    }
}
