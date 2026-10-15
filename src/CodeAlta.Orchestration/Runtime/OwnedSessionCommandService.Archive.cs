namespace CodeAlta.Orchestration.Runtime;

/// <summary>Archives sessions and takes them out of the archive.</summary>
public interface ISessionArchive
{
    /// <summary>
    /// Archives a session, or takes it out of the archive; with <paramref name="withChildren"/> the sessions below it
    /// too. A session at work, or one below it, cannot be archived.
    /// </summary>
    /// <param name="sessionId">Exact catalog session identity.</param>
    /// <param name="archived">Whether the session is archived (true) or restored (false).</param>
    /// <param name="withChildren">Whether the sessions below it are archived or restored with it.</param>
    /// <returns>What changed, and the status (see <see cref="SessionArchiveResult.Status"/>).</returns>
    /// <exception cref="ArgumentException">The session is blank.</exception>
    Task<SessionArchiveResult> SetSessionArchivedAsync(string sessionId, bool archived, bool withChildren);
}

public sealed partial class OwnedSessionCommandService : ISessionArchive
{
    /// <inheritdoc/>
    /// <remarks>Uses the exclusive catalog-mutation reservation shared with deletion and moves: send and queue admission
    /// and shutdown honor and drain it. Refuses a session that runs, waits for an answer or has a send of this owner
    /// under way, rather than stopping it. No automatic retry is performed.</remarks>
    public Task<SessionArchiveResult> SetSessionArchivedAsync(string sessionId, bool archived, bool withChildren)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult(SessionArchiveResult.Refused("closed"));
            if (_deleteWork is not null) return Task.FromResult(SessionArchiveResult.Refused("busy"));
            if (archived && Asks.List(sessionId).Head is not null) return Task.FromResult(SessionArchiveResult.Refused("interaction_pending", sessionId));
            var completion = new TaskCompletionSource<SessionArchiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reservation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = reservation.Task;
            _ = RunArchiveAsync(sessionId, archived, withChildren, completion, reservation);
            return completion.Task;
        }
    }

    private async Task RunArchiveAsync(string sessionId, bool archived, bool withChildren, TaskCompletionSource<SessionArchiveResult> completion,
        TaskCompletionSource<string> reservation)
    {
        try
        {
            completion.TrySetResult(await _runtime.SetOwnedSessionArchivedAsync(sessionId, archived, withChildren, OwnerBusyOrAsking).ConfigureAwait(false));
        }
        catch (Exception)
        {
            // Some journals may already have been written. The caller must inspect, never retry automatically.
            completion.TrySetResult(SessionArchiveResult.Refused("archive_unconfirmed"));
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_deleteWork, reservation.Task)) _deleteWork = null; }
            reservation.TrySetResult(completion.Task.IsCompletedSuccessfully ? completion.Task.Result.Status : "archive_unconfirmed");
        }
    }
}
