using System.Net.Http;

namespace CodeAlta.Agent.LocalRuntime.Tools;

/// <summary>
/// Configures the default built-in tools for a local raw-API session.
/// </summary>
public sealed class LocalAgentBuiltInToolOptions
{
    /// <summary>
    /// Gets or initializes the backend identifier.
    /// </summary>
    public required AgentBackendId BackendId { get; init; }

    /// <summary>
    /// Gets or initializes the session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets or initializes the default working directory used to resolve relative paths.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or initializes the permission request handler.
    /// </summary>
    public required AgentPermissionRequestHandler OnPermissionRequest { get; init; }

    /// <summary>
    /// Gets or initializes the optional user-input request handler.
    /// </summary>
    public AgentUserInputRequestHandler? OnUserInputRequest { get; init; }

    /// <summary>
    /// Gets or initializes the shared HTTP client for web retrieval.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>
    /// Gets or initializes the provider descriptor used to tailor the built-in tool set.
    /// </summary>
    public ModelProviderRuntimeDescriptor? Provider { get; init; }

    /// <summary>
    /// Gets or initializes the default maximum number of lines returned by <c>read_file</c>.
    /// </summary>
    public int DefaultReadFileLineLimit { get; init; } = 2000;

    /// <summary>
    /// Gets or initializes the maximum number of lines returned by <c>read_file</c>.
    /// </summary>
    public int MaxReadFileLineLimit { get; init; } = 5000;

    /// <summary>
    /// Gets or initializes the maximum number of grep matches returned.
    /// </summary>
    public int MaxGrepMatches { get; init; } = 200;

    /// <summary>
    /// Gets or initializes the maximum payload size fetched by <c>webget</c>.
    /// </summary>
    public int MaxWebGetBytes { get; init; } = 1_000_000;

    /// <summary>
    /// Gets or initializes the default timeout used by <c>webget</c>.
    /// </summary>
    public TimeSpan WebGetTimeout { get; init; } = TimeSpan.FromSeconds(20);
}
