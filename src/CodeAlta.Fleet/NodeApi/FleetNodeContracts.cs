using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>The methods and events of the node API (<c>doc/fleet/protocol.md</c>, "Node API").</summary>
public static class FleetNodeMethods
{
    /// <summary>Redeems a pairing offer: the only method an unpaired client may call.</summary>
    public const string PairRedeem = "pair.redeem";

    /// <summary>What the node is: its name, system, version, spaces and projects.</summary>
    public const string NodeInfo = "node.info";

    /// <summary>The sessions of the node, the latest activity first, with their state.</summary>
    public const string SessionsList = "sessions.list";

    /// <summary>The runtime identities of a session (instance, attachment, active run) that steer and stop are bound to.</summary>
    public const string SessionsState = "sessions.state";

    /// <summary>A bounded window of the timeline of a session, by cursor.</summary>
    public const string SessionsHistory = "sessions.history";

    /// <summary>Starts the live display updates of a session (<see cref="SessionDisplayEvent"/> events).</summary>
    public const string SessionsWatch = "sessions.watch";

    /// <summary>Stops a watch.</summary>
    public const string SessionsUnwatch = "sessions.unwatch";

    /// <summary>Creates a session, and sends its first prompt when one is given.</summary>
    public const string SessionsCreate = "sessions.create";

    /// <summary>Sends a prompt that starts a turn (the idempotency key is the retry key of the owned command).</summary>
    public const string SessionsSend = "sessions.send";

    /// <summary>Queues a prompt for after the running turn.</summary>
    public const string SessionsQueue = "sessions.queue";

    /// <summary>Steers the running turn of a session, bound to its exact run.</summary>
    public const string SessionsSteer = "sessions.steer";

    /// <summary>Stops a send, or the exact run of a session.</summary>
    public const string SessionsAbort = "sessions.abort";

    /// <summary>The permission requests of a session that wait for a person.</summary>
    public const string SessionsPermissionsList = "sessions.permissions.list";

    /// <summary>Answers a permission request.</summary>
    public const string SessionsPermissionsAnswer = "sessions.permissions.answer";

    /// <summary>The question of a session that waits for a person.</summary>
    public const string SessionsAsksList = "sessions.asks.list";

    /// <summary>Answers (or cancels) the question of a session.</summary>
    public const string SessionsAsksAnswer = "sessions.asks.answer";

    /// <summary>The owner's local control: creates a pairing offer (only from the node's own key, over loopback).</summary>
    public const string ControlPairingCreate = "control.pairing.create";

    /// <summary>The owner's local control: waits until the open offer is redeemed, expires or is replaced.</summary>
    public const string ControlPairingWait = "control.pairing.wait";

    /// <summary>The owner's local control: what the node does now (sessions at work, open channels, paired devices).</summary>
    public const string ControlStatus = "control.status";

    /// <summary>The owner's local control: stops the node gracefully (as Ctrl+C does), when it was started with a way to stop.</summary>
    public const string ControlStop = "control.stop";

    /// <summary>The owner's local control: the paired devices, the revoked ones included.</summary>
    public const string ControlDevicesList = "control.devices.list";

    /// <summary>The owner's local control: revokes a device and closes its channels.</summary>
    public const string ControlDevicesRevoke = "control.devices.revoke";

    /// <summary>The owner's local control: changes what a device may do.</summary>
    public const string ControlDevicesPermissions = "control.devices.permissions";

    /// <summary>The owner's local control: the projects of the node's catalog, archived ones included.</summary>
    public const string ControlProjectsList = "control.projects.list";

    /// <summary>The owner's local control: the secrets of the machine by name, and its variables.</summary>
    public const string ControlSecretsList = "control.secrets.list";

    /// <summary>The owner's local control: sets a secret or a variable of the machine, or gives it a new version.</summary>
    public const string ControlSecretsSet = "control.secrets.set";

    /// <summary>The owner's local control: gives a secret of the machine a new value and a new version; it must be set.</summary>
    public const string ControlSecretsRotate = "control.secrets.rotate";

    /// <summary>The owner's local control: deletes a secret or a variable of the machine.</summary>
    public const string ControlSecretsDelete = "control.secrets.delete";

    /// <summary>The owner's local control: adds a folder of the node to its catalog, and to a space.</summary>
    public const string ControlProjectsAdd = "control.projects.add";

    /// <summary>The owner's local control: clones a repository into the node's clone root and adds it to its catalog.</summary>
    public const string ControlProjectsClone = "control.projects.clone";

    /// <summary>The owner's local control: removes a project from the node's catalog; its files stay.</summary>
    public const string ControlProjectsRemove = "control.projects.remove";

    /// <summary>The owner's local control, or a device that manages the node: the last lines of the node's log.</summary>
    public const string ControlLog = "control.log";

    /// <summary>The owner's local control, or a device that manages the node: checks its feed for an update, or installs one.</summary>
    public const string ControlUpdate = "control.update";

    /// <summary>The owner's local control, or a device that manages the node: enrolls the node in a server with a token, as <c>altanode join</c>.</summary>
    public const string ControlServersJoin = "control.servers.join";

    /// <summary>The owner's local control, or a device that manages the node: its providers and how each stands.</summary>
    public const string ControlProvidersList = "control.providers.list";

    /// <summary>The owner's local control, or a paired device that manages the node: adds or changes a provider, its key kept in the secrets of the machine.</summary>
    public const string ControlProvidersSet = "control.providers.set";

    /// <summary>The owner's local control, or a paired device that manages the node: starts the device-code sign-in of a provider on the node.</summary>
    public const string ControlProvidersSignIn = "control.providers.signin";

    /// <summary>The owner's local control, or a paired device that manages the node: waits a while for the sign-in of a provider to end.</summary>
    public const string ControlProvidersSignInWait = "control.providers.signin.wait";

    /// <summary>The owner's local control, or a paired device that manages the node: what the node signs in (providers, MCP servers, tools) and how each stands.</summary>
    public const string ControlSignInsList = "control.signins.list";

    /// <summary>The owner's local control, or a paired device that manages the node: starts a sign-in the node keeps, completed from the desktop.</summary>
    public const string ControlSignInsStart = "control.signins.start";

    /// <summary>The owner's local control, or a paired device that manages the node: gives a sign-in the callback of the browser or a pasted code.</summary>
    public const string ControlSignInsComplete = "control.signins.complete";

    /// <summary>The owner's local control, or a paired device that manages the node: waits a while for a sign-in to end.</summary>
    public const string ControlSignInsWait = "control.signins.wait";

    /// <summary>The owner's local control, or a paired device that manages the node: ends a sign-in that waits.</summary>
    public const string ControlSignInsCancel = "control.signins.cancel";

    /// <summary>The owner's local control, or a paired device that manages the node: signs a provider, an MCP server or a tool out on the node.</summary>
    public const string ControlSignInsSignOut = "control.signins.signout";

    /// <summary>A target: takes the offer of a session (or, as a probe, only says whether it would).</summary>
    public const string MovesOffer = "moves.offer";

    /// <summary>A target: receives a chunk of the archive of a bundle, at the length it holds (resumable).</summary>
    public const string MovesTransfer = "moves.transfer";

    /// <summary>A target: verifies the archive by its hash, unpacks it and prepares the worktree of the session.</summary>
    public const string MovesPrepare = "moves.prepare";

    /// <summary>A target, arbiter in paired mode: commits a prepared move and installs the session.</summary>
    public const string MovesCommit = "moves.commit";

    /// <summary>A target: the source says it relinquished the session; the copies of the bundle go.</summary>
    public const string MovesComplete = "moves.complete";

    /// <summary>The outcome of a move as the arbiter knows it, and the records of the machine (asked by a source after a lost connection).</summary>
    public const string MovesStatus = "moves.status";

    /// <summary>A source: gives one of its sessions to the caller, or through the caller to another machine (the same id resumes the move).</summary>
    public const string MovesStart = "moves.start";

    /// <summary>The records of the moves of the machine.</summary>
    public const string MovesList = "moves.list";

    /// <summary>
    /// Takes what the sessions agents of the caller started on the node said to their parents (final answers, failures,
    /// updates), oldest first: they stay until <see cref="AgentsDeliveriesAck"/> acknowledges them.
    /// </summary>
    public const string AgentsDeliveriesTake = "agents.deliveries.take";

    /// <summary>Acknowledges deliveries the caller took and delivered: the node forgets them.</summary>
    public const string AgentsDeliveriesAck = "agents.deliveries.ack";

    /// <summary>The owner's local control, or a device that manages the node: whether agents of other machines may start sessions on it, per space.</summary>
    public const string ControlAgentsGet = "control.agents.get";

    /// <summary>The owner's local control, or a device that manages the node: changes that policy.</summary>
    public const string ControlAgentsSet = "control.agents.set";

    /// <summary>The owner's local control, or a device that manages the node: what its sessions run with when nothing else chooses it.</summary>
    public const string ControlSessionDefaultsGet = "control.session_defaults.get";

    /// <summary>The owner's local control, or a device that manages the node: changes what its sessions run with by default.</summary>
    public const string ControlSessionDefaultsSet = "control.session_defaults.set";

    /// <summary>The owner's local control, or a device that manages the node: the labels of the machine.</summary>
    public const string ControlLabelsGet = "control.labels.get";

    /// <summary>The owner's local control, or a device that manages the node: sets the labels of the machine.</summary>
    public const string ControlLabelsSet = "control.labels.set";

    /// <summary>The owner's local control, or a device that manages the node: the limits of the machine (sessions at work at once).</summary>
    public const string ControlLimitsGet = "control.limits.get";

    /// <summary>The owner's local control, or a device that manages the node: sets the limits of the machine.</summary>
    public const string ControlLimitsSet = "control.limits.set";

    /// <summary>The seats of the machine: its limit of sessions at work, the seats taken, and the runs that wait for one.</summary>
    public const string SeatsList = "seats.list";

    /// <summary>The owner's local control, or a device that manages the node: starts a run that waits for a seat now, past the limit.</summary>
    public const string SeatsStart = "seats.start";

    /// <summary>Takes a run that waits for a seat out of the queue: it does not start.</summary>
    public const string SeatsCancel = "seats.cancel";

    /// <summary>
    /// The owner's local control, or a device that manages the node: the people of the organization this personal machine lets
    /// in, and the people who asked to start sessions on it (<c>doc/fleet/access-control.md</c>, what the node decides).
    /// </summary>
    public const string ControlPeopleGet = "control.people.get";

    /// <summary>The owner's local control, or a device that manages the node: lets a person in with permissions, takes them out, or refuses their request.</summary>
    public const string ControlPeopleSet = "control.people.set";

    /// <summary>Copies an automation to the node, which runs it from now on (<see cref="IFleetNodeAutomations"/>).</summary>
    public const string AutomationsPut = "automations.put";

    /// <summary>Removes an automation a device assigned to the node.</summary>
    public const string AutomationsRemove = "automations.remove";

    /// <summary>The automations devices assigned to the node.</summary>
    public const string AutomationsList = "automations.list";

    /// <summary>Runs an assigned automation now.</summary>
    public const string AutomationsRun = "automations.run";

    /// <summary>
    /// Tries an assigned automation now: a dry run, whose tools it calls by itself record their calls instead of making them. A
    /// method of its own, so that a node that does not know tries answers <c>method_not_found</c> rather than run it for real.
    /// </summary>
    public const string AutomationsTry = "automations.try";

    /// <summary>The runs of the assigned automations, newest first.</summary>
    public const string AutomationsRuns = "automations.runs";

    /// <summary>The event of a live display replacement of a watched session (<see cref="FleetSessionDisplay"/>).</summary>
    public const string SessionDisplayEvent = "session.display";

    /// <summary>The event of a watch the node ended (<see cref="FleetWatchEnded"/>).</summary>
    public const string SessionWatchEndedEvent = "session.watch.ended";
}

/// <summary>The error codes of the node API, beside those of the protocol (<see cref="Protocol.FleetErrorCodes"/>).</summary>
public static class FleetNodeErrorCodes
{
    /// <summary>The kill switch of an organization stops the work of the fleet on the node: it takes none until a version of its policy lifts it.</summary>
    public const string FleetStopped = "fleet.stopped";

    /// <summary>The signed policy of the organization of the space refuses the request: a provider, a model, a sandbox, who starts.</summary>
    public const string PolicyRefused = "policy.refused";

    /// <summary>No session of the node has that id.</summary>
    public const string SessionNotFound = "session.not_found";

    /// <summary>No paired device has that id or name.</summary>
    public const string DeviceNotFound = "device.not_found";

    /// <summary>The pairing secret was wrong, expired, or no offer is open.</summary>
    public const string PairingRefused = "pair.refused";

    /// <summary>The node accepts no new work (it stops).</summary>
    public const string NodeStopping = "node.stopping";

    /// <summary>The node takes no new work for a moment: it drains its sessions to switch to a new version.</summary>
    public const string NodeUpdating = "node.updating";

    /// <summary>The project, the space, the provider or the model asked for is not available on the node.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>No project of the node has that id or name.</summary>
    public const string ProjectNotFound = "project.not_found";

    /// <summary>The project has sessions on the node: it is not removed.</summary>
    public const string ProjectHasSessions = "project.has_sessions";

    /// <summary>The repository could not be cloned.</summary>
    public const string CloneFailed = "project.clone_failed";

    /// <summary>The server refused the enrollment of the node (a token used, expired or unknown).</summary>
    public const string ServerRefused = "server.refused";

    /// <summary>The node could not reach the server.</summary>
    public const string ServerUnreachable = "server.unreachable";

    /// <summary>No automation assigned to the node has that id.</summary>
    public const string AutomationNotFound = "automation.not_found";

    /// <summary>The automation cannot run on the node as it is written (what is wrong is in the message).</summary>
    public const string AutomationInvalid = "automation.invalid";

    /// <summary>The node runs no automation for other devices (its host has no automation engine).</summary>
    public const string AutomationsUnavailable = "automation.unavailable";

    /// <summary>No secret or variable of the machine has that name.</summary>
    public const string SecretNotFound = "secret.not_found";

    /// <summary>No provider of the node has that key.</summary>
    public const string ProviderNotFound = "provider.not_found";

    /// <summary>The provider does not sign in with a device code (it signs in with a browser on the machine, or takes a key).</summary>
    public const string ProviderSignInUnsupported = "provider.sign_in_unsupported";

    /// <summary>The sign-in of the provider could not start on the node (what failed is in the message).</summary>
    public const string ProviderSignInFailed = "provider.sign_in_failed";

    /// <summary>The node has no such thing to sign in (<c>control.signins.*</c>).</summary>
    public const string SignInTargetNotFound = "signin.not_found";

    /// <summary>What to sign in does not sign in that way from another computer.</summary>
    public const string SignInUnsupported = "signin.unsupported";

    /// <summary>The sign-in could not start, or what it was given does not fit it.</summary>
    public const string SignInFailed = "signin.failed";

    /// <summary>This node does not sign in from the desktop (<see cref="FleetNodeServiceOptions.SignIns"/> is null).</summary>
    public const string SignInsUnavailable = "signin.unavailable";

    /// <summary>The node cannot change its providers (its host has none to change).</summary>
    public const string ProvidersUnavailable = "provider.unavailable";

    /// <summary>The secrets of the machine cannot be read or written (what failed is in the message).</summary>
    public const string SecretsUnavailable = "secret.unavailable";

    /// <summary>The node does not accept sessions or prompts from agents of other machines in that space (its owner has not allowed it).</summary>
    public const string AgentSessionsOff = "agents.off";

    /// <summary>The request of an agent is not signed, or not as the node requires (another device, another machine, too old, seen before).</summary>
    public const string AgentRequestInvalid = "agents.invalid";

    /// <summary>The request of an agent asks what the node cannot hold (a sandbox it does not have), or more than its policy allows.</summary>
    public const string AgentRequestDenied = "agents.denied";
}

/// <summary>The parameters of <c>pair.redeem</c>.</summary>
/// <param name="Secret">The one-time secret of the offer, base64url.</param>
/// <param name="Name">The name the client gives itself, shown in the node's paired devices.</param>
public sealed record FleetPairRedeemParams(string Secret, string Name)
{
    /// <summary>
    /// Gets whether the client asks to pair both ways: it trusts the node already, over this channel pinned to the key of the
    /// link, and the node records the client as a machine it reaches, when its offer said so (<see cref="Identity.PairingPayload.BothWays"/>).
    /// </summary>
    public bool BothWays { get; init; }

    /// <summary>Gets where the node reaches the client, as <c>host:port</c>, when it pairs both ways; empty when the client does not listen.</summary>
    public IReadOnlyList<string>? Addresses { get; init; }

    /// <summary>Gets what the node may do on the client (<c>view</c>, <c>drive</c>, <c>start</c>, <c>move</c>), when it pairs both ways.</summary>
    public IReadOnlyList<string>? Permissions { get; init; }
}

/// <summary>The result of <c>pair.redeem</c>.</summary>
/// <param name="NodeName">The name of the node.</param>
/// <param name="NodeDeviceId">The device id of the node.</param>
/// <param name="Permissions">What the client may do on the node (<c>view</c>, <c>drive</c>, <c>start</c>, <c>move</c>).</param>
public sealed record FleetPairRedeemResult(string NodeName, string NodeDeviceId, IReadOnlyList<string> Permissions)
{
    /// <summary>Gets whether the node recorded the client as a machine it reaches, as the client asked; false for one way only.</summary>
    public bool BothWays { get; init; }
}

/// <summary>A space of the node.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
public sealed record FleetSpaceInfo(string Id, string Name)
{
    /// <summary>
    /// Gets whether the node publishes the space to a server: a controller device of its person keeps a key of it, which seals its
    /// titles and projects; false for a space the node keeps to itself, and from an older node, which does not say.
    /// </summary>
    public bool Published { get; init; }
}

/// <summary>A project of the node.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Path">Its folder on the node (paired devices are the owner's).</param>
/// <param name="Spaces">The spaces it is in; empty for every space.</param>
public sealed record FleetProjectInfo(string Id, string Name, string Path, IReadOnlyList<string> Spaces)
{
    /// <summary>
    /// Gets the repository key of its checkout (the normalized URL of its <c>origin</c> remote), which recognizes the same
    /// repository on other machines; null for a folder in no repository with a remote.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepositoryKey { get; init; }

    /// <summary>Gets the path of its folder from the root of its checkout, with forward slashes (empty for the root); null without a repository key.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepositoryPath { get; init; }

    /// <summary>Gets whether the project is archived (only <c>control.projects.list</c> lists archived projects).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Archived { get; init; }
}

/// <summary>A model a ready provider of the node offers.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
public sealed record FleetModelInfo(string Id, string Name)
{
    /// <summary>
    /// Gets the reasoning efforts the model takes (<c>Low</c>, <c>High</c>...), which <see cref="FleetModelSelection.ReasoningEffort"/>
    /// names; empty for a model without one, null for a node that does not say (an older one).
    /// </summary>
    public IReadOnlyList<string>? Efforts { get; init; }

    /// <summary>Gets the effort the model runs with when none is named; null when it has none, or the node does not say.</summary>
    public string? DefaultEffort { get; init; }
}

/// <summary>A provider of the node that is ready to run sessions.</summary>
/// <param name="Key">Its key, which <see cref="FleetModelSelection.ProviderKey"/> names.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsDefault">Whether a session that names no provider runs on it.</param>
/// <param name="DefaultModelId">The model a session takes when it names none; null when the provider chooses.</param>
/// <param name="Models">Its models, as its last probe listed them.</param>
public sealed record FleetProviderInfo(string Key, string Name, bool IsDefault, string? DefaultModelId, IReadOnlyList<FleetModelInfo> Models)
{
    /// <summary>
    /// Gets the permission modes a session started from another device can be given on this provider
    /// (<see cref="FleetCreateSessionParams.PermissionMode"/>): those that ask at least as much as the mode it runs in without
    /// one; empty for a provider without modes, null for a node that does not say (an older one).
    /// </summary>
    public IReadOnlyList<string>? PermissionModes { get; init; }

    /// <summary>Gets the permission mode a new session of the provider runs in when it is given none; null when the node does not say.</summary>
    public string? DefaultPermissionMode { get; init; }

    /// <summary>Gets the sandbox profile the settings of the node give a new session of the provider; null for none.</summary>
    public string? Sandbox { get; init; }
}

/// <summary>An agent prompt a session of the node can run with.</summary>
/// <param name="Id">Its id, which <see cref="FleetModelSelection.AgentPromptId"/> names.</param>
/// <param name="Name">Its name.</param>
public sealed record FleetAgentPromptInfo(string Id, string Name);

/// <summary>The result of <c>node.info</c>.</summary>
/// <param name="Name">The name of the node.</param>
/// <param name="DeviceId">Its device id.</param>
/// <param name="Os">Its operating system (<c>windows</c>, <c>macos</c>, <c>linux</c>).</param>
/// <param name="Version">The version of CodeAlta it runs.</param>
/// <param name="Spaces">Its spaces.</param>
/// <param name="Projects">The projects of its catalog, archived ones left out.</param>
/// <param name="Permissions">What the caller may do on it.</param>
public sealed record FleetNodeInfo(string Name, string DeviceId, string Os, string Version, IReadOnlyList<FleetSpaceInfo> Spaces,
    IReadOnlyList<FleetProjectInfo> Projects, IReadOnlyList<string> Permissions)
{
    /// <summary>Gets the providers of the node that are ready to run sessions; empty for a node that does not say (an older one).</summary>
    public IReadOnlyList<FleetProviderInfo> Providers { get; init; } = [];

    /// <summary>Gets what the node could not describe (a project file it cannot read, left out of <see cref="Projects"/>); empty when all is well.</summary>
    public IReadOnlyList<FleetNodeDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Gets the versions of the node and where its self-update stands; null for a node that does not say (an older one).</summary>
    public Protocol.FleetUpdateStatus? Update { get; init; }

    /// <summary>Gets the load of the machine: the seats taken, the runs that wait, its limit; null for a node that does not say (an older one).</summary>
    public FleetMachineLoad? Load { get; init; }

    /// <summary>Gets the tags its owner gives the machine (<c>gpu</c>, <c>prod</c>); null for a node that does not say (an older one).</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Gets the agent prompts a new session of the node can run with; null for a node that does not say (an older one).</summary>
    public IReadOnlyList<FleetAgentPromptInfo>? AgentPrompts { get; init; }

    /// <summary>Gets the agent prompt a new session runs with when it names none; null when the node does not say.</summary>
    public string? DefaultAgentPromptId { get; init; }

    /// <summary>
    /// Gets the sandbox profiles a new session can be given (<see cref="FleetCreateSessionParams.Sandbox"/>); empty when the node
    /// cannot start a sandbox, null for a node that does not say (an older one).
    /// </summary>
    public IReadOnlyList<string>? SandboxProfiles { get; init; }
}

/// <summary>The load of a machine (<see cref="FleetSessionSeats"/>).</summary>
/// <param name="Working">The trees of sessions at work, and the runs admitted that did not start yet.</param>
/// <param name="Queued">The runs that wait for a seat.</param>
/// <param name="Limit">How many trees of sessions work at once; null for no limit.</param>
public sealed record FleetMachineLoad(int Working, int Queued, int? Limit);

/// <summary>Something the node could not describe in <c>node.info</c>, which its owner can fix.</summary>
/// <param name="Code">What it is: <see cref="FleetNodeDiagnostic.UnreadableProject"/>.</param>
/// <param name="Message">What is wrong, for people.</param>
/// <param name="Path">The file on the node, when it is about one.</param>
public sealed record FleetNodeDiagnostic(string Code, string Message, string? Path = null)
{
    /// <summary>A project file the node cannot read or that is not a valid project: the project is not listed.</summary>
    public const string UnreadableProject = "project.unreadable";
}

/// <summary>The parameters of <c>sessions.list</c>.</summary>
/// <param name="Limit">The most sessions to return (1 to 500, 100 by default).</param>
/// <param name="ProjectId">Keeps the sessions of one project; null for all.</param>
/// <param name="SpaceId">Keeps the sessions of one space; null for all.</param>
/// <param name="IncludeArchived">Whether archived sessions are listed.</param>
public sealed record FleetSessionListParams(int? Limit = null, string? ProjectId = null, string? SpaceId = null, bool IncludeArchived = false);

/// <summary>A session as the list shows it.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Title">Its title as lists show it.</param>
/// <param name="ProjectId">Its project; null for a session outside a project.</param>
/// <param name="SpaceId">Its space.</param>
/// <param name="ParentSessionId">The session that started it, for a sub-agent.</param>
/// <param name="ProviderKey">Its provider.</param>
/// <param name="ModelId">Its model.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="State">What it does: <c>working</c>, <c>waiting</c> (a question or a permission waits for a person), <c>idle</c> or <c>archived</c>.</param>
public sealed record FleetSessionSummary(string Id, string Title, string? ProjectId, string? SpaceId, string? ParentSessionId, string? ProviderKey,
    string? ModelId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string State)
{
    /// <summary>Gets the device id of the machine its parent runs on, for a session an agent of another machine started; null otherwise.</summary>
    public string? ParentMachineId { get; init; }

    /// <summary>Gets the name of that machine; null otherwise.</summary>
    public string? ParentMachineName { get; init; }

    /// <summary>Gets the place of its run in the queue of the seats of the machine (1 for the next one); null when it waits for none.</summary>
    public int? QueuedPosition { get; init; }
}

/// <summary>The result of <c>sessions.list</c>.</summary>
/// <param name="Sessions">The sessions, the latest activity first.</param>
/// <param name="HasMore">Whether sessions were left out by the limit.</param>
public sealed record FleetSessionListResult(IReadOnlyList<FleetSessionSummary> Sessions, bool HasMore)
{
    /// <summary>Gets the load of the machine when the list was read; null for a node that does not say (an older one).</summary>
    public FleetMachineLoad? Load { get; init; }
}

/// <summary>The parameters of a call about one session.</summary>
/// <param name="SessionId">The session.</param>
public sealed record FleetSessionRef(string SessionId);

/// <summary>The result of <c>sessions.state</c>: the identities the commands of a run are bound to.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="RuntimeInstanceId">The runtime of the node (changes when it restarts).</param>
/// <param name="AttachmentGeneration">The attachment of the session in that runtime; null when it is not attached.</param>
/// <param name="ActiveRunId">The run at work; null when none.</param>
/// <param name="QueueDrainInProgress">Whether queued prompts are being sent.</param>
public sealed record FleetSessionState(string SessionId, Guid RuntimeInstanceId, long? AttachmentGeneration, string? ActiveRunId, bool QueueDrainInProgress);

/// <summary>The parameters of <c>sessions.history</c>.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Cursor">The cursor a previous window gave (<see cref="FleetHistoryResult.Next"/>); null for the first window.</param>
/// <param name="Direction"><c>backward</c> (default: the latest records, then older ones) or <c>forward</c> (from the start).</param>
/// <param name="Format">
/// <c>entries</c> (default: <see cref="FleetHistoryResult.Entries"/>, each record projected and cut) or <c>events</c>
/// (<see cref="FleetHistoryResult.Events"/>: the journal's own events, which a desktop shows in its timeline).
/// </param>
/// <param name="Known">
/// For the latest <c>events</c> window (backward, no cursor): the stamp a previous one gave; the node answers
/// <see cref="FleetHistoryResult.Unchanged"/> with no events while the journal still has it.
/// </param>
public sealed record FleetHistoryParams(string SessionId, string? Cursor = null, string? Direction = null, string? Format = null, string? Known = null);

/// <summary>An event of the journal of a session, in the <c>events</c> format of <c>sessions.history</c>.</summary>
/// <param name="Offset">The offset of its record in the journal.</param>
/// <param name="End">The exclusive end of its record, when the window was read backward; null otherwise.</param>
/// <param name="Event">The event, as the journal holds it.</param>
public sealed record FleetHistoryEvent(long Offset, long? End, JsonElement Event);

/// <summary>The journal a window of events was read from.</summary>
/// <param name="Length">Its length.</param>
/// <param name="LastWriteUtcTicks">Its last write.</param>
public sealed record FleetHistoryRevision(long Length, long LastWriteUtcTicks);

/// <summary>One record of the timeline of a session.</summary>
/// <param name="Offset">Its offset in the journal: its identity in the timeline.</param>
/// <param name="Timestamp">When it was recorded.</param>
/// <param name="Type">What it is: <c>content</c>, <c>activity</c>, <c>error</c>, <c>notes</c>, <c>workflow</c>, <c>sessionUpdate</c>, <c>plan</c> or <c>other</c>.</param>
/// <param name="Kind">Its kind within the type (for content: <c>User</c>, <c>Assistant</c>, <c>Reasoning</c>, a tool output...).</param>
/// <param name="Text">Its text, cut at a bound.</param>
/// <param name="Truncated">Whether the text was cut.</param>
/// <param name="RunId">The run it belongs to.</param>
/// <param name="Name">The name of a tool call or an activity.</param>
/// <param name="Phase">The phase of an activity.</param>
/// <param name="ActivityId">The id of an activity.</param>
/// <param name="ParentId">The activity it belongs to.</param>
public sealed record FleetHistoryEntry(long Offset, DateTimeOffset Timestamp, string Type, string? Kind, string? Text, bool Truncated, string? RunId,
    string? Name = null, string? Phase = null, string? ActivityId = null, string? ParentId = null);

/// <summary>The result of <c>sessions.history</c>.</summary>
/// <param name="Entries">The records of the window, in the order of the journal.</param>
/// <param name="Next">The cursor of the next window (older for <c>backward</c>, newer for <c>forward</c>); null when there is none yet.</param>
/// <param name="HasMore">Whether records remain in that direction.</param>
public sealed record FleetHistoryResult(IReadOnlyList<FleetHistoryEntry> Entries, string? Next, bool HasMore)
{
    /// <summary>Gets the events of the window, in the order of the journal, in the <c>events</c> format; null otherwise.</summary>
    public IReadOnlyList<FleetHistoryEvent>? Events { get; init; }

    /// <summary>Gets the journal a window of events was read from, when the node knows it.</summary>
    public FleetHistoryRevision? Revision { get; init; }

    /// <summary>Gets the stamp of the journal of the latest window of events, to give back as <see cref="FleetHistoryParams.Known"/>; null when unsure.</summary>
    public string? Stamp { get; init; }

    /// <summary>Gets whether the journal still has the stamp the request knew: nothing was read.</summary>
    public bool Unchanged { get; init; }
}

/// <summary>The parameters of <c>sessions.watch</c>.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="WatchId">
/// The id the client gives the watch (1 to 64 letters, digits, <c>-</c> or <c>_</c>), so that it routes the events that may
/// come before the result; null to let the node choose one.
/// </param>
public sealed record FleetWatchParams(string SessionId, string? WatchId = null);

/// <summary>The result of <c>sessions.watch</c>.</summary>
/// <param name="WatchId">The id the display events of the watch carry.</param>
public sealed record FleetWatchResult(string WatchId);

/// <summary>The parameters of <c>sessions.unwatch</c>.</summary>
/// <param name="WatchId">The watch.</param>
public sealed record FleetUnwatchParams(string WatchId);

/// <summary>The lifecycle of a session in a display replacement.</summary>
/// <param name="Kind">The kind of the lifecycle (<c>Running</c>, <c>Idle</c>...).</param>
/// <param name="RunId">The run it is about.</param>
/// <param name="Message">What it says.</param>
public sealed record FleetDisplayLifecycle(string Kind, string? RunId, string? Message);

/// <summary>A text of the live display: a prompt, an answer or a reasoning being written.</summary>
/// <param name="RunId">Its run.</param>
/// <param name="ContentId">Its id.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Text">Its text so far, cut at the bound of the display.</param>
/// <param name="IsComplete">Whether it is complete.</param>
/// <param name="IsTruncated">Whether it was cut.</param>
public sealed record FleetDisplayText(string? RunId, string ContentId, string Kind, string Text, bool IsComplete, bool IsTruncated)
{
    /// <summary>Gets the retry key of the prompt of a user message, as the node recorded it (<c>fleet:&lt;device&gt;:&lt;key&gt;</c> for a device's); null for other texts.</summary>
    public string? ClientRequestId { get; init; }
}

/// <summary>A tool call of the live display.</summary>
/// <param name="ActivityId">Its activity.</param>
/// <param name="RunId">Its run.</param>
/// <param name="Phase">Its phase.</param>
/// <param name="Name">The name of the tool.</param>
public sealed record FleetDisplayTool(string ActivityId, string? RunId, string Phase, string? Name);

/// <summary>
/// The payload of a <c>session.display</c> event: a full replacement of the live display of a session, never a delta.
/// Intermediate replacements are dropped for a slow reader: <see cref="HasGap"/> says so.
/// </summary>
/// <param name="WatchId">The watch.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Revision">The revision of the display.</param>
/// <param name="PreviousRevision">The revision the reader got before; null for the first.</param>
/// <param name="HasGap">Whether revisions were dropped since the previous one.</param>
/// <param name="IsClosed">Whether the display of the node closed (it stops): the watch ends.</param>
/// <param name="Lifecycle">The lifecycle of the session; null when the runtime has none.</param>
/// <param name="QueuedPromptCount">The prompts queued for after the turn.</param>
/// <param name="StatusKind">The kind of the status line.</param>
/// <param name="StatusMessage">The status line.</param>
/// <param name="Text">The texts of the display.</param>
/// <param name="Tools">The tool calls of the display.</param>
public sealed record FleetSessionDisplay(string WatchId, string SessionId, long Revision, long? PreviousRevision, bool HasGap, bool IsClosed,
    FleetDisplayLifecycle? Lifecycle, int? QueuedPromptCount, string? StatusKind, string? StatusMessage,
    IReadOnlyList<FleetDisplayText> Text, IReadOnlyList<FleetDisplayTool> Tools);

/// <summary>The payload of a <c>session.watch.ended</c> event.</summary>
/// <param name="WatchId">The watch.</param>
/// <param name="Reason">
/// Why: <c>closed</c>, <c>failed</c>, <c>unwatched</c>, <c>private</c> when the session became Private, or <c>forbidden</c> once the
/// watcher may no longer read it (a grant expired or taken away, a pairing revoked or narrowed).
/// </param>
public sealed record FleetWatchEnded(string WatchId, string Reason);

/// <summary>The model a prompt asks for; null fields keep the session's.</summary>
/// <param name="ProviderKey">The provider.</param>
/// <param name="ModelId">The model.</param>
/// <param name="AgentPromptId">The agent prompt (<c>default</c> when null).</param>
/// <param name="ReasoningEffort">The reasoning effort (<c>Low</c>, <c>Medium</c>, <c>High</c>...).</param>
public sealed record FleetModelSelection(string? ProviderKey = null, string? ModelId = null, string? AgentPromptId = null, string? ReasoningEffort = null);

/// <summary>The parameters of <c>sessions.create</c>. The request's idempotency key makes a retry the same creation.</summary>
/// <param name="ProjectId">The project; null for a session outside a project.</param>
/// <param name="SpaceId">The space; null for the space of the project, or the default space.</param>
/// <param name="Title">The title; null to name it from its prompt.</param>
/// <param name="Prompt">The first prompt; null to create a session that waits for one.</param>
/// <param name="Selection">The provider and model; null for the defaults of the node.</param>
public sealed record FleetCreateSessionParams(string? ProjectId = null, string? SpaceId = null, string? Title = null, string? Prompt = null,
    FleetModelSelection? Selection = null)
{
    /// <summary>
    /// Gets the environment of the fleet the session uses; null for none. The node admits the session first (its rules,
    /// and its approval when it requires one): its first prompt is sent once it was admitted, and its processes get the
    /// values of the environment.
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>
    /// Gets the signed request of the agent that starts the session as the child of its own, on behalf of the person of
    /// the calling device (<see cref="Protocol.FleetAgentRequestKinds.Start"/>); null for a session a person starts.
    /// </summary>
    public Protocol.FleetSignedAgentRequest? Agent { get; init; }

    /// <summary>
    /// Gets the permission mode the session runs in: one of <see cref="FleetProviderInfo.PermissionModes"/>, never one that asks
    /// less than the mode the node gives it; null for that mode. A session an agent starts takes its mode from its request.
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// Gets the sandbox the session runs in: a profile of <see cref="FleetNodeInfo.SandboxProfiles"/>, or <c>off</c>, never one
    /// weaker than the sandbox the settings of the node give it; null for that sandbox.
    /// </summary>
    public string? Sandbox { get; init; }

    /// <summary>Gets whether the session can start sessions of its own; null for yes. A session an agent starts takes it from its request.</summary>
    public bool? SubSessions { get; init; }
}

/// <summary>The result of <c>sessions.create</c>.</summary>
/// <param name="SessionId">The new session.</param>
/// <param name="ProviderKey">The provider it runs on.</param>
/// <param name="Send">The admission of its first prompt; null without one.</param>
public sealed record FleetCreateSessionResult(string SessionId, string ProviderKey, FleetCommandResult? Send)
{
    /// <summary>Gets the permission mode a session an agent started runs in, as the node decided it; null for another session.</summary>
    public string? PermissionMode { get; init; }

    /// <summary>Gets the sandbox profile a session an agent started runs in; null for none.</summary>
    public string? Sandbox { get; init; }
}

/// <summary>The parameters of <c>sessions.send</c>. The request's idempotency key is the retry key of the owned command.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Text">The prompt.</param>
/// <param name="Selection">The model of the turn; null keeps the session's.</param>
public sealed record FleetSendParams(string SessionId, string Text, FleetModelSelection? Selection = null)
{
    /// <summary>Gets the signed request of the agent that sends it (<see cref="Protocol.FleetAgentRequestKinds.Send"/>); null for a person's prompt.</summary>
    public Protocol.FleetSignedAgentRequest? Agent { get; init; }
}

/// <summary>The parameters of <c>sessions.queue</c>.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Text">The prompt to send after the running turn.</param>
public sealed record FleetQueueParams(string SessionId, string Text)
{
    /// <summary>Gets the signed request of the agent that queues it (<see cref="Protocol.FleetAgentRequestKinds.Queue"/>); null for a person's prompt.</summary>
    public Protocol.FleetSignedAgentRequest? Agent { get; init; }
}

/// <summary>The parameters of <c>sessions.steer</c>, bound to the exact run (<see cref="FleetSessionState"/>).</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Text">The text given to the running turn.</param>
/// <param name="RuntimeInstanceId">The runtime the caller saw.</param>
/// <param name="AttachmentGeneration">The attachment the caller saw.</param>
/// <param name="RunId">The run the caller saw.</param>
public sealed record FleetSteerParams(string SessionId, string Text, Guid RuntimeInstanceId, long AttachmentGeneration, string RunId)
{
    /// <summary>Gets the signed request of the agent that steers (<see cref="Protocol.FleetAgentRequestKinds.Steer"/>); null for a person's text.</summary>
    public Protocol.FleetSignedAgentRequest? Agent { get; init; }
}

/// <summary>
/// The parameters of <c>sessions.abort</c>: either the send to stop (<see cref="OperationId"/>), or the exact run of the
/// session (<see cref="RuntimeInstanceId"/>, <see cref="AttachmentGeneration"/> and <see cref="RunId"/>).
/// </summary>
/// <param name="SessionId">The session.</param>
/// <param name="OperationId">The operation of a send this caller was given.</param>
/// <param name="RuntimeInstanceId">The runtime the caller saw.</param>
/// <param name="AttachmentGeneration">The attachment the caller saw.</param>
/// <param name="RunId">The run the caller saw.</param>
public sealed record FleetAbortParams(string SessionId, Guid? OperationId = null, Guid? RuntimeInstanceId = null, long? AttachmentGeneration = null, string? RunId = null);

/// <summary>
/// The admission of an owned command: <c>accepted</c>, <c>replay</c> (a retry of the same key: the first receipt),
/// <c>conflict</c> (the key was used for another command), <c>busy</c>, <c>capacity</c>, <c>closed</c>,
/// <c>unknown_target</c> or <c>expired</c> (a retry whose receipt is gone: never run again).
/// </summary>
/// <param name="Status">The admission.</param>
/// <param name="OperationId">The operation of the receipt; null when the command was not admitted.</param>
/// <param name="Outcome">How the command settled when it already did (<c>Completed</c>, <c>Cancelled</c>, <c>Failed</c>); null while it runs.</param>
/// <param name="RunId">The run the command started or acted on, once known.</param>
/// <param name="Code">The code of its outcome.</param>
public sealed record FleetCommandResult(string Status, Guid? OperationId = null, string? Outcome = null, string? RunId = null, string? Code = null)
{
    /// <summary>Gets the place of a send that waits for a seat of the machine (<see cref="FleetSeatStatuses.Queued"/>): 1 for the next one; null otherwise.</summary>
    public int? Position { get; init; }
}

/// <summary>The status of a send that waits for a seat of the machine.</summary>
public static class FleetSeatStatuses
{
    /// <summary>The machine runs as many sessions as its owner lets it: the send waits for its turn, and is sent then.</summary>
    public const string Queued = "queued";
}

/// <summary>The result of <c>sessions.queue</c>.</summary>
/// <param name="Status"><c>queued</c> (a retry of the same key gets the first result).</param>
/// <param name="QueueItemId">The queued prompt.</param>
public sealed record FleetQueueResult(string Status, string QueueItemId);

/// <summary>A permission request that waits for a person.</summary>
/// <param name="Id">Its id, which the answer names.</param>
/// <param name="RunId">The run that asks.</param>
/// <param name="Kind">What it asks for: <c>commandExecution</c>, <c>fileChange</c>, <c>browser</c>, <c>skill</c>...</param>
/// <param name="Command">The command, for a command.</param>
/// <param name="WorkingDirectory">Where the command runs.</param>
/// <param name="GrantRoot">The folder, for a file change.</param>
/// <param name="Reason">Why the agent asks.</param>
/// <param name="Timestamp">When it asked.</param>
public sealed record FleetPermissionRequest(Guid Id, string? RunId, string Kind, string? Command, string? WorkingDirectory, string? GrantRoot, string? Reason,
    DateTimeOffset Timestamp);

/// <summary>The result of <c>sessions.permissions.list</c>.</summary>
/// <param name="Requests">The requests that wait.</param>
/// <param name="HasMore">Whether more wait than were listed.</param>
public sealed record FleetPermissionList(IReadOnlyList<FleetPermissionRequest> Requests, bool HasMore);

/// <summary>The parameters of <c>sessions.permissions.answer</c>.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Id">The request.</param>
/// <param name="Decision"><c>allow_once</c>, <c>allow_always</c>, <c>deny</c> or <c>cancel</c>.</param>
public sealed record FleetPermissionAnswerParams(string SessionId, Guid Id, string Decision);

/// <summary>The result of an answer: <c>resolved</c>, <c>rejected</c> (no longer waits) or <c>not_found</c>.</summary>
/// <param name="Status">The status.</param>
/// <param name="RunId">The run the answer went to, when known.</param>
public sealed record FleetAnswerResult(string Status, string? RunId = null);

/// <summary>A choice of a question.</summary>
/// <param name="Title">Its title.</param>
/// <param name="Description">What it means.</param>
public sealed record FleetAskChoice(string? Title, string? Description);

/// <summary>A question of an ask.</summary>
/// <param name="Title">Its title.</param>
/// <param name="Question">The question.</param>
/// <param name="Description">Its details.</param>
/// <param name="Choices">Its choices.</param>
/// <param name="AllowsText">Whether a free answer is allowed.</param>
public sealed record FleetAskQuestion(string? Title, string? Question, string? Description, IReadOnlyList<FleetAskChoice> Choices, bool AllowsText);

/// <summary>The question of a session that waits for a person.</summary>
/// <param name="AskId">Its id.</param>
/// <param name="RunId">The run that asks.</param>
/// <param name="State">Its state.</param>
/// <param name="Questions">Its questions.</param>
public sealed record FleetAsk(string AskId, string RunId, string State, IReadOnlyList<FleetAskQuestion> Questions);

/// <summary>The result of <c>sessions.asks.list</c>.</summary>
/// <param name="Ask">The question that waits; null when none does.</param>
/// <param name="HasMore">Whether more wait after it.</param>
public sealed record FleetAskList(FleetAsk? Ask, bool HasMore);

/// <summary>The answer to one question.</summary>
/// <param name="QuestionIndex">The question.</param>
/// <param name="Choices">The indexes of the choices taken.</param>
/// <param name="Text">The free answer.</param>
public sealed record FleetAskAnswer(int QuestionIndex, IReadOnlyList<int> Choices, string? Text = null);

/// <summary>The parameters of <c>sessions.asks.answer</c>; the idempotency key makes a retry the same answer.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="AskId">The question.</param>
/// <param name="Answers">The answers; ignored when <paramref name="Cancel"/> is true.</param>
/// <param name="Cancel">Whether the question is cancelled instead.</param>
public sealed record FleetAskAnswerParams(string SessionId, string AskId, IReadOnlyList<FleetAskAnswer> Answers, bool Cancel = false);

/// <summary>The parameters of <c>control.pairing.create</c>.</summary>
/// <param name="LifetimeSeconds">How long the offer stays open; null for 10 minutes.</param>
/// <param name="Manage">Whether the device that redeems the offer manages the node (<see cref="Identity.DevicePermissions.Manage"/>).</param>
public sealed record FleetPairingCreateParams(int? LifetimeSeconds = null, bool Manage = false);

/// <summary>The result of <c>control.pairing.create</c>.</summary>
/// <param name="Link">The <c>codealta-pair://</c> link to give the other device.</param>
/// <param name="ExpiresAt">When the offer expires.</param>
/// <param name="Addresses">The addresses the link names.</param>
public sealed record FleetPairingOffer(string Link, DateTimeOffset ExpiresAt, IReadOnlyList<string> Addresses);

/// <summary>The result of <c>control.pairing.wait</c>.</summary>
/// <param name="Status"><c>redeemed</c>, <c>expired</c> or <c>replaced</c>.</param>
/// <param name="DeviceName">The device that paired.</param>
/// <param name="DeviceId">Its device id.</param>
/// <param name="BothWays">Whether the node recorded the device as a machine it reaches too.</param>
public sealed record FleetPairingOutcome(string Status, string? DeviceName = null, string? DeviceId = null, bool BothWays = false);

/// <summary>The result of <c>control.status</c>.</summary>
/// <param name="Name">The name of the node.</param>
/// <param name="DeviceId">Its device id.</param>
/// <param name="Version">The version of CodeAlta it runs.</param>
/// <param name="AcceptsWork">Whether it accepts new work (false once it stops).</param>
/// <param name="SessionsAtWork">How many sessions work now (a turn, a script, a job).</param>
/// <param name="Channels">How many channels of paired devices are open.</param>
/// <param name="TrustedDevices">How many devices are paired and not revoked.</param>
/// <param name="RevokedDevices">How many devices were revoked.</param>
public sealed record FleetNodeStatus(string Name, string DeviceId, string Version, bool AcceptsWork, int SessionsAtWork, int Channels,
    int TrustedDevices, int RevokedDevices)
{
    /// <summary>Gets whether the node refuses new work because it drains to switch versions, not because it stops.</summary>
    public bool Updating { get; init; }

    /// <summary>Gets the versions of the node and where its self-update stands; null for a node that does not say.</summary>
    public Protocol.FleetUpdateStatus? Update { get; init; }

    /// <summary>Gets what the worktrees of its sessions take on its disk; null before their first measure, or for a node that does not say.</summary>
    public FleetWorktreeUsage? Worktrees { get; init; }

    /// <summary>Gets the sandbox its sessions run in by default, and whether it can run there; null for a node that does not say.</summary>
    public FleetNodeSandboxStatus? Sandbox { get; init; }

    /// <summary>Gets the service that runs the node, and whether it will still run after a restart or a logout; null for a node that does not say.</summary>
    public FleetNodeServiceStatus? Service { get; init; }
}

/// <summary>The service that runs a node (<c>altanode install</c>), and whether it will still run after a restart or a logout.</summary>
/// <param name="Installed">Whether a service runs the node.</param>
/// <param name="StartsAtBoot">Whether it starts when the machine starts, with nobody logged in.</param>
/// <param name="Lines">What it is and what happens after a restart and a logout, in words (as <c>altanode status</c> says it).</param>
/// <param name="Platform">The OS: <c>windows</c>, <c>macos</c> or <c>linux</c>.</param>
/// <param name="User">The user the node runs as.</param>
public sealed record FleetNodeServiceStatus(bool Installed, bool StartsAtBoot, IReadOnlyList<string> Lines, string Platform, string User)
{
    /// <summary>Gets the commands, one per line, that make the node start at boot as the same person with the same options; null when it does.</summary>
    public string? Fix { get; init; }
}

/// <summary>A paired device as the owner's local control shows it.</summary>
/// <param name="DeviceId">Its device id.</param>
/// <param name="Name">The name it gave itself at pairing.</param>
/// <param name="Fingerprint">The fingerprint of its key, as people read it.</param>
/// <param name="Permissions">What it may do (<c>view</c>, <c>drive</c>, <c>start</c>, <c>move</c>).</param>
/// <param name="PairedAt">When it was paired.</param>
/// <param name="RevokedAt">When it was revoked; null while it is trusted.</param>
/// <param name="Channels">How many channels it has open now.</param>
public sealed record FleetDeviceInfo(string DeviceId, string Name, string Fingerprint, IReadOnlyList<string> Permissions, DateTimeOffset PairedAt,
    DateTimeOffset? RevokedAt, int Channels);

/// <summary>A secret or a variable of the machine as its owner's local control shows it: never the value of a secret.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Version">Its version: 1 when set, one more at each change.</param>
/// <param name="SetBy">Who set its current value.</param>
/// <param name="SetAt">When its current value was set.</param>
/// <param name="Value">The value of a variable; null for a secret.</param>
public sealed record FleetSecretInfo(string Name, int Version, string SetBy, DateTimeOffset SetAt, string? Value);

/// <summary>The result of <c>control.secrets.list</c>.</summary>
/// <param name="Secrets">The secrets, by name, without their values.</param>
/// <param name="Variables">The variables, with their values.</param>
/// <param name="Protection">What protects the values of the secrets (<c>dpapi</c>, <c>keychain</c>, <c>libsecret</c>, <c>file</c>); null while none is set.</param>
public sealed record FleetSecretList(IReadOnlyList<FleetSecretInfo> Secrets, IReadOnlyList<FleetSecretInfo> Variables, string? Protection);

/// <summary>The parameters of <c>control.secrets.set</c> and <c>control.secrets.rotate</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Value">The value: written, never sent back.</param>
/// <param name="Variable">Whether it is a variable rather than a secret.</param>
public sealed record FleetSecretSetParams(string Name, string Value, bool Variable = false);

/// <summary>The parameters of <c>control.secrets.delete</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Variable">Whether it is a variable rather than a secret.</param>
public sealed record FleetSecretRef(string Name, bool Variable = false);

/// <summary>The result of a change of a secret or a variable.</summary>
/// <param name="Secret">What is known of it now; null once deleted.</param>
/// <param name="Changed">Whether something changed.</param>
public sealed record FleetSecretChange(FleetSecretInfo? Secret, bool Changed);

/// <summary>The result of <c>control.devices.list</c>.</summary>
/// <param name="Devices">The devices, in the order they were paired.</param>
public sealed record FleetDeviceList(IReadOnlyList<FleetDeviceInfo> Devices);

/// <summary>The parameters of <c>control.devices.revoke</c>.</summary>
/// <param name="Device">The device: its id, its name, or the start of its id.</param>
public sealed record FleetDeviceRef(string Device);

/// <summary>The parameters of <c>control.devices.permissions</c>: what is denied wins over what is allowed.</summary>
/// <param name="Device">The device: its id, its name, or the start of its id.</param>
/// <param name="Allow">The permissions to add.</param>
/// <param name="Deny">The permissions to remove.</param>
public sealed record FleetDevicePermissionsParams(string Device, IReadOnlyList<string> Allow, IReadOnlyList<string> Deny);

/// <summary>The result of <c>control.devices.revoke</c> and <c>control.devices.permissions</c>.</summary>
/// <param name="Device">The device as it is now.</param>
/// <param name="Changed">Whether anything changed.</param>
/// <param name="ClosedChannels">How many of its channels were closed: a revoked device, or one that lost a permission, reconnects to go on.</param>
public sealed record FleetDeviceChange(FleetDeviceInfo Device, bool Changed, int ClosedChannels);

/// <summary>The result of <c>control.projects.list</c>.</summary>
/// <param name="Projects">The projects of the catalog, archived ones included, by name.</param>
/// <param name="Diagnostics">The project files that could not be read (<see cref="FleetNodeDiagnostic.UnreadableProject"/>).</param>
public sealed record FleetProjectList(IReadOnlyList<FleetProjectInfo> Projects, IReadOnlyList<FleetNodeDiagnostic> Diagnostics);

/// <summary>The parameters of <c>control.projects.add</c>.</summary>
/// <param name="Path">The folder, an absolute path of the node.</param>
/// <param name="Space">The space the project joins too, by id or name; null for the default space only.</param>
public sealed record FleetProjectAddParams(string Path, string? Space = null);

/// <summary>The parameters of <c>control.projects.clone</c>.</summary>
/// <param name="Url">The URL of the repository (https, ssh, git or <c>user@host:owner/repo</c>).</param>
/// <param name="Space">The space the project joins too, by id or name; null for the default space only.</param>
public sealed record FleetProjectCloneParams(string Url, string? Space = null);

/// <summary>The parameters of <c>control.projects.remove</c>.</summary>
/// <param name="Project">The project: its id, its slug or its name.</param>
public sealed record FleetProjectRef(string Project);

/// <summary>The result of <c>control.projects.add</c>, <c>clone</c> and <c>remove</c>.</summary>
/// <param name="Project">The project as it is now (as it was, for a removal).</param>
/// <param name="Changed">Whether the catalog changed: false for a folder it had already.</param>
/// <param name="Note">What else the person should know (where the repository was cloned, a folder reused); null for nothing.</param>
public sealed record FleetProjectChange(FleetProjectInfo Project, bool Changed, string? Note = null);

/// <summary>An empty result.</summary>
public sealed record FleetEmpty;

/// <summary>
/// Whether agents of other machines may start sessions on the node and send prompts to its sessions
/// (<c>doc/fleet/access-control.md</c>, "What the node decides"): never in a space it does not name, which is every space
/// by default.
/// </summary>
/// <param name="Spaces">The spaces where they may.</param>
/// <param name="Sandbox">The sandbox profile their sessions run in at least; null for the one their parent has.</param>
/// <param name="AllowBypass">Whether their sessions may bypass permissions (when their parent does); off by default.</param>
public sealed record FleetAgentPolicyInfo(IReadOnlyList<string> Spaces, string? Sandbox = null, bool AllowBypass = false);

/// <summary>A person of the organization a personal machine names, with what their devices may do there.</summary>
/// <param name="PersonId">The person, as the signed device list names them.</param>
/// <param name="Permissions">What their controller devices may do: <c>view</c>, <c>drive</c>, <c>start</c>; on their own sessions only.</param>
/// <param name="AddedAt">When the owner named them.</param>
/// <param name="OrganizationId">The organization of the person, whose ids are unique within it only; null in a list written before it was kept.</param>
public sealed record FleetNodePerson(string PersonId, IReadOnlyList<string> Permissions, DateTimeOffset AddedAt, string? OrganizationId = null);

/// <summary>A person of the organization who tried to start a session on the machine, and waits for its owner.</summary>
/// <param name="PersonId">The person.</param>
/// <param name="DeviceId">The device they asked from.</param>
/// <param name="AskedAt">When they first asked.</param>
/// <param name="OrganizationId">The organization of the person; null for a request written before it was kept.</param>
public sealed record FleetNodeAsk(string PersonId, string DeviceId, DateTimeOffset AskedAt, string? OrganizationId = null);

/// <summary>The people a personal machine lets in, and who asks to be.</summary>
/// <param name="People">The people it names.</param>
/// <param name="Asks">The requests that wait for its owner.</param>
/// <param name="Refused">The people its owner refused: their requests are not recorded again.</param>
public sealed record FleetNodePeopleInfo(IReadOnlyList<FleetNodePerson> People, IReadOnlyList<FleetNodeAsk> Asks, IReadOnlyList<string> Refused);

/// <summary>The parameters of <c>control.people.set</c>: one person let in, changed, taken out, or refused.</summary>
/// <param name="PersonId">The person.</param>
/// <param name="Permissions">What they may do; null or empty takes them out.</param>
/// <param name="Refuse">Whether to refuse their request: it is dropped and not recorded again until they are let in.</param>
/// <param name="OrganizationId">
/// The organization of the person; null for the one of their request, else the only organization the machine is linked to.
/// </param>
public sealed record FleetNodePeopleChange(string PersonId, IReadOnlyList<string>? Permissions = null, bool Refuse = false, string? OrganizationId = null);

/// <summary>
/// What the sessions of a node run with when nothing else chooses it (<c>control.session_defaults.get</c>,
/// <c>control.session_defaults.set</c>; <see cref="FleetSessionDefaults"/>).
/// </summary>
/// <param name="PermissionMode">
/// The permission mode of a session started there without one of its own: <c>bypassPermissions</c>, the default, or
/// <c>default</c> (ask first). A policy of the organization narrows it, and a session an agent of another machine starts
/// has at most the mode of its parent.
/// </param>
public sealed record FleetSessionDefaultsInfo(string PermissionMode);

/// <summary>The labels the owner gives a machine (<c>control.labels.get</c>, <c>control.labels.set</c>), which it publishes in its inventory.</summary>
/// <param name="Labels">The labels, such as <c>gpu</c> or <c>prod</c>.</param>
public sealed record FleetMachineLabelsInfo(IReadOnlyList<string> Labels);

/// <summary>The limits the owner gives a machine (<c>control.limits.get</c>, <c>control.limits.set</c>).</summary>
/// <param name="Sessions">How many sessions work there at once, a session and its sub-sessions counting once; null for no limit.</param>
public sealed record FleetMachineLimitsInfo(int? Sessions);

/// <summary>The parameters of <c>seats.start</c> and <c>seats.cancel</c>.</summary>
/// <param name="Id">The run that waits (<see cref="FleetSeatQueueItem.Id"/>).</param>
public sealed record FleetSeatRef(string Id);

/// <summary>The result of <c>seats.start</c> and <c>seats.cancel</c>.</summary>
/// <param name="Status"><c>ok</c>, or <c>not_found</c> when the run no longer waits.</param>
public sealed record FleetSeatResult(string Status);

/// <summary>The parameters of <c>agents.deliveries.take</c>.</summary>
/// <param name="Limit">The most deliveries to take; null for 50.</param>
public sealed record FleetAgentDeliveryTakeParams(int? Limit = null);

/// <summary>What a session an agent of the caller started said to its parent.</summary>
/// <param name="Id">The id of the delivery, which the acknowledgement names.</param>
/// <param name="ChildSessionId">The session that speaks.</param>
/// <param name="ChildProjectId">Its project on the node, or null.</param>
/// <param name="ParentSessionId">Its parent, on the caller's machine.</param>
/// <param name="Kind"><c>answer</c>, <c>error</c>, or an update (<c>progress</c>...).</param>
/// <param name="Body">What it says.</param>
/// <param name="RunId">The run it is about, or null.</param>
/// <param name="ContentId">The content it comes from.</param>
/// <param name="CreatedAt">When the node kept it.</param>
public sealed record FleetAgentDelivery(string Id, string ChildSessionId, string? ChildProjectId, string ParentSessionId, string Kind, string Body,
    string? RunId, string ContentId, DateTimeOffset CreatedAt);

/// <summary>The result of <c>agents.deliveries.take</c>.</summary>
/// <param name="Deliveries">The deliveries, oldest first.</param>
public sealed record FleetAgentDeliveryList(IReadOnlyList<FleetAgentDelivery> Deliveries);

/// <summary>The parameters of <c>agents.deliveries.ack</c>.</summary>
/// <param name="Ids">The deliveries taken and delivered.</param>
public sealed record FleetAgentDeliveryAckParams(IReadOnlyList<string> Ids);

/// <summary>The source-generated JSON contracts of the node API.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetPairRedeemParams))]
[JsonSerializable(typeof(FleetPairRedeemResult))]
[JsonSerializable(typeof(FleetNodeInfo))]
[JsonSerializable(typeof(FleetSessionListParams))]
[JsonSerializable(typeof(FleetSessionListResult))]
[JsonSerializable(typeof(FleetSessionRef))]
[JsonSerializable(typeof(FleetSessionState))]
[JsonSerializable(typeof(FleetHistoryParams))]
[JsonSerializable(typeof(FleetHistoryResult))]
[JsonSerializable(typeof(FleetHistoryEntry))]
[JsonSerializable(typeof(FleetWatchParams))]
[JsonSerializable(typeof(FleetWatchResult))]
[JsonSerializable(typeof(FleetUnwatchParams))]
[JsonSerializable(typeof(FleetSessionDisplay))]
[JsonSerializable(typeof(FleetWatchEnded))]
[JsonSerializable(typeof(FleetCreateSessionParams))]
[JsonSerializable(typeof(FleetCreateSessionResult))]
[JsonSerializable(typeof(FleetSendParams))]
[JsonSerializable(typeof(FleetQueueParams))]
[JsonSerializable(typeof(FleetSteerParams))]
[JsonSerializable(typeof(FleetAbortParams))]
[JsonSerializable(typeof(FleetCommandResult))]
[JsonSerializable(typeof(FleetQueueResult))]
[JsonSerializable(typeof(FleetPermissionList))]
[JsonSerializable(typeof(FleetPermissionAnswerParams))]
[JsonSerializable(typeof(FleetAnswerResult))]
[JsonSerializable(typeof(FleetAskList))]
[JsonSerializable(typeof(FleetAskAnswerParams))]
[JsonSerializable(typeof(FleetPairingCreateParams))]
[JsonSerializable(typeof(FleetPairingOffer))]
[JsonSerializable(typeof(FleetPairingOutcome))]
[JsonSerializable(typeof(FleetNodeStatus))]
[JsonSerializable(typeof(FleetDeviceList))]
[JsonSerializable(typeof(FleetSecretList))]
[JsonSerializable(typeof(FleetSecretSetParams))]
[JsonSerializable(typeof(FleetSecretRef))]
[JsonSerializable(typeof(FleetSecretChange))]
[JsonSerializable(typeof(FleetProviderList))]
[JsonSerializable(typeof(FleetProviderStatus))]
[JsonSerializable(typeof(FleetProviderSetParams))]
[JsonSerializable(typeof(FleetProviderRef))]
[JsonSerializable(typeof(FleetProviderSignInPrompt))]
[JsonSerializable(typeof(FleetProviderSignInState))]
[JsonSerializable(typeof(FleetProviderSignInWaitParams))]
[JsonSerializable(typeof(FleetNodeServiceStatus))]
[JsonSerializable(typeof(FleetMachineSignInList))]
[JsonSerializable(typeof(FleetMachineSignInItem))]
[JsonSerializable(typeof(FleetMachineSignInTarget))]
[JsonSerializable(typeof(FleetMachineSignInStartParams))]
[JsonSerializable(typeof(FleetMachineSignInPrompt))]
[JsonSerializable(typeof(FleetMachineSignInCompleteParams))]
[JsonSerializable(typeof(FleetMachineSignInWaitParams))]
[JsonSerializable(typeof(FleetMachineSignInAttemptRef))]
[JsonSerializable(typeof(FleetMachineSignInState))]
[JsonSerializable(typeof(FleetDeviceRef))]
[JsonSerializable(typeof(FleetDevicePermissionsParams))]
[JsonSerializable(typeof(FleetDeviceChange))]
[JsonSerializable(typeof(FleetEmpty))]
[JsonSerializable(typeof(FleetProjectList))]
[JsonSerializable(typeof(FleetProjectAddParams))]
[JsonSerializable(typeof(FleetProjectCloneParams))]
[JsonSerializable(typeof(FleetProjectRef))]
[JsonSerializable(typeof(FleetProjectChange))]
[JsonSerializable(typeof(FleetNodeLogParams))]
[JsonSerializable(typeof(FleetNodeLog))]
[JsonSerializable(typeof(FleetNodeUpdateParams))]
[JsonSerializable(typeof(FleetNodeUpdateResult))]
[JsonSerializable(typeof(FleetServerJoinParams))]
[JsonSerializable(typeof(FleetServerJoinResult))]
[JsonSerializable(typeof(FleetMoveOffer))]
[JsonSerializable(typeof(FleetMoveOfferResult))]
[JsonSerializable(typeof(FleetMoveChunk))]
[JsonSerializable(typeof(FleetMoveChunkResult))]
[JsonSerializable(typeof(FleetMoveRef))]
[JsonSerializable(typeof(FleetMoveStepResult))]
[JsonSerializable(typeof(FleetMoveStatusParams))]
[JsonSerializable(typeof(FleetMoveStatus))]
[JsonSerializable(typeof(FleetMoveList))]
[JsonSerializable(typeof(FleetMoveStartParams))]
[JsonSerializable(typeof(FleetMoveResult))]
[JsonSerializable(typeof(FleetAutomationPutParams))]
[JsonSerializable(typeof(FleetAutomationRef))]
[JsonSerializable(typeof(FleetAutomationRemoved))]
[JsonSerializable(typeof(FleetAutomationInfo))]
[JsonSerializable(typeof(FleetAutomationList))]
[JsonSerializable(typeof(FleetAutomationRunsParams))]
[JsonSerializable(typeof(FleetAutomationRunInfo))]
[JsonSerializable(typeof(FleetAutomationRunList))]
[JsonSerializable(typeof(FleetAgentPolicyInfo))]
[JsonSerializable(typeof(FleetNodePeopleInfo))]
[JsonSerializable(typeof(FleetNodePeopleChange))]
[JsonSerializable(typeof(FleetMachineLabelsInfo))]
[JsonSerializable(typeof(FleetMachineLimitsInfo))]
[JsonSerializable(typeof(FleetSeatsSnapshot))]
[JsonSerializable(typeof(FleetSeatRef))]
[JsonSerializable(typeof(FleetSeatResult))]
[JsonSerializable(typeof(FleetSessionDefaultsInfo))]
[JsonSerializable(typeof(FleetAgentDeliveryTakeParams))]
[JsonSerializable(typeof(FleetAgentDeliveryList))]
[JsonSerializable(typeof(FleetAgentDeliveryAckParams))]
public sealed partial class FleetNodeJsonContext : JsonSerializerContext;
