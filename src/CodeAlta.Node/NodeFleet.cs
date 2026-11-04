using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using XenoAtom.Logging;

namespace CodeAlta.Node;

/// <summary>Where <c>altanode run</c> listens for direct session channels.</summary>
internal sealed record NodeListenOptions
{
    /// <summary>Gets whether the node listens at all. A node that does not listen has no pairing either.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets the address the listener binds: loopback by default, so that nothing is reachable from the network unasked.</summary>
    public IPAddress Address { get; init; } = IPAddress.Loopback;

    /// <summary>Gets the port (<see cref="FleetChannelProtocol.DefaultPort"/>; 0 picks a free one).</summary>
    public int Port { get; init; } = FleetChannelProtocol.DefaultPort;
}

/// <summary>
/// The fleet part of a running node: its device key, the devices it trusts, the node API over its runtime and the TCP
/// listener of direct channels. While it runs, <c>fleet/node-listener.json</c> of the state root tells the commands of the
/// same profile (<c>altanode pair</c>) where it listens.
/// </summary>
/// <remarks>
/// <c>altanode pair</c> is the owner's local control: it reads the node's device key from the profile (only the owner of
/// the profile can) and opens a channel to the node with it, from this machine; the node accepts its own key from this
/// machine only, and only for pairing offers. No token or second endpoint is needed: the key file is the credential.
/// </remarks>
internal sealed class NodeFleet : IAsyncDisposable
{
    /// <summary>The name of the file that says where a running node listens, in the fleet folder of its state root.</summary>
    public const string ListenerFileName = "node-listener.json";

    private readonly DeviceIdentity _identity;
    private readonly FleetDirectListener? _listener;
    private readonly string? _listenerFile;

    private NodeFleet(DeviceIdentity identity, FleetNodeService service, FleetDirectListener? listener, IReadOnlyList<string> addresses, string? listenerFile)
    {
        _identity = identity;
        Service = service;
        _listener = listener;
        Addresses = addresses;
        _listenerFile = listenerFile;
    }

    /// <summary>Gets the node API.</summary>
    public FleetNodeService Service { get; }

    /// <summary>Gets the device id of the node.</summary>
    public string DeviceId => _identity.DeviceId;

    /// <summary>Gets the end point the listener is bound to; null for a node that does not listen, which devices reach through its servers only.</summary>
    public IPEndPoint? EndPoint => _listener?.LocalEndPoint;

    /// <summary>Gets the addresses a pairing code names, those of this machine first.</summary>
    public IReadOnlyList<string> Addresses { get; }

    /// <summary>Starts the node API and its listener.</summary>
    /// <param name="node">The running node.</param>
    /// <param name="listen">Where to listen.</param>
    /// <param name="name">The name of the node.</param>
    /// <param name="version">The version of CodeAlta it runs.</param>
    /// <returns>The fleet part of the node.</returns>
    /// <exception cref="SocketException">The address cannot be bound.</exception>
    public static NodeFleet Start(NodeHost node, NodeListenOptions listen, string name, string version) => Start(node, listen, name, version, null);

    /// <summary>Starts the node API and its listener, with a way for the owner's local control to stop the node.</summary>
    /// <param name="node">The running node.</param>
    /// <param name="listen">Where to listen.</param>
    /// <param name="name">The name of the node.</param>
    /// <param name="version">The version of CodeAlta it runs.</param>
    /// <param name="requestStop">What stops the node gracefully (<c>altanode uninstall</c> on Windows); null for none.</param>
    /// <returns>The fleet part of the node.</returns>
    /// <exception cref="SocketException">The address cannot be bound.</exception>
    public static NodeFleet Start(NodeHost node, NodeListenOptions listen, string name, string version, Action? requestStop) => Start(node, listen, name, version, requestStop, null, null);

    /// <summary>
    /// Starts the node API and its listener, with the stop of the owner's local control, the versions it reports and the
    /// instruments of its requests and moves.
    /// </summary>
    /// <param name="node">The running node.</param>
    /// <param name="listen">Where to listen.</param>
    /// <param name="name">The name of the node.</param>
    /// <param name="version">The version of CodeAlta it runs.</param>
    /// <param name="requestStop">What stops the node gracefully (<c>altanode uninstall</c> on Windows); null for none.</param>
    /// <param name="updateStatus">What reads the versions of the node and its self-update (<c>node.info</c>, <c>control.status</c>); null for none.</param>
    /// <param name="metrics">The instruments of the node API; null for none.</param>
    /// <returns>The fleet part of the node.</returns>
    /// <exception cref="SocketException">The address cannot be bound.</exception>
    public static NodeFleet Start(NodeHost node, NodeListenOptions listen, string name, string version, Action? requestStop,
        Func<Fleet.Protocol.FleetUpdateStatus?>? updateStatus, Fleet.Telemetry.FleetMetrics? metrics)
        => Start(node, listen, name, version, requestStop, updateStatus, metrics, null);

    /// <summary>
    /// Starts the node API and its listener, with the stop of the owner's local control, the versions it reports, the
    /// instruments of its requests and moves, and the log and self-update its owner and its managing devices reach.
    /// </summary>
    /// <param name="node">The running node.</param>
    /// <param name="listen">Where to listen.</param>
    /// <param name="name">The name of the node.</param>
    /// <param name="version">The version of CodeAlta it runs.</param>
    /// <param name="requestStop">What stops the node gracefully (<c>altanode uninstall</c> on Windows); null for none.</param>
    /// <param name="updateStatus">What reads the versions of the node and its self-update (<c>node.info</c>, <c>control.status</c>); null for none.</param>
    /// <param name="metrics">The instruments of the node API; null for none.</param>
    /// <param name="management">The log and the self-update of <c>control.log</c> and <c>control.update</c>; null for none.</param>
    /// <returns>The fleet part of the node.</returns>
    /// <exception cref="SocketException">The address cannot be bound.</exception>
    public static NodeFleet Start(NodeHost node, NodeListenOptions listen, string name, string version, Action? requestStop,
        Func<Fleet.Protocol.FleetUpdateStatus?>? updateStatus, Fleet.Telemetry.FleetMetrics? metrics, IFleetNodeManagement? management)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(listen);
        var catalog = node.Host.CatalogOptions;
        var identity = DeviceIdentity.LoadOrCreate(catalog);
        FleetNodeService? service = null;
        FleetDirectListener? listener = null;
        try
        {
            IReadOnlyList<string> addresses = [];
            var trusted = new TrustedDeviceStore(catalog);
            service = new FleetNodeService(new FleetNodeServiceOptions
            {
                Host = node.Host, Identity = identity, TrustedDevices = trusted, Name = name, Version = version,
                AcceptsWork = () => node.AcceptsWork, PairingAddresses = () => addresses, RequestStop = requestStop, CloneRoot = node.Options.CloneRoot,
                Updating = () => node.AdmissionPaused, UpdateStatus = updateStatus, Metrics = metrics, Automations = node.Automations,
                Management = management,
                // A session that names an environment is admitted as a run of an automation is, once the node joined a server.
                AdmitEnvironment = (environment, runId, title, token) => node.Automations?.Environments is { } gate
                    ? gate.AdmitAsync(environment, runId, null, title, token)
                    : Task.FromResult<string?>($"This machine runs no session in the environment '{environment}': it joined no server."),
            });
            // An assigned automation runs while the device that assigned it is still paired, with the automate permission.
            if (node.Automations is { } automations)
            {
                automations.Devices = deviceId => trusted.List().FirstOrDefault(device => string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
                automations.DeviceId = identity.DeviceId;
            }
            string? file = null;
            // Without a listener the node API still serves the channels its servers relay.
            if (listen.Enabled)
            {
                listener = FleetDirectListener.Start(new IPEndPoint(listen.Address, listen.Port), service.ServeAsync);
                addresses = DescribeAddresses(listener.LocalEndPoint);
                file = Path.Combine(catalog.FleetRoot, ListenerFileName);
                WriteListenerFile(file, new NodeListenerFile { ProcessId = Environment.ProcessId, DeviceId = identity.DeviceId, Port = listener.LocalEndPoint.Port, Addresses = [.. addresses] });
            }

            // The moves this node took and a stop left unfinished end by its own decisions: a committed one is installed,
            // one never committed is undone. Those it gave away wait for the device that drove them to come back.
            _ = RecoverMovesAsync(service);
            return new NodeFleet(identity, service, listener, addresses, file);
        }
        catch
        {
            listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            service?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            identity.Dispose();
            throw;
        }
    }

    private static async Task RecoverMovesAsync(FleetNodeService service)
    {
        try
        {
            // The exports a stop interrupted before anything was offered are undone by this machine alone, first.
            foreach (var result in await Task.Run(() => service.MoveSender.Moves.RecoverExportsAsync(CancellationToken.None)).ConfigureAwait(false))
                Log(false, $"Move {result.Transaction?.MoveId} of session {result.Transaction?.SessionId}: {result.Status}. {string.Join(" ", result.Warnings)}");
            foreach (var result in await Task.Run(() => service.MoveReceiver.RecoverAsync(CancellationToken.None)).ConfigureAwait(false))
                Log(false, $"Move {result.Transaction?.MoveId} of session {result.Transaction?.SessionId}: {result.Status}. {string.Join(" ", result.Warnings)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            Log(true, $"The unfinished moves could not be recovered: {exception.Message}");
        }
    }

    private static void Log(bool warning, string message)
    {
        if (!LogManager.IsInitialized) return;
        var logger = LogManager.GetLogger("CodeAlta.Node");
        if (warning) logger.Warn(message);
        else logger.Info(message);
    }

    /// <summary>Reads where the node of a profile listens, as it wrote it.</summary>
    /// <param name="catalog">The catalog of the profile.</param>
    /// <returns>The listener, or null when no node wrote one.</returns>
    public static NodeListenerFile? ReadListenerFile(CatalogOptions catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(catalog.FleetRoot, ListenerFileName)), NodeFleetJsonContext.Default.NodeListenerFile);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    // The addresses of a pairing code: loopback when the node listens there; otherwise this machine's name and its
    // addresses on the networks that are up, then loopback, for the owner's local control.
    internal static IReadOnlyList<string> DescribeAddresses(IPEndPoint endPoint)
    {
        var port = endPoint.Port;
        var bound = endPoint.Address.IsIPv4MappedToIPv6 ? endPoint.Address.MapToIPv4() : endPoint.Address;
        if (IPAddress.IsLoopback(bound)) return [FleetDirectConnector.Format(bound, port)];
        var addresses = new List<string>();
        if (bound.Equals(IPAddress.Any) || bound.Equals(IPAddress.IPv6Any))
        {
            try
            {
                foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                    {
                        var address = unicast.Address;
                        if (address.AddressFamily == AddressFamily.InterNetwork
                            || address.AddressFamily == AddressFamily.InterNetworkV6 && bound.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal)
                            addresses.Add(FleetDirectConnector.Format(address, port));
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // Without the list of networks, the name of the machine stays.
            }

            var host = Dns.GetHostName();
            if (PairingPayload.IsValidAddress($"{host}:{port}")) addresses.Insert(0, $"{host}:{port}");
            addresses.Add(FleetDirectConnector.Format(IPAddress.Loopback, port));
        }
        else
        {
            addresses.Add(FleetDirectConnector.Format(bound, port));
        }

        return [.. addresses.Distinct(StringComparer.OrdinalIgnoreCase).Where(PairingPayload.IsValidAddress).Take(PairingPayload.MaxAddresses)];
    }

    private static void WriteListenerFile(string path, NodeListenerFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(file, NodeFleetJsonContext.Default.NodeListenerFile));
        File.Move(staging, path, overwrite: true);
    }

    /// <summary>Stops the listener and the node API, and removes the listener file.</summary>
    /// <returns>A task that completes once they stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_listenerFile is not null)
        {
            try { File.Delete(_listenerFile); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        await Service.DisposeAsync().ConfigureAwait(false);
        _identity.Dispose();
    }
}

/// <summary>Where a running node listens (<see cref="NodeFleet.ListenerFileName"/>).</summary>
internal sealed class NodeListenerFile
{
    /// <summary>Gets or sets the process of the node.</summary>
    public int ProcessId { get; set; }

    /// <summary>Gets or sets the device id of the node.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets the port.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets the addresses of its pairing codes.</summary>
    public List<string> Addresses { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(NodeListenerFile))]
internal sealed partial class NodeFleetJsonContext : JsonSerializerContext;

/// <summary>
/// The CodeAlta Servers a running node joined (<c>altanode join</c>): one control connection each, which publishes the
/// presence and inventory of the machine and the facts of the sessions of the spaces linked to that server. Nothing
/// connects for a profile that joined none; a link <c>join</c> adds while the node runs is taken within seconds.
/// </summary>
internal sealed class NodeServers : IAsyncDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly Fleet.Server.FleetHostPublicationSource _source;
    private readonly Fleet.Server.FleetServerConnections _connections;
    private readonly Fleet.Server.FleetHostStatisticsSource _statistics;

    private NodeServers(DeviceIdentity identity, Fleet.Server.FleetHostPublicationSource source, Fleet.Server.FleetServerConnections connections,
        Fleet.Server.FleetHostStatisticsSource statistics, NodeFleetSecrets secrets)
    {
        _identity = identity;
        _source = source;
        _connections = connections;
        _statistics = statistics;
        Secrets = secrets;
    }

    /// <summary>Gets the connections to the servers.</summary>
    public Fleet.Server.FleetServerConnections Connections => _connections;

    /// <summary>Gets the values the servers give this node, kept in the protected store of the machine.</summary>
    public NodeFleetSecrets Secrets { get; }

    /// <summary>Starts the connections of the servers of the profile.</summary>
    /// <param name="node">The running node.</param>
    /// <param name="name">The name of the node.</param>
    /// <param name="version">The version of CodeAlta it runs.</param>
    /// <param name="configure">What changes the options of each connector, for tests; null for none.</param>
    /// <returns>The connections; dispose them before the node stops.</returns>
    public static NodeServers Start(NodeHost node, string name, string version,
        Func<Fleet.Server.FleetConnectorOptions, Fleet.Server.FleetConnectorOptions>? configure = null) => Start(node, null, name, version, configure);

    /// <summary>Starts the connections of the servers of the profile, which relay session channels to the node API.</summary>
    /// <param name="node">The running node.</param>
    /// <param name="service">The node API that serves the relayed channels; null declines every relay.</param>
    /// <param name="name">The name of the node.</param>
    /// <param name="version">The version of CodeAlta it runs.</param>
    /// <param name="configure">What changes the options of each connector, for tests; null for none.</param>
    /// <returns>The connections; dispose them before the node stops.</returns>
    public static NodeServers Start(NodeHost node, FleetNodeService? service, string name, string version,
        Func<Fleet.Server.FleetConnectorOptions, Fleet.Server.FleetConnectorOptions>? configure = null) => Start(node, service, name, version, configure, null);

    /// <summary>Starts the connections of the servers of the profile, which relay session channels to the node API and push the fleet's telemetry setting.</summary>
    /// <param name="node">The running node.</param>
    /// <param name="service">The node API that serves the relayed channels; null declines every relay.</param>
    /// <param name="name">The name of the node.</param>
    /// <param name="version">The version of CodeAlta it runs.</param>
    /// <param name="configure">What changes the options of each connector, for tests; null for none.</param>
    /// <param name="telemetry">The telemetry of the node, which verifies and applies the setting a server pushes; null ignores it.</param>
    /// <returns>The connections; dispose them before the node stops.</returns>
    public static NodeServers Start(NodeHost node, FleetNodeService? service, string name, string version,
        Func<Fleet.Server.FleetConnectorOptions, Fleet.Server.FleetConnectorOptions>? configure, NodeTelemetry? telemetry)
        => Start(node, service, name, version, configure, telemetry, null);

    /// <summary>
    /// Starts the connections of the servers of the profile, which relay session channels to the node API, push the
    /// fleet's telemetry setting and ask the updates of the fleet.
    /// </summary>
    /// <param name="node">The running node.</param>
    /// <param name="service">The node API that serves the relayed channels; null declines every relay.</param>
    /// <param name="name">The name of the node.</param>
    /// <param name="version">The version of CodeAlta it runs.</param>
    /// <param name="configure">What changes the options of each connector, for tests; null for none.</param>
    /// <param name="telemetry">The telemetry of the node, which verifies and applies the setting a server pushes; null ignores it.</param>
    /// <param name="updates">What takes the updates the servers ask and reports where the node stands; null ignores them.</param>
    /// <returns>The connections; dispose them before the node stops.</returns>
    public static NodeServers Start(NodeHost node, FleetNodeService? service, string name, string version,
        Func<Fleet.Server.FleetConnectorOptions, Fleet.Server.FleetConnectorOptions>? configure, NodeTelemetry? telemetry, Fleet.Server.FleetUpdateAgent? updates)
    {
        ArgumentNullException.ThrowIfNull(node);
        var catalog = node.Host.CatalogOptions;
        var identity = DeviceIdentity.LoadOrCreate(catalog);
        var source = new Fleet.Server.FleetHostPublicationSource(node.Host, name, version, ["sessions", "moves"]);
        // The statistics the node counts (its Statistics plugin), published with the facts of its sessions.
        var statistics = Fleet.Server.FleetHostStatisticsSource.ForHost(node.Host);
        NodeFleetSecrets? secrets = null;
        var connections = new Fleet.Server.FleetServerConnections(new Fleet.Server.FleetServerConnectionsOptions
        {
            FleetRoot = catalog.FleetRoot,
            Identity = identity,
            Client = new CodeAlta.Fleet.Protocol.FleetClientInfo("node", version, FleetChannelProtocol.OperatingSystemName),
            Spaces = node.Host.SpaceCatalog,
            Source = source,
            Statistics = statistics,
            Configure = configure,
            AcceptRelay = service is null ? null : (connector, _, stream, token) => ServeRelayAsync(service, connector, stream, token),
            TelemetrySettings = telemetry is null ? null : telemetry.AcceptAsync,
            Metrics = telemetry?.Metrics,
            Updates = updates,
            // The automations a server gives this node, verified against its device list and opened with the key of this node.
            Automations = node.Automations is { } given ? new NodeServerAutomations(given, identity) : null,
            // The values the servers seal for this node: read again as soon as a server says they changed.
            SecretsChanged = linkId => secrets?.Changed(linkId),
        });
        secrets = new NodeFleetSecrets(node.Host, connections);
        // A run that names an environment is admitted by its rules and its approval, as the servers verified them.
        if (node.Automations is { } admitted) admitted.Environments = new NodeEnvironmentGate(secrets, catalog);
        if (node.Automations is { } automations)
        {
            automations.DeviceId ??= identity.DeviceId;
            Fleet.Server.FleetConnector? ConnectorOf(string linkId) => connections.Find(linkId)?.Connector;
            automations.ServerDevices = linkId => ConnectorOf(linkId)?.Devices.Current;
            automations.ServerClaim = (linkId, claim, token) => ConnectorOf(linkId) is { } connector
                ? connector.ClaimAutomationAsync(claim, token)
                : Task.FromResult<CodeAlta.Fleet.Protocol.FleetAutomationClaimResult?>(null);
            automations.ServerReport = (linkId, facts, token) => ConnectorOf(linkId) is { } connector ? connector.ReportAutomationRunsAsync(facts, token) : Task.FromResult(false);
        }

        connections.Start();
        secrets.Start();
        return new NodeServers(identity, source, connections, statistics, secrets);
    }

    // The device list is read again for each relay, so that a device removed from it is refused at once; when the server
    // does not answer, the list last verified is kept.
    private static async Task ServeRelayAsync(FleetNodeService service, Fleet.Server.FleetConnector connector, Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            await connector.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Fleet.Server.FleetServerException or HttpRequestException or IOException or TaskCanceledException)
        {
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Node").Info($"The device list could not be read again: {exception.Message}");
        }
        catch (Fleet.Protocol.FleetDeviceListException exception)
        {
            // A list that does not verify is no ground to trust anyone through this server.
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Node").Warn($"A relayed channel was refused: {exception.Message}");
            await stream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await service.ServeRelayedAsync(stream, connector.Devices.Trust, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Secrets.DisposeAsync().ConfigureAwait(false);
        await _connections.DisposeAsync().ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
        _statistics.Dispose();
        _identity.Dispose();
    }
}
