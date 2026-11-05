using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Moves;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Fleet;

/// <summary>Where a session goes and where it is, for the page: a paired machine, or this computer (null).</summary>
/// <param name="Preview">What the source says of the session.</param>
/// <param name="Target">What the target answers to a probe; null when the source refused first.</param>
/// <param name="TargetRefusal">Why the target cannot be asked (offline, forbidden), in words.</param>
/// <param name="ReceivesMoves">Whether this computer receives moved sessions.</param>
internal sealed record DesktopMovePreview(FleetMovePreview Preview, FleetMoveOfferResult? Target, string? TargetRefusal, bool ReceivesMoves);

/// <summary>
/// The moves of sessions between this computer and its paired machines (<c>Move to machine…</c>): a session of this
/// computer goes to a paired machine (this computer is the source and calls the machine's target methods); a session of a
/// paired machine comes here (the machine gives it with <c>moves.start</c> and calls the target's methods back on the
/// channel, which this computer serves only while the person lets it receive moved sessions), or goes to another paired
/// machine through this computer, which forwards the target's methods of the moves it started.
/// </summary>
/// <remarks>
/// A move a lost connection left unfinished goes on when its machine is online again: the moves this computer gave, and
/// those of the machine that this computer drove (its records name this computer, or a paired machine, as the target).
/// The moves this computer took are recovered by its own decisions when it starts.
/// </remarks>
internal sealed class DesktopMoves
{
    private const string SettingsFileName = "desktop-moves.json";
    private static readonly TimeSpan ScheduledMoveWait = TimeSpan.FromHours(2);
    private readonly CodeAltaHost _host;
    private readonly DesktopFleet _fleet;
    private readonly string _settingsPath;
    private readonly ConcurrentDictionary<string, string> _relays = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _driving = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<FleetNodeClient, object> _resumed = new();
    private readonly Lock _gate = new();
    private bool _receivesMoves;

    /// <summary>Creates the moves of this computer and its paired machines.</summary>
    /// <param name="host">The runtime of this computer.</param>
    /// <param name="fleet">Its paired machines.</param>
    /// <param name="machineName">The name of this computer, as the sessions it gives or takes say.</param>
    /// <param name="fleetRoot">The fleet folder of the state root, where the setting is kept.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public DesktopMoves(CodeAltaHost host, DesktopFleet fleet, string machineName, string fleetRoot)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        _host = host;
        _fleet = fleet;
        MachineName = machineName;
        _settingsPath = Path.Combine(fleetRoot, SettingsFileName);
        var moves = new SessionMoveService(host, machineName) { DeviceIdSource = () => fleet.DeviceId };
        Receiver = new FleetMoveReceiver(host, moves, FleetMoveReceiver.DefaultCloneRoot);
        Sender = new FleetMoveSender(host, moves);
        _receivesMoves = ReadSetting();
        fleet.Changed += OnFleetChanged;
    }

    /// <summary>Gets the name of this computer.</summary>
    public string MachineName { get; }

    /// <summary>Gets the target side of the moves of this computer.</summary>
    public FleetMoveReceiver Receiver { get; }

    /// <summary>Gets the source side of the moves of this computer.</summary>
    public FleetMoveSender Sender { get; }

    /// <summary>Gets whether this computer receives sessions moved from its paired machines (off until the person turns it on).</summary>
    public bool ReceivesMoves { get { lock (_gate) return _receivesMoves; } }

    /// <summary>Lets this computer receive moved sessions, or not.</summary>
    /// <param name="enabled">Whether it receives them.</param>
    /// <exception cref="IOException">The setting cannot be written.</exception>
    public void SetReceivesMoves(bool enabled)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var staging = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(new DesktopMoveSettings { ReceiveMoves = enabled }, DesktopMoveJsonContext.Default.DesktopMoveSettings));
            File.Move(staging, _settingsPath, overwrite: true);
            _receivesMoves = enabled;
        }

        if (enabled) OnFleetChanged();
    }

    /// <summary>
    /// Ends, by its own decisions, the moves this computer took and a stop left unfinished, and those it was giving and
    /// had offered to no machine yet.
    /// </summary>
    /// <param name="cancellationToken">Stops the recovery.</param>
    /// <returns>A task that completes once they are ended.</returns>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var result in await Sender.Moves.RecoverExportsAsync(cancellationToken).ConfigureAwait(false))
                Log($"Move {result.Transaction?.MoveId} of session {result.Transaction?.SessionId}: {result.Status}.");
            foreach (var result in await Receiver.RecoverAsync(cancellationToken).ConfigureAwait(false))
                Log($"Move {result.Transaction?.MoveId} of session {result.Transaction?.SessionId}: {result.Status}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"The unfinished moves could not be recovered: {exception.Message}");
        }
    }

    /// <summary>The methods this computer serves on the channel of a machine: those of a target, for the moves that machine gives.</summary>
    /// <param name="machine">The machine.</param>
    /// <returns>What registers them on the channel.</returns>
    public Action<FleetConnection> Serve(PairedMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return connection => FleetMoveRoutes.Register(connection, (moveId, via) => Route(machine, moveId, via));
    }

    /// <summary>Says what a move would meet: the source's preview and the target's answer to a probe.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="sourceMachineId">The machine the session runs on; null for this computer.</param>
    /// <param name="targetMachineId">The machine it would go to; null for this computer.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>The preview.</returns>
    /// <exception cref="KeyNotFoundException">No paired machine has one of the ids.</exception>
    /// <exception cref="ObjectDisposedException">The source machine is not online.</exception>
    /// <exception cref="FleetException">The source machine refused.</exception>
    public async Task<DesktopMovePreview> PreviewAsync(string sessionId, string? sourceMachineId, string? targetMachineId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var preview = sourceMachineId is null
            ? await Sender.PreviewAsync(sessionId, cancellationToken).ConfigureAwait(false)
            : (await Online(sourceMachineId).StartMoveAsync(new FleetMoveStartParams(sessionId, NewMoveId(), Probe: true), TimeSpan.FromMinutes(1), cancellationToken)
                .ConfigureAwait(false)).Preview ?? throw new FleetException(FleetErrorCodes.ProtocolError, null, "The machine said nothing of the session.");
        if (preview.Status != "ok" || preview.RepositoryKey is null) return new(preview, null, null, ReceivesMoves);
        var probe = new FleetMoveOffer(NewMoveId(), sessionId, preview.Title, null, preview.RepositoryKey, preview.RelativeFolder, preview.Branch, preview.RemoteUrl,
            preview.ProviderKey, 0, "", Probe: true);
        try
        {
            var target = targetMachineId is null
                ? await Receiver.OfferAsync(probe, "probe", cancellationToken).ConfigureAwait(false)
                : await Online(targetMachineId).AsMoveTarget().OfferAsync(probe, cancellationToken).ConfigureAwait(false);
            return new(preview, target, null, ReceivesMoves);
        }
        catch (FleetException exception) when (exception.Error.Code == FleetErrorCodes.Forbidden)
        {
            return new(preview, null, "forbidden", ReceivesMoves);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or FleetException or IOException or TimeoutException)
        {
            return new(preview, null, "unreachable", ReceivesMoves);
        }
    }

    /// <summary>Moves a session from where it runs to a machine, or to this computer.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="sourceMachineId">The machine it runs on; null for this computer.</param>
    /// <param name="targetMachineId">The machine it goes to; null for this computer.</param>
    /// <param name="clone">Lets the target clone the repository when it has no project of it.</param>
    /// <param name="cancellationToken">Stops waiting; the move stays recoverable.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="KeyNotFoundException">No paired machine has one of the ids.</exception>
    /// <exception cref="ObjectDisposedException">A machine is not online.</exception>
    /// <exception cref="FleetException">A machine refused.</exception>
    public async Task<FleetMoveResult> MoveAsync(string sessionId, string? sourceMachineId, string? targetMachineId, bool clone, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var moveId = NewMoveId();
        if (string.Equals(sourceMachineId, targetMachineId, StringComparison.Ordinal))
            return new FleetMoveResult("refused", "same_machine", "The session runs there already.", moveId, sessionId, 0, null, []);
        _driving[moveId] = 0;
        try
        {
            FleetMoveResult result;
            if (sourceMachineId is null)
            {
                var target = Host(targetMachineId!);
                result = await Sender.MoveAsync(sessionId, moveId, Online(target).AsMoveTarget(), target.Machine.Name, new SessionMoveRoute(target.Machine.DeviceId), clone,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (targetMachineId is null)
            {
                if (!ReceivesMoves) return new FleetMoveResult("refused", "receive_disabled", $"{MachineName} does not receive moved sessions.", moveId, sessionId, 0, null, []);
                result = await Online(sourceMachineId).StartMoveAsync(new FleetMoveStartParams(sessionId, moveId, MachineName, CloneRepository: clone),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var target = Host(targetMachineId);
                Online(target);
                _relays[moveId] = targetMachineId;
                result = await Online(sourceMachineId).StartMoveAsync(new FleetMoveStartParams(sessionId, moveId, target.Machine.Name, targetMachineId, CloneRepository: clone),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (SessionMoveState.IsFinal(result.Status) || result.Status == "refused") _relays.TryRemove(moveId, out _);
            foreach (var id in new[] { sourceMachineId, targetMachineId }.OfType<string>()) _fleet.Find(id)?.RequestRefresh();
            if (result.Status == SessionMoveState.Finished && targetMachineId is not null && _fleet.Find(targetMachineId) is { } listing)
                await listing.WaitListedAsync(sessionId, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _driving.TryRemove(moveId, out _);
        }
    }

    /// <summary>
    /// Moves a session of this computer to a machine once its turn ends: an agent that moves its own session. A move the
    /// machine refuses then is told to the session, which stays here.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="targetMachineId">The machine.</param>
    /// <param name="targetName">Its name.</param>
    /// <param name="clone">Lets the machine clone the repository when it has no project of it.</param>
    public void ScheduleMove(string sessionId, string targetMachineId, string targetName, bool clone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMachineId);
        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTimeOffset.UtcNow + ScheduledMoveWait;
                // The turn that asked for the move ends first, with its answer.
                do
                {
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    if (DateTimeOffset.UtcNow > deadline)
                    {
                        Log($"The move of session {sessionId} to {targetName} did not happen: the session stayed at work.");
                        return;
                    }
                }
                while ((await _host.Commands.ReadSessionsAtWorkAsync([sessionId]).ConfigureAwait(false)).Contains(sessionId)
                    || (await _host.ListSessionsWaitingForPersonAsync().ConfigureAwait(false)).Contains(sessionId, StringComparer.OrdinalIgnoreCase));

                var result = await MoveAsync(sessionId, null, targetMachineId, clone, CancellationToken.None).ConfigureAwait(false);
                Log($"Move {result.MoveId} of session {sessionId} to {targetName}, when its turn ended: {result.Status}{(result.Code is { } code ? $" ({code})" : "")}.");
                if (result.Status is "refused" or "aborted")
                {
                    await _host.RuntimeService.DeliverHostPromptAsync(sessionId, $"<codealta-context>The move of this session to {targetName} that you asked for did not happen "
                        + $"({result.Code ?? result.Status}): {result.Message ?? "the machine refused it."} The session stays on {MachineName}.</codealta-context>", "session-move")
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is FleetException or IOException or TimeoutException or ObjectDisposedException
                or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                Log($"The move of session {sessionId} to {targetName} did not happen: {exception.Message}");
            }
        });
    }

    private IFleetMoveTarget Route(PairedMachine from, string moveId, string? via)
    {
        if (via is null)
        {
            if (!ReceivesMoves)
                throw new FleetException(FleetErrorCodes.Forbidden, null, $"{MachineName} does not receive moved sessions: it is turned on in Settings > Fleet > Machines.");
            return Receiver.For(from.DeviceId);
        }

        // Only the moves this computer started (or goes on with) are forwarded, to the machine it named.
        if (!_relays.TryGetValue(moveId, out var target) || !string.Equals(target, via, StringComparison.Ordinal))
            throw new FleetException(FleetErrorCodes.Forbidden, null, "This computer forwards only the moves it started.");
        try
        {
            return Online(via).AsMoveTarget();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or KeyNotFoundException)
        {
            throw new FleetException(FleetNodeErrorCodes.Unavailable, FleetErrorHints.Retry, exception.Message);
        }
    }

    // A machine came online: the moves a lost connection left unfinished with it go on, once per connection.
    private void OnFleetChanged()
    {
        foreach (var host in _fleet.Hosts)
        {
            if (host.State != RemoteMachineState.Online || host.CurrentClient is not { } client) continue;
            lock (_gate)
            {
                if (_resumed.TryGetValue(client, out _)) continue;
                _resumed.Add(client, new object());
            }

            _ = Task.Run(() => ResumeAsync(host, client));
        }
    }

    private async Task ResumeAsync(RemoteSessionHost host, FleetNodeClient client)
    {
        try
        {
            // The moves this computer gave to that machine, by its device: never those of another machine of the same name.
            foreach (var result in await Sender.ResumeAsync(client.AsMoveTarget(), new SessionMoveRoute(host.Machine.DeviceId), CancellationToken.None).ConfigureAwait(false))
                Log($"Move {result.MoveId} of session {result.SessionId} to {host.Machine.Name}: {result.Status}.");

            if (!(host.Listing.Info?.Permissions ?? host.Machine.Permissions).Contains("move")) return;
            var moves = await client.ListMovesAsync().ConfigureAwait(false);
            foreach (var record in moves.Moves.Where(record => record.Role == SessionMoveRole.Source && !SessionMoveState.IsFinal(record.State)
                         && !_driving.ContainsKey(record.MoveId)))
            {
                if (!TryResumeRoute(record, _fleet.DeviceId, host.Machine.DeviceId, id => _fleet.Find(id) is not null, out var via)) continue;
                if (via is null && !ReceivesMoves) continue;
                if (via is not null) _relays[record.MoveId] = via;

                var result = await client.StartMoveAsync(new FleetMoveStartParams(record.SessionId, record.MoveId, record.ToMachine, via)).ConfigureAwait(false);
                if (SessionMoveState.IsFinal(result.Status)) _relays.TryRemove(record.MoveId, out _);
                Log($"Move {record.MoveId} of session {record.SessionId} from {host.Machine.Name}: {result.Status}.");
            }
        }
        catch (Exception exception) when (exception is FleetException or IOException or TimeoutException or ObjectDisposedException or InvalidOperationException)
        {
            Log($"The moves with {host.Machine.Name} could not go on: {exception.Message}");
        }
    }

    /// <summary>
    /// How this computer goes on with a move a paired machine gives and a lost connection left unfinished: as its target,
    /// or as the relay to the paired machine the record names, by device. Only a move the machine bound to this computer
    /// goes on, and only with the device it recorded: never with a machine chosen by its name.
    /// </summary>
    /// <param name="record">The source's record of the move.</param>
    /// <param name="ownDeviceId">The device of this computer.</param>
    /// <param name="sourceDeviceId">The device of the machine that gives the session.</param>
    /// <param name="isPaired">Whether a device is a paired machine of this computer.</param>
    /// <param name="via">The device this computer forwards the move to; null when it is the target.</param>
    /// <returns>Whether this computer goes on with the move.</returns>
    internal static bool TryResumeRoute(FleetMoveRecord record, string ownDeviceId, string sourceDeviceId, Func<string, bool> isPaired, out string? via)
    {
        via = null;
        if (!string.Equals(record.ToDeviceId, ownDeviceId, StringComparison.Ordinal)) return false;
        if (record.Via is null) return true;
        if (string.Equals(record.Via, sourceDeviceId, StringComparison.Ordinal) || string.Equals(record.Via, ownDeviceId, StringComparison.Ordinal) || !isPaired(record.Via))
            return false;
        via = record.Via;
        return true;
    }

    private RemoteSessionHost Host(string machineId) => _fleet.Find(machineId) ?? throw new KeyNotFoundException($"No paired machine has the id '{machineId}'.");

    private FleetNodeClient Online(string machineId) => Online(Host(machineId));

    private static FleetNodeClient Online(RemoteSessionHost host)
        => host.CurrentClient ?? throw new ObjectDisposedException(nameof(RemoteSessionHost), $"The machine {host.Machine.Name} is {RemoteSessionHost.Describe(host.State)}.");

    private static string NewMoveId() => "move-" + Guid.NewGuid().ToString("N");

    private bool ReadSetting()
    {
        try
        {
            return File.Exists(_settingsPath)
                && JsonSerializer.Deserialize(File.ReadAllBytes(_settingsPath), DesktopMoveJsonContext.Default.DesktopMoveSettings) is { ReceiveMoves: true };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void Log(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Fleet").Info(message);
    }
}

/// <summary>The setting of the moves of this computer, in the fleet folder of its state root.</summary>
internal sealed class DesktopMoveSettings
{
    /// <summary>Gets or sets whether this computer receives sessions moved from its paired machines.</summary>
    public bool ReceiveMoves { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DesktopMoveSettings))]
internal sealed partial class DesktopMoveJsonContext : JsonSerializerContext;
