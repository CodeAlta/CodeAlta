using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Agent;

/// <summary>
/// Base type for a normalized agent event.
/// </summary>
/// <param name="ProviderId">The model provider identifier. Serialized as <c>backendId</c> for session-journal compatibility.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AgentRawEvent), "raw")]
[JsonDerivedType(typeof(AgentContentDeltaEvent), "contentDelta")]
[JsonDerivedType(typeof(AgentContentCompletedEvent), "contentCompleted")]
[JsonDerivedType(typeof(AgentActivityEvent), "activity")]
[JsonDerivedType(typeof(AgentSystemPromptEvent), "system_prompt")]
[JsonDerivedType(typeof(AgentSessionUpdateEvent), "sessionUpdate")]
[JsonDerivedType(typeof(AgentNotesEvent), "notes")]
[JsonDerivedType(typeof(AgentWorkflowEvent), "workflow")]
[JsonDerivedType(typeof(AgentPlanSnapshotEvent), "planSnapshot")]
[JsonDerivedType(typeof(AgentInteractionEvent), "interaction")]
[JsonDerivedType(typeof(AgentErrorEvent), "error")]
[JsonDerivedType(typeof(AgentGenericPermissionRequest), "permissionGeneric")]
[JsonDerivedType(typeof(AgentCommandPermissionRequest), "permissionCommand")]
[JsonDerivedType(typeof(AgentFileChangePermissionRequest), "permissionFileChange")]
[JsonDerivedType(typeof(AgentSkillPermissionRequest), "permissionSkill")]
[JsonDerivedType(typeof(AgentUserInputRequest), "userInputRequest")]
[JsonDerivedType(typeof(AgentBackgroundTasksEvent), "backgroundTasks")]
[JsonDerivedType(typeof(AgentRemoteControlEvent), "remoteControl")]
[JsonDerivedType(typeof(AgentPromptSuggestionEvent), "promptSuggestion")]
public abstract record AgentEvent(
    [property: JsonPropertyName("backendId")]
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId = null);

/// <summary>
/// Identifies the content channel for a streamed or completed content event.
/// </summary>
public enum AgentContentKind
{
    /// <summary>
    /// User-authored prompt text.
    /// </summary>
    User,

    /// <summary>
    /// Assistant/user-facing response text.
    /// </summary>
    Assistant,

    /// <summary>
    /// Reasoning text emitted by the model.
    /// </summary>
    Reasoning,

    /// <summary>
    /// Reasoning summary text emitted by the provider.
    /// </summary>
    ReasoningSummary,

    /// <summary>
    /// Plan text emitted by the provider.
    /// </summary>
    Plan,

    /// <summary>
    /// Command execution output text.
    /// </summary>
    CommandOutput,

    /// <summary>
    /// File-change output text.
    /// </summary>
    FileChangeOutput,

    /// <summary>
    /// Tool output text.
    /// </summary>
    ToolOutput,

    /// <summary>
    /// Notice-like content that should still render in the timeline.
    /// </summary>
    Notice,
}

/// <summary>
/// Identifies the activity channel for operation lifecycle events.
/// </summary>
public enum AgentActivityKind
{
    /// <summary>
    /// A turn/run lifecycle event.
    /// </summary>
    Turn,

    /// <summary>
    /// A generic tool call lifecycle event.
    /// </summary>
    ToolCall,

    /// <summary>
    /// A command execution lifecycle event.
    /// </summary>
    CommandExecution,

    /// <summary>
    /// A file-change lifecycle event.
    /// </summary>
    FileChange,

    /// <summary>
    /// An MCP tool call lifecycle event.
    /// </summary>
    McpToolCall,

    /// <summary>
    /// A dynamic tool call lifecycle event.
    /// </summary>
    DynamicToolCall,

    /// <summary>
    /// A collaboration/sub-agent tool call lifecycle event.
    /// </summary>
    CollabAgentToolCall,

    /// <summary>
    /// A subagent lifecycle event.
    /// </summary>
    Subagent,

    /// <summary>
    /// A hook lifecycle event.
    /// </summary>
    Hook,

    /// <summary>
    /// A skill lifecycle event.
    /// </summary>
    Skill,

    /// <summary>
    /// A compaction lifecycle event.
    /// </summary>
    Compaction,

    /// <summary>
    /// A web search lifecycle event.
    /// </summary>
    WebSearch,

    /// <summary>
    /// An image generation lifecycle event.
    /// </summary>
    ImageGeneration,
}

/// <summary>
/// Identifies the phase of an activity lifecycle event.
/// </summary>
public enum AgentActivityPhase
{
    /// <summary>
    /// The activity has been requested but not yet started.
    /// </summary>
    Requested,

    /// <summary>
    /// The activity has started.
    /// </summary>
    Started,

    /// <summary>
    /// The activity emitted progress.
    /// </summary>
    Progressed,

    /// <summary>
    /// The activity completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The activity failed.
    /// </summary>
    Failed,

    /// <summary>
    /// The activity was canceled.
    /// </summary>
    Canceled,

    /// <summary>
    /// A selectable activity or agent was selected.
    /// </summary>
    Selected,

    /// <summary>
    /// A selectable activity or agent was deselected.
    /// </summary>
    Deselected,
}

/// <summary>
/// Identifies the kind of a session update event.
/// </summary>
public enum AgentSessionUpdateKind
{
    /// <summary>
    /// The session started.
    /// </summary>
    Started,

    /// <summary>
    /// The session resumed.
    /// </summary>
    Resumed,

    /// <summary>
    /// The session became idle.
    /// </summary>
    Idle,

    /// <summary>
    /// Informational session update.
    /// </summary>
    Info,

    /// <summary>
    /// Warning session update.
    /// </summary>
    Warning,

    /// <summary>
    /// The provider stream is reconnecting or retrying after a transient failure.
    /// </summary>
    Reconnecting,

    /// <summary>
    /// Model changed or was rerouted.
    /// </summary>
    ModelChanged,

    /// <summary>
    /// Session mode changed.
    /// </summary>
    ModeChanged,

    /// <summary>
    /// Session title changed.
    /// </summary>
    TitleChanged,

    /// <summary>
    /// Session context changed.
    /// </summary>
    ContextChanged,

    /// <summary>
    /// Plan state changed.
    /// </summary>
    PlanUpdated,

    /// <summary>
    /// Usage information changed.
    /// </summary>
    UsageUpdated,

    /// <summary>
    /// Compaction started.
    /// </summary>
    CompactionStarted,

    /// <summary>
    /// Compaction completed.
    /// </summary>
    CompactionCompleted,

    /// <summary>
    /// Session handoff occurred.
    /// </summary>
    Handoff,

    /// <summary>
    /// Session truncation occurred.
    /// </summary>
    Truncated,

    /// <summary>
    /// Session shutdown occurred.
    /// </summary>
    Shutdown,

    /// <summary>
    /// A task completed.
    /// </summary>
    TaskCompleted,

    /// <summary>
    /// A diff or patch preview changed.
    /// </summary>
    DiffUpdated,

    /// <summary>
    /// A run failed on its provider (an exhausted quota, an unavailable provider) and the session goes on with the
    /// next target of its tier of models. The details name the reason, the model left, the model taken and the reset.
    /// </summary>
    ProviderFallback,
}

/// <summary>
/// Identifies the kind of a session notes update.
/// </summary>
public enum AgentNotesUpdateKind
{
    /// <summary>
    /// The session notes Markdown was replaced.
    /// </summary>
    Set,

    /// <summary>
    /// The session notes Markdown was cleared.
    /// </summary>
    Cleared,
}

/// <summary>
/// Identifies the kind of a generic interaction lifecycle event.
/// </summary>
public enum AgentInteractionKind
{
    /// <summary>
    /// A permission request was resolved.
    /// </summary>
    PermissionResolved,

    /// <summary>
    /// A user-input request was resolved.
    /// </summary>
    UserInputResolved,
}

/// <summary>
/// Identifies how a plan changed.
/// </summary>
public enum AgentPlanChangeKind
{
    /// <summary>
    /// The plan was created.
    /// </summary>
    Created,

    /// <summary>
    /// The plan was updated.
    /// </summary>
    Updated,

    /// <summary>
    /// The plan was deleted.
    /// </summary>
    Deleted,
}

/// <summary>
/// Identifies the status of a structured plan step.
/// </summary>
public enum AgentPlanStepStatus
{
    /// <summary>
    /// The step is pending.
    /// </summary>
    Pending,

    /// <summary>
    /// The step is in progress.
    /// </summary>
    InProgress,

    /// <summary>
    /// The step completed.
    /// </summary>
    Completed,
}

/// <summary>
/// A raw, provider-specific event emitted when no normalized mapping exists.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="BackendEventType">Provider raw-event type identifier.</param>
/// <param name="Raw">Raw provider payload.</param>
/// <param name="RunId">Optional run identifier.</param>
public sealed record AgentRawEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    string BackendEventType,
    JsonElement Raw,
    AgentRunId? RunId = null)
    : AgentEvent(ProviderId, SessionId, Timestamp, RunId);

/// <summary>
/// Streaming delta of normalized content.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="Kind">The content channel.</param>
/// <param name="ContentId">Stable content identifier.</param>
/// <param name="ParentActivityId">Optional parent activity identifier.</param>
/// <param name="Delta">Delta content.</param>
/// <param name="Details">Optional structured content metadata.</param>
public sealed record AgentContentDeltaEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentContentKind Kind,
    string ContentId,
    string? ParentActivityId,
    string Delta,
    JsonElement? Details = null)
    : AgentEvent(ProviderId, SessionId, Timestamp, RunId);

/// <summary>
/// Finalized normalized content.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="Kind">The content channel.</param>
/// <param name="ContentId">Stable content identifier.</param>
/// <param name="ParentActivityId">Optional parent activity identifier.</param>
/// <param name="Content">The finalized content.</param>
/// <param name="Details">Optional structured content metadata.</param>
/// <param name="AskId">Optional ask identifier associated with a user prompt.</param>
/// <param name="SourceSessionId">The session of the agent that sent a user prompt; null for a prompt of a person or of the host.</param>
/// <param name="Provenance">
/// The provenance of a user prompt the host sent (see <see cref="AgentPromptProvenance"/>); null for a prompt the user typed,
/// and for the prompts of the journals written before it was recorded.
/// </param>
public sealed record AgentContentCompletedEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentContentKind Kind,
    string ContentId,
    string? ParentActivityId,
    string Content,
    JsonElement? Details = null,
    [property: JsonPropertyName("ask_id")] string? AskId = null,
    [property: JsonPropertyName("source_session_id")] string? SourceSessionId = null,
    [property: JsonPropertyName("provenance")] string? Provenance = null)
    : AgentEvent(ProviderId, SessionId, Timestamp, RunId);

/// <summary>
/// Generic activity lifecycle event.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="Kind">The activity channel.</param>
/// <param name="Phase">The lifecycle phase.</param>
/// <param name="ActivityId">Stable activity identifier.</param>
/// <param name="ParentActivityId">Optional parent activity identifier.</param>
/// <param name="Name">Optional activity display name.</param>
/// <param name="Message">Optional activity message.</param>
/// <param name="Details">Optional structured details.</param>
public sealed record AgentActivityEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentActivityKind Kind,
    AgentActivityPhase Phase,
    string ActivityId,
    string? ParentActivityId,
    string? Name,
    string? Message,
    JsonElement? Details = null)
    : AgentEvent(ProviderId, SessionId, Timestamp, RunId);

/// <summary>
/// Auditable system prompt event containing the logical prompt applied to a session turn.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="Reason">Reason the prompt event was emitted.</param>
/// <param name="EffectivePromptHash">Stable effective prompt hash.</param>
/// <param name="AgentPromptId">Agent prompt identifier used to compose the developer instructions.</param>
/// <param name="SystemMessage">Logical system message.</param>
/// <param name="DeveloperInstructions">Logical developer instructions.</param>
/// <param name="ProviderPayloadSummary">Provider payload summary.</param>
/// <param name="Manifest">Prompt manifest.</param>
/// <param name="Statistics">Prompt statistics.</param>
/// <param name="Change">Change summary compared with the previous prompt event.</param>
/// <param name="AgentPromptUsage">Agent prompt selection details, when known.</param>
public sealed record AgentSystemPromptEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    string Reason,
    string EffectivePromptHash,
    string? AgentPromptId,
    string? SystemMessage,
    string? DeveloperInstructions,
    AgentSystemPromptProviderPayloadSummary ProviderPayloadSummary,
    JsonElement? Manifest,
    AgentSystemPromptStatistics Statistics,
    AgentSystemPromptChangeSummary Change,
    AgentPromptUsageInfo? AgentPromptUsage = null)
    : AgentEvent(ProviderId, SessionId, Timestamp, RunId);

/// <summary>
/// Describes the agent prompt used to compose a system prompt event.
/// </summary>
/// <param name="PromptName">File-derived prompt identifier.</param>
/// <param name="DisplayName">User-facing prompt name, when known.</param>
/// <param name="SourcePath">Prompt source path, project-relative when it is under the project root.</param>
public sealed record AgentPromptUsageInfo(string PromptName, string? DisplayName, string? SourcePath);

/// <summary>
/// Provider payload mapping summary for a system prompt event.
/// </summary>
/// <param name="ChannelMapping">Channel mapping label.</param>
/// <param name="AppliedToProvider">Whether the prompt was applied to the provider request.</param>
/// <param name="Lossy">Whether the mapping was lossy.</param>
public sealed record AgentSystemPromptProviderPayloadSummary(string ChannelMapping, bool AppliedToProvider, bool Lossy);

/// <summary>
/// Approximate system prompt statistics.
/// </summary>
/// <param name="SystemApproxTokens">Approximate system token count.</param>
/// <param name="DeveloperApproxTokens">Approximate developer token count.</param>
/// <param name="TotalApproxTokens">Approximate total token count.</param>
/// <param name="SystemChars">System character count.</param>
/// <param name="DeveloperChars">Developer character count.</param>
public sealed record AgentSystemPromptStatistics(int SystemApproxTokens, int DeveloperApproxTokens, int TotalApproxTokens, int SystemChars, int DeveloperChars);

/// <summary>
/// Change summary for a system prompt event.
/// </summary>
/// <param name="Kind">Change kind.</param>
/// <param name="AddedParts">Added prompt part keys.</param>
/// <param name="RemovedParts">Removed prompt part keys.</param>
/// <param name="ChangedParts">Changed prompt part keys.</param>
public sealed record AgentSystemPromptChangeSummary(string Kind, IReadOnlyList<string> AddedParts, IReadOnlyList<string> RemovedParts, IReadOnlyList<string> ChangedParts);

/// <summary>
/// Generic session update event.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="Kind">The session update kind.</param>
/// <param name="Message">Optional update message.</param>
/// <param name="Details">Optional structured details.</param>
/// <param name="Usage">Optional normalized usage payload.</param>
public sealed record AgentSessionUpdateEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentSessionUpdateKind Kind,
    string? Message,
    JsonElement? Details = null,
    AgentSessionUsage? Usage = null)
    : AgentEvent(ProviderId, SessionId, Timestamp, RunId);

/// <summary>
/// Session-scoped sticky notes state update.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="Kind">The notes update kind.</param>
/// <param name="Markdown">The notes Markdown after the update.</param>
public sealed record AgentNotesEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentNotesUpdateKind Kind,
    string Markdown)
    : AgentEvent(ProviderId, SessionId, Timestamp, RunId);

/// <summary>What a record of a run of a script of Code Mode says.</summary>
public enum AgentWorkflowEventKind
{
    /// <summary>The run started: its title, and the tool call that runs it.</summary>
    RunStarted,

    /// <summary>A step of the script started.</summary>
    StepStarted,

    /// <summary>A step of the script ended: its status and its duration.</summary>
    StepEnded,

    /// <summary>The script started a session of its own (<c>agent()</c>): its id, its step.</summary>
    SessionStarted,

    /// <summary>A session the script started ended: its status, its duration and its tokens.</summary>
    SessionEnded,

    /// <summary>The run ended: its status, its duration and its calls.</summary>
    RunEnded,

    /// <summary>
    /// The run waits for the user: <see cref="AgentWorkflowEvent.Status"/> says for what (<c>question</c> for <c>ask()</c>,
    /// <c>approval</c> for a call of the script to approve), with its step and the question.
    /// </summary>
    WaitStarted,

    /// <summary>The run no longer waits for the user: the question was answered, dismissed or withdrawn.</summary>
    WaitEnded,

    /// <summary>
    /// A script of the run declares its steps (<c>meta.steps</c>): <see cref="AgentWorkflowEvent.Steps"/>, which its graph
    /// draws before they run. <see cref="AgentWorkflowEvent.Title"/> is the title of the script.
    /// </summary>
    StepsDeclared,

    /// <summary>
    /// <c>workflow("name")</c> resolved a script of the library: <see cref="AgentWorkflowEvent.ScriptName"/> and
    /// <see cref="AgentWorkflowEvent.ScriptScope"/>. A run that writes none ran only the code sent to <c>exec</c>.
    /// </summary>
    ScriptLoaded,

    /// <summary>
    /// The script showed the user a part (<c>present(markdown)</c>): <see cref="AgentWorkflowEvent.Markdown"/>, with its step.
    /// The card of the run shows the parts in their order, as they come.
    /// </summary>
    Presented,
}

/// <summary>A step a script declares in its <c>meta</c>, for the graph of its run.</summary>
/// <param name="Id">The id that <c>step()</c> names.</param>
/// <param name="Title">The title of its box.</param>
public sealed record AgentWorkflowStep(string Id, string Title)
{
    /// <summary>Gets the steps it runs after: null for the step declared before it, empty for the start.</summary>
    public IReadOnlyList<string>? After { get; init; }

    /// <summary>Gets whether the step fans out: one instance per item at run time.</summary>
    public bool Each { get; init; }
}

/// <summary>
/// A record of a run of a script of Code Mode, in the journal of the session that runs it. Its <see cref="AgentEvent.RunId"/>
/// is always null: a run goes on after the turn that started it, and its records belong to no turn.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session that runs the script.</param>
/// <param name="Timestamp">When it happened.</param>
/// <param name="RunId">Always null.</param>
/// <param name="Kind">What happened.</param>
/// <param name="WorkflowRunId">The run of the script, such as <c>run-7f3c2a</c>.</param>
public sealed record AgentWorkflowEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentWorkflowEventKind Kind,
    string WorkflowRunId)
    : AgentEvent(ProviderId, SessionId, Timestamp, RunId)
{
    /// <summary>Gets the tool call that runs the script (<c>exec</c>), when known.</summary>
    public string? CallId { get; init; }

    /// <summary>Gets the title of the run, or the title of the session it started.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the step, for the records of a step or of a session.</summary>
    public string? StepId { get; init; }

    /// <summary>Gets the session the script started, for the records of a session.</summary>
    public string? ChildSessionId { get; init; }

    /// <summary>Gets the provider the session the script started runs on, for the start of a session.</summary>
    public string? ProviderKey { get; init; }

    /// <summary>Gets the model the session the script started runs with, for the start of a session.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets how it ended (<c>completed</c>, <c>failed</c>, <c>cancelled</c>...), for the records of an end.</summary>
    public string? Status { get; init; }

    /// <summary>Gets what went wrong, for a failed end.</summary>
    public string? Message { get; init; }

    /// <summary>Gets the duration in milliseconds, for the records of an end.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Gets the calls the run made, for its end.</summary>
    public int? Calls { get; init; }

    /// <summary>Gets the input tokens of a session the script started, for its end.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Gets the output tokens of a session the script started, for its end.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Gets the key a resume matches the step by, such as <c>merge#1</c>, for the records of a step.</summary>
    public string? StepKey { get; init; }

    /// <summary>Gets whether the step gave the result a previous run recorded instead of running, for the end of a step.</summary>
    public bool? Replayed { get; init; }

    /// <summary>Gets the run this one resumes or reruns over its recorded steps, for the start of a run.</summary>
    public string? ResumesRunId { get; init; }

    /// <summary>
    /// Gets when the run wakes: for the wait of a sleep (<see cref="AgentWorkflowEventKind.WaitStarted"/> with the status
    /// <c>sleep</c>), and for the end of an interrupted run that resumes by itself then.
    /// </summary>
    public DateTimeOffset? WakeAt { get; init; }

    /// <summary>Gets the steps a script declares, for <see cref="AgentWorkflowEventKind.StepsDeclared"/>.</summary>
    public IReadOnlyList<AgentWorkflowStep>? Steps { get; init; }

    /// <summary>Gets the name of the library script, for <see cref="AgentWorkflowEventKind.ScriptLoaded"/>.</summary>
    public string? ScriptName { get; init; }

    /// <summary>
    /// Gets where the library script comes from (<c>project</c>, <c>user</c> or <c>sample</c>), for
    /// <see cref="AgentWorkflowEventKind.ScriptLoaded"/>.
    /// </summary>
    public string? ScriptScope { get; init; }

    /// <summary>
    /// Gets whether the run writes a <see cref="AgentWorkflowEventKind.ScriptLoaded"/> record for each library script it
    /// loads, for the start of a run: a run recorded before these records existed has none, and which script it ran is not known.
    /// </summary>
    public bool? RecordsScripts { get; init; }

    /// <summary>
    /// Gets whether the run is the one the host started to run a script of the library for the user (the Workflows tab, an
    /// automation, <c>alta script run</c>), for the start of a run: no agent works from its value, which its card shows as
    /// Markdown. Null for a run of an agent, whose value is data for the agent.
    /// </summary>
    public bool? ForUser { get; init; }

    /// <summary>Gets the Markdown the script showed the user, for <see cref="AgentWorkflowEventKind.Presented"/>.</summary>
    public string? Markdown { get; init; }
}

/// <summary>
/// Structured plan snapshot event.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="Snapshot">The structured plan snapshot.</param>
public sealed record AgentPlanSnapshotEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentPlanSnapshot Snapshot)
    : AgentEvent(ProviderId, SessionId, Timestamp, RunId);

/// <summary>
/// Structured plan snapshot payload.
/// </summary>
/// <param name="ChangeKind">Optional change kind.</param>
/// <param name="Explanation">Optional plan explanation.</param>
/// <param name="Steps">Optional plan steps.</param>
public sealed record AgentPlanSnapshot(
    AgentPlanChangeKind? ChangeKind,
    string? Explanation,
    IReadOnlyList<AgentPlanStep>? Steps);

/// <summary>
/// Structured plan step payload.
/// </summary>
/// <param name="Text">The step text.</param>
/// <param name="Status">The optional step status.</param>
public sealed record AgentPlanStep(
    string Text,
    AgentPlanStepStatus? Status);

/// <summary>
/// Generic interaction lifecycle event.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="Kind">The interaction kind.</param>
/// <param name="InteractionId">Stable interaction identifier.</param>
/// <param name="Message">Optional interaction message.</param>
/// <param name="Details">Optional structured details.</param>
public sealed record AgentInteractionEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    AgentInteractionKind Kind,
    string InteractionId,
    string? Message,
    JsonElement? Details = null)
    : AgentEvent(ProviderId, SessionId, Timestamp, RunId);

/// <summary>
/// Represents an error event.
/// </summary>
public sealed record AgentErrorEvent : AgentEvent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AgentErrorEvent"/> class.
    /// </summary>
    /// <param name="ProviderId">The model provider identifier.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="timestamp">Event timestamp.</param>
    /// <param name="message">Error message.</param>
    /// <param name="exceptionInfo">Optional JSON-safe exception payload.</param>
    /// <param name="runId">Optional run identifier.</param>
    [JsonConstructor]
    internal AgentErrorEvent(
        ModelProviderId ProviderId,
        string sessionId,
        DateTimeOffset timestamp,
        string message,
        AgentExceptionInfo? exceptionInfo = null,
        AgentRunId? runId = null)
        : base(ProviderId, sessionId, timestamp, runId)
    {
        Message = message;
        ExceptionInfo = exceptionInfo;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentErrorEvent"/> class.
    /// </summary>
    /// <param name="ProviderId">The model provider identifier.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="timestamp">Event timestamp.</param>
    /// <param name="message">Error message.</param>
    /// <param name="exception">Optional runtime exception.</param>
    /// <param name="runId">Optional run identifier.</param>
    public AgentErrorEvent(
        ModelProviderId ProviderId,
        string sessionId,
        DateTimeOffset timestamp,
        string message,
        Exception? exception = null,
        AgentRunId? runId = null)
        : base(ProviderId, sessionId, timestamp, runId)
    {
        Message = message;
        Exception = exception;
        ExceptionInfo = exception is null ? null : AgentExceptionInfo.FromException(exception);
    }

    /// <summary>
    /// Gets the error message.
    /// </summary>
    public string Message { get; init; }

    /// <summary>
    /// Gets the optional runtime exception.
    /// </summary>
    [JsonIgnore]
    public Exception? Exception { get; init; }

    /// <summary>
    /// Gets a JSON-safe exception payload for display and diagnostics.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentExceptionInfo? ExceptionInfo { get; init; }

    /// <summary>
    /// Gets what kind of failure ended the run, when the provider said it (an exhausted quota, a provider that stays
    /// unavailable...), or <see langword="null"/> when it is not known.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentFailureInfo? Failure { get; init; }
}

/// <summary>What kind of failure ended a run, as the provider said it.</summary>
public enum AgentFailureKind
{
    /// <summary>Any other failure: a tool, a malformed request, a context overflow, an unknown error.</summary>
    Other,

    /// <summary>The quota, credits or allowance of the account are exhausted until they reset.</summary>
    QuotaExhausted,

    /// <summary>The provider limits the rate of requests for a short while.</summary>
    RateLimited,

    /// <summary>The provider stays unavailable (outage, overload, network) after its own retries.</summary>
    Unavailable,

    /// <summary>The account is not signed in or not allowed.</summary>
    Auth,

    /// <summary>
    /// The account cannot use the model it asked for (not found, not enabled for it, not in its plan); its other
    /// models may still answer.
    /// </summary>
    ModelUnavailable,
}

/// <summary>What kind of failure ended a run, and when it ends when the provider said it.</summary>
/// <param name="Kind">The kind of failure.</param>
/// <param name="ResetsAt">When the quota or the limit resets, when known.</param>
/// <param name="RetryAfterSeconds">How long the provider asks to wait before a new request, when it says it.</param>
public sealed record AgentFailureInfo(
    AgentFailureKind Kind,
    DateTimeOffset? ResetsAt = null,
    int? RetryAfterSeconds = null);

/// <summary>
/// JSON-safe exception payload.
/// </summary>
/// <param name="Type">The exception type name.</param>
/// <param name="Message">The exception message.</param>
/// <param name="StackTrace">Optional stack trace.</param>
/// <param name="Source">Optional exception source.</param>
/// <param name="HResult">Optional HRESULT.</param>
/// <param name="InnerException">Optional inner exception payload.</param>
public sealed record AgentExceptionInfo(
    string Type,
    string Message,
    string? StackTrace = null,
    string? Source = null,
    int? HResult = null,
    AgentExceptionInfo? InnerException = null)
{
    /// <summary>
    /// Gets what the provider attached to the exception beyond its message: its error code, the HTTP status, the
    /// request id, the payload it returned... (see the keys of <c>AgentErrorData</c>), with secrets redacted and
    /// values cut to a bounded length; <see langword="null"/> when there is nothing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    /// <summary>
    /// Returns the entries of <see cref="Data"/> of this exception and of the exceptions it wraps, an entry of an
    /// outer exception before the same entry of an inner one.
    /// </summary>
    /// <returns>The entries, empty when no exception of the chain has any.</returns>
    public IReadOnlyDictionary<string, string> CollectData()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var current = this; current is not null; current = current.InnerException)
        {
            if (current.Data is null)
            {
                continue;
            }

            foreach (var (key, value) in current.Data)
            {
                result.TryAdd(key, value);
            }
        }

        return result;
    }

    internal static AgentExceptionInfo FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new AgentExceptionInfo(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.StackTrace,
            exception.Source,
            exception.HResult,
            exception.InnerException is null ? null : FromException(exception.InnerException))
        {
            Data = AgentErrorData.Collect(exception),
        };
    }
}
