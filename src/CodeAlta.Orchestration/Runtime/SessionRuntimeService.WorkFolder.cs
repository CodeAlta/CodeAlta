using System.Collections.Concurrent;
using System.Text.Json;

using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What a change of the folder a project session works in did.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>unchanged</c> when the session already works there; <c>scheduled</c> when the change waits for the
/// session to end what it does; or why nothing changed: <c>busy</c>, <c>closed</c>, <c>session_in_use</c>,
/// <c>background_running</c>, <c>interaction_pending</c>, <c>session_missing</c>, <c>invalid</c>, <c>not_found</c>,
/// <c>not_repository</c>, <c>other_repository</c>, <c>project_missing</c> or <c>switch_unconfirmed</c>.
/// </param>
/// <param name="WorktreeDirectory">
/// The git worktree the session works in after the change (or will, once a scheduled change is made); null for the
/// folder of its project.
/// </param>
/// <param name="Warnings">What went wrong without stopping the change, in words.</param>
public sealed record OwnedSessionWorkFolderResult(string Status, string? WorktreeDirectory, IReadOnlyList<string> Warnings)
{
    internal static OwnedSessionWorkFolderResult Refused(string status) => new(status, null, []);
}

// A project session changes the checkout it works in: another worktree of the repository of its project, or the folder
// of the project. Its conversation and its identity stay; no file is moved, copied or removed in either folder.
public sealed partial class SessionRuntimeService
{
    // What the next prompt of a session whose folder changed tells its agent, once: the folder the agent knew before the
    // first change it was not told of yet, and the note. Kept in memory: after a restart the agent still sees the folder
    // it works in from its instructions, only not the one it worked in before.
    private readonly ConcurrentDictionary<string, WorkFolderNotice> _workFolderNotices = new(StringComparer.OrdinalIgnoreCase);

    // The sessions whose queue waits for a change of their folder that waits for them to end what they do: what waits in
    // the queue then runs in the new folder.
    private readonly ConcurrentDictionary<string, object> _workFolderHolds = new(StringComparer.OrdinalIgnoreCase);

    private sealed record WorkFolderNotice(string KnownFolder, string Text);

    // The command owner excludes new owned sends and queues while this runs; the session is checked again on its actor,
    // where its summary is written and its attachment retired.
    internal Task<OwnedSessionWorkFolderResult> SwitchOwnedSessionWorkFolderAsync(string sessionId, string projectId, string workspacePath,
        string? folder, Func<string, bool> ownerBusy)
        => AdmitAsync(() => SwitchWorkFolderBodyAsync(sessionId, projectId, workspacePath, folder, ownerBusy), CancellationToken.None);

    private async Task<OwnedSessionWorkFolderResult> SwitchWorkFolderBodyAsync(string sessionId, string projectId, string workspacePath,
        string? folder, Func<string, bool> ownerBusy)
    {
        var project = await _projectCatalog.GetByIdAsync(projectId, CancellationToken.None).ConfigureAwait(false);
        if (project is null || project.Archived || project.ProjectPath != workspacePath) return OwnedSessionWorkFolderResult.Refused("session_missing");
        string? worktree = null;
        if (folder is not null)
        {
            var (status, resolved) = GitWorktreeService.ResolveSessionCheckout(project.ProjectPath, folder);
            if (status != GitWorktreeService.Ok) return OwnedSessionWorkFolderResult.Refused(status);
            worktree = resolved;
        }

        var warnings = new List<string>();
        string outcome;
        string? fromDirectory = null;
        // A session another process runs keeps its folder.
        if (!TryHoldForWrite(sessionId, out var lease)) return OwnedSessionWorkFolderResult.Refused("session_in_use");
        using (lease)
        {
            outcome = await SwitchOneSessionAsync(sessionId, projectId, workspacePath, worktree, ownerBusy, warnings, from => fromDirectory = from).ConfigureAwait(false);
        }

        // The timeline says where the session works from here on.
        if (outcome == "ok")
            await RecordWorkFolderAsync(sessionId, AgentSessionUpdateKind.Info, worktree ?? workspacePath,
                $"This session now works in `{worktree ?? workspacePath}`" + (fromDirectory is null ? "." : $", and no longer in `{fromDirectory}`."), warnings).ConfigureAwait(false);
        return new(outcome, outcome is "ok" or "unchanged" ? worktree : null, warnings);
    }

    private async Task<string> SwitchOneSessionAsync(string sessionId, string projectId, string workspacePath, string? worktree,
        Func<string, bool> ownerBusy, List<string> warnings, Action<string> left)
    {
        if (ownerBusy(sessionId)) return "busy";
        var actor = GetActorForWork(sessionId);
        Task? retirement = null;
        AgentSessionState? providerState = null;
        string? fromDirectory = null, toDirectory = null;
        var status = await actor.QueryAsync(async token =>
        {
            if (await WorkFolderRefusalAsync(sessionId, token).ConfigureAwait(false) is { } refusal) return refusal;
            var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
            var metadata = await store.GetSessionAsync(sessionId, token).ConfigureAwait(false);
            if (metadata is null || metadata.WorkspacePath != workspacePath) return "session_missing";
            var projects = await _projectCatalog.LoadAsync(token).ConfigureAwait(false);
            var session = TryCreateRecoverableSession(metadata, projects);
            if (session is null || session.Kind != SessionViewKind.ProjectSession || session.ProjectRef != projectId) return "session_missing";
            if (await store.GetSessionSummaryAsync(sessionId, token).ConfigureAwait(false) is not { } summary) return "session_missing";

            var recorded = NormalizeOptionalText(summary.WorktreeDirectory);
            if (SameFolder(recorded, worktree)) return "unchanged";
            providerState = await store.GetStateAsync(sessionId, token).ConfigureAwait(false);
            // The summary says where the session works: its next attachment, its row and its tools read it there.
            await store.UpsertSessionAsync(summary with { WorktreeDirectory = worktree, UpdatedAt = DateTimeOffset.UtcNow }, token).ConfigureAwait(false);
            if (_entries.TryGetValue(sessionId, out var entry))
            {
                // Its next summary would write the old folder back: the next send attaches the session again, there.
                retirement = _forwarding.RetireAsync(entry.Attachment);
                if (_entries.TryRemove(sessionId, out _)) ReleaseEntryLeaseAfter(sessionId, retirement);
                _transitions[sessionId] = retirement;
            }

            // The folder the agent knew: the worktree it recorded, also once it is gone (the attachment that would tell it
            // so reads the record that is replaced here), else the folder of the project. What its provider keeps of the
            // session is in the place of that folder too.
            fromDirectory = recorded ?? workspacePath;
            toDirectory = worktree ?? workspacePath;
            NoteWorkFolder(sessionId, fromDirectory, toDirectory, worktree is not null);
            return "ok";
        }, CancellationToken.None).ConfigureAwait(false);
        if (status != "ok") return status;
        left(fromDirectory!);

        // The change is committed: the summary says where the session works. What follows never undoes it, and the
        // transition of the retired attachment is always removed, or the session could never be attached again.
        var stopped = true;
        try
        {
            try
            {
                PublishSessionLifecycleEvent(sessionId);
                if (await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } changed)
                    PublishSessionCatalogEvent(changed);
                await _agentSessionCatalog.NotifySessionUpdatedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"The session {sessionId} works in its new folder, but the lists may show the old one until they are read again: {exception.Message}");
            }

            if (retirement is not null)
            {
                try
                {
                    await retirement.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    warnings.Add($"The session {sessionId} works in its new folder, but its provider did not stop cleanly: {exception.Message}");
                    stopped = false;
                }
            }
        }
        finally
        {
            if (retirement is not null)
            {
                await actor.QueryAsync(_ =>
                {
                    if (_transitions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, retirement))
                        _transitions.TryRemove(sessionId, out var removed);
                    return ValueTask.FromResult(true);
                }, CancellationToken.None).ConfigureAwait(false);
                PublishSessionLifecycleEvent(sessionId);
            }
        }

        // A turn the retirement ended writes the summary its session held, with the old folder: the new one is written
        // again over it.
        if (retirement is not null)
            await RestoreMovedSummaryAsync(actor, new OwnedSessionMove(sessionId, projectId, workspacePath, worktree),
                new MovedSummaryFields(null, null, null, null), warnings).ConfigureAwait(false);
        if (!stopped) return "ok";

        if (providerState is not null && fromDirectory is not null && toDirectory is not null && !SameFolder(fromDirectory, toDirectory))
        {
            try
            {
                if (await _agentHub.RelocateProviderSessionAsync(providerState, fromDirectory, toDirectory, CancellationToken.None).ConfigureAwait(false) is { } warning)
                    warnings.Add(warning);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"The conversation its provider keeps for the session {sessionId} stayed with the old folder: {exception.Message}");
            }
        }

        return "ok";
    }

    // Why the folder of a session cannot change now, or null. Runs on the actor of the session. Its background commands
    // run in the folder it leaves, and tell their end to the session: it changes folder once they ended.
    private async Task<string?> WorkFolderRefusalAsync(string sessionId, CancellationToken token)
    {
        if (Jobs.HasUnsettled(sessionId) || RunsScript(sessionId)
            || _entries.TryGetValue(sessionId, out var entry) && entry.BackgroundTasks.Any(static task => task.Outcome is null))
            return "background_running";
        return await MoveRefusalAsync(sessionId, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until a session has nothing under way that keeps its folder from changing: no run, no queue drain, no
    /// background command, no work of the owner (<paramref name="ownerBusy"/>) and no provider callback still being
    /// forwarded. What waits in its queue does not count: it runs after the change.
    /// </summary>
    internal async Task WhenReadyForWorkFolderSwitchAsync(string sessionId, Func<string, bool> ownerBusy, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Taken before the look, so that a change during it is not missed.
            var changed = _settlement.Next;
            var forwarded = _forwarding.Changed;
            Task? wake = Jobs.HasUnsettled(sessionId) ? Jobs.WhenSessionSettledAsync(sessionId) : null;
            // A script of Code Mode that runs in the background tells no end: it is looked at again at the next recheck.
            if (wake is null && !RunsScript(sessionId) && !_forwarding.HasSessionWork(sessionId) && !ownerBusy(sessionId)
                && !await AtWorkForWorkFolderSwitchAsync(sessionId, cancellationToken).ConfigureAwait(false))
                return;
            Task[] waits = wake is null ? [changed, forwarded] : [changed, forwarded, wake];
            await Task.WhenAny(Task.WhenAny(waits), Task.Delay(SettlementRecheck, cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task<bool> AtWorkForWorkFolderSwitchAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_sessionActors.TryGet(sessionId, out var actor)) return false;
        return await actor.QueryAsync(_ => ValueTask.FromResult(_transitions.ContainsKey(sessionId)
            || _entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated
            && (entry.HasActiveRun || entry.QueueDrainInProgress || entry.BackgroundTasks.Any(static task => task.Outcome is null))), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Holds the queue of a session until <see cref="ReleaseWorkFolderHold"/>: what waits there runs in the new folder.</summary>
    internal void HoldForWorkFolderSwitch(string sessionId, object owner) => _workFolderHolds[sessionId] = owner;

    /// <summary>
    /// Releases the hold of a change of folder, when it is still the one of <paramref name="owner"/>. The caller drains
    /// the queue (<see cref="ResumeQueueAfterWorkFolderSwitchAsync"/>).
    /// </summary>
    internal void ReleaseWorkFolderHold(string sessionId, object owner)
    {
        if (_workFolderHolds.TryRemove(new KeyValuePair<string, object>(sessionId, owner))) PulseSettlement();
    }

    /// <summary>
    /// Runs what waits in the queue of a session whose folder changed: a change retires its attachment, and only an
    /// attached session drains its queue. <paramref name="attach"/> attaches it, in its new folder, when prompts wait.
    /// </summary>
    internal async Task ResumeQueueAfterWorkFolderSwitchAsync(string sessionId, Func<string, Task> attach)
    {
        try
        {
            if (_disposed || HeldForWorkFolderSwitch(sessionId)) return;
            if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated)
            {
                if (await ResolveOwnedSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is not { } session) return;
                var pending = await GetActorForWork(sessionId).QueryAsync(async token =>
                    (await ReadLatestLocalStateAsync(sessionId, session.CreatedAt, token).ConfigureAwait(false))?.HasPendingQueuedPrompts() == true,
                    CancellationToken.None).ConfigureAwait(false);
                if (!pending) return;
                // The owner reads the session again once nothing else can change where it works.
                await attach(sessionId).ConfigureAwait(false);
            }

            await TryDrainNextQueuedPromptAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException or InvalidOperationException
            or IOException or ArgumentException or KeyNotFoundException or NotSupportedException)
        {
            // The queue drains when the session is given a prompt, which attaches it again.
        }
    }

    /// <summary>Waits for the next change the runtime signals (an event, a released claim, an ended command), or a short while.</summary>
    internal async Task WhenSettlementChangesAsync(CancellationToken cancellationToken)
    {
        var changed = _settlement.Next;
        var forwarded = _forwarding.Changed;
        await Task.WhenAny(Task.WhenAny(changed, forwarded), Task.Delay(SettlementRecheck, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Records in the timeline of a session that a change of its folder that waited for it could not be made.</summary>
    internal Task RecordWorkFolderSwitchRefusedAsync(string sessionId, string folder, string status)
        => RecordWorkFolderAsync(sessionId, AgentSessionUpdateKind.Warning, folder,
            $"This session could not change to `{folder}` ({status}): it still works where it worked.", null);

    /// <summary>The name of the detail of an update event about the folder a session works in: the folder it names.</summary>
    public const string WorkFolderDetail = "workFolder";

    // A card of the timeline about the folder of a session; its detail names the folder, which the timeline titles it
    // by. The session that is being removed, or a runtime that closes, keeps none.
    private async Task RecordWorkFolderAsync(string sessionId, AgentSessionUpdateKind kind, string folder, string message, List<string>? warnings)
    {
        try
        {
            if (await ResolveOwnedSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is not { } session) return;
            using var details = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { [WorkFolderDetail] = folder }));
            await AppendSessionEventAsync(session, new AgentSessionUpdateEvent(new ModelProviderId(session.ResolvedProviderKey), sessionId,
                DateTimeOffset.UtcNow, null, kind, message, details.RootElement.Clone()), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException or UnauthorizedAccessException)
        {
            warnings?.Add($"The timeline of the session {sessionId} does not say where it works now: {exception.Message}");
        }
    }

    // Whether a script of Code Mode of the session runs: it calls the tools of the session, in the folder it works in.
    private bool RunsScript(string sessionId)
        => ScriptRuns?.ListSessionsWithRunningScripts().Any(id => string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase)) == true;

    // Whether the queue of a session waits for a change of its folder.
    private bool HeldForWorkFolderSwitch(string sessionId) => _workFolderHolds.ContainsKey(sessionId);

    // Records what the next prompt tells the agent. Two changes before a prompt are told as one, from the folder the agent
    // knew; a change back to that folder tells nothing.
    private void NoteWorkFolder(string sessionId, string fromDirectory, string toDirectory, bool inWorktree)
    {
        var known = _workFolderNotices.TryGetValue(sessionId, out var pending) ? pending.KnownFolder : fromDirectory;
        if (SameFolder(known, toDirectory))
        {
            _workFolderNotices.TryRemove(sessionId, out _);
            return;
        }

        _workFolderNotices[sessionId] = new WorkFolderNotice(known, WorkFolderNoteText(known, toDirectory, inWorktree));
    }

    internal static string WorkFolderNoteText(string fromDirectory, string toDirectory, bool inWorktree)
        => $"<codealta-context>Working directory: this session now works in `{toDirectory}`"
            + (inWorktree ? ", a git worktree of the project" : ", the folder of the project")
            + $", and no longer in `{fromDirectory}`. Read, edit and run commands in `{toDirectory}` from now on. What was read, run or said before applies to `{fromDirectory}`: the new folder may be on another branch and hold other changes. Nothing was moved or copied between the two folders: changes left in `{fromDirectory}` stay there.</codealta-context>";

    // The note of a change of folder the agent was not told of yet, added to what a run sends; null when there is none.
    private AgentSendOptions WithWorkFolderNote(string sessionId, AgentSendOptions options, out object? noted)
    {
        noted = null;
        if (!_workFolderNotices.TryGetValue(sessionId, out var notice)) return options;
        noted = notice;
        return options.WithInput(new AgentInput([.. options.Input.Items, new AgentInputItem.HostContext("workfolder", notice.Text)]));
    }

    // The note was sent with a run: the next one does not tell it again, unless the folder changed meanwhile.
    private void WorkFolderNoted(string sessionId, object? noted)
    {
        if (noted is WorkFolderNotice notice) _workFolderNotices.TryRemove(new KeyValuePair<string, WorkFolderNotice>(sessionId, notice));
    }

    /// <summary>The note the next run of a session sends about a change of its folder; null when there is none.</summary>
    internal string? PeekWorkFolderNote(string sessionId) => _workFolderNotices.TryGetValue(sessionId, out var notice) ? notice.Text : null;

    private static bool SameFolder(string? first, string? second)
        => first is null || second is null
            ? first is null && second is null
            : string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), FolderComparison);
}
