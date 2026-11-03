using System.Buffers.Text;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using XenoAtom.Logging;

namespace CodeAlta.Fleet.Server;

/// <summary>How the control connection of a <see cref="FleetConnector"/> stands.</summary>
public enum FleetConnectorState
{
    /// <summary>Not started, or stopped.</summary>
    Stopped,

    /// <summary>Connecting, or waiting before the next attempt after a failure.</summary>
    Connecting,

    /// <summary>Connected and welcomed: presence, publications and the directory flow.</summary>
    Online,

    /// <summary>The server does not answer; the connector tries again, with backoff.</summary>
    Offline,

    /// <summary>The device waits for a controller device to approve it; the connector asks again now and then.</summary>
    PendingApproval,

    /// <summary>The server does not know the device, or its proof failed; the connector tries again slowly.</summary>
    Unauthenticated,

    /// <summary>The device was revoked: the connector stopped for good.</summary>
    Revoked,
}

/// <summary>What a <see cref="FleetConnector"/> connects with and publishes.</summary>
public sealed class FleetConnectorOptions
{
    /// <summary>Gets the address of the server.</summary>
    public required Uri ServerAddress { get; init; }

    /// <summary>Gets the key of the device; the caller keeps and disposes it.</summary>
    public required DeviceIdentity Identity { get; init; }

    /// <summary>Gets the file of the outbox of the link (<c>fleet/servers/&lt;link&gt;/outbox.json</c>).</summary>
    public required string OutboxPath { get; init; }

    /// <summary>Gets what the device says of itself in the hello: kind (<c>node</c>, <c>desktop</c>), version, system.</summary>
    public required FleetClientInfo Client { get; init; }

    /// <summary>Gets what there is to publish; null publishes nothing.</summary>
    public IFleetPublicationSource? Source { get; init; }

    /// <summary>
    /// Gets whether the sessions of a space are published to this server: only the spaces linked to it are (the default
    /// space when null). Private sessions are never published, whatever this says.
    /// </summary>
    public Func<string?, bool> PublishesSpace { get; init; } = static _ => false;

    /// <summary>Gets whether the connector follows the directory of the organization (a desktop does; a node does not).</summary>
    public bool WatchDirectory { get; init; }

    /// <summary>Gets the clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets the first wait after a failure; it doubles up to <see cref="MaximumBackoff"/>, with jitter.</summary>
    public TimeSpan MinimumBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets how often a device that waits for its approval asks again.</summary>
    public TimeSpan PendingApprovalRetry { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the longest wait between two attempts.</summary>
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets how long the connector gathers the changes of the source before it reads it.</summary>
    public TimeSpan PublishDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets how often the source is read even when it says nothing changed.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets what opens the transport to the control address; null opens a WebSocket.</summary>
    public Func<Uri, CancellationToken, Task<IFleetTransport>>? ConnectTransport { get; init; }
}

/// <summary>
/// The control connection of this instance to one CodeAlta Server (<c>doc/fleet/protocol.md</c>): the handshake with the
/// device proof, reconnection with backoff and jitter, presence in each heartbeat, the inventory of the machine and the
/// facts of its published sessions from an outbox (versions per session, acknowledgements, resumed from the cursors of the
/// welcome), and the directory of the organization for a desktop.
/// </summary>
/// <remarks>
/// <para>
/// Only the sessions of the spaces linked to the server are published (<see cref="FleetConnectorOptions.PublishesSpace"/>),
/// and never a Private one: no fact, not listed. A session published before that is made Private, or whose space is
/// unlinked, has its facts withdrawn (<c>facts.withdraw</c>). Facts carry the allow-list only; the sealed metadata
/// (titles) is not sent yet.
/// </para>
/// <para>One loop owns the connection and the outbox; the source and the callers only wake it.</para>
/// </remarks>
public sealed class FleetConnector : IAsyncDisposable
{
    /// <summary>The most facts in one batch, as the server takes them.</summary>
    public const int MaxFactsPerBatch = 500;

    private readonly FleetConnectorOptions _options;
    private readonly FleetPublicationOutbox _outbox;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Fleet.Server");
    private readonly Lock _gate = new();
    private Task _loop = Task.CompletedTask;
    private FleetDirectoryView _directory = FleetDirectoryView.Empty;
    private FleetConnectorState _state;
    private FleetError? _lastError;
    private FleetConnection? _connection;
    private int _started;
    private TimeSpan _heartbeat = TimeSpan.FromMilliseconds(FleetConnectionPolicy.DefaultHeartbeatMilliseconds);
    private int _activeSessions;

    /// <summary>Initializes a new instance of the <see cref="FleetConnector"/> class; <see cref="Start"/> connects.</summary>
    /// <param name="options">The server, the device and what to publish.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public FleetConnector(FleetConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ServerAddress);
        ArgumentNullException.ThrowIfNull(options.Identity);
        _options = options;
        _outbox = FleetPublicationOutbox.Load(options.OutboxPath);
    }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event Action<FleetConnector>? StateChanged;

    /// <summary>Raised when the directory changes.</summary>
    public event Action<FleetConnector>? DirectoryChanged;

    /// <summary>Raised after a batch of facts was answered, with the answer.</summary>
    public event Action<FleetConnector, FleetFactsPublishResult>? Published;

    /// <summary>Raised after a withdrawal of facts was answered, with the answer.</summary>
    public event Action<FleetConnector, FleetFactsWithdrawResult>? Withdrawn;

    /// <summary>Gets the address of the server.</summary>
    public Uri ServerAddress => _options.ServerAddress;

    /// <summary>Gets how the connection stands.</summary>
    public FleetConnectorState State { get { lock (_gate) return _state; } }

    /// <summary>Gets the last refusal or failure, or null.</summary>
    public FleetError? LastError { get { lock (_gate) return _lastError; } }

    /// <summary>Gets the directory of the organization, as the server last said it (empty without <see cref="FleetConnectorOptions.WatchDirectory"/>).</summary>
    public FleetDirectoryView Directory => Volatile.Read(ref _directory);

    /// <summary>Gets the cursors the outbox believes the server holds.</summary>
    internal FleetPublicationCursors Cursors => _outbox.Cursors;

    /// <summary>Starts the loop of the connection; a second call does nothing.</summary>
    /// <exception cref="ObjectDisposedException">The connector was disposed.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        if (_options.Source is { } source) source.Changed += Nudge;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Asks the connector to read its source again soon: something to publish may have changed.</summary>
    public void Nudge() => _wake.Writer.TryWrite(true);

    /// <summary>Stops the connection and waits for the loop to end.</summary>
    /// <returns>A task that completes once stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_options.Source is { } source) source.Changed -= Nudge;
        if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        SetState(FleetConnectorState.Stopped, LastError);
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            SetState(FleetConnectorState.Connecting, LastError);
            TimeSpan wait;
            try
            {
                await using var connection = await ConnectAsync(stop).ConfigureAwait(false);
                attempt = 0;
                var closing = await ServeAsync(connection, stop).ConfigureAwait(false);
                if (closing?.Code == FleetErrorCodes.Revoked)
                {
                    SetState(FleetConnectorState.Revoked, closing);
                    return;
                }

                SetState(FleetConnectorState.Offline, closing ?? new FleetError(FleetErrorCodes.ConnectionClosed, FleetErrorHints.Reconnect, "The connection closed."));
                wait = Backoff(attempt++);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (FleetHandshakeException exception)
            {
                var error = exception.Error;
                switch (error.Code)
                {
                    case FleetErrorCodes.Revoked:
                        SetState(FleetConnectorState.Revoked, error);
                        return;
                    case FleetErrorCodes.PendingApproval:
                        SetState(FleetConnectorState.PendingApproval, error);
                        wait = _options.PendingApprovalRetry;
                        break;
                    case FleetErrorCodes.Unauthenticated:
                        SetState(FleetConnectorState.Unauthenticated, error);
                        wait = _options.MaximumBackoff;
                        break;
                    case FleetErrorCodes.ServerBusy when error.RetryAfterSeconds is > 0:
                        SetState(FleetConnectorState.Offline, error);
                        wait = TimeSpan.FromSeconds(Math.Min(error.RetryAfterSeconds.Value, 3600));
                        break;
                    default:
                        SetState(FleetConnectorState.Offline, error);
                        wait = Backoff(attempt++);
                        break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                // A connection that fails, an answer that does not read, an outbox that cannot be written: the connector
                // stays alive and tries again, so that a fault of one attempt never ends the publications of the instance.
                if (exception is not (IOException or TimeoutException or System.Net.WebSockets.WebSocketException or HttpRequestException or FleetException)
                    && LogManager.IsInitialized)
                    _logger.Warn(exception, $"The control connection to {FleetServerAddress.Format(_options.ServerAddress)} failed");
                SetState(FleetConnectorState.Offline, new FleetError(FleetErrorCodes.TransportError, FleetErrorHints.Reconnect, exception.Message));
                wait = Backoff(attempt++);
            }

            try { await Task.Delay(wait, _options.TimeProvider, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<FleetConnection> ConnectAsync(CancellationToken stop)
    {
        var address = FleetServerAddress.ControlAddress(_options.ServerAddress);
        var transport = _options.ConnectTransport is { } connect
            ? await connect(address, stop).ConfigureAwait(false)
            : await FleetWebSocketTransport.ConnectAsync(address, null, stop).ConfigureAwait(false);
        FleetWelcome welcome;
        try
        {
            welcome = await FleetHandshake.ConnectAsync(transport, new FleetHandshakeClientOptions
            {
                Protocol = new FleetProtocolRange(1, 1),
                Client = _options.Client,
                ResumeCursors = JsonSerializer.SerializeToElement(_outbox.Cursors, FleetServerJsonContext.Default.FleetPublicationCursors),
            }, new Signer(_options.Identity), stop).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        FleetPublicationCursors? cursors = null;
        try { cursors = welcome.Cursors?.Deserialize(FleetServerJsonContext.Default.FleetPublicationCursors); }
        catch (JsonException) { /* A welcome without cursors of ours: the server holds nothing of this device. */ }
        _outbox.Resume(cursors);
        var connection = new FleetConnection(transport, FleetConnectionOptions.FromPolicy(welcome.Policy));
        _heartbeat = TimeSpan.FromMilliseconds(Math.Clamp(welcome.Policy.HeartbeatMilliseconds, 1_000, 120_000));
        return connection;
    }

    // One connection: events in, publications out, until it closes or the connector stops. Gives the error of a connection.closing.
    private async Task<FleetError?> ServeAsync(FleetConnection connection, CancellationToken stop)
    {
        FleetError? closing = null;
        connection.SetEventHandler((fleetEvent, _) =>
        {
            switch (fleetEvent.Event)
            {
                case FleetServerMethods.ConnectionClosing:
                    try { closing = fleetEvent.Payload?.Deserialize(FleetServerJsonContext.Default.FleetError); }
                    catch (JsonException) { closing = new FleetError(FleetErrorCodes.ConnectionClosed); }
                    break;
                case FleetServerMethods.DirectoryChanged:
                    try
                    {
                        if (fleetEvent.Payload?.Deserialize(FleetServerJsonContext.Default.FleetDirectoryChange) is { } change) ApplyDirectory(change);
                    }
                    catch (JsonException) { /* A change of a newer server this one does not read. */ }
                    break;
            }

            return ValueTask.CompletedTask;
        });
        connection.Start();
        Volatile.Write(ref _connection, connection);
        try
        {
            SetState(FleetConnectorState.Online, null);
            if (_options.WatchDirectory)
            {
                var snapshot = await connection.SendRequestAsync(FleetServerMethods.DirectoryWatch, null, null, stop).ConfigureAwait(false);
                if (snapshot?.Deserialize(FleetServerJsonContext.Default.FleetDirectorySnapshot) is { } directory) SetDirectory(FleetDirectoryView.From(directory));
            }

            var lastPresence = DateTimeOffset.MinValue;
            var lastRead = DateTimeOffset.MinValue;
            var dirty = true;
            while (!stop.IsCancellationRequested && connection.State == FleetConnectionState.Open)
            {
                var now = _options.TimeProvider.GetUtcNow();
                if (dirty || now - lastRead >= _options.PollInterval)
                {
                    var active = await PublishAsync(connection, stop).ConfigureAwait(false);
                    lastRead = _options.TimeProvider.GetUtcNow();
                    dirty = false;
                    if (now - lastPresence >= _heartbeat || active != _activeSessions)
                    {
                        await SendPresenceAsync(connection, active, stop).ConfigureAwait(false);
                        lastPresence = now;
                    }
                }
                else if (now - lastPresence >= _heartbeat)
                {
                    await SendPresenceAsync(connection, _activeSessions, stop).ConfigureAwait(false);
                    lastPresence = now;
                }

                var next = Min(_heartbeat - (now - lastPresence), _options.PollInterval - (now - lastRead));
                var woken = await WaitAsync(connection, next, stop).ConfigureAwait(false);
                if (woken)
                {
                    // Changes come in bursts (a turn writes many records): gathered for a moment, then read once.
                    try { await Task.Delay(_options.PublishDelay, _options.TimeProvider, stop).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    _wake.Reader.TryRead(out _);
                    dirty = true;
                }
            }
        }
        catch (Exception exception) when (exception is FleetConnectionClosedException or IOException or TimeoutException or System.Net.WebSockets.WebSocketException)
        {
            // The connection closed under a request: its completion says why.
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await CloseQuietlyAsync(connection).ConfigureAwait(false);
            throw;
        }
        finally
        {
            Volatile.Write(ref _connection, null);
        }

        if (stop.IsCancellationRequested) await CloseQuietlyAsync(connection).ConfigureAwait(false);
        var completed = await connection.Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return closing ?? (completed.IsClean ? null : new FleetError(completed.Code, FleetErrorHints.Reconnect, completed.Message));
    }

    // Reads the source and sends what the server lacks: the inventory when it changed, then the facts by batches. Gives how many published sessions run.
    private async Task<int> PublishAsync(FleetConnection connection, CancellationToken stop)
    {
        if (_options.Source is not { } source) return 0;
        var inventory = await source.ReadInventoryAsync(stop).ConfigureAwait(false);
        if (_outbox.Inventory(inventory) is { } publication)
        {
            var ack = await connection.SendRequestAsync(FleetServerMethods.InventoryPublish, publication.Publication, FleetServerJsonContext.Default.FleetInventoryPublish,
                FleetServerJsonContext.Default.FleetPublishAck, null, stop).ConfigureAwait(false);
            if (ack is not null) _outbox.InventoryAcknowledged(ack.Ack, publication.Publication.Sequence, publication.Hash);
        }

        var all = await source.ReadSessionsAsync(stop).ConfigureAwait(false);
        var sessions = all.Where(IsPublished).ToArray();
        // A session published before and not now (made Private, its space unlinked) has its facts taken back; a session
        // gone from the instance (deleted, moved away) keeps them, as statistics keep the facts of deleted sessions.
        var withdrawals = _outbox.Withdrawals(all.Where(session => !IsPublished(session)).Select(static session => session.SessionId), MaxFactsPerBatch);
        if (withdrawals.Count > 0)
        {
            var withdrawn = await connection.SendRequestAsync(FleetServerMethods.FactsWithdraw, new FleetFactsWithdraw(withdrawals),
                FleetServerJsonContext.Default.FleetFactsWithdraw, FleetServerJsonContext.Default.FleetFactsWithdrawResult, null, stop).ConfigureAwait(false);
            if (withdrawn is not null)
            {
                _outbox.Withdrawn(withdrawals);
                Withdrawn?.Invoke(this, withdrawn);
            }
        }

        _outbox.Retain([.. sessions.Select(static session => session.SessionId)]);
        while (!stop.IsCancellationRequested)
        {
            var pending = _outbox.Pending(sessions, MaxFactsPerBatch);
            if (pending.Count == 0) break;
            var batch = _outbox.Batch(pending);
            var result = await connection.SendRequestAsync(FleetServerMethods.FactsPublish, batch, FleetServerJsonContext.Default.FleetFactsPublish,
                FleetServerJsonContext.Default.FleetFactsPublishResult, null, stop).ConfigureAwait(false);
            if (result is null) break;
            _outbox.Acknowledged(result);
            if (result.Stale.Count > 0) _logger.Warn($"The server refused the facts of {result.Stale.Count} session(s) as stale: another machine owns them now.");
            Published?.Invoke(this, result);
        }

        var active = sessions.Count(static session => session.State == FleetSessionStates.Running);
        return active;
    }

    private bool IsPublished(FleetLocalSession session) => !session.IsPrivate && _options.PublishesSpace(session.SpaceId);

    private async Task SendPresenceAsync(FleetConnection connection, int active, CancellationToken stop)
    {
        _activeSessions = active;
        await connection.SendRequestAsync(FleetServerMethods.PresenceUpdate,
            JsonSerializer.SerializeToElement(new FleetPresenceUpdate(active > 0 ? "busy" : "active", active), FleetServerJsonContext.Default.FleetPresenceUpdate),
            null, stop).ConfigureAwait(false);
    }

    // Waits for a wake, the time given or the end of the connection; true when woken.
    private async Task<bool> WaitAsync(FleetConnection connection, TimeSpan delay, CancellationToken stop)
    {
        if (delay <= TimeSpan.Zero) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var wake = _wake.Reader.WaitToReadAsync(linked.Token).AsTask();
        var timer = Task.Delay(delay, _options.TimeProvider, linked.Token);
        var done = await Task.WhenAny(wake, timer, connection.Completion).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        stop.ThrowIfCancellationRequested();
        return done == wake && wake.IsCompletedSuccessfully;
    }

    private static async Task CloseQuietlyAsync(FleetConnection connection)
    {
        try { await connection.CloseAsync("stopping").ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException or OperationCanceledException) { }
    }

    private TimeSpan Backoff(int attempt)
    {
        var ceiling = Math.Min(_options.MaximumBackoff.TotalMilliseconds, _options.MinimumBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 16)));
        // Jitter over the upper half, so that machines a restart disconnected together do not come back together.
        return TimeSpan.FromMilliseconds(ceiling / 2 + Random.Shared.NextDouble() * ceiling / 2);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private void ApplyDirectory(FleetDirectoryChange change)
    {
        FleetDirectoryView current, next;
        do
        {
            current = Volatile.Read(ref _directory);
            next = current.Apply(change);
            if (ReferenceEquals(next, current)) return;
        }
        while (Interlocked.CompareExchange(ref _directory, next, current) != current);
        DirectoryChanged?.Invoke(this);
    }

    private void SetDirectory(FleetDirectoryView view)
    {
        Volatile.Write(ref _directory, view);
        DirectoryChanged?.Invoke(this);
    }

    private void SetState(FleetConnectorState state, FleetError? error)
    {
        lock (_gate)
        {
            if (_state == state && Equals(_lastError, error)) return;
            _state = state;
            _lastError = error;
        }

        if (state != FleetConnectorState.Online) Log(state, error);
        StateChanged?.Invoke(this);
    }

    private void Log(FleetConnectorState state, FleetError? error)
    {
        if (!LogManager.IsInitialized || state is FleetConnectorState.Connecting or FleetConnectorState.Stopped) return;
        _logger.Info($"The control connection to {FleetServerAddress.Format(_options.ServerAddress)} is {state}: {error?.Code ?? "-"} {error?.Message ?? ""}");
    }

    /// <summary>The device proof of the hello, signed with the device key.</summary>
    private sealed class Signer(DeviceIdentity identity) : IFleetHandshakeSigner
    {
        public string DeviceId => identity.DeviceId;

        public ValueTask<string> SignAsync(FleetProofContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(Base64Url.EncodeToString(identity.SignData(context.GetSignedBytes())));
    }
}

/// <summary>The directory of an organization as a connector last heard it: machines, published sessions, presence.</summary>
/// <param name="Machines">The machines, by device id.</param>
/// <param name="Sessions">The published sessions, by session id.</param>
/// <param name="Presence">The presence of the devices, by device id.</param>
public sealed record FleetDirectoryView(
    ImmutableDictionary<string, FleetDirectoryMachine> Machines,
    ImmutableDictionary<string, FleetDirectorySession> Sessions,
    ImmutableDictionary<string, FleetPresence> Presence)
{
    /// <summary>Gets an empty directory.</summary>
    public static FleetDirectoryView Empty { get; } = new(ImmutableDictionary.Create<string, FleetDirectoryMachine>(StringComparer.Ordinal),
        ImmutableDictionary.Create<string, FleetDirectorySession>(StringComparer.Ordinal), ImmutableDictionary.Create<string, FleetPresence>(StringComparer.Ordinal));

    /// <summary>Gets whether a device is online.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>Whether it holds a control connection, as the server last said.</returns>
    public bool IsOnline(string deviceId) => Presence.TryGetValue(deviceId, out var presence) && presence.Online;

    /// <summary>Makes the directory of a snapshot of <c>directory.watch</c>.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The directory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public static FleetDirectoryView From(FleetDirectorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var view = Empty;
        foreach (var machine in snapshot.Machines ?? []) view = view with { Machines = view.Machines.SetItem(machine.DeviceId, machine) };
        foreach (var session in snapshot.Sessions ?? []) view = view.Apply(new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Session, session.Fact.SessionId, Session: session));
        foreach (var presence in snapshot.Presence ?? []) view = view with { Presence = view.Presence.SetItem(presence.DeviceId, presence) };
        return view;
    }

    /// <summary>Applies a change; a session's facts replace those held only when newer (epoch, then version).</summary>
    /// <param name="change">The change.</param>
    /// <returns>The directory after it; this one when nothing changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
    public FleetDirectoryView Apply(FleetDirectoryChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        switch (change.Kind)
        {
            case FleetServerMethods.ChangeKinds.Machine when change.Machine is { } machine:
                return this with { Machines = Machines.SetItem(machine.DeviceId, machine) };
            case FleetServerMethods.ChangeKinds.Session when change.Session is { } session:
                if (Sessions.TryGetValue(session.Fact.SessionId, out var held)
                    && (session.Fact.Epoch < held.Fact.Epoch || session.Fact.Epoch == held.Fact.Epoch && session.Fact.Version <= held.Fact.Version))
                    return this;
                return this with { Sessions = Sessions.SetItem(session.Fact.SessionId, session) };
            case FleetServerMethods.ChangeKinds.Presence when change.Presence is { } presence:
                return this with { Presence = Presence.SetItem(presence.DeviceId, presence) };
            case FleetServerMethods.ChangeKinds.SessionRemoved when Sessions.ContainsKey(change.Id):
                return this with { Sessions = Sessions.Remove(change.Id) };
            default:
                return this;
        }
    }
}
