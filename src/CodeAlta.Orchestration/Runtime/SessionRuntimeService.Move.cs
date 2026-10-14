using System.Collections.Concurrent;

using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Orchestration.Runtime.Actors;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>A session a move took to another project.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ProjectId">The project it belongs to now.</param>
/// <param name="WorkspacePath">The folder of that project.</param>
/// <param name="WorktreePath">The git worktree it still works in, or <see langword="null"/>.</param>
public sealed record OwnedSessionMove(string SessionId, string ProjectId, string WorkspacePath, string? WorktreePath);

/// <summary>What a move of a session to another project did.</summary>
/// <param name="Status"><c>ok</c>, or why nothing (or not everything) moved: <c>busy</c>, <c>closed</c>,
/// <c>session_in_use</c>, <c>interaction_pending</c>, <c>session_missing</c>, <c>target_missing</c>,
/// <c>same_project</c>, <c>requires_recovery</c>, <c>move_partial</c> or <c>move_unconfirmed</c>.</param>
/// <param name="Moved">The sessions that moved: the session and the sessions it started in the same project.</param>
/// <param name="Warnings">What went wrong without stopping the move, in words.</param>
public sealed record OwnedSessionMoveResult(string Status, IReadOnlyList<OwnedSessionMove> Moved, IReadOnlyList<string> Warnings)
{
    internal static OwnedSessionMoveResult Refused(string status) => new(status, [], []);
}

public sealed partial class SessionRuntimeService
{
    // What the next attachment of a moved session tells its agent, once. Kept in memory: after a restart the agent
    // still sees the folder it works in from its instructions, only not the folder it came from.
    private readonly ConcurrentDictionary<string, string> _movedNotices = new(StringComparer.OrdinalIgnoreCase);

    private static readonly StringComparison FolderComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // The command owner excludes new owned sends and queues while this runs. Each session is checked again by its
    // actor where its journal is rewritten and its attachment retired.
    internal Task<OwnedSessionMoveResult> MoveOwnedSessionAsync(string sessionId, string projectId, string workspacePath,
        string targetProjectId, string targetPath, Func<string, bool> ownerBusy)
        => AdmitAsync(() => MoveOwnedSessionBodyAsync(sessionId, projectId, workspacePath, targetProjectId, targetPath, ownerBusy), CancellationToken.None);

    private async Task<OwnedSessionMoveResult> MoveOwnedSessionBodyAsync(string sessionId, string projectId, string workspacePath,
        string targetProjectId, string targetPath, Func<string, bool> ownerBusy)
    {
        var projects = await _projectCatalog.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var source = projects.FirstOrDefault(project => string.Equals(project.Id, projectId, StringComparison.Ordinal));
        if (source is null) return OwnedSessionMoveResult.Refused("session_missing");
        var target = projects.FirstOrDefault(project => string.Equals(project.Id, targetProjectId, StringComparison.Ordinal));
        if (target is null || target.Archived || target.ProjectPath != targetPath) return OwnedSessionMoveResult.Refused("target_missing");
        if (target.Id == source.Id || string.Equals(target.ProjectPath, source.ProjectPath, FolderComparison))
            return OwnedSessionMoveResult.Refused("same_project");

        var members = await ListMovedSessionsAsync(sessionId, projectId, workspacePath, projects).ConfigureAwait(false);
        if (members.Count == 0) return OwnedSessionMoveResult.Refused("session_missing");
        foreach (var member in members)
        {
            if (ownerBusy(member)) return OwnedSessionMoveResult.Refused("busy");
            var refusal = await GetActorForWork(member).QueryAsync(token => new ValueTask<string?>(MoveRefusalAsync(member, token)), CancellationToken.None)
                .ConfigureAwait(false);
            if (refusal is not null) return OwnedSessionMoveResult.Refused(refusal);
        }

        // The sessions it started first and the session last: a move stopped half way leaves the session where it
        // was, with what it started already in the target.
        var moved = new List<OwnedSessionMove>();
        var warnings = new List<string>();
        for (var index = members.Count - 1; index >= 0; index--)
        {
            string status;
            try
            {
                status = await MoveOneSessionAsync(members[index], projectId, workspacePath, source, target, ownerBusy, moved, warnings).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (moved.Any(value => string.Equals(value.SessionId, members[index], StringComparison.OrdinalIgnoreCase)))
                {
                    // Its journal was rewritten before what failed: it moved, and the move goes on.
                    warnings.Add($"The session {members[index]} moved, but: {exception.Message}");
                    status = "ok";
                }
                else if (exception is InvalidOperationException)
                {
                    warnings.Add($"The session {members[index]} cannot be moved: {exception.Message}");
                    status = "requires_recovery";
                }
                else
                {
                    // The journal of this one is replaced at once or not at all; those moved before it stay moved, and
                    // the answer names them so that their tabs and their remote control follow them.
                    warnings.Add($"The session {members[index]} was not moved: {exception.Message}");
                    status = "move_unconfirmed";
                }
            }

            if (status != "ok") return new(moved.Count == 0 ? status : "move_partial", moved, warnings);
        }

        return new("ok", moved, warnings);
    }

    // The session and, below it, the sessions it started that belong to the same project, the session first.
    private async Task<IReadOnlyList<string>> ListMovedSessionsAsync(string sessionId, string projectId, string workspacePath,
        IReadOnlyList<ProjectDescriptor> projects)
    {
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        var all = new List<AgentSessionMetadata>();
        await foreach (var metadata in store.ListSessionsAsync(filter: null, cancellationToken: CancellationToken.None).ConfigureAwait(false))
            all.Add(metadata);

        bool InScope(AgentSessionMetadata metadata)
            => metadata.WorkspacePath == workspacePath && TryCreateRecoverableSession(metadata, projects) is { } session
                && session.Kind == SessionViewKind.ProjectSession && session.ProjectRef == projectId;

        var root = all.FirstOrDefault(metadata => string.Equals(metadata.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        if (root is null || !InScope(root)) return [];
        var members = new List<string> { root.SessionId };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.SessionId };
        for (var index = 0; index < members.Count; index++)
        {
            var parent = members[index];
            foreach (var candidate in all)
            {
                if (seen.Contains(candidate.SessionId) || !InScope(candidate)) continue;
                if (string.Equals(candidate.ParentSessionId, parent, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.CreatedBySessionId, parent, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.ViewState?.ParentSessionId, parent, StringComparison.OrdinalIgnoreCase))
                {
                    seen.Add(candidate.SessionId);
                    members.Add(candidate.SessionId);
                }
            }
        }

        return members;
    }

    // Why a session cannot move now, or null. Runs on the actor of the session.
    private async Task<string?> MoveRefusalAsync(string sessionId, CancellationToken token)
    {
        // Remote Control does not keep a session where it is: its owner turns it on again in the new folder.
        if (_transitions.ContainsKey(sessionId)) return "session_in_use";
        if (_entries.TryGetValue(sessionId, out var entry) && (entry.HasActiveRun || entry.QueueDrainInProgress || entry.OwnedQueue is not null
            || entry.Attachment.IsRetiring || !HasOwnedCommandDefaults(entry) || entry.PendingAgentPromptId is not null
            // The tasks of its provider end with its process, which a move stops.
            || entry.BackgroundTasks.Any(static task => task.Outcome is null))) return "session_in_use";
        if ((await Permissions.ListAsync().ConfigureAwait(false)).Any(permission => permission.Handle.SessionId == sessionId)
            || (await Permissions.ListOwnedUserInputsAsync(sessionId, token).ConfigureAwait(false)).Entries.Count != 0) return "interaction_pending";
        return null;
    }

    private async Task<string> MoveOneSessionAsync(string sessionId, string projectId, string workspacePath, ProjectDescriptor source,
        ProjectDescriptor target, Func<string, bool> ownerBusy, List<OwnedSessionMove> moved, List<string> warnings)
    {
        if (ownerBusy(sessionId)) return "busy";
        var actor = GetActorForWork(sessionId);
        Task? retirement = null;
        AgentSessionState? providerState = null;
        string? fromDirectory = null, toDirectory = null;
        OwnedSessionMove? done = null;
        MovedSummaryFields? written = null;
        var status = await actor.QueryAsync(async token =>
        {
            if (await MoveRefusalAsync(sessionId, token).ConfigureAwait(false) is { } refusal) return refusal;
            var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
            var metadata = await store.GetSessionAsync(sessionId, token).ConfigureAwait(false);
            if (metadata is null || metadata.WorkspacePath != workspacePath) return "session_missing";
            var projects = await _projectCatalog.LoadAsync(token).ConfigureAwait(false);
            var session = TryCreateRecoverableSession(metadata, projects);
            if (session is null || session.Kind != SessionViewKind.ProjectSession || session.ProjectRef != projectId) return "session_missing";
            var summary = await store.GetSessionSummaryAsync(sessionId, token).ConfigureAwait(false);
            if (summary is null) return "session_missing";

            var recorded = NormalizeOptionalText(summary.WorktreeDirectory);
            var worktree = KeptWorktree(recorded, target.ProjectPath);
            // A session that still has the title it was created with, the name of its project, takes the name of
            // the new one. A title somebody chose stays, and so does one CodeAlta derived from the conversation.
            var saved = NormalizeOptionalText(summary.Title);
            var title = !TitleWasChosen(metadata, session.Kind, source) && (saved is null || IsCreationTitle(saved, session.Kind, source))
                ? target.DisplayName
                : null;
            var preview = summary.Summary == UnnamedProjectSessionSummary(source) ? UnnamedProjectSessionSummary(target) : null;
            providerState = await store.GetStateAsync(sessionId, token).ConfigureAwait(false);
            await _sessionViewCatalog.JournalStore.MoveSessionAsync(session,
                new SessionViewMove(target.Id, target.ProjectPath, worktree, title, preview)
                    { ProviderFamily = await RuntimeFamilyOfKeyWrittenAsFamilyAsync(summary, token).ConfigureAwait(false) }, token).ConfigureAwait(false);
            written = new MovedSummaryFields(summary.Title, string.IsNullOrWhiteSpace(title) ? null : title.Trim(), summary.Summary, preview);
            if (_entries.TryGetValue(sessionId, out var entry))
            {
                // Its next summary would write the old folder back: the next send attaches the session again.
                retirement = _forwarding.RetireAsync(entry.Attachment);
                _entries.TryRemove(sessionId, out _);
                _transitions[sessionId] = retirement;
            }

            fromDirectory = ExistingWorktree(recorded) ?? workspacePath;
            toDirectory = worktree ?? target.ProjectPath;
            _movedNotices[sessionId] = MovedNotice(source, target, fromDirectory, toDirectory, recorded is not null && worktree is null);
            done = new OwnedSessionMove(sessionId, target.Id, target.ProjectPath, worktree);
            return "ok";
        }, CancellationToken.None).ConfigureAwait(false);
        if (status != "ok" || done is null) return status;
        moved.Add(done);

        // The move is committed: its journal says where the session is. What follows never undoes it, and the
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
                warnings.Add($"The session {sessionId} moved, but the lists may show it in its old project until they are read again: {exception.Message}");
            }

            if (retirement is not null)
            {
                try
                {
                    await retirement.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // Only what its provider keeps stays where it was.
                    warnings.Add($"The session {sessionId} moved, but its provider did not stop cleanly: {exception.Message}");
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

        // A turn the retirement ended (one started from the phone, with Remote Control on) writes the summary its
        // session held, with the old folder, title and preview: what the move wrote is written again over it. Also
        // when its provider did not stop cleanly: the turn wrote before the provider was asked to stop.
        if (retirement is not null) await RestoreMovedSummaryAsync(actor, done, written!, warnings).ConfigureAwait(false);
        if (!stopped) return "ok";

        if (providerState is not null && fromDirectory is not null && toDirectory is not null
            && !string.Equals(Path.TrimEndingDirectorySeparator(fromDirectory), Path.TrimEndingDirectorySeparator(toDirectory), FolderComparison))
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

    // What the move wrote in the summary of a session, and what it replaced: the title and the preview of a session
    // that was never named (null when the move kept them).
    private sealed record MovedSummaryFields(string? FromTitle, string? ToTitle, string? FromPreview, string? ToPreview);

    private async Task RestoreMovedSummaryAsync(SessionActor actor, OwnedSessionMove done, MovedSummaryFields written, List<string> warnings)
    {
        try
        {
            var changed = await actor.QueryAsync(async token =>
            {
                var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
                if (await store.GetSessionSummaryAsync(done.SessionId, token).ConfigureAwait(false) is not { } summary) return false;
                var restored = summary with { WorkingDirectory = done.WorkspacePath, WorktreeDirectory = done.WorktreePath };
                // Only what still is what the move replaced: a rename, or a preview of a later turn, stays.
                if (written.ToTitle is not null && summary.Title == written.FromTitle) restored = restored with { Title = written.ToTitle };
                if (written.ToPreview is not null && summary.Summary == written.FromPreview) restored = restored with { Summary = written.ToPreview };
                if (restored == summary) return false;
                await store.UpsertSessionAsync(restored with { UpdatedAt = DateTimeOffset.UtcNow }, token).ConfigureAwait(false);
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
            if (!changed) return;
            // Whoever read the sessions meanwhile has what the ended turn wrote.
            await _agentSessionCatalog.NotifySessionUpdatedAsync(done.SessionId, CancellationToken.None).ConfigureAwait(false);
            if (await ResolveOwnedSessionBodyAsync(done.SessionId, CancellationToken.None).ConfigureAwait(false) is { } session)
                PublishSessionCatalogEvent(session);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add($"The session {done.SessionId} moved, but its last turn may have written its old folder back: {exception.Message}");
        }
    }

    // The family of the runtime of the provider of a session whose summary names the provider key as family, as
    // CodeAlta 1.8 wrote it when it attached a session of a custom-keyed provider: a move or a switch reads that
    // summary with this family. Null for any other summary, which is read as it is.
    private async Task<string?> RuntimeFamilyOfKeyWrittenAsFamilyAsync(AgentSessionSummary? summary, CancellationToken token)
        => summary is not null && !string.IsNullOrWhiteSpace(summary.ProviderKey)
           && string.Equals(summary.ProtocolFamily, summary.ProviderKey, StringComparison.OrdinalIgnoreCase)
            ? await _agentHub.GetProviderProtocolFamilyAsync(summary.ProviderKey, token).ConfigureAwait(false)
            : null;

    // The worktree a moved session keeps working in: the one it works in, when that folder still exists, belongs to
    // the repository of the target and is not the checkout the target itself lives in.
    internal static string? KeptWorktree(string? recorded, string targetPath)
    {
        if (ExistingWorktree(recorded) is not { } worktree) return null;
        if (GitWorktreeService.FindCheckoutRoot(targetPath) is { } targetCheckout
            && GitWorktreeService.FindCheckoutRoot(worktree) is { } worktreeCheckout
            && string.Equals(Path.TrimEndingDirectorySeparator(targetCheckout), Path.TrimEndingDirectorySeparator(worktreeCheckout), FolderComparison))
            return null;
        return GitWorktreeService.SameRepository(targetPath, worktree) ? worktree : null;
    }

    private static string MovedNotice(ProjectDescriptor source, ProjectDescriptor target, string fromDirectory, string toDirectory, bool leftWorktree)
        => $"This session was moved from the project {source.DisplayName} (`{fromDirectory}`) to the project {target.DisplayName} (`{toDirectory}`)"
            + (leftWorktree ? ", and no longer works in its git worktree" : string.Empty)
            + $". What it said and did before names the old folder: work in `{toDirectory}` now, and not in `{fromDirectory}`.";

    // Whether the session is to be controlled remotely: a move keeps that wish, and the process it ends is started
    // again in the new folder with its remote control.
    internal bool RemoteControlWanted(string sessionId) => _remoteControlWanted.ContainsKey(sessionId);

    // The notice of a move, for the attachment that is being created; the next one no longer has it.
    internal string? TakeMovedNotice(string sessionId) => _movedNotices.TryRemove(sessionId, out var notice) ? notice : null;
}
