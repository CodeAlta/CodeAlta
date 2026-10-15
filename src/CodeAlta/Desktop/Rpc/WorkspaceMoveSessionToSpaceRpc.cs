using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    private readonly Func<string, string, Task<OwnedSessionSpaceMoveResult>>? _moveToSpace;

    internal WorkspaceService(OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch,
        Func<string, string, Task<OwnedSessionSpaceMoveResult>> moveToSpace) : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(moveToSpace);
        _moveToSpace = moveToSpace;
        _sessionSpaces = new SpaceCatalog(catalog);
    }

    /// <summary>
    /// Gives an exact owned session, with the sessions it started, another space; cancellation only stops waiting after
    /// admission. The answer is <c>space_missing</c> for a space that does not exist.
    /// </summary>
    [NeoRpcMethod("moveSessionToSpace")]
    public async Task<WorkspaceMoveToSpaceResponse> MoveSessionToSpaceAsync(WorkspaceMoveToSpaceRequest request, CancellationToken cancellationToken)
    {
        WorkspaceMoveToSpaceResponse Refused(string status) => new(status, _importEpoch, request?.SessionId, request?.SpaceId, [], []);
        if (_moveToSpace is null || _sessionSpaces is null || _importEpoch is null) return Refused("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.SessionId, 256) || !SpaceDescriptor.IsValidId(request.SpaceId))
            return Refused("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Refused("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceMoveToSpaceResponse> work;
        lock (_sessionGate)
        {
            if (_sessionsClosed) return Refused("closed");
            if (_deleteWork is not null || _renameWork is not null || _branchWork is not null || _moveWork is not null || _projectDeleteWork is not null) return Refused("busy");
            var completion = new TaskCompletionSource<WorkspaceMoveSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<WorkspaceMoveToSpaceResponse>(CatalogReplyOptions);
            // The move to a space shares the slot of the moves: one at a time, drained when the window closes.
            _moveWork = completion.Task;
            work = answer.Task;
            _ = MoveToSpaceOwnedAsync(request, answer, completion);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MoveToSpaceOwnedAsync(WorkspaceMoveToSpaceRequest request, TaskCompletionSource<WorkspaceMoveToSpaceResponse> answer,
        TaskCompletionSource<WorkspaceMoveSessionResponse> slot)
    {
        WorkspaceMoveToSpaceResponse? response = null;
        try
        {
            if (await _sessionSpaces!.GetAsync(request.SpaceId, CancellationToken.None).ConfigureAwait(false) is null)
            {
                response = new("space_missing", _importEpoch, request.SessionId, request.SpaceId, [], []);
                return;
            }

            var result = await _moveToSpace!(request.SessionId, request.SpaceId).ConfigureAwait(false);
            if (LogManager.IsInitialized)
                foreach (var warning in result.Warnings) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Warn($"Session move to a space: {warning}");
            response = new(result.Status, _importEpoch, request.SessionId, request.SpaceId, [.. result.Moved],
                [.. result.Warnings.Take(8).Select(static warning => warning.Length <= 1024 ? warning : warning[..1024])]);
        }
        catch (Exception)
        {
            // Journals may already have been rewritten. No retry or definite refusal after admission.
            response = new("move_unconfirmed", _importEpoch, request.SessionId, request.SpaceId, [], []);
        }
        finally
        {
            SettleCatalogWork(ref _moveWork, slot, new("ok", _importEpoch, null, null, null, null, null, [], []));
            if (response is not null) answer.TrySetResult(response);
        }
    }
}

/// <summary>Asks to give a session another space.</summary>
internal sealed record WorkspaceMoveToSpaceRequest(string ExpectedHostEpoch, string SessionId, string SpaceId);

/// <summary>
/// What giving a session another space did. Status is <c>ok</c>, <c>space_missing</c>, <c>busy</c>, <c>closed</c>,
/// <c>session_in_use</c>, <c>interaction_pending</c>, <c>session_missing</c>, <c>requires_recovery</c>,
/// <c>move_partial</c>, <c>move_unconfirmed</c>, <c>invalid_scope</c>, <c>stale_epoch</c> or <c>unconfigured</c>.
/// </summary>
/// <param name="Moved">The sessions whose space changed: the session and the sessions it started.</param>
/// <param name="Warnings">What went wrong without stopping the move.</param>
internal sealed record WorkspaceMoveToSpaceResponse(string Status, string? HostEpoch, string? SessionId, string? SpaceId,
    IReadOnlyList<string> Moved, IReadOnlyList<string> Warnings);
