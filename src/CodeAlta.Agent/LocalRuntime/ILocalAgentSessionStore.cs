namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Defines the shared local-runtime session store abstraction.
/// </summary>
public interface ILocalAgentSessionStore
{
    /// <summary>
    /// Creates or updates a persisted provider registration.
    /// </summary>
    /// <param name="provider">Provider descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted provider descriptor.</returns>
    Task<LocalAgentProviderDescriptor> UpsertProviderAsync(
        LocalAgentProviderDescriptor provider,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a provider descriptor by protocol family and provider key.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provider descriptor when found; otherwise <see langword="null" />.</returns>
    Task<LocalAgentProviderDescriptor?> GetProviderAsync(
        string protocolFamily,
        string providerKey,
        CancellationToken cancellationToken = default);

    /// <summary>
     /// Creates or updates the persisted session summary.
     /// </summary>
     /// <param name="session">Session summary.</param>
     /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertSessionAsync(
        LocalAgentSessionSummary session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a session summary.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <param name="sessionId">Local session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session summary when found; otherwise <see langword="null" />.</returns>
    Task<LocalAgentSessionSummary?> GetSessionAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sessions for a configured provider.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session summaries ordered by most recent update first.</returns>
    Task<IReadOnlyList<LocalAgentSessionSummary>> ListSessionsAsync(
        string protocolFamily,
        string providerKey,
        CancellationToken cancellationToken = default);

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
    /// Reads canonical session events.
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
        LocalAgentSessionState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets session state.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <param name="sessionId">Local session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session state when found; otherwise <see langword="null" />.</returns>
    Task<LocalAgentSessionState?> GetStateAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a persisted session when present.
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
