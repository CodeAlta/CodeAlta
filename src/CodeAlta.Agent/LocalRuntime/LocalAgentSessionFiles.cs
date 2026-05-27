using System.Text.Json;

namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Represents the persisted provider-agnostic session summary stored in the local session journal.
/// </summary>
public sealed record LocalAgentSessionSummary
{
    /// <summary>
    /// Gets or initializes the local session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets or initializes the legacy backend identifier. New code treats this as persisted compatibility data.
    /// </summary>
    public AgentBackendId BackendId { get; init; }

    /// <summary>
    /// Gets or initializes the last-used protocol family.
    /// </summary>
    public string ProtocolFamily { get; init; } = string.Empty;

    /// <summary>
    /// Gets or initializes the last-used provider key.
    /// </summary>
    public string ProviderKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets or initializes the last-used model identifier.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Gets or initializes the working directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or initializes the user-facing title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets or initializes the latest preview text.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets or initializes the parent session identifier used for lineage/orchestration metadata only.
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>
    /// Gets or initializes the session that created this session, when known.
    /// </summary>
    public string? CreatedBySessionId { get; init; }

    /// <summary>
    /// Gets or initializes the run that created this session, when known.
    /// </summary>
    public AgentRunId? CreatedByRunId { get; init; }

    /// <summary>
    /// Gets or initializes the creation timestamp.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Gets or initializes the update timestamp.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Gets or initializes the latest usage snapshot.
    /// </summary>
    public AgentSessionUsage? Usage { get; init; }

    /// <summary>
    /// Gets or initializes additional summary metadata.
    /// </summary>
    public JsonElement? Metadata { get; init; }
}

/// <summary>
/// Represents the persisted provider-agnostic session state stored in the local session journal.
/// </summary>
public sealed record LocalAgentSessionState
{
    /// <summary>
    /// Gets or initializes the local session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets or initializes the last-used protocol family.
    /// </summary>
    public string ProtocolFamily { get; init; } = string.Empty;

    /// <summary>
    /// Gets or initializes the last-used provider key.
    /// </summary>
    public string ProviderKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets or initializes the provider-native session or response identifier for the last-used provider.
    /// </summary>
    public string? ProviderSessionId { get; init; }

    /// <summary>
    /// Gets or initializes the canonical event offset for the most recent compaction.
    /// </summary>
    public long? CompactionEventOffset { get; init; }

    /// <summary>
    /// Gets or initializes the hash of the effective instruction bundle.
    /// </summary>
    public string? InstructionHash { get; init; }

    /// <summary>
    /// Gets or initializes the latest usage snapshot.
    /// </summary>
    public AgentSessionUsage? Usage { get; init; }

    /// <summary>
    /// Gets or initializes provider-specific replay hints and diagnostics for the last-used provider.
    /// </summary>
    public JsonElement? ProviderState { get; init; }

    /// <summary>
    /// Gets or initializes the latest compaction summary event identifier when present.
    /// </summary>
    public string? CompactionSummaryContentId { get; init; }

    /// <summary>
    /// Gets or initializes the latest compaction checkpoint event identifier when present.
    /// </summary>
    public string? CompactionCheckpointEventId { get; init; }

    /// <summary>
    /// Gets or initializes when compaction last completed.
    /// </summary>
    public DateTimeOffset? LastCompactedAt { get; init; }

    /// <summary>
    /// Gets or initializes the latest compaction trigger when known.
    /// </summary>
    public string? LastCompactionTrigger { get; init; }

    /// <summary>
    /// Gets or initializes the prompt token count before the latest compaction.
    /// </summary>
    public long? LastCompactionTokensBefore { get; init; }

    /// <summary>
    /// Gets or initializes the prompt token count after the latest compaction.
    /// </summary>
    public long? LastCompactionTokensAfter { get; init; }

    /// <summary>
    /// Gets or initializes the update timestamp.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Gets or initializes the currently loaded skills for the session.
    /// </summary>
    public IReadOnlyList<LocalAgentLoadedSkillState> LoadedSkills { get; init; } = [];
}
