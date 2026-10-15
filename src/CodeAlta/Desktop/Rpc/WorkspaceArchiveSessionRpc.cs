using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    // Session, archived (true) or restored (false), with the sessions below it.
    private readonly Func<string, bool, bool, Task<SessionArchiveResult>>? _archiveSession;

    internal WorkspaceService(OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch,
        Func<string, bool, bool, Task<SessionArchiveResult>> archiveSession) : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(archiveSession);
        _archiveSession = archiveSession;
    }

    /// <summary>
    /// Archives an exact owned session, with the sessions below it when asked: it leaves the lists and keeps its
    /// history, its project and its space. A session at work, or one below it, is refused with the session that works.
    /// </summary>
    [NeoRpcMethod("archiveSession")]
    public Task<WorkspaceArchiveSessionResponse> ArchiveSessionAsync(WorkspaceArchiveSessionRequest request, CancellationToken cancellationToken)
        => SetArchivedAsync(request, archived: true, cancellationToken);

    /// <summary>Takes an exact archived session out of the archive, with the archived sessions below it when asked.</summary>
    [NeoRpcMethod("restoreSession")]
    public Task<WorkspaceArchiveSessionResponse> RestoreSessionAsync(WorkspaceArchiveSessionRequest request, CancellationToken cancellationToken)
        => SetArchivedAsync(request, archived: false, cancellationToken);

    private async Task<WorkspaceArchiveSessionResponse> SetArchivedAsync(WorkspaceArchiveSessionRequest request, bool archived, CancellationToken cancellationToken)
    {
        WorkspaceArchiveSessionResponse Reply(string status) => new(status, _importEpoch, request?.SessionId, [], null);
        if (_archiveSession is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.SessionId, 256)) return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceArchiveSessionResponse> work;
        lock (_sessionGate)
        {
            if (_sessionsClosed) return Reply("closed");
            if (_deleteWork is not null || _renameWork is not null || _branchWork is not null || _moveWork is not null || _projectDeleteWork is not null) return Reply("busy");
            // An archive shares the slot of the moves: one at a time, drained when the window closes.
            var slot = new TaskCompletionSource<WorkspaceMoveSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<WorkspaceArchiveSessionResponse>(CatalogReplyOptions);
            _moveWork = slot.Task;
            work = answer.Task;
            _ = SetArchivedOwnedAsync(request, archived, answer, slot);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetArchivedOwnedAsync(WorkspaceArchiveSessionRequest request, bool archived, TaskCompletionSource<WorkspaceArchiveSessionResponse> answer,
        TaskCompletionSource<WorkspaceMoveSessionResponse> slot)
    {
        WorkspaceArchiveSessionResponse response;
        try
        {
            var result = await _archiveSession!(request.SessionId, archived, request.WithChildren).ConfigureAwait(false);
            response = new(result.Status, _importEpoch, request.SessionId, [.. result.Changed], result.BlockingSessionId);
        }
        catch (Exception)
        {
            // Journals may already have been written. No retry or definite refusal after admission.
            response = new("archive_unconfirmed", _importEpoch, request.SessionId, [], null);
        }
        SettleCatalogWork(ref _moveWork, slot, new("ok", _importEpoch, null, null, null, null, null, [], []));
        answer.TrySetResult(response);
    }
}

/// <summary>Asks to archive a session, or to take it out of the archive.</summary>
/// <param name="WithChildren">The sessions below it are archived or restored with it.</param>
internal sealed record WorkspaceArchiveSessionRequest(string ExpectedHostEpoch, string SessionId, bool WithChildren);

/// <summary>
/// What archiving a session, or taking it out of the archive, did. Status is <c>ok</c>, <c>session_missing</c>,
/// <c>session_in_use</c>, <c>queue_pending</c>, <c>interaction_pending</c>, <c>io_failed</c>, <c>archive_partial</c>,
/// <c>archive_unconfirmed</c>, <c>busy</c>, <c>closed</c>, <c>invalid_scope</c>, <c>stale_epoch</c> or <c>unconfigured</c>.
/// </summary>
/// <param name="Changed">The sessions whose archive state changed.</param>
/// <param name="BlockingSessionId">The session at work, or the one that could not be changed.</param>
internal sealed record WorkspaceArchiveSessionResponse(string Status, string? HostEpoch, string? SessionId, IReadOnlyList<string> Changed, string? BlockingSessionId);
