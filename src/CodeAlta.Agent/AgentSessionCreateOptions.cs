namespace CodeAlta.Agent;

/// <summary>
/// Options for creating an agent session.
/// </summary>
public class AgentSessionCreateOptions
{
    /// <summary>
    /// Gets or initializes the canonical CodeAlta thread identifier requested for this session.
    /// </summary>
    public string? ThreadId { get; init; }

    /// <summary>
    /// Gets or initializes the user-facing session title when known at creation time.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets or initializes the configured provider key.
    /// </summary>
    public string? ProviderKey { get; init; }

    /// <summary>
    /// Gets or initializes the model identifier.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or initializes the working directory for the session.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or initializes the project roots used for local instruction overlays.
    /// </summary>
    public IReadOnlyList<string> ProjectRoots { get; init; } = [];

    /// <summary>
    /// Gets or initializes whether streaming is enabled.
    /// </summary>
    public bool Streaming { get; init; }

    /// <summary>
    /// Gets or initializes the reasoning effort.
    /// </summary>
    public AgentReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>
    /// Gets or initializes the system message (system prompt).
    /// </summary>
    public string? SystemMessage { get; init; }

    /// <summary>
    /// Gets or initializes the developer instructions.
    /// </summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>
    /// Gets or initializes custom tools available to this session.
    /// </summary>
    public IReadOnlyList<AgentToolDefinition>? Tools { get; init; }

    /// <summary>
    /// Gets or initializes MCP server configurations available to this session.
    /// </summary>
    public IReadOnlyDictionary<string, AgentMcpServerConfig>? McpServers { get; init; }

    /// <summary>
    /// Gets or initializes the permission request handler (required; deny-by-default if you provide a handler that denies).
    /// </summary>
    public required AgentPermissionRequestHandler OnPermissionRequest { get; init; }

    /// <summary>
    /// Gets or initializes the user input request handler (optional).
    /// </summary>
    public AgentUserInputRequestHandler? OnUserInputRequest { get; init; }
}
