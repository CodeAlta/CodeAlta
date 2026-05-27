using Anthropic;
using Anthropic.Core;
using Anthropic.Credentials;
using Anthropic.Models.Models;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using Microsoft.Extensions.AI;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Anthropic Messages model-provider runtime.
/// </summary>
public sealed class AnthropicModelProviderRuntime : IAgentModelProviderRuntime
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.Anthropic");
    private readonly IAgentModelProviderRuntime _runtime;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnthropicModelProviderRuntime"/> class.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    public AnthropicModelProviderRuntime(AnthropicModelProviderRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Providers.Count == 0)
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
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

    private static AgentModelProviderRuntime CreateProviderRuntime(AnthropicProviderOptions provider)
    {
        var providerKey = provider.ProviderKey.Trim();
        var displayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? providerKey : provider.DisplayName.Trim();
        var runtimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "anthropic-messages",
            ProviderKey = providerKey,
            DisplayName = displayName,
            TransportKind = AgentTransportKind.AnthropicMessages,
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            Profile = provider.Profile ?? new AgentProviderProfile
            {
                SupportsDeveloperRole = false,
                StreamsUsage = true,
                SupportsThoughtSignatures = true,
            },
            Compaction = provider.Compaction ?? AgentCompactionSettings.Default,
        };
        var descriptor = new ModelProviderDescriptor(new ModelProviderId(providerKey), displayName, "anthropic")
        {
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            DefaultModelId = provider.SingleModelId,
        };
        return new AgentModelProviderRuntime(
            descriptor,
            runtimeDescriptor,
            CreateTurnExecutor(provider));
    }

    internal static IModelProviderTurnExecutor CreateTurnExecutor(AnthropicProviderOptions provider)
    {
        return new ChatClientTurnExecutor(
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
            $"Using Anthropic streaming compatibility fallback provider={providerDescriptor.ProviderKey} displayName={providerDescriptor.DisplayName}");
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
