using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    internal WorkspaceService(CodeAlta.Orchestration.Runtime.OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch,
        Func<string, string?, string, string, Task<bool>> renameSession) : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(renameSession);
        _renameSession = renameSession;
    }

    /// <summary>Renames an exact owned catalog session; cancellation only stops waiting after admission.</summary>
    [NeoRpcMethod("renameSession")]
    public async Task<WorkspaceRenameSessionResponse> RenameSessionAsync(WorkspaceRenameSessionRequest request, CancellationToken cancellationToken)
    {
        WorkspaceRenameSessionResponse Reply(string status) => new(status, _importEpoch,
            status is "invalid_scope" or "unconfigured" ? null : request?.Scope,
            status is "invalid_scope" or "unconfigured" ? null : request?.ProjectId,
            status is "invalid_scope" or "unconfigured" ? null : request?.ProjectPath,
            status is "invalid_scope" or "unconfigured" ? null : request?.SessionId, status == "ok" ? request?.Title : null);
        if (_renameSession is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.SessionId, 256)
            || !ValidScopeValue(request.Title, 256) || request.Scope is not ("global" or "project")
            || request.Scope == "global" && (request.ProjectId is not null || request.ProjectPath != _importCatalog.Options.GlobalRoot)
            || request.Scope == "project" && (!ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096)))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceRenameSessionResponse> work;
        lock (_sessionGate)
        {
            if (_sessionsClosed) return Reply("closed");
            if (_renameWork is not null) return Reply("busy");
            var completion = new TaskCompletionSource<WorkspaceRenameSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _renameWork = work = completion.Task;
            _ = RenameOwnedAsync(request, completion);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RenameOwnedAsync(WorkspaceRenameSessionRequest request, TaskCompletionSource<WorkspaceRenameSessionResponse> completion)
    {
        WorkspaceRenameSessionResponse Reply(string status) => new(status, _importEpoch, request.Scope, request.ProjectId,
            request.ProjectPath, request.SessionId, status == "ok" ? request.Title : null);
        try
        {
            if (request.Scope == "project")
            {
                var project = await _importCatalog!.GetByIdAsync(request.ProjectId!, CancellationToken.None).ConfigureAwait(false);
                if (project is null || project.Archived || project.Id != request.ProjectId || project.ProjectPath != request.ProjectPath)
                {
                    completion.TrySetResult(Reply("scope_missing"));
                    return;
                }
            }
            var renamed = await _renameSession!(request.SessionId, request.ProjectId, request.ProjectPath!, request.Title).ConfigureAwait(false);
            completion.TrySetResult(Reply(renamed ? "ok" : "session_missing"));
        }
        catch (Exception)
        {
            // The journal append may have committed. No retry or definite refusal after admission.
            completion.TrySetResult(Reply("rename_unconfirmed"));
        }
        finally { lock (_sessionGate) { if (ReferenceEquals(_renameWork, completion.Task)) _renameWork = null; } }
    }
}

internal sealed record WorkspaceRenameSessionRequest(string ExpectedHostEpoch, string Scope, string? ProjectId,
    string? ProjectPath, string SessionId, string Title);
internal sealed record WorkspaceRenameSessionResponse(string Status, string? HostEpoch, string? Scope, string? ProjectId,
    string? ProjectPath, string? SessionId, string? Title);
