using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.LocalRuntime.Compaction;

namespace CodeAlta.Agent.Copilot;

/// <summary>
/// Options used to create a GitHub Copilot direct local-runtime backend.
/// </summary>
public sealed class CopilotDirectAgentBackendOptions
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
    /// Gets the configured Copilot direct provider registrations.
    /// </summary>
    public IList<CopilotDirectProviderOptions> Providers { get; } = [];
}

/// <summary>
/// Describes one configured GitHub Copilot direct provider.
/// </summary>
public sealed class CopilotDirectProviderOptions
{
    /// <summary>
    /// Gets or sets the stable provider key used for local storage and configuration.
    /// </summary>
    public required string ProviderKey { get; set; }

    /// <summary>
    /// Gets or sets the user-facing provider display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets an optional explicit Copilot API base endpoint override.
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

    /// <summary>
    /// Gets or sets normalized compaction settings for the provider.
    /// </summary>
    public LocalAgentCompactionSettings? Compaction { get; set; }

    /// <summary>
    /// Gets or sets Copilot authentication options.
    /// </summary>
    public CopilotDirectAuthOptions Auth { get; set; } = new();

    /// <summary>
    /// Gets or sets the model discovery mode.
    /// </summary>
    public string ModelDiscovery { get; set; } = CopilotDirectModelDiscoveryModes.EndpointWithStaticFallback;

    /// <summary>
    /// Gets or sets whether disabled/pending model policies may be enabled on demand.
    /// </summary>
    public bool EnableModelPolicies { get; set; }

    /// <summary>
    /// Gets or sets whether preview models should be included when the Copilot catalog exposes them.
    /// </summary>
    public bool IncludePreviewModels { get; set; }

    /// <summary>
    /// Gets or sets an optional fixed model identifier to expose when discovery is disabled.
    /// </summary>
    public string? SingleModelId { get; set; }

    /// <summary>
    /// Gets or sets per-model metadata overrides.
    /// </summary>
    public IReadOnlyDictionary<string, AgentModelOverride>? ModelOverrides { get; set; }

    /// <summary>
    /// Gets or sets the optional root directory used to persist provider state.
    /// </summary>
    public string? StateRootPath { get; set; }

    /// <summary>
    /// Gets or sets the timeout used for model discovery HTTP requests.
    /// </summary>
    public TimeSpan ModelDiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets whether Copilot HTTP protocol tracing is enabled.
    /// </summary>
    public bool ProtocolTraceEnabled { get; set; }

    /// <summary>
    /// Gets or sets an optional HTTP client override for tests.
    /// </summary>
    public HttpClient? HttpClient { get; set; }
}

/// <summary>
/// Authentication options for GitHub Copilot direct access.
/// </summary>
public sealed class CopilotDirectAuthOptions
{
    /// <summary>
    /// Gets or sets the configured credential source.
    /// </summary>
    public string AuthSource { get; set; } = CopilotDirectAuthSources.GitHubDeviceFlow;

    /// <summary>
    /// Gets or sets the optional GitHub Enterprise domain.
    /// </summary>
    public string? EnterpriseDomain { get; set; }

    /// <summary>
    /// Gets or sets the environment variable containing a GitHub OAuth token.
    /// </summary>
    public string? GitHubTokenEnvironmentVariable { get; set; }

    /// <summary>
    /// Gets or sets the environment variable containing a pre-exchanged Copilot API token.
    /// </summary>
    public string? CopilotTokenEnvironmentVariable { get; set; }

    /// <summary>
    /// Gets or sets an optional OAuth device-flow client identifier.
    /// </summary>
    public string? DeviceFlowClientId { get; set; }

    /// <summary>
    /// Gets or sets the refresh skew applied to expiring Copilot tokens.
    /// </summary>
    public TimeSpan TokenRefreshSkew { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Supported Copilot direct auth sources.
/// </summary>
public static class CopilotDirectAuthSources
{
    /// <summary>
    /// CodeAlta-managed GitHub OAuth device flow.
    /// </summary>
    public const string GitHubDeviceFlow = "github_device_flow";

    /// <summary>
    /// GitHub OAuth token read from an environment variable, exchanged for a Copilot API token.
    /// </summary>
    public const string GitHubTokenEnvironment = "github_token_env";

    /// <summary>
    /// Pre-exchanged Copilot token read from an environment variable.
    /// </summary>
    public const string CopilotTokenEnvironment = "copilot_token_env";
}

/// <summary>
/// Supported Copilot direct model discovery modes.
/// </summary>
public static class CopilotDirectModelDiscoveryModes
{
    /// <summary>
    /// Use Copilot /models and fall back to a small static catalog if discovery fails.
    /// </summary>
    public const string EndpointWithStaticFallback = "copilot_endpoint_with_static_fallback";

    /// <summary>
    /// Use only Copilot /models.
    /// </summary>
    public const string Endpoint = "copilot_endpoint";

    /// <summary>
    /// Use only the static fallback catalog.
    /// </summary>
    public const string Static = "static";
}
