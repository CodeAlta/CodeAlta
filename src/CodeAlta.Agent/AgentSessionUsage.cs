using System.Text.Json.Serialization;

namespace CodeAlta.Agent;

/// <summary>
/// Represents normalized active-context and token-usage information for a session.
/// </summary>
/// <param name="Window">The current known active input-context usage snapshot when available.</param>
/// <param name="LastOperation">The most recent meaningful operation usage snapshot when available.</param>
/// <param name="RateLimits">The normalized rate-limit summary when available.</param>
/// <param name="Scope">The scope represented by this usage snapshot.</param>
/// <param name="Source">The provider/runtime event source that produced this usage snapshot.</param>
/// <param name="UpdatedAt">The time the usage snapshot was last updated.</param>
/// <param name="Details">Optional provider/runtime-specific usage details.</param>
public sealed record AgentSessionUsage(
    AgentWindowUsageSnapshot? Window = null,
    AgentOperationUsageSnapshot? LastOperation = null,
    AgentRateLimitSummary? RateLimits = null,
    AgentUsageScope Scope = AgentUsageScope.Unknown,
    AgentUsageSource Source = AgentUsageSource.Unknown,
    DateTimeOffset UpdatedAt = default,
    AgentSessionUsageDetails? Details = null)
{
    /// <summary>
    /// Gets the current number of tokens in the active input context when known.
    /// </summary>
    public long? CurrentTokens => Window?.CurrentTokens;

    /// <summary>
    /// Gets the active input-context limit for the active model when known.
    /// </summary>
    public long? TokenLimit => Window?.TokenLimit;

    /// <summary>
    /// Gets the number of messages currently contributing to the active input context when known.
    /// </summary>
    public int? MessageCount => Window?.MessageCount;

    /// <summary>
    /// Gets the percentage of the active input context currently in use when both values are available.
    /// </summary>
    public double? WindowUsagePercentage =>
        Window?.TokenLimit is > 0 && Window.CurrentTokens is >= 0
            ? (Window.CurrentTokens.Value * 100d) / Window.TokenLimit.Value
            : null;
}

/// <summary>
/// Represents a normalized active input-context usage snapshot.
/// </summary>
/// <param name="CurrentTokens">The current number of tokens in the active input context when known.</param>
/// <param name="TokenLimit">The active input-context limit when known.</param>
/// <param name="MessageCount">The number of messages currently contributing to the active input context when known.</param>
/// <param name="Label">The UI label describing the window snapshot.</param>
/// <param name="TotalContextEnvelope">The optional advertised input-plus-output model envelope.</param>
/// <param name="MaxOutputTokens">The optional maximum output-token limit.</param>
public sealed record AgentWindowUsageSnapshot(
    long? CurrentTokens,
    long? TokenLimit,
    int? MessageCount,
    string? Label = null,
    long? TotalContextEnvelope = null,
    long? MaxOutputTokens = null);

/// <summary>
/// Represents normalized usage for the most recent meaningful model operation.
/// </summary>
/// <param name="Model">The model that serviced the operation when known.</param>
/// <param name="InputTokens">The number of fresh input tokens consumed.</param>
/// <param name="OutputTokens">The number of output tokens produced.</param>
/// <param name="CacheReadTokens">The number of tokens read from prompt cache when known.</param>
/// <param name="CacheWriteTokens">The number of tokens written to prompt cache when known.</param>
/// <param name="CachedInputTokens">The number of cached input tokens reused when known.</param>
/// <param name="ReasoningTokens">The number of reasoning tokens consumed or produced when known.</param>
/// <param name="Cost">The provider-reported cost when available.</param>
/// <param name="DurationMs">The provider-reported duration in milliseconds when available.</param>
/// <param name="Initiator">The initiator of the operation when reported.</param>
/// <param name="ParentToolCallId">The parent tool call identifier when this operation belongs to a sub-agent or tool request.</param>
/// <param name="ReasoningEffort">The reasoning-effort setting used for the operation when reported.</param>
/// <param name="Label">The UI label describing the operation snapshot.</param>
public sealed record AgentOperationUsageSnapshot(
    string? Model = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? CacheReadTokens = null,
    long? CacheWriteTokens = null,
    long? CachedInputTokens = null,
    long? ReasoningTokens = null,
    double? Cost = null,
    double? DurationMs = null,
    string? Initiator = null,
    string? ParentToolCallId = null,
    string? ReasoningEffort = null,
    string? Label = null);

/// <summary>
/// Represents normalized rate-limit information for a session.
/// </summary>
/// <param name="Name">The rate-limit or quota display name when known.</param>
/// <param name="PlanType">The active plan type when known.</param>
/// <param name="Primary">The primary rate-limit window when available.</param>
/// <param name="Secondary">The secondary rate-limit window when available.</param>
/// <param name="Label">The UI label describing the rate-limit snapshot.</param>
public sealed record AgentRateLimitSummary(
    string? Name = null,
    string? PlanType = null,
    AgentRateLimitWindow? Primary = null,
    AgentRateLimitWindow? Secondary = null,
    string? Label = null);

/// <summary>
/// Represents a normalized rate-limit window.
/// </summary>
/// <param name="UsedPercent">The percentage of the window currently consumed when known.</param>
/// <param name="ResetsAt">The time the window resets when known.</param>
/// <param name="WindowDurationMinutes">The duration of the rate-limit window in minutes when known.</param>
public sealed record AgentRateLimitWindow(
    int? UsedPercent = null,
    DateTimeOffset? ResetsAt = null,
    long? WindowDurationMinutes = null);

/// <summary>
/// Identifies the semantic scope represented by a usage snapshot.
/// </summary>
public enum AgentUsageScope
{
    /// <summary>
    /// The scope is unknown.
    /// </summary>
    Unknown,

    /// <summary>
    /// The snapshot represents the current context window.
    /// </summary>
    CurrentWindow,

    /// <summary>
    /// The snapshot represents the most recent model operation.
    /// </summary>
    LastOperation,

    /// <summary>
    /// The snapshot represents cumulative session totals.
    /// </summary>
    SessionTotal,

    /// <summary>
    /// The snapshot represents compaction.
    /// </summary>
    Compaction,

    /// <summary>
    /// The snapshot represents truncation.
    /// </summary>
    Truncation,

    /// <summary>
    /// The snapshot only contains rate-limit data.
    /// </summary>
    RateLimitOnly
}

/// <summary>
/// Identifies which provider event produced a usage snapshot.
/// </summary>
public enum AgentUsageSource
{
    /// <summary>
    /// The source is unknown.
    /// </summary>
    Unknown,

    /// <summary>
    /// Copilot session usage information.
    /// </summary>
    CopilotSessionUsageInfo,

    /// <summary>
    /// Copilot assistant usage.
    /// </summary>
    CopilotAssistantUsage,

    /// <summary>
    /// Copilot account quota snapshots fetched explicitly from the provider.
    /// </summary>
    CopilotAccountQuota,

    /// <summary>
    /// Copilot session compaction completion.
    /// </summary>
    CopilotCompactionComplete,

    /// <summary>
    /// Copilot session truncation.
    /// </summary>
    CopilotTruncation,

    /// <summary>
    /// Codex session token usage updates.
    /// </summary>
    CodexSessionTokenUsageUpdated,

    /// <summary>
    /// Codex token-count events.
    /// </summary>
    CodexTokenCountEvent,

    /// <summary>
    /// Codex account rate-limit updates.
    /// </summary>
    CodexAccountRateLimitsUpdated,

    /// <summary>
    /// Usage data recovered from persisted state or history.
    /// </summary>
    RecoveredHistory,

    /// <summary>
    /// Live usage data reported by a provider turn executor.
    /// </summary>
    [JsonStringEnumMemberName("LocalProviderUsage")]
    ProviderUsage
}

/// <summary>
/// Base type for provider/runtime-specific session usage information.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(CodexSessionUsageDetails), "codex")]
[JsonDerivedType(typeof(CopilotSessionUsageDetails), "copilot")]
public abstract record AgentSessionUsageDetails;

/// <summary>
/// Codex-specific usage details.
/// </summary>
/// <param name="LastTurnUsage">Token usage for the most recent turn when available.</param>
/// <param name="TotalUsage">Cumulative session token usage when available.</param>
/// <param name="ModelContextWindow">The model context-window size reported by Codex when available.</param>
/// <param name="RateLimits">The latest Codex account rate-limit snapshot when available.</param>
/// <param name="NamedRateLimits">The latest Codex rate-limit snapshots keyed by provider limit identifier.</param>
public sealed record CodexSessionUsageDetails(
    CodexTokenUsage? LastTurnUsage = null,
    CodexTokenUsage? TotalUsage = null,
    long? ModelContextWindow = null,
    CodexRateLimitSnapshot? RateLimits = null,
    CodexRateLimitSnapshot[]? NamedRateLimits = null)
    : AgentSessionUsageDetails;

/// <summary>
/// Copilot-specific usage details.
/// </summary>
/// <param name="LastAssistantUsage">Usage for the most recent Copilot model call when available.</param>
/// <param name="LastCompaction">Usage and reduction details for the most recent compaction when available.</param>
/// <param name="QuotaSnapshots">Typed Copilot quota snapshots when available.</param>
public sealed record CopilotSessionUsageDetails(
    CopilotAssistantUsage? LastAssistantUsage = null,
    CopilotCompactionUsage? LastCompaction = null,
    CopilotQuotaSnapshot[]? QuotaSnapshots = null)
    : AgentSessionUsageDetails;

/// <summary>
/// Codex token-usage breakdown.
/// </summary>
/// <param name="CachedInputTokens">The number of cached input tokens reused.</param>
/// <param name="InputTokens">The number of fresh input tokens consumed.</param>
/// <param name="OutputTokens">The number of output tokens produced.</param>
/// <param name="ReasoningOutputTokens">The number of reasoning output tokens produced.</param>
/// <param name="TotalTokens">The total number of tokens represented by the breakdown.</param>
public sealed record CodexTokenUsage(
    long CachedInputTokens,
    long InputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens);

/// <summary>
/// Codex rate-limit snapshot.
/// </summary>
/// <param name="LimitId">The provider-specific limit identifier.</param>
/// <param name="LimitName">The provider-specific limit display name.</param>
/// <param name="PlanType">The active Codex plan type when known.</param>
/// <param name="Primary">The primary rate-limit window when available.</param>
/// <param name="Secondary">The secondary rate-limit window when available.</param>
/// <param name="Credits">The optional account-credit snapshot.</param>
public sealed record CodexRateLimitSnapshot(
    string? LimitId,
    string? LimitName,
    string? PlanType,
    CodexRateLimitWindow? Primary,
    CodexRateLimitWindow? Secondary,
    CodexCreditsSnapshot? Credits = null);

/// <summary>
/// Represents the allowlisted Codex account-credit snapshot.
/// </summary>
/// <param name="HasCredits">Whether credits are available.</param>
/// <param name="Unlimited">Whether credit use is unlimited.</param>
/// <param name="Balance">The provider-formatted balance when available.</param>
public sealed record CodexCreditsSnapshot(
    bool HasCredits,
    bool Unlimited,
    string? Balance = null);

/// <summary>
/// Codex rate-limit window details.
/// </summary>
/// <param name="UsedPercent">The percentage of the window currently consumed.</param>
/// <param name="ResetsAt">The time the window resets when known.</param>
/// <param name="WindowDurationMinutes">The rate-limit window duration in minutes when known.</param>
public sealed record CodexRateLimitWindow(
    int UsedPercent,
    DateTimeOffset? ResetsAt,
    long? WindowDurationMinutes);

/// <summary>
/// Copilot assistant-call token and billing usage.
/// </summary>
/// <param name="Model">The model that serviced the call.</param>
/// <param name="InputTokens">The number of input tokens consumed.</param>
/// <param name="OutputTokens">The number of output tokens produced.</param>
/// <param name="CacheReadTokens">The number of tokens read from prompt cache.</param>
/// <param name="CacheWriteTokens">The number of tokens written to prompt cache.</param>
/// <param name="Cost">The provider-reported request cost when available.</param>
/// <param name="DurationMs">The provider-reported request duration in milliseconds when available.</param>
/// <param name="Initiator">The initiator for the call when reported.</param>
/// <param name="ParentToolCallId">The parent tool call identifier when this usage belongs to a sub-agent or tool request.</param>
/// <param name="ReasoningEffort">The reasoning-effort setting used for the request when reported.</param>
/// <param name="TotalNanoAiu">The Copilot AIU cost reported by the provider when available.</param>
/// <param name="TokenDetails">Additional provider token details when available.</param>
public sealed record CopilotAssistantUsage(
    string Model,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? CacheReadTokens = null,
    long? CacheWriteTokens = null,
    double? Cost = null,
    double? DurationMs = null,
    string? Initiator = null,
    string? ParentToolCallId = null,
    string? ReasoningEffort = null,
    double? TotalNanoAiu = null,
    CopilotTokenDetail[]? TokenDetails = null);

/// <summary>
/// Copilot token-detail entry.
/// </summary>
/// <param name="TokenType">The token category label.</param>
/// <param name="TokenCount">The number of tokens in that category.</param>
public sealed record CopilotTokenDetail(
    string TokenType,
    long TokenCount);

/// <summary>
/// Copilot compaction usage and reduction details.
/// </summary>
/// <param name="Success">Whether the compaction completed successfully.</param>
/// <param name="PreCompactionTokens">The token count before compaction when reported.</param>
/// <param name="PostCompactionTokens">The token count after compaction when reported.</param>
/// <param name="PreCompactionMessages">The message count before compaction when reported.</param>
/// <param name="MessagesRemoved">The number of messages removed by compaction when reported.</param>
/// <param name="TokensRemoved">The number of tokens removed by compaction when reported.</param>
/// <param name="TokensUsed">The token usage for the compaction model call when reported.</param>
/// <param name="SummaryContent">The provider-provided compaction summary when reported.</param>
public sealed record CopilotCompactionUsage(
    bool Success,
    long? PreCompactionTokens = null,
    long? PostCompactionTokens = null,
    int? PreCompactionMessages = null,
    int? MessagesRemoved = null,
    long? TokensRemoved = null,
    CopilotCompactionTokenUsage? TokensUsed = null,
    string? SummaryContent = null);

/// <summary>
/// Copilot token usage for the compaction LLM call.
/// </summary>
/// <param name="InputTokens">The number of input tokens consumed.</param>
/// <param name="OutputTokens">The number of output tokens produced.</param>
/// <param name="CachedInputTokens">The number of cached input tokens reused.</param>
public sealed record CopilotCompactionTokenUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens);

/// <summary>
/// Named Copilot quota snapshot.
/// </summary>
/// <param name="Name">The quota identifier.</param>
/// <param name="Details">The typed quota details.</param>
public sealed record CopilotQuotaSnapshot(
    string Name,
    CopilotQuotaDetails Details);

/// <summary>
/// Base type for typed Copilot quota details.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(CopilotRequestQuotaDetails), "request")]
[JsonDerivedType(typeof(CopilotOpaqueQuotaDetails), "opaque")]
public abstract record CopilotQuotaDetails;

/// <summary>
/// Typed Copilot request-quota snapshot.
/// </summary>
/// <param name="EntitlementRequests">The number of requests included in the entitlement when known.</param>
/// <param name="UsedRequests">The number of requests used in the current period when known.</param>
/// <param name="RemainingPercentage">The percentage of the entitlement remaining when known.</param>
/// <param name="Overage">The number of overage requests when known.</param>
/// <param name="UsageAllowedWithExhaustion">Whether usage continues when the quota is exhausted.</param>
/// <param name="IsUnlimitedEntitlement">Whether the quota is effectively unlimited.</param>
/// <param name="ResetDate">The time the quota resets when known.</param>
public sealed record CopilotRequestQuotaDetails(
    long? EntitlementRequests = null,
    long? UsedRequests = null,
    double? RemainingPercentage = null,
    long? Overage = null,
    bool? UsageAllowedWithExhaustion = null,
    bool? IsUnlimitedEntitlement = null,
    DateTimeOffset? ResetDate = null)
    : CopilotQuotaDetails;

/// <summary>
/// Typed opaque Copilot quota snapshot fallback for unknown shapes.
/// </summary>
/// <param name="Summary">A compact text summary of the unknown payload.</param>
public sealed record CopilotOpaqueQuotaDetails(
    string Summary)
    : CopilotQuotaDetails;
