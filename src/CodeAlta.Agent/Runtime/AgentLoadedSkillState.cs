namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Represents one skill that has been activated for a agent runtime session.
/// </summary>
public sealed record AgentLoadedSkillState
{
    /// <summary>
    /// Gets or initializes the skill name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or initializes the absolute <c>SKILL.md</c> path when known.
    /// </summary>
    public required string SkillFilePath { get; init; }

    /// <summary>
    /// Gets or initializes the absolute skill root directory when known.
    /// </summary>
    public required string SkillRootPath { get; init; }

    /// <summary>
    /// Gets or initializes the source kind label.
    /// </summary>
    public string? SourceKind { get; init; }

    /// <summary>
    /// Gets or initializes the opaque source identifier when known.
    /// </summary>
    public string? SourceId { get; init; }

    /// <summary>
    /// Gets or initializes when the skill was activated.
    /// </summary>
    public required DateTimeOffset ActivatedAt { get; init; }

    /// <summary>
    /// Gets or initializes the activation mode (for example <c>user</c>, <c>model</c>, or <c>host</c>).
    /// </summary>
    public required string ActivationMode { get; init; }

    /// <summary>
    /// Gets or initializes the stable activation identifier.
    /// </summary>
    public required string ActivationId { get; init; }

    /// <summary>
    /// Gets or initializes the canonical payload injected into the conversation.
    /// </summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Gets or initializes the base directory URI when known.
    /// </summary>
    public string? BaseDirectoryUri { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether the skill still exists on disk.
    /// </summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>
    /// Gets or initializes the recoverable missing-on-restore diagnostic when the skill is unavailable.
    /// </summary>
    public string? MissingReason { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether the runtime reconstructed this skill from persisted history.
    /// </summary>
    public bool RestoredFromHistory { get; init; }

    /// <summary>
    /// Gets or initializes the key of the MCP server that published the skill, for a skill of an MCP server
    /// (<see cref="SourceKind"/> <c>mcp</c>); null for a skill of this computer.
    /// </summary>
    public string? McpServer { get; init; }

    /// <summary>Gets or initializes the URI of the <c>SKILL.md</c> of a skill of an MCP server on its server; null otherwise.</summary>
    public string? McpUri { get; init; }

    /// <summary>The source kind of a skill an MCP server published.</summary>
    public const string McpSourceKind = "mcp";

    /// <summary>Gets whether the skill was published by an MCP server: it has no file on this computer.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMcp => string.Equals(SourceKind, McpSourceKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets what tells the skill apart from the other skills a session loaded: its name for a skill of this computer, its
    /// server and URI for a skill of an MCP server, which never takes the place of a skill of another origin.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => IsMcp ? $"mcp:{McpServer}\n{McpUri ?? Name}" : Name;
}
