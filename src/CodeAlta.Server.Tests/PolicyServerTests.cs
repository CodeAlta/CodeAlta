using System.Net;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Services;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The signed policy at the server (<c>doc/fleet/security-and-privacy.md</c>, policies and the kill switch): the server keeps a
/// version only when the owner's controller device signed it and the chain verifies; a member, a machine or a forged version
/// is refused and audited; the kill switch set and lifted are audited as such; every device reads the chain.
/// </summary>
[TestClass]
public sealed class PolicyServerTests
{
    [TestMethod]
    public async Task TheOwnerSignsTheStopAndLiftsIt_OthersAndForgeriesAreRefused_AndEachIsAudited()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Build box");
        var (setup, _) = await server.SetUpWithNodeAsync(owner, node);
        var (bea, _) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Bea");
        var list = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/device-list")).Read<ListResponse<DeviceListEntryDto>>().Items[^1].Sequence;
        FleetPolicyDocument Policy(long number, string? previous, FleetPolicyStop? stop) => new(FleetPolicyDocument.CurrentVersion, setup.Organization.Id, number, previous, list, 0,
            new FleetPolicyRules(Providers: ["anthropic"], Sandbox: "workspace"), stop, string.Empty, server.Clock.Now);

        var first = owner.SignPolicy(Policy(1, null, new FleetPolicyStop(FleetPolicyStopScopes.Fleet, [], [], "A leaked key")));
        var stored = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/policies", new AppendPolicyDocumentRequest(first));
        Assert.AreEqual(HttpStatusCode.Created, stored.Status, stored.Body.ToString());
        Assert.AreEqual((1L, FleetPolicyDocuments.Hash(first)), (stored.Read<PolicyDocumentDto>().Number, stored.Read<PolicyDocumentDto>().Hash));

        // Every device of the organization reads the chain, a machine too: it verifies it itself.
        var read = (await server.SendAsync(node, HttpMethod.Get, "/api/v1/policies?after=0")).Read<PolicyDocumentListDto>();
        Assert.AreEqual(first.Payload, read.Items.Single().Document.Payload);
        Assert.AreEqual(0, (await server.SendAsync(bea, HttpMethod.Get, "/api/v1/policies?after=1")).Read<PolicyDocumentListDto>().Items.Count);

        var byMember = await server.SendAsync(bea, HttpMethod.Post, "/api/v1/policies", new AppendPolicyDocumentRequest(bea.SignPolicy(Policy(2, FleetPolicyDocuments.Hash(first), null))));
        Assert.AreEqual(HttpStatusCode.Forbidden, byMember.Status, "a member signs no policy");
        var byMachine = await server.SendAsync(node, HttpMethod.Post, "/api/v1/policies", new AppendPolicyDocumentRequest(node.SignPolicy(Policy(2, FleetPolicyDocuments.Hash(first), null))));
        Assert.AreEqual(HttpStatusCode.Forbidden, byMachine.Status, "a machine signs no policy");
        var signedByAnother = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/policies", new AppendPolicyDocumentRequest(bea.SignPolicy(Policy(2, FleetPolicyDocuments.Hash(first), null))));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.PolicyInvalid), (signedByAnother.Status, signedByAnother.Code), "a version is sent by the device that signed it");
        var late = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/policies", new AppendPolicyDocumentRequest(owner.SignPolicy(Policy(3, FleetPolicyDocuments.Hash(first), null))));
        Assert.AreEqual((HttpStatusCode.Conflict, CoordinatorErrorCodes.PolicyConflict), (late.Status, late.Code));
        var unchained = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/policies", new AppendPolicyDocumentRequest(owner.SignPolicy(Policy(2, "elsewhere", null))));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.PolicyInvalid), (unchained.Status, unchained.Code));

        var lifted = await server.SendAsync(owner, HttpMethod.Post, "/api/v1/policies", new AppendPolicyDocumentRequest(owner.SignPolicy(Policy(2, FleetPolicyDocuments.Hash(first), null))));
        Assert.AreEqual(HttpStatusCode.Created, lifted.Status, lifted.Body.ToString());

        var audit = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit" + new FleetAuditQuery { Actions = ["policy."] }.ToQueryString()))
            .Read<ListResponse<AuditRecordDto>>().Items.Select(static record => (record.Action, record.Result)).ToList();
        CollectionAssert.AreEqual(new[]
        {
            ("policy.stop", "success"), ("policy.sign", FleetErrorCodes.Forbidden), ("policy.sign", FleetErrorCodes.Forbidden), ("policy.sign", CoordinatorErrorCodes.PolicyInvalid),
            ("policy.sign", CoordinatorErrorCodes.PolicyConflict), ("policy.sign", CoordinatorErrorCodes.PolicyInvalid), ("policy.lift", "success"),
        }, audit);
    }
}
