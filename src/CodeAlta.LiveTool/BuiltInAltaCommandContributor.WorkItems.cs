using CodeAlta.Catalog;
using CodeAlta.Catalog.WorkItems;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta task` and `alta plan` commands: the follow-up tasks and the plans of a project, which are files
// under its `.alta/tasks/` and `.alta/plans/` folders.
internal sealed partial class BuiltInAltaCommandContributor
{
    /// <summary>The most characters of the Markdown one <c>show</c> returns.</summary>
    internal const int MaximumWorkItemMarkdown = 48 * 1024;

    private static readonly AltaCommandPolicy[] WorkItemPolicies =
    [
        Read("task list"),
        Read("task show"),
        Mutating("task create"),
        Mutating("task start"),
        Mutating("task complete"),
        Mutating("task later"),
        Mutating("task reopen"),
        Mutating("task dismiss"),
        Mutating("task remove"),
        Read("plan list"),
        Read("plan show"),
        Mutating("plan status"),
        Mutating("plan remove"),
    ];

    private static Command CreateTaskCommand(AltaCommandContext context)
    {
        var group = Group("task", "Use the follow-up tasks of a project: pieces of work proposed to the user, kept as files under `.alta/tasks/`.");
        group.Add(CreateTaskListCommand(context));
        group.Add(CreateTaskShowCommand(context));
        group.Add(CreateTaskCreateCommand(context));
        group.Add(CreateTaskChangeCommand(context, "start", "Say that this session now does a task. It then shows as in progress, with this session."));
        group.Add(CreateTaskChangeCommand(context, "complete", "Mark a task as done, once the work is implemented and verified."));
        group.Add(CreateTaskChangeCommand(context, "later", "Set a task aside for later: it stays, out of the list of what waits for a decision."));
        group.Add(CreateTaskChangeCommand(context, "reopen", "Put a task back among the ones that wait for a decision."));
        group.Add(CreateTaskChangeCommand(context, "dismiss", "Dismiss a task that is not worth doing."));
        group.Add(CreateTaskChangeCommand(context, "remove", "Delete the file of a task."));
        AddHelpText(
            group,
            "A task is one specific piece of work, separate from what the session was asked: a real gap, a problem or an improvement found on the way. The user sees a proposed task at once, as a card in the session that proposed it, and decides: start it in a new worktree, in this session or in a new session, keep it for later, or dismiss it.",
            "Propose a task only for something concrete that you verified and that is outside the current request. What belongs to the current request is done, not proposed. Look at `alta task list` first: do not propose what is already there. A session has at most a few open proposals.",
            "A task is not a plan: a plan (`alta plan`) describes how a larger piece of work will be done, for review before it starts.",
            "The commands use the project of the calling session; `--project` names another one. A completed or dismissed task is deleted, or kept when the user chose so in the settings.",
            "Examples: `alta task list`; `alta task create --title \"Make the locator test pass on Windows\" --kind problem --summary \"...\" --stdin`; `alta task complete <task-id>`.");
        return group;
    }

    private static Command CreateTaskListCommand(AltaCommandContext context)
    {
        string? project = null;
        string? status = null;
        var all = false;
        var command = Leaf("list", "List the tasks of a project: id, title, kind, status, and the session that does it.");
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session, then to the cwd.", value => project = value);
        command.Add("all", "The tasks of every project.", value => all = value is not null);
        command.Add("status=", "`open` (default: pending, later and in progress), `pending`, `later`, `in-progress`, `done`, `dismissed` or `all`.", value => status = value);
        command.Add(async (_, _) => await HandleWorkItemListAsync(context, WorkItemKinds.Task, project, all, status).ConfigureAwait(false));
        return command;
    }

    private static Command CreateTaskShowCommand(AltaCommandContext context)
    {
        string? id = null;
        string? project = null;
        var command = Leaf("show", "Show one task with its description.");
        command.Add("<task-id>", "Task id from `alta task list`.", value => id = value);
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => project = value);
        command.Add(async (_, _) => await HandleWorkItemShowAsync(context, WorkItemKinds.Task, project, id).ConfigureAwait(false));
        return command;
    }

    private static Command CreateTaskCreateCommand(AltaCommandContext context)
    {
        var options = new TaskCreateOptions();
        var command = Leaf("create", "Propose a follow-up task to the user. It is written under `.alta/tasks/` and shown at once in the session.");
        command.Add("title=", "What the task is, as an action in one line. Required.", value => options.Title = value);
        command.Add("kind=", "`gap` (something missing), `problem` (something wrong) or `improvement` (default).", value => options.Kind = value);
        command.Add("summary=", "One or two sentences for the card: what and why, without the details.", value => options.Summary = value);
        command.Add("content=", "The description. Prefer --stdin for several lines.", value => options.Content = value);
        command.Add("stdin", "Read the description from stdin.", value => options.UseStdin = value is not null);
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => options.Project = value);
        command.Add(async (_, _) => await HandleTaskCreateAsync(context, options).ConfigureAwait(false));
        AddHelpText(
            command,
            "Write the description in Markdown for someone who was not there: `## Why` (what you saw, with the file and line or the command output that shows it), `## What to do` (the change, concretely) and `## Where` (the files or the area). Keep it short: a task is small enough to do in one sitting.",
            "Example: `alta task create --title \"Report the provider error in the model list\" --kind gap --summary \"The model list shows nothing when a provider fails; it should say why.\" --stdin`.");
        return command;
    }

    private static Command CreateTaskChangeCommand(AltaCommandContext context, string name, string description)
    {
        string? id = null;
        string? project = null;
        var command = Leaf(name, description);
        command.Add("<task-id>", "Task id from `alta task list`.", value => id = value);
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => project = value);
        command.Add(async (_, _) => await HandleTaskChangeAsync(context, name, project, id).ConfigureAwait(false));
        return command;
    }

    private static Command CreatePlanCommand(AltaCommandContext context)
    {
        var group = Group("plan", "Use the plans of a project: Markdown files under `.alta/plans/` that say how a piece of work will be done.");
        string? project = null;
        string? status = null;
        var all = false;
        var list = Leaf("list", "List the plans of a project: id, title, status, and the session that carries it out.");
        list.Add("project=", "Project id, slug or path. Defaults to the project of the calling session, then to the cwd.", value => project = value);
        list.Add("all", "The plans of every project.", value => all = value is not null);
        list.Add("status=", "`open` (default: every plan that is not done), `draft`, `approved`, `in-progress`, `blocked`, `done` or `all`.", value => status = value);
        list.Add(async (_, _) => await HandleWorkItemListAsync(context, WorkItemKinds.Plan, project, all, status).ConfigureAwait(false));
        group.Add(list);

        string? shownId = null;
        string? shownProject = null;
        var show = Leaf("show", "Show one plan with its text.");
        show.Add("<plan-id>", "Plan id from `alta plan list`: the name of its file without `.md`.", value => shownId = value);
        show.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => shownProject = value);
        show.Add(async (_, _) => await HandleWorkItemShowAsync(context, WorkItemKinds.Plan, shownProject, shownId).ConfigureAwait(false));
        group.Add(show);

        string? changedId = null;
        string? changedStatus = null;
        string? changedProject = null;
        var change = Leaf("status", "Set the status of a plan: `draft`, `approved`, `in-progress`, `done` or `blocked`.");
        change.Add("<plan-id>", "Plan id from `alta plan list`.", value => changedId = value);
        change.Add("<status>", "The new status.", value => changedStatus = value);
        change.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => changedProject = value);
        change.Add(async (_, _) => await HandlePlanStatusAsync(context, changedProject, changedId, changedStatus).ConfigureAwait(false));
        AddHelpText(
            change,
            "`approved` is set once the user approved the plan: the session then shows it as a card, and the user chooses where it is carried out (a new worktree, this session, a new session) or keeps it for later. Do not start the work yourself after setting it.",
            "`in-progress` says this session carries the plan out; `done` that everything in it is implemented and verified. A plan that is done is kept, or deleted when the user chose so in the settings: set `done` last, after the final update of the file.");
        group.Add(change);

        string? removedId = null;
        string? removedProject = null;
        var remove = Leaf("remove", "Delete the file of a plan.");
        remove.Add("<plan-id>", "Plan id from `alta plan list`.", value => removedId = value);
        remove.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => removedProject = value);
        remove.Add(async (_, _) => await HandlePlanRemoveAsync(context, removedProject, removedId).ConfigureAwait(false));
        AddHelpText(remove, "Remove only a plan the user asks you to remove.");
        group.Add(remove);

        AddHelpText(
            group,
            "A plan is written as a file, `.alta/plans/yyyy-mm-dd-<name>.md`, with a front matter (`title`, `status`, `created`, `summary`). These commands list the plans, read one, and change its status without rewriting the file.",
            "The id of a plan is the name of its file without `.md`. A session that works in a git worktree which has the plan reads and updates that copy.",
            "Examples: `alta plan list`; `alta plan show 2026-10-07-task-cards`; `alta plan status 2026-10-07-task-cards approved`.");
        return group;
    }

    private static bool TryGetWorkItems(AltaCommandContext context, out WorkItemService items)
    {
        if (context.Services.Get<WorkItemService>() is { } found)
        {
            items = found;
            return true;
        }

        items = null!;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'WorkItemService' is unavailable.");
        return false;
    }

    // The project a command works on: the one it names, else the one of the calling session, else the one of the cwd.
    private static async Task<(ProjectDescriptor? Project, int ExitCode)> ResolveWorkItemProjectAsync(AltaCommandContext context, string? projectRef)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return (null, AltaExitCodes.ServiceUnavailable);
        }

        var reference = NormalizeOptionalText(projectRef) ?? NormalizeOptionalText(context.Caller.SourceProjectId);
        var project = reference is not null
            ? await ResolveProjectAsync(catalog, reference, context, includeArchived: false).ConfigureAwait(false)
            : await catalog.GetByPathAsync(ResolvePath(context, context.Cwd ?? Environment.CurrentDirectory), context.CancellationToken).ConfigureAwait(false);
        return project is null || project.Archived
            ? (null, NotFound(context, "project.notFound", reference is null ? "No catalog project matches the current directory. Use --project." : $"Project '{reference}' was not found."))
            : (project, AltaExitCodes.Success);
    }

    private static async ValueTask<int> HandleWorkItemListAsync(AltaCommandContext context, string kind, string? projectRef, bool all, string? status)
    {
        var command = $"alta {kind} list";
        if (!TryGetWorkItems(context, out var items))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var filter = NormalizeOptionalText(status)?.ToLowerInvariant() ?? "open";
        var known = kind == WorkItemKinds.Task
            ? new[] { "open", "all", "pending", "later", "in-progress", "done", "dismissed" }
            : ["open", "all", "draft", "approved", "in-progress", "blocked", "done"];
        if (!known.Contains(filter))
        {
            return UsageError(context, "usage.invalidStatus", $"--status is one of: {string.Join(", ", known)}.", command);
        }

        IReadOnlyList<ProjectDescriptor> projects;
        if (all)
        {
            if (NormalizeOptionalText(projectRef) is not null)
            {
                return UsageError(context, "usage.scopeConflict", "Use either --project or --all, not both.", command);
            }

            if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
            {
                return AltaExitCodes.ServiceUnavailable;
            }

            projects = [.. (await catalog.LoadAsync(context.CancellationToken).ConfigureAwait(false)).Where(static project => !project.Archived)];
        }
        else
        {
            var (project, exitCode) = await ResolveWorkItemProjectAsync(context, projectRef).ConfigureAwait(false);
            if (project is null)
            {
                return exitCode;
            }

            projects = [project];
        }

        var count = 0;
        foreach (var project in projects)
        {
            if (kind == WorkItemKinds.Task)
            {
                foreach (var task in items.ListTasks(project))
                {
                    var link = items.GetLink(project.Id, kind, task.Id);
                    var shown = TaskStatusName(task, link);
                    if (filter == "all" || filter == shown || filter == "open" && shown is "pending" or "later" or "in-progress")
                    {
                        AltaJsonlWriter.WriteRecord(context.Stdout, TaskRecord(context, project, task, link, markdown: null));
                        count++;
                    }
                }
            }
            else
            {
                var worktree = all ? null : CallerWorktree(context, project);
                foreach (var plan in worktree is null ? items.ListPlans(project) : PlansOfCaller(items, project, worktree))
                {
                    var name = WorkItemFiles.NameOf(plan.Status);
                    if (filter == "all" || filter == name || filter == "open" && plan.Status != WorkPlanStatus.Done)
                    {
                        AltaJsonlWriter.WriteRecord(context.Stdout, PlanRecord(context, project, plan, items.GetLink(project.Id, kind, plan.Id), markdown: null, truncated: false));
                        count++;
                    }
                }
            }
        }

        WriteSummary(context, $"alta.{kind}.summary", count, truncated: false);
        return AltaExitCodes.Success;
    }

    // The plans a session in a worktree sees: the ones of the project, each as its worktree has it when it does.
    private static IEnumerable<WorkPlan> PlansOfCaller(WorkItemService items, ProjectDescriptor project, string worktree)
        => items.ListPlans(project).Select(plan => items.GetPlan(project, plan.Id, worktree) ?? plan);

    private static async ValueTask<int> HandleWorkItemShowAsync(AltaCommandContext context, string kind, string? projectRef, string? id)
    {
        var command = $"alta {kind} show";
        if (!TryGetWorkItems(context, out var items))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } itemId)
        {
            return UsageError(context, "usage.missingId", $"A {kind} id is required.", command);
        }

        var (project, exitCode) = await ResolveWorkItemProjectAsync(context, projectRef).ConfigureAwait(false);
        if (project is null)
        {
            return exitCode;
        }

        itemId = StripExtension(itemId);
        if (kind == WorkItemKinds.Task)
        {
            if (items.GetTask(project, itemId) is not { } task)
            {
                return WorkItemNotFound(context, kind, itemId);
            }

            AltaJsonlWriter.WriteRecord(context.Stdout, TaskRecord(context, project, task, items.GetLink(project.Id, kind, task.Id), task.Body));
            return AltaExitCodes.Success;
        }

        var worktree = CallerWorktree(context, project);
        if (items.GetPlan(project, itemId, worktree) is not { } plan || items.ReadMarkdown(project, kind, itemId, MaximumWorkItemMarkdown, worktree) is not { } text)
        {
            return WorkItemNotFound(context, kind, itemId);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, PlanRecord(context, project, plan, items.GetLink(project.Id, kind, plan.Id), text.Markdown, text.Truncated));
        return AltaExitCodes.Success;
    }

    // What the session that proposes an item runs with: a session started for the item takes the same.
    private static async Task<WorkItemSelection?> CallerSelectionAsync(AltaCommandContext context, string? sessionId)
    {
        if (sessionId is null)
        {
            return null;
        }

        var resolved = await ResolveSessionModelSelectionAsync(context, sessionId).ConfigureAwait(false);
        return resolved.Selection is { ProviderKey: { Length: > 0 } provider } selection
            ? new(provider, selection.ModelId, selection.ReasoningEffort?.ToString().ToLowerInvariant())
            : null;
    }

    private static async ValueTask<int> HandleTaskCreateAsync(AltaCommandContext context, TaskCreateOptions options)
    {
        const string Command = "alta task create";
        if (!TryGetWorkItems(context, out var items))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(options.Title) is not { } title)
        {
            return UsageError(context, "usage.missingTitle", "--title is required: what the task is, as an action in one line.", Command);
        }

        if (options.UseStdin == (options.Content is not null))
        {
            return UsageError(context, "usage.missingContent", "Give the description of the task with --stdin or with --content, not both.", Command);
        }

        var body = options.UseStdin ? await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false) : options.Content!;
        if (string.IsNullOrWhiteSpace(body))
        {
            return UsageError(context, "usage.missingContent", "The description of the task is empty: say why, what to do and where.", Command);
        }

        var (project, exitCode) = await ResolveWorkItemProjectAsync(context, options.Project).ConfigureAwait(false);
        if (project is null)
        {
            return exitCode;
        }

        WorkTaskCreation creation;
        try
        {
            var proposer = NormalizeOptionalText(context.Caller.SourceSessionId);
            creation = items.CreateTask(project, new WorkTaskDraft(title, options.Kind, options.Summary, body), proposer,
                await CallerSelectionAsync(context, proposer).ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return UsageError(context, "usage.invalidTask", exception.Message.Split(" (Parameter", 2)[0], Command);
        }
        catch (IOException exception)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "task.writeFailed", AltaExitCodes.Failure, $"The task could not be written: {exception.Message}");
            return AltaExitCodes.Failure;
        }

        if (creation.Task is not { } task)
        {
            return creation.Refusal switch
            {
                WorkTaskRefusal.Disabled => PermissionDenied(context, "task.proposalsDisabled",
                    "The user turned task proposals off in the settings. Do not create the task: mention the finding in your answer instead."),
                WorkTaskRefusal.TooManyProposals => PermissionDenied(context, "task.tooManyProposals",
                    $"This session already has {WorkItemService.MaximumProposals} proposed tasks waiting for a decision of the user. Keep the most useful ones: mention the rest in your answer instead."),
                _ => PermissionDenied(context, "task.tooManyTasks", $"The project already has {WorkItemService.MaximumItems} tasks."),
            };
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, TaskRecord(context, project, task, items.GetLink(project.Id, WorkItemKinds.Task, task.Id), markdown: null) with
        {
            type = "alta.task.created",
            nextStep = "The user now sees this task and decides what to do with it. Do not start it yourself; finish your current work and mention the task in one line of your answer.",
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleTaskChangeAsync(AltaCommandContext context, string action, string? projectRef, string? id)
    {
        var command = $"alta task {action}";
        if (!TryGetWorkItems(context, out var items))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } taskId)
        {
            return UsageError(context, "usage.missingId", "A task id is required.", command);
        }

        var session = NormalizeOptionalText(context.Caller.SourceSessionId);
        if (action == "start" && session is null)
        {
            return UsageError(context, "usage.missingSession", "Only a session starts a task this way.", command);
        }

        var (project, exitCode) = await ResolveWorkItemProjectAsync(context, projectRef).ConfigureAwait(false);
        if (project is null)
        {
            return exitCode;
        }

        taskId = StripExtension(taskId);
        if (items.GetTask(project, taskId) is not { } task)
        {
            return WorkItemNotFound(context, WorkItemKinds.Task, taskId);
        }

        var done = action switch
        {
            "start" => Start(),
            "complete" => items.CompleteTask(project, taskId),
            "later" => items.SetTaskStatus(project, taskId, WorkTaskStatus.Later),
            "reopen" => items.SetTaskStatus(project, taskId, WorkTaskStatus.Pending),
            "dismiss" => items.DismissTask(project, taskId),
            _ => items.RemoveTask(project, taskId),
        };
        if (!done)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "task.writeFailed", AltaExitCodes.Failure, $"The file of the task '{taskId}' could not be changed.");
            return AltaExitCodes.Failure;
        }

        var after = items.GetTask(project, taskId);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.task.changed",
            version = 1,
            correlationId = context.CorrelationId,
            id = taskId,
            projectId = project.Id,
            project = project.Slug,
            action,
            task.Title,
            status = after is null ? (action == "complete" ? "done" : "removed") : TaskStatusName(after, items.GetLink(project.Id, WorkItemKinds.Task, taskId)),
            kept = after is not null,
        });
        return AltaExitCodes.Success;

        bool Start()
        {
            if (task.Status != WorkTaskStatus.Pending && !items.SetTaskStatus(project, taskId, WorkTaskStatus.Pending))
            {
                return false;
            }

            items.SetRunner(project.Id, WorkItemKinds.Task, taskId, session);
            return true;
        }
    }

    private static async ValueTask<int> HandlePlanStatusAsync(AltaCommandContext context, string? projectRef, string? id, string? status)
    {
        const string Command = "alta plan status";
        if (!TryGetWorkItems(context, out var items))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } planId)
        {
            return UsageError(context, "usage.missingId", "A plan id is required.", Command);
        }

        if (WorkItemFiles.PlanStatusOf(status) is not { } next)
        {
            return UsageError(context, "usage.invalidStatus", "The status is one of: draft, approved, in-progress, done, blocked.", Command);
        }

        var (project, exitCode) = await ResolveWorkItemProjectAsync(context, projectRef).ConfigureAwait(false);
        if (project is null)
        {
            return exitCode;
        }

        planId = StripExtension(planId);
        var worktree = CallerWorktree(context, project);
        if (items.GetPlan(project, planId, worktree) is null)
        {
            return WorkItemNotFound(context, WorkItemKinds.Plan, planId);
        }

        if (!items.SetPlanStatus(project, planId, next, worktree))
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "plan.writeFailed", AltaExitCodes.Failure, $"The file of the plan '{planId}' could not be changed.");
            return AltaExitCodes.Failure;
        }

        // The session that had its plan approved shows it; the one that says it carries it out runs it.
        if (NormalizeOptionalText(context.Caller.SourceSessionId) is { } session)
        {
            if (next == WorkPlanStatus.Approved)
            {
                items.Propose(project.Id, WorkItemKinds.Plan, planId, session, await CallerSelectionAsync(context, session).ConfigureAwait(false));
            }
            else if (next == WorkPlanStatus.InProgress)
            {
                items.SetRunner(project.Id, WorkItemKinds.Plan, planId, session);
            }
        }

        if (items.GetPlan(project, planId, worktree) is not { } plan || next == WorkPlanStatus.Done && items.Settings.CompletedPlans == WorkItemClosing.Delete)
        {
            // The user does not keep completed plans: the file is gone with the work it described.
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.plan.removed",
                version = 1,
                correlationId = context.CorrelationId,
                id = planId,
                projectId = project.Id,
                project = project.Slug,
                status = WorkItemFiles.NameOf(next),
                message = "The plan is done. Its file was deleted: the settings do not keep completed plans.",
            });
            return AltaExitCodes.Success;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, PlanRecord(context, project, plan, items.GetLink(project.Id, WorkItemKinds.Plan, planId), markdown: null, truncated: false) with
        {
            type = "alta.plan.changed",
            nextStep = next == WorkPlanStatus.Approved
                ? "The user now sees the approved plan in this session and chooses where it is carried out. Do not start the work and do not hand off: say in one line that the plan is ready, and stop."
                : null,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandlePlanRemoveAsync(AltaCommandContext context, string? projectRef, string? id)
    {
        const string Command = "alta plan remove";
        if (!TryGetWorkItems(context, out var items))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } planId)
        {
            return UsageError(context, "usage.missingId", "A plan id is required.", Command);
        }

        var (project, exitCode) = await ResolveWorkItemProjectAsync(context, projectRef).ConfigureAwait(false);
        if (project is null)
        {
            return exitCode;
        }

        planId = StripExtension(planId);
        if (items.GetPlan(project, planId) is not { } plan)
        {
            return WorkItemNotFound(context, WorkItemKinds.Plan, planId);
        }

        if (!items.RemovePlan(project, planId))
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "plan.writeFailed", AltaExitCodes.Failure, $"The file of the plan '{planId}' could not be deleted.");
            return AltaExitCodes.Failure;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.plan.removed",
            version = 1,
            correlationId = context.CorrelationId,
            id = planId,
            projectId = project.Id,
            project = project.Slug,
            plan.Title,
        });
        return AltaExitCodes.Success;
    }

    private static int WorkItemNotFound(AltaCommandContext context, string kind, string id)
        => NotFound(context, $"{kind}.notFound", $"The project has no {kind} '{id}'. List them with `alta {kind} list`.");

    private static string StripExtension(string id) => id.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(id[..^3]) : Path.GetFileName(id);

    // What a task is for the user: the session that does it makes a pending task one in progress.
    private static string TaskStatusName(WorkTask task, WorkItemLink? link)
        => task.Status == WorkTaskStatus.Pending && link?.Runner is not null ? "in-progress" : WorkItemFiles.NameOf(task.Status);

    private static WorkItemRecord TaskRecord(AltaCommandContext context, ProjectDescriptor project, WorkTask task, WorkItemLink? link, string? markdown)
        => new("alta.task", 1, context.CorrelationId, task.Id, project.Id, project.Slug, task.Title, task.Kind, TaskStatusName(task, link), null, task.Summary, task.Created,
            WorkItemFiles.TasksFolder + "/" + task.Id + ".md", link?.ProposedBy, link?.Runner, markdown, null, null);

    private static WorkItemRecord PlanRecord(AltaCommandContext context, ProjectDescriptor project, WorkPlan plan, WorkItemLink? link, string? markdown, bool truncated)
        => new("alta.plan", 1, context.CorrelationId, plan.Id, project.Id, project.Slug, plan.Title, null, WorkItemFiles.NameOf(plan.Status), plan.StatusText, plan.Summary, plan.Created,
            WorkItemFiles.PlansFolder + "/" + plan.Id + ".md", link?.ProposedBy, link?.Runner, markdown, truncated ? true : null, null);

#pragma warning disable IDE1006, SA1300 // The names are those of the JSONL record.
    private sealed record WorkItemRecord(string type, int version, string correlationId, string id, string projectId, string project, string title, string? kind,
        string status, string? statusText, string? summary, string? created, string file, string? proposedBy, string? sessionId, string? markdown, bool? truncated, string? nextStep);
#pragma warning restore IDE1006, SA1300

    private sealed class TaskCreateOptions
    {
        public string? Title { get; set; }

        public string? Kind { get; set; }

        public string? Summary { get; set; }

        public string? Content { get; set; }

        public bool UseStdin { get; set; }

        public string? Project { get; set; }
    }
}
