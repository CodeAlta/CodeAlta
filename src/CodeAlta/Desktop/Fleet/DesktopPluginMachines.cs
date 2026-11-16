using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Server;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Fleet;

/// <summary>
/// The machines this computer reaches, for the built-in Mobile plugin only: the paired machines and those of its servers it reaches
/// through their relay, over the session hosts the window uses (<see cref="DesktopFleet.Hosts"/>). A machine is named by its device
/// id, never guessed from a session id. What a plugin does there is what this device may do there: every call goes to the machine's
/// node API, which checks <c>view</c>, <c>drive</c> and <c>start</c> itself, and a call this side already knows the machine would
/// refuse is not sent. Nothing here polls: states are read from what the hosts already follow.
/// </summary>
internal sealed class DesktopPluginMachines : IPluginMachineRuntimeService
{
    /// <summary>The runtime key of the plugin that is given the machines: the built-in Mobile plugin, for the page of the phones.</summary>
    internal const string MobileRuntimeKey = PluginPermissionServiceBridge.MobileRuntimeKey;

    /// <summary>The longest line of a live display.</summary>
    internal const int MaxLine = 160;

    private DesktopFleet? _fleet;
    private CodeAltaHost? _host;
    private readonly TaskCompletionSource<DesktopFleet> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gives the machines of the host once the fleet of this instance exists.</summary>
    /// <param name="fleet">The fleet.</param>
    /// <param name="host">The local host, to read the recorded destinations of relinquished sessions.</param>
    internal void Attach(DesktopFleet fleet, CodeAltaHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        _host = host;
        Volatile.Write(ref _fleet, fleet);
        _ready.TrySetResult(fleet);
    }

    /// <inheritdoc />
    public IPluginMachineService ForPlugin(string pluginRuntimeKey, bool builtIn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        return builtIn && pluginRuntimeKey == MobileRuntimeKey ? this : NoopPluginMachineService.Instance;
    }

    /// <summary>
    /// Gets the device id of this computer as its machines know it; null while this computer reaches no machine, so that asking never creates the key of a
    /// profile that never paired.
    /// </summary>
    public string? LocalDeviceId => Volatile.Read(ref _fleet) is { Hosts.Count: > 0 } fleet ? fleet.DeviceId : null;

    /// <inheritdoc />
    public bool HasMachines => Volatile.Read(ref _fleet) is { Hosts.Count: > 0 };

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, PluginSessionMove>> LocalSessionMovesAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, PluginSessionMove>(StringComparer.OrdinalIgnoreCase);
        if (_host is { } host)
        {
            var transactions = new SessionMoveTransactionStore(host.CatalogOptions);
            foreach (var id in host.RuntimeService.Relinquishments.ListRelinquished())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (host.RuntimeService.Relinquishments.Read(id) is not { IsRelinquished: true } moved) continue;
                SessionMoveTransaction? transaction = null;
                try
                {
                    if (!string.IsNullOrEmpty(moved.MoveId)) transaction = transactions.Read(moved.MoveId, SessionMoveRole.Source);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    // A missing or damaged routing record never makes a relinquished session local again.
                }
                // A relay's record names the relay in ToDeviceId and the final destination in Via.
                result[id] = new(transaction?.Via ?? transaction?.ToDeviceId, moved.ToMachine);
            }
        }
        return ValueTask.FromResult<IReadOnlyDictionary<string, PluginSessionMove>>(result);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PluginMachine>> ListAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<PluginMachine>>(Volatile.Read(ref _fleet) is { } fleet ? [.. fleet.Hosts.Select(Describe)] : []);

    /// <inheritdoc />
    public ValueTask<PluginMachineServices?> GetAsync(string machineId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        if (Volatile.Read(ref _fleet) is not { } fleet || fleet.Find(machineId) is not { } host) return ValueTask.FromResult<PluginMachineServices?>(null);
        var machine = Describe(host);
        // Each call finds the machine again by its id: a machine forgotten, revoked or paired again is what the next call sees, and one
        // that is not there fails closed. A read needs `view`; what answers or changes something needs `drive`, a new session `start`.
        Func<CancellationToken, ValueTask<FleetNodeClient?>> connect = _ => ValueTask.FromResult(ClientOf(fleet, machineId, FleetPermissions.View));
        Func<string, bool> allows = permission => Permissions(fleet.Find(machineId)).Contains(permission);
        return ValueTask.FromResult<PluginMachineServices?>(new PluginMachineServices(machine,
            new GatedAlta(new FleetMachineAlta(machine.Name, connect), allows),
            new GatedAsks(new FleetMachineAsks(connect), allows),
            new GatedPermissions(new FleetMachinePermissions(connect), allows)));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PluginMachineChange> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Built-in plugins start before the desktop creates its fleet. Keep their watchers alive until it is attached.
        var fleet = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        // One slot: any number of changes of the hosts make one wake-up, and what changed is found by comparing, so a slow reader
        // costs nothing, and the reader is never handed a queue that grows.
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        void Poke() => wake.Writer.TryWrite(true);
        fleet.Changed += Poke;
        try
        {
            var follower = new Follower();
            var pending = new List<PluginMachineChange>();
            while (true)
            {
                follower.Compare(fleet.Hosts, DateTimeOffset.UtcNow, pending);
                foreach (var change in pending) yield return change;
                pending.Clear();
                if (!await WaitAsync(wake.Reader, cancellationToken).ConfigureAwait(false)) yield break;
            }
        }
        finally
        {
            fleet.Changed -= Poke;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PluginMachineDisplay> WatchSessionAsync(string machineId, string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        ArgumentNullException.ThrowIfNull(sessionId);
        if (Volatile.Read(ref _fleet) is not { } fleet || fleet.Find(machineId) is not { } host || !Viewable(host)) yield break;
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // A machine forgotten or paired again (its host is another), or one that no longer lets this device see, ends the stream.
        void Check()
        {
            if (!ReferenceEquals(fleet.Find(machineId), host) || !Viewable(host)) ended.Cancel();
        }

        fleet.Changed += Check;
        try
        {
            Check();
            await using var display = host.ObserveDisplayAsync(sessionId, ended.Token).GetAsyncEnumerator(ended.Token);
            (bool Running, string? Kind, string? Line)? last = null;
            while (true)
            {
                bool more;
                try { more = await display.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                if (!more) yield break;
                if (ToDisplay(machineId, sessionId, display.Current, host, DateTimeOffset.UtcNow) is not { } replacement) continue;
                // The page shows the line: a replacement that says the same is not sent again.
                var shown = (replacement.Running, replacement.Kind, replacement.Line);
                if (last == shown) continue;
                last = shown;
                yield return replacement;
            }
        }
        finally
        {
            fleet.Changed -= Check;
        }
    }

    private static async ValueTask<bool> WaitAsync(ChannelReader<bool> reader, CancellationToken cancellationToken)
    {
        try
        {
            if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return false;
            reader.TryRead(out _);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // What this device may do on a machine, as the machine said it last, or as the pairing recorded it before the machine said anything.
    private static IReadOnlySet<string> Permissions(RemoteSessionHost? host)
        => host is null ? FrozenEmpty : (host.Listing.Info?.Permissions ?? host.Machine.Permissions).ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> FrozenEmpty = FrozenSet<string>.Empty;

    private static bool Viewable(RemoteSessionHost host) => host.State != RemoteMachineState.Unauthorized && Permissions(host).Contains(FleetPermissions.View);

    // The channel of a machine while it is reached and this device holds the permission; null otherwise.
    private static FleetNodeClient? ClientOf(DesktopFleet fleet, string machineId, string permission)
        => fleet.Find(machineId) is { State: RemoteMachineState.Online } host && Permissions(host).Contains(permission) ? host.CurrentClient : null;

    internal static PluginMachine Describe(RemoteSessionHost host)
    {
        var permissions = Permissions(host);
        var state = host.State switch
        {
            // A device that may not see the machine is not allowed on it, whatever the channel says.
            RemoteMachineState.Online => permissions.Contains(FleetPermissions.View) ? "online" : "unauthorized",
            RemoteMachineState.Connecting => "connecting",
            RemoteMachineState.Unauthorized => "unauthorized",
            _ => "offline",
        };
        var listing = host.Listing;
        return new PluginMachine(host.MachineId!, string.IsNullOrWhiteSpace(host.Machine.Name) ? listing.Info?.Name ?? host.MachineId! : host.Machine.Name, state,
            state == "online" ? host.ChannelPath switch { FleetChannelPath.Direct => "direct", FleetChannelPath.Relayed => "relayed", _ => null } : null,
            host.LastSeen, [.. FleetPermissions.Names.Where(permissions.Contains)], [.. listing.Info?.Features ?? []],
            listing.Sessions.Count(static session => session.State == "waiting"));
    }

    // The line of the live display of a session: what the page shows under its title while its turn runs, as the local one says it.
    internal static PluginMachineDisplay? ToDisplay(string machineId, string sessionId, RuntimeDisplayReplacement replacement, RemoteSessionHost? host, DateTimeOffset now)
    {
        var session = replacement.Snapshot.Sessions.FirstOrDefault(value => string.Equals(value.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        if (session.SessionId is null) return null;
        var text = session.Text.LastOrDefault(static item => item.Kind is AgentContentKind.Assistant or AgentContentKind.User);
        string? kind = null, line = null;
        if (text.ContentId is not null)
        {
            (kind, line) = text.Kind == AgentContentKind.User ? ("user", Concise(text.Text))
                : text.IsComplete ? ("assistant", Concise(text.Text)) : ("writing", "Writing…");
        }

        var running = session.Lifecycle?.Kind switch
        {
            SessionLifecycleEventKind.RunSubmitted => true,
            SessionLifecycleEventKind.RunCompleted or SessionLifecycleEventKind.RunFailed or SessionLifecycleEventKind.RunAborted => false,
            _ => host?.Listing.Sessions.FirstOrDefault(value => string.Equals(value.Id, sessionId, StringComparison.OrdinalIgnoreCase))?.State == "working",
        };
        return new PluginMachineDisplay(machineId, sessionId, replacement.Snapshot.Revision, running, kind, line, now);
    }

    internal static string Concise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return line.Length <= MaxLine ? line : line[..(MaxLine - 1)] + "…";
    }

    /// <summary>
    /// What one <see cref="WatchAsync"/> reader has been told: it compares the hosts with it and gives the difference. A machine reached
    /// again (a new connection, whatever states were seen between) gives its sessions as a baseline, which notifies nothing.
    /// </summary>
    internal sealed class Follower
    {
        private sealed class Known(long connections, string state)
        {
            internal long Connections = connections;
            internal string State = state;
            internal readonly Dictionary<string, string> Sessions = new(StringComparer.Ordinal);
        }

        private readonly Dictionary<string, Known> _machines = new(StringComparer.Ordinal);

        /// <summary>Appends what changed since the last comparison.</summary>
        /// <param name="hosts">The hosts now.</param>
        /// <param name="now">The time of the connection changes.</param>
        /// <param name="changes">Receives the changes.</param>
        internal void Compare(IReadOnlyList<RemoteSessionHost> hosts, DateTimeOffset now, List<PluginMachineChange> changes)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var host in hosts)
            {
                var machine = Describe(host);
                var connections = host.Connections;
                var listing = host.Listing;
                var online = machine.State == "online";
                present.Add(machine.Id);
                var first = !_machines.TryGetValue(machine.Id, out var known);
                if (first) _machines[machine.Id] = known = new Known(connections, "");
                var wasOnline = known!.State == "online";
                if (first || known.State != machine.State)
                {
                    changes.Add(new PluginMachineChange(machine.Id, null, machine.State, machine.LastSeen ?? now, null, Baseline: first));
                    known.State = machine.State;
                }

                if (!online)
                {
                    // A machine that does not answer, or does not let this device see, is told nothing about: it is a baseline again once it does.
                    known.Sessions.Clear();
                    known.Connections = connections;
                    continue;
                }

                // What a machine says when it is reached again is how things stand, not what happened: its states are given as a baseline.
                var baseline = !wasOnline || known.Connections != connections;
                known.Connections = connections;
                if (baseline) known.Sessions.Clear();
                var alive = new HashSet<string>(StringComparer.Ordinal);
                foreach (var session in listing.Sessions)
                {
                    alive.Add(session.Id);
                    if (known.Sessions.TryGetValue(session.Id, out var state) && state == session.State) continue;
                    known.Sessions[session.Id] = session.State;
                    changes.Add(new PluginMachineChange(machine.Id, session.Id, session.State, session.UpdatedAt, session.SpaceId, baseline));
                }

                foreach (var gone in known.Sessions.Keys.Where(id => !alive.Contains(id)).ToArray()) known.Sessions.Remove(gone);
            }

            foreach (var id in _machines.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                // Forgotten or taken out of the lists of its servers: nothing reaches it any more.
                _machines.Remove(id);
                changes.Add(new PluginMachineChange(id, null, "unauthorized", now, null, Baseline: false));
            }
        }
    }

    // What a node may be asked, by the permission of this device there.
    private static class FleetPermissions
    {
        internal const string View = "view", Drive = "drive", Start = "start";
        internal static readonly ImmutableArray<string> Names = [View, Drive, Start];
    }

    private static PluginAltaCommandResult Failed(string code, string message)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "alta.error");
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        return new PluginAltaCommandResult { ExitCode = 1, TranscriptJsonl = Encoding.UTF8.GetString(buffer.ToArray()) + "\n", Error = message };
    }

    /// <summary>The permission a command needs on the machine, as the node API asks it; null for a command no call of the node API carries.</summary>
    internal static string? Needs(IReadOnlyList<string> args)
    {
        if (FleetSessionReads.TryParse(args, out var request)) return FleetSessionReads.IsAction(request!) ? FleetPermissions.Drive : FleetPermissions.View;
        if (args.Count < 2 || args[0] != "session") return null;
        return args[1] switch { "send" or "queue" or "steer" or "abort" => FleetPermissions.Drive, "create" => FleetPermissions.Start, _ => null };
    }

    // The commands of a machine, refused here when this device is not allowed them: the node would refuse them as well.
    private sealed class GatedAlta(IPluginAltaService inner, Func<string, bool> allows) : IPluginAltaService
    {
        public ValueTask<PluginAltaCommandResult> InvokeAsync(IReadOnlyList<string> args, string? stdin = null, PluginAltaInvocationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(args);
            return Needs(args) is { } permission && !allows(permission)
                ? ValueTask.FromResult(Failed("machine.forbidden", $"This computer may not do that on the machine ('{permission}' is not given)."))
                : inner.InvokeAsync(args, stdin, options, cancellationToken);
        }
    }

    // Reading what waits is `view`; answering is `drive`, as on the machine.
    private sealed class GatedAsks(IPluginAskService inner, Func<string, bool> allows) : IPluginAskService
    {
        public bool HasAsks => inner.HasAsks;

        public ValueTask<IReadOnlyList<PluginAskSummary>> ListAskingAsync(CancellationToken cancellationToken = default) => inner.ListAskingAsync(cancellationToken);

        public ValueTask<PluginAsk?> GetPendingAsync(string sessionId, CancellationToken cancellationToken = default) => inner.GetPendingAsync(sessionId, cancellationToken);

        public ValueTask<PluginAskOutcome> AnswerAsync(PluginAskReference ask, IReadOnlyList<PluginAskAnswer> answers, PluginAskReviewAnswer? review,
            CancellationToken cancellationToken = default)
            => allows(FleetPermissions.Drive) ? inner.AnswerAsync(ask, answers, review, cancellationToken) : ValueTask.FromResult(new PluginAskOutcome("rejected"));

        public ValueTask<PluginAskOutcome> CancelAsync(PluginAskReference ask, CancellationToken cancellationToken = default)
            => allows(FleetPermissions.Drive) ? inner.CancelAsync(ask, cancellationToken) : ValueTask.FromResult(new PluginAskOutcome("rejected"));
    }

    private sealed class GatedPermissions(IPluginPermissionService inner, Func<string, bool> allows) : IPluginPermissionService
    {
        public bool HasPermissions => inner.HasPermissions;

        public ValueTask<IReadOnlyList<PluginPermissionSummary>> ListWaitingAsync(CancellationToken cancellationToken = default) => inner.ListWaitingAsync(cancellationToken);

        public ValueTask<PluginPermissionPage> ListAsync(string sessionId, CancellationToken cancellationToken = default) => inner.ListAsync(sessionId, cancellationToken);

        public ValueTask<PluginPermissionOutcome> ResolveAsync(string sessionId, string requestId, PluginPermissionDecision decision, CancellationToken cancellationToken = default)
            => allows(FleetPermissions.Drive) ? inner.ResolveAsync(sessionId, requestId, decision, cancellationToken) : ValueTask.FromResult(new PluginPermissionOutcome("rejected"));
    }
}
