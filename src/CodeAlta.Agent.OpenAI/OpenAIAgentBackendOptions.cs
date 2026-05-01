#pragma warning disable OPENAI001

using System.ClientModel;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using OpenAI.Chat;
using OpenAI.Responses;

namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// Shared options for the OpenAI-backed local-runtime backends.
/// </summary>
public abstract class OpenAIAgentBackendOptions
{
    /// <summary>
    /// Gets or sets the optional backend identifier override.
    /// </summary>
    public AgentBackendId? BackendIdOverride { get; set; }

    /// <summary>
    /// Gets or sets the optional backend display name override.
    /// </summary>
    public string? DisplayNameOverride { get; set; }

    /// <summary>
    /// Gets or sets the optional root directory used to persist machine-local agent state.
    /// </summary>
    public string? StateRootPath { get; set; }

    /// <summary>
    /// Gets or sets the configured provider registrations.
    /// </summary>
    public IList<OpenAIProviderOptions> Providers { get; } = [];
}

/// <summary>
/// Options for the OpenAI Responses backend.
/// </summary>
public sealed class OpenAIResponsesAgentBackendOptions : OpenAIAgentBackendOptions;

/// <summary>
/// Options for the OpenAI Chat/Completions backend.
/// </summary>
public sealed class OpenAIChatAgentBackendOptions : OpenAIAgentBackendOptions;

/// <summary>
/// Describes one configured OpenAI-compatible provider.
/// </summary>
public sealed class OpenAIProviderOptions
{
    /// <summary>
    /// Gets or sets the stable provider key used for local storage and configuration.
    /// </summary>
    public required string ProviderKey { get; set; }

    /// <summary>
    /// Gets or sets the provider display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the API key used to authenticate requests.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the base endpoint for the provider.
    /// </summary>
    public Uri? BaseUri { get; set; }

    /// <summary>
    /// Gets or sets the optional OpenAI organization header value.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Gets or sets the optional OpenAI project header value.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Gets or sets whether this provider is the default registration for the backend.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets the compatibility profile for the provider.
    /// </summary>
    public LocalAgentProviderProfile? Profile { get; set; }

    /// <summary>
    /// Gets or sets normalized compaction settings for the provider.
    /// </summary>
    public LocalAgentCompactionSettings? Compaction { get; set; }

    /// <summary>
    /// Gets or sets the models.dev provider identifier used to enrich model metadata.
    /// </summary>
    public string? ModelsDevProviderId { get; set; }

    /// <summary>
    /// Gets or sets the optional fixed model identifier to expose when the provider endpoint serves a single model.
    /// </summary>
    public string? SingleModelId { get; set; }

    /// <summary>
    /// Gets or sets provider-specific request-body fields to append to OpenAI-compatible requests.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ExtraBody { get; set; }

    /// <summary>
    /// Gets or sets per-model metadata overrides.
    /// </summary>
    public IReadOnlyDictionary<string, AgentModelOverride>? ModelOverrides { get; set; }

    /// <summary>
    /// Gets or sets the shared models.dev catalog service.
    /// </summary>
    public ModelsDevCatalogService? ModelCatalog { get; set; }

    /// <summary>
    /// Gets or sets Codex ChatGPT subscription-specific options when this provider uses ChatGPT OAuth.
    /// </summary>
    public OpenAICodexSubscriptionOptions? CodexSubscription { get; set; }

    internal Func<string?, ResponsesClient>? ResponsesClientFactory { get; set; }

    internal Func<OpenAIResponsesClientFactoryContext, ResponsesClient>? ResponsesClientContextFactory { get; set; }

    internal Func<OpenAIResponsesWebSocketSessionFactoryContext, ValueTask<IOpenAIResponsesWebSocketSession>>? ResponsesWebSocketSessionFactory { get; set; }

    internal TimeSpan? ResponsesWebSocketIdleTimeout { get; set; }

    internal string? StateRootPath { get; set; }

    internal Action<OpenAIResponsesRequestCustomizationContext>? ResponsesRequestCustomizer { get; set; }

    internal Func<string?, ChatClient>? ChatClientFactory { get; set; }

    internal HttpClient? CodexSubscriptionHttpClient { get; set; }

    internal Func<CancellationToken, ValueTask>? CodexSubscriptionCredentialRefreshAsync { get; set; }

    internal Func<CancellationToken, Task<IReadOnlyList<AgentModelInfo>>>? ModelListAsync { get; set; }
}

/// <summary>
/// Describes Codex ChatGPT subscription-specific provider settings.
/// </summary>
public sealed class OpenAICodexSubscriptionOptions
{
    /// <summary>
    /// Gets or sets the ChatGPT/Codex OAuth credential source.
    /// </summary>
    public string AuthSource { get; set; } = "codealta_oauth";

    /// <summary>
    /// Gets or sets an explicit ChatGPT account or workspace identifier.
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>
    /// Gets or sets the maximum concurrent requests per ChatGPT account.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 1;

    /// <summary>
    /// Gets or sets the configured text verbosity.
    /// </summary>
    public string TextVerbosity { get; set; } = "medium";

    /// <summary>
    /// Gets or sets whether encrypted reasoning continuity should be requested.
    /// </summary>
    public bool IncludeEncryptedReasoning { get; set; } = true;

    /// <summary>
    /// Gets or sets the model discovery mode.
    /// </summary>
    public string ModelDiscovery { get; set; } = "codex_endpoint_with_static_fallback";

    /// <summary>
    /// Gets or sets the Responses transport mode. Use <c>websocket_with_http_fallback</c> for the default WebSocket path or <c>http</c> to force SSE.
    /// </summary>
    public string ResponseTransport { get; set; } = "websocket_with_http_fallback";

    /// <summary>
    /// Gets or sets whether to send the Responses experimental beta header.
    /// </summary>
    public bool SendResponsesBetaHeader { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include a stable installation id in request metadata.
    /// </summary>
    public bool SendInstallationId { get; set; }

    /// <summary>
    /// Gets or sets the installation id source used when installation metadata is enabled.
    /// </summary>
    public string InstallationIdSource { get; set; } = "codealta_state";

    /// <summary>
    /// Gets or sets whether the provider was explicitly opted into as experimental.
    /// </summary>
    public bool Experimental { get; set; }
}

internal sealed record OpenAIResponsesClientFactoryContext(
    string? ModelId,
    string SessionId,
    AgentRunId RunId,
    LocalAgentProviderDescriptor Provider);

internal sealed record OpenAIResponsesWebSocketSessionFactoryContext(
    string? ModelId,
    string SessionId,
    AgentRunId RunId,
    LocalAgentProviderDescriptor Provider);

internal sealed record OpenAIResponsesRequestCustomizationContext(
    LocalAgentTurnRequest Request,
    CreateResponseOptions Options);

internal interface IOpenAIResponsesWebSocketSession : IDisposable
{
    AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
        CreateResponseOptions options,
        CreateResponseOptions? reconnectOptions = null,
        CancellationToken cancellationToken = default);
}
