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
            var tags = _items.ListTags(project, tasks);
            rows.Add(new(project.Id,
                [.. tasks.Take(MaximumRows).Select(task => Row(task, links.GetValueOrDefault((project.Id, WorkItemKinds.Task, task.Id))))],
                [.. plans.Take(MaximumRows).Select(plan => Row(plan, links.GetValueOrDefault((project.Id, WorkItemKinds.Plan, plan.Id))))],
                tasks.Count > MaximumRows || plans.Count > MaximumRows,
                [.. tags.Tags.Select(static tag => new WorkItemTagRow(tag.Name, tag.Description, tag.OpenTasks, tag.Listed))],
                tags.Declined,
                [.. _items.ListTagProposals(project.Id).Select(static proposal => new WorkItemTagProposalRow(proposal.Name, proposal.Description, proposal.NewTag, proposal.SessionId,
                    [.. proposal.Tasks.Select(static match => new WorkItemTagMatchRow(match.TaskId, match.Reason))]))]));
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
    /// <c>later</c>, <c>reopen</c>, <c>complete</c>, <c>dismiss</c> and <c>tags</c> (the tags, separated by commas, in
    /// <see cref="WorkItemActionRequest.Value"/>) are for tasks; <c>status</c> (with
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
            || request.SessionId is not null && !Identifier(request.SessionId)
            || request.ProviderId is not null && !Identifier(request.ProviderId)
            || request.ModelId is not null && (request.ProviderId is null || !Identifier(request.ModelId))
            || request.ReasoningEffort is not null && (request.ModelId is null || !Identifier(request.ReasoningEffort))
            || request.PermissionMode is not null && !Identifier(request.PermissionMode) || request.Sandbox is not null && !Identifier(request.Sandbox)
            || request.AgentPromptId is not null && !Identifier(request.AgentPromptId)
            // A tier gives the model: the request names one or the other.
            || request.Tier is not null && (request.ProviderId is not null || SessionStartTier.Parse(request.Tier) is null)) return new("invalid_request");
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
            case "tags" when task:
            {
                // The tags of the task, separated by commas; none removes them.
                var tags = (request.Value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (tags.Length > WorkItemFiles.MaximumTaskTags || tags.Any(static tag => !WorkItemFiles.IsTagName(tag))) return new("invalid_request");
                return Done(_items.SetTaskTags(project, request.Id, tags));
            }
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
                var asked = request.ProviderId is null ? null : new WorkItemSelection(request.ProviderId, request.ModelId, request.ReasoningEffort);
                var settings = SessionStartSettings.Of(request.AgentPromptId, request.PermissionMode, request.ReviewBeforeCommit, request.Sandbox,
                    SessionOptionsChoice.Record(request.Options), SessionStartTier.Parse(request.Tier));
                var started = await _runner!.StartAsync(project, request.Kind, request.Id, request.Action == "start_worktree", request.SessionId, asked, settings).ConfigureAwait(false);
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

    /// <summary>Changes the tags of a project, in its tags file and, for a rename or a deletion, in its tasks.</summary>
    /// <remarks>
    /// <c>set</c> adds a tag or changes its description (<see cref="WorkItemTagRequest.Value"/>); <c>rename</c> gives it
    /// the name in <see cref="WorkItemTagRequest.Value"/>; <c>delete</c> removes it; <c>decline</c> records that the user
    /// turned down a name an agent proposed, and <c>forget</c> forgets it. <c>find</c> starts a session that finds the open
    /// tasks the tag fits, which it proposes; <c>apply</c> applies a proposal to <see cref="WorkItemTagRequest.Tasks"/> (with the
    /// description of a new tag in <see cref="WorkItemTagRequest.Value"/>); <c>dismiss</c> dismisses it, and declines a new tag.
    /// </remarks>
    [NeoRpcMethod("tag")]
    public async Task<WorkItemActionResponse> TagAsync(WorkItemTagRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (!Identifier(request.ProjectId) || !WorkItemFiles.IsTagName(request.Name) || request.Value is { Length: > WorkItemFiles.MaximumTagDescription }
            || request.Action is "rename" && !WorkItemFiles.IsTagName(request.Value)
            || request.Tasks is { } named && (named.Count > MaximumRows || named.Any(static id => !WorkItemFiles.IsValidId(id)))) return new("invalid_request");
        if (await ProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found");
        if (request.Action == "find")
        {
            var tag = _items!.ListTags(project).Tags.FirstOrDefault(tag => tag.Name == request.Name);
            if (tag is null) return new("not_found");
            var started = await _runner!.FindTagTasksAsync(project, tag.Name, tag.Description).ConfigureAwait(false);
            return started.Problem is null ? new("ok") { SessionId = started.SessionId }
                : new("refused") { SessionId = started.SessionId, Message = started.Problem, Reason = started.Reason };
        }

        WorkTagChange? change = request.Action switch
        {
            "apply" => _items!.ApplyTagProposal(project, request.Name, request.Tasks ?? [], request.Value),
            "dismiss" => _items!.DismissTagProposal(project, request.Name),
            "set" => _items!.SetTag(project, request.Name, request.Value),
            "rename" => _items!.RenameTag(project, request.Name, request.Value!),
            "delete" => _items!.DeleteTag(project, request.Name),
            "decline" => _items!.SetTagDeclined(project, request.Name, declined: true),
            "forget" => _items!.SetTagDeclined(project, request.Name, declined: false),
            _ => null,
        };
        return change switch
        {
            WorkTagChange.Done => new("ok"),
            WorkTagChange.NotFound => new("not_found"),
            WorkTagChange.Exists => new("exists"),
            WorkTagChange.Failed => new("write_failed"),
            _ => new("invalid_request"),
        };
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
            WorkItemFiles.TasksFolder + "/" + task.Id + ".md", link?.ProposedBy, link?.Runner, link?.Acknowledged ?? false, RunsWith(link), task.ShortId, Updated(task.Path), task.Tags);

    private static WorkItemRow Row(WorkPlan plan, WorkItemLink? link)
        => new(plan.Id, WorkItemKinds.Plan, plan.Title, plan.Summary, null, WorkItemFiles.NameOf(plan.Status), plan.StatusText, plan.Created,
            WorkItemFiles.PlansFolder + "/" + plan.Id + ".md", link?.ProposedBy, link?.Runner, link?.Acknowledged ?? false, RunsWith(link), Updated: Updated(plan.Path));

    // When the file of an item was last written; null when it cannot be read.
    private static DateTimeOffset? Updated(string path)
    {
        try { return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static WorkItemRunsWith? RunsWith(WorkItemLink? link)
        => link?.RunsWith is { } selection ? new(selection.ProviderKey, selection.ModelId, selection.ReasoningEffort) : null;

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
/// <param name="Tags">The tags of the tasks of the project: those of its tags file, then those only found on its tasks.</param>
/// <param name="DeclinedTags">The names the user declined when an agent proposed them.</param>
/// <param name="TagProposals">The tag proposals of the project that wait for the user.</param>
internal sealed record WorkItemsProject(string ProjectId, IReadOnlyList<WorkItemRow> Tasks, IReadOnlyList<WorkItemRow> Plans, bool Truncated,
    IReadOnlyList<WorkItemTagRow>? Tags = null, IReadOnlyList<string>? DeclinedTags = null, IReadOnlyList<WorkItemTagProposalRow>? TagProposals = null);

/// <param name="Name">The name of the tag.</param>
/// <param name="Description">What a new tag is for; null for an existing one.</param>
/// <param name="NewTag">Whether the tag does not exist yet.</param>
/// <param name="SessionId">The session that proposed it, whose card shows a new tag; null when it is not known.</param>
/// <param name="Tasks">The tasks the tag would be given to.</param>
internal sealed record WorkItemTagProposalRow(string Name, string? Description, bool NewTag, string? SessionId, IReadOnlyList<WorkItemTagMatchRow> Tasks);

/// <param name="Id">The id of the task.</param>
/// <param name="Reason">Why the tag fits it; null when the agent did not say.</param>
internal sealed record WorkItemTagMatchRow(string Id, string? Reason);

/// <param name="Description">What the tag is for, in a line; null when it has none.</param>
/// <param name="OpenTasks">How many tasks that are not closed have the tag.</param>
/// <param name="Listed">Whether the tags file of the project lists the tag.</param>
internal sealed record WorkItemTagRow(string Name, string? Description, int OpenTasks, bool Listed);

/// <param name="Action"><c>set</c>, <c>rename</c>, <c>delete</c>, <c>decline</c>, <c>forget</c>, <c>find</c>, <c>apply</c> or <c>dismiss</c>: see <see cref="WorkItemsService.TagAsync"/>.</param>
/// <param name="Name">The name of the tag.</param>
/// <param name="Value">The description for <c>set</c> and for <c>apply</c> of a new tag (null or empty for none), the new name for <c>rename</c>.</param>
/// <param name="Tasks">The tasks a proposal is applied to, by id, for <c>apply</c>.</param>
internal sealed record WorkItemTagRequest(string? ExpectedEpoch, string ProjectId, string Action, string Name, string? Value = null, IReadOnlyList<string>? Tasks = null);

/// <param name="Kind"><c>task</c> or <c>plan</c>.</param>
/// <param name="Category">Why a task exists: <c>gap</c>, <c>problem</c> or <c>improvement</c>; null for a plan.</param>
/// <param name="Status">As its file says: <c>pending</c>, <c>later</c>, <c>done</c>, <c>dismissed</c> for a task; <c>draft</c>, <c>approved</c>, <c>in-progress</c>, <c>done</c>, <c>blocked</c> for a plan.</param>
/// <param name="StatusText">What the file of a plan says of its status when that is more than the name of a status.</param>
/// <param name="File">The file, relative to the folder of the project.</param>
/// <param name="ProposedBy">The session that proposed the item, which shows it as a card.</param>
/// <param name="Runner">The session that carries the item out.</param>
/// <param name="Acknowledged">The user put the card of the item away.</param>
/// <param name="RunsWith">What the session that proposed the item ran with, which a session started for it takes; null when it is not known.</param>
/// <param name="ShortId">The short id of a task, which agents name it by; null for a plan and for a task written before ids had one.</param>
/// <param name="Updated">When the file of the item was last written; null when it is not known.</param>
/// <param name="Tags">The tags of a task; null for a plan.</param>
internal sealed record WorkItemRow(string Id, string Kind, string Title, string? Summary, string? Category, string Status, string? StatusText, string? Created,
    string File, string? ProposedBy, string? Runner, bool Acknowledged, WorkItemRunsWith? RunsWith = null, string? ShortId = null, DateTimeOffset? Updated = null,
    IReadOnlyList<string>? Tags = null);

/// <param name="ProviderId">The key of the model provider.</param>
/// <param name="ModelId">The model; null leaves it to the provider.</param>
/// <param name="ReasoningEffort">The name of the reasoning effort, in lower case; null leaves it to the model.</param>
internal sealed record WorkItemRunsWith(string ProviderId, string? ModelId, string? ReasoningEffort);

/// <param name="CompletedTasks"><c>delete</c> or <c>keep</c>.</param>
/// <param name="DismissedTasks"><c>delete</c> or <c>keep</c>.</param>
/// <param name="CompletedPlans"><c>delete</c> or <c>keep</c>.</param>
/// <param name="Start"><c>worktree</c>, <c>session</c> or <c>here</c>.</param>
internal sealed record WorkItemsSettings(bool Propose, bool Notify, string CompletedTasks, string DismissedTasks, string CompletedPlans, string Start);

internal sealed record WorkItemRequest(string? ExpectedEpoch, string ProjectId, string Kind, string Id);

internal sealed record WorkItemReadResponse(string Status, string? Markdown, bool Truncated);

/// <param name="Action">What to do: see <see cref="WorkItemsService.ActAsync"/>.</param>
/// <param name="Value">The status of a plan, for <c>status</c>; the tags of a task separated by commas, for <c>tags</c>.</param>
/// <param name="SessionId">The session that shows the item: the one that carries it out for <c>start_here</c>, the one a new session takes its model from otherwise.</param>
/// <param name="WorkingDirectory">The folder that session works in, for <c>start_here</c>.</param>
/// <param name="ProviderId">The provider the user chose for a new session; null to take what the item or the showing session says, then the default.</param>
/// <param name="ModelId">The model the user chose, of that provider; null leaves it to the provider.</param>
/// <param name="ReasoningEffort">The reasoning effort the user chose, for that model; null leaves it to the model.</param>
/// <param name="AgentPromptId">The agent prompt the user chose for a new session; null for the default one of its space.</param>
/// <param name="PermissionMode">The permission mode the user chose for a new session; null for the one of its provider.</param>
/// <param name="ReviewBeforeCommit">Whether the user reviews the changes of a new session before a commit; null follows the setting.</param>
/// <param name="Sandbox">The sandbox of a new session, a profile or <c>off</c>; null for what the settings give.</param>
/// <param name="Options">The options of a new session, such as whether it can start sessions of its own; null for the defaults.</param>
/// <param name="Tier">
/// The tier of models a new session starts and runs on (<c>complex</c>, <c>standard</c>, <c>quick</c>), or Adaptive (<c>adaptive</c>,
/// <c>adaptive:&lt;level&gt;</c>), which gives its model; null for the model of <paramref name="ProviderId"/>, else the tier and the model of the showing session.
/// </param>
internal sealed record WorkItemActionRequest(string? ExpectedEpoch, string ProjectId, string Kind, string Id, string Action, string? Value = null,
    string? SessionId = null, string? WorkingDirectory = null, string? ProviderId = null, string? ModelId = null, string? ReasoningEffort = null,
    string? AgentPromptId = null, string? PermissionMode = null, bool? ReviewBeforeCommit = null, string? Sandbox = null, SessionOptionsChoice? Options = null,
    string? Tier = null);

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
