namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Defines agent-runtime persistence operations for CodeAlta-owned sessions.
/// </summary>
public interface IAgentSessionJournalStore : CodeAlta.Agent.IAgentSessionStore
{
    /// <summary>
    /// Creates or updates the persisted local session summary.
    /// </summary>
    /// <param name="session">Session summary.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertSessionAsync(
        AgentSessionSummary session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a session summary for a provider scope.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <param name="sessionId">Local session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session summary when found; otherwise <see langword="null" />.</returns>
    Task<AgentSessionSummary?> GetSessionAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a local session summary without applying a provider-scope filter.
    /// </summary>
    /// <param name="sessionId">Local session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session summary when found; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId" /> is empty.</exception>
    Task<AgentSessionSummary?> GetSessionSummaryAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists session summaries for a provider scope.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session summaries ordered by most recent update first.</returns>
    IAsyncEnumerable<AgentSessionSummary> ListSessionSummariesAsync(
        string protocolFamily,
        string providerKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists session summaries without applying a provider-scope filter.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session summaries ordered by most recent update first.</returns>
    IAsyncEnumerable<AgentSessionSummary> ListSessionSummariesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends canonical events to the session event log.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <param name="sessionId">Local session identifier.</param>
    /// <param name="events">Events to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendEventsAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        IReadOnlyList<AgentEvent> events,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads canonical session events for a provider scope.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <param name="sessionId">Local session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The canonical event list.</returns>
    Task<IReadOnlyList<AgentEvent>> ReadEventsAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists session state.
    /// </summary>
    /// <param name="state">Session state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertStateAsync(
        AgentSessionState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets session state for a provider scope.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <param name="sessionId">Local session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session state when found; otherwise <see langword="null" />.</returns>
    Task<AgentSessionState?> GetStateAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets session state without applying a provider-scope filter.
    /// </summary>
    /// <param name="sessionId">Local session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session state when found; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId" /> is empty.</exception>
    Task<AgentSessionState?> GetStateAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a persisted session when present for a provider scope.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <param name="sessionId">Local session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true" /> when the session existed and was deleted; otherwise <see langword="false" />.</returns>
    Task<bool> DeleteSessionAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        CancellationToken cancellationToken = default);
}
