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
}
