using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary>
/// `alta fleet audit` (<c>doc/fleet/security-and-privacy.md</c>, audit log): records are listed newest first with their filters
/// and a cursor, the chain is checked, a person who may not read the log is refused, and nothing writes a file.
/// </summary>
public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public async Task FleetAudit_ListsFilteredRecordsWithACursor_AndVerifiesTheChain()
    {
        var audit = new FakeFleetAudit();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add<IAltaFleet>(new FakeFleet()).Add<IAltaFleetAudit>(audit));

        var list = await dispatcher.InvokeAsync(["fleet", "audit", "list", "--action", "team.,grant.revoke", "--result", "failure", "--since", "2026-11-01", "--limit", "2"],
            caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, list.ExitCode, list.Transcript);
        CollectionAssert.AreEqual(new[] { 12L, 11L }, Records(list, "alta.fleet.audit.record").Select(static line => line.GetProperty("sequence").GetInt64()).ToArray());
        var summary = Records(list, "alta.fleet.audit.summary").Single();
        Assert.AreEqual(11, summary.GetProperty("before").GetInt64());
        StringAssert.Contains(summary.GetProperty("note").GetString(), "--before 11");
        var asked = audit.Asked.Single();
        CollectionAssert.AreEqual(new[] { "team.", "grant.revoke" }, asked.Filters.Actions!.ToArray());
        Assert.AreEqual(("failure", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), 2), (asked.Filters.Result, asked.Filters.From, asked.Limit));

        var next = await dispatcher.InvokeAsync(["fleet", "audit", "list", "--before", "11"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, next.ExitCode, next.Transcript);
        Assert.AreEqual(11L, audit.Asked[^1].Before);

        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["fleet", "audit", "list", "--since", "yesterday"], caller: FleetAgent)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["fleet", "audit", "list", "--limit", "9999"], caller: FleetAgent)).ExitCode);

        var verify = await dispatcher.InvokeAsync(["fleet", "audit", "verify"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, verify.ExitCode, verify.Transcript);
        var verification = Records(verify, "alta.fleet.audit.verification").Single();
        Assert.AreEqual((true, 12L, "h12"), (verification.GetProperty("valid").GetBoolean(), verification.GetProperty("records").GetInt64(), verification.GetProperty("head").GetString()));

        audit.Forbidden = true;
        Assert.AreEqual(AltaExitCodes.PolicyDenied, (await dispatcher.InvokeAsync(["fleet", "audit", "list"], caller: FleetAgent)).ExitCode, "a member reads no audit log");
        Assert.AreNotEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["fleet", "audit", "export"], caller: FleetAgent)).ExitCode, "an agent writes no export");
        Assert.IsTrue(AltaSandboxGate.Allows("alta fleet audit list") && AltaSandboxGate.Allows("alta fleet audit verify"), "the log is read from a sandbox as access is");
    }

    private sealed class FakeFleetAudit : IAltaFleetAudit
    {
        public bool Forbidden { get; set; }

        public List<(AltaFleetAuditFilters Filters, long? Before, int Limit)> Asked { get; } = [];

        public Task<AltaFleetAuditPage> ReadAuditAsync(string? server, AltaFleetAuditFilters filters, long? before, int limit, CancellationToken cancellationToken)
        {
            if (Forbidden) return Task.FromResult(new AltaFleetAuditPage("forbidden", null));
            Asked.Add((filters, before, limit));
            var time = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
            return Task.FromResult(new AltaFleetAuditPage("ok", null)
            {
                Server = "https://fleet.test",
                Records = [new AltaFleetAuditRecord(12, time, "team.create", "forbidden", "Mia", "Mia laptop", "t1", "10.0.0.2", null),
                    new AltaFleetAuditRecord(11, time.AddMinutes(-1), "grant.revoke", "forbidden", "Mia", "Mia laptop", "g1", "10.0.0.2", null)],
                Before = 11,
            });
        }

        public Task<AltaFleetAuditCheck> VerifyAuditAsync(string? server, CancellationToken cancellationToken)
            => Task.FromResult(Forbidden ? new AltaFleetAuditCheck("forbidden", null, false, 0, null, null) : new AltaFleetAuditCheck("ok", null, true, 12, null, "h12"));
    }
}
