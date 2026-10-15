using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    internal WorkspaceService(OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch,
        Func<string, string, string, string, string, Task<OwnedSessionMoveResult>> moveSession) : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(moveSession);
        _moveSession = moveSession;
    }

    /// <summary>
    /// Moves an exact owned project session, with the sessions it started in the same project, to another project;
    /// cancellation only stops waiting after admission.
    /// </summary>
    [NeoRpcMethod("moveSession")]
    public async Task<WorkspaceMoveSessionResponse> MoveSessionAsync(WorkspaceMoveSessionRequest request, CancellationToken cancellationToken)
    {
        if (_moveSession is null || _importCatalog is null || _importEpoch is null) return Refused("unconfigured", null);
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.SessionId, 256)
            || !ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096)
            || !ValidScopeValue(request.TargetProjectId, 256) || !ValidScopeValue(request.TargetProjectPath, 4096))
            return Refused("invalid_scope", null);
        if (request.ExpectedHostEpoch != _importEpoch) return Refused("stale_epoch", request);
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceMoveSessionResponse> work;
        lock (_sessionGate)
        {
            if (_sessionsClosed) return Refused("closed", request);
            if (_deleteWork is not null || _renameWork is not null || _branchWork is not null || _moveWork is not null || _projectDeleteWork is not null) return Refused("busy", request);
            var completion = new TaskCompletionSource<WorkspaceMoveSessionResponse>(CatalogReplyOptions);
            _moveWork = work = completion.Task;
            _ = MoveOwnedAsync(request, completion);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private WorkspaceMoveSessionResponse Refused(string status, WorkspaceMoveSessionRequest? request)
        => new(status, _importEpoch, request?.SessionId, request?.ProjectId, request?.ProjectPath,
            request?.TargetProjectId, request?.TargetProjectPath, [], []);

    private async Task MoveOwnedAsync(WorkspaceMoveSessionRequest request, TaskCompletionSource<WorkspaceMoveSessionResponse> completion)
    {
        WorkspaceMoveSessionResponse? response = null;
        try
        {
            var source = await _importCatalog!.GetByIdAsync(request.ProjectId, CancellationToken.None).ConfigureAwait(false);
            if (source is null || source.Archived || source.ProjectPath != request.ProjectPath)
            {
                response = Refused("scope_missing", request);
                return;
            }

            var result = await _moveSession!(request.SessionId, request.ProjectId, request.ProjectPath,
                request.TargetProjectId, request.TargetProjectPath).ConfigureAwait(false);
            if (LogManager.IsInitialized)
                foreach (var warning in result.Warnings) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Warn($"Session move: {warning}");
            response = new(result.Status, _importEpoch, request.SessionId, request.ProjectId, request.ProjectPath,
                request.TargetProjectId, request.TargetProjectPath,
                [.. result.Moved.Select(static moved => new WorkspaceMovedSession(moved.SessionId, moved.ProjectId, moved.WorkspacePath, moved.WorktreePath))],
                // Words for the log and the page, bounded like the other texts of a reply.
                [.. result.Warnings.Take(8).Select(static warning => warning.Length <= 1024 ? warning : warning[..1024])]);
        }
        catch (Exception)
        {
            // Journals may already have been rewritten. No retry or definite refusal after admission.
            response = Refused("move_unconfirmed", request);
        }
        finally { SettleCatalogWork(ref _moveWork, completion, response); }
    }
}

internal sealed record WorkspaceMoveSessionRequest(string ExpectedHostEpoch, string SessionId, string ProjectId, string ProjectPath,
    string TargetProjectId, string TargetProjectPath);
internal sealed record WorkspaceMovedSession(string SessionId, string ProjectId, string ProjectPath, string? WorktreePath);
internal sealed record WorkspaceMoveSessionResponse(string Status, string? HostEpoch, string? SessionId, string? ProjectId, string? ProjectPath,
    string? TargetProjectId, string? TargetProjectPath, IReadOnlyList<WorkspaceMovedSession> Moved, IReadOnlyList<string> Warnings);
