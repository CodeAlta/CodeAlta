using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Desktop.Rpc;

// Full durable-session totals, independent of the loaded timeline window and the Statistics plugin switch.
internal static class SessionUsageSummaryReader
{
    // The read lists every usage record of the journal or fails: a sum is never one of a part of the session.
    internal static async Task<SessionUsageSummary?> ReadAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<AgentSessionUsageUpdate>>> read, string sessionId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Sum(await read(sessionId, token).ConfigureAwait(false));
    }

    internal static SessionUsageSummary? Sum(IReadOnlyList<AgentSessionUsageUpdate> operations)
    {
        long? input = null, output = null, read = null, write = null, reasoning = null;
        double? duration = null;
        // A sum per unit, with the unit as a provider named it: null while no record of the sum names one.
        var costs = new Dictionary<string, (double Sum, string? Unit)>(StringComparer.Ordinal);
        var lastTurnCosts = new Dictionary<string, (double Cost, double? Duration, string Unit)>(StringComparer.Ordinal);
        foreach (var (run, usage) in operations)
        {
            if (usage.InputTokens is < 0 || usage.OutputTokens is < 0 || usage.CacheReadTokens is < 0
                || usage.CachedInputTokens is < 0 || usage.CacheWriteTokens is < 0 || usage.ReasoningTokens is < 0)
                return null;
            checked
            {
                // Each request is normalized before it is added: old journals sometimes counted the cache beside the input.
                var split = AgentInputTokenUsage.From(usage);
                if (split is { Total: < 0 }) return null;
                Add(ref input, split?.Total);
                // A part that was not reported stays unknown, apart from a reported zero.
                Add(ref read, usage.CachedInputTokens ?? usage.CacheReadTokens);
                Add(ref write, usage.CacheWriteTokens);
                Add(ref output, usage.OutputTokens);
                Add(ref reasoning, usage.ReasoningTokens);
            }
            if (usage.CostUnit is { } reportedUnit && (reportedUnit.Length > 128 || reportedUnit.Any(char.IsControl))) return null;
            // The same unit normalization as Statistics: unlabelled provider costs are US dollars.
            var named = string.IsNullOrWhiteSpace(usage.CostUnit) ? null
                : usage.CostUnit.Trim().Equals("AI credits", StringComparison.OrdinalIgnoreCase) ? "AI credits" : usage.CostUnit.Trim();
            var unit = named ?? "usd";
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
                    var known = costs.GetValueOrDefault(unit);
                    var sum = known.Sum + amount;
                    if (!double.IsFinite(sum)) return null;
                    costs[unit] = (sum, known.Unit ?? named);
                }
            }
        }
        if (input is null && output is null && read is null && write is null && reasoning is null && duration is null && costs.Count == 0)
            return null;
        return new(new(Text(input), Text(output), null, Text(write), Text(read), Text(reasoning), null,
            duration?.ToString("R", CultureInfo.InvariantCulture)),
            // Ten decimals drop what the additions of doubles leave behind, such as 0.30000000000000004.
            [.. costs.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new SessionUsageCost(Math.Round(pair.Value.Sum, 10).ToString("R", CultureInfo.InvariantCulture), pair.Value.Unit))]);
    }

    private static string? Text(long? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static void Add(ref long? total, long? value)
    {
        if (value is not null) total = checked((total ?? 0) + value.Value);
    }
}
