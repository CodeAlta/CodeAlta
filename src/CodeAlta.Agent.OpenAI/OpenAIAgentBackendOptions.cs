#pragma warning disable OPENAI001

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
    /// Gets or sets per-model metadata overrides.
    /// </summary>
    public IReadOnlyDictionary<string, AgentModelOverride>? ModelOverrides { get; set; }

    /// <summary>
    /// Gets or sets the shared models.dev catalog service.
    /// </summary>
    public ModelsDevCatalogService? ModelCatalog { get; set; }

    internal Func<string?, ResponsesClient>? ResponsesClientFactory { get; set; }

    internal Func<string?, ChatClient>? ChatClientFactory { get; set; }

    internal Func<CancellationToken, Task<IReadOnlyList<AgentModelInfo>>>? ModelListAsync { get; set; }
}
