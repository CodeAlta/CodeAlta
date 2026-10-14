using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Identifies the prompt/session context for a headless session-view command.
/// </summary>
public sealed record SessionCommandContext
{
    /// <summary>Gets the owning project identifier.</summary>
    public required string ProjectId { get; init; }

    /// <summary>Gets the owning project path.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Gets the prompt-session identifier.</summary>
    public required string PromptSessionId { get; init; }

    /// <summary>Gets the selected model-provider identifier.</summary>
    public required string ModelProviderId { get; init; }

    /// <summary>Gets the selected model identifier, when one is selected.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets the session-draft identifier, when the command targets an unmaterialized draft.</summary>
    public string? SessionDraftId { get; init; }

    /// <summary>Gets the durable session identifier, when the command targets a materialized session.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets provider execution options associated with the command.</summary>
    public SessionExecutionOptions? ExecutionOptions { get; init; }
}

/// <summary>
/// Describes an attachment submitted with a headless session-view prompt.
/// </summary>
public sealed record SessionPromptAttachment
{
    /// <summary>Gets the stable attachment identifier supplied by the caller.</summary>
    public required string AttachmentId { get; init; }

    /// <summary>Gets the attachment kind. <see cref="SessionPromptAttachmentKind.Auto"/> derives the kind from available metadata.</summary>
    public SessionPromptAttachmentKind Kind { get; init; } = SessionPromptAttachmentKind.Auto;

    /// <summary>Gets the attachment path, when the attachment is file-backed.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the attachment display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets text content for text-backed attachments or selections.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the attachment content type, when known.</summary>
    public string? ContentType { get; init; }

    /// <summary>Gets the attachment bytes, when the caller materialized content in memory.</summary>
    public ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>Gets an optional line range for file or directory attachments.</summary>
    public AgentLineRange? LineRange { get; init; }

    /// <summary>Gets an optional selection range for selection attachments.</summary>
    public AgentSelectionRange? SelectionRange { get; init; }
}

/// <summary>
/// Identifies a headless session-view prompt attachment kind.
/// </summary>
public enum SessionPromptAttachmentKind
{
    /// <summary>Derive the attachment kind from path, content type, and selection metadata.</summary>
    Auto,

    /// <summary>Plain text content.</summary>
    Text,

    /// <summary>A file path attachment.</summary>
    File,

    /// <summary>A directory path attachment.</summary>
    Directory,

    /// <summary>An image path or URL attachment.</summary>
    Image,

    /// <summary>A code/text selection attachment.</summary>
    Selection,

    /// <summary>Metadata for plugin consumers; ignored by agent input materialization.</summary>
    Metadata,
}

/// <summary>
/// Carries approval behavior for headless session-view commands.
/// </summary>
public sealed record SessionApprovalContext
{
    /// <summary>Gets a value indicating whether tool/permission requests may be auto-approved by policy.</summary>
    public bool AutoApprove { get; init; }

    /// <summary>Gets an optional approval policy identifier.</summary>
    public string? ApprovalPolicyId { get; init; }

    /// <summary>Gets caller-supplied approval metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Requests creation of a prompt draft scoped to a project and model provider.
/// </summary>
public sealed record CreateSessionDraftRequest
{
    /// <summary>Gets the command context.</summary>
    public required SessionCommandContext Context { get; init; }

    /// <summary>Gets the optional draft title.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// Requests materialization or launch of a session view from a draft/session context.
/// </summary>
public sealed record LaunchSessionRequest
{
    /// <summary>Gets the command context.</summary>
    public required SessionCommandContext Context { get; init; }

    /// <summary>Gets the optional session title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the optional first prompt.</summary>
    public string? InitialPrompt { get; init; }

    /// <summary>Gets prompt attachments for the first prompt.</summary>
    public IReadOnlyList<SessionPromptAttachment> Attachments { get; init; } = [];

    /// <summary>Gets approval behavior for the launch.</summary>
    public SessionApprovalContext Approval { get; init; } = new();
}

/// <summary>
/// Requests prompt submission to an existing or draft-backed session view.
/// </summary>
public sealed record SubmitSessionPromptRequest
{
    /// <summary>Gets the command context.</summary>
    public required SessionCommandContext Context { get; init; }

    /// <summary>Gets the prompt text.</summary>
    public required string Prompt { get; init; }

    /// <summary>Gets a pre-materialized agent input when the caller has already resolved attachments/references.</summary>
    public AgentInput? PreparedInput { get; init; }

    /// <summary>Gets the optional ask identifier associated with this user prompt.</summary>
    public string? AskId { get; init; }

    /// <summary>Gets prompt attachments.</summary>
    public IReadOnlyList<SessionPromptAttachment> Attachments { get; init; } = [];

    /// <summary>Gets approval behavior for the prompt.</summary>
    public SessionApprovalContext Approval { get; init; } = new();

    /// <summary>Gets a value indicating whether the prompt should be queued if the target session is busy.</summary>
    public bool QueueIfBusy { get; init; }
}

/// <summary>
/// Requests steering input for an active session view run.
/// </summary>
public sealed record SteerSessionRequest
{
    /// <summary>Gets the command context.</summary>
    public required SessionCommandContext Context { get; init; }

    /// <summary>Gets the steering prompt.</summary>
    public required string Prompt { get; init; }

    /// <summary>Gets a pre-materialized agent input when the caller has already resolved attachments/references.</summary>
    public AgentInput? PreparedInput { get; init; }

    /// <summary>Gets steering attachments.</summary>
    public IReadOnlyList<SessionPromptAttachment> Attachments { get; init; } = [];

    /// <summary>Gets approval behavior for the steering prompt.</summary>
    public SessionApprovalContext Approval { get; init; } = new();
}

/// <summary>
/// Requests abort of an active session-view run.
/// </summary>
public sealed record AbortSessionRequest
{
    /// <summary>Gets the durable session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the owning project identifier.</summary>
    public required string ProjectId { get; init; }

    /// <summary>Gets the reason surfaced to diagnostics.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Requests session-view compaction.
/// </summary>
public sealed record CompactSessionRequest
{
    /// <summary>Gets the command context.</summary>
    public required SessionCommandContext Context { get; init; }

    /// <summary>Gets a value indicating whether the compacted result should be submitted immediately.</summary>
    public bool SubmitAfterCompaction { get; init; }
}

/// <summary>
/// Requests activation of a skill for a session-view context.
/// </summary>
public sealed record ActivateSkillRequest
{
    /// <summary>Gets the command context.</summary>
    public required SessionCommandContext Context { get; init; }

    /// <summary>Gets the skill name.</summary>
    public required string SkillName { get; init; }
}

/// <summary>
/// Requests queueing a prompt for later session-view submission.
/// </summary>
public sealed record QueueSessionPromptRequest
{
    /// <summary>Gets the command context.</summary>
    public required SessionCommandContext Context { get; init; }

    /// <summary>Gets the prompt text.</summary>
    public required string Prompt { get; init; }

    /// <summary>Gets prompt attachments.</summary>
    public IReadOnlyList<SessionPromptAttachment> Attachments { get; init; } = [];

    /// <summary>Gets approval behavior for the queued prompt.</summary>
    public SessionApprovalContext Approval { get; init; } = new();
}

/// <summary>
/// Classifies command outcomes returned by the session-view orchestrator facade.
/// </summary>
public enum SessionCommandOutcomeKind
{
    /// <summary>The command completed successfully.</summary>
    Completed,

    /// <summary>The command submitted work to a running session.</summary>
    Submitted,

    /// <summary>The command queued work for later processing.</summary>
    Queued,

    /// <summary>The command steered an active run.</summary>
    Steered,

    /// <summary>The command was cancelled by a plugin or caller policy.</summary>
    Cancelled,

    /// <summary>The command was rejected because the target or provider lacks a required capability.</summary>
    Rejected,

    /// <summary>The command failed and the caller should consider restoring the submitted prompt.</summary>
    FailedWithRestoreRecommendation,
}

/// <summary>
/// Immutable snapshot of a durable session-view descriptor.
/// </summary>
public sealed record SessionViewDescriptorSnapshot
{
    /// <summary>Gets the stable session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the session kind.</summary>
    public SessionViewKind Kind { get; init; }

    /// <summary>Gets the model provider identifier for the session.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Gets the provider key selected for the session.</summary>
    public string? ProviderKey { get; init; }

    /// <summary>Gets the owning project identifier for project sessions.</summary>
    public string? ProjectRef { get; init; }

    /// <summary>Gets the legacy parent session identifier for internal session metadata.</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>Gets durable attribution for the actor that created this session.</summary>
    public AltaActorProvenance? CreatedBy { get; init; }

    /// <summary>Gets the space the session records; <see langword="null"/> for a session that records none.</summary>
    public string? SpaceId { get; init; }

    /// <summary>Gets the session working directory.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Gets the session title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the durable status.</summary>
    public SessionViewStatus Status { get; init; }

    /// <summary>Gets the creation time.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the last update time.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets the last active time.</summary>
    public DateTimeOffset LastActiveAt { get; init; }

    /// <summary>Gets the optional first prompt timestamp.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Gets the latest durable summary.</summary>
    public string? LatestSummary { get; init; }

    /// <summary>Gets the cached number of displayable messages when known.</summary>
    public int? MessageCount { get; init; }

    /// <summary>Gets the path to the source markdown file when loaded from disk.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Gets the markdown body loaded from disk.</summary>
    public string? MarkdownBody { get; init; }

    /// <summary>
    /// Creates an immutable snapshot from a mutable session-view descriptor.
    /// </summary>
    /// <param name="descriptor">The descriptor to copy.</param>
    /// <returns>An immutable descriptor snapshot.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public static SessionViewDescriptorSnapshot FromDescriptor(SessionViewDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new SessionViewDescriptorSnapshot
        {
            SessionId = descriptor.SessionId,
            Kind = descriptor.Kind,
            ProviderId = descriptor.ProviderId,
            ProviderKey = descriptor.ProviderKey,
            ProjectRef = descriptor.ProjectRef,
            ParentSessionId = descriptor.ParentSessionId,
            CreatedBy = descriptor.CreatedBy,
            SpaceId = descriptor.SpaceId,
            WorkingDirectory = descriptor.WorkingDirectory,
            Title = descriptor.Title,
            Status = descriptor.Status,
            CreatedAt = descriptor.CreatedAt,
            UpdatedAt = descriptor.UpdatedAt,
            LastActiveAt = descriptor.LastActiveAt,
            StartedAt = descriptor.StartedAt,
            LatestSummary = descriptor.LatestSummary,
            MessageCount = descriptor.MessageCount,
            SourcePath = descriptor.SourcePath,
            MarkdownBody = descriptor.MarkdownBody,
        };
    }
}

/// <summary>
/// Describes the result of an orchestrator command.
/// </summary>
public sealed record SessionCommandResult
{
    /// <summary>Gets the outcome kind.</summary>
    public required SessionCommandOutcomeKind Outcome { get; init; }

    /// <summary>Gets the immutable durable session snapshot, when a command materialized or affected a session.</summary>
    public SessionViewDescriptorSnapshot? Session { get; init; }

    /// <summary>Gets a user- or log-facing message for the outcome.</summary>
    public string? Message { get; init; }

    /// <summary>Gets the runtime run identifier, when a command submitted or steered a run.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets a value indicating whether the caller should restore the prompt text and attachments.</summary>
    public bool ShouldRestorePrompt { get; init; }
}

/// <summary>
/// Represents a session-view snapshot returned by the orchestrator facade.
/// </summary>
public sealed record SessionSnapshot
{
    /// <summary>Gets the immutable durable session snapshot.</summary>
    public required SessionViewDescriptorSnapshot Session { get; init; }

    /// <summary>Gets a value indicating whether a run is currently active.</summary>
    public bool IsRunning { get; init; }

    /// <summary>Gets the queued prompt count known to the orchestrator.</summary>
    public int QueuedPromptCount { get; init; }
}

/// <summary>
/// Base type for events emitted by the session-view orchestrator facade.
/// </summary>
public abstract record SessionOrchestratorEvent
{
    /// <summary>Gets the durable session identifier associated with the event.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets a monotonically increasing sequence number scoped to the session when known.</summary>
    public long? SequenceNumber { get; init; }
}

/// <summary>
/// Classifies session lifecycle events emitted by orchestration for runtime observers and plugins.
/// </summary>
public enum SessionLifecycleEventKind
{
    /// <summary>A prompt session was associated with a session or draft.</summary>
    SessionStarted,

    /// <summary>A durable session was created or materialized.</summary>
    SessionMaterialized,

    /// <summary>A provider run was submitted.</summary>
    RunSubmitted,

    /// <summary>A provider run completed successfully.</summary>
    RunCompleted,

    /// <summary>A provider run failed.</summary>
    RunFailed,

    /// <summary>A provider run was aborted.</summary>
    RunAborted,

    /// <summary>The legacy session view was rekeyed to a new runtime session identifier.</summary>
    SessionRekeyed,
}

/// <summary>
/// Describes a session-view lifecycle event suitable for plugin and frontend projections.
/// </summary>
public sealed record SessionLifecycleEvent : SessionOrchestratorEvent
{
    /// <summary>Gets the lifecycle event kind.</summary>
    public required SessionLifecycleEventKind Kind { get; init; }

    /// <summary>Gets the prompt-session identifier associated with the event, when known.</summary>
    public string? PromptSessionId { get; init; }

    /// <summary>Gets the run identifier associated with the event, when known.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets the previous session or session identifier for rekey events.</summary>
    public string? PreviousId { get; init; }

    /// <summary>Gets a diagnostic message for failure or cancellation lifecycle events.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Describes a session-view queue change event suitable for plugin and frontend projections.
/// </summary>
public sealed record SessionQueueChangedEvent : SessionOrchestratorEvent
{
    /// <summary>Gets the current queued prompt count after the change.</summary>
    public int QueuedPromptCount { get; init; }

    /// <summary>Gets the queue item identifier associated with the change, when known.</summary>
    public string? QueueItemId { get; init; }

    /// <summary>Gets a value indicating whether the event represents enqueueing a prompt.</summary>
    public bool IsEnqueued { get; init; }
}

/// <summary>
/// Exposes headless command/query/event APIs for session-view orchestration.
/// </summary>
public interface ISessionOrchestrator
{
    /// <summary>Creates a prompt draft.</summary>
    ValueTask<SessionCommandResult> CreateDraftAsync(CreateSessionDraftRequest request, CancellationToken cancellationToken = default);

    /// <summary>Launches or materializes a session view.</summary>
    ValueTask<SessionCommandResult> LaunchSessionAsync(LaunchSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Submits a prompt to a session view.</summary>
    ValueTask<SessionCommandResult> SubmitPromptAsync(SubmitSessionPromptRequest request, CancellationToken cancellationToken = default);

    /// <summary>Steers an active session-view run.</summary>
    ValueTask<SessionCommandResult> SteerAsync(SteerSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Aborts an active session-view run.</summary>
    ValueTask<SessionCommandResult> AbortAsync(AbortSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Compacts a session view.</summary>
    ValueTask<SessionCommandResult> CompactAsync(CompactSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Activates a skill for a session-view context.</summary>
    ValueTask<SessionCommandResult> ActivateSkillAsync(ActivateSkillRequest request, CancellationToken cancellationToken = default);

    /// <summary>Queues a prompt for later submission.</summary>
    ValueTask<SessionCommandResult> QueuePromptAsync(QueueSessionPromptRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets a snapshot for a durable session view.</summary>
    ValueTask<SessionSnapshot?> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Streams orchestrator events without exposing actor references or actor paths.</summary>
    IAsyncEnumerable<SessionOrchestratorEvent> StreamEventsAsync(CancellationToken cancellationToken = default);
}
