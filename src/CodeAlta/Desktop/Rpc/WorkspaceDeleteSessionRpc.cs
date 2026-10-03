using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    internal WorkspaceService(CodeAlta.Orchestration.Runtime.OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch,
        Func<string, string?, string, string, Task<string>> deleteSession) : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(deleteSession);
        _deleteSession = deleteSession;
    }

    /// <summary>Deletes only the explicitly confirmed, exact catalog session; never a project or its files.</summary>
    [NeoRpcMethod("deleteSession")]
    public async Task<WorkspaceDeleteSessionResponse> DeleteSessionAsync(WorkspaceDeleteSessionRequest request, CancellationToken cancellationToken)
    {
        WorkspaceDeleteSessionResponse Reply(string status) => new(status, _importEpoch,
            status is "invalid_scope" or "unconfigured" ? null : request?.Scope,
            status is "invalid_scope" or "unconfigured" ? null : request?.ProjectId,
            status is "invalid_scope" or "unconfigured" ? null : request?.ProjectPath,
            status is "invalid_scope" or "unconfigured" ? null : request?.SessionId);
        if (_deleteSession is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.SessionId, 256)
            || !ValidScopeValue(request.ConfirmedTitle, 256) || request.Scope is not ("global" or "project")
            || request.Scope == "global" && (request.ProjectId is not null || request.ProjectPath != _importCatalog.Options.GlobalRoot)
            || request.Scope == "project" && (!ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096)))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceDeleteSessionResponse> work;
        lock (_sessionGate)
        {
            if (_sessionsClosed) return Reply("closed");
            if (_deleteWork is not null || _renameWork is not null) return Reply("busy");
            var completion = new TaskCompletionSource<WorkspaceDeleteSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = work = completion.Task;
            _ = DeleteSessionOwnedAsync(request, completion);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteSessionOwnedAsync(WorkspaceDeleteSessionRequest request,
        TaskCompletionSource<WorkspaceDeleteSessionResponse> completion)
    {
        WorkspaceDeleteSessionResponse Reply(string status) => new(status, _importEpoch, request.Scope,
            request.ProjectId, request.ProjectPath, request.SessionId);
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
            var result = await _deleteSession!(request.SessionId, request.ProjectId, request.ProjectPath!, request.ConfirmedTitle)
                .ConfigureAwait(false);
            completion.TrySetResult(Reply(result));
        }
        catch (Exception)
        {
            // Journal removal can precede cache/bridge failure; never claim definite refusal or retry.
            completion.TrySetResult(Reply("delete_unconfirmed"));
        }
        finally { lock (_sessionGate) { if (ReferenceEquals(_deleteWork, completion.Task)) _deleteWork = null; } }
    }
}

internal sealed record WorkspaceDeleteSessionRequest(string ExpectedHostEpoch, string Scope, string? ProjectId,
    string? ProjectPath, string SessionId, string ConfirmedTitle);
internal sealed record WorkspaceDeleteSessionResponse(string Status, string? HostEpoch, string? Scope, string? ProjectId,
    string? ProjectPath, string? SessionId);
