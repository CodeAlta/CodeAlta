using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Represents the persisted provider-agnostic session summary stored in the session journal.
/// </summary>
public sealed record AgentSessionSummary
{
    /// <summary>
    /// Gets or initializes the session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets or initializes the model provider identifier serialized as <c>backendId</c> for existing session summaries.
    /// </summary>
    [JsonPropertyName("backendId")]
    public ModelProviderId ProviderId { get; init; }

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
    /// Gets or initializes the last-used reasoning effort.
    /// </summary>
    public AgentReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>
    /// Gets or initializes the last-used agent prompt identifier.
    /// </summary>
    public string? AgentPromptId { get; init; }

    /// <summary>
    /// Gets or initializes the working directory: the folder the session belongs to.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or initializes the git worktree the session works in; null when it works in
    /// <see cref="WorkingDirectory"/>. Older journals have no such value.
    /// </summary>
    public string? WorktreeDirectory { get; init; }

    /// <summary>
    /// Gets the folder tools run in and paths are resolved from: the worktree of the session when it has one,
    /// its working directory otherwise.
    /// </summary>
    [JsonIgnore]
    public string? ExecutionDirectory => string.IsNullOrWhiteSpace(WorktreeDirectory) ? WorkingDirectory : WorktreeDirectory;

    /// <summary>
    /// Gets or initializes the user-facing title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets or initializes who chose <see cref="Title"/>: CodeAlta, which may replace it, or a person or an agent,
    /// whose title is never replaced automatically. Null for a session written before sources were recorded: it is
    /// decided when the session is next attached or renamed.
    /// </summary>
    public AgentSessionTitleSource? TitleSource { get; init; }

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
    /// Gets or initializes where the session comes from when it starts with the turns of another one; null otherwise.
    /// </summary>
    public AgentSessionForkOrigin? ForkedFrom { get; init; }

    /// <summary>
    /// Gets or initializes the number of times the session was rewound. A reader that follows the journal of a session
    /// knows from a change of this number that the journal became shorter, and reads it again from its start.
    /// </summary>
    public int RewindCount { get; init; }

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
/// Represents the persisted provider-agnostic session state stored in the session journal.
/// </summary>
public sealed record AgentSessionState
{
    /// <summary>
    /// Gets or initializes the session identifier.
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
    /// Gets or initializes how the conversation of the provider is taken up again by a session that was just forked
    /// or rewound: <see cref="ProviderSessionId"/> and <see cref="ProviderState"/> are then those of the point the
    /// session continues from. Null once a turn ran, and for a session that was never forked or rewound.
    /// </summary>
    public AgentProviderBranch? ProviderBranch { get; init; }

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
    public IReadOnlyList<AgentLoadedSkillState> LoadedSkills { get; init; } = [];
}

/// <summary>How a session continues a conversation that a provider keeps on its side.</summary>
public enum AgentProviderBranchKind
{
    /// <summary>As another session: the conversation of the source goes on as it was.</summary>
    Fork,

    /// <summary>As the same session, without what followed the point.</summary>
    Rewind,
}

/// <summary>
/// What a provider that keeps the conversation of a session on its side needs to know about a session that was just
/// forked or rewound. A provider that is sent the whole conversation with each request needs nothing of it.
/// </summary>
/// <param name="Kind">Whether the session is another one than the one the provider knows, or the same one cut.</param>
/// <param name="AtEnd">Whether the point is the end of the conversation as the provider has it: nothing follows it there.</param>
public sealed record AgentProviderBranch(AgentProviderBranchKind Kind, bool AtEnd)
{
    /// <summary>
    /// Gets what the provider recorded at the end of the conversation the session continues from, when that is later
    /// than the point: a provider that counts what a conversation cost in total reads there what was counted already.
    /// </summary>
    public JsonElement? CurrentProviderState { get; init; }
}
