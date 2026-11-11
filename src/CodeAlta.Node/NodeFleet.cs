using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Catalog;
using CodeAlta.Core.IO;
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
    private readonly Fleet.ModelServerProviderRefresh _providerModels;
    // The watch of the configuration file, which applies a provider edited by hand.
    private IDisposable? _configurationWatch;

    private NodeFleet(DeviceIdentity identity, FleetNodeService service, FleetDirectListener? listener, IReadOnlyList<string> addresses, string? listenerFile,
        Fleet.ModelServerProviderRefresh providerModels)
    {
        _identity = identity;
        Service = service;
        _listener = listener;
        Addresses = addresses;
        _listenerFile = listenerFile;
        _providerModels = providerModels;
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

    /// <summary>
    /// Whether a provider of a credential kind serves only the owner's own work: all but the key of an account. Fails closed:
    /// credentials CodeAlta cannot tell (a provider of a plugin, a key read from nowhere) are not taken for an account's.
    /// </summary>
    /// <param name="kind">The credential kind of the provider.</param>
    /// <returns>Whether the sessions other people start here are refused it.</returns>
    internal static bool ServesOwnerOnly(CodeAlta.Automations.AutomationCredentialKind kind) => kind != CodeAlta.Automations.AutomationCredentialKind.Account;

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
        => Start(node, listen, name, version, requestStop, updateStatus, metrics, management, null);

    /// <summary>Starts the node API with the model host of the machine too.</summary>
    /// <param name="node">The running node.</param>
    /// <param name="listen">Where to listen.</param>
    /// <param name="name">The name of the node.</param>
    /// <param name="version">The version of CodeAlta it runs.</param>
    /// <param name="requestStop">What stops the node gracefully; null for none.</param>
    /// <param name="updateStatus">What reads the versions of the node and its self-update; null for none.</param>
    /// <param name="metrics">The instruments of the node API; null for none.</param>
    /// <param name="management">The log and the self-update of <c>control.log</c> and <c>control.update</c>; null for none.</param>
    /// <param name="modelServers">The model host of the machine (llama.cpp); null for none.</param>
    /// <returns>The fleet part of the node.</returns>
    /// <exception cref="SocketException">The address cannot be bound.</exception>
    public static NodeFleet Start(NodeHost node, NodeListenOptions listen, string name, string version, Action? requestStop,
        Func<Fleet.Protocol.FleetUpdateStatus?>? updateStatus, Fleet.Telemetry.FleetMetrics? metrics, IFleetNodeManagement? management,
        CodeAlta.ModelServers.ModelHost? modelServers)
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
            var providers = new NodeProviders(node.Host, node.ModelCatalog) { PluginProviderTypes = node.PluginProviderTypes };
            // The programs the providers run follow the versions this release of CodeAlta pins, in the background.
            _ = providers.FollowPinnedVersionsAsync(CancellationToken.None);
            service = new FleetNodeService(new FleetNodeServiceOptions
            {
                Host = node.Host, Identity = identity, TrustedDevices = trusted, Name = name, Version = version,
                AcceptsWork = () => node.AcceptsWork, PairingAddresses = () => addresses, RequestStop = requestStop, CloneRoot = node.Options.CloneRoot,
                Updating = () => node.AdmissionPaused, UpdateStatus = updateStatus, Metrics = metrics, Automations = node.Automations,
                Management = management,
                ModelServers = modelServers,
                // A personal plan serves the owner's own work only: the sessions other people start here keep to account credentials.
                IsPersonalProvider = provider => ServesOwnerOnly(node.Automations?.CredentialKindOf(provider) ?? NodeAutomations.Classify(catalog, provider)),
                // Its providers, their readiness, a key typed on the desktop kept in the secrets of the machine, and their device-code sign-in.
                Providers = providers,
                // Whether the node still runs after a restart or a logout, for the page of the machine.
                ServiceStatus = () => NodeServiceStatus.Read(node.Options.Profile),
                // The sign-ins the node keeps for itself, made from the desktop: the node starts each one and keeps its tokens.
                SignIns = new NodeSignIns(node.Host, providers) { PluginProviderTypes = node.PluginProviderTypes },
                // A session that names an environment is admitted as a run of an automation is, once the node joined a server.
                // What the session is about stays here: the approval it asks names the run only.
                AdmitEnvironment = (environment, runId, _, token) => node.Automations?.Environments is { } gate
                    ? gate.AdmitAsync(environment, runId, null, token)
                    : Task.FromResult(CodeAlta.Orchestration.Runtime.EnvironmentAdmissionResult.Refused($"This machine runs no session in the environment '{environment}': it joined no server.")),
                // A prompt sent through the node API starts a run the budgets of the machine admit first, once it joined a server.
                AdmitBudget = (run, held, token) => node.Budgets is { } budgets
                    ? budgets.AdmitAsync(run, held, token)
                    : Task.FromResult(Fleet.Server.FleetBudgetAdmission.Admitted),
            });
            // An assigned automation runs while the device that assigned it is still paired, with the automate permission.
            if (node.Automations is { } automations)
            {
                automations.Devices = deviceId => trusted.List().FirstOrDefault(device => string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
                automations.DeviceId = identity.DeviceId;
                // A run that is not positively its owner's work is recorded as such before its session attaches: none of the owner's values.
                automations.RecordNotOwnerWork = service.RecordUnattendedWork;
                // A run takes a seat of the machine after its budgets, and may wait for its turn.
                automations.Seats = service.Seats;
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
            var fleet = new NodeFleet(identity, service, listener, addresses, file, new Fleet.ModelServerProviderRefresh(node.Host, modelServers));
            fleet._configurationWatch = providers.WatchConfiguration();
            return fleet;
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

    // The addresses of a pairing code, as a desktop that other computers pair with describes its own.
    internal static IReadOnlyList<string> DescribeAddresses(IPEndPoint endPoint) => FleetListenAddresses.Describe(endPoint);

    private static void WriteListenerFile(string path, NodeListenerFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SafeFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(file, NodeFleetJsonContext.Default.NodeListenerFile));
    }

    /// <summary>Stops the listener and the node API, and removes the listener file.</summary>
    /// <returns>A task that completes once they stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        _configurationWatch?.Dispose();
        await _providerModels.DisposeAsync().ConfigureAwait(false);
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
        var source = new Fleet.Server.FleetHostPublicationSource(node.Host, name, version, ["sessions", "moves"]) {
            PersonOf = service is null ? null : service.PersonOfSession,
            PersonOrganizationOf = service is null ? null : service.OrganizationOfSession,
        };
        // The providers that run with the key of an account, which the server checks automations of a team or the organization against.
        if (node.Automations is { } classifier) source.IsAccountProvider = provider => classifier.CredentialKindOf(provider) == CodeAlta.Automations.AutomationCredentialKind.Account;
        // The statistics the node counts (its Statistics plugin), published with the facts of its sessions.
        var statistics = Fleet.Server.FleetHostStatisticsSource.ForHost(node.Host);
        NodeFleetSecrets? secrets = null;
        NodeBudgets? budgets = null;
        var health = new NodeHealth(node)
        {
            AccessRequests = service is null ? null : () => [.. service.People.Read().Asks.Select(static ask => ask.PersonId)],
        };
        // A provider the fleet gives, rotates or takes away is run as it is now at once: none keeps its old key, none runs once taken away.
        var providers = new NodeScopeProviders(node.Host.CatalogOptions, new CodeAlta.Catalog.Secrets.LocalSecretStore(node.Host.CatalogOptions.GlobalRoot), identity)
        {
            ProvidersChanged = new NodeProviders(node.Host, node.ModelCatalog).Reconcile,
        };
        Fleet.Server.FleetServerConnections? connections = null;
        connections = new Fleet.Server.FleetServerConnections(new Fleet.Server.FleetServerConnectionsOptions
        {
            FleetRoot = catalog.FleetRoot,
            Identity = identity,
            Client = new CodeAlta.Fleet.Protocol.FleetClientInfo("node", version, FleetChannelProtocol.OperatingSystemName),
            Spaces = node.Host.SpaceCatalog,
            Source = source,
            Statistics = statistics,
            Configure = configure,
            AcceptRelay = service is null ? null : (connector, _, stream, token) => service.ServeRelayedAsync(stream, connector, token),
            TelemetrySettings = telemetry is null ? null : telemetry.AcceptAsync,
            Metrics = telemetry?.Metrics,
            Updates = updates,
            // The automations a server gives this node, verified against its device list and opened with the key of this node.
            Automations = node.Automations is { } given ? new NodeServerAutomations(given, identity) : null,
            // The values the servers seal for this node: read again as soon as a server says they changed.
            SecretsChanged = linkId => secrets?.Changed(linkId),
            // The budgets and holds of the owner: the runs that wait read them again at once.
            BudgetsChanged = _ => budgets?.Changed(),
            // A newer access document: the sessions people it suspends started on this machine stop, their channels close.
            // A machine given to another owner also ends the pairings and the people its former owner gave it.
            AccessChanged = service is null ? null : (linkId, access) => { _ = ApplyAccessAsync(service, access, connections?.Find(linkId)?.Connector?.Devices.Current); },
            // A newer policy: a stop of the kill switch ends the work of the fleet here and refuses more, until a version lifts it.
            PolicyChanged = (_, _) => { _ = ApplyPolicyAsync(service, node.Automations); },
            // Access read again: the providers of the teams and of the organization this machine runs with, their keys opened here.
            AccessRead = linkId => _ = RefreshProvidersAsync(providers, connections, linkId),
            // The problems of the machine travel with its heartbeat; the server tells its owner of those that last.
            Health = health.Read,
            // The seats of the machine: the runs that wait and its limit travel with its presence.
            Load = service is null ? null : service.ReadLoad,
        });
        // The people the signed access document of an organization grants the content of a session of this machine read it here.
        if (service is not null)
            service.ContentGrants = (organization, person, session) => organization is not null && connections.Connections.Any(connection =>
                connection.Link.OrganizationId == organization && connection.Connector?.Access.Current?.Reads(session, person, DateTimeOffset.UtcNow) == true);
        // A person named on this machine before organizations were kept is of its organization while it is linked to one only.
        if (service is not null)
            service.People.SoleOrganization = () => connections.Connections.Select(static connection => connection.Link.OrganizationId).OfType<string>().Distinct(StringComparer.Ordinal)
                .ToArray() is [var sole] ? sole : null;
        secrets = new NodeFleetSecrets(node.Host, connections);
        budgets = new NodeBudgets(connections, source, statistics);
        node.Budgets = budgets;
        if (node.Automations is { } limited) limited.Budgets = budgets;
        // A run that names an environment is admitted by its rules and its approval, as the servers verified them.
        if (node.Automations is { } admitted) admitted.Environments = new NodeEnvironmentGate(secrets, catalog);
        // An admission kept across a restart holds while the rules this node verified are the version that admitted it.
        node.Host.Identities.Environments.Validate = secrets.Holds;
        if (node.Automations is { } automations)
        {
            automations.DeviceId ??= identity.DeviceId;
            Fleet.Server.FleetConnector? ConnectorOf(string linkId) => connections.Find(linkId)?.Connector;
            automations.ServerDevices = linkId => ConnectorOf(linkId)?.Devices.Current;
            // The tools the policy turns off for the work of the fleet the node API started, at every attachment of its sessions.
            if (service is not null) automations.WorkToolsOff = service.PolicyToolsOffAsync;
            automations.ServerAccess = linkId => ConnectorOf(linkId)?.Access.Current;
            automations.ServerAccesses = () => connections.Connections.Select(static connection => connection.Connector?.Access.Current);
            automations.ServerClaim = (linkId, claim, token) => ConnectorOf(linkId) is { } connector
                ? connector.ClaimAutomationAsync(claim, token)
                : Task.FromResult<CodeAlta.Fleet.Protocol.FleetAutomationClaimResult?>(null);
            automations.ServerReport = (linkId, facts, token) => ConnectorOf(linkId) is { } connector ? connector.ReportAutomationRunsAsync(facts, token) : Task.FromResult(false);
        }
        else if (service is not null)
        {
            // A node that runs no automation: the work of the fleet of its node API keeps the tools the policy turns off.
            node.Host.RuntimeService.ToolPatternGate = service.PolicyToolsOffAsync;
        }

        // The signed policies of the servers, as this node verified them and kept them: read at each request of the fleet.
        var guard = new Fleet.Server.FleetPolicyGuard(identity.DeviceId, () => connections.Connections.Select(static connection => connection.Connector).OfType<Fleet.Server.FleetConnector>());
        if (service is not null) service.Policy = guard;
        if (node.Automations is { } governed)
        {
            governed.Policy = guard;
            governed.ServerPolicy = linkId => connections.Find(linkId)?.Connector?.Policy.Current;
        }

        connections.Start();
        secrets.Start();
        // The policies kept apply from the start, whether the servers answer or not.
        _ = ApplyPolicyAsync(service, node.Automations);
        // The budgets and the policies are set, and the links read (their connectors exist, their kept budgets with them): the sends a
        // budget held when the node stopped are taken again through them, never before.
        if (service is not null) _ = ResumeOnceLoadedAsync(connections, service);
        var servers = new NodeServers(identity, source, connections, statistics, secrets) { Health = health };
        servers.WatchPolicy(service, node.Automations);
        return servers;
    }

    // Takes the held sends again once the links were read.
    private static async Task ResumeOnceLoadedAsync(Fleet.Server.FleetServerConnections connections, FleetNodeService service)
    {
        await connections.Loaded.ConfigureAwait(false);
        service.ResumeHeldSends();
    }

    private readonly CancellationTokenSource _stopping = new();

    // The policies held, applied to the work under way every half minute: a stop the node holds ends work that started before
    // the node read it (as at its start), and the longest time a policy gives a session of the fleet or a run ends them after it.
    private void WatchPolicy(FleetNodeService? service, NodeAutomations? automations)
        => _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try
            {
                while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
                {
                    try
                    {
                        if (service is not null) await service.EnforcePolicyAsync(_stopping.Token).ConfigureAwait(false);
                        if (automations is not null) await automations.EnforcePolicyAsync(_stopping.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
                    {
                        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Node").Warn($"The policy could not be applied to the work under way: {exception.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The node stops.
            }
        });

    // Applied beside the connection that verified the policy; a failure is logged, the next version applies again.
    private static async Task ApplyPolicyAsync(FleetNodeService? service, NodeAutomations? automations)
    {
        try
        {
            if (service is not null) await service.ApplyPolicyAsync(CancellationToken.None).ConfigureAwait(false);
            if (automations?.Policy?.Halt is { } halt) await automations.StopRunsAsync(halt.Describe("this machine"), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Node").Warn($"The policy could not be applied: {exception.Message}");
        }
    }

    /// <summary>Gets the problems of the machine its heartbeats report.</summary>
    public NodeHealth Health { get; private init; } = null!;

    // The providers of a link, read beside the connection that read access again; a failure is logged, the next read applies them.
    private static async Task RefreshProvidersAsync(NodeScopeProviders providers, Fleet.Server.FleetServerConnections? connections, string linkId)
    {
        if (connections?.Find(linkId)?.Connector is not { } connector) return;
        try
        {
            await providers.RefreshAsync(linkId, connector, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Fleet.Server.FleetServerException or HttpRequestException or IOException or UnauthorizedAccessException
            or CodeAlta.Fleet.Protocol.FleetDeviceListException or CodeAlta.Catalog.Secrets.SecretStoreUnavailableException or InvalidOperationException)
        {
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Node.Providers").Warn($"The providers of {linkId} could not be read: {exception.Message}");
        }
    }

    // Applied beside the connection that verified the document; a failure is logged, the next version applies again.
    private static async Task ApplyAccessAsync(FleetNodeService service, CodeAlta.Fleet.Protocol.FleetAccessState access, CodeAlta.Fleet.Protocol.FleetDeviceListState? devices)
    {
        try
        {
            await service.ApplyAccessAsync(access, devices, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Node").Warn($"The access document could not be applied: {exception.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Secrets.DisposeAsync().ConfigureAwait(false);
        await _connections.DisposeAsync().ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
        _statistics.Dispose();
        _identity.Dispose();
    }
}
