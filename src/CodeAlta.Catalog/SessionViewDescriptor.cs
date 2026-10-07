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

