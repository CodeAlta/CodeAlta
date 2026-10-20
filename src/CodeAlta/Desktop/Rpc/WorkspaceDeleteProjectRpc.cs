using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    private readonly Func<string, string, string, bool, Task<string>>? _deleteProject;
    private Task<WorkspaceDeleteProjectResponse>? _projectDeleteWork;

    /// <summary>Told the id of a project once it is deleted: what the application keeps of it elsewhere goes too.</summary>
    internal Action<string>? ProjectDeleted { get; set; }

    internal WorkspaceService(CodeAlta.Orchestration.Runtime.OwnedSessionWorkspace reads, CodeAlta.Catalog.ProjectCatalog catalog, string epoch,
        Func<string, string, string, bool, Task<string>> deleteProject) : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(deleteProject);
        _deleteProject = deleteProject;
    }

    /// <summary>Removes a confirmed catalog project and its sessions, not the source folder.</summary>
    [NeoRpcMethod("deleteProject", TimeoutMilliseconds = 600_000)]
    public async Task<WorkspaceDeleteProjectResponse> DeleteProjectAsync(WorkspaceDeleteProjectRequest request, CancellationToken cancellationToken)
    {
        WorkspaceDeleteProjectResponse Reply(string status) => new(status, _importEpoch, request?.ProjectId, request?.ProjectPath);
        if (_deleteProject is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.ProjectId, 256)
            || !ValidProjectPath(request.ProjectPath) || !ValidScopeValue(request.ConfirmedName, 256)) return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        if (!request.Confirmed) return Reply("confirmation_required");
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceDeleteProjectResponse> work;
        lock (_importGate)
        {
            if (CatalogAdmissionClosed) return Reply("closed");
            if (CatalogAdmissionBusy || _deleteWork is not null || _renameWork is not null || _branchWork is not null || _moveWork is not null) return Reply("busy");
            var completion = new TaskCompletionSource<WorkspaceDeleteProjectResponse>(CatalogReplyOptions);
            _projectDeleteWork = work = completion.Task;
            _ = DeleteProjectOwnedAsync(request, completion);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteProjectOwnedAsync(WorkspaceDeleteProjectRequest request, TaskCompletionSource<WorkspaceDeleteProjectResponse> completion)
    {
        WorkspaceDeleteProjectResponse? response = null;
        try
        {
            var status = await _deleteProject!(request.ProjectId, request.ProjectPath, request.ConfirmedName, request.ExpectedArchived).ConfigureAwait(false);
            if (status == "ok")
            {
                try { ProjectDeleted?.Invoke(request.ProjectId); }
                catch (Exception) { /* The project is deleted; what is left of it elsewhere is listed by the storage page. */ }
            }

            response = new(status, _importEpoch, request.ProjectId, request.ProjectPath);
        }
        catch (Exception) { response = new("delete_unconfirmed", _importEpoch, request.ProjectId, request.ProjectPath); }
        finally { SettleCatalogWork(ref _projectDeleteWork, completion, response); }
    }
}

internal sealed record WorkspaceDeleteProjectRequest(string ExpectedHostEpoch, string ProjectId, string ProjectPath,
    string ConfirmedName, bool ExpectedArchived, bool Confirmed);
internal sealed record WorkspaceDeleteProjectResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath);
