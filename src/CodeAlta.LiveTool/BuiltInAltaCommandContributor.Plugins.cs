using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.LiveTool;

// The `alta plugin` commands: what the host did with each plugin and, for a source plugin, the loop of its
// development: create it, build it, load it again, look at what the host says.
internal sealed partial class BuiltInAltaCommandContributor
{
    /// <summary>The most compiler errors and warnings one record carries.</summary>
    internal const int MaximumBuildDiagnostics = 40;

    /// <summary>The most types of the plugin API one record shows with their members.</summary>
    internal const int MaximumApiTypes = 8;

    private const int MaximumBuildOutput = 2000;
    private const int MaximumPluginDiagnostics = 12;
    private const int MaximumContributions = 100;

    // Only a host that gives its plugins to these commands has them.
    private static readonly AltaCommandPolicy[] PluginWorkshopPolicies =
    [
        Mutating("plugin build"),
        Mutating("plugin reload"),
        Mutating("plugin refresh"),
        Mutating("plugin create"),
    ];

    // It changes nothing but what the window shows.
    private static readonly AltaCommandPolicy PluginOpenPolicy = Read("plugin open");

    private static Command CreatePluginCommand(AltaCommandContext context)
    {
        var workshop = context.Services.Get<AltaPluginWorkshop>();
        var group = Group("plugin", workshop is null ? "Inspect the plugins." : "Inspect the plugins, and create, build and reload source plugins.");
        group.Add(CreatePluginListCommand(context));
        group.Add(CreatePluginStatusCommand(context));
        group.Add(CreatePluginApiCommand(context));
        if (workshop is null) return group;
        group.Add(CreatePluginCreateCommand(context));
        group.Add(CreatePluginBuildCommand(context, reload: false));
        group.Add(CreatePluginBuildCommand(context, reload: true));
        group.Add(CreatePluginRefreshCommand(context));
        if (workshop.OpenEditor is not null) group.Add(CreatePluginOpenCommand(context));
        AddHelpText(
            group,
            "A source plugin is one C# file, `plugin.cs`, in its own folder: `~/.alta/plugins/<id>/` for all projects, `<project>/.alta/plugins/<id>/` for one project. CodeAlta builds it and loads it.",
            "To work on one: `alta plugin create <id>` writes a first plugin and starts it; edit its `plugin.cs`; `alta plugin reload <id>` builds it and replaces the running plugin. When the build fails the record has the compiler errors, and the plugin that ran keeps running.",
            "`alta plugin status <id>` shows the folder, the state, the last build and what the plugin contributes.",
            "`alta plugin api <type>` shows a type of the plugin API with its members: look a name up there instead of guessing it.",
            "The skill `codealta-plugin-runtime` says how a plugin is written and has samples: `alta skill activate codealta-plugin-runtime`.",
            "Examples: `alta plugin list`; `alta plugin create notes`; `alta plugin reload notes`; `alta plugin status notes`.");
        return group;
    }

    private static Command CreatePluginListCommand(AltaCommandContext context)
    {
        var detailed = false;
        var command = Leaf("list", "List the plugins, each with its id, where it comes from and its state.");
        command.Add("detailed", "Emit one record per plugin, with its folder, its last build and its diagnostics.", value => detailed = value is not null);
        command.Add((_, _) => ValueTask.FromResult(HandlePluginList(context, detailed)));
        AddHelpText(command, "`scope` is `builtin`, `global` or `project`. `state` is `running`, `failed` (not built or not started: `alta plugin status <id>` says why), `disabled`, `unsupported` (made for the other application) or `stopped` (not started yet).");
        return command;
    }

    private static Command CreatePluginStatusCommand(AltaCommandContext context)
    {
        var target = new PluginTarget();
        var command = Leaf("status", "Show one plugin: its folder, its state, its last build with the compiler errors and warnings, and what it contributes.");
        AddPluginTarget(command, target);
        command.Add((_, _) => ValueTask.FromResult(HandlePluginStatus(context, target)));
        return command;
    }

    private static Command CreatePluginApiCommand(AltaCommandContext context)
    {
        string? query = null;
        var command = Leaf("api", "Look the API of plugins up: a type with its members, the types whose name has a word, or every type.");
        command.Add("<name>?", "A type name (`PluginDialogRequest`), a part of one (`Dialog`), or a member name (`SelectedProjectPath`). Without it, every type is listed.", value => query = value);
        command.Add((_, _) => ValueTask.FromResult(HandlePluginApi(context, query)));
        AddHelpText(
            command,
            "The signatures are those of the running version of CodeAlta: the types of CodeAlta.Plugins.Abstractions and CodeAlta.Plugins.Tui, and the types of CodeAlta.Agent they use (tools, events).",
            "Examples: `alta plugin api PluginCommandContext`; `alta plugin api Dialog`; `alta plugin api AgentToolResult`.");
        return command;
    }

    private static int HandlePluginApi(AltaCommandContext context, string? query)
    {
        var reference = new PluginApiReference();
        static string Line(PluginApiType type) => type.Summary is null ? $"{type.Name} ({type.Kind})" : $"{type.Name} ({type.Kind}): {type.Summary}";
        if (NormalizeOptionalText(query) is not { } name)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.plugin.api.index",
                version = 1,
                correlationId = context.CorrelationId,
                count = reference.Types.Count,
                types = reference.Types.Select(static type => $"{type.Name} ({type.Kind})").ToArray(),
                next = "`alta plugin api <type>` shows a type with its members; a part of a name lists the types that have it.",
            });
            return AltaExitCodes.Success;
        }

        var found = reference.Find(name);
        if (found.Count == 0)
        {
            var near = reference.Suggest(name);
            return NotFound(context, "plugin.apiNotFound", near.Count > 0
                ? $"The plugin API has no type or member named '{name}'. Close names: {string.Join(", ", near)}. `alta plugin api` lists every type."
                : $"The plugin API has no type or member named '{name}'. `alta plugin api` lists every type.");
        }

        var detailed = found.Count <= MaximumApiTypes;
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.plugin.api",
            version = 1,
            correlationId = context.CorrelationId,
            query = name,
            count = found.Count,
            types = detailed
                ? found.Select(static type => (object)new
                {
                    name = type.Name,
                    @namespace = type.Namespace,
                    declaration = type.Declaration,
                    summary = type.Summary,
                    derived = type.DerivedTypes.Count > 0 ? type.DerivedTypes : null,
                    members = type.Members.Select(static member => member.Summary is null ? member.Signature : $"{member.Signature}  // {member.Summary}").ToArray(),
                }).ToArray()
                : found.Select(static type => (object)Line(type)).ToArray(),
            next = detailed ? null : "Name one of these types to see its members.",
        });
        return AltaExitCodes.Success;
    }

    private static Command CreatePluginCreateCommand(AltaCommandContext context)
    {
        string? id = null;
        string? name = null;
        string? description = null;
        var project = false;
        var noStart = false;
        var command = Leaf("create", "Create a source plugin: its folder with a first `plugin.cs` that has one command, built and started.");
        command.Add("<id>", "Id of the plugin: the name of its folder. Letters, digits, '.', '_' and '-', starting with a letter or a digit.", value => id = value);
        command.Add("project", "Create it for the project of CodeAlta, in `<project>/.alta/plugins`. Without it the plugin is global, in `~/.alta/plugins`.", value => project = value is not null);
        command.Add("name=", "Name shown for the plugin. Defaults to the id in words.", value => name = value);
        command.Add("description=", "One sentence that says what the plugin does.", value => description = value);
        command.Add("no-start", "Write the files and do not build or start the plugin.", value => noStart = value is not null);
        command.Add(async (_, _) => await HandlePluginCreateAsync(context, id, project, name, description, noStart).ConfigureAwait(false));
        AddHelpText(
            command,
            "The record has `file`, the path of the `plugin.cs` to edit. The first plugin adds the command `/<id>`: replace it by what the plugin is for.",
            "Examples: `alta plugin create notes --description \"Keeps short notes.\"`; `alta plugin create review-helper --project`.");
        return command;
    }

    private static Command CreatePluginBuildCommand(AltaCommandContext context, bool reload)
    {
        var target = new PluginTarget();
        var force = false;
        var command = reload
            ? Leaf("reload", "Build a source plugin and replace the running plugin by the new build, or start it. When the build fails, the plugin that ran keeps running.")
            : Leaf("build", "Build a source plugin without loading it: says whether its `plugin.cs` compiles, with the compiler errors and warnings.");
        AddPluginTarget(command, target);
        command.Add("force", "Build even when the source did not change since the last build.", value => force = value is not null);
        command.Add(async (_, _) => await HandlePluginBuildAsync(context, target, force, reload).ConfigureAwait(false));
        if (reload)
        {
            AddHelpText(
                command,
                "`change` is `reloaded`, `started`, `buildFailed` (see `build.diagnostics`: file, line, column, code, message), `startFailed` (see `diagnostics`) or `disabled`. The exit code is 0 only for `reloaded` and `started`.",
                "The commands, the status items and the content of the plugin change at once in the window. `agentTools` names the agent tools of the plugin: call them at once when `available` is `now`. Other sessions get new tools, and every session the prompt text of the plugin, with their next prompt.");
        }

        return command;
    }

    private static Command CreatePluginRefreshCommand(AltaCommandContext context)
    {
        var command = Leaf("refresh", "Apply what changed on disk and in the configuration: start the new plugins, reload those whose source changed, stop those that were removed or turned off.");
        command.Add(async (_, _) => await HandlePluginRefreshAsync(context).ConfigureAwait(false));
        AddHelpText(command, "A plugin whose last build failed is built again only when its source changed; `alta plugin reload <id> --force` builds it anyway.");
        return command;
    }

    private static Command CreatePluginOpenCommand(AltaCommandContext context)
    {
        var target = new PluginTarget();
        string? file = null;
        string? line = null;
        string? column = null;
        var command = Leaf("open", "Open the folder of a source plugin in the code editor of the CodeAlta window, and a file in it.");
        AddPluginTarget(command, target);
        command.Add("file=", "File to open, relative to the folder of the plugin. Defaults to `plugin.cs`.", value => file = value);
        command.Add("line=", "1-based line to go to in the file.", value => line = value);
        command.Add("column=", "1-based column on that line.", value => column = value);
        command.Add((_, _) => ValueTask.FromResult(HandlePluginOpen(context, target, file, line, column)));
        AddHelpText(command, "The editor is where the user reads and edits the plugin: use it to show them a file, not to read one yourself.");
        return command;
    }

    private sealed class PluginTarget
    {
        public string? Reference { get; set; }

        public bool Global { get; set; }

        public bool Project { get; set; }
    }

    private static void AddPluginTarget(Command command, PluginTarget target)
    {
        command.Add("<plugin>", "Plugin id (the folder name of a source plugin, the id of a built-in one) or runtime key.", value => target.Reference = value);
        command.Add("global", "The global plugin of that id, when a project plugin has the same id.", value => target.Global = value is not null);
        command.Add("project", "The project plugin of that id, when a global plugin has the same id.", value => target.Project = value is not null);
    }

    private static int HandlePluginList(AltaCommandContext context, bool detailed)
    {
        var runtime = PluginRuntime(context);
        var packages = runtime?.GetPackages() ?? [];
        // With the packages of the host, the catalog adds the plugins that have none: the built-in ones.
        var plugins = (context.Services.Get<IAltaPluginCatalog>()?.ListPlugins() ?? [])
            .Where(plugin => runtime is null || string.Equals(plugin.Scope, "builtin", StringComparison.Ordinal))
            .ToArray();
        if (!detailed)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.plugin.refs",
                plugins = plugins.Select(static plugin => new { id = PluginId(plugin.RuntimeKey), scope = plugin.Scope, state = PluginState(plugin.State), runtimeKey = (string?)plugin.RuntimeKey })
                    .Concat(packages.Select(static package => new
                    {
                        id = package.Package.PackageId,
                        scope = (string?)PluginScopeName(package.Package.Root.Scope),
                        state = (string?)PluginStateName(package.State),
                        runtimeKey = package.Plugins.Count == 1 ? package.Plugins[0].RuntimeKey : null,
                    }))
                    .ToArray(),
            });
            return AltaExitCodes.Success;
        }

        foreach (var plugin in plugins)
        {
            WritePlugin(context, "alta.plugin.item", plugin);
        }

        foreach (var package in packages)
        {
            WritePluginPackage(context, runtime!, "alta.plugin.item", package, change: null, contributions: false);
        }

        WriteSummary(context, "alta.plugin.summary", plugins.Length + packages.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int HandlePluginStatus(AltaCommandContext context, PluginTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Reference))
        {
            return UsageError(context, "usage.missingPlugin", "A plugin id is required.", "alta plugin status");
        }

        if (PluginRuntime(context) is { } runtime && FindPluginPackages(runtime, target) is { Count: > 0 } packages)
        {
            if (packages.Count > 1) return AmbiguousPlugin(context, target, "alta plugin status");
            WritePluginPackage(context, runtime, "alta.plugin.status", packages[0], change: null, contributions: true);
            return AltaExitCodes.Success;
        }

        // A plugin without a package: a built-in one, by its runtime key or its id.
        var catalog = context.Services.Get<IAltaPluginCatalog>();
        var plugin = catalog?.GetPlugin(target.Reference) ?? catalog?.GetPlugin("builtin:" + target.Reference);
        if (plugin is null)
        {
            return NotFound(context, "plugin.notFound", $"Plugin '{target.Reference}' was not found. `alta plugin list` shows the plugins.");
        }

        WritePlugin(context, "alta.plugin.status", plugin);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandlePluginBuildAsync(AltaCommandContext context, PluginTarget target, bool force, bool reload)
    {
        var commandPath = reload ? "alta plugin reload" : "alta plugin build";
        if (!TryGetPluginWorkshop(context, out var workshop)) return AltaExitCodes.ServiceUnavailable;
        if (string.IsNullOrWhiteSpace(target.Reference)) return UsageError(context, "usage.missingPlugin", "A plugin id is required.", commandPath);
        var packages = FindPluginPackages(workshop.Runtime, target);
        if (packages.Count == 0)
        {
            return NotFound(context, "plugin.notFound", $"No source plugin '{target.Reference}' was found. `alta plugin list` shows the plugins; `alta plugin create {target.Reference}` creates one.");
        }

        if (packages.Count > 1) return AmbiguousPlugin(context, target, commandPath);
        try
        {
            if (!reload)
            {
                var built = await workshop.Runtime.BuildPackageAsync(packages[0].Package, force, context.CancellationToken).ConfigureAwait(false);
                WritePluginPackage(context, workshop.Runtime, "alta.plugin.build", built, change: null, contributions: false);
                return built.Build is { Succeeded: true } ? AltaExitCodes.Success : AltaExitCodes.Failure;
            }

            var result = await workshop.Runtime.ReloadPackageAsync(packages[0].Package, force, context.CancellationToken).ConfigureAwait(false);
            var started = result.Change is PluginPackageChange.Reloaded or PluginPackageChange.Started;
            WritePluginPackage(context, workshop.Runtime, "alta.plugin.reload", result.Status, result.Change, contributions: true, started ? OfferPluginTools(context, workshop, result.Status) : null);
            return started ? AltaExitCodes.Success : AltaExitCodes.Failure;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return PluginsUnavailable(context, exception);
        }
    }

    private static async ValueTask<int> HandlePluginRefreshAsync(AltaCommandContext context)
    {
        if (!TryGetPluginWorkshop(context, out var workshop)) return AltaExitCodes.ServiceUnavailable;
        try
        {
            var results = await workshop.Runtime.RefreshPackagesAsync(context.CancellationToken).ConfigureAwait(false);
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.plugin.refresh",
                version = 1,
                correlationId = context.CorrelationId,
                changes = results.Where(static result => result.Change != PluginPackageChange.Unchanged && result.Change != PluginPackageChange.Disabled)
                    .Select(static result => new
                    {
                        id = result.Status.Package.PackageId,
                        scope = PluginScopeName(result.Status.Package.Root.Scope),
                        change = PluginChangeName(result.Change),
                        state = PluginStateName(result.Status.State),
                    })
                    .ToArray(),
                unchanged = results.Count(static result => result.Change is PluginPackageChange.Unchanged or PluginPackageChange.Disabled),
                next = results.Any(static result => result.Change is PluginPackageChange.BuildFailed or PluginPackageChange.StartFailed)
                    ? "`alta plugin status <id>` says why a plugin was not built or not started."
                    : null,
            });
            return AltaExitCodes.Success;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return PluginsUnavailable(context, exception);
        }
    }

    private static async ValueTask<int> HandlePluginCreateAsync(AltaCommandContext context, string? id, bool project, string? name, string? description, bool noStart)
    {
        if (!TryGetPluginWorkshop(context, out var workshop)) return AltaExitCodes.ServiceUnavailable;
        if (string.IsNullOrWhiteSpace(id)) return UsageError(context, "usage.missingPlugin", "A plugin id is required.", "alta plugin create");
        var scope = project ? PluginScope.Project : PluginScope.Global;
        if (workshop.Runtime.Roots.FirstOrDefault(root => root.Scope == scope) is not { } root)
        {
            return project
                ? Unsupported(context, "plugin.noProject", "CodeAlta was not started in a project: create a global plugin, without --project.")
                : PluginsUnavailable(context, new InvalidOperationException("The plugins of this application have not started."));
        }

        // A project plugin is loaded from the folder CodeAlta was started in: one written elsewhere would not run.
        if (project && await CallerProjectAsync(context).ConfigureAwait(false) is { } caller && root.ProjectPath is { } loaded && !SameDirectory(caller.ProjectPath, loaded))
        {
            return Unsupported(context, "plugin.otherProject",
                $"CodeAlta loads the project plugins of the folder it was started in, '{loaded}'. This session works in '{caller.ProjectPath}': create a global plugin (without --project), or start CodeAlta in that folder.");
        }

        var created = SourcePluginScaffold.Create(root, id.Trim(), name, description);
        if (created.Package is not { } package)
        {
            return created.Error == "write_failed"
                ? PluginFailure(context, "plugin.writeFailed", created.Message!)
                : UsageError(context, created.Error == "exists" ? "plugin.exists" : "usage.invalidPlugin", created.Message!, "alta plugin create");
        }

        try
        {
            if (noStart)
            {
                var status = workshop.Runtime.GetPackages().FirstOrDefault(found => SameDirectory(found.Package.PackageDirectory, package.PackageDirectory));
                WritePluginPackage(context, workshop.Runtime, "alta.plugin.created", status ?? new() { Package = package, State = PluginPackageState.Stopped, Enabled = true }, change: null, contributions: false);
                return AltaExitCodes.Success;
            }

            var result = await workshop.Runtime.ReloadPackageAsync(package, force: false, context.CancellationToken).ConfigureAwait(false);
            WritePluginPackage(context, workshop.Runtime, "alta.plugin.created", result.Status, result.Change, contributions: true, OfferPluginTools(context, workshop, result.Status));
            return AltaExitCodes.Success;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return PluginsUnavailable(context, exception);
        }
    }

    private static int HandlePluginOpen(AltaCommandContext context, PluginTarget target, string? file, string? line, string? column)
    {
        const string CommandPath = "alta plugin open";
        if (!TryGetPluginWorkshop(context, out var workshop)) return AltaExitCodes.ServiceUnavailable;
        if (workshop.OpenEditor is not { } open) return Unsupported(context, "plugin.noEditor", "This application has no code editor.");
        if (string.IsNullOrWhiteSpace(target.Reference)) return UsageError(context, "usage.missingPlugin", "A plugin id is required.", CommandPath);
        var packages = FindPluginPackages(workshop.Runtime, target);
        if (packages.Count == 0) return NotFound(context, "plugin.notFound", $"No source plugin '{target.Reference}' was found. `alta plugin list` shows the plugins.");
        if (packages.Count > 1) return AmbiguousPlugin(context, target, CommandPath);
        var package = packages[0].Package;

        int? lineNumber = null;
        int? columnNumber = null;
        if (NormalizeOptionalText(line) is { } lineText)
        {
            if (!int.TryParse(lineText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return UsageError(context, "usage.invalidLine", "The line is a number from 1.", CommandPath);
            }

            lineNumber = parsed;
        }

        if (NormalizeOptionalText(column) is { } columnText)
        {
            if (lineNumber is null || !int.TryParse(columnText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return UsageError(context, "usage.invalidColumn", "The column is a number from 1, given with --line.", CommandPath);
            }

            columnNumber = parsed;
        }

        // Only the name of a file inside the folder of the plugin.
        var relative = (NormalizeOptionalText(file) ?? Path.GetFileName(package.EntryFilePath)).Replace('\\', '/');
        if (relative.Length > 1024 || Path.IsPathRooted(relative) || relative.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            return UsageError(context, "usage.invalidFile", "The file is a path relative to the folder of the plugin.", CommandPath);
        }

        if (!File.Exists(Path.Combine(package.PackageDirectory, relative)))
        {
            return NotFound(context, "file.notFound", $"The plugin has no file '{relative}'.");
        }

        if (!open(package, relative, lineNumber, columnNumber))
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "view.unavailable", AltaExitCodes.ServiceUnavailable, "No CodeAlta window is open to show the plugin.");
            return AltaExitCodes.ServiceUnavailable;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.plugin.opened",
            version = 1,
            correlationId = context.CorrelationId,
            id = package.PackageId,
            scope = PluginScopeName(package.Root.Scope),
            directory = package.PackageDirectory,
            file = relative,
            line = lineNumber,
            column = columnNumber,
        });
        return AltaExitCodes.Success;
    }

    private static bool TryGetPluginWorkshop(AltaCommandContext context, out AltaPluginWorkshop workshop)
    {
        if (context.Services.Get<AltaPluginWorkshop>() is { } found)
        {
            workshop = found;
            return true;
        }

        workshop = null!;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'AltaPluginWorkshop' is unavailable.");
        return false;
    }

    // The plugin runtime of the host: every host that has one lists its packages; only some change them.
    private static PluginRuntimeManager? PluginRuntime(AltaCommandContext context)
        => context.Services.Get<AltaPluginWorkshop>()?.Runtime ?? context.Services.Get<PluginRuntimeManager>();

    // The packages a reference names: by id, else by the runtime key of one of their active plugins.
    private static IReadOnlyList<PluginPackageStatus> FindPluginPackages(PluginRuntimeManager runtime, PluginTarget target)
    {
        var reference = target.Reference!.Trim();
        var packages = runtime.GetPackages()
            .Where(package => (!target.Global || package.Package.Root.Scope == PluginScope.Global) && (!target.Project || package.Package.Root.Scope == PluginScope.Project))
            .ToArray();
        var byId = packages.Where(package => string.Equals(package.Package.PackageId, reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        return byId.Length > 0 ? byId : [.. packages.Where(package => package.Plugins.Any(plugin => string.Equals(plugin.RuntimeKey, reference, StringComparison.OrdinalIgnoreCase)))];
    }

    private static int AmbiguousPlugin(AltaCommandContext context, PluginTarget target, string commandPath)
        => UsageError(context, "plugin.ambiguous", $"A global plugin and a project plugin are both named '{target.Reference}': add --global or --project.", commandPath);

    private static int PluginsUnavailable(AltaCommandContext context, Exception exception)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "plugin.unavailable", AltaExitCodes.ServiceUnavailable, exception.Message);
        return AltaExitCodes.ServiceUnavailable;
    }

    private static int PluginFailure(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.Failure, message);
        return AltaExitCodes.Failure;
    }

    // The project the caller works in: the one of its session, else the one of its working folder.
    private static async ValueTask<ProjectDescriptor?> CallerProjectAsync(AltaCommandContext context)
    {
        if (context.Services.Get<ProjectCatalog>() is not { } catalog) return null;
        if (NormalizeOptionalText(context.Caller.SourceProjectId) is { } projectId)
        {
            return await ResolveProjectAsync(catalog, projectId, context, includeArchived: false).ConfigureAwait(false);
        }

        return NormalizeOptionalText(context.Cwd) is { } cwd
            ? await catalog.GetByPathAsync(ResolvePath(context, cwd), context.CancellationToken).ConfigureAwait(false)
            : null;
    }

    private static bool SameDirectory(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // The agent tools of the plugins of a package, and when the caller has them. A session whose turn runs gets
    // them for the rest of that turn, the new version of a tool in place of the previous one; any session gets
    // them with its next prompt.
    private static object? OfferPluginTools(AltaCommandContext context, AltaPluginWorkshop workshop, PluginPackageStatus status)
    {
        string[] keys = [.. status.Plugins.Select(static plugin => plugin.RuntimeKey)];
        string[] names = [.. workshop.Runtime.Registry.GetSnapshot()
            .Where(registration => keys.Contains(registration.Handle.PluginRuntimeKey, StringComparer.Ordinal))
            .Select(static registration => registration.Contribution)
            .OfType<PluginAgentToolContribution>()
            .Select(static tool => tool.Definition.Spec.Name)];
        if (names.Length == 0) return null;
        IReadOnlyList<string> registered = [];
        if (context.Caller is { Kind: "agent", RunTools: { } runTools } && context.Services.Get<PluginOrchestrationBridge>() is { } bridge)
        {
            // Only a session of the CodeAlta runtime has the tools of its turn.
            registered = runTools.Set(bridge.CreateAgentTools(new PluginAdapterOperationOptions
            {
                ProjectId = NormalizeOptionalText(context.Caller.SourceProjectId),
                ProjectPath = NormalizeOptionalText(context.Cwd),
                SessionId = NormalizeOptionalText(context.Caller.SourceSessionId),
                IsCodeAltaManagedProvider = true,
            }, keys));
        }

        return new
        {
            names,
            available = registered.Count > 0 ? "now" : "next_prompt",
            note = registered.Count > 0
                ? "Registered for the rest of this turn: call them now."
                : "A session has them from its next prompt.",
        };
    }

    private static void WritePluginPackage(AltaCommandContext context, PluginRuntimeManager runtime, string type, PluginPackageStatus status, PluginPackageChange? change, bool contributions,
        object? agentTools = null)
    {
        var package = status.Package;
        var build = status.Build;
        var errors = build?.Diagnostics.Count(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error) ?? 0;
        var registered = contributions && status.Plugins.Count > 0 ? runtime.Registry.GetContributionSummaries() : [];
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            id = package.PackageId,
            scope = PluginScopeName(package.Root.Scope),
            change = change is { } changed ? PluginChangeName(changed) : null,
            state = PluginStateName(status.State),
            enabled = status.Enabled,
            directory = package.PackageDirectory,
            file = package.EntryFilePath,
            readme = package.Sidecars.ReadmePath,
            sourceChanged = status.SourceChanged ? true : (bool?)null,
            plugins = status.Plugins.Select(plugin => new
            {
                runtimeKey = plugin.RuntimeKey,
                displayName = plugin.DisplayName ?? plugin.TypeName,
                pluginVersion = plugin.Version,
                contributions = !contributions ? null : registered
                    .Where(contribution => string.Equals(contribution.Handle.PluginRuntimeKey, plugin.RuntimeKey, StringComparison.Ordinal))
                    .Take(MaximumContributions)
                    .Select(static contribution => new { point = contribution.Handle.Point.ToString(), name = contribution.Handle.NaturalName })
                    .ToArray(),
            }).ToArray(),
            build = build is null ? null : new
            {
                succeeded = build.Succeeded,
                upToDate = build.IsUpToDate ? true : (bool?)null,
                at = build.CompletedAt,
                durationMs = build.Duration is { } duration ? (long?)duration.TotalMilliseconds : null,
                errors,
                warnings = build.Diagnostics.Count - errors,
                diagnostics = build.Diagnostics
                    .OrderByDescending(static diagnostic => diagnostic.Severity)
                    .Take(MaximumBuildDiagnostics)
                    .Select(static diagnostic => new
                    {
                        severity = diagnostic.Severity >= PluginDiagnosticSeverity.Error ? "error" : "warning",
                        code = diagnostic.Code,
                        message = diagnostic.Message,
                        file = diagnostic.File,
                        line = diagnostic.LineNumber > 0 ? diagnostic.LineNumber : (int?)null,
                        column = diagnostic.ColumnNumber > 0 ? diagnostic.ColumnNumber : (int?)null,
                    })
                    .ToArray(),
                truncated = build.Diagnostics.Count > MaximumBuildDiagnostics ? true : (bool?)null,
                // A build that failed without a compiler error: what the tools printed.
                output = !build.Succeeded && errors == 0 ? Tail(build.StandardOutput + build.StandardError, MaximumBuildOutput) : null,
            },
            // The latest ones: a callback that fails each time it is asked would fill the record.
            diagnostics = status.Diagnostics
                .Where(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Warning || diagnostic.Metadata.ContainsKey(PluginRuntimeManager.UnsupportedFrontendMetadataKey))
                .Select(DescribePluginDiagnostic)
                .Distinct(StringComparer.Ordinal)
                .TakeLast(MaximumPluginDiagnostics)
                .ToArray(),
            agentTools,
            next = PluginNextStep(context, status, errors),
        });
    }

    // "Error/Callback: Command contribution failed. [Command 'notes'] InvalidOperationException: ... (plugin.cs:line 42)"
    private static string DescribePluginDiagnostic(PluginRuntimeDiagnostic diagnostic)
    {
        var text = $"{diagnostic.Severity}/{diagnostic.Source}: {diagnostic.Message}";
        if (diagnostic.Metadata.TryGetValue("NaturalName", out var name) && name.Length > 0 && diagnostic.Metadata.TryGetValue("Point", out var point))
        {
            text += $" [{point} '{name}']";
        }

        if (diagnostic.Exception is not { } exception || diagnostic.Message.Contains(exception.Message, StringComparison.Ordinal)) return text;
        text += $" {exception.TypeName[(exception.TypeName.LastIndexOf('.') + 1)..]}: {exception.Message}";
        // The first frame in the source of the plugin: its symbols are loaded with it.
        var frame = exception.StackTrace?.Split('\n').Select(static line => line.Trim()).FirstOrDefault(static line => line.Contains(".cs:line ", StringComparison.Ordinal));
        if (frame is null) return text;
        var file = frame[(frame.LastIndexOfAny(['\\', '/']) + 1)..];
        return $"{text} ({file})";
    }

    // One sentence that says what to do with the plugin now.
    private static string? PluginNextStep(AltaCommandContext context, PluginPackageStatus status, int errors)
    {
        var id = status.Package.PackageId;
        // A host whose sessions do not build plugins loads them when it starts.
        var load = context.Services.Get<AltaPluginWorkshop>() is null ? "a restart of CodeAlta" : $"`alta plugin reload {id}`";
        if (!status.Enabled) return $"Turned off in the configuration: `[plugins.{id}] enabled = true` in config.toml, or Settings > Plugins.";
        if (status.Build is { Succeeded: false })
        {
            var subject = errors > 0 ? $"Fix the errors in {Path.GetFileName(status.Package.EntryFilePath)}" : "Fix what `diagnostics` and `build.output` say";
            return status.State == PluginPackageState.Running
                ? $"{subject}, then {load}. The previous version is still running."
                : $"{subject}, then {load}.";
        }

        return status.State switch
        {
            PluginPackageState.Running when status.SourceChanged => $"The source changed since this version was loaded: {load} loads it.",
            PluginPackageState.Running when GetEffectivePolicies(context).Any(static policy => policy.Path == "ui" || policy.Path.StartsWith("ui ", StringComparison.Ordinal))
                => "Running. To see it in the window: `alta ui activate`, then `take_snapshot` or `take_screenshot`.",
            PluginPackageState.Running => "Running.",
            PluginPackageState.Failed => $"Fix what `diagnostics` says, then {load}.",
            PluginPackageState.Unsupported => "The plugin is made for the other application: see `Frontends` of its [Plugin] attribute.",
            PluginPackageState.Stopped => $"Not started: {load} starts it.",
            _ => null,
        };
    }

    private static string Tail(string text, int maximum)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[^maximum..];
    }

    private static string PluginScopeName(PluginScope scope) => scope == PluginScope.Project ? "project" : "global";

    private static string PluginStateName(PluginPackageState state) => state switch
    {
        PluginPackageState.Running => "running",
        PluginPackageState.Disabled => "disabled",
        PluginPackageState.Failed => "failed",
        PluginPackageState.Unsupported => "unsupported",
        _ => "stopped",
    };

    private static string PluginChangeName(PluginPackageChange change) => change switch
    {
        PluginPackageChange.Started => "started",
        PluginPackageChange.Reloaded => "reloaded",
        PluginPackageChange.Stopped => "stopped",
        PluginPackageChange.BuildFailed => "buildFailed",
        PluginPackageChange.StartFailed => "startFailed",
        PluginPackageChange.Disabled => "disabled",
        _ => "unchanged",
    };

    // "builtin:git" is listed as "git"; an active plugin is one that runs.
    private static string PluginId(string runtimeKey) => runtimeKey[(runtimeKey.IndexOf(':', StringComparison.Ordinal) + 1)..];

    private static string? PluginState(string? state) => string.Equals(state, "active", StringComparison.Ordinal) ? "running" : state;

    private static void WritePlugin(AltaCommandContext context, string type, AltaPluginSummary plugin)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            id = PluginId(plugin.RuntimeKey),
            plugin.RuntimeKey,
            plugin.DisplayName,
            pluginVersion = plugin.Version,
            plugin.Scope,
            state = PluginState(plugin.State),
            plugin.Diagnostics,
        });
    }
}
