using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Models;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using Microsoft.Extensions.AI;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Local-runtime backend for Anthropic Messages providers.
/// </summary>
public sealed class AnthropicAgentBackend : IAgentBackend, IAgentSharedSessionMetadataBackend
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.Anthropic");
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

        _inner = new LocalAgentBackend(
            options.BackendIdOverride ?? AgentBackendIds.AnthropicMessages,
            string.IsNullOrWhiteSpace(options.DisplayNameOverride) ? "Anthropic Messages" : options.DisplayNameOverride.Trim(),
            new LocalAgentBackendOptions
            {
                StateRootPath = options.StateRootPath,
                Providers =
                [
                    .. options.Providers.Select(provider => new LocalAgentBackendProviderRegistration
                    {
                        Provider = new LocalAgentProviderDescriptor
                        {
                            ProtocolFamily = "anthropic-messages",
                            ProviderKey = provider.ProviderKey.Trim(),
                            DisplayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.ProviderKey.Trim() : provider.DisplayName.Trim(),
                            BackendId = options.BackendIdOverride ?? AgentBackendIds.AnthropicMessages,
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

    private static ILocalAgentTurnExecutor CreateTurnExecutor(AnthropicProviderOptions provider)
    {
        return new LocalAgentChatClientTurnExecutor(
            (providerDescriptor, cancellationToken) => CreateChatClientAsync(provider, providerDescriptor, cancellationToken),
            (providerDescriptor, cancellationToken) => ListModelsAsync(provider, providerDescriptor, cancellationToken));
    }

    private static ValueTask<IChatClient> CreateChatClientAsync(
        AnthropicProviderOptions provider,
        LocalAgentProviderDescriptor providerDescriptor,
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
        return ValueTask.FromResult<IChatClient>(
            WrapChatClient(new OwnedChatClient(client.AsIChatClient(), client), providerDescriptor, useStreamingCompatibilityFallback));
    }

    private static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        AnthropicProviderOptions provider,
        LocalAgentProviderDescriptor providerDescriptor,
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

        using var client = CreateSdkClient(provider, providerDescriptor);
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
        return AgentModelMetadataEnricher.EnrichModels(
            models,
            provider.ModelCatalog,
            provider.ModelsDevProviderId,
            provider.ModelOverrides);
    }

    private static AnthropicClient CreateSdkClient(
        AnthropicProviderOptions provider,
        LocalAgentProviderDescriptor providerDescriptor)
    {
        var options = new ClientOptions
        {
            ApiKey = provider.ApiKey,
        };
        if (provider.BaseUri is not null)
        {
            options.BaseUrl = provider.BaseUri.ToString();
        }

        return new AnthropicClient(options);
    }

    private static IChatClient WrapChatClient(
        IChatClient chatClient,
        LocalAgentProviderDescriptor providerDescriptor,
        bool useStreamingCompatibilityFallback)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(providerDescriptor);

        if (!useStreamingCompatibilityFallback)
        {
            return chatClient;
        }

        LogInfo(
            $"Using Anthropic streaming compatibility fallback backend={providerDescriptor.BackendId.Value} provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName}");
        return new AnthropicStreamingCompatibilityChatClient(chatClient);
    }

    private static bool ShouldUseStreamingCompatibilityFallback(LocalAgentProviderDescriptor providerDescriptor)
    {
        ArgumentNullException.ThrowIfNull(providerDescriptor);

        return HasHost(providerDescriptor.BaseUri, "minimax.io") ||
            HasHost(providerDescriptor.BaseUri, "minimaxi.com");
    }

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
        LocalAgentProviderDescriptor provider,
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
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Info))
        {
            Logger.Info(message);
        }
    }

    private static AgentModelInfo CreateSingleModelInfo(
        string modelId,
        LocalAgentProviderDescriptor providerDescriptor)
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
