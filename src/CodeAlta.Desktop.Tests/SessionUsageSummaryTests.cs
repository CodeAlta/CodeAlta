using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SessionUsageSummaryTests
{
    [TestMethod]
    public void TotalsCoverEveryRunWithoutCountingARepeatedWholeTurnCostTwice()
    {
        var first = new AgentOperationUsageSnapshot(InputTokens: 100, OutputTokens: 20, CacheReadTokens: 50,
            CachedInputTokens: 50, CacheWriteTokens: 10, ReasoningTokens: 5, Cost: .25, CostUnit: "AI credits");
        var old = new AgentOperationUsageSnapshot(InputTokens: 10, OutputTokens: 30, CachedInputTokens: 90,
            CacheWriteTokens: 5, ReasoningTokens: 7, Cost: 1, DurationMs: 100, CostUnit: "USD");
        var result = SessionUsageSummaryReader.Sum([new("first", first), new("first", first), new("second", old), new("second", old)]);
        Assert.AreEqual("410", result!.Operation.InputTokens);
        Assert.AreEqual("280", result.Operation.CachedInputTokens);
        Assert.AreEqual("30", result.Operation.CacheWriteTokens);
        Assert.AreEqual("100", result.Operation.OutputTokens);
        Assert.AreEqual("24", result.Operation.ReasoningTokens);
        Assert.AreEqual("100", result.Operation.DurationMs);
        CollectionAssert.AreEqual(new[] { new SessionUsageCost("0.5", "AI credits"), new SessionUsageCost("1", "USD") }, result.Costs);
    }

    [TestMethod]
    public void EachRequestIsSplitAsTheInputTokenUsageOfTheAgentSplitsIt()
    {
        AgentOperationUsageSnapshot[] operations = [new(InputTokens: 100, CachedInputTokens: 50, CacheWriteTokens: 10), new(InputTokens: 10, CacheReadTokens: 90, CacheWriteTokens: 5),
            new(CachedInputTokens: 7), new(InputTokens: 3), new(InputTokens: 0, CachedInputTokens: 0), new(OutputTokens: 4), new(InputTokens: 60, CachedInputTokens: 40, CacheReadTokens: 1, CacheWriteTokens: 20)];
        var result = SessionUsageSummaryReader.Sum([.. operations.Select(static operation => new AgentSessionUsageUpdate("run", operation))])!.Operation;
        var splits = operations.Select(static operation => AgentInputTokenUsage.From(operation)).ToArray();
        Assert.AreEqual(splits.Sum(static split => split?.Total ?? 0).ToString(), result.InputTokens);
        Assert.AreEqual(splits.Sum(static split => split?.CacheRead ?? 0).ToString(), result.CachedInputTokens);
        Assert.AreEqual(splits.Sum(static split => split?.CacheWrite ?? 0).ToString(), result.CacheWriteTokens);
        Assert.AreEqual(("275", "187", "35"), (result.InputTokens, result.CachedInputTokens, result.CacheWriteTokens));
    }

    [TestMethod]
    public void CostOnlyRequestsResetWholeTurnDeduplicationAndACostKeepsTheUnitItsProviderNamed()
    {
        AgentSessionUsageUpdate[] updates = [
            new("run", new(Cost: 1, DurationMs: 100)),
            new("run", new(Cost: 2)),
            new("run", new(Cost: 1, DurationMs: 100, CostUnit: " ")),
            new("other", new(Cost: .25, CostUnit: "ai credits")),
            new("other", new(Cost: .25, CostUnit: " AI credits ")),
        ];
        var totals = SessionUsageSummaryReader.Sum(updates);
        Assert.AreEqual("200", totals!.Operation.DurationMs);
        // A cost whose provider names no unit has none, as the last operation shows it.
        CollectionAssert.AreEqual(new[] { new SessionUsageCost("0.5", "AI credits"), new SessionUsageCost("4", null) }, totals.Costs);
        // A named unit of the same sum is kept, and the sum has no trace of the additions.
        CollectionAssert.AreEqual(new[] { new SessionUsageCost("0.3", "usd") },
            SessionUsageSummaryReader.Sum([new("run", new(Cost: .1)), new("run", new(Cost: .2, CostUnit: "usd"))])!.Costs);
    }

    [TestMethod]
    public async Task UnknownZeroLargeCountersAndFailedReadsAreNotConfused()
    {
        static SessionUsageSummary? Read(AgentOperationUsageSnapshot operation) => SessionUsageSummaryReader.Sum([new("run", operation)]);
        Assert.IsNull(SessionUsageSummaryReader.Sum([]));
        Assert.IsNull(Read(new()));
        var zero = Read(new(InputTokens: 0, Cost: 0));
        Assert.AreEqual("0", zero!.Operation.InputTokens);
        Assert.IsNull(zero.Operation.OutputTokens);
        Assert.IsNull(zero.Operation.CachedInputTokens);
        Assert.AreEqual("0", zero.Costs.Single().Cost);
        Assert.AreEqual(long.MaxValue.ToString(), Read(new(InputTokens: long.MaxValue))!.Operation.InputTokens);
        Assert.IsNull(Read(new(Cost: double.NaN)));
        Assert.IsNull(Read(new(InputTokens: -1)));
        // A journal that cannot be read whole gives no list, and so no total of a part of the session.
        await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() => SessionUsageSummaryReader.ReadAsync(
            (_, _) => Task.FromException<IReadOnlyList<AgentSessionUsageUpdate>>(new AgentSessionHistoryException("corrupt_record")), "session", default));
        using var cancellation = new CancellationTokenSource();
        var reading = SessionUsageSummaryReader.ReadAsync(async (id, token) =>
        {
            Assert.AreEqual("session", id);
            await Task.Delay(Timeout.Infinite, token);
            return [];
        }, "session", cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading);
        await Assert.ThrowsAsync<OperationCanceledException>(() => SessionUsageSummaryReader.ReadAsync(
            (_, _) => throw new AssertFailedException("No read after cancellation"), "session", new CancellationToken(true)));
    }

    [TestMethod]
    public async Task TotalsAreReadFromTheJournalOfTheSessionThroughTheWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-usage-totals-" + Guid.NewGuid().ToString("N"));
        try
        {
            var created = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var journals = new SessionViewJournalStore(catalog.Options);
            var store = journals.CreateSessionStore();
            await journals.EnsureHeaderAsync(new SessionViewDescriptor
            {
                SessionId = "session", Kind = SessionViewKind.GlobalSession, ProviderId = "fixture", ProviderKey = "fixture",
                WorkingDirectory = root, Title = "Usage", CreatedAt = created, UpdatedAt = created,
            });
            await store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = "session", ProviderId = new("fixture"), ProviderKey = "fixture", WorkingDirectory = root,
                Title = "Usage", CreatedAt = created, UpdatedAt = created,
            });
            AgentSessionUpdateEvent Update(string run, AgentSessionUpdateKind kind, AgentOperationUsageSnapshot operation)
                => new(new("fixture"), "session", created, new(run), kind, null, Usage: new(LastOperation: operation));
            await using var reads = new OwnedSessionWorkspace(catalog, journals);
            Task<SessionUsageSummary?> Totals() => SessionUsageSummaryReader.ReadAsync(reads.ReadUsageUpdatesAsync, "session", default);
            Assert.IsNull(await Totals(), "A session without recorded usage has no totals, not zeros.");
            var request = new AgentOperationUsageSnapshot(InputTokens: 1000, OutputTokens: 200, CachedInputTokens: 600, CacheWriteTokens: 100, ReasoningTokens: 50, Cost: 1.5, DurationMs: 61_000);
            await store.AppendEventsAsync("fixture", "fixture", "session", [
                Update("run-1", AgentSessionUpdateKind.UsageUpdated, request),
                // The end of a run repeats the last usage: it is not another request.
                Update("run-1", AgentSessionUpdateKind.Idle, request),
                new AgentContentCompletedEvent(new("fixture"), "session", created, new("run-1"), AgentContentKind.ToolOutput, "tool:1", null, new string('x', 9 * 1024 * 1024)),
                Update("run-2", AgentSessionUpdateKind.UsageUpdated, request with { Cost = .5, CostUnit = "AI credits", DurationMs = null }),
            ]);
            var totals = await Totals();
            Assert.AreEqual(("2000", "400", "1200", "200", "100", "61000"), (totals!.Operation.InputTokens, totals.Operation.OutputTokens,
                totals.Operation.CachedInputTokens, totals.Operation.CacheWriteTokens, totals.Operation.ReasoningTokens, totals.Operation.DurationMs));
            CollectionAssert.AreEqual(new[] { new SessionUsageCost("0.5", "AI credits"), new SessionUsageCost("1.5", null) }, totals.Costs);
            var missing = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() => SessionUsageSummaryReader.ReadAsync(reads.ReadUsageUpdatesAsync, "other", default));
            Assert.AreEqual("missing_session", missing.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AJournalThatCannotBeReadLeavesTheObservationWithoutTotals()
    {
        const string epoch = "11111111-1111-4111-8111-111111111111";
        var state = new OwnedUsageReadResult("no_observation", Guid.NewGuid(), "session", 1, null, 0);
        var service = new SessionUsageService((_, _, _, _, _) => Task.FromResult(state), epoch,
            (_, _) => Task.FromException<SessionUsageSummary?>(new AgentSessionHistoryException("corrupt_record")));
        var result = await service.ReadAsync(new(epoch, "session", "global", null, null, IncludeCurrentSession: true), default);
        Assert.AreEqual("no_observation", result.Status);
        Assert.IsNull(result.CurrentSession);
    }

    [TestMethod]
    public async Task SavedSessionTotalsDoNotRequireAnActorAndScopeIsVerifiedTwice()
    {
        const string epoch = "11111111-1111-4111-8111-111111111111";
        const string createdAt = "2026-10-12T18:58:50.797343+00:00";
        var runtime = Guid.NewGuid();
        var checks = 0;
        var reads = 0;
        var total = new SessionUsageSummary(new("100", "20", null, null, null, null, null, null), []);
        var service = new SessionUsageService((_, _, _, _, _) => Task.FromResult(new OwnedUsageReadResult("missing_session", runtime, "session", null, null, 0)), epoch,
            (_, _) => { reads++; return Task.FromResult<SessionUsageSummary?>(total); },
            (id, time, scope, project, path, _) =>
            {
                checks++;
                Assert.AreEqual(DateTimeOffset.Parse(createdAt), time);
                Assert.AreEqual("global", scope);
                Assert.IsNull(project);
                Assert.IsNull(path);
                return Task.FromResult(new OwnedRuntimeObservation("ok", new(runtime, id, false, null)));
            });
        var result = await service.ReadAsync(new(epoch, "session", "global", null, null, true, createdAt), default);
        Assert.AreEqual("saved_session", result.Status);
        Assert.AreEqual(total, result.CurrentSession);
        Assert.IsNull(result.Observation);
        Assert.IsNull(result.AttachmentGeneration);
        Assert.AreEqual(2, checks);
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task SavedSessionRefusesUnverifiedScopeOrChangedOwnershipWithoutReleasingTotals()
    {
        const string epoch = "11111111-1111-4111-8111-111111111111";
        var runtime = Guid.NewGuid();
        var reads = 0;
        var checks = 0;
        var request = new SessionUsageRequest(epoch, "session", "global", null, null, true, "2026-10-12T18:58:50Z");
        SessionUsageService Service(Func<OwnedRuntimeObservation> observe) => new(
            (_, _, _, _, _) => Task.FromResult(new OwnedUsageReadResult("missing_session", runtime, "session", null, null, 0)), epoch,
            (_, _) => { reads++; return Task.FromResult<SessionUsageSummary?>(new(new("100", null, null, null, null, null, null, null), [])); },
            (_, _, _, _, _, _) => { checks++; return Task.FromResult(observe()); });
        var service = Service(() => new("scope_mismatch", null));
        Assert.AreEqual("scope_mismatch", (await service.ReadAsync(request, default)).Status);
        Assert.AreEqual(0, reads);
        checks = 0;
        service = Service(() => checks == 1 ? new("ok", new(runtime, "session", false, null)) : new("archived_project", null));
        var changed = await service.ReadAsync(request, default);
        Assert.AreEqual("archived_project", changed.Status);
        Assert.IsNull(changed.CurrentSession);
        Assert.AreEqual(1, reads);
        Assert.AreEqual("invalid_request", (await service.ReadAsync(request with { CreatedAt = "bad" }, default)).Status);
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task SummaryRequiresOwnedAttachmentAndIsRecheckedAfterHistoryRead()
    {
        const string epoch = "11111111-1111-4111-8111-111111111111";
        var runtime = Guid.NewGuid();
        var state = new OwnedUsageReadResult("no_observation", runtime, "session", 1, null, 0);
        var calls = 0;
        var totals = new SessionUsageSummary(new("100", null, null, null, null, null, null, null), []);
        var service = new SessionUsageService((_, _, _, _, _) => Task.FromResult(state), epoch, (_, _) => { calls++; return Task.FromResult<SessionUsageSummary?>(totals); });
        var request = new SessionUsageRequest(epoch, "session", "global", null, null, IncludeCurrentSession: true);
        Assert.IsNull((await service.ReadAsync(request with { IncludeCurrentSession = false }, default)).CurrentSession);
        Assert.AreEqual(0, calls, "The background context meter must not scan history.");
        Assert.AreEqual(totals, (await service.ReadAsync(request, default)).CurrentSession);
        state = state with { Status = "scope_mismatch", AttachmentGeneration = null };
        Assert.IsNull((await service.ReadAsync(request, default)).CurrentSession);
        Assert.AreEqual(1, calls, "No journal read before ownership is established.");
        state = state with { Status = "no_observation", AttachmentGeneration = 1 };
        service = new SessionUsageService((_, _, _, _, _) => Task.FromResult(state), epoch, (_, _) =>
        {
            state = state with { AttachmentGeneration = 2 };
            return Task.FromResult<SessionUsageSummary?>(totals);
        });
        var stale = await service.ReadAsync(request, default);
        Assert.AreEqual("stale_attachment", stale.Status);
        Assert.IsNull(stale.CurrentSession);
    }
}
