using CodeAlta.Catalog.SessionMoves;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>
    /// Freezes an idle project session for a move to another machine and writes its bundle: the journal and the
    /// attachments, what its provider keeps of it, and the git payload of its work in progress, described by a manifest.
    /// </summary>
    /// <remarks>
    /// Uses the exclusive catalog-mutation reservation shared with deletion, moves and changes of folder. A session at
    /// work is refused rather than stopped: a run, a queued prompt, a pending question or permission, a background task,
    /// a job, a script of Code Mode, Remote Control. Once frozen, nothing of this machine writes the session, the command
    /// line included, until the move is undone (<see cref="SessionRuntimeService.UnfreezeMovedSession"/>) or finished
    /// (<see cref="SessionRuntimeService.RelinquishMovedSession"/>). A failed export unfreezes it.
    /// </remarks>
    /// <param name="sessionId">Exact catalog session identity.</param>
    /// <param name="request">The move and the folder of the bundle.</param>
    /// <returns>What was exported (see <see cref="SessionBundleExportResult.Status"/>).</returns>
    /// <exception cref="ArgumentException">The session or the move id is not valid, or the folder is blank.</exception>
    public Task<SessionBundleExportResult> ExportSessionBundleAsync(string sessionId, SessionBundleExportRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        SessionMoveTransactionStore.ValidateMoveId(request.MoveId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BundleFolder);
        TaskCompletionSource<SessionBundleExportResult> completion;
        TaskCompletionSource<string> reservation;
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult(SessionBundleExportResult.Refused("closed"));
            if (_deleteWork is not null || IsOwnerBusy(sessionId) || HasScheduledSwitch(sessionId)) return Task.FromResult(SessionBundleExportResult.Refused("busy"));
            if (Asks.List(sessionId).Head is not null) return Task.FromResult(SessionBundleExportResult.Refused("interaction_pending"));
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            reservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = reservation.Task;
            _ = ExportSessionBundleOwnedAsync(sessionId, request, completion, reservation);
        }

        return completion.Task;
    }

    private async Task ExportSessionBundleOwnedAsync(string sessionId, SessionBundleExportRequest request,
        TaskCompletionSource<SessionBundleExportResult> completion, TaskCompletionSource<string> reservation)
    {
        SessionBundleExportResult result;
        try
        {
            result = await _runtime.ExportOwnedSessionBundleAsync(sessionId, request,
                member => { lock (_gate) return IsOwnerBusy(member) || Asks.List(member).Head is not null; }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = SessionBundleExportResult.Refused("export_failed", exception.Message);
        }

        lock (_gate)
        {
            if (ReferenceEquals(_deleteWork, reservation.Task)) _deleteWork = null;
        }

        reservation.TrySetResult(result.Status);
        completion.TrySetResult(result);
    }
}
