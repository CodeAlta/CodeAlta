using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    private Task<WorkspaceArchiveProjectResponse>? _archiveWork;
    internal Func<WorkspaceArchiveProjectRequest, TextFileRevision, Task<ProjectDisplayNameRenameStatus>>? ArchiveWriter { get; set; }

    [NeoRpcMethod("archiveProject")]
    public async Task<WorkspaceArchiveProjectResponse> ArchiveProjectAsync(WorkspaceArchiveProjectRequest request, CancellationToken cancellationToken)
    {
        WorkspaceArchiveProjectResponse Reply(string status) => new(status, _importEpoch, request?.ProjectId, request?.ProjectPath, null, null, null);
        if (_importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.ProjectId, 256) || !ValidProjectPath(request.ProjectPath)
            || request.Confirmed && (!ValidProjectPath(request.SourcePath) || request.Revision is not { Length: 64 }
                || request.Revision.Any(ch => ch is not (>= '0' and <= '9' or >= 'A' and <= 'F')) || request.Archived == request.ExpectedArchived)) return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceArchiveProjectResponse> work;
        lock (_importGate)
        {
            if (CatalogAdmissionClosed) return Reply("closed");
            if (CatalogAdmissionBusy) return Reply("busy");
            var completion = new TaskCompletionSource<WorkspaceArchiveProjectResponse>(CatalogReplyOptions);
            _archiveWork = work = completion.Task;
            _ = ArchiveProjectOwnedAsync(request, completion);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ArchiveProjectOwnedAsync(WorkspaceArchiveProjectRequest request, TaskCompletionSource<WorkspaceArchiveProjectResponse> completion)
    {
        WorkspaceArchiveProjectResponse Reply(string status, ProjectArchiveSnapshot? evidence = null) => new(status, _importEpoch,
            request.ProjectId, request.ProjectPath, evidence?.SourcePath, evidence?.Revision.ContentHash, evidence?.Archived);
        WorkspaceArchiveProjectResponse? response = null;
        try
        {
            ProjectArchiveSnapshot? snapshot;
            try { snapshot = await _importCatalog!.ReadArchiveAsync(request.ProjectId, request.ProjectPath, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { response = Reply("read_failure"); return; }
            if (snapshot is null) { response = Reply("unsupported"); return; }
            if (!request.Confirmed) { response = Reply("confirmation_required", snapshot); return; }
            if (snapshot.SourcePath != request.SourcePath || snapshot.Revision.ContentHash != request.Revision || snapshot.Archived != request.ExpectedArchived)
            { response = Reply("conflict"); return; }
            try
            {
                var result = ArchiveWriter is { } write ? await write(request, snapshot.Revision).ConfigureAwait(false)
                    : await _importCatalog.SetArchivedAsync(request.ProjectId, request.ProjectPath, snapshot.SourcePath, snapshot.Revision,
                        request.ExpectedArchived, request.Archived, CancellationToken.None).ConfigureAwait(false);
                response = Reply(result switch { ProjectDisplayNameRenameStatus.Updated => "ok", ProjectDisplayNameRenameStatus.Conflict => "conflict", _ => "unsupported" },
                    snapshot with { Archived = result == ProjectDisplayNameRenameStatus.Updated ? request.Archived : snapshot.Archived });
            }
            catch (Exception) { response = Reply("archive_unconfirmed", snapshot); }
        }
        finally { SettleCatalogWork(ref _archiveWork, completion, response); }
    }
}

internal sealed record WorkspaceArchiveProjectRequest(string ExpectedHostEpoch, string ProjectId, string ProjectPath, bool ExpectedArchived,
    bool Archived, bool Confirmed, string? SourcePath, string? Revision);
internal sealed record WorkspaceArchiveProjectResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath,
    string? SourcePath, string? Revision, bool? Archived);
