using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Point-in-time actor-owned observation of the latest admitted typed usage event for one attachment. Not a complete or live context, cumulative total, or history recovery.</summary>
/// <param name="RuntimeInstanceId">Exact owner identity, independent of other runtime instances.</param>
/// <param name="SessionId">Exact requested session identity.</param>
/// <param name="CoordinatorTransitionInProgress">Whether the attachment is changing; observation is withheld during a transition.</param>
/// <param name="AttachmentGeneration">Existing attachment ordinal, if present; not a usage revision.</param>
/// <param name="IsRetiring">Whether the existing attachment is retiring.</param>
/// <param name="IsTerminated">Whether Shutdown was observed on the attachment.</param>
/// <param name="Observation">Last admitted usage event on this attachment, or null when absent/withheld. Missing values are unknown, not zero.</param>
/// <param name="OmittedUsageEvents">Typed usage callbacks with mismatched session/provider identity on this attachment; not a completeness counter for events never admitted.</param>
public sealed record SessionRuntimeUsageState(Guid RuntimeInstanceId, string SessionId,
    bool CoordinatorTransitionInProgress, long? AttachmentGeneration, bool IsRetiring,
    bool IsTerminated, SessionRuntimeUsageObservation? Observation, long OmittedUsageEvents);

/// <summary>Provider-reported input-window values from one typed usage event. No model-catalog fallback or aggregation.</summary>
/// <param name="CurrentTokens">Reported occupancy, when nonnegative.</param>
/// <param name="TokenLimit">Reported positive input-context limit.</param>
/// <param name="MessageCount">Reported nonnegative contributing message count.</param>
/// <param name="Label">Reported bounded window label.</param>
/// <param name="TotalContextEnvelope">Reported positive total context window of the model (input and output).</param>
/// <param name="MaxOutputTokens">Reported positive maximum output size of the model.</param>
public readonly record struct SessionRuntimeUsageWindow(long? CurrentTokens, long? TokenLimit, int? MessageCount,
    string? Label = null, long? TotalContextEnvelope = null, long? MaxOutputTokens = null);

/// <summary>Provider-reported last-operation values from one typed usage event; no calculated totals.</summary>
/// <param name="InputTokens">Fresh input tokens.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CacheReadTokens">Cache read tokens.</param>
/// <param name="CacheWriteTokens">Cache write tokens.</param>
/// <param name="CachedInputTokens">Reused input tokens.</param>
/// <param name="ReasoningTokens">Reasoning tokens.</param>
/// <param name="Cost">Provider-reported nonnegative finite cost, without an inferred currency.</param>
/// <param name="DurationMs">Provider-reported nonnegative finite duration in milliseconds.</param>
/// <param name="Model">Reported bounded model name.</param>
/// <param name="ReasoningEffort">Reported bounded reasoning-effort setting.</param>
/// <param name="Initiator">Reported bounded initiator of the operation.</param>
/// <param name="Label">Reported bounded operation label.</param>
/// <param name="CostUnit">Reported bounded unit of the cost, such as AI credits; null when the provider names none.</param>
public readonly record struct SessionRuntimeUsageOperation(long? InputTokens, long? OutputTokens,
    long? CacheReadTokens, long? CacheWriteTokens, long? CachedInputTokens, long? ReasoningTokens,
    double? Cost, double? DurationMs, string? Model = null, string? ReasoningEffort = null,
    string? Initiator = null, string? Label = null, string? CostUnit = null);

/// <summary>One provider-reported rate-limit window from one typed usage event.</summary>
/// <param name="UsedPercent">Consumed share of the window, 0 to 100.</param>
/// <param name="ResetsAt">Reported reset time, if nondefault.</param>
/// <param name="WindowDurationMinutes">Reported nonnegative window length in minutes.</param>
public readonly record struct SessionRuntimeUsageRateWindow(int? UsedPercent, DateTimeOffset? ResetsAt, long? WindowDurationMinutes);

/// <summary>Provider-reported rate limits from one typed usage event.</summary>
/// <param name="Name">Reported bounded limit name.</param>
/// <param name="PlanType">Reported bounded plan type.</param>
/// <param name="Primary">Primary window, if supplied.</param>
/// <param name="Secondary">Secondary window, if supplied.</param>
public sealed record SessionRuntimeUsageRateLimits(string? Name, string? PlanType,
    SessionRuntimeUsageRateWindow? Primary, SessionRuntimeUsageRateWindow? Secondary);

/// <summary>Provider-reported cumulative session token totals from one typed usage event; never calculated here.</summary>
/// <param name="TotalTokens">Total tokens.</param>
/// <param name="InputTokens">Fresh input tokens.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CachedInputTokens">Reused input tokens.</param>
/// <param name="ReasoningTokens">Reasoning output tokens.</param>
public readonly record struct SessionRuntimeUsageTotals(long TotalTokens, long InputTokens, long OutputTokens,
    long CachedInputTokens, long ReasoningTokens);

/// <summary>Immutable, bounded last-event projection, ordered only by actor admission sequence. Neither provider timestamp is an ordering authority.</summary>
/// <param name="Sequence">Per-attachment observed usage-event sequence; not an event journal offset or completeness watermark.</param>
/// <param name="SourceUpdatedAt">Provider-reported usage timestamp, if nondefault.</param>
/// <param name="EventTimestamp">Provider event timestamp, if nondefault.</param>
/// <param name="Scope">Reported usage scope, or Unknown if invalid/missing.</param>
/// <param name="Source">Reported usage source, or Unknown if invalid/missing.</param>
/// <param name="Window">Only values in this event's window, if supplied.</param>
/// <param name="LastOperation">Only values in this event's last operation, if supplied.</param>
/// <param name="HadInvalidValues">Some reported numeric value or enum was invalid and was omitted; null is not zero.</param>
/// <param name="HadOmittedData">Provider details, over-long text or other nonprojected usage fields were supplied but are not retained.</param>
/// <param name="RateLimits">Only the rate limits in this event, if supplied.</param>
/// <param name="SessionTotal">Only the provider's cumulative session totals in this event, if supplied.</param>
public sealed record SessionRuntimeUsageObservation(long Sequence, DateTimeOffset? SourceUpdatedAt,
    DateTimeOffset? EventTimestamp, AgentUsageScope Scope, AgentUsageSource Source,
    SessionRuntimeUsageWindow? Window, SessionRuntimeUsageOperation? LastOperation, bool HadInvalidValues,
    bool HadOmittedData, SessionRuntimeUsageRateLimits? RateLimits = null, SessionRuntimeUsageTotals? SessionTotal = null)
{
    /// <summary>Longest retained provider text (labels, model, plan); longer text is dropped, not truncated.</summary>
    public const int MaximumTextLength = 128;

    internal static SessionRuntimeUsageObservation FromEvent(long sequence, AgentSessionUpdateEvent update)
    {
        var usage = update.Usage!;
        var invalid = false;
        long? nonnegative(long? value)
        {
            if (value is >= 0 or null) return value;
            invalid = true;
            return null;
        }
        int? nonnegativeCount(int? value)
        {
            if (value is >= 0 or null) return value;
            invalid = true;
            return null;
        }
        double? finite(double? value)
        {
            if (value is null || (double.IsFinite(value.Value) && value.Value >= 0)) return value;
            invalid = true;
            return null;
        }
        var omitted = false;
        SessionRuntimeUsageWindow? window = usage.Window is { } w
            ? new SessionRuntimeUsageWindow(nonnegative(w.CurrentTokens), positiveLimit(w.TokenLimit), nonnegativeCount(w.MessageCount),
                text(w.Label), positiveLimit(w.TotalContextEnvelope), positiveLimit(w.MaxOutputTokens))
            : null;
        SessionRuntimeUsageOperation? operation = usage.LastOperation is { } o
            ? new SessionRuntimeUsageOperation(nonnegative(o.InputTokens), nonnegative(o.OutputTokens),
                nonnegative(o.CacheReadTokens), nonnegative(o.CacheWriteTokens), nonnegative(o.CachedInputTokens),
                nonnegative(o.ReasoningTokens), finite(o.Cost), finite(o.DurationMs),
                text(o.Model), text(o.ReasoningEffort), text(o.Initiator), text(o.Label), text(o.CostUnit))
            : null;
        var rateLimits = usage.RateLimits is { } r
            ? new SessionRuntimeUsageRateLimits(text(r.Name), text(r.PlanType), rateWindow(r.Primary), rateWindow(r.Secondary))
            : null;
        SessionRuntimeUsageTotals? total = null;
        var codex = usage.Details as CodexSessionUsageDetails;
        if (codex?.TotalUsage is { } t)
        {
            if (t.TotalTokens >= 0 && t.InputTokens >= 0 && t.OutputTokens >= 0 && t.CachedInputTokens >= 0 && t.ReasoningOutputTokens >= 0)
                total = new SessionRuntimeUsageTotals(t.TotalTokens, t.InputTokens, t.OutputTokens, t.CachedInputTokens, t.ReasoningOutputTokens);
            else invalid = true;
        }
        var scope = Enum.IsDefined(usage.Scope) ? usage.Scope : AgentUsageScope.Unknown;
        var source = Enum.IsDefined(usage.Source) ? usage.Source : AgentUsageSource.Unknown;
        if (scope != usage.Scope || source != usage.Source) invalid = true;
        // Not retained: the rate-limit display label, an operation's parent tool call and every provider detail
        // other than the cumulative session totals above (quotas, named limits, compaction, per-turn breakdowns).
        if (usage.RateLimits?.Label is not null || usage.LastOperation?.ParentToolCallId is not null
            || usage.Details is not null && (codex is null || codex.LastTurnUsage is not null || codex.ModelContextWindow is not null
                || codex.RateLimits is not null || codex.NamedRateLimits is not null))
            omitted = true;
        return new(sequence, usage.UpdatedAt == default ? null : usage.UpdatedAt,
            update.Timestamp == default ? null : update.Timestamp, scope, source, window, operation, invalid, omitted,
            rateLimits, total);

        long? positiveLimit(long? value)
        {
            if (value is > 0 or null) return value;
            invalid = true;
            return null;
        }

        // Bounded single-line text; anything longer or carrying control characters is dropped, never truncated.
        string? text(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.Length <= MaximumTextLength && !value.Any(char.IsControl)) return value;
            omitted = true;
            return null;
        }

        SessionRuntimeUsageRateWindow? rateWindow(AgentRateLimitWindow? value)
        {
            if (value is null) return null;
            var used = value.UsedPercent;
            if (used is < 0 or > 100) { invalid = true; used = null; }
            return new SessionRuntimeUsageRateWindow(used, value.ResetsAt is { } reset && reset != default ? reset : null,
                nonnegative(value.WindowDurationMinutes));
        }
    }
}
