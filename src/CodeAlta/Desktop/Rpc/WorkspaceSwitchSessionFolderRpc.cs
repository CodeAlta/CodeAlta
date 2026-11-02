using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    // Session, project, project folder and the folder of the checkout to work in (null: the folder of the project).
    private readonly Func<string, string, string, string?, Task<OwnedSessionWorkFolderResult>>? _switchFolder;

    internal WorkspaceService(OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch,
        Func<string, string, string, string?, Task<OwnedSessionWorkFolderResult>> switchFolder, GitWorktreeService? worktrees) : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(switchFolder);
        _switchFolder = switchFolder;
        _worktrees = worktrees;
    }

    /// <summary>
    /// Moves an exact owned project session to another checkout of the repository of its project: an existing worktree,
    /// a new one created for it, or the folder of the project. The session keeps its conversation; no file moves.
    /// Cancellation only stops waiting after admission.
    /// </summary>
    [NeoRpcMethod("switchSessionFolder", TimeoutMilliseconds = 600_000)]
    public async Task<WorkspaceSwitchSessionFolderResponse> SwitchSessionFolderAsync(WorkspaceSwitchSessionFolderRequest request, CancellationToken cancellationToken)
    {
        if (_switchFolder is null || _importCatalog is null || _importEpoch is null) return Refused("unconfigured", request);
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.SessionId, 256)
            || !ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096)
            || request.Target is not ("project" or "worktree" or "new")
            || request.Target == "worktree" != ValidScopeValue(request.Folder, 4096)
            || request.BaseBranch is not null && (request.Target != "new" || !GitWorktreeService.IsReferenceName(request.BaseBranch)))
            return Refused("invalid_scope", request);
        if (request.ExpectedHostEpoch != _importEpoch) return Refused("stale_epoch", request);
        if (request.Target == "new" && _worktrees is null) return Refused("unconfigured", request);
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceSwitchSessionFolderResponse> work;
        lock (_sessionGate)
        {
            if (_sessionsClosed) return Refused("closed", request);
            if (_deleteWork is not null || _renameWork is not null || _branchWork is not null || _moveWork is not null || _projectDeleteWork is not null)
                return Refused("busy", request);
            // A change of folder shares the slot of the moves: one at a time, drained when the window closes.
            var slot = new TaskCompletionSource<WorkspaceMoveSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<WorkspaceSwitchSessionFolderResponse>(CatalogReplyOptions);
            _moveWork = slot.Task;
            work = answer.Task;
            _ = SwitchSessionFolderOwnedAsync(request, answer, slot);
        }

        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private WorkspaceSwitchSessionFolderResponse Refused(string status, WorkspaceSwitchSessionFolderRequest? request, string? message = null)
        => new(status, _importEpoch, request?.SessionId, null, null, [], message);

    private async Task SwitchSessionFolderOwnedAsync(WorkspaceSwitchSessionFolderRequest request, TaskCompletionSource<WorkspaceSwitchSessionFolderResponse> answer,
        TaskCompletionSource<WorkspaceMoveSessionResponse> slot)
    {
        WorkspaceSwitchSessionFolderResponse response;
        GitWorktreeCreation? created = null;
        ProjectDescriptor? project = null;
        try
        {
            project = await _importCatalog!.GetByIdAsync(request.ProjectId, CancellationToken.None).ConfigureAwait(false);
            if (project is null || project.Archived || project.ProjectPath != request.ProjectPath)
            {
                response = Refused("scope_missing", request);
            }
            else
            {
                var folder = request.Target == "worktree" ? request.Folder : null;
                if (request.Target == "new")
                {
                    created = await _worktrees!.CreateAsync(project, request.BaseBranch, progress: null, CancellationToken.None).ConfigureAwait(false);
                    folder = created.Succeeded ? created.Folder : null;
                }

                if (created is { Succeeded: false })
                {
                    response = Refused("worktree_failed", request, created.Message) with { Reason = created.Status };
                    created = null;
                }
                else
                {
                    var result = await _switchFolder!(request.SessionId, request.ProjectId, request.ProjectPath, folder).ConfigureAwait(false);
                    if (LogManager.IsInitialized)
                        foreach (var warning in result.Warnings) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Warn($"Session folder change: {warning}");
                    response = new(result.Status, _importEpoch, request.SessionId, result.WorktreeDirectory, created?.Branch,
                        // Words for the log and the page, bounded like the other texts of a reply.
                        [.. result.Warnings.Take(8).Select(static warning => warning.Length <= 1024 ? warning : warning[..1024])], created?.Warning);
                    // A worktree made for a session that did not go there is taken away again; one the session may work in
                    // (an unconfirmed change) stays.
                    if (result.Status is "ok" or "unchanged" or "switch_unconfirmed") created = null;
                }
            }
        }
        catch (Exception)
        {
            // The summary may already have been written. No retry or definite refusal after admission.
            response = Refused("switch_unconfirmed", request);
            created = null;
        }

        if (created is not null && project is not null)
        {
            try { await _worktrees!.RemoveAsync(project.ProjectPath, created.Root!, force: false, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // The worktree stays; the Worktrees window can remove it.
            }
        }

        SettleCatalogWork(ref _moveWork, slot, new("ok", _importEpoch, null, null, null, null, null, [], []));
        answer.TrySetResult(response);
    }
}

/// <summary>Asks to move a project session to another checkout of the repository of its project.</summary>
/// <param name="ExpectedHostEpoch">The host the page reads.</param>
/// <param name="SessionId">The session.</param>
/// <param name="ProjectId">The project it belongs to.</param>
/// <param name="ProjectPath">The folder of that project.</param>
/// <param name="Target"><c>project</c> (the folder of the project), <c>worktree</c> (an existing checkout, <paramref name="Folder"/>) or <c>new</c> (a new worktree).</param>
/// <param name="Folder">A folder of the checkout, for <c>worktree</c>.</param>
/// <param name="BaseBranch">What a new worktree starts from; null for the commit the folder of the project is on.</param>
internal sealed record WorkspaceSwitchSessionFolderRequest(string ExpectedHostEpoch, string SessionId, string ProjectId, string ProjectPath,
    string Target, string? Folder, string? BaseBranch);

/// <summary>
/// What a change of the folder of a session did. Status is <c>ok</c>, <c>unchanged</c>, <c>busy</c>,
/// <c>session_in_use</c>, <c>background_running</c>, <c>interaction_pending</c>, <c>session_missing</c>,
/// <c>scope_missing</c>, <c>invalid</c>, <c>not_found</c>, <c>not_repository</c>, <c>other_repository</c>,
/// <c>project_missing</c>, <c>worktree_failed</c>, <c>switch_unconfirmed</c>, <c>closed</c>, <c>invalid_scope</c>,
/// <c>stale_epoch</c> or <c>unconfigured</c>.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="HostEpoch">The host that answered.</param>
/// <param name="SessionId">The session.</param>
/// <param name="WorktreePath">The worktree the session works in now; null for the folder of its project.</param>
/// <param name="Branch">The branch of a new worktree.</param>
/// <param name="Warnings">What went wrong without stopping the change.</param>
/// <param name="Message">What git said, when a worktree could not be made or its submodules checked out.</param>
internal sealed record WorkspaceSwitchSessionFolderResponse(string Status, string? HostEpoch, string? SessionId, string? WorktreePath, string? Branch,
    IReadOnlyList<string> Warnings, string? Message)
{
    /// <summary>Gets why git did not create a worktree, with <c>worktree_failed</c>.</summary>
    public string? Reason { get; init; }
}
