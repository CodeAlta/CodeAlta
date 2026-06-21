using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Base type for session runtime events.
/// </summary>
/// <param name="SessionId">The owning session id.</param>
/// <param name="Timestamp">The event timestamp.</param>
public abstract record SessionRuntimeEvent(string SessionId, DateTimeOffset Timestamp);

/// <summary>
/// Wraps a sanitized agent event for a specific session.
/// </summary>
/// <param name="SessionId">The owning session id.</param>
/// <param name="Event">The sanitized agent event.</param>
public sealed record SessionAgentEvent(
    string SessionId,
    AgentEvent Event)
    : SessionRuntimeEvent(SessionId, Event.Timestamp);

/// <summary>
/// Represents a host-generated session event.
/// </summary>
/// <param name="SessionId">The owning session id.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <param name="Kind">The session-update kind.</param>
/// <param name="Message">The user-visible message.</param>
public sealed record SessionHostEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    AgentSessionUpdateKind Kind,
    string Message)
    : SessionRuntimeEvent(SessionId, Timestamp);

/// <summary>
/// Wraps a host lifecycle event for a specific session.
/// </summary>
/// <param name="SessionId">The owning session id.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <param name="Event">The lifecycle event.</param>
public sealed record SessionLifecycleRuntimeEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    SessionLifecycleEvent Event)
    : SessionRuntimeEvent(SessionId, Timestamp);

/// <summary>
/// Announces that a session descriptor was materialized or refreshed by the runtime.
/// </summary>
/// <param name="SessionId">The owning session id.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <param name="Session">The materialized session descriptor snapshot.</param>
public sealed record SessionCatalogRuntimeEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    SessionViewDescriptor Session)
    : SessionRuntimeEvent(SessionId, Timestamp);

/// <summary>
/// Announces that a session's agent configuration changed without requiring a catalog refresh.
/// </summary>
/// <param name="SessionId">The owning session id.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <param name="ProviderId">The selected provider identifier, when known.</param>
/// <param name="ProviderKey">The selected provider key, when known.</param>
/// <param name="ModelId">The selected model identifier, when known.</param>
/// <param name="ReasoningEffort">The selected reasoning effort, when known.</param>
/// <param name="AgentPromptId">The selected agent prompt identifier, when known.</param>
public sealed record SessionAgentConfigurationRuntimeEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string? ProviderId,
    string? ProviderKey,
    string? ModelId,
    AgentReasoningEffort? ReasoningEffort,
    string? AgentPromptId)
    : SessionRuntimeEvent(SessionId, Timestamp);

/// <summary>
/// Wraps a host queue change event for a specific session.
/// </summary>
/// <param name="SessionId">The owning session id.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <param name="QueuedPromptCount">The current queued prompt count after the change.</param>
/// <param name="QueueItemId">The queue item identifier associated with the change, when known.</param>
/// <param name="PromptPreview">The short visible prompt preview associated with the queue item, when known.</param>
/// <param name="IsEnqueued">A value indicating whether the change represents enqueueing a prompt.</param>
public sealed record SessionQueueRuntimeEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    int QueuedPromptCount,
    string? QueueItemId,
    string? PromptPreview,
    bool IsEnqueued)
    : SessionRuntimeEvent(SessionId, Timestamp)
{
    /// <summary>
    /// Gets the queue item kind, when known.
    /// </summary>
    public string? QueueKind { get; init; }
}
