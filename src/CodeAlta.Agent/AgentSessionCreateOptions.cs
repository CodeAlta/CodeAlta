namespace CodeAlta.Agent;

/// <summary>
/// Options for creating an agent session.
/// </summary>
public class AgentSessionCreateOptions
{
    /// <summary>
    /// Gets or initializes the canonical CodeAlta session identifier requested for this session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets or initializes the optional durable parent session identifier used for lineage and coordination metadata only.
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>
    /// Gets or initializes the session that directly created this session, when different from <see cref="ParentSessionId" />.
    /// </summary>
    public string? CreatedBySessionId { get; init; }

    /// <summary>
    /// Gets or initializes the run that created this session, when known.
    /// </summary>
    public AgentRunId? CreatedByRunId { get; init; }

    /// <summary>
    /// Gets or initializes the creation time recorded for a new session. A host that has already recorded
    /// one, such as the header of the session's journal, passes it so both name the same instant; otherwise
    /// the session is stamped when it starts.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }

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
    /// Gets or initializes the git worktree the session works in: tools run there and paths are resolved from
    /// there, while <see cref="WorkingDirectory"/> stays the folder the session belongs to. When a session is
    /// resumed, null keeps the worktree it has; <see cref="LeaveWorktree"/> takes it out of one.
    /// </summary>
    public string? WorktreeDirectory { get; init; }

    /// <summary>
    /// Gets or initializes whether a resumed session leaves its worktree: it works in
    /// <see cref="WorkingDirectory"/> again, and the worktree is no longer recorded with it.
    /// </summary>
    public bool LeaveWorktree { get; init; }

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
    /// Gets or initializes the permission mode of the session, one of the
    /// <see cref="Runtime.AgentProviderProfile.PermissionModes"/> of its provider. Null leaves the mode the provider
    /// is configured with.
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// Gets or initializes the system message (system prompt).
    /// </summary>
    public string? SystemMessage { get; init; }

    /// <summary>
    /// Gets or initializes the developer instructions.
    /// </summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>
    /// Gets or initializes whether the supplied system/developer instructions are already fully composed.
    /// </summary>
    /// <remarks>
    /// When set, the agent runtime does not add fallback runtime, project, or active-skill instruction sections.
    /// </remarks>
    public bool InstructionsAlreadyComposed { get; init; }

    /// <summary>
    /// Gets or initializes audit-safe metadata describing trusted plugin instruction transformations.
    /// </summary>
    public IReadOnlyList<AgentInstructionTransformationInfo> InstructionTransformations { get; init; } = [];

    /// <summary>
    /// Gets or initializes the agent prompt identifier used to compose the developer instructions.
    /// </summary>
    public string? AgentPromptId { get; init; }

    /// <summary>
    /// Gets or initializes agent prompt selection details used for prompt audit events.
    /// </summary>
    public AgentPromptUsageInfo? AgentPromptUsage { get; init; }

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

/// <summary>
/// Audit-safe metadata describing a trusted plugin transformation of final instructions.
/// </summary>
public sealed record AgentInstructionTransformationInfo
{
    /// <summary>Gets the plugin runtime key.</summary>
    public required string PluginRuntimeKey { get; init; }

    /// <summary>Gets the runtime contribution key.</summary>
    public required string RuntimeContributionKey { get; init; }

    /// <summary>Gets the natural contribution name, when known.</summary>
    public string? NaturalName { get; init; }

    /// <summary>Gets the processor order.</summary>
    public int Order { get; init; }

    /// <summary>Gets the processing stage label.</summary>
    public required string Stage { get; init; }

    /// <summary>Gets the result disposition label.</summary>
    public required string Disposition { get; init; }

    /// <summary>Gets changed instruction channels.</summary>
    public IReadOnlyList<string> ChangedChannels { get; init; } = [];

    /// <summary>Gets an audit-safe change summary.</summary>
    public string? ChangeSummary { get; init; }

    /// <summary>Gets the post-transform instruction hash.</summary>
    public string? ResultInstructionHash { get; init; }

    /// <summary>Gets audit-safe plugin-owned metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
