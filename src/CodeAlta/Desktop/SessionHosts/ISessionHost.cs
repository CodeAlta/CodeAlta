using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Jobs;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.SessionHosts;

/// <summary>
/// A host of sessions the page of the window reads and drives: the runtime of this process, or (later) a node of
/// the fleet that owns sessions on another machine.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam between the RPC services of the page (<c>Desktop/Rpc</c>) and whoever runs a session. The
/// services keep what belongs to the page (the host epoch of the window, the validation of requests, the wire
/// records and their limits, the index of command receipts) and ask the host of the session, resolved by
/// <see cref="SessionHostRegistry"/>, for everything else. <see cref="LocalSessionHost"/> forwards to the
/// <c>CodeAltaHost</c> of the process.
/// </para>
/// <para>
/// What a remote host (the client side of a node's API over a session channel) must implement, and keep:
/// </para>
/// <list type="bullet">
/// <item><description><b>The list</b>: <see cref="ReadSnapshotAsync"/> returns the projects and sessions of that
/// node; the registry, not the host, merges the lists of several hosts.</description></item>
/// <item><description><b>History windows</b>: bounded pages with the journal's cursors, never a whole journal; a
/// cursor of one host is only ever given back to that host.</description></item>
/// <item><description><b>The live display</b>: full replacements with a revision and a gap flag, lossy by design:
/// a slow or reconnected viewer gets the latest state, not a backlog. Tool and job output are the same kind of
/// stream.</description></item>
/// <item><description><b>Owned commands</b>: admissions bound to exact identities (client request id, runtime
/// instance, attachment generation, run). A retry with the same client request id must return the first receipt,
/// and a stale identity is refused, never retargeted. A remote admission that cannot be confirmed is reported as
/// such, never as a success.</description></item>
/// <item><description><b>Asks, permissions and user input</b>: listed per session, answered by their exact
/// handles; an answer to a handle the owner no longer holds is refused.</description></item>
/// <item><description><b>Jobs</b>: a job is only found through the session it belongs to.</description></item>
/// <item><description><b>Creation</b>: a new session on that host, with the project, provider and space as that
/// host knows them (the local catalog's descriptors are only meaningful for the local host).</description></item>
/// </list>
/// <para>
/// Every member may throw what the runtime throws for the same call (<see cref="ObjectDisposedException"/> while
/// the host closes, <see cref="ArgumentException"/> for an invalid identity, <see cref="OperationCanceledException"/>);
/// the services already turn those into wire statuses. A remote host adds no exception of its own kind for an
/// offline machine: it reports it the way the runtime reports a host that is closing, so the page shows a retryable
/// state.
/// </para>
/// </remarks>
internal interface ISessionHost
{
    /// <summary>Gets the machine of the host; null for the host of this process.</summary>
    string? MachineId { get; }

    /// <summary>
    /// Gets whether the host lists a session, as its last list said: the registry gives a session this process does not
    /// own to the one host of a machine that lists it, and to the host of this process otherwise.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    bool Lists(string sessionId);

    /// <summary>
    /// Gets whether the host owns a session by its own records, never by what another machine says: the host of this
    /// process owns the sessions it lists and did not give away; a remote host owns nothing here (its machine's word is
    /// <see cref="Lists"/>). The registry keeps a session the host of this process owns, whatever a machine lists.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    bool Owns(string sessionId);

    /// <summary>Gets whether the host admitted an operation and still keeps its receipt, for the abort of that operation.</summary>
    /// <param name="operationId">The operation.</param>
    bool Admitted(Guid operationId);

    /// <summary>Reads the projects and sessions of the host.</summary>
    Task<OwnedWorkspaceSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Reads a bounded page of the journal of a session, forward from a cursor.</summary>
    Task<AgentSessionHistoryPage> ReadHistoryPageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken);

    /// <summary>Reads a bounded page of the journal of a session, backward from a cursor or from its end.</summary>
    Task<AgentSessionHistoryPage> ReadHistoryTailPageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken);

    /// <summary>Reads a bounded page of the timeline of a session.</summary>
    Task<AgentSessionHistoryPage> ReadTimelinePageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken);

    /// <summary>Reads the end of the timeline of a session, or nothing when it did not change since a known stamp.</summary>
    Task<OwnedTimelineTail> ReadTimelineTailAsync(string sessionId, string? knownStamp, CancellationToken cancellationToken);

    /// <summary>Reads a part of the source text of a range of a journal, at a revision of that journal.</summary>
    Task<AgentHistorySourceChunk> ReadHistorySourceAsync(AgentHistoryRevision revision, long start, long end, long offset, CancellationToken cancellationToken);

    /// <summary>Reads the record of a journal at an offset; null when there is none.</summary>
    Task<AgentEvent?> ReadHistoryRecordAsync(string sessionId, long offset, CancellationToken cancellationToken);

    /// <summary>Reads the system prompt record of a journal at an offset.</summary>
    Task<AgentSystemPromptRecord> ReadSystemPromptAsync(string sessionId, long offset, CancellationToken cancellationToken);

    /// <summary>Observes the display projection of a session: a full replacement each time it changes.</summary>
    IAsyncEnumerable<RuntimeDisplayReplacement> ObserveDisplayAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Reads the current runtime state of a session.</summary>
    Task<SessionRuntimeCurrentState> ReadRuntimeStateAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Observes the runtime state of a session in the scope (global or project) the page shows it in.</summary>
    Task<OwnedRuntimeObservation> ObserveRuntimeStateAsync(string sessionId, DateTimeOffset createdAt, string scope, string? projectId,
        string? projectPath, CancellationToken cancellationToken);

    /// <summary>Observes what a running tool call of a session writes.</summary>
    IAsyncEnumerable<RuntimeToolOutputUpdate> ObserveToolOutputAsync(string sessionId, string activityId, CancellationToken cancellationToken);

    /// <summary>Gets how many command receipts the host keeps, so the page's index keeps as many.</summary>
    int ReceiptCapacity { get; }

    /// <summary>Admits a prompt sent to a session.</summary>
    OwnedSessionCommandAdmission AdmitSend(OwnedTextSendRequest request, CancellationToken cancellationToken);

    /// <summary>Admits the abort of an operation the host admitted.</summary>
    OwnedSessionCommandAdmission AdmitAbort(OwnedAbortRequest request, CancellationToken cancellationToken);

    /// <summary>Admits a steering message for the run of a session.</summary>
    OwnedSessionCommandAdmission AdmitSteer(OwnedTextSteerRequest request, CancellationToken cancellationToken);

    /// <summary>Admits the compaction of a session.</summary>
    OwnedSessionCommandAdmission AdmitCompact(OwnedCompactRequest request, CancellationToken cancellationToken);

    /// <summary>Admits the cancellation of a run of a session.</summary>
    OwnedSessionCommandAdmission AdmitAbortRun(OwnedAbortRunRequest request, CancellationToken cancellationToken);

    /// <summary>Gets whether the host answers the asks of its sessions.</summary>
    bool AsksEnabled { get; }

    /// <summary>Lists the pending ask of a session and the last answer.</summary>
    OwnedAskPage ListAsks(string sessionId);

    /// <summary>Reads what became of an answer; null when the host does not know it.</summary>
    OwnedAskDisposition? ObserveAsk(Guid actionId, OwnedAskHandle handle);

    /// <summary>Answers an ask.</summary>
    Task<OwnedAskDisposition> AnswerAskAsync(OwnedAskAction action, CancellationToken cancellationToken);

    /// <summary>Cancels an ask.</summary>
    Task<OwnedAskDisposition> CancelAskAsync(OwnedAskAction action, CancellationToken cancellationToken);

    /// <summary>Lists the permission requests of a session that wait for the user.</summary>
    ValueTask<SessionOwnedPermissionPage> ListPermissionsAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Resolves a permission request by its exact handle; false when the host no longer holds it.</summary>
    ValueTask<bool> ResolvePermissionAsync(SessionOwnedPermissionHandle handle, AgentPermissionDecisionKind decision, CancellationToken cancellationToken);

    /// <summary>Lists the questions of a provider of a session that wait for the user.</summary>
    ValueTask<SessionOwnedUserInputPage> ListUserInputsAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Answers a question of a provider by its exact handle; false when the host no longer holds it.</summary>
    ValueTask<bool> ResolveUserInputAsync(SessionOwnedUserInputHandle handle, IReadOnlyList<SessionOwnedUserInputAnswer> answers, CancellationToken cancellationToken);

    /// <summary>Cancels a question of a provider by its exact handle; false when the host no longer holds it.</summary>
    ValueTask<bool> CancelUserInputAsync(SessionOwnedUserInputHandle handle, CancellationToken cancellationToken);

    /// <summary>Finds a background job of a session; null when the session has no job with that identifier.</summary>
    SessionJob? FindJob(string sessionId, string jobId);

    /// <summary>Observes what a background job of a session writes.</summary>
    IAsyncEnumerable<RuntimeToolOutputUpdate> ObserveJobOutputAsync(string sessionId, string jobId, CancellationToken cancellationToken);

    /// <summary>Stops a background task a provider runs for a session; returns the status of the request.</summary>
    Task<string> StopBackgroundTaskAsync(string sessionId, string taskId, CancellationToken cancellationToken);

    /// <summary>Gets the permission modes a new session of a provider can be created with.</summary>
    OwnedPermissionModeChoices GetNewSessionPermissionModes(string providerKey);

    /// <summary>Reads the agent prompts a new session can start with; null when the host has none to offer.</summary>
    Task<OwnedDraftPrompts?> GetDraftPromptsAsync(OwnedProjectReferenceScope? scope, string? spaceId, CancellationToken cancellationToken);

    /// <summary>Creates a session on the host, before its first prompt.</summary>
    /// <param name="project">The project of the session; null for a session of no project.</param>
    /// <param name="provider">The provider of the session.</param>
    /// <param name="title">The title, or null.</param>
    /// <param name="worktreeDirectory">The worktree of the project the session works in, or null for the project's folder.</param>
    /// <param name="spaceId">The space, or null for the one of the project.</param>
    /// <param name="permissionMode">The permission mode, or null for the default.</param>
    /// <param name="options">The options of the session, or null for the defaults.</param>
    Task<SessionViewDescriptor> CreateSessionAsync(ProjectDescriptor? project, ModelProviderDescriptor provider, string? title, string? worktreeDirectory,
        string? spaceId, string? permissionMode, AgentSessionOptions? options);
}
