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
/// their space, whether they are Private, their ownership epoch, and their counts (turns, tool calls, tokens). It says
/// <see cref="Changed"/> when a session starts or ends a turn; the rest is read when the connector polls.
/// </summary>
public sealed class FleetHostPublicationSource : IFleetPublicationSource, IAsyncDisposable
{
    /// <summary>The longest title published (sealed), in characters.</summary>
    public const int MaxTitleLength = 512;

    /// <summary>How long the counts of a session at work are kept before they are read again.</summary>
    public static readonly TimeSpan CountsRefresh = TimeSpan.FromSeconds(10);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset UpdatedAt, DateTimeOffset ReadAt, FleetSessionCounts? Counts)> _counts =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly CodeAltaHost _host;
    private readonly string _name;
    private readonly string _version;
    private readonly IReadOnlyList<string> _capabilities;
    private readonly SessionMoveTransactionStore _moves;
    private readonly RepositoryLocationCache _repositories = new();
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

    /// <summary>
    /// Gets or sets what names the person who started a session here through a team membership (a team machine), which the
    /// server's directory shows that session to; null names none.
    /// </summary>
    public Func<string, string?>? PersonOf { get; set; }

    /// <inheritdoc />
    public ValueTask<FleetMachineInventory> ReadInventoryAsync(CancellationToken cancellationToken)
    {
        var providers = _host.ModelProviderInitializationService.CurrentStates
            .Where(static state => state.Descriptor.IsEnabled && state.Availability == ModelProviderAvailability.Ready)
            .Select(static state => state.Descriptor.ProviderType)
            .Where(static type => !string.IsNullOrWhiteSpace(type) && type.Length <= 128)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(64).ToArray();
        return ValueTask.FromResult(new FleetMachineInventory(_name, FleetChannelProtocol.OperatingSystemName,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), _version, _capabilities,
            // The labels its owner gave it, read at each publication: a change is published at the next one.
            FleetMachineLabels.Read(_host.CatalogOptions.FleetRoot) is { Count: > 0 } labels ? labels : null, providers));
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
        // A question, a command or a file change to allow, a conversation held elsewhere: the session waits for a person.
        var waiting = new HashSet<string>(await _host.ListSessionsWaitingForPersonAsync().ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        var providerTypes = _host.ModelProviderInitializationService.CurrentStates
            .GroupBy(static state => state.ProviderId.Value, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Descriptor.ProviderType, StringComparer.Ordinal);
        var result = new List<FleetLocalSession>(sessions.Length);
        var store = _host.SessionViewCatalog.JournalStore.CreateSessionStore();
        var now = _time.GetUtcNow();
        foreach (var session in sessions)
        {
            var archived = session.ViewState?.Archived == true;
            var state = archived ? FleetSessionStates.Archived
                : waiting.Contains(session.SessionId) ? FleetSessionStates.RequiresAction
                : atWork.Contains(session.SessionId) ? FleetSessionStates.Running
                : FleetSessionStates.Idle;
            var providerKey = session.ViewState?.ProviderKey ?? session.ProviderKey;
            var providerType = providerKey is not null && providerTypes.TryGetValue(providerKey, out var type) ? type : session.ProtocolFamily;
            var isPrivate = session.ViewState?.Options?.Private == true;
            // A Private session is never published: its counts are not read.
            var counts = isPrivate ? null : await CountsAsync(store, session, state, now, cancellationToken).ConfigureAwait(false);
            var (space, title, project) = Describe(session, snapshot, spaces);
            // What recognizes its project on the person's machines; its statistics carry only its blind index.
            var projectIdentity = isPrivate || project is null ? null : await FleetProjectKeys.IdentityAsync(project, _repositories, cancellationToken).ConfigureAwait(false);
            result.Add(new FleetLocalSession(session.SessionId, space, isPrivate,
                epochs.TryGetValue(session.SessionId, out var epoch) ? epoch : 0, state, session.CreatedAt, session.UpdatedAt, Bounded(providerType),
                Bounded(session.ViewState?.ModelId ?? session.ModelId), counts, isPrivate ? null : title, projectIdentity, PersonOf?.Invoke(session.SessionId)));
        }

        // The sessions gone from the instance are forgotten with their counts.
        if (_counts.Count > sessions.Length)
        {
            var present = sessions.Select(static session => session.SessionId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _counts.Keys.Where(id => !present.Contains(id)).ToArray()) _counts.TryRemove(id, out _);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<FleetLocalSession>> ReadRetainedSessionsAsync(CancellationToken cancellationToken)
    {
        var movedAway = _host.RuntimeService.Relinquishments.ListRelinquished();
        if (movedAway.Count == 0) return [];
        OwnedWorkspaceSnapshot snapshot;
        try
        {
            snapshot = await _host.WorkspaceReads.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OwnedWorkspaceBusyException)
        {
            return [];
        }

        var spaces = await _host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var epochs = Epochs();
        var result = new List<FleetLocalSession>();
        foreach (var session in snapshot.Sessions.Where(session => movedAway.Contains(session.SessionId)))
        {
            // As it was published while this machine had it: its space, whether it is Private, its epoch here, its project.
            var isPrivate = session.ViewState?.Options?.Private == true;
            var (space, _, project) = Describe(session, snapshot, spaces);
            var projectIdentity = isPrivate || project is null ? null : await FleetProjectKeys.IdentityAsync(project, _repositories, cancellationToken).ConfigureAwait(false);
            result.Add(new FleetLocalSession(session.SessionId, space, isPrivate, epochs.TryGetValue(session.SessionId, out var epoch) ? epoch : 0, FleetSessionStates.Idle,
                session.CreatedAt, session.UpdatedAt, ProjectIdentity: projectIdentity));
        }

        return result;
    }

    // The counts of a session as its journal says them: turns and tool calls of its turns, tokens of its usage records,
    // without what a fork copied from its source or what was made outside CodeAlta (statistics count them the same way).
    // Read again when the session changed, and at most every few seconds while it is at work: a turn that writes many
    // records makes a new version of its facts now and then, not at each record.
    private async ValueTask<FleetSessionCounts?> CountsAsync(Agent.Runtime.FileSystemAgentSessionStore store, AgentSessionMetadata session, string state,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var atWork = state is FleetSessionStates.Running or FleetSessionStates.RequiresAction;
        if (_counts.TryGetValue(session.SessionId, out var cached) && cached.UpdatedAt == session.UpdatedAt && (!atWork || now - cached.ReadAt < CountsRefresh))
        {
            return cached.Counts;
        }

        try
        {
            var countedFrom = (await store.GetSessionSummaryAsync(session.SessionId, cancellationToken).ConfigureAwait(false))?.ForkedFrom?.CountedFrom;
            var turns = (await store.ReadTurnsAsync(session.SessionId, cancellationToken).ConfigureAwait(false)).Turns
                .Where(turn => !turn.MadeElsewhere && (countedFrom is null || turn.StartedAt >= countedFrom.Value)).ToArray();
            long input = 0, output = 0;
            foreach (var usage in await _host.WorkspaceReads.ReadUsageUpdatesAsync(session.SessionId, cancellationToken).ConfigureAwait(false))
            {
                input += Math.Max(0, usage.Operation.InputTokens ?? 0);
                output += Math.Max(0, usage.Operation.OutputTokens ?? 0);
            }

            var counts = new FleetSessionCounts(turns.Length, turns.Sum(static turn => (long)Math.Max(0, turn.ToolCallCount)), input, output);
            _counts[session.SessionId] = (session.UpdatedAt, now, counts);
            return counts;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
            or OwnedWorkspaceBusyException)
        {
            // A journal that cannot be read now (the history readers' own errors are IOExceptions): the counts known
            // before, read again at the next poll.
            return cached.Counts;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
        try { await _watch.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    // The space a session is in, as the desktop lists it (the one its journal records, else the one of its project), and
    // its title as the lists show it; null for a session listed by its id. The title leaves the machine only sealed.
    private static (string Space, string? Title, ProjectDescriptor? Project) Describe(AgentSessionMetadata session, OwnedWorkspaceSnapshot snapshot, IReadOnlyList<SpaceDescriptor> spaces)
    {
        var recorded = session.SpaceId;
        ProjectDescriptor? project = null;
        SessionViewKind? kind = null;
        if (snapshot.SessionHeaders.TryGetValue(session.SessionId, out var header) && header.SessionId == session.SessionId && header.CreatedAt == session.CreatedAt
            && header.WorkingDirectory == session.WorkspacePath)
        {
            recorded = header.SpaceId ?? recorded;
            kind = header.Kind;
            if (header.Kind == SessionViewKind.ProjectSession) project = ProjectCatalog.ResolveSessionProject(header.ProjectRef, session.WorkspacePath, snapshot.Projects);
        }

        var title = SessionRuntimeService.ListedTitle(session, kind, project).Trim();
        return (SpaceCatalog.ResolveSessionSpace(recorded, project, spaces),
            title.Length == 0 || title == session.SessionId ? null : title.Length > MaxTitleLength ? title[..MaxTitleLength] : title, project);
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
