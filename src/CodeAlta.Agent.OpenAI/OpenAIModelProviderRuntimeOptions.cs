#pragma warning disable OPENAI001

using System.ClientModel;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI.Codex;
using OpenAI.Chat;
using OpenAI.Responses;

namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// Shared options for the OpenAI-backed agent-runtime provider runtimes.
/// </summary>
public abstract class OpenAIModelProviderRuntimeOptions
{
    /// <summary>
    /// Gets or sets the optional provider identifier override.
    /// </summary>
    public ModelProviderId? ProviderIdOverride { get; set; }

    /// <summary>
    /// Gets or sets the optional provider display name override.
    /// </summary>
    public string? DisplayNameOverride { get; set; }

    /// <summary>
    /// Gets or sets the optional root directory used to persist machine-scoped agent state.
    /// </summary>
    public string? StateRootPath { get; set; }

    /// <summary>
    /// Gets or sets the configured provider registrations.
    /// </summary>
    public IList<OpenAIProviderOptions> Providers { get; } = [];
}

/// <summary>
/// Options for the OpenAI Responses model provider runtime.
/// </summary>
public sealed class OpenAIResponsesModelProviderRuntimeOptions : OpenAIModelProviderRuntimeOptions
{
    internal CodexSubscriptionConcurrencyLimiter? CodexSubscriptionConcurrencyLimiter { get; set; }
}

/// <summary>
/// Options for the OpenAI Chat/Completions model provider runtime.
/// </summary>
public sealed class OpenAIChatModelProviderRuntimeOptions : OpenAIModelProviderRuntimeOptions;

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
    /// Gets or sets whether this provider should use the Azure OpenAI SDK client instead of the OpenAI platform SDK client.
    /// </summary>
    public bool IsAzureOpenAI { get; set; }

    /// <summary>
    /// Gets or sets the base endpoint for the provider.
    /// </summary>
    public Uri? BaseUri { get; set; }

    /// <summary>
    /// Gets or sets the optional OpenAI SDK network timeout for HTTP operations.
    /// </summary>
    public TimeSpan? NetworkTimeout { get; set; }

    /// <summary>
    /// Gets or sets the optional OpenAI organization header value.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Gets or sets the optional OpenAI project header value.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Gets or sets whether this provider is the default registration for the provider runtime.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets the compatibility profile for the provider.
    /// </summary>
    public AgentProviderProfile? Profile { get; set; }

    /// <summary>
    /// Gets or sets normalized compaction settings for the provider.
    /// </summary>
    public AgentCompactionSettings? Compaction { get; set; }

    /// <summary>
    /// Gets or sets the models.dev provider identifier used to enrich model metadata.
    /// </summary>
    public string? ModelsDevProviderId { get; set; }

    /// <summary>
    /// Gets or sets the optional fixed model identifier to expose when the provider endpoint serves a single model.
    /// </summary>
    public string? SingleModelId { get; set; }

    /// <summary>
    /// Gets or sets the optional regular expression used to include discovered model ids.
    /// </summary>
    public string? ModelsIncludeRegex { get; set; }

    /// <summary>
    /// Gets or sets provider-specific request-body fields to append to OpenAI-compatible requests.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ExtraBody { get; set; }

    /// <summary>
    /// Gets or sets additional static HTTP headers to include with provider requests.
    /// Authentication headers are owned by the provider runtime and should not be supplied here.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; set; }

    /// <summary>
    /// Gets or sets per-model metadata overrides.
    /// </summary>
    public IReadOnlyDictionary<string, AgentModelOverride>? ModelOverrides { get; set; }

    /// <summary>
    /// Gets or sets per-model request customizations.
    /// </summary>
    public IReadOnlyDictionary<string, AgentModelRequestOverride>? ModelRequestOverrides { get; set; }

    /// <summary>
    /// Gets or sets the shared models.dev catalog service.
    /// </summary>
    public ModelsDevCatalogService? ModelCatalog { get; set; }

    /// <summary>
    /// Gets or sets Codex ChatGPT subscription-specific options when this provider uses ChatGPT OAuth.
    /// </summary>
    public OpenAICodexSubscriptionOptions? CodexSubscription { get; set; }

    /// <summary>
    /// Gets or sets optional low-level protocol tracing settings.
    /// </summary>
    public OpenAIProtocolTraceOptions? ProtocolTracing { get; set; }

    internal Func<string?, ResponsesClient>? ResponsesClientFactory { get; set; }

    internal Func<OpenAIResponsesClientFactoryContext, ResponsesClient>? ResponsesClientContextFactory { get; set; }

    internal Func<OpenAIResponsesWebSocketSessionFactoryContext, ValueTask<IOpenAIResponsesWebSocketSession>>? ResponsesWebSocketSessionFactory { get; set; }

    internal TimeSpan? ResponsesWebSocketIdleTimeout { get; set; }

    internal string? StateRootPath { get; set; }

    internal Action<OpenAIResponsesRequestCustomizationContext>? ResponsesRequestCustomizer { get; set; }

    // Reads the streamed body of an answer on its way to the client: what an endpoint adds to the events beside the
    // protocol, the client does not hand over.
    internal Func<Stream, Stream>? ResponseStreamObserver { get; set; }

    internal Func<string?, ChatClient>? ChatClientFactory { get; set; }

    internal OpenAIRequestHeaderContext? RequestHeaderContext { get; set; }

    internal HttpClient? HttpClient { get; set; }

    internal HttpClient? CodexSubscriptionHttpClient { get; set; }

    internal Func<CancellationToken, ValueTask>? CodexSubscriptionCredentialRefreshAsync { get; set; }

    internal Func<CancellationToken, Task<IReadOnlyList<AgentModelInfo>>>? ModelListAsync { get; set; }
}

/// <summary>
/// Describes low-level OpenAI SDK protocol tracing settings.
/// </summary>
public sealed class OpenAIProtocolTraceOptions
{
    /// <summary>
    /// Gets or sets whether protocol tracing is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the root directory used to write per-session trace files. When unset, the provider state root is used.
    /// </summary>
    public string? StateRootPath { get; set; }

    /// <summary>
    /// Gets or sets the maximum request or buffered response body bytes to write before truncating.
    /// </summary>
    public int MaxBodyBytes { get; set; } = 4 * 1024 * 1024;
}

/// <summary>
/// Describes Codex ChatGPT subscription-specific provider settings.
/// </summary>
public sealed class OpenAICodexSubscriptionOptions
{
    private string? _serviceTier;

    internal bool EnableSequentialCutoffReasoningSummaries { get; set; }

    /// <summary>
    /// Gets or sets the ChatGPT OAuth credential source. Only <c>codealta_oauth</c> is supported;
    /// credentials must come from CodeAlta's Sign in with ChatGPT token-sharing flow.
    /// </summary>
    public string AuthSource { get; set; } = "codealta_oauth";

    /// <summary>
    /// Gets or sets an optional account/workspace header override. This does not select an account;
    /// the saved issued client registration determines the authenticated account and workspace.
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>
    /// Gets or sets the maximum concurrent requests per ChatGPT account.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 16;

    /// <summary>
    /// Gets or sets the configured text verbosity.
    /// </summary>
    public string TextVerbosity { get; set; } = "medium";

    /// <summary>
    /// Gets or sets the provider-wide subscription routing tier. <c>priority</c> (alias <c>fast</c>)
    /// requests fast routing only when the model advertises it; null or <c>default</c> omits the tier.
    /// Includes child sessions and compaction and may increase subscription usage or cost.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not default, priority, fast, null, or whitespace.</exception>
    public string? ServiceTier
    {
        get => _serviceTier;
        set => _serviceTier = value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "default" => "default",
            "priority" or "fast" => "priority",
            _ => throw new ArgumentException("Codex service tier must be default, priority, or fast (alias for priority).", nameof(value)),
        };
    }

    /// <summary>
    /// Gets or sets whether encrypted reasoning continuity should be requested.
    /// </summary>
    public bool IncludeEncryptedReasoning { get; set; } = true;

    /// <summary>
    /// Gets or sets the model discovery mode.
    /// </summary>
    public string ModelDiscovery { get; set; } = "codex_endpoint_with_static_fallback";

    /// <summary>
    /// Gets or sets the Responses transport mode. The default is <c>http</c> (SSE); <c>websocket_with_http_fallback</c> opts into WebSocket transport.
    /// </summary>
    public string ResponseTransport { get; set; } = "http";

    /// <summary>
    /// Gets or sets whether to send the legacy Responses experimental beta header. The default is <see langword="false" />.
    /// </summary>
    public bool SendResponsesBetaHeader { get; set; }

    /// <summary>
    /// Gets or sets whether to include an optional stable telemetry installation id in request metadata.
    /// This is independent of the required host identifier used during ChatGPT authorization.
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
    ModelProviderRuntimeDescriptor Provider,
    CodexTurnState? TurnState = null,
    CodexSubscriptionRequestContext? RequestContext = null);

internal sealed record OpenAIResponsesWebSocketSessionFactoryContext(
    string? ModelId,
    string SessionId,
    ModelProviderRuntimeDescriptor Provider);

internal sealed record OpenAIResponsesRequestCustomizationContext(
    AgentTurnRequest Request,
    CreateResponseOptions Options);

internal sealed record OpenAIResponsesWebSocketSideChannelEvent(
    string Type,
    BinaryData Payload);

internal interface IOpenAIResponsesWebSocketSession : IDisposable
{
    bool HasOpenConnection { get; }

    Action<OpenAIResponsesWebSocketSideChannelEvent>? SideChannelReceived { get; set; }

    AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
        CreateResponseOptions options,
        CreateResponseOptions? reconnectOptions = null,
        CancellationToken cancellationToken = default);

    async IAsyncEnumerable<CodexProtocolEvent> CreateProtocolEventsAsync(
        CreateResponseOptions options,
        CreateResponseOptions? reconnectOptions = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in CreateResponseStreamingAsync(options, reconnectOptions, cancellationToken).ConfigureAwait(false))
        {
            yield return new CodexProtocolEvent(
                CodexProtocolTransport.WebSocket,
                update.GetType().Name,
                update,
                new CodexResponseMetadata());
        }
    }

    IAsyncEnumerable<CodexProtocolEvent> CreateProtocolEventsAsync(
        CreateResponseOptions options,
        CreateResponseOptions? reconnectOptions,
        CodexSubscriptionRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        return CreateProtocolEventsAsync(options, reconnectOptions, cancellationToken);
    }
}
