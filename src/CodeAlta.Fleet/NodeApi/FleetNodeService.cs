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

    /// <summary>Gets what tells whether the node refuses new work because it drains to switch versions (not because it stops).</summary>
    public Func<bool> Updating { get; init; } = static () => false;

    /// <summary>Gets what reads the versions of the node and its self-update; null for a host that does not update itself.</summary>
    public Func<Protocol.FleetUpdateStatus?>? UpdateStatus { get; init; }

    /// <summary>
    /// Gets what tells whether a provider of the machine may run with a personal plan (a subscription signed in by a person), which
    /// is true too for one whose credentials cannot be told: such a provider serves only the owner's own work, so a session another
    /// person started here, and every session it starts, is refused it (<c>doc/fleet/security-and-privacy.md</c>, "Subscriptions
    /// are personal"). Null refuses nothing.
    /// </summary>
    public Func<CodeAlta.Agent.ModelProviderDescriptor, bool>? IsPersonalProvider { get; init; }

    /// <summary>Gets the model host of the machine (its llama.cpp builds, models and model servers); null for a host that has none.</summary>
    public CodeAlta.ModelServers.ModelHost? ModelServers { get; init; }

    /// <summary>Gets what reads the service that runs the node and whether it starts at boot (<c>control.status</c>); null for a host that has none.</summary>
    public Func<FleetNodeServiceStatus?>? ServiceStatus { get; init; }

    /// <summary>Gets the sign-ins the node keeps for itself, made from the desktop that manages it; null for a host that has none.</summary>
    public IFleetNodeSignIns? SignIns { get; init; }

    /// <summary>Gets the addresses a pairing offer names: those the node listens on, as <c>host:port</c>.</summary>
    public Func<IReadOnlyList<string>> PairingAddresses { get; init; } = static () => [];

    /// <summary>Gets the clock of the offers.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Gets what records a device that pairs both ways as a machine this node reaches, once its secret redeemed an offer made
    /// both ways: whether it was recorded. Null for a node that pairs one way only (a headless node, which reaches no desktop).
    /// </summary>
    public Func<FleetPairBackRequest, CancellationToken, Task<bool>>? PairBack { get; init; }

    // What protects the held sends in the place of the store of the machine, for tests (a store that goes away); null for that store.
    internal CodeAlta.Catalog.Secrets.LocalSecretStore? HeldSendsProtection { get; init; }

    /// <summary>Gets what stops the node gracefully when its owner asks (<c>control.stop</c>); null when the owner cannot stop it this way.</summary>
    public Action? RequestStop { get; init; }

    /// <summary>
    /// Gets where the node clones a repository a moved session needs and it lacks (<c>&lt;root&gt;/&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;</c>):
    /// <c>~/code</c> by default; null clones nothing.
    /// </summary>
    public string? CloneRoot { get; init; } = Moves.FleetMoveReceiver.DefaultCloneRoot;

    /// <summary>Gets how many results of side-effecting calls the node keeps for their retries; fewer in tests.</summary>
    internal int RetryReceiptCapacity { get; init; } = FleetRetryReceipts.DefaultResultCapacity;

    /// <summary>Gets the instruments of the requests and the moves; null for none.</summary>
    public Telemetry.FleetMetrics? Metrics { get; init; }

    /// <summary>
    /// Gets the automations the node runs for its paired devices (<c>automations.*</c>); null for a host that runs none,
    /// whose methods then answer <see cref="FleetNodeErrorCodes.AutomationsUnavailable"/>.
    /// </summary>
    public IFleetNodeAutomations? Automations { get; init; }

    /// <summary>
    /// Gets what admits a run to an environment of the fleet (<c>doc/fleet/secrets.md</c>, environments), by the name of the
    /// environment, the run, and what the run is for its approver: null when it may start, else why not. It may wait for
    /// an approval. Null runs no session in an environment.
    /// </summary>
    public Func<string, string, string?, CancellationToken, Task<EnvironmentAdmissionResult>>? AdmitEnvironment { get; init; }

    /// <summary>
    /// Gets what admits a run to the budgets of the machine (<c>doc/fleet/observability.md</c>, budgets and alerts) before its
    /// prompt is sent: the run, what is told once it waits for a person, and the wait. Null sends without a budget.
    /// </summary>
    public Func<FleetNodeBudgetRun, Action, CancellationToken, Task<Server.FleetBudgetAdmission>>? AdmitBudget { get; init; }

    /// <summary>
    /// Gets the log and the self-update the node gives its owner and the devices that manage it (<c>control.log</c>,
    /// <c>control.update</c>); null for a host without them, whose methods then answer <see cref="FleetNodeErrorCodes.Unavailable"/>.
    /// </summary>
    public IFleetNodeManagement? Management { get; init; }

    /// <summary>
    /// Gets the providers the node gives its owner and the devices that manage it (<c>control.providers.*</c>): their readiness,
    /// a provider added with its key, and the device-code sign-in; null for a host without them, whose methods then answer
    /// <see cref="FleetNodeErrorCodes.ProvidersUnavailable"/>.
    /// </summary>
    public IFleetNodeProviders? Providers { get; init; }
}

/// <summary>A device that paired with a node both ways: the node reaches it as a machine of its own, pinned to its key.</summary>
/// <param name="Name">The name the device gives itself.</param>
/// <param name="Fingerprint">The fingerprint of its key, from the channel of the pairing.</param>
/// <param name="Addresses">Where the node reaches it, as <c>host:port</c>; empty when it does not listen (a server may relay to it).</param>
/// <param name="Permissions">What the node may do on it, as it says (<c>view</c>, <c>drive</c>, <c>start</c>, <c>move</c>).</param>
public sealed record FleetPairBackRequest(string Name, DeviceFingerprint Fingerprint, IReadOnlyList<string> Addresses, IReadOnlyList<string> Permissions);

/// <summary>
/// The node API over the runtime of a node (<see cref="CodeAltaHost"/>), served on session channels. Each call is checked
/// against what the calling device may do (<see cref="DevicePermissions"/>), read again at every call so that a revocation
/// or a change of permissions applies at once. An unpaired client may only redeem the open pairing offer; the node's own
/// key, from this machine, is the owner's local control (<c>altanode pair</c>, <c>status</c>, <c>devices</c>, <c>revoke</c>, <c>permissions</c>, <c>projects</c>).
/// A paired device with <see cref="DevicePermissions.Manage"/> calls the same <c>control.*</c> methods from elsewhere,
/// pairing and stopping the node excepted: the node's status, its devices and what they may do, its projects, its log,
/// its updates, and its secrets and variables when the node trusts the device by its own pairing.
/// </summary>
public sealed partial class FleetNodeService : IAsyncDisposable
{
    private const int MaxIdempotencyKeyLength = 128;

    private readonly FleetNodeServiceOptions _options;
    private readonly CodeAltaHost _host;
    // The worktree each session started here works in (doc/fleet/node.md, "Workspaces").
    private readonly FleetSessionWorktrees _worktrees;
    // What removes those worktrees once their sessions no longer use them.
    private readonly FleetSessionWorktreeCleanup _worktreeCleanup;
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
    private readonly FleetAgentSessions _agents;
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
        _worktrees = new FleetSessionWorktrees(options.Host.CatalogOptions);
        _worktreeCleanup = new FleetSessionWorktreeCleanup(options.Host, options.TimeProvider);
        _trusted = options.TrustedDevices;
        Pairing = new FleetPairingCoordinator(options.Identity, options.TrustedDevices, options.TimeProvider);
        _certificate = options.Identity.CreateTlsCertificate();
        var moves = new SessionMoveService(options.Host, options.Name) { DeviceIdSource = () => options.Identity.DeviceId };
        MoveReceiver = new Moves.FleetMoveReceiver(options.Host, moves, options.CloneRoot);
        MoveSender = new Moves.FleetMoveSender(options.Host, moves);
        _projectEditor = new FleetProjectEditor(options.Host.SpaceCatalog, options.CloneRoot);
        _receipts = new FleetRetryReceipts(options.Host.CatalogOptions.FleetRoot, options.TimeProvider, options.RetryReceiptCapacity,
            Math.Max(options.RetryReceiptCapacity, FleetRetryReceipts.DefaultKeyCapacity), LogWarn);
        // What the sessions agents of other machines started here say to their parents waits in the outbox of the node.
        _agents = new FleetAgentSessions(options.Host.CatalogOptions.FleetRoot, options.TimeProvider);
        options.Host.RuntimeService.RemoteParents = _agents;
        // A queued prompt starts its turn later: the budgets of the machine admit it then, as a send.
        if (options.AdmitBudget is not null)
        {
            _queuedRunGate = AdmitQueuedAsync;
            options.Host.RuntimeService.QueuedRunGate = _queuedRunGate;
        }

        // A session another person started here (a member of a team) resolves none of the owner's secrets.
        // A session another person started here (a member of a team), or a run nobody attends that is not the owner's (an automation
        // of a team or of the organization), resolves none of the owner's secrets.
        options.Host.Identities.IsOwnerWork = sessionId => !Principals.IsAttributed(sessionId);
        // A session such a session starts on this machine is the work of the same person or run, and so are those it starts.
        // One that cannot be recorded is refused: its identity is not resolved, and it does not attach.
        options.Host.Identities.InheritWork = (sessionId, parentSessionId) => Principals.Inherit(sessionId, parentSessionId);
        // A session deleted is gone for sure: whose work it was is forgotten then, never because it is not found now.
        options.Host.RuntimeService.SessionDeleted += ForgetPrincipalOfDeleted;
        // A personal plan serves its owner's own work only: at every attachment of a session of another person, of their sub-sessions too.
        if (options.IsPersonalProvider is not null) options.Host.RuntimeService.AddProviderGate(CheckOthersWorkProviderAsync);
        _worktreeCleanup.Start();
        if (options.AdmitBudget is not null)
        {
            // A session whose send a budget holds waits for a person (it does not move); the sends held when the node stopped are
            // taken again once the host wired its budgets and policies (ResumeHeldSends), not here.
            _heldSessions = () => HeldSends.SessionIds;
            options.Host.HeldSends = _heldSessions;
        }

        RestoreAgentRestrictions();
    }

    // What tells the host the sessions whose send a budget holds, taken back when the service stops.
    private readonly Func<IReadOnlyCollection<string>>? _heldSessions;

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
        var trust = new Server.FleetCombinedTrust(_trusted, new FleetNamedPeopleTrust(organization, People));
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

    /// <summary>
    /// Serves a stream a server relays to this machine (see <see cref="ServeRelayedAsync(Stream, IDeviceTrustPolicy, CancellationToken)"/>),
    /// with the members of its organization as the connector of that server verifies them: its device list and its access document
    /// are read again first, so that a device removed from it is refused at once; when the server does not answer, the versions
    /// verified last decide, and a list that does not verify lets nobody in. Failures are logged; they never escape.
    /// </summary>
    /// <param name="stream">The stream of the relay; the node owns it.</param>
    /// <param name="connector">The control connection of the server that relays.</param>
    /// <param name="cancellationToken">Ends the channel.</param>
    /// <returns>A task that completes once the channel closed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public async Task ServeRelayedAsync(Stream stream, Server.FleetConnector connector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(connector);
        try
        {
            await connector.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
            await connector.Access.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Server.FleetServerException or HttpRequestException or IOException or TaskCanceledException)
        {
            LogInfo($"The device list could not be read again: {exception.Message}");
        }
        catch (FleetAccessDocumentException exception)
        {
            // A document that does not verify changes nothing: the version verified before still decides.
            LogWarn($"The access document was refused: {exception.Message}");
        }
        catch (FleetDeviceListException exception)
        {
            // A list that does not verify is no ground to trust anyone through this server.
            LogWarn($"A relayed channel was refused: {exception.Message}");
            await stream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await ServeRelayedAsync(stream, connector.Trust, cancellationToken).ConfigureAwait(false);
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
        if (ReferenceEquals(_host.RuntimeService.RemoteParents, _agents)) _host.RuntimeService.RemoteParents = null;
        if (_queuedRunGate is not null && ReferenceEquals(_host.RuntimeService.QueuedRunGate, _queuedRunGate)) _host.RuntimeService.QueuedRunGate = null;
        if (_heldSessions is not null && ReferenceEquals(_host.HeldSends, _heldSessions)) _host.HeldSends = null;
        _host.RuntimeService.SessionDeleted -= ForgetPrincipalOfDeleted;
        await _worktreeCleanup.DisposeAsync().ConfigureAwait(false);
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
            RegisterManagementExtras(caller, Access.Owner);
            // The secrets of the machine: its owner, on the machine, manages them.
            RegisterSecrets(caller, Access.Owner);
            RegisterProviders(caller, Access.Owner, Access.Owner);
            RegisterSignIns(caller, Access.Owner, Access.Owner);
            RegisterAgentPolicy(caller, Access.Owner);
            RegisterSessionDefaults(caller, Access.Owner);
            RegisterLabels(caller, Access.Owner);
            RegisterPeople(caller, Access.Owner);
            RegisterModelServers(caller, owner: true);
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
        // A channel ends its own watches only, whatever its rights now: a reader whose grant gave the watch ends it too.
        On(caller, FleetNodeMethods.SessionsUnwatch, Access.Anyone, FleetNodeJsonContext.Default.FleetUnwatchParams,
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
        RegisterAutomations(caller);
        RegisterAgents(caller);
        RegisterManagement(caller);
    }

    // What a device that manages the node may do from elsewhere: the owner's local control, pairing and stopping excepted.
    private void RegisterManagement(FleetNodeCaller caller)
    {
        On(caller, FleetNodeMethods.ControlStatus, Access.Manage, FleetNodeJsonContext.Default.FleetEmpty,
            FleetNodeJsonContext.Default.FleetNodeStatus, ControlStatusAsync, optionalParams: true);
        On(caller, FleetNodeMethods.ControlDevicesList, Access.Manage, FleetNodeJsonContext.Default.FleetEmpty,
            FleetNodeJsonContext.Default.FleetDeviceList, ControlListDevicesAsync, optionalParams: true);
        On(caller, FleetNodeMethods.ControlDevicesRevoke, Access.Manage, FleetNodeJsonContext.Default.FleetDeviceRef,
            FleetNodeJsonContext.Default.FleetDeviceChange, ControlRevokeDeviceAsync);
        On(caller, FleetNodeMethods.ControlDevicesPermissions, Access.Manage, FleetNodeJsonContext.Default.FleetDevicePermissionsParams,
            FleetNodeJsonContext.Default.FleetDeviceChange, ControlSetPermissionsAsync);
        On(caller, FleetNodeMethods.ControlProjectsList, Access.Manage, FleetNodeJsonContext.Default.FleetEmpty,
            FleetNodeJsonContext.Default.FleetProjectList, ControlListProjectsAsync, optionalParams: true);
        On(caller, FleetNodeMethods.ControlProjectsAdd, Access.Manage, FleetNodeJsonContext.Default.FleetProjectAddParams,
            FleetNodeJsonContext.Default.FleetProjectChange, ControlAddProjectAsync);
        On(caller, FleetNodeMethods.ControlProjectsClone, Access.Manage, FleetNodeJsonContext.Default.FleetProjectCloneParams,
            FleetNodeJsonContext.Default.FleetProjectChange, ControlCloneProjectAsync);
        On(caller, FleetNodeMethods.ControlProjectsRemove, Access.Manage, FleetNodeJsonContext.Default.FleetProjectRef,
            FleetNodeJsonContext.Default.FleetProjectChange, ControlRemoveProjectAsync);
        RegisterManagementExtras(caller, Access.Manage);
        // And its secrets, for a device paired with the node only: never one trusted through the list of an organization.
        RegisterSecrets(caller, Access.ManagePaired);
        // Its providers are read by any device that manages it; a key or a sign-in, a credential of the machine, by a paired one only.
        RegisterProviders(caller, Access.Manage, Access.ManagePaired);
        RegisterSignIns(caller, Access.Manage, Access.ManagePaired);
        RegisterAgentPolicy(caller, Access.Manage);
        RegisterSessionDefaults(caller, Access.Manage);
        RegisterLabels(caller, Access.Manage);
        // Who the machine lets in is its owner's: a device paired to manage it, never one trusted through the organization alone.
        RegisterPeople(caller, Access.ManagePaired);
        RegisterModelServers(caller, owner: false);
    }

    private void RegisterManagementExtras(FleetNodeCaller caller, Access access)
    {
        On(caller, FleetNodeMethods.ControlLog, access, FleetNodeJsonContext.Default.FleetNodeLogParams,
            FleetNodeJsonContext.Default.FleetNodeLog, ControlLogAsync, optionalParams: true);
        On(caller, FleetNodeMethods.ControlUpdate, access, FleetNodeJsonContext.Default.FleetNodeUpdateParams,
            FleetNodeJsonContext.Default.FleetNodeUpdateResult, ControlUpdateAsync, optionalParams: true);
        On(caller, FleetNodeMethods.ControlServersJoin, access, FleetNodeJsonContext.Default.FleetServerJoinParams,
            FleetNodeJsonContext.Default.FleetServerJoinResult, ControlJoinServerAsync);
    }

    // Who may call a method: anyone on the channel (pairing), the owner's local control, or a trusted device with a permission;
    // PairedOnly asks that the node trust the device itself, by its pairing, and not through the device list of an organization.
    private readonly record struct Access(DevicePermissions Required, bool AnyoneMayCall, bool OwnerOnly, bool PairedOnly = false)
    {
        public static Access Anyone => new(DevicePermissions.None, true, false);
        public static Access Owner => new(DevicePermissions.None, false, true);
        public static Access View => new(DevicePermissions.View, false, false);
        public static Access Drive => new(DevicePermissions.View | DevicePermissions.Drive, false, false);
        public static Access Start => new(DevicePermissions.View | DevicePermissions.Start, false, false);
        public static Access Move => new(DevicePermissions.Move, false, false);
        public static Access Relocate => new(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Move, false, false);
        public static Access Automations => new(DevicePermissions.View | DevicePermissions.Automate, false, false);
        public static Access Automate => new(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, false, false);
        public static Access Manage => new(DevicePermissions.Manage, false, false);
        public static Access ManagePaired => new(DevicePermissions.Manage, false, false, PairedOnly: true);
        public static Access Paired => new(DevicePermissions.None, false, false);
    }

    private delegate Task<TResult> Handler<TParams, TResult>(FleetNodeCall call, TParams parameters, CancellationToken cancellationToken);

    private void On<TParams, TResult>(FleetNodeCaller caller, string method, Access access, JsonTypeInfo<TParams> parametersType,
        JsonTypeInfo<TResult> resultType, Handler<TParams, TResult> handler, bool optionalParams = false)
        where TParams : class
    {
        caller.Connection.RegisterRequestHandler(method, async (context, cancellationToken) =>
        {
            // A span per request: the method, the device in short and the outcome; never the parameters.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var activity = _options.Metrics?.StartNodeApiRequest(method, caller.DeviceId);
            string? failure = null;
            try
            {
                return await HandleAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetException exception)
            {
                failure = exception.Error.Code;
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failure = FleetErrorCodes.InternalError;
                throw;
            }
            finally
            {
                _options.Metrics?.EndNodeApiRequest(activity, method, started, failure);
            }
        });

        async ValueTask<JsonElement?> HandleAsync(FleetRequestContext context, CancellationToken cancellationToken)
        {
            // A person granted the content of a session reads it live here (its state, its history, what it does), and nothing else.
            var granted = method is FleetNodeMethods.SessionsState or FleetNodeMethods.SessionsHistory or FleetNodeMethods.SessionsWatch
                ? ContentReader(caller, context.Params) : null;
            var device = granted?.Device ?? Authorize(caller, access);
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

            // On a team machine a person reaches only the sessions they started here; the moves of the machine are not theirs to read.
            if (device is { OwnSessionsOnly: true } && method == FleetNodeMethods.MovesList)
                throw new FleetException(FleetErrorCodes.Forbidden, null, "The moves of a team machine are for its owner's devices.");
            if (granted is null) RequireOwnSession(device, parameters);
            else if (SessionIdOf(parameters) != granted.Value.SessionId)
                throw new FleetException(FleetNodeErrorCodes.SessionNotFound, null, "No session of yours has this id on this machine.");

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
        }
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
        {
            // A person of the organization the machine does not name: their first try asks its owner, who lets them in or not.
            if (device is { PersonId: { } person, OwnSessionsOnly: true, Permissions: DevicePermissions.None } && _trusted.FindTrusted(caller.Fingerprint) is null
                && (access.Required & (DevicePermissions.View | DevicePermissions.Start)) != 0)
            {
                People.Ask(person, device.DeviceId, device.OrganizationId);
                throw new FleetException(FleetErrorCodes.Forbidden, null, "The owner of this machine was asked to let you use it: try again once they did.");
            }

            throw new FleetException(FleetErrorCodes.Forbidden, null, $"This device may not do this on the node (it needs {string.Join(", ", Describe(access.Required & ~device.Permissions))}).");
        }

        if (access.PairedOnly && _trusted.FindTrusted(caller.Fingerprint) is null)
            throw new FleetException(FleetErrorCodes.Forbidden, null, "Only a device paired with the node may do this.");
        return device;
    }

    /// <summary>The permissions as the API names them.</summary>
    internal static IReadOnlyList<string> Describe(DevicePermissions permissions) => DevicePermissionNames.Format(permissions);

    private void RequireWork()
    {
        if (_options.AcceptsWork()) return;
        if (_options.Updating()) throw new FleetException(FleetNodeErrorCodes.NodeUpdating, FleetErrorHints.Retry, "The node is switching to a new version: it takes work again in a moment.");
        throw new FleetException(FleetNodeErrorCodes.NodeStopping, FleetErrorHints.Retry, "The node is stopping.");
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
