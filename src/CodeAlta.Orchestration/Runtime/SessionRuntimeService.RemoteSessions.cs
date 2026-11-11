using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// What a session an agent of another machine started says to its parent, which runs on that machine
/// (<c>doc/fleet/sessions-across-machines.md</c>): its final answer, a failure, or a <c>&lt;notify-parent&gt;</c> update.
/// </summary>
/// <param name="ChildSessionId">The session that speaks, on this machine.</param>
/// <param name="ChildProjectId">Its project on this machine, or null.</param>
/// <param name="ParentSessionId">The parent, on the other machine.</param>
/// <param name="ParentMachineId">The device id of the machine of the parent.</param>
/// <param name="Kind"><c>answer</c>, <c>error</c>, <c>progress</c>, <c>note</c>, <c>result</c> or <c>handoff</c>.</param>
/// <param name="Body">What it says.</param>
/// <param name="RunId">The run it is about, or null.</param>
/// <param name="ContentId">The content it comes from, which keeps a delivery once.</param>
public sealed record RemoteParentNotification(string ChildSessionId, string? ChildProjectId, string ParentSessionId, string ParentMachineId,
    string Kind, string Body, string? RunId, string ContentId);

/// <summary>
/// Keeps what sessions say to parents of other machines until those machines take it (a node's outbox): set on the runtime
/// of a host that accepts sessions started by agents of other machines.
/// </summary>
public interface IRemoteParentNotifications
{
    /// <summary>Keeps a notification for the machine of its parent; it must survive a restart of the host.</summary>
    /// <param name="notification">The notification.</param>
    /// <returns>A task that completes once it is kept.</returns>
    Task EnqueueAsync(RemoteParentNotification notification);
}

/// <summary>What a session of another machine, a child of a session of this one, says to its parent.</summary>
/// <param name="ChildSessionId">The child, on the other machine.</param>
/// <param name="ChildProjectId">Its project there, or null.</param>
/// <param name="MachineName">The name of that machine.</param>
/// <param name="ParentSessionId">The parent, on this machine.</param>
/// <param name="Kind"><c>answer</c>, <c>error</c>, or an update (<c>progress</c>...).</param>
/// <param name="Body">What it says.</param>
/// <param name="RunId">The run of the child it is about, or null.</param>
/// <param name="ContentId">The content it comes from.</param>
public sealed record RemoteChildNotification(string ChildSessionId, string? ChildProjectId, string MachineName, string ParentSessionId,
    string Kind, string Body, string? RunId, string ContentId)
{
    /// <summary>
    /// Gets the identity of the delivery, the same each time the machine gives it again (its machine and its id): a parent
    /// that took a delivery of that identity is not prompted again with it, across retries and restarts. Null to deliver
    /// it as it comes.
    /// </summary>
    public string? DeliveryId { get; init; }
}

/// <summary>A machine an agent can start sessions on, or send prompts to.</summary>
/// <param name="Id">Its device id.</param>
/// <param name="Name">Its name.</param>
/// <param name="State"><c>online</c>, <c>connecting</c>, <c>offline</c> or <c>unauthorized</c>.</param>
public sealed record RemoteMachine(string Id, string Name, string State);

/// <summary>
/// A session an agent asks another machine to start, as the child of its own session, with what the parent has at most
/// (<c>doc/fleet/readme.md</c>, "No more power elsewhere").
/// </summary>
/// <param name="ParentSessionId">The session of the agent.</param>
/// <param name="Machine">The machine: its name or its device id.</param>
public sealed record RemoteChildRequest(string ParentSessionId, string Machine)
{
    /// <summary>Gets the first prompt, or null to create a session that waits for one.</summary>
    public string? Prompt { get; init; }

    /// <summary>Gets the project of the machine (its id or name); null for the project of the same repository as the parent's.</summary>
    public string? Project { get; init; }

    /// <summary>Gets whether the session is outside a project.</summary>
    public bool Global { get; init; }

    /// <summary>Gets the title, or null.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the space of the parent: the child is in the same one.</summary>
    public required string SpaceId { get; init; }

    /// <summary>Gets the provider of the machine, or null for its default.</summary>
    public string? ProviderKey { get; init; }

    /// <summary>Gets the model, or null for the provider's.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets the reasoning effort, or null.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Gets the agent prompt, or null for <c>default</c>.</summary>
    public string? AgentPromptId { get; init; }

    /// <summary>Gets the tools, or patterns over tool names, the child does without: at least those of the parent.</summary>
    public IReadOnlyList<string> DisabledTools { get; init; } = [];

    /// <summary>Gets the widest permission mode the child may run in; null for <c>default</c>.</summary>
    public string? PermissionMode { get; init; }

    /// <summary>Gets the sandbox profile the child runs in at least; null when the parent runs without one.</summary>
    public string? Sandbox { get; init; }

    /// <summary>
    /// Gets what <see cref="Sandbox"/> confines on this computer: the machine runs the child in a profile at least as
    /// strict, or refuses it (<c>doc/fleet/security-and-privacy.md</c>, "Remote execution", rule 8). Set with <see cref="Sandbox"/>.
    /// </summary>
    public SandboxConfinement? Confinement { get; init; }

    /// <summary>Gets whether the child may start sessions of its own.</summary>
    public bool SubSessions { get; init; } = true;

    /// <summary>Gets whether a script waits for the answer (<c>agent()</c>): it goes to the script, not to the parent as a prompt.</summary>
    public bool ForScript { get; init; }
}

/// <summary>What became of a <see cref="RemoteChildRequest"/>.</summary>
/// <param name="Status"><c>created</c>, or <c>refused</c>.</param>
/// <param name="Code">Why it was refused (<c>machine_not_found</c>, <c>machine_offline</c>, <c>agent_sessions_off</c>, <c>project_missing</c>...), or null.</param>
/// <param name="Message">What to tell the agent, or null.</param>
public sealed record RemoteChildResult(string Status, string? Code, string? Message)
{
    /// <summary>Gets the new session, once created.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the device id of the machine.</summary>
    public string? MachineId { get; init; }

    /// <summary>Gets the name of the machine.</summary>
    public string? MachineName { get; init; }

    /// <summary>Gets the project of the machine it works in, or null.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the provider it runs on.</summary>
    public string? ProviderKey { get; init; }

    /// <summary>Gets the permission mode it runs in, as the machine decided it.</summary>
    public string? PermissionMode { get; init; }

    /// <summary>Gets the sandbox profile it runs in, or null for none.</summary>
    public string? Sandbox { get; init; }

    /// <summary>Gets whether its first prompt was handed over.</summary>
    public bool PromptSent { get; init; }

    /// <summary>
    /// Gets the place of its first prompt in the queue of the machine (1 for the next one) when the machine runs as many sessions
    /// as its owner lets it: handed over, sent once its turn comes. Null when it was sent at once, or not sent.
    /// </summary>
    public int? QueuedPosition { get; init; }
}

/// <summary>The answer of a session of another machine a script waits for.</summary>
/// <param name="Status"><c>completed</c>, <c>failed</c>, <c>timeout</c> or <c>refused</c>.</param>
/// <param name="Answer">Its final answer, or null.</param>
/// <param name="Failure">What went wrong, or null.</param>
public sealed record RemoteChildAnswer(string Status, string? Answer, string? Failure);

/// <summary>A prompt an agent sends to a session of another machine.</summary>
/// <param name="SenderSessionId">The session of the agent.</param>
/// <param name="SessionId">The session of the other machine.</param>
/// <param name="Text">The prompt.</param>
/// <param name="Kind"><c>send</c>, <c>queue</c> or <c>steer</c>.</param>
public sealed record RemoteMessageRequest(string SenderSessionId, string SessionId, string Text, string Kind)
{
    /// <summary>Gets the space of the sender: the session reached must be in the same one.</summary>
    public required string SpaceId { get; init; }

    /// <summary>Gets the machine (name or id), or null for the one that lists the session.</summary>
    public string? Machine { get; init; }

    /// <summary>
    /// Gets the floor of the sender: the machine refuses the prompt for a session that runs in a wider permission mode,
    /// or less confined, than the sender (<c>doc/fleet/security-and-privacy.md</c>, "Remote execution", rule 8).
    /// </summary>
    public required SessionFleetFloor Floor { get; init; }
}

/// <summary>What became of a <see cref="RemoteMessageRequest"/>.</summary>
/// <param name="Status"><c>accepted</c>, <c>queued</c>, or <c>refused</c>.</param>
/// <param name="Code">Why it was refused, or the admission of the node (<c>busy</c>...); null when accepted.</param>
/// <param name="Message">What to tell the agent, or null.</param>
/// <param name="MachineName">The machine of the session, when known.</param>
public sealed record RemoteMessageResult(string Status, string? Code, string? Message, string? MachineName)
{
    /// <summary>Gets the run the prompt started or steered, once known.</summary>
    public string? RunId { get; init; }
}

/// <summary>What a session of another machine is doing, and what it said last.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="MachineId">The device id of its machine.</param>
/// <param name="MachineName">The name of its machine.</param>
/// <param name="Title">Its title.</param>
/// <param name="State"><c>working</c>, <c>waiting</c>, <c>idle</c> or <c>archived</c>.</param>
/// <param name="ParentSessionId">Its parent, or null.</param>
/// <param name="LastAnswer">Its last assistant message, cut to a bound; null when it has none.</param>
/// <param name="UpdatedAt">When it last changed.</param>
public sealed record RemoteSessionReport(string SessionId, string MachineId, string MachineName, string Title, string State, string? ParentSessionId,
    string? LastAnswer, DateTimeOffset UpdatedAt);

/// <summary>
/// The sessions of other machines the agents of this host start and reach (<c>alta session create --machine</c>, the
/// <c>agent({ machine })</c> of scripts, <c>alta session send</c> to a session of another machine), within the rules of
/// <c>doc/fleet/access-control.md</c>: each request is signed by the device of the person, and the machine decides.
/// </summary>
public interface IRemoteChildSessions
{
    /// <summary>Lists the machines this host reaches.</summary>
    /// <returns>The machines.</returns>
    IReadOnlyList<RemoteMachine> ListMachines();

    /// <summary>Finds the machine that lists a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The machine, or null when no machine this host reaches lists it.</returns>
    RemoteMachine? FindMachineOf(string sessionId);

    /// <summary>Finds the session of this host that started a session of another machine.</summary>
    /// <param name="childSessionId">The session of the other machine.</param>
    /// <returns>The session of this host that started it, as this host recorded it; null when no session of this host did.</returns>
    string? FindParentOf(string childSessionId);

    /// <summary>Starts a session on another machine, as the child of a session of this host.</summary>
    /// <param name="request">The request, with the limits of the parent.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The session, or the refusal; a refusal is a result, not an exception.</returns>
    Task<RemoteChildResult> CreateAsync(RemoteChildRequest request, CancellationToken cancellationToken);

    /// <summary>Waits for the answer of a session started for a script (<see cref="RemoteChildRequest.ForScript"/>).</summary>
    /// <param name="childSessionId">The session.</param>
    /// <param name="timeout">How long to wait, or null for no limit.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Its answer, or how it ended.</returns>
    Task<RemoteChildAnswer> WaitForAnswerAsync(string childSessionId, TimeSpan? timeout, CancellationToken cancellationToken);

    /// <summary>Sends, queues or steers a prompt of a session of another machine, for an agent of this host.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the machine did; a refusal is a result.</returns>
    Task<RemoteMessageResult> SendAsync(RemoteMessageRequest request, CancellationToken cancellationToken);

    /// <summary>Reads what a session of another machine is doing.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The report, or null when no machine this host reaches lists it.</returns>
    Task<RemoteSessionReport?> ReportAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed partial class SessionRuntimeService
{
    // The remote deliveries being given to their parent now, by correlation: the same one given twice at once goes once.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _remoteDeliveriesInFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets where the answers of sessions started by agents of other machines go: the outbox of the node, which
    /// the machine of each parent takes from. Null on a host that accepts no such session; their answers are then dropped
    /// with a warning.
    /// </summary>
    public IRemoteParentNotifications? RemoteParents { get; set; }

    /// <summary>
    /// Gets or sets the sessions of other machines the agents of this host start and reach; null on a host whose agents
    /// reach no other machine (a node, which signs for no person).
    /// </summary>
    public IRemoteChildSessions? RemoteChildren { get; set; }

    /// <summary>
    /// Delivers to a session of this host what its child on another machine says, as the answer of a child of this host
    /// is delivered: it steers the running turn of the parent, or is queued for its next one. A notification with a
    /// <see cref="RemoteChildNotification.DeliveryId"/> the parent already took, or is taking, is not given again.
    /// </summary>
    /// <param name="notification">What the child says.</param>
    /// <returns>Whether the parent was found and took it, now or before.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="notification"/> is null.</exception>
    public async Task<bool> DeliverRemoteChildNotificationAsync(RemoteChildNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (NormalizeOptionalText(notification.DeliveryId) is not { } deliveryId)
            return await DeliverRemoteChildCoreAsync(notification, "remote-parent-" + Guid.NewGuid().ToString("N")).ConfigureAwait(false);

        // The correlation of the prompt is kept with the parent (its prompt provenance): a delivery given again after a
        // lost acknowledgement, or a restart, finds it there. The id comes from another machine: only its hash is written.
        var correlationId = "remote-parent-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(deliveryId)))[..32];
        if (!_remoteDeliveriesInFlight.TryAdd(correlationId, 0)) return true;
        try
        {
            if (await HasPromptOfCorrelationAsync(notification.ParentSessionId, correlationId).ConfigureAwait(false)) return true;
            return await DeliverRemoteChildCoreAsync(notification, correlationId).ConfigureAwait(false);
        }
        finally
        {
            _remoteDeliveriesInFlight.TryRemove(correlationId, out _);
        }
    }

    // Whether a parent was given, or has queued, a prompt of that correlation.
    private async Task<bool> HasPromptOfCorrelationAsync(string parentSessionId, string correlationId)
    {
        var parent = await TryResolveSessionForParentDeliveryAsync(parentSessionId, CancellationToken.None).ConfigureAwait(false);
        if (parent is null) return false;
        var state = await ReadLatestLocalStateAsync(parent.SessionId, parent.CreatedAt, CancellationToken.None).ConfigureAwait(false);
        return state?.PromptProvenance?.Any(prompt => string.Equals(prompt.SubmittedBy?.CorrelationId, correlationId, StringComparison.Ordinal)) == true
            || state?.QueuedPrompts?.Any(prompt => string.Equals(prompt.SubmittedBy?.CorrelationId, correlationId, StringComparison.Ordinal)) == true;
    }

    private Task<bool> DeliverRemoteChildCoreAsync(RemoteChildNotification notification, string correlationId)
        => DeliverParentNotificationCoreAsync(new ParentNotificationWork(
            SourceSessionId: notification.ChildSessionId,
            SourceProjectId: notification.ChildProjectId,
            SourceAgentId: notification.ChildSessionId,
            ParentSessionId: notification.ParentSessionId,
            Kind: notification.Kind,
            Body: notification.Body,
            RunId: notification.RunId,
            ContentId: notification.ContentId,
            CorrelationId: correlationId,
            SourceMachineName: notification.MachineName));

    /// <summary>
    /// Gets the permission mode a session runs in now, as a mode to compare with others (<see cref="Runtime.SessionPermissionModes.Rank"/>):
    /// its own, else the one its provider is configured with, else what no mode means on this host. A session that is not
    /// attached is taken to ask (<see cref="Runtime.SessionPermissionModes.Ask"/>), the most it may ask.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The mode.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public string GetEffectivePermissionMode(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_entries.TryGetValue(sessionId, out var entry)) return Runtime.SessionPermissionModes.Ask;
        return ModeOrDefaults(entry.PermissionMode, entry.ProviderKey) ?? Runtime.SessionPermissionModes.Default(!_autoApproveOwnedPermissions());
    }

    private async Task RouteToRemoteParentAsync(ParentNotificationWork notification)
    {
        if (RemoteParents is not { } outbox)
        {
            PublishParentNotificationWarning(notification.SourceSessionId,
                $"The parent session '{notification.ParentSessionId}' runs on another machine, which this host does not deliver to.");
            return;
        }

        await outbox.EnqueueAsync(new RemoteParentNotification(notification.SourceSessionId, notification.SourceProjectId, notification.ParentSessionId,
            notification.ParentMachineId!, notification.Kind, notification.Body, notification.RunId, notification.ContentId)).ConfigureAwait(false);
    }
}
