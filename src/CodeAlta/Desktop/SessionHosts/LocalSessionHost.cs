using System.Collections.Immutable;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Jobs;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.SessionHosts;

/// <summary>The sessions of this process: every call goes to its <see cref="CodeAltaHost"/>, unchanged.</summary>
internal sealed class LocalSessionHost : ISessionHost
{
    // The sessions of the last list of this process (the rows the window shows as its own), with those it created since.
    private ImmutableHashSet<string> _owned = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
    private ImmutableHashSet<string> _createdSinceList = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the local host over the host of the process.</summary>
    /// <param name="host">The host of the process.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    public LocalSessionHost(CodeAltaHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
    }

    /// <summary>
    /// Gets the host of the process, for what the services still ask it directly: what only exists on this machine
    /// (catalogs, settings, files) and the session commands no remote host offers yet.
    /// </summary>
    public CodeAltaHost Host { get; }

    /// <inheritdoc />
    public string? MachineId => null;

    /// <inheritdoc />
    /// <remarks>The host of this process is the one of every session no other host lists: it answers true.</remarks>
    public bool Lists(string sessionId) => true;

    /// <inheritdoc />
    /// <remarks>
    /// A session of its last list, or one it created since, that it did not give to another machine (a move): the rows the
    /// window shows as sessions of this computer.
    /// </remarks>
    public bool Owns(string sessionId) => sessionId is not null && Volatile.Read(ref _owned).Contains(sessionId)
        && Runtime.Relinquishments.Read(sessionId) is not { IsRelinquished: true };

    /// <inheritdoc />
    /// <remarks>The host of this process is the one of every operation no other host admitted: it answers true.</remarks>
    public bool Admitted(Guid operationId) => true;

    private OwnedSessionWorkspace Reads => Host.WorkspaceReads;

    private OwnedSessionCommandService Commands => Host.Commands;

    private SessionRuntimeService Runtime => Host.RuntimeService;

    /// <inheritdoc />
    public async Task<OwnedWorkspaceSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var created = Interlocked.Exchange(ref _createdSinceList, _createdSinceList.Clear());
        var snapshot = await Reads.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        // A session created while the list was read is kept: the next list has it.
        var owned = snapshot.Sessions.Select(static session => session.SessionId).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
            .Union(created).Union(Volatile.Read(ref _createdSinceList));
        Volatile.Write(ref _owned, owned);
        return snapshot;
    }

    /// <inheritdoc />
    public Task<AgentSessionHistoryPage> ReadHistoryPageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
        => Reads.ReadHistoryPageAsync(sessionId, cursor, cancellationToken);

    /// <inheritdoc />
    public Task<AgentSessionHistoryPage> ReadHistoryTailPageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
        => Reads.ReadHistoryTailPageAsync(sessionId, cursor, cancellationToken);

    /// <inheritdoc />
    public Task<AgentSessionHistoryPage> ReadTimelinePageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
        => Reads.ReadTimelinePageAsync(sessionId, cursor, cancellationToken);

    /// <inheritdoc />
    public Task<OwnedTimelineTail> ReadTimelineTailAsync(string sessionId, string? knownStamp, CancellationToken cancellationToken)
        => Reads.ReadTimelineTailAsync(sessionId, knownStamp, cancellationToken);

    /// <inheritdoc />
    public Task<AgentHistorySourceChunk> ReadHistorySourceAsync(AgentHistoryRevision revision, long start, long end, long offset, CancellationToken cancellationToken)
        => Reads.ReadHistorySourceAsync(revision, start, end, offset, cancellationToken);

    /// <inheritdoc />
    public Task<AgentEvent?> ReadHistoryRecordAsync(string sessionId, long offset, CancellationToken cancellationToken)
        => Reads.ReadHistoryRecordAsync(sessionId, offset, cancellationToken);

    /// <inheritdoc />
    public Task<AgentSystemPromptRecord> ReadSystemPromptAsync(string sessionId, long offset, CancellationToken cancellationToken)
        => Reads.ReadSystemPromptAsync(sessionId, offset, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<RuntimeDisplayReplacement> ObserveDisplayAsync(string sessionId, CancellationToken cancellationToken)
        => Runtime.Display.ObserveSessionAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public Task<SessionRuntimeCurrentState> ReadRuntimeStateAsync(string sessionId, CancellationToken cancellationToken)
        => Runtime.GetCurrentStateAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public Task<OwnedRuntimeObservation> ObserveRuntimeStateAsync(string sessionId, DateTimeOffset createdAt, string scope, string? projectId,
        string? projectPath, CancellationToken cancellationToken)
        => Runtime.ObserveOwnedStateAsync(sessionId, createdAt, scope, projectId, projectPath, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<RuntimeToolOutputUpdate> ObserveToolOutputAsync(string sessionId, string activityId, CancellationToken cancellationToken)
        => Runtime.ToolOutput.ObserveAsync(sessionId, activityId, cancellationToken);

    /// <inheritdoc />
    public int ReceiptCapacity => Commands.ReceiptCapacity;

    /// <inheritdoc />
    public OwnedSessionCommandAdmission AdmitSend(OwnedTextSendRequest request, CancellationToken cancellationToken) => Commands.AdmitSend(request, cancellationToken);

    /// <inheritdoc />
    public OwnedSessionCommandAdmission AdmitAbort(OwnedAbortRequest request, CancellationToken cancellationToken) => Commands.AdmitAbort(request, cancellationToken);

    /// <inheritdoc />
    public OwnedSessionCommandAdmission AdmitSteer(OwnedTextSteerRequest request, CancellationToken cancellationToken) => Commands.AdmitSteer(request, cancellationToken);

    /// <inheritdoc />
    public OwnedSessionCommandAdmission AdmitCompact(OwnedCompactRequest request, CancellationToken cancellationToken) => Commands.AdmitCompact(request, cancellationToken);

    /// <inheritdoc />
    public OwnedSessionCommandAdmission AdmitAbortRun(OwnedAbortRunRequest request, CancellationToken cancellationToken) => Commands.AdmitAbortRun(request, cancellationToken);

    /// <inheritdoc />
    public bool AsksEnabled => Commands.Asks.Enabled;

    /// <inheritdoc />
    public OwnedAskPage ListAsks(string sessionId) => Commands.Asks.List(sessionId);

    /// <inheritdoc />
    public OwnedAskDisposition? ObserveAsk(Guid actionId, OwnedAskHandle handle) => Commands.Asks.Observe(actionId, handle);

    /// <inheritdoc />
    public Task<OwnedAskDisposition> AnswerAskAsync(OwnedAskAction action, CancellationToken cancellationToken) => Commands.Asks.AnswerAsync(action, cancellationToken);

    /// <inheritdoc />
    public Task<OwnedAskDisposition> CancelAskAsync(OwnedAskAction action, CancellationToken cancellationToken) => Commands.Asks.CancelAsync(action, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SessionOwnedPermissionPage> ListPermissionsAsync(string sessionId, CancellationToken cancellationToken)
        => Runtime.Permissions.ListOwnedCommandsAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> ResolvePermissionAsync(SessionOwnedPermissionHandle handle, AgentPermissionDecisionKind decision, CancellationToken cancellationToken)
        => Runtime.Permissions.ResolveOwnedCommandAsync(handle, decision, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SessionOwnedUserInputPage> ListUserInputsAsync(string sessionId, CancellationToken cancellationToken)
        => Runtime.Permissions.ListOwnedUserInputsAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> ResolveUserInputAsync(SessionOwnedUserInputHandle handle, IReadOnlyList<SessionOwnedUserInputAnswer> answers, CancellationToken cancellationToken)
        => Runtime.Permissions.ResolveOwnedUserInputAsync(handle, answers, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> CancelUserInputAsync(SessionOwnedUserInputHandle handle, CancellationToken cancellationToken)
        => Runtime.Permissions.CancelOwnedUserInputAsync(handle, cancellationToken);

    /// <inheritdoc />
    public SessionJob? FindJob(string sessionId, string jobId)
        => Runtime.Jobs.Get(jobId) is { } job && string.Equals(job.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ? job : null;

    /// <inheritdoc />
    public IAsyncEnumerable<RuntimeToolOutputUpdate> ObserveJobOutputAsync(string sessionId, string jobId, CancellationToken cancellationToken)
        => Runtime.Jobs.ObserveOutputAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public Task<string> StopBackgroundTaskAsync(string sessionId, string taskId, CancellationToken cancellationToken)
        => Commands.StopBackgroundTaskAsync(sessionId, taskId, cancellationToken);

    /// <inheritdoc />
    public OwnedPermissionModeChoices GetNewSessionPermissionModes(string providerKey) => Commands.GetNewSessionPermissionModes(providerKey);

    /// <inheritdoc />
    public Task<OwnedDraftPrompts?> GetDraftPromptsAsync(OwnedProjectReferenceScope? scope, string? spaceId, CancellationToken cancellationToken)
        => Commands.GetDraftPromptsAsync(scope, spaceId, cancellationToken);

    /// <inheritdoc />
    public async Task<SessionViewDescriptor> CreateSessionAsync(ProjectDescriptor? project, ModelProviderDescriptor provider, string? title, string? worktreeDirectory,
        string? spaceId, string? permissionMode, AgentSessionOptions? options)
    {
        var session = await Commands.CreateDraftSessionAsync(project, provider, title, null, worktreeDirectory, spaceId, permissionMode, null, null, options)
            .ConfigureAwait(false);
        ImmutableInterlocked.Update(ref _createdSinceList, static (set, id) => set.Add(id), session.SessionId);
        ImmutableInterlocked.Update(ref _owned, static (set, id) => set.Add(id), session.SessionId);
        return session;
    }
}
