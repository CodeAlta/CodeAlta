using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    /// <summary>
    /// Raised with the identifier of a session after it was deleted on purpose, by the user or by a command. It is
    /// not raised for a session that automatic retention removed.
    /// </summary>
    public event Action<string>? SessionDeleted;

    // What keeps something about a session (its bookmarks) drops it; a handler that fails does not undo the deletion.
    private void NotifySessionDeleted(string sessionId)
    {
        try { SessionDeleted?.Invoke(sessionId); }
        catch (Exception) { /* The session is deleted whatever a listener does. */ }
    }

    // Shares the runtime identity gate with session/lineage admission.
    private readonly HashSet<string> _deletingProjectPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deletedProjectIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _projectCreations = new(StringComparer.OrdinalIgnoreCase);

    private IDisposable ClaimProjectCreation(ProjectDescriptor project)
    {
        var path = NormalizePath(project.ProjectPath);
        lock (_identityGate)
        {
            if (_deletingProjectPaths.Contains(path) || _deletedProjectIds.Contains(project.Id))
                throw new InvalidOperationException("The project is being deleted or has been deleted.");
            _projectCreations[path] = _projectCreations.GetValueOrDefault(path) + 1;
            return new ProjectCreationLease(this, path);
        }
    }

    private sealed class ProjectCreationLease(SessionRuntimeService owner, string path) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._identityGate)
                if (--owner._projectCreations[path] == 0) owner._projectCreations.Remove(path);
        }
    }

    // The command owner excludes new owned sends/queues while this original runs. The actor
    // rechecks coordinator state at the retirement claim and again at the journal removal.
    internal Task<string> DeleteOwnedCatalogSessionAsync(string sessionId, string? projectId, string workspacePath, string confirmedTitle)
        => AdmitAsync(() => DeleteOwnedCatalogSessionBodyAsync(sessionId, projectId, workspacePath, confirmedTitle), CancellationToken.None);

    internal Task<string> DeleteOwnedCatalogProjectAsync(string projectId, string projectPath, string confirmedName, bool expectedArchived,
        Func<string, bool> isBusy)
        => AdmitAsync(() => DeleteOwnedCatalogProjectBodyAsync(projectId, projectPath, confirmedName, expectedArchived, isBusy), CancellationToken.None);

    private async Task<string> DeleteOwnedCatalogProjectBodyAsync(string projectId, string projectPath, string confirmedName, bool expectedArchived,
        Func<string, bool> isBusy)
    {
        lock (_identityGate)
        {
            if (_projectCreations.ContainsKey(projectPath) || !_deletingProjectPaths.Add(projectPath)) return "busy";
        }
        var claims = new List<IDisposable>();
        try
        {
            var ownership = await _projectCatalog.ReadBoundedOwnershipAsync(projectId, projectPath, CancellationToken.None).ConfigureAwait(false);
            if (ownership.Status != ProjectOwnershipStatus.Match) return "scope_missing";
            var project = await _projectCatalog.GetByIdAsync(projectId, CancellationToken.None).ConfigureAwait(false);
            if (project is null || project.Id != projectId || project.ProjectPath != projectPath
                || project.DisplayName != confirmedName || project.Archived != expectedArchived) return "scope_missing";
            var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
            var all = await ListSessionRowsAsync(store).ConfigureAwait(false);
            var sessions = all.Where(metadata => ProjectSession(metadata, projectPath)).ToArray();
            var ids = sessions.Select(metadata => metadata.SessionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Do not orphan descendants in another project or global scope.
            if (all.Any(metadata => !ids.Contains(metadata.SessionId) && HasProjectParent(metadata, ids))) return "has_children";
            // Children go first so even a partial failure keeps their surviving parents.
            // Cyclic lineage cannot be safely ordered: refuse before deleting anything.
            var remaining = sessions.ToDictionary(session => session.SessionId, StringComparer.OrdinalIgnoreCase);
            var ordered = new List<AgentSessionMetadata>();
            while (remaining.Count > 0)
            {
                var leaves = remaining.Values.Where(session => !remaining.Values.Any(candidate => candidate.SessionId != session.SessionId
                    && HasProjectParent(candidate, session.SessionId))).ToArray();
                if (leaves.Length == 0 || leaves.Any(session => HasProjectParent(session, session.SessionId)))
                    return "has_children";
                foreach (var leaf in leaves) { ordered.Add(leaf); remaining.Remove(leaf.SessionId); }
            }
            foreach (var session in sessions)
                if (!await ClaimIdleForDeletionAsync(session.SessionId, isBusy, claims).ConfigureAwait(false)) return "session_in_use";
            var removed = false;
            foreach (var session in ordered)
            {
                var result = await DeleteOwnedCatalogSessionBodyAsync(session.SessionId, projectId, projectPath, confirmedName, ids).ConfigureAwait(false);
                if (result != "ok") return removed ? "delete_unconfirmed" : result;
                removed = true;
                lock (_identityGate) _retiredSessionIds.Add(session.SessionId);
            }
            // Never remove the catalog entry while a session that came meanwhile still belongs to it.
            if ((await ListSessionRowsAsync(store).ConfigureAwait(false)).Any(metadata => ProjectSession(metadata, projectPath) || HasProjectParent(metadata, ids)))
                return "delete_unconfirmed";
            var current = await _projectCatalog.GetByIdAsync(projectId, CancellationToken.None).ConfigureAwait(false);
            if (current is null || current.ProjectPath != projectPath || current.DisplayName != confirmedName || current.Archived != expectedArchived)
                return removed ? "delete_unconfirmed" : "scope_missing";
            ownership = await _projectCatalog.ReadBoundedOwnershipAsync(projectId, projectPath, CancellationToken.None).ConfigureAwait(false);
            if (ownership.Status != ProjectOwnershipStatus.Match) return "delete_unconfirmed";
            if (!await _projectCatalog.DeleteAsync(current, CancellationToken.None).ConfigureAwait(false)) return "delete_unconfirmed";
            lock (_identityGate) _deletedProjectIds.Add(projectId);
            return "ok";
        }
        finally
        {
            foreach (var claim in claims) claim.Dispose();
            lock (_identityGate) _deletingProjectPaths.Remove(projectPath);
        }
    }

    // Holds the session for a deletion of several sessions: no job, no retention, no run can start in it. False when
    // something goes on in it, its claims taken so far being in claims all the same.
    private async Task<bool> ClaimIdleForDeletionAsync(string sessionId, Func<string, bool> isBusy, List<IDisposable> claims)
    {
        if (isBusy(sessionId)) return false;
        if (!TryHoldForWrite(sessionId, out var lease)) return false;
        claims.Add(lease);
        var jobClaim = Jobs.TryReserveDeletion(sessionId);
        if (jobClaim is null) return false;
        claims.Add(jobClaim);
        var runtimeClaim = TryClaimRuntimeRetirement(sessionId);
        if (runtimeClaim is null) return false;
        claims.Add(runtimeClaim);
        return !await IsAtWorkAsync(sessionId).ConfigureAwait(false);
    }

    // What a deletion of several sessions waits for in one of them: a question, a run, a queue, background work.
    private async Task<bool> IsAtWorkAsync(string sessionId)
    {
        if ((await Permissions.ListWaitingSessionsAsync().ConfigureAwait(false)).Contains(sessionId, StringComparer.OrdinalIgnoreCase)
            || (await Permissions.ListAsync().ConfigureAwait(false)).Any(item => string.Equals(item.Handle.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
            return true;
        return await GetActorForWork(sessionId).QueryAsync(_ => ValueTask.FromResult(
            _transitions.ContainsKey(sessionId) || _remoteControlWanted.ContainsKey(sessionId) || _cloudWork.ContainsKey(sessionId)
            || _entries.TryGetValue(sessionId, out var entry)
            && (entry.HasActiveRun || entry.QueueDrainInProgress || entry.BackgroundTasks.Any(task => task.Outcome is null))), CancellationToken.None).ConfigureAwait(false);
    }

    // Every session with its parent, its creator and its folder, as the list of sessions in the application database has
    // them: a deletion finds what it deletes there and reads no journal, whatever their number. A journal changed since its
    // row was written is read again by the listing; the rows are rebuilt from the journals only when the database is new.
    private static async Task<List<AgentSessionMetadata>> ListSessionRowsAsync(FileSystemAgentSessionStore store)
    {
        var all = new List<AgentSessionMetadata>();
        await foreach (var metadata in store.ListSessionsAsync(filter: null, cancellationToken: CancellationToken.None).ConfigureAwait(false))
            all.Add(metadata);
        return all;
    }

    private static bool ProjectSession(AgentSessionMetadata metadata, string projectPath)
        => (metadata.Context?.Cwd ?? metadata.WorkspacePath) is { Length: > 0 } path
            && string.Equals(NormalizePath(path), NormalizePath(projectPath), StringComparison.OrdinalIgnoreCase);

    private static bool HasProjectParent(AgentSessionMetadata metadata, IReadOnlySet<string> ids)
        => metadata.ParentSessionId is { } parent && ids.Contains(parent)
            || metadata.CreatedBySessionId is { } creator && ids.Contains(creator)
            || metadata.ViewState?.ParentSessionId is { } viewParent && ids.Contains(viewParent);

    private static bool HasProjectParent(AgentSessionMetadata metadata, string id)
        => string.Equals(metadata.ParentSessionId, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(metadata.CreatedBySessionId, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(metadata.ViewState?.ParentSessionId, id, StringComparison.OrdinalIgnoreCase);

    private async Task<string> DeleteOwnedCatalogSessionBodyAsync(string sessionId, string? projectId, string workspacePath, string confirmedTitle,
        IReadOnlySet<string>? projectSessions = null)
    {
        // A session another process runs is not deleted under it.
        if (!TryHoldForWrite(sessionId, out var lease)) return "session_in_use";
        using var held = lease;
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        async Task<string> ValidateAsync(CancellationToken token)
        {
            var metadata = await store.GetSessionAsync(sessionId, token).ConfigureAwait(false);
            if (metadata is null) return "session_missing";
            if (projectSessions is not null)
            {
                if (!projectSessions.Contains(sessionId) || !ProjectSession(metadata, workspacePath)) return "session_missing";
            }
            else if (await ValidateListedSessionAsync(metadata, projectId, workspacePath, confirmedTitle, token).ConfigureAwait(false) != "ok")
                return "session_missing";
            // The project or tree worker already checked every descendant in the session rows and
            // holds all lineage claims. Do not list the store again for every batch member.
            if (projectSessions is not null) return "ok";
            await foreach (var candidate in store.ListSessionsAsync(filter: null, cancellationToken: token).ConfigureAwait(false))
            {
                if (candidate.SessionId != sessionId &&
                    (string.Equals(candidate.ParentSessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(candidate.CreatedBySessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(candidate.ViewState?.ParentSessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
                    return "has_children";
            }
            return "ok";
        }

        var validation = await ValidateAsync(CancellationToken.None).ConfigureAwait(false);
        if (validation != "ok") return validation;
        var actor = GetActorForWork(sessionId);
        var preparation = await actor.QueryAsync(_ =>
        {
            // A cloud session being brought here, or a message being sent to it, holds the session as a run does.
            if (_transitions.ContainsKey(sessionId) || _cloudWork.ContainsKey(sessionId)) return ValueTask.FromResult((Busy: true, Work: (Task?)null));
            if (!_entries.TryGetValue(sessionId, out var entry)) return ValueTask.FromResult((Busy: false, Work: (Task?)null));
            if (entry.HasActiveRun || entry.QueueDrainInProgress || projectSessions is not null
                && (_remoteControlWanted.ContainsKey(sessionId) || entry.BackgroundTasks.Any(task => task.Outcome is null)))
                return ValueTask.FromResult((Busy: true, Work: (Task?)null));
            var retirement = _forwarding.RetireAsync(entry.Attachment);
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? ticket = null;
            var work = _forwarding.RunAsync(async () =>
            {
                await launch.Task.ConfigureAwait(false);
                try { await retirement.ConfigureAwait(false); }
                finally
                {
                    await actor.QueryAsync(token =>
                    {
                        if (_entries.TryGetValue(sessionId, out var active) && ReferenceEquals(active, entry)
                            && _entries.TryRemove(sessionId, out var removedEntry))
                            ReleaseEntryLease(sessionId);
                        if (_transitions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, ticket))
                            _transitions.TryRemove(sessionId, out var removedTransition);
                        return ValueTask.FromResult(true);
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }, external: false);
            _transitions[sessionId] = work;
            ticket = work;
            launch.TrySetResult();
            return ValueTask.FromResult((Busy: false, Work: (Task?)work));
        }, CancellationToken.None).ConfigureAwait(false);
        if (preparation.Busy) return "session_in_use";
        if (preparation.Work is not null) await preparation.Work.ConfigureAwait(false);
        var outcome = await actor.QueryAsync(async token =>
        {
            if (_transitions.ContainsKey(sessionId) || _entries.ContainsKey(sessionId) || _cloudWork.ContainsKey(sessionId)) return "session_in_use";
            var checkedScope = await ValidateAsync(token).ConfigureAwait(false);
            if (checkedScope != "ok") return checkedScope;
            return await _agentSessionCatalog.DeleteSessionAsync(sessionId, token).ConfigureAwait(false)
                ? "ok" : "delete_unconfirmed";
        }, CancellationToken.None).ConfigureAwait(false);
        if (outcome == "ok") NotifySessionDeleted(sessionId);
        return outcome;
    }
}
