using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>
    /// Gives an idle session, with the sessions it started, another space. The resources of the new space (its
    /// providers, tools, identity) are the ones of its next turn. Caller cancellation is owned by the waiting bridge only.
    /// </summary>
    /// <remarks>Uses the exclusive catalog-mutation reservation shared with deletion, moves and provider selection: send
    /// admission and shutdown honor and drain it. Refuses a session that runs or waits for an answer rather
    /// than stopping it. The caller validates that the space exists. No automatic retry is performed.</remarks>
    /// <param name="sessionId">Exact catalog session identity.</param>
    /// <param name="spaceId">The identifier of the space.</param>
    /// <returns>What moved, and the status of the move (see <see cref="OwnedSessionSpaceMoveResult.Status"/>).</returns>
    /// <exception cref="ArgumentException">The session is blank, or the space is not the identifier of a space.</exception>
    public Task<OwnedSessionSpaceMoveResult> MoveSessionToSpaceAsync(string sessionId, string spaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SpaceDescriptor.IsValidId(spaceId)) throw new ArgumentException($"'{spaceId}' is not the identifier of a space.", nameof(spaceId));
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult(OwnedSessionSpaceMoveResult.Refused("closed"));
            if (_deleteWork is not null || IsOwnerBusy(sessionId)) return Task.FromResult(OwnedSessionSpaceMoveResult.Refused("busy"));
            if (Asks.List(sessionId).Head is not null) return Task.FromResult(OwnedSessionSpaceMoveResult.Refused("interaction_pending"));
            return Reserve(() => _runtime.MoveOwnedSessionToSpaceAsync(sessionId, spaceId, OwnerBusyOrAsking));
        }
    }

    /// <summary>
    /// Gives the chats of a space another space: what deleting a space does with its chats, which go to the default
    /// space. A chat at work is left as it is; it reads as in the default space all the same once its space is gone.
    /// </summary>
    /// <param name="fromSpaceId">The space whose chats move.</param>
    /// <param name="toSpaceId">The space they move to.</param>
    /// <returns>The chats that moved and those that were left.</returns>
    /// <exception cref="ArgumentException">A space is not the identifier of a space.</exception>
    public Task<OwnedSessionSpaceMoveResult> MoveSpaceChatsAsync(string fromSpaceId, string toSpaceId)
    {
        if (!SpaceDescriptor.IsValidId(fromSpaceId)) throw new ArgumentException($"'{fromSpaceId}' is not the identifier of a space.", nameof(fromSpaceId));
        if (!SpaceDescriptor.IsValidId(toSpaceId)) throw new ArgumentException($"'{toSpaceId}' is not the identifier of a space.", nameof(toSpaceId));
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult(OwnedSessionSpaceMoveResult.Refused("closed"));
            if (_deleteWork is not null) return Task.FromResult(OwnedSessionSpaceMoveResult.Refused("busy"));
            return Reserve(() => _runtime.MoveOwnedSpaceChatsAsync(fromSpaceId, toSpaceId, OwnerBusyOrAsking));
        }
    }

    private bool OwnerBusyOrAsking(string member)
    {
        lock (_gate) return IsOwnerBusy(member) || Asks.List(member).Head is not null;
    }

    // Holds the catalog-mutation reservation while the move runs. The caller holds the gate.
    private Task<OwnedSessionSpaceMoveResult> Reserve(Func<Task<OwnedSessionSpaceMoveResult>> move)
    {
        var completion = new TaskCompletionSource<OwnedSessionSpaceMoveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _deleteWork = reservation.Task;
        _ = RunSpaceMoveAsync(move, completion, reservation);
        return completion.Task;
    }

    private async Task RunSpaceMoveAsync(Func<Task<OwnedSessionSpaceMoveResult>> move, TaskCompletionSource<OwnedSessionSpaceMoveResult> completion,
        TaskCompletionSource<string> reservation)
    {
        try
        {
            completion.TrySetResult(await move().ConfigureAwait(false));
        }
        catch (Exception)
        {
            // Some journals may already have been rewritten. The caller must inspect, never retry automatically.
            completion.TrySetResult(OwnedSessionSpaceMoveResult.Refused("move_unconfirmed"));
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_deleteWork, reservation.Task)) _deleteWork = null; }
            reservation.TrySetResult(completion.Task.IsCompletedSuccessfully ? completion.Task.Result.Status : "move_unconfirmed");
        }

        // A session that was controlled remotely is attached again, in its new space, with its remote control on.
        if (completion.Task.IsCompletedSuccessfully)
            foreach (var moved in completion.Task.Result.Moved)
                if (_runtime.RemoteControlWanted(moved)) _ = ReconnectRemoteControlAsync(moved);
    }
}
