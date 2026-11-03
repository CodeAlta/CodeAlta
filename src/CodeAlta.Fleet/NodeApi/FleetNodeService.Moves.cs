using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.Moves;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

// The moves of the node: as a target (offer, transfer, prepare, commit, status, complete), arbiter of them in paired
// mode, for the devices it lets move sessions here; as a source (start), giving one of its sessions to the caller, or
// through it to another machine, by calling the target's methods back on the same channel.
public sealed partial class FleetNodeService
{
    private const int MaxMoveIdLength = 128;

    /// <summary>Gets the target side of the moves of the node.</summary>
    public FleetMoveReceiver MoveReceiver { get; }

    /// <summary>Gets the source side of the moves of the node.</summary>
    public FleetMoveSender MoveSender { get; }

    private void RegisterMoves(FleetNodeCaller caller, Access target)
    {
        On(caller, FleetNodeMethods.MovesOffer, target, FleetNodeJsonContext.Default.FleetMoveOffer, FleetNodeJsonContext.Default.FleetMoveOfferResult,
            (call, value, token) => { RequireWork(); return MoveReceiver.OfferAsync(value, call.Caller.DeviceId, token); });
        On(caller, FleetNodeMethods.MovesTransfer, target, FleetNodeJsonContext.Default.FleetMoveChunk, FleetNodeJsonContext.Default.FleetMoveChunkResult,
            (call, value, token) => MoveReceiver.TransferAsync(value, call.Caller.DeviceId, token));
        On(caller, FleetNodeMethods.MovesPrepare, target, FleetNodeJsonContext.Default.FleetMoveRef, FleetNodeJsonContext.Default.FleetMoveStepResult,
            (call, value, token) => MoveReceiver.PrepareAsync(RequireMoveId(value.MoveId), call.Caller.DeviceId, token));
        On(caller, FleetNodeMethods.MovesCommit, target, FleetNodeJsonContext.Default.FleetMoveRef, FleetNodeJsonContext.Default.FleetMoveStepResult,
            (call, value, token) => MoveReceiver.CommitAsync(RequireMoveId(value.MoveId), call.Caller.DeviceId, token));
        On(caller, FleetNodeMethods.MovesStatus, target, FleetNodeJsonContext.Default.FleetMoveStatusParams, FleetNodeJsonContext.Default.FleetMoveStatus,
            (call, value, token) => MoveReceiver.StatusAsync(RequireMoveId(value.MoveId), value.Resolve, token));
        On(caller, FleetNodeMethods.MovesComplete, target, FleetNodeJsonContext.Default.FleetMoveRef, FleetNodeJsonContext.Default.FleetMoveStepResult,
            (call, value, token) => MoveReceiver.CompleteAsync(RequireMoveId(value.MoveId), call.Caller.DeviceId, token));
    }

    // The node gives one of its sessions to the caller: the caller serves the target's methods back on this channel
    // (a desktop that receives moves), and forwards them when it names another machine (Via).
    private async Task<FleetMoveResult> StartMoveAsync(FleetNodeCall call, FleetMoveStartParams parameters, CancellationToken cancellationToken)
    {
        var sessionId = RequireSessionId(parameters.SessionId);
        var moveId = RequireMoveId(parameters.MoveId);
        if (parameters.Probe)
        {
            var preview = await MoveSender.PreviewAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return new FleetMoveResult("preview", preview.Status == "ok" ? preview.Busy : preview.Status, preview.Message, moveId, sessionId, 0, null, [], preview);
        }

        // A move that is under way goes on when the node stops accepting new work; a new one does not start.
        if (MoveSender.Moves.Store.Read(moveId, SessionMoveRole.Source) is null) RequireWork();
        var target = new FleetMoveTargetClient(call.Caller.Connection, parameters.Via);
        var name = parameters.TargetName is { Length: > 0 and <= 128 } given ? given.Trim() : call.Device?.Name;
        LogInfo($"Device {call.Device?.Name ?? call.Caller.DeviceId} moves session {sessionId} to {name ?? "itself"} (move {moveId}).");
        var result = await MoveSender.MoveAsync(sessionId, moveId, target, name, parameters.CloneRepository, cancellationToken).ConfigureAwait(false);
        LogInfo($"Move {moveId} of session {sessionId}: {result.Status}{(result.Code is { } code ? $" ({code})" : "")}.");
        return result;
    }

    private Task<FleetMoveList> ListMovesAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
        => Task.FromResult(new FleetMoveList([.. MoveSender.Moves.Store.List().Select(FleetMoveReceiver.Describe)]));

    private static string RequireMoveId(string? moveId)
    {
        try
        {
            SessionMoveTransactionStore.ValidateMoveId(moveId);
            return moveId!;
        }
        catch (ArgumentException)
        {
            throw new FleetException(FleetErrorCodes.InvalidParams, null, $"A move id has 1 to {MaxMoveIdLength} letters, digits, '-' or '_'.");
        }
    }
}
