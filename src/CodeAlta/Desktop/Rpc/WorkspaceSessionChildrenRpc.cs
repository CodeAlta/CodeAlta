using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    // The rows a view of the child sessions shows; the identities of every descendant go with them all the same.
    internal const int MaximumShownChildren = 200;
    // Beyond that many descendants, the window does not offer to delete them in one go.
    internal const int MaximumDescendants = 2000;

    private Func<CancellationToken, Task<OwnedWorkspaceSnapshot>>? _readAllSessions;
    private Func<IReadOnlyCollection<string>, Task<IReadOnlySet<string>>>? _sessionsAtWork;
    private Func<string, string?, string, string, IReadOnlyCollection<string>, Task<OwnedSessionTreeDeletion>>? _deleteSessionTree;

    internal WorkspaceService(OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch,
        Func<IReadOnlyCollection<string>, Task<IReadOnlySet<string>>> sessionsAtWork,
        Func<string, string?, string, string, IReadOnlyCollection<string>, Task<OwnedSessionTreeDeletion>> deleteSessionTree) : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(sessionsAtWork);
        ArgumentNullException.ThrowIfNull(deleteSessionTree);
        _sessionsAtWork = sessionsAtWork;
        _deleteSessionTree = deleteSessionTree;
    }

    /// <summary>
    /// Reads the sessions below a session, wherever they are: in other projects, in the chats, in other spaces. Each
    /// comes with the session it is shown under and whether something goes on in it.
    /// </summary>
    [NeoRpcMethod("sessionChildren")]
    public async Task<WorkspaceSessionChildrenResponse> SessionChildrenAsync(WorkspaceSessionChildrenRequest request, CancellationToken cancellationToken)
    {
        WorkspaceSessionChildrenResponse Reply(string status) => new(status, _importEpoch, request?.SessionId, null, false, [], [], 0, 0, false);
        if (_readAllSessions is null || _sessionsAtWork is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.SessionId, 256)) return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        try
        {
            var snapshot = await _readAllSessions(cancellationToken).ConfigureAwait(false);
            var root = snapshot.Sessions.FirstOrDefault(session => session.SessionId == request.SessionId);
            if (root is null) return Reply("session_missing");
            var descendants = SessionLineage.Descendants(root.SessionId, snapshot.Sessions);
            var shown = descendants.Take(MaximumShownChildren).ToArray();
            var rows = ProjectSnapshot(snapshot.Projects, [root, .. shown.Select(static descendant => descendant.Session)], snapshot.SessionHeaders)
                .Sessions.ToDictionary(static session => session.Id, StringComparer.Ordinal);
            var ids = descendants.Select(static descendant => descendant.Session.SessionId).ToArray();
            var atWork = await _sessionsAtWork([root.SessionId, .. ids]).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!rows.TryGetValue(root.SessionId, out var rootRow)) return Reply("read_failed");
            var children = shown.Where(descendant => rows.ContainsKey(descendant.Session.SessionId))
                .Select(descendant => new WorkspaceSessionChild(rows[descendant.Session.SessionId], descendant.ParentSessionId, descendant.Depth,
                    atWork.Contains(descendant.Session.SessionId)))
                .ToArray();
            var complete = ids.Length <= MaximumDescendants;
            return new("ok", _importEpoch, root.SessionId, rootRow, atWork.Contains(root.SessionId), children, complete ? ids : [],
                descendants.Count(static descendant => descendant.Depth == 1), descendants.Count, children.Length < descendants.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { return Reply("closed"); }
        catch (Exception) { return Reply("read_failed"); }
    }

    /// <summary>
    /// Deletes an exact confirmed session with every session below it, when they are still the ones the user saw and
    /// none of them is at work; never a project or its files.
    /// </summary>
    [NeoRpcMethod("deleteSessionTree")]
    public async Task<WorkspaceDeleteSessionTreeResponse> DeleteSessionTreeAsync(WorkspaceDeleteSessionTreeRequest request, CancellationToken cancellationToken)
    {
        WorkspaceDeleteSessionTreeResponse Reply(string status) => new(status, _importEpoch, request?.SessionId, [], null);
        if (_deleteSessionTree is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.SessionId, 256)
            || !ValidScopeValue(request.ConfirmedTitle, 256) || request.Scope is not ("global" or "project")
            || request.Scope == "global" && (request.ProjectId is not null || request.ProjectPath != _importCatalog.Options.GlobalRoot)
            || request.Scope == "project" && (!ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096))
            || request.Descendants is null || request.Descendants.Count > MaximumDescendants
            || request.Descendants.Any(id => !ValidScopeValue(id, 256)))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceDeleteSessionTreeResponse> work;
        lock (_sessionGate)
        {
            if (_sessionsClosed) return Reply("closed");
            if (_deleteWork is not null || _renameWork is not null || _branchWork is not null || _moveWork is not null || _projectDeleteWork is not null) return Reply("busy");
            // The deletion of a tree shares the slot of the deletions: one at a time, drained when the window closes.
            var slot = new TaskCompletionSource<WorkspaceDeleteSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<WorkspaceDeleteSessionTreeResponse>(CatalogReplyOptions);
            _deleteWork = slot.Task;
            work = answer.Task;
            _ = DeleteSessionTreeOwnedAsync(request, answer, slot);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteSessionTreeOwnedAsync(WorkspaceDeleteSessionTreeRequest request, TaskCompletionSource<WorkspaceDeleteSessionTreeResponse> answer,
        TaskCompletionSource<WorkspaceDeleteSessionResponse> slot)
    {
        WorkspaceDeleteSessionTreeResponse? response = null;
        try
        {
            if (request.Scope == "project")
            {
                var project = await _importCatalog!.GetByIdAsync(request.ProjectId!, CancellationToken.None).ConfigureAwait(false);
                if (project is null || project.Archived || project.Id != request.ProjectId || project.ProjectPath != request.ProjectPath)
                {
                    response = new("scope_missing", _importEpoch, request.SessionId, [], null);
                    return;
                }
            }

            var result = await _deleteSessionTree!(request.SessionId, request.ProjectId, request.ProjectPath!, request.ConfirmedTitle, request.Descendants)
                .ConfigureAwait(false);
            response = new(result.Status, _importEpoch, request.SessionId, [.. result.Deleted], result.BlockingSessionId);
        }
        catch (Exception)
        {
            // Journals may already have been removed. No retry or definite refusal after admission.
            response = new("delete_unconfirmed", _importEpoch, request.SessionId, [], null);
        }
        finally
        {
            SettleCatalogWork(ref _deleteWork, slot, new("ok", _importEpoch, null, null, null, null));
            if (response is not null) answer.TrySetResult(response);
        }
    }
}

/// <summary>Asks for the sessions below a session.</summary>
internal sealed record WorkspaceSessionChildrenRequest(string ExpectedHostEpoch, string SessionId);

/// <summary>
/// The sessions below a session. Status is <c>ok</c>, <c>session_missing</c>, <c>read_failed</c>, <c>closed</c>,
/// <c>invalid_scope</c>, <c>stale_epoch</c> or <c>unconfigured</c>.
/// </summary>
/// <param name="Root">The session itself, as the lists show it.</param>
/// <param name="RootAtWork">Something goes on in the session itself.</param>
/// <param name="Children">The descendants, the tree read from the top; at most <see cref="WorkspaceService.MaximumShownChildren"/>.</param>
/// <param name="DescendantIds">Every descendant, shown or not: what a deletion of the tree confirms. Empty when there
/// are more than <see cref="WorkspaceService.MaximumDescendants"/>.</param>
/// <param name="DirectCount">The children of the session.</param>
/// <param name="TotalCount">All its descendants.</param>
/// <param name="Truncated">Some descendants are not in <paramref name="Children"/>.</param>
internal sealed record WorkspaceSessionChildrenResponse(string Status, string? HostEpoch, string? SessionId, WorkspaceSession? Root, bool RootAtWork,
    IReadOnlyList<WorkspaceSessionChild> Children, IReadOnlyList<string> DescendantIds, int DirectCount, int TotalCount, bool Truncated);

/// <summary>A session below another one.</summary>
/// <param name="Session">The session, as the lists show it.</param>
/// <param name="ParentSessionId">The session it is shown under.</param>
/// <param name="Depth">1 for a child, 2 for a grandchild, and so on.</param>
/// <param name="AtWork">Something goes on in it: a run, a queue, a question, a job.</param>
internal sealed record WorkspaceSessionChild(WorkspaceSession Session, string ParentSessionId, int Depth, bool AtWork);

/// <summary>Asks to delete a session with all the sessions below it, those the user saw.</summary>
internal sealed record WorkspaceDeleteSessionTreeRequest(string ExpectedHostEpoch, string Scope, string? ProjectId,
    string? ProjectPath, string SessionId, string ConfirmedTitle, IReadOnlyList<string> Descendants);

/// <summary>
/// What deleting a session with its descendants did. Status is <c>ok</c>, <c>descendants_changed</c>,
/// <c>session_in_use</c>, <c>session_missing</c>, <c>scope_missing</c>, <c>delete_partial</c>,
/// <c>delete_unconfirmed</c>, <c>busy</c>, <c>closed</c>, <c>invalid_scope</c>, <c>stale_epoch</c> or <c>unconfigured</c>.
/// </summary>
/// <param name="Deleted">The sessions that were deleted.</param>
/// <param name="BlockingSessionId">The session at work, or the one that could not be deleted.</param>
internal sealed record WorkspaceDeleteSessionTreeResponse(string Status, string? HostEpoch, string? SessionId,
    IReadOnlyList<string> Deleted, string? BlockingSessionId);
