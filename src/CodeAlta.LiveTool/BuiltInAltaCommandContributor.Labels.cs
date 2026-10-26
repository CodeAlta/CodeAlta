using CodeAlta.Catalog;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta label` commands: labels name groups of projects inside a space, and can be under one another.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly AltaCommandPolicy[] LabelPolicies =
    [
        Read("label list"),
        Read("label show"),
        Mutating("label create"),
        Mutating("label set"),
        Mutating("label move"),
        Disruptive("label delete"),
        Mutating("label assign"),
        Mutating("label unassign"),
    ];

    private static Command CreateLabelCommand(AltaCommandContext context)
    {
        var group = Group("label", "Use the labels of CodeAlta: names given to projects to find them together, which can be under one another.");
        group.Add(CreateLabelListCommand(context));
        group.Add(CreateLabelShowCommand(context));
        group.Add(CreateLabelCreateCommand(context));
        group.Add(CreateLabelSetCommand(context));
        group.Add(CreateLabelMoveCommand(context));
        group.Add(CreateLabelDeleteCommand(context));
        group.Add(CreateLabelAssignCommand(context, assign: true));
        group.Add(CreateLabelAssignCommand(context, assign: false));
        AddHelpText(
            group,
            "A label groups projects, such as `Infra` or `Client X`; a project can have several labels, and a label can be under another one (`Infra › Helm charts`).",
            "A top-level label is in one space or in every space; its sub-labels are in the same. The CodeAlta window lists the projects of a space under their labels.",
            "<label> is the id of a label, its name, its path (`Infra › Helm` or `Infra/Helm`), or the start of its id when only one label starts so.",
            "When the user asks for work on the projects of a label, list them with `alta label show <label>` or `alta project list --label <label>`.",
            "Examples: `alta label list`; `alta label show infra`; `alta label create \"Helm charts\" --parent infra`; `alta label assign infra CodeAlta C:/code/Tomlyn`.");
        return group;
    }

    private static Command CreateLabelListCommand(AltaCommandContext context)
    {
        string? space = null;
        var all = false;
        var command = Leaf("list", "List the labels in the order of their tree: id, name, path, parent, icon, color, favorite, space and how many projects each has.");
        command.Add("space=", "List the labels of this space: id, start of id, or name.", value => space = value);
        command.Add("all", "List every label, whatever its space.", value => all = value is not null);
        command.Add(async (_, _) => await HandleLabelListAsync(context, space, all).ConfigureAwait(false));
        AddHelpText(command,
            "Without --space or --all, the labels are those of the space of the calling session, else of the space the CodeAlta window shows: the labels of that space and the labels of every space.",
            "`projectCount` counts the projects that have the label itself and are not archived; `alta label show` also lists those of its sub-labels.",
            "Examples: `alta label list`; `alta label list --all`.");
        return command;
    }

    private static Command CreateLabelShowCommand(AltaCommandContext context)
    {
        string? reference = null;
        var includeArchived = false;
        var command = Leaf("show", "Show one label with its sub-labels and its projects, the projects of its sub-labels included.");
        command.Add("<label>", "Label id, name, path, or start of id.", value => reference = value);
        command.Add("include-archived", "Include archived projects.", value => includeArchived = value is not null);
        command.Add(async (_, _) => await HandleLabelShowAsync(context, reference, includeArchived).ConfigureAwait(false));
        AddHelpText(command,
            "Each project names the labels of this tree it has (`labels`). Its `path` is the folder to work in.",
            "Examples: `alta label show infra`; `alta label show \"Infra › Helm charts\"`.");
        return command;
    }

    private static Command CreateLabelCreateCommand(AltaCommandContext context)
    {
        var options = new LabelWriteOptions();
        string? name = null;
        string? parent = null;
        var command = Leaf("create", "Create a label, last among the labels of its parent.");
        command.Add("<name>", "Name of the label, 1 to 64 characters.", value => name = value);
        command.Add("parent=", "The label to put it under: id, name, path, or start of id.", value => parent = value);
        AddLabelWriteOptions(command, options);
        command.Add(async (_, _) => await HandleLabelCreateAsync(context, name, parent, options).ConfigureAwait(false));
        AddHelpText(command,
            "The id of the label is worked out from its name and never changes afterwards.",
            "Without --space, a top-level label is in the space of the calling session; created from the default space, or by the user, it is in every space. A sub-label is in the space of its parent.",
            "Examples: `alta label create Infra --icon cube --color #2d72d2`; `alta label create \"Helm charts\" --parent infra`.");
        return command;
    }

    private static Command CreateLabelSetCommand(AltaCommandContext context)
    {
        var options = new LabelWriteOptions();
        string? reference = null;
        string? name = null;
        var command = Leaf("set", "Change the name, the description, the icon, the color, the favorite or the space of a label. Its id stays.");
        command.Add("<label>", "Label id, name, path, or start of id.", value => reference = value);
        command.Add("name=", "New name, 1 to 64 characters.", value => name = value);
        AddLabelWriteOptions(command, options);
        command.Add("no-favorite", "The label is no longer a favorite.", value => { if (value is not null) options.Favorite = false; });
        command.Add(async (_, _) => await HandleLabelSetAsync(context, reference, name, options).ConfigureAwait(false));
        AddHelpText(command,
            "What is not given stays as it is. An empty value removes a description, an icon or a color, and `--space \"\"` puts a top-level label in every space.",
            "Examples: `alta label set infra --name Infrastructure`; `alta label set infra --favorite`; `alta label set infra --color \"\"`.");
        return command;
    }

    private static void AddLabelWriteOptions(Command command, LabelWriteOptions options)
    {
        command.Add("description=", "What the label stands for. Prefer --stdin for several lines.", value => options.Description = value);
        command.Add("stdin", "Read the description from stdin.", value => options.UseStdin = value is not null);
        command.Add("icon=", "Name of the icon of the label, as the CodeAlta window lists them.", value => options.Icon = value);
        command.Add("color=", "Color of the badge of the label, `#rgb` or `#rrggbb`.", value => options.Color = value);
        command.Add("space=", "Space of a top-level label: id, start of id, or name; empty for every space.", value => options.Space = value);
        command.Add("favorite", "Keep the label on top of the others.", value => { if (value is not null) options.Favorite = true; });
    }

    private static Command CreateLabelMoveCommand(AltaCommandContext context)
    {
        string? reference = null;
        string? parent = null;
        var root = false;
        string? before = null;
        string? after = null;
        var command = Leaf("move", "Move a label, with its sub-labels, under another label or to the top, and before or after a label beside it.");
        command.Add("<label>", "Label id, name, path, or start of id.", value => reference = value);
        command.Add("parent=", "The label to move it under.", value => parent = value);
        command.Add("root", "Move it to the top level.", value => root = value is not null);
        command.Add("before=", "The label it goes before, among its new siblings.", value => before = value);
        command.Add("after=", "The label it goes after, among its new siblings.", value => after = value);
        command.Add(async (_, _) => await HandleLabelMoveAsync(context, reference, parent, root, before, after).ConfigureAwait(false));
        AddHelpText(command,
            "Without --parent or --root, the label stays under its parent, or goes under the parent of the label of --before or --after. Without --before or --after, it goes last.",
            "Examples: `alta label move helm --parent infra`; `alta label move terraform --before infra`; `alta label move helm --root`.");
        return command;
    }

    private static Command CreateLabelDeleteCommand(AltaCommandContext context)
    {
        string? reference = null;
        var command = Leaf("delete", "Delete a label. Its projects lose it and stay; its sub-labels take its place.");
        command.Add("<label>", "Label id, name, path, or start of id.", value => reference = value);
        command.Add(async (_, _) => await HandleLabelDeleteAsync(context, reference).ConfigureAwait(false));
        AddHelpText(command, "Delete only the labels you created, or the ones the user asks you to remove.");
        return command;
    }

    private static Command CreateLabelAssignCommand(AltaCommandContext context, bool assign)
    {
        var references = new List<string>();
        var command = assign
            ? Leaf("assign", "Give a label to projects. They keep their other labels.")
            : Leaf("unassign", "Take a label from projects.");
        command.Add("<label>", "Label id, name, path, or start of id.", value => { if (value is not null) references.Add(value); });
        command.Add("<project>*", "Projects: id, slug or path.", value => { if (value is not null) references.Add(value); });
        command.Add(async (_, _) => await HandleLabelAssignAsync(context, references, assign).ConfigureAwait(false));
        AddHelpText(command, assign ? "Example: `alta label assign infra CodeAlta C:/code/Tomlyn`." : "Example: `alta label unassign infra CodeAlta`.");
        return command;
    }

    // The space whose labels a command sees: the space of the calling session, else none (the user sees every label).
    private static async Task<SpaceDescriptor?> LabelCallerSpaceAsync(AltaCommandContext context, SpaceCatalog spaces)
        => await CallerSpaceAsync(context, spaces).ConfigureAwait(false);

    // A label by its id, then its name or its path, then the start of its id when only one label starts so; only among
    // the labels the calling session sees.
    private static (LabelDescriptor? Label, int ExitCode) ResolveLabel(AltaCommandContext context, IReadOnlyList<LabelDescriptor> labels,
        string? reference, string commandPath)
    {
        if (NormalizeOptionalText(reference) is not { } text)
        {
            return (null, UsageError(context, "usage.missingLabel", "A label is required: its id, its name, its path, or the start of its id.", commandPath));
        }

        if (labels.FirstOrDefault(label => string.Equals(label.Id, text, StringComparison.OrdinalIgnoreCase)) is { } byId)
        {
            return (byId, AltaExitCodes.Success);
        }

        var path = NormalizeLabelPath(text);
        var named = labels.Where(label => string.Equals(label.Name, text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeLabelPath(LabelCatalog.PathOf(label, labels)), path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 1)
        {
            return (named[0], AltaExitCodes.Success);
        }

        if (named.Length > 1)
        {
            return (null, UsageError(context, "usage.ambiguousLabel",
                $"Several labels are named '{text}': {string.Join(", ", named.Select(label => $"{label.Id} ({LabelCatalog.PathOf(label, labels)})"))}. Use the id or the path.", commandPath));
        }

        var starting = labels.Where(label => label.Id.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        return starting.Length switch
        {
            1 => (starting[0], AltaExitCodes.Success),
            0 => (null, NotFound(context, "label.notFound", $"No label has the id, the name or the path '{text}'. List them with `alta label list`.")),
            _ => (null, UsageError(context, "usage.ambiguousLabel",
                $"Several labels have an id that starts with '{text}': {string.Join(", ", starting.Select(static label => label.Id))}.", commandPath)),
        };
    }

    private static string NormalizeLabelPath(string path)
        => string.Join('/', path.Split(['/', '›'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // The labels the calling session sees: those of its space and those of every space; every label for the user.
    private static async Task<(IReadOnlyList<LabelDescriptor> Labels, SpaceDescriptor? Own)> VisibleLabelsAsync(AltaCommandContext context, SpaceCatalog spaces)
    {
        var labels = await spaces.Labels.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var own = await LabelCallerSpaceAsync(context, spaces).ConfigureAwait(false);
        return (own is null ? labels : labels.Where(label => label.IsIn(own.Id)).ToArray(), own);
    }

    private static int LabelChangeFailed(AltaCommandContext context, LabelChange change, string commandPath, string? prefix = null)
    {
        var message = prefix + (change.Message ?? "The label could not be changed.");
        switch (change.Status)
        {
            case LabelChangeStatus.NotFound:
                return NotFound(context, "label.notFound", message);
            case LabelChangeStatus.Invalid:
                return UsageError(context, "label.invalid", message, commandPath);
            case LabelChangeStatus.Refused:
                return SpaceRefused(context, "label.refused", message);
            default:
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "label.conflict", AltaExitCodes.Failure, message);
                return AltaExitCodes.Failure;
        }
    }

    private static Dictionary<string, object?> LabelRecord(AltaCommandContext context, string type, LabelDescriptor label, IReadOnlyList<LabelDescriptor> labels, int? projectCount)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = label.Id,
            ["name"] = label.Name,
            ["path"] = LabelCatalog.PathOf(label, labels),
        };
        if (label.Parent is not null) record["parent"] = label.Parent;
        if (label.Description is not null) record["description"] = label.Description;
        if (label.Icon is not null) record["icon"] = label.Icon;
        if (label.Color is not null) record["color"] = label.Color;
        record["favorite"] = label.Favorite;
        if (label.Space is not null) record["space"] = label.Space;
        record["order"] = label.Order;
        if (projectCount is not null) record["projectCount"] = projectCount.Value;
        return record;
    }

    private static async Task<(string? Description, int ExitCode)> ReadLabelDescriptionAsync(AltaCommandContext context, LabelWriteOptions options, string commandPath)
    {
        if (!options.UseStdin)
        {
            return (options.Description, AltaExitCodes.Success);
        }

        if (options.Description is not null)
        {
            return (null, UsageError(context, "usage.contentConflict", "Use either --description or --stdin, not both.", commandPath));
        }

        return (await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false), AltaExitCodes.Success);
    }

    // The space given to --space: null for none asked, empty for every space. A session names only its own space.
    private static async Task<(string? SpaceId, int ExitCode)> ResolveLabelSpaceAsync(AltaCommandContext context, SpaceCatalog spaces, string? reference,
        SpaceDescriptor? own, string commandPath)
    {
        if (reference is null) return (null, AltaExitCodes.Success);
        if (NormalizeOptionalText(reference) is null) return (string.Empty, AltaExitCodes.Success);
        var (space, exitCode) = await ResolveSpaceAsync(context, spaces, reference, commandPath).ConfigureAwait(false);
        if (space is null) return (null, exitCode);
        if (own is not null && own.Id != space.Id)
        {
            return (null, SpaceRefused(context, "space.notOwnSpace",
                $"A session puts labels only in its own space. This session is in the space '{own.Name}' ({own.Id}), not in '{space.Name}' ({space.Id}): ask the user to do it from the CodeAlta window."));
        }

        return (space.Id, AltaExitCodes.Success);
    }

    private static async ValueTask<int> HandleLabelListAsync(AltaCommandContext context, string? spaceRef, bool all)
    {
        const string CommandPath = "alta label list";
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (all && NormalizeOptionalText(spaceRef) is not null)
        {
            return UsageError(context, "usage.scopeConflict", "Use either --space or --all, not both.", CommandPath);
        }

        var (visible, own) = await VisibleLabelsAsync(context, spaces).ConfigureAwait(false);
        string? spaceId = null;
        if (NormalizeOptionalText(spaceRef) is not null)
        {
            var (space, exitCode) = await ResolveSpaceAsync(context, spaces, spaceRef, CommandPath).ConfigureAwait(false);
            if (space is null) return exitCode;
            spaceId = space.Id;
        }
        else if (!all)
        {
            spaceId = own?.Id ?? (await ShownSpaceAsync(context, spaces).ConfigureAwait(false))?.Id;
        }

        var members = await spaces.Labels.LoadMembersAsync(includeArchived: false, context.CancellationToken).ConfigureAwait(false);
        var labels = members.Select(static member => member.Label).ToArray();
        var listed = members.Where(member => visible.Any(label => label.Id == member.Label.Id) && (spaceId is null || member.Label.IsIn(spaceId))).ToArray();
        foreach (var member in listed)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, LabelRecord(context, "alta.label.item", member.Label, labels, member.ProjectIds.Count));
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.label.summary",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["count"] = listed.Length,
            ["truncated"] = false,
            ["spaceId"] = spaceId,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleLabelShowAsync(AltaCommandContext context, string? reference, bool includeArchived)
    {
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (visible, _) = await VisibleLabelsAsync(context, spaces).ConfigureAwait(false);
        var (label, exitCode) = ResolveLabel(context, visible, reference, "alta label show");
        if (label is null)
        {
            return exitCode;
        }

        await WriteLabelDetailAsync(context, spaces, label, "alta.label.detail", includeArchived).ConfigureAwait(false);
        return AltaExitCodes.Success;
    }

    private static async Task WriteLabelDetailAsync(AltaCommandContext context, SpaceCatalog spaces, LabelDescriptor label, string type, bool includeArchived)
    {
        var labels = await spaces.Labels.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var tree = LabelCatalog.Subtree(label.Id, labels);
        var projects = (await spaces.Projects.LoadAsync(context.CancellationToken).ConfigureAwait(false))
            .Where(project => (includeArchived || !project.Archived) && project.Labels.Any(tree.Contains))
            .OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var record = LabelRecord(context, type, label, labels, projects.Count(project => project.Labels.Contains(label.Id, StringComparer.Ordinal)));
        record["subLabels"] = tree.Skip(1).ToArray();
        record["projects"] = projects.Select(project => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = project.Id,
            ["slug"] = project.Slug,
            ["name"] = project.DisplayName,
            ["path"] = project.ProjectPath,
            ["archived"] = project.Archived,
            ["labels"] = project.Labels.Where(tree.Contains).ToArray(),
        }).ToArray();
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
    }

    private static async ValueTask<int> HandleLabelCreateAsync(AltaCommandContext context, string? name, string? parentRef, LabelWriteOptions options)
    {
        const string CommandPath = "alta label create";
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(name) is not { } labelName)
        {
            return UsageError(context, "usage.missingName", "A label needs a name: `alta label create <name>`.", CommandPath);
        }

        var (description, descriptionExit) = await ReadLabelDescriptionAsync(context, options, CommandPath).ConfigureAwait(false);
        if (descriptionExit != AltaExitCodes.Success)
        {
            return descriptionExit;
        }

        var (visible, own) = await VisibleLabelsAsync(context, spaces).ConfigureAwait(false);
        LabelDescriptor? parent = null;
        if (NormalizeOptionalText(parentRef) is not null)
        {
            (parent, var exitCode) = ResolveLabel(context, visible, parentRef, CommandPath);
            if (parent is null) return exitCode;
        }

        var (space, spaceExit) = await ResolveLabelSpaceAsync(context, spaces, options.Space, own, CommandPath).ConfigureAwait(false);
        if (spaceExit != AltaExitCodes.Success)
        {
            return spaceExit;
        }

        // A session that names no space puts a top-level label in its own space, unless that is the default space.
        space ??= own is { IsDefault: false } ? own.Id : null;
        var created = await spaces.Labels.CreateAsync(labelName, parent?.Id, options.Icon, options.Color, NormalizeOptionalText(space), options.Favorite ?? false,
            description, context.CancellationToken).ConfigureAwait(false);
        if (created.Label is not { } label || !created.Succeeded)
        {
            return LabelChangeFailed(context, created, CommandPath);
        }

        await WriteLabelDetailAsync(context, spaces, label, "alta.label.created", includeArchived: false).ConfigureAwait(false);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleLabelSetAsync(AltaCommandContext context, string? reference, string? name, LabelWriteOptions options)
    {
        const string CommandPath = "alta label set";
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (visible, own) = await VisibleLabelsAsync(context, spaces).ConfigureAwait(false);
        var (label, exitCode) = ResolveLabel(context, visible, reference, CommandPath);
        if (label is null)
        {
            return exitCode;
        }

        var (description, descriptionExit) = await ReadLabelDescriptionAsync(context, options, CommandPath).ConfigureAwait(false);
        if (descriptionExit != AltaExitCodes.Success)
        {
            return descriptionExit;
        }

        var (space, spaceExit) = await ResolveLabelSpaceAsync(context, spaces, options.Space, own, CommandPath).ConfigureAwait(false);
        if (spaceExit != AltaExitCodes.Success)
        {
            return spaceExit;
        }

        if (name is null && description is null && options.Icon is null && options.Color is null && options.Favorite is null && space is null)
        {
            return UsageError(context, "usage.missingChange", "Nothing to change: use --name, --description, --stdin, --icon, --color, --favorite, --no-favorite or --space.", CommandPath);
        }

        var updated = await spaces.Labels.UpdateAsync(label.Id, new LabelEdit(name, description, options.Icon, options.Color, options.Favorite, space),
            context.CancellationToken).ConfigureAwait(false);
        if (updated.Label is not { } after || !updated.Succeeded)
        {
            return LabelChangeFailed(context, updated, CommandPath);
        }

        await WriteLabelDetailAsync(context, spaces, after, "alta.label.updated", includeArchived: false).ConfigureAwait(false);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleLabelMoveAsync(AltaCommandContext context, string? reference, string? parentRef, bool root, string? beforeRef, string? afterRef)
    {
        const string CommandPath = "alta label move";
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (root && NormalizeOptionalText(parentRef) is not null)
        {
            return UsageError(context, "usage.scopeConflict", "Use either --parent or --root, not both.", CommandPath);
        }

        if (NormalizeOptionalText(beforeRef) is not null && NormalizeOptionalText(afterRef) is not null)
        {
            return UsageError(context, "usage.scopeConflict", "Use either --before or --after, not both.", CommandPath);
        }

        var (visible, _) = await VisibleLabelsAsync(context, spaces).ConfigureAwait(false);
        var (label, exitCode) = ResolveLabel(context, visible, reference, CommandPath);
        if (label is null)
        {
            return exitCode;
        }

        var parent = label.Parent;
        if (root)
        {
            parent = null;
        }
        else if (NormalizeOptionalText(parentRef) is not null)
        {
            var (above, parentExit) = ResolveLabel(context, visible, parentRef, CommandPath);
            if (above is null) return parentExit;
            parent = above.Id;
        }

        var index = -1;
        if (NormalizeOptionalText(beforeRef ?? afterRef) is not null)
        {
            var (beside, besideExit) = ResolveLabel(context, visible, beforeRef ?? afterRef, CommandPath);
            if (beside is null) return besideExit;
            if (beside.Id == label.Id)
            {
                return UsageError(context, "label.invalid", "A label goes before or after another label, not itself.", CommandPath);
            }

            if (!root && NormalizeOptionalText(parentRef) is null) parent = beside.Parent;
            if (beside.Parent != parent)
            {
                return UsageError(context, "label.invalid", $"The label '{beside.Id}' is not under the same parent as where '{label.Id}' goes.", CommandPath);
            }

            // The labels of other spaces are among the siblings in the catalog: the place is counted among all of them.
            var siblings = (await spaces.Labels.LoadAsync(context.CancellationToken).ConfigureAwait(false))
                .Where(value => value.Parent == parent && value.Id != label.Id).Select(static value => value.Id).ToList();
            index = siblings.IndexOf(beside.Id) + (NormalizeOptionalText(afterRef) is not null ? 1 : 0);
        }

        var moved = await spaces.Labels.MoveAsync(label.Id, parent, index, context.CancellationToken).ConfigureAwait(false);
        if (moved.Label is not { } after || !moved.Succeeded)
        {
            return LabelChangeFailed(context, moved, CommandPath);
        }

        await WriteLabelDetailAsync(context, spaces, after, "alta.label.moved", includeArchived: false).ConfigureAwait(false);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleLabelDeleteAsync(AltaCommandContext context, string? reference)
    {
        const string CommandPath = "alta label delete";
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (visible, _) = await VisibleLabelsAsync(context, spaces).ConfigureAwait(false);
        var (label, exitCode) = ResolveLabel(context, visible, reference, CommandPath);
        if (label is null)
        {
            return exitCode;
        }

        var labels = await spaces.Labels.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var path = LabelCatalog.PathOf(label, labels);
        var deleted = await spaces.Labels.DeleteAsync(label.Id, context.CancellationToken).ConfigureAwait(false);
        if (!deleted.Succeeded)
        {
            return LabelChangeFailed(context, deleted, CommandPath);
        }

        var record = LabelRecord(context, "alta.label.deleted", label, labels, projectCount: null);
        record["path"] = path;
        record["note"] = "The projects of the label lost it and stay in the catalog; its sub-labels took its place.";
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleLabelAssignAsync(AltaCommandContext context, IReadOnlyList<string> references, bool assign)
    {
        var commandPath = assign ? "alta label assign" : "alta label unassign";
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (visible, _) = await VisibleLabelsAsync(context, spaces).ConfigureAwait(false);
        var (label, exitCode) = ResolveLabel(context, visible, references.Count > 0 ? references[0] : null, commandPath);
        if (label is null)
        {
            return exitCode;
        }

        if (references.Count < 2)
        {
            return UsageError(context, "usage.missingProject", "At least one project is required: its id, its slug or its path.", commandPath);
        }

        var projects = new List<ProjectDescriptor>(references.Count - 1);
        foreach (var reference in references.Skip(1))
        {
            if (await ResolveProjectAsync(spaces.Projects, reference, context, includeArchived: true).ConfigureAwait(false) is not { } project)
            {
                return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
            }

            if (!projects.Any(other => other.Id == project.Id)) projects.Add(project);
        }

        var changed = new List<string>(projects.Count);
        var unchanged = new List<string>();
        foreach (var project in projects)
        {
            var had = project.Labels.Contains(label.Id, StringComparer.Ordinal);
            var result = await spaces.Labels.AssignAsync(project.Id, assign ? [label.Id] : null, assign ? null : [label.Id], context.CancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                if (changed.Count > 0) NotifySpacesChanged(context);
                return LabelChangeFailed(context, result, commandPath);
            }

            (had == assign ? unchanged : changed).Add(project.Id);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.label.projects",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["labelId"] = label.Id,
            [assign ? "assigned" : "unassigned"] = changed,
            ["unchanged"] = unchanged,
        });
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    // The labels whose projects `alta project list --label` lists: the label and, unless --direct, those under it.
    private static async Task<(IReadOnlyList<string>? Ids, int ExitCode)> ResolveProjectListLabelAsync(AltaCommandContext context, string? reference, bool direct)
    {
        if (NormalizeOptionalText(reference) is null) return (null, AltaExitCodes.Success);
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces)) return (null, AltaExitCodes.ServiceUnavailable);
        var (visible, _) = await VisibleLabelsAsync(context, spaces).ConfigureAwait(false);
        var (label, exitCode) = ResolveLabel(context, visible, reference, "alta project list");
        if (label is null) return (null, exitCode);
        return (direct ? [label.Id] : LabelCatalog.Subtree(label.Id, visible), AltaExitCodes.Success);
    }

    private sealed class LabelWriteOptions
    {
        public string? Description { get; set; }

        public bool UseStdin { get; set; }

        public string? Icon { get; set; }

        public string? Color { get; set; }

        public string? Space { get; set; }

        public bool? Favorite { get; set; }
    }
}
