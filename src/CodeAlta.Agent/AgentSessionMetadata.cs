using System.Text.Json.Serialization;

namespace CodeAlta.Agent;

/// <summary>
/// Describes a stored or active CodeAlta agent session.
/// </summary>
/// <param name="SessionId">The durable session identifier.</param>
/// <param name="CreatedAt">The time the session was created.</param>
/// <param name="UpdatedAt">The time the session was last updated.</param>
/// <param name="Summary">Optional session summary or preview text.</param>
/// <param name="Context">Optional directory/repo context.</param>
/// <param name="WorkspacePath">Optional workspace path for the session.</param>
/// <param name="Details">Optional provider or runtime-specific metadata details.</param>
/// <param name="ProtocolFamily">Optional last-used protocol family.</param>
/// <param name="ProviderKey">Optional last-used configured provider key.</param>
/// <param name="ModelId">Optional last-used model identifier.</param>
/// <param name="ReasoningEffort">Optional last-used reasoning effort.</param>
/// <param name="AgentPromptId">Optional last-used agent prompt identifier.</param>
/// <param name="ParentSessionId">Optional parent session identifier used only for lineage/orchestration metadata.</param>
/// <param name="CreatedBySessionId">Optional session identifier that created this session.</param>
/// <param name="CreatedByRunId">Optional run identifier that created this session.</param>
/// <param name="ViewState">Optional cached session-view state projection.</param>
/// <param name="WorktreePath">Optional git worktree the session works in, when it is not its workspace path.</param>
public sealed record AgentSessionMetadata(
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Summary = null,
    AgentSessionContext? Context = null,
    string? WorkspacePath = null,
    AgentSessionMetadataDetails? Details = null,
    string? ProtocolFamily = null,
    string? ProviderKey = null,
    string? ModelId = null,
    AgentReasoningEffort? ReasoningEffort = null,
    string? AgentPromptId = null,
    string? ParentSessionId = null,
    string? CreatedBySessionId = null,
    AgentRunId? CreatedByRunId = null,
    AgentSessionViewStateMetadata? ViewState = null,
    string? WorktreePath = null)
{
    /// <summary>Gets where the session comes from when it starts with the turns of another one; null otherwise.</summary>
    public Runtime.AgentSessionForkOrigin? ForkedFrom { get; init; }

    /// <summary>Gets the number of times the session was rewound: its journal became shorter each time.</summary>
    public int RewindCount { get; init; }

    /// <summary>
    /// Gets the space the session belongs to, as the CodeAlta header of its journal records it; null for a session
    /// that records none, such as one created before sessions had a space.
    /// </summary>
    public string? SpaceId { get; init; }

    /// <summary>
    /// Gets the CodeAlta header of the session's journal (its first line), serialized as JSON, as the session list
    /// recorded it; null when the journal has none or the list does not say it. It is the evidence of the session's scope
    /// that a reader of the list uses instead of opening the journal.
    /// </summary>
    public string? JournalHeaderJson { get; init; }
}

/// <summary>
/// Describes cached CodeAlta-local session view state associated with a persisted agent session.
/// </summary>
/// <param name="ProviderKey">Optional locally selected provider key.</param>
/// <param name="ModelId">Optional locally selected model identifier.</param>
/// <param name="ReasoningEffort">Optional locally selected reasoning effort.</param>
/// <param name="AgentPromptId">Optional locally selected agent prompt identifier.</param>
/// <param name="Archived">Whether the session is locally archived.</param>
/// <param name="MessageCount">Optional cached displayable message count.</param>
/// <param name="ParentSessionId">Optional locally persisted parent session identifier.</param>
/// <param name="CreatedByJson">Optional serialized CodeAlta actor provenance.</param>
public sealed record AgentSessionViewStateMetadata(
    string? ProviderKey = null,
    string? ModelId = null,
    AgentReasoningEffort? ReasoningEffort = null,
    string? AgentPromptId = null,
    bool Archived = false,
    int? MessageCount = null,
    string? ParentSessionId = null,
    string? CreatedByJson = null)
{
    /// <summary>Gets the permission mode chosen for the session, or <see langword="null"/> for the one of its provider.</summary>
    public string? PermissionMode { get; init; }

    /// <summary>Gets the session's requested total context capacity, or null for Default.</summary>
    public long? ContextWindowTokens { get; init; }

    /// <summary>Gets the sandbox chosen for the session (a profile name, or <c>off</c>), or <see langword="null"/> when its scopes decide.</summary>
    public string? Sandbox { get; init; }

    /// <summary>Gets the options of the session, or <see langword="null"/> for the defaults (<see cref="AgentSessionOptions"/>).</summary>
    public AgentSessionOptions? Options { get; init; }

    /// <summary>Gets how the session titles itself, or <see langword="null"/> when it follows the setting.</summary>
    public AgentSessionTitleMode? TitleMode { get; init; }

    /// <summary>Gets when the session was archived, or <see langword="null"/> when it is not archived or the time is not known.</summary>
    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>
    /// Gets how many queued prompts of the session wait to be sent (<c>queued</c> or <c>submitting</c>), as the last state
    /// record of its journal says; <see langword="null"/> when it is not known, for a state cached before it was kept.
    /// </summary>
    public int? QueuedPromptCount { get; init; }
}

/// <summary>
/// Base type for provider/runtime-specific session metadata details.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(CodexSessionMetadataDetails), "codex")]
[JsonDerivedType(typeof(CopilotSessionMetadataDetails), "copilot")]
[JsonDerivedType(typeof(RawApiSessionMetadataDetails), "rawApi")]
public abstract record AgentSessionMetadataDetails;

/// <summary>
/// Codex-specific session metadata details.
/// </summary>
/// <param name="ModelProvider">The model provider reported by Codex when available.</param>
/// <param name="Source">The provider-reported origin for the session.</param>
/// <param name="Status">The provider-reported runtime status.</param>
/// <param name="IsEphemeral">Whether the provider-reported session is ephemeral.</param>
/// <param name="SessionName">The optional legacy provider title reported by Codex.</param>
public sealed record CodexSessionMetadataDetails(
    string? ModelProvider = null,
    string? Source = null,
    string? Status = null,
    bool IsEphemeral = false,
    string? SessionName = null)
    : AgentSessionMetadataDetails;

/// <summary>
/// Copilot-specific session metadata details.
/// </summary>
/// <param name="IsRemote">Whether the session is running remotely.</param>
public sealed record CopilotSessionMetadataDetails(
    bool IsRemote = false)
    : AgentSessionMetadataDetails;

/// <summary>
/// Provider-backed agent-runtime session metadata details.
/// </summary>
/// <param name="ProviderDisplayName">The configured provider display name.</param>
/// <param name="ProviderBaseUri">The configured provider base URI when applicable.</param>
/// <param name="ProviderSessionId">The provider-native session or response identifier when available.</param>
/// <param name="Title">The latest locally persisted session title when available.</param>
public sealed record RawApiSessionMetadataDetails(
    string? ProviderDisplayName = null,
    string? ProviderBaseUri = null,
    string? ProviderSessionId = null,
    string? Title = null)
    : AgentSessionMetadataDetails
{
    /// <summary>
    /// Gets who chose <see cref="Title"/>; <see langword="null"/> for a session written before sources were recorded.
    /// </summary>
    public AgentSessionTitleSource? TitleSource { get; init; }
}

