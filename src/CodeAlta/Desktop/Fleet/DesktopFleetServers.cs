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
internal sealed partial class DesktopFleetServers : IAsyncDisposable
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
    private FleetHostStatisticsSource? _statistics;
    private readonly CodeAlta.Catalog.SessionMoves.RepositoryLocationCache _repositories = new();
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

    /// <summary>
    /// Finds the way to give automations to a machine through a server that lists it: a connection online to a server
    /// of an organization this computer belongs to; the caller disposes it.
    /// </summary>
    /// <param name="machineId">The device of the machine.</param>
    /// <returns>The route, or null when no server this computer is connected to lists the machine.</returns>
    public DesktopServerAutomationRoute? AutomationRoute(string machineId)
    {
        foreach (var connection in Connections)
        {
            if (connection.Connector is not { State: FleetConnectorState.Online } connector || !connector.Directory.Machines.TryGetValue(machineId, out var machine)
                || machine.Kind != FleetDeviceKinds.Node)
                continue;
            if ((connection.Link.OrganizationId ?? connector.Devices.Current?.OrganizationId) is not { } organization) continue;
            return new DesktopServerAutomationRoute(connection.Link.Id, organization, machine.Inventory.Name, connector.Devices, Identity(), Client(connection.Link.Id));
        }

        return null;
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
    public Task<FleetServerLink> CreateServerAsync(Uri address, string code, string ownerName, string? organizationName, CancellationToken cancellationToken)
        => CreateServerAsync(address, address, code, ownerName, organizationName, cancellationToken);

    /// <summary>
    /// Sets up a server through another address than the one its machines use: a port forwarded over SSH to its loopback,
    /// from which alone it accepts its setup. The link keeps <paramref name="address"/>.
    /// </summary>
    /// <param name="address">The address this computer and the machines reach the server at.</param>
    /// <param name="setupAddress">The address its setup goes to.</param>
    /// <param name="code">The one-time setup code.</param>
    /// <param name="ownerName">The name of the owner.</param>
    /// <param name="organizationName">The name of the organization; null for the server's default.</param>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The link, approved, its device list pinned to the first entry this desktop signed.</returns>
    /// <exception cref="FleetServerException">The server refused the setup.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    /// <exception cref="IOException">The pin of the device list cannot be written: no link is made.</exception>
    public async Task<FleetServerLink> CreateServerAsync(Uri address, Uri setupAddress, string code, string ownerName, string? organizationName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setupAddress);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireNoLink(address);
            var identity = Identity();
            var id = FleetServerLinkStore.NewId();
            var folder = _store.LinkFolder(id);
            SetupResponse setup;
            try
            {
                // The first entry this desktop signs, and the organization the setup made, anchor the link before it exists:
                // the connector then refuses a list of the server's making, from its first read on.
                using var client = new FleetServerClient(setupAddress, identity, Handler);
                setup = await client.SetUpAsync(code, ownerName, _deviceName, organizationName, Path.Combine(folder, FleetOrganizationDevices.PinFileName), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Nothing reads a folder without its link. */ }
                throw;
            }

            var now = DateTimeOffset.UtcNow;
            var link = new FleetServerLink(id, FleetServerAddress.Format(address), setup.Organization.Id, setup.Organization.Name, identity.DeviceId,
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

    /// <summary>
    /// Leaves a server: the spaces linked to it are unlinked, the server withdraws what this computer published there
    /// (facts and sealed metadata, statistics, inventory), its copies of keys and the automations given to it, and the
    /// link goes with its outbox and the anchor of its device list. A server that does not answer keeps what it holds.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Whether there was such a link, and what the server withdrew; null when it did not answer or refused.</returns>
    public async Task<(bool Removed, LeaveResponse? Withdrawn)> LeaveAsync(string linkId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool removed;
        LeaveResponse? withdrawn = null;
        try
        {
            var link = _store.List().FirstOrDefault(candidate => candidate.Id == linkId);
            if (link is null) return (false, null);
            foreach (var space in await _host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (link.IsServer(space.Settings.Fleet?.Server))
                    await _host.SpaceCatalog.UpdateSettingsAsync(space.Id, settings => settings with { Fleet = null }, cancellationToken).ConfigureAwait(false);
            }

            if (link.State != FleetServerLinkStates.Revoked)
            {
                try
                {
                    using var client = Client(linkId);
                    withdrawn = await client.LeaveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is FleetServerException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    Log(exception, $"{link.Url} did not withdraw what this computer published there");
                }
            }

            // The connector and its keys go with the link, and the folder of the link with its anchor.
            removed = _connections is { } connections ? await connections.RemoveAsync(linkId, cancellationToken).ConfigureAwait(false) : _store.Remove(linkId);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return (removed, withdrawn);
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

    /// <summary>
    /// Sums the statistics the machines of this computer's person published to a server, with the names only this computer can
    /// give: the projects whose key (a blind index of the person's metadata key) it computes for its own projects.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="request">The query.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The sums, and the names of the project keys this computer knows.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused the query.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<(StatisticsQueryResponse Response, IReadOnlyDictionary<string, string> ProjectNames)> QueryStatisticsAsync(string linkId, StatisticsQueryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var client = Client(linkId);
        var response = await client.QueryStatisticsAsync(request, cancellationToken).ConfigureAwait(false);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        // A project key is the blind index of the key of its space: every key this computer holds may name one of its projects.
        if (Volatile.Read(ref _connections)?.Find(linkId)?.Connector?.MetadataKeys?.Held is { Count: > 0 } keys)
        {
            foreach (var project in await _host.ProjectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await FleetProjectKeys.IdentityAsync(project, _repositories, cancellationToken).ConfigureAwait(false) is not { } identity) continue;
                foreach (var key in keys) names.TryAdd(FleetProjectKeys.Of(key, identity), string.IsNullOrWhiteSpace(project.DisplayName) ? project.Name : project.DisplayName);
            }
        }

        return (response, names);
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

    /// <summary>
    /// Approves a pending device of a server once the person confirmed its class and that it shows the same words, signing
    /// the entry of the device list that adds its key as that class with this device's key; it gets the keys of the person only then.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="kind">The class the person approves it as (<see cref="FleetDeviceKinds.Controller"/> or <see cref="FleetDeviceKinds.Node"/>).</param>
    /// <param name="confirmedWords">The words of its key the person compared; null when none were compared.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The device.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not a class of device.</exception>
    /// <exception cref="FleetDeviceListException">The list does not verify, the server lists the device as another class, or the key is not the one whose words were confirmed: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<DeviceDto> ApproveAsync(string linkId, string deviceId, string kind, string? confirmedWords, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        var approved = await client.ApproveAsync(deviceId, kind, confirmedWords, cancellationToken).ConfigureAwait(false);
        // The approved device gets its copy of the metadata key at the next publication of this computer.
        Volatile.Read(ref _connections)?.Find(linkId)?.Connector?.RefreshKeys();
        return approved;
    }

    /// <summary>
    /// Approves at a server a machine this computer paired with, without a person: the device of the key this computer
    /// pinned for it, as an execution node only. What the machine or the server answers chooses neither the key nor the
    /// class, so a machine never brings a controller in this way.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="machine">The fingerprint of the key of the machine, as pairing pinned it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The device.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="ArgumentException"><paramref name="machine"/> is empty.</exception>
    /// <exception cref="FleetDeviceListException">The list does not verify, or the server lists that device as a controller or with another key: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused, for example no such device waits.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<DeviceDto> ApproveMachineAsync(string linkId, DeviceFingerprint machine, CancellationToken cancellationToken)
    {
        if (machine.IsEmpty) throw new ArgumentException("The machine has no pinned key.", nameof(machine));
        using var client = Client(linkId);
        var approved = await client.ApproveAsync(DeviceIdentity.DeviceIdOf(machine), FleetDeviceKinds.Node, machine, cancellationToken).ConfigureAwait(false);
        Volatile.Read(ref _connections)?.Find(linkId)?.Connector?.RefreshKeys();
        return approved;
    }

    /// <summary>
    /// Makes the recovery key of this computer's person at a server (<c>doc/fleet/security-and-privacy.md</c>, keys): its
    /// words are given once, to be shown to the person, and never kept. One the person had before stops working.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The words of the recovery key.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetDeviceListException">This device is not a controller device of the verified device list.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<string> CreateRecoveryKeyAsync(string linkId, CancellationToken cancellationToken)
    {
        using var key = FleetRecoveryKey.Generate();
        using (var client = Client(linkId)) await client.AddRecoveryKeyAsync(key, cancellationToken).ConfigureAwait(false);
        // The recovery key gets its copy of the keys at the next publication of this computer.
        Volatile.Read(ref _connections)?.Find(linkId)?.Connector?.RefreshKeys();
        Changed?.Invoke();
        return key.Words;
    }

    /// <summary>Gets whether the person of this computer has a recovery key at a server, as its verified device list says; null before the list was read.</summary>
    /// <param name="link">The link.</param>
    /// <returns>Whether a recovery key of this computer's person is in the list.</returns>
    public static bool? HasRecoveryKey(FleetServerConnection link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.Connector?.Devices.Current is not { } list || !list.Members.TryGetValue(link.Link.DeviceId, out var self)) return null;
        // The first device names its person by the first entry it signs: before any, the list holds no recovery key of its person.
        return self.OwnerId is { } owner && list.Members.Values.Any(member => member.Kind == FleetDeviceKinds.Recovery && member.OwnerId == owner);
    }

    /// <summary>
    /// Recovers a server with the recovery key of a person whose devices are all lost: the key signs this computer in as a
    /// controller device of that person and gives it their keys; the link is made, approved.
    /// </summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="words">The words of the recovery key.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The link.</returns>
    /// <exception cref="ArgumentException">The words are not 32 words of the list.</exception>
    /// <exception cref="FleetRecoveryException">The words are not the recovery key of a person of the server.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not start with the anchor the recovery key sealed.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    /// <exception cref="InvalidOperationException">This instance is linked to that server already.</exception>
    public async Task<FleetServerLink> RecoverAsync(Uri address, string words, CancellationToken cancellationToken)
    {
        if (!FleetRecoveryKey.TryParse(words, out var key)) throw new ArgumentException("A recovery key is 32 words of its list.", nameof(words));
        using var recovery = key;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireNoLink(address);
            var identity = Identity();
            var id = FleetServerLinkStore.NewId();
            var folder = _store.LinkFolder(id);
            FleetRecoveryResult result;
            try
            {
                result = await FleetRecovery.RecoverAsync(address, recovery, identity, _deviceName, Path.Combine(folder, FleetOrganizationDevices.PinFileName), Handler, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Nothing reads a folder without its link. */ }
                throw;
            }

            var now = DateTimeOffset.UtcNow;
            var link = new FleetServerLink(id, FleetServerAddress.Format(address), result.OrganizationId, result.OrganizationName, identity.DeviceId, FleetDeviceKinds.Controller,
                FleetServerLinkStates.Approved, now, now);
            _store.Save(link);
            await EnsureConnectionsAsync(cancellationToken).ConfigureAwait(false);
            return link;
        }
        finally
        {
            _gate.Release();
        }
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
            _statistics?.Dispose();
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
        _statistics = FleetHostStatisticsSource.ForHost(_host);
        var connections = new FleetServerConnections(new FleetServerConnectionsOptions
        {
            FleetRoot = _fleetRoot,
            Identity = identity,
            Client = new FleetClientInfo("desktop", _version, CodeAlta.Fleet.Channels.FleetChannelProtocol.OperatingSystemName),
            Spaces = _host.SpaceCatalog,
            Source = _source,
            Statistics = _statistics,
            WatchDirectory = true,
            Configure = _configure,
            // A budget reached, a run that waits, a notification: the pages and the Fleet button read them again.
            BudgetsChanged = _ => OnChanged(),
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
