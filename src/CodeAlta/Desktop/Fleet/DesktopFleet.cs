using System.Collections.Immutable;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Server;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Fleet;

/// <summary>What a pairing link says, shown to the person before they pair.</summary>
/// <param name="Name">The name of the machine.</param>
/// <param name="DeviceId">Its device id.</param>
/// <param name="Words">The six words of its key, which <c>altanode pair</c> shows too.</param>
/// <param name="Addresses">Where the link says it listens.</param>
/// <param name="ExpiresAt">When the link expires.</param>
/// <param name="AlreadyPaired">Whether this device paired with that machine already (pairing again replaces it).</param>
internal sealed record FleetPairingPreview(string Name, string DeviceId, string Words, IReadOnlyList<string> Addresses, DateTimeOffset ExpiresAt, bool AlreadyPaired);

/// <summary>
/// The paired machines of this instance and their session hosts: it reads the machines of the profile
/// (<see cref="PairedMachineStore"/> in the fleet folder of the state root), keeps one <see cref="RemoteSessionHost"/>
/// per machine in the <see cref="SessionHostRegistry"/>, and pairs and forgets machines. It is the one owner of the
/// membership of the registry: its changes are made one at a time.
/// </summary>
/// <remarks>
/// Nothing happens for a profile that never paired: the device key of this instance is created by the first pairing,
/// and no connection is made without a paired machine.
/// </remarks>
internal sealed class DesktopFleet : IAsyncDisposable
{
    private readonly SessionHostRegistry.Membership _membership;
    private readonly PairedMachineStore _store;
    private readonly string _fleetRoot;
    private readonly string _deviceName;
    private readonly Func<PairedMachine, DeviceIdentity, RemoteSessionHost> _createHost;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private ImmutableList<RemoteSessionHost> _hosts = ImmutableList<RemoteSessionHost>.Empty;
    private DeviceIdentity? _identity;
    private bool _disposed;

    /// <summary>Creates the fleet of an instance; <see cref="StartAsync"/> connects its paired machines.</summary>
    /// <param name="fleetRoot">The fleet folder of the state root of the instance.</param>
    /// <param name="membership">The membership of the session hosts of the window.</param>
    /// <param name="deviceName">The name this device gives itself to the machines it pairs with.</param>
    /// <param name="createHost">Makes the host of a machine; null connects to it directly with the device key.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public DesktopFleet(string fleetRoot, SessionHostRegistry.Membership membership, string deviceName,
        Func<PairedMachine, DeviceIdentity, RemoteSessionHost>? createHost = null)
        : this(fleetRoot, membership, deviceName, createHost, null)
    {
    }

    /// <summary>Creates the fleet of an instance whose hosts connect again as the options say; <see cref="StartAsync"/> connects its paired machines.</summary>
    /// <param name="fleetRoot">The fleet folder of the state root of the instance.</param>
    /// <param name="membership">The membership of the session hosts of the window.</param>
    /// <param name="deviceName">The name this device gives itself to the machines it pairs with.</param>
    /// <param name="createHost">Makes the host of a machine; null connects to it directly, else through the relay of a server.</param>
    /// <param name="hostOptions">How the hosts made here connect again; null for the defaults.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public DesktopFleet(string fleetRoot, SessionHostRegistry.Membership membership, string deviceName,
        Func<PairedMachine, DeviceIdentity, RemoteSessionHost>? createHost, RemoteSessionHostOptions? hostOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        _fleetRoot = fleetRoot;
        _membership = membership;
        _deviceName = deviceName;
        _store = new PairedMachineStore(fleetRoot);
        // A machine may give this computer a session over the channel: the methods of a target are served on it.
        _createHost = createHost ?? ((machine, identity) => new RemoteSessionHost(machine, token => ConnectAsync(machine, identity, token), hostOptions, localDeviceId: identity.DeviceId));
    }

    /// <summary>
    /// Gets or sets what finds the connection to a server whose relay reaches a machine (<see cref="DesktopFleetServers.FindRoute"/>);
    /// null reaches machines directly only. Set before <see cref="StartAsync"/>.
    /// </summary>
    public Func<string, FleetConnector?>? RelayRoute { get; set; }

    /// <summary>Gets or sets the moves of sessions between this computer and its machines; set before <see cref="StartAsync"/>.</summary>
    public DesktopMoves? Moves { get; set; }

    /// <summary>Raised when a machine was paired or forgotten, or a machine changed its state or its list of sessions.</summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the repositories of the projects of this computer, which recognize the projects of the paired machines that
    /// are the same repository (their repository keys).
    /// </summary>
    public RepositoryLocationCache Locations { get; } = new();

    /// <summary>Gets the hosts of the paired machines, in the order they were paired.</summary>
    public IReadOnlyList<RemoteSessionHost> Hosts => Volatile.Read(ref _hosts);

    /// <summary>Gets the device id of this computer, as its paired machines authenticate it.</summary>
    public string DeviceId => Identity().DeviceId;

    /// <summary>Finds the host of a machine.</summary>
    /// <param name="machineId">The device id of the machine.</param>
    /// <returns>Its host; null when no paired machine has that id.</returns>
    public RemoteSessionHost? Find(string machineId) => Hosts.FirstOrDefault(host => string.Equals(host.MachineId, machineId, StringComparison.Ordinal));

    /// <summary>Connects the paired machines of the profile.</summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A task that completes once their hosts are started.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<PairedMachine> machines;
            try { machines = _store.List(); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Log(exception, "The paired machines could not be read");
                return;
            }

            if (machines.Count == 0) return;
            var identity = Identity();
            foreach (var machine in machines)
            {
                // A machine of a server reached through its relay so far keeps one host: the paired one.
                if (_membership.Remove(machine.DeviceId) is RemoteSessionHost previous) await RetireAsync(previous).ConfigureAwait(false);
                AddHost(machine, identity);
            }
        }
        finally
        {
            _changes.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Reads a pairing link, without connecting.</summary>
    /// <param name="link">The <c>codealta-pair://</c> link.</param>
    /// <param name="preview">What it says, when it can be used.</param>
    /// <param name="error">Why it cannot be used, otherwise.</param>
    /// <returns>Whether the link can be used now.</returns>
    public bool TryPreview(string? link, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FleetPairingPreview? preview, out PairingPayloadError error)
    {
        preview = null;
        if (!PairingPayload.TryParse(link?.Trim(), DateTimeOffset.UtcNow, out var payload, out error)) return false;
        preview = new FleetPairingPreview(payload.DeviceName, payload.DeviceId, payload.Fingerprint.ToShortWords(), payload.Addresses, payload.ExpiresAt,
            Find(payload.DeviceId) is not null);
        return true;
    }

    /// <summary>Pairs with the machine of a link: connects to it, pinned to its key, redeems the link and records the machine.</summary>
    /// <param name="link">The <c>codealta-pair://</c> link.</param>
    /// <param name="cancellationToken">Cancels the pairing.</param>
    /// <returns>The paired machine.</returns>
    /// <exception cref="FormatException">The link is not a valid, unexpired pairing link.</exception>
    /// <exception cref="IOException">No address of the machine answered.</exception>
    /// <exception cref="DeviceAuthenticationException">The machine presented another key, or refused this device.</exception>
    /// <exception cref="CodeAlta.Fleet.Protocol.FleetException">The machine refused the link.</exception>
    /// <exception cref="ObjectDisposedException">The fleet is closing.</exception>
    public async Task<PairedMachine> PairAsync(string link, CancellationToken cancellationToken)
    {
        if (!PairingPayload.TryParse(link?.Trim(), DateTimeOffset.UtcNow, out var payload, out var error))
            throw new FormatException($"The pairing link is not usable ({error}).");
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        PairedMachine machine;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var identity = Identity();
            machine = await FleetNodeClient.PairAsync(payload, identity, _deviceName, _store, cancellationToken).ConfigureAwait(false);
            // Pairing again replaces the machine: its addresses and permissions may have changed.
            if (_membership.Remove(machine.DeviceId) is RemoteSessionHost previous) await RetireAsync(previous).ConfigureAwait(false);
            AddHost(machine, identity);
        }
        finally
        {
            _changes.Release();
        }

        Changed?.Invoke();
        return machine;
    }

    /// <summary>Forgets a machine: its host stops and the machine leaves the paired machines of the profile.</summary>
    /// <param name="machineId">The device id of the machine.</param>
    /// <param name="cancellationToken">Cancels the wait for the change.</param>
    /// <returns>Whether a machine was forgotten.</returns>
    public async Task<bool> ForgetAsync(string machineId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        bool removed;
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            removed = _store.Remove(machineId);
            if (_membership.Remove(machineId) is RemoteSessionHost host)
            {
                removed = true;
                await RetireAsync(host).ConfigureAwait(false);
            }
        }
        finally
        {
            _changes.Release();
        }

        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var host in Volatile.Read(ref _hosts))
            {
                _membership.Remove(host.MachineId!);
                host.Changed -= OnHostChanged;
                await host.DisposeAsync().ConfigureAwait(false);
            }

            Volatile.Write(ref _hosts, ImmutableList<RemoteSessionHost>.Empty);
            _identity?.Dispose();
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>
    /// Follows the machines of the organizations of the servers this computer is linked to: a node it is not paired with
    /// gets a host that reaches it through the relay of its server, and loses it once no server lists it. A paired machine
    /// keeps its own host, which goes through the relay when no address of it answers.
    /// </summary>
    /// <param name="machines">The nodes the servers list, by device id, with their names.</param>
    /// <param name="cancellationToken">Cancels the wait for the change.</param>
    /// <returns>A task that completes once the hosts follow the list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="machines"/> is null.</exception>
    public async Task SyncOrganizationMachinesAsync(IReadOnlyList<(string Id, string Name)> machines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machines);
        var changed = false;
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            var listed = machines.ToDictionary(static machine => machine.Id, static machine => machine.Name, StringComparer.Ordinal);
            foreach (var host in Volatile.Read(ref _hosts).Where(host => !host.IsPaired && !listed.ContainsKey(host.MachineId!)).ToArray())
            {
                _membership.Remove(host.MachineId!);
                await RetireAsync(host).ConfigureAwait(false);
                changed = true;
            }

            var added = listed.Where(pair => Find(pair.Key) is null).ToArray();
            if (added.Length > 0)
            {
                var identity = Identity();
                // The key of the machine comes from the signed device list at each connection: none is pinned here.
                foreach (var (id, name) in added)
                {
                    AddHost(new PairedMachine(string.IsNullOrWhiteSpace(name) ? id : name, id, default, [], DateTimeOffset.UnixEpoch, []), identity);
                    changed = true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log(exception, "The machines of the organization could not be followed");
        }
        finally
        {
            _changes.Release();
        }

        if (changed) Changed?.Invoke();
    }

    // The channel of a machine: direct first when it has addresses and one answers, through the relay of a server that
    // lists it otherwise. The relay is the path that always works; a direct one is an optimization.
    private async Task<FleetNodeClient> ConnectAsync(PairedMachine machine, DeviceIdentity identity, CancellationToken cancellationToken)
    {
        var serve = Moves?.Serve(machine);
        var route = RelayRoute?.Invoke(machine.DeviceId);
        if (machine.Addresses.Count > 0)
        {
            try
            {
                return await FleetNodeClient.ConnectAsync(machine, identity, serve, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (route is not null && route.Directory.IsOnline(machine.DeviceId))
            {
                // No address answers (another network): the server reaches it.
            }
        }

        if (route is null) throw new IOException("No server this computer is connected to lists the machine.");
        // An offline machine is not asked for: a relay to it would fail, and leave a refusal in the audit of the server.
        if (!route.Directory.IsOnline(machine.DeviceId)) throw new IOException("The machine is offline.");
        try
        {
            return await FleetNodeClient.ConnectRelayedAsync(route, machine.DeviceId, identity, serve, machine.Fingerprint, cancellationToken).ConfigureAwait(false);
        }
        catch (FleetServerException exception) when (exception.Code is CodeAlta.Fleet.Protocol.FleetErrorCodes.Revoked or CodeAlta.Fleet.Protocol.FleetErrorCodes.Unauthenticated
            or CodeAlta.Fleet.Protocol.FleetErrorCodes.Forbidden)
        {
            // The server refuses this device: it takes a person to change that, as a machine that refuses it.
            throw new DeviceAuthenticationException(DeviceAuthenticationFailure.ClientNotTrusted, exception.Message, exception);
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or InvalidDataException or CodeAlta.Fleet.Protocol.FleetDeviceListException or System.Net.WebSockets.WebSocketException)
        {
            // The ways a relay fails that the host knows as a machine that does not answer: it tries again later.
            throw new IOException(exception.Message, exception);
        }
    }

    // The key of this device, created by the first pairing.
    private DeviceIdentity Identity() => _identity ??= DeviceIdentity.LoadOrCreate(_fleetRoot);

    private void AddHost(PairedMachine machine, DeviceIdentity identity)
    {
        var host = _createHost(machine, identity);
        host.Changed += OnHostChanged;
        _membership.Add(host);
        Volatile.Write(ref _hosts, Volatile.Read(ref _hosts).Add(host));
        host.Start();
    }

    private async Task RetireAsync(RemoteSessionHost host)
    {
        host.Changed -= OnHostChanged;
        Volatile.Write(ref _hosts, Volatile.Read(ref _hosts).Remove(host));
        await host.DisposeAsync().ConfigureAwait(false);
    }

    private void OnHostChanged(RemoteSessionHost host) => Changed?.Invoke();

    private static void Log(Exception exception, string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Fleet").Warn(exception, message);
    }
}
