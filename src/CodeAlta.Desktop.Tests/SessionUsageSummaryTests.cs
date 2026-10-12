using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SessionUsageSummaryTests
{
    [TestMethod]
    public async Task TotalsCoverEveryPageAndRunWithoutCountingIdleOrCompactionRepeats()
    {
        var first = new AgentOperationUsageSnapshot(InputTokens: 100, OutputTokens: 20, CacheReadTokens: 50,
            CachedInputTokens: 50, CacheWriteTokens: 10, ReasoningTokens: 5, Cost: .25, CostUnit: "AI credits");
        var old = new AgentOperationUsageSnapshot(InputTokens: 10, OutputTokens: 30, CachedInputTokens: 90,
            CacheWriteTokens: 5, ReasoningTokens: 7, Cost: 1, DurationMs: 100, CostUnit: "USD");
        AgentSessionHistoryEntry[] events = [Entry(0, "first", first), Entry(1, "first", first),
            Entry(2, "first", first, AgentSessionUpdateKind.Idle), Entry(3, "second", old), Entry(4, "second", old),
            Entry(5, "second", old, AgentSessionUpdateKind.CompactionCompleted)];
        var calls = 0;
        var result = await SessionUsageSummaryReader.ReadAsync((_, cursor, _) =>
        {
            calls++;
            var end = cursor is null ? events.Length : (int)cursor.Offset;
            var start = Math.Max(0, end - 2);
            return Task.FromResult(new AgentSessionHistoryPage(events[start..end], start == 0 ? null : new("session", 6, 1, start), false));
        }, "session", default);
        Assert.AreEqual(3, calls);
        Assert.AreEqual("410", result!.Operation.InputTokens);
        Assert.AreEqual("280", result.Operation.CachedInputTokens);
        Assert.AreEqual("30", result.Operation.CacheWriteTokens);
        Assert.AreEqual("100", result.Operation.OutputTokens);
        Assert.AreEqual("24", result.Operation.ReasoningTokens);
        Assert.AreEqual("100", result.Operation.DurationMs);
        CollectionAssert.AreEqual(new[] { new SessionUsageCost("0.5", "AI credits"), new SessionUsageCost("1", "USD") }, result.Costs);
    }

    [TestMethod]
    public async Task CostOnlyRequestsResetWholeTurnDeduplicationAndUnitsAreNormalized()
    {
        AgentSessionHistoryEntry[] events = [
            Entry(0, "run", new(Cost: 1, DurationMs: 100)),
            Entry(1, "run", new(Cost: 2)),
            Entry(2, "run", new(Cost: 1, DurationMs: 100, CostUnit: " ")),
            Entry(3, "other", new(Cost: .25, CostUnit: "ai credits")),
            Entry(4, "other", new(Cost: .25, CostUnit: " AI credits ")),
        ];
        var totals = await SessionUsageSummaryReader.ReadAsync((_, _, _) => Task.FromResult(new AgentSessionHistoryPage(events, null, false)), "session", default);
        Assert.AreEqual("200", totals!.Operation.DurationMs);
        CollectionAssert.AreEqual(new[] { new SessionUsageCost("0.5", "AI credits"), new SessionUsageCost("4", "usd") }, totals.Costs);
    }

    [TestMethod]
    public async Task UnknownZeroLargeCountersAndIncompleteReadsAreNotConfused()
    {
        async Task<SessionUsageSummary?> Read(AgentOperationUsageSnapshot operation, bool omitted = false) =>
            await SessionUsageSummaryReader.ReadAsync((_, _, _) => Task.FromResult(new AgentSessionHistoryPage([Entry(0, "run", operation)], null, omitted)), "session", default);
        Assert.IsNull(await Read(new()));
        var zero = await Read(new(InputTokens: 0, Cost: 0));
        Assert.AreEqual("0", zero!.Operation.InputTokens);
        Assert.IsNull(zero.Operation.OutputTokens);
        Assert.AreEqual("0", zero.Costs.Single().Cost);
        Assert.AreEqual(long.MaxValue.ToString(), (await Read(new(InputTokens: long.MaxValue)))!.Operation.InputTokens);
        Assert.IsNull(await Read(new(InputTokens: 1), omitted: true));
        Assert.IsNull(await Read(new(Cost: double.NaN)));
        await Assert.ThrowsAsync<OperationCanceledException>(() => SessionUsageSummaryReader.ReadAsync(
            (_, _, _) => throw new AssertFailedException("No read after cancellation"), "session", new CancellationToken(true)));
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

    private static AgentSessionHistoryEntry Entry(long offset, string run, AgentOperationUsageSnapshot operation,
        AgentSessionUpdateKind kind = AgentSessionUpdateKind.UsageUpdated) => new(offset,
            new AgentSessionUpdateEvent(new("provider"), "session", DateTimeOffset.UnixEpoch.AddSeconds(offset), new(run), kind, null,
                Usage: new(LastOperation: operation)));
}
