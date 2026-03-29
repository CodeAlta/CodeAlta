namespace CodeAlta.Agent;

/// <summary>
/// Represents an agent backend runtime capable of creating and resuming sessions.
/// </summary>
public interface IAgentBackend : IAsyncDisposable
{
    /// <summary>
    /// Gets the identifier of this backend.
    /// </summary>
    AgentBackendId BackendId { get; }

    /// <summary>
    /// Gets a human-friendly backend name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Starts the backend runtime and performs any required handshake.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the backend runtime and releases runtime resources.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists available models.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sessions known to the backend.
    /// </summary>
    /// <param name="filter">Optional filter.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an existing backend session when supported.
    /// </summary>
    /// <param name="sessionId">The backend session identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the backend deleted the session; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId"/> is empty.</exception>
    Task<bool> DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    /// <summary>
    /// Creates a new session.
    /// </summary>
    /// <param name="options">Session creation options.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    Task<IAgentSession> CreateSessionAsync(
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes an existing session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="options">Session resume options.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="sessionId"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    Task<IAgentSession> ResumeSessionAsync(
        string sessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default);
}

