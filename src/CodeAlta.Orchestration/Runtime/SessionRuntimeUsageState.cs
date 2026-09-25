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

/// <summary>Only numeric provider-reported input-window values from one typed usage event. No model-catalog fallback or aggregation.</summary>
/// <param name="CurrentTokens">Reported occupancy, when nonnegative.</param>
/// <param name="TokenLimit">Reported positive input-context limit.</param>
/// <param name="MessageCount">Reported nonnegative contributing message count.</param>
public readonly record struct SessionRuntimeUsageWindow(long? CurrentTokens, long? TokenLimit, int? MessageCount);

/// <summary>Only numeric provider-reported last-operation values from one typed usage event; no calculated totals.</summary>
/// <param name="InputTokens">Fresh input tokens.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CacheReadTokens">Cache read tokens.</param>
/// <param name="CacheWriteTokens">Cache write tokens.</param>
/// <param name="CachedInputTokens">Reused input tokens.</param>
/// <param name="ReasoningTokens">Reasoning tokens.</param>
/// <param name="Cost">Provider-reported nonnegative finite cost, without an inferred currency.</param>
/// <param name="DurationMs">Provider-reported nonnegative finite duration in milliseconds.</param>
public readonly record struct SessionRuntimeUsageOperation(long? InputTokens, long? OutputTokens,
    long? CacheReadTokens, long? CacheWriteTokens, long? CachedInputTokens, long? ReasoningTokens,
    double? Cost, double? DurationMs);

/// <summary>Immutable, bounded last-event projection, ordered only by actor admission sequence. Neither provider timestamp is an ordering authority.</summary>
/// <param name="Sequence">Per-attachment observed usage-event sequence; not an event journal offset or completeness watermark.</param>
/// <param name="SourceUpdatedAt">Provider-reported usage timestamp, if nondefault.</param>
/// <param name="EventTimestamp">Provider event timestamp, if nondefault.</param>
/// <param name="Scope">Reported usage scope, or Unknown if invalid/missing.</param>
/// <param name="Source">Reported usage source, or Unknown if invalid/missing.</param>
/// <param name="Window">Only values in this event's window, if supplied.</param>
/// <param name="LastOperation">Only values in this event's last operation, if supplied.</param>
/// <param name="HadInvalidValues">Some reported numeric value or enum was invalid and was omitted; null is not zero.</param>
/// <param name="HadOmittedData">Provider details, labels, rate limits or other nonprojected usage fields were supplied but are not retained.</param>
public sealed record SessionRuntimeUsageObservation(long Sequence, DateTimeOffset? SourceUpdatedAt,
    DateTimeOffset? EventTimestamp, AgentUsageScope Scope, AgentUsageSource Source,
    SessionRuntimeUsageWindow? Window, SessionRuntimeUsageOperation? LastOperation, bool HadInvalidValues,
    bool HadOmittedData)
{
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
        SessionRuntimeUsageWindow? window = usage.Window is { } w
            ? new SessionRuntimeUsageWindow(nonnegative(w.CurrentTokens), positiveLimit(w.TokenLimit), nonnegativeCount(w.MessageCount))
            : null;
        SessionRuntimeUsageOperation? operation = usage.LastOperation is { } o
            ? new SessionRuntimeUsageOperation(nonnegative(o.InputTokens), nonnegative(o.OutputTokens),
                nonnegative(o.CacheReadTokens), nonnegative(o.CacheWriteTokens), nonnegative(o.CachedInputTokens),
                nonnegative(o.ReasoningTokens), finite(o.Cost), finite(o.DurationMs))
            : null;
        var scope = Enum.IsDefined(usage.Scope) ? usage.Scope : AgentUsageScope.Unknown;
        var source = Enum.IsDefined(usage.Source) ? usage.Source : AgentUsageSource.Unknown;
        if (scope != usage.Scope || source != usage.Source) invalid = true;
        var omitted = usage.Details is not null || usage.RateLimits is not null
            || hasUnprojectedWindowFields(usage.Window) || hasUnprojectedOperationFields(usage.LastOperation);
        return new(sequence, usage.UpdatedAt == default ? null : usage.UpdatedAt,
            update.Timestamp == default ? null : update.Timestamp, scope, source, window, operation, invalid, omitted);

        long? positiveLimit(long? value)
        {
            if (value is > 0 or null) return value;
            invalid = true;
            return null;
        }

        static bool hasUnprojectedWindowFields(AgentWindowUsageSnapshot? w)
            => w is not null && (w.Label is not null || w.TotalContextEnvelope is not null || w.MaxOutputTokens is not null);
        static bool hasUnprojectedOperationFields(AgentOperationUsageSnapshot? o)
            => o is not null && (o.Model is not null || o.Initiator is not null || o.ParentToolCallId is not null
                || o.ReasoningEffort is not null || o.Label is not null);
    }
}
