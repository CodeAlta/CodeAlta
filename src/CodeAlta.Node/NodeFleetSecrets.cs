using System.Collections.Concurrent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Node;

/// <summary>
/// The values the servers of a node give it (<c>doc/fleet/secrets.md</c>, how a run gets them): one
/// <see cref="FleetSecretSync"/> per server link, run when the connection starts, when the server says the values
/// changed, now and then, and before a run (<see cref="SyncAsync(CancellationToken)"/>); the values land in the protected
/// store of the machine and are masked at once. The uses the runs record are published to the server they came through.
/// A link that is gone takes its values and its uses away.
/// </summary>
internal sealed class NodeFleetSecrets : IAsyncDisposable
{
    /// <summary>How often the values are read again when the server says nothing.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly CodeAltaHost _host;
    private readonly FleetServerConnections _connections;
    private readonly LocalSecretStore _store;
    private readonly SecretUseLog _uses;
    private readonly RepositoryLocationCache _repositories = new();
    private readonly ConcurrentDictionary<string, LinkState> _links = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly TimeSpan _interval;
    private Task _loop = Task.CompletedTask;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="NodeFleetSecrets"/> class.</summary>
    /// <param name="host">The host of the node.</param>
    /// <param name="connections">The connections to the servers.</param>
    /// <param name="interval">How often the values are read again; null for <see cref="DefaultInterval"/>.</param>
    public NodeFleetSecrets(CodeAltaHost host, FleetServerConnections connections, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(connections);
        _host = host;
        _connections = connections;
        _store = new LocalSecretStore(host.CatalogOptions.GlobalRoot);
        _uses = new SecretUseLog(host.CatalogOptions.GlobalRoot);
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Starts the loop.</summary>
    public void Start() => _loop = Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Gets a task that completes the next time a server says the values changed: an approval a run waits for may have been given.</summary>
    public Task ApprovalSignal => Volatile.Read(ref _signal).Task;

    /// <summary>Asks the values of a link to be read again soon: its server says they changed.</summary>
    /// <param name="linkId">The link.</param>
    public void Changed(string linkId)
    {
        if (_links.TryGetValue(linkId, out var state)) state.Dirty = true;
        _wake.Release();
        Interlocked.Exchange(ref _signal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>Gets the environments of the person as the last refresh of each link verified them, with the sync of their link.</summary>
    /// <returns>The environments, by name; the first link that has one wins.</returns>
    public IReadOnlyDictionary<string, (FleetEnvironmentDefinition Definition, FleetSecretSync Sync)> Environments()
    {
        var environments = new Dictionary<string, (FleetEnvironmentDefinition, FleetSecretSync)>(StringComparer.Ordinal);
        foreach (var state in _links.Values.OrderBy(static state => state.Sync.Source, StringComparer.Ordinal))
        {
            foreach (var (name, definition) in state.Sync.Environments) environments.TryAdd(name, (definition, state.Sync));
        }

        return environments;
    }

    /// <summary>
    /// Tells whether an admission of a session to an environment still holds: until a server was read since the node
    /// started, the kept admission holds; then the environment must be the one admitted, at the version that admitted it.
    /// </summary>
    /// <param name="admission">The admission.</param>
    /// <returns>Whether it holds.</returns>
    public bool Holds(SessionEnvironmentAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!_links.Values.Any(static state => state.Synced)) return true;
        return Environments().TryGetValue(admission.Admission.Environment, out var found) && found.Definition.EnvironmentId == admission.Admission.EnvironmentId
            && found.Definition.Number == admission.Admission.Version;
    }

    /// <summary>Reads the values of every link again now, before a run: what a server does not answer keeps what was held.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes once every link answered or failed.</returns>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        foreach (var connection in _connections.Connections)
        {
            if (connection.Connector is not { } connector) continue;
            await SyncLinkAsync(connection.Link.Id, connector, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // The node may dispose its servers twice: the second time finds the loop stopped.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var live = new HashSet<string>(StringComparer.Ordinal);
                foreach (var connection in _connections.Connections)
                {
                    live.Add(connection.Link.Id);
                    if (connection.Connector is not { } connector) continue;
                    var state = _links.GetOrAdd(connection.Link.Id, _ => new LinkState(Sync(connection.Link.Id, connector)));
                    var online = connector.State == FleetConnectorState.Online;
                    // Once online, and whenever the server says the values changed or the interval passed.
                    if (online && (state.Dirty || !state.WasOnline || DateTimeOffset.UtcNow - state.LastSync >= _interval))
                        await SyncLinkAsync(connection.Link.Id, connector, stop).ConfigureAwait(false);
                    if (connector.State == FleetConnectorState.Revoked) Forget(connection.Link.Id);
                    state.WasOnline = online;
                    if (online) await PublishUsesAsync(connection.Link.Id, connector, stop).ConfigureAwait(false);
                }

                // A link another command of the profile removed: the node left that server.
                foreach (var gone in _links.Keys.Where(id => !live.Contains(id)).ToArray()) Forget(gone);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log(true, $"The values of the fleet could not be kept: {exception.Message}");
            }

            try { await _wake.WaitAsync(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private FleetSecretSync Sync(string linkId, FleetConnector connector)
        => new(linkId, connector.Devices, connector.MetadataKeys, _store, ProjectsAsync) { Access = () => connector.Access.Current };

    private async Task SyncLinkAsync(string linkId, FleetConnector connector, CancellationToken cancellationToken)
    {
        var state = _links.GetOrAdd(linkId, _ => new LinkState(Sync(linkId, connector)));
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            state.Dirty = false;
            var result = await state.Sync.SyncAsync(cancellationToken).ConfigureAwait(false);
            state.LastSync = DateTimeOffset.UtcNow;
            state.Synced = !result.Revoked;
            foreach (var refused in result.Refused) Log(true, $"A value of {FleetServerAddress.Format(connector.ServerAddress)} was refused: {refused}.");
            if (result.Revoked) Log(true, $"This machine was revoked by {FleetServerAddress.Format(connector.ServerAddress)}: the values it gave were taken away.");
            // The sessions of the node mask the values they may now be given, and stop masking those taken away.
            _host.AgentHub.SecretMasker?.Refresh();
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or TaskCanceledException or FleetDeviceListException
            or SecretStoreUnavailableException or IOException or UnauthorizedAccessException)
        {
            state.Dirty = true;
            Log(false, $"The values of {FleetServerAddress.Format(connector.ServerAddress)} were not read again: {exception.Message}");
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task PublishUsesAsync(string linkId, FleetConnector connector, CancellationToken cancellationToken)
    {
        try
        {
            if (!_links.TryGetValue(linkId, out var state)) return;
            await _uses.PublishAsync(linkId, FleetUsesPerBatch, async uses =>
            {
                await state.Sync.PublishUsesAsync([.. uses.Select(static use => new FleetSecretUseDto(use.SecretId, use.Name, use.Version, use.RunId, use.UsedAt))],
                    cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or TaskCanceledException)
        {
            Log(false, $"The uses of the values of {FleetServerAddress.Format(connector.ServerAddress)} were not published: {exception.Message}");
        }
    }

    private const int FleetUsesPerBatch = 500;

    private void Forget(string linkId)
    {
        if (!_links.TryRemove(linkId, out var state)) return;
        try
        {
            state.Sync.Forget();
            _uses.Forget(linkId);
            _host.AgentHub.SecretMasker?.Refresh();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log(true, $"The values of a server that is gone could not be taken away: {exception.Message}");
        }
    }

    // The projects of the node with what recognizes them on the person's machines.
    private async Task<IReadOnlyList<FleetSecretProject>> ProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = new List<FleetSecretProject>();
        foreach (var project in await _host.ProjectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(project.Id)) continue;
            if (await FleetProjectKeys.IdentityAsync(project, _repositories, cancellationToken).ConfigureAwait(false) is { } identity) projects.Add(new(project.Id, identity));
        }

        return projects;
    }

    private static void Log(bool warning, string message)
    {
        if (!LogManager.IsInitialized) return;
        var logger = LogManager.GetLogger("CodeAlta.Node.Secrets");
        if (warning) logger.Warn(message);
        else logger.Info(message);
    }

    private sealed class LinkState(FleetSecretSync sync)
    {
        public FleetSecretSync Sync { get; } = sync;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public volatile bool Dirty = true;

        public bool WasOnline { get; set; }

        public bool Synced { get; set; }

        public DateTimeOffset LastSync { get; set; } = DateTimeOffset.MinValue;
    }
}
