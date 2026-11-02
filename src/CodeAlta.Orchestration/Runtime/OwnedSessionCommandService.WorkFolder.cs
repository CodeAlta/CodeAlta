using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    // How many times in a row a scheduled change of folder is refused while its session looks idle before it is given
    // up: what refuses it then (another process, a remote control that reconnects) is not something the session ends.
    private const int MaximumScheduledSwitchRefusals = 30;

    // The changes of folder that wait for their session to end what it does, by session; under _gate.
    private readonly Dictionary<string, ScheduledWorkFolderSwitch> _scheduledSwitches = new(StringComparer.OrdinalIgnoreCase);

    // The sessions being attached again after a change of their folder, for their queue; under _gate. A change of folder
    // waits for it (IsOwnerBusy): the attachment is made with what the session records once it is marked here.
    private readonly HashSet<string> _attachingForQueue = new(StringComparer.OrdinalIgnoreCase);

    // The sessions whose queue is being run again after a change of their folder, with how many times; under _gate. The
    // settlement of a session waits for it (HasWork), from the moment the change ends.
    private readonly Dictionary<string, int> _resumingQueues = new(StringComparer.OrdinalIgnoreCase);

    private sealed class ScheduledWorkFolderSwitch(string sessionId, string projectId, string workspacePath, string? folder)
    {
        public string SessionId { get; } = sessionId;
        public string ProjectId { get; } = projectId;
        public string WorkspacePath { get; } = workspacePath;
        public string? Folder { get; } = folder;
        public CancellationTokenSource Stop { get; } = new();
    }

    /// <summary>
    /// Changes the folder an idle project session works in: a checkout of the repository of its project (a git
    /// worktree, wherever it is), or the folder of the project. The conversation and the identity of the session stay;
    /// no file is moved, copied or removed. Caller cancellation is owned by the waiting bridge only.
    /// </summary>
    /// <remarks>Uses the exclusive catalog-mutation reservation shared with deletion, moves and provider selection.
    /// Refuses a session that runs, has background commands, waits for an answer or runs remotely, rather than stopping
    /// it. The next prompt of the session tells its agent of the change.</remarks>
    /// <param name="sessionId">Exact catalog session identity.</param>
    /// <param name="projectId">Exact identity of the project the session belongs to.</param>
    /// <param name="workspacePath">Exact folder of that project.</param>
    /// <param name="folder">A folder of the checkout to work in, an absolute path; null for the folder of the project.</param>
    /// <returns>What changed (see <see cref="OwnedSessionWorkFolderResult.Status"/>).</returns>
    /// <exception cref="ArgumentException">A required identity is blank.</exception>
    public Task<OwnedSessionWorkFolderResult> SwitchWorkFolderAsync(string sessionId, string projectId, string workspacePath, string? folder)
        => SwitchWorkFolderAsync(sessionId, projectId, workspacePath, folder, whenIdle: false);

    /// <summary>
    /// Changes the folder a project session works in, as <see cref="SwitchWorkFolderAsync(string, string, string, string?)"/>
    /// does, or, for a session at work when <paramref name="whenIdle"/> is set, once it ended what it does.
    /// </summary>
    /// <remarks>A scheduled change answers <c>scheduled</c> at once. Until it is made, the queue of the session waits,
    /// so that what waits there runs in the new folder. A later change of the same session replaces it. A change that
    /// fails then is recorded in the timeline of the session.</remarks>
    /// <param name="sessionId">Exact catalog session identity.</param>
    /// <param name="projectId">Exact identity of the project the session belongs to.</param>
    /// <param name="workspacePath">Exact folder of that project.</param>
    /// <param name="folder">A folder of the checkout to work in, an absolute path; null for the folder of the project.</param>
    /// <param name="whenIdle">Whether a session at work changes folder once it ended what it does, instead of being refused.</param>
    /// <returns>What changed (see <see cref="OwnedSessionWorkFolderResult.Status"/>).</returns>
    /// <exception cref="ArgumentException">A required identity is blank.</exception>
    public async Task<OwnedSessionWorkFolderResult> SwitchWorkFolderAsync(string sessionId, string projectId, string workspacePath, string? folder, bool whenIdle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        // The folder is checked before anything waits on it: a folder that cannot be used is refused at once.
        var project = await _projects.GetByIdAsync(projectId, CancellationToken.None).ConfigureAwait(false);
        if (project is null || project.Archived || project.ProjectPath != workspacePath) return OwnedSessionWorkFolderResult.Refused("session_missing");
        string? worktree = null;
        if (folder is not null)
        {
            var (status, resolved) = GitWorktreeService.ResolveSessionCheckout(project.ProjectPath, folder);
            if (status != GitWorktreeService.Ok) return OwnedSessionWorkFolderResult.Refused(status);
            worktree = resolved;
        }

        var result = await SwitchWorkFolderNowAsync(sessionId, projectId, workspacePath, folder, scheduled: null).ConfigureAwait(false);
        // A change that was made has replaced one that waited (SwitchWorkFolderOwnedAsync).
        if (!whenIdle || !RetriedWhenIdle(result.Status)) return result;

        lock (_gate)
        {
            if (_closed || _retained) return OwnedSessionWorkFolderResult.Refused("closed");
            if (_scheduledSwitches.Remove(sessionId, out var previous)) previous.Stop.Cancel();
            var scheduled = new ScheduledWorkFolderSwitch(sessionId, projectId, workspacePath, folder);
            _scheduledSwitches[sessionId] = scheduled;
            _runtime.HoldForWorkFolderSwitch(sessionId, scheduled);
            _ = RunScheduledSwitchAsync(scheduled);
        }

        return new("scheduled", worktree, []);
    }

    // What waits for the session to end what it does, rather than being refused.
    private static bool RetriedWhenIdle(string status) => status is "busy" or "session_in_use" or "background_running" or "interaction_pending";

    private Task<OwnedSessionWorkFolderResult> SwitchWorkFolderNowAsync(string sessionId, string projectId, string workspacePath, string? folder,
        ScheduledWorkFolderSwitch? scheduled)
    {
        TaskCompletionSource<OwnedSessionWorkFolderResult> completion;
        // The reservation others wait on, ended with the status of the change.
        TaskCompletionSource<string> reservation;
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult(OwnedSessionWorkFolderResult.Refused("closed"));
            // A change that waits is made by its own task alone.
            if (scheduled is not null && (!_scheduledSwitches.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, scheduled)))
                return Task.FromResult(OwnedSessionWorkFolderResult.Refused("closed"));
            if (_deleteWork is not null || IsOwnerBusy(sessionId)) return Task.FromResult(OwnedSessionWorkFolderResult.Refused("busy"));
            if (Asks.List(sessionId).Head is not null) return Task.FromResult(OwnedSessionWorkFolderResult.Refused("interaction_pending"));
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            reservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = reservation.Task;
            _ = SwitchWorkFolderOwnedAsync(sessionId, projectId, workspacePath, folder, scheduled, completion, reservation);
        }

        return completion.Task;
    }

    private async Task SwitchWorkFolderOwnedAsync(string sessionId, string projectId, string workspacePath, string? folder,
        ScheduledWorkFolderSwitch? scheduled, TaskCompletionSource<OwnedSessionWorkFolderResult> completion, TaskCompletionSource<string> reservation)
    {
        OwnedSessionWorkFolderResult result;
        try
        {
            result = await _runtime.SwitchOwnedSessionWorkFolderAsync(sessionId, projectId, workspacePath, folder,
                member => { lock (_gate) return IsOwnerBusy(member) || Asks.List(member).Head is not null; }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The summary may already have been written. The caller must inspect, never retry automatically.
            result = OwnedSessionWorkFolderResult.Refused("switch_unconfirmed");
        }

        // What waits in its queue runs in the new folder; a change that waited runs it itself, once it released the queue.
        var resume = result.Status == "ok" && scheduled is null;
        var replacedHold = false;
        lock (_gate)
        {
            // A change made now replaces one that waited, before the reservation lets that one run; the queue that one
            // held is this change's to run.
            if (scheduled is null && result.Status is "ok" or "unchanged" && _scheduledSwitches.Remove(sessionId, out var replaced))
            {
                replaced.Stop.Cancel();
                _runtime.ReleaseWorkFolderHold(sessionId, replaced);
                replacedHold = true;
            }

            resume |= replacedHold;
            if (resume) BeginQueueResume(sessionId);
            if (ReferenceEquals(_deleteWork, reservation.Task)) _deleteWork = null;
        }

        reservation.TrySetResult(result.Status);
        completion.TrySetResult(result);
        if (resume) await ResumeQueueAsync(sessionId, reconnect: result.Status == "ok").ConfigureAwait(false);
    }

    // Counts a run of the queue of a session again as work of the owner. The caller holds the gate.
    private void BeginQueueResume(string sessionId) => _resumingQueues[sessionId] = _resumingQueues.GetValueOrDefault(sessionId) + 1;

    // Runs the queue of a session whose folder changed (BeginQueueResume counted it). A session that was controlled
    // remotely is attached again first, with its remote control turned on in its new folder: the change ended its
    // process, and the link it had. Nothing starts once the owner closes.
    private async Task ResumeQueueAsync(string sessionId, bool reconnect)
    {
        try
        {
            lock (_gate) { if (_closed || _retained) return; }
            if (reconnect && _runtime.RemoteControlWanted(sessionId)) await ReconnectRemoteControlAsync(sessionId).ConfigureAwait(false);
            lock (_gate) { if (_closed || _retained) return; }
            await _runtime.ResumeQueueAfterWorkFolderSwitchAsync(sessionId, AttachAsync).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_resumingQueues.TryGetValue(sessionId, out var count) && count > 1) _resumingQueues[sessionId] = count - 1;
                else _resumingQueues.Remove(sessionId);
            }

            _runtime.PulseSettlement();
        }
    }

    // Attaches a session with what it runs with, as a send does, without sending anything. Marked for the time it takes,
    // so that no change of its folder runs meanwhile; the session is read once marked.
    private async Task AttachAsync(string sessionId)
    {
        lock (_gate)
        {
            if (_closed || _retained) throw new ObjectDisposedException(nameof(OwnedSessionCommandService));
            // A change of sessions under way, or a send of the session, attaches it itself.
            if (Reserved(sessionId) || IsOwnerBusy(sessionId)) return;
            _attachingForQueue.Add(sessionId);
        }

        try
        {
            if (await _runtime.ResolveOwnedSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } session)
                await AttachResolvedAsync(session).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _attachingForQueue.Remove(sessionId);
            _runtime.PulseSettlement();
        }
    }

    private async Task AttachResolvedAsync(SessionViewDescriptor session)
    {
        var project = string.IsNullOrWhiteSpace(session.ProjectRef) ? null
            : await _projects.GetByIdAsync(session.ProjectRef, CancellationToken.None).ConfigureAwait(false);
        var (modelId, effort) = await CompleteModelAsync(session.ResolvedProviderKey, session.ModelId, session.ReasoningEffort, observedOnly: false).ConfigureAwait(false);
        var policy = SessionExecutionPolicy.CaptureSession(session, project, _catalog.GlobalRoot, default, modelId, effort, session.AgentPromptId);
        var options = SessionExecutionPolicy.BuildOptions(
            policy, ToolsFor(session.SessionId, project, project is null ? _catalog.GlobalRoot : WorkFolder(session, project), session.ResolvedProviderKey, modelId, session.Options),
            _runtime.Permissions.OwnedDefaultPermissionHandler,
            _runtime.Permissions.OwnedDefaultUserInputHandler);
        if (Plugins is not null)
            options = await Plugins.AugmentAttachmentAsync(options, session.ProjectRef, session.SessionId,
                (await _runtime.ResolveSpaceAsync(session.SpaceId, project, CancellationToken.None).ConfigureAwait(false)).Id, CancellationToken.None).ConfigureAwait(false);
        await _runtime.EnsureOwnedCoordinatorSessionAsync(session, options).ConfigureAwait(false);
    }

    // Makes a change that waits once its session ended what it does; the queue of the session waits with it.
    private async Task RunScheduledSwitchAsync(ScheduledWorkFolderSwitch scheduled)
    {
        await Task.Yield();
        OwnedSessionWorkFolderResult? result = null;
        try
        {
            for (var refusals = 0; ; refusals++)
            {
                await _runtime.WhenReadyForWorkFolderSwitchAsync(scheduled.SessionId, BusyForWorkFolderSwitch, scheduled.Stop.Token).ConfigureAwait(false);
                result = await SwitchWorkFolderNowAsync(scheduled.SessionId, scheduled.ProjectId, scheduled.WorkspacePath, scheduled.Folder, scheduled)
                    .ConfigureAwait(false);
                if (!RetriedWhenIdle(result.Status) || refusals >= MaximumScheduledSwitchRefusals) break;
                // Refused while the session looked idle (another change under way, a prompt that just started): it is
                // looked at again at the next change of the runtime.
                await _runtime.WhenSettlementChangesAsync(scheduled.Stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // Replaced by another change, or CodeAlta closes: nothing is recorded.
            result = null;
        }
        finally
        {
            lock (_gate)
            {
                // The settlement goes on waiting, for the queue that runs next. A change that was replaced leaves the
                // queue to what replaced it, and one the closing owner stopped starts nothing.
                if (result is { Status: not "closed" }) BeginQueueResume(scheduled.SessionId);
                if (_scheduledSwitches.TryGetValue(scheduled.SessionId, out var current) && ReferenceEquals(current, scheduled))
                    _scheduledSwitches.Remove(scheduled.SessionId);
            }

            _runtime.ReleaseWorkFolderHold(scheduled.SessionId, scheduled);
            scheduled.Stop.Dispose();
        }

        if (result is null or { Status: "closed" }) return;
        // What waited in the queue runs now, in the new folder when the change was made.
        await ResumeQueueAsync(scheduled.SessionId, reconnect: result.Status == "ok").ConfigureAwait(false);

        if (result is not null && result.Status is not ("ok" or "unchanged" or "closed"))
            await _runtime.RecordWorkFolderSwitchRefusedAsync(scheduled.SessionId, scheduled.Folder ?? scheduled.WorkspacePath, result.Status).ConfigureAwait(false);
    }

    // Whether the owner has work of the session under way that a change of its folder waits for: its commands, or a
    // fallback or Adaptive that continues its run. The change that waits is not part of it.
    private bool BusyForWorkFolderSwitch(string sessionId)
    {
        lock (_gate)
        {
            if (IsOwnerBusy(sessionId) || Asks.List(sessionId).Head is not null) return true;
        }

        return Fallback.IsRunning(sessionId) || Adaptive.IsRunning(sessionId);
    }

    // Whether a change of the folder of the session waits for it to end what it does, or runs its queue after it. The
    // caller holds the gate.
    private bool HasScheduledSwitch(string sessionId) => _scheduledSwitches.ContainsKey(sessionId) || _resumingQueues.ContainsKey(sessionId);

    // Stops the changes that wait, when the owner closes. The caller holds the gate.
    private void CancelScheduledSwitches()
    {
        foreach (var scheduled in _scheduledSwitches.Values) scheduled.Stop.Cancel();
        _scheduledSwitches.Clear();
    }
}
