using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>What a <see cref="FleetNodeService"/> serves and with which identity.</summary>
public sealed class FleetNodeServiceOptions
{
    /// <summary>Gets the runtime the node API reads and drives.</summary>
    public required CodeAltaHost Host { get; init; }

    /// <summary>Gets the key of the node.</summary>
    public required DeviceIdentity Identity { get; init; }

    /// <summary>Gets the devices the node trusts.</summary>
    public required TrustedDeviceStore TrustedDevices { get; init; }

    /// <summary>Gets the name of the node, shown to the devices that pair with it.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the version of CodeAlta the node runs.</summary>
    public string Version { get; init; } = "0.0.0";

    /// <summary>Gets what tells whether the node accepts new work (false once it stops).</summary>
    public Func<bool> AcceptsWork { get; init; } = static () => true;

    /// <summary>Gets the addresses a pairing offer names: those the node listens on, as <c>host:port</c>.</summary>
    public Func<IReadOnlyList<string>> PairingAddresses { get; init; } = static () => [];

    /// <summary>Gets the clock of the offers.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets what stops the node gracefully when its owner asks (<c>control.stop</c>); null when the owner cannot stop it this way.</summary>
    public Action? RequestStop { get; init; }

    /// <summary>
    /// Gets where the node clones a repository a moved session needs and it lacks (<c>&lt;root&gt;/&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;</c>):
    /// <c>~/code</c> by default; null clones nothing.
    /// </summary>
    public string? CloneRoot { get; init; } = Moves.FleetMoveReceiver.DefaultCloneRoot;

    /// <summary>Gets how many results of side-effecting calls the node keeps for their retries; fewer in tests.</summary>
    internal int RetryReceiptCapacity { get; init; } = FleetRetryReceipts.DefaultResultCapacity;
}

/// <summary>
/// The node API over the runtime of a node (<see cref="CodeAltaHost"/>), served on session channels. Each call is checked
/// against what the calling device may do (<see cref="DevicePermissions"/>), read again at every call so that a revocation
/// or a change of permissions applies at once. An unpaired client may only redeem the open pairing offer; the node's own
/// key, from this machine, is the owner's local control (<c>altanode pair</c>, <c>status</c>, <c>devices</c>, <c>revoke</c>, <c>permissions</c>, <c>projects</c>).
/// </summary>
public sealed partial class FleetNodeService : IAsyncDisposable
{
    private const int MaxIdempotencyKeyLength = 128;

    private readonly FleetNodeServiceOptions _options;
    private readonly CodeAltaHost _host;
    private readonly TrustedDeviceStore _trusted;
    private readonly X509Certificate2 _certificate;
    private readonly ConcurrentDictionary<FleetConnection, FleetNodeCaller> _callers = new();
    private readonly FleetRetryReceipts _receipts;
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Fleet.Node");
    // The repository of each project, which node.info publishes: read with git once in a while, not at every call.
    private readonly CodeAlta.Catalog.SessionMoves.RepositoryLocationCache _locations = new();
    // The unreadable project files already logged, so that each is logged once while it stays as it is.
    private readonly Lock _unreadableGate = new();
    private readonly FleetProjectEditor _projectEditor;
    private readonly HashSet<string> _warnedUnreadable = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="FleetNodeService"/> class.</summary>
    /// <param name="options">The runtime, the identity and the trust of the node.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The name of the node is not a valid device name.</exception>
    public FleetNodeService(FleetNodeServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        TrustedDeviceStore.ValidateName(options.Name, nameof(options));
        _options = options;
        _host = options.Host;
        _trusted = options.TrustedDevices;
        Pairing = new FleetPairingCoordinator(options.Identity, options.TrustedDevices, options.TimeProvider);
        _certificate = options.Identity.CreateTlsCertificate();
        var moves = new SessionMoveService(options.Host, options.Name);
        MoveReceiver = new Moves.FleetMoveReceiver(options.Host, moves, options.CloneRoot);
        MoveSender = new Moves.FleetMoveSender(options.Host, moves);
        _projectEditor = new FleetProjectEditor(options.Host.SpaceCatalog, options.CloneRoot);
        _receipts = new FleetRetryReceipts(options.Host.CatalogOptions.FleetRoot, options.TimeProvider, options.RetryReceiptCapacity,
            Math.Max(options.RetryReceiptCapacity, FleetRetryReceipts.DefaultKeyCapacity), LogWarn);
    }

    /// <summary>Gets the pairing of the node.</summary>
    public FleetPairingCoordinator Pairing { get; }

    /// <summary>Gets the devices the node trusts.</summary>
    public TrustedDeviceStore TrustedDevices => _trusted;

    /// <summary>Gets how many channels are open.</summary>
    public int ConnectionCount => _callers.Count;

    /// <summary>
    /// Serves a stream a listener accepted: runs the channel stack (<see cref="FleetChannelStack.AcceptAsync"/>), then the
    /// node API on it until it closes. Failures are logged; they never escape.
    /// </summary>
    /// <param name="accepted">The stream and where it comes from.</param>
    /// <param name="cancellationToken">Ends the channel.</param>
    /// <returns>A task that completes once the channel closed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="accepted"/> is null.</exception>
    public async Task ServeAsync(FleetAcceptedStream accepted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        FleetChannel channel;
        try
        {
            channel = await FleetChannelStack.AcceptAsync(accepted.Stream, new FleetChannelServerOptions
            {
                Certificate = _certificate,
                TrustPolicy = _trusted,
                AllowUnpairedClients = () => Pairing.HasOpenOffer,
                OwnFingerprint = _options.Identity.Fingerprint,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogInfo($"A fleet channel from {accepted.RemoteEndPoint} was refused: {exception.Message}");
            return;
        }

        await ServeAsync(channel, accepted.IsFromThisMachine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves a relayed stream (<c>doc/fleet/protocol.md</c>, Connections): the same channel stack as a direct one, end to
    /// end with the viewer, then the node API. The viewer may be a paired device, with its permissions, or a member of the
    /// organization in the signed device list (<paramref name="organization"/>); never the node's own key, and no pairing.
    /// Failures are logged; they never escape.
    /// </summary>
    /// <param name="stream">The stream of the relay; the node owns it.</param>
    /// <param name="organization">The members of the organization of the server that relays, as this node verified its device list.</param>
    /// <param name="cancellationToken">Ends the channel.</param>
    /// <returns>A task that completes once the channel closed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public async Task ServeRelayedAsync(Stream stream, IDeviceTrustPolicy organization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(organization);
        var trust = new Server.FleetCombinedTrust(_trusted, organization);
        FleetChannel channel;
        try
        {
            channel = await FleetChannelStack.AcceptAsync(stream, new FleetChannelServerOptions
            {
                Certificate = _certificate,
                TrustPolicy = trust,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogInfo($"A relayed fleet channel was refused: {exception.Message}");
            return;
        }

        await ServeAsync(channel, false, trust, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serves the node API on an authenticated channel until it closes.</summary>
    /// <param name="channel">The channel, not started.</param>
    /// <param name="fromThisMachine">Whether the channel comes from this machine: the node's own key is admitted only then.</param>
    /// <param name="cancellationToken">Ends the channel.</param>
    /// <returns>A task that completes once the channel closed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> is null.</exception>
    public Task ServeAsync(FleetChannel channel, bool fromThisMachine, CancellationToken cancellationToken) => ServeAsync(channel, fromThisMachine, _trusted, cancellationToken);

    private async Task ServeAsync(FleetChannel channel, bool fromThisMachine, IDeviceTrustPolicy trust, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var isOwner = channel.RemoteFingerprint == _options.Identity.Fingerprint;
        await using (channel.ConfigureAwait(false))
        {
            if (isOwner && !fromThisMachine)
            {
                // The node's key never comes from another machine: whoever has it there has stolen it.
                LogWarn("A fleet channel with the node's own key from another machine was refused.");
                return;
            }

            var caller = new FleetNodeCaller(channel.Connection, channel.RemoteDeviceId, channel.RemoteFingerprint, isOwner, channel.Client?.Kind) { Trust = trust };
            Register(caller);
            if (!_callers.TryAdd(channel.Connection, caller)) return;
            try
            {
                channel.Connection.Start();
                using var registration = cancellationToken.Register(static state => _ = ((FleetConnection)state!).CloseAsync("node_stopping"), channel.Connection);
                var closed = await channel.Connection.Completion.ConfigureAwait(false);
                LogDebug($"The fleet channel of {caller.DeviceId} closed: {closed.Code}.");
            }
            finally
            {
                _callers.TryRemove(channel.Connection, out _);
                await caller.StopWatchesAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Revokes a device and closes its open channels.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>Whether the device was trusted and is now revoked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="deviceId"/> is null.</exception>
    public async Task<bool> RevokeAsync(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        var revoked = _trusted.Revoke(deviceId);
        foreach (var caller in _callers.Values.Where(caller => string.Equals(caller.DeviceId, deviceId, StringComparison.Ordinal)))
            await caller.Connection.CloseAsync(FleetErrorCodes.Revoked).ConfigureAwait(false);
        return revoked;
    }

    /// <summary>Closes every channel and releases the certificate.</summary>
    /// <returns>A task that completes once the channels are closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var caller in _callers.Values)
        {
            try { await caller.Connection.CloseAsync("node_stopping").ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException) { }
        }

        _certificate.Dispose();
    }

    private void Register(FleetNodeCaller caller)
    {
        if (caller.IsOwner)
        {
            On(caller, FleetNodeMethods.ControlPairingCreate, Access.Owner, FleetNodeJsonContext.Default.FleetPairingCreateParams,
                FleetNodeJsonContext.Default.FleetPairingOffer, CreatePairingOfferAsync, optionalParams: true);
            On(caller, FleetNodeMethods.ControlPairingWait, Access.Owner, FleetNodeJsonContext.Default.FleetEmpty,
                FleetNodeJsonContext.Default.FleetPairingOutcome, WaitPairingAsync, optionalParams: true);
            On(caller, FleetNodeMethods.ControlStop, Access.Owner, FleetNodeJsonContext.Default.FleetEmpty,
                FleetNodeJsonContext.Default.FleetEmpty, ControlStopAsync, optionalParams: true);
            On(caller, FleetNodeMethods.ControlStatus, Access.Owner, FleetNodeJsonContext.Default.FleetEmpty,
                FleetNodeJsonContext.Default.FleetNodeStatus, ControlStatusAsync, optionalParams: true);
            On(caller, FleetNodeMethods.ControlDevicesList, Access.Owner, FleetNodeJsonContext.Default.FleetEmpty,
                FleetNodeJsonContext.Default.FleetDeviceList, ControlListDevicesAsync, optionalParams: true);
            On(caller, FleetNodeMethods.ControlDevicesRevoke, Access.Owner, FleetNodeJsonContext.Default.FleetDeviceRef,
                FleetNodeJsonContext.Default.FleetDeviceChange, ControlRevokeDeviceAsync);
            On(caller, FleetNodeMethods.ControlDevicesPermissions, Access.Owner, FleetNodeJsonContext.Default.FleetDevicePermissionsParams,
                FleetNodeJsonContext.Default.FleetDeviceChange, ControlSetPermissionsAsync);
            On(caller, FleetNodeMethods.ControlProjectsList, Access.Owner, FleetNodeJsonContext.Default.FleetEmpty,
                FleetNodeJsonContext.Default.FleetProjectList, ControlListProjectsAsync, optionalParams: true);
            On(caller, FleetNodeMethods.ControlProjectsAdd, Access.Owner, FleetNodeJsonContext.Default.FleetProjectAddParams,
                FleetNodeJsonContext.Default.FleetProjectChange, ControlAddProjectAsync);
            On(caller, FleetNodeMethods.ControlProjectsClone, Access.Owner, FleetNodeJsonContext.Default.FleetProjectCloneParams,
                FleetNodeJsonContext.Default.FleetProjectChange, ControlCloneProjectAsync);
            On(caller, FleetNodeMethods.ControlProjectsRemove, Access.Owner, FleetNodeJsonContext.Default.FleetProjectRef,
                FleetNodeJsonContext.Default.FleetProjectChange, ControlRemoveProjectAsync);
            // The owner may bring a session to its own node (altanode remote <machine> move), and read its moves.
            RegisterMoves(caller, Access.Owner);
            On(caller, FleetNodeMethods.MovesList, Access.Owner, FleetNodeJsonContext.Default.FleetEmpty, FleetNodeJsonContext.Default.FleetMoveList,
                ListMovesAsync, optionalParams: true);
            return;
        }

        On(caller, FleetNodeMethods.PairRedeem, Access.Anyone, FleetNodeJsonContext.Default.FleetPairRedeemParams,
            FleetNodeJsonContext.Default.FleetPairRedeemResult, RedeemAsync);
        On(caller, FleetNodeMethods.NodeInfo, Access.View, FleetNodeJsonContext.Default.FleetEmpty, FleetNodeJsonContext.Default.FleetNodeInfo,
            NodeInfoAsync, optionalParams: true);
        On(caller, FleetNodeMethods.SessionsList, Access.View, FleetNodeJsonContext.Default.FleetSessionListParams,
            FleetNodeJsonContext.Default.FleetSessionListResult, ListSessionsAsync, optionalParams: true);
        On(caller, FleetNodeMethods.SessionsState, Access.View, FleetNodeJsonContext.Default.FleetSessionRef,
            FleetNodeJsonContext.Default.FleetSessionState, SessionStateAsync);
        On(caller, FleetNodeMethods.SessionsHistory, Access.View, FleetNodeJsonContext.Default.FleetHistoryParams,
            FleetNodeJsonContext.Default.FleetHistoryResult, HistoryAsync);
        On(caller, FleetNodeMethods.SessionsWatch, Access.View, FleetNodeJsonContext.Default.FleetWatchParams,
            FleetNodeJsonContext.Default.FleetWatchResult, WatchAsync);
        On(caller, FleetNodeMethods.SessionsUnwatch, Access.View, FleetNodeJsonContext.Default.FleetUnwatchParams,
            FleetNodeJsonContext.Default.FleetEmpty, UnwatchAsync);
        On(caller, FleetNodeMethods.SessionsCreate, Access.Start, FleetNodeJsonContext.Default.FleetCreateSessionParams,
            FleetNodeJsonContext.Default.FleetCreateSessionResult, CreateSessionAsync);
        On(caller, FleetNodeMethods.SessionsSend, Access.Drive, FleetNodeJsonContext.Default.FleetSendParams,
            FleetNodeJsonContext.Default.FleetCommandResult, SendAsync);
        On(caller, FleetNodeMethods.SessionsQueue, Access.Drive, FleetNodeJsonContext.Default.FleetQueueParams,
            FleetNodeJsonContext.Default.FleetQueueResult, QueueAsync);
        On(caller, FleetNodeMethods.SessionsSteer, Access.Drive, FleetNodeJsonContext.Default.FleetSteerParams,
            FleetNodeJsonContext.Default.FleetCommandResult, SteerAsync);
        On(caller, FleetNodeMethods.SessionsAbort, Access.Drive, FleetNodeJsonContext.Default.FleetAbortParams,
            FleetNodeJsonContext.Default.FleetCommandResult, AbortAsync);
        On(caller, FleetNodeMethods.SessionsPermissionsList, Access.View, FleetNodeJsonContext.Default.FleetSessionRef,
            FleetNodeJsonContext.Default.FleetPermissionList, ListPermissionsAsync);
        On(caller, FleetNodeMethods.SessionsPermissionsAnswer, Access.Drive, FleetNodeJsonContext.Default.FleetPermissionAnswerParams,
            FleetNodeJsonContext.Default.FleetAnswerResult, AnswerPermissionAsync);
        On(caller, FleetNodeMethods.SessionsAsksList, Access.View, FleetNodeJsonContext.Default.FleetSessionRef,
            FleetNodeJsonContext.Default.FleetAskList, ListAsksAsync);
        On(caller, FleetNodeMethods.SessionsAsksAnswer, Access.Drive, FleetNodeJsonContext.Default.FleetAskAnswerParams,
            FleetNodeJsonContext.Default.FleetAnswerResult, AnswerAskAsync);
        RegisterMoves(caller, Access.Move);
        On(caller, FleetNodeMethods.MovesStart, Access.Relocate, FleetNodeJsonContext.Default.FleetMoveStartParams,
            FleetNodeJsonContext.Default.FleetMoveResult, StartMoveAsync);
        On(caller, FleetNodeMethods.MovesList, Access.View, FleetNodeJsonContext.Default.FleetEmpty, FleetNodeJsonContext.Default.FleetMoveList,
            ListMovesAsync, optionalParams: true);
    }

    // Who may call a method: anyone on the channel (pairing), the owner's local control, or a trusted device with a permission.
    private readonly record struct Access(DevicePermissions Required, bool AnyoneMayCall, bool OwnerOnly)
    {
        public static Access Anyone => new(DevicePermissions.None, true, false);
        public static Access Owner => new(DevicePermissions.None, false, true);
        public static Access View => new(DevicePermissions.View, false, false);
        public static Access Drive => new(DevicePermissions.View | DevicePermissions.Drive, false, false);
        public static Access Start => new(DevicePermissions.View | DevicePermissions.Start, false, false);
        public static Access Move => new(DevicePermissions.Move, false, false);
        public static Access Relocate => new(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Move, false, false);
    }

    private delegate Task<TResult> Handler<TParams, TResult>(FleetNodeCall call, TParams parameters, CancellationToken cancellationToken);

    private void On<TParams, TResult>(FleetNodeCaller caller, string method, Access access, JsonTypeInfo<TParams> parametersType,
        JsonTypeInfo<TResult> resultType, Handler<TParams, TResult> handler, bool optionalParams = false)
        where TParams : class
    {
        caller.Connection.RegisterRequestHandler(method, async (context, cancellationToken) =>
        {
            var device = Authorize(caller, access);
            TParams? parameters;
            try
            {
                parameters = context.Params is { ValueKind: not JsonValueKind.Null } element ? element.Deserialize(parametersType) : null;
            }
            catch (JsonException exception)
            {
                throw new FleetException(FleetErrorCodes.InvalidParams, null, $"The parameters of '{method}' are not valid: {exception.Message}");
            }

            if (parameters is null)
            {
                if (!optionalParams) throw new FleetException(FleetErrorCodes.InvalidParams, null, $"'{method}' needs parameters.");
                parameters = JsonSerializer.Deserialize("{}", parametersType)!;
            }

            TResult result;
            try
            {
                result = await handler(new FleetNodeCall(caller, device, context), parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not FleetException && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // The caller gets an internal error without the details: the owner finds them in the log of the node.
                LogError($"'{method}' failed for {caller.DeviceId}: {exception}");
                throw;
            }

            return JsonSerializer.SerializeToElement(result, resultType);
        });
    }

    // Read at every call: a device revoked or whose permissions changed since its channel opened is refused at once.
    private TrustedDevice? Authorize(FleetNodeCaller caller, Access access)
    {
        if (caller.IsOwner)
        {
            if (access.OwnerOnly) return null;
            throw new FleetException(FleetErrorCodes.Forbidden, null, "The local control of the node only manages its pairing, its devices, its projects and its moves.");
        }

        if (access.OwnerOnly) throw new FleetException(FleetErrorCodes.Forbidden, null, "Only the node's owner, on its machine, may do this.");
        if (caller.Trust.IsRevoked(caller.Fingerprint))
        {
            _ = caller.Connection.CloseAsync(FleetErrorCodes.Revoked);
            throw new FleetException(FleetErrorCodes.Revoked, null, "This device was revoked by the node.");
        }

        var device = caller.Trust.FindTrusted(caller.Fingerprint);
        if (access.AnyoneMayCall) return device;
        if (device is null) throw new FleetException(FleetErrorCodes.Unauthenticated, FleetErrorHints.Reauthenticate, "This device is not paired with the node.");
        if ((device.Permissions & access.Required) != access.Required)
            throw new FleetException(FleetErrorCodes.Forbidden, null, $"This device may not do this on the node (it needs {Describe(access.Required & ~device.Permissions)}).");
        return device;
    }

    /// <summary>The permissions as the API names them.</summary>
    internal static IReadOnlyList<string> Describe(DevicePermissions permissions) => DevicePermissionNames.Format(permissions);

    private void RequireWork()
    {
        if (!_options.AcceptsWork()) throw new FleetException(FleetNodeErrorCodes.NodeStopping, FleetErrorHints.Retry, "The node is stopping.");
    }

    // The idempotency key of a side-effecting call: required, bounded, and namespaced by the device, so that no device
    // ever gets the receipt of another one's command.
    private static string RetryKey(FleetNodeCall call)
    {
        var key = call.Context.IdempotencyKey;
        if (string.IsNullOrEmpty(key) || key.Length > MaxIdempotencyKeyLength || key.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new FleetException(FleetErrorCodes.InvalidRequest, null, $"'{call.Context.Method}' needs an idempotency key of 1 to {MaxIdempotencyKeyLength} characters without spaces.");
        return $"fleet:{call.Caller.DeviceId}:{key}";
    }

    // A side effect the runtime does not deduplicate itself (a creation, a queued prompt, an answer): the first call of a
    // key runs it, a retry of the same key with the same parameters gets its result, with other parameters a conflict,
    // and a key whose receipt was evicted or interrupted by a stop answers expired, across restarts (FleetRetryReceipts).
    private Task<T> Once<T>(FleetNodeCall call, JsonTypeInfo<T> resultType, Func<Task<T>> run) where T : class
        => _receipts.RunOnceAsync(call.Context.Method + "|" + RetryKey(call), call.Context.Params?.GetRawText() ?? "", resultType, run);

    private void LogInfo(string message)
    {
        if (LogManager.IsInitialized) _logger.Info(message);
    }

    private void LogWarn(string message)
    {
        if (LogManager.IsInitialized) _logger.Warn(message);
    }

    private void LogError(string message)
    {
        if (LogManager.IsInitialized) _logger.Error(message);
    }

    private void LogDebug(string message)
    {
        if (LogManager.IsInitialized) _logger.Debug(message);
    }

    /// <summary>One call: who makes it, the device as the node trusts it now, and the request.</summary>
    private sealed record FleetNodeCall(FleetNodeCaller Caller, TrustedDevice? Device, FleetRequestContext Context);
}

/// <summary>The device at the other end of one channel, and the watches it opened.</summary>
internal sealed class FleetNodeCaller(FleetConnection connection, string deviceId, DeviceFingerprint fingerprint, bool isOwner, string? clientKind)
{
    /// <summary>The most watches one channel keeps at a time.</summary>
    internal const int MaxWatches = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FleetNodeWatch> _watches = new(StringComparer.Ordinal);
    private bool _closed;

    public FleetConnection Connection { get; } = connection;

    public string DeviceId { get; } = deviceId;

    public DeviceFingerprint Fingerprint { get; } = fingerprint;

    public bool IsOwner { get; } = isOwner;

    public string? ClientKind { get; } = clientKind;

    /// <summary>Gets the trust the calls of the channel are checked against at each call: the paired devices, and on a relayed channel the members of the organization.</summary>
    public required IDeviceTrustPolicy Trust { get; init; }

    public bool TryAddWatch(FleetNodeWatch watch)
    {
        lock (_gate)
        {
            if (_closed || _watches.Count >= MaxWatches) return false;
            return _watches.TryAdd(watch.WatchId, watch);
        }
    }

    public FleetNodeWatch? RemoveWatch(string watchId)
    {
        lock (_gate)
        {
            return _watches.Remove(watchId, out var watch) ? watch : null;
        }
    }

    public async Task StopWatchesAsync()
    {
        FleetNodeWatch[] watches;
        lock (_gate)
        {
            _closed = true;
            watches = [.. _watches.Values];
            _watches.Clear();
        }

        foreach (var watch in watches) await watch.StopAsync().ConfigureAwait(false);
    }
}
