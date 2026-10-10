namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>
    /// Moves an idle project session, with the sessions it started in the same project, to another project.
    /// Caller cancellation is owned by the waiting bridge only.
    /// </summary>
    /// <remarks>Uses the exclusive catalog-mutation reservation shared with deletion and provider selection: send and
    /// queue admission and shutdown honor and drain it. Refuses a session that runs, waits for an answer, or runs
    /// remotely, rather than stopping it. No automatic retry is performed.</remarks>
    /// <param name="sessionId">Exact catalog session identity.</param>
    /// <param name="projectId">Exact identity of the project the session belongs to.</param>
    /// <param name="workspacePath">Exact folder of that project.</param>
    /// <param name="targetProjectId">Exact identity of the project to move the session to.</param>
    /// <param name="targetPath">Exact folder of that project.</param>
    /// <returns>What moved, and the status of the move (see <see cref="OwnedSessionMoveResult.Status"/>).</returns>
    /// <exception cref="ArgumentException">A required identity is blank.</exception>
    public Task<OwnedSessionMoveResult> MoveSessionAsync(string sessionId, string projectId, string workspacePath, string targetProjectId, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        TaskCompletionSource<OwnedSessionMoveResult> completion;
        // The reservation others wait on, ended with the status of the move.
        TaskCompletionSource<string> reservation;
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult(OwnedSessionMoveResult.Refused("closed"));
            if (_deleteWork is not null || IsOwnerBusy(sessionId)) return Task.FromResult(OwnedSessionMoveResult.Refused("busy"));
            if (Asks.List(sessionId).Head is not null) return Task.FromResult(OwnedSessionMoveResult.Refused("interaction_pending"));
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            reservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = reservation.Task;
            _ = MoveSessionOwnedAsync(sessionId, projectId, workspacePath, targetProjectId, targetPath, completion, reservation);
        }
        return completion.Task;
    }

    private async Task MoveSessionOwnedAsync(string sessionId, string projectId, string workspacePath, string targetProjectId, string targetPath,
        TaskCompletionSource<OwnedSessionMoveResult> completion, TaskCompletionSource<string> reservation)
    {
        try
        {
            completion.TrySetResult(await _runtime.MoveOwnedSessionAsync(sessionId, projectId, workspacePath, targetProjectId, targetPath,
                member => { lock (_gate) return IsOwnerBusy(member) || Asks.List(member).Head is not null; }).ConfigureAwait(false));
        }
        catch (Exception)
        {
            // Some journals may already have been rewritten. The caller must inspect, never retry automatically.
            completion.TrySetResult(OwnedSessionMoveResult.Refused("move_unconfirmed"));
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_deleteWork, reservation.Task)) _deleteWork = null; }
            reservation.TrySetResult(completion.Task.IsCompletedSuccessfully ? completion.Task.Result.Status : "move_unconfirmed");
        }
    }

    // Whether this owner has work of the session going on. The caller holds the gate.
    private bool IsOwnerBusy(string sessionId)
        => _active.ContainsKey(sessionId) || _steering.Contains(sessionId) || _compacting.Contains(sessionId)
            || _abortingRuns.Contains(sessionId) || _queueing.Contains(sessionId) || _remoteControlling.ContainsKey(sessionId)
            || _operations.Values.Any(operation => !operation.Released && string.Equals(operation.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            || _queues.Values.Any(operation => !operation.Released && string.Equals(operation.Request.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
}
