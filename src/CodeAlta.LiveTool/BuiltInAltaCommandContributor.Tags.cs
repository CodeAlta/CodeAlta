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
        var group = Group("tag", "Read the tags the user organizes the tasks of a project with, kept in `.alta/tags.yaml`.");
        group.Add(CreateTagListCommand(context));
        AddHelpText(
            group,
            "The tags belong to the user: each has a name and, mostly, a description of what it is for. Give a new task the tags whose description fits it (`alta task create --tag <name>`), or whose name clearly does when the tag has no description, and none when none fits. A tag without a description whose name does not say what it covers is given to no task. Never invent a tag: only the user creates one.",
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
