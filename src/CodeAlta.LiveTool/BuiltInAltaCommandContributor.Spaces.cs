using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta space` commands, and the `alta project` commands that change the catalog: a space is a named group of
// projects, and the default space holds every project.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly AltaCommandPolicy[] SpacePolicies =
    [
        Read("space list"),
        Read("space show"),
        Read("space current"),
        Mutating("space create"),
        Mutating("space update"),
        Disruptive("space delete"),
        Mutating("space add"),
        Mutating("space remove"),
        Mutating("space reorder"),
    ];

    // It changes nothing but what the window shows.
    private static readonly AltaCommandPolicy SpaceSwitchPolicy = Read("space switch");

    private static Command CreateSpaceCommand(AltaCommandContext context)
    {
        var group = Group("space", "Use the spaces of CodeAlta: named groups of projects.");
        group.Add(CreateSpaceListCommand(context));
        group.Add(CreateSpaceShowCommand(context));
        group.Add(CreateSpaceCurrentCommand(context));
        group.Add(CreateSpaceCreateCommand(context));
        group.Add(CreateSpaceUpdateCommand(context));
        group.Add(CreateSpaceDeleteCommand(context));
        group.Add(CreateSpaceMembersCommand(context, add: true));
        group.Add(CreateSpaceMembersCommand(context, add: false));
        group.Add(CreateSpaceReorderCommand(context));
        // Only a host with a window that shows one space at a time has the command.
        if (context.Services.Get<IAltaSpaceView>() is not null)
        {
            group.Add(CreateSpaceSwitchCommand(context));
        }

        AddHelpText(
            group,
            "A space is a named group of projects. The default space (`default`) holds every project and cannot be deleted; a project can be in several spaces.",
            "A space has a description that tells what it is for: read it to know which projects belong there.",
            "<space> is the id of a space, the start of its id when only one space starts so, or its name.",
            "Examples: `alta space list`; `alta space show work`; `alta space create --name \"Open source\" --description \"Libraries published on GitHub.\"`; `alta space add work CodeAlta`.");
        return group;
    }

    private static Command CreateSpaceListCommand(AltaCommandContext context)
    {
        var command = Leaf("list", "List the spaces: id, name, description, icon, color and how many projects each holds.");
        command.Add(async (_, _) => await HandleSpaceListAsync(context).ConfigureAwait(false));
        AddHelpText(command,
            "The description of a space tells what the space is for. `current` is true for the space the CodeAlta window shows. `projectCount` counts the projects that are not archived.",
            "Example: `alta space list`.");
        return command;
    }

    private static Command CreateSpaceShowCommand(AltaCommandContext context)
    {
        string? reference = null;
        var includeArchived = false;
        var command = Leaf("show", "Show one space with its projects.");
        command.Add("<space>", "Space id, start of id, or name.", value => reference = value);
        command.Add("include-archived", "Include archived projects.", value => includeArchived = value is not null);
        command.Add(async (_, _) => await HandleSpaceShowAsync(context, reference, includeArchived).ConfigureAwait(false));
        AddHelpText(command,
            "The description of the space tells what the space is for.",
            "Examples: `alta space show work`; `alta space show default --include-archived`.");
        return command;
    }

    private static Command CreateSpaceCurrentCommand(AltaCommandContext context)
    {
        var command = Leaf("current", "Show the space the CodeAlta window shows, with its projects.");
        command.Add(async (_, _) => await HandleSpaceCurrentAsync(context).ConfigureAwait(false));
        AddHelpText(command,
            "`shown` is true when a window shows this space. Without a window, the record is the default space, which holds every project, and `shown` is false.",
            "Example: `alta space current`.");
        return command;
    }

    private static Command CreateSpaceCreateCommand(AltaCommandContext context)
    {
        var options = new SpaceWriteOptions();
        var projects = new List<string>();
        var command = Leaf("create", "Create a space.");
        AddSpaceWriteOptions(command, options, create: true);
        command.Add("project=", "A project to put in the space: id, slug or path. Repeat the option for several.", value => { if (value is not null) projects.Add(value); });
        command.Add(async (_, _) => await HandleSpaceCreateAsync(context, options, projects).ConfigureAwait(false));
        AddHelpText(command,
            "The id of the space is worked out from its name and never changes afterwards.",
            "Write a description that tells what the space is for: CodeAlta and its agents read it.",
            "Examples: `alta space create --name Work --description \"The projects of my job.\" --icon briefcase --color #2d72d2`; `alta space create --name Libraries --project Tomlyn --project SharpYaml`.");
        return command;
    }

    private static Command CreateSpaceUpdateCommand(AltaCommandContext context)
    {
        var options = new SpaceWriteOptions();
        string? reference = null;
        var command = Leaf("update", "Change the name, the description, the icon or the color of a space. Its id stays.");
        command.Add("<space>", "Space id, start of id, or name.", value => reference = value);
        AddSpaceWriteOptions(command, options, create: false);
        command.Add(async (_, _) => await HandleSpaceUpdateAsync(context, reference, options).ConfigureAwait(false));
        AddHelpText(command,
            "What is not given stays as it is. An empty value removes a description, an icon or a color: `--description \"\"`.",
            "Examples: `alta space update work --name \"Day job\"`; `alta space update work --stdin` with the description on stdin; `alta space update work --color \"\"`.");
        return command;
    }

    private static void AddSpaceWriteOptions(Command command, SpaceWriteOptions options, bool create)
    {
        command.Add("name=", create ? "Name of the space, 1 to 64 characters. Required." : "New name, 1 to 64 characters.", value => options.Name = value);
        command.Add("description=", "What the space is for. Prefer --stdin for several lines.", value => options.Description = value);
        command.Add("stdin", "Read the description from stdin.", value => options.UseStdin = value is not null);
        command.Add("icon=", "Name of the icon of the space, as the CodeAlta window lists them.", value => options.Icon = value);
        command.Add("color=", "Color of the space, `#rgb` or `#rrggbb`.", value => options.Color = value);
    }

    private static Command CreateSpaceDeleteCommand(AltaCommandContext context)
    {
        string? reference = null;
        var command = Leaf("delete", "Delete a space. Its projects, their folders and their sessions stay.");
        command.Add("<space>", "Space id, start of id, or name.", value => reference = value);
        command.Add(async (_, _) => await HandleSpaceDeleteAsync(context, reference).ConfigureAwait(false));
        AddHelpText(command,
            "The projects of the space are still in the catalog and in the default space. The default space cannot be deleted.",
            "Delete only the spaces you created, or the ones the user asks you to remove.");
        return command;
    }

    private static Command CreateSpaceMembersCommand(AltaCommandContext context, bool add)
    {
        var references = new List<string>();
        var command = add
            ? Leaf("add", "Put projects in a space. They stay in the other spaces they are in.")
            : Leaf("remove", "Take projects out of a space. They stay in the catalog and in the default space.");
        command.Add("<space>", "Space id, start of id, or name.", value => { if (value is not null) references.Add(value); });
        command.Add("<project>*", "Projects: id, slug or path.", value => { if (value is not null) references.Add(value); });
        command.Add(async (_, _) => await HandleSpaceMembersAsync(context, references, add).ConfigureAwait(false));
        AddHelpText(command,
            "Every project is in the default space: it takes no `add` and no `remove`.",
            add ? "Example: `alta space add work CodeAlta C:/code/Tomlyn`." : "Example: `alta space remove work CodeAlta`.");
        return command;
    }

    private static Command CreateSpaceReorderCommand(AltaCommandContext context)
    {
        var references = new List<string>();
        var command = Leaf("reorder", "Put the spaces in the order given; the ones not named follow in their present order.");
        command.Add("<space>*", "Spaces, first to last: id, start of id, or name.", value => { if (value is not null) references.Add(value); });
        command.Add(async (_, _) => await HandleSpaceReorderAsync(context, references).ConfigureAwait(false));
        AddHelpText(command, "The default space is always first.", "Example: `alta space reorder personal work`.");
        return command;
    }

    private static Command CreateSpaceSwitchCommand(AltaCommandContext context)
    {
        string? reference = null;
        var command = Leaf("switch", "Show another space in the CodeAlta window.");
        command.Add("<space>", "Space id, start of id, or name.", value => reference = value);
        command.Add(async (_, _) => await HandleSpaceSwitchAsync(context, reference).ConfigureAwait(false));
        AddHelpText(command,
            "It changes what the window of the user shows and no setting: use it when the user asks to see another space, not to read one. `alta space show <space>` reads any space.",
            "Example: `alta space switch work`.");
        return command;
    }

    private static Command CreateProjectAddCommand(AltaCommandContext context)
    {
        string? path = null;
        var spaces = new List<string>();
        var command = Leaf("add", "Add a folder to the catalog as a project, and put it in spaces.");
        command.Add("<path>", "Local project path.", value => path = value);
        command.Add("space=", "A space the project joins: id, start of id, or name. Repeat the option for several.", value => { if (value is not null) spaces.Add(value); });
        command.Add(async (_, _) => await HandleProjectAddAsync(context, path, spaces).ConfigureAwait(false));
        AddHelpText(command,
            "A folder that is already a project only joins the spaces named. Every project is in the default space.",
            "Examples: `alta project add C:/code/Tomlyn`; `alta project add . --space work`.");
        return command;
    }

    private static Command CreateProjectRenameCommand(AltaCommandContext context)
    {
        string? reference = null;
        string? name = null;
        var command = Leaf("rename", "Change the name CodeAlta shows for a project. Its id, its slug and its folder stay.");
        command.Add("<project>", "Project id, slug, or path.", value => reference = value);
        command.Add("<name>", "New display name, at most 256 characters.", value => name = value);
        command.Add(async (_, _) => await HandleProjectRenameAsync(context, reference, name).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta project rename codealta \"CodeAlta (main)\"`.");
        return command;
    }

    private static Command CreateProjectArchiveCommand(AltaCommandContext context, bool archive)
    {
        string? reference = null;
        var command = archive
            ? Leaf("archive", "Archive a project: it leaves the lists, and its folder, its sessions and its spaces stay.")
            : Leaf("unarchive", "Bring an archived project back into the lists.");
        command.Add("<project>", "Project id, slug, or path.", value => reference = value);
        command.Add(async (_, _) => await HandleProjectArchiveAsync(context, reference, archive).ConfigureAwait(false));
        AddHelpText(command, archive
            ? "Example: `alta project archive old-prototype`. List archived projects with `alta project list --all --include-archived`."
            : "Example: `alta project unarchive old-prototype`.");
        return command;
    }

    private static Command CreateProjectRemoveCommand(AltaCommandContext context)
    {
        string? reference = null;
        var deleteSessions = false;
        var command = Leaf("remove", "Remove a project from the catalog. Its folder and the files in it are never touched.");
        command.Add("<project>", "Project id, slug, or path.", value => reference = value);
        command.Add("delete-sessions", "Delete the sessions of the project with it. Without it, a project that has sessions is not removed.", value => deleteSessions = value is not null);
        command.Add(async (_, _) => await HandleProjectRemoveAsync(context, reference, deleteSessions).ConfigureAwait(false));
        AddHelpText(command,
            "Remove a project only when the user asks for it. To put a project aside and keep its sessions, use `alta project archive <project>`.",
            "Example: `alta project remove old-prototype`.");
        return command;
    }

    private static void NotifySpacesChanged(AltaCommandContext context)
        => context.Services.Get<IAltaSpaceView>()?.NotifyChanged();

    // The space the window shows, when a window said which one and the space is still there.
    private static async Task<SpaceDescriptor?> ShownSpaceAsync(AltaCommandContext context, SpaceCatalog spaces)
        => NormalizeOptionalText(context.Services.Get<IAltaSpaceView>()?.ShownSpaceId) is { } shown
            ? await spaces.GetAsync(shown, context.CancellationToken).ConfigureAwait(false)
            : null;

    // What stands between a project and the user: the space the window shows, which does not have the project, and
    // the space to show for it (the first of its spaces, else the default one, which has every project). Null when
    // the window shows the project: its space has it, no window said what it shows, or the host keeps no spaces.
    private static async Task<(SpaceDescriptor Shown, SpaceDescriptor Home)?> ProjectOutOfViewAsync(AltaCommandContext context, ProjectDescriptor project)
    {
        if (context.Services.Get<SpaceCatalog>() is not { } spaces
            || await ShownSpaceAsync(context, spaces).ConfigureAwait(false) is not { IsDefault: false } shown
            || project.Spaces.Contains(shown.Id, StringComparer.Ordinal))
        {
            return null;
        }

        var all = await spaces.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        return (shown, all.FirstOrDefault(space => !space.IsDefault && project.Spaces.Contains(space.Id, StringComparer.Ordinal)) ?? all[0]);
    }

    // The answer to a command that shows a project to the user while the window shows a space without it: nothing
    // was shown, and the window is not moved to another space unless the user asks for it.
    private static int ProjectNotInShownSpace(AltaCommandContext context, ProjectDescriptor project, SpaceDescriptor shown, SpaceDescriptor home, string notDone)
        => Unsupported(context, "project.notInShownSpace",
            $"{notDone}: the window shows the space '{shown.Name}', which does not have the project '{project.DisplayName}'. " +
            $"The project is in the space '{home.Name}', and the window offers the user a button that shows it there. " +
            $"Tell the user so. When the user asks you to show it, run `alta space switch {home.Id}`, then this command again.");

    // A space by its id, then by its name, then by the start of its id when only one space starts so.
    private static async Task<(SpaceDescriptor? Space, int ExitCode)> ResolveSpaceAsync(
        AltaCommandContext context, SpaceCatalog spaces, string? reference, string commandPath)
    {
        if (NormalizeOptionalText(reference) is not { } text)
        {
            return (null, UsageError(context, "usage.missingSpace", "A space is required: its id, the start of its id, or its name.", commandPath));
        }

        var all = await spaces.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        if ((all.FirstOrDefault(space => string.Equals(space.Id, text, StringComparison.OrdinalIgnoreCase))
             ?? all.FirstOrDefault(space => string.Equals(space.Name, text, StringComparison.OrdinalIgnoreCase))) is { } exact)
        {
            return (exact, AltaExitCodes.Success);
        }

        var starting = all.Where(space => space.Id.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        return starting.Length switch
        {
            1 => (starting[0], AltaExitCodes.Success),
            0 => (null, NotFound(context, "space.notFound", $"No space has the id or the name '{text}'. List them with `alta space list`.")),
            _ => (null, UsageError(context, "usage.ambiguousSpace",
                $"Several spaces have an id that starts with '{text}': {string.Join(", ", starting.Select(static space => space.Id))}.", commandPath)),
        };
    }

    private static async Task<(IReadOnlyList<SpaceDescriptor>? Spaces, int ExitCode)> ResolveSpacesAsync(
        AltaCommandContext context, SpaceCatalog spaces, IReadOnlyList<string> references, string commandPath)
    {
        var resolved = new List<SpaceDescriptor>(references.Count);
        foreach (var reference in references)
        {
            var (space, exitCode) = await ResolveSpaceAsync(context, spaces, reference, commandPath).ConfigureAwait(false);
            if (space is null)
            {
                return (null, exitCode);
            }

            if (!resolved.Any(other => other.Id == space.Id))
            {
                resolved.Add(space);
            }
        }

        return (resolved, AltaExitCodes.Success);
    }

    private static int SpaceRefused(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.Unsupported, message);
        return AltaExitCodes.Unsupported;
    }

    private static int SpaceChangeFailed(AltaCommandContext context, SpaceChange change, string commandPath, string? prefix = null)
    {
        var message = prefix + (change.Message ?? "The space could not be changed.");
        switch (change.Status)
        {
            case SpaceChangeStatus.NotFound:
                return NotFound(context, "space.notFound", message);
            case SpaceChangeStatus.Invalid:
                return UsageError(context, "space.invalid", message, commandPath);
            case SpaceChangeStatus.Refused:
                return SpaceRefused(context, "space.refused", message);
            default:
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "space.conflict", AltaExitCodes.Failure, message);
                return AltaExitCodes.Failure;
        }
    }

    private static Dictionary<string, object?> SpaceRecord(AltaCommandContext context, string type, SpaceDescriptor space, int projectCount, bool? current)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = space.Id,
            ["name"] = space.Name,
        };
        if (space.Description is not null) record["description"] = space.Description;
        if (space.Icon is not null) record["icon"] = space.Icon;
        if (space.Color is not null) record["color"] = space.Color;
        record["default"] = space.IsDefault;
        record["projectCount"] = projectCount;
        if (current is not null) record["current"] = current.Value;
        return record;
    }

    private static async Task<int> CountSpaceProjectsAsync(AltaCommandContext context, SpaceCatalog spaces, string spaceId)
        => (await spaces.LoadMembersAsync(includeArchived: false, context.CancellationToken).ConfigureAwait(false))
            .FirstOrDefault(members => members.Space.Id == spaceId)?.ProjectIds.Count ?? 0;

    private static async Task WriteSpaceDetailAsync(AltaCommandContext context, SpaceCatalog spaces, SpaceDescriptor space, bool includeArchived, bool current, bool? shown)
    {
        var projects = (await spaces.Projects.LoadAsync(context.CancellationToken).ConfigureAwait(false))
            .Where(project => (includeArchived || !project.Archived) && (space.IsDefault || project.Spaces.Contains(space.Id, StringComparer.Ordinal)))
            .OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var record = SpaceRecord(context, "alta.space.detail", space, projects.Length, current);
        if (shown is not null) record["shown"] = shown.Value;
        record["projects"] = projects.Select(static project => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = project.Id,
            ["slug"] = project.Slug,
            ["name"] = project.DisplayName,
            ["path"] = project.ProjectPath,
            ["archived"] = project.Archived,
        }).ToArray();
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
    }

    private static async ValueTask<int> HandleSpaceListAsync(AltaCommandContext context)
    {
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var members = await spaces.LoadMembersAsync(includeArchived: false, context.CancellationToken).ConfigureAwait(false);
        var shown = NormalizeOptionalText(context.Services.Get<IAltaSpaceView>()?.ShownSpaceId);
        var currentId = shown is not null && members.Any(item => item.Space.Id == shown) ? shown : SpaceDescriptor.DefaultId;
        foreach (var item in members)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, SpaceRecord(context, "alta.space.item", item.Space, item.ProjectIds.Count, item.Space.Id == currentId));
        }

        WriteSummary(context, "alta.space.summary", members.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSpaceShowAsync(AltaCommandContext context, string? reference, bool includeArchived)
    {
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (space, exitCode) = await ResolveSpaceAsync(context, spaces, reference, "alta space show").ConfigureAwait(false);
        if (space is null)
        {
            return exitCode;
        }

        var current = (await ShownSpaceAsync(context, spaces).ConfigureAwait(false))?.Id ?? SpaceDescriptor.DefaultId;
        await WriteSpaceDetailAsync(context, spaces, space, includeArchived, space.Id == current, shown: null).ConfigureAwait(false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSpaceCurrentAsync(AltaCommandContext context)
    {
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var shown = await ShownSpaceAsync(context, spaces).ConfigureAwait(false);
        var space = shown ?? (await spaces.LoadAsync(context.CancellationToken).ConfigureAwait(false))[0];
        await WriteSpaceDetailAsync(context, spaces, space, includeArchived: false, current: true, shown: shown is not null).ConfigureAwait(false);
        return AltaExitCodes.Success;
    }

    // The description of a space comes from --description or from stdin, never from both.
    private static async Task<(bool Ok, string? Description, int ExitCode)> ReadSpaceDescriptionAsync(AltaCommandContext context, SpaceWriteOptions options, string commandPath)
    {
        if (!options.UseStdin)
        {
            return (true, options.Description, AltaExitCodes.Success);
        }

        if (options.Description is not null)
        {
            return (false, null, UsageError(context, "usage.contentConflict", "Use either --description or --stdin, not both.", commandPath));
        }

        return (true, await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false), AltaExitCodes.Success);
    }

    private static async ValueTask<int> HandleSpaceCreateAsync(AltaCommandContext context, SpaceWriteOptions options, IReadOnlyList<string> projectRefs)
    {
        const string CommandPath = "alta space create";
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(options.Name) is not { } name)
        {
            return UsageError(context, "usage.missingName", "A space needs a name. Use --name <name>.", CommandPath);
        }

        var description = await ReadSpaceDescriptionAsync(context, options, CommandPath).ConfigureAwait(false);
        if (!description.Ok)
        {
            return description.ExitCode;
        }

        // Every project is found before the space is written, so that a wrong reference creates nothing.
        var projects = new List<ProjectDescriptor>(projectRefs.Count);
        foreach (var reference in projectRefs)
        {
            if (await ResolveProjectAsync(spaces.Projects, reference, context, includeArchived: true).ConfigureAwait(false) is not { } project)
            {
                return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
            }

            if (!projects.Any(other => other.Id == project.Id))
            {
                projects.Add(project);
            }
        }

        var created = await spaces.CreateAsync(name, description.Description, options.Icon, options.Color, context.CancellationToken).ConfigureAwait(false);
        if (created.Space is not { } space || !created.Succeeded)
        {
            return SpaceChangeFailed(context, created, CommandPath);
        }

        foreach (var project in projects)
        {
            var assigned = await spaces.AssignAsync(project.Id, [space.Id], null, context.CancellationToken).ConfigureAwait(false);
            if (!assigned.Succeeded)
            {
                NotifySpacesChanged(context);
                return SpaceChangeFailed(context, assigned, CommandPath, $"The space '{space.Id}' was created, but the project '{project.Slug}' is not in it: ");
            }
        }

        var record = SpaceRecord(context, "alta.space.created", space, await CountSpaceProjectsAsync(context, spaces, space.Id).ConfigureAwait(false), current: null);
        record["projects"] = projects.Select(static project => project.Id).ToArray();
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSpaceUpdateAsync(AltaCommandContext context, string? reference, SpaceWriteOptions options)
    {
        const string CommandPath = "alta space update";
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (before, exitCode) = await ResolveSpaceAsync(context, spaces, reference, CommandPath).ConfigureAwait(false);
        if (before is null)
        {
            return exitCode;
        }

        var description = await ReadSpaceDescriptionAsync(context, options, CommandPath).ConfigureAwait(false);
        if (!description.Ok)
        {
            return description.ExitCode;
        }

        if (options.Name is null && description.Description is null && options.Icon is null && options.Color is null)
        {
            return UsageError(context, "usage.missingChange", "Nothing to change: use --name, --description, --stdin, --icon or --color.", CommandPath);
        }

        var (name, text, icon, color) = (before.Name, before.Description, before.Icon, before.Color);
        var updated = await spaces.UpdateAsync(before.Id, new SpaceEdit(options.Name, description.Description, options.Icon, options.Color), context.CancellationToken).ConfigureAwait(false);
        if (updated.Space is not { } space || !updated.Succeeded)
        {
            return SpaceChangeFailed(context, updated, CommandPath);
        }

        var changed = new List<string>(4);
        if (!string.Equals(name, space.Name, StringComparison.Ordinal)) changed.Add("name");
        if (!string.Equals(text, space.Description, StringComparison.Ordinal)) changed.Add("description");
        if (!string.Equals(icon, space.Icon, StringComparison.Ordinal)) changed.Add("icon");
        if (!string.Equals(color, space.Color, StringComparison.Ordinal)) changed.Add("color");
        var record = SpaceRecord(context, "alta.space.updated", space, await CountSpaceProjectsAsync(context, spaces, space.Id).ConfigureAwait(false), current: null);
        record["changed"] = changed;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSpaceDeleteAsync(AltaCommandContext context, string? reference)
    {
        const string CommandPath = "alta space delete";
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (space, exitCode) = await ResolveSpaceAsync(context, spaces, reference, CommandPath).ConfigureAwait(false);
        if (space is null)
        {
            return exitCode;
        }

        var projectCount = space.IsDefault ? 0 : await CountSpaceProjectsAsync(context, spaces, space.Id).ConfigureAwait(false);
        var deleted = await spaces.DeleteAsync(space.Id, context.CancellationToken).ConfigureAwait(false);
        if (!deleted.Succeeded)
        {
            return SpaceChangeFailed(context, deleted, CommandPath);
        }

        var record = SpaceRecord(context, "alta.space.deleted", space, projectCount, current: null);
        record["note"] = "The projects of the space stay in the catalog and in the default space; their folders and their sessions were not touched.";
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSpaceMembersAsync(AltaCommandContext context, IReadOnlyList<string> references, bool add)
    {
        var commandPath = add ? "alta space add" : "alta space remove";
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (space, exitCode) = await ResolveSpaceAsync(context, spaces, references.Count > 0 ? references[0] : null, commandPath).ConfigureAwait(false);
        if (space is null)
        {
            return exitCode;
        }

        if (references.Count < 2)
        {
            return UsageError(context, "usage.missingProject", "At least one project is required: its id, its slug or its path.", commandPath);
        }

        if (space.IsDefault)
        {
            return SpaceRefused(context, "space.refused", "Every project is in the default space: it takes no add and no remove.");
        }

        var projects = new List<ProjectDescriptor>(references.Count - 1);
        foreach (var reference in references.Skip(1))
        {
            if (await ResolveProjectAsync(spaces.Projects, reference, context, includeArchived: true).ConfigureAwait(false) is not { } project)
            {
                return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
            }

            if (!projects.Any(other => other.Id == project.Id))
            {
                projects.Add(project);
            }
        }

        var changed = new List<string>(projects.Count);
        var unchanged = new List<string>();
        foreach (var project in projects)
        {
            var member = project.Spaces.Contains(space.Id, StringComparer.Ordinal);
            var assigned = await spaces.AssignAsync(project.Id, add ? [space.Id] : null, add ? null : [space.Id], context.CancellationToken).ConfigureAwait(false);
            if (!assigned.Succeeded)
            {
                if (changed.Count > 0) NotifySpacesChanged(context);
                return SpaceChangeFailed(context, assigned, commandPath);
            }

            (member == add ? unchanged : changed).Add(project.Id);
        }

        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.space.projects",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["spaceId"] = space.Id,
            [add ? "added" : "removed"] = changed,
            ["unchanged"] = unchanged,
            ["projectCount"] = await CountSpaceProjectsAsync(context, spaces, space.Id).ConfigureAwait(false),
        };
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSpaceReorderAsync(AltaCommandContext context, IReadOnlyList<string> references)
    {
        const string CommandPath = "alta space reorder";
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (references.Count == 0)
        {
            return UsageError(context, "usage.missingSpace", "At least one space is required: its id, the start of its id, or its name.", CommandPath);
        }

        var (ordered, exitCode) = await ResolveSpacesAsync(context, spaces, references, CommandPath).ConfigureAwait(false);
        if (ordered is null)
        {
            return exitCode;
        }

        var reordered = await spaces.ReorderAsync(ordered.Select(static space => space.Id).ToArray(), context.CancellationToken).ConfigureAwait(false);
        if (!reordered.Succeeded)
        {
            return SpaceChangeFailed(context, reordered, CommandPath);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.space.order",
            version = 1,
            correlationId = context.CorrelationId,
            ids = (await spaces.LoadAsync(context.CancellationToken).ConfigureAwait(false)).Select(static space => space.Id).ToArray(),
        });
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSpaceSwitchAsync(AltaCommandContext context, string? reference)
    {
        if (context.Services.Get<IAltaSpaceView>() is not { } view)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
                "Required in-process service 'IAltaSpaceView' is unavailable.");
            return AltaExitCodes.ServiceUnavailable;
        }

        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (space, exitCode) = await ResolveSpaceAsync(context, spaces, reference, "alta space switch").ConfigureAwait(false);
        if (space is null)
        {
            return exitCode;
        }

        if (!view.Show(space.Id))
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "view.unavailable", AltaExitCodes.ServiceUnavailable,
                "No CodeAlta window is open to show the space.");
            return AltaExitCodes.ServiceUnavailable;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.space.shown",
            version = 1,
            correlationId = context.CorrelationId,
            spaceId = space.Id,
            name = space.Name,
        });
        return AltaExitCodes.Success;
    }

    // The projects `alta project list` reads: those of the space named, else of the space the window shows.
    // A null space with a success means every project, which is what the default space holds.
    private static async Task<(SpaceDescriptor? Space, int ExitCode)> ResolveProjectListSpaceAsync(AltaCommandContext context, string? spaceRef, bool all)
    {
        const string CommandPath = "alta project list";
        var reference = NormalizeOptionalText(spaceRef);
        if (all && reference is not null)
        {
            return (null, UsageError(context, "usage.scopeConflict", "Use either --space or --all, not both.", CommandPath));
        }

        if (reference is not null)
        {
            if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var named))
            {
                return (null, AltaExitCodes.ServiceUnavailable);
            }

            var (space, exitCode) = await ResolveSpaceAsync(context, named, reference, CommandPath).ConfigureAwait(false);
            return (space is { IsDefault: false } ? space : null, exitCode);
        }

        if (all || context.Services.Get<SpaceCatalog>() is not { } spaces)
        {
            return (null, AltaExitCodes.Success);
        }

        var shown = await ShownSpaceAsync(context, spaces).ConfigureAwait(false);
        return (shown is { IsDefault: false } ? shown : null, AltaExitCodes.Success);
    }

    private static async ValueTask<int> HandleProjectCurrentAsync(AltaCommandContext context)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        // The project of the calling session first: a session that works in a worktree is not in the folder of its project.
        if (NormalizeOptionalText(context.Caller.SourceProjectId) is { } projectId &&
            await catalog.GetByIdAsync(projectId, context.CancellationToken).ConfigureAwait(false) is { } own)
        {
            WriteProject(context, "alta.project.resolution", own);
            return AltaExitCodes.Success;
        }

        return await HandleProjectResolveAsync(context, null).ConfigureAwait(false);
    }

    private static async ValueTask<int> HandleProjectAddAsync(AltaCommandContext context, string? path, IReadOnlyList<string> spaceRefs)
    {
        const string CommandPath = "alta project add";
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return UsageError(context, "usage.missingPath", "Project path is required.", CommandPath);
        }

        var resolvedPath = ResolvePath(context, path);
        if (!Directory.Exists(resolvedPath))
        {
            return NotFound(context, "project.pathNotFound", $"Project path '{resolvedPath}' does not exist.");
        }

        SpaceCatalog? spaces = null;
        IReadOnlyList<SpaceDescriptor> joined = [];
        if (spaceRefs.Count > 0)
        {
            if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var catalogOfSpaces))
            {
                return AltaExitCodes.ServiceUnavailable;
            }

            spaces = catalogOfSpaces;
            var (resolved, exitCode) = await ResolveSpacesAsync(context, catalogOfSpaces, spaceRefs, CommandPath).ConfigureAwait(false);
            if (resolved is null)
            {
                return exitCode;
            }

            // Every project is in the default space already.
            joined = resolved.Where(static space => !space.IsDefault).ToArray();
        }

        var created = await catalog.GetByPathAsync(resolvedPath, context.CancellationToken).ConfigureAwait(false) is null;
        var project = await catalog.UpsertFromPathAsync(resolvedPath, context.CancellationToken).ConfigureAwait(false);
        if (spaces is not null && joined.Count > 0)
        {
            var assigned = await spaces.AssignAsync(project.Id, joined.Select(static space => space.Id).ToArray(), null, context.CancellationToken).ConfigureAwait(false);
            if (!assigned.Succeeded)
            {
                NotifySpacesChanged(context);
                return SpaceChangeFailed(context, assigned, CommandPath, $"The project '{project.Slug}' is in the catalog, but it joined no space: ");
            }

            project = await catalog.GetByIdAsync(project.Id, context.CancellationToken).ConfigureAwait(false) ?? project;
        }

        var record = ProjectRecord(context, "alta.project.added", project);
        record["created"] = created;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static int ProjectEditFailed(AltaCommandContext context, ProjectDisplayNameRenameStatus status, ProjectDescriptor project)
    {
        if (status == ProjectDisplayNameRenameStatus.Conflict)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "project.conflict", AltaExitCodes.Failure,
                $"The file of the project '{project.Slug}' changed meanwhile. Run the command again.");
            return AltaExitCodes.Failure;
        }

        return Unsupported(context, "project.unsupported", $"The file of the project '{project.Slug}' cannot be edited as it is written: {project.SourcePath}");
    }

    private static async ValueTask<int> HandleProjectRenameAsync(AltaCommandContext context, string? reference, string? name)
    {
        const string CommandPath = "alta project rename";
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            return UsageError(context, "usage.missingProject", "Project reference is required.", CommandPath);
        }

        if (NormalizeOptionalText(name) is not { } displayName)
        {
            return UsageError(context, "usage.missingName", "The new name of the project is required.", CommandPath);
        }

        if (await ResolveProjectAsync(catalog, reference, context, includeArchived: true).ConfigureAwait(false) is not { } project)
        {
            return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
        }

        if (await catalog.ReadDisplayNameAsync(project.Id, project.ProjectPath, context.CancellationToken).ConfigureAwait(false) is not { } snapshot)
        {
            return ProjectEditFailed(context, ProjectDisplayNameRenameStatus.Unsupported, project);
        }

        var previousName = snapshot.DisplayName;
        if (!string.Equals(previousName, displayName, StringComparison.Ordinal))
        {
            ProjectDisplayNameRenameStatus status;
            try
            {
                status = await catalog.RenameDisplayNameAsync(project.Id, project.ProjectPath, snapshot.SourcePath, snapshot.Revision, displayName, context.CancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return UsageError(context, "usage.invalidName", "A project name is one line of at most 256 characters.", CommandPath);
            }

            if (status != ProjectDisplayNameRenameStatus.Updated)
            {
                return ProjectEditFailed(context, status, project);
            }

            project = await catalog.GetByIdAsync(project.Id, context.CancellationToken).ConfigureAwait(false) ?? project;
        }

        var record = ProjectRecord(context, "alta.project.renamed", project);
        record["previousName"] = previousName;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleProjectArchiveAsync(AltaCommandContext context, string? reference, bool archive)
    {
        var commandPath = archive ? "alta project archive" : "alta project unarchive";
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            return UsageError(context, "usage.missingProject", "Project reference is required.", commandPath);
        }

        if (await ResolveProjectAsync(catalog, reference, context, includeArchived: true).ConfigureAwait(false) is not { } project)
        {
            return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
        }

        if (await catalog.ReadArchiveAsync(project.Id, project.ProjectPath, context.CancellationToken).ConfigureAwait(false) is not { } snapshot)
        {
            return ProjectEditFailed(context, ProjectDisplayNameRenameStatus.Unsupported, project);
        }

        var changed = snapshot.Archived != archive;
        if (changed)
        {
            var status = await catalog.SetArchivedAsync(project.Id, project.ProjectPath, snapshot.SourcePath, snapshot.Revision, snapshot.Archived, archive, context.CancellationToken).ConfigureAwait(false);
            if (status != ProjectDisplayNameRenameStatus.Updated)
            {
                return ProjectEditFailed(context, status, project);
            }

            project = await catalog.GetByIdAsync(project.Id, context.CancellationToken).ConfigureAwait(false) ?? project;
        }

        var record = ProjectRecord(context, "alta.project.archived", project);
        record["changed"] = changed;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleProjectRemoveAsync(AltaCommandContext context, string? reference, bool deleteSessions)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            return UsageError(context, "usage.missingProject", "Project reference is required.", "alta project remove");
        }

        if (await ResolveProjectAsync(catalog, reference, context, includeArchived: true).ConfigureAwait(false) is not { } project)
        {
            return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
        }

        // A project is never removed without knowing its sessions: they would stay behind with no project to show them in.
        if (await LoadSessionInfosAsync(context).ConfigureAwait(false) is not { } infos)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var sessions = infos.Where(info => string.Equals(info.Session.ProjectRef, project.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sessions.Length > 0)
        {
            if (!deleteSessions)
            {
                return SpaceRefused(context, "project.hasSessions",
                    $"The project '{project.Slug}' has {sessions.Length} session(s) and was not removed. Archive it to keep them (`alta project archive {project.Slug}`), or remove it with --delete-sessions to delete them with it.");
            }

            if (sessions.FirstOrDefault(static info => info.IsRunning) is { } running)
            {
                return SpaceRefused(context, "project.sessionsRunning",
                    $"The session '{running.Session.SessionId}' of the project '{project.Slug}' is running: nothing was removed.");
            }

            if (NormalizeOptionalText(context.Caller.SourceSessionId) is { } caller &&
                sessions.Any(info => string.Equals(info.Session.SessionId, caller, StringComparison.OrdinalIgnoreCase)))
            {
                return SpaceRefused(context, "project.callerSession", "A session does not remove its own project: its own session would be deleted with it.");
            }

            if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
            {
                return AltaExitCodes.ServiceUnavailable;
            }

            foreach (var info in sessions)
            {
                await runtime.DeleteSessionAsync(info.Session, context.CancellationToken).ConfigureAwait(false);
            }
        }

        if (!await catalog.DeleteAsync(project, context.CancellationToken).ConfigureAwait(false))
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "project.removeFailed", AltaExitCodes.Failure,
                $"No catalog file was found for the project '{project.Slug}': nothing was removed.");
            if (sessions.Length > 0) NotifySpacesChanged(context);
            return AltaExitCodes.Failure;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.project.removed",
            version = 1,
            correlationId = context.CorrelationId,
            projectId = project.Id,
            project.Slug,
            project.DisplayName,
            projectPath = project.ProjectPath,
            deletedSessions = sessions.Length,
            deletedSessionIds = sessions.Select(static info => info.Session.SessionId).ToArray(),
            note = "The folder of the project was left as it is.",
        });
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private sealed class SpaceWriteOptions
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public bool UseStdin { get; set; }

        public string? Icon { get; set; }

        public string? Color { get; set; }
    }
}
