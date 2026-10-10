using System.Collections.Concurrent;

using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;

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
            catch (InvalidOperationException exception)
            {
                warnings.Add($"The session {members[index]} cannot be moved: {exception.Message}");
                status = "requires_recovery";
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
        if (_transitions.ContainsKey(sessionId) || _remoteControlWanted.ContainsKey(sessionId)) return "session_in_use";
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
            // A session that was never named is named after its project: it takes the name of the new one.
            var named = GivenTitle(metadata, session, projects) is not null;
            var title = named ? null : target.DisplayName;
            var preview = summary.Summary == UnnamedProjectSessionSummary(source) ? UnnamedProjectSessionSummary(target) : null;
            providerState = await store.GetStateAsync(sessionId, token).ConfigureAwait(false);
            await _sessionViewCatalog.JournalStore.MoveSessionAsync(session,
                new SessionViewMove(target.Id, target.ProjectPath, worktree, title, preview), token).ConfigureAwait(false);
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

        PublishSessionLifecycleEvent(sessionId);
        if (await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } changed)
            PublishSessionCatalogEvent(changed);
        await _agentSessionCatalog.NotifySessionUpdatedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (retirement is not null)
        {
            try
            {
                await retirement.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The move is committed: its journal says where the session is. Only what its provider keeps stays.
                warnings.Add($"The session {sessionId} moved, but its provider did not stop cleanly: {exception.Message}");
                return "ok";
            }
            finally
            {
                await actor.QueryAsync(_ =>
                {
                    if (_transitions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, retirement))
                        _transitions.TryRemove(sessionId, out var removed);
                    return ValueTask.FromResult(true);
                }, CancellationToken.None).ConfigureAwait(false);
            }

            PublishSessionLifecycleEvent(sessionId);
        }

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

    // The notice of a move, for the attachment that is being created; the next one no longer has it.
    internal string? TakeMovedNotice(string sessionId) => _movedNotices.TryRemove(sessionId, out var notice) ? notice : null;
}
