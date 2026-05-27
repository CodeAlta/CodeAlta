using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.GoogleGenAI;

/// <summary>
/// Google GenAI model-provider runtime.
/// </summary>
public sealed class GoogleGenAIModelProviderRuntime : IAgentModelProviderRuntime
{
    private readonly IAgentModelProviderRuntime _runtime;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleGenAIModelProviderRuntime"/> class.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    public GoogleGenAIModelProviderRuntime(GoogleGenAIModelProviderRuntimeOptions options)
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

    private static AgentModelProviderRuntime CreateProviderRuntime(GoogleGenAIProviderOptions provider)
    {
        var providerKey = provider.ProviderKey.Trim();
        var displayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? providerKey : provider.DisplayName.Trim();
        var runtimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = provider.UseVertexAI ? "vertex-ai" : "google-genai",
            ProviderKey = providerKey,
            DisplayName = displayName,
            TransportKind = provider.UseVertexAI ? AgentTransportKind.GoogleVertexAI : AgentTransportKind.GoogleGeminiApi,
            BaseUri = provider.BaseUri,
            IsDefault = provider.IsDefault,
            Profile = provider.Profile ?? new AgentProviderProfile
            {
                SupportsDeveloperRole = false,
                SupportsReasoningEffort = true,
                StreamsUsage = true,
                SupportsThoughtSignatures = true,
            },
            Compaction = provider.Compaction ?? AgentCompactionSettings.Default,
        };
        var descriptor = new ModelProviderDescriptor(new ModelProviderId(providerKey), displayName, runtimeDescriptor.ProtocolFamily)
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

    private static IModelProviderTurnExecutor CreateTurnExecutor(GoogleGenAIProviderOptions provider)
    {
        return new ChatClientTurnExecutor(
            (providerDescriptor, cancellationToken) => CreateChatClientAsync(provider, providerDescriptor, cancellationToken),
            (providerDescriptor, cancellationToken) => ListModelsAsync(provider, providerDescriptor, cancellationToken));
    }

    private static ValueTask<IChatClient> CreateChatClientAsync(
        GoogleGenAIProviderOptions provider,
        ModelProviderRuntimeDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (provider.ChatClientFactory is not null)
        {
            return ValueTask.FromResult(provider.ChatClientFactory());
        }

        var client = CreateSdkClient(provider);
        return ValueTask.FromResult<IChatClient>(new OwnedChatClient(client.AsIChatClient(), client));
    }

    private static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        GoogleGenAIProviderOptions provider,
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

        using var client = CreateSdkClient(provider);
        var pager = await client.Models.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<AgentModelInfo>();
        await foreach (var model in pager.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ToAgentModelInfo(providerDescriptor, model));
        }

        models = results;
        return AgentModelMetadataEnricher.EnrichModels(
            models,
            provider.ModelCatalog,
            provider.ModelsDevProviderId,
            provider.ModelOverrides);
    }

    private static Client CreateSdkClient(GoogleGenAIProviderOptions provider)
    {
        var httpOptions = provider.BaseUri is not null || provider.ExtraHeaders is { Count: > 0 }
            ? new HttpOptions
            {
                BaseUrl = provider.BaseUri?.ToString(),
                Headers = provider.ExtraHeaders is null
                    ? null
                    : new Dictionary<string, string>(provider.ExtraHeaders, StringComparer.OrdinalIgnoreCase),
            }
            : null;
        return new Client(
            vertexAI: provider.UseVertexAI,
            apiKey: provider.ApiKey,
            project: provider.Project,
            location: provider.Location,
            httpOptions: httpOptions);
    }

    private static AgentModelInfo ToAgentModelInfo(
        ModelProviderRuntimeDescriptor provider,
        Model model)
    {
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["inputTokenLimit"] = model.InputTokenLimit,
            ["outputTokenLimit"] = model.OutputTokenLimit,
            ["thinking"] = model.Thinking,
            ["supportedActions"] = model.SupportedActions?.ToArray(),
        };

        return new AgentModelInfo(
            model.Name ?? string.Empty,
            DisplayName: model.DisplayName,
            Description: model.Description,
            Provider: provider.ProviderKey,
            Capabilities: capabilities);
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
