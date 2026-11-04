using System.Net;
using System.Text;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The secrets, variables and environments of a person at the server (<c>doc/fleet/secrets.md</c>): values sealed end to
/// end by a controller device for the machines allowed to use them, write-only at the API, rotated, given to a machine
/// allowed later; and what a server alone cannot do: read a value, give one to a machine, keep a revoked machine served,
/// forge or replay the approval of an environment.
/// </summary>
[TestClass]
public sealed class FleetSecretsTests
{
    private const string Token = "glsa_fleet-secret-0123456789";

    [TestMethod]
    public async Task AValueIsSealedForItsMachines_TheServerHoldsOnlyCopies_AndItIsWriteOnly()
    {
        await using var fleet = await SecretsFixture.StartAsync();
        var secret = await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "GRAFANA_TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), Token, false,
            [fleet.NodeKey.DeviceId], CancellationToken.None);
        Assert.AreEqual((1, "GRAFANA_TOKEN"), (secret.Version, secret.Name));
        CollectionAssert.AreEquivalent(new[] { fleet.DesktopKey.DeviceId, fleet.NodeKey.DeviceId }, secret.Holders.ToArray(), "the machine allowed and the person's controller device");
        Assert.IsNotNull(secret.Copy, "the device that set it holds its own copy");

        // The machine opens its copy; another machine of the person, not allowed, holds none.
        Assert.AreEqual(Token, await fleet.OpenHeldAsync(fleet.NodeKey, "GRAFANA_TOKEN"));
        Assert.AreEqual(0, (await fleet.HeldAsync(fleet.OtherKey)).Secrets.Count);

        // The API never answers a value, and a machine does not list the values of its person.
        var listed = await fleet.Server.SendAsync(TestDevice.From(fleet.DesktopKey), HttpMethod.Get, "/api/v1/secrets");
        Assert.AreEqual(HttpStatusCode.OK, listed.Status);
        Assert.IsFalse(listed.Body.ToString().Contains(Token, StringComparison.Ordinal));
        var byNode = await fleet.Server.SendAsync(TestDevice.From(fleet.NodeKey), HttpMethod.Get, "/api/v1/secrets");
        Assert.AreEqual(HttpStatusCode.Forbidden, byNode.Status);

        // A variable is readable back by the person's devices, from their own copy.
        var variable = await fleet.Manager.SetAsync(FleetSecretKinds.Variable, "GRAFANA_URL", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), "https://grafana.example",
            false, [], CancellationToken.None);
        Assert.AreEqual("https://grafana.example", fleet.Manager.ReadVariable(variable));
        Assert.IsNull(fleet.Manager.ReadVariable(secret), "a secret is never read back");

        // Nothing the server stores holds a value: not a row, not a file of its data root.
        Assert.IsFalse(DataRootHolds(fleet.Server, Token), "the server never holds a value");
        Assert.IsFalse(DataRootHolds(fleet.Server, "https://grafana.example"), "nor the value of a variable");
    }

    [TestMethod]
    public async Task ARotation_GivesTheMachinesTheNewVersion_AndDropsTheOld()
    {
        await using var fleet = await SecretsFixture.StartAsync();
        var scope = new FleetSecretScopeDto(FleetSecretScopeKinds.Space, "space-opaque-id");
        var set = await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "DEPLOY_KEY", scope, "v1-value", true, [fleet.NodeKey.DeviceId], CancellationToken.None);
        var oldCopy = (await fleet.HeldAsync(fleet.NodeKey)).Secrets.Single();
        var rotated = await fleet.Manager.RotateAsync(set.Id, "v2-value", CancellationToken.None);
        Assert.AreEqual((2, true), (rotated.Version, rotated.HostOnly));
        CollectionAssert.AreEquivalent(set.Holders.ToArray(), rotated.Holders.ToArray(), "the machines that held it get the new version");

        var held = (await fleet.HeldAsync(fleet.NodeKey)).Secrets.Single();
        Assert.AreEqual(2, held.Version);
        Assert.AreEqual("v2-value", await fleet.OpenHeldAsync(fleet.NodeKey, "DEPLOY_KEY"));
        // The copy of version 1 no longer opens as the current value: it is bound to its version.
        Assert.IsFalse(FleetSecretSealing.TryOpen(oldCopy.Copy, FleetSecretBinding.Of(held), fleet.NodeKey, await fleet.NodeListAsync(), out _, out _));

        // Setting the same name in the same scope rotates it; a stale version is refused.
        var third = await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "deploy_key", scope, "v3-value", true, [fleet.NodeKey.DeviceId], CancellationToken.None);
        Assert.AreEqual((set.Id, 3), (third.Id, third.Version));
        var stale = await fleet.Server.SendAsync(TestDevice.From(fleet.DesktopKey), HttpMethod.Put, $"/api/v1/secrets/{set.Id}",
            new SetFleetSecretRequest(FleetSecretKinds.Secret, "DEPLOY_KEY", scope, 3, true, [new(fleet.DesktopKey.DeviceId, "{}")]));
        Assert.AreEqual((HttpStatusCode.Conflict, CoordinatorErrorCodes.SecretConflict), (stale.Status, stale.Code));
        var taken = await fleet.Server.SendAsync(TestDevice.From(fleet.DesktopKey), HttpMethod.Put, "/api/v1/secrets/another-id",
            new SetFleetSecretRequest(FleetSecretKinds.Secret, "DEPLOY_KEY", scope, 1, false, [new(fleet.DesktopKey.DeviceId, "{}")]));
        Assert.AreEqual((HttpStatusCode.Conflict, CoordinatorErrorCodes.SecretExists), (taken.Status, taken.Code));

        // Teams and the organization come with their roles.
        var team = await fleet.Server.SendAsync(TestDevice.From(fleet.DesktopKey), HttpMethod.Put, "/api/v1/secrets/team-id",
            new SetFleetSecretRequest(FleetSecretKinds.Secret, "TEAM_TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Team, "t1"), 1, false, [new(fleet.DesktopKey.DeviceId, "{}")]));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.SecretInvalid), (team.Status, team.Code));

        var audit = await fleet.AuditAsync("secret.");
        CollectionAssert.AreEqual(new[] { "secret.set", "secret.rotate", "secret.rotate", "secret.rotate", "secret.set", "secret.set" }, audit.Select(static record => record.Action).ToArray());
        CollectionAssert.AreEqual(new[] { "success", "success", "success", CoordinatorErrorCodes.SecretConflict, CoordinatorErrorCodes.SecretExists, CoordinatorErrorCodes.SecretInvalid },
            audit.Select(static record => record.Result).ToArray());
        Assert.IsFalse(audit.Any(static record => record.Details?.Contains("value", StringComparison.Ordinal) == true), "no value is in the audit log");
    }

    [TestMethod]
    public async Task ARotation_SealsForTheMachinesAControllerGaveTheValueTo_NotTheHoldersTheServerLists()
    {
        await using var fleet = await SecretsFixture.StartAsync();
        var set = await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "GRAFANA_TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), Token, false,
            [fleet.NodeKey.DeviceId], CancellationToken.None);
        // A genuine copy of another value, which a controller device gave the other machine.
        var other = await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "OTHER_TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), "other-value", false,
            [fleet.OtherKey.DeviceId], CancellationToken.None);
        var otherCopy = (await fleet.HeldAsync(fleet.OtherKey)).Secrets.Single().Copy!;

        // The server lists the other machine as a holder of the first value too, with that copy moved over.
        var store = (IFleetSecretStore)fleet.Server.GetService<ICoordinatorStore>();
        var organization = (await fleet.NodeListAsync()).OrganizationId!;
        await store.AddFleetSecretCopiesAsync(organization, set.Id, 1, [new FleetSecretCopyRecord(set.Id, fleet.OtherKey.DeviceId, 1, otherCopy, fleet.DesktopKey.DeviceId,
            DateTimeOffset.UtcNow)], new AuditEntry("test.forge", AuditEntry.Success, null, null, null, null, null));
        CollectionAssert.Contains((await fleet.Manager.ListAsync(CancellationToken.None)).Secrets.Single(secret => secret.Id == set.Id).Holders.ToArray(), fleet.OtherKey.DeviceId);

        // Rotate seals the new version for the machines a controller device gave it to, never for one the server added.
        var rotated = await fleet.Manager.RotateAsync(set.Id, "rotated-value", CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { fleet.DesktopKey.DeviceId, fleet.NodeKey.DeviceId }, rotated.Holders.ToArray());
        Assert.AreEqual("rotated-value", await fleet.OpenHeldAsync(fleet.NodeKey, "GRAFANA_TOKEN"));
        Assert.IsFalse((await fleet.HeldAsync(fleet.OtherKey)).Secrets.Any(secret => secret.Id == set.Id), "the machine the server added gets nothing");

        // A machine given the value later by a controller device keeps it across the rotation.
        await fleet.Manager.GrantAsync(other.Id, fleet.NodeKey.DeviceId, CancellationToken.None);
        await fleet.Manager.RotateAsync(other.Id, "other-rotated", CancellationToken.None);
        Assert.AreEqual("other-rotated", await fleet.OpenHeldAsync(fleet.NodeKey, "OTHER_TOKEN"));
        Assert.AreEqual("other-rotated", await fleet.OpenHeldAsync(fleet.OtherKey, "OTHER_TOKEN"));
    }

    [TestMethod]
    public async Task AMachineAllowedLater_GetsItsCopy_WhenAControllerOfThePersonGivesIt()
    {
        await using var fleet = await SecretsFixture.StartAsync();
        var set = await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "GRAFANA_TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), Token, false,
            [fleet.NodeKey.DeviceId], CancellationToken.None);
        Assert.AreEqual(0, (await fleet.HeldAsync(fleet.OtherKey)).Secrets.Count);

        var granted = await fleet.Manager.GrantAsync(set.Id, fleet.OtherKey.DeviceId, CancellationToken.None);
        CollectionAssert.Contains(granted.Holders.ToArray(), fleet.OtherKey.DeviceId);
        Assert.AreEqual(Token, await fleet.OpenHeldAsync(fleet.OtherKey, "GRAFANA_TOKEN"));

        // A machine only reads; withdrawn, it gets nothing at its next run.
        var byNode = await fleet.Server.SendAsync(TestDevice.From(fleet.NodeKey), HttpMethod.Post, $"/api/v1/secrets/{set.Id}/copies",
            new AddFleetSecretCopiesRequest(1, [new(fleet.OtherKey.DeviceId, "{}")]));
        Assert.AreEqual(HttpStatusCode.Forbidden, byNode.Status);
        await fleet.Manager.WithdrawAsync(set.Id, fleet.OtherKey.DeviceId, CancellationToken.None);
        Assert.AreEqual(0, (await fleet.HeldAsync(fleet.OtherKey)).Secrets.Count);
        CollectionAssert.AreEqual(new[] { "secret.set", "secret.grant", "secret.grant", "secret.withdraw" }, (await fleet.AuditAsync("secret.")).Select(static record => record.Action).ToArray());
    }

    [TestMethod]
    public async Task AServerAlone_CannotGiveAValueToAMachine_NorMoveOneToAnotherName()
    {
        await using var fleet = await SecretsFixture.StartAsync();
        var set = await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "GRAFANA_TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), Token, true,
            [fleet.NodeKey.DeviceId], CancellationToken.None);
        var list = await fleet.NodeListAsync();
        var held = (await fleet.HeldAsync(fleet.NodeKey)).Secrets.Single();

        // A server that makes a copy of its own, with a key it controls, signs no grant a controller device of the list made.
        using var rogue = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "rogue"));
        FleetDevicePublicKey.TryParse(Convert.ToBase64String(rogue.ExportSubjectPublicKeyInfo()), out var rogueKey);
        var person = list.Members[fleet.NodeKey.DeviceId].OwnerId!;
        var forgedList = list with { Members = list.Members.Add(rogue.DeviceId, new FleetDeviceListMember(rogue.DeviceId, FleetDeviceKinds.Controller, rogueKey!, person)) };
        var forged = FleetSecretSealing.Seal("attacker-value", FleetSecretBinding.Of(held), [list.Members[fleet.NodeKey.DeviceId]], forgedList, rogue).Single();
        await ((IFleetSecretStore)fleet.Server.GetService<ICoordinatorStore>()).PutFleetSecretAsync(new FleetSecretRecord("forged", list.OrganizationId!, person, FleetSecretKinds.Secret, "INJECTED",
            FleetSecretScopeKinds.Person, string.Empty, false, 1, rogue.DeviceId, person, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            [new FleetSecretCopyRecord("forged", fleet.NodeKey.DeviceId, 1, forged.Sealed, rogue.DeviceId, DateTimeOffset.UtcNow)],
            new AuditEntry("test.forge", AuditEntry.Success, null, null, null, null, null));
        var injected = (await fleet.HeldAsync(fleet.NodeKey)).Secrets.Single(static secret => secret.Name == "INJECTED");
        Assert.IsFalse(FleetSecretSealing.TryOpen(injected.Copy, FleetSecretBinding.Of(injected), fleet.NodeKey, list, out _, out var reason));
        StringAssert.Contains(reason, "controller device");

        // The genuine copy relabelled by the server: another name, scope, version or kind, or made shell-visible, does not open.
        var genuine = FleetSecretBinding.Of(held);
        Assert.IsTrue(FleetSecretSealing.TryOpen(held.Copy, genuine, fleet.NodeKey, list, out var value, out _));
        Assert.AreEqual(Token, value);
        foreach (var moved in new[]
        {
            genuine with { Name = "OTHER_TOKEN" }, genuine with { SecretId = "other-id" }, genuine with { Version = 2 }, genuine with { HostOnly = false },
            genuine with { ScopeKind = FleetSecretScopeKinds.Space, ScopeId = "space" }, genuine with { Kind = FleetSecretKinds.Variable },
        })
        {
            Assert.IsFalse(FleetSecretSealing.TryOpen(held.Copy, moved, fleet.NodeKey, list, out _, out _), $"relabelled as {moved}");
        }

        // A copy sealed for the node opens for no other device, the server's own key included.
        Assert.IsFalse(FleetSecretSealing.TryOpen(held.Copy, genuine, fleet.OtherKey, list, out _, out _));
        Assert.IsFalse(FleetSecretSealing.TryOpen(set.Copy, genuine, fleet.NodeKey, list, out _, out _), "the desktop's copy is the desktop's");
    }

    [TestMethod]
    public async Task ARevokedMachine_IsRefused_AndTheValuesItHeldAreFlaggedForRotation()
    {
        await using var fleet = await SecretsFixture.StartAsync();
        var set = await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "GRAFANA_TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), Token, false,
            [fleet.NodeKey.DeviceId, fleet.OtherKey.DeviceId], CancellationToken.None);
        Assert.AreEqual(0, set.Flagged.Count);
        await fleet.Desktop.RevokeAsync(fleet.NodeKey.DeviceId);

        // The server refuses the revoked machine; its copies are no longer given.
        using (var node = new FleetServerClient(fleet.Server.BaseAddress, fleet.NodeKey))
        {
            var refused = await Assert.ThrowsExactlyAsync<FleetServerException>(() => node.ListHeldSecretsAsync());
            Assert.IsTrue(refused.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, refused.Status.ToString());
        }

        var flagged = (await fleet.Manager.ListAsync(CancellationToken.None)).Secrets.Single();
        CollectionAssert.AreEqual(new[] { fleet.NodeKey.DeviceId }, flagged.Flagged.ToArray(), "the value it held needs a rotation");
        CollectionAssert.DoesNotContain(flagged.Holders.ToArray(), fleet.NodeKey.DeviceId);

        // The rotation seals for the machines still in the list: the revoked one never gets the new version.
        var rotated = await fleet.Manager.RotateAsync(set.Id, "rotated-value", CancellationToken.None);
        Assert.AreEqual(0, rotated.Flagged.Count);
        CollectionAssert.AreEquivalent(new[] { fleet.DesktopKey.DeviceId, fleet.OtherKey.DeviceId }, rotated.Holders.ToArray());
        Assert.AreEqual("rotated-value", await fleet.OpenHeldAsync(fleet.OtherKey, "GRAFANA_TOKEN"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fleet.Manager.GrantAsync(set.Id, fleet.NodeKey.DeviceId, CancellationToken.None));
    }

    [TestMethod]
    public async Task AnApprovalOfAnEnvironment_CannotBeForgedNorReplayed()
    {
        await using var fleet = await SecretsFixture.StartAsync();
        var list = await fleet.NodeListAsync();
        var person = list.Members[fleet.DesktopKey.DeviceId].OwnerId!;
        var environment = await fleet.Manager.PutEnvironmentAsync("production", [person], ["prod"], [], false, CancellationToken.None);
        Assert.IsTrue(FleetEnvironmentDocuments.TryVerify(environment.Signed, list, fleet.NodeKey.DeviceId, out var definition, out var reason), reason);
        Assert.AreEqual((1L, "production"), (definition.Number, definition.Name));

        // A definition the server changed (a rule dropped) does not verify on the machine.
        FleetEnvironmentDocuments.TryRead(environment.Signed.Payload, out var read, out _);
        var dropped = new FleetSignedEnvironment(FleetEnvironmentDocuments.Serialize(read! with { Approvers = [] }), environment.Signed.Signature);
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerify(dropped, list, fleet.NodeKey.DeviceId, out _, out _));

        // The machine asks the approval of a run; the person's controller device approves it.
        using var node = new FleetServerClient(fleet.Server.BaseAddress, fleet.NodeKey);
        var asked = await node.RequestApprovalAsync(environment.Id, new RequestFleetApprovalRequest("run-1", "nonce-1", definition.Number, null, "Deploy"));
        Assert.AreEqual(FleetApprovalStates.Pending, asked.Status);
        Assert.AreEqual(asked.Id, (await fleet.Manager.ListApprovalsAsync(CancellationToken.None)).Approvals.Single().Id, "it waits for the approver");
        var approved = await fleet.Manager.ApproveAsync(asked.Id, CancellationToken.None);
        Assert.AreEqual(FleetApprovalStates.Approved, approved.Status);
        var given = (await node.GetApprovalAsync(asked.Id)).Approval;
        var now = DateTimeOffset.UtcNow;
        Assert.IsTrue(FleetEnvironmentDocuments.TryVerifyApproval(given, definition, list, fleet.NodeKey.DeviceId, "run-1", "nonce-1", now, out _, out reason), reason);

        // Replayed for another run, or another nonce, or after it expired: refused.
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerifyApproval(given, definition, list, fleet.NodeKey.DeviceId, "run-2", "nonce-1", now, out _, out _));
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerifyApproval(given, definition, list, fleet.NodeKey.DeviceId, "run-1", "nonce-2", now, out _, out _));
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerifyApproval(given, definition, list, fleet.OtherKey.DeviceId, "run-1", "nonce-1", now, out _, out _));
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerifyApproval(given, definition, list, fleet.NodeKey.DeviceId, "run-1", "nonce-1", now.AddHours(2), out _, out _));
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerifyApproval(given, definition with { Number = 2 }, list, fleet.NodeKey.DeviceId, "run-1", "nonce-1", now, out _, out _),
            "an approval is of one version of the rules");

        // Forged by the machine itself, or by a server (a key not of a controller device of an approver): refused.
        FleetEnvironmentDocuments.TryReadApproval(given!.Payload, out var approval, out _);
        var byNode = FleetServerClient.SignApproval(fleet.NodeKey, approval!);
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerifyApproval(byNode, definition, list, fleet.NodeKey.DeviceId, "run-1", "nonce-1", now, out _, out reason));
        StringAssert.Contains(reason, "controller device");
        var tampered = new FleetSignedApproval(FleetEnvironmentDocuments.Serialize(approval! with { RunId = "run-2" }), given.Signature);
        Assert.IsFalse(FleetEnvironmentDocuments.TryVerifyApproval(tampered, definition, list, fleet.NodeKey.DeviceId, "run-2", "nonce-1", now, out _, out _));

        // The server takes an approval once, from an approver's device, for what the machine asked.
        var twice = await fleet.Server.SendAsync(TestDevice.From(fleet.DesktopKey), HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveFleetApprovalRequest(given));
        Assert.AreEqual((HttpStatusCode.Conflict, CoordinatorErrorCodes.ApprovalConflict), (twice.Status, twice.Code));
        var other = await node.RequestApprovalAsync(environment.Id, new RequestFleetApprovalRequest("run-3", "nonce-3", definition.Number, null, null));
        var mismatched = await fleet.Server.SendAsync(TestDevice.From(fleet.DesktopKey), HttpMethod.Post, $"/api/v1/approvals/{other.Id}/approve", new ApproveFleetApprovalRequest(given));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.ApprovalInvalid), (mismatched.Status, mismatched.Code));
        var nodeApproves = await fleet.Server.SendAsync(TestDevice.From(fleet.NodeKey), HttpMethod.Post, $"/api/v1/approvals/{other.Id}/approve", new ApproveFleetApprovalRequest(byNode));
        Assert.AreEqual(HttpStatusCode.Forbidden, nodeApproves.Status);
        CollectionAssert.IsSubsetOf(new[] { "environment.set", "environment.approval.request", "environment.approve" },
            (await fleet.AuditAsync("environment.")).Select(static record => record.Action).Distinct().ToArray());
    }

    [TestMethod]
    public async Task AMachine_TakesItsValuesByScope_TheRotationAtItsNextRefresh_AndNothingOnceRevoked()
    {
        await using var fleet = await SecretsFixture.StartAsync();
        const string Repository = "repo:github.com/example/fleet-fixture";
        // The key of the space: made by the desktop, held by the node; the server knows the space by an opaque id only.
        var spaceKey = (await new FleetMetadataKeyring(fleet.Manager.Devices).RefreshAsync("default", CancellationToken.None))!;
        var nodeDevices = new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey, Path.Combine(fleet.Root.Path, "node-link", FleetOrganizationDevices.PinFileName));
        var nodeKeys = new FleetMetadataKeyring(nodeDevices);
        Assert.AreEqual(spaceKey.Id, (await nodeKeys.RefreshAsync("default", CancellationToken.None))?.Id);
        var space = new FleetSecretScopeDto(FleetSecretScopeKinds.Space, spaceKey.Grant!.SpaceId);
        var project = new FleetSecretScopeDto(FleetSecretScopeKinds.Project, FleetProjectKeys.Of(spaceKey, Repository));
        var staging = await fleet.Manager.PutEnvironmentAsync("staging", [], [], [], false, CancellationToken.None);
        var machines = new[] { fleet.NodeKey.DeviceId };
        var person = await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), "person-value", false, machines, CancellationToken.None);
        await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "TOKEN", space, "space-value", false, machines, CancellationToken.None);
        await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "TOKEN", project, "project-value", false, machines, CancellationToken.None);
        await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Environment, staging.Id), "staging-value", false, machines,
            CancellationToken.None);
        await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Project, FleetProjectKeys.Of(spaceKey, "repo:github.com/example/other")),
            "other-project", false, machines, CancellationToken.None);
        await fleet.Manager.SetAsync(FleetSecretKinds.Variable, "GRAFANA_URL", space, "https://grafana.example", false, machines, CancellationToken.None);

        using var nodeRoot = new TempDirectory();
        var store = new CodeAlta.Catalog.Secrets.LocalSecretStore(nodeRoot.Path, new CodeAlta.Catalog.Secrets.MasterKeySecretProtector(
            new CodeAlta.Catalog.Secrets.FileSecretKeyStore(Path.Combine(nodeRoot.Path, "secrets", "key"))));
        var sync = new FleetSecretSync("link-1", nodeDevices, nodeKeys, store,
            _ => Task.FromResult<IReadOnlyList<FleetSecretProject>>([new FleetSecretProject("local-project", Repository)]));
        var result = await sync.SyncAsync(CancellationToken.None);
        Assert.AreEqual((5, 0, false), (result.Taken, result.Refused.Count, result.Revoked), string.Join("; ", result.Refused));
        Assert.AreEqual("staging", sync.Environments.Single().Key);
        string Resolve(CodeAlta.Catalog.Secrets.SecretScope scope, string text = "${secret:TOKEN}")
            => CodeAlta.Catalog.Secrets.SecretReferenceResolver.ForScope(nodeRoot.Path, scope, shell: true).Resolve(text);
        Assert.AreEqual("staging-value", Resolve(new("default", "local-project", "staging")));
        Assert.AreEqual("project-value", Resolve(new("default", "local-project", null)));
        Assert.AreEqual("space-value", Resolve(new("default", "another-project", null)));
        Assert.AreEqual("person-value", Resolve(new("home", null, null)));
        Assert.AreEqual("https://grafana.example", Resolve(new("default", null, null), "${var:GRAFANA_URL}"));
        Assert.IsFalse(store.ListFleetCopies().Any(static copy => copy.ScopeId is null && copy.ScopeKind == "project"), "a project this machine does not have is not kept");

        // A rotation reaches the machine at its next refresh; the old version is no longer masked nor given.
        await fleet.Manager.RotateAsync(person.Id, "person-rotated", CancellationToken.None);
        await sync.SyncAsync(CancellationToken.None);
        Assert.AreEqual("person-rotated", Resolve(new("home", null, null)));
        Assert.IsFalse(store.GetSecretValues().Contains("person-value"));
        Assert.IsTrue(store.GetSecretValues().Contains("person-rotated"));

        // A copy no controller device of the person gave is refused, and not kept.
        var list = await fleet.NodeListAsync();
        using var rogue = DeviceIdentity.LoadOrCreate(Path.Combine(fleet.Root.Path, "rogue"));
        FleetDevicePublicKey.TryParse(Convert.ToBase64String(rogue.ExportSubjectPublicKeyInfo()), out var rogueKey);
        var forgedList = list with { Members = list.Members.Add(rogue.DeviceId, new FleetDeviceListMember(rogue.DeviceId, FleetDeviceKinds.Controller, rogueKey!, list.Members[fleet.NodeKey.DeviceId].OwnerId)) };
        var binding = new FleetSecretBinding("forged", FleetSecretKinds.Secret, "INJECTED", FleetSecretScopeKinds.Person, null, 1, false);
        var forged = FleetSecretSealing.Seal("attacker", binding, [list.Members[fleet.NodeKey.DeviceId]], forgedList, rogue).Single();
        await ((IFleetSecretStore)fleet.Server.GetService<ICoordinatorStore>()).PutFleetSecretAsync(new FleetSecretRecord("forged", list.OrganizationId!,
            list.Members[fleet.NodeKey.DeviceId].OwnerId!, FleetSecretKinds.Secret, "INJECTED", FleetSecretScopeKinds.Person, string.Empty, false, 1, rogue.DeviceId, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), [new FleetSecretCopyRecord("forged", fleet.NodeKey.DeviceId, 1, forged.Sealed, rogue.DeviceId, DateTimeOffset.UtcNow)],
            new AuditEntry("test.forge", AuditEntry.Success, null, null, null, null, null));
        var withForged = await sync.SyncAsync(CancellationToken.None);
        StringAssert.StartsWith(withForged.Refused.Single(), "INJECTED:");
        Assert.IsFalse(store.ListFleetCopies().Any(static copy => copy.Name == "INJECTED"));

        // Revoked, the machine takes every value of that server away.
        await fleet.Desktop.RevokeAsync(fleet.NodeKey.DeviceId);
        var revoked = await sync.SyncAsync(CancellationToken.None);
        Assert.IsTrue(revoked.Revoked);
        Assert.AreEqual(0, store.ListFleetCopies().Count);
        Assert.IsFalse(CodeAlta.Catalog.Secrets.SecretReferenceResolver.ForMachine(nodeRoot.Path).TryGet(
            new CodeAlta.Catalog.Secrets.SecretReference(CodeAlta.Catalog.Secrets.SecretReferenceKind.Secret, "TOKEN"), out _));
    }

    [TestMethod]
    public async Task AMachine_RefusesARollBackOfItsEnvironmentsAndValues_AlsoAfterARestart()
    {
        await using var fleet = await SecretsFixture.StartAsync();
        var person = (await fleet.NodeListAsync()).Members[fleet.DesktopKey.DeviceId].OwnerId!;
        await fleet.Manager.PutEnvironmentAsync("production", [], [], [], false, CancellationToken.None);
        await fleet.Manager.PutEnvironmentAsync("staging", [], [], [], false, CancellationToken.None);
        var secret = await fleet.Manager.SetAsync(FleetSecretKinds.Secret, "TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), "v1-value", false,
            [fleet.NodeKey.DeviceId], CancellationToken.None);

        using var replay = new ReplayHandler { InnerHandler = new HttpClientHandler() };
        using var nodeRoot = new TempDirectory();
        var store = new CodeAlta.Catalog.Secrets.LocalSecretStore(nodeRoot.Path, new CodeAlta.Catalog.Secrets.MasterKeySecretProtector(
            new CodeAlta.Catalog.Secrets.FileSecretKeyStore(Path.Combine(nodeRoot.Path, "secrets", "key"))));
        FleetSecretSync NewSync() => new("link-1", new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey,
            Path.Combine(fleet.Root.Path, "node-link", FleetOrganizationDevices.PinFileName), replay), null, store, null);
        var sync = NewSync();
        replay.Recording = true;
        await sync.SyncAsync(CancellationToken.None);
        replay.Recording = false;
        Assert.AreEqual(1L, sync.Environments["production"].Number);

        // The rules get stricter, staging is taken away, the value is rotated: the machine takes it all.
        await fleet.Manager.PutEnvironmentAsync("production", [person], [], [], false, CancellationToken.None);
        await fleet.Manager.PutEnvironmentAsync("staging", [], [], [], true, CancellationToken.None);
        await fleet.Manager.RotateAsync(secret.Id, "v2-value", CancellationToken.None);
        await sync.SyncAsync(CancellationToken.None);
        Assert.AreEqual(2L, sync.Environments["production"].Number);
        Assert.IsFalse(sync.Environments.ContainsKey("staging"));

        // The server answers what it held before, correctly signed: the machine keeps none of it, also once restarted.
        replay.Replaying = true;
        foreach (var refreshed in new[] { sync, NewSync() })
        {
            var result = await refreshed.SyncAsync(CancellationToken.None);
            Assert.IsFalse(refreshed.Environments.TryGetValue("production", out var production) && production.Approvers.Count == 0, "the rule of approval is not dropped");
            Assert.IsFalse(refreshed.Environments.ContainsKey("staging"), "an environment taken away does not come back");
            Assert.IsFalse(store.GetSecretValues().Contains("v1-value"), "a superseded value does not come back");
            Assert.IsTrue(result.Refused.Count >= 3, string.Join("; ", result.Refused));
        }
    }

    // Answers the environments and the values of a machine as the server answered them while it recorded.
    private sealed class ReplayHandler : DelegatingHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _recorded = new(StringComparer.Ordinal);

        public bool Recording { get; set; }

        public bool Replaying { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var replayed = request.Method == HttpMethod.Get && (path.EndsWith("/api/v1/environments", StringComparison.Ordinal) || path.EndsWith("/api/v1/secrets/held", StringComparison.Ordinal));
            if (replayed && Replaying && _recorded.TryGetValue(path, out var body))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            var response = await base.SendAsync(request, cancellationToken);
            if (replayed && Recording) _recorded[path] = await response.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    // The files of the data root, the database and its log included, read while the server holds them open.
    private static bool DataRootHolds(ServerFixture server, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        foreach (var file in Directory.EnumerateFiles(server.Server.Paths.DataRoot, "*", SearchOption.AllDirectories))
        {
            // The lock of the data root is held without sharing: it holds no data.
            if (file.EndsWith(".lock", StringComparison.Ordinal)) continue;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (copy.GetBuffer().AsSpan(0, (int)copy.Length).IndexOf(bytes) >= 0) return true;
        }

        return false;
    }

    /// <summary>A server with its owner's desktop, and two machines of the owner.</summary>
    private sealed class SecretsFixture : IAsyncDisposable
    {
        private SecretsFixture(ServerFixture server, TempDirectory root, DeviceIdentity desktopKey, DeviceIdentity nodeKey, DeviceIdentity otherKey, FleetServerClient desktop)
        {
            Server = server;
            Root = root;
            DesktopKey = desktopKey;
            NodeKey = nodeKey;
            OtherKey = otherKey;
            Desktop = desktop;
            Manager = new FleetSecretsManager(new FleetOrganizationDevices(server.BaseAddress, desktopKey, Path.Combine(root.Path, "desktop-link", FleetOrganizationDevices.PinFileName)));
        }

        public ServerFixture Server { get; }

        public TempDirectory Root { get; }

        public DeviceIdentity DesktopKey { get; }

        public DeviceIdentity NodeKey { get; }

        public DeviceIdentity OtherKey { get; }

        public FleetServerClient Desktop { get; }

        public FleetSecretsManager Manager { get; }

        public static async Task<SecretsFixture> StartAsync()
        {
            var server = await ServerFixture.StartAsync();
            var root = new TempDirectory();
            var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
            var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
            var otherKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "other"));
            var desktop = new FleetServerClient(server.BaseAddress, desktopKey);
            await desktop.SetUpAsync(server.SetupCode, "Alex", "Desktop");
            foreach (var (key, name) in new[] { (nodeKey, "Mac mini"), (otherKey, "Build box") })
            {
                using var machine = new FleetServerClient(server.BaseAddress, key);
                await machine.EnrollAsync((await desktop.CreateEnrollmentTokenAsync()).Token, name);
                await desktop.ApproveAsync(key.DeviceId, FleetDeviceKinds.Node);
            }

            return new SecretsFixture(server, root, desktopKey, nodeKey, otherKey, desktop);
        }

        public async Task<FleetDeviceListState> NodeListAsync() => await new FleetOrganizationDevices(Server.BaseAddress, NodeKey).RefreshAsync(CancellationToken.None);

        public async Task<FleetSecretListDto> HeldAsync(DeviceIdentity machine)
        {
            using var client = new FleetServerClient(Server.BaseAddress, machine);
            return await client.ListHeldSecretsAsync();
        }

        public async Task<string?> OpenHeldAsync(DeviceIdentity machine, string name)
        {
            var list = await new FleetOrganizationDevices(Server.BaseAddress, machine).RefreshAsync(CancellationToken.None);
            var held = (await HeldAsync(machine)).Secrets.Single(secret => secret.Name == name);
            return FleetSecretSealing.TryOpen(held.Copy, FleetSecretBinding.Of(held), machine, list, out var value, out var reason) ? value : throw new AssertFailedException(reason);
        }

        public async Task<IReadOnlyList<AuditRecordDto>> AuditAsync(string prefix)
            => [.. (await Server.SendAsync(TestDevice.From(DesktopKey), HttpMethod.Get, "/api/v1/audit")).Read<ListResponse<AuditRecordDto>>().Items
                .Where(record => record.Action.StartsWith(prefix, StringComparison.Ordinal))];

        public async ValueTask DisposeAsync()
        {
            Desktop.Dispose();
            await Server.DisposeAsync();
            DesktopKey.Dispose();
            NodeKey.Dispose();
            OtherKey.Dispose();
            Root.Dispose();
        }
    }
}
