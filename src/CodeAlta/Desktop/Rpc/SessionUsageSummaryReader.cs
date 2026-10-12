using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Desktop.Rpc;

// Full durable-session totals, independent of the loaded timeline window and the Statistics plugin switch.
internal static class SessionUsageSummaryReader
{
    internal static async Task<SessionUsageSummary?> ReadAsync(
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> read,
        string sessionId, CancellationToken token)
    {
        var operations = new List<(long Offset, string? Run, AgentOperationUsageSnapshot Usage)>();
        AgentSessionHistoryCursor? cursor = null;
        // Bound work without ever presenting a partial scan as an entire-session total.
        for (var pages = 0; pages < 1000; pages++)
        {
            token.ThrowIfCancellationRequested();
            var page = await read(sessionId, cursor, token).ConfigureAwait(false);
            if (page.TailOmitted) return null;
            foreach (var entry in page.Entries)
                if (entry.Event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.UsageUpdated, Usage.LastOperation: { } usage } update)
                    operations.Add((entry.Offset, update.RunId?.Value, usage));
            if (page.Next is null) return Sum(operations.OrderBy(static item => item.Offset));
            if (cursor is not null && page.Next.Offset >= cursor.Offset) return null;
            cursor = page.Next;
        }
        return null;
    }

    private static SessionUsageSummary? Sum(IEnumerable<(long Offset, string? Run, AgentOperationUsageSnapshot Usage)> operations)
    {
        long? input = null, output = null, read = null, write = null, reasoning = null;
        double? duration = null;
        var costs = new Dictionary<string, double>(StringComparer.Ordinal);
        var lastTurnCosts = new Dictionary<string, (double Cost, double? Duration, string Unit)>(StringComparer.Ordinal);
        foreach (var (_, run, usage) in operations)
        {
            if (usage.InputTokens is < 0 || usage.OutputTokens is < 0 || usage.CacheReadTokens is < 0
                || usage.CachedInputTokens is < 0 || usage.CacheWriteTokens is < 0 || usage.ReasoningTokens is < 0)
                return null;
            checked
            {
                // Normalize each request before adding: old journals sometimes counted cache beside input.
                var cached = usage.CachedInputTokens ?? usage.CacheReadTokens;
                var cache = (cached ?? 0) + (usage.CacheWriteTokens ?? 0);
                Add(ref input, usage.InputTokens is { } reported ? reported >= cache ? reported : reported + cache : cache > 0 ? cache : null);
                Add(ref read, cached);
                Add(ref write, usage.CacheWriteTokens);
                Add(ref output, usage.OutputTokens);
                Add(ref reasoning, usage.ReasoningTokens);
            }
            if (usage.CostUnit is { } reportedUnit && (reportedUnit.Length > 128 || reportedUnit.Any(char.IsControl))) return null;
            // The same unit normalization as Statistics: unlabelled provider costs are US dollars.
            var unit = string.IsNullOrWhiteSpace(usage.CostUnit) ? "usd"
                : usage.CostUnit.Trim().Equals("AI credits", StringComparison.OrdinalIgnoreCase) ? "AI credits" : usage.CostUnit.Trim();
            // Like Statistics: a provider may repeat a whole-turn cost/duration, but equal request costs are distinct.
            var repeated = run is not null && usage.Cost is { } c && usage.DurationMs is { } d
                && lastTurnCosts.TryGetValue(run, out var last) && last == (c, d, unit);
            if (run is not null && usage.Cost is { } cost)
                lastTurnCosts[run] = (cost, usage.DurationMs, unit);
            if (!repeated)
            {
                if (usage.DurationMs is { } elapsed)
                {
                    if (!double.IsFinite(elapsed) || elapsed < 0) return null;
                    duration = (duration ?? 0) + elapsed;
                    if (!double.IsFinite(duration.Value)) return null;
                }
                if (usage.Cost is { } amount)
                {
                    if (!double.IsFinite(amount) || amount < 0) return null;
                    var sum = costs.GetValueOrDefault(unit) + amount;
                    if (!double.IsFinite(sum)) return null;
                    costs[unit] = sum;
                }
            }
        }
        if (input is null && output is null && read is null && write is null && reasoning is null && duration is null && costs.Count == 0)
            return null;
        return new(new(Text(input), Text(output), null, Text(write), Text(read), Text(reasoning), null,
            duration?.ToString("R", CultureInfo.InvariantCulture)),
            [.. costs.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new SessionUsageCost(pair.Value.ToString("R", CultureInfo.InvariantCulture), pair.Key))]);
    }

    private static string? Text(long? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static void Add(ref long? total, long? value)
    {
        if (value is not null) total = checked((total ?? 0) + value.Value);
    }
}
