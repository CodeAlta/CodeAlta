using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;

namespace CodeAlta.Agent.Copilot;

/// <summary>
/// Direct GitHub Copilot provider runtime with a transitional backend facade.
/// </summary>
public sealed class CopilotDirectAgentBackend : IAgentBackend, IAgentSharedSessionMetadataBackend, ICodeAltaModelProviderRuntime
{
    /// <summary>
    /// The canonical provider type and protocol family for direct Copilot access.
    /// </summary>
    public const string ProtocolFamily = "copilot";

    private readonly ICodeAltaModelProviderRuntime _runtime;
    private readonly IAgentBackend _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="CopilotDirectAgentBackend"/> class.
    /// </summary>
    /// <param name="options">The backend options.</param>
    /// <exception cref="ArgumentException">Thrown when no provider is configured.</exception>
    public CopilotDirectAgentBackend(CopilotDirectAgentBackendOptions options)
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

        var backendId = options.BackendIdOverride ?? new AgentBackendId(options.Providers[0].ProviderKey.Trim());
        var displayName = string.IsNullOrWhiteSpace(options.DisplayNameOverride)
            ? "Copilot"
            : options.DisplayNameOverride.Trim();

        _runtime = CreateProviderRuntime(options.Providers[0]);
        _inner = new CodeAltaAgentRuntime(
            backendId,
            displayName,
            new CodeAltaAgentRuntimeOptions
            {
                StateRootPath = options.StateRootPath,
                Providers =
                [
                    .. options.Providers.Select(provider => new CodeAltaAgentRuntimeProviderRegistration
                    {
                        Provider = new ModelProviderRuntimeDescriptor
                        {
                            ProtocolFamily = ProtocolFamily,
                            ProviderKey = provider.ProviderKey.Trim(),
                            DisplayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.ProviderKey.Trim() : provider.DisplayName.Trim(),
                            TransportKind = LocalAgentTransportKind.OpenAIChatCompletions,
                            BaseUri = provider.BaseUri,
                            IsDefault = provider.IsDefault,
                            Profile = provider.Profile ?? CreateDefaultProfile(),
                            Compaction = provider.Compaction ?? LocalAgentCompactionSettings.Default,
                        },
                        TurnExecutor = new CopilotDirectTurnExecutor(provider),
                    }),
                ],
            });
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

    private static CodeAltaModelProviderRuntime CreateProviderRuntime(CopilotDirectProviderOptions provider)
    {
        var providerKey = provider.ProviderKey.Trim();
        var displayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? providerKey : provider.DisplayName.Trim();
        var runtimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = ProtocolFamily,
            ProviderKey = providerKey,
            DisplayName = displayName,
            TransportKind = LocalAgentTransportKind.OpenAIChatCompletions,
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            Profile = provider.Profile ?? CreateDefaultProfile(),
            Compaction = provider.Compaction ?? LocalAgentCompactionSettings.Default,
        };
        var descriptor = new ModelProviderDescriptor(new ModelProviderId(providerKey), displayName, ProtocolFamily)
        {
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            DefaultModelId = provider.SingleModelId,
        };
        return new CodeAltaModelProviderRuntime(
            descriptor,
            runtimeDescriptor,
            new CopilotDirectTurnExecutor(provider));
    }

    private static LocalAgentProviderProfile CreateDefaultProfile()
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
