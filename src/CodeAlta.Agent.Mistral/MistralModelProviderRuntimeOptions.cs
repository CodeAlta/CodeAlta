using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Mistral;

/// <summary>
/// Options for the Mistral chat model provider runtime.
/// </summary>
public sealed class MistralModelProviderRuntimeOptions
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
    /// Gets the configured Mistral providers.
    /// </summary>
    public IList<MistralProviderOptions> Providers { get; } = [];
}

/// <summary>
/// Describes one configured Mistral provider.
/// </summary>
public sealed class MistralProviderOptions
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
    /// Gets or sets what gives the API key before each request, for a key the configuration does not hold, such as
    /// the one a Mistral Vibe sign-in stored; null uses <see cref="ApiKey"/>. It throws when there is no key, with a
    /// message the user reads.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? ApiKeyProvider { get; set; }

    /// <summary>
    /// Gets or sets the base endpoint for the provider.
    /// </summary>
    public Uri? BaseUri { get; set; }

    /// <summary>
    /// Gets or sets the API the turns use: Chat Completions by default, or the Responses API.
    /// </summary>
    public MistralApi Api { get; set; }

    /// <summary>
    /// Gets or sets whether the models are the ones Mistral Vibe offers (<c>/v1/model-catalog?surface=vibe-code</c>),
    /// with their reasoning efforts, instead of every model of the account (<c>/v1/models</c>).
    /// </summary>
    public bool UsesVibeModelCatalog { get; set; }

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
    /// Gets or sets the optional fixed model identifier to expose when remote model discovery is unavailable.
    /// </summary>
    public string? SingleModelId { get; set; }

    /// <summary>
    /// Gets or sets the optional regular expression used to include discovered model ids.
    /// </summary>
    public string? ModelsIncludeRegex { get; set; }

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
    /// Gets or sets the shared models.dev catalog service.
    /// </summary>
    public ModelsDevCatalogService? ModelCatalog { get; set; }

    internal Func<IChatClient>? ChatClientFactory { get; set; }

    internal Func<CancellationToken, Task<IReadOnlyList<AgentModelInfo>>>? ModelListAsync { get; set; }

    internal HttpClient? HttpClient { get; set; }

    internal int? MaxRetryAttempts { get; set; }

    internal Func<TimeSpan, CancellationToken, Task>? RetryDelayAsync { get; set; }

    internal async ValueTask<string?> ResolveApiKeyAsync(CancellationToken cancellationToken)
        => ApiKeyProvider is null ? ApiKey : await ApiKeyProvider(cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// The API of Mistral that a provider uses for its turns.
/// </summary>
public enum MistralApi
{
    /// <summary>The Chat Completions API (<c>/v1/chat/completions</c>).</summary>
    ChatCompletions,

    /// <summary>The Responses API (<c>/v1/responses</c>), stateless: the whole conversation goes with each request.</summary>
    Responses,
}
