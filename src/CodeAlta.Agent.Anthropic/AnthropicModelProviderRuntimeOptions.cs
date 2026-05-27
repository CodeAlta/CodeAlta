using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.ModelCatalog;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Options for the Anthropic Messages model provider runtime.
/// </summary>
public sealed class AnthropicModelProviderRuntimeOptions
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
    /// Gets or sets the optional root directory used to persist machine-local agent state.
    /// </summary>
    public string? StateRootPath { get; set; }

    /// <summary>
    /// Gets the configured Anthropic providers.
    /// </summary>
    public IList<AnthropicProviderOptions> Providers { get; } = [];
}

/// <summary>
/// Describes one configured Anthropic provider.
/// </summary>
public sealed class AnthropicProviderOptions
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

    internal string? AuthToken { get; set; }

    internal HttpClient? HttpClient { get; set; }
}
