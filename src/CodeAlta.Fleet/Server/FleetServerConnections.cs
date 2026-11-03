using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using XenoAtom.Logging;

namespace CodeAlta.Fleet.Server;

/// <summary>A link of the instance and its running connector.</summary>
/// <param name="Link">The link as the store holds it.</param>
/// <param name="Connector">Its connector; null for a revoked link, which connects no more.</param>
public sealed record FleetServerConnection(FleetServerLink Link, FleetConnector? Connector);

/// <summary>What the instance says of a link while it runs, in <c>fleet/servers/&lt;link&gt;/state.json</c>, for <c>altanode status</c>.</summary>
/// <param name="ProcessId">The process that runs the connector.</param>
/// <param name="State">The state of the connector (<see cref="FleetConnectorState"/>).</param>
/// <param name="Code">The code of the last refusal or failure, or null.</param>
/// <param name="Message">Its description, or null.</param>
/// <param name="Since">When the state began.</param>
public sealed record FleetServerRuntimeState(int ProcessId, string State, string? Code, string? Message, DateTimeOffset Since);

/// <summary>What <see cref="FleetServerConnections"/> runs with.</summary>
public sealed class FleetServerConnectionsOptions
{
    /// <summary>Gets the fleet folder of the state root: <c>servers.json</c> and the outboxes.</summary>
    public required string FleetRoot { get; init; }

    /// <summary>Gets the key of the device; the caller keeps and disposes it.</summary>
    public required DeviceIdentity Identity { get; init; }

    /// <summary>Gets what the instance says of itself in the hello.</summary>
    public required FleetClientInfo Client { get; init; }

    /// <summary>Gets the spaces, whose settings name the server each is linked to.</summary>
    public required SpaceCatalog Spaces { get; init; }

    /// <summary>Gets what there is to publish; null publishes nothing.</summary>
    public IFleetPublicationSource? Source { get; init; }

    /// <summary>Gets whether the connectors follow the directory of their organization (a desktop does).</summary>
    public bool WatchDirectory { get; init; }

    /// <summary>Gets how often the file of the links is read again, for a link another command of the profile made (<c>altanode join</c>).</summary>
    public TimeSpan ReloadInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets what changes the options of each connector, for tests; null for none.</summary>
    public Func<FleetConnectorOptions, FleetConnectorOptions>? Configure { get; init; }

    /// <summary>
    /// Gets what serves a relayed channel a server offers this instance, with the connector of that server (whose
    /// <see cref="FleetConnector.Devices"/> say who its organization holds); null declines every relay (a desktop does).
    /// </summary>
    public Func<FleetConnector, FleetRelayIncoming, Stream, CancellationToken, Task>? AcceptRelay { get; init; }
}

/// <summary>
/// The links of an instance to its servers, each with its <see cref="FleetConnector"/>: it starts one per link that is
/// not revoked, publishes to each only the sessions of the spaces linked to that server, records in the link what the
/// server says (approved, revoked), and notices the links another command of the same profile adds or removes.
/// </summary>
/// <remarks>Nothing connects for a profile without a link.</remarks>
public sealed class FleetServerConnections : IAsyncDisposable
{
    private readonly FleetServerConnectionsOptions _options;
    private readonly FleetServerLinkStore _store;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Fleet.Server");
    private ImmutableDictionary<string, Running> _running = ImmutableDictionary<string, Running>.Empty;
    private Task _reload = Task.CompletedTask;
    private DateTime _seen;

    /// <summary>Initializes a new instance of the <see cref="FleetServerConnections"/> class; <see cref="Start"/> connects.</summary>
    /// <param name="options">The links and what to publish.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public FleetServerConnections(FleetServerConnectionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _store = new FleetServerLinkStore(options.FleetRoot);
    }

    /// <summary>Raised when a link was added or removed, a connector changed its state, or a directory changed.</summary>
    public event Action? Changed;

    /// <summary>Gets the store of the links.</summary>
    public FleetServerLinkStore Store => _store;

    /// <summary>Gets the links and their connectors, in the order they were made.</summary>
    public IReadOnlyList<FleetServerConnection> Connections
    {
        get
        {
            IReadOnlyList<FleetServerLink> links;
            try { links = _store.List(); }
            catch (InvalidDataException) { links = []; }
            var running = Volatile.Read(ref _running);
            return [.. links.Select(link => new FleetServerConnection(link, running.TryGetValue(link.Id, out var value) ? value.Connector : null))];
        }
    }

    /// <summary>Finds the connection to a server by the id of its link.</summary>
    /// <param name="linkId">The id of the link.</param>
    /// <returns>The connection, or null.</returns>
    public FleetServerConnection? Find(string linkId) => Connections.FirstOrDefault(connection => connection.Link.Id == linkId);

    /// <summary>Starts the connectors of the links, and the reading of the file of the links.</summary>
    public void Start()
    {
        _reload = Task.Run(() => ReloadLoopAsync(_stop.Token));
    }

    /// <summary>Reads the file of the links again now: starts the connectors of new links and stops those of removed or revoked ones.</summary>
    /// <param name="cancellationToken">Cancels the wait for the change.</param>
    /// <returns>A task that completes once the connectors follow the file.</returns>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            changed = await SyncAsync().ConfigureAwait(false);
        }
        finally
        {
            _changes.Release();
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>Removes a link: its connector stops and its outbox is deleted. The spaces linked to it keep their setting until changed.</summary>
    /// <param name="linkId">The id of the link.</param>
    /// <param name="cancellationToken">Cancels the wait for the change.</param>
    /// <returns>Whether a link was removed.</returns>
    public async Task<bool> RemoveAsync(string linkId, CancellationToken cancellationToken = default)
    {
        bool removed;
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _running).TryGetValue(linkId, out var running)) await StopAsync(linkId, running).ConfigureAwait(false);
            removed = _store.Remove(linkId);
        }
        finally
        {
            _changes.Release();
        }

        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Tells the connectors that the spaces or the sessions changed: they publish what changed soon.</summary>
    public void Nudge()
    {
        foreach (var running in Volatile.Read(ref _running).Values) running.Connector.Nudge();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
        try { await _reload.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (id, running) in Volatile.Read(ref _running)) await StopAsync(id, running).ConfigureAwait(false);
        }
        finally
        {
            _changes.Release();
        }
    }

    private async Task ReloadLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var written = File.Exists(_store.FilePath) ? File.GetLastWriteTimeUtc(_store.FilePath) : DateTime.MinValue;
                if (written != _seen || _running.IsEmpty && written != DateTime.MinValue)
                {
                    _seen = written;
                    await ReloadAsync(stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (LogManager.IsInitialized) _logger.Warn(exception, "The server links could not be read");
            }

            try { await Task.Delay(_options.ReloadInterval, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // Under the gate: the connectors follow the file.
    private async Task<bool> SyncAsync()
    {
        var links = _store.List();
        var running = Volatile.Read(ref _running);
        var changed = false;
        foreach (var (id, value) in running)
        {
            var link = links.FirstOrDefault(candidate => candidate.Id == id);
            if (link is null || link.State == FleetServerLinkStates.Revoked || !string.Equals(link.Url, value.Url, StringComparison.Ordinal))
            {
                await StopAsync(id, value).ConfigureAwait(false);
                changed = true;
            }
        }

        foreach (var link in links)
        {
            if (link.State == FleetServerLinkStates.Revoked || Volatile.Read(ref _running).ContainsKey(link.Id)) continue;
            if (!string.Equals(link.DeviceId, _options.Identity.DeviceId, StringComparison.Ordinal)) continue;
            StartConnector(link);
            changed = true;
        }

        return changed;
    }

    private void StartConnector(FleetServerLink link)
    {
        var linked = new LinkedSpaces(link.Url, _options.Spaces);
        var source = _options.Source is null ? null : new LinkedSource(_options.Source, linked);
        FleetConnector? created = null;
        var options = new FleetConnectorOptions
        {
            ServerAddress = link.Address,
            Identity = _options.Identity,
            OutboxPath = Path.Combine(_store.LinkFolder(link.Id), "outbox.json"),
            Client = _options.Client,
            Source = source,
            PublishesSpace = linked.Contains,
            WatchDirectory = _options.WatchDirectory,
            DeviceListPinPath = Path.Combine(_store.LinkFolder(link.Id), FleetOrganizationDevices.PinFileName),
            AcceptRelay = _options.AcceptRelay is { } accept ? (incoming, stream, token) => accept(created!, incoming, stream, token) : null,
        };
        if (_options.Configure is { } configure) options = configure(options);
        var connector = new FleetConnector(options);
        created = connector;
        var running = new Running(link.Url, connector, source);
        connector.StateChanged += value => OnStateChanged(link.Id, value);
        connector.DirectoryChanged += _ => Changed?.Invoke();
        ImmutableInterlocked.Update(ref _running, static (map, item) => map.SetItem(item.Id, item.Running), (Id: link.Id, Running: running));
        connector.Start();
    }

    private async Task StopAsync(string id, Running running)
    {
        ImmutableInterlocked.Update(ref _running, static (map, key) => map.Remove(key), id);
        await running.Connector.DisposeAsync().ConfigureAwait(false);
        running.Source?.Dispose();
        try { File.Delete(StatePath(id)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    // What the server says of the device is kept in the link: welcomed is approved, refused for good is revoked.
    private void OnStateChanged(string linkId, FleetConnector connector)
    {
        var state = connector.State;
        try
        {
            var stored = state switch
            {
                FleetConnectorState.Online => FleetServerLinkStates.Approved,
                FleetConnectorState.Revoked => FleetServerLinkStates.Revoked,
                FleetConnectorState.PendingApproval => FleetServerLinkStates.Pending,
                _ => null,
            };
            if (stored is not null)
            {
                var link = _store.Update(linkId, link => link.State == stored ? link : link with { State = stored, UpdatedAt = DateTimeOffset.UtcNow });
                // The write is this instance's own: the reload loop does not take it for another command's.
                _seen = File.GetLastWriteTimeUtc(_store.FilePath);
                if (state == FleetConnectorState.Online && link is { OrganizationId: null }) _ = ReadOrganizationAsync(link);
            }

            if (state != FleetConnectorState.Stopped)
            {
                var error = connector.LastError;
                var file = new FleetServerRuntimeState(Environment.ProcessId, state.ToString(), error?.Code, error?.Message, DateTimeOffset.UtcNow);
                FleetFiles.WriteAtomically(StatePath(linkId), JsonSerializer.SerializeToUtf8Bytes(file, FleetServerStateJson.Default.FleetServerRuntimeState));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (LogManager.IsInitialized) _logger.Warn(exception, "The state of a server link could not be written");
        }

        Changed?.Invoke();
    }

    // Once approved, the link learns the organization it belongs to, which the spaces linked to it record.
    private async Task ReadOrganizationAsync(FleetServerLink link)
    {
        try
        {
            using var client = new FleetServerClient(link.Address, _options.Identity);
            var organization = await client.GetOrganizationAsync(_stop.Token).ConfigureAwait(false);
            _store.Update(link.Id, current => current with { OrganizationId = organization.Id, OrganizationName = organization.Name });
            _seen = File.GetLastWriteTimeUtc(_store.FilePath);
            Changed?.Invoke();
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or OperationCanceledException or InvalidDataException
            or ObjectDisposedException)
        {
            // Read at the next connection.
        }
    }

    private string StatePath(string linkId) => Path.Combine(_store.LinkFolder(linkId), "state.json");

    /// <summary>Reads what the instance that runs a link last said of it.</summary>
    /// <param name="store">The store of the links.</param>
    /// <param name="linkId">The id of the link.</param>
    /// <returns>The state, or null when none was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public static FleetServerRuntimeState? ReadRuntimeState(FleetServerLinkStore store, string linkId)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(store.LinkFolder(linkId), "state.json")), FleetServerStateJson.Default.FleetServerRuntimeState);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record Running(string Url, FleetConnector Connector, LinkedSource? Source);

    /// <summary>The spaces linked to one server, read again with each read of the sessions.</summary>
    private sealed class LinkedSpaces(string url, SpaceCatalog spaces)
    {
        private ImmutableHashSet<string> _ids = ImmutableHashSet<string>.Empty;

        public bool Contains(string? spaceId) => Volatile.Read(ref _ids).Contains(spaceId ?? SpaceDescriptor.DefaultId);

        public async Task RefreshAsync(CancellationToken cancellationToken)
        {
            var linked = (await spaces.LoadAsync(cancellationToken).ConfigureAwait(false))
                .Where(space => space.Settings.Fleet?.Server is { } server && FleetServerAddress.TryParse(server, out var address)
                    && string.Equals(FleetServerAddress.Format(address), url, StringComparison.OrdinalIgnoreCase))
                .Select(static space => space.Id);
            Volatile.Write(ref _ids, [.. linked]);
        }
    }

    /// <summary>The source of the instance, with the linked spaces read before each read of the sessions.</summary>
    private sealed class LinkedSource : IFleetPublicationSource, IDisposable
    {
        private readonly IFleetPublicationSource _inner;
        private readonly LinkedSpaces _spaces;

        public LinkedSource(IFleetPublicationSource inner, LinkedSpaces spaces)
        {
            _inner = inner;
            _spaces = spaces;
            _inner.Changed += OnChanged;
        }

        public event Action? Changed;

        public ValueTask<FleetMachineInventory> ReadInventoryAsync(CancellationToken cancellationToken) => _inner.ReadInventoryAsync(cancellationToken);

        public async ValueTask<IReadOnlyList<FleetLocalSession>> ReadSessionsAsync(CancellationToken cancellationToken)
        {
            await _spaces.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return await _inner.ReadSessionsAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => _inner.Changed -= OnChanged;

        private void OnChanged() => Changed?.Invoke();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetServerRuntimeState))]
internal sealed partial class FleetServerStateJson : JsonSerializerContext;
