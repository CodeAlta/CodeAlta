namespace CodeAlta.LiveTool;

/// <summary>
/// The fleet of a host that has one (CodeAlta Desktop), for <c>alta fleet</c>: this computer, the machines it is paired
/// with or reaches through a server, their sessions, devices and versions, where the automations run, and the statistics
/// and updates of the servers. Every read is what the person of this computer may see; nothing here changes access. A
/// host without a fleet does not give this service, and <c>alta fleet</c> is then not part of its commands.
/// </summary>
public interface IAltaFleet
{
    /// <summary>Lists the machines: this computer first, then the machines it reaches, as it last heard from them.</summary>
    /// <returns>The machines.</returns>
    IReadOnlyList<AltaFleetMachine> ListMachines();

    /// <summary>Reads one machine now, with its projects, its ready providers and where its update stands.</summary>
    /// <param name="machine">Its name or its id.</param>
    /// <param name="cancellationToken">Stops waiting for the machine.</param>
    /// <returns>The machine, or why it could not be read (<c>not_found</c>, <c>offline</c>...).</returns>
    Task<AltaFleetMachineDetail> ShowMachineAsync(string machine, CancellationToken cancellationToken);

    /// <summary>Lists the sessions a machine lists, the latest activity first.</summary>
    /// <param name="machine">Its name or its id.</param>
    /// <param name="limit">The most sessions to list.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The sessions, or why they could not be read.</returns>
    Task<AltaFleetSessions> ListSessionsAsync(string machine, int limit, CancellationToken cancellationToken);

    /// <summary>Lists the devices paired with a machine this computer manages, and what each may do there. Read only.</summary>
    /// <param name="machine">Its name or its id.</param>
    /// <param name="cancellationToken">Stops waiting for the machine.</param>
    /// <returns>The devices, or why they could not be read (<c>not_manager</c> when this computer does not manage it).</returns>
    Task<AltaFleetDevices> ListDevicesAsync(string machine, CancellationToken cancellationToken);

    /// <summary>Says where a session runs, and what a move of it to a machine would meet.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="machine">The machine it would move to, by name or id; null to say only where it runs.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The status.</returns>
    Task<AltaFleetMoveStatus> GetMoveStatusAsync(string sessionId, string? machine, CancellationToken cancellationToken);

    /// <summary>Lists the automations of the person with where each runs: this computer, or a machine and where its copy stands there.</summary>
    /// <returns>The automations.</returns>
    IReadOnlyList<AltaFleetAutomationPlacement> ListAutomationPlacements();

    /// <summary>Lists the runs the machines of the automations reported, newest first.</summary>
    /// <param name="automationId">The automation; null for all.</param>
    /// <param name="limit">The most runs.</param>
    /// <returns>The runs.</returns>
    IReadOnlyList<AltaAutomationRun> ListPlacedRuns(string? automationId, int limit);

    /// <summary>Makes an automation run on a machine, or on this computer again.</summary>
    /// <param name="automationId">The automation.</param>
    /// <param name="machine">The machine, by name or id; null for this computer.</param>
    /// <param name="cancellationToken">Stops before the configuration is written.</param>
    /// <returns><c>ok</c>, <c>not_found</c>, <c>machine_not_found</c>, <c>not_allowed</c> (a paired machine does not let this computer automate, or a machine reached through a server is not one of the person's own) or <c>refused</c>.</returns>
    Task<AltaFleetChange> PlaceAutomationAsync(string automationId, string? machine, CancellationToken cancellationToken);

    /// <summary>Reads the sums of the statistics of the person's machines from a server.</summary>
    /// <param name="query">The period, the time zone and the groups.</param>
    /// <param name="cancellationToken">Stops waiting for the server.</param>
    /// <returns>The sums, or why they could not be read (<c>no_server</c>, <c>unreachable</c>...).</returns>
    Task<AltaFleetStatistics> QueryStatisticsAsync(AltaFleetStatisticsQuery query, CancellationToken cancellationToken);

    /// <summary>Reads the versions of the fleet of a server, its update policy and its recent campaigns.</summary>
    /// <param name="server">The server, by its link id or its address; null for the one online.</param>
    /// <param name="cancellationToken">Stops waiting for the server.</param>
    /// <returns>The updates, or why they could not be read.</returns>
    Task<AltaFleetUpdates> ReadUpdatesAsync(string? server, CancellationToken cancellationToken);

    /// <summary>Lists the servers this computer is linked to and how each connection stands.</summary>
    /// <returns>The servers.</returns>
    IReadOnlyList<AltaFleetServer> ListServers();

    /// <summary>Lists the secrets and variables of this computer, or of a machine it manages, by name: never a value.</summary>
    /// <param name="machine">The machine, by name or id; null for this computer.</param>
    /// <param name="cancellationToken">Stops waiting for the machine.</param>
    /// <returns>The names, or why they could not be read (<c>not_manager</c>, <c>not_found</c>, <c>offline</c>...).</returns>
    Task<AltaFleetSecrets> ListSecretsAsync(string? machine, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the hooks of the person of this computer on a server: the addresses tools push their signed events to, for one
    /// machine each, and, for one hook, what its machine did with its latest events. Never an event's content.
    /// </summary>
    /// <param name="server">The server, by its link id or its address; null for the one online.</param>
    /// <param name="hookId">The hook whose latest events are listed; null for none.</param>
    /// <param name="cancellationToken">Stops waiting for the server.</param>
    /// <returns>The hooks, or why they could not be read.</returns>
    Task<AltaFleetHooks> ListHooksAsync(string? server, string? hookId, CancellationToken cancellationToken)
        => Task.FromResult(new AltaFleetHooks("unavailable", "This host lists no hooks.", [], []));
}

/// <summary>The hooks of a person on a server.</summary>
/// <param name="Status"><c>ok</c>, <c>no_server</c>, <c>unreachable</c>, or the code the server answered.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Items">The hooks.</param>
/// <param name="Events">The latest events of the hook asked, the newest first; empty otherwise.</param>
public sealed record AltaFleetHooks(string Status, string? Message, IReadOnlyList<AltaFleetHook> Items, IReadOnlyList<AltaFleetHookEvent> Events);

/// <summary>A hook of a server: the address a tool pushes its signed events to, for one machine.</summary>
/// <param name="Id">Its id, which a trigger names with <c>hook = "&lt;id&gt;"</c>.</param>
/// <param name="Address">Its full address, given to the tool.</param>
/// <param name="Machine">The name of the machine its events go to, or its id.</param>
/// <param name="Source"><c>incident_io</c> or <c>webhook</c>.</param>
/// <param name="Label">Its name.</param>
/// <param name="LastReceivedAt">When it last received an event.</param>
/// <param name="Pending">How many events wait for the machine.</param>
public sealed record AltaFleetHook(string Id, string Address, string Machine, string Source, string Label, DateTimeOffset? LastReceivedAt, int Pending);

/// <summary>What a machine did with one event of a hook, as facts.</summary>
/// <param name="DeliveryId">The delivery.</param>
/// <param name="ReceivedAt">When it came.</param>
/// <param name="State"><c>pending</c>, <c>accepted</c>, <c>refused</c> or <c>expired</c>.</param>
/// <param name="Message">What the machine said of it, such as why its signature did not verify.</param>
public sealed record AltaFleetHookEvent(string DeliveryId, DateTimeOffset ReceivedAt, string State, string? Message);

/// <summary>The secrets and variables of a machine, by name.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>not_found</c>, the state of a machine that is not online, or the code the machine answered.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Machine">The name of the machine.</param>
/// <param name="Items">The secrets and the variables.</param>
public sealed record AltaFleetSecrets(string Status, string? Message, string? Machine, IReadOnlyList<AltaFleetSecret> Items);

/// <summary>A secret or a variable of a machine, without its value.</summary>
/// <param name="Kind"><c>secret</c> or <c>variable</c>.</param>
/// <param name="Name">Its name, which the configuration names as <c>${secret:NAME}</c> or <c>${var:NAME}</c>.</param>
/// <param name="Version">Its version, which a rotation increases.</param>
/// <param name="SetAt">When it was set.</param>
public sealed record AltaFleetSecret(string Kind, string Name, int Version, DateTimeOffset SetAt);

/// <summary>A machine of the fleet.</summary>
/// <param name="Id">Its device id; empty for this computer when it has none yet.</param>
/// <param name="Name">Its name.</param>
/// <param name="State"><c>online</c>, <c>connecting</c>, <c>offline</c> or <c>unauthorized</c>.</param>
/// <param name="Readiness">
/// Whether it takes a new session now: <c>ready</c>, <c>not_allowed</c> (this computer may not start sessions there),
/// <c>no_provider</c>, <c>updating</c>, or the state of a machine that is not online.
/// </param>
public sealed record AltaFleetMachine(string Id, string Name, string State, string Readiness)
{
    /// <summary>Gets whether it is this computer.</summary>
    public bool IsThisComputer { get; init; }

    /// <summary>Gets its system (<c>windows</c>, <c>macos</c>, <c>linux</c>), once it said it.</summary>
    public string? Os { get; init; }

    /// <summary>Gets the version of CodeAlta it runs, once it said it.</summary>
    public string? Version { get; init; }

    /// <summary>Gets what it offers (<c>sandbox</c>, <c>moves</c>), as it publishes it to a server; empty when unknown.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Gets the labels its owner gave it, as it publishes them to a server; empty when unknown.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Gets the names of its ready providers.</summary>
    public IReadOnlyList<string> Providers { get; init; } = [];

    /// <summary>Gets what this computer may do there: <c>view</c>, <c>drive</c>, <c>start</c>, <c>move</c>, <c>automate</c>, <c>manage</c>.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>Gets how many sessions it listed last.</summary>
    public int Sessions { get; init; }

    /// <summary>Gets how many of them work now (its load); null when unknown.</summary>
    public int? Working { get; init; }

    /// <summary>Gets whether this computer paired with it; false for a machine reached through a server only.</summary>
    public bool Paired { get; init; }

    /// <summary>Gets where its channel goes while it is online: <c>direct</c> or <c>relayed</c>.</summary>
    public string? Channel { get; init; }

    /// <summary>Gets when it last answered.</summary>
    public DateTimeOffset? LastSeen { get; init; }

    /// <summary>Gets why it is not online, in words.</summary>
    public string? Problem { get; init; }
}

/// <summary>A machine read now.</summary>
/// <param name="Status"><c>ok</c>, <c>not_found</c>, the state of a machine that is not online, or the code of its refusal.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Machine">The machine.</param>
public sealed record AltaFleetMachineDetail(string Status, string? Message, AltaFleetMachine? Machine)
{
    /// <summary>Gets its projects, which a session there can work in.</summary>
    public IReadOnlyList<AltaFleetProject> Projects { get; init; } = [];

    /// <summary>Gets its ready providers.</summary>
    public IReadOnlyList<AltaFleetProvider> Providers { get; init; } = [];

    /// <summary>Gets where its update stands; null when it does not say.</summary>
    public AltaFleetUpdateState? Update { get; init; }
}

/// <summary>A project of a machine.</summary>
/// <param name="Id">Its id there, which <c>alta session create --machine --project</c> names.</param>
/// <param name="Name">Its name.</param>
/// <param name="RepositoryKey">Its repository, such as <c>github.com/owner/repo</c>; null for a folder in no repository with a remote.</param>
public sealed record AltaFleetProject(string Id, string Name, string? RepositoryKey);

/// <summary>A provider ready on a machine.</summary>
/// <param name="Key">Its key there, which <c>--model-ref</c> names.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsDefault">Whether a session that names no provider runs on it.</param>
/// <param name="DefaultModel">The model it takes when none is named.</param>
/// <param name="Models">Its models.</param>
public sealed record AltaFleetProvider(string Key, string Name, bool IsDefault, string? DefaultModel, IReadOnlyList<string> Models);

/// <summary>Where the update of a machine stands.</summary>
/// <param name="Running">The version that runs.</param>
/// <param name="Installed">The version installed.</param>
/// <param name="Pending">The version a switch waits to start.</param>
/// <param name="Phase">Where that switch stands.</param>
/// <param name="LastOutcome">How the last switch ended, with its version.</param>
public sealed record AltaFleetUpdateState(string? Running, string? Installed, string? Pending, string? Phase, string? LastOutcome);

/// <summary>The sessions of a machine.</summary>
/// <param name="Status"><c>ok</c>, <c>not_found</c> or the state of a machine never reached.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Machine">The name of the machine.</param>
/// <param name="Items">The sessions.</param>
public sealed record AltaFleetSessions(string Status, string? Message, string? Machine, IReadOnlyList<AltaFleetSession> Items);

/// <summary>A session a machine lists.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Title">Its title.</param>
/// <param name="State"><c>working</c>, <c>waiting</c>, <c>idle</c> or <c>archived</c>.</param>
/// <param name="ProjectId">Its project there.</param>
/// <param name="ProviderKey">Its provider.</param>
/// <param name="ModelId">Its model.</param>
/// <param name="ParentSessionId">The session that started it, for a sub-agent.</param>
/// <param name="UpdatedAt">When it last changed.</param>
public sealed record AltaFleetSession(string Id, string Title, string State, string? ProjectId, string? ProviderKey, string? ModelId, string? ParentSessionId,
    DateTimeOffset UpdatedAt);

/// <summary>The devices of a machine, as the person who manages it sees them.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>not_found</c>, <c>offline</c> or the code the machine answered.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Machine">The name of the machine.</param>
/// <param name="Items">The devices.</param>
public sealed record AltaFleetDevices(string Status, string? Message, string? Machine, IReadOnlyList<AltaFleetDevice> Items);

/// <summary>A device paired with a machine.</summary>
/// <param name="Id">Its device id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Permissions">What it may do there.</param>
/// <param name="PairedAt">When it paired.</param>
/// <param name="RevokedAt">When it was revoked; null while trusted.</param>
/// <param name="IsThisComputer">Whether it is this computer.</param>
public sealed record AltaFleetDevice(string Id, string Name, IReadOnlyList<string> Permissions, DateTimeOffset PairedAt, DateTimeOffset? RevokedAt, bool IsThisComputer);

/// <summary>Where a session runs, and what a move of it would meet.</summary>
/// <param name="Status"><c>ok</c>, or why the session cannot move: <c>session_missing</c>, <c>machine_not_found</c>, <c>not_movable</c>, <c>machine_offline</c>...</param>
/// <param name="Message">What was said of a refusal.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Machine">The name of the machine it runs on now.</param>
public sealed record AltaFleetMoveStatus(string Status, string? Message, string SessionId, string? Machine)
{
    /// <summary>Gets whether it runs on this computer.</summary>
    public bool RunsHere { get; init; }

    /// <summary>Gets the machine the move was asked of.</summary>
    public string? Target { get; init; }

    /// <summary>Gets what keeps the session busy (<c>working</c>, <c>waiting</c>); null when idle and it can move.</summary>
    public string? Busy { get; init; }

    /// <summary>Gets what the target answers: <c>accepted</c>, <c>refused</c>, <c>forbidden</c>, <c>unreachable</c>; null when not asked.</summary>
    public string? TargetStatus { get; init; }

    /// <summary>Gets why the target refuses (<c>project_missing</c>...).</summary>
    public string? TargetCode { get; init; }

    /// <summary>Gets the project of the target the session would go to.</summary>
    public string? Project { get; init; }

    /// <summary>Gets whether the target can clone the repository when it has no project of it (<c>--clone</c>).</summary>
    public bool CanClone { get; init; }

    /// <summary>Gets the background jobs that run: they do not move.</summary>
    public IReadOnlyList<string> Jobs { get; init; } = [];

    /// <summary>Gets the changed files that look like secrets: they stay.</summary>
    public IReadOnlyList<string> StaysBehind { get; init; } = [];

    /// <summary>Gets what the target says without refusing.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>An automation of the person and where it runs.</summary>
/// <param name="Id">The automation.</param>
/// <param name="Name">Its name.</param>
/// <param name="Enabled">Whether its triggers start it.</param>
/// <param name="MachineId">The machine it runs on; null for this computer.</param>
/// <param name="Machine">The name of that machine.</param>
/// <param name="State"><c>here</c> (it runs on this computer), <c>copied</c>, <c>pending</c>, <c>waiting</c> (for the machine it left to take its copy away) or <c>failed</c> (its copy on the machine).</param>
/// <param name="Message">Why its copy is pending, waiting or failed.</param>
public sealed record AltaFleetAutomationPlacement(string Id, string Name, bool Enabled, string? MachineId, string? Machine, string State, string? Message)
{
    /// <summary>Gets when the machine last took its copy.</summary>
    public DateTimeOffset? CopiedAt { get; init; }

    /// <summary>Gets when a schedule of it is next due where it runs.</summary>
    public DateTimeOffset? NextRunAt { get; init; }
}

/// <summary>What a change of the fleet gave.</summary>
/// <param name="Status"><c>ok</c>, or the code of the refusal.</param>
/// <param name="Message">Why it was refused.</param>
public sealed record AltaFleetChange(string Status, string? Message = null);

/// <summary>A question about the statistics of the person's machines.</summary>
/// <param name="From">The first local day, <c>yyyy-MM-dd</c>.</param>
/// <param name="To">The last local day, <c>yyyy-MM-dd</c>.</param>
/// <param name="TimeZone">The time zone of the days; null for UTC.</param>
/// <param name="GroupBy">Up to three of <c>machine</c>, <c>project</c>, <c>provider</c>, <c>model</c>, <c>day</c>, <c>month</c>.</param>
/// <param name="Server">The server, by its link id or its address; null for the one online.</param>
public sealed record AltaFleetStatisticsQuery(string From, string To, string? TimeZone, IReadOnlyList<string> GroupBy, string? Server);

/// <summary>The sums of a group of the statistics.</summary>
/// <param name="ActiveMs">The active time of the runs, in milliseconds.</param>
/// <param name="Runs">The runs.</param>
/// <param name="RunsFailed">The runs that failed.</param>
/// <param name="Requests">The requests to models.</param>
/// <param name="InputTokens">The input tokens.</param>
/// <param name="OutputTokens">The output tokens.</param>
/// <param name="ToolCalls">The tool calls.</param>
/// <param name="ToolFailures">The tool calls that failed.</param>
/// <param name="Costs">The estimated costs, by unit (<c>usd</c>).</param>
public sealed record AltaFleetMeasures(decimal ActiveMs, decimal Runs, decimal RunsFailed, decimal Requests, decimal InputTokens, decimal OutputTokens,
    decimal ToolCalls, decimal ToolFailures, IReadOnlyDictionary<string, decimal> Costs);

/// <summary>A group of the statistics.</summary>
/// <param name="Machine">The name of the machine, when grouped by machine.</param>
/// <param name="Project">The name of the project (its key when this computer does not know it), when grouped by project.</param>
/// <param name="Provider">The provider, when grouped by provider.</param>
/// <param name="Model">The model, when grouped by model.</param>
/// <param name="Period">The day or the month, when grouped by one.</param>
/// <param name="Measures">The sums.</param>
public sealed record AltaFleetStatisticsGroup(string? Machine, string? Project, string? Provider, string? Model, string? Period, AltaFleetMeasures Measures);

/// <summary>The statistics of the person's machines.</summary>
/// <param name="Status"><c>ok</c>, <c>no_server</c>, <c>unreachable</c>, <c>invalid_request</c>, or the code of the server's refusal.</param>
/// <param name="Message">What went wrong.</param>
/// <param name="Totals">The sums of the period.</param>
/// <param name="Groups">The groups, the largest first.</param>
public sealed record AltaFleetStatistics(string Status, string? Message, AltaFleetMeasures? Totals, IReadOnlyList<AltaFleetStatisticsGroup> Groups)
{
    /// <summary>Gets the address of the server that answered.</summary>
    public string? Server { get; init; }

    /// <summary>Gets whether groups were left out.</summary>
    public bool Truncated { get; init; }
}

/// <summary>An update campaign of a fleet.</summary>
/// <param name="Id">The campaign.</param>
/// <param name="State">Its state.</param>
/// <param name="TargetVersion">The version it installs.</param>
/// <param name="CreatedAt">When it was started.</param>
/// <param name="Automatic">Whether the automatic policy started it.</param>
/// <param name="Machines">Each machine and where it stands: <c>name: state</c>.</param>
public sealed record AltaFleetCampaign(string Id, string State, string TargetVersion, DateTimeOffset CreatedAt, bool Automatic, IReadOnlyList<string> Machines);

/// <summary>The version a machine of a fleet runs.</summary>
/// <param name="Id">Its device id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">The version it said it runs.</param>
/// <param name="Online">Whether it is connected to the server.</param>
public sealed record AltaFleetMachineVersion(string Id, string Name, string? Version, bool Online);

/// <summary>The updates of the fleet of a server.</summary>
/// <param name="Status"><c>ok</c>, <c>no_server</c>, <c>unreachable</c>, or the code of the server's refusal.</param>
/// <param name="Message">What went wrong.</param>
public sealed record AltaFleetUpdates(string Status, string? Message)
{
    /// <summary>Gets the address of the server.</summary>
    public string? Server { get; init; }

    /// <summary>Gets the version the server runs.</summary>
    public string? ServerVersion { get; init; }

    /// <summary>Gets the update policy: <c>manual</c>, <c>pinned</c> or <c>automatic</c>.</summary>
    public string? Policy { get; init; }

    /// <summary>Gets the version a pinned fleet keeps.</summary>
    public string? PinnedVersion { get; init; }

    /// <summary>Gets the recent campaigns, the newest first.</summary>
    public IReadOnlyList<AltaFleetCampaign> Campaigns { get; init; } = [];

    /// <summary>Gets the machines and their versions.</summary>
    public IReadOnlyList<AltaFleetMachineVersion> Machines { get; init; } = [];
}

/// <summary>A server this computer is linked to.</summary>
/// <param name="Id">The id of the link.</param>
/// <param name="Url">The address of the server.</param>
/// <param name="Organization">The name of its organization.</param>
/// <param name="Connection"><c>online</c>, <c>connecting</c>, <c>pending</c> (a person approves this device), <c>offline</c>, <c>unauthenticated</c> or <c>revoked</c>.</param>
/// <param name="Problem">What its last failure said.</param>
public sealed record AltaFleetServer(string Id, string Url, string? Organization, string Connection, string? Problem);
