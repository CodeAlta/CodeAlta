using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Catalog.WorkItems;
using CodeAlta.Desktop.WorkItems;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the tasks and the plans of the projects: it lists them, reads the text of one, changes
/// one (later, done, dismissed, removed, the status of a plan), starts the work of one in a new session, reads
/// and saves the user's choices, and is told when anything of this changes.
/// </summary>
/// <remarks>
/// Work that starts in the session that shows the item goes through the composer of that session, as a prompt
/// the user sends: the page asks for the prompt (<c>start_here</c>) and sends it.
/// </remarks>
[NeoRpcService("workItems", Version = 1)]
internal sealed class WorkItemsService
{
    /// <summary>The most characters of the text of an item one answer holds.</summary>
    internal const int MaximumMarkdown = 200 * 1024;

    /// <summary>The most tasks, and the most plans, of one project in an answer.</summary>
    internal const int MaximumRows = 100;

    /// <summary>The most projects one request names.</summary>
    internal const int MaximumProjects = 32;

    private readonly WorkItemService? _items;
    private readonly ProjectCatalog? _projects;
    private readonly IWorkItemRunner? _runner;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal WorkItemsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="items">The tasks and the plans.</param>
    /// <param name="projects">The host's project catalog.</param>
    /// <param name="runner">What starts the work of an item in a new session.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    internal WorkItemsService(WorkItemService items, ProjectCatalog projects, IWorkItemRunner runner, string epoch)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_items, _projects, _runner, _epoch) = (items, projects, runner, epoch);
    }

    /// <summary>
    /// The tasks and the plans of the projects that have some, and the user's choices. A request that names
    /// projects is answered for those only: the page asks for a few at a time, the ones used last first, so
    /// that what it shows first does not wait for the files of every project.
    /// </summary>
    [NeoRpcMethod("list")]
    public async Task<WorkItemsListResponse> ListAsync(WorkItemsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, [], null);
        if (request.ProjectIds is { } named && (named.Count > MaximumProjects || named.Any(static id => !Identifier(id)))) return new("invalid_request", [], null);
        IReadOnlyList<ProjectDescriptor> projects;
        try
        {
            projects = await _projects!.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return new("read_failed", [], null);
        }

        var links = _items!.Links.ToDictionary(static link => (link.ProjectId, link.Kind, link.Id));
        var rows = new List<WorkItemsProject>();
        foreach (var project in request.ProjectIds is { } wanted ? wanted.Select(id => projects.FirstOrDefault(project => project.Id == id)).OfType<ProjectDescriptor>() : projects)
        {
            if (project.Archived) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = _items.ListTasks(project);
            var plans = _items.ListPlans(project);
            if (tasks.Count == 0 && plans.Count == 0) continue;
            rows.Add(new(project.Id,
                [.. tasks.Take(MaximumRows).Select(task => Row(task, links.GetValueOrDefault((project.Id, WorkItemKinds.Task, task.Id))))],
                [.. plans.Take(MaximumRows).Select(plan => Row(plan, links.GetValueOrDefault((project.Id, WorkItemKinds.Plan, plan.Id))))],
                tasks.Count > MaximumRows || plans.Count > MaximumRows));
        }

        return new("ok", rows, Settings(_items.Settings));
    }

    /// <summary>The Markdown of one item, without its front matter.</summary>
    [NeoRpcMethod("read")]
    public async Task<WorkItemReadResponse> ReadAsync(WorkItemRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, false);
        if (!Valid(request)) return new("invalid_request", null, false);
        if (await ProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found", null, false);
        return _items!.ReadMarkdown(project, request.Kind, request.Id, MaximumMarkdown) is { } text
            ? new("ok", text.Markdown, text.Truncated)
            : new("not_found", null, false);
    }

    /// <summary>Does one thing to an item.</summary>
    /// <remarks>
    /// <c>later</c>, <c>reopen</c>, <c>complete</c> and <c>dismiss</c> are for tasks; <c>status</c> (with
    /// <see cref="WorkItemActionRequest.Value"/>) is for plans; <c>remove</c> deletes the file of either;
    /// <c>acknowledge</c> puts a card away; <c>release</c> forgets the session that carries the item out.
    /// <c>start_worktree</c> and <c>start_session</c> create a session and send it the item;
    /// <c>start_here</c> answers the prompt for the session named by the request, which then carries it out.
    /// </remarks>
    [NeoRpcMethod("act", TimeoutMilliseconds = 600_000)]
    public async Task<WorkItemActionResponse> ActAsync(WorkItemActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (!Valid(request.ProjectId, request.Kind, request.Id) || request.Action is not { Length: > 0 and <= 32 }
            || request.SessionId is not null && !Identifier(request.SessionId)) return new("invalid_request");
        if (await ProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found");
        var task = request.Kind == WorkItemKinds.Task;
        var exists = task ? _items!.GetTask(project, request.Id) is not null : _items!.GetPlan(project, request.Id) is not null;
        if (!exists) return new("not_found");

        switch (request.Action)
        {
            case "later" when task: return Done(_items.SetTaskStatus(project, request.Id, WorkTaskStatus.Later));
            case "reopen" when task: return Done(_items.SetTaskStatus(project, request.Id, WorkTaskStatus.Pending));
            case "complete" when task: return Done(_items.CompleteTask(project, request.Id));
            case "dismiss" when task: return Done(_items.DismissTask(project, request.Id));
            case "remove": return Done(task ? _items.RemoveTask(project, request.Id) : _items.RemovePlan(project, request.Id));
            case "status" when !task:
                return WorkItemFiles.PlanStatusOf(request.Value) is { } status ? Done(_items.SetPlanStatus(project, request.Id, status)) : new("invalid_request");
            case "acknowledge":
                _items.Acknowledge(project.Id, request.Kind, request.Id);
                return new("ok");
            case "release":
                _items.SetRunner(project.Id, request.Kind, request.Id, null);
                return new("ok");
            case "start_worktree" or "start_session":
            {
                // The session is created whether or not the page still waits: it is told when it lists again.
                var started = await _runner!.StartAsync(project, request.Kind, request.Id, request.Action == "start_worktree", request.SessionId).ConfigureAwait(false);
                return started.Problem is null ? new("ok") { SessionId = started.SessionId }
                    : new("refused") { SessionId = started.SessionId, Message = started.Problem, Reason = started.Reason };
            }
            case "start_here":
            {
                if (request.SessionId is null) return new("invalid_request");
                var prompt = task
                    ? WorkItemPrompts.ForTask(_items.GetTask(project, request.Id)!)
                    : WorkItemPrompts.ForPlan(_items.GetPlan(project, request.Id)!, project.ProjectPath, request.WorkingDirectory is { Length: > 0 } folder && Path.IsPathFullyQualified(folder) ? folder : project.ProjectPath);
                _items.SetRunner(project.Id, request.Kind, request.Id, request.SessionId);
                // A plan is carried out by the default agent, whatever the session was planning with.
                return new("ok") { SessionId = request.SessionId, Prompt = prompt, AgentPromptId = task ? null : "default" };
            }
            default: return new("invalid_request");
        }

        static WorkItemActionResponse Done(bool changed) => new(changed ? "ok" : "write_failed");
    }

    /// <summary>Saves the user's choices for tasks and plans.</summary>
    [NeoRpcMethod("saveSettings")]
    public Task<WorkItemsSettingsResponse> SaveSettingsAsync(WorkItemsSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return Task.FromResult(new WorkItemsSettingsResponse(refused, null));
        if (request.Settings is not { } wanted || WorkItemSettings.ParseClosing(wanted.CompletedTasks) is not { } completed
            || WorkItemSettings.ParseClosing(wanted.DismissedTasks) is not { } dismissed || WorkItemSettings.ParseClosing(wanted.CompletedPlans) is not { } plans
            || WorkItemSettings.ParseStart(wanted.Start) is not { } start)
            return Task.FromResult(new WorkItemsSettingsResponse("invalid_request", null));
        try
        {
            _items!.SaveSettings(new WorkItemSettings(wanted.Propose, wanted.Notify, completed, dismissed, plans, start));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult(new WorkItemsSettingsResponse("write_failed", Settings(_items!.Settings)));
        }

        return Task.FromResult(new WorkItemsSettingsResponse("ok", Settings(_items.Settings)));
    }

    /// <summary>Tells when tasks, plans or the choices changed through the application: the page then lists again.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<WorkItemsEvent> Watch(WorkItemsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.WorkItemsEvent);
    }

    internal async IAsyncEnumerable<WorkItemsEvent> WatchAsync(WorkItemsRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null) yield break;
        // A page that does not read misses nothing: one pending notice says everything that changed.
        var notices = Channel.CreateBounded<int>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        void Notify() => notices.Writer.TryWrite(0);
        _items!.Changed += Notify;
        try
        {
            var revision = 0;
            yield return new WorkItemsEvent(revision);
            await foreach (var _ in notices.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return new WorkItemsEvent(++revision);
        }
        finally
        {
            _items.Changed -= Notify;
        }
    }

    private async Task<ProjectDescriptor?> ProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            var project = await _projects!.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
            return project is { Archived: false } ? project : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    private string? Refuse(string? expectedEpoch)
        => _items is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static bool Valid(WorkItemRequest request) => Valid(request.ProjectId, request.Kind, request.Id);

    private static bool Valid(string? projectId, string? kind, string? id)
        => Identifier(projectId) && kind is WorkItemKinds.Task or WorkItemKinds.Plan && WorkItemFiles.IsValidId(id);

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);

    private static WorkItemRow Row(WorkTask task, WorkItemLink? link)
        => new(task.Id, WorkItemKinds.Task, task.Title, task.Summary, task.Kind, WorkItemFiles.NameOf(task.Status), null, task.Created,
            WorkItemFiles.TasksFolder + "/" + task.Id + ".md", link?.ProposedBy, link?.Runner, link?.Acknowledged ?? false);

    private static WorkItemRow Row(WorkPlan plan, WorkItemLink? link)
        => new(plan.Id, WorkItemKinds.Plan, plan.Title, plan.Summary, null, WorkItemFiles.NameOf(plan.Status), plan.StatusText, plan.Created,
            WorkItemFiles.PlansFolder + "/" + plan.Id + ".md", link?.ProposedBy, link?.Runner, link?.Acknowledged ?? false);

    private static WorkItemsSettings Settings(WorkItemSettings settings)
        => new(settings.Propose, settings.Notify, WorkItemSettings.NameOf(settings.CompletedTasks), WorkItemSettings.NameOf(settings.DismissedTasks),
            WorkItemSettings.NameOf(settings.CompletedPlans), WorkItemSettings.NameOf(settings.Start));
}

/// <param name="ProjectIds">The projects to list, in the order of the answer; every project when null.</param>
internal sealed record WorkItemsRequest(string? ExpectedEpoch, IReadOnlyList<string>? ProjectIds = null);

/// <param name="Projects">The projects that have tasks or plans.</param>
/// <param name="Settings">The user's choices; null when the answer is not <c>ok</c>.</param>
internal sealed record WorkItemsListResponse(string Status, IReadOnlyList<WorkItemsProject> Projects, WorkItemsSettings? Settings);

/// <param name="Truncated">The project has more tasks or plans than the answer holds.</param>
internal sealed record WorkItemsProject(string ProjectId, IReadOnlyList<WorkItemRow> Tasks, IReadOnlyList<WorkItemRow> Plans, bool Truncated);

/// <param name="Kind"><c>task</c> or <c>plan</c>.</param>
/// <param name="Category">Why a task exists: <c>gap</c>, <c>problem</c> or <c>improvement</c>; null for a plan.</param>
/// <param name="Status">As its file says: <c>pending</c>, <c>later</c>, <c>done</c>, <c>dismissed</c> for a task; <c>draft</c>, <c>approved</c>, <c>in-progress</c>, <c>done</c>, <c>blocked</c> for a plan.</param>
/// <param name="StatusText">What the file of a plan says of its status when that is more than the name of a status.</param>
/// <param name="File">The file, relative to the folder of the project.</param>
/// <param name="ProposedBy">The session that proposed the item, which shows it as a card.</param>
/// <param name="Runner">The session that carries the item out.</param>
/// <param name="Acknowledged">The user put the card of the item away.</param>
internal sealed record WorkItemRow(string Id, string Kind, string Title, string? Summary, string? Category, string Status, string? StatusText, string? Created,
    string File, string? ProposedBy, string? Runner, bool Acknowledged);

/// <param name="CompletedTasks"><c>delete</c> or <c>keep</c>.</param>
/// <param name="DismissedTasks"><c>delete</c> or <c>keep</c>.</param>
/// <param name="CompletedPlans"><c>delete</c> or <c>keep</c>.</param>
/// <param name="Start"><c>worktree</c>, <c>session</c> or <c>here</c>.</param>
internal sealed record WorkItemsSettings(bool Propose, bool Notify, string CompletedTasks, string DismissedTasks, string CompletedPlans, string Start);

internal sealed record WorkItemRequest(string? ExpectedEpoch, string ProjectId, string Kind, string Id);

internal sealed record WorkItemReadResponse(string Status, string? Markdown, bool Truncated);

/// <param name="Action">What to do: see <see cref="WorkItemsService.ActAsync"/>.</param>
/// <param name="Value">The status of a plan, for <c>status</c>.</param>
/// <param name="SessionId">The session that shows the item: the one that carries it out for <c>start_here</c>, the one a new session takes its model from otherwise.</param>
/// <param name="WorkingDirectory">The folder that session works in, for <c>start_here</c>.</param>
internal sealed record WorkItemActionRequest(string? ExpectedEpoch, string ProjectId, string Kind, string Id, string Action, string? Value = null,
    string? SessionId = null, string? WorkingDirectory = null);

/// <param name="Status"><c>ok</c>, <c>refused</c> (with a message), <c>not_found</c>, <c>write_failed</c>, <c>invalid_request</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
internal sealed record WorkItemActionResponse(string Status)
{
    /// <summary>Gets the session that carries the item out.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets why the work did not start.</summary>
    public string? Message { get; init; }

    /// <summary>Gets a short code of the refusal, such as <c>worktree_not_repository</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the prompt the page sends to the session, for <c>start_here</c>.</summary>
    public string? Prompt { get; init; }

    /// <summary>Gets the agent prompt the session takes for that send, when it is not its own.</summary>
    public string? AgentPromptId { get; init; }
}

internal sealed record WorkItemsSettingsRequest(string? ExpectedEpoch, WorkItemsSettings? Settings);

internal sealed record WorkItemsSettingsResponse(string Status, WorkItemsSettings? Settings);

internal sealed record WorkItemsEvent(int Revision);
