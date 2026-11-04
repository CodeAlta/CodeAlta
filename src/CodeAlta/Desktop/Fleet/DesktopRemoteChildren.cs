using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Fleet;

/// <summary>A session an agent of this computer started on another machine, as this computer records it.</summary>
/// <param name="ChildSessionId">The session, on the machine.</param>
/// <param name="MachineId">The device id of the machine.</param>
/// <param name="MachineName">Its name.</param>
/// <param name="ParentSessionId">The session of the agent, here.</param>
/// <param name="ForScript">Whether a script waits for its answer, rather than its parent.</param>
/// <param name="CreatedAt">When it was started.</param>
internal sealed record DesktopRemoteChild(string ChildSessionId, string MachineId, string MachineName, string ParentSessionId, bool ForScript,
    DateTimeOffset CreatedAt);

/// <summary>
/// The sessions the agents of this computer start on other machines and the prompts they send there
/// (<c>doc/fleet/sessions-across-machines.md</c>): each request is signed with the key of this device, which its channels to
/// those machines authenticate, with the limits of the session of the agent; the machine decides by its own policy. The
/// lineage of each such session is recorded here too, and a loop takes what those sessions say to their parents from
/// their machines and delivers it as the answer of a child of this computer is delivered, then acknowledges it. A delivery
/// reaches only the parent recorded for its child on its machine, and only once: the deliveries delivered are recorded
/// too, so that one a machine gives again (a lost acknowledgement, a restart) is acknowledged again, not delivered.
/// </summary>
internal sealed class DesktopRemoteChildren : IRemoteChildSessions, IAsyncDisposable
{
    /// <summary>The most remote children this computer remembers; the oldest go first.</summary>
    public const int MaxRemembered = 500;

    /// <summary>The most deliveries this computer remembers having delivered; the oldest go first.</summary>
    public const int MaxReceived = 2000;

    private static readonly TimeSpan OnlineWait = TimeSpan.FromSeconds(10);
    private readonly CodeAltaHost _host;
    private readonly DesktopFleet _fleet;
    private readonly string _machineName;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteChildAnswer>> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Desktop.Fleet");
    private readonly string _receivedPath;
    private List<DesktopRemoteChild> _children;
    // The deliveries this computer delivered, by machine and id, the oldest first: one taken again (its acknowledgement
    // was lost, or this computer stopped before it) is acknowledged again, never delivered twice.
    private readonly List<string> _received;
    private readonly HashSet<string> _receivedSet;
    // The creations in progress, by machine: a delivery of a child this computer does not know, from a machine a child is
    // being created on, may be that child's, whose creation has not returned yet. It stays in the outbox of the machine.
    private readonly Dictionary<string, int> _creating = new(StringComparer.Ordinal);
    private Task _loop = Task.CompletedTask;

    /// <summary>Creates the service of an instance; <see cref="Start"/> starts the loop of its deliveries.</summary>
    /// <param name="host">The runtime of this computer.</param>
    /// <param name="fleet">Its machines, and the key that signs.</param>
    /// <param name="machineName">The name of this computer, which the machines show as the machine of the parent.</param>
    /// <param name="time">The clock; null for the system's.</param>
    /// <param name="interval">How often the machines are asked for deliveries; null for two seconds.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public DesktopRemoteChildren(CodeAltaHost host, DesktopFleet fleet, string machineName, TimeProvider? time = null, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        _host = host;
        _fleet = fleet;
        _machineName = machineName;
        _time = time ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _path = Path.Combine(host.CatalogOptions.FleetRoot, "agents", "children.json");
        _receivedPath = Path.Combine(host.CatalogOptions.FleetRoot, "agents", "received.json");
        _children = Read(_path, DesktopRemoteChildrenJson.Default.ListDesktopRemoteChild);
        _received = Read(_receivedPath, DesktopRemoteChildrenJson.Default.ListString);
        _receivedSet = new HashSet<string>(_received, StringComparer.Ordinal);
    }

    /// <summary>Raised, on a thread of the pool, when a remote child was started or said something.</summary>
    public event Action? Changed;

    /// <summary>Gets the remote children this computer started, the oldest first.</summary>
    public IReadOnlyList<DesktopRemoteChild> Children { get { lock (_gate) return [.. _children]; } }

    /// <summary>
    /// Gets or sets what is called once a machine created a child, before its lineage is recorded here, with the id of the
    /// child; tests hold a creation there. Null calls nothing.
    /// </summary>
    internal Func<string, CancellationToken, Task>? Created { get; set; }

    /// <summary>
    /// Gets or sets the signed policies of the servers of this computer (<c>doc/fleet/security-and-privacy.md</c>, policies and
    /// the kill switch): a stop that reaches it, or rules of the organization of a space that keep agents from other machines,
    /// refuse here what an agent of this computer would start or send elsewhere. Null while it joined no server.
    /// </summary>
    internal CodeAlta.Fleet.Server.FleetPolicyGuard? Policy { get; set; }

    // Why the policy refuses work of an agent of this computer on another machine, in a space; null when it allows it.
    private string? PolicyRefusal(string? spaceId)
    {
        if (Policy is not { } policy) return null;
        if (policy.Halt is { } halt) return halt.Describe(_machineName);
        return policy.RulesFor(spaceId) is { AgentStarts: false } ? "The policy of the organization lets no agent start work on another machine, nor prompt it." : null;
    }

    /// <summary>Starts the loop that takes the deliveries of the machines.</summary>
    public void Start() => _loop = Task.Run(RunAsync);

    /// <inheritdoc />
    public IReadOnlyList<RemoteMachine> ListMachines()
        => [.. _fleet.Hosts.Select(host => new RemoteMachine(host.MachineId!, NameOf(host), RemoteSessionHost.Describe(host.State)))];

    /// <inheritdoc />
    public RemoteMachine? FindMachineOf(string sessionId)
        => _fleet.Hosts.FirstOrDefault(host => host.Lists(sessionId)) is { } found ? new RemoteMachine(found.MachineId!, NameOf(found), RemoteSessionHost.Describe(found.State)) : null;

    /// <inheritdoc />
    public string? FindParentOf(string childSessionId)
        => Children.FirstOrDefault(child => string.Equals(child.ChildSessionId, childSessionId, StringComparison.OrdinalIgnoreCase))?.ParentSessionId;

    /// <inheritdoc />
    public async Task<RemoteChildResult> CreateAsync(RemoteChildRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Resolve(request.Machine) is not { } host) return Refused("machine_not_found", $"No machine is named '{request.Machine}': `alta session create --machine-list` lists them.");
        // A sandbox goes with what it confines here, which the machine holds the child to: never its name alone.
        if (request.Sandbox is not null && request.Confinement is null)
            return Refused("agent_request_denied", $"The sandbox '{request.Sandbox}' of the session cannot be used here: it starts nothing on another machine.");
        if (PolicyRefusal(request.SpaceId) is { } policyRefusal) return Refused("policy_refused", policyRefusal);
        var name = NameOf(host);
        if (await OnlineAsync(host, cancellationToken).ConfigureAwait(false) is null) return Refused("machine_offline", $"{name} is {RemoteSessionHost.Describe(host.State)}.", name);
        try
        {
            var info = await host.ReadInfoAsync(cancellationToken).ConfigureAwait(false);
            if (!info.Spaces.Any(space => string.Equals(space.Id, request.SpaceId, StringComparison.OrdinalIgnoreCase)))
                return Refused("space_missing", $"{name} has no space '{request.SpaceId}': a session of another machine is in the space of its parent.", name);
            var (projectId, refusal) = await ResolveProjectAsync(request, info, name, cancellationToken).ConfigureAwait(false);
            if (refusal is not null) return refusal;
            var signed = _fleet.SignAgentRequest(new FleetAgentRequest(FleetAgentRequest.CurrentVersion, FleetAgentRequestKinds.Start, host.MachineId!, "", "",
                _time.GetUtcNow(), "", request.ParentSessionId, _machineName, request.SpaceId, null, projectId,
                request.Prompt is { } prompt ? FleetAgentRequestDocuments.HashText(prompt) : null,
                new FleetAgentLimits(request.DisabledTools, request.PermissionMode, request.Sandbox, request.SubSessions, 1,
                    request.Confinement is { } confinement ? FleetSandboxFloors.ToWire(confinement) : null)));
            var selection = request.ProviderKey is null && request.ModelId is null && request.ReasoningEffort is null && request.AgentPromptId is null
                ? null : new FleetModelSelection(request.ProviderKey, request.ModelId, request.AgentPromptId, request.ReasoningEffort);
            // The answer of a child a script waits for is kept from the start: it may come before the script waits. Until its
            // lineage is recorded, a delivery of a child unknown here from that machine waits in its outbox (DeliverAsync).
            FleetCreateSessionResult created;
            Creating(host.MachineId!, 1);
            try
            {
                created = await host.CreateRemoteSessionAsync(new FleetCreateSessionParams(projectId, request.SpaceId, request.Title, request.Prompt, selection)
                    { Agent = signed }, Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
                if (Created is { } hold) await hold(created.SessionId, cancellationToken).ConfigureAwait(false);
                if (request.ForScript) _answers.TryAdd(created.SessionId, NewAnswer());
                Remember(new DesktopRemoteChild(created.SessionId, host.MachineId!, name, request.ParentSessionId, request.ForScript, _time.GetUtcNow()));
            }
            finally
            {
                Creating(host.MachineId!, -1);
            }

            Log($"Started session {created.SessionId} on {name} for the agent session {request.ParentSessionId} (mode {created.PermissionMode ?? "provider"}, sandbox {created.Sandbox ?? "none"}).");
            return new RemoteChildResult("created", null, null)
            {
                SessionId = created.SessionId,
                MachineId = host.MachineId,
                MachineName = name,
                ProjectId = projectId,
                ProviderKey = created.ProviderKey,
                PermissionMode = created.PermissionMode,
                Sandbox = created.Sandbox,
                PromptSent = created.Send is { Status: "accepted" or "replay" },
            };
        }
        catch (FleetException exception)
        {
            return Refused(Code(exception.Error.Code), exception.Error.Message ?? exception.Message, name);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException or TimeoutException)
        {
            return Refused("machine_offline", exception.Message, name);
        }
    }

    /// <inheritdoc />
    public async Task<RemoteChildAnswer> WaitForAnswerAsync(string childSessionId, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childSessionId);
        if (!_answers.TryGetValue(childSessionId, out var answer)) return new RemoteChildAnswer("refused", null, $"No script of this computer waits for '{childSessionId}'.");
        try
        {
            return timeout is { } limit ? await answer.Task.WaitAsync(limit, _time, cancellationToken).ConfigureAwait(false) : await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new RemoteChildAnswer("timeout", null, $"The session did not answer within {timeout}.");
        }
        finally
        {
            if (answer.Task.IsCompleted) _answers.TryRemove(childSessionId, out _);
        }
    }

    /// <inheritdoc />
    public async Task<RemoteMessageResult> SendAsync(RemoteMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Machine is { } machine ? Resolve(machine) : _fleet.Hosts.FirstOrDefault(value => value.Lists(request.SessionId));
        if (host is null) return new("refused", "session_missing", $"No machine this computer reaches lists the session '{request.SessionId}'.", request.Machine);
        if (PolicyRefusal(request.SpaceId) is { } refusal) return new("refused", "policy_refused", refusal, request.Machine);
        var name = NameOf(host);
        if (await OnlineAsync(host, cancellationToken).ConfigureAwait(false) is not { } client) return new("refused", "machine_offline", $"{name} is {RemoteSessionHost.Describe(host.State)}.", name);
        try
        {
            var kind = request.Kind;
            FleetSessionState? state = null;
            if (kind is FleetAgentRequestKinds.Send or FleetAgentRequestKinds.Steer)
            {
                // As on one machine: a prompt to a session at work steers its turn; to an idle one, starts a turn.
                state = await client.GetSessionStateAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
                if (kind == FleetAgentRequestKinds.Send && state.ActiveRunId is not null) kind = FleetAgentRequestKinds.Steer;
                if (kind == FleetAgentRequestKinds.Steer && (state.ActiveRunId is null || state.AttachmentGeneration is null))
                    return new("refused", "not_running", $"The session '{request.SessionId}' on {name} has no turn at work to steer.", name);
            }

            var signed = _fleet.SignAgentRequest(new FleetAgentRequest(FleetAgentRequest.CurrentVersion, kind, host.MachineId!, "", "", _time.GetUtcNow(), "",
                request.SenderSessionId, _machineName, request.SpaceId, request.SessionId, null, FleetAgentRequestDocuments.HashText(request.Text), null,
                FleetSandboxFloors.ToWire(request.Floor)));
            var key = Guid.NewGuid().ToString("N");
            switch (kind)
            {
                case FleetAgentRequestKinds.Queue:
                    await client.QueueAsync(new FleetQueueParams(request.SessionId, request.Text) { Agent = signed }, key, cancellationToken).ConfigureAwait(false);
                    return new("queued", null, null, name);
                case FleetAgentRequestKinds.Steer:
                    var steered = await client.SteerAsync(new FleetSteerParams(request.SessionId, request.Text, state!.RuntimeInstanceId, state.AttachmentGeneration!.Value,
                        state.ActiveRunId!) { Agent = signed }, key, cancellationToken).ConfigureAwait(false);
                    return Admission(steered, name, "steered");
                default:
                    var sent = await client.SendAsync(new FleetSendParams(request.SessionId, request.Text) { Agent = signed }, key, cancellationToken).ConfigureAwait(false);
                    return Admission(sent, name, "accepted");
            }
        }
        catch (FleetException exception)
        {
            return new("refused", Code(exception.Error.Code), exception.Error.Message, name);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException or TimeoutException)
        {
            return new("refused", "machine_offline", exception.Message, name);
        }
    }

    /// <inheritdoc />
    public async Task<RemoteSessionReport?> ReportAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_fleet.Hosts.FirstOrDefault(value => value.Lists(sessionId)) is not { } host) return null;
        var summary = host.Listing.Sessions.First(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        string? last = null;
        if (host.CurrentClient is { } client)
        {
            try
            {
                var window = await client.ReadHistoryAsync(new FleetHistoryParams(sessionId), cancellationToken).ConfigureAwait(false);
                last = window.Entries.LastOrDefault(static entry => entry.Type == "content" && string.Equals(entry.Kind, "Assistant", StringComparison.OrdinalIgnoreCase))?.Text;
            }
            catch (Exception exception) when (exception is FleetException or ObjectDisposedException or IOException or TimeoutException)
            {
                // The state of the list is the report: what it said last is read when the machine answers.
            }
        }

        return new RemoteSessionReport(sessionId, host.MachineId!, NameOf(host), summary.Title, summary.State, summary.ParentSessionId, last, summary.UpdatedAt);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        foreach (var (_, answer) in _answers) answer.TrySetResult(new RemoteChildAnswer("failed", null, "CodeAlta closed before the session answered."));
        _stop.Dispose();
    }

    /// <summary>Takes once what the machines of the remote children kept for this computer, delivers it, and acknowledges it.</summary>
    /// <param name="cancellationToken">Cancels the round.</param>
    /// <returns>How many deliveries were delivered.</returns>
    internal async Task<int> DeliverOnceAsync(CancellationToken cancellationToken)
    {
        var delivered = 0;
        foreach (var machineId in Children.Select(static child => child.MachineId).Distinct(StringComparer.Ordinal))
        {
            if (_fleet.Find(machineId) is not { CurrentClient: { } client } host) continue;
            try
            {
                var taken = await client.TakeAgentDeliveriesAsync(null, cancellationToken).ConfigureAwait(false);
                if (taken.Deliveries.Count == 0) continue;
                var done = new List<string>(taken.Deliveries.Count);
                var received = new List<string>(taken.Deliveries.Count);
                foreach (var delivery in taken.Deliveries)
                {
                    var key = ReceivedKey(machineId, delivery.Id);
                    if (HasReceived(key) || received.Contains(key, StringComparer.Ordinal))
                    {
                        // Delivered at an earlier round, whose acknowledgement did not reach the machine, or earlier in this one.
                        done.Add(delivery.Id);
                        continue;
                    }

                    if (await DeliverAsync(delivery, machineId, NameOf(host), key).ConfigureAwait(false) is not { } outcome) continue;
                    done.Add(delivery.Id);
                    if (outcome) received.Add(key);
                }

                // Kept before the acknowledgement: what a machine gives again after a lost one, or after a restart, is known.
                Receive(received);
                if (done.Count > 0)
                {
                    await client.AcknowledgeAgentDeliveriesAsync(done, cancellationToken).ConfigureAwait(false);
                    delivered += received.Count;
                    Changed?.Invoke();
                }
            }
            catch (Exception exception) when (exception is FleetException or ObjectDisposedException or IOException or TimeoutException)
            {
                // Taken again at the next round, from the outbox of the machine.
                Log($"The deliveries of {NameOf(host)} could not be taken: {exception.Message}");
            }
        }

        return delivered;
    }

    /// <summary>
    /// Finds the child a delivery of a machine speaks for: the one this computer started on that machine, for the parent
    /// the delivery names. The machine says who the parent is, and is not believed: a delivery of a child this computer
    /// did not start there, or for another parent, has no lineage and reaches no session.
    /// </summary>
    /// <param name="children">The remote children this computer recorded.</param>
    /// <param name="machineId">The device id of the machine the delivery was taken from.</param>
    /// <param name="delivery">The delivery.</param>
    /// <returns>The recorded child, or null when the delivery matches none.</returns>
    internal static DesktopRemoteChild? FindLineage(IReadOnlyList<DesktopRemoteChild> children, string machineId, FleetAgentDelivery delivery)
        => children.FirstOrDefault(child => string.Equals(child.MachineId, machineId, StringComparison.Ordinal)
            && string.Equals(child.ChildSessionId, delivery.ChildSessionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(child.ParentSessionId, delivery.ParentSessionId, StringComparison.OrdinalIgnoreCase));

    // A delivery goes to the script that waits for it, else to the parent this computer recorded, which takes it as the
    // answer of a child: true once delivered, false when it is dropped (acknowledged all the same), null to keep it in the
    // outbox of the machine for the next round.
    private async Task<bool?> DeliverAsync(FleetAgentDelivery delivery, string machineId, string machineName, string key)
    {
        DesktopRemoteChild? child;
        bool creating;
        // Read together: a creation records its lineage before it ends, so one of the two is always seen.
        lock (_gate)
        {
            child = FindLineage(_children, machineId, delivery);
            creating = _creating.GetValueOrDefault(machineId) > 0;
        }

        if (child is null && creating)
        {
            Log($"{machineName} gave a {delivery.Kind} of {delivery.ChildSessionId}, unknown here while a session is being created there: it is taken again at the next round.");
            return null;
        }

        if (child is null)
        {
            Log($"{machineName} gave a {delivery.Kind} of {delivery.ChildSessionId} for {delivery.ParentSessionId}, which this computer did not start there for that session: it is dropped.");
            return false;
        }

        if (child.ForScript)
        {
            if (delivery.Kind is "answer" or "error" && _answers.TryGetValue(child.ChildSessionId, out var answer))
                answer.TrySetResult(delivery.Kind == "answer" ? new RemoteChildAnswer("completed", delivery.Body, null) : new RemoteChildAnswer("failed", null, delivery.Body));
            return true;
        }

        try
        {
            if (!await _host.RuntimeService.DeliverRemoteChildNotificationAsync(new RemoteChildNotification(child.ChildSessionId, delivery.ChildProjectId, machineName,
                    child.ParentSessionId, delivery.Kind, delivery.Body, delivery.RunId, delivery.ContentId) { DeliveryId = key }).ConfigureAwait(false))
                Log($"The parent session {child.ParentSessionId} of {child.ChildSessionId} on {machineName} is gone: its {delivery.Kind} is dropped.");
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log($"The {delivery.Kind} of {child.ChildSessionId} on {machineName} could not be delivered to {child.ParentSessionId}: {exception.Message}");
            return null;
        }
    }

    // Counts a creation of a child on a machine that starts (1) or ends (-1).
    private void Creating(string machineId, int change)
    {
        lock (_gate)
        {
            var count = _creating.GetValueOrDefault(machineId) + change;
            if (count > 0) _creating[machineId] = count;
            else _creating.Remove(machineId);
        }
    }

    private static string ReceivedKey(string machineId, string deliveryId) => machineId + "/" + deliveryId;

    private bool HasReceived(string key)
    {
        lock (_gate) return _receivedSet.Contains(key);
    }

    private void Receive(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return;
        lock (_gate)
        {
            foreach (var key in keys)
            {
                if (_receivedSet.Add(key)) _received.Add(key);
            }

            if (_received.Count > MaxReceived)
            {
                foreach (var forgotten in _received.Take(_received.Count - MaxReceived)) _receivedSet.Remove(forgotten);
                _received.RemoveRange(0, _received.Count - MaxReceived);
            }

            try
            {
                Write(_receivedPath, JsonSerializer.SerializeToUtf8Bytes(_received, DesktopRemoteChildrenJson.Default.ListString));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Held in memory meanwhile; a parent also takes a delivery once by its id (SessionRuntimeService).
                Log($"The deliveries received could not be written: {exception.Message}");
            }
        }
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, _time, token).ConfigureAwait(false);
                await DeliverOnceAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log($"A round of deliveries failed: {exception.Message}");
            }
        }
    }

    private async Task<(string? ProjectId, RemoteChildResult? Refusal)> ResolveProjectAsync(RemoteChildRequest request, FleetNodeInfo info, string name,
        CancellationToken cancellationToken)
    {
        if (request.Global) return (null, null);
        if (request.Project is { } asked)
        {
            var named = info.Projects.Where(project => string.Equals(project.Id, asked, StringComparison.OrdinalIgnoreCase)
                || string.Equals(project.Name, asked, StringComparison.OrdinalIgnoreCase)).ToArray();
            return named.Length == 1 ? (named[0].Id, null)
                : (null, Refused("project_missing", $"{name} has {(named.Length == 0 ? "no" : "several")} project named '{asked}': `alta session create --machine-list` names its projects.", name));
        }

        // The project of the parent, by its repository: the checkout of the same repository on the machine.
        var parent = await _host.RuntimeService.TryGetResumableSessionAsync(request.ParentSessionId, cancellationToken).ConfigureAwait(false);
        if (parent?.ProjectRef is not { } localProjectId) return (null, null);
        var local = await _host.ProjectCatalog.GetByIdAsync(localProjectId, cancellationToken).ConfigureAwait(false);
        var location = local is null ? null : await _fleet.Locations.GetAsync(local.ProjectPath, cancellationToken).ConfigureAwait(false);
        var same = location is null ? [] : info.Projects.Where(project => string.Equals(project.RepositoryKey, location.Key, StringComparison.Ordinal)
            && string.Equals(project.RepositoryPath ?? "", location.Path, StringComparison.Ordinal)).ToArray();
        return same.Length >= 1 ? (same[0].Id, null)
            : (null, Refused("project_missing", $"{name} has no project of the repository of this session: name one of its projects with --project, or --global.", name));
    }

    private RemoteSessionHost? Resolve(string machine)
    {
        var name = machine.Trim();
        var found = _fleet.Hosts.Where(host => string.Equals(host.MachineId, name, StringComparison.Ordinal) || string.Equals(NameOf(host), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return found.Length == 1 ? found[0] : null;
    }

    private static async Task<CodeAlta.Fleet.Client.FleetNodeClient?> OnlineAsync(RemoteSessionHost host, CancellationToken cancellationToken)
    {
        if (host.CurrentClient is { } client) return client;
        if (host.State == RemoteMachineState.Unauthorized) return null;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(OnlineWait);
        try { return await host.WaitOnlineAsync(wait.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (ObjectDisposedException) { return null; }
    }

    private static string NameOf(RemoteSessionHost host) => host.Listing.Info?.Name ?? host.Machine.Name;

    private static RemoteMessageResult Admission(FleetCommandResult result, string machine, string accepted)
        => result.Status is "accepted" or "replay" ? new(accepted, null, null, machine) { RunId = result.RunId }
            : new("refused", result.Status, $"The session did not take the prompt ({result.Status}).", machine);

    private static string Code(string code) => code switch
    {
        FleetNodeErrorCodes.AgentSessionsOff => "agent_sessions_off",
        FleetNodeErrorCodes.AgentRequestInvalid => "agent_request_invalid",
        FleetNodeErrorCodes.AgentRequestDenied => "agent_request_denied",
        FleetNodeErrorCodes.Unavailable => "unavailable",
        FleetNodeErrorCodes.SessionNotFound => "session_missing",
        FleetErrorCodes.Forbidden => "forbidden",
        FleetErrorCodes.ServerBusy => "rate_limited",
        _ => code,
    };

    private static RemoteChildResult Refused(string code, string message, string? machine = null) => new("refused", code, message) { MachineName = machine };

    private static TaskCompletionSource<RemoteChildAnswer> NewAnswer() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Remember(DesktopRemoteChild child)
    {
        lock (_gate)
        {
            _children.Add(child);
            if (_children.Count > MaxRemembered) _children.RemoveRange(0, _children.Count - MaxRemembered);
            try
            {
                Write(_path, JsonSerializer.SerializeToUtf8Bytes(_children, DesktopRemoteChildrenJson.Default.ListDesktopRemoteChild));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Held in memory meanwhile: its deliveries are still taken while this instance runs.
                Log($"The remote children could not be written: {exception.Message}");
            }
        }

        Changed?.Invoke();
    }

    // Written beside, then moved over: a reader never sees half a file.
    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + ".tmp";
        File.WriteAllBytes(staging, bytes);
        File.Move(staging, path, overwrite: true);
    }

    private static List<T> Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> type)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), type) ?? [] : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Log(string message)
    {
        if (LogManager.IsInitialized) _logger.Info(message);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<DesktopRemoteChild>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class DesktopRemoteChildrenJson : JsonSerializerContext;
