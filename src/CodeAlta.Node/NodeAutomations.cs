using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Automations;
using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Core.IO;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Node;

/// <summary>
/// The local policy of a node for automations (<c>doc/fleet/access-control.md</c>, "What the node decides"), kept by its
/// owner in <c>fleet/automation-policy.json</c> of the state root and read again before each run starts, so that a change
/// applies at once. A missing file is the default policy; a file that cannot be read or does not hold a policy runs
/// nothing, so that a policy that turned automations off never lets them run by breaking.
/// </summary>
internal sealed record NodeAutomationPolicy
{
    /// <summary>The name of the file, in the fleet folder of the state root.</summary>
    public const string FileName = "automation-policy.json";

    /// <summary>Credentials of any kind: personal ones (the owner's own plans and logins) and account ones.</summary>
    public const string AnyCredentials = "any";

    /// <summary>Account credentials only (API keys, gateway keys, local models): never a person's plan.</summary>
    public const string AccountCredentials = "account";

    /// <summary>Gets whether the node runs automations at all. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets the credentials its runs may use: <see cref="AnyCredentials"/> (the default, the owner's own machine) or <see cref="AccountCredentials"/>.</summary>
    public string Credentials { get; init; } = AnyCredentials;

    /// <summary>Gets why the file of the policy was not read (it runs nothing then); null for a policy read, or the default of a missing file.</summary>
    [JsonIgnore]
    public string? Problem { get; init; }

    /// <summary>Reads the policy of a fleet folder: the default when the file is missing, a policy that runs nothing when it cannot be read.</summary>
    internal static NodeAutomationPolicy Load(string fleetRoot)
    {
        var path = Path.Combine(fleetRoot, FileName);
        try
        {
            // Opened at once: a file that is there but cannot be told apart from none (a folder that cannot be read) is not
            // missing, nor is one being replaced.
            using var stream = StagedFileReplacement.OpenReplacedFile(path);
            if (stream is null) return new();
            var policy = JsonSerializer.Deserialize(stream, NodeAutomationJsonContext.Default.NodeAutomationPolicy);
            if (policy is null) return Closed($"{path} holds no policy");
            if (policy.Credentials is not (AnyCredentials or AccountCredentials)) return Closed($"{path} names credentials '{policy.Credentials}', neither '{AnyCredentials}' nor '{AccountCredentials}'");
            return policy;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Missing: the default policy.
            return new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // What the owner set cannot be told: it may have turned automations off, so nothing runs until it reads again.
            return Closed($"{path} cannot be read: {exception.Message}");
        }

        static NodeAutomationPolicy Closed(string problem) => new() { Enabled = false, Credentials = AccountCredentials, Problem = problem };
    }

    /// <summary>Writes the policy of a fleet folder, replacing the file at once.</summary>
    internal static void Save(string fleetRoot, NodeAutomationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Directory.CreateDirectory(fleetRoot);
        var path = Path.Combine(fleetRoot, FileName);
        // A missing policy is the default one: it is never missing while it is replaced.
        SafeFile.WriteAllBytesWithoutGap(path, JsonSerializer.SerializeToUtf8Bytes(policy, NodeAutomationJsonContext.Default.NodeAutomationPolicy));
    }
}

/// <summary>Who assigned an automation to the node, and what they assigned.</summary>
/// <param name="DeviceId">The device that assigned it.</param>
/// <param name="DeviceName">Its name at that time.</param>
/// <param name="Fingerprint">The fingerprint of the definition it assigned: what the node runs, and nothing else.</param>
/// <param name="AssignedAt">When it was assigned.</param>
internal sealed record NodeAutomationAssignment(string DeviceId, string DeviceName, string Fingerprint, DateTimeOffset AssignedAt)
{
    /// <summary>Gets the number of the version held; a version that is not newer is refused, after a removal too.</summary>
    public long Number { get; init; }

    /// <summary>Gets the version as its device signed it; null for an assignment made before versions were signed, which does not run.</summary>
    public FleetSignedAutomationVersion? Signed { get; init; }

    /// <summary>Gets the server link it came through; null for a paired device on a direct channel.</summary>
    public string? Via { get; init; }

    /// <summary>Gets whether the automation was taken away: nothing runs, and the number stays so that an older version stays refused.</summary>
    public bool Removed { get; init; }
}

/// <summary>What a node answers to a version of an automation a server delivers.</summary>
/// <param name="Applied">Whether the node holds that version now (or a newer one).</param>
/// <param name="Message">Why it refused it, or null.</param>
internal sealed record NodeAutomationOutcome(bool Applied, string? Message);

/// <summary>What <see cref="NodeAutomations"/> starts with.</summary>
internal sealed record NodeAutomationOptions
{
    /// <summary>Gets the clock of the schedules, the runs and the claims.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets the time zone the schedules are read in.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Gets what tells the credential kind of a provider; null for <see cref="AutomationCredentials.Classify"/> on the configuration of the profile.</summary>
    public Func<ModelProviderDescriptor, AutomationCredentialKind>? CredentialKind { get; init; }

    /// <summary>Gets who claims the occurrences of the schedules: the id of this node.</summary>
    public string Holder { get; init; } = "node:" + Environment.MachineName;

    /// <summary>Gets whether the repositories of the projects are watched for the event triggers (issues, pull requests). Default true.</summary>
    public bool WatchRepositories { get; init; } = true;

    /// <summary>Gets what the triggers of other tools use (Jira, Slack, hooks); null for the clients and the secrets of the profile.</summary>
    public AutomationIntegrationOptions? Integrations { get; init; }
}

/// <summary>
/// The automations of a node (<c>doc/fleet/node.md</c>, "Automations and statistics"): the engine of
/// <c>CodeAlta.Automations</c> over the runtime of the node, which fires the schedules and the triggers of the
/// automations of the profile and of those the paired devices assigned to it, kept in <c>fleet/assigned-automations.toml</c>
/// so that they fire while those devices and any server are away. Each due time is claimed once
/// (<c>fleet/automation-claims.json</c>); before a run starts, the node checks its own policy, the credential kind of the
/// provider the run would use, and, for an assigned automation, that the device that assigned it may still do so and
/// that the definition is the one it assigned.
/// </summary>
internal sealed partial class NodeAutomations : IFleetNodeAutomations, IAutomationRunGate, IAsyncDisposable
{
    /// <summary>The file of the automations the devices assigned, in the fleet folder of the state root.</summary>
    public const string AssignedFileName = "assigned-automations.toml";

    /// <summary>The file of who assigned them.</summary>
    public const string AssignmentsFileName = "assigned-automations.json";

    /// <summary>The file of the claimed occurrences.</summary>
    public const string ClaimsFileName = "automation-claims.json";

    private const int MaximumRuns = 200;

    private readonly CodeAltaHost _host;
    private readonly AutomationRunner _runner;
    private readonly NodeAutomationOptions _options;
    private readonly string _fleetRoot;
    // The worktree each run works in, as each session started on the node does.
    private readonly FleetSessionWorktrees _worktrees;
    private readonly Lock _gate = new();
    // One version applied at a time, from the check of the number held to the write: two at once could both pass it.
    private readonly SemaphoreSlim _applies = new(1, 1);
    private readonly Dictionary<string, NodeAutomationAssignment> _assignments;
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Node.Automations");

    // The runs by hand whose caller waits to hear that a budget holds them, by run.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _holdWaiters = new(StringComparer.Ordinal);

    /// <summary>What a run that a budget holds says it waits for.</summary>
    internal const string HeldMessage = "A budget of the machine is reached: the run waits for Continue anyway.";
    private int _disposed;

    /// <summary>Creates the automations of a node; nothing fires before <see cref="Start"/>.</summary>
    /// <param name="host">The runtime of the node.</param>
    /// <param name="options">The clock and what tests replace.</param>
    internal NodeAutomations(CodeAltaHost host, NodeAutomationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _options = options ?? new();
        var catalog = host.CatalogOptions;
        _fleetRoot = catalog.FleetRoot;
        _worktrees = new FleetSessionWorktrees(catalog);
        _assignments = LoadAssignments(Path.Combine(_fleetRoot, AssignmentsFileName));
        Engine = new AutomationService(catalog.ConfigPath, token => host.ProjectCatalog.LoadAsync(token),
            new AutomationStateStore(Path.Combine(catalog.StateRoot, "automations.json"), pausedByDefault: false),
            _runner = new AutomationRunner(host, this), _options.TimeProvider, _options.TimeZone,
            _options.WatchRepositories ? new GitAutomationFeed() : null,
            () => [.. (host.PluginRuntime?.ActivePlugins ?? []).Select(static plugin => plugin.Instance).OfType<CodeAlta.Plugins.Abstractions.IIssueEventSource>()],
            // A command runs with the identity of the space of its project, as in CodeAlta Desktop.
            new ShellAutomationCommands(projectId => host.Identities.ResolveAsync(null, projectId).GetAwaiter().GetResult().Environment),
            entry => entry.Definition.Script is { } script ? host.CodeModeScripts.FindHash(script, entry.ProjectPath) : null,
            new NodeAutomationClaims(new AutomationClaimStore(Path.Combine(_fleetRoot, ClaimsFileName), _options.Holder), this),
            Path.Combine(_fleetRoot, AssignedFileName))
        {
            // Jira, Slack and the hooks of its servers, with the secrets of this machine: the node watches them while nobody is at the keyboard.
            Integrations = _options.Integrations ?? AutomationIntegrationOptions.ForHost(catalog.GlobalRoot),
        };
        // The triggers of CodeAlta, and those its plugins give, as they run now.
        Engine.AddProjectTrigger(host.ProjectCatalog);
        Engine.Sources.Plugins = () => host.PluginRuntime?.GetAutomationTriggers() ?? [];
        // The credential rules of a run hold at every attachment of its sessions and of those they start.
        host.RuntimeService.AddProviderGate(CheckSessionProviderAsync);
        // So do the tools the policy turns off, which the runtime holds in memory only: also once the node started again.
        host.RuntimeService.ToolPatternGate = PolicyToolsOffAsync;
    }

    /// <summary>Runs the scripts of automations without a model with the <c>alta</c> commands of the node and the plugins that run.</summary>
    /// <param name="dispatcher">The <c>alta</c> commands of the node.</param>
    internal void RunScriptsWith(CodeAlta.LiveTool.AltaCommandDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var plugins = new CodeAlta.LiveTool.RuntimeAltaPluginCatalog(_host.PluginRuntime);
        var runner = new CodeAlta.LiveTool.Scripts.UnattendedScriptRunner(dispatcher, _host.CodeModeScripts, _host.RuntimeService,
            () => [.. plugins.ListCommandContributions().Select(static contribution => contribution.Command.Path)]);
        _runner.Unattended = async (run, cancellationToken) =>
        {
            var result = await runner.RunAsync(new CodeAlta.LiveTool.Scripts.UnattendedScriptRequest(run.RunId, run.Script)
            {
                ArgumentsJson = run.ArgumentsJson, EventText = run.EventText, EventJson = run.EventJson, ProjectId = run.ProjectId, ProjectPath = run.ProjectPath,
                Spaces = run.Spaces, Sandbox = run.Sandbox, Write = run.Write, Environment = run.Environment,
            }, cancellationToken).ConfigureAwait(false);
            return new AutomationScriptOutcome(result.Succeeded, result.Value, result.Error, result.Presented);
        };
    }

    /// <summary>
    /// Gets or sets what gives the tools the signed policy turns off for a session of the work of the fleet that no automation
    /// started (one started through the node API, and those it starts); null for none.
    /// </summary>
    internal Func<SessionViewDescriptor, CancellationToken, ValueTask<IReadOnlyList<string>?>>? WorkToolsOff { get; set; }

    /// <summary>
    /// Gives the tools the signed policy turns off for a session of this node, at every attachment of it
    /// (<see cref="CodeAlta.Orchestration.Runtime.SessionRuntimeService.ToolPatternGate"/>): for a session of a run of an
    /// automation, and every session it started, those of the policy that applies to that automation, as when its run started;
    /// for the other work of the fleet, those of the policy of its space.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Cancels the reads of its lineage.</param>
    /// <returns>The patterns; null for none.</returns>
    internal async ValueTask<IReadOnlyList<string>?> PolicyToolsOffAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var origin = await _host.RuntimeService.FindLineageCreatorAsync(session,
            static createdBy => createdBy.Kind == AltaActorProvenance.AutomationKind && !string.IsNullOrEmpty(createdBy.AutomationId), cancellationToken).ConfigureAwait(false);
        if (origin is not null)
        {
            var spaceId = session.SpaceId ?? SpaceDescriptor.DefaultId;
            // An automation removed since: the policy of the space of its session.
            var rules = Engine.Snapshot.Find(origin.AutomationId!) is { } entry ? RulesOf(entry, spaceId) : Policy?.RulesFor(spaceId);
            return rules?.ToolsOff is { Count: > 0 } off ? off : null;
        }

        return WorkToolsOff is { } work ? await work(session, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>Gets the engine.</summary>
    internal AutomationService Engine { get; }

    /// <summary>
    /// Gets or sets what finds a paired device by its id, as the node trusts it now; null until the fleet part of the node
    /// starts, and then an assigned automation does not run.
    /// </summary>
    internal Func<string, TrustedDevice?>? Devices { get; set; }

    /// <summary>Gets or sets the device id of this node, which the versions it runs must name; null until the fleet part of the node starts.</summary>
    internal string? DeviceId { get; set; }

    /// <summary>
    /// Gets or sets what gives the device list of a server link as this node last verified it, to refuse what a device
    /// removed from it signed; null, or a null list (not read since the node started), keeps the check made when the
    /// version was accepted.
    /// </summary>
    internal Func<string, FleetDeviceListState?>? ServerDevices { get; set; }

    /// <summary>
    /// Gets or sets what gives the signed access document of a server link as this node last verified it: an automation
    /// whose signer the organization suspended waits for its new owner, and a team machine runs what its team allows; null,
    /// or a null document, keeps the check made when the version was accepted.
    /// </summary>
    internal Func<string, FleetAccessState?>? ServerAccess { get; set; }

    /// <summary>
    /// Gets or sets what gives the signed access documents of all the server links of the node as it last verified them: what
    /// says whether this machine is a team's or the organization's, whatever gave an automation; null names none.
    /// </summary>
    internal Func<IEnumerable<FleetAccessState?>>? ServerAccesses { get; set; }

    /// <summary>Starts reading the automations and firing their triggers.</summary>
    internal void Start()
    {
        Engine.Changed += PokeReports;
        Engine.Start();
    }

    /// <summary>Gets who assigned an automation; null for one the node was not assigned, or that was taken away.</summary>
    internal NodeAutomationAssignment? AssignmentOf(string id)
    {
        lock (_gate) return _assignments.GetValueOrDefault(id) is { Removed: false } assignment ? assignment : null;
    }

    /// <summary>Gets the number of the version of an automation this node holds or last removed; 0 for none.</summary>
    internal long NumberOf(string id)
    {
        lock (_gate) return _assignments.GetValueOrDefault(id)?.Number ?? 0;
    }

    /// <summary>Gets when the local policy of the node last refused a run; null when it refused none since the node started.</summary>
    internal DateTimeOffset? LastPolicyRefusal { get; private set; }

    /// <inheritdoc />
    public string? Check(AutomationEntry entry, ModelProviderDescriptor provider)
    {
        var refused = CheckPolicy(entry, provider);
        // A policy that refuses runs is a problem of the machine its owner hears of (its health).
        if (refused is not null) LastPolicyRefusal = DateTimeOffset.UtcNow;
        return refused;
    }

    private string? CheckPolicy(AutomationEntry entry, ModelProviderDescriptor provider)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(provider);
        var policy = NodeAutomationPolicy.Load(_fleetRoot);
        if (policy.Problem is { } problem)
            return $"This machine runs no automations while its policy cannot be read ({problem}): its owner sets it again with altanode automations policy.";
        if (!policy.Enabled) return "This machine runs no automations: its owner turned them off.";
        // The kill switch of an organization stops every run of this machine until a signed version lifts it.
        if (Policy?.Halt is { } halt) return halt.Describe("this machine");
        if (entry.Source.IsAssigned && CheckAssignment(entry) is { } refused) return refused;

        // Who a run acts as (doc/fleet/orchestration.md): an automation of a team runs on a machine of that team, as one of the
        // service identities the signed access document says the team turned on; a machine of a team runs no personal plan.
        var (team, scope) = TeamsOf(entry);
        if (scope is not null && team?.Id != scope) return "This automation of a team runs on a machine of that team only.";
        if (scope is not null && team!.Services is not { Count: > 0 })
            return "The automations of a team run as one of its service identities: a maintainer turns one on in People and access, and a signer signs it for machines.";
        var (organizationMachine, organizationScope, organizationServices) = OrganizationOf(entry);
        if (organizationScope && !organizationMachine) return "This automation of the organization runs on a machine of the organization only.";
        if (organizationScope && organizationServices.Count == 0)
            return "The automations of the organization run as one of its service identities: an admin turns one on in People and access, and a signer signs it for machines.";

        return CredentialRefusal(entry, provider, null, policy);
    }

    // Why a run of an automation may not use a provider by the credential it runs with: one that cannot be told, or a personal plan
    // where account credentials are required (a machine of a team or of the organization, this machine's policy, and, with a space,
    // the signed policy that applies there). An automation no longer known here keeps to account credentials.
    private string? CredentialRefusal(AutomationEntry? entry, ModelProviderDescriptor provider, string? spaceId, NodeAutomationPolicy? policy = null)
    {
        var kind = CredentialKindOf(provider);
        if (kind == AutomationCredentialKind.Unknown)
            return $"The credential the provider '{provider.ProviderId.Value}' runs with cannot be told: an unattended run uses a provider whose credential is known.";
        if (kind != AutomationCredentialKind.Personal) return null;
        if (entry is null)
            return $"The provider '{provider.ProviderId.Value}' runs with a personal plan, and the automation this session runs for is no longer on this machine: it keeps to account credentials.";
        if (TeamsOf(entry).Machine is not null || OrganizationOf(entry).Machine || IsSharedMachine())
            return $"The provider '{provider.ProviderId.Value}' runs with a personal plan, and a machine of a team or of the organization runs automations with account credentials only.";
        policy ??= NodeAutomationPolicy.Load(_fleetRoot);
        if (policy.Problem is not null || policy.Credentials != NodeAutomationPolicy.AnyCredentials)
            return $"The provider '{provider.ProviderId.Value}' runs with a personal plan, and this machine runs automations with account credentials only.";
        if (spaceId is not null && RulesOf(entry, spaceId)?.AccountCredentials == true)
            return $"The provider '{provider.ProviderId.Value}' runs with a personal plan, and the policy of the organization runs every automation with account credentials.";
        return null;
    }

    /// <summary>
    /// Checks the provider a session of this node attaches with (<see cref="CodeAlta.Orchestration.Runtime.SessionRuntimeService.ProviderGate"/>):
    /// a session of a run of an automation, and every session it started, keeps the credential rules of that automation for the
    /// whole run, whatever changes its provider (a person, a model tier, a fallback) and whatever starts a child.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="providerKey">The provider it would run with.</param>
    /// <param name="cancellationToken">Cancels the reads of its lineage.</param>
    /// <returns>Why it may not; null when it may, or when no automation started it.</returns>
    internal async ValueTask<string?> CheckSessionProviderAsync(SessionViewDescriptor session, string providerKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        AltaActorProvenance? origin;
        bool unknown;
        try
        {
            origin = await _host.RuntimeService.FindLineageCreatorAsync(session,
                static createdBy => createdBy.Kind == AltaActorProvenance.AutomationKind && !string.IsNullOrEmpty(createdBy.AutomationId), cancellationToken).ConfigureAwait(false);
            unknown = false;
        }
        catch (SessionLineageException)
        {
            // Whether an automation started it cannot be told: it keeps to account credentials, as one whose automation left.
            (origin, unknown) = (null, true);
        }

        if (origin is null && !unknown) return null;
        if (!_host.ModelProviderRegistry.TryGetProvider(new ModelProviderId(providerKey), out var provider))
            return $"The provider '{providerKey}' is not one of this machine: a session of an automation runs with a provider whose credential is known.";
        return CredentialRefusal(origin is null ? null : Engine.Snapshot.Find(origin.AutomationId!), provider, session.SpaceId ?? SpaceDescriptor.DefaultId);
    }

    /// <inheritdoc />
    public string? CheckModel(AutomationEntry entry, ModelProviderDescriptor provider, string modelId, string spaceId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(provider);
        // The signed policy of an organization: the one of the server an automation came through, else the one of the server
        // the space of the run is linked to, for this machine's own automations too.
        if (RulesOf(entry, spaceId) is not { } rules) return null;
        if (FleetPolicyGuard.Refusal(rules, provider.ProviderType, provider.ProviderId.Value, modelId) is { } refusal) return refusal;
        if (rules.AccountCredentials == true && CredentialKindOf(provider) == AutomationCredentialKind.Personal)
            return $"The provider '{provider.ProviderId.Value}' runs with a personal plan, and the policy of the organization runs every automation with account credentials.";
        return null;
    }

    /// <inheritdoc />
    public AutomationRunLimits LimitsFor(AutomationEntry entry, ModelProviderDescriptor provider, string spaceId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(provider);
        // The limits a session of the fleet keeps to (doc/fleet/security-and-privacy.md, policies and the kill switch): those of
        // the policy of the server the automation came through, else of the server the space of the run is linked to.
        if (RulesOf(entry, spaceId) is not { } rules) return AutomationRunLimits.None;
        var definition = entry.Definition;
        if (rules.ToolsOff is { Count: > 0 } off && off.Select(AgentToolPatterns.Validate).FirstOrDefault(static error => error is not null) is { } invalid)
            return AutomationRunLimits.Refused($"The tools the policy of the organization turns off do not read: {invalid}");

        var mode = definition.PermissionMode;
        if (rules.PermissionMode is { } ceiling)
        {
            var rank = SessionPermissionModes.Rank(ceiling);
            if (mode is not null && SessionPermissionModes.Rank(mode) > rank)
                return AutomationRunLimits.Refused($"This automation runs in the permission mode '{mode}', and the policy of the organization allows '{ceiling}' at most.");
            if (mode is null)
            {
                // The widest mode the provider offers within the ceiling, when its default is above it.
                var choices = _host.Commands.GetNewSessionPermissionModes(provider.ProviderId.Value);
                if (SessionPermissionModes.Rank(choices.DefaultMode) > rank)
                {
                    mode = choices.Modes.Where(candidate => SessionPermissionModes.Rank(candidate) <= rank).OrderByDescending(SessionPermissionModes.Rank).FirstOrDefault();
                    if (mode is null)
                        return AutomationRunLimits.Refused($"The provider '{provider.ProviderId.Value}' offers no permission mode as narrow as '{ceiling}', which the policy of the organization requires.");
                }
            }
        }

        var sandbox = definition.Sandbox ?? rules.Sandbox;
        if (FleetPolicyGuard.SandboxProblem(sandbox, rules, _host.RuntimeService.ReadSandboxSettings().Profiles) is { } problem)
            return AutomationRunLimits.Refused($"This automation cannot run here: {problem}.");
        return new AutomationRunLimits(null, mode, sandbox, rules.ToolsOff is { Count: > 0 } tools ? tools : null);
    }

    /// <inheritdoc />
    public async Task<AutomationRunFolder> PrepareFolderAsync(ProjectDescriptor? project, CancellationToken cancellationToken)
    {
        var worktree = await _worktrees.PrepareAsync(project, cancellationToken).ConfigureAwait(false);
        return new AutomationRunFolder(worktree.Folder, worktree.Root, worktree.Problem);
    }

    /// <inheritdoc />
    public Task ReleaseFolderAsync(ProjectDescriptor? project, AutomationRunFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return _worktrees.RemoveUnusedAsync(project, new FleetSessionWorktree(folder.Worktree, folder.Root, folder.Problem));
    }

    // The rules that apply to a run of an automation in a space; null when no signed policy says anything there.
    private FleetPolicyRules? RulesOf(AutomationEntry entry, string? spaceId)
        => AssignmentOf(entry.Id)?.Via is { } via
            ? ServerPolicy?.Invoke(via) is { Number: > 0 } signed ? signed.Rules : null
            : Policy?.RulesFor(spaceId);

    /// <summary>
    /// Applies the signed policies to the runs under way: while a stop reaches this machine every run is stopped (also one that
    /// started before the node read the stop, as at its start); otherwise a run that ran longer than the policy of its space
    /// allows. The node calls it on a timer, so that the policy it holds reaches runs whatever started them and whenever.
    /// </summary>
    /// <param name="cancellationToken">Cancels before the next run is stopped.</param>
    /// <returns>The sessions stopped.</returns>
    internal async Task<IReadOnlyList<string>> EnforcePolicyAsync(CancellationToken cancellationToken)
    {
        if (Policy?.Halt is { } halt)
        {
            var halted = await StopRunsAsync(halt.Describe("this machine"), cancellationToken).ConfigureAwait(false);
            if (halted.Count > 0 && LogManager.IsInitialized) _logger.Info($"The kill switch of {halt.Server} stopped runs under way: {string.Join(", ", halted)}.");
            return halted;
        }

        if (Policy is null && ServerPolicy is null) return [];
        var now = _options.TimeProvider.GetUtcNow();
        var stopped = new List<string>();
        IReadOnlyList<SpaceDescriptor>? spaces = null;
        foreach (var run in Engine.Runs(null, 500))
        {
            if (run.Status is not (AutomationRun.Running or AutomationRun.Waiting or AutomationRun.Held or AutomationRun.Queued) || run.SessionId is not { } sessionId) continue;
            if (Engine.Snapshot.Find(run.AutomationId) is not { } entry) continue;
            // The space of the run, as its runner chose it: the one of its project, the default one otherwise.
            spaces ??= await _host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            var project = entry.ProjectId is { } projectId ? await _host.ProjectCatalog.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false) : null;
            var spaceId = SpaceCatalog.ResolveSessionSpace(null, project, spaces);
            if (RulesOf(entry, spaceId)?.MaxSessionMinutes is not { } minutes || now - run.StartedAt <= TimeSpan.FromMinutes(minutes)) continue;
            if (await StopAsync(sessionId, "The policy of the organization stops a run after the longest time of a session.", cancellationToken).ConfigureAwait(false))
                stopped.Add(sessionId);
        }

        if (stopped.Count > 0 && LogManager.IsInitialized) _logger.Info($"Runs past the longest time of their policy were stopped: {string.Join(", ", stopped)}.");
        return stopped;
    }

    private async Task<bool> StopAsync(string sessionId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _host.RuntimeService.AbortTreeAsync(sessionId, reason, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or IOException)
        {
            if (LogManager.IsInitialized) _logger.Warn($"The run of session {sessionId} could not be stopped by the policy: {exception.Message}");
            return false;
        }
    }

    /// <summary>Gets or sets the signed policies of the servers of the node: a stop refuses every run; null while it joined none.</summary>
    internal FleetPolicyGuard? Policy { get; set; }

    /// <summary>Gets or sets the policy of a server, by the id of its link, as the node verified it; null while it joined none.</summary>
    internal Func<string, FleetPolicyState?>? ServerPolicy { get; set; }

    /// <summary>Stops the runs under way: a stop of the kill switch reached this machine.</summary>
    /// <param name="reason">Why, recorded by each session stopped.</param>
    /// <param name="cancellationToken">Cancels before the next run is stopped.</param>
    /// <returns>The sessions stopped.</returns>
    internal async Task<IReadOnlyList<string>> StopRunsAsync(string reason, CancellationToken cancellationToken)
    {
        var stopped = new List<string>();
        foreach (var run in Engine.Runs(null, 500))
        {
            if (run.Status is not (AutomationRun.Running or AutomationRun.Waiting or AutomationRun.Held or AutomationRun.Queued) || run.SessionId is not { } sessionId) continue;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _host.RuntimeService.AbortTreeAsync(sessionId, reason, cancellationToken).ConfigureAwait(false);
                stopped.Add(sessionId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or IOException)
            {
                if (LogManager.IsInitialized) _logger.Warn($"The run of session {sessionId} could not be stopped by the kill switch: {exception.Message}");
            }
        }

        return stopped;
    }

    // Whether a signed access document of a server of this machine says it is a team's or the organization's: a shared machine,
    // which runs every automation with account credentials, whatever gave it (a server, a device paired directly, its owner).
    private bool IsSharedMachine()
        => DeviceId is { } self && ServerAccesses?.Invoke() is { } documents
            && documents.Any(access => access is not null && (access.TeamOf(self) is not null || access.IsOrganizationMachine(self)));

    // Whether this machine is the organization's, whether the automation is the organization's, and the service identities the
    // organization turned on, as the access document of the server it came through says.
    private (bool Machine, bool Automation, IReadOnlyList<string> Services) OrganizationOf(AutomationEntry entry)
    {
        if (!entry.Source.IsAssigned || AssignmentOf(entry.Id) is not { Via: { } link } assignment || DeviceId is not { } self) return (false, false, []);
        var access = ServerAccess?.Invoke(link);
        var scoped = assignment.Signed is { } signed && FleetAutomationVersionDocuments.TryParse(signed.Payload, out var version) && version.IsOrganizationScope;
        return (access?.IsOrganizationMachine(self) == true, scoped, access?.OrganizationServices ?? []);
    }

    // The team of this machine, as the access document of the server an automation came through says, and the team the
    // automation is of (its signed scope); both null for an automation of this machine's own, or one of a personal machine.
    private (FleetAccessTeam? Machine, string? Automation) TeamsOf(AutomationEntry entry)
    {
        if (!entry.Source.IsAssigned || AssignmentOf(entry.Id) is not { Via: { } link } assignment || DeviceId is not { } self) return (null, null);
        var team = ServerAccess?.Invoke(link)?.TeamOf(self);
        var scope = assignment.Signed is { } signed && FleetAutomationVersionDocuments.TryParse(signed.Payload, out var version) ? version.TeamId : null;
        return (team, scope);
    }

    /// <summary>Gets or sets what admits a run to an environment of the fleet; null until the node joined a server, and then no run uses one.</summary>
    internal NodeEnvironmentGate? Environments { get; set; }

    /// <summary>Gets or sets what admits a run to the budgets of the machine; null until the node joined a server, and then no budget limits it.</summary>
    internal NodeBudgets? Budgets { get; set; }

    /// <summary>
    /// Gets or sets what admits a run to budgets in place of <see cref="Budgets"/>: a test holds runs with it; null for the
    /// budgets of the machine.
    /// </summary>
    internal Func<Fleet.NodeApi.FleetNodeBudgetRun, Action?, CancellationToken, Task<Fleet.Server.FleetBudgetAdmission>>? AdmitBudget { get; set; }

    /// <inheritdoc />
    public async Task<string?> AdmitBudgetAsync(AutomationEntry entry, string runId, ProjectDescriptor? project, string spaceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var admit = AdmitBudget ?? (Budgets is { } budgets ? budgets.AdmitAsync : null);
        if (admit is null) return null;
        // A run that waits for its owner says so: in its runs, and to a caller of Run now that waits for its answer.
        var run = new Fleet.NodeApi.FleetNodeBudgetRun(runId, entry.Definition.Name, spaceId, project);
        return (await admit(run, () => Held(runId), cancellationToken).ConfigureAwait(false)).Problem;
    }

    /// <summary>Gets or sets the seats of the machine its runs take (<see cref="Fleet.NodeApi.FleetSessionSeats"/>); null for no limit.</summary>
    internal Fleet.NodeApi.FleetSessionSeats? Seats { get; set; }

    internal const string QueuedMessage = "As many sessions work on the machine as its owner lets work at once: the run waits for its turn.";

    /// <inheritdoc />
    public async Task<string?> AdmitSeatAsync(AutomationEntry entry, string runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Seats is not { } seats) return null;
        // The run takes the seat under its own id until its session exists and holds it (BindSeat).
        var ticket = await seats.RequestAsync(new Fleet.NodeApi.FleetSeatRequest(runId, runId, null, entry.Definition.Name, "automation"), cancellationToken).ConfigureAwait(false);
        if (ticket.IsAdmitted) return null;
        // A run that waits for its turn says so: in its runs, and to a caller of Run now that waits for its answer.
        Engine.MarkQueued(runId, QueuedMessage);
        if (_holdWaiters.TryGetValue(runId, out var waiter)) waiter.TrySetResult();
        using var registration = cancellationToken.Register(() => seats.Cancel(runId));
        try
        {
            await ticket.Admitted.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return "The run was taken out of the queue of the machine.";
        }

        return null;
    }

    /// <inheritdoc />
    public void BindSeat(string runId, string sessionId) => Seats?.Bind(runId, sessionId);

    /// <inheritdoc />
    public void ReleaseSeat(string runId) => Seats?.Release(runId);

    private void Held(string runId)
    {
        Engine.MarkHeld(runId, HeldMessage);
        if (_holdWaiters.TryGetValue(runId, out var waiter)) waiter.TrySetResult();
    }

    /// <inheritdoc />
    public Task<CodeAlta.Orchestration.Runtime.EnvironmentAdmissionResult> AdmitEnvironmentAsync(AutomationEntry entry, string runId, string environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Environments is { } environments
            ? environments.AdmitAsync(environment, runId, entry.Definition.Id, cancellationToken)
            : Task.FromResult(CodeAlta.Orchestration.Runtime.EnvironmentAdmissionResult.Refused($"This machine runs no automation in the environment '{environment}': it joined no server."));
    }

    /// <inheritdoc />
    public async Task<FleetAutomationInfo> PutAsync(FleetAutomationCaller caller, FleetAutomationPutParams parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Signed is not { } signed)
            throw Invalid("This machine runs signed automations only: update CodeAlta on the computer that gives it this one.");
        if (DeviceId is not { } self) throw Invalid("This machine does not know its own device yet: try again in a moment.");
        if (!FleetAutomationVersionDocuments.TryVerify(signed, self, out var version, out var key, out var reason))
            throw Invalid($"The signature of the automation does not hold: {reason}.");
        // The device that signs is the one on the channel, with the key it paired with.
        if (!string.Equals(key.DeviceId, caller.DeviceId, StringComparison.Ordinal) || !string.Equals(key.Fingerprint, caller.Fingerprint, StringComparison.Ordinal))
            throw Invalid("The automation is signed by another device than the one that gives it.");
        if (version.Removed || version.Definition is not { } text) throw Invalid("On a direct channel, an automation is given with its definition in clear.");
        var (outcome, saved, assignment) = await ApplyAsync(caller.DeviceId, caller.Name, signed, version, new FleetAutomationContent(text, version.Repository), null,
            cancellationToken).ConfigureAwait(false);
        if (!outcome.Applied || saved is null) throw Invalid(outcome.Message ?? "The automation was not taken.");
        return Describe(saved, assignment);
    }

    /// <summary>
    /// Takes a version of an automation a server delivered: its signature verified against the device list of that server
    /// (signed by a controller device of the person who owns this machine, for this machine and organization), newer than
    /// the one held, and what it does opened with the key of this machine; a removal takes the automation away.
    /// </summary>
    /// <param name="linkId">The server link it came through.</param>
    /// <param name="devices">The device list of that server, as this node verified it now.</param>
    /// <param name="organizationId">The organization of that server.</param>
    /// <param name="signed">The signed version.</param>
    /// <param name="open">Opens the sealed content of a version, or says why it does not open.</param>
    /// <param name="cancellationToken">Cancels before the automation is written.</param>
    /// <param name="nameOf">Reads the name the server gives the device that signed (shown, never trusted); null names it by its id.</param>
    /// <returns>Whether the node holds that version now, or why it refused it.</returns>
    internal async Task<NodeAutomationOutcome> AcceptFromServerAsync(string linkId, FleetDeviceListState devices, string? organizationId, FleetSignedAutomationVersion signed,
        Func<FleetAutomationVersion, (FleetAutomationContent? Content, string? Reason)> open, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<string?>>? nameOf = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(linkId);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(open);
        if (DeviceId is not { } self) return new(false, "This machine does not know its own device yet.");
        if (!FleetAutomationVersionDocuments.TryVerify(signed, devices, ServerAccess?.Invoke(linkId), organizationId, self, out var version, out var reason))
        {
            Log($"A version of an automation delivered by the server was refused: {reason}.");
            return new(false, $"The signature of the version does not hold: {reason}.");
        }

        var signer = devices.Members[version.SignerDeviceId];
        if (version.Removed)
        {
            var removed = await RemoveVersionAsync(version, cancellationToken).ConfigureAwait(false);
            if (removed.Applied) Log($"The automation {version.AutomationId} was taken away by {signer.DeviceId} (version {version.Number}).");
            return removed;
        }

        var (content, closed) = open(version);
        if (content is null) return new(false, $"The definition does not open on this machine: {closed}.");
        try
        {
            var name = nameOf is null ? null : await nameOf(signer.DeviceId, cancellationToken).ConfigureAwait(false);
            var (outcome, _, _) = await ApplyAsync(signer.DeviceId, name ?? signer.DeviceId, signed, version, content, linkId, cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        catch (FleetException exception)
        {
            return new(false, exception.Message);
        }
    }

    /// <summary>
    /// Takes away the automations a server gave this node, once the link to that server is gone (the node left it): they
    /// run no more, and their numbers stay so that a version replayed later is refused.
    /// </summary>
    /// <param name="linkId">The server link they came through.</param>
    /// <param name="cancellationToken">Cancels before the next automation is removed.</param>
    /// <returns>How many automations were taken away.</returns>
    internal async Task<int> ForgetServerAsync(string linkId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(linkId);
        string[] ids;
        lock (_gate) ids = [.. _assignments.Where(pair => !pair.Value.Removed && pair.Value.Via == linkId).Select(static pair => pair.Key)];
        foreach (var id in ids)
        {
            if (Engine.Snapshot.Find(id) is { Source.IsAssigned: true } && await Engine.DeleteAsync(id, cancellationToken).ConfigureAwait(false) is { } refused)
                Log($"The automation {id} given through a server that is gone could not be removed: {refused}");
            lock (_gate)
            {
                if (_assignments.TryGetValue(id, out var known) && !known.Removed) _assignments[id] = known with { Removed = true, Signed = null };
            }
        }

        if (ids.Length > 0)
        {
            lock (_gate) SaveAssignments();
            Log($"{ids.Length} automation(s) given through the server link {linkId} were taken away: the node left that server.");
        }

        return ids.Length;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(FleetAutomationCaller caller, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var removed = false;
        if (Engine.Snapshot.Find(id) is { Source.IsAssigned: true })
        {
            if (await Engine.DeleteAsync(id, cancellationToken).ConfigureAwait(false) is { } refused) throw Invalid(refused);
            removed = true;
        }

        // The number stays: a version a channel or a server replays later is older, and refused.
        lock (_gate)
        {
            if (_assignments.TryGetValue(id, out var known) && !known.Removed)
            {
                _assignments[id] = known with { Removed = true, Signed = null };
                SaveAssignments();
            }
        }

        if (removed) Log($"{caller.Name} ({caller.DeviceId}) removed the automation {id}.");
        return removed;
    }

    /// <inheritdoc />
    public IReadOnlyList<FleetAutomationInfo> List(FleetAutomationCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return [.. Engine.Snapshot.Entries.Where(static entry => entry.Source.IsAssigned).Select(entry => Describe(entry, AssignmentOf(entry.Id)))];
    }

    // Checks an assigned automation before a run: a version signed by the device that gave it, whose signature still
    // verifies, read as it was given, from a device that may still give it.
    private string? CheckAssignment(AutomationEntry entry)
    {
        if (AssignmentOf(entry.Id) is not { } assignment) return "No device of yours assigned this automation to this machine.";
        if (assignment.Signed is not { } signed) return "This automation was given to this machine before its versions were signed: give it to this machine again.";
        if (!string.Equals(assignment.Fingerprint, entry.Definition.Fingerprint(), StringComparison.Ordinal))
            return "This automation changed on this machine since it was assigned: it runs only as its device assigned it.";
        if (!FleetAutomationVersionDocuments.TryVerify(signed, DeviceId, out var version, out var key, out var reason) || version.Number != assignment.Number)
            return $"The signature of this automation no longer holds on this machine ({reason ?? "another version"}): give it to this machine again.";
        if (assignment.Via is { } link)
        {
            // Through a server: the signer stays a controller device of the organization, as this node last verified its list.
            if (ServerDevices?.Invoke(link) is { } devices
                && (!devices.Members.TryGetValue(key.DeviceId, out var member) || member.Kind != FleetDeviceKinds.Controller || devices.Removed.Contains(key.DeviceId)
                    || !string.Equals(member.Key.Text, key.Text, StringComparison.Ordinal)))
                return "The device that gave this automation is no longer a controller device of the organization.";
            // The signed access document as this node verified it: a suspended signer, a team machine that no longer runs it.
            if (ServerDevices?.Invoke(link) is { } listed && ServerAccess?.Invoke(link) is { } access && DeviceId is { } machine
                && listed.Members.TryGetValue(key.DeviceId, out var signer)
                && FleetAutomationVersionDocuments.CheckAccess(signer.OwnerId, machine, listed, access, entry.Id) is { } refused)
                return $"This automation waits for its owner: {refused}.";
            return null;
        }

        if (Devices?.Invoke(assignment.DeviceId) is not { } device)
            return $"The device that assigned this automation ({assignment.DeviceName}) is no longer paired with this machine.";
        if (!string.Equals(device.Fingerprint.ToString(), key.Fingerprint, StringComparison.Ordinal))
            return $"The device that assigned this automation ({device.Name}) is paired now with another key.";
        if (!device.Permissions.HasFlag(DevicePermissions.Automate))
            return $"The device that assigned this automation ({device.Name}) may no longer give automations to this machine.";
        return null;
    }

    // Writes a verified version as the one this machine runs. The same version given again changes nothing; an older one is refused.
    private async Task<(NodeAutomationOutcome Outcome, AutomationEntry? Saved, NodeAutomationAssignment? Assignment)> ApplyAsync(string deviceId, string deviceName,
        FleetSignedAutomationVersion signed, FleetAutomationVersion version, FleetAutomationContent content, string? via, CancellationToken cancellationToken)
    {
        await _applies.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NodeAutomationAssignment? held;
            lock (_gate) held = _assignments.GetValueOrDefault(version.AutomationId);
            if (held is { Removed: false, Signed: { } same } && version.Number == held.Number && same.Payload == signed.Payload
                && Engine.Snapshot.Find(version.AutomationId) is { Source.IsAssigned: true } current)
                return (new(true, null), current, held);
            if (held is not null && version.Number <= held.Number)
                return (new(false, $"This machine holds version {held.Number} of this automation: a version is never replaced by one that is not newer."), null, null);

            var text = content.Definition;
            if (text.Length is 0 or > AutomationConfig.MaximumContentLength) throw Invalid("The automation is one table of a configuration file.");
            var (definitions, faults) = AutomationConfig.Read(text, new AutomationSource("automations.put", null, null) { IsAssigned = true });
            if (faults.Count > 0) throw Invalid(faults[0].Message);
            if (definitions.Count != 1) throw Invalid("The automation is one table of a configuration file.");
            var definition = definitions[0];
            if (!string.Equals(definition.Id, version.AutomationId, StringComparison.Ordinal)) throw Invalid("The signed version names another automation than its definition.");
            if (definition.Enabled != version.Enabled) throw Invalid("The signed version says another state than its definition.");
            // A command of a trigger runs on this machine without anybody to approve it: only its owner writes one here.
            if (definition.Triggers.Any(static trigger => trigger.IsCommand))
                throw Invalid("A command trigger runs a command on this machine: it is not taken from another device.");
            string? project = null;
            if (content.Repository is { } repository)
            {
                project = await FindProjectAsync(repository, cancellationToken).ConfigureAwait(false)
                    ?? throw new FleetException(FleetNodeErrorCodes.ProjectNotFound, null,
                        $"This machine has no project of the repository {repository}: clone it there first (altanode projects clone).");
            }

            if (Engine.Snapshot.Find(definition.Id) is { Source.IsAssigned: false })
                throw Invalid("An automation of this machine has the identifier of this one.");
            if (await Engine.SaveAssignedAsync(definition with { Project = project }, cancellationToken).ConfigureAwait(false) is { } refused) throw Invalid(refused);
            if (Engine.Snapshot.Find(definition.Id) is not { Source.IsAssigned: true } saved) throw Invalid("The automation was not read back on this machine.");
            var assignment = new NodeAutomationAssignment(deviceId, deviceName, saved.Definition.Fingerprint(), _options.TimeProvider.GetUtcNow())
            {
                Number = version.Number, Signed = signed, Via = via,
            };
            lock (_gate)
            {
                _assignments[saved.Id] = assignment;
                SaveAssignments();
            }

            Log($"{(deviceName == deviceId ? deviceId : $"{deviceName} ({deviceId})")} assigned the automation '{saved.Definition.Name}' ({saved.Id}), version {version.Number}" + (via is null ? "." : " through a server."));
            return (new(true, null), saved, assignment);
        }
        finally
        {
            _applies.Release();
        }
    }

    // Takes an automation away by a signed removal newer than what the node holds.
    private async Task<NodeAutomationOutcome> RemoveVersionAsync(FleetAutomationVersion version, CancellationToken cancellationToken)
    {
        await _applies.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NodeAutomationAssignment? held;
            lock (_gate) held = _assignments.GetValueOrDefault(version.AutomationId);
            if (held is { Removed: true } && held.Number >= version.Number) return new(true, null);
            if (held is not null && version.Number <= held.Number)
                return new(false, $"This machine holds version {held.Number} of this automation: a removal older than it changes nothing.");
            if (Engine.Snapshot.Find(version.AutomationId) is { Source.IsAssigned: true }
                && await Engine.DeleteAsync(version.AutomationId, cancellationToken).ConfigureAwait(false) is { } refused)
                return new(false, refused);
            lock (_gate)
            {
                _assignments[version.AutomationId] = (held ?? new NodeAutomationAssignment(version.SignerDeviceId, version.SignerDeviceId, string.Empty, _options.TimeProvider.GetUtcNow()))
                    with { Number = version.Number, Removed = true, Signed = null };
                SaveAssignments();
            }

            return new(true, null);
        }
        finally
        {
            _applies.Release();
        }
    }

    /// <inheritdoc />
    public Task<FleetAutomationRunInfo> RunAsync(FleetAutomationCaller caller, string id, CancellationToken cancellationToken) => RunAsync(caller, id, false, cancellationToken);

    /// <inheritdoc />
    public async Task<FleetAutomationRunInfo> RunAsync(FleetAutomationCaller caller, string id, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (Engine.Snapshot.Find(id) is not { Source.IsAssigned: true })
            throw new FleetException(FleetNodeErrorCodes.AutomationNotFound, null, "No automation of yours on this machine has that id.");
        var run = await RunByHandAsync(id, Guid.CreateVersion7().ToString("N"), dryRun, cancellationToken).ConfigureAwait(false)
            ?? throw new FleetException(FleetNodeErrorCodes.AutomationNotFound, null, "No automation of yours on this machine has that id.");
        Log($"{caller.Name} ({caller.DeviceId}) {(dryRun ? "tried" : "ran")} the automation {id}: {run.Status}.");
        return Describe(run);
    }

    /// <summary>
    /// Runs an automation by hand with the id of its run, and answers once its session started or it failed to start, or as
    /// soon as a budget of the machine holds it (<see cref="AutomationRun.Held"/>): a run held waits for its owner as long as
    /// it takes, in the work of the engine, which it outlives the call in.
    /// </summary>
    /// <param name="id">The automation.</param>
    /// <param name="runId">The id of the run.</param>
    /// <param name="dryRun">Whether it is a dry run, whose tools it calls by itself record their calls instead of making them.</param>
    /// <param name="cancellationToken">Cancels the wait for the answer, not the run.</param>
    /// <returns>The run; null for an automation that does not exist.</returns>
    private async Task<AutomationRun?> RunByHandAsync(string id, string runId, bool dryRun, CancellationToken cancellationToken)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _holdWaiters[runId] = held;
        try
        {
            var running = Engine.RunAsync(id, runId, dryRun, Engine.Stopping);
            if (await Task.WhenAny(running, held.Task).WaitAsync(cancellationToken).ConfigureAwait(false) != running
                && Engine.Runs(id, MaximumRuns).FirstOrDefault(candidate => candidate.Id == runId) is { Status: AutomationRun.Held or AutomationRun.Queued } waiting)
            {
                _ = running.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                return waiting;
            }

            return await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _holdWaiters.TryRemove(runId, out _);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<FleetAutomationRunInfo> Runs(FleetAutomationCaller caller, string? id, int limit)
    {
        ArgumentNullException.ThrowIfNull(caller);
        limit = Math.Clamp(limit, 1, MaximumRuns);
        HashSet<string> assigned;
        lock (_gate) assigned = [.. _assignments.Keys];
        foreach (var entry in Engine.Snapshot.Entries.Where(static entry => entry.Source.IsAssigned)) assigned.Add(entry.Id);
        if (id is not null)
            return assigned.Contains(id) ? [.. Engine.Runs(id, limit).Select(Describe)] : [];
        // The runs of the automations of the profile stay with its owner on this machine.
        return [.. Engine.Runs(null, AutomationStateStore.MaximumRuns).Where(run => assigned.Contains(run.AutomationId)).Take(limit).Select(Describe)];
    }

    /// <summary>Stops the schedules and the triggers; sessions that run keep going.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Engine.Changed -= PokeReports;
        await Engine.DisposeAsync().ConfigureAwait(false);
        await StopReportsAsync().ConfigureAwait(false);
    }

    internal static FleetAutomationRunInfo Describe(AutomationRun run)
        => new(run.Id, run.AutomationId, run.StartedAt, run.Trigger, run.Status)
        {
            Name = run.Name, SessionId = run.SessionId, Detail = run.Detail, Message = run.Message, EndedAt = run.EndedAt,
            InputTokens = run.InputTokens, OutputTokens = run.OutputTokens, Cost = run.Cost, CostUnit = run.CostUnit,
        };

    private FleetAutomationInfo Describe(AutomationEntry entry, NodeAutomationAssignment? assignment)
        => new(entry.Id, entry.Definition.Name, entry.Definition.Enabled, entry.Definition.Fingerprint())
        {
            AssignedBy = assignment?.DeviceId, AssignedByName = assignment?.DeviceName, AssignedAt = assignment?.AssignedAt,
            Project = entry.ProjectPath, NextDue = Engine.NextDue(entry.Id), Running = Engine.IsRunning(entry.Id), Problem = entry.Problem,
        };

    /// <summary>Tells the credential kind a provider runs with on this machine, as the check before a run does.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>Its kind.</returns>
    internal AutomationCredentialKind CredentialKindOf(ModelProviderDescriptor provider) => _options.CredentialKind?.Invoke(provider) ?? Classify(provider);

    private AutomationCredentialKind Classify(ModelProviderDescriptor provider) => Classify(_host.CatalogOptions, provider);

    /// <summary>Tells the credential kind a provider runs with, from its type and the configuration of the profile.</summary>
    /// <param name="catalog">The folders of the profile.</param>
    /// <param name="provider">The provider.</param>
    /// <returns>Its kind.</returns>
    internal static AutomationCredentialKind Classify(CatalogOptions catalog, ModelProviderDescriptor provider)
    {
        CodeAltaProviderDocument? configuration = null;
        try
        {
            configuration = new CodeAltaConfigStore(catalog).LoadGlobalProviderDefinitions(includeDisabled: true)
                .FirstOrDefault(candidate => string.Equals(candidate.ProviderKey, provider.ProviderId.Value, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Without the configuration, a provider whose kind depends on it is unknown, and refused.
        }

        return AutomationCredentials.Classify(provider.ProviderType, configuration);
    }

    // The project of the node whose repository has this key.
    private async Task<string?> FindProjectAsync(string repository, CancellationToken cancellationToken)
    {
        var key = RepositoryKey.Normalize(repository) ?? repository.Trim();
        foreach (var project in await _host.ProjectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (project.Archived || string.IsNullOrWhiteSpace(project.ProjectPath)) continue;
            if (string.Equals(await RepositoryKey.ReadAsync(project.ProjectPath, cancellationToken).ConfigureAwait(false), key, StringComparison.Ordinal))
                return project.ProjectPath;
        }

        return null;
    }

    private static FleetException Invalid(string message) => new(FleetNodeErrorCodes.AutomationInvalid, null, message);

    private static Dictionary<string, NodeAutomationAssignment> LoadAssignments(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = JsonSerializer.Deserialize(stream, NodeAutomationJsonContext.Default.DictionaryStringNodeAutomationAssignment);
            return new((read ?? []).Where(static pair => pair.Value is { DeviceId.Length: > 0, Fingerprint.Length: > 0 } or { DeviceId.Length: > 0, Removed: true }),
                StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Without a record of who assigned them, the assigned automations do not run until assigned again.
            return new(StringComparer.Ordinal);
        }
    }

    // Under the gate.
    private void SaveAssignments()
    {
        try
        {
            Directory.CreateDirectory(_fleetRoot);
            var path = Path.Combine(_fleetRoot, AssignmentsFileName);
            SafeFile.WriteAllText(path, JsonSerializer.Serialize(_assignments, NodeAutomationJsonContext.Default.DictionaryStringNodeAutomationAssignment));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"The assignments of the automations could not be written: {exception.Message}");
        }
    }

    private void Log(string message)
    {
        if (LogManager.IsInitialized) _logger.Info(message);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(NodeAutomationPolicy))]
[JsonSerializable(typeof(Dictionary<string, NodeAutomationAssignment>))]
internal sealed partial class NodeAutomationJsonContext : JsonSerializerContext;
