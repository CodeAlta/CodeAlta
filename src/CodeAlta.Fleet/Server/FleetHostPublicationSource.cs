using System.Runtime.InteropServices;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// What a running instance (a node, CodeAlta Desktop) publishes, read from its runtime: the inventory of the machine,
/// and its sessions as the owned workspace lists them, with their state (at work, waiting for a person, idle, archived),
/// their space, whether they are Private, and their ownership epoch. It says <see cref="Changed"/> when a session starts
/// or ends a turn; the rest is read when the connector polls.
/// </summary>
public sealed class FleetHostPublicationSource : IFleetPublicationSource, IAsyncDisposable
{
    private readonly CodeAltaHost _host;
    private readonly string _name;
    private readonly string _version;
    private readonly IReadOnlyList<string> _capabilities;
    private readonly SessionMoveTransactionStore _moves;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _watch;

    /// <summary>Initializes a new instance of the <see cref="FleetHostPublicationSource"/> class and starts following the turns of the runtime.</summary>
    /// <param name="host">The runtime.</param>
    /// <param name="name">The name of the machine.</param>
    /// <param name="version">The version of CodeAlta it runs.</param>
    /// <param name="capabilities">What it offers, such as <c>moves</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FleetHostPublicationSource(CodeAltaHost host, string name, string version, IReadOnlyList<string>? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _host = host;
        _name = name;
        _version = version;
        _capabilities = capabilities ?? [];
        _moves = new SessionMoveTransactionStore(host.CatalogOptions);
        _watch = Task.Run(() => WatchAsync(_stop.Token));
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public ValueTask<FleetMachineInventory> ReadInventoryAsync(CancellationToken cancellationToken)
    {
        var providers = _host.ModelProviderInitializationService.CurrentStates
            .Where(static state => state.Descriptor.IsEnabled && state.Availability == ModelProviderAvailability.Ready)
            .Select(static state => state.Descriptor.ProviderType)
            .Where(static type => !string.IsNullOrWhiteSpace(type) && type.Length <= 128)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(64).ToArray();
        return ValueTask.FromResult(new FleetMachineInventory(_name, FleetChannelProtocol.OperatingSystemName,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), _version, _capabilities, null, providers));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<FleetLocalSession>> ReadSessionsAsync(CancellationToken cancellationToken)
    {
        OwnedWorkspaceSnapshot snapshot;
        try
        {
            snapshot = await _host.WorkspaceReads.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OwnedWorkspaceBusyException)
        {
            // Read at the next poll: nothing is published meanwhile, nothing is lost.
            return [];
        }

        var spaces = await _host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var movedAway = _host.RuntimeService.Relinquishments.ListRelinquished();
        var epochs = Epochs();
        var sessions = snapshot.Sessions.Where(session => !movedAway.Contains(session.SessionId)).ToArray();
        var atWork = await _host.Commands.ReadSessionsAtWorkAsync([.. sessions.Select(static session => session.SessionId)]).ConfigureAwait(false);
        var waiting = new HashSet<string>(_host.Commands.Asks.ListWaitingSessions(), StringComparer.OrdinalIgnoreCase);
        var providerTypes = _host.ModelProviderInitializationService.CurrentStates
            .GroupBy(static state => state.ProviderId.Value, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Descriptor.ProviderType, StringComparer.Ordinal);
        var result = new List<FleetLocalSession>(sessions.Length);
        foreach (var session in sessions)
        {
            var archived = session.ViewState?.Archived == true;
            var state = archived ? FleetSessionStates.Archived
                : waiting.Contains(session.SessionId) ? FleetSessionStates.RequiresAction
                : atWork.Contains(session.SessionId) ? FleetSessionStates.Running
                : FleetSessionStates.Idle;
            var providerKey = session.ViewState?.ProviderKey ?? session.ProviderKey;
            var providerType = providerKey is not null && providerTypes.TryGetValue(providerKey, out var type) ? type : session.ProtocolFamily;
            result.Add(new FleetLocalSession(session.SessionId, SpaceOf(session, snapshot, spaces), session.ViewState?.Options?.Private == true,
                epochs.TryGetValue(session.SessionId, out var epoch) ? epoch : 0, state, session.CreatedAt, session.UpdatedAt, Bounded(providerType),
                Bounded(session.ViewState?.ModelId ?? session.ModelId)));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
        try { await _watch.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    // The space a session is in, as the desktop lists it: the one its journal records, else the one of its project.
    private static string SpaceOf(AgentSessionMetadata session, OwnedWorkspaceSnapshot snapshot, IReadOnlyList<SpaceDescriptor> spaces)
    {
        var recorded = session.SpaceId;
        ProjectDescriptor? project = null;
        if (snapshot.SessionHeaders.TryGetValue(session.SessionId, out var header) && header.SessionId == session.SessionId && header.CreatedAt == session.CreatedAt
            && header.WorkingDirectory == session.WorkspacePath)
        {
            recorded = header.SpaceId ?? recorded;
            if (header.Kind == SessionViewKind.ProjectSession) project = ProjectCatalog.ResolveSessionProject(header.ProjectRef, session.WorkspacePath, snapshot.Projects);
        }

        return SpaceCatalog.ResolveSessionSpace(recorded, project, spaces);
    }

    // The epoch a move gave each session this machine received; 0 for the others.
    private Dictionary<string, long> Epochs()
    {
        var epochs = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var move in _moves.List())
            {
                if (move.Role != SessionMoveRole.Target || move.State is not (SessionMoveState.Committed or SessionMoveState.Finished)) continue;
                if (!epochs.TryGetValue(move.SessionId, out var known) || move.Epoch > known) epochs[move.SessionId] = move.Epoch;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Without the moves, the sessions keep the epoch of a session that never moved.
        }

        return epochs;
    }

    private static string? Bounded(string? value) => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) ? null : value;

    // A turn that starts or ends, a session that asks: the lifecycle of the display projection changes. Text does not count.
    private async Task WatchAsync(CancellationToken stop)
    {
        string? signature = null;
        try
        {
            await foreach (var replacement in _host.RuntimeService.Display.ObserveAsync(stop).ConfigureAwait(false))
            {
                var builder = new StringBuilder();
                foreach (var session in replacement.Snapshot.Sessions)
                {
                    builder.Append(session.SessionId).Append(':').Append(session.Lifecycle?.Kind.ToString()).Append(':').Append(session.StatusKind?.ToString())
                        .Append(':').Append(session.QueuedPromptCount).Append(';');
                }

                var next = builder.ToString();
                if (next == signature) continue;
                signature = next;
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException)
        {
            // The projection has its most observers: the connector polls.
        }
    }
}
