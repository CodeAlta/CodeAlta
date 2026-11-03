using System.Collections.Concurrent;
using System.Security.Authentication;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Desktop.Fleet;

/// <summary>The connection state of a paired machine, as its <see cref="RemoteSessionHost"/> sees it.</summary>
internal enum RemoteMachineState
{
    /// <summary>A connection is being made.</summary>
    Connecting,

    /// <summary>The machine answers: its sessions are read and driven.</summary>
    Online,

    /// <summary>The machine does not answer; the host tries again, waiting longer each time.</summary>
    Offline,

    /// <summary>The machine refused this device (it was forgotten or revoked there, or presented another key).</summary>
    Unauthorized,
}

/// <summary>How a <see cref="RemoteSessionHost"/> connects again and reads its list.</summary>
internal sealed record RemoteSessionHostOptions
{
    /// <summary>Gets the first wait before connecting again.</summary>
    public TimeSpan MinimumBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the longest wait before connecting again.</summary>
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how often the list of sessions is read while the machine is online.</summary>
    public TimeSpan ListInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the most sessions the list reads.</summary>
    public int ListLimit { get; init; } = 200;
}

/// <summary>What the host last read of its machine: what it is and its sessions, kept while the machine is offline.</summary>
/// <param name="Info">The machine, as it last described itself; null before the first connection.</param>
/// <param name="Sessions">Its sessions, the latest activity first.</param>
/// <param name="ListedAt">When the list was read; null before the first connection.</param>
internal sealed record RemoteListing(FleetNodeInfo? Info, IReadOnlyList<FleetSessionSummary> Sessions, DateTimeOffset? ListedAt)
{
    /// <summary>The listing of a machine never reached.</summary>
    public static readonly RemoteListing Empty = new(null, [], null);
}

/// <summary>
/// The sessions of one paired machine, through the client of its node API (<see cref="FleetNodeClient"/>): the host the
/// registry gives the sessions that machine lists. It owns its connection: one loop connects, reads the list while the
/// machine is online, and connects again with a growing wait when the channel closes. That loop is the only writer of the
/// state, the listing and the client; readers take the values it published.
/// </summary>
internal sealed partial class RemoteSessionHost : ISessionHost, IAsyncDisposable
{
    // What ReadInfoAsync read of the machine, on which connection.
    private sealed record FleetNodeInfoRead(FleetNodeClient Client, FleetNodeInfo Info);

    private readonly Func<CancellationToken, Task<FleetNodeClient>> _connect;
    private readonly string? _ownKey;
    private readonly string? _ownKeyPrefix;
    private readonly RemoteSessionHostOptions _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _refresh = new(0, 1);
    private readonly object _gate = new();
    private Task _loop = Task.CompletedTask;
    private FleetNodeClient? _client;
    private TaskCompletionSource<FleetNodeClient> _online = NewOnline();
    private RemoteMachineState _state = RemoteMachineState.Connecting;
    private string? _problem;
    private RemoteListing _listing = RemoteListing.Empty;
    private IReadOnlySet<string> _listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastSeen;
    private FleetNodeInfoRead? _readInfo;

    /// <summary>Creates the host of a paired machine; <see cref="Start"/> starts its connection.</summary>
    /// <param name="machine">The machine.</param>
    /// <param name="connect">Opens a channel to the node of the machine.</param>
    /// <param name="options">How it connects again; null for the defaults.</param>
    /// <param name="time">The clock; null for the system's.</param>
    /// <param name="localDeviceId">
    /// The device id of this computer: the node records the retry key of a command of this device as
    /// <c>fleet:&lt;device&gt;:&lt;key&gt;</c>, and the page knows its prompts by the key alone. Null keeps the records as they are.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="machine"/> or <paramref name="connect"/> is null.</exception>
    public RemoteSessionHost(PairedMachine machine, Func<CancellationToken, Task<FleetNodeClient>> connect, RemoteSessionHostOptions? options = null,
        TimeProvider? time = null, string? localDeviceId = null)
    {
        _ownKey = localDeviceId is { Length: > 0 } device ? $"fleet:{device}:" : null;
        _ownKeyPrefix = _ownKey is null ? null : $"\"clientRequestId\":\"{_ownKey}";
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(connect);
        Machine = machine;
        _connect = connect;
        _options = options ?? new RemoteSessionHostOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised, on a thread of the pool, when the state or the listing of the machine changed.</summary>
    public event Action<RemoteSessionHost>? Changed;

    /// <summary>Gets the machine.</summary>
    public PairedMachine Machine { get; }

    /// <inheritdoc />
    public string? MachineId => Machine.DeviceId;

    /// <summary>Gets the connection state.</summary>
    public RemoteMachineState State { get { lock (_gate) return _state; } }

    /// <summary>Gets why the last connection failed, in words; null while online or before a failure.</summary>
    public string? Problem { get { lock (_gate) return _problem; } }

    /// <summary>Gets when the machine last answered; null when it never did since the host started.</summary>
    public DateTimeOffset? LastSeen { get { lock (_gate) return _lastSeen; } }

    /// <summary>Gets the client of the machine while it is online; null otherwise.</summary>
    internal FleetNodeClient? CurrentClient { get { lock (_gate) return _client; } }

    /// <summary>
    /// Gets whether this computer paired with the machine; false for a machine of an organization it reaches through the
    /// relay of a server only, whose key the signed device list gives at each connection.
    /// </summary>
    public bool IsPaired => !Machine.Fingerprint.IsEmpty;

    /// <summary>Gets where the channel of the machine goes while it is online: straight to it, or through a server; null otherwise.</summary>
    public CodeAlta.Fleet.Server.FleetChannelPath? ChannelPath => CurrentClient?.Path;

    /// <summary>Gets what the host last read of the machine.</summary>
    public RemoteListing Listing => Volatile.Read(ref _listing);

    /// <inheritdoc />
    public bool Lists(string sessionId) => sessionId is not null && Volatile.Read(ref _listed).Contains(sessionId);

    /// <inheritdoc />
    /// <remarks>What the machine lists is its word, not a record of this process: it owns nothing here.</remarks>
    public bool Owns(string sessionId) => false;

    /// <summary>Starts the connection loop.</summary>
    public void Start() => _loop = Task.Run(RunAsync);

    /// <summary>Asks for the list again until it holds a session, or the wait ends.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Whether the machine lists the session.</returns>
    public async Task<bool> WaitListedAsync(string sessionId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (!Lists(sessionId))
        {
            if (_time.GetUtcNow() > deadline) return false;
            RequestRefresh();
            await Task.Delay(TimeSpan.FromMilliseconds(100), _time, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Asks for the list again now, instead of at the next interval.</summary>
    public void RequestRefresh()
    {
        try { _refresh.Release(); }
        catch (SemaphoreFullException) { /* A refresh is asked already. */ }
        catch (ObjectDisposedException) { /* The host is closing. */ }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    /// <summary>Waits until the machine is online, and gives its client.</summary>
    internal async Task<FleetNodeClient> WaitOnlineAsync(CancellationToken cancellationToken)
    {
        Task<FleetNodeClient> online;
        lock (_gate) online = _online.Task;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        return await online.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    // The client of the machine while it is online. An offline machine is reported as a host that is closing is: a
    // request to it fails at once, retryable, and never waits for the machine.
    private FleetNodeClient RequireClient()
    {
        lock (_gate)
            return _client ?? throw new ObjectDisposedException(nameof(RemoteSessionHost), $"The machine {Machine.Name} is {Describe(_state)}.");
    }

    internal static string Describe(RemoteMachineState state) => state switch
    {
        RemoteMachineState.Online => "online",
        RemoteMachineState.Connecting => "connecting",
        RemoteMachineState.Unauthorized => "unauthorized",
        _ => "offline",
    };

    private async Task RunAsync()
    {
        var token = _stop.Token;
        var backoff = _options.MinimumBackoff;
        while (!token.IsCancellationRequested)
        {
            Publish(RemoteMachineState.Connecting, null, null);
            FleetNodeClient? client = null;
            var next = RemoteMachineState.Offline;
            string? problem = null;
            try
            {
                client = await _connect(token).ConfigureAwait(false);
                var info = await client.GetInfoAsync(token).ConfigureAwait(false);
                var sessions = await client.ListSessionsAsync(new FleetSessionListParams(_options.ListLimit), token).ConfigureAwait(false);
                SetListing(new RemoteListing(info, sessions.Sessions, _time.GetUtcNow()));
                Publish(RemoteMachineState.Online, null, client);
                backoff = _options.MinimumBackoff;
                await FollowAsync(client, info, token).ConfigureAwait(false);
                problem = "The connection closed.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (DeviceAuthenticationException exception)
            {
                next = RemoteMachineState.Unauthorized;
                problem = exception.Message;
            }
            catch (FleetException exception) when (exception.Error.Code is FleetErrorCodes.Unauthenticated or FleetErrorCodes.Revoked or FleetErrorCodes.Forbidden)
            {
                next = RemoteMachineState.Unauthorized;
                problem = exception.Message;
            }
            catch (Exception exception) when (exception is IOException or FleetException or TimeoutException or AuthenticationException
                or System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                problem = exception.Message;
            }
            finally
            {
                if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            }

            if (token.IsCancellationRequested) break;
            Publish(next, problem, null);
            // A machine that refused this device is asked again rarely: it takes a person to change that.
            var wait = next == RemoteMachineState.Unauthorized ? _options.MaximumBackoff : backoff;
            try { await Task.Delay(Jitter(wait), _time, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromTicks(Math.Min(_options.MaximumBackoff.Ticks, backoff.Ticks * 2));
        }

        Publish(RemoteMachineState.Offline, null, null);
    }

    // While the channel is open: the list again at each interval, or when a refresh is asked.
    private async Task FollowAsync(FleetNodeClient client, FleetNodeInfo info, CancellationToken token)
    {
        while (!client.Completion.IsCompleted)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            var tick = Task.Delay(_options.ListInterval, _time, wait.Token);
            var asked = _refresh.WaitAsync(wait.Token);
            await Task.WhenAny(tick, asked, client.Completion).ConfigureAwait(false);
            await wait.CancelAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (client.Completion.IsCompleted) return;
            var sessions = await client.ListSessionsAsync(new FleetSessionListParams(_options.ListLimit), token).ConfigureAwait(false);
            // The machine as it was read last: ReadInfoAsync may have read it again since the connection.
            var read = Volatile.Read(ref _readInfo);
            SetListing(new RemoteListing(read is not null && read.Client == client ? read.Info : info, sessions.Sessions, _time.GetUtcNow()));
        }
    }

    /// <summary>
    /// Asks the machine what it is now (its projects, its ready providers), for a new session on it: what the connection
    /// read may be old. The listing keeps what it says.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The machine as it describes itself.</returns>
    /// <exception cref="ObjectDisposedException">The machine is not online.</exception>
    /// <exception cref="FleetException">The machine refused the call.</exception>
    public async Task<FleetNodeInfo> ReadInfoAsync(CancellationToken cancellationToken)
    {
        var client = RequireClient();
        var info = await client.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        // The loop, the one writer of the listing, takes it at its next read of the list, while that connection lasts.
        Volatile.Write(ref _readInfo, new FleetNodeInfoRead(client, info));
        RequestRefresh();
        return info;
    }

    private TimeSpan Jitter(TimeSpan wait) => wait + TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Math.Min(1000, wait.TotalMilliseconds / 4 + 1)));

    private void SetListing(RemoteListing listing)
    {
        var previous = Volatile.Read(ref _listing);
        Volatile.Write(ref _listing, listing);
        Volatile.Write(ref _listed, listing.Sessions.Select(static session => session.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
        lock (_gate) _lastSeen = listing.ListedAt;
        if (!SameList(previous, listing)) Changed?.Invoke(this);
    }

    private static bool SameList(RemoteListing previous, RemoteListing next)
        => previous.Info?.Name == next.Info?.Name && previous.Sessions.Count == next.Sessions.Count
            && previous.Sessions.Zip(next.Sessions).All(static pair => pair.First == pair.Second);

    private void Publish(RemoteMachineState state, string? problem, FleetNodeClient? client)
    {
        bool changed;
        lock (_gate)
        {
            changed = _state != state || _problem != problem;
            _state = state;
            _problem = problem;
            _client = client;
            if (client is not null)
            {
                _online.TrySetResult(client);
            }
            else if (_online.Task.IsCompleted)
            {
                _online = NewOnline();
            }
        }

        if (changed) Changed?.Invoke(this);
    }

    private static TaskCompletionSource<FleetNodeClient> NewOnline() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The operations this host admitted, by the operation id of their local receipt: an abort finds the operation of the
    // node through it. Bounded: the oldest go first.
    private readonly ConcurrentDictionary<Guid, RemoteOperation> _operations = new();
    private readonly ConcurrentQueue<Guid> _operationOrder = new();

    /// <inheritdoc />
    public bool Admitted(Guid operationId) => _operations.ContainsKey(operationId);
}
