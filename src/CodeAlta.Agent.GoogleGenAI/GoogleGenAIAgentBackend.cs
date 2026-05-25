using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.GoogleGenAI;

/// <summary>
/// Local-runtime backend for Google GenAI providers.
/// </summary>
public sealed class GoogleGenAIAgentBackend : IAgentBackend, IAgentSharedSessionMetadataBackend
{
    private readonly IAgentBackend _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleGenAIAgentBackend"/> class.
    /// </summary>
    /// <param name="options">The backend options.</param>
    public GoogleGenAIAgentBackend(GoogleGenAIAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Providers.Count == 0)
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
        }

        _inner = new LocalAgentBackend(
            options.BackendIdOverride ?? AgentBackendIds.GoogleGenAI,
            string.IsNullOrWhiteSpace(options.DisplayNameOverride) ? "Google GenAI" : options.DisplayNameOverride.Trim(),
            new LocalAgentBackendOptions
            {
                StateRootPath = options.StateRootPath,
                Providers =
                [
                    .. options.Providers.Select(provider => new LocalAgentBackendProviderRegistration
                    {
                        Provider = new LocalAgentProviderDescriptor
                        {
                            ProtocolFamily = "google-genai",
                            ProviderKey = provider.ProviderKey.Trim(),
                            DisplayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.ProviderKey.Trim() : provider.DisplayName.Trim(),
                            BackendId = options.BackendIdOverride ?? AgentBackendIds.GoogleGenAI,
                            TransportKind = provider.UseVertexAI ? LocalAgentTransportKind.GoogleVertexAI : LocalAgentTransportKind.GoogleGeminiApi,
                            BaseUri = provider.BaseUri,
                            IsDefault = provider.IsDefault,
                            Profile = provider.Profile ?? new LocalAgentProviderProfile
                            {
                                SupportsDeveloperRole = false,
                                SupportsReasoningEffort = true,
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
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private static ILocalAgentTurnExecutor CreateTurnExecutor(GoogleGenAIProviderOptions provider)
    {
        return new LocalAgentChatClientTurnExecutor(
            (providerDescriptor, cancellationToken) => CreateChatClientAsync(provider, providerDescriptor, cancellationToken),
            (providerDescriptor, cancellationToken) => ListModelsAsync(provider, providerDescriptor, cancellationToken));
    }

    private static ValueTask<IChatClient> CreateChatClientAsync(
        GoogleGenAIProviderOptions provider,
        LocalAgentProviderDescriptor providerDescriptor,
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
        LocalAgentProviderDescriptor provider,
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
