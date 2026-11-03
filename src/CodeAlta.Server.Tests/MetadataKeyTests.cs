using System.Net;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Api;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The metadata keys at the server: opaque wrapped copies, made and given by controller devices of the person, each
/// device reading its own copy only; the server never sees a key in clear.
/// </summary>
[TestClass]
public sealed class MetadataKeyTests
{
    [TestMethod]
    public async Task AControllerRecordsTheKeyOfItsPersonWrappedToItsDevices()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        using var pc = new TestDevice("PC");
        await server.SetUpWithNodeAsync(owner, mac);
        var pending = await server.EnrollAsync(owner, pc);

        // A node never makes the key, and a key goes only to approved devices of the person, the maker included.
        var byNode = await server.SendAsync(mac, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("k1", 1, [new(mac.DeviceId, "wrapped-mac")]));
        Assert.AreEqual((HttpStatusCode.Forbidden, FleetErrorCodes.Forbidden), (byNode.Status, byNode.Code));
        var toPending = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys",
            new CreateMetadataKeyRequest("k1", 1, [new(owner.DeviceId, "wrapped-owner"), new(pending.Id, "wrapped-pc")]));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.MetadataKeyInvalid), (toPending.Status, toPending.Code));
        var withoutMaker = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("k1", 1, [new(mac.DeviceId, "wrapped-mac")]));
        Assert.AreEqual(CoordinatorErrorCodes.MetadataKeyInvalid, withoutMaker.Code);

        var created = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("k1", 1, [new(owner.DeviceId, "wrapped-owner")]));
        Assert.AreEqual(HttpStatusCode.Created, created.Status, created.Body.ToString());
        Assert.AreEqual(("k1", "wrapped-owner"), (created.Read<MetadataKeyDto>().Id, created.Read<MetadataKeyDto>().Wrapped));
        var again = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("k2", 1, [new(owner.DeviceId, "wrapped-owner")]));
        Assert.AreEqual((HttpStatusCode.Conflict, CoordinatorErrorCodes.MetadataKeyExists), (again.Status, again.Code));

        // The node has no copy yet; the controller that holds the key gives it one, once.
        var nodeView = (await server.SendAsync(mac, HttpMethod.Get, "/api/v1/metadata-keys")).Read<ListResponse<MetadataKeyDto>>().Items.Single();
        Assert.IsNull(nodeView.Wrapped);
        var given = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys/k1/wraps", new AddMetadataKeyWrapsRequest([new(mac.DeviceId, "wrapped-mac")]));
        Assert.AreEqual(HttpStatusCode.OK, given.Status, given.Body.ToString());
        CollectionAssert.AreEquivalent(new[] { owner.DeviceId, mac.DeviceId }, given.Read<MetadataKeyDto>().WrappedTo.ToArray());
        var replaced = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys/k1/wraps", new AddMetadataKeyWrapsRequest([new(mac.DeviceId, "other")]));
        Assert.AreEqual(HttpStatusCode.OK, replaced.Status);
        Assert.AreEqual("wrapped-mac", (await server.SendAsync(mac, HttpMethod.Get, "/api/v1/metadata-keys")).Read<ListResponse<MetadataKeyDto>>().Items.Single().Wrapped,
            "a device keeps the copy it has, and reads its own only");
        Assert.AreEqual("wrapped-owner", (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/metadata-keys")).Read<ListResponse<MetadataKeyDto>>().Items.Single().Wrapped);
        var unknown = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys/nope/wraps", new AddMetadataKeyWrapsRequest([new(mac.DeviceId, "x")]));
        Assert.AreEqual((HttpStatusCode.NotFound, CoordinatorErrorCodes.MetadataKeyNotFound), (unknown.Status, unknown.Code));

        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit")).Read<ListResponse<AuditRecordDto>>().Items
            .Where(static record => record.Action.StartsWith("metadata_key.", StringComparison.Ordinal)).Select(static record => (record.Action, record.Result, record.Details)).ToArray();
        CollectionAssert.AreEqual(new[]
        {
            ("metadata_key.create", FleetErrorCodes.Forbidden, (string?)null),
            ("metadata_key.create", CoordinatorErrorCodes.MetadataKeyInvalid, null),
            ("metadata_key.create", CoordinatorErrorCodes.MetadataKeyInvalid, null),
            ("metadata_key.create", "success", "{\"epoch\":1,\"devices\":1}"),
            ("metadata_key.create", CoordinatorErrorCodes.MetadataKeyExists, null),
            ("metadata_key.wrap", "success", $"{{\"devices\":[\"{mac.DeviceId}\"]}}"),
            ("metadata_key.wrap", CoordinatorErrorCodes.MetadataKeyNotFound, null),
        }, audit, "a wrap that adds nothing is not recorded as a change; no key material is in the log");
    }

    [TestMethod]
    public async Task EachSpaceHasItsOwnEpochsAndItsSealedDescriptor()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Desktop");
        using var mac = new TestDevice("Mac mini");
        await server.SetUpWithNodeAsync(owner, mac);

        // A key of a space carries its sealed descriptor; a key of the person (older devices) needs none.
        var bare = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("k-work", 1, [new(owner.DeviceId, "w")], "space-work"));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.MetadataKeyInvalid), (bare.Status, bare.Code));
        var badId = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("k-work", 1, [new(owner.DeviceId, "w")], "a/b", "sealed"));
        Assert.AreEqual(CoordinatorErrorCodes.MetadataKeyInvalid, badId.Code);
        Assert.AreEqual(HttpStatusCode.Created,
            (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("k-person", 1, [new(owner.DeviceId, "p")]))).Status);
        Assert.AreEqual(HttpStatusCode.Created,
            (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("k-work", 1, [new(owner.DeviceId, "w")], "space-work", "sealed-work"))).Status);
        Assert.AreEqual(HttpStatusCode.Created,
            (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("k-home", 1, [new(owner.DeviceId, "h")], "space-home", "sealed-home"))).Status,
            "epoch 1 of another space");
        var twice = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("k-work-b", 1, [new(owner.DeviceId, "w")], "space-work", "sealed-work"));
        Assert.AreEqual((HttpStatusCode.Conflict, CoordinatorErrorCodes.MetadataKeyExists), (twice.Status, twice.Code));
        Assert.AreEqual(HttpStatusCode.Created,
            (await server.SendAsync(owner, HttpMethod.Post, "/api/v1/metadata-keys", new CreateMetadataKeyRequest("k-work-2", 2, [new(owner.DeviceId, "w2")], "space-work", "sealed-work-2"))).Status);

        var listed = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/metadata-keys")).Read<ListResponse<MetadataKeyDto>>().Items;
        CollectionAssert.AreEquivalent(new[] { ("k-person", 1, (string?)null, (string?)null), ("k-work", 1, "space-work", "sealed-work"), ("k-work-2", 2, "space-work", "sealed-work-2"),
            ("k-home", 1, "space-home", "sealed-home") }, listed.Select(static key => (key.Id, key.Epoch, key.Space, key.SealedDescriptor)).ToArray());
    }
}
