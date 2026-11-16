using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

internal sealed record MobileMachinePage(PluginMachine Machine, MobileSessions Sessions, MobileApi Api, MobileAsks Asks, MobilePermissions Permissions);

// A phone holds only its Mobile key. The host's confined machine service acts with the host's device key, as the window
// does. No Mobile HTTP endpoint, fleet key, or machine-of-a-machine is exposed through this service.
internal sealed class MobileMachines(IPluginMachineService machines, IPluginSpaceService spaces, MobileMachinePage local, MobileLive live,
    MobilePushEvents push, Func<bool> enabled)
{
    internal const string Local = "local";
    private const int MaxMachines = 32;
    private const int MaxWindow = 5000;
    private const int MaxStates = MaxMachines * 200;
    private const int MaxLive = 512;
    private readonly ConcurrentDictionary<string, JsonObject> _lastLists = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonObject> _remoteLive = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _states = new(StringComparer.Ordinal);
    private readonly OnceLedger _localOnce = new();
    private string? _scope;

    internal bool Enabled => enabled() && machines.HasMachines;
    internal MobileMachinePage LocalPage => local;
    internal int Generation => push.Generation;

    internal async Task<PluginSessionMove?> LocalMoveAsync(string sessionId, CancellationToken cancellationToken)
        => (await machines.LocalSessionMovesAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(sessionId);

    internal void ReconcileScope()
    {
        var scope = Enabled + "|" + string.Join('|', spaces.GetAssignedSpaces().Order(StringComparer.Ordinal));
        if (Interlocked.Exchange(ref _scope, scope) is { } before && before != scope) Invalidate();
    }

    internal void Invalidate()
    {
        _lastLists.Clear();
        _remoteLive.Clear();
        _states.Clear();
        push.Reset();
        live.CloseListeners();
    }

    internal async Task<JsonObject> ListAsync(CancellationToken cancellationToken)
    {
        var list = new JsonArray { Describe(local.Machine, true) };
        if (Enabled)
            foreach (var machine in (await machines.ListAsync(cancellationToken).ConfigureAwait(false)).Take(MaxMachines))
                list.Add(Describe(machine, false));
        return new() { ["machines"] = list, ["error"] = null };
    }

    private JsonObject Describe(PluginMachine machine, bool isLocal) => new()
    {
        ["id"] = machine.Id, ["name"] = machine.Name, ["this"] = isLocal, ["deviceId"] = isLocal ? machines.LocalDeviceId : machine.Id,
        ["state"] = machine.State, ["path"] = machine.Path, ["lastSeen"] = machine.LastSeen?.ToString("O"), ["waiting"] = machine.Waiting,
        ["can"] = Can(machine), ["features"] = Features(machine),
    };

    private static JsonObject Can(PluginMachine machine) => new()
    {
        ["view"] = machine.Permissions.Contains("view"), ["drive"] = machine.State == "online" && machine.Permissions.Contains("drive"),
        ["start"] = machine.State == "online" && machine.Permissions.Contains("start"),
    };

    private static JsonArray Features(PluginMachine machine) => machine.Id == Local
        ? new([.. machine.Features.Select(static feature => (JsonNode)feature)])
        : machine.Features.Contains("sessions.read") ? new("sessions.read", "provider", "archive", "jobs", "jobs.stop", "questions", "permissions") : new();

    internal async Task<MobileMachinePage?> OpenAsync(string machineId, CancellationToken cancellationToken, string? requestId = null)
    {
        ReconcileScope();
        if (machineId == Local)
        {
            if (requestId is null) return local;
            var localAlta = new RequestAlta(new OnceAlta(local.Api.Alta, _localOnce), requestId);
            var localSessions = new MobileSessions(localAlta, spaces, live);
            return local with { Sessions = localSessions, Api = local.Api.Rebind(localAlta, localSessions) };
        }
        if (!Enabled || !MobileApi.IsId(machineId)) return null;
        var service = await machines.GetAsync(machineId, cancellationToken).ConfigureAwait(false);
        if (service is null) return null;
        var state = new MobileLive();
        IPluginAltaService alta = requestId is null ? service.Alta : new RequestAlta(service.Alta, requestId);
        var sessions = new MobileSessions(alta, spaces, state, strictSpaces: true);
        return new(service.Machine, sessions, new MobileApi(alta, sessions),
            new MobileAsks(service.Asks, sessions), new MobilePermissions(service.Permissions, sessions, null, service.Alta));
    }

    internal async Task<JsonObject?> SessionsAsync(string? machineId, int offset, int limit, IReadOnlyList<string>? projects, CancellationToken cancellationToken)
    {
        ReconcileScope();
        machineId ??= Local; // Old clients and links retain their local meaning.
        offset = Math.Clamp(offset, 0, MaxWindow - 1);
        limit = Math.Clamp(limit, 1, MobileSessions.MaximumPageSize);
        if (machineId != "all")
        {
            if (await OpenAsync(machineId, cancellationToken).ConfigureAwait(false) is not { } page) return null;
            return await ReadListAsync(page, offset, limit, projects, cancellationToken).ConfigureAwait(false);
        }

        var pages = new List<MobileMachinePage> { local };
        if (Enabled)
            foreach (var machine in (await machines.ListAsync(cancellationToken).ConfigureAwait(false)).Take(MaxMachines))
                if (await OpenAsync(machine.Id, cancellationToken).ConfigureAwait(false) is { } page) pages.Add(page);
        var sessions = new List<JsonObject>();
        var parents = new List<JsonObject>();
        var more = false;
        var wanted = Math.Min(MaxWindow, offset + limit + 1);
        foreach (var page in pages)
        {
            var read = await ReadListAsync(page, 0, wanted, projects, cancellationToken).ConfigureAwait(false);
            sessions.AddRange((read["sessions"] as JsonArray ?? []).OfType<JsonObject>().Select(static item => (JsonObject)item.DeepClone()));
            parents.AddRange((read["parents"] as JsonArray ?? []).OfType<JsonObject>().Select(static item => (JsonObject)item.DeepClone()));
            more |= MobileAlta.Flag(read, "hasMore");
        }
        var ordered = sessions.OrderByDescending(static item => MobileAlta.Text(item, "lastActivity"), StringComparer.Ordinal).ThenBy(Key, StringComparer.Ordinal).ToArray();
        var shown = ordered.Skip(offset).Take(limit).ToArray();
        var held = shown.Select(Key).ToHashSet(StringComparer.Ordinal);
        var ancestors = parents.Concat(ordered).Where(item => !held.Contains(Key(item)))
            .Where(item => shown.Any(child => ParentKey(child) == Key(item))).DistinctBy(Key).Take(limit * 8).ToArray();
        return new()
        {
            ["sessions"] = new JsonArray([.. shown.Select(static item => (JsonNode)item)]),
            ["parents"] = new JsonArray([.. ancestors.Select(static item => (JsonNode)item.DeepClone())]),
            ["next"] = offset + shown.Length, ["hasMore"] = shown.Length > 0 && (more || ordered.Length > offset + shown.Length),
            ["machine"] = local.Machine.Name, ["machineId"] = "all", ["error"] = null,
            ["projects"] = new JsonArray([.. (projects ?? []).Select(static id => (JsonNode)id)]),
            ["spaces"] = (await local.Sessions.ListAsync(0, 1, null, cancellationToken).ConfigureAwait(false))["spaces"]?.DeepClone(),
        };
    }

    private static string Key(JsonObject session) => MobileAlta.Text(session, "machineId") + "/" + MobileAlta.Text(session, "id");
    private static string ParentKey(JsonObject session) => (MobileAlta.Text(session, "parentMachineId") ?? MobileAlta.Text(session, "machineId")) + "/" + MobileAlta.Text(session, "parentId");

    private async Task<JsonObject> ReadListAsync(MobileMachinePage page, int offset, int limit, IReadOnlyList<string>? projects, CancellationToken cancellationToken)
    {
        var id = page.Machine.Id;
        if (id != Local && page.Machine.State != "online")
        {
            if (page.Machine.State == "unauthorized") _lastLists.TryRemove(id, out _);
            var result = _lastLists.TryGetValue(id, out var held) ? (JsonObject)held.DeepClone() : new JsonObject { ["sessions"] = new JsonArray(), ["parents"] = new JsonArray() };
            result["next"] = offset + (result["sessions"] as JsonArray)?.Count;
            result["hasMore"] = false;
            result["error"] = page.Machine.State == "unauthorized" ? "This computer is no longer allowed on that machine." : "This machine is offline. Its last known sessions are read-only.";
            QualifyList(result, page.Machine);
            return result;
        }
        if (id != Local && !page.Machine.Features.Contains("sessions.read"))
            return new() { ["sessions"] = new JsonArray(), ["parents"] = new JsonArray(), ["next"] = offset, ["hasMore"] = false, ["error"] = "Update CodeAlta on this machine to open it on the phone." };
        var read = await page.Sessions.ListAsync(offset, limit, projects, cancellationToken, MaxWindow).ConfigureAwait(false);
        if (id == Local)
        {
            var moved = await machines.LocalSessionMovesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var name in new[] { "sessions", "parents" })
                if (read[name] is JsonArray items)
                    for (var index = items.Count - 1; index >= 0; index--)
                        if (MobileAlta.Text(items[index] as JsonObject, "id") is { } sessionId && moved.ContainsKey(sessionId)) items.RemoveAt(index);
        }
        QualifyList(read, page.Machine);
        if (id != Local && read["error"] is null)
        {
            // Only bounded list cards are kept, never timelines, images, tool output, asks or permission requests.
            if (_lastLists.Count < MaxMachines || _lastLists.ContainsKey(id))
            {
                var kept = (JsonObject)read.DeepClone();
                kept["sessions"] = new JsonArray([.. (kept["sessions"] as JsonArray ?? []).Take(200).Select(static item => item?.DeepClone())]);
                kept["parents"] = new JsonArray([.. (kept["parents"] as JsonArray ?? []).Take(200 - kept["sessions"]!.AsArray().Count).Select(static item => item?.DeepClone())]);
                _lastLists[id] = kept;
            }
        }
        return read;
    }

    private void QualifyList(JsonObject list, PluginMachine machine)
    {
        foreach (var item in (list["sessions"] as JsonArray ?? []).Concat(list["parents"] as JsonArray ?? []).OfType<JsonObject>()) Qualify(item, machine);
        list["machineId"] = machine.Id;
        list["machine"] = machine.Name;
    }

    internal JsonObject? Qualify(JsonObject? session, PluginMachine machine)
    {
        if (session is null) return null;
        session["machineId"] = machine.Id;
        session["machine"] = machine.Name;
        session["machineState"] = machine.State;
        session["can"] = Can(machine);
        session["features"] = Features(machine);
        if (session["live"] is JsonObject state)
        {
            var id = MobileAlta.Text(session, "id")!;
            if (_remoteLive.TryGetValue(machine.Id + "/" + id, out var current)) session["live"] = state = (JsonObject)current.DeepClone();
            state["machineId"] = machine.Id;
            state["machine"] = machine.Name;
            if (machine.State != "online") state["canSteer"] = false;
        }
        return session;
    }

    internal async Task<bool> MayReadFrameAsync(JsonObject frame, CancellationToken cancellationToken)
    {
        var id = MobileAlta.Text(frame, "id");
        if (id is null) return false;
        var machine = MobileAlta.Text(frame, "machineId") ?? Local;
        if (machine == Local && await LocalMoveAsync(id, cancellationToken).ConfigureAwait(false) is not null) return false;
        var page = await OpenAsync(machine, cancellationToken).ConfigureAwait(false);
        return page is not null && page.Machine.State == "online" && await page.Sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is not null;
    }

    internal async Task FollowAsync(CancellationToken cancellationToken)
    {
        await foreach (var change in machines.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            ReconcileScope();
            if (!Enabled) continue;
            if (change.SessionId is not { } id)
            {
                if (change.State != "online")
                {
                    foreach (var held in _states.Keys.Where(key => key.StartsWith(change.MachineId + "/", StringComparison.Ordinal))) _states.TryRemove(held, out _);
                    foreach (var held in _remoteLive.Keys.Where(key => key.StartsWith(change.MachineId + "/", StringComparison.Ordinal))) _remoteLive.TryRemove(held, out _);
                    if (change.State == "unauthorized") _lastLists.TryRemove(change.MachineId, out _);
                    live.CloseListeners();
                }
                // A machine that comes back: the pages read their machines and their lists again.
                else live.Broadcast("machines", "{}");
                continue;
            }
            var assigned = spaces.GetAssignedSpaces();
            if (assigned.Count > 0 && (change.SpaceId is null || !assigned.Contains(change.SpaceId, StringComparer.OrdinalIgnoreCase))) continue;
            var key = change.MachineId + "/" + id;
            var previous = _states.GetValueOrDefault(key);
            if (!Remember(key, change.State)) continue;
            // What a machine says when it is reached (again) replaces what was held of its sessions, and notifies of nothing.
            var first = change.Baseline || previous is null;
            if (!first && previous == change.State) continue;
            if (first && change.State == "idle")
            {
                // An idle session is what a read of it says: nothing is held over it.
                if (_remoteLive.TryRemove(key, out _)) live.Broadcast("session", Idle(change, id).ToJsonString());
                continue;
            }
            if (await OpenAsync(change.MachineId, cancellationToken).ConfigureAwait(false) is not { } page || page.Machine.State != "online") continue;
            var state = Idle(change, id);
            state["machine"] = page.Machine.Name;
            state["live"] = change.State == "working" ? MobileLive.Running : MobileLive.Idle;
            state["canSteer"] = change.State == "working" && page.Machine.Permissions.Contains("drive");
            // The line of the open session (FollowSessionAsync) stays while the turn runs.
            if (change.State == "working" && _remoteLive.TryGetValue(key, out var shown))
                foreach (var name in new[] { "kind", "line", "activity" }) state[name] = shown[name]?.DeepClone();
            if (change.State == "waiting")
            {
                JsonObject? approval, ask;
                try
                {
                    approval = await page.Permissions.GetAsync(id, cancellationToken).ConfigureAwait(false);
                    ask = await page.Asks.GetAsync(id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
                {
                    // A machine disconnecting while its pending interaction is read must not stop the other machines.
                    continue;
                }
                state["approving"] = approval?["requests"] is JsonArray { Count: > 0 } requests
                    ? new JsonObject { ["id"] = MobileAlta.Text(requests[0] as JsonObject, "id"), ["count"] = requests.Count } : null;
                state["asking"] = ask?["ask"] is JsonObject pending
                    ? new JsonObject { ["id"] = MobileAlta.Text(pending, "id"), ["revision"] = MobileAlta.Text(pending, "revision"), ["state"] = MobileAlta.Text(pending, "state"), ["immediate"] = MobileAlta.Flag(pending, "immediate") } : null;
                if (!first) push.Remote(change.MachineId, id, state["approving"] is null ? "answer" : "approval");
            }
            else if (!first && previous == "working" && change.State == "idle") push.Remote(change.MachineId, id, "completed");
            Hold(key, state);
            live.Broadcast("session", state.ToJsonString());
        }
    }

    private static JsonObject Idle(PluginMachineChange change, string id) => new()
    {
        ["id"] = id, ["machineId"] = change.MachineId, ["machine"] = null, ["live"] = MobileLive.Idle,
        ["kind"] = null, ["line"] = null, ["at"] = change.At.ToString("O"), ["activity"] = null, ["canSteer"] = false,
    };

    // The last state of each followed session, to tell a transition from a repeat. Idle ones are forgotten first when the
    // table is full: the next change of one is then taken as its first, which notifies of nothing.
    private bool Remember(string key, string state)
    {
        if (_states.Count >= MaxStates && !_states.ContainsKey(key))
        {
            foreach (var idle in _states.Where(static held => held.Value == "idle").Select(static held => held.Key).Take(MaxStates / 4)) _states.TryRemove(idle, out _);
            if (_states.Count >= MaxStates) return false;
        }
        _states[key] = state;
        return true;
    }

    // What is shown over a read of a session. Sessions that neither work nor wait leave first when the table is full.
    private void Hold(string key, JsonObject state)
    {
        if (_remoteLive.Count >= MaxLive && !_remoteLive.ContainsKey(key))
        {
            foreach (var quiet in _remoteLive.Where(static held => MobileAlta.Text(held.Value, "live") != MobileLive.Running && held.Value["approving"] is null && held.Value["asking"] is null)
                .Select(static held => held.Key).ToArray()) _remoteLive.TryRemove(quiet, out _);
            if (_remoteLive.Count >= MaxLive) return;
        }
        _remoteLive[key] = state;
    }

    internal async Task FollowSessionAsync(string machineId, string sessionId, CancellationToken cancellationToken)
    {
        if (machineId == Local || !Enabled || await OpenAsync(machineId, cancellationToken).ConfigureAwait(false) is not { } page
            || await page.Sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is null) return;
        var key = machineId + "/" + sessionId;
        await foreach (var display in machines.WatchSessionAsync(machineId, sessionId, cancellationToken).ConfigureAwait(false))
        {
            if (!Enabled) return;
            var state = new JsonObject
            {
                ["id"] = sessionId, ["machineId"] = machineId, ["machine"] = page.Machine.Name, ["live"] = display.Running ? MobileLive.Running : MobileLive.Idle,
                ["kind"] = display.Kind, ["line"] = MobileLive.Concise(display.Line), ["at"] = display.At.ToString("O"), ["activity"] = null,
                ["canSteer"] = display.Running && page.Machine.Permissions.Contains("drive"),
            };
            // What the session waits for is told by the changes of its machine (FollowAsync): the display keeps it.
            if (_remoteLive.TryGetValue(key, out var held) && _states.GetValueOrDefault(key) == "waiting")
                foreach (var name in new[] { "approving", "asking" }) state[name] = held[name]?.DeepClone();
            Hold(key, state);
            live.Broadcast("session", state.ToJsonString());
        }
    }

    // The admissions of this computer that already ran for an action of the phone: the plugin bridge of a desktop has no
    // ledger of its own, so a request the phone sends again after a lost answer gets the first answer, and runs nothing.
    private sealed class OnceLedger
    {
        private const int Capacity = 256;
        private readonly Lock _gate = new();
        private readonly Dictionary<string, Task<PluginAltaCommandResult>> _runs = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();

        internal Task<PluginAltaCommandResult> RunAsync(string key, Func<Task<PluginAltaCommandResult>> run)
        {
            lock (_gate)
            {
                if (_runs.TryGetValue(key, out var ran)) return ran;
                while (_order.Count >= Capacity) _runs.Remove(_order.Dequeue());
                var task = run();
                _runs[key] = task;
                _order.Enqueue(key);
                // What failed is asked again by a retry.
                _ = task.ContinueWith(done =>
                {
                    if (done.IsCompletedSuccessfully && done.Result.ExitCode == 0) return;
                    lock (_gate) { if (_runs.TryGetValue(key, out var held) && held == done) _runs.Remove(key); }
                }, TaskScheduler.Default);
                return task;
            }
        }
    }

    private sealed class OnceAlta(IPluginAltaService inner, OnceLedger ledger) : IPluginAltaService
    {
        public ValueTask<PluginAltaCommandResult> InvokeAsync(IReadOnlyList<string> args, string? stdin = null,
            PluginAltaInvocationOptions? options = null, CancellationToken cancellationToken = default)
            => options?.RequestId is { } key && args is ["session", "send" or "queue" or "steer" or "create" or "abort", ..]
                // The first run is not tied to the request that started it: its retry waits for the same answer.
                ? new(ledger.RunAsync(key, () => inner.InvokeAsync(args, stdin, options, CancellationToken.None).AsTask()))
                : inner.InvokeAsync(args, stdin, options, cancellationToken);
    }

    // One HTTP action can create a session and send its first prompt. Keep distinct, repeatable keys for those two
    // admissions. The browser chooses the action id once, before any sign-in retry; no caller supplies remote arguments.
    private sealed class RequestAlta(IPluginAltaService inner, string requestId) : IPluginAltaService
    {
        public ValueTask<PluginAltaCommandResult> InvokeAsync(IReadOnlyList<string> args, string? stdin = null,
            PluginAltaInvocationOptions? options = null, CancellationToken cancellationToken = default)
            => inner.InvokeAsync(args, stdin, (options ?? new()) with { RequestId = requestId + ":" + string.Join(':', args.Take(2)) }, cancellationToken);
    }
}
