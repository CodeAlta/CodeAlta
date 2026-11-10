using CodeAlta.Catalog;
using CodeAlta.Catalog.WorkItems;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta tag` commands and `alta task tag`: the tags the user organizes the tasks of a project with, listed in its
// `.alta/tags.yaml`. Agents apply them; only the user creates, renames or deletes one.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static Command CreateTagCommand(AltaCommandContext context)
    {
        var group = Group("tag", "Read the tags the user organizes the tasks of a project with, kept in `.alta/tags.yaml`, and propose some.");
        group.Add(CreateTagListCommand(context));
        group.Add(CreateTagMatchesCommand(context));
        group.Add(CreateTagProposeCommand(context));
        AddHelpText(
            group,
            "The tags belong to the user: each has a name and, mostly, a description of what it is for. Give a new task the tags whose description fits it (`alta task create --tag <name>`), or whose name clearly does when the tag has no description, and none when none fits. A tag without a description whose name does not say what it covers is given to no task. Never invent a tag: only the user creates one.",
            "When no tag fits a task you create and at least two other open tasks share its topic, you may propose a new tag with `alta tag propose`: the user sees it and decides. Propose at most one in a turn, and never a name `alta tag list` says was declined.",
            "Examples: `alta tag list`; `alta task list --tag mobile`; `alta task tag p53b --add mobile`.");
        return group;
    }

    private static Command CreateTagListCommand(AltaCommandContext context)
    {
        string? project = null;
        var command = Leaf("list", "List the tags of a project: name, description, and how many open tasks have it.");
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session, then to the cwd.", value => project = value);
        command.Add(async (_, _) => await HandleTagListAsync(context, project).ConfigureAwait(false));
        return command;
    }

    private static Command CreateTagMatchesCommand(AltaCommandContext context)
    {
        string? name = null;
        string? project = null;
        var tasks = new List<string>();
        var stdin = false;
        var command = Leaf("matches", "Give the user the open tasks an existing tag fits, for them to tag. Nothing is tagged before they apply it.");
        command.Add("<tag>", "The name of the tag.", value => name = value);
        command.Add("task=", "A task id or short id the tag fits. Repeat it for several.", value => { if (value is not null) tasks.Add(value); });
        command.Add("stdin", "Read the tasks from stdin, one a line: its short id, then why the tag fits it.", value => stdin = value is not null);
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => project = value);
        command.Add(async (_, _) => await HandleTagProposalAsync(context, "matches", project, name, null, tasks, stdin).ConfigureAwait(false));
        return command;
    }

    private static Command CreateTagProposeCommand(AltaCommandContext context)
    {
        string? name = null;
        string? description = null;
        string? project = null;
        var tasks = new List<string>();
        var stdin = false;
        var command = Leaf("propose", "Propose a new tag to the user, with the open tasks it would have. It is not a tag until the user creates it.");
        command.Add("name=", "The name: lower case letters, digits and hyphens, such as `flaky-tests`. Required.", value => name = value);
        command.Add("description=", "What the tag is for, in one line, which agents will match tasks against. Required.", value => description = value);
        command.Add("task=", "An open task id or short id the tag fits. Repeat it for several.", value => { if (value is not null) tasks.Add(value); });
        command.Add("stdin", "Read the tasks from stdin, one a line: its short id, then why the tag fits it.", value => stdin = value is not null);
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => project = value);
        command.Add(async (_, _) => await HandleTagProposalAsync(context, "propose", project, name, description, tasks, stdin).ConfigureAwait(false));
        AddHelpText(
            command,
            $"Name at least {WorkItemService.MinimumNewTagTasks} open tasks, the one that leads to it included. Propose a tag only when no tag of `alta tag list` fits and its topic is shared; at most one in a turn, and never a declined name.",
            "Example: `alta tag propose --name flaky-tests --description \"A test that fails at random, or only on some machines.\" --stdin`, with lines such as `p53b fails one run in three`.");
        return command;
    }

    private static async ValueTask<int> HandleTagProposalAsync(AltaCommandContext context, string action, string? projectRef, string? name, string? description, List<string> tasks, bool stdin)
    {
        var command = $"alta tag {action}";
        if (!TryGetWorkItems(context, out var items))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (WorkItemFiles.TagNameOf(name) is not { } tag)
        {
            return UsageError(context, "usage.invalidTag", action == "propose"
                ? "--name is required: lower case letters, digits and single hyphens, at most 32 characters."
                : "The name of an existing tag is required. List the tags with `alta tag list`.", command);
        }

        if (action == "propose" && string.IsNullOrWhiteSpace(description))
        {
            return UsageError(context, "usage.missingDescription", "--description is required: what the tag is for, in one line.", command);
        }

        var matches = tasks.Select(static task => new WorkTagMatch(task, null)).ToList();
        if (stdin)
        {
            // One task a line: its short id or id, then why the tag fits it.
            foreach (var line in (await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimStart('-', '*', ' ').Split((char[]?)null, 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    matches.Add(new WorkTagMatch(parts[0].Trim('`', ':'), parts.Length > 1 ? parts[1].TrimStart(':', '-', ' ') : null));
                }
            }
        }

        if (matches.Count == 0)
        {
            return UsageError(context, "usage.missingTasks", "Name the tasks with --task, or one a line with --stdin.", command);
        }

        var (project, exitCode) = await ResolveWorkItemProjectAsync(context, projectRef).ConfigureAwait(false);
        if (project is null)
        {
            return exitCode;
        }

        WorkTagProposalResult result;
        try
        {
            var session = NormalizeOptionalText(context.Caller.SourceSessionId);
            result = action == "propose" ? items.ProposeTag(project, tag, description!, matches, session) : items.ProposeTagMatches(project, tag, matches, session);
        }
        catch (ArgumentException exception)
        {
            return UsageError(context, "usage.invalidTag", exception.Message.Split(" (Parameter", 2)[0], command);
        }

        if (result.Proposal is not { } proposal)
        {
            return result.Refusal switch
            {
                WorkTagProposalRefusal.UnknownTag => NotFound(context, "tag.notFound", $"The project has no tag '{tag}'. List the tags with `alta tag list`."),
                WorkTagProposalRefusal.UnknownTask => NotFound(context, "task.notFound", $"'{result.Detail}' is not an open task of the project. List them with `alta task list`."),
                WorkTagProposalRefusal.Exists => UsageError(context, "tag.exists", $"The project already has the tag '{tag}': give it to the task with `alta task create --tag` or `alta task tag`.", command),
                WorkTagProposalRefusal.Declined => PermissionDenied(context, "tag.declined", $"The user declined the tag '{tag}'. Do not propose it again."),
                WorkTagProposalRefusal.TooFewTasks => UsageError(context, "tag.tooFewTasks",
                    $"A new tag is proposed for at least {WorkItemService.MinimumNewTagTasks} open tasks. Do not propose it: create the task without a tag.", command),
                _ => PermissionDenied(context, "tag.alreadyProposed", "This session already proposed a new tag that waits for the user. Do not propose another one."),
            };
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = action == "propose" ? "alta.tag.proposed" : "alta.tag.matches",
            version = 1,
            correlationId = context.CorrelationId,
            name = proposal.Name,
            description = proposal.Description,
            projectId = project.Id,
            project = project.Slug,
            tasks = proposal.Tasks.Select(static match => match.TaskId).ToArray(),
            nextStep = action == "propose"
                ? "The user now sees the proposed tag and decides. Mention it in one line of your answer; do not propose another one."
                : "The user now sees these tasks and decides which to tag. Say in one line how many you found.",
        });
        return AltaExitCodes.Success;
    }

    private static Command CreateTaskTagCommand(AltaCommandContext context)
    {
        string? id = null;
        string? project = null;
        var added = new List<string>();
        var removed = new List<string>();
        var command = Leaf("tag", "Add tags to a task, or remove some, when the user asks for it.");
        command.Add("<task-id>", "Task id or short id from `alta task list`.", value => id = value);
        command.Add("add=", "A tag of the project to add (`alta tag list`). Repeat it for several.", value => { if (value is not null) added.Add(value); });
        command.Add("remove=", "A tag to remove. Repeat it for several.", value => { if (value is not null) removed.Add(value); });
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => project = value);
        command.Add(async (_, _) => await HandleTaskTagAsync(context, project, id, added, removed).ConfigureAwait(false));
        return command;
    }

    private static async ValueTask<int> HandleTagListAsync(AltaCommandContext context, string? projectRef)
    {
        if (!TryGetWorkItems(context, out var items))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (project, exitCode) = await ResolveWorkItemProjectAsync(context, projectRef).ConfigureAwait(false);
        if (project is null)
        {
            return exitCode;
        }

        var tags = items.ListTags(project);
        foreach (var tag in tags.Tags)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.tag",
                version = 1,
                correlationId = context.CorrelationId,
                name = tag.Name,
                description = tag.Description,
                openTasks = tag.OpenTasks,
                listed = tag.Listed,
            });
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.tag.summary",
            version = 1,
            correlationId = context.CorrelationId,
            projectId = project.Id,
            project = project.Slug,
            count = tags.Tags.Count,
            declined = tags.Declined.Count > 0 ? tags.Declined : null,
            nextStep = tags.Tags.Count == 0 ? "The project has no tags: create tasks without --tag." : null,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleTaskTagAsync(AltaCommandContext context, string? projectRef, string? id, List<string> added, List<string> removed)
    {
        const string Command = "alta task tag";
        if (!TryGetWorkItems(context, out var items))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } taskId)
        {
            return UsageError(context, "usage.missingId", "A task id is required.", Command);
        }

        if (added.Count == 0 && removed.Count == 0)
        {
            return UsageError(context, "usage.missingTag", "Name the tags to add with --add, or to remove with --remove.", Command);
        }

        var (project, exitCode) = await ResolveWorkItemProjectAsync(context, projectRef).ConfigureAwait(false);
        if (project is null)
        {
            return exitCode;
        }

        if (UnknownTags(items, project, added) is { } unknown)
        {
            return UsageError(context, "usage.unknownTag", unknown, Command);
        }

        if (FindTask(context, items, project, StripExtension(taskId), Command, out var notFound) is not { } task)
        {
            return notFound;
        }

        var drop = removed.Select(WorkItemFiles.TagNameOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var tags = task.Tags.Where(tag => !drop.Contains(tag)).Concat(added.Select(WorkItemFiles.TagNameOf).OfType<string>()).Distinct(StringComparer.Ordinal).ToList();
        if (tags.Count > WorkItemFiles.MaximumTaskTags)
        {
            return UsageError(context, "usage.tooManyTags", $"A task has at most {WorkItemFiles.MaximumTaskTags} tags.", Command);
        }

        if (!items.SetTaskTags(project, task.Id, tags))
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "task.writeFailed", AltaExitCodes.Failure, $"The file of the task '{task.Id}' could not be changed.");
            return AltaExitCodes.Failure;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.task.tagged",
            version = 1,
            correlationId = context.CorrelationId,
            id = task.Id,
            shortId = task.ShortId,
            projectId = project.Id,
            project = project.Slug,
            task.Title,
            tags,
        });
        return AltaExitCodes.Success;
    }

    // Why tags cannot be given to a task: one that is not a tag name, or that the project does not have; null when all can.
    private static string? UnknownTags(WorkItemService items, ProjectDescriptor project, IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return null;
        }

        var known = items.ListTags(project).Tags.Select(static tag => tag.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = tags.Where(tag => WorkItemFiles.TagNameOf(tag) is not { } name || !known.Contains(name)).ToList();
        if (unknown.Count == 0)
        {
            return null;
        }

        return known.Count == 0
            ? $"The project has no tags, so the task cannot have '{string.Join("', '", unknown)}'. Only the user creates tags: leave --tag out."
            : $"The project has no tag '{string.Join("', '", unknown)}'. Its tags are: {string.Join(", ", known.Order(StringComparer.Ordinal))}. Only the user creates tags: use one of these, or none.";
    }
}
