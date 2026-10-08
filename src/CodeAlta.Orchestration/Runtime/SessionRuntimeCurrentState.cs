using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What one session of the runtime is doing, for a summary of every session at once.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ProjectId">The project of the session; <see langword="null"/> for a chat.</param>
/// <param name="Title">The title of the session, as the runtime knows it.</param>
/// <param name="Running">Whether a run is in flight or queued prompts are being sent.</param>
/// <param name="BackgroundTasks">How many tasks its provider goes on doing in the background.</param>
/// <param name="Failed">Whether its last run ended with an error and no run started since.</param>
public sealed record SessionRuntimeOverview(string SessionId, string? ProjectId, string Title, bool Running, int BackgroundTasks, bool Failed);

/// <summary>A session that is at work, and the folder it works in.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Folder">The folder its tools run in.</param>
/// <param name="Worktree">Whether that folder is a git worktree of its project, not the folder of the project.</param>
public sealed record SessionWorkFolder(string SessionId, string Folder, bool Worktree);

/// <summary>Immutable point-in-time facts known to this runtime, not provider quiescence or recoverable history.</summary>
/// <param name="RuntimeInstanceId">Identity of this runtime instance, independent of Display epochs.</param>
/// <param name="SessionId">The queried durable session identifier.</param>
/// <param name="CoordinatorTransitionInProgress">Whether coordinator creation/replacement/detachment has a retained transition.</param>
/// <param name="Entry">The observed runtime entry, or null when none is retained. Absence does not mean idle, completed, or no durable session.</param>
public sealed record SessionRuntimeCurrentState(Guid RuntimeInstanceId, string SessionId,
    bool CoordinatorTransitionInProgress, SessionRuntimeCurrentEntry? Entry);

/// <summary>
/// Immutable captured entry facts. No active run recorded does not prove provider inactivity.
/// Queue depth is unknown: only the in-memory drain flag is captured. Configuration is runtime-captured,
/// not verified provider-effective settings. This value carries no effect acknowledgement or state revision.
/// </summary>
/// <param name="AttachmentGeneration">Existing attachment ordinal, scoped by runtime instance. Identifies replacement, not state ordering.</param>
/// <param name="IsTerminated">Whether the runtime entry observed a Shutdown event, not successful run completion.</param>
/// <param name="IsRetiring">Whether attachment handle admission was closed at capture.</param>
/// <param name="ActiveRunId">Recorded active run identifier, or null when none is recorded.</param>
/// <param name="QueueDrainInProgress">Whether the entry has a queue submission drain in progress.</param>
/// <param name="ProviderId">Captured provider identifier.</param>
/// <param name="ProviderKey">Captured provider configuration key, not credentials.</param>
/// <param name="ModelId">Captured model setting, when supplied.</param>
/// <param name="ReasoningEffort">Captured reasoning setting, when supplied.</param>
/// <param name="AgentPromptId">Captured coordinator prompt identifier.</param>
/// <param name="PendingAgentPromptId">Separately pending prompt selection, not yet the captured coordinator setting.</param>
public sealed record SessionRuntimeCurrentEntry(long AttachmentGeneration, bool IsTerminated, bool IsRetiring,
    string? ActiveRunId, bool QueueDrainInProgress, string ProviderId, string ProviderKey, string? ModelId,
    AgentReasoningEffort? ReasoningEffort, string? AgentPromptId, string? PendingAgentPromptId)
{
    /// <summary>Last valid timestamp admitted from this attachment's session/provider-correlated agent events, not historical latest activity.</summary>
    public SessionRuntimeActivity? Activity { get; init; }

    /// <summary>
    /// What the provider of this attachment does in the background outside its runs, as its last event listed it:
    /// the tasks that go on, then the last ones that failed or were stopped. Bounded; empty for a provider that
    /// has no such tasks.
    /// </summary>
    public IReadOnlyList<SessionRuntimeBackgroundTask> BackgroundTasks { get; init; } = [];
}

/// <summary>A background task of a session, as the runtime last heard of it.</summary>
/// <param name="TaskId">The identity of the task for its provider.</param>
/// <param name="Kind">What the task is: <c>command</c>, <c>agent</c>, <c>workflow</c>, or the name the provider gives it.</param>
/// <param name="Description">What the task does, when the provider says it.</param>
/// <param name="ToolCallId">The tool call that started the task, when the provider says it.</param>
/// <param name="StartedAt">When the task was first known; null for a task that ended.</param>
/// <param name="Outcome">Null while the task goes on; how it ended otherwise.</param>
public sealed record SessionRuntimeBackgroundTask(string TaskId, string Kind, string? Description, string? ToolCallId,
    DateTimeOffset? StartedAt, AgentBackgroundTaskOutcome? Outcome);

/// <summary>Actor-owned attachment-local activity. Arrival order, not maximum timestamp; never a journal total.</summary>
/// <param name="Timestamp">Timestamp of the last valid matching admitted agent event, otherwise unknown.</param>
/// <param name="AdmittedEvents">Matching valid callback count, saturating at Int64 maximum.</param>
/// <param name="OmittedEvents">Foreign-identity or invalid-time callbacks rejected on the current nonretiring attachment.</param>
public sealed record SessionRuntimeActivity(DateTimeOffset? Timestamp, long AdmittedEvents, long OmittedEvents);
