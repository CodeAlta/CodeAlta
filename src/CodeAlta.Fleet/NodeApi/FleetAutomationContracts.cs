namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// The automations a node runs for the devices paired with it: the <c>automations.*</c> method group of the node API
/// (<c>doc/fleet/orchestration.md</c>, "Placement and dispatch": <b>Runs on</b> a paired machine copies a personal
/// automation to it). A device needs the <c>automate</c> permission; the node checks its own policy when a run starts.
/// </summary>
public interface IFleetNodeAutomations
{
    /// <summary>Copies an automation to the node, or replaces the copy it has: it runs there from now on, by its triggers.</summary>
    /// <param name="caller">The device that assigns it.</param>
    /// <param name="parameters">The automation and the repository of its project.</param>
    /// <param name="cancellationToken">Cancels the call before the automation is written.</param>
    /// <returns>The automation as the node keeps it.</returns>
    /// <exception cref="Protocol.FleetException">The node refused it (<see cref="FleetNodeErrorCodes.AutomationInvalid"/>, <see cref="FleetNodeErrorCodes.ProjectNotFound"/>).</exception>
    Task<FleetAutomationInfo> PutAsync(FleetAutomationCaller caller, FleetAutomationPutParams parameters, CancellationToken cancellationToken);

    /// <summary>Removes an automation a device assigned to the node; its runs and their sessions stay.</summary>
    /// <param name="caller">The device that asks.</param>
    /// <param name="id">The automation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether the node had it.</returns>
    Task<bool> RemoveAsync(FleetAutomationCaller caller, string id, CancellationToken cancellationToken);

    /// <summary>The automations devices assigned to the node.</summary>
    /// <param name="caller">The device that asks.</param>
    /// <returns>The automations, by name.</returns>
    IReadOnlyList<FleetAutomationInfo> List(FleetAutomationCaller caller);

    /// <summary>Runs an assigned automation now, whatever its triggers.</summary>
    /// <param name="caller">The device that asks.</param>
    /// <param name="id">The automation.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The run, once its session exists or it failed to start.</returns>
    /// <exception cref="Protocol.FleetException">No such automation (<see cref="FleetNodeErrorCodes.AutomationNotFound"/>).</exception>
    Task<FleetAutomationRunInfo> RunAsync(FleetAutomationCaller caller, string id, CancellationToken cancellationToken);

    /// <summary>Runs an assigned automation now, or tries it: a dry run, whose tools it calls by itself record their calls instead of making them.</summary>
    /// <param name="caller">The device that asks.</param>
    /// <param name="id">The automation.</param>
    /// <param name="dryRun">Whether it is a dry run.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The run, once its session exists or it failed to start.</returns>
    /// <exception cref="Protocol.FleetException">No such automation, or this node tries none (<see cref="FleetNodeErrorCodes.AutomationNotFound"/>).</exception>
    Task<FleetAutomationRunInfo> RunAsync(FleetAutomationCaller caller, string id, bool dryRun, CancellationToken cancellationToken)
        => dryRun ? throw new Protocol.FleetException(FleetNodeErrorCodes.AutomationNotFound, null, "This machine tries no automation.") : RunAsync(caller, id, cancellationToken);

    /// <summary>The runs of the assigned automations, newest first.</summary>
    /// <param name="caller">The device that asks.</param>
    /// <param name="id">The automation; null for all the assigned ones.</param>
    /// <param name="limit">The most runs returned.</param>
    /// <returns>The runs.</returns>
    IReadOnlyList<FleetAutomationRunInfo> Runs(FleetAutomationCaller caller, string? id, int limit);
}

/// <summary>The device that calls a method of the <c>automations.*</c> group.</summary>
/// <param name="DeviceId">Its device id.</param>
/// <param name="Name">The name it gave itself at pairing.</param>
public sealed record FleetAutomationCaller(string DeviceId, string Name)
{
    /// <summary>Gets the fingerprint of the key the device authenticated the channel with; null when unknown.</summary>
    public string? Fingerprint { get; init; }
}

/// <summary>The parameters of <c>automations.put</c>.</summary>
/// <param name="Definition">
/// The automation as one table of a configuration file (<c>[automations.&lt;id&gt;]</c>, the format of
/// <c>config.toml</c>): the node reads it as it reads its own. Its <c>project</c> and <c>runs_on</c> are ignored.
/// </param>
/// <param name="Repository">
/// The repository key of the project it runs in (<c>host/owner/repo</c>), which the node finds among its projects; null
/// for an automation that runs as a chat.
/// </param>
public sealed record FleetAutomationPutParams(string Definition, string? Repository)
{
    /// <summary>
    /// Gets the version the device signed for this machine (<see cref="Protocol.FleetSignedAutomationVersion"/>), with the
    /// definition and the repository in clear: the node runs what it signed, and only while the signature verifies with
    /// the key of the device it is paired with. Required: a definition without it is refused.
    /// </summary>
    public Protocol.FleetSignedAutomationVersion? Signed { get; init; }
}

/// <summary>The parameters of <c>automations.remove</c> and <c>automations.run</c>.</summary>
/// <param name="Id">The automation.</param>
public sealed record FleetAutomationRef(string Id)
{
    /// <summary>
    /// Gets whether <c>automations.run</c> tries the automation: a dry run, whose tools it calls by itself are replaced by
    /// fakes that record their calls on the run. Null or false: a run.
    /// </summary>
    public bool? DryRun { get; init; }
}

/// <summary>The answer of <c>automations.remove</c>.</summary>
/// <param name="Id">The automation.</param>
/// <param name="Removed">Whether the node had it.</param>
public sealed record FleetAutomationRemoved(string Id, bool Removed);

/// <summary>An automation a device assigned to the node, as the node keeps it.</summary>
/// <param name="Id">The automation.</param>
/// <param name="Name">Its name.</param>
/// <param name="Enabled">Whether its triggers start it.</param>
/// <param name="Fingerprint">What the node runs: changes with what the automation does and when.</param>
public sealed record FleetAutomationInfo(string Id, string Name, bool Enabled, string Fingerprint)
{
    /// <summary>Gets the device that assigned it; null when the node does not know.</summary>
    public string? AssignedBy { get; init; }

    /// <summary>Gets the name of that device.</summary>
    public string? AssignedByName { get; init; }

    /// <summary>Gets when it was assigned.</summary>
    public DateTimeOffset? AssignedAt { get; init; }

    /// <summary>Gets the folder of its project on the node; null for a chat.</summary>
    public string? Project { get; init; }

    /// <summary>Gets the next time a schedule of it is due on the node; null without one, or while it cannot start.</summary>
    public DateTimeOffset? NextDue { get; init; }

    /// <summary>Gets whether a run of it is in progress.</summary>
    public bool Running { get; init; }

    /// <summary>Gets why it cannot run on the node as it is; null when it can.</summary>
    public string? Problem { get; init; }
}

/// <summary>The answer of <c>automations.list</c>.</summary>
/// <param name="Automations">The automations, by name.</param>
public sealed record FleetAutomationList(IReadOnlyList<FleetAutomationInfo> Automations);

/// <summary>The parameters of <c>automations.runs</c>.</summary>
/// <param name="Id">The automation; null for every assigned one.</param>
/// <param name="Limit">The most runs returned; null for 50.</param>
public sealed record FleetAutomationRunsParams(string? Id, int? Limit);

/// <summary>A run of an automation on the node.</summary>
/// <param name="Id">The run.</param>
/// <param name="AutomationId">The automation.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="Trigger">What started it: <c>manual</c>, or the kind of its trigger.</param>
/// <param name="Status">Its state: <c>running</c>, <c>completed</c>, <c>failed</c>, <c>cancelled</c>, <c>interrupted</c> or <c>skipped</c>.</param>
public sealed record FleetAutomationRunInfo(string Id, string AutomationId, DateTimeOffset StartedAt, string Trigger, string Status)
{
    /// <summary>Gets the name of the automation when it ran.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the session of the run on the node; null when none started.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets what the trigger was about.</summary>
    public string? Detail { get; init; }

    /// <summary>Gets why it failed or was skipped.</summary>
    public string? Message { get; init; }

    /// <summary>Gets when it ended.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Gets the input tokens of its session, the prompt cache included; null when unknown.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Gets the output tokens of its session; null when unknown.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Gets the cost the provider reported for its session, in <see cref="CostUnit"/>; null when none was.</summary>
    public double? Cost { get; init; }

    /// <summary>Gets the unit of <see cref="Cost"/>: <c>usd</c>, or the one the provider named.</summary>
    public string? CostUnit { get; init; }
}

/// <summary>The answer of <c>automations.runs</c>.</summary>
/// <param name="Runs">The runs, newest first.</param>
public sealed record FleetAutomationRunList(IReadOnlyList<FleetAutomationRunInfo> Runs);
