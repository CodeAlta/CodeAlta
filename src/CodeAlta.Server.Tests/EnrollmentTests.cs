using System.Net;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Api;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

[TestClass]
public sealed class EnrollmentTests
{
    [TestMethod]
    public async Task AnEnrollmentTokenRegistersOneMachineAsPending()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        using var pc = new TestDevice("PC");
        var setup = await server.SetUpAsync(owner);
        var token = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/enrollment-tokens", new EnrollmentTokenRequest(600, "mac"))).Read<EnrollmentTokenResponse>();

        var enrolled = await server.SendAsync(mac, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(token.Token, mac.PublicKey, mac.Name));
        Assert.AreEqual(HttpStatusCode.Created, enrolled.Status, enrolled.Body.ToString());
        var device = enrolled.Read<EnrollResponse>().Device;
        Assert.AreEqual((mac.DeviceId, FleetDeviceKinds.Node, DeviceStatuses.Pending, owner.DeviceId, setup.Owner.Id),
            (device.Id, device.Kind, device.Status, device.EnrolledBy, device.OwnerId));

        // Single use: a second machine with the same token is refused.
        var reused = await server.SendAsync(pc, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(token.Token, pc.PublicKey, pc.Name));
        Assert.AreEqual((HttpStatusCode.Unauthorized, CoordinatorErrorCodes.EnrollmentTokenUsed), (reused.Status, reused.Code));
        var unknown = await server.SendAsync(pc, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest("aet_nothing", pc.PublicKey, pc.Name));
        Assert.AreEqual(CoordinatorErrorCodes.EnrollmentTokenInvalid, unknown.Code);

        // A pending device cannot call the API yet.
        var early = await server.SendAsync(mac, HttpMethod.Get, "/api/v1/machines");
        Assert.AreEqual((HttpStatusCode.Forbidden, FleetErrorCodes.PendingApproval), (early.Status, early.Code));

        // An enrollment is signed by the key it registers.
        var second = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/enrollment-tokens", new EnrollmentTokenRequest())).Read<EnrollmentTokenResponse>();
        var notItsKey = await server.SendAsync(mac, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(second.Token, pc.PublicKey, pc.Name));
        Assert.AreEqual(CoordinatorErrorCodes.SignatureInvalid, notItsKey.Code);
        Assert.AreEqual(HttpStatusCode.Created, (await server.SendAsync(pc, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(second.Token, pc.PublicKey, pc.Name))).Status,
            "The refusal did not use the token.");
    }

    [TestMethod]
    public async Task AnEnrollmentTokenExpires()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        await server.SetUpAsync(owner);
        var tooShort = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/enrollment-tokens", new EnrollmentTokenRequest(60));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.InvalidBody), (tooShort.Status, tooShort.Code));
        var token = (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/enrollment-tokens", new EnrollmentTokenRequest(600))).Read<EnrollmentTokenResponse>();
        server.Clock.Advance(TimeSpan.FromMinutes(11));
        var expired = await server.SendAsync(mac, HttpMethod.Post, "/api/v1/enroll", new EnrollRequest(token.Token, mac.PublicKey, mac.Name));
        Assert.AreEqual(CoordinatorErrorCodes.EnrollmentTokenExpired, expired.Code);
    }

    [TestMethod]
    public async Task AControllerApprovesANodeWithASignedEntryAndTheChainVerifies()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        var (setup, node) = await server.SetUpWithNodeAsync(owner, mac);
        Assert.AreEqual((DeviceStatuses.Approved, setup.Owner.Id), (node.Status, node.OwnerId));
        Assert.IsNotNull(node.ApprovedAt);

        // The approved node calls the API, but it signs no one: a node is an execution device.
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(mac, HttpMethod.Get, "/api/v1/machines")).Status);
        var nodeToken = await server.SendAsync(mac, HttpMethod.Post, "/api/v1/enrollment-tokens", new EnrollmentTokenRequest());
        Assert.AreEqual((HttpStatusCode.Forbidden, FleetErrorCodes.Forbidden), (nodeToken.Status, nodeToken.Code));

        var entries = await server.Server.Store.ListDeviceListAsync(setup.Organization.Id);
        var verification = DeviceListChain.Verify(entries);
        Assert.IsTrue(verification.IsValid);
        Assert.AreEqual(2, verification.Count);
        Assert.AreEqual(FleetDeviceKinds.Node, verification.Members[mac.DeviceId]);
        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit")).Read<ListResponse<AuditRecordDto>>().Items;
        CollectionAssert.AreEqual(new[] { "server.setup", "enrollment_token.create", "device.enroll", "device.approve", "enrollment_token.create" },
            audit.Select(static record => record.Action).ToArray());
        Assert.AreEqual((mac.DeviceId, FleetErrorCodes.Forbidden), (audit[^1].ActorDeviceId, audit[^1].Result), "The refusal of the node is audited.");
        Assert.AreEqual((owner.DeviceId, setup.Owner.Id, mac.DeviceId), (audit[3].ActorDeviceId, audit[3].ActorPersonId, audit[3].Target));
    }

    [TestMethod]
    public async Task AnApprovalWithABadEntryIsRefusedAndAudited()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        using var stranger = new TestDevice("Stranger");
        var setup = await server.SetUpAsync(owner);
        var pending = await server.EnrollAsync(owner, mac);
        var good = await server.NextEntryAsync(owner, FleetDeviceListOperations.Add, pending.Id, mac.PublicKey, FleetDeviceKinds.Node, setup.Owner.Id, setup.Organization.Id);

        // Signed by a key that is not the caller's.
        var payload = FleetDeviceListEntries.TryParsePayload(good.Payload, out var parsed) ? parsed! : throw new InvalidOperationException();
        var forged = new FleetSignedDeviceListEntry(good.Payload, stranger.SignEntry(payload).Signature);
        var badSignature = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(forged));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.EntrySignatureInvalid), (badSignature.Status, badSignature.Code));

        // Signed right, but saying something else than the server holds, or behind the list.
        var otherKey = owner.SignEntry(payload with { PublicKey = stranger.PublicKey });
        Assert.AreEqual(CoordinatorErrorCodes.EntryInvalid,
            (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(otherKey))).Code);
        var asController = owner.SignEntry(payload with { Kind = FleetDeviceKinds.Controller });
        Assert.AreEqual(CoordinatorErrorCodes.EntryInvalid,
            (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(asController))).Code);
        var behind = owner.SignEntry(payload with { Sequence = 1 });
        var conflict = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(behind));
        Assert.AreEqual((HttpStatusCode.Conflict, CoordinatorErrorCodes.EntryConflict), (conflict.Status, conflict.Code));
        var missing = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(null));
        Assert.AreEqual(CoordinatorErrorCodes.EntryInvalid, missing.Code);

        Assert.AreEqual(DeviceStatuses.Pending, (await server.SendAsync(owner, HttpMethod.Get, $"/api/v1/devices/{pending.Id}")).Read<DeviceDto>().Status);
        var refusals = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit")).Read<ListResponse<AuditRecordDto>>().Items
            .Where(static record => record.Action == "device.approve").Select(static record => record.Result).ToArray();
        CollectionAssert.AreEqual(new[] { CoordinatorErrorCodes.EntrySignatureInvalid, CoordinatorErrorCodes.EntryInvalid, CoordinatorErrorCodes.EntryInvalid,
            CoordinatorErrorCodes.EntryConflict, CoordinatorErrorCodes.EntryInvalid }, refusals);

        // The good entry still works: nothing was appended by the refusals.
        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(good))).Status);
    }

    [TestMethod]
    public async Task RevocationRemovesTheDeviceWithASignedEntry()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        using var pc = new TestDevice("PC");
        var (setup, node) = await server.SetUpWithNodeAsync(owner, mac);

        var unsigned = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{node.Id}/revoke", new DeviceListChangeRequest(null));
        Assert.AreEqual(CoordinatorErrorCodes.EntryInvalid, unsigned.Code);
        var remove = await server.NextEntryAsync(owner, FleetDeviceListOperations.Remove, node.Id, null, null, null, setup.Organization.Id);
        var revoked = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{node.Id}/revoke", new DeviceListChangeRequest(remove));
        Assert.AreEqual(HttpStatusCode.OK, revoked.Status, revoked.Body.ToString());
        Assert.AreEqual(DeviceStatuses.Revoked, revoked.Read<DeviceDto>().Status);
        Assert.AreEqual(FleetErrorCodes.Revoked, (await server.SendAsync(mac, HttpMethod.Get, "/api/v1/machines")).Code);
        var verification = DeviceListChain.Verify(await server.Server.Store.ListDeviceListAsync(setup.Organization.Id));
        Assert.IsTrue(verification.IsValid);
        Assert.IsFalse(verification.Members.ContainsKey(mac.DeviceId));

        // A pending device is refused without an entry; the last controller cannot be revoked.
        var pending = await server.EnrollAsync(owner, pc);
        Assert.AreEqual(DeviceStatuses.Revoked,
            (await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/revoke", new DeviceListChangeRequest(null))).Read<DeviceDto>().Status);
        var removeSelf = await server.NextEntryAsync(owner, FleetDeviceListOperations.Remove, owner.DeviceId, null, null, null, setup.Organization.Id);
        var last = await server.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{owner.DeviceId}/revoke", new DeviceListChangeRequest(removeSelf));
        Assert.AreEqual((HttpStatusCode.Conflict, CoordinatorErrorCodes.LastController), (last.Status, last.Code));
        Assert.IsTrue((await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit/verify")).Read<AuditVerificationDto>().Valid);
    }
}
