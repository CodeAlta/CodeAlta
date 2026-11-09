using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    /// <summary>
    /// Binds to the folder of their project the sessions recorded for that project in a folder that is gone: the folder
    /// of the project changed since (its file names another one, as when the folder was moved). Without it such a
    /// session is listed under no project and cannot be opened. An idle attachment is retired, as a move does; a session at
    /// work, or that another process holds, is left for a later pass.
    /// </summary>
    internal Task<OwnedSessionMoveResult> FollowMovedProjectFoldersAsync(Func<string, bool> ownerBusy)
        => AdmitAsync(() => FollowMovedProjectFoldersBodyAsync(ownerBusy), CancellationToken.None);

    private async Task<OwnedSessionMoveResult> FollowMovedProjectFoldersBodyAsync(Func<string, bool> ownerBusy)
    {
        var projects = await _projectCatalog.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        var candidates = new List<(AgentSessionMetadata Metadata, ProjectDescriptor Project)>();
        await foreach (var metadata in store.ListSessionsAsync(filter: null, cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            if (SessionViewJournalHeader.FromJson(metadata.JournalHeaderJson) is not { Kind: SessionViewKind.ProjectSession } header
                || header.SessionId != metadata.SessionId || header.WorkingDirectory != metadata.WorkspacePath) continue;
            if (ProjectCatalog.FindMovedSessionProject(header.ProjectRef, metadata.WorkspacePath, projects, Directory.Exists) is { } project)
                candidates.Add((metadata, project));
        }

        var moved = new List<OwnedSessionMove>();
        var warnings = new List<string>();
        foreach (var (metadata, project) in candidates)
        {
            try
            {
                await FollowMovedProjectFolderAsync(metadata.SessionId, metadata.WorkspacePath!, project, ownerBusy, moved, warnings).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The journal is replaced at once or not at all: the session stays where it was, for the next pass.
                warnings.Add($"The session {metadata.SessionId} was not bound to the new folder of its project: {exception.Message}");
            }
        }

        return new(candidates.Count == moved.Count ? "ok" : "move_partial", moved, warnings);
    }

    private async Task FollowMovedProjectFolderAsync(string sessionId, string fromDirectory, ProjectDescriptor project, Func<string, bool> ownerBusy,
        List<OwnedSessionMove> moved, List<string> warnings)
    {
        // A session at work, or that another process runs, keeps its folder until it is idle here.
        if (ownerBusy(sessionId) || !TryHoldForWrite(sessionId, out var lease)) return;
        using var held = lease;
        var actor = GetActorForWork(sessionId);
        Task? retirement = null;
        MovedSummaryFields? written = null;
        string? recordedWorktree = null;
        Agent.Runtime.AgentSessionState? providerState = null;
        OwnedSessionMove? done = null;
        await actor.QueryAsync(async token =>
        {
            if (await MoveRefusalAsync(sessionId, token).ConfigureAwait(false) is not null) return false;
            var metadata = await store().GetSessionAsync(sessionId, token).ConfigureAwait(false);
            if (metadata is null || metadata.WorkspacePath != fromDirectory) return false;
            var header = await _sessionViewCatalog.JournalStore.ReadHeaderAsync(sessionId, metadata.CreatedAt, token).ConfigureAwait(false);
            if (header is null || header.Kind != SessionViewKind.ProjectSession || header.ProjectRef != project.Id) return false;
            var summary = await store().GetSessionSummaryAsync(sessionId, token).ConfigureAwait(false);
            if (summary is null) return false;
            recordedWorktree = NormalizeOptionalText(summary.WorktreeDirectory);
            var worktree = KeptWorktree(recordedWorktree, project.ProjectPath);
            providerState = await store().GetStateAsync(sessionId, token).ConfigureAwait(false);
            await _sessionViewCatalog.JournalStore.MoveSessionAsync(header.ToDescriptor(),
                new SessionViewMove(project.Id, project.ProjectPath, worktree, null, null)
                    { ProviderFamily = await RuntimeFamilyOfKeyWrittenAsFamilyAsync(summary, token).ConfigureAwait(false) }, token).ConfigureAwait(false);
            var toDirectory = worktree ?? project.ProjectPath;
            _movedNotices[sessionId] = $"The folder of the project {project.DisplayName} moved from `{fromDirectory}` to `{project.ProjectPath}`. "
                + $"What this session said and did before names the old folder: work in `{toDirectory}` now.";
            done = new OwnedSessionMove(sessionId, project.Id, project.ProjectPath, worktree);
            written = new MovedSummaryFields(summary.Title, null, summary.Summary, null);
            if (_entries.TryGetValue(sessionId, out var entry))
            {
                // An idle attachment would write the old folder back with its next summary: the next send attaches it again.
                retirement = _forwarding.RetireAsync(entry.Attachment);
                if (_entries.TryRemove(sessionId, out _)) ReleaseEntryLeaseAfter(sessionId, retirement);
                _transitions[sessionId] = retirement;
            }
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        if (done is null) return;
        moved.Add(done);

        try
        {
            PublishSessionLifecycleEvent(sessionId);
            if (await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } changed)
                PublishSessionCatalogEvent(changed);
            await _agentSessionCatalog.NotifySessionUpdatedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add($"The session {sessionId} follows the new folder of its project, but the lists may not show it until they are read again: {exception.Message}");
        }

        if (retirement is not null)
        {
            var stopped = true;
            try { await retirement.ConfigureAwait(false); }
            catch (Exception exception)
            {
                warnings.Add($"The session {sessionId} follows the new folder of its project, but its provider did not stop cleanly: {exception.Message}");
                stopped = false;
            }
            finally
            {
                await actor.QueryAsync(_ =>
                {
                    if (_transitions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, retirement)) _transitions.TryRemove(sessionId, out var removed);
                    return ValueTask.FromResult(true);
                }, CancellationToken.None).ConfigureAwait(false);
                PublishSessionLifecycleEvent(sessionId);
            }

            await RestoreMovedSummaryAsync(actor, done, written!, warnings).ConfigureAwait(false);
            if (!stopped) return;
        }

        // What its provider keeps of the conversation (the transcript of Claude Code) goes with it, from the old folder.
        var from = recordedWorktree ?? fromDirectory;
        var to = done.WorktreePath ?? done.WorkspacePath;
        if (providerState is not null && !string.Equals(Path.TrimEndingDirectorySeparator(from), Path.TrimEndingDirectorySeparator(to), FolderComparison))
        {
            try
            {
                if (await _agentHub.RelocateProviderSessionAsync(providerState, from, to, CancellationToken.None).ConfigureAwait(false) is { } warning)
                    warnings.Add(warning);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"The conversation its provider keeps for the session {sessionId} stayed with the old folder: {exception.Message}");
            }
        }

        Agent.Runtime.FileSystemAgentSessionStore store() => _sessionViewCatalog.JournalStore.CreateSessionStore();
    }
}
