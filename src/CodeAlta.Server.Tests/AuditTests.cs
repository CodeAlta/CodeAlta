using System.Net;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The audit log as auditors read it (<c>doc/fleet/security-and-privacy.md</c>, audit log): filtered and paged in both
/// orders, its chain checked with its head, and exported in CSV and JSON Lines, every export audited itself.
/// </summary>
[TestClass]
public sealed class AuditTests
{
    [TestMethod]
    public async Task TheLogFiltersByActionPersonResultTimeAndWords_AndPagesNewestFirst()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        var setup = await server.SetUpAsync(owner);
        var store = server.GetService<ICoordinatorStore>();
        var start = server.Clock.Now;
        for (var index = 0; index < 7; index++)
        {
            server.Clock.Advance(TimeSpan.FromMinutes(1));
            await store.AppendAuditAsync(new AuditEntry(index % 2 == 0 ? "team.create" : "grant.revoke", index == 3 ? FleetErrorCodes.Forbidden : AuditEntry.Success,
                setup.Owner.Id, owner.DeviceId, "t" + index, "10.0.0." + index, "r" + index, index == 5 ? "{\"name\":\"night_build 100%\"}" : null));
        }

        var teams = await PageAsync(server, owner, new FleetAuditQuery { Actions = ["team."] });
        CollectionAssert.AreEqual(new[] { "t0", "t2", "t4", "t6" }, teams.Items.Select(static record => record.Target).ToArray());
        Assert.AreEqual(("Alex", owner.Name), (teams.Items[0].ActorName, teams.Items[0].DeviceName), "a record names the person and the device as the server knows them");

        var failures = await PageAsync(server, owner, new FleetAuditQuery { Result = FleetAuditResults.Failure });
        Assert.AreEqual("t3", failures.Items.Single().Target);
        Assert.AreEqual(0, (await PageAsync(server, owner, new FleetAuditQuery { PersonId = "nobody" })).Items.Count);

        var window = await PageAsync(server, owner, new FleetAuditQuery { From = start.AddMinutes(2), To = start.AddMinutes(4), Actions = ["grant.revoke"] });
        CollectionAssert.AreEqual(new[] { "t1" }, window.Items.Select(static record => record.Target).ToArray(), "from is included, to is not");

        // Words match as they are written: wildcards of LIKE are letters here.
        Assert.AreEqual("t5", (await PageAsync(server, owner, new FleetAuditQuery { Text = "night_build 100%" })).Items.Single().Target);
        Assert.AreEqual(0, (await PageAsync(server, owner, new FleetAuditQuery { Text = "night%build" })).Items.Count);

        var first = await PageAsync(server, owner, new FleetAuditQuery { Newest = true, Limit = 3 });
        CollectionAssert.AreEqual(new[] { "t6", "t5", "t4" }, first.Items.Select(static record => record.Target).ToArray());
        Assert.IsNotNull(first.Next);
        var second = await PageAsync(server, owner, new FleetAuditQuery { Newest = true, Limit = 3, Before = long.Parse(first.Next!) });
        CollectionAssert.AreEqual(new[] { "t3", "t2", "t1" }, second.Items.Select(static record => record.Target).ToArray());

        var refused = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit?from=yesterday");
        Assert.AreEqual((HttpStatusCode.BadRequest, FleetErrorCodes.InvalidParams), (refused.Status, refused.Code));
    }

    [TestMethod]
    public async Task VerificationNamesTheHeadOfTheChain()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        await server.SetUpAsync(owner);
        var verification = (await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit/verify")).Read<AuditVerificationDto>();
        var last = (await PageAsync(server, owner, new FleetAuditQuery { Newest = true, Limit = 1 })).Items.Single();
        Assert.IsTrue(verification.Valid);
        Assert.AreEqual((last.Sequence, last.Hash), (verification.Count, verification.HeadHash));
        Assert.AreEqual(server.Clock.Now, verification.CheckedAt);
    }

    [TestMethod]
    public async Task ExportsAreCsvAndJsonLines_TheWholeLogVerifiesElsewhere_AndEachExportIsAudited()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Home server");
        var (setup, _) = await server.SetUpWithNodeAsync(owner, node);
        var store = server.GetService<ICoordinatorStore>();
        await store.AppendAuditAsync(new AuditEntry("team.create", AuditEntry.Success, setup.Owner.Id, owner.DeviceId, "=HYPERLINK(\"x\")", null, null, "{\"a\":\"b, c\"}"));

        var jsonl = await server.GetTextAsync(owner, "/api/v1/audit/export?format=jsonl");
        Assert.AreEqual(HttpStatusCode.OK, jsonl.Status, jsonl.Text);
        Assert.AreEqual("application/jsonl", jsonl.ContentType);
        StringAssert.EndsWith(jsonl.FileName, ".jsonl");
        var records = jsonl.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => JsonSerializer.Deserialize(line, ServerApiJsonContext.Default.AuditRecordDto)!).ToList();
        var verification = AuditChain.Verify(records.Select(static record => new AuditRecord(record.Sequence, record.RecordedAt,
            new AuditEntry(record.Action, record.Result, record.ActorPersonId, record.ActorDeviceId, record.Target, record.SourceAddress, record.RequestId, record.Details),
            record.PreviousHash, record.Hash)));
        Assert.IsTrue(verification.IsValid, "an export of the whole log in JSON Lines verifies without the server");
        Assert.AreEqual(records[^1].Hash, verification.HeadHash);

        var csv = await server.GetTextAsync(owner, "/api/v1/audit/export?format=csv&action=team.");
        Assert.AreEqual((HttpStatusCode.OK, "text/csv"), (csv.Status, csv.ContentType));
        var lines = csv.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual("sequence,recorded_at,action,result,person_id,person,device_id,device,target,source_address,request_id,details,previous_hash,hash", lines[0]);
        Assert.AreEqual(2, lines.Length, "the filter selects one record");
        StringAssert.Contains(lines[1], ",team.create,success,");
        StringAssert.Contains(lines[1], "\"'=HYPERLINK(\"\"x\"\")\"", "a cell a spreadsheet would read as a formula starts with an apostrophe, and quotes are doubled");
        StringAssert.Contains(lines[1], "\"{\"\"a\"\":\"\"b, c\"\"}\"");

        var exported = (await PageAsync(server, owner, new FleetAuditQuery { Actions = ["audit.export"] })).Items;
        Assert.AreEqual(2, exported.Count, "each export is audited");
        StringAssert.Contains(exported[1].Details, "\"format\":\"csv\"");
        StringAssert.Contains(exported[1].Details, "\"records\":1");
        StringAssert.Contains(exported[1].Details, "action=team.");

        var unknown = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/audit/export?format=xml");
        Assert.AreEqual((HttpStatusCode.BadRequest, FleetErrorCodes.InvalidParams), (unknown.Status, unknown.Code));
    }

    [TestMethod]
    public async Task AnExportThatFailsWhileItWrites_IsAuditedAsAFailure()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Home server");
        await server.SetUpWithNodeAsync(owner, node);
        var store = server.GetService<ICoordinatorStore>();
        var actor = (await store.GetDeviceAsync(owner.DeviceId))!;
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = new NoServices() };
        context.Response.Body = new FailingStream();

        await Assert.ThrowsExactlyAsync<IOException>(() => CodeAlta.Server.Api.AuditEndpoints.ExportAsync(context, store, actor, new FleetAuditQuery { Limit = FleetAuditQuery.MaxPage },
            FleetAuditExportFormats.Csv, TimeProvider.System));
        var exported = (await PageAsync(server, owner, new FleetAuditQuery { Actions = ["audit.export"] })).Items.Single();
        Assert.AreEqual("failure", exported.Result, "an export that did not complete is not recorded as one that did");
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    // A response whose connection breaks: every write fails.
    private sealed class FailingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("The connection broke.");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("The connection broke.");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new IOException("The connection broke.");
    }

    [TestMethod]
    public async Task AnAuditorReadsAndExports_AMemberDoesNeither()
    {
        await using var server = await ServerFixture.StartAsync();
        using var owner = new TestDevice("Alex desktop");
        using var node = new TestDevice("Home server");
        var (setup, _) = await server.SetUpWithNodeAsync(owner, node);
        var (auditor, _) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Ines", FleetOrganizationRoles.Auditor);
        var (member, _) = await AccessControlTests.AddPersonAsync(server, owner, setup, "Bea");

        Assert.AreEqual(HttpStatusCode.OK, (await server.SendAsync(auditor, HttpMethod.Get, "/api/v1/audit?order=desc&limit=5")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await server.GetTextAsync(auditor, "/api/v1/audit/export?format=csv")).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.GetTextAsync(member, "/api/v1/audit/export?format=csv")).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.SendAsync(member, HttpMethod.Get, "/api/v1/audit/verify")).Status);
    }

    [TestMethod]
    public void AQueryRoundTripsThroughItsQueryString()
    {
        var query = new FleetAuditQuery
        {
            Actions = ["team.", "grant.revoke"], PersonId = "p1", DeviceId = "d1", Result = FleetAuditResults.Failure, Target = "a b", SourceAddress = "::1",
            From = DateTimeOffset.FromUnixTimeMilliseconds(1_000), To = DateTimeOffset.FromUnixTimeMilliseconds(2_000), Text = "x&y=z", Before = 9, Newest = true, Limit = 50,
        };
        var text = query.ToQueryString();
        var values = text.TrimStart('?').Split('&').Select(static pair => pair.Split('=', 2)).ToDictionary(static pair => pair[0], static pair => Uri.UnescapeDataString(pair[1]));
        Assert.IsTrue(FleetAuditQuery.TryParse(name => values.GetValueOrDefault(name), out var read, out var problem), problem);
        Assert.AreEqual(text, read!.ToQueryString());
        Assert.IsTrue(FleetAuditQuery.Matches("team.", "team.create") && FleetAuditQuery.Matches("grant*", "grant.revoke") && !FleetAuditQuery.Matches("team", "team.create"));
        Assert.IsFalse(FleetAuditQuery.TryParse(name => name == "order" ? "up" : null, out _, out _));
    }

    private static async Task<ListResponse<AuditRecordDto>> PageAsync(ServerFixture server, TestDevice device, FleetAuditQuery query)
    {
        var answer = await server.SendAsync(device, HttpMethod.Get, "/api/v1/audit" + query.ToQueryString());
        Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
        return answer.Read<ListResponse<AuditRecordDto>>();
    }
}
