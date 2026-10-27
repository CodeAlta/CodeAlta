using System.Text.Json.Serialization;
using CodeAlta.Agent;
using SharpYaml.Model;

namespace CodeAlta.Catalog;

/// <summary>
/// Describes the machine-local UI state for restoring open session views.
/// </summary>
public sealed class SessionViewViewState
{
    /// <summary>Gets or sets frontend-owned layout trees, keyed by frontend identity (for example tui or desktop).
    /// Unrecognized frontend values must be retained, not filtered by local availability.</summary>
    [JsonIgnore]
    public YamlMapping FrontendLayouts { get; set; } = new();

    /// <summary>Gets or sets shared logical tab descriptors in order. Descriptors are YAML mappings
    /// with stable id/kind/reference or contribution fields; unavailable kinds remain opaque data.
    /// This is a persistence seam, not a renderer or plugin activation contract.</summary>
    [JsonIgnore]
    public YamlSequence LogicalTabs { get; set; } = new();

    /// <summary>Gets the raw-byte revision observed on load. A newly constructed state expects a missing file.
    /// Saves return a separate acknowledgment; this baseline is never advanced by a failed save.</summary>
    [JsonIgnore]
    public TextFileRevision Revision { get; internal set; } = TextFileRevision.Missing;

    // Keep unknown nested fields outside replaceable TUI selection/navigator/preference projections.
    internal string? RetainedYaml { get; set; }

    /// <summary>
    /// Gets or sets the ordered open session identifiers.
    /// </summary>
    [JsonPropertyName("open_session_ids")]
    public List<string> OpenSessionIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the persisted shell selection.
    /// </summary>
    [JsonPropertyName("selection")]
    public SessionViewSelectionState Selection { get; set; } = SessionViewSelectionState.GlobalDraft();

    /// <summary>
    /// Gets or sets the selected session identifier.
    /// This legacy field remains for compatibility with older persisted state.
    /// </summary>
    [JsonPropertyName("selected_session_id")]
    public string? SelectedSessionId { get; set; }

    /// <summary>
    /// Gets or sets the last update time.
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets per-project execution preferences restored by the terminal UI.
    /// </summary>
    [JsonPropertyName("project_preferences")]
    public Dictionary<string, SessionViewPreference> ProjectPreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets transient legacy per-session execution preferences. This property is intentionally not serialized.
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, SessionViewPreference> SessionPreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets transient legacy session state. This property is intentionally not serialized.
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, SessionViewLocalState> SessionStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets machine-local navigator settings.
    /// </summary>
    [JsonPropertyName("navigator")]
    public NavigatorSettings Navigator { get; set; } = new();

    /// <summary>
    /// Validates the view state.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the data is invalid.</exception>
    public void Validate()
    {
        Selection ??= SessionViewSelectionState.GlobalDraft();
        Selection.Validate();

        var duplicateSessionId = OpenSessionIds
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .Where(static x => x.Count() > 1)
            .Select(static x => x.Key)
            .FirstOrDefault();

        if (duplicateSessionId is not null)
        {
            throw new ArgumentException($"Open session ids contain duplicate entry '{duplicateSessionId}'.", nameof(OpenSessionIds));
        }

        var selectedSessionId = Selection.Surface == SessionViewSelectionSurface.Session
            ? Selection.SessionId
            : SelectedSessionId;
        if (!string.IsNullOrWhiteSpace(selectedSessionId)
            && !OpenSessionIds.Contains(selectedSessionId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Selected session id must be present in open_session_ids.", nameof(Selection));
        }

        var legacySelectedSessionId = Selection.Surface == SessionViewSelectionSurface.Session
            ? Selection.SessionId
            : null;
        if (!string.Equals(SelectedSessionId, legacySelectedSessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Selected session id must mirror the persisted selection.", nameof(SelectedSessionId));
        }

        var invalidPreferenceKey = ProjectPreferences.Keys.FirstOrDefault(string.IsNullOrWhiteSpace);
        if (invalidPreferenceKey is not null)
        {
            throw new ArgumentException("Project preference keys must be non-empty.", nameof(ProjectPreferences));
        }

        Navigator ??= new NavigatorSettings();
        Navigator.Validate();
    }
}

/// <summary>
/// Describes the persisted shell selection restored by the terminal UI.
/// </summary>
public sealed class SessionViewSelectionState
{
    /// <summary>
    /// Gets or sets the selected shell surface.
    /// </summary>
    [JsonPropertyName("surface")]
    public SessionViewSelectionSurface Surface { get; set; } = SessionViewSelectionSurface.Draft;

    /// <summary>
    /// Gets or sets the draft scope when the draft workspace is selected.
    /// </summary>
    [JsonPropertyName("draft_scope")]
    public SessionViewDraftScope DraftScope { get; set; } = SessionViewDraftScope.Global;

    /// <summary>
    /// Gets or sets the selected or preferred project identifier.
    /// </summary>
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    /// <summary>
    /// Gets or sets the selected session identifier when a session workspace is selected.
    /// </summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    /// <summary>
    /// Creates a persisted global draft selection.
    /// </summary>
    public static SessionViewSelectionState GlobalDraft(string? projectId = null)
        => new()
        {
            Surface = SessionViewSelectionSurface.Draft,
            DraftScope = SessionViewDraftScope.Global,
            ProjectId = projectId,
        };

    /// <summary>
    /// Creates a persisted project draft selection.
    /// </summary>
    public static SessionViewSelectionState ProjectDraft(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return new SessionViewSelectionState
        {
            Surface = SessionViewSelectionSurface.Draft,
            DraftScope = SessionViewDraftScope.Project,
            ProjectId = projectId,
        };
    }

    /// <summary>
    /// Creates a persisted session selection.
    /// </summary>
    public static SessionViewSelectionState Session(string sessionId, string? projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return new SessionViewSelectionState
        {
            Surface = SessionViewSelectionSurface.Session,
            ProjectId = projectId,
            SessionId = sessionId,
        };
    }

    /// <summary>
    /// Validates the selection state.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the selection is invalid.</exception>
    public void Validate()
    {
        if (Surface == SessionViewSelectionSurface.Session)
        {
            if (string.IsNullOrWhiteSpace(SessionId))
            {
                throw new ArgumentException("Session selection must specify session_id.", nameof(SessionId));
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(SessionId))
        {
            throw new ArgumentException("Draft selection cannot specify session_id.", nameof(SessionId));
        }

        if (DraftScope == SessionViewDraftScope.Project && string.IsNullOrWhiteSpace(ProjectId))
        {
            throw new ArgumentException("Project draft selection must specify project_id.", nameof(ProjectId));
        }
    }
}

/// <summary>
/// Identifies the persisted shell surface.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionViewSelectionSurface>))]
public enum SessionViewSelectionSurface
{
    /// <summary>
    /// The draft workspace is selected.
    /// </summary>
    Draft,

    /// <summary>
    /// A session workspace is selected.
    /// </summary>
    Session,
}

/// <summary>
/// Identifies the persisted draft scope.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionViewDraftScope>))]
public enum SessionViewDraftScope
{
    /// <summary>
    /// The global draft scope is selected.
    /// </summary>
    Global,

    /// <summary>
    /// A project draft scope is selected.
    /// </summary>
    Project,
}

/// <summary>
/// Describes persisted machine-local metadata for a session.
/// </summary>
public sealed class SessionViewLocalState
{
    /// <summary>
    /// Gets or sets the provider key selected for this session.
    /// </summary>
    [JsonPropertyName("provider_key")]
    public string? ProviderKey { get; set; }

    /// <summary>
    /// Gets or sets the session model identifier.
    /// </summary>
    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    /// <summary>
    /// Gets or sets the session reasoning effort.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public AgentReasoningEffort? ReasoningEffort { get; set; }

    /// <summary>
    /// Gets or sets the permission mode chosen for the session, or <see langword="null"/> for the one its provider is
    /// configured with. A journal written before the choice existed has none.
    /// </summary>
    [JsonPropertyName("permission_mode")]
    public string? PermissionMode { get; set; }

    /// <summary>
    /// Gets or sets the sandbox chosen for the session: the name of a sandbox profile, or <c>off</c>; null when its
    /// model, provider, project, space or the setting of every session decides. A journal written before the sandbox
    /// existed has none.
    /// </summary>
    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; set; }

    /// <summary>
    /// Gets or sets how the session titles itself (<c>first_prompt</c>, <c>last_message</c> or <c>model</c>), or
    /// <see langword="null"/> when it follows the setting. A journal written before the choice existed has none.
    /// </summary>
    [JsonPropertyName("title_mode")]
    public string? TitleMode { get; set; }

    /// <summary>
    /// Gets or sets whether the user reviews the changes of the session before an agent commits them, or
    /// <see langword="null"/> when the session follows the setting (<c>[review] before_commit</c>).
    /// </summary>
    [JsonPropertyName("review_before_commit")]
    public bool? ReviewBeforeCommit { get; set; }

    /// <summary>
    /// Gets or sets what the agent of the session was last told of the review before commit, or <see langword="null"/>
    /// when it was told nothing yet: the host tells it again only when this differs from what applies.
    /// </summary>
    [JsonPropertyName("review_noted")]
    public bool? ReviewNoted { get; set; }

    /// <summary>
    /// Gets or sets the options of the session (<see cref="CodeAlta.Agent.AgentSessionOptions"/>), or <see langword="null"/>
    /// for the defaults, every option on. A journal written before the options existed has none.
    /// </summary>
    [JsonPropertyName("options")]
    public CodeAlta.Agent.AgentSessionOptions? Options { get; set; }

    /// <summary>
    /// Gets or sets what the agent of the session was last told of its sandbox (<c>off</c>, or its profile and the form
    /// of the note), or <see langword="null"/> when it was told nothing yet: the host tells it again only when this differs.
    /// </summary>
    [JsonPropertyName("sandbox_noted")]
    public string? SandboxNoted { get; set; }

    /// <summary>
    /// Gets or sets the tier of models the session runs on (<c>complex</c>, <c>standard</c> or <c>quick</c>), or
    /// <see langword="null"/> for a model chosen by hand. A fallback goes on with the next target of that tier.
    /// </summary>
    [JsonPropertyName("tier")]
    public string? Tier { get; set; }

    /// <summary>
    /// Gets or sets the selected agent prompt identifier.
    /// </summary>
    [JsonPropertyName("agent_prompt_id")]
    public string? AgentPromptId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the session is archived locally.
    /// </summary>
    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    /// <summary>
    /// Gets or sets when the session was archived, or <see langword="null"/> when it is not archived or was archived
    /// before the time was kept.
    /// </summary>
    [JsonPropertyName("archived_at")]
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>
    /// Gets or sets the cached message count when known.
    /// </summary>
    [JsonPropertyName("message_count")]
    public int? MessageCount { get; set; }

    /// <summary>
    /// Gets or sets the same-scope parent session identifier used for durable sidebar lineage.
    /// </summary>
    [JsonPropertyName("parent_session_id")]
    public string? ParentSessionId { get; set; }

    /// <summary>
    /// Gets or sets durable attribution for the actor that created this session.
    /// </summary>
    [JsonPropertyName("created_by")]
    public AltaActorProvenance? CreatedBy { get; set; }

    /// <summary>
    /// Gets or sets durable prompt/queue/steering provenance for restart-time timeline reconstruction.
    /// </summary>
    [JsonPropertyName("prompt_provenance")]
    public List<SessionViewPromptProvenance> PromptProvenance { get; set; } = [];

    /// <summary>
    /// Gets or sets persisted headless prompts waiting for later submission or retained for drain-state reconstruction.
    /// </summary>
    [JsonPropertyName("queued_prompts")]
    public List<SessionViewQueuedPrompt> QueuedPrompts { get; set; } = [];

    /// <summary>
    /// Tells whether the queue of the session holds a prompt that waits or is being submitted. A prompt that was
    /// submitted, failed or was canceled is a record of work that ended.
    /// </summary>
    /// <returns><see langword="true"/> when a queued prompt is pending.</returns>
    public bool HasPendingQueuedPrompts()
        => QueuedPrompts?.Any(static prompt => SessionViewQueuedPrompt.IsPendingState(prompt.State)) == true;

    /// <summary>
    /// Counts the queued prompts of the session that wait or are being submitted, as <see cref="HasPendingQueuedPrompts"/>
    /// finds them.
    /// </summary>
    /// <returns>The number of pending queued prompts.</returns>
    public int CountPendingQueuedPrompts()
        => QueuedPrompts?.Count(static prompt => SessionViewQueuedPrompt.IsPendingState(prompt.State)) ?? 0;

    /// <summary>
    /// Validates the session local state.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the message count is negative.</exception>
    public void Validate()
    {
        if (MessageCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MessageCount), MessageCount, "MessageCount cannot be negative.");
        }

        if (AgentPromptId is not null && string.IsNullOrWhiteSpace(AgentPromptId))
        {
            throw new ArgumentException("Agent prompt id must be non-empty when present.", nameof(AgentPromptId));
        }

        if (CreatedBy is not null && string.IsNullOrWhiteSpace(CreatedBy.Kind))
        {
            throw new ArgumentException("CreatedBy kind is required when session provenance is present.", nameof(CreatedBy));
        }

        PromptProvenance ??= [];
        foreach (var provenance in PromptProvenance)
        {
            provenance.Validate();
        }

        QueuedPrompts ??= [];
        foreach (var queuedPrompt in QueuedPrompts)
        {
            queuedPrompt.Validate();
        }
    }
}

/// <summary>
/// Describes a durable headless prompt queue item for a session view.
/// </summary>
public sealed class SessionViewQueuedPrompt
{
    /// <summary>
    /// Gets or sets the stable queue item identifier.
    /// </summary>
    [JsonPropertyName("queue_item_id")]
    public string QueueItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the prompt dispatch kind, such as <c>send</c>, <c>message</c>, or <c>request</c>.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the queued prompt text to submit when the queue is drained.
    /// </summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the short visible prompt preview stored for diagnostics and timeline reconstruction.
    /// </summary>
    [JsonPropertyName("prompt_preview")]
    public string? PromptPreview { get; set; }

    /// <summary>
    /// Gets or sets the images sent with the prompt, saved with the attachments of the session (such as the pictures of the
    /// result of a script); null for none.
    /// </summary>
    [JsonPropertyName("images")]
    public List<SessionViewQueuedPromptImage>? Images { get; set; }

    /// <summary>
    /// Gets or sets the durable queue state, such as <c>queued</c>, <c>submitting</c>, <c>submitted</c>, or <c>failed</c>.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "queued";

    /// <summary>
    /// Tells whether a queue state is pending: <c>queued</c> or <c>submitting</c>. The other states (<c>submitted</c>,
    /// <c>failed</c>, <c>canceled</c>) are records of work that ended.
    /// </summary>
    /// <param name="state">The queue state.</param>
    /// <returns><see langword="true"/> for a pending state.</returns>
    public static bool IsPendingState(string? state)
        => string.Equals(state, "queued", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(state, "submitting", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the run identifier produced when this queued prompt is drained, if any.
    /// </summary>
    [JsonPropertyName("run_id")]
    public string? RunId { get; set; }

    /// <summary>
    /// Gets or sets durable attribution for the actor that queued the prompt.
    /// </summary>
    [JsonPropertyName("submitted_by")]
    public AltaActorProvenance? SubmittedBy { get; set; }

    /// <summary>
    /// Gets or sets the creation timestamp.
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the timestamp at which queue draining was attempted or completed.
    /// </summary>
    [JsonPropertyName("drained_at")]
    public DateTimeOffset? DrainedAt { get; set; }

    /// <summary>
    /// Gets or sets the last drain failure message, when draining failed.
    /// </summary>
    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets the runtime that marked the prompt <c>submitting</c>: a runtime that finds the mark of another one, or
    /// none, knows that the send was cut by the end of the process that made it.
    /// </summary>
    [JsonPropertyName("submitting_runtime")]
    public string? SubmittingRuntime { get; set; }

    /// <summary>
    /// Gets or sets how many sends of the prompt were cut by the end of the process that made them; null for none.
    /// </summary>
    [JsonPropertyName("interrupted_sends")]
    public int? InterruptedSends { get; set; }

    /// <summary>
    /// Validates the queued prompt record.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when required fields are invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(QueueItemId))
        {
            throw new ArgumentException("Queue item id is required.", nameof(QueueItemId));
        }

        if (string.IsNullOrWhiteSpace(Kind))
        {
            throw new ArgumentException("Queued prompt kind is required.", nameof(Kind));
        }

        if (string.IsNullOrWhiteSpace(Prompt))
        {
            throw new ArgumentException("Queued prompt text is required.", nameof(Prompt));
        }

        if (string.IsNullOrWhiteSpace(State))
        {
            throw new ArgumentException("Queued prompt state is required.", nameof(State));
        }

        if (SubmittedBy is not null && string.IsNullOrWhiteSpace(SubmittedBy.Kind))
        {
            throw new ArgumentException("SubmittedBy kind is required when queued prompt attribution is present.", nameof(SubmittedBy));
        }
    }
}

/// <summary>
/// Describes an image of a queued prompt: a file saved with the attachments of its session.
/// </summary>
public sealed class SessionViewQueuedPromptImage
{
    /// <summary>
    /// Gets or sets the full path of the saved file.
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the title of the image.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the media type of the image, such as <c>image/png</c>.
    /// </summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    /// <summary>
    /// Gets or sets whether the attachment is a file the model reads in the form it can (a document as Markdown), not an image;
    /// false for an image.
    /// </summary>
    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsFile { get; set; }
}

/// <summary>
/// Describes durable attribution for a prompt-like operation targeting a session.
/// </summary>
public sealed class SessionViewPromptProvenance
{
    /// <summary>
    /// Gets or sets a stable prompt provenance identifier.
    /// </summary>
    [JsonPropertyName("prompt_id")]
    public string PromptId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the prompt dispatch kind, such as <c>send</c>, <c>steer</c>, <c>message</c>, or <c>request</c>.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider run identifier, when one was produced immediately.
    /// </summary>
    [JsonPropertyName("run_id")]
    public string? RunId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the prompt was queued instead of submitted immediately.
    /// </summary>
    [JsonPropertyName("queued")]
    public bool Queued { get; set; }

    /// <summary>
    /// Gets or sets the short visible prompt preview stored for diagnostics and timeline reconstruction.
    /// </summary>
    [JsonPropertyName("prompt_preview")]
    public string? PromptPreview { get; set; }

    /// <summary>
    /// Gets or sets durable attribution for the actor that submitted the prompt-like operation.
    /// </summary>
    [JsonPropertyName("submitted_by")]
    public AltaActorProvenance? SubmittedBy { get; set; }

    /// <summary>
    /// Gets or sets the creation timestamp.
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Validates the prompt provenance record.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when required fields are invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PromptId))
        {
            throw new ArgumentException("Prompt provenance id is required.", nameof(PromptId));
        }

        if (string.IsNullOrWhiteSpace(Kind))
        {
            throw new ArgumentException("Prompt provenance kind is required.", nameof(Kind));
        }

        if (SubmittedBy is not null && string.IsNullOrWhiteSpace(SubmittedBy.Kind))
        {
            throw new ArgumentException("SubmittedBy kind is required when prompt provenance attribution is present.", nameof(SubmittedBy));
        }
    }
}

/// <summary>
/// Describes a persisted provider, model, and reasoning preference.
/// </summary>
public sealed class SessionViewPreference
{
    /// <summary>
    /// Gets or sets the preferred provider key.
    /// </summary>
    [JsonPropertyName("provider_key")]
    public string? ProviderKey { get; set; }

    /// <summary>
    /// Gets or sets the preferred model identifier.
    /// </summary>
    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    /// <summary>
    /// Gets or sets the preferred agent prompt identifier.
    /// </summary>
    [JsonPropertyName("agent_prompt_id")]
    public string? AgentPromptId { get; set; }

    /// <summary>
    /// Gets or sets the preferred reasoning effort.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public AgentReasoningEffort? ReasoningEffort { get; set; }

}

