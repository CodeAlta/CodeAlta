using CodeAlta.Agent.LocalRuntime;

namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// OpenAI-compatible Chat/Completions provider runtime with a transitional backend facade.
/// </summary>
public sealed class OpenAIChatAgentBackend : IAgentBackend, IAgentSharedSessionMetadataBackend, ICodeAltaModelProviderRuntime
{
    private readonly ICodeAltaModelProviderRuntime _runtime;
    private readonly IAgentBackend _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAIChatAgentBackend"/> class.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    public OpenAIChatAgentBackend(OpenAIChatAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _runtime = OpenAIBackendFactory.CreateChatProviderRuntime(options);
        _inner = OpenAIBackendFactory.CreateChatBackend(options);
    }

    /// <inheritdoc />
    public AgentBackendId BackendId => _inner.BackendId;

    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;

    /// <inheritdoc />
    public ModelProviderDescriptor Descriptor => _runtime.Descriptor;

    /// <inheritdoc />
    public ModelProviderRuntimeDescriptor RuntimeDescriptor => _runtime.RuntimeDescriptor;

    /// <inheritdoc />
    public IModelProviderModelCatalog? ModelCatalog => _runtime.ModelCatalog;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _runtime.StartAsync(cancellationToken).ConfigureAwait(false);
        await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _runtime.StopAsync(cancellationToken).ConfigureAwait(false);
        await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        => _inner.ListModelsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        => _runtime.ProbeAsync(cancellationToken);

    /// <inheritdoc />
    public IModelProviderTurnExecutor CreateTurnExecutor() => _runtime.CreateTurnExecutor();

    /// <inheritdoc />
    public CodeAltaAgentRuntimeProviderRegistration CreateProviderRegistration() => _runtime.CreateProviderRegistration();

    /// <inheritdoc />
    public IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
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
    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await _runtime.DisposeAsync().ConfigureAwait(false);
    }
}
