using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    // The import gate owns both catalog mutations and preflight reads. Closing the host drains
    // admitted reads/writes before disposing the catalog; canceling a bridge wait never abandons work.
    private Task<WorkspaceReadProjectNameResponse>? _projectReadWork;
    private Task<WorkspaceRenameProjectResponse>? _projectRenameWork;
    private readonly Func<string, string, string, TextFileRevision, string, Task<ProjectDisplayNameRenameStatus>>? _projectNameWrite;

    internal WorkspaceService(CodeAlta.Orchestration.Runtime.OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch,
        Func<string, string, string, TextFileRevision, string, Task<ProjectDisplayNameRenameStatus>> write)
        : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(write);
        _projectNameWrite = write;
    }

    [NeoRpcMethod("readProjectName")]
    public async Task<WorkspaceReadProjectNameResponse> ReadProjectNameAsync(WorkspaceReadProjectNameRequest request, CancellationToken cancellationToken)
    {
        WorkspaceReadProjectNameResponse Reply(string status) => new(status, _importEpoch,
            status is "invalid_scope" or "unconfigured" ? null : request?.ProjectId,
            status is "invalid_scope" or "unconfigured" ? null : request?.ProjectPath, null, null, null);
        if (_importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.ProjectId, 256)
            || !ValidProjectPath(request.ProjectPath)) return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceReadProjectNameResponse> work;
        lock (_importGate)
        {
            if (_importsClosed) return Reply("closed");
            if (_importWork is not null || _projectReadWork is not null || _projectRenameWork is not null) return Reply("busy");
            var completion = new TaskCompletionSource<WorkspaceReadProjectNameResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _projectReadWork = work = completion.Task;
            _ = ReadProjectNameOwnedAsync(request, completion);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    [NeoRpcMethod("renameProject")]
    public async Task<WorkspaceRenameProjectResponse> RenameProjectAsync(WorkspaceRenameProjectRequest request, CancellationToken cancellationToken)
    {
        WorkspaceRenameProjectResponse Reply(string status) => status is "invalid_scope" or "unconfigured"
            ? new(status, _importEpoch, null, null, null, null, null)
            : new(status, _importEpoch, request?.ProjectId, request?.ProjectPath,
                request?.SourcePath, request?.Revision, status == "ok" ? request?.DisplayName : null);
        if (_importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.ProjectId, 256)
            || !ValidProjectPath(request.ProjectPath) || !ValidProjectPath(request.SourcePath)
            || request.Revision is not { Length: 64 } || request.Revision.Any(static ch => ch is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
            || !ValidScopeValue(request.DisplayName, 256) || string.IsNullOrWhiteSpace(request.DisplayName)) return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceRenameProjectResponse> work;
        lock (_importGate)
        {
            if (_importsClosed) return Reply("closed");
            if (_importWork is not null || _projectReadWork is not null || _projectRenameWork is not null) return Reply("busy");
            var completion = new TaskCompletionSource<WorkspaceRenameProjectResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _projectRenameWork = work = completion.Task;
            _ = RenameProjectOwnedAsync(request, completion);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadProjectNameOwnedAsync(WorkspaceReadProjectNameRequest request,
        TaskCompletionSource<WorkspaceReadProjectNameResponse> completion)
    {
        WorkspaceReadProjectNameResponse Reply(string status, ProjectDisplayNameSnapshot? snapshot = null)
            => new(status, _importEpoch, request.ProjectId, request.ProjectPath, snapshot?.SourcePath,
                snapshot?.Revision.ContentHash, snapshot?.DisplayName);
        try
        {
            var project = await _importCatalog!.GetByIdAsync(request.ProjectId, CancellationToken.None).ConfigureAwait(false);
            if (project is null || project.Archived || project.Id != request.ProjectId || project.ProjectPath != request.ProjectPath)
                completion.TrySetResult(Reply("scope_missing"));
            else
            {
                var snapshot = await _importCatalog.ReadDisplayNameAsync(request.ProjectId, request.ProjectPath, CancellationToken.None).ConfigureAwait(false);
                completion.TrySetResult(snapshot is null || !ValidProjectPath(snapshot.SourcePath)
                    || !ValidScopeValue(snapshot.DisplayName, 256) || string.IsNullOrWhiteSpace(snapshot.DisplayName)
                    || snapshot.Revision.ContentHash is not { Length: 64 } ? Reply("unsupported") : Reply("ok", snapshot));
            }
        }
        catch (Exception) { completion.TrySetResult(Reply("read_failure")); }
        finally { lock (_importGate) { if (ReferenceEquals(_projectReadWork, completion.Task)) _projectReadWork = null; } }
    }

    private async Task RenameProjectOwnedAsync(WorkspaceRenameProjectRequest request,
        TaskCompletionSource<WorkspaceRenameProjectResponse> completion)
    {
        WorkspaceRenameProjectResponse Reply(string status) => new(status, _importEpoch, request.ProjectId, request.ProjectPath,
            request.SourcePath, request.Revision, status == "ok" ? request.DisplayName : null);
        try
        {
            ProjectDescriptor? project;
            ProjectDisplayNameSnapshot? snapshot;
            try
            {
                project = await _importCatalog!.GetByIdAsync(request.ProjectId, CancellationToken.None).ConfigureAwait(false);
                if (project is null || project.Archived || project.Id != request.ProjectId || project.ProjectPath != request.ProjectPath)
                {
                    completion.TrySetResult(Reply("scope_missing"));
                    return;
                }
                snapshot = await _importCatalog.ReadDisplayNameAsync(request.ProjectId, request.ProjectPath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) { completion.TrySetResult(Reply("read_failure")); return; }
            if (snapshot is null) { completion.TrySetResult(Reply("unsupported")); return; }
            if (snapshot.SourcePath != request.SourcePath || snapshot.Revision.ContentHash != request.Revision)
            {
                completion.TrySetResult(Reply("conflict"));
                return;
            }
            try
            {
                var result = await _projectNameWrite!(request.ProjectId, request.ProjectPath, request.SourcePath,
                    snapshot.Revision, request.DisplayName).ConfigureAwait(false);
                completion.TrySetResult(Reply(result switch
                {
                    ProjectDisplayNameRenameStatus.Updated => "ok",
                    ProjectDisplayNameRenameStatus.Conflict => "conflict",
                    _ => "unsupported",
                }));
            }
            catch (Exception) { completion.TrySetResult(Reply("rename_unconfirmed")); }
        }
        finally { lock (_importGate) { if (ReferenceEquals(_projectRenameWork, completion.Task)) _projectRenameWork = null; } }
    }

    private static bool ValidProjectPath(string? path)
    {
        if (!ValidScopeValue(path, 4096)) return false;
        try { return Path.IsPathFullyQualified(path!); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

internal sealed record WorkspaceReadProjectNameRequest(string ExpectedHostEpoch, string ProjectId, string ProjectPath);
internal sealed record WorkspaceReadProjectNameResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath,
    string? SourcePath, string? Revision, string? DisplayName);
internal sealed record WorkspaceRenameProjectRequest(string ExpectedHostEpoch, string ProjectId, string ProjectPath,
    string SourcePath, string Revision, string DisplayName);
internal sealed record WorkspaceRenameProjectResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath,
    string? SourcePath, string? Revision, string? DisplayName);
