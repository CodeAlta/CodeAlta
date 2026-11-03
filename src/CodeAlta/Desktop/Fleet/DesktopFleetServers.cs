using CodeAlta.Catalog;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Fleet;

/// <summary>
/// The CodeAlta Servers this instance is linked to (<c>doc/fleet/console.md</c>): it creates a server it was given the
/// setup code of (this desktop becomes the owner's controller device), joins one with an enrollment token, makes
/// enrollment tokens and approves devices with entries this device signs, links spaces, and keeps a control connection
/// per server that publishes the facts of the linked spaces and follows the directory of the organization.
/// </summary>
/// <remarks>
/// Nothing happens for a profile without a server: the device key is the one pairing makes, created by the first
/// pairing or the first server, and no connection is made before a link exists. Keys and requests stay in the host.
/// </remarks>
internal sealed class DesktopFleetServers : IAsyncDisposable
{
    private readonly CodeAltaHost _host;
    private readonly string _deviceName;
    private readonly string _version;
    private readonly string _fleetRoot;
    private readonly FleetServerLinkStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<FleetConnectorOptions, FleetConnectorOptions>? _configure;
    private DeviceIdentity? _identity;
    private FleetHostPublicationSource? _source;
    private FleetServerConnections? _connections;
    private bool _disposed;

    /// <summary>Creates the servers of an instance; <see cref="StartAsync"/> connects the links it has.</summary>
    /// <param name="host">The runtime of the window.</param>
    /// <param name="deviceName">The name of this computer at the servers.</param>
    /// <param name="version">The version of CodeAlta.</param>
    /// <param name="configure">What changes the options of each connector, for tests; null for none.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public DesktopFleetServers(CodeAltaHost host, string deviceName, string version, Func<FleetConnectorOptions, FleetConnectorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        _host = host;
        _deviceName = TrustedDeviceStore.NormalizeName(deviceName);
        _version = string.IsNullOrWhiteSpace(version) ? "0.0.0" : version.Split('+')[0];
        _fleetRoot = host.CatalogOptions.FleetRoot;
        _store = new FleetServerLinkStore(_fleetRoot);
        _configure = configure;
    }

    /// <summary>Raised when a link, a state or a directory changed.</summary>
    public event Action? Changed;

    /// <summary>Gets the HTTP handler of the calls to the servers, for tests; null for the usual one.</summary>
    internal HttpMessageHandler? Handler { get; init; }

    /// <summary>Gets the links and their connectors.</summary>
    public IReadOnlyList<FleetServerConnection> Connections
    {
        get
        {
            if (Volatile.Read(ref _connections) is { } connections) return connections.Connections;
            try { return [.. _store.List().Select(static link => new FleetServerConnection(link, null))]; }
            catch (InvalidDataException) { return []; }
        }
    }

    /// <summary>
    /// Finds the connection to a server whose relay reaches a machine: one online that lists it, online itself first.
    /// </summary>
    /// <param name="machineId">The device of the machine.</param>
    /// <returns>The connector, or null when no server this computer is connected to lists it.</returns>
    public FleetConnector? FindRoute(string machineId)
    {
        var listing = Connections.Select(static connection => connection.Connector)
            .Where(connector => connector is { State: FleetConnectorState.Online } && connector.Directory.Machines.ContainsKey(machineId))
            .ToArray();
        return listing.FirstOrDefault(connector => connector!.Directory.IsOnline(machineId)) ?? listing.FirstOrDefault();
    }

    /// <summary>Lists the nodes of the organizations of the servers this computer is connected to, but this computer.</summary>
    /// <returns>Their device ids and names, once each.</returns>
    public IReadOnlyList<(string Id, string Name)> OrganizationMachines()
    {
        var self = DeviceId;
        var machines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var connector in Connections.Select(static connection => connection.Connector))
        {
            if (connector is not { State: FleetConnectorState.Online }) continue;
            foreach (var machine in connector.Directory.Machines.Values)
                if (machine.Kind == FleetDeviceKinds.Node && machine.DeviceId != self) machines.TryAdd(machine.DeviceId, machine.Inventory.Name);
        }

        return [.. machines.Select(static pair => (pair.Key, pair.Value))];
    }

    /// <summary>Gets the id of this device, once its key exists.</summary>
    public string? DeviceId => Volatile.Read(ref _identity)?.DeviceId;

    /// <summary>Gets the words of the key of this device, once it exists.</summary>
    public string? Words => Volatile.Read(ref _identity)?.Fingerprint.ToShortWords();

    /// <summary>Connects the servers this instance is linked to, if any.</summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A task that completes once the connectors run.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FleetServerLink> links;
        try { links = _store.List(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log(exception, "The server links could not be read");
            return;
        }

        if (links.Count == 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectionsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sets a server up with its one-time code: this desktop becomes the first controller device of a new organization.</summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="code">The setup code (<c>altaserver setup-code</c>).</param>
    /// <param name="ownerName">The name of the owner.</param>
    /// <param name="organizationName">The name of the organization; null for the owner's.</param>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The link.</returns>
    /// <exception cref="FleetServerException">The server refused the setup.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    /// <exception cref="InvalidOperationException">This instance is linked to that server already.</exception>
    public async Task<FleetServerLink> CreateServerAsync(Uri address, string code, string ownerName, string? organizationName, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireNoLink(address);
            var identity = Identity();
            using var client = new FleetServerClient(address, identity, Handler);
            var setup = await client.SetUpAsync(code, ownerName, _deviceName, organizationName, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var link = new FleetServerLink(FleetServerLinkStore.NewId(), FleetServerAddress.Format(address), setup.Organization.Id, setup.Organization.Name, identity.DeviceId,
                FleetDeviceKinds.Controller, FleetServerLinkStates.Approved, now, now);
            _store.Save(link);
            await EnsureConnectionsAsync(cancellationToken).ConfigureAwait(false);
            return link;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Joins a server with an enrollment token, as a controller device of the person who made the token: this desktop
    /// waits for one of that person's controller devices to approve it, then may approve others.
    /// </summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="token">The enrollment token.</param>
    /// <param name="cancellationToken">Cancels the enrollment.</param>
    /// <returns>The link, pending.</returns>
    /// <exception cref="FleetServerException">The server refused the token.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    /// <exception cref="InvalidOperationException">This instance is linked to that server already.</exception>
    public async Task<FleetServerLink> JoinAsync(Uri address, string token, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireNoLink(address);
            var identity = Identity();
            using var client = new FleetServerClient(address, identity, Handler);
            var device = await client.EnrollAsync(token, _deviceName, FleetDeviceKinds.Controller, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var link = new FleetServerLink(FleetServerLinkStore.NewId(), FleetServerAddress.Format(address), null, null, identity.DeviceId, device.Kind,
                FleetServerLinkStates.Pending, now, now);
            _store.Save(link);
            await EnsureConnectionsAsync(cancellationToken).ConfigureAwait(false);
            return link;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Leaves a server: the link and its outbox go, and the spaces linked to it are unlinked. The server keeps what it was given.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Whether there was such a link.</returns>
    public async Task<bool> LeaveAsync(string linkId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool removed;
        try
        {
            var link = _store.List().FirstOrDefault(candidate => candidate.Id == linkId);
            if (link is null) return false;
            foreach (var space in await _host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (link.IsServer(space.Settings.Fleet?.Server))
                    await _host.SpaceCatalog.UpdateSettingsAsync(space.Id, settings => settings with { Fleet = null }, cancellationToken).ConfigureAwait(false);
            }

            removed = _connections is { } connections ? await connections.RemoveAsync(linkId, cancellationToken).ConfigureAwait(false) : _store.Remove(linkId);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return removed;
    }

    /// <summary>Makes an enrollment token for a machine (<c>altanode join</c>); only a controller device may.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="lifetime">How long it works; null for an hour.</param>
    /// <param name="label">What it is for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token, shown once.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<EnrollmentTokenResponse> CreateEnrollmentTokenAsync(string linkId, TimeSpan? lifetime, string? label, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return await client.CreateEnrollmentTokenAsync(lifetime, label, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the devices of the organization of a server.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The devices.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<DeviceDto>> ListDevicesAsync(string linkId, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return await client.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the telemetry setting of the fleet of a server, with the headers only the server holds; an owner's controller device only.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The setting as the server holds it.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<TelemetrySettingsDto> GetTelemetryAsync(string linkId, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return await client.GetTelemetryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets the telemetry of the fleet of a server with a setting this device signs; the machines check the signature before they apply it.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="change">What the setting says.</param>
    /// <param name="serverHeaders">The headers only the server holds, for the server route; null for none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The setting as the server holds it now.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="ArgumentException">The setting does not hold.</exception>
    /// <exception cref="FleetDeviceListException">This device is not a controller device of the verified device list.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<TelemetrySettingsDto> SetTelemetryAsync(string linkId, FleetTelemetryChange change, IReadOnlyDictionary<string, string>? serverHeaders,
        CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return await client.SetTelemetryAsync(change, serverHeaders, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the updates of the fleet of a server: its versions, its policy, its recent campaigns; a controller device only.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The updates, and the machines of the organization with their versions.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<(UpdatesDto Updates, IReadOnlyList<MachineDto> Machines)> GetUpdatesAsync(string linkId, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        var updates = await client.GetUpdatesAsync(cancellationToken).ConfigureAwait(false);
        return (updates, await client.ListMachinesAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Starts an update campaign: this device reads the hashes of the feed and signs the manifest.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="change">The campaign.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The campaign.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="CodeAlta.Fleet.Update.UpdateException">The feed is not valid or lacks the version.</exception>
    /// <exception cref="FleetDeviceListException">This device is not a controller device of the verified device list.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server or the feed does not answer.</exception>
    public async Task<UpdateCampaignDto> StartUpdateAsync(string linkId, FleetUpdateCampaignChange change, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return await client.StartUpdateAsync(change, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels an update campaign before the server's step.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="campaignId">The campaign.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The campaign.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<UpdateCampaignDto> CancelUpdateAsync(string linkId, string campaignId, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return await client.CancelUpdateAsync(campaignId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets the update policy of the fleet of a server with a policy this device signs.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="change">The policy.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The policy as the server holds it.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="ArgumentException">The policy does not hold.</exception>
    /// <exception cref="FleetDeviceListException">This device is not a controller device of the verified device list.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<UpdatePolicyDto> SetUpdatePolicyAsync(string linkId, FleetUpdatePolicyChange change, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return await client.SetUpdatePolicyAsync(change, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Approves a pending device of a server, signing the entry of the device list that adds it with this device's key.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The device.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<DeviceDto> ApproveAsync(string linkId, string deviceId, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        var approved = await client.ApproveAsync(deviceId, cancellationToken).ConfigureAwait(false);
        // The approved device gets its copy of the metadata key at the next publication of this computer.
        Volatile.Read(ref _connections)?.Find(linkId)?.Connector?.RefreshKeys();
        return approved;
    }

    /// <summary>Revokes a device of a server, or refuses a pending one.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The device.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<DeviceDto> RevokeAsync(string linkId, string deviceId, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        var revoked = await client.RevokeAsync(deviceId, cancellationToken).ConfigureAwait(false);
        // A removal from the device list gives the person a new metadata key at the next publication, which the revoked device never gets.
        Volatile.Read(ref _connections)?.Find(linkId)?.Connector?.RefreshKeys();
        return revoked;
    }

    /// <summary>Links a space to a server, or unlinks it: its sessions that are not Private publish their facts there.</summary>
    /// <param name="spaceId">The space.</param>
    /// <param name="linkId">The link; null unlinks the space.</param>
    /// <param name="cancellationToken">Cancels before the file of the space is written.</param>
    /// <returns>The change of the space.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    public async Task<SpaceChange> LinkSpaceAsync(string spaceId, string? linkId, CancellationToken cancellationToken)
    {
        SpaceFleet? fleet = null;
        if (linkId is not null)
        {
            var link = _store.List().FirstOrDefault(candidate => candidate.Id == linkId) ?? throw new KeyNotFoundException($"No server link has the id '{linkId}'.");
            fleet = new SpaceFleet(link.Url, link.OrganizationId);
        }

        var change = await _host.SpaceCatalog.UpdateSettingsAsync(spaceId, settings => settings with { Fleet = fleet }, cancellationToken).ConfigureAwait(false);
        if (change.Succeeded)
        {
            Volatile.Read(ref _connections)?.Nudge();
            Changed?.Invoke();
        }

        return change;
    }

    /// <summary>Gets the link a space publishes to: the link of the server its settings name, or null.</summary>
    /// <param name="space">The space.</param>
    /// <returns>The link, or null.</returns>
    public FleetServerLink? LinkOf(SpaceDescriptor space)
    {
        ArgumentNullException.ThrowIfNull(space);
        return space.Settings.Fleet?.Server is { } server ? Connections.Select(static connection => connection.Link).FirstOrDefault(link => link.IsServer(server)) : null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_connections is { } connections)
            {
                connections.Changed -= OnChanged;
                await connections.DisposeAsync().ConfigureAwait(false);
            }

            if (_source is { } source) await source.DisposeAsync().ConfigureAwait(false);
            _identity?.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Under the gate: the connections of the links start with the first link, and read the file again after a change.
    private async Task EnsureConnectionsAsync(CancellationToken cancellationToken)
    {
        if (_connections is { } running)
        {
            await running.ReloadAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var identity = Identity();
        _source = new FleetHostPublicationSource(_host, _deviceName, _version, ["sessions", "moves", "console"]);
        var connections = new FleetServerConnections(new FleetServerConnectionsOptions
        {
            FleetRoot = _fleetRoot,
            Identity = identity,
            Client = new FleetClientInfo("desktop", _version, CodeAlta.Fleet.Channels.FleetChannelProtocol.OperatingSystemName),
            Spaces = _host.SpaceCatalog,
            Source = _source,
            WatchDirectory = true,
            Configure = _configure,
        });
        connections.Changed += OnChanged;
        Volatile.Write(ref _connections, connections);
        connections.Start();
        await connections.ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RequireNoLink(Uri address)
    {
        if (_store.Find(FleetServerAddress.Format(address)) is not null)
            throw new InvalidOperationException($"This computer is linked to {FleetServerAddress.Format(address)} already: leave it first.");
    }

    private FleetServerClient Client(string linkId)
    {
        var link = _store.List().FirstOrDefault(candidate => candidate.Id == linkId) ?? throw new KeyNotFoundException($"No server link has the id '{linkId}'.");
        // The entries this computer signs follow the device list as the anchor of the link (its pin and checkpoint) verified
        // it: the one of the connector of the link while it runs, so that one verifier holds the checkpoint.
        var identity = Identity();
        var devices = Volatile.Read(ref _connections)?.Find(linkId)?.Connector?.Devices is { } running && running.ServerAddress == link.Address && Handler is null
            ? running
            : new FleetOrganizationDevices(link.Address, identity, Path.Combine(_store.LinkFolder(link.Id), FleetOrganizationDevices.PinFileName), Handler);
        return new FleetServerClient(link.Address, identity, Handler) { DeviceList = devices };
    }

    // The key of this device: the one of pairing, made on first use.
    private DeviceIdentity Identity()
    {
        if (Volatile.Read(ref _identity) is { } identity) return identity;
        var loaded = DeviceIdentity.LoadOrCreate(_fleetRoot);
        if (Interlocked.CompareExchange(ref _identity, loaded, null) is { } other)
        {
            loaded.Dispose();
            return other;
        }

        return loaded;
    }

    private void OnChanged() => Changed?.Invoke();

    private static void Log(Exception exception, string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Fleet").Warn(exception, message);
    }
}
