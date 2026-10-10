using System.Text.Json;
using CodeAlta.Catalog;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta canvas` commands: the tabs that plugins provide in the window. Listing, showing and invoking need no
// window; opening, focusing and closing a tab do.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly AltaCommandPolicy[] CanvasPolicies =
    [
        Read("canvas list"),
        Read("canvas show"),
        // They change nothing but what the window shows.
        Read("canvas open"),
        Read("canvas focus"),
        Read("canvas close"),
        Mutating("canvas invoke"),
    ];

    private static Command CreateCanvasCommand(AltaCommandContext context)
    {
        var group = Group("canvas", "Use the canvases that plugins provide: tabs of the CodeAlta window that a plugin fills and keeps up to date.");
        group.Add(CreateCanvasListCommand(context));
        group.Add(CreateCanvasShowCommand(context));
        group.Add(CreateCanvasOpenCommand(context, "open", "Open the tab of a canvas in the CodeAlta window, or bring it to the front.", focusOnly: false));
        group.Add(CreateCanvasOpenCommand(context, "focus", "Bring the tab of a canvas that is open to the front.", focusOnly: true));
        group.Add(CreateCanvasCloseCommand(context));
        group.Add(CreateCanvasInvokeCommand(context));
        AddHelpText(
            group,
            "A canvas is declared by a plugin and shows what the plugin keeps: a board, a checklist, a dashboard. The plugin holds the state, so an action works whether the tab is open or not.",
            "<id> is `plugin-key/canvas-id`, or the canvas id alone when only one plugin declares it. A canvas is about the application, a project (`--project`) or a session (`--session`).",
            "Create a canvas with `alta plugin create`, as the `codealta-plugin-runtime` skill says; the window lists what plugins declare in its Canvases page.",
            "Examples: `alta canvas list`; `alta canvas show checklist`; `alta canvas open checklist --project CodeAlta`; `alta canvas invoke checklist tick --project CodeAlta --stdin`.");
        return group;
    }

    private static Command CreateCanvasListCommand(AltaCommandContext context)
    {
        string? plugin = null, space = null;
        var open = false;
        var all = false;
        var command = Leaf("list", "List the canvases that plugins declare, or the tabs that are open.");
        command.Add("plugin=", "Only the canvases of this plugin: its key or its name.", value => plugin = value);
        command.Add("open", "List the open tabs instead of the declared canvases.", value => open = value is not null);
        command.Add("space=", "With --open: the tabs of this space: id, start of id, or name. Without it, the space the window shows.", value => space = value);
        command.Add("all", "With --open: the tabs of every space.", value => all = value is not null);
        command.Add(async (_, _) => await HandleCanvasListAsync(context, plugin, open, space, all).ConfigureAwait(false));
        AddHelpText(command,
            "The declared canvases are the same in every space. `ref` is what the other commands take as <id>.",
            "Examples: `alta canvas list`; `alta canvas list --plugin statistics`; `alta canvas list --open`; `alta canvas list --open --all`.");
        return command;
    }

    private static Command CreateCanvasShowCommand(AltaCommandContext context)
    {
        string? reference = null, project = null, session = null, key = null;
        var command = Leaf("show", "Show a canvas: its declaration, its actions with their schemas, and what it shows now.");
        command.Add("<id>", "Canvas id: `plugin-key/canvas-id`, or the canvas id alone.", value => reference = value);
        AddCanvasContextOptions(command, value => project = value, value => session = value, value => key = value);
        command.Add(async (_, _) => await HandleCanvasShowAsync(context, reference, project, session, key).ConfigureAwait(false));
        AddHelpText(command,
            "`markdown` is what the canvas shows now, when it can describe itself: read it instead of looking at a screenshot. A canvas of a project or of a session describes itself once you name the project or the session.",
            "Examples: `alta canvas show statistics`; `alta canvas show checklist --project CodeAlta`.");
        return command;
    }

    private static Command CreateCanvasOpenCommand(AltaCommandContext context, string name, string description, bool focusOnly)
    {
        string? reference = null, project = null, session = null, key = null, space = null;
        var stdin = false;
        var command = Leaf(name, description);
        command.Add("<id>", "Canvas id: `plugin-key/canvas-id`, or the canvas id alone.", value => reference = value);
        AddCanvasContextOptions(command, value => project = value, value => session = value, value => key = value);
        command.Add("space=", "The space to show the tab in: id, start of id, or name. Without it, the space the window shows when it has the project or the session, else the first space of the project.", value => space = value);
        if (!focusOnly)
        {
            command.Add("stdin", "Read the JSON input of the canvas from stdin.", value => stdin = value is not null);
        }

        command.Add(async (_, _) => await HandleCanvasOpenAsync(context, reference, project, session, key, space, stdin, focusOnly).ConfigureAwait(false));
        AddHelpText(command,
            "The window shows one space at a time. For another space the tab is added to that space and the window stays where the user is: the answer says `shown: false` and the space, and the user finds the canvas when they show it.",
            "When the user asks you to show it, run `alta space switch <space>` first, then this command. Nothing here changes a setting of the user.",
            focusOnly
                ? "Examples: `alta canvas focus checklist --project CodeAlta`."
                : "Examples: `alta canvas open statistics`; `alta canvas open checklist --project CodeAlta`; `alta canvas open board --session <id> --stdin` with the JSON input on stdin.");
        return command;
    }

    private static Command CreateCanvasCloseCommand(AltaCommandContext context)
    {
        string? reference = null, project = null, session = null, key = null, space = null;
        var command = Leaf("close", "Close the tab of a canvas. The plugin keeps its state, so nothing is lost.");
        command.Add("<id>", "Canvas id: `plugin-key/canvas-id`, or the canvas id alone.", value => reference = value);
        AddCanvasContextOptions(command, value => project = value, value => session = value, value => key = value);
        command.Add("space=", "The space of the tab: id, start of id, or name. Without it, the same space `alta canvas open` uses.", value => space = value);
        command.Add(async (_, _) => await HandleCanvasCloseAsync(context, reference, project, session, key, space).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta canvas close checklist --project CodeAlta`.");
        return command;
    }

    private static Command CreateCanvasInvokeCommand(AltaCommandContext context)
    {
        string? reference = null, action = null, project = null, session = null, key = null;
        var stdin = false;
        var command = Leaf("invoke", "Run an action that a canvas declares, and print its JSON result.");
        command.Add("<id>", "Canvas id: `plugin-key/canvas-id`, or the canvas id alone.", value => reference = value);
        command.Add("<action>", "The name of the action, as `alta canvas show` lists it.", value => action = value);
        AddCanvasContextOptions(command, value => project = value, value => session = value, value => key = value);
        command.Add("stdin", "Read the JSON input of the action from stdin.", value => stdin = value is not null);
        command.Add(async (_, _) => await HandleCanvasInvokeAsync(context, reference, action, project, session, key, stdin).ConfigureAwait(false));
        AddHelpText(command,
            "It works whether the tab is open or not, and without a window: the plugin holds the state. The input must match the schema that `alta canvas show` prints for the action.",
            "Example: `alta canvas invoke checklist tick --project CodeAlta --stdin` with `{\"step\":\"tag\"}` on stdin.");
        return command;
    }

    private static void AddCanvasContextOptions(Command command, Action<string?> project, Action<string?> session, Action<string?> key)
    {
        command.Add("project=", "Project id, slug or path, for a canvas of a project. Defaults to the project of the calling session, then to the cwd.", project);
        command.Add("session=", "Session id, for a canvas of a session. Defaults to the calling session.", session);
        command.Add("key=", "The key that tells apart several instances of the canvas in the same context.", key);
    }

    private static string CanvasRef(string pluginKey, string canvasId) => $"{pluginKey}/{canvasId}";

    // `plugin-key/canvas-id`, or the id of a canvas that one plugin only declares.
    private static (AltaCanvasDeclaration? Canvas, int ExitCode) ResolveCanvas(AltaCommandContext context, IAltaCanvasView view, string? reference, string commandPath)
    {
        if (NormalizeOptionalText(reference) is not { } text)
        {
            return (null, UsageError(context, "usage.missingCanvas", "A canvas is required: `plugin-key/canvas-id`, or the id of the canvas.", commandPath));
        }

        var canvases = view.List();
        var slash = text.LastIndexOf('/');
        if (slash >= 0)
        {
            var pluginKey = text[..slash];
            var id = text[(slash + 1)..];
            var exact = canvases.FirstOrDefault(canvas => string.Equals(canvas.PluginKey, pluginKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(canvas.Id, id, StringComparison.OrdinalIgnoreCase));
            return exact is not null
                ? (exact, AltaExitCodes.Success)
                : (null, NotFound(context, "canvas.notFound", $"No active plugin declares the canvas '{text}'. List them with `alta canvas list`."));
        }

        var named = canvases.Where(canvas => string.Equals(canvas.Id, text, StringComparison.OrdinalIgnoreCase)).ToArray();
        return named.Length switch
        {
            1 => (named[0], AltaExitCodes.Success),
            0 => (null, NotFound(context, "canvas.notFound", $"No active plugin declares the canvas '{text}'. List them with `alta canvas list`.")),
            _ => (null, UsageError(context, "usage.ambiguousCanvas",
                $"Several plugins declare a canvas '{text}': {string.Join(", ", named.Select(canvas => CanvasRef(canvas.PluginKey, canvas.Id)))}. Use `plugin-key/canvas-id`.", commandPath)),
        };
    }

    // What the instance of a canvas is about: the project and the session its scope needs, from the options and the caller.
    private sealed record CanvasContext(string? ProjectId, string? SessionId, string? ProjectName, ProjectDescriptor? Project);

    private static async Task<(CanvasContext? Context, int ExitCode)> ResolveCanvasContextAsync(AltaCommandContext context, AltaCanvasDeclaration canvas, string? projectRef, string? sessionRef,
        string commandPath, bool required)
    {
        switch (canvas.Scope)
        {
            case AltaCanvasScopes.Project:
            {
                if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
                {
                    return (null, AltaExitCodes.ServiceUnavailable);
                }

                var reference = NormalizeOptionalText(projectRef) ?? NormalizeOptionalText(context.Caller.SourceProjectId);
                ProjectDescriptor? project = reference is not null
                    ? await ResolveProjectAsync(catalog, reference, context, includeArchived: false).ConfigureAwait(false)
                    : NormalizeOptionalText(context.Cwd) is { } cwd ? await catalog.GetByPathAsync(ResolvePath(context, cwd), context.CancellationToken).ConfigureAwait(false) : null;
                if (project is null || project.Archived)
                {
                    if (!required && reference is null)
                    {
                        return (new CanvasContext(null, null, null, null), AltaExitCodes.Success);
                    }

                    return (null, reference is null
                        ? UsageError(context, "usage.missingProject", $"The canvas '{CanvasRef(canvas.PluginKey, canvas.Id)}' is about a project: add `--project <project>`.", commandPath)
                        : NotFound(context, "project.notFound", $"Project '{reference}' was not found."));
                }

                // The id the window uses, not the slug.
                return (new CanvasContext(project.Id, null, project.DisplayName, project), AltaExitCodes.Success);
            }

            case AltaCanvasScopes.Session:
            {
                var reference = NormalizeOptionalText(sessionRef) ?? NormalizeOptionalText(context.Caller.SourceSessionId);
                if (reference is null)
                {
                    return required
                        ? (null, UsageError(context, "usage.missingSession", $"The canvas '{CanvasRef(canvas.PluginKey, canvas.Id)}' is about a session: add `--session <id>`.", commandPath))
                        : (new CanvasContext(null, null, null, null), AltaExitCodes.Success);
                }

                var resolved = await ResolveSessionInfoAsync(context, reference).ConfigureAwait(false);
                if (resolved.ExitCode != AltaExitCodes.Success)
                {
                    return (null, resolved.ExitCode);
                }

                var info = resolved.Info!.Session;
                ProjectDescriptor? project = null;
                if (NormalizeOptionalText(info.ProjectRef) is { } projectId && context.Services.Get<ProjectCatalog>() is { } projects)
                {
                    project = await projects.GetByIdAsync(projectId, context.CancellationToken).ConfigureAwait(false);
                }

                return (new CanvasContext(NormalizeOptionalText(info.ProjectRef), info.SessionId, project?.DisplayName, project), AltaExitCodes.Success);
            }

            default:
                return (new CanvasContext(null, null, null, null), AltaExitCodes.Success);
        }
    }

    // The space of a tab: the one named; else the space the window shows when it has the project or the session (or the canvas is
    // about the application); else the first space of the project, the rule the other commands use for a project that is out of view.
    // Null when the host says no space: the window then uses the one it shows.
    private static async Task<(string? SpaceId, string? SpaceName, int ExitCode)> ResolveCanvasSpaceAsync(AltaCommandContext context, AltaCanvasDeclaration canvas,
        CanvasContext target, string? spaceRef, string commandPath)
    {
        var inSpaces = context.Services.Get<SpaceCatalog>();
        if (NormalizeOptionalText(spaceRef) is { } reference)
        {
            if (inSpaces is null)
            {
                return (null, null, Unsupported(context, "space.unavailable", "This host keeps no spaces: leave out --space."));
            }

            var (named, exitCode) = await ResolveSpaceAsync(context, inSpaces, reference, commandPath).ConfigureAwait(false);
            if (named is null)
            {
                return (null, null, exitCode);
            }

            if (target.Project is { } owner && !named.IsDefault && !owner.Spaces.Contains(named.Id, StringComparer.Ordinal))
            {
                return (null, null, Unsupported(context, "project.notInSpace",
                    $"The space '{named.Name}' does not have the project '{owner.DisplayName}', so a canvas about it cannot be shown there. Add the project with `alta space add {named.Id} {owner.Slug}`, or name another space."));
            }

            return (named.Id, named.Name, AltaExitCodes.Success);
        }

        if (inSpaces is null)
        {
            return (null, null, AltaExitCodes.Success);
        }

        var shown = await ShownSpaceAsync(context, inSpaces).ConfigureAwait(false);
        if (canvas.Scope == AltaCanvasScopes.Application || target.Project is null)
        {
            return (shown?.Id, shown?.Name, AltaExitCodes.Success);
        }

        if (await ProjectOutOfViewAsync(context, target.Project).ConfigureAwait(false) is { } elsewhere)
        {
            return (elsewhere.Home.Id, elsewhere.Home.Name, AltaExitCodes.Success);
        }

        return (shown?.Id, shown?.Name, AltaExitCodes.Success);
    }

    private static AltaCanvasTarget CanvasTargetOf(AltaCanvasDeclaration canvas, CanvasContext target, string? spaceId, string? key)
        => new(canvas.PluginKey, canvas.Id, spaceId, target.ProjectId, target.SessionId, NormalizeOptionalText(key));

    private static Dictionary<string, object?> CanvasItemRecord(AltaCommandContext context, string type, AltaCanvasDeclaration canvas)
        => new(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["ref"] = CanvasRef(canvas.PluginKey, canvas.Id),
            ["pluginKey"] = canvas.PluginKey,
            ["plugin"] = canvas.Plugin,
            ["canvasId"] = canvas.Id,
            ["title"] = canvas.Title,
            ["description"] = canvas.Description,
            ["icon"] = canvas.Icon,
            ["scope"] = canvas.Scope,
            ["input"] = canvas.InputSchema is not null,
            ["actions"] = canvas.Actions.Count,
            ["describes"] = canvas.Describes,
        };

    private static bool CanvasOfPlugin(AltaCanvasDeclaration canvas, string? plugin)
        => plugin is null || string.Equals(canvas.PluginKey, plugin, StringComparison.OrdinalIgnoreCase) || string.Equals(canvas.Plugin, plugin, StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<int> HandleCanvasListAsync(AltaCommandContext context, string? pluginFilter, bool open, string? spaceRef, bool all)
    {
        const string CommandPath = "alta canvas list";
        if (!TryGetCanvasView(context, out var view))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var plugin = NormalizeOptionalText(pluginFilter);
        if (!open)
        {
            if (spaceRef is not null || all)
            {
                return UsageError(context, "usage.scopeConflict", "--space and --all go with --open: the declared canvases are the same in every space.", CommandPath);
            }

            var declared = view.List().Where(canvas => CanvasOfPlugin(canvas, plugin)).ToArray();
            foreach (var canvas in declared)
            {
                WriteCanvasRecord(context, CanvasItemRecord(context, "alta.canvas.item", canvas));
            }

            WriteSummary(context, "alta.canvas.summary", declared.Length, truncated: false);
            return AltaExitCodes.Success;
        }

        if (all && NormalizeOptionalText(spaceRef) is not null)
        {
            return UsageError(context, "usage.scopeConflict", "Use either --space or --all, not both.", CommandPath);
        }

        // Which tabs: the space named, else the space the window shows, else (no spaces to tell) every tab.
        string? spaceId = null;
        var spaces = context.Services.Get<SpaceCatalog>();
        if (!all && spaces is not null)
        {
            if (NormalizeOptionalText(spaceRef) is { } named)
            {
                var (space, exitCode) = await ResolveSpaceAsync(context, spaces, named, CommandPath).ConfigureAwait(false);
                if (space is null)
                {
                    return exitCode;
                }

                spaceId = space.Id;
            }
            else
            {
                spaceId = (await ShownSpaceAsync(context, spaces).ConfigureAwait(false))?.Id ?? SpaceDescriptor.DefaultId;
            }
        }
        else if (!all && NormalizeOptionalText(spaceRef) is not null)
        {
            return Unsupported(context, "space.unavailable", "This host keeps no spaces: leave out --space.");
        }

        var canvases = view.List();
        var instances = view.ListOpen()
            .Where(instance => (spaceId is null || string.Equals(instance.SpaceId ?? SpaceDescriptor.DefaultId, spaceId, StringComparison.Ordinal))
                && (plugin is null || canvases.Any(canvas => string.Equals(canvas.PluginKey, instance.PluginKey, StringComparison.Ordinal)
                    && string.Equals(canvas.Id, instance.CanvasId, StringComparison.Ordinal) && CanvasOfPlugin(canvas, plugin))))
            .ToArray();
        foreach (var instance in instances)
        {
            WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "alta.canvas.instance",
                ["version"] = 1,
                ["correlationId"] = context.CorrelationId,
                ["instanceId"] = instance.InstanceId,
                ["ref"] = CanvasRef(instance.PluginKey, instance.CanvasId),
                ["pluginKey"] = instance.PluginKey,
                ["canvasId"] = instance.CanvasId,
                ["title"] = instance.Title,
                ["spaceId"] = instance.SpaceId,
                ["projectId"] = instance.ProjectId,
                ["sessionId"] = instance.SessionId,
                ["key"] = instance.Key,
                ["visible"] = instance.Visible,
            });
        }

        WriteSummary(context, "alta.canvas.instanceSummary", instances.Length, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleCanvasShowAsync(AltaCommandContext context, string? reference, string? projectRef, string? sessionRef, string? key)
    {
        const string CommandPath = "alta canvas show";
        if (!TryGetCanvasView(context, out var view))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (canvas, exitCode) = ResolveCanvas(context, view, reference, CommandPath);
        if (canvas is null)
        {
            return exitCode;
        }

        var (target, targetExit) = await ResolveCanvasContextAsync(context, canvas, projectRef, sessionRef, CommandPath, required: false).ConfigureAwait(false);
        if (target is null)
        {
            return targetExit;
        }

        var record = CanvasItemRecord(context, "alta.canvas.detail", canvas);
        record["inputSchema"] = ParseSchema(canvas.InputSchema);
        record["actions"] = canvas.Actions.Select(static action => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = action.Name,
            ["description"] = action.Description,
            ["inputSchema"] = ParseSchema(action.InputSchema),
        }).ToArray();
        record["open"] = view.ListOpen()
            .Where(instance => string.Equals(instance.PluginKey, canvas.PluginKey, StringComparison.Ordinal) && string.Equals(instance.CanvasId, canvas.Id, StringComparison.Ordinal))
            .Select(static instance => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["instanceId"] = instance.InstanceId,
                ["spaceId"] = instance.SpaceId,
                ["projectId"] = instance.ProjectId,
                ["sessionId"] = instance.SessionId,
                ["key"] = instance.Key,
                ["title"] = instance.Title,
                ["visible"] = instance.Visible,
            }).ToArray();
        if (canvas.Describes && (canvas.Scope == AltaCanvasScopes.Application || target.ProjectId is not null || target.SessionId is not null))
        {
            var description = await view.DescribeAsync(CanvasTargetOf(canvas, target, null, key), context.CancellationToken).ConfigureAwait(false);
            record["describeStatus"] = description.Status;
            record["markdown"] = description.Markdown;
        }

        WriteCanvasRecord(context, record);
        return AltaExitCodes.Success;
    }

    private static object? ParseSchema(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(schema);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return schema;
        }
    }

    private static async Task<(JsonElement? Input, int ExitCode)> ReadCanvasInputAsync(AltaCommandContext context, bool stdin, string commandPath)
    {
        if (!stdin)
        {
            return (null, AltaExitCodes.Success);
        }

        var text = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, AltaExitCodes.Success);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return (document.RootElement.Clone(), AltaExitCodes.Success);
        }
        catch (JsonException exception)
        {
            return (null, UsageError(context, "usage.invalidInput", $"The input on stdin is not JSON: {exception.Message}", commandPath));
        }
    }

    private static async ValueTask<int> HandleCanvasOpenAsync(AltaCommandContext context, string? reference, string? projectRef, string? sessionRef, string? key, string? spaceRef,
        bool stdin, bool focusOnly)
    {
        var commandPath = focusOnly ? "alta canvas focus" : "alta canvas open";
        if (!TryGetCanvasView(context, out var view))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (canvas, exitCode) = ResolveCanvas(context, view, reference, commandPath);
        if (canvas is null)
        {
            return exitCode;
        }

        var (target, targetExit) = await ResolveCanvasContextAsync(context, canvas, projectRef, sessionRef, commandPath, required: true).ConfigureAwait(false);
        if (target is null)
        {
            return targetExit;
        }

        var (input, inputExit) = await ReadCanvasInputAsync(context, stdin, commandPath).ConfigureAwait(false);
        if (inputExit != AltaExitCodes.Success)
        {
            return inputExit;
        }

        var (spaceId, spaceName, spaceExit) = await ResolveCanvasSpaceAsync(context, canvas, target, spaceRef, commandPath).ConfigureAwait(false);
        if (spaceExit != AltaExitCodes.Success)
        {
            return spaceExit;
        }

        var where = CanvasTargetOf(canvas, target, spaceId, key);
        if (!view.HasWindow)
        {
            return CanvasWindowUnavailable(context);
        }

        if (focusOnly && !IsCanvasOpen(view, where))
        {
            return NotFound(context, "canvas.notOpen",
                $"No tab of the canvas '{CanvasRef(canvas.PluginKey, canvas.Id)}' is open{(spaceName is null ? string.Empty : $" in the space '{spaceName}'")}. Open it with `alta canvas open`.");
        }

        var opened = await view.OpenAsync(where, input, focus: true, context.CancellationToken).ConfigureAwait(false);
        switch (opened.Status)
        {
            case "requested":
                break;
            case "unavailable":
                return CanvasWindowUnavailable(context);
            case "unknown_canvas":
                return NotFound(context, "canvas.notFound", $"The plugin no longer declares the canvas '{CanvasRef(canvas.PluginKey, canvas.Id)}'.");
            case "plugin_stopped":
                return Unsupported(context, "canvas.pluginStopped", $"The plugin '{canvas.Plugin}' is not running: build or reload it, then open the canvas again.");
            default:
                return UsageError(context, "usage.invalidCanvasRequest", "The canvas could not be opened with these options: its context or its input is not valid.", commandPath);
        }

        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = focusOnly ? "alta.canvas.focused" : "alta.canvas.opened",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["ref"] = CanvasRef(canvas.PluginKey, canvas.Id),
            ["pluginKey"] = canvas.PluginKey,
            ["canvasId"] = canvas.Id,
            ["instanceId"] = opened.InstanceId,
            ["spaceId"] = opened.SpaceId,
            ["space"] = spaceName,
            ["shown"] = opened.Shown,
            ["projectId"] = target.ProjectId,
            ["sessionId"] = target.SessionId,
            ["key"] = where.Key,
        });
        return AltaExitCodes.Success;
    }

    private static int CanvasWindowUnavailable(AltaCommandContext context)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "view.unavailable", AltaExitCodes.ServiceUnavailable, "No CodeAlta window is open to show the canvas.");
        return AltaExitCodes.ServiceUnavailable;
    }

    // Whether a tab of the canvas, in the space and the context named, is open.
    private static bool IsCanvasOpen(IAltaCanvasView view, AltaCanvasTarget target)
        => view.ListOpen().Any(instance => string.Equals(instance.PluginKey, target.PluginKey, StringComparison.Ordinal)
            && string.Equals(instance.CanvasId, target.CanvasId, StringComparison.Ordinal)
            && (target.SpaceId is null || string.Equals(instance.SpaceId, target.SpaceId, StringComparison.Ordinal))
            && string.Equals(instance.ProjectId, target.ProjectId, StringComparison.Ordinal)
            && string.Equals(instance.SessionId, target.SessionId, StringComparison.Ordinal)
            && string.Equals(instance.Key, target.Key, StringComparison.Ordinal));

    private static async ValueTask<int> HandleCanvasCloseAsync(AltaCommandContext context, string? reference, string? projectRef, string? sessionRef, string? key, string? spaceRef)
    {
        const string CommandPath = "alta canvas close";
        if (!TryGetCanvasView(context, out var view))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (canvas, exitCode) = ResolveCanvas(context, view, reference, CommandPath);
        if (canvas is null)
        {
            return exitCode;
        }

        var (target, targetExit) = await ResolveCanvasContextAsync(context, canvas, projectRef, sessionRef, CommandPath, required: true).ConfigureAwait(false);
        if (target is null)
        {
            return targetExit;
        }

        var (spaceId, spaceName, spaceExit) = await ResolveCanvasSpaceAsync(context, canvas, target, spaceRef, CommandPath).ConfigureAwait(false);
        if (spaceExit != AltaExitCodes.Success)
        {
            return spaceExit;
        }

        if (!view.HasWindow)
        {
            return CanvasWindowUnavailable(context);
        }

        var where = CanvasTargetOf(canvas, target, spaceId, key);
        if (!IsCanvasOpen(view, where) || !await view.CloseAsync(where, context.CancellationToken).ConfigureAwait(false))
        {
            return NotFound(context, "canvas.notOpen",
                $"No tab of the canvas '{CanvasRef(canvas.PluginKey, canvas.Id)}' is open{(spaceName is null ? string.Empty : $" in the space '{spaceName}'")}.");
        }

        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.canvas.closed",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["ref"] = CanvasRef(canvas.PluginKey, canvas.Id),
            ["spaceId"] = spaceId,
            ["projectId"] = target.ProjectId,
            ["sessionId"] = target.SessionId,
            ["key"] = where.Key,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleCanvasInvokeAsync(AltaCommandContext context, string? reference, string? actionName, string? projectRef, string? sessionRef, string? key, bool stdin)
    {
        const string CommandPath = "alta canvas invoke";
        if (!TryGetCanvasView(context, out var view))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (canvas, exitCode) = ResolveCanvas(context, view, reference, CommandPath);
        if (canvas is null)
        {
            return exitCode;
        }

        if (NormalizeOptionalText(actionName) is not { } action)
        {
            return UsageError(context, "usage.missingAction", $"An action is required. The canvas declares: {string.Join(", ", canvas.Actions.Select(static declared => declared.Name))}.", CommandPath);
        }

        if (!canvas.Actions.Any(declared => string.Equals(declared.Name, action, StringComparison.Ordinal)))
        {
            return NotFound(context, "canvas.actionNotFound",
                $"The canvas '{CanvasRef(canvas.PluginKey, canvas.Id)}' declares no action '{action}'. It declares: {(canvas.Actions.Count == 0 ? "none" : string.Join(", ", canvas.Actions.Select(static declared => declared.Name)))}.");
        }

        var (target, targetExit) = await ResolveCanvasContextAsync(context, canvas, projectRef, sessionRef, CommandPath, required: true).ConfigureAwait(false);
        if (target is null)
        {
            return targetExit;
        }

        var (input, inputExit) = await ReadCanvasInputAsync(context, stdin, CommandPath).ConfigureAwait(false);
        if (inputExit != AltaExitCodes.Success)
        {
            return inputExit;
        }

        var result = await view.InvokeAsync(CanvasTargetOf(canvas, target, null, key), action, input, context.CancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case "ok":
                WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "alta.canvas.result",
                    ["version"] = 1,
                    ["correlationId"] = context.CorrelationId,
                    ["ref"] = CanvasRef(canvas.PluginKey, canvas.Id),
                    ["action"] = action,
                    ["result"] = result.Result,
                });
                return AltaExitCodes.Success;
            case "unknown_canvas":
                return NotFound(context, "canvas.notFound", $"The plugin no longer declares the canvas '{CanvasRef(canvas.PluginKey, canvas.Id)}'.");
            case "unknown_action":
                return NotFound(context, "canvas.actionNotFound", $"The canvas '{CanvasRef(canvas.PluginKey, canvas.Id)}' declares no action '{action}'.");
            case "plugin_stopped":
                return Unsupported(context, "canvas.pluginStopped", $"The plugin '{canvas.Plugin}' is not running: build or reload it, then run the action again.");
            case "invalid_request":
                return UsageError(context, "usage.invalidCanvasRequest", "The action could not run with these options: its context or its input is not valid.", CommandPath);
            default:
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "canvas.failed", AltaExitCodes.Failure,
                    $"The action '{action}' of the canvas '{CanvasRef(canvas.PluginKey, canvas.Id)}' failed. The plugin wrote the reason in the application log.");
                return AltaExitCodes.Failure;
        }
    }

    // What a canvas does not have is left out of its records: a record that lists a dozen nulls costs the agent that reads it.
    private static void WriteCanvasRecord(AltaCommandContext context, Dictionary<string, object?> record)
        => AltaJsonlWriter.WriteRecord(context.Stdout, record.Where(static pair => pair.Value is not null).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));

    private static bool TryGetCanvasView(AltaCommandContext context, out IAltaCanvasView view)
    {
        view = context.Services.Get<IAltaCanvasView>()!;
        if (view is not null)
        {
            return true;
        }

        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'IAltaCanvasView' is unavailable.");
        return false;
    }
}
