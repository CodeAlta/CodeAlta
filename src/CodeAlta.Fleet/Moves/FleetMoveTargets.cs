using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.Moves;

/// <summary>
/// The target of a move as its source talks to it: the methods <c>moves.offer</c>, <c>moves.transfer</c>,
/// <c>moves.prepare</c>, <c>moves.commit</c>, <c>moves.status</c> and <c>moves.complete</c>, on another machine
/// (<see cref="FleetMoveTargetClient"/>) or on this one (<see cref="FleetMoveReceiver.For"/>).
/// </summary>
public interface IFleetMoveTarget
{
    /// <summary>Offers a session, or asks whether it would be taken (<see cref="FleetMoveOffer.Probe"/>).</summary>
    /// <param name="offer">The offer.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>What the target answers.</returns>
    /// <exception cref="FleetException">The target refused the call (a permission it does not give, for one).</exception>
    /// <exception cref="FleetConnectionClosedException">The channel closed.</exception>
    /// <exception cref="TimeoutException">The target did not answer in time.</exception>
    Task<FleetMoveOfferResult> OfferAsync(FleetMoveOffer offer, CancellationToken cancellationToken);

    /// <summary>Sends a chunk of the archive.</summary>
    /// <param name="chunk">The chunk, at the length the target holds.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>How much the target holds now.</returns>
    /// <exception cref="FleetException">The target refused the chunk.</exception>
    /// <exception cref="FleetConnectionClosedException">The channel closed.</exception>
    /// <exception cref="TimeoutException">The target did not answer in time.</exception>
    Task<FleetMoveChunkResult> TransferAsync(FleetMoveChunk chunk, CancellationToken cancellationToken);

    /// <summary>Asks the target to verify the archive and prepare the session.</summary>
    /// <param name="move">The move.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>What the target did.</returns>
    /// <exception cref="FleetException">The target refused the call.</exception>
    /// <exception cref="FleetConnectionClosedException">The channel closed.</exception>
    /// <exception cref="TimeoutException">The target did not answer in time.</exception>
    Task<FleetMoveStepResult> PrepareAsync(FleetMoveRef move, CancellationToken cancellationToken);

    /// <summary>Asks the target, arbiter in paired mode, to commit the move and install the session.</summary>
    /// <param name="move">The move.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>What the target did.</returns>
    /// <exception cref="FleetException">The target refused the call.</exception>
    /// <exception cref="FleetConnectionClosedException">The channel closed.</exception>
    /// <exception cref="TimeoutException">The target did not answer in time.</exception>
    Task<FleetMoveStepResult> CommitAsync(FleetMoveRef move, CancellationToken cancellationToken);

    /// <summary>Asks the outcome of the move.</summary>
    /// <param name="parameters">The move, and whether the arbiter decides one it did not decide yet.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The outcome and the records of the target.</returns>
    /// <exception cref="FleetException">The target refused the call.</exception>
    /// <exception cref="FleetConnectionClosedException">The channel closed.</exception>
    /// <exception cref="TimeoutException">The target did not answer in time.</exception>
    Task<FleetMoveStatus> StatusAsync(FleetMoveStatusParams parameters, CancellationToken cancellationToken);

    /// <summary>Tells the target the source relinquished the session.</summary>
    /// <param name="move">The move.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The state of the move on the target.</returns>
    /// <exception cref="FleetException">The target refused the call.</exception>
    /// <exception cref="FleetConnectionClosedException">The channel closed.</exception>
    /// <exception cref="TimeoutException">The target did not answer in time.</exception>
    Task<FleetMoveStepResult> CompleteAsync(FleetMoveRef move, CancellationToken cancellationToken);
}

/// <summary>
/// The target of a move on another machine, through a channel: the node API of a node the caller reaches, or the
/// methods a caller serves back on the channel it opened (a desktop that receives a session from a node).
/// </summary>
/// <param name="connection">The channel.</param>
/// <param name="via">The machine the other end forwards the calls to, when it relays them; null when it is the target.</param>
public sealed class FleetMoveTargetClient(FleetConnection connection, string? via = null) : IFleetMoveTarget
{
    private static readonly TimeSpan ShortCall = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ChunkCall = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LongCall = TimeSpan.FromMinutes(15);
    private readonly FleetConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <inheritdoc />
    public Task<FleetMoveOfferResult> OfferAsync(FleetMoveOffer offer, CancellationToken cancellationToken)
        => CallAsync(FleetNodeMethods.MovesOffer, offer with { Via = via }, FleetNodeJsonContext.Default.FleetMoveOffer,
            FleetNodeJsonContext.Default.FleetMoveOfferResult, offer.CloneRepository && !offer.Probe ? LongCall : ShortCall, cancellationToken);

    /// <inheritdoc />
    public Task<FleetMoveChunkResult> TransferAsync(FleetMoveChunk chunk, CancellationToken cancellationToken)
        => CallAsync(FleetNodeMethods.MovesTransfer, chunk with { Via = via }, FleetNodeJsonContext.Default.FleetMoveChunk,
            FleetNodeJsonContext.Default.FleetMoveChunkResult, ChunkCall, cancellationToken);

    /// <inheritdoc />
    public Task<FleetMoveStepResult> PrepareAsync(FleetMoveRef move, CancellationToken cancellationToken)
        => CallAsync(FleetNodeMethods.MovesPrepare, move with { Via = via }, FleetNodeJsonContext.Default.FleetMoveRef,
            FleetNodeJsonContext.Default.FleetMoveStepResult, LongCall, cancellationToken);

    /// <inheritdoc />
    public Task<FleetMoveStepResult> CommitAsync(FleetMoveRef move, CancellationToken cancellationToken)
        => CallAsync(FleetNodeMethods.MovesCommit, move with { Via = via }, FleetNodeJsonContext.Default.FleetMoveRef,
            FleetNodeJsonContext.Default.FleetMoveStepResult, LongCall, cancellationToken);

    /// <inheritdoc />
    public Task<FleetMoveStatus> StatusAsync(FleetMoveStatusParams parameters, CancellationToken cancellationToken)
        => CallAsync(FleetNodeMethods.MovesStatus, parameters with { Via = via }, FleetNodeJsonContext.Default.FleetMoveStatusParams,
            FleetNodeJsonContext.Default.FleetMoveStatus, ShortCall, cancellationToken);

    /// <inheritdoc />
    public Task<FleetMoveStepResult> CompleteAsync(FleetMoveRef move, CancellationToken cancellationToken)
        => CallAsync(FleetNodeMethods.MovesComplete, move with { Via = via }, FleetNodeJsonContext.Default.FleetMoveRef,
            FleetNodeJsonContext.Default.FleetMoveStepResult, ShortCall, cancellationToken);

    private async Task<TResult> CallAsync<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> parametersType, JsonTypeInfo<TResult> resultType,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await _connection.SendRequestAsync(method, JsonSerializer.SerializeToElement(parameters, parametersType),
            new FleetRequestOptions { Timeout = timeout }, cancellationToken).ConfigureAwait(false);
        return (result is { } element ? element.Deserialize(resultType) : default)
            ?? throw new FleetException(FleetErrorCodes.ProtocolError, null, $"The target answered '{method}' without a result.");
    }
}

/// <summary>
/// The arbiter of a move in paired mode, asked by the source: the target, through its <see cref="IFleetMoveTarget"/>.
/// A source never commits: the target does, on its own records.
/// </summary>
/// <param name="target">The target of the move.</param>
public sealed class FleetMoveArbiterClient(IFleetMoveTarget target) : ISessionMoveArbiter
{
    private readonly IFleetMoveTarget _target = target ?? throw new ArgumentNullException(nameof(target));

    /// <summary>Not used: in paired mode the target commits its moves itself.</summary>
    /// <param name="moveId">The move.</param>
    /// <param name="epoch">The epoch.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<SessionMoveDecision> CommitAsync(string moveId, long epoch, CancellationToken cancellationToken)
        => throw new NotSupportedException("The target commits a move itself, on its own records.");

    /// <inheritdoc />
    public async Task<SessionMoveDecision?> ResolveAsync(string moveId, CancellationToken cancellationToken)
    {
        try
        {
            var status = await _target.StatusAsync(new FleetMoveStatusParams(moveId, Resolve: true), cancellationToken).ConfigureAwait(false);
            return status.Outcome is SessionMoveState.Committed or SessionMoveState.Aborted
                ? new SessionMoveDecision(moveId, status.Outcome, status.Epoch, DateTimeOffset.UtcNow)
                : null;
        }
        catch (Exception exception) when (exception is FleetException or FleetConnectionClosedException or TimeoutException or IOException or ObjectDisposedException
            or InvalidOperationException)
        {
            // The arbiter cannot be reached, or will not say: nothing is undone.
            return null;
        }
    }
}

/// <summary>
/// The methods of a target that a client serves back on a channel it opened, so that a node it connects to can give it
/// a session (<c>moves.start</c>): a desktop serves them only when the person lets it receive moves, and forwards those
/// that name another machine (<see cref="FleetMoveOffer.Via"/>) to that machine when it started that move.
/// </summary>
public static class FleetMoveRoutes
{
    /// <summary>Registers the methods of a target on a channel, before it starts.</summary>
    /// <param name="connection">The channel, not started.</param>
    /// <param name="resolve">
    /// Gives the target of a call: the move, and the machine it names to forward to (null for this one). It throws a
    /// <see cref="FleetException"/> to refuse (<see cref="FleetErrorCodes.Forbidden"/> when this machine does not receive moves).
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void Register(FleetConnection connection, Func<string, string?, IFleetMoveTarget> resolve)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(resolve);
        On(connection, FleetNodeMethods.MovesOffer, FleetNodeJsonContext.Default.FleetMoveOffer, FleetNodeJsonContext.Default.FleetMoveOfferResult,
            (value, token) => resolve(value.MoveId, value.Via).OfferAsync(value with { Via = null }, token));
        On(connection, FleetNodeMethods.MovesTransfer, FleetNodeJsonContext.Default.FleetMoveChunk, FleetNodeJsonContext.Default.FleetMoveChunkResult,
            (value, token) => resolve(value.MoveId, value.Via).TransferAsync(value with { Via = null }, token));
        On(connection, FleetNodeMethods.MovesPrepare, FleetNodeJsonContext.Default.FleetMoveRef, FleetNodeJsonContext.Default.FleetMoveStepResult,
            (value, token) => resolve(value.MoveId, value.Via).PrepareAsync(value with { Via = null }, token));
        On(connection, FleetNodeMethods.MovesCommit, FleetNodeJsonContext.Default.FleetMoveRef, FleetNodeJsonContext.Default.FleetMoveStepResult,
            (value, token) => resolve(value.MoveId, value.Via).CommitAsync(value with { Via = null }, token));
        On(connection, FleetNodeMethods.MovesStatus, FleetNodeJsonContext.Default.FleetMoveStatusParams, FleetNodeJsonContext.Default.FleetMoveStatus,
            (value, token) => resolve(value.MoveId, value.Via).StatusAsync(value with { Via = null }, token));
        On(connection, FleetNodeMethods.MovesComplete, FleetNodeJsonContext.Default.FleetMoveRef, FleetNodeJsonContext.Default.FleetMoveStepResult,
            (value, token) => resolve(value.MoveId, value.Via).CompleteAsync(value with { Via = null }, token));
    }

    private static void On<TParams, TResult>(FleetConnection connection, string method, JsonTypeInfo<TParams> parametersType, JsonTypeInfo<TResult> resultType,
        Func<TParams, CancellationToken, Task<TResult>> handler)
        where TParams : class
    {
        connection.RegisterRequestHandler(method, async (context, cancellationToken) =>
        {
            TParams? parameters;
            try
            {
                parameters = context.Params is { ValueKind: not JsonValueKind.Null } element ? element.Deserialize(parametersType) : null;
            }
            catch (JsonException exception)
            {
                throw new FleetException(FleetErrorCodes.InvalidParams, null, $"The parameters of '{method}' are not valid: {exception.Message}");
            }

            if (parameters is null) throw new FleetException(FleetErrorCodes.InvalidParams, null, $"'{method}' needs parameters.");
            try
            {
                SessionMoveTransactionStore.ValidateMoveId(MoveIdOf(parameters));
            }
            catch (ArgumentException exception)
            {
                throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message);
            }

            var result = await handler(parameters, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, resultType);
        });
    }

    private static string? MoveIdOf(object parameters) => parameters switch
    {
        FleetMoveOffer offer => offer.MoveId,
        FleetMoveChunk chunk => chunk.MoveId,
        FleetMoveRef move => move.MoveId,
        FleetMoveStatusParams status => status.MoveId,
        _ => null,
    };
}
