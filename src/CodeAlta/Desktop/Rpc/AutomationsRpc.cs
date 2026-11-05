using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Automations;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.Scripts;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the automations: it lists them with where they run, when they are next due and what they
/// last started; it writes one in the configuration of the user or of a project, removes one, runs one now,
/// pauses them all, and is told when anything of this changes.
/// </summary>
[NeoRpcService("automations", Version = 1)]
internal sealed class AutomationsService
{
    /// <summary>The most runs one answer holds.</summary>
    internal const int MaximumRuns = 200;

    private const int RecentRuns = 40;
    private const int UpcomingSlots = 72; // one every twenty minutes
    private const int MaximumUpcoming = 480;
    private const int PreviewTimes = 5;

    private readonly AutomationService? _automations;
    private readonly ProjectCatalog? _projects;
    private readonly CodeModeScriptLibrary? _scripts;
    private readonly Fleet.DesktopAutomationPlacement? _placement;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal AutomationsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="automations">The automations of the application.</param>
    /// <param name="projects">The host's project catalog, for the names of the projects.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="scripts">The library of scripts an automation may run in place of a prompt; null for none.</param>
    /// <param name="placement">What copies the automations that run on a paired machine there, and reads their runs; null for none.</param>
    internal AutomationsService(AutomationService automations, ProjectCatalog projects, string epoch, CodeModeScriptLibrary? scripts = null,
        Fleet.DesktopAutomationPlacement? placement = null)
    {
        ArgumentNullException.ThrowIfNull(automations);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_automations, _projects, _epoch, _scripts, _placement) = (automations, projects, epoch, scripts, placement);
    }

    /// <summary>
    /// Gets what reads whom this computer's person may publish an automation to on the server of a machine: the teams they
    /// maintain and, for an owner or an admin, the organization; null where no server is linked.
    /// </summary>
    internal Func<string, CancellationToken, Task<IReadOnlyList<AutomationPublishTarget>>>? PublishTargets { get; init; }

    /// <summary>Whom an automation that runs on a machine may be published to: the person, a team they maintain, the organization.</summary>
    [NeoRpcMethod("publishTargets")]
    public async Task<AutomationPublishTargetsResponse> PublishTargetsAsync(AutomationMachineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        if (string.IsNullOrEmpty(request.MachineId) || request.MachineId.Length > AutomationDefinition.MaximumMachineLength) return new("invalid_request", []);
        if (PublishTargets is null) return new("ok", []);
        try
        {
            return new("ok", [.. await PublishTargets(request.MachineId, cancellationToken).ConfigureAwait(false)]);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException
            or CodeAlta.Fleet.Server.FleetServerException && !cancellationToken.IsCancellationRequested)
        {
            return new("unreachable", []);
        }
    }

    /// <summary>
    /// Gets what lists the automations of the teams this person maintains, and of the organization they answer for, that
    /// another person gave their machines and this computer does not hold; null where no server is linked.
    /// </summary>
    internal Func<CancellationToken, Task<IReadOnlyList<AutomationTeamItem>>>? TeamAutomations { get; init; }

    /// <summary>Gets what takes one of those automations into the automations of this computer, to change it here; null where no server is linked.</summary>
    internal Func<string, string, string, CancellationToken, Task<string?>>? OpenTeamAutomation { get; init; }

    /// <summary>The automations of the teams this person maintains, and of the organization, that another person gave their machines.</summary>
    [NeoRpcMethod("teamAutomations")]
    public async Task<AutomationTeamItemsResponse> TeamAutomationsAsync(AutomationsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        if (TeamAutomations is null) return new("ok", []);
        return new("ok", [.. await TeamAutomations(cancellationToken).ConfigureAwait(false)]);
    }

    /// <summary>Takes an automation of a team, or of the organization, into the automations of this computer, to change it in the editor.</summary>
    [NeoRpcMethod("openTeamAutomation")]
    public async Task<AutomationMutationResponse> OpenTeamAutomationAsync(AutomationTeamOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (string.IsNullOrEmpty(request.LinkId) || string.IsNullOrEmpty(request.Id) || !Identifier(request.Id) || string.IsNullOrEmpty(request.MachineId)
            || request.MachineId.Length > AutomationDefinition.MaximumMachineLength)
            return new("invalid_request", null, null);
        if (OpenTeamAutomation is null) return new("unavailable", null, "No server is linked.");
        try
        {
            var problem = await OpenTeamAutomation(request.LinkId, request.Id, request.MachineId, cancellationToken).ConfigureAwait(false);
            return problem is null ? new("ok", request.Id, null) : new("failed", null, problem);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException
            or CodeAlta.Fleet.Server.FleetServerException && !cancellationToken.IsCancellationRequested)
        {
            return new("unreachable", null, exception.Message);
        }
    }

    /// <summary>The scripts of the library an automation of a project, or of the user, can run, with their parameters.</summary>
    [NeoRpcMethod("scripts")]
    public async Task<AutomationScriptsResponse> ScriptsAsync(AutomationScriptsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        if (request.ProjectId is { } projectId && !Identifier(projectId)) return new("invalid_request", []);
        if (_scripts is null) return new("ok", []);
        var project = request.ProjectId is null ? null : await _projects!.GetByIdAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var scripts = await Task.Run(() => _scripts.List(project?.ProjectPath), cancellationToken).ConfigureAwait(false);
        return new("ok", [.. scripts.Where(static script => script.Problem is null).Select(static script => new AutomationScriptItem(script.Name,
            script.Scope.ToString().ToLowerInvariant(), script.Description, Parameters(script.ParametersSchemaJson)))]);

        static IReadOnlyList<AutomationScriptParameter> Parameters(string? schema)
        {
            if (schema is null) return [];
            using var document = System.Text.Json.JsonDocument.Parse(schema);
            var root = document.RootElement;
            var required = root.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(static name => name.GetString()).ToHashSet(StringComparer.Ordinal) : [];
            return root.TryGetProperty("properties", out var properties) && properties.ValueKind == System.Text.Json.JsonValueKind.Object
                ? [.. properties.EnumerateObject().Select(property => new AutomationScriptParameter(property.Name,
                    property.Value.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String ? type.GetString() : null,
                    required.Contains(property.Name),
                    property.Value.TryGetProperty("description", out var description) && description.ValueKind == System.Text.Json.JsonValueKind.String ? description.GetString() : null))]
                : [];
        }
    }

    /// <summary>The automations as they were last read, their state and the latest runs.</summary>
    [NeoRpcMethod("list")]
    public async Task<AutomationsListResponse> ListAsync(AutomationsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, false, false, [], [], [], []);
        return await ListAsync("ok", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the configuration files again, then lists.</summary>
    [NeoRpcMethod("refresh")]
    public async Task<AutomationsListResponse> RefreshAsync(AutomationsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, false, false, [], [], [], []);
        await _automations!.RefreshAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return await ListAsync("ok", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes an automation: a new one when the request names no identifier.</summary>
    [NeoRpcMethod("save")]
    public async Task<AutomationMutationResponse> SaveAsync(AutomationSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request.Automation is not { } input || !TryRead(input, out var definition, out var problem)) return new("invalid_request", null, "The automation is not complete.");
        if (problem is not null) return new("refused", null, problem);
        if (request.StoreProjectId is { } store && !Identifier(store)) return new("invalid_request", null, null);
        // Stored with a project, it runs there; stored with the user, it runs where it says, or as a chat.
        if (request.StoreProjectId is null && input.ProjectId is { } runs)
        {
            var project = await _projects!.GetByIdAsync(runs, cancellationToken).ConfigureAwait(false);
            if (project is null || project.Archived) return new("refused", null, "Its project is no longer one of the projects of CodeAlta.");
            definition = definition! with { Project = project.ProjectPath };
        }

        return await _automations!.SaveAsync(definition!, request.StoreProjectId, cancellationToken).ConfigureAwait(false) is { } failure
            ? new("refused", null, failure) : new("ok", definition!.Id, null);
    }

    /// <summary>Removes an automation from its configuration file.</summary>
    [NeoRpcMethod("delete")]
    public async Task<AutomationMutationResponse> DeleteAsync(AutomationIdRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!AutomationDefinition.IsId(request.Id)) return new("invalid_request", null, null);
        return await _automations!.DeleteAsync(request.Id, cancellationToken).ConfigureAwait(false) is { } failure
            ? new("refused", null, failure) : new("ok", request.Id, null);
    }

    /// <summary>Enables or disables the triggers of one automation.</summary>
    [NeoRpcMethod("setEnabled")]
    public async Task<AutomationMutationResponse> SetEnabledAsync(AutomationEnableRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!AutomationDefinition.IsId(request.Id)) return new("invalid_request", null, null);
        // Turning its triggers on or off does not say that what it does was read: it stays allowed, or not, as it was.
        return await _automations!.SetEnabledAsync(request.Id, request.Enabled, cancellationToken).ConfigureAwait(false) is { } failure
            ? new("refused", null, failure) : new("ok", request.Id, null);
    }

    /// <summary>Allows the triggers of an automation kept with a project to start it, as it is now.</summary>
    [NeoRpcMethod("allow")]
    public AutomationMutationResponse Allow(AutomationIdRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!AutomationDefinition.IsId(request.Id)) return new("invalid_request", null, null);
        return _automations!.Allow(request.Id) ? new("ok", request.Id, null) : new("refused", null, "There is no such automation.");
    }

    /// <summary>
    /// Starts an automation on the machine it was placed on without waiting for the machine it left to take its copy away:
    /// the person chooses it, knowing both machines may run it until that one answers.
    /// </summary>
    [NeoRpcMethod("startPlaced")]
    public AutomationMutationResponse StartPlaced(AutomationIdRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!AutomationDefinition.IsId(request.Id)) return new("invalid_request", null, null);
        return _placement?.ForceHandoff(request.Id) == true ? new("ok", request.Id, null) : new("refused", null, "It waits for no other machine.");
    }

    /// <summary>Pauses every trigger, or starts them again. Running an automation by hand still works while paused.</summary>
    [NeoRpcMethod("setPaused")]
    public AutomationMutationResponse SetPaused(AutomationPauseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        _automations!.Paused = request.Paused;
        return new("ok", null, null);
    }

    /// <summary>Runs an automation now. The answer comes once its session exists, or says why it did not start.</summary>
    [NeoRpcMethod("run")]
    public async Task<AutomationRunResponse> RunAsync(AutomationIdRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!AutomationDefinition.IsId(request.Id)) return new("invalid_request", null);
        var run = await _automations!.RunAsync(request.Id, cancellationToken).ConfigureAwait(false);
        return run is null ? new("not_found", null)
            : new("ok", Run(run) with { MachineId = _automations.Snapshot.Find(request.Id)?.Definition.RunsOn });
    }

    /// <summary>The runs of one automation, or of all, newest first.</summary>
    [NeoRpcMethod("runs")]
    public AutomationRunsResponse Runs(AutomationRunsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        if (request.Id is { } id && !AutomationDefinition.IsId(id)) return new("invalid_request", []);
        var limit = Math.Clamp(request.Limit ?? MaximumRuns, 1, MaximumRuns);
        // The runs of an automation placed on a machine are those the machine reports.
        if (request.Id is { } placed && _automations!.Snapshot.Find(placed) is { Definition.RunsOn: { } machine } && _placement is not null)
            return new("ok", [.. _placement.Runs(placed).Take(limit).Select(run => Run(run) with { MachineId = machine })]);
        return new("ok", [.. _automations!.Runs(request.Id, limit).Select(Run)]);
    }

    /// <summary>Tests the connection of a trigger of another tool as the editor writes it, with the secrets of this computer.</summary>
    [NeoRpcMethod("testTrigger")]
    public async Task<AutomationTriggerTestResponse> TestTriggerAsync(AutomationTriggerTestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (request.Trigger is null || !TryRead(request.Trigger, out var trigger) || request.ProjectId is { } project && !Identifier(project)) return new("invalid_request", null);
        var (message, problem) = await _automations!.TestTriggerAsync(request.ProjectId, trigger!, cancellationToken).ConfigureAwait(false);
        return problem is null ? new("ok", message) : new("refused", problem);
    }

    /// <summary>The event a trigger of another tool would start on now, and the prompt it would send; nothing starts.</summary>
    [NeoRpcMethod("sampleEvent")]
    public async Task<AutomationSampleResponse> SampleEventAsync(AutomationSampleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, null, null);
        if (request.Trigger is null || !TryRead(request.Trigger, out var trigger) || request.ProjectId is { } project && !Identifier(project)
            || request.Prompt is { Length: > AutomationDefinition.MaximumPromptLength * 2 } || request.Body is { Length: > 256 * 1024 }) return new("invalid_request", null, null, null, null);
        var (sample, problem) = await _automations!.SampleAsync(request.ProjectId, request.Prompt ?? string.Empty, trigger!, request.Body, cancellationToken).ConfigureAwait(false);
        return sample is null ? new("refused", problem, null, null, null) : new("ok", null, sample.Detail, sample.Prompt, sample.Reply?.Where);
    }

    /// <summary>Runs a saved automation once on the sample event of one of its triggers, its answer kept instead of written back.</summary>
    [NeoRpcMethod("tryRun")]
    public async Task<AutomationTryResponse> TryRunAsync(AutomationTryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!AutomationDefinition.IsId(request.Id) || request.Body is { Length: > 256 * 1024 }) return new("invalid_request", null, null);
        var (run, problem) = await _automations!.TryAsync(request.Id, request.TriggerIndex, request.Body, cancellationToken).ConfigureAwait(false);
        return run is null ? new("refused", problem, null) : new("ok", null, Run(run));
    }

    /// <summary>
    /// Makes a hook on the server of a machine this computer reaches through one: the address incident.io or another tool
    /// pushes its signed events to. The machine checks their signature; the server holds no secret of the sender.
    /// </summary>
    [NeoRpcMethod("createHook")]
    public async Task<AutomationHookResponse> CreateHookAsync(AutomationHookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, null);
        if (!Identifier(request.MachineId) || request.Source is not ("incident_io" or "webhook") || request.Label is { Length: > 120 }) return new("invalid_request", null, null, null);
        if (_placement?.ServerRoute?.Invoke(request.MachineId) is not { } route)
            return new("refused", "This machine is reached through no server online: a hook is an address of a server, for a machine connected to it.", null, null);
        using (route)
        {
            try
            {
                var (hook, address) = await route.CreateHookAsync(request.MachineId, request.Source, Blank(request.Label), cancellationToken).ConfigureAwait(false);
                return new("ok", null, hook.Id, address.ToString());
            }
            catch (Exception exception) when (exception is CodeAlta.Fleet.Server.FleetServerException or HttpRequestException or TaskCanceledException)
            {
                return new("refused", "The server did not make the hook: " + exception.Message.ReplaceLineEndings(" "), null, null);
            }
        }
    }

    /// <summary>
    /// Reads again the address of a hook a trigger names, from the server of its machine: for the person in the window, who
    /// gives it to a tool. No command of the agents reads it from a sandbox (<c>alta fleet hook list</c> is refused there).
    /// </summary>
    [NeoRpcMethod("hookAddress")]
    public async Task<AutomationHookResponse> HookAddressAsync(AutomationHookAddressRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, null);
        if (!Identifier(request.MachineId) || !AutomationIntegrationConfig.IsHookId(request.HookId)) return new("invalid_request", null, null, null);
        if (_placement?.ServerRoute?.Invoke(request.MachineId) is not { } route)
            return new("refused", "This machine is reached through no server online now: its hooks are read from its server.", null, null);
        using (route)
        {
            try
            {
                return await route.HookAddressAsync(request.MachineId, request.HookId, cancellationToken).ConfigureAwait(false) is { } address
                    ? new("ok", null, request.HookId, address.ToString())
                    : new("refused", "No hook of yours on the server of this machine has this id: make a new one.", null, null);
            }
            catch (Exception exception) when (exception is CodeAlta.Fleet.Server.FleetServerException or HttpRequestException or TaskCanceledException)
            {
                return new("refused", "The server did not answer: " + exception.Message.ReplaceLineEndings(" "), null, null);
            }
        }
    }

    /// <summary>
    /// Takes a run in progress of an automation with autonomy over: what it waits for waits for this person, as long as they
    /// take, and no approval stops it any more. A run on this computer is taken over here; one on a machine of a server through
    /// that server, with a request this computer signs. The answer names the session to open, for a run of this computer.
    /// </summary>
    [NeoRpcMethod("takeOver")]
    public async Task<AutomationTakeOverResponse> TakeOverAsync(AutomationTakeOverRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!AutomationDefinition.IsId(request.Id) || !Identifier(request.RunId)) return new("invalid_request", null, null);
        var (status, message, sessionId) = await AutomationTakeOvers.TakeOverAsync(_automations!, _placement, request.Id, request.RunId, cancellationToken).ConfigureAwait(false);
        return new(status, message, sessionId);
    }

    /// <summary>
    /// Takes a run that waits on a machine of a server over, by the ids of the message of that server: for a person who answers
    /// the approvals of an automation without having placed it (a member of the team that owns it, a person granted to approve
    /// it), as for its owner. The server and the machine check who may.
    /// </summary>
    [NeoRpcMethod("takeOverOnMachine")]
    public async Task<AutomationTakeOverResponse> TakeOverOnMachineAsync(AutomationTakeOverOnMachineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!AutomationDefinition.IsId(request.Id) || !Identifier(request.MachineId) || !Identifier(request.RunId)) return new("invalid_request", null, null);
        if (_placement is null) return new("refused", "Its machine is reached from no fleet here.", null);
        var (status, message, _) = (await _placement.TakeOverAsync(request.Id, request.MachineId, request.RunId, cancellationToken).ConfigureAwait(false)).Status();
        return new(status, message, null);
    }

    /// <summary>The next times a trigger being written would be due, or what is wrong with it.</summary>
    [NeoRpcMethod("preview")]
    public AutomationPreviewResponse Preview(AutomationPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, []);
        if (request.Trigger is null || !TryRead(request.Trigger, out var trigger)) return new("invalid_request", null, []);
        var (times, problem) = _automations!.Preview(trigger!, PreviewTimes);
        return problem is null ? new("ok", null, times) : new("refused", problem, []);
    }

    /// <summary>Tells when the automations, their runs or their state changed: the page then lists again.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<AutomationsEvent> Watch(AutomationsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.AutomationsEvent);
    }

    internal async IAsyncEnumerable<AutomationsEvent> WatchAsync(AutomationsRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null) yield break;
        // A page that does not read misses nothing: one pending notice says everything that changed.
        var notices = Channel.CreateBounded<int>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        void Notify() => notices.Writer.TryWrite(0);
        _automations!.Changed += Notify;
        if (_placement is not null) _placement.Changed += Notify;
        try
        {
            var revision = 0;
            yield return new AutomationsEvent(revision);
            await foreach (var _ in notices.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return new AutomationsEvent(++revision);
        }
        finally
        {
            _automations.Changed -= Notify;
            if (_placement is not null) _placement.Changed -= Notify;
        }
    }

    private async Task<AutomationsListResponse> ListAsync(string status, CancellationToken cancellationToken)
    {
        var snapshot = _automations!.Snapshot;
        IReadOnlyList<ProjectDescriptor> projects;
        try
        {
            projects = await _projects!.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            projects = [];
        }

        var remoteRuns = new List<AutomationRunItem>();
        var items = snapshot.Entries.Select(entry =>
        {
            var definition = entry.Definition;
            var project = entry.ProjectId is null ? null : projects.FirstOrDefault(candidate => candidate.Id == entry.ProjectId);
            AutomationRunItem? lastRun;
            AutomationPlacementItem? placed = null;
            var running = _automations.IsRunning(entry.Id);
            if (definition.RunsOn is { } machine && _placement is not null)
            {
                // It runs on its machine: what is known of it is what that machine reports.
                var runs = _placement.Runs(entry.Id);
                remoteRuns.AddRange(runs.Take(RecentRuns).Select(run => Run(run) with { MachineId = machine }));
                lastRun = runs.Count > 0 ? Run(runs[0]) with { MachineId = machine } : null;
                running = runs.Count > 0 && runs[0].Status == AutomationRun.Running;
                placed = _placement.Status(entry) is { } status ? new AutomationPlacementItem(status.MachineId, status.MachineName, status.State, status.Message, status.CopiedAt) : null;
            }
            else
            {
                var runs = _automations.Runs(entry.Id, 1);
                lastRun = runs.Count > 0 ? Run(runs[0]) : null;
                // Back here, it may wait for a machine it left to take its copy away.
                if (_placement?.Status(entry) is { } waiting)
                    placed = new AutomationPlacementItem(waiting.MachineId, waiting.MachineName, waiting.State, waiting.Message, waiting.CopiedAt);
            }

            return new AutomationItem(entry.Id, definition.Name, definition.Enabled, definition.Prompt, entry.ProjectId, project?.DisplayName,
                entry.ProjectPath ?? definition.Project, entry.Source.ProjectId, entry.Source.FilePath, definition.Model.Provider, definition.Model.Model,
                definition.Model.Effort?.ToString(), definition.Agent, definition.PermissionMode, definition.ReviewBeforeCommit, definition.Sandbox, definition.CatchUp, [.. definition.Triggers.Select(Trigger)], entry.Problem,
                // A placed automation is next due when its machine says.
                definition.RunsOn is not null && _placement is not null ? _placement.NextDue(entry.Id) : _automations.NextDue(entry.Id), running, lastRun,
                _automations.Repository(entry.Id), definition.Enabled ? _automations.WatchProblem(entry.Id) : null, _automations.IsAllowed(entry))
            {
                Script = definition.Script,
                ScriptArguments = definition.ScriptArguments,
                Options = SessionOptionsChoice.Of(definition.SessionOptions),
                RunsOn = definition.RunsOn,
                PublishTo = definition.PublishTo,
                Placement = placed,
                Autonomy = Autonomy(definition.Autonomy),
            };
        }).ToArray();
        IEnumerable<AutomationRunItem> recent = _automations.Runs(null, RecentRuns).Select(Run);
        if (remoteRuns.Count > 0) recent = recent.Concat(remoteRuns).OrderByDescending(static run => run.StartedAt).Take(RecentRuns);
        return new(status, _automations.Paused, snapshot.Scanned, items,
            [.. snapshot.Faults.Select(static fault => new AutomationFaultItem(fault.Source.FilePath, fault.Source.ProjectId, fault.Key, fault.Message))],
            [.. recent],
            [.. _automations.Upcoming(TimeSpan.FromHours(24), UpcomingSlots, MaximumUpcoming).Select(static time => new AutomationUpcomingItem(time.Id, time.At))]);
    }

    private string? Refuse(string? expectedEpoch)
        => _automations is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);

    private static AutomationAutonomyItem? Autonomy(AutomationAutonomy? autonomy)
        => autonomy is null ? null : new(autonomy.AllowedTools, autonomy.MaxCallsPerHour, autonomy.ApprovalTimeout, autonomy.OnTimeout, autonomy.Approvers)
        {
            Tools = autonomy.Tools.Count == 0 ? null : [.. autonomy.Tools.Select(static tool => new AutomationAllowedToolItem(tool.Name, tool.Version, tool.Hash,
                [.. tool.Bounds.Select(static bound => new AutomationToolBoundItem(bound.Argument, bound.Values, Decimal(bound.Min), Decimal(bound.Max)))],
                [.. tool.Preconditions.Select(static precondition => new AutomationToolPreconditionItem(precondition.Tool, precondition.Arguments, precondition.Expect))]))],
            OnCall = autonomy.OnCall is { } onCall ? new AutomationOnCallItem(onCall.Source, onCall.Schedule, onCall.Token, onCall.Region) : null,
        };

    // A bound as the page shows it, as a decimal: one beyond what a decimal holds (7.9e28) is none a person writes for an argument.
    private static decimal? Decimal(double? value) => value is { } number && double.IsFinite(number) && Math.Abs(number) < 7.9e27 ? (decimal)number : null;

    // What the page gives back of the tools an autonomy describes, for those it still allows.
    private static IReadOnlyList<AutomationAllowedTool> Tools(AutomationAutonomyItem autonomy)
        => [.. (autonomy.Tools ?? []).Where(tool => tool is not null && (autonomy.AllowedTools ?? []).Contains(tool.Name, StringComparer.Ordinal)).Select(static tool =>
            new AutomationAllowedTool(tool.Name.Trim())
            {
                Version = Blank(tool.Version), Hash = Blank(tool.Hash)?.ToLowerInvariant(),
                Bounds = [.. (tool.Bounds ?? []).Select(static bound => new AutomationToolBound(bound.Argument) { Values = bound.Values, Min = (double?)bound.Min, Max = (double?)bound.Max })],
                Preconditions = [.. (tool.Preconditions ?? []).Select(static precondition =>
                    new AutomationToolPrecondition(precondition.Tool) { Arguments = precondition.Arguments ?? [], Expect = precondition.Expect ?? string.Empty })],
            }).Where(static tool => tool.HasDetails)];

    private static AutomationRunItem Run(AutomationRun run)
        => new(run.Id, run.AutomationId, run.Name, run.SessionId, run.ProjectId, run.StartedAt, run.EndedAt, run.Trigger, run.Detail, run.Status, run.Message)
        {
            Reply = run.Reply, Output = run.Output, Calls = run.Calls,
        };

    internal static AutomationTriggerItem Trigger(AutomationTrigger trigger)
        => new(trigger.KindName, trigger.Minute, trigger.Every, [.. trigger.At.Select(static time => time.ToString())],
            [.. trigger.Days.Select(static day => AutomationTrigger.DayNames[(int)day])], trigger.Expression, trigger.Event,
            trigger.Authors == AutomationAuthors.Anyone ? "anyone" : "trusted", trigger.Command, trigger.Folder)
        {
            Integration = trigger.IsIntegration
                ? new AutomationIntegrationItem(trigger.Site, trigger.Email, trigger.Token, trigger.JiraProject, trigger.Query, trigger.AppToken, trigger.BotToken,
                    trigger.Channel, trigger.Match, trigger.ApiKey, trigger.Hook, trigger.Secret, trigger.Signature, trigger.SignatureHeader, trigger.Fields, trigger.Reply)
                {
                    Threads = trigger.Threads,
                }
                : null,
        };

    // False: the request is not one the page sends. A problem: what the user wrote is refused, with the reason.
    private static bool TryRead(AutomationInput input, out AutomationDefinition? definition, out string? problem)
    {
        (definition, problem) = (null, null);
        if (input.Name is null || input.Prompt is null || input.Triggers is null || input.Triggers.Count > AutomationDefinition.MaximumTriggers
            || input.Id is { } id && !AutomationDefinition.IsId(id) || input.ProjectId is { } project && !Identifier(project)
            || input.Name.Length > 4096 || input.Prompt.Length > AutomationDefinition.MaximumPromptLength * 2
            || input.Script is { Length: > 256 } || input.ScriptArguments is { Length: > AutomationDefinition.MaximumScriptArgumentsLength * 2 }) return false;
        AgentReasoningEffort? effort = null;
        if (input.Effort is { Length: > 0 } text)
        {
            if (!Enum.TryParse<AgentReasoningEffort>(text, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(text, out _)) return false;
            effort = parsed;
        }

        var triggers = new List<AutomationTrigger>();
        foreach (var item in input.Triggers)
        {
            if (item is null || !TryRead(item, out var trigger)) return false;
            triggers.Add(trigger!);
        }

        var provider = Blank(input.Provider);
        var model = Blank(input.Model);
        if (provider is null && (model is not null || effort is not null))
        {
            problem = "A model or a reasoning effort needs its provider.";
            return true;
        }

        definition = new AutomationDefinition(input.Id ?? AutomationDefinition.NewId(), input.Name.Trim())
        {
            Enabled = input.Enabled,
            // An automation that runs a script sends no prompt of its own.
            Prompt = Blank(input.Script) is null ? input.Prompt : string.Empty,
            Script = Blank(input.Script),
            ScriptArguments = Blank(input.Script) is null ? null : Blank(input.ScriptArguments),
            Model = new(provider, model, effort),
            Agent = Blank(input.Agent),
            PermissionMode = Blank(input.PermissionMode),
            ReviewBeforeCommit = input.ReviewBeforeCommit,
            Sandbox = Blank(input.Sandbox),
            RunsOn = Blank(input.RunsOn),
            PublishTo = Blank(input.RunsOn) is null ? null : Blank(input.PublishTo),
            SessionOptions = SessionOptionsChoice.Record(input.Options),
            Autonomy = input.Autonomy is { } autonomy ? new AutomationAutonomy
            {
                AllowedTools = [.. (autonomy.AllowedTools ?? []).Select(static tool => tool?.Trim() ?? string.Empty).Where(static tool => tool.Length > 0)],
                MaxCallsPerHour = autonomy.MaxCallsPerHour,
                ApprovalTimeout = autonomy.ApprovalTimeout,
                OnTimeout = Blank(autonomy.OnTimeout) ?? AutomationAutonomy.StopOnTimeout,
                Approvers = Blank(autonomy.Approvers) ?? AutomationAutonomy.OwnerApprovers,
                Tools = Tools(autonomy),
                OnCall = autonomy.OnCall is { } onCall && autonomy.Approvers == AutomationAutonomy.OnCallApprovers
                    ? new AutomationOnCall(onCall.Source.Trim(), onCall.Schedule.Trim(), onCall.Token.Trim()) { Region = Blank(onCall.Region) } : null,
            } : null,
            CatchUp = input.CatchUp,
            Triggers = triggers,
        };
        problem = AutomationConfig.Validate(definition with { Prompt = definition.Prompt.ReplaceLineEndings("\n").Trim() });
        return true;
    }

    internal static bool TryRead(AutomationTriggerItem item, out AutomationTrigger? trigger)
    {
        trigger = null;
        if (!AutomationTrigger.TryParseKind(item.Type, out var kind) || item.At is null || item.Days is null || item.At.Count > 24 || item.Days.Count > 7
            || item.Expression is { Length: > AutomationTrigger.MaximumExpressionLength } || item.Command is { Length: > AutomationTrigger.MaximumCommandLength * 2 }
            || item.Folder is { Length: > AutomationTrigger.MaximumFolderLength * 2 }) return false;
        var times = new List<AutomationTime>();
        foreach (var text in item.At)
        {
            if (!AutomationTime.TryParse(text, out var time)) return false;
            if (!times.Contains(time)) times.Add(time);
        }

        var days = new List<DayOfWeek>();
        foreach (var text in item.Days)
        {
            var day = Array.IndexOf(AutomationTrigger.DayNames, text);
            if (day < 0) return false;
            if (!days.Contains((DayOfWeek)day)) days.Add((DayOfWeek)day);
        }

        times.Sort();
        days.Sort();
        var integration = new AutomationTrigger(kind).IsIntegration ? item.Integration : null;
        if (integration is not null && (integration.Fields is null || integration.Fields.Count > AutomationTrigger.MaximumFields * 2
            || new[] { integration.Site, integration.Email, integration.Token, integration.JiraProject, integration.Query, integration.AppToken, integration.BotToken, integration.Channel,
                integration.Match, integration.ApiKey, integration.Hook, integration.Secret, integration.Signature, integration.SignatureHeader }.Any(static text => text is { Length: > AutomationTrigger.MaximumQueryLength * 2 })))
            return false;
        if (integration is not null)
        {
            var events = AutomationTrigger.Events(kind);
            trigger = new AutomationTrigger(kind)
            {
                Event = events.Contains(item.Event) ? item.Event : events[0],
                Site = Blank(integration.Site)?.TrimEnd('/'),
                Email = Blank(integration.Email),
                Token = Blank(integration.Token),
                JiraProject = Blank(integration.JiraProject),
                Query = Blank(integration.Query),
                AppToken = Blank(integration.AppToken),
                BotToken = Blank(integration.BotToken),
                Channel = Blank(integration.Channel),
                Match = Blank(integration.Match),
                ApiKey = Blank(integration.ApiKey),
                Hook = Blank(integration.Hook),
                Secret = Blank(integration.Secret),
                Signature = kind == AutomationTriggerKind.Webhook ? Blank(integration.Signature) ?? AutomationSignatures.Standard : kind == AutomationTriggerKind.IncidentIo ? AutomationSignatures.Standard : null,
                SignatureHeader = Blank(integration.SignatureHeader),
                Fields = [.. integration.Fields.Select(static field => field?.Trim() ?? string.Empty).Where(static field => field.Length > 0).Distinct(StringComparer.Ordinal)],
                Reply = integration.Reply,
                Threads = integration.Threads,
            };
            return true;
        }

        trigger = new AutomationTrigger(kind)
        {
            Minute = item.Minute,
            Every = item.Every,
            At = times,
            Days = days,
            Expression = kind == AutomationTriggerKind.Cron ? string.Join(' ', (item.Expression ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null,
            Event = item.Event is "updated" ? "updated" : kind == AutomationTriggerKind.Jira ? "created" : "opened",
            Authors = item.Authors == "anyone" ? AutomationAuthors.Anyone : AutomationAuthors.Trusted,
            Command = kind == AutomationTriggerKind.Command ? (item.Command ?? string.Empty).Trim() : null,
            Folder = kind == AutomationTriggerKind.Command ? Blank(item.Folder) : null,
        };
        return true;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

internal sealed record AutomationsRequest(string? ExpectedEpoch);

/// <summary>One trigger, as the page shows and edits it. Which members count depends on <see cref="Type"/>.</summary>
/// <param name="Type"><c>hourly</c>, <c>daily</c>, <c>weekly</c>, <c>cron</c>, <c>issue</c>, <c>pull_request</c>, <c>jira</c> or <c>command</c>.</param>
/// <param name="Minute">Hourly: the minute of the hour.</param>
/// <param name="Every">Hourly: every how many hours.</param>
/// <param name="At">Daily and weekly: the times of day, <c>HH:mm</c>.</param>
/// <param name="Days">Weekly: <c>sun</c> to <c>sat</c>.</param>
/// <param name="Expression">Cron: the five fields.</param>
/// <param name="Event">Issue: <c>opened</c>; pull request: <c>opened</c> or <c>updated</c>; Jira: <c>created</c> or <c>updated</c>.</param>
/// <param name="Authors">Issue and pull request: <c>trusted</c> or <c>anyone</c>.</param>
/// <param name="Command">Command: the command line the automation keeps running.</param>
/// <param name="Folder">Command: the folder it runs in, from the folder of the project; null for that folder.</param>
internal sealed record AutomationTriggerItem(string Type, int Minute, int Every, IReadOnlyList<string> At, IReadOnlyList<string> Days, string? Expression, string Event, string Authors,
    string? Command, string? Folder)
{
    /// <summary>The settings of a trigger of another tool (Jira versions and queries, Slack, incident.io, webhooks); null for the others.</summary>
    public AutomationIntegrationItem? Integration { get; init; }
}

/// <summary>
/// The settings of a trigger of another tool, as the page shows and edits them: credentials are <c>${secret:NAME}</c>
/// references, never values.
/// </summary>
/// <param name="Site">Jira: the https address of the site, or a <c>${var:NAME}</c> reference.</param>
/// <param name="Email">Jira: the email of the account of the token.</param>
/// <param name="Token">Jira: the API token, a secret reference.</param>
/// <param name="JiraProject">Jira version: the key of the project.</param>
/// <param name="Query">Jira query: the JQL.</param>
/// <param name="AppToken">Slack: the app-level token of Socket Mode, a secret reference.</param>
/// <param name="BotToken">Slack: the bot token, a secret reference.</param>
/// <param name="Channel">Slack: the id of the channel.</param>
/// <param name="Match">Slack: a text the message contains; null for any.</param>
/// <param name="ApiKey">incident.io: the API key, a secret reference.</param>
/// <param name="Hook">incident.io and webhooks: the hook of the server.</param>
/// <param name="Secret">incident.io and webhooks: the signing secret, a secret reference.</param>
/// <param name="Signature">Webhooks: <c>standard</c>, <c>github</c> or <c>hmac_sha256</c>.</param>
/// <param name="SignatureHeader">Webhooks with <c>hmac_sha256</c>: the header of the signature.</param>
/// <param name="Fields">Webhooks: the JSON pointers of the fields that reach the prompt.</param>
/// <param name="Reply">Whether the answer of the run is written back to the source.</param>
internal sealed record AutomationIntegrationItem(string? Site, string? Email, string? Token, string? JiraProject, string? Query, string? AppToken, string? BotToken,
    string? Channel, string? Match, string? ApiKey, string? Hook, string? Secret, string? Signature, string? SignatureHeader, IReadOnlyList<string> Fields, bool Reply)
{
    /// <summary>Slack, on every message: whether a person's replies in a thread the automation answered start runs too.</summary>
    public bool Threads { get; init; }
}

/// <summary>An automation as the page shows it.</summary>
/// <param name="ProjectId">The project it runs in; null for a chat.</param>
/// <param name="ProjectFolder">The folder it runs in, or the folder it names when that is not a project.</param>
/// <param name="StoreProjectId">The project whose configuration defines it; null for the configuration of the user.</param>
/// <param name="File">The configuration file that defines it.</param>
/// <param name="PermissionMode">The permission mode its sessions start in; null for the one of their provider.</param>
/// <param name="ReviewBeforeCommit">Whether the user reviews the changes of its sessions before a commit; null follows the setting.</param>
/// <param name="Sandbox">The sandbox its sessions run in, a profile or <c>off</c>; null for what the settings give.</param>
/// <param name="Problem">Why it cannot run as defined.</param>
/// <param name="NextRunAt">When a schedule of it is next due.</param>
/// <param name="LastRun">Its latest run.</param>
/// <param name="Repository">The repository its event triggers watch, as <c>owner/name</c>.</param>
/// <param name="WatchProblem">Why its event triggers see nothing of that repository, or what is wrong with the command of a trigger.</param>
/// <param name="Allowed">
/// Whether its triggers may start it: false for an automation kept with a project that the user has not allowed as it is now.
/// </param>
internal sealed record AutomationItem(string Id, string Name, bool Enabled, string Prompt, string? ProjectId, string? ProjectName, string? ProjectFolder,
    string? StoreProjectId, string File, string? Provider, string? Model, string? Effort, string? Agent, string? PermissionMode, bool? ReviewBeforeCommit, string? Sandbox, bool CatchUp,
    IReadOnlyList<AutomationTriggerItem> Triggers, string? Problem, DateTimeOffset? NextRunAt, bool Running, AutomationRunItem? LastRun,
    string? Repository, string? WatchProblem, bool Allowed)
{
    /// <summary>The script of the library it runs in place of a prompt; null when it sends its prompt.</summary>
    public string? Script { get; init; }

    /// <summary>The arguments of its script, a JSON object; null for none.</summary>
    public string? ScriptArguments { get; init; }

    /// <summary>The options of its sessions, such as whether they can start sessions of their own.</summary>
    public SessionOptionsChoice? Options { get; init; }

    /// <summary>The paired machine it runs on (its device id); null when it runs on this computer.</summary>
    public string? RunsOn { get; init; }

    /// <summary>Whom it is published to on the server of its machine: <c>team:&lt;id&gt;</c>, <c>organization</c>, or null for the person's own.</summary>
    public string? PublishTo { get; init; }

    /// <summary>Where it stands on the machine it runs on; null when it runs on this computer.</summary>
    public AutomationPlacementItem? Placement { get; init; }

    /// <summary>What its runs may do without a person; null for an automation that runs as before.</summary>
    public AutomationAutonomyItem? Autonomy { get; init; }
}

/// <summary>What the runs of an automation may do without a person, as the page shows and edits it.</summary>
/// <param name="AllowedTools">The tools of MCP servers and plugins its runs call by themselves.</param>
/// <param name="MaxCallsPerHour">The circuit breaker: the most calls of those tools in an hour; null for no limit.</param>
/// <param name="ApprovalTimeout">How many minutes an approval waits; null for as long as it takes.</param>
/// <param name="OnTimeout"><c>stop</c> or <c>wait</c>.</param>
/// <param name="Approvers"><c>owner</c>.</param>
internal sealed record AutomationAutonomyItem(IReadOnlyList<string> AllowedTools, int? MaxCallsPerHour, int? ApprovalTimeout, string OnTimeout, string Approvers)
{
    /// <summary>Gets what the allowed tools that say more than their name are pinned to and bounded by; null for none.</summary>
    public IReadOnlyList<AutomationAllowedToolItem>? Tools { get; init; }

    /// <summary>Gets the schedule of the on-call tool its runs read who is on call from; null for the rotation of its team.</summary>
    public AutomationOnCallItem? OnCall { get; init; }
}

/// <param name="Source"><c>incident_io</c>, <c>pagerduty</c> or <c>opsgenie</c>.</param>
/// <param name="Schedule">The id of the schedule in that tool.</param>
/// <param name="Token">The <c>${secret:NAME}</c> reference of its token.</param>
/// <param name="Region">For Opsgenie, <c>eu</c> or <c>us</c>; null for the United States.</param>
internal sealed record AutomationOnCallItem(string Source, string Schedule, string Token, string? Region);

/// <summary>An allowed tool that says more than its name.</summary>
/// <param name="Name">The tool.</param>
/// <param name="Version">The version of its server it is pinned to; null for any.</param>
/// <param name="Hash">The hash of its definition it is pinned to; null for any.</param>
/// <param name="Bounds">The bounds of its arguments.</param>
/// <param name="Preconditions">The tools it calls first.</param>
internal sealed record AutomationAllowedToolItem(string Name, string? Version, string? Hash, IReadOnlyList<AutomationToolBoundItem> Bounds,
    IReadOnlyList<AutomationToolPreconditionItem> Preconditions);

/// <param name="Argument">The argument.</param>
/// <param name="Values">The values it may take; null for any.</param>
/// <param name="Min">The least number; null for none.</param>
/// <param name="Max">The greatest number; null for none.</param>
internal sealed record AutomationToolBoundItem(string Argument, IReadOnlyList<string>? Values, decimal? Min, decimal? Max);

/// <param name="Tool">The tool it calls.</param>
/// <param name="Arguments">The arguments of the call it passes on.</param>
/// <param name="Expect">What its answer must contain.</param>
internal sealed record AutomationToolPreconditionItem(string Tool, IReadOnlyList<string> Arguments, string Expect);

/// <summary>Where an automation that runs on a paired machine stands there.</summary>
/// <param name="MachineId">The machine.</param>
/// <param name="MachineName">Its name; null when no paired machine has that id.</param>
/// <param name="State">
/// <c>copied</c>, <c>pending</c> (copied once the machine answers), <c>waiting</c> (copied once the machine it left took its
/// copy away, or when the person starts it anyway) or <c>failed</c> (the machine refused it, see the message).
/// </param>
/// <param name="Message">Why it is pending or failed.</param>
/// <param name="CopiedAt">When the machine last took it.</param>
internal sealed record AutomationPlacementItem(string MachineId, string? MachineName, string State, string? Message, DateTimeOffset? CopiedAt);

/// <summary>Asks whom an automation that runs on a machine may be published to.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine it runs on.</param>
internal sealed record AutomationMachineRequest(string? ExpectedEpoch, string? MachineId);

/// <summary>Whom an automation may be published to: the person, a team they maintain, the organization.</summary>
/// <param name="Scope"><c>team:&lt;id&gt;</c> or <c>organization</c>.</param>
/// <param name="Name">Its name.</param>
internal sealed record AutomationPublishTarget(string Scope, string Name);

/// <summary>An automation of a team, or of the organization, that another person gave a machine, as this computer opens it.</summary>
/// <param name="LinkId">The server that holds it.</param>
/// <param name="Id">The automation.</param>
/// <param name="MachineId">The machine it runs on.</param>
/// <param name="MachineName">The name of that machine.</param>
/// <param name="Name">Its name.</param>
/// <param name="Scope"><c>team:&lt;id&gt;</c> or <c>organization</c>.</param>
/// <param name="ScopeName">The name of the team, or of the organization.</param>
internal sealed record AutomationTeamItem(string LinkId, string Id, string MachineId, string MachineName, string Name, string Scope, string ScopeName);

/// <summary>The automations of the teams and of the organization that this computer does not hold.</summary>
/// <param name="Status"><c>ok</c> or <c>stale_epoch</c>.</param>
/// <param name="Items">The automations.</param>
internal sealed record AutomationTeamItemsResponse(string Status, AutomationTeamItem[] Items);

/// <summary>Takes an automation of a team, or of the organization, into the automations of this computer.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The server that holds it.</param>
/// <param name="Id">The automation.</param>
/// <param name="MachineId">The machine it runs on.</param>
internal sealed record AutomationTeamOpenRequest(string? ExpectedEpoch, string? LinkId, string? Id, string? MachineId);

/// <summary>Whom an automation may be published to.</summary>
/// <param name="Status"><c>ok</c>, <c>unreachable</c>, <c>invalid_request</c> or <c>stale_epoch</c>.</param>
/// <param name="Targets">The teams and the organization, beside the person.</param>
internal sealed record AutomationPublishTargetsResponse(string Status, AutomationPublishTarget[] Targets);

/// <summary>Asks for the scripts an automation can run.</summary>
/// <param name="ProjectId">The project it runs in, whose scripts come first; null for a chat.</param>
internal sealed record AutomationScriptsRequest(string? ExpectedEpoch, string? ProjectId);

/// <summary>The scripts an automation can run.</summary>
internal sealed record AutomationScriptsResponse(string Status, IReadOnlyList<AutomationScriptItem> Scripts);

/// <summary>A script of the library, as the editor of an automation offers it.</summary>
/// <param name="Scope"><c>project</c>, <c>global</c> or <c>builtin</c>.</param>
internal sealed record AutomationScriptItem(string Name, string Scope, string? Description, IReadOnlyList<AutomationScriptParameter> Parameters);

/// <summary>A parameter of a script.</summary>
/// <param name="Type">Its JSON type, such as <c>integer</c>; null for any.</param>
internal sealed record AutomationScriptParameter(string Name, string? Type, bool Required, string? Description);

/// <summary>One run of an automation.</summary>
/// <param name="Trigger"><c>manual</c>, or the type of the trigger that started it.</param>
/// <param name="Status"><c>running</c>, <c>completed</c>, <c>failed</c>, <c>cancelled</c>, <c>interrupted</c> or <c>skipped</c>.</param>
internal sealed record AutomationRunItem(string Id, string AutomationId, string? Name, string? SessionId, string? ProjectId, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string Trigger, string? Detail, string Status, string? Message)
{
    /// <summary>The paired machine the run ran on, whose session it is; null for this computer.</summary>
    public string? MachineId { get; init; }

    /// <summary>What was done with the answer of a run started by another tool: written back to its source, failed, or kept by a try.</summary>
    public string? Reply { get; init; }

    /// <summary>The answer a try kept instead of writing it back.</summary>
    public string? Output { get; init; }

    /// <summary>The calls of allowed tools a try recorded instead of making them; null for none.</summary>
    public IReadOnlyList<string>? Calls { get; init; }
}

/// <param name="Id">The automation.</param>
/// <param name="RunId">Its run in progress.</param>
internal sealed record AutomationTakeOverRequest(string? ExpectedEpoch, string Id, string RunId);

/// <param name="Id">The automation.</param>
/// <param name="MachineId">The machine that runs it.</param>
/// <param name="RunId">Its run that waits.</param>
internal sealed record AutomationTakeOverOnMachineRequest(string? ExpectedEpoch, string Id, string MachineId, string RunId);

/// <param name="Message">Why the run was not taken over.</param>
/// <param name="SessionId">The session of the run to open, for a run of this computer; null for a run of another machine.</param>
internal sealed record AutomationTakeOverResponse(string Status, string? Message, string? SessionId);

/// <param name="ProjectId">The project the automation runs in, whose secrets come first; null for a chat.</param>
/// <param name="Prompt">Unused by the test; kept for one shape of request.</param>
/// <param name="Body">Unused by the test.</param>
internal sealed record AutomationTriggerTestRequest(string? ExpectedEpoch, string? ProjectId, AutomationTriggerItem Trigger, string? Prompt, string? Body);

/// <param name="Message">What works, or what does not.</param>
internal sealed record AutomationTriggerTestResponse(string Status, string? Message);

/// <param name="ProjectId">The project the automation runs in; null for a chat.</param>
/// <param name="Prompt">The prompt of the automation, which the sample prompt starts with.</param>
/// <param name="Body">A webhook: the JSON body of an example event.</param>
internal sealed record AutomationSampleRequest(string? ExpectedEpoch, string? ProjectId, AutomationTriggerItem Trigger, string? Prompt, string? Body);

/// <param name="Detail">What the event is about, in a line.</param>
/// <param name="Prompt">What the session would be sent.</param>
/// <param name="ReplyTo">Where its answer would be written back; null for nowhere.</param>
internal sealed record AutomationSampleResponse(string Status, string? Message, string? Detail, string? Prompt, string? ReplyTo);

/// <param name="TriggerIndex">The trigger whose sample event the try runs on; -1 tries its own prompt, a dry run of an automation with autonomy.</param>
/// <param name="Body">A webhook: the JSON body of an example event.</param>
internal sealed record AutomationTryRequest(string? ExpectedEpoch, string Id, int TriggerIndex, string? Body);

internal sealed record AutomationTryResponse(string Status, string? Message, AutomationRunItem? Run);

/// <param name="MachineId">The machine of a server the hook is for.</param>
/// <param name="Source"><c>incident_io</c> or <c>webhook</c>.</param>
internal sealed record AutomationHookRequest(string? ExpectedEpoch, string MachineId, string Source, string? Label);

/// <param name="MachineId">The machine of a server the hook is for.</param>
/// <param name="HookId">The hook a trigger names.</param>
internal sealed record AutomationHookAddressRequest(string? ExpectedEpoch, string MachineId, string HookId);

/// <param name="HookId">The id of the hook, which its trigger names.</param>
/// <param name="Url">The address to give the tool.</param>
internal sealed record AutomationHookResponse(string Status, string? Message, string? HookId, string? Url);

internal sealed record AutomationFaultItem(string File, string? ProjectId, string Key, string Message);

/// <summary>What the page knows of the automations.</summary>
/// <param name="Paused">Whether the triggers start nothing.</param>
/// <param name="Scanned">Whether the configuration files were read at least once.</param>
/// <param name="Runs">The latest runs of all the automations, newest first.</param>
/// <param name="Upcoming">When the schedules are due in the next 24 hours, soonest first.</param>
internal sealed record AutomationsListResponse(string Status, bool Paused, bool Scanned, IReadOnlyList<AutomationItem> Items,
    IReadOnlyList<AutomationFaultItem> Faults, IReadOnlyList<AutomationRunItem> Runs, IReadOnlyList<AutomationUpcomingItem> Upcoming);

internal sealed record AutomationUpcomingItem(string AutomationId, DateTimeOffset At);

/// <summary>An automation as the page writes it.</summary>
/// <param name="Id">The automation to replace; null for a new one.</param>
/// <param name="ProjectId">The project it runs in; null for a chat.</param>
/// <param name="PermissionMode">The permission mode its sessions start in; null for the one of their provider.</param>
/// <param name="ReviewBeforeCommit">Whether the user reviews the changes of its sessions before a commit; null follows the setting.</param>
/// <param name="Sandbox">The sandbox its sessions run in, a profile or <c>off</c>; null for what the settings give.</param>
internal sealed record AutomationInput(string? Id, string Name, bool Enabled, string Prompt, string? ProjectId, string? Provider, string? Model,
    string? Effort, string? Agent, string? PermissionMode, bool? ReviewBeforeCommit, string? Sandbox, bool CatchUp, IReadOnlyList<AutomationTriggerItem> Triggers)
{
    /// <summary>The script of the library it runs in place of its prompt; null to send its prompt.</summary>
    public string? Script { get; init; }

    /// <summary>The arguments of its script, a JSON object; null for none.</summary>
    public string? ScriptArguments { get; init; }

    /// <summary>The options of its sessions; null for the defaults, every option on.</summary>
    public SessionOptionsChoice? Options { get; init; }

    /// <summary>The paired machine it runs on (its device id); null to run it on this computer. Only for an automation of the user.</summary>
    public string? RunsOn { get; init; }

    /// <summary>Whom it is published to on the server of its machine: <c>team:&lt;id&gt;</c>, <c>organization</c>, or null for the person's own.</summary>
    public string? PublishTo { get; init; }

    /// <summary>What its runs may do without a person; null for an automation that runs as before.</summary>
    public AutomationAutonomyItem? Autonomy { get; init; }
}

/// <param name="StoreProjectId">The project whose configuration holds it; null for the configuration of the user.</param>
internal sealed record AutomationSaveRequest(string? ExpectedEpoch, AutomationInput Automation, string? StoreProjectId);

internal sealed record AutomationIdRequest(string? ExpectedEpoch, string Id);

internal sealed record AutomationEnableRequest(string? ExpectedEpoch, string Id, bool Enabled);

internal sealed record AutomationPauseRequest(string? ExpectedEpoch, bool Paused);

internal sealed record AutomationRunsRequest(string? ExpectedEpoch, string? Id, int? Limit);

internal sealed record AutomationPreviewRequest(string? ExpectedEpoch, AutomationTriggerItem Trigger);

/// <param name="Id">The automation that was written or removed.</param>
/// <param name="Message">Why the request was refused.</param>
internal sealed record AutomationMutationResponse(string Status, string? Id, string? Message);

internal sealed record AutomationRunResponse(string Status, AutomationRunItem? Run);

internal sealed record AutomationRunsResponse(string Status, IReadOnlyList<AutomationRunItem> Runs);

internal sealed record AutomationPreviewResponse(string Status, string? Message, IReadOnlyList<DateTimeOffset> Times);

internal sealed record AutomationsEvent(int Revision);
