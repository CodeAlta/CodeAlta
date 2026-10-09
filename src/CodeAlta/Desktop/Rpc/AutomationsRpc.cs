using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Automations;
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
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal AutomationsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="automations">The automations of the application.</param>
    /// <param name="projects">The host's project catalog, for the names of the projects.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    internal AutomationsService(AutomationService automations, ProjectCatalog projects, string epoch)
    {
        ArgumentNullException.ThrowIfNull(automations);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_automations, _projects, _epoch) = (automations, projects, epoch);
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
        return run is null ? new("not_found", null) : new("ok", Run(run));
    }

    /// <summary>The runs of one automation, or of all, newest first.</summary>
    [NeoRpcMethod("runs")]
    public AutomationRunsResponse Runs(AutomationRunsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        if (request.Id is { } id && !AutomationDefinition.IsId(id)) return new("invalid_request", []);
        return new("ok", [.. _automations!.Runs(request.Id, Math.Clamp(request.Limit ?? MaximumRuns, 1, MaximumRuns)).Select(Run)]);
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
        try
        {
            var revision = 0;
            yield return new AutomationsEvent(revision);
            await foreach (var _ in notices.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return new AutomationsEvent(++revision);
        }
        finally
        {
            _automations.Changed -= Notify;
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

        var items = snapshot.Entries.Select(entry =>
        {
            var definition = entry.Definition;
            var project = entry.ProjectId is null ? null : projects.FirstOrDefault(candidate => candidate.Id == entry.ProjectId);
            var runs = _automations.Runs(entry.Id, 1);
            return new AutomationItem(entry.Id, definition.Name, definition.Enabled, definition.Prompt, entry.ProjectId, project?.DisplayName,
                entry.ProjectPath ?? definition.Project, entry.Source.ProjectId, entry.Source.FilePath, definition.Model.Provider, definition.Model.Model,
                definition.Model.Effort?.ToString(), definition.Agent, definition.CatchUp, [.. definition.Triggers.Select(Trigger)], entry.Problem,
                _automations.NextDue(entry.Id), _automations.IsRunning(entry.Id), runs.Count > 0 ? Run(runs[0]) : null,
                _automations.Repository(entry.Id), definition.Enabled ? _automations.WatchProblem(entry.Id) : null, _automations.IsAllowed(entry));
        }).ToArray();
        return new(status, _automations.Paused, snapshot.Scanned, items,
            [.. snapshot.Faults.Select(static fault => new AutomationFaultItem(fault.Source.FilePath, fault.Source.ProjectId, fault.Key, fault.Message))],
            [.. _automations.Runs(null, RecentRuns).Select(Run)],
            [.. _automations.Upcoming(TimeSpan.FromHours(24), UpcomingSlots, MaximumUpcoming).Select(static time => new AutomationUpcomingItem(time.Id, time.At))]);
    }

    private string? Refuse(string? expectedEpoch)
        => _automations is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);

    private static AutomationRunItem Run(AutomationRun run)
        => new(run.Id, run.AutomationId, run.Name, run.SessionId, run.ProjectId, run.StartedAt, run.EndedAt, run.Trigger, run.Detail, run.Status, run.Message);

    private static AutomationTriggerItem Trigger(AutomationTrigger trigger)
        => new(trigger.KindName, trigger.Minute, trigger.Every, [.. trigger.At.Select(static time => time.ToString())],
            [.. trigger.Days.Select(static day => AutomationTrigger.DayNames[(int)day])], trigger.Expression, trigger.Event,
            trigger.Authors == AutomationAuthors.Anyone ? "anyone" : "trusted", trigger.Command, trigger.Folder);

    // False: the request is not one the page sends. A problem: what the user wrote is refused, with the reason.
    private static bool TryRead(AutomationInput input, out AutomationDefinition? definition, out string? problem)
    {
        (definition, problem) = (null, null);
        if (input.Name is null || input.Prompt is null || input.Triggers is null || input.Triggers.Count > AutomationDefinition.MaximumTriggers
            || input.Id is { } id && !AutomationDefinition.IsId(id) || input.ProjectId is { } project && !Identifier(project)
            || input.Name.Length > 4096 || input.Prompt.Length > AutomationDefinition.MaximumPromptLength * 2) return false;
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
            Prompt = input.Prompt,
            Model = new(provider, model, effort),
            Agent = Blank(input.Agent),
            CatchUp = input.CatchUp,
            Triggers = triggers,
        };
        problem = AutomationConfig.Validate(definition with { Prompt = input.Prompt.ReplaceLineEndings("\n").Trim() });
        return true;
    }

    private static bool TryRead(AutomationTriggerItem item, out AutomationTrigger? trigger)
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
        trigger = new AutomationTrigger(kind)
        {
            Minute = item.Minute,
            Every = item.Every,
            At = times,
            Days = days,
            Expression = kind == AutomationTriggerKind.Cron ? string.Join(' ', (item.Expression ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null,
            Event = item.Event is "updated" ? "updated" : "opened",
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
/// <param name="Event">Issue and pull request: <c>opened</c> or <c>updated</c>.</param>
/// <param name="Authors">Issue and pull request: <c>trusted</c> or <c>anyone</c>.</param>
/// <param name="Command">Command: the command line the automation keeps running.</param>
/// <param name="Folder">Command: the folder it runs in, from the folder of the project; null for that folder.</param>
internal sealed record AutomationTriggerItem(string Type, int Minute, int Every, IReadOnlyList<string> At, IReadOnlyList<string> Days, string? Expression, string Event, string Authors,
    string? Command, string? Folder);

/// <summary>An automation as the page shows it.</summary>
/// <param name="ProjectId">The project it runs in; null for a chat.</param>
/// <param name="ProjectFolder">The folder it runs in, or the folder it names when that is not a project.</param>
/// <param name="StoreProjectId">The project whose configuration defines it; null for the configuration of the user.</param>
/// <param name="File">The configuration file that defines it.</param>
/// <param name="Problem">Why it cannot run as defined.</param>
/// <param name="NextRunAt">When a schedule of it is next due.</param>
/// <param name="LastRun">Its latest run.</param>
/// <param name="Repository">The repository its event triggers watch, as <c>owner/name</c>.</param>
/// <param name="WatchProblem">Why its event triggers see nothing of that repository, or what is wrong with the command of a trigger.</param>
/// <param name="Allowed">
/// Whether its triggers may start it: false for an automation kept with a project that the user has not allowed as it is now.
/// </param>
internal sealed record AutomationItem(string Id, string Name, bool Enabled, string Prompt, string? ProjectId, string? ProjectName, string? ProjectFolder,
    string? StoreProjectId, string File, string? Provider, string? Model, string? Effort, string? Agent, bool CatchUp,
    IReadOnlyList<AutomationTriggerItem> Triggers, string? Problem, DateTimeOffset? NextRunAt, bool Running, AutomationRunItem? LastRun,
    string? Repository, string? WatchProblem, bool Allowed);

/// <summary>One run of an automation.</summary>
/// <param name="Trigger"><c>manual</c>, or the type of the trigger that started it.</param>
/// <param name="Status"><c>running</c>, <c>completed</c>, <c>failed</c>, <c>cancelled</c>, <c>interrupted</c> or <c>skipped</c>.</param>
internal sealed record AutomationRunItem(string Id, string AutomationId, string? Name, string? SessionId, string? ProjectId, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string Trigger, string? Detail, string Status, string? Message);

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
internal sealed record AutomationInput(string? Id, string Name, bool Enabled, string Prompt, string? ProjectId, string? Provider, string? Model,
    string? Effort, string? Agent, bool CatchUp, IReadOnlyList<AutomationTriggerItem> Triggers);

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
