using CodeAlta.Agent.LocalRuntime;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.GoogleGenAI;

/// <summary>
/// Options for the Google GenAI backend.
/// </summary>
public sealed class GoogleGenAIAgentBackendOptions
{
    /// <summary>
    /// Gets or sets the optional root directory used to persist machine-local agent state.
    /// </summary>
    public string? StateRootPath { get; set; }

    /// <summary>
    /// Gets the configured Google providers.
    /// </summary>
    public IList<GoogleGenAIProviderOptions> Providers { get; } = [];
}

/// <summary>
/// Describes one configured Google GenAI provider.
/// </summary>
public sealed class GoogleGenAIProviderOptions
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
    /// Gets or sets the API key used to authenticate Gemini Developer API requests.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets whether the provider should use Vertex AI instead of the Gemini Developer API.
    /// </summary>
    public bool UseVertexAI { get; set; }

    /// <summary>
    /// Gets or sets the Google Cloud project for Vertex AI.
    /// </summary>
    public string? Project { get; set; }

    /// <summary>
    /// Gets or sets the Google Cloud location for Vertex AI.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// Gets or sets an optional base endpoint override.
    /// </summary>
    public Uri? BaseUri { get; set; }

    /// <summary>
    /// Gets or sets whether this provider is the default registration for the backend.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets the compatibility profile for the provider.
    /// </summary>
    public LocalAgentProviderProfile? Profile { get; set; }

    internal Func<IChatClient>? ChatClientFactory { get; set; }

    internal Func<CancellationToken, Task<IReadOnlyList<AgentModelInfo>>>? ModelListAsync { get; set; }
}
