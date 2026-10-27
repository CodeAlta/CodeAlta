using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>
/// Describes a durable session view.
/// </summary>
public sealed class SessionViewDescriptor
{
    /// <summary>
    /// Gets or sets the stable session identifier.
    /// </summary>
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the session kind.
    /// </summary>
    [JsonPropertyName("kind")]
    public SessionViewKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the model provider identifier for the session.
    /// Serialized as <c>backend_id</c> so existing session-view front matter remains readable.
    /// </summary>
    [JsonPropertyName("backend_id")]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider key selected for the session.
    /// </summary>
    [JsonPropertyName("provider_key")]
    public string? ProviderKey { get; set; }

    /// <summary>
    /// Gets or sets the owning project identifier for project sessions.
    /// </summary>
    [JsonPropertyName("project_ref")]
    public string? ProjectRef { get; set; }

    /// <summary>
    /// Gets or sets the legacy parent session identifier for internal session metadata.
    /// </summary>
    [JsonPropertyName("parent_session_id")]
    public string? ParentSessionId { get; set; }

    /// <summary>
    /// Gets or sets durable attribution for the actor that created this session.
    /// </summary>
    [JsonPropertyName("created_by")]
    public AltaActorProvenance? CreatedBy { get; set; }

    /// <summary>
    /// Gets or sets the space the session belongs to, as its journal records it: written when the session is
    /// created and kept for its whole life. <see langword="null"/> for a session created before sessions had a
    /// space; <see cref="SpaceCatalog.ResolveSessionSpace"/> tells which space such a session is in.
    /// </summary>
    [JsonPropertyName("space_id")]
    public string? SpaceId { get; set; }

    /// <summary>
    /// Gets or sets where the session comes from when it starts with the turns of another one: the session it was
    /// forked from, or the session whose rewind it keeps. Lineage only; null for a session that started empty.
    /// </summary>
    [JsonPropertyName("forked_from")]
    public CodeAlta.Agent.Runtime.AgentSessionForkOrigin? ForkedFrom { get; set; }

    /// <summary>
    /// Gets or sets the number of times the session was rewound. A view of the session that holds what it read of its
    /// conversation reads it again when this number changes.
    /// </summary>
    [JsonPropertyName("rewind_count")]
    public int RewindCount { get; set; }

    /// <summary>
    /// Gets or sets the session working directory: the folder the session belongs to, which is the folder of its
    /// project for a project session. It names the session's project and does not change.
    /// </summary>
    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the git worktree the session works in, when it does not work in
    /// <see cref="WorkingDirectory"/>: a separate checkout of the project's repository, where the agent reads,
    /// writes and runs commands. Null for a session that works in the folder of its project.
    /// </summary>
    /// <remarks>
    /// It is what the session records. A worktree can be removed at any time: the session works there only while
    /// the folder exists, and no longer records it once it has continued in the folder of its project.
    /// </remarks>
    [JsonPropertyName("worktree_dir")]
    public string? WorktreeDirectory { get; set; }

    /// <summary>
    /// Gets or sets the session title.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the durable status.
    /// </summary>
    [JsonPropertyName("status")]
    public SessionViewStatus Status { get; set; } = SessionViewStatus.Draft;

    /// <summary>
    /// Gets or sets the creation time.
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the last update time.
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the last active time.
    /// </summary>
    [JsonPropertyName("last_active_at")]
    public DateTimeOffset LastActiveAt { get; set; }

    /// <summary>
    /// Gets or sets the optional first prompt timestamp.
    /// </summary>
    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the latest durable summary.
    /// </summary>
    [JsonPropertyName("latest_summary")]
    public string? LatestSummary { get; set; }

    /// <summary>
    /// Gets or sets the session model identifier when known.
    /// </summary>
    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    /// <summary>
    /// Gets or sets the session reasoning effort when known.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public CodeAlta.Agent.AgentReasoningEffort? ReasoningEffort { get; set; }

    /// <summary>
    /// Gets or sets the permission mode chosen for the session, or <see langword="null"/> for the one its provider is
    /// configured with.
    /// </summary>
    [JsonPropertyName("permission_mode")]
    public string? PermissionMode { get; set; }

    /// <summary>
    /// Gets or sets the sandbox chosen for the session (a profile name, or <c>off</c>), or <see langword="null"/> when
    /// its scopes decide.
    /// </summary>
    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; set; }

    /// <summary>
    /// Gets or sets how the session titles itself (<c>first_prompt</c>, <c>last_message</c> or <c>model</c>), or
    /// <see langword="null"/> when it follows the setting.
    /// </summary>
    [JsonPropertyName("title_mode")]
    public string? TitleMode { get; set; }

    /// <summary>
    /// Gets or sets whether the user reviews the changes of the session before an agent commits them, as the session
    /// was created with; <see langword="null"/> when it follows the setting.
    /// </summary>
    [JsonPropertyName("review_before_commit")]
    public bool? ReviewBeforeCommit { get; set; }

    /// <summary>
    /// Gets or sets the options of the session (<see cref="CodeAlta.Agent.AgentSessionOptions"/>), such as whether it can
    /// start sessions of its own; <see langword="null"/> for the defaults, every option on.
    /// </summary>
    [JsonPropertyName("options")]
    public CodeAlta.Agent.AgentSessionOptions? Options { get; set; }

    /// <summary>
    /// Gets or sets who chose <see cref="Title"/>, as its saved summary says; <see langword="null"/> when that is
    /// not recorded. It is not part of the header of the journal.
    /// </summary>
    [JsonIgnore]
    public CodeAlta.Agent.AgentSessionTitleSource? TitleSource { get; set; }

    /// <summary>
    /// Gets or sets the view state of the session as the list of sessions in the application database keeps it, when the
    /// descriptor was made from that list; <see langword="null"/> when it was read from the journal or made otherwise.
    /// It is not part of the header of the journal.
    /// </summary>
    [JsonIgnore]
    public CodeAlta.Agent.AgentSessionViewStateMetadata? CachedViewState { get; set; }

    /// <summary>
    /// Gets or sets the selected agent prompt identifier when known.
    /// </summary>
    [JsonPropertyName("agent_prompt_id")]
    public string? AgentPromptId { get; set; }

    /// <summary>
    /// Gets or sets the cached number of displayable messages when known.
    /// </summary>
    [JsonPropertyName("message_count")]
    public int? MessageCount { get; set; }

    /// <summary>
    /// Gets the path to the source markdown file when loaded from disk.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the markdown body loaded from disk.
    /// </summary>
    public string? MarkdownBody { get; set; }

    /// <summary>
    /// Gets a value indicating whether the session can no longer change providers.
    /// </summary>
    public bool IsProviderLocked => StartedAt is not null;

    /// <summary>
    /// Gets the provider key to use for execution.
    /// </summary>
    [JsonIgnore]
    public string ResolvedProviderKey => string.IsNullOrWhiteSpace(ProviderKey) ? ProviderId : ProviderKey;

    /// <summary>
    /// Marks the session as started.
    /// </summary>
    /// <param name="timestamp">The timestamp to record.</param>
    public void MarkStarted(DateTimeOffset timestamp)
    {
        if (StartedAt is null)
        {
            StartedAt = timestamp;
        }

        UpdatedAt = timestamp;
        LastActiveAt = timestamp;
        if (Status == SessionViewStatus.Draft)
        {
            Status = SessionViewStatus.Active;
        }
    }

    /// <summary>
    /// Validates the descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when required values are missing or invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SessionId))
        {
            throw new ArgumentException("Session id is required.", nameof(SessionId));
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            throw new ArgumentException("Session title is required.", nameof(Title));
        }

        if (CreatedAt == default)
        {
            throw new ArgumentException("CreatedAt is required.", nameof(CreatedAt));
        }

        if (UpdatedAt == default)
        {
            throw new ArgumentException("UpdatedAt is required.", nameof(UpdatedAt));
        }

        if (LastActiveAt == default)
        {
            throw new ArgumentException("LastActiveAt is required.", nameof(LastActiveAt));
        }

        if (string.IsNullOrWhiteSpace(ProviderId))
        {
            throw new ArgumentException("Provider id is required.", nameof(ProviderId));
        }

        if (string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            throw new ArgumentException("Working directory is required.", nameof(WorkingDirectory));
        }

        if (MessageCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MessageCount), MessageCount, "MessageCount cannot be negative.");
        }

        if (AgentPromptId is not null && string.IsNullOrWhiteSpace(AgentPromptId))
        {
            throw new ArgumentException("Agent prompt id must be non-empty when present.", nameof(AgentPromptId));
        }

        switch (Kind)
        {
            case SessionViewKind.GlobalSession:
                if (!string.IsNullOrWhiteSpace(ProjectRef))
                {
                    throw new ArgumentException("Global sessions cannot declare a project.", nameof(ProjectRef));
                }

                break;

            case SessionViewKind.ProjectSession:
                if (!ProjectId.TryParse(ProjectRef, out _))
                {
                    throw new ArgumentException("Project sessions require a valid project_ref.", nameof(ProjectRef));
                }

                break;

            case SessionViewKind.InternalSession:
                break;
        }
    }
}

