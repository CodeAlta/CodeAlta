using System.Globalization;
using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.Moves;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Hosting;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode pair</c>.</summary>
internal sealed class NodePairOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets or sets how many minutes the code stays valid, as typed; null for 10.</summary>
    public string? Minutes { get; set; }
}

/// <summary>The options of <c>altanode connect</c>.</summary>
internal sealed class NodeConnectOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets or sets the name this device gives itself on the node; null for the name of the machine.</summary>
    public string? Name { get; set; }

    /// <summary>Gets the words: the pairing link.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>The options of <c>altanode remote</c>.</summary>
internal sealed class NodeRemoteOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets or sets the most sessions a list shows, as typed.</summary>
    public string? Limit { get; set; }

    /// <summary>Gets or sets the cursor of a history window.</summary>
    public string? Cursor { get; set; }

    /// <summary>Gets or sets whether history reads from the start.</summary>
    public bool Forward { get; set; }

    /// <summary>Gets or sets how many updates <c>watch</c> prints before it stops, as typed; null until Ctrl+C.</summary>
    public string? Count { get; set; }

    /// <summary>Gets or sets the project of <c>create</c>.</summary>
    public string? Project { get; set; }

    /// <summary>Gets or sets the move <c>move</c> goes on with; null for a new one.</summary>
    public string? MoveId { get; set; }

    /// <summary>Gets or sets whether <c>move</c> lets the node clone the repository of the session.</summary>
    public bool Clone { get; set; }

    /// <summary>Gets the words: the machine, the verb and its arguments.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// The commands of <c>altanode</c> that act as a client of the fleet: they read the device key and the paired machines of
/// the profile, and never take its lock, so they run beside CodeAlta Desktop or a node on the same profile.
/// </summary>
internal sealed class NodeClientCommands(TextWriter output, TextWriter error)
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets what stops <c>watch</c> and <c>pair</c> early, for tests; none by default.</summary>
    public CancellationToken Stop { get; init; }

    public async ValueTask<int> PairAsync(NodePairOptions options)
    {
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        TimeSpan? lifetime = null;
        if (options.Minutes is { } text)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes is < 1 or > 1440)
                return await UsageAsync("--minutes takes 1 to 1440.").ConfigureAwait(false);
            lifetime = TimeSpan.FromMinutes(minutes);
        }

        var control = NodeLocalControl.Read(profile);
        if (!control.NodeListens)
        {
            await error.WriteLineAsync(NotListening("pair", control)).ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        if (!control.HasNodeKey)
        {
            await error.WriteLineAsync("altanode pair: the node has no device key yet.").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Stop);
        using var signals = new NodeSignals(stop.Cancel, stop.Cancel, static () => Environment.Exit(NodeExitCodes.Failure));
        var (client, identity) = await control.ConnectAsync(stop.Token).ConfigureAwait(false);
        using var ownedIdentity = identity;
        await using var ownedClient = client;
        var offer = await client.CreatePairingOfferAsync(lifetime, stop.Token).ConfigureAwait(false);
        await output.WriteLineAsync("Give this pairing link to the device to pair (altanode connect <link>, or Pair a machine in CodeAlta Desktop):").ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync(offer.Link).ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);
        // The device that pairs shows the same words before it connects: they say the link names this node's key.
        await output.WriteLineAsync($"The words of this node: {identity.Fingerprint.ToShortWords()}").ConfigureAwait(false);
        await output.WriteLineAsync($"It names {string.Join(", ", offer.Addresses)} and is valid until {offer.ExpiresAt.ToLocalTime():HH:mm}. Waiting... (Ctrl+C stops)")
            .ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
        FleetPairingOutcome outcome;
        try
        {
            outcome = await client.WaitPairingAsync(offer.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await output.WriteLineAsync("Stopped waiting; the code stays valid until it expires.").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        switch (outcome.Status)
        {
            case "redeemed":
                await output.WriteLineAsync($"Paired with {outcome.DeviceName} ({outcome.DeviceId}).").ConfigureAwait(false);
                return NodeExitCodes.Success;
            case "replaced":
                await error.WriteLineAsync("altanode pair: another pairing code replaced this one.").ConfigureAwait(false);
                return NodeExitCodes.Failure;
            default:
                await error.WriteLineAsync("altanode pair: the code expired before a device used it.").ConfigureAwait(false);
                return NodeExitCodes.Failure;
        }
    }

    public async ValueTask<int> MachinesAsync(NodeProfileOptions options)
    {
        if (options.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var machines = new PairedMachineStore(Catalog(profile)).List();
        if (machines.Count == 0)
        {
            await output.WriteLineAsync("No paired machine. Pair one with altanode connect <link>.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        foreach (var machine in machines)
        {
            await output.WriteLineAsync($"{machine.Name}  {machine.DeviceId}  {string.Join(", ", machine.Addresses)}  [{string.Join(", ", machine.Permissions)}]  paired {machine.PairedAt.ToLocalTime():yyyy-MM-dd HH:mm}")
                .ConfigureAwait(false);
        }

        return NodeExitCodes.Success;
    }

    public async ValueTask<int> ConnectAsync(NodeConnectOptions options)
    {
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        if (options.Words is not [var link]) return await UsageAsync("connect takes the pairing link of the node.").ConfigureAwait(false);
        if (!PairingPayload.TryParse(link, DateTimeOffset.UtcNow, out var offer, out var parseError))
        {
            return await UsageAsync(parseError switch
            {
                PairingPayloadError.Expired => "the pairing link has expired.",
                PairingPayloadError.UnsupportedVersion => "the pairing link comes from a version of CodeAlta this one does not read.",
                _ => "the text is not a CodeAlta pairing link.",
            }).ConfigureAwait(false);
        }

        var name = options.Name ?? Environment.MachineName;
        try
        {
            name = TrustedDeviceStore.NormalizeName(name);
        }
        catch (ArgumentException exception)
        {
            return await UsageAsync(exception.Message).ConfigureAwait(false);
        }

        var catalog = Catalog(profile);
        using var identity = DeviceIdentity.LoadOrCreate(catalog);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Stop);
        timeout.CancelAfter(ConnectTimeout);
        var machine = await FleetNodeClient.PairAsync(offer, identity, name, new PairedMachineStore(catalog), timeout.Token).ConfigureAwait(false);
        await output.WriteLineAsync($"Paired with {machine.Name} ({machine.DeviceId}) as {name}: it lets this device {string.Join(", ", machine.Permissions)}.").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    public async ValueTask<int> RemoteAsync(NodeRemoteOptions options)
    {
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        if (options.Words.Count < 2) return await UsageAsync("remote takes a machine and a command: info, sessions, history, send, create, watch, move.").ConfigureAwait(false);
        var catalog = Catalog(profile);
        var store = new PairedMachineStore(catalog);
        var machine = store.Find(options.Words[0]);
        if (machine is null)
        {
            await error.WriteLineAsync($"altanode remote: no paired machine is named '{options.Words[0]}' (altanode machines lists them).").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        var verb = options.Words[1];
        var arguments = options.Words.Skip(2).ToArray();
        var valid = verb switch
        {
            "info" or "sessions" => arguments.Length == 0,
            "history" or "watch" or "move" => arguments.Length == 1,
            "send" => arguments.Length >= 2,
            "create" => arguments.Length >= 1,
            _ => false,
        };
        if (!valid)
        {
            return await UsageAsync(verb switch
            {
                "info" or "sessions" => $"remote <machine> {verb} takes no argument.",
                "history" or "watch" or "move" => $"remote <machine> {verb} takes a session id.",
                "send" => "remote <machine> send takes a session id and the prompt.",
                "create" => "remote <machine> create takes the prompt.",
                _ => $"'{verb}' is not a command of remote: info, sessions, history, send, create, watch, move.",
            }).ConfigureAwait(false);
        }

        int? limit = null, count = null;
        if (options.Limit is { } limitText)
        {
            if (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > 500)
                return await UsageAsync("--limit takes 1 to 500.").ConfigureAwait(false);
            limit = value;
        }

        if (options.Count is { } countText)
        {
            if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
                return await UsageAsync("--count takes a positive number.").ConfigureAwait(false);
            count = value;
        }

        if (options.MoveId is { } given && (verb != "move" || !IsMoveId(given))) return await UsageAsync("--move-id takes the id of a move of move.").ConfigureAwait(false);
        using var identity = DeviceIdentity.LoadOrCreate(catalog);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Stop);
        using var signals = new NodeSignals(stop.Cancel, stop.Cancel, static () => Environment.Exit(NodeExitCodes.Failure));
        if (verb == "move") return await MoveAsync(profile, machine, identity, arguments[0], options, stop.Token).ConfigureAwait(false);
        await using var client = await FleetNodeClient.ConnectAsync(machine, identity, stop.Token).ConfigureAwait(false);
        switch (verb)
        {
            case "info":
            {
                var info = await client.GetInfoAsync(stop.Token).ConfigureAwait(false);
                await output.WriteLineAsync($"{info.Name} ({info.DeviceId}), {info.Os}, CodeAlta {info.Version}; this device may {string.Join(", ", info.Permissions)}.").ConfigureAwait(false);
                foreach (var space in info.Spaces) await output.WriteLineAsync($"space    {space.Id}  {space.Name}").ConfigureAwait(false);
                foreach (var project in info.Projects) await output.WriteLineAsync($"project  {project.Id}  {project.Name}  {project.Path}").ConfigureAwait(false);
                return NodeExitCodes.Success;
            }
            case "sessions":
            {
                var list = await client.ListSessionsAsync(new FleetSessionListParams(limit ?? 50), stop.Token).ConfigureAwait(false);
                foreach (var session in list.Sessions)
                    await output.WriteLineAsync($"{session.State,-8} {session.Id}  {session.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {OneLine(session.Title, 80)}").ConfigureAwait(false);
                if (list.HasMore) await output.WriteLineAsync("(more sessions: --limit shows more)").ConfigureAwait(false);
                return NodeExitCodes.Success;
            }
            case "history":
            {
                var window = await client.ReadHistoryAsync(new FleetHistoryParams(arguments[0], options.Cursor, options.Forward ? "forward" : "backward"), stop.Token)
                    .ConfigureAwait(false);
                foreach (var entry in window.Entries)
                {
                    var label = entry.Kind is null ? entry.Type : $"{entry.Type}/{entry.Kind}";
                    var text = entry.Name is null ? entry.Text : $"{entry.Name} {entry.Phase} {entry.Text}";
                    await output.WriteLineAsync($"{entry.Timestamp.ToLocalTime():HH:mm:ss} {label,-28} {OneLine(text, 120)}").ConfigureAwait(false);
                }

                if (window.Next is { } next)
                    await output.WriteLineAsync($"(more: --cursor {next}{(options.Forward ? " --forward" : "")})").ConfigureAwait(false);
                return NodeExitCodes.Success;
            }
            case "send":
            {
                var sent = await client.SendAsync(arguments[0], string.Join(' ', arguments.Skip(1)), cancellationToken: stop.Token).ConfigureAwait(false);
                await output.WriteLineAsync($"{sent.Status}{(sent.OperationId is { } operation ? $" (operation {operation:D})" : "")}").ConfigureAwait(false);
                return sent.Status is "accepted" or "replay" ? NodeExitCodes.Success : NodeExitCodes.Failure;
            }
            case "create":
            {
                var created = await client.CreateSessionAsync(new FleetCreateSessionParams(options.Project, Prompt: string.Join(' ', arguments)), cancellationToken: stop.Token)
                    .ConfigureAwait(false);
                await output.WriteLineAsync($"Created session {created.SessionId} on {created.ProviderKey}; its prompt: {created.Send?.Status ?? "none"}.").ConfigureAwait(false);
                return NodeExitCodes.Success;
            }
            default:
                return await WatchAsync(client, arguments[0], count, stop.Token).ConfigureAwait(false);
        }
    }

    public async ValueTask<int> MovesAsync(NodeProfileOptions options)
    {
        if (options.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var records = new SessionMoveTransactionStore(Catalog(profile)).List();
        if (records.Count == 0)
        {
            await output.WriteLineAsync("No move of a session was recorded on this profile.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        foreach (var record in records)
        {
            var detail = record.Reason ?? record.WorkFolder;
            await output.WriteLineAsync($"{record.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {record.Role,-6} {record.State,-9} {record.MoveId}  {record.SessionId}  "
                + $"{record.FromMachine ?? "?"} -> {record.ToMachine ?? "?"}{(detail is null ? "" : "  " + detail)}").ConfigureAwait(false);
        }

        return NodeExitCodes.Success;
    }

    // A session of the machine comes to the node of this profile: the machine calls the target's methods back on the
    // channel, and this command forwards them to the node through its local control.
    private async Task<int> MoveAsync(CodeAltaInstanceProfile profile, PairedMachine machine, DeviceIdentity identity, string sessionId, NodeRemoteOptions options,
        CancellationToken cancellationToken)
    {
        var control = NodeLocalControl.Read(profile);
        if (!control.NodeListens || !control.HasNodeKey)
        {
            await error.WriteLineAsync(control.NodeListens ? "altanode remote move: the node has no device key yet." : NotListening("remote move", control))
                .ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        var (owner, nodeIdentity) = await control.ConnectAsync(cancellationToken).ConfigureAwait(false);
        using var ownedIdentity = nodeIdentity;
        await using var ownedClient = owner;
        var name = (await owner.GetStatusAsync(cancellationToken).ConfigureAwait(false)).Name;
        var node = owner.AsMoveTarget();
        await using var client = await FleetNodeClient.ConnectAsync(machine, identity, connection => FleetMoveRoutes.Register(connection, (_, via) => via is null
                ? node
                : throw new FleetException(FleetErrorCodes.Forbidden, null, "This command forwards a move to the node of its profile only.")), cancellationToken)
            .ConfigureAwait(false);
        var moveId = options.MoveId ?? "move-" + Guid.NewGuid().ToString("N");
        await output.WriteLineAsync($"Moving {sessionId} from {machine.Name} to {name} (move {moveId})...").ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.StartMoveAsync(new FleetMoveStartParams(sessionId, moveId, name, CloneRepository: options.Clone), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        foreach (var warning in result.Warnings) await output.WriteLineAsync("  " + warning).ConfigureAwait(false);
        switch (result.Status)
        {
            case SessionMoveState.Finished:
                await output.WriteLineAsync($"The session runs on {name} now{(result.WorkFolder is { } folder ? $", in {folder}" : "")}.").ConfigureAwait(false);
                return NodeExitCodes.Success;
            case SessionMoveState.Offered:
                await error.WriteLineAsync($"altanode remote move: the move stopped ({result.Code}): {result.Message} Run the command again with --move-id {moveId} to go on.")
                    .ConfigureAwait(false);
                return NodeExitCodes.Failure;
            default:
                await error.WriteLineAsync($"altanode remote move: {result.Status} ({result.Code}): {result.Message}").ConfigureAwait(false);
                return NodeExitCodes.Failure;
        }
    }

    private static bool IsMoveId(string text)
    {
        try
        {
            SessionMoveTransactionStore.ValidateMoveId(text);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<int> WatchAsync(FleetNodeClient client, string sessionId, int? count, CancellationToken cancellationToken)
    {
        await using var watch = await client.WatchAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Watching {sessionId} (Ctrl+C stops).").ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var printed = 0;
        try
        {
            await foreach (var display in watch.Updates.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var lifecycle = display.Lifecycle is { } life ? life.Kind : "-";
                var latest = display.Text.LastOrDefault();
                var tool = display.Tools.LastOrDefault();
                await output.WriteLineAsync($"r{display.Revision}{(display.HasGap ? "+" : "")} {lifecycle}"
                    + (display.QueuedPromptCount is > 0 ? $" queued:{display.QueuedPromptCount}" : "")
                    + (tool is not null ? $" tool:{tool.Name}/{tool.Phase}" : "")
                    + (latest is not null ? $" {latest.Kind}{(latest.IsComplete ? "" : "…")}: {OneLine(latest.Text, 100)}" : "")).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (count is { } most && ++printed >= most) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        if (watch.EndReason is { } reason) await output.WriteLineAsync($"The node ended the watch: {reason}.").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    /// <summary>Tells what went wrong with a remote call in one line.</summary>
    internal static string Describe(Exception exception) => exception switch
    {
        DeviceAuthenticationException authentication => authentication.Message,
        FleetException fleet => fleet.Error.Message is { } message ? $"{message} ({fleet.Error.Code})" : fleet.Error.Code,
        _ => exception.Message,
    };

    /// <summary>Says why a command of the local control cannot reach the node of a profile.</summary>
    internal static string NotListening(string command, NodeLocalControl control) => control.Status.IsHeld
        ? $"altanode {command}: the instance that holds {control.Profile.StateRoot} does not listen for other devices (a node started with --no-listen, or CodeAlta Desktop)."
        : $"altanode {command}: no node runs on {control.Profile.StateRoot}. Start it with altanode run.";

    private static CatalogOptions Catalog(CodeAltaInstanceProfile profile) => new() { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot };

    private static string OneLine(string? text, int most)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= most ? line : line[..(most - 1)] + "…";
    }

    private async ValueTask<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
