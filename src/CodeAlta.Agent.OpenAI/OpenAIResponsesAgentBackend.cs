namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// Local-runtime backend for OpenAI Responses providers.
/// </summary>
public sealed class OpenAIResponsesAgentBackend : IAgentBackend, IAgentSharedSessionMetadataBackend
{
    private readonly IAgentBackend _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAIResponsesAgentBackend"/> class.
    /// </summary>
    /// <param name="options">The backend options.</param>
    public OpenAIResponsesAgentBackend(OpenAIResponsesAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inner = OpenAIBackendFactory.CreateResponsesBackend(options);
    }

    /// <inheritdoc />
    public AgentBackendId BackendId => _inner.BackendId;

    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        => _inner.ListModelsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        CancellationToken cancellationToken = default)
        => _inner.ListSessionsAsync(filter, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _inner.DeleteSessionAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public Task<IAgentSession> CreateSessionAsync(
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default)
        => _inner.CreateSessionAsync(options, cancellationToken);

    /// <inheritdoc />
    public Task<IAgentSession> ResumeSessionAsync(
        string sessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default)
        => _inner.ResumeSessionAsync(sessionId, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
