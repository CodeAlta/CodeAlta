using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;
using CodeAlta.Agent.ModelCatalog;

namespace CodeAlta.Agent.Xai;

/// <summary>
/// Options used to create an xAI direct local-runtime backend.
/// </summary>
public sealed class XaiAgentBackendOptions
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
    /// Gets the configured xAI direct provider registrations.
    /// </summary>
    public IList<XaiProviderOptions> Providers { get; } = [];
}

/// <summary>
/// Describes one configured xAI direct provider.
/// </summary>
public sealed class XaiProviderOptions
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
    /// Gets or sets an optional explicit xAI API base endpoint override.
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
    /// Gets or sets xAI authentication options.
    /// </summary>
    public XaiAuthOptions Auth { get; set; } = new();

    /// <summary>
    /// Gets or sets the model discovery mode.
    /// </summary>
    public string ModelDiscovery { get; set; } = XaiModelDiscoveryModes.EndpointWithStaticFallback;

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
    /// Gets or sets whether xAI HTTP protocol tracing is enabled.
    /// </summary>
    public bool ProtocolTraceEnabled { get; set; }

    /// <summary>
    /// Gets or sets an optional HTTP client override for tests.
    /// </summary>
    public HttpClient? HttpClient { get; set; }
}

/// <summary>
/// Authentication options for xAI direct access.
/// </summary>
public sealed class XaiAuthOptions
{
    /// <summary>
    /// Gets or sets the configured credential source.
    /// </summary>
    public string AuthSource { get; set; } = XaiAuthSources.XaiBrowserOAuth;

    /// <summary>
    /// Gets or sets the refresh skew applied to expiring xAI access tokens.
    /// </summary>
    public TimeSpan TokenRefreshSkew { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Supported xAI direct auth sources.
/// </summary>
public static class XaiAuthSources
{
    /// <summary>
    /// CodeAlta-managed xAI browser PKCE OAuth flow against the public Grok-CLI client id.
    /// </summary>
    public const string XaiBrowserOAuth = "xai_browser_oauth";

    /// <summary>
    /// CodeAlta-managed xAI device-code OAuth flow against the public Grok-CLI client id.
    /// </summary>
    public const string XaiDeviceFlow = "xai_device_flow";
}

/// <summary>
/// Supported xAI direct model discovery modes.
/// </summary>
public static class XaiModelDiscoveryModes
{
    /// <summary>
    /// Use the xAI /models endpoint and fall back to a small static catalog if discovery fails.
    /// </summary>
    public const string EndpointWithStaticFallback = "xai_endpoint_with_static_fallback";

    /// <summary>
    /// Use only the xAI /models endpoint.
    /// </summary>
    public const string Endpoint = "xai_endpoint";

    /// <summary>
    /// Use only the static fallback catalog.
    /// </summary>
    public const string Static = "static";
}
