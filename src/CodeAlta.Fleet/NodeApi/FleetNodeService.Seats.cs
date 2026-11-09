using System.Collections.Concurrent;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.NodeApi;

// The seats of the machine (doc/fleet/node.md, limits): how many trees of sessions work at once, set by its owner in
// fleet/limits.json. The work that comes from elsewhere asks a seat after its budgets: a prompt sent from another computer, the
// first prompt of a session an agent of another machine starts, a queued prompt of the work of the fleet when its turn comes.
// One that finds none waits, first in first out; the work a person starts at the machine itself is counted and never waits.
public sealed partial class FleetNodeService
{
    private const int MaxKnownRoots = 4096;
    private const int MaxParentDepth = 32;
    private const int MaxSeatAdmitted = 4096;

    // The root of the tree of each session looked at: a parent never changes.
    private readonly ConcurrentDictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);

    // The sends that wait for a seat, by their hold: a retry while one waits answers its place.
    private readonly ConcurrentDictionary<string, byte> _seatHolds = new(StringComparer.Ordinal);

    // The sends seated and sent, by their hold: a retry after a lost answer is the runtime's replay, not a new run.
    private readonly ConcurrentDictionary<string, byte> _seatAdmitted = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the seats of the machine: its limit of sessions at work at once (<see cref="FleetMachineLimits"/>), the seats taken
    /// and the runs that wait for one.
    /// </summary>
    public FleetSessionSeats Seats { get; }

    /// <summary>Gets the load of the machine as last decided, which <c>node.info</c> and the presence of the node publish.</summary>
    /// <returns>The seats taken, the runs that wait and the limit.</returns>
    public FleetMachineLoad ReadLoad()
    {
        var seats = Seats.Snapshot();
        return new FleetMachineLoad(seats.Working, seats.Queue.Count, seats.Limit);
    }

    // The load decided again now: the seats are otherwise decided only while runs wait, and a reading must say what is.
    private async Task<FleetSeatsSnapshot> ReadSeatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Seats.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Stopping: what was decided last.
        }

        return Seats.Snapshot();
    }

    private async Task<FleetMachineLoad> ReadLoadAsync(CancellationToken cancellationToken)
    {
        var seats = await ReadSeatsAsync(cancellationToken).ConfigureAwait(false);
        return new FleetMachineLoad(seats.Working, seats.Queue.Count, seats.Limit);
    }

    private FleetSessionSeats CreateSeats()
    {
        var seats = new FleetSessionSeats(() => FleetMachineLimits.ReadSessions(_host.CatalogOptions.FleetRoot), BusyRootsAsync, _options.TimeProvider);
        // The limit as the file says it, before the first run asks: a reading of the load names it from the start.
        _ = Task.Run(async () =>
        {
            try { await seats.EvaluateAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException) { }
        });
        return seats;
    }

    // The roots of the trees of sessions at work now that do not wait for a person: each holds one seat.
    private async Task<IReadOnlyCollection<string>> BusyRootsAsync(CancellationToken cancellationToken)
    {
        var working = _host.RuntimeService.ListSessionsAtWork();
        if (working.Count == 0) return [];
        var waiting = new HashSet<string>(await _host.ListSessionsWaitingForPersonAsync().ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sessionId in working)
        {
            if (!waiting.Contains(sessionId)) roots.Add(await RootOfAsync(sessionId, cancellationToken).ConfigureAwait(false));
        }

        return roots;
    }

    // The session at the root of the tree of a session, on this machine: the parent of a session an agent of another machine
    // started runs there, and holds its seat there; its children here take a seat of this machine.
    private async Task<string> RootOfAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_roots.TryGetValue(sessionId, out var known)) return known;
        var current = sessionId;
        for (var depth = 0; depth < MaxParentDepth; depth++)
        {
            if (!ReferenceEquals(current, sessionId) && _roots.TryGetValue(current, out var root))
            {
                current = root;
                break;
            }

            var session = await _host.RuntimeService.TryGetRecoverableSessionAsync(current, cancellationToken).ConfigureAwait(false);
            if (session?.ParentSessionId is not { Length: > 0 } parent
                || await _host.RuntimeService.TryGetRecoverableSessionAsync(parent, cancellationToken).ConfigureAwait(false) is null)
                break;
            current = parent;
        }

        if (_roots.Count >= MaxKnownRoots) _roots.Clear();
        _roots[sessionId] = current;
        return current;
    }

    // Sends a prompt once it has a seat of the machine: at once when one is free or its tree holds one already; else it answers
    // that it is queued with its place, kept before it says so (FleetHeldSends, reason queue: a restart takes it again), and is
    // sent when its turn comes. The budgets admitted it before: a send that waits for Continue anyway holds no seat.
    private async Task<FleetCommandResult> WithinSeatAsync(SessionViewDescriptor session, string? title, FleetHeldSend held, string kind)
    {
        var id = held.HoldId;
        if (_seatAdmitted.ContainsKey(id)) return SendSeated(held);
        if (_seatHolds.ContainsKey(id)) return QueuedResult(Seats.Snapshot().Queue.FirstOrDefault(item => item.Id == id)?.Position ?? 1);
        var root = await RootOfAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
        var ticket = await Seats.RequestAsync(new FleetSeatRequest(id, root, session.SessionId, title ?? session.Title, kind), CancellationToken.None).ConfigureAwait(false);
        if (ticket.IsAdmitted)
        {
            // A stop of the kill switch reaches it here too: a send taken again after a restart was not checked before.
            if (Policy?.Halt is { } halt)
            {
                Seats.Release(id);
                HeldSends.Remove(id);
                throw new FleetException(FleetNodeErrorCodes.FleetStopped, null, halt.Describe(_options.Name));
            }

            return SeatedSend(held);
        }
        if (!_seatHolds.TryAdd(id, 0)) return QueuedResult(ticket.Position);
        try
        {
            // Kept before the answer says it waits: a send accepted is never lost with the process.
            // Its place among the sends kept is when it joined the queue, not when a budget first held it: a restart keeps the order.
            HeldSends.Add(held with { Reason = FleetHeldSend.QueueReason, HeldAt = held.IsQueued ? held.HeldAt : _options.TimeProvider.GetUtcNow() });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or CodeAlta.Catalog.Secrets.SecretStoreUnavailableException or FleetHeldSendsFullException)
        {
            _seatHolds.TryRemove(id, out _);
            Seats.Cancel(id);
            LogInfo($"The prompt of the session {session.SessionId} that waits for a seat could not be kept: {exception.Message}");
            return new FleetCommandResult("capacity", Code: "capacity");
        }

        LogInfo($"The prompt of the session {session.SessionId} waits for a seat of the machine (place {ticket.Position}).");
        _ = SendOnceSeatedAsync(id, session.SessionId, ticket.Admitted, held);
        return QueuedResult(ticket.Position);
    }

    // A send that found its session at work, and that no caller waits for: queued in the session, to start after its turn.
    private async Task<FleetCommandResult> QueueInSessionAsync(FleetHeldSend held)
    {
        try
        {
            if (await _host.RuntimeService.TryGetRecoverableSessionAsync(held.SessionId).ConfigureAwait(false) is not { } session)
                return new FleetCommandResult("unknown_target", Code: "unknown_target");
            await _host.RuntimeService.QueuePromptAsync(session, held.Text, "queue", null, CancellationToken.None).ConfigureAwait(false);
            return new FleetCommandResult(FleetSeatStatuses.Queued, Code: "queued_in_session");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException or UnauthorizedAccessException)
        {
            LogWarn($"The prompt of the session {held.SessionId} found it at work and could not be queued in it: {exception.Message}");
            return new FleetCommandResult("busy", Code: "busy");
        }
    }

    private static FleetCommandResult QueuedResult(int position)
        => new(FleetSeatStatuses.Queued, Code: FleetSeatStatuses.Queued) { Position = Math.Max(1, position) };

    // A send that has its seat: sent, and its seat given back when the runtime did not start a run for it.
    private FleetCommandResult SeatedSend(FleetHeldSend held)
    {
        if (_seatAdmitted.Count >= MaxSeatAdmitted) _seatAdmitted.Clear();
        _seatAdmitted[held.HoldId] = 0;
        return SendSeated(held);
    }

    private FleetCommandResult SendSeated(FleetHeldSend held)
    {
        var result = SendHeld(held);
        if (result.Status is not ("accepted" or "replay")) Seats.Release(held.HoldId);
        return result;
    }

    private async Task SendOnceSeatedAsync(string id, string sessionId, Task admitted, FleetHeldSend held)
    {
        try
        {
            try
            {
                await admitted.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A node that stops leaves it kept, for its next start; a run taken out of the queue is never sent.
                if (Volatile.Read(ref _disposed) != 0) return;
                HeldSends.Remove(id);
                LogInfo($"The prompt of the session {sessionId} that waited for a seat was taken out of the queue: it was not sent.");
                return;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                Seats.Release(id);
                return;
            }

            // A stop of the kill switch that came while it waited reaches it: it is never sent.
            if (Policy?.Halt is { } halt)
            {
                HeldSends.Remove(id);
                Seats.Release(id);
                LogInfo($"The prompt of the session {sessionId} that waited for a seat was not sent: {halt.Describe(_options.Name)}");
                return;
            }

            // Forgotten once its run started (SendHeld), not before: a process that ends meanwhile takes it again.
            var result = SeatedSend(held);
            // Its session works meanwhile (another prompt of its tree took the seat first): nobody waits for this answer, so the
            // prompt goes into the queue of the session, as the caller does with a busy answer.
            if (result.Status == "busy") result = await QueueInSessionAsync(held).ConfigureAwait(false);
            LogInfo($"The prompt of the session {sessionId} was sent once it had a seat ({result.Status}).");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
        {
            Seats.Release(id);
            LogInfo($"The prompt of the session {sessionId} that waited for a seat was not sent: {exception.Message}");
        }
        finally
        {
            _seatHolds.TryRemove(id, out _);
        }
    }

    // A queued prompt of the work of the fleet whose turn would start: it waits for a seat as a send does. The prompts a session
    // queues for itself (the result of a child) and the work of the person at the machine never wait.
    private async Task<string?> WithinSeatQueuedAsync(QueuedRunAdmission queued, string? title, CancellationToken cancellationToken)
    {
        if (queued.Kind != "queue" || !Work.List().Any(item => string.Equals(item.SessionId, queued.SessionId, StringComparison.OrdinalIgnoreCase))) return null;
        var root = await RootOfAsync(queued.SessionId, cancellationToken).ConfigureAwait(false);
        var ticket = await Seats.RequestAsync(new FleetSeatRequest(queued.QueueItemId, root, queued.SessionId, title, "queue"), cancellationToken).ConfigureAwait(false);
        if (!ticket.IsAdmitted)
        {
            LogInfo($"The queued prompt {queued.QueueItemId} of the session {queued.SessionId} waits for a seat of the machine (place {ticket.Position}).");
            using var registration = cancellationToken.Register(() => Seats.Cancel(queued.QueueItemId));
            try
            {
                await ticket.Admitted.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return "The queued prompt was taken out of the queue of the machine.";
            }
        }

        if (Policy?.Halt is { } halt)
        {
            Seats.Release(queued.QueueItemId);
            return halt.Describe(_options.Name);
        }

        return null;
    }

    private void RegisterLimits(FleetNodeCaller caller, Access access)
    {
        On(caller, FleetNodeMethods.ControlLimitsGet, access, FleetNodeJsonContext.Default.FleetEmpty, FleetNodeJsonContext.Default.FleetMachineLimitsInfo,
            (_, _, _) => Task.FromResult(ReadLimits()), optionalParams: true);
        On(caller, FleetNodeMethods.ControlLimitsSet, access, FleetNodeJsonContext.Default.FleetMachineLimitsInfo, FleetNodeJsonContext.Default.FleetMachineLimitsInfo,
            SetLimitsAsync);
        On(caller, FleetNodeMethods.SeatsStart, access, FleetNodeJsonContext.Default.FleetSeatRef, FleetNodeJsonContext.Default.FleetSeatResult,
            (call, parameters, _) => Task.FromResult(StartSeat(call, parameters)));
    }

    private void RegisterSeats(FleetNodeCaller caller, Access view, Access drive)
    {
        On(caller, FleetNodeMethods.SeatsList, view, FleetNodeJsonContext.Default.FleetEmpty, FleetNodeJsonContext.Default.FleetSeatsSnapshot,
            async (call, _, cancellationToken) => VisibleSeats(call.Device, await ReadSeatsAsync(cancellationToken).ConfigureAwait(false)), optionalParams: true);
        On(caller, FleetNodeMethods.SeatsCancel, drive, FleetNodeJsonContext.Default.FleetSeatRef, FleetNodeJsonContext.Default.FleetSeatResult,
            (call, parameters, _) => Task.FromResult(CancelSeat(call, parameters)));
    }

    private FleetMachineLimitsInfo ReadLimits()
    {
        try
        {
            return new FleetMachineLimitsInfo(FleetMachineLimits.ReadSessions(_host.CatalogOptions.FleetRoot));
        }
        catch (InvalidDataException exception)
        {
            throw new FleetException(FleetErrorCodes.InternalError, null, exception.Message);
        }
    }

    private async Task<FleetMachineLimitsInfo> SetLimitsAsync(FleetNodeCall call, FleetMachineLimitsInfo parameters, CancellationToken cancellationToken)
    {
        if (parameters.Sessions is < 1 or > FleetMachineLimits.MaxSessions)
            throw new FleetException(FleetErrorCodes.InvalidParams, null, $"A machine runs between 1 and {FleetMachineLimits.MaxSessions} sessions at once, or has no limit.");
        try
        {
            FleetMachineLimits.WriteSessions(_host.CatalogOptions.FleetRoot, parameters.Sessions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FleetException(FleetErrorCodes.InternalError, FleetErrorHints.Retry, $"The limits could not be written: {exception.Message}");
        }

        LogInfo($"The limit of sessions at work of the machine changed ({(call.Caller.IsOwner ? "the owner" : call.Caller.DeviceId)}): {parameters.Sessions?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}.");
        // A higher limit, or none, starts the runs that wait at once, and the load says the new limit.
        _ = await ReadSeatsAsync(cancellationToken).ConfigureAwait(false);
        return new FleetMachineLimitsInfo(parameters.Sessions);
    }

    private FleetSeatResult StartSeat(FleetNodeCall call, FleetSeatRef parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.Id)) throw new FleetException(FleetErrorCodes.InvalidParams, null, "The request names the run that waits.");
        if (!Seats.StartNow(parameters.Id)) return new FleetSeatResult("not_found");
        LogInfo($"The run {parameters.Id} was started past the limit of the machine ({(call.Caller.IsOwner ? "the owner" : call.Caller.DeviceId)}).");
        return new FleetSeatResult("ok");
    }

    // What a device sees of the queue: all of it for the owner's devices; for a person who reaches only their own sessions here
    // (a member of a team), their own runs only, the place of each in the whole queue kept.
    private FleetSeatsSnapshot VisibleSeats(Identity.TrustedDevice? device, FleetSeatsSnapshot seats)
        => device is not { OwnSessionsOnly: true } ? seats
            : seats with { Queue = [.. seats.Queue.Where(item => item.SessionId is { } session && Principals.Owns(session, device.PersonId, device.OrganizationId))] };

    private FleetSeatResult CancelSeat(FleetNodeCall call, FleetSeatRef parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.Id)) throw new FleetException(FleetErrorCodes.InvalidParams, null, "The request names the run that waits.");
        // A person who reaches only their own sessions here takes only their own runs out: the others are not shown to exist.
        if (call.Device is { OwnSessionsOnly: true } && !VisibleSeats(call.Device, Seats.Snapshot()).Queue.Any(item => item.Id == parameters.Id))
            return new FleetSeatResult("not_found");
        if (!Seats.Cancel(parameters.Id)) return new FleetSeatResult("not_found");
        // A send taken out is forgotten before the answer says so: a restart right after does not send it.
        if (_seatHolds.ContainsKey(parameters.Id)) HeldSends.Remove(parameters.Id);
        LogInfo($"The run {parameters.Id} was taken out of the queue of the machine ({(call.Caller.IsOwner ? "the owner" : call.Caller.DeviceId)}).");
        return new FleetSeatResult("ok");
    }
}
