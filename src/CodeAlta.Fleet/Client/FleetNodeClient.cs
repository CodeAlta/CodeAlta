using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Client;

/// <summary>
/// The client of the node API of one node, over a session channel: typed calls for its sessions, and the live display of
/// the sessions it watches. Side-effecting calls take a retry key: the same key again is the same command (a retry after
/// a lost reply gets the first receipt), so a caller that retries passes the key it used.
/// </summary>
public sealed class FleetNodeClient : IAsyncDisposable
{
    private static readonly TimeSpan AddressTimeout = TimeSpan.FromSeconds(5);
    private readonly FleetChannel _channel;
    private readonly ConcurrentDictionary<string, FleetSessionWatch> _watches = new(StringComparer.Ordinal);

    private FleetNodeClient(FleetChannel channel)
    {
        _channel = channel;
        channel.Connection.SetEventHandler(OnEventAsync);
        channel.Connection.Start();
    }

    /// <summary>Gets the device id of the node.</summary>
    public string NodeDeviceId => _channel.RemoteDeviceId;

    /// <summary>Gets a task that completes, never faulted, with how the channel closed.</summary>
    public Task<FleetConnectionCloseInfo> Completion => _channel.Connection.Completion;

    /// <summary>Connects to a paired machine, trying its addresses in order.</summary>
    /// <param name="machine">The machine.</param>
    /// <param name="identity">The key of this device.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The connected client.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="IOException">No address of the machine answered.</exception>
    /// <exception cref="DeviceAuthenticationException">The machine presented another key, or refused this device (not paired, revoked).</exception>
    /// <exception cref="FleetHandshakeException">The machine refused the handshake.</exception>
    public static async Task<FleetNodeClient> ConnectAsync(PairedMachine machine, DeviceIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(identity);
        var (stream, _) = await FleetDirectConnector.ConnectAsync(machine.Addresses, AddressTimeout, cancellationToken).ConfigureAwait(false);
        return await ConnectAsync(stream, machine.Fingerprint, identity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Connects to a node over a stream that is already open (a TCP connection, later a relayed stream).</summary>
    /// <param name="stream">The stream to the node; the client owns it.</param>
    /// <param name="nodeFingerprint">The pinned key of the node.</param>
    /// <param name="identity">The key of this device.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The connected client.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DeviceAuthenticationException">The node presented another key, or refused this device.</exception>
    /// <exception cref="FleetHandshakeException">The node refused the handshake.</exception>
    public static async Task<FleetNodeClient> ConnectAsync(Stream stream, DeviceFingerprint nodeFingerprint, DeviceIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(identity);
        var channel = await FleetChannelStack.ConnectAsync(stream, new FleetChannelClientOptions { Identity = identity, NodeFingerprint = nodeFingerprint },
            cancellationToken).ConfigureAwait(false);
        return new FleetNodeClient(channel);
    }

    /// <summary>
    /// Connects to the node this process runs beside, with the node's own key: the owner's local control, which only
    /// creates pairing offers (<c>altanode pair</c>).
    /// </summary>
    /// <param name="addresses">The addresses the node listens on.</param>
    /// <param name="nodeIdentity">The key of the node, read from its profile.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The connected client.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="IOException">The node did not answer.</exception>
    public static async Task<FleetNodeClient> ConnectAsOwnerAsync(IReadOnlyList<string> addresses, DeviceIdentity nodeIdentity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(nodeIdentity);
        var (stream, _) = await FleetDirectConnector.ConnectAsync(addresses, AddressTimeout, cancellationToken).ConfigureAwait(false);
        var channel = await FleetChannelStack.ConnectAsync(stream, new FleetChannelClientOptions
        {
            Identity = nodeIdentity,
            NodeFingerprint = nodeIdentity.Fingerprint,
            Client = new FleetClientInfo(FleetChannelProtocol.ControlClientKind, "1", FleetChannelProtocol.OperatingSystemName),
        }, cancellationToken).ConfigureAwait(false);
        return new FleetNodeClient(channel);
    }

    /// <summary>
    /// Pairs this device with a node from its pairing code: connects to the node it names (pinned to its key), redeems the
    /// secret, and records the machine in <paramref name="store"/>.
    /// </summary>
    /// <param name="offer">The pairing code of the node.</param>
    /// <param name="identity">The key of this device.</param>
    /// <param name="name">The name this device gives itself on the node.</param>
    /// <param name="store">Where the machine is recorded; null to record it nowhere.</param>
    /// <param name="cancellationToken">Cancels the pairing.</param>
    /// <returns>The paired machine.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="IOException">No address of the node answered.</exception>
    /// <exception cref="DeviceAuthenticationException">The node presented another key than its code names, or refused the connection.</exception>
    /// <exception cref="FleetException">The node refused the secret (<see cref="FleetNodeErrorCodes.PairingRefused"/>).</exception>
    public static async Task<PairedMachine> PairAsync(PairingPayload offer, DeviceIdentity identity, string name, PairedMachineStore? store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(name);
        var (stream, address) = await FleetDirectConnector.ConnectAsync(offer.Addresses, AddressTimeout, cancellationToken).ConfigureAwait(false);
        await using var client = await ConnectAsync(stream, offer.Fingerprint, identity, cancellationToken).ConfigureAwait(false);
        var redeemed = await client.CallAsync(FleetNodeMethods.PairRedeem, new FleetPairRedeemParams(Base64Url.EncodeToString(offer.Secret), name),
            FleetNodeJsonContext.Default.FleetPairRedeemParams, FleetNodeJsonContext.Default.FleetPairRedeemResult, null, cancellationToken).ConfigureAwait(false);
        // The address that answered comes first next time.
        var addresses = new List<string> { address };
        addresses.AddRange(offer.Addresses.Where(other => !string.Equals(other, address, StringComparison.OrdinalIgnoreCase)));
        var machine = new PairedMachine(offer.DeviceName, offer.DeviceId, offer.Fingerprint, addresses, DateTimeOffset.UtcNow, redeemed.Permissions);
        store?.Save(machine);
        await client.CloseAsync(cancellationToken).ConfigureAwait(false);
        return machine;
    }

    /// <summary>Redeems a pairing secret on this channel (see <see cref="PairAsync"/>, which also connects and records the machine).</summary>
    /// <param name="secret">The secret of the offer, base64url.</param>
    /// <param name="name">The name this device gives itself.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The node and what this device may do on it.</returns>
    /// <exception cref="FleetException">The node refused the secret.</exception>
    public Task<FleetPairRedeemResult> RedeemAsync(string secret, string name, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.PairRedeem, new FleetPairRedeemParams(secret, name), FleetNodeJsonContext.Default.FleetPairRedeemParams,
            FleetNodeJsonContext.Default.FleetPairRedeemResult, null, cancellationToken);

    /// <summary>Reads what the node is.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Its name, system, version, spaces and projects.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetNodeInfo> GetInfoAsync(CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.NodeInfo, new FleetEmpty(), FleetNodeJsonContext.Default.FleetEmpty, FleetNodeJsonContext.Default.FleetNodeInfo, null, cancellationToken);

    /// <summary>Lists the sessions of the node.</summary>
    /// <param name="parameters">The limit and the filters; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The sessions, the latest activity first.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetSessionListResult> ListSessionsAsync(FleetSessionListParams? parameters = null, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsList, parameters ?? new FleetSessionListParams(), FleetNodeJsonContext.Default.FleetSessionListParams,
            FleetNodeJsonContext.Default.FleetSessionListResult, null, cancellationToken);

    /// <summary>Reads the runtime identities of a session, which steer and stop are bound to.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetSessionState> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsState, new FleetSessionRef(sessionId), FleetNodeJsonContext.Default.FleetSessionRef,
            FleetNodeJsonContext.Default.FleetSessionState, null, cancellationToken);

    /// <summary>Reads a window of the timeline of a session.</summary>
    /// <param name="parameters">The session, the cursor and the direction.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The window and the cursor of the next one.</returns>
    /// <exception cref="FleetException">The node refused the call (<see cref="FleetNodeErrorCodes.SessionNotFound"/>, a stale cursor).</exception>
    public Task<FleetHistoryResult> ReadHistoryAsync(FleetHistoryParams parameters, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsHistory, parameters, FleetNodeJsonContext.Default.FleetHistoryParams,
            FleetNodeJsonContext.Default.FleetHistoryResult, null, cancellationToken);

    /// <summary>Watches the live display of a session until the watch is disposed, the node ends it, or the channel closes.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The watch, whose <see cref="FleetSessionWatch.Updates"/> gives the replacements.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public async Task<FleetSessionWatch> WatchAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var watch = new FleetSessionWatch(Guid.NewGuid().ToString("N"), sessionId, this);
        _watches[watch.WatchId] = watch;
        try
        {
            await CallAsync(FleetNodeMethods.SessionsWatch, new FleetWatchParams(sessionId, watch.WatchId), FleetNodeJsonContext.Default.FleetWatchParams,
                FleetNodeJsonContext.Default.FleetWatchResult, null, cancellationToken).ConfigureAwait(false);
            return watch;
        }
        catch
        {
            _watches.TryRemove(watch.WatchId, out _);
            watch.Complete();
            throw;
        }
    }

    internal async Task UnwatchAsync(FleetSessionWatch watch)
    {
        if (!_watches.TryRemove(watch.WatchId, out _)) return;
        watch.Complete();
        if (_channel.Connection.State != FleetConnectionState.Open) return;
        try
        {
            await CallAsync(FleetNodeMethods.SessionsUnwatch, new FleetUnwatchParams(watch.WatchId), FleetNodeJsonContext.Default.FleetUnwatchParams,
                FleetNodeJsonContext.Default.FleetEmpty, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetException or TimeoutException)
        {
            // The node ends the watch with the channel anyway.
        }
    }

    /// <summary>Creates a session on the node, with its first prompt when one is given.</summary>
    /// <param name="parameters">The project, space, title, prompt and model.</param>
    /// <param name="retryKey">The key of the creation: the same key again returns the same session; null for a new key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new session and the admission of its prompt.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetCreateSessionResult> CreateSessionAsync(FleetCreateSessionParams parameters, string? retryKey = null, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsCreate, parameters, FleetNodeJsonContext.Default.FleetCreateSessionParams,
            FleetNodeJsonContext.Default.FleetCreateSessionResult, retryKey ?? NewRetryKey(), cancellationToken);

    /// <summary>Sends a prompt that starts a turn of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="text">The prompt.</param>
    /// <param name="selection">The model of the turn; null keeps the session's.</param>
    /// <param name="retryKey">The retry key of the command; null for a new key.</param>
    /// <param name="cancellationToken">Cancels the call; the node may still have admitted the send.</param>
    /// <returns>The admission.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetCommandResult> SendAsync(string sessionId, string text, FleetModelSelection? selection = null, string? retryKey = null,
        CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsSend, new FleetSendParams(sessionId, text, selection), FleetNodeJsonContext.Default.FleetSendParams,
            FleetNodeJsonContext.Default.FleetCommandResult, retryKey ?? NewRetryKey(), cancellationToken);

    /// <summary>Queues a prompt for after the running turn of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="text">The prompt.</param>
    /// <param name="retryKey">The key of the call; null for a new key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The queued prompt.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetQueueResult> QueueAsync(string sessionId, string text, string? retryKey = null, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsQueue, new FleetQueueParams(sessionId, text), FleetNodeJsonContext.Default.FleetQueueParams,
            FleetNodeJsonContext.Default.FleetQueueResult, retryKey ?? NewRetryKey(), cancellationToken);

    /// <summary>Steers the exact running turn of a session.</summary>
    /// <param name="parameters">The session, the text and the identities of the run (<see cref="GetSessionStateAsync"/>).</param>
    /// <param name="retryKey">The retry key of the command; null for a new key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The admission.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetCommandResult> SteerAsync(FleetSteerParams parameters, string? retryKey = null, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsSteer, parameters, FleetNodeJsonContext.Default.FleetSteerParams,
            FleetNodeJsonContext.Default.FleetCommandResult, retryKey ?? NewRetryKey(), cancellationToken);

    /// <summary>Stops a send, or the exact run of a session.</summary>
    /// <param name="parameters">The session and the send or the run.</param>
    /// <param name="retryKey">The retry key of the command; null for a new key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The admission.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetCommandResult> AbortAsync(FleetAbortParams parameters, string? retryKey = null, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsAbort, parameters, FleetNodeJsonContext.Default.FleetAbortParams,
            FleetNodeJsonContext.Default.FleetCommandResult, retryKey ?? NewRetryKey(), cancellationToken);

    /// <summary>Lists the permission requests of a session that wait for a person.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The requests.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetPermissionList> ListPermissionsAsync(string sessionId, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsPermissionsList, new FleetSessionRef(sessionId), FleetNodeJsonContext.Default.FleetSessionRef,
            FleetNodeJsonContext.Default.FleetPermissionList, null, cancellationToken);

    /// <summary>Answers a permission request.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="requestId">The request.</param>
    /// <param name="decision"><c>allow_once</c>, <c>allow_always</c>, <c>deny</c> or <c>cancel</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether the request was resolved.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetAnswerResult> AnswerPermissionAsync(string sessionId, Guid requestId, string decision, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsPermissionsAnswer, new FleetPermissionAnswerParams(sessionId, requestId, decision),
            FleetNodeJsonContext.Default.FleetPermissionAnswerParams, FleetNodeJsonContext.Default.FleetAnswerResult, null, cancellationToken);

    /// <summary>Reads the question of a session that waits for a person.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The question, if any.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetAskList> ListAsksAsync(string sessionId, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsAsksList, new FleetSessionRef(sessionId), FleetNodeJsonContext.Default.FleetSessionRef,
            FleetNodeJsonContext.Default.FleetAskList, null, cancellationToken);

    /// <summary>Answers or cancels the question of a session.</summary>
    /// <param name="parameters">The session, the question and the answers.</param>
    /// <param name="retryKey">The key of the answer; null for a new key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What became of the answer.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetAnswerResult> AnswerAskAsync(FleetAskAnswerParams parameters, string? retryKey = null, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.SessionsAsksAnswer, parameters, FleetNodeJsonContext.Default.FleetAskAnswerParams,
            FleetNodeJsonContext.Default.FleetAnswerResult, retryKey ?? NewRetryKey(), cancellationToken);

    /// <summary>The owner's local control: creates a pairing offer on the node.</summary>
    /// <param name="lifetime">How long the offer stays open; null for 10 minutes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The offer.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetPairingOffer> CreatePairingOfferAsync(TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
        => CallAsync(FleetNodeMethods.ControlPairingCreate, new FleetPairingCreateParams(lifetime is { } value ? (int)value.TotalSeconds : null),
            FleetNodeJsonContext.Default.FleetPairingCreateParams, FleetNodeJsonContext.Default.FleetPairingOffer, null, cancellationToken);

    /// <summary>The owner's local control: waits until the open offer is redeemed, expires or is replaced.</summary>
    /// <param name="timeout">How long to wait at most.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    /// <exception cref="TimeoutException">Nothing happened within <paramref name="timeout"/>.</exception>
    public async Task<FleetPairingOutcome> WaitPairingAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var result = await _channel.Connection.SendRequestAsync(FleetNodeMethods.ControlPairingWait, null, new FleetRequestOptions { Timeout = timeout }, cancellationToken)
            .ConfigureAwait(false);
        return Read(result, FleetNodeJsonContext.Default.FleetPairingOutcome);
    }

    /// <summary>Closes the channel.</summary>
    /// <param name="cancellationToken">Cancels the wait for the close.</param>
    /// <returns>A task that completes once the channel is closed.</returns>
    public Task CloseAsync(CancellationToken cancellationToken = default) => _channel.Connection.CloseAsync(null, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var watch in _watches.Values) watch.Complete();
        _watches.Clear();
        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    private static string NewRetryKey() => Guid.NewGuid().ToString("N");

    private async Task<TResult> CallAsync<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> parametersType, JsonTypeInfo<TResult> resultType,
        string? retryKey, CancellationToken cancellationToken)
    {
        var result = await _channel.Connection.SendRequestAsync(method, JsonSerializer.SerializeToElement(parameters, parametersType),
            retryKey is null ? null : new FleetRequestOptions { IdempotencyKey = retryKey }, cancellationToken).ConfigureAwait(false);
        return Read(result, resultType);
    }

    private static TResult Read<TResult>(JsonElement? result, JsonTypeInfo<TResult> resultType)
        => (result is { } element ? element.Deserialize(resultType) : default)
           ?? throw new FleetException(FleetErrorCodes.ProtocolError, null, "The node answered without a result.");

    private ValueTask OnEventAsync(FleetEvent fleetEvent, CancellationToken cancellationToken)
    {
        try
        {
            switch (fleetEvent.Event)
            {
                case FleetNodeMethods.SessionDisplayEvent when fleetEvent.Payload?.Deserialize(FleetNodeJsonContext.Default.FleetSessionDisplay) is { } display:
                    if (_watches.TryGetValue(display.WatchId, out var watch)) watch.Post(display);
                    break;
                case FleetNodeMethods.SessionWatchEndedEvent when fleetEvent.Payload?.Deserialize(FleetNodeJsonContext.Default.FleetWatchEnded) is { } ended:
                    if (_watches.TryRemove(ended.WatchId, out var finished)) finished.Complete(ended.Reason);
                    break;
            }
        }
        catch (JsonException)
        {
            // An event this client does not read is dropped: events are display data, never commands.
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The live display of a session a <see cref="FleetNodeClient"/> watches: full replacements, the latest ones kept when the
/// reader is slow (each says with <see cref="FleetSessionDisplay.HasGap"/> whether revisions were dropped before it).
/// </summary>
public sealed class FleetSessionWatch : IAsyncDisposable
{
    /// <summary>How many replacements wait for a slow reader; older ones are dropped.</summary>
    public const int Capacity = 8;

    private readonly Channel<FleetSessionDisplay> _updates = Channel.CreateBounded<FleetSessionDisplay>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });

    private readonly FleetNodeClient _client;

    internal FleetSessionWatch(string watchId, string sessionId, FleetNodeClient client)
    {
        WatchId = watchId;
        SessionId = sessionId;
        _client = client;
    }

    /// <summary>Gets the id of the watch.</summary>
    public string WatchId { get; }

    /// <summary>Gets the session.</summary>
    public string SessionId { get; }

    /// <summary>Gets why the node ended the watch (<c>closed</c>, <c>failed</c>), or null while it runs or when it was disposed.</summary>
    public string? EndReason { get; private set; }

    /// <summary>Gets the replacements, until the watch ends.</summary>
    public ChannelReader<FleetSessionDisplay> Updates => _updates.Reader;

    internal void Post(FleetSessionDisplay display) => _updates.Writer.TryWrite(display);

    internal void Complete(string? reason = null)
    {
        EndReason ??= reason;
        _updates.Writer.TryComplete();
    }

    /// <summary>Stops the watch on the node.</summary>
    /// <returns>A task that completes once the node was told.</returns>
    public ValueTask DisposeAsync() => new(_client.UnwatchAsync(this));
}
