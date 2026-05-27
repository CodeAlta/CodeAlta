using Anthropic;
using Anthropic.Core;
using Anthropic.Credentials;
using Anthropic.Models.Models;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using Microsoft.Extensions.AI;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Anthropic Messages provider runtime with a transitional backend facade.
/// </summary>
public sealed class AnthropicAgentBackend : IAgentBackend, IAgentSharedSessionMetadataBackend, ICodeAltaModelProviderRuntime
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.Anthropic");
    private readonly ICodeAltaModelProviderRuntime _runtime;
    private readonly IAgentBackend _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnthropicAgentBackend"/> class.
    /// </summary>
    /// <param name="options">The backend options.</param>
    public AnthropicAgentBackend(AnthropicAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Providers.Count == 0)
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
        }

        var backendId = options.BackendIdOverride ?? AgentBackendIds.AnthropicMessages;
        var displayName = string.IsNullOrWhiteSpace(options.DisplayNameOverride) ? "Anthropic Messages" : options.DisplayNameOverride.Trim();
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
                            ProtocolFamily = "anthropic-messages",
                            ProviderKey = provider.ProviderKey.Trim(),
                            DisplayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.ProviderKey.Trim() : provider.DisplayName.Trim(),
                            TransportKind = LocalAgentTransportKind.AnthropicMessages,
                            BaseUri = provider.BaseUri,
                            IsDefault = provider.IsDefault,
                            Profile = provider.Profile ?? new LocalAgentProviderProfile
                            {
                                SupportsDeveloperRole = false,
                                StreamsUsage = true,
                                SupportsThoughtSignatures = true,
                            },
                            Compaction = provider.Compaction ?? LocalAgentCompactionSettings.Default,
                        },
                        TurnExecutor = CreateTurnExecutor(provider),
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

    private static CodeAltaModelProviderRuntime CreateProviderRuntime(AnthropicProviderOptions provider)
    {
        var providerKey = provider.ProviderKey.Trim();
        var displayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? providerKey : provider.DisplayName.Trim();
        var runtimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "anthropic-messages",
            ProviderKey = providerKey,
            DisplayName = displayName,
            TransportKind = LocalAgentTransportKind.AnthropicMessages,
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            Profile = provider.Profile ?? new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = false,
                StreamsUsage = true,
                SupportsThoughtSignatures = true,
            },
            Compaction = provider.Compaction ?? LocalAgentCompactionSettings.Default,
        };
        var descriptor = new ModelProviderDescriptor(new ModelProviderId(providerKey), displayName, "anthropic")
        {
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            DefaultModelId = provider.SingleModelId,
        };
        return new CodeAltaModelProviderRuntime(
            descriptor,
            runtimeDescriptor,
            CreateTurnExecutor(provider));
    }

    internal static IModelProviderTurnExecutor CreateTurnExecutor(AnthropicProviderOptions provider)
    {
        return new LocalAgentChatClientTurnExecutor(
            (providerDescriptor, cancellationToken) => CreateChatClientAsync(provider, providerDescriptor, cancellationToken),
            (providerDescriptor, cancellationToken) => ListModelsAsync(provider, providerDescriptor, cancellationToken));
    }

    private static ValueTask<IChatClient> CreateChatClientAsync(
        AnthropicProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var useStreamingCompatibilityFallback = ShouldUseStreamingCompatibilityFallback(providerDescriptor);

        if (provider.ChatClientFactory is not null)
        {
            return ValueTask.FromResult(
                WrapChatClient(provider.ChatClientFactory(), providerDescriptor, useStreamingCompatibilityFallback));
        }

        var client = CreateSdkClient(provider, providerDescriptor);
        var chatClient = client.AsIChatClient();
        if (provider.HttpClient is null)
        {
            chatClient = new OwnedChatClient(chatClient, client);
        }

        return ValueTask.FromResult<IChatClient>(
            WrapChatClient(chatClient, providerDescriptor, useStreamingCompatibilityFallback));
    }

    private static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        AnthropicProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentModelInfo> models;
        if (provider.ModelListAsync is not null)
        {
            models = await provider.ModelListAsync(cancellationToken).ConfigureAwait(false);
            return AgentModelMetadataEnricher.EnrichModels(
                models,
                provider.ModelCatalog,
                provider.ModelsDevProviderId,
                provider.ModelOverrides);
        }

        if (!string.IsNullOrWhiteSpace(provider.SingleModelId))
        {
            models =
            [
                CreateSingleModelInfo(provider.SingleModelId, providerDescriptor),
            ];
            return AgentModelMetadataEnricher.EnrichModels(
                models,
                provider.ModelCatalog,
                provider.ModelsDevProviderId,
                provider.ModelOverrides);
        }

        var client = CreateSdkClient(provider, providerDescriptor);
        try
        {
            var results = new List<AgentModelInfo>();
            var page = await client.Models.List(cancellationToken: cancellationToken).ConfigureAwait(false);
            while (true)
            {
                results.AddRange(page.Items.Select(model => ToAgentModelInfo(providerDescriptor, model)));
                if (!page.HasNext())
                {
                    break;
                }

                page = await page.Next(cancellationToken).ConfigureAwait(false);
            }

            models = results;
        }
        finally
        {
            if (provider.HttpClient is null)
            {
                client.Dispose();
            }
        }

        return AgentModelMetadataEnricher.EnrichModels(
            models,
            provider.ModelCatalog,
            provider.ModelsDevProviderId,
            provider.ModelOverrides);
    }

    private static AnthropicClient CreateSdkClient(
        AnthropicProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor)
    {
        var options = new ClientOptions
        {
            ApiKey = provider.ApiKey,
            ExtraHeaders = provider.ExtraHeaders,
        };
        if (!string.IsNullOrEmpty(provider.AuthToken))
        {
            // Route bearer tokens through the SDK's Credentials pipeline so the Authorization
            // header is set via the HttpRequestHeaders.Authorization setter (which accepts
            // tokens containing characters such as ';' or ',' that GitHub Copilot tokens
            // include) instead of the validating Headers.Add path used by ClientOptions.AuthToken.
            options.Credentials = new StaticTokenCredentials(provider.AuthToken);
        }
        if (provider.HttpClient is not null)
        {
            options.HttpClient = provider.HttpClient;
        }

        if (provider.BaseUri is not null)
        {
            options.BaseUrl = provider.BaseUri.ToString();
        }

        return new AnthropicClient(options);
    }

    private static IChatClient WrapChatClient(
        IChatClient chatClient,
        ModelProviderRuntimeDescriptor providerDescriptor,
        bool useStreamingCompatibilityFallback)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(providerDescriptor);

        var adaptiveThinkingChatClient = new AnthropicAdaptiveThinkingChatClient(chatClient);
        if (!useStreamingCompatibilityFallback)
        {
            return adaptiveThinkingChatClient;
        }

        LogInfo(
            $"Using Anthropic streaming compatibility fallback backend={providerDescriptor.ProviderKey} provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName}");
        return new AnthropicStreamingCompatibilityChatClient(adaptiveThinkingChatClient);
    }

    private static bool ShouldUseStreamingCompatibilityFallback(ModelProviderRuntimeDescriptor providerDescriptor)
    {
        ArgumentNullException.ThrowIfNull(providerDescriptor);

        return HasHost(providerDescriptor.BaseUri, "minimax.io") ||
            HasHost(providerDescriptor.BaseUri, "minimaxi.com") ||
            HasHost(providerDescriptor.BaseUri, "githubcopilot.com") ||
            HasCopilotApiHost(providerDescriptor.BaseUri) ||
            providerDescriptor.ProviderKey.Contains("copilot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCopilotApiHost(Uri? baseUri)
        => baseUri?.Host.StartsWith("copilot-api.", StringComparison.OrdinalIgnoreCase) == true;

    private static bool HasHost(Uri? baseUri, string expectedHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHost);

        var host = baseUri?.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{expectedHost}", StringComparison.OrdinalIgnoreCase);
    }

    private static AgentModelInfo ToAgentModelInfo(
        ModelProviderRuntimeDescriptor provider,
        ModelInfo model)
    {
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["createdAt"] = model.CreatedAt,
            ["maxInputTokens"] = model.MaxInputTokens,
            ["maxTokens"] = model.MaxTokens,
        };

        return new AgentModelInfo(
            model.ID,
            DisplayName: model.DisplayName,
            Description: null,
            Provider: provider.ProviderKey,
            Capabilities: capabilities);
    }

    private static void LogInfo(string message)
    {
        Logger.Info(message);
    }

    private static AgentModelInfo CreateSingleModelInfo(
        string modelId,
        ModelProviderRuntimeDescriptor providerDescriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(providerDescriptor);

        var trimmedModelId = modelId.Trim();
        return new AgentModelInfo(
            trimmedModelId,
            DisplayName: trimmedModelId,
            Provider: providerDescriptor.ProviderKey);
    }
}
