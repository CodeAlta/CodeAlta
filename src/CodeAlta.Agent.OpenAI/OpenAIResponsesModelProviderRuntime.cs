using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// OpenAI Responses model-provider runtime.
/// </summary>
public sealed class OpenAIResponsesModelProviderRuntime : IAgentModelProviderRuntime
{
    private readonly IAgentModelProviderRuntime _runtime;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAIResponsesModelProviderRuntime"/> class.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    public OpenAIResponsesModelProviderRuntime(OpenAIResponsesModelProviderRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _runtime = OpenAIModelProviderRuntimeFactory.CreateResponsesProviderRuntime(options);
    }

    /// <inheritdoc />
    public ModelProviderDescriptor Descriptor => _runtime.Descriptor;

    /// <inheritdoc />
    public ModelProviderRuntimeDescriptor RuntimeDescriptor => _runtime.RuntimeDescriptor;

    /// <inheritdoc />
    public IModelProviderModelCatalog? ModelCatalog => _runtime.ModelCatalog;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
        => _runtime.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
        => _runtime.StopAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        => _runtime.ProbeAsync(cancellationToken);

    /// <inheritdoc />
    public IModelProviderTurnExecutor CreateTurnExecutor() => _runtime.CreateTurnExecutor();

    /// <inheritdoc />
    public AgentRuntimeProviderRegistration CreateProviderRegistration() => _runtime.CreateProviderRegistration();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
