using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Agent.Copilot;

/// <summary>
/// Direct GitHub Copilot model-provider runtime.
/// </summary>
public sealed class CopilotDirectModelProviderRuntime : IAgentModelProviderRuntime
{
    /// <summary>
    /// The canonical provider type and protocol family for direct Copilot access.
    /// </summary>
    public const string ProtocolFamily = "copilot";

    private readonly IAgentModelProviderRuntime _runtime;

    /// <summary>
    /// Initializes a new instance of the <see cref="CopilotDirectModelProviderRuntime"/> class.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    /// <exception cref="ArgumentException">Thrown when no provider is configured.</exception>
    public CopilotDirectModelProviderRuntime(CopilotDirectModelProviderRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Providers.Count == 0)
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
        }

        foreach (var provider in options.Providers)
        {
            provider.StateRootPath ??= options.StateRootPath;
        }


        _runtime = CreateProviderRuntime(options.Providers[0]);

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

    private static AgentModelProviderRuntime CreateProviderRuntime(CopilotDirectProviderOptions provider)
    {
        var providerKey = provider.ProviderKey.Trim();
        var displayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? providerKey : provider.DisplayName.Trim();
        var runtimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = ProtocolFamily,
            ProviderKey = providerKey,
            DisplayName = displayName,
            TransportKind = AgentTransportKind.OpenAIChatCompletions,
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            Profile = provider.Profile ?? CreateDefaultProfile(),
            Compaction = provider.Compaction ?? AgentCompactionSettings.Default,
        };
        var descriptor = new ModelProviderDescriptor(new ModelProviderId(providerKey), displayName, ProtocolFamily)
        {
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            DefaultModelId = provider.SingleModelId,
        };
        return new AgentModelProviderRuntime(
            descriptor,
            runtimeDescriptor,
            new CopilotDirectTurnExecutor(provider));
    }

    private static AgentProviderProfile CreateDefaultProfile()
        => new()
        {
            SupportsDeveloperRole = true,
            SupportsReasoningEffort = true,
            SupportsStore = false,
            StreamsUsage = true,
            MaxTokensFieldName = "max_completion_tokens",
            ReasoningFieldNames = ["reasoning_text", "reasoning_content", "reasoning"],
            ReasoningInputFieldName = "reasoning_opaque",
        };
}
