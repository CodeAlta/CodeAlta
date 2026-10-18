using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Plugins;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

internal sealed partial class BuiltInAltaCommandContributor : IAltaCommandContributor
{
    // Agent-originated sends should return after the delegated run is accepted, not after
    // the delegated LLM finishes. The timeout is only a submission-acknowledgement guard.
    private static readonly TimeSpan AgentCallerSubmitAckTimeout = TimeSpan.FromSeconds(5);
    private const string NotificationFollowUpNextStep = "Do not call any tool, shell sleep, reminder, status, tail, events, or polling command to wait for completion; yield control and wait for parent-session notifications.";
    private const string NotificationFollowUpGuidance = "Do not poll or actively wait for this delegated session to complete. CodeAlta will forward the delegated session's final assistant reply to the parent session automatically.";
    private const string AskNextStep = "Do not call another tool or poll. End your turn now with one short sentence saying that you are waiting for the answer; the next user prompt contains the ask response.";
    private const string AskPayloadSchema = "{\"type\":\"object\",\"properties\":{\"file\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false},\"questions\":{\"type\":\"array\",\"minItems\":1,\"items\":{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"question\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"choices\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"}},\"required\":[\"title\"],\"additionalProperties\":false}},\"freeform\":{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"placeholder\":{\"type\":\"string\"}},\"additionalProperties\":false}},\"required\":[\"title\",\"question\"],\"additionalProperties\":false}}},\"required\":[\"questions\"],\"additionalProperties\":false}";

    private static readonly string[] NotificationFollowUpForbiddenWaitActions =
    [
        "tool call",
        "shell sleep",
        "reminder",
        "session status",
        "session tail",
        "session events",
        "polling",
    ];

    private static readonly AltaCommandPolicy[] Policies =
    [
        Read("version", supportsCatalogOnlyContext: true),
        Mutating("ask"),
        Read("notes get"),
        Mutating("notes set"),
        Mutating("notes clear"),
        Read("note get"),
        Mutating("note set"),
        Mutating("note clear"),
        Read("project list", supportsCatalogOnlyContext: true),
        Read("project show", supportsCatalogOnlyContext: true),
        Read("project resolve", supportsCatalogOnlyContext: true),
        Read("project current", supportsCatalogOnlyContext: true),
        Mutating("project upsert", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Mutating("project add", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Mutating("project rename", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Mutating("project archive", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Mutating("project unarchive", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Disruptive("project remove"),
        Read("session current", supportsCatalogOnlyContext: true),
        Read("session list"),
        Read("session info"),
        Read("session status"),
        Read("session result"),
        Read("session report"),
        Read("session children"),
        Read("session model"),
        Read("session metrics"),
        Read("session tail"),
        Read("session events"),
        Read("session turns"),
        Mutating("session create"),
        Mutating("session send"),
        Mutating("session steer"),
        Mutating("session queue"),
        Mutating("session set_agent"),
        Mutating("session title"),
        Disruptive("session abort"),
        Mutating("session compact"),
        Mutating("session fork"),
        Disruptive("session rewind"),
        Mutating("session archive"),
        Mutating("session restore"),
        Read("session join"),
        Mutating("session message"),
        Mutating("session request"),
        Mutating("reminder create"),
        Read("reminder list"),
        Mutating("reminder delete"),
        Mutating("job start"),
        Read("job list"),
        Read("job status"),
        Read("job output"),
        Disruptive("job cancel"),
        Read("skill list", supportsCatalogOnlyContext: true),
        Read("skill show", supportsCatalogOnlyContext: true),
        Mutating("skill activate"),
        Mutating("skills activate"),
        Mutating("skills_activate"),
        Read("tool status", supportsCatalogOnlyContext: true),
        Read("tool list", supportsCatalogOnlyContext: true),
        Read("tool capability list", supportsCatalogOnlyContext: true),
        Read("provider list"),
        Read("provider model list"),
        Read("model list"),
        Read("model show"),
        Read("model resolve"),
        Read("prompt list", supportsCatalogOnlyContext: true),
        Read("prompt show", supportsCatalogOnlyContext: true),
        Mutating("prompt create", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Mutating("prompt edit", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Read("plugin list"),
        Read("plugin status"),
        Read("plugin api", requiresRuntime: false, supportsCatalogOnlyContext: true),
    ];

    // They change nothing but what the window shows.
    private static readonly AltaCommandPolicy DiffShowPolicy = Read("diff show");
    private static readonly AltaCommandPolicy EditorOpenPolicy = Read("editor open");

    public IEnumerable<CommandNode> CreateCommandLineNodes(AltaCommandContributionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        yield return CreateVersionCommand(context.Invocation);
        yield return CreateAskCommand(context.Invocation);
        yield return CreateNotesCommand(context.Invocation, "notes");
        yield return CreateNotesCommand(context.Invocation, "note");
        yield return CreateProjectCommand(context.Invocation);
        // The spaces that group the projects, in a host that keeps them.
        if (context.Invocation.Services.Get<SpaceCatalog>() is not null)
        {
            yield return CreateSpaceCommand(context.Invocation);
        }

        yield return CreateSessionCommand(context.Invocation);
        yield return CreateReminderCommand(context.Invocation);
        yield return CreateJobCommand(context.Invocation);
        yield return CreateSkillCommand(context.Invocation);
        yield return CreateSkillsAliasCommand(context.Invocation);
        yield return CreateSkillsActivateAliasCommand(context.Invocation);
        yield return CreateToolCommand(context.Invocation);
        yield return CreateProviderCommand(context.Invocation);
        yield return CreateModelCommand(context.Invocation);
        yield return CreatePromptCommand(context.Invocation);
        yield return CreatePluginCommand(context.Invocation);
        // Only a host with a view for changed files (the desktop window) has the command.
        if (context.Invocation.Services.Get<IAltaChangesView>() is not null)
        {
            yield return CreateDiffCommand(context.Invocation);
        }

        // Likewise for the code editor.
        if (context.Invocation.Services.Get<IAltaEditorView>() is not null)
        {
            yield return CreateEditorCommand(context.Invocation);
        }

        // And for the canvases of plugins.
        if (context.Invocation.Services.Get<IAltaCanvasView>() is not null)
        {
            yield return CreateCanvasCommand(context.Invocation);
            // The landing page is one of them.
            yield return CreateLandingCommand(context.Invocation);
        }

        // The user guide, in a host that ships it.
        if (context.Invocation.Services.Get<CodeAlta.Catalog.Documentation.ShippedDocumentation>() is not null)
        {
            yield return CreateDocumentationCommand(context.Invocation);
        }

        // The search index, in a host that keeps one (the desktop window).
        if (context.Invocation.Services.Get<CodeAlta.Search.SearchService>() is not null)
        {
            yield return CreateSearchCommand(context.Invocation);
        }

        // And for the terminals.
        if (context.Invocation.Services.Get<IAltaTerminals>() is not null)
        {
            yield return CreateTerminalCommand(context.Invocation);
        }

        // And for the automations.
        if (context.Invocation.Services.Get<IAltaAutomations>() is not null)
        {
            yield return CreateAutomationCommand(context.Invocation);
        }

        // The tasks and the plans of projects, in a host that keeps them.
        if (context.Invocation.Services.Get<CodeAlta.Catalog.WorkItems.WorkItemService>() is not null)
        {
            yield return CreateTaskCommand(context.Invocation);
            yield return CreatePlanCommand(context.Invocation);
        }

        // The bookmarks of sessions and of their messages, in a host that keeps them.
        if (context.Invocation.Services.Get<CodeAlta.Catalog.Bookmarks.BookmarkService>() is not null)
        {
            yield return CreateBookmarkCommand(context.Invocation);
        }

        // How the window looks: only a host with a window has the commands.
        if (context.Invocation.Services.Get<IAltaAppearance>() is not null)
        {
            yield return CreateAppearanceCommand(context.Invocation);
        }

        // The issues and the pull requests of projects, from the trackers the plugins of the host know.
        if (context.Invocation.Services.Get<CodeAlta.Plugins.PluginRuntimeManager>() is not null)
        {
            yield return CreateIssueCommand(context.Invocation);
            // Both names are written: one item, or the list of them.
            yield return CreateIssueCommand(context.Invocation, "issues");
        }
    }

    public IEnumerable<AltaCommandPolicy> GetCommandPolicies(AltaCommandContributionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PoliciesFor(context.Invocation);
    }

    private static IReadOnlyList<AltaCommandPolicy> PoliciesFor(AltaCommandContext context)
    {
        var changes = context.Services.Get<IAltaChangesView>() is not null;
        var editor = context.Services.Get<IAltaEditorView>() is not null;
        var terminals = context.Services.Get<IAltaTerminals>() is not null;
        var automations = context.Services.Get<IAltaAutomations>() is not null;
        var workshop = context.Services.Get<AltaPluginWorkshop>();
        var workItems = context.Services.Get<CodeAlta.Catalog.WorkItems.WorkItemService>() is not null;
        var bookmarks = context.Services.Get<CodeAlta.Catalog.Bookmarks.BookmarkService>() is not null;
        var issues = context.Services.Get<CodeAlta.Plugins.PluginRuntimeManager>() is not null;
        var appearance = context.Services.Get<IAltaAppearance>() is not null;
        var spaces = context.Services.Get<SpaceCatalog>() is not null;
        var canvases = context.Services.Get<IAltaCanvasView>() is not null;
        var documentation = context.Services.Get<CodeAlta.Catalog.Documentation.ShippedDocumentation>() is not null;
        var search = context.Services.Get<CodeAlta.Search.SearchService>() is not null;
        if (!changes && !editor && !terminals && !automations && !workItems && !bookmarks && !issues && !appearance && !spaces && !canvases && !documentation && !search && workshop is null) return Policies;
        var policies = new List<AltaCommandPolicy>(Policies);
        if (spaces) policies.AddRange(SpacePolicies);
        if (spaces && context.Services.Get<IAltaSpaceView>() is not null) policies.Add(SpaceSwitchPolicy);
        if (appearance) policies.AddRange(AppearancePolicies);
        if (workItems) policies.AddRange(WorkItemPolicies);
        if (bookmarks) policies.AddRange(BookmarkPolicies);
        if (issues) policies.AddRange(IssuePolicies);
        if (workshop is not null) policies.AddRange(PluginWorkshopPolicies);
        if (workshop?.OpenEditor is not null) policies.Add(PluginOpenPolicy);
        if (changes) policies.Add(DiffShowPolicy);
        if (editor) policies.Add(EditorOpenPolicy);
        if (canvases) policies.AddRange(CanvasPolicies);
        if (canvases) policies.AddRange(LandingPolicies);
        if (documentation) policies.AddRange(DocumentationPolicies);
        if (documentation && context.Services.Get<IAltaDocumentationView>() is not null) policies.Add(DocumentationOpenPolicy);
        if (search) policies.AddRange(SearchPolicies);
        if (terminals) policies.AddRange(TerminalPolicies);
        if (automations) policies.AddRange(AutomationPolicies);
        return policies;
    }

    private static IReadOnlyList<AltaCommandPolicy> GetEffectivePolicies(AltaCommandContext context)
    {
        if (context.Services.Get<AltaCommandRegistry>() is { } registry)
        {
            return registry.GetPolicies(context);
        }

        if (context.Services.Get<IAltaPluginCatalog>() is { } catalog)
        {
            return PoliciesFor(context).Concat(catalog.ListCommandPolicies())
                .OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return PoliciesFor(context);
    }

    private static AltaCommandPolicy Read(string path, bool requiresRuntime = true, bool supportsCatalogOnlyContext = false)
        => new()
        {
            Path = path,
            RequiresInProcessRuntime = requiresRuntime,
            SupportsCatalogOnlyContext = supportsCatalogOnlyContext,
        };

    private static AltaCommandPolicy Mutating(string path, bool requiresRuntime = true, bool supportsCatalogOnlyContext = false)
        => new()
        {
            Path = path,
            RequiresInProcessRuntime = requiresRuntime,
            SupportsCatalogOnlyContext = supportsCatalogOnlyContext,
            IsMutating = true,
        };

    private static AltaCommandPolicy Disruptive(string path)
        => new()
        {
            Path = path,
            RequiresInProcessRuntime = true,
            IsMutating = true,
            IsDisruptive = true,
        };

    private static Command CreateVersionCommand(AltaCommandContext context)
    {
        var command = Leaf("version", "Print the version of the CodeAlta application as JSONL.");
        command.Add((_, _) =>
        {
            // The application that runs, not this library, whose version is not the one of a release.
            var application = AltaApplicationVersion.Read();
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.version",
                version = 1,
                correlationId = context.CorrelationId,
                product = "CodeAlta",
                application = application.Application,
                informationalVersion = application.InformationalVersion,
                packageVersion = application.PackageVersion,
            });
            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static Command CreateAskCommand(AltaCommandContext context)
    {
        var useStdin = false;
        string? sessionId = null;
        var command = Leaf("ask", "Queue structured user questions for this session and return immediately with yield guidance.");
        command.Add("stdin", "Read the ask JSON payload from stdin. Required.", value => useStdin = value is not null);
        command.Add("session=", "Target session id. Defaults to the calling agent session; required outside an agent session.", value => sessionId = value);
        command.Add(async (_, _) => await HandleAskAsync(context, sessionId, useStdin).ConfigureAwait(false));
        AddHelpText(
            command,
            "Payload JSON Schema:",
            EscapeHelpText(AskPayloadSchema),
            "Example:",
            "  `alta ask --stdin`",
            EscapeHelpText("  stdin: {\"questions\":[{\"title\":\"Plan\",\"question\":\"Does this plan look correct?\",\"choices\":[{\"title\":\"Approve\"},{\"title\":\"Revise\"}],\"freeform\":{\"title\":\"Notes\",\"placeholder\":\"Optional notes...\"}}]}"),
            "LLM guidance: after a successful `alta.ask.queued` result, end your turn with one short sentence saying that you are waiting for the answer. Do not poll; the next user prompt contains the ask response.");
        return command;
    }

    private static Command CreateProjectCommand(AltaCommandContext context)
    {
        var group = Group("project", "Inspect and manage project catalog entries.");
        group.Add(CreateProjectListCommand(context));
        group.Add(CreateProjectShowCommand(context));
        group.Add(CreateProjectResolveCommand(context));
        group.Add(CreateProjectCurrentCommand(context));
        group.Add(CreateProjectUpsertCommand(context));
        group.Add(CreateProjectAddCommand(context));
        group.Add(CreateProjectRenameCommand(context));
        group.Add(CreateProjectArchiveCommand(context, archive: true));
        group.Add(CreateProjectArchiveCommand(context, archive: false));
        group.Add(CreateProjectRemoveCommand(context));
        AddHelpText(
            group,
            "A project is a folder CodeAlta knows. Projects are grouped in spaces (`alta space --help`): `spaces` names the spaces of a project besides the default one, which holds every project.",
            "Examples: `alta project current`; `alta project list`; `alta project list --all`; `alta project show CodeAlta`; `alta project resolve --path C:/code/CodeAlta`; `alta project add C:/code/Tomlyn --space work`.");
        return group;
    }

    private static Command CreateDiffCommand(AltaCommandContext context)
    {
        var group = Group("diff", "Show the changed files of a project to the user.");
        string? projectRef = null;
        string? file = null;
        var show = Leaf("show", "Open the changes view of a project in the CodeAlta window: its changed files and their diff.");
        show.Add("project=", "Project id, slug or path. Defaults to the project of the calling session, then to the cwd.", value => projectRef = value);
        show.Add("file=", "Changed file to select, relative to the repository root.", value => file = value);
        show.Add(async (_, _) => await HandleDiffShowAsync(context, projectRef, file).ConfigureAwait(false));
        AddHelpText(
            show,
            "The view lists what the work tree changed since the last commit, untracked files included.",
            "It only shows the changes to the user: to read a diff yourself, run git.",
            "The window shows one space at a time: for a project that space does not have, nothing is shown and the answer is `project.notInShownSpace`, with the space to show.",
            "Examples: `alta diff show`; `alta diff show --file src/app.ts`; `alta diff show --project CodeAlta`.");
        group.Add(show);
        AddHelpText(group, "Example: `alta diff show` opens the changed files of the current project for the user.");
        return group;
    }

    private static async ValueTask<int> HandleDiffShowAsync(AltaCommandContext context, string? projectRef, string? file)
    {
        if (context.Services.Get<IAltaChangesView>() is not { } view)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
                "Required in-process service 'IAltaChangesView' is unavailable.");
            return AltaExitCodes.ServiceUnavailable;
        }

        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var reference = NormalizeOptionalText(projectRef) ?? NormalizeOptionalText(context.Caller.SourceProjectId);
        var project = reference is not null
            ? await ResolveProjectAsync(catalog, reference, context, includeArchived: false).ConfigureAwait(false)
            : await catalog.GetByPathAsync(ResolvePath(context, context.Cwd ?? Environment.CurrentDirectory), context.CancellationToken).ConfigureAwait(false);
        if (project is null || project.Archived)
        {
            return NotFound(context, "project.notFound", reference is null ? "No catalog project matches the current directory." : $"Project '{reference}' was not found.");
        }

        // Only the name of a file inside the repository: the window selects it when it is one of the changed files.
        var path = NormalizeOptionalText(file)?.Replace('\\', '/');
        if (path is not null && (path.Length > 1024 || Path.IsPathRooted(path) || path.Split('/').Any(static segment => segment is "" or "." or "..")))
        {
            return UsageError(context, "usage.invalidFile", "The file must be a path relative to the repository root.", "alta diff show");
        }

        // A session that works in a git worktree of the project is shown the changes of that checkout.
        var worktree = NormalizeOptionalText(projectRef) is null ? CallerWorktree(context, project) : null;
        // The window is asked in any case: for a project its space does not have, it offers the user that space.
        var outOfView = await ProjectOutOfViewAsync(context, project).ConfigureAwait(false);
        if (!view.Show(project.Id, path, worktree))
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "view.unavailable", AltaExitCodes.ServiceUnavailable,
                "No CodeAlta window is open to show the changes.");
            return AltaExitCodes.ServiceUnavailable;
        }

        if (outOfView is { } elsewhere)
        {
            return ProjectNotInShownSpace(context, project, elsewhere.Shown, elsewhere.Home, "The changes were not shown");
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.diff.shown",
            version = 1,
            correlationId = context.CorrelationId,
            projectId = project.Id,
            project.Slug,
            projectPath = project.ProjectPath,
            file = path,
        });
        return AltaExitCodes.Success;
    }

    private static Command CreateEditorCommand(AltaCommandContext context)
    {
        var group = Group("editor", "Show the files of a project to the user in the code editor.");
        string? projectRef = null;
        string? file = null;
        string? line = null;
        string? column = null;
        var open = Leaf("open", "Open the code editor of a project in the CodeAlta window, and a file in it.");
        open.Add("project=", "Project id, slug or path. Defaults to the project of the calling session, then to the cwd.", value => projectRef = value);
        open.Add("file=", "File to open: a path relative to the project folder, or a full path inside it. Without it the editor opens with the files of the project.", value => file = value);
        open.Add("line=", "1-based line to go to in the file.", value => line = value);
        open.Add("column=", "1-based column on that line.", value => column = value);
        open.Add(async (_, _) => await HandleEditorOpenAsync(context, projectRef, file, line, column).ConfigureAwait(false));
        AddHelpText(
            open,
            "The editor is where the user reads and edits files: use it to show them a file, not to read one yourself.",
            "The window shows one space at a time: for a project that space does not have, nothing is opened and the answer is `project.notInShownSpace`, with the space to show.",
            "Examples: `alta editor open`; `alta editor open --file src/app.ts`; `alta editor open --file src/app.ts --line 120`; `alta editor open --project CodeAlta`.");
        group.Add(open);
        AddHelpText(group, "Example: `alta editor open --file readme.md` opens that file of the current project for the user.");
        return group;
    }

    // The folder the caller works in when it is a git worktree of the project: another checkout of its repository.
    // A session that works in one calls from there; a caller in the folder of the project, or elsewhere, has none.
    private static string? CallerWorktree(AltaCommandContext context, ProjectDescriptor project)
    {
        if (NormalizeOptionalText(context.Caller.SourceProjectId) is null || NormalizeOptionalText(context.Cwd) is not { } working)
        {
            return null;
        }

        try
        {
            if (!Path.IsPathFullyQualified(working) || !Directory.Exists(working))
            {
                return null;
            }

            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(working));
            var home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.ProjectPath));
            return !string.Equals(folder, home, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && GitWorktreeService.SameRepository(home, folder)
                ? folder
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static async ValueTask<int> HandleEditorOpenAsync(AltaCommandContext context, string? projectRef, string? file, string? line, string? column)
    {
        if (context.Services.Get<IAltaEditorView>() is not { } view)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
                "Required in-process service 'IAltaEditorView' is unavailable.");
            return AltaExitCodes.ServiceUnavailable;
        }

        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var reference = NormalizeOptionalText(projectRef) ?? NormalizeOptionalText(context.Caller.SourceProjectId);
        var project = reference is not null
            ? await ResolveProjectAsync(catalog, reference, context, includeArchived: false).ConfigureAwait(false)
            : await catalog.GetByPathAsync(ResolvePath(context, context.Cwd ?? Environment.CurrentDirectory), context.CancellationToken).ConfigureAwait(false);
        if (project is null || project.Archived)
        {
            return NotFound(context, "project.notFound", reference is null ? "No catalog project matches the current directory." : $"Project '{reference}' was not found.");
        }

        int? lineNumber = null, columnNumber = null;
        if (NormalizeOptionalText(line) is { } lineText)
        {
            if (!int.TryParse(lineText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            {
                return UsageError(context, "usage.invalidLine", "The line must be a number, starting at 1.", "alta editor open");
            }

            lineNumber = number;
        }

        if (NormalizeOptionalText(column) is { } columnText)
        {
            if (lineNumber is null || !int.TryParse(columnText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            {
                return UsageError(context, "usage.invalidColumn", "The column must be a number, starting at 1, and needs a line.", "alta editor open");
            }

            columnNumber = number;
        }

        // The code editor shows the folder of the project. A session that works in a git worktree has other files
        // there: showing the ones of the project under their names would show what the session did not write.
        if (NormalizeOptionalText(projectRef) is null && CallerWorktree(context, project) is { } worktree)
        {
            return Unsupported(context, "editor.worktree",
                $"The code editor shows the files of the project folder, and this session works in the git worktree '{worktree}'. Use `alta diff show` to show the changes of the worktree.");
        }

        // A file of the project folder: its relative path, or a full path that is inside the folder.
        var path = NormalizeOptionalText(file);
        if (path is not null)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.ProjectPath));
            if (Path.IsPathFullyQualified(path))
            {
                var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
                path = relative == "." || Path.IsPathRooted(relative) ? null : relative;
            }

            // Either separator on every system, as `alta diff show` and the window take a path.
            path = path?.Replace('\\', '/');
            if (path is null || path.Length > 1024 || Path.IsPathRooted(path) || path.Contains(':') || path.Split('/').Any(static segment => segment is "" or "." or ".."))
            {
                return UsageError(context, "usage.invalidFile", "The file must be a path inside the project folder.", "alta editor open");
            }

            if (!File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))))
            {
                return NotFound(context, "file.notFound", $"File '{path}' was not found in the project folder.");
            }
        }
        else if (lineNumber is not null)
        {
            return UsageError(context, "usage.missingFile", "A line needs the file it is in: add `--file`.", "alta editor open");
        }

        // The window is asked in any case: for a project its space does not have, it offers the user that space.
        var outOfView = await ProjectOutOfViewAsync(context, project).ConfigureAwait(false);
        if (!view.Open(project.Id, path, lineNumber, columnNumber))
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "view.unavailable", AltaExitCodes.ServiceUnavailable,
                "No CodeAlta window is open to show the editor.");
            return AltaExitCodes.ServiceUnavailable;
        }

        if (outOfView is { } elsewhere)
        {
            return ProjectNotInShownSpace(context, project, elsewhere.Shown, elsewhere.Home, "The editor was not opened");
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.editor.opened",
            version = 1,
            correlationId = context.CorrelationId,
            projectId = project.Id,
            project.Slug,
            projectPath = project.ProjectPath,
            file = path,
            line = lineNumber,
            column = columnNumber,
        });
        return AltaExitCodes.Success;
    }

    private static Command CreateNotesCommand(AltaCommandContext context, string name)
    {
        var group = Group(name, name == "note"
            ? "Compatibility alias for `notes`."
            : "Read and update the current session's sticky Markdown notes shown in the sidebar.");
        group.Add(CreateNotesGetCommand(context));
        group.Add(CreateNotesSetCommand(context));
        group.Add(CreateNotesClearCommand(context));
        AddHelpText(
            group,
            "Examples: `alta notes get`; `alta notes set --stdin`; `alta notes clear`.",
            "Use notes for session-specific sticky Markdown status, plans, and checklists intended to remain visible to the user.");
        return group;
    }

    private static Command CreateNotesGetCommand(AltaCommandContext context)
    {
        var command = Leaf("get", "Get the current sticky notes Markdown.");
        command.Add((_, _) => HandleNotesGetAsync(context));
        return command;
    }

    private static Command CreateNotesSetCommand(AltaCommandContext context)
    {
        var useStdin = false;
        var command = Leaf("set", "Replace the current sticky notes Markdown from stdin.");
        command.Add("stdin", "Read replacement Markdown from stdin. Accepted for consistency; stdin is read by default.", value => useStdin = value is not null);
        command.Add(async (_, _) => await HandleNotesSetAsync(context, useStdin).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta notes set --stdin` with Markdown content on stdin.");
        return command;
    }

    private static Command CreateNotesClearCommand(AltaCommandContext context)
    {
        var command = Leaf("clear", "Clear the current sticky notes Markdown.");
        command.Add(async (_, _) => await HandleNotesClearAsync(context).ConfigureAwait(false));
        return command;
    }

    private static Command CreateProjectListCommand(AltaCommandContext context)
    {
        var includeArchived = false;
        var detailed = false;
        string? space = null;
        var all = false;
        var command = Leaf("list", "List the projects of the current space from the CodeAlta catalog.");
        command.Add("include-archived", "Include archived projects.", value => includeArchived = value is not null);
        command.Add("detailed", "Emit one detailed metadata record per project instead of the compact project refs array.", value => detailed = value is not null);
        command.Add("space=", "List the projects of this space: id, start of id, or name. `default` lists every project.", value => space = value);
        command.Add("all", "List every project, whatever its spaces.", value => all = value is not null);
        command.Add(async (_, _) => await HandleProjectListAsync(context, includeArchived, detailed, space, all).ConfigureAwait(false));
        AddHelpText(command,
            "Without --space or --all, the projects are those of the space the CodeAlta window shows; without a window, every project. `spaceId` names the space that was listed.",
            "Examples: `alta project list`; `alta project list --all`; `alta project list --space work --detailed`.");
        return command;
    }

    private static Command CreateProjectShowCommand(AltaCommandContext context)
    {
        string? reference = null;
        var command = Leaf("show", "Show one project by id, slug, or path.");
        command.Add("<project>", "Project id, slug, or path.", value => reference = value);
        command.Add(async (_, _) => await HandleProjectShowAsync(context, reference).ConfigureAwait(false));
        return command;
    }

    private static Command CreateProjectResolveCommand(AltaCommandContext context)
    {
        string? path = null;
        var command = Leaf("resolve", "Resolve the current directory or a supplied path to a catalog project.");
        command.Add("path=", "Project path to resolve. Defaults to cwd/current directory.", value => path = value);
        command.Add(async (_, _) => await HandleProjectResolveAsync(context, path).ConfigureAwait(false));
        return command;
    }

    private static Command CreateProjectCurrentCommand(AltaCommandContext context)
    {
        var command = Leaf("current", "Show the project of the calling session, or the catalog project of the invocation cwd/current directory.");
        command.Add(async (_, _) => await HandleProjectCurrentAsync(context).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta project current` returns the project of the calling session, or else the catalog project matched by the live-tool cwd.");
        return command;
    }

    private static Command CreateProjectUpsertCommand(AltaCommandContext context)
    {
        string? path = null;
        var command = Leaf("upsert", "Ensure a local path exists as a project catalog entry.");
        command.Add("<path>", "Local project path.", value => path = value);
        command.Add(async (_, _) => await HandleProjectUpsertAsync(context, path).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionCommand(AltaCommandContext context)
    {
        var group = Group("session", "Inspect, create, and control CodeAlta work sessions.");
        group.Add(CreateSessionCurrentCommand(context));
        group.Add(CreateSessionListCommand(context));
        group.Add(CreateSessionInfoCommand(context));
        group.Add(CreateSessionStatusCommand(context));
        group.Add(CreateSessionResultCommand(context));
        group.Add(CreateSessionReportCommand(context));
        group.Add(CreateSessionChildrenCommand(context));
        group.Add(CreateSessionModelCommand(context));
        group.Add(CreateSessionMetricsCommand(context));
        group.Add(CreateSessionTailCommand(context));
        group.Add(CreateSessionEventsCommand(context));
        group.Add(CreateSessionTurnsCommand(context));
        group.Add(CreateSessionCreateCommand(context));
        group.Add(CreateSessionSendCommand(context));
        group.Add(CreateSessionSteerCommand(context));
        group.Add(CreateSessionQueueCommand(context));
        group.Add(CreateSessionSetAgentCommand(context));
        group.Add(CreateSessionTitleCommand(context));
        group.Add(CreateSessionAbortCommand(context));
        group.Add(CreateSessionCompactCommand(context));
        group.Add(CreateSessionForkCommand(context));
        group.Add(CreateSessionRewindCommand(context));
        group.Add(CreateSessionArchiveCommand(context, archive: true));
        group.Add(CreateSessionArchiveCommand(context, archive: false));
        group.Add(CreateSessionJoinCommand(context));
        group.Add(CreateSessionMessageCommand(context));
        group.Add(CreateSessionRequestCommand(context));
        AddHelpText(
            group,
            "Common: list by project, create child sessions, send prompts, inspect result/metrics snapshots, and steer active runs.",
            "A session is a list of turns (`session turns`): `session fork` starts a new",
            "session at one of them, `session rewind` removes one and the turns after it.",
            "`session archive` takes a session out of the lists, `session restore` brings it back.",
            "Examples:",
            "  `alta session current`",
            "  `alta session list --project CodeAlta --state all --limit 20`",
            "  `alta session create --project CodeAlta --same-model-as <session-id>`",
            "  `alta session send <session-id> --stdin`",
            "  `alta session result <session-id>`",
            "  `alta session report <session-id-1> <session-id-2> --include=result,metrics`",
            "  `alta session turns <session-id> --last 5`",
            "  `alta session fork <session-id> --before 4 --title \"Other approach\"`");
        return group;
    }

    private static Command CreateSessionCurrentCommand(AltaCommandContext context)
    {
        var command = Leaf("current", "Show the calling agent's current session id, and the git worktree it works in when it has one.");
        command.Add(async (_, _) => await HandleSessionCurrentAsync(context).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta session current` returns the current caller session id for an agent-invoked live-tool call.");
        return command;
    }

    private static Command CreateSessionListCommand(AltaCommandContext context)
    {
        string? project = null;
        string? space = null;
        string? state = null;
        string? provider = null;
        var limit = 50;
        var includeMetrics = false;
        var includeArchived = false;
        var command = Leaf("list", "List recoverable/live sessions as JSONL.");
        command.Add("project=", "Filter by project id, slug, or path.", value => project = value);
        command.Add("space=", "Filter by space: id, start of id, or name. The sessions listed in that space.", value => space = value);
        command.Add("state=", "Filter by running, idle, inactive, archived (only the archived sessions), or all.", value => state = ValidateState(value));
        command.Add("provider=", "Filter by provider id.", value => provider = value);
        command.Add("backend=", "Compatibility alias for --provider.", value => provider = value);
        command.Add("limit=", "Maximum sessions to return.", (int value) => limit = value);
        command.Add("metrics", "Include compact session metrics for emitted rows. This reads stored session history.", value => includeMetrics = value is not null);
        command.Add("archived", "Include the archived sessions, which are left out otherwise.", value => includeArchived = value is not null);
        command.Add(async (_, _) => await HandleSessionListAsync(context, project, space, state, provider, limit, includeMetrics, includeArchived).ConfigureAwait(false));
        AddHelpText(command,
            "Examples: `alta session list --project CodeAlta --state all --limit 20`; add `--metrics` for compact duration/usage fields.",
            "Archived sessions are left out unless `--archived` (or `--state all`) is given; `--state archived` lists only them.",
            "`--space <space>` lists the sessions of a space: the ones that record it, and those created before sessions had a space in every space of their project. `spaceId` is the space a session records.");
        return command;
    }

    private static Command CreateSessionInfoCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var command = Leaf("info", "Show one session's agent prompt, model selection, status, and timestamps.");
        command.Add("<session-id>?", "CodeAlta session id. Defaults to the calling agent session when omitted.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSessionShowAsync(context, FirstNonEmpty(sessionId, context.Caller.SourceSessionId), "alta.session.info").ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session info`; `alta session info <session-id>`.");
        return command;
    }

    private static Command CreateSessionStatusCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var command = Leaf("status", "Show compact live status for one session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSessionShowAsync(context, sessionId, "alta.session.status").ConfigureAwait(false));
        AddHelpText(command, "Example: `alta session status <session-id>` after choosing a session from `alta session list`.");
        return command;
    }

    private static Command CreateSessionResultCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var scope = "last-turn";
        var command = Leaf("result", "Return one session's final answer or error with compact metrics.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("scope=", "Result scope: last-turn or session. Defaults to last-turn.", value => scope = value ?? scope);
        command.Add(async (_, _) => await HandleSessionResultAsync(context, sessionId, scope).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta session result <session-id>` after a completion notification or for diagnostics.");
        return command;
    }

    private static Command CreateSessionReportCommand(AltaCommandContext context)
    {
        var sessionIds = new List<string>();
        var scope = "last-turn";
        var include = "result,metrics";
        var useStdin = false;
        var command = Leaf("report", "Aggregate result/metric snapshots for multiple sessions.");
        command.Add("<session-id>*", "Session ids to report.", value =>
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                sessionIds.Add(value);
            }
        });
        command.Add("stdin", "Read additional session ids from stdin, separated by whitespace, commas, or newlines.", value => useStdin = value is not null);
        command.Add("scope=", "Result scope: last-turn or session. Defaults to last-turn.", value => scope = value ?? scope);
        command.Add("include=", "Comma-separated sections: result,metrics. Defaults to result,metrics.", value => include = value ?? include);
        command.Add(async (_, _) => await HandleSessionReportAsync(context, sessionIds, useStdin, scope, include).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session report <session-id-1> <session-id-2> --include=result,metrics`; `alta session report --stdin --include=result,metrics`.");
        return command;
    }

    private static Command CreateSessionChildrenCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var recursive = false;
        var command = Leaf("children", "List child sessions for a parent session.");
        command.Add("<session-id>", "Parent session id.", value => sessionId = value);
        command.Add("recursive", "Include descendants recursively.", value => recursive = value is not null);
        command.Add(async (_, _) => await HandleSessionChildrenAsync(context, sessionId, recursive).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionModelCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var command = Leaf("model", "Show the provider/model/reasoning selection for one session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSessionModelAsync(context, sessionId).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionMetricsCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var scope = "last-turn";
        var command = Leaf("metrics", "Summarize session timing, answer, tool, and usage metrics.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("scope=", "Metric scope: last-turn or session. Defaults to last-turn.", value => scope = value ?? scope);
        command.Add(async (_, _) => await HandleSessionMetricsAsync(context, sessionId, scope).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session metrics <session-id>`; `alta session metrics <session-id> --scope session`.");
        return command;
    }

    private static Command CreateSessionTailCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var last = 20;
        var options = new SessionEventsOptions();
        var command = Leaf("tail", "Return a finite sanitized snapshot of recent session events.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("last=", "Number of recent events to return.", (int value) => last = value);
        AddSessionEventOptions(command, options);
        command.Add(async (_, _) => await HandleSessionEventsAsync(context, sessionId, since: null, limit: last, options, fromTail: true).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session tail <session-id> --last 10`; add `--include user,assistant` or `--kind assistant.message` to narrow output.");
        return command;
    }

    private static Command CreateSessionEventsCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        long? since = null;
        var limit = 50;
        var options = new SessionEventsOptions();
        var command = Leaf("events", "Return a finite sanitized snapshot of session events.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("since=", "Return events after this sequence number.", (long value) => since = value);
        command.Add("limit=", "Maximum events to return.", (int value) => limit = value);
        AddSessionEventOptions(command, options);
        command.Add(async (_, _) => await HandleSessionEventsAsync(context, sessionId, since, limit, options, fromTail: false).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session events <session-id> --since 120 --limit 50 --include assistant,tool`; `alta session events <session-id> --kind assistant.message --fields timestamp,kind,content`.");
        return command;
    }

    private static Command CreateSessionTurnsCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        int? last = null;
        var command = Leaf("turns", "List the turns of a session: the points it can be forked at or rewound to.");
        command.Add("<session-id>?", "CodeAlta session id. Defaults to the calling agent session when omitted.", value => sessionId = value);
        command.Add("last=", "Return the last <n> turns only.", (int value) => last = value);
        command.Add(async (_, _) => await HandleSessionTurnsAsync(context, FirstNonEmpty(sessionId, context.Caller.SourceSessionId), last).ConfigureAwait(false));
        AddHelpText(
            command,
            "A turn is one prompt and everything the session did until it became idle.",
            "Each `alta.session.turn` record has the number of the turn (`turn`, from 1),",
            "its `runId`, the start of its prompt and its `status`:",
            "`running`, `completed`, `failed` or `unfinished`.",
            "`session fork` and `session rewind` take the number or the run id.",
            "Examples: `alta session turns`; `alta session turns <session-id> --last 5`.");
        return command;
    }

    private static void AddSessionEventOptions(Command command, SessionEventsOptions options)
    {
        command.Add("include=", "Comma-separated categories: user,assistant,reasoning,tool,host,event.", value => options.Include = value);
        command.Add("kind=", "Comma-separated exact event kinds, e.g. assistant.message or tool.output.", value => options.Kind = value);
        command.Add("fields=", "Comma-separated output fields. Always includes type/version/correlationId. Supported: sessionId,sequenceNumber,timestamp,kind,role,source,content,text,metadata.", value => options.Fields = value);
        command.Add("no-tool-output", "Suppress tool output content records while keeping non-output events.", value => options.NoToolOutput = value is not null);
    }

    private static Command CreateSessionCreateCommand(AltaCommandContext context)
    {
        var options = new SessionCreateOptions();
        var command = Leaf("create", "Create a project or global session using resolved model selection.");
        AddModelSelectionOptions(command, options.Model);
        command.Add("project=", "Target project id, slug, or path.", value => options.Project = value);
        command.Add("global", "Create a global coordinator session.", value => options.Global = value is not null);
        command.Add("title=", "Session title.", value => options.Title = value);
        command.Add("parent=", "Explicit parent session id for lineage.", value => options.ParentSessionId = value);
        command.Add("no-parent", "Suppress automatic parent assignment.", value => options.NoParent = value is not null);
        command.Add("worktree", "Create a git worktree for the session: a checkout of its own, on a new branch `alta/<name>`.", value => options.Worktree = value is not null);
        command.Add("base=", "Branch or commit a new worktree starts from. Defaults to the commit the project folder is on.", value => options.WorktreeBase = value);
        command.Add("no-worktree", "Work in the project folder, also when the calling session works in a worktree.", value => options.NoWorktree = value is not null);
        command.Add("space=", "The space the session belongs to: id, start of id, or name. A session creates sessions only in its own space.", value => options.Space = value);
        command.Add("permission-mode=", "The permission mode the session starts in, one its provider offers (default, acceptEdits, bypassPermissions; Claude Code also auto, dontAsk).", value => options.PermissionMode = value);
        command.Add(async (_, _) => await HandleSessionCreateAsync(context, options).ConfigureAwait(false));
        AddHelpText(
            command,
            "A session belongs to one space for its whole life. A session created by a session is in the space of that",
            "session, which is the only space it can name with `--space`; without a calling session, it is in the space",
            "the window shows, or in the one `--space` names.",
            "A project session works in the project folder. With `--worktree` it works in a new git worktree instead,",
            "which starts from a commit: what is not committed in the project folder is not in it.",
            "A session created by a session that works in a worktree works in that same worktree, unless",
            "`--worktree` gives it one of its own or `--no-worktree` sends it to the project folder.",
            "The record names the folder as `worktreeDirectory`.",
            "With `--tier` the session runs on the first ready model of that tier of the settings, and falls back",
            "along it when the fallback is on. A session that names no model and inherits none starts on `standard`.",
            "With `--permission-mode` the session starts in that mode, as one chosen in the composer before it exists;",
            "without it, a session created by a session gets the mode the host gives it. A session that asks the user",
            "first does not create a session that asks less.",
            "Examples:",
            "  `alta session create --project CodeAlta --tier quick`",
            "  `alta session create --project CodeAlta --permission-mode acceptEdits`",
            "  `alta session create --project CodeAlta --prompt-id default --reasoning low`",
            "  `alta session create --project CodeAlta --same-model-as <session-id>`",
            "  `alta session create --project CodeAlta --worktree --base main`",
            "  `alta session create --global --model-ref codex:gpt-5.5@high`");
        return command;
    }

    private static Command CreateSessionSendCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var options = new PromptOptions();
        var command = Leaf("send", "Submit a normal prompt to a session and return run metadata.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        AddMessageOptions(command, options);
        command.Add("prompt-id=", "Agent prompt id for this send/session, as shown by `alta prompt list`. Defaults unchanged when omitted.", value => options.PromptId = value);
        command.Add("queue-if-busy", "Queue instead of failing when the target is busy, if a queue service is available.", value => options.QueueIfBusy = value is not null);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, sessionId, options, PromptDispatchKind.Send).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session send <session-id> --message \"Summarize status\"`; prefer `--stdin` for multi-line prompts.", "An agent sending to another running session steers its active turn; an idle session starts a new turn. Use --queue-if-busy to explicitly wait instead.", "Use `--prompt-id <id>` to select an agent prompt from `alta prompt list`; omit it to keep the current/default prompt selection.");
        return command;
    }

    private static Command CreateSessionSteerCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var options = new PromptOptions();
        var command = Leaf("steer", "Send steering input to an active run.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        AddMessageOptions(command, options);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, sessionId, options, PromptDispatchKind.Steer).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta session steer <session-id> --message \"Please focus on tests first\"`; use only while the target is running.");
        return command;
    }

    private static Command CreateSessionQueueCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var options = new PromptOptions();
        var command = Leaf("queue", "Queue a prompt for later session submission when a queue service is available.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        AddMessageOptions(command, options);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, sessionId, options, PromptDispatchKind.Queue).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionSetAgentCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        string? promptId = null;
        var command = Leaf("set_agent", "Set the agent prompt-id used by a session for subsequent runs.");
        command.Add("<session-id>?", "CodeAlta session id. Defaults to the calling agent session when omitted.", value => sessionId = value);
        command.Add("prompt-id=", "Agent prompt id from `alta prompt list`. Use `default` to restore the default agent prompt.", value => promptId = value);
        command.Add(async (_, _) => await HandleSessionSetAgentAsync(context, FirstNonEmpty(sessionId, context.Caller.SourceSessionId), promptId).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session set_agent --prompt-id reviewer`; `alta session set_agent <session-id> --prompt-id default`.");
        return command;
    }

    private static Command CreateSessionTitleCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        string? title = null;
        string? mode = null;
        var command = Leaf("title", "Show the title of a session, name the session, or choose how CodeAlta titles it.");
        command.Add("<session-id>?", "CodeAlta session id. Defaults to the calling agent session when omitted.", value => sessionId = value);
        command.Add("set=", "Name the session. A title that is given stays until somebody changes it.", value => title = value);
        command.Add("mode=", "How CodeAlta titles the session: `first_prompt`, `last_message`, `model`, or `default` to follow the setting. The title then follows the mode, unless `--set` names the session in the same call.", value => mode = value);
        command.Add(async (_, _) => await HandleSessionTitleAsync(context, FirstNonEmpty(sessionId, context.Caller.SourceSessionId), title, mode).ConfigureAwait(false));
        AddHelpText(
            command,
            "Without an option, prints the title, who chose it (`user` or `auto`) and how the session is titled.",
            "It changes that session only, never the setting. Modes: `first_prompt` keeps the title of the first prompt,",
            "`last_message` follows the first line of the last answer, `model` asks the model for a title.",
            "Examples:",
            "  `alta session title`",
            "  `alta session title --set \"Parser rewrite\"`",
            "  `alta session title <session-id> --mode last_message`",
            "  `alta session title --mode default`");
        return command;
    }

    private static Command CreateSessionAbortCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        string? reason = null;
        var command = Leaf("abort", "Abort active work in a session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("reason=", "Abort reason for diagnostics.", value => reason = value);
        command.Add(async (_, _) => await HandleSessionAbortAsync(context, sessionId, reason).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionCompactCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var submit = false;
        var command = Leaf("compact", "Request manual compaction for a session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("submit", "Reserved for submit-after-compaction behavior.", value => submit = value is not null);
        command.Add(async (_, _) => await HandleSessionCompactAsync(context, sessionId, submit).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionForkCommand(AltaCommandContext context)
    {
        var options = new SessionForkOptions();
        var command = Leaf("fork", "Create a session that starts with the turns of another one up to a point.");
        command.Add("<session-id>?", "CodeAlta session id. Defaults to the calling agent session when omitted.", value => options.SessionId = value);
        command.Add("before=", "The fork stops before this turn: a turn number or a run id from `alta session turns`.", value => options.Before = value);
        command.Add("after=", "The fork keeps the turns up to this one, included: a turn number or a run id.", value => options.After = value);
        command.Add("title=", "Title of the fork. Defaults to the title of the session followed by `(fork)`.", value => options.Title = value);
        command.Add("parent=", "Make the fork a sub-agent of this session, which changes its instructions.", value => options.ParentSessionId = value);
        command.Add("worktree", "Create a git worktree for the fork: a checkout of its own, on a new branch `alta/<name>`.", value => options.Worktree = value is not null);
        command.Add("base=", "Branch or commit a new worktree starts from. Defaults to the commit the project folder is on.", value => options.WorktreeBase = value);
        command.Add("no-worktree", "Work in the project folder, also when the session works in a worktree.", value => options.NoWorktree = value is not null);
        command.Add(async (_, _) => await HandleSessionForkAsync(context, options).ConfigureAwait(false));
        AddHelpText(
            command,
            "The session is left as it is and may be running:",
            "a turn in progress is not part of the fork.",
            "Without `--before` or `--after` the fork has every complete turn.",
            "The fork is a stored session: nothing runs in it until it is sent a prompt,",
            "with `alta session send <fork-id> --stdin`.",
            "A fork has no parent unless `--parent` names one, also when an agent forks.",
            "A parent makes the fork a sub-agent, with other instructions than its session:",
            "its first request then no longer reads the prompt cache of that session.",
            "The fork works where the session works: its worktree, or the project folder.",
            "`--worktree` gives it a worktree of its own, which starts from a commit,",
            "and `--no-worktree` sends it to the project folder.",
            "Files are not copied or restored: the fork sees the folder as it is now.",
            "Examples:",
            "  `alta session fork` forks the calling session at its last complete turn",
            "  `alta session fork <session-id> --before 4 --title \"Other approach\"`",
            "  `alta session fork <session-id> --after <run-id> --worktree`");
        return command;
    }

    private static Command CreateSessionRewindCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        string? before = null;
        string? after = null;
        var discard = false;
        var command = Leaf("rewind", "Remove a turn of a session and every turn after it.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("before=", "The first turn to remove: a turn number or a run id from `alta session turns`.", value => before = value);
        command.Add("after=", "The last turn to keep: a turn number or a run id.", value => after = value);
        command.Add("discard", "Do not keep the removed turns in a session of their own. This cannot be undone.", value => discard = value is not null);
        command.Add(async (_, _) => await HandleSessionRewindAsync(context, sessionId, before, after, discard).ConfigureAwait(false));
        AddHelpText(
            command,
            "One of `--before` or `--after` is required.",
            "The session must be idle: no run, no queued prompt, no question or permission",
            "waiting, no background work. A session cannot rewind itself.",
            "Files changed by the removed turns are NOT restored:",
            "only the conversation goes back.",
            "The session as it was before the rewind is kept as a new session,",
            "`<title> (before rewind)`, named by `removedTurnsSessionId`.",
            "With `--discard` it is not kept:",
            "nothing of the removed turns can be found again, and nothing can be undone.",
            "Examples:",
            "  `alta session rewind <session-id> --before 4`",
            "  `alta session rewind <session-id> --after <run-id> --discard`");
        return command;
    }

    private static Command CreateSessionJoinCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var command = Leaf("join", "Return non-blocking context needed to address a target session later.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSessionShowAsync(context, sessionId, "alta.session.join").ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionMessageCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var options = new PromptOptions { MessageKind = "note" };
        var command = Leaf("message", "Send an attributed peer-agent message to a session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        AddMessageOptions(command, options);
        command.Add("kind=", "Message kind: note, request, handoff, or answer.", value => options.MessageKind = ValidateMessageKind(value));
        command.Add(async (_, _) => await HandleSessionSendAsync(context, sessionId, options, PromptDispatchKind.Message).ConfigureAwait(false));
        AddHelpText(command, "Agent-to-agent messages steer a running recipient and start a new turn when it is idle. If live steering is unsupported, the message is durably queued.");
        return command;
    }

    private static Command CreateSessionRequestCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var options = new PromptOptions { MessageKind = "request" };
        var command = Leaf("request", "Send an attributed peer-agent request to a session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        AddMessageOptions(command, options);
        command.Add("reply-requested", "Annotate the request as expecting a reply.", value => options.ReplyRequested = value is not null);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, sessionId, options, PromptDispatchKind.Request).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta session request <session-id> --reply-requested --stdin` for coordinator-to-agent requests.", "Agent-to-agent requests steer a running recipient and start a new turn when it is idle. If live steering is unsupported, the request is durably queued.");
        return command;
    }

    private static Command CreateReminderCommand(AltaCommandContext context)
    {
        var group = Group("reminder", "Create, list, and delete delayed prompt reminders.");
        group.Add(CreateReminderCreateCommand(context));
        group.Add(CreateReminderListCommand(context));
        group.Add(CreateReminderDeleteCommand(context));
        AddHelpText(
            group,
            "Examples: `alta reminder create --duration 60 --content \"Check status\"`; `alta reminder create --duration 300 --repeat 3 --session <session-id> --stdin`; `alta reminder list --all`; `alta reminder delete <reminder-id>`.");
        return group;
    }

    private static Command CreateReminderCreateCommand(AltaCommandContext context)
    {
        var options = new ReminderCreateOptions();
        var command = Leaf("create", "Schedule prompt content to be sent to a session later.");
        command.Add("duration=", "Delay before each firing, in seconds; TimeSpan values such as 00:01:00 are also accepted.", value => options.Duration = value);
        command.Add("content=", "Prompt content to send when the reminder fires. Prefer --stdin for multi-line content.", value => options.Content = value);
        command.Add("stdin", "Read reminder prompt content from stdin.", value => options.UseStdin = value is not null);
        command.Add("session=", "Target CodeAlta session id. Defaults to the caller's current session when available.", value => options.SessionId = value);
        command.Add("session-id=", "Alias for --session.", value => options.SessionId = value);
        command.Add("repeat=", "Number of total firings. Defaults to 1.", value => options.Repeat = value);
        command.Add(async (_, _) => await HandleReminderCreateAsync(context, options).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta reminder create --duration 60 --content \"Remind me to check tests\"`; use `--session <session-id>` to target another session and `--repeat 10` to fire ten times.");
        return command;
    }

    private static Command CreateReminderListCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var includeCompleted = false;
        var command = Leaf("list", "List scheduled prompt reminders.");
        command.Add("session=", "Filter by target session id.", value => sessionId = value);
        command.Add("session-id=", "Alias for --session.", value => sessionId = value);
        command.Add("all", "Include completed reminders as well as active reminders.", value => includeCompleted = value is not null);
        command.Add((_, _) => ValueTask.FromResult(HandleReminderList(context, sessionId, includeCompleted)));
        return command;
    }

    private static Command CreateReminderDeleteCommand(AltaCommandContext context)
    {
        string? reminderId = null;
        var command = Leaf("delete", "Delete a scheduled prompt reminder.");
        command.Add("<reminder-id>", "Reminder id returned by `alta reminder create` or `alta reminder list`.", value => reminderId = value);
        command.Add((_, _) => ValueTask.FromResult(HandleReminderDelete(context, reminderId)));
        return command;
    }

    private static Command CreateSkillCommand(AltaCommandContext context)
    {
        var group = Group("skill", "Discover and activate CodeAlta-managed skills.");
        group.Add(CreateSkillListCommand(context));
        group.Add(CreateSkillShowCommand(context));
        group.Add(CreateSkillActivateCommand(context, "activate"));
        AddHelpText(group,
            "Examples: `alta skill list`; `alta skill show <skill-name>`; `alta skill activate <skill-name>` for your own session; `alta skill activate <skill-name> --session <session-id>` for another one.",
            "Many skills: `alta skill list --query <words>`, `--scope project|user|builtin|plugin` and `--limit <n>` narrow the list; in CodeAlta Desktop, `alta search --kind skill <need>` finds skills by what they do.");
        return group;
    }

    private static Command CreateSkillsAliasCommand(AltaCommandContext context)
    {
        var group = Group("skills", "Compatibility alias for `skill`.");
        group.Add(CreateSkillActivateCommand(context, "activate"));
        return group;
    }

    private static Command CreateSkillsActivateAliasCommand(AltaCommandContext context)
    {
        return CreateSkillActivateCommand(context, "skills_activate");
    }

    private static Command CreateSkillListCommand(AltaCommandContext context)
    {
        string? project = null;
        string? query = null;
        string? scope = null;
        string? limit = null;
        var detailed = false;
        var command = Leaf("list", "List discovered CodeAlta skills.");
        command.Add("project=", "Project id, slug, or path for project-local skill roots.", value => project = value);
        command.Add("detailed", "Emit one detailed metadata record per skill instead of compact skill refs.", value => detailed = value is not null);
        command.Add("query=", "Only the skills whose name, title or description has every word of the text, in any case.", value => query = value);
        command.Add("scope=", "Only the skills of one scope: project, user, builtin or plugin.", value => scope = value);
        command.Add("limit=", $"The most skills: every one for the names, {SkillListDetailedDefaultLimit.ToString(CultureInfo.InvariantCulture)} with `--detailed` when left out.", value => limit = value);
        command.Add(async (_, _) => await HandleSkillListAsync(context, project, detailed, query, scope, limit).ConfigureAwait(false));
        AddHelpText(command,
            "The skills keep the order of the catalog. With `--detailed`, `alta.skill.summary` says the `total` that matched, how many were `returned`, whether the limit cut them (`truncated`), and the command to go on with (`next`).",
            "Many skills: list the names first, then narrow with `--query <words>` or `--scope`, raise `--limit`, or read one with `alta skill show <skill-name>`; in CodeAlta Desktop, `alta search --kind skill <need>` finds skills by what they do.",
            "Examples: `alta skill list`; `alta skill list --query pdf`; `alta skill list --detailed --scope project --limit 20`.");
        return command;
    }

    private static Command CreateSkillShowCommand(AltaCommandContext context)
    {
        string? skillName = null;
        string? project = null;
        var command = Leaf("show", "Show one skill descriptor and content metadata.");
        command.Add("<skill-name>", "Skill name.", value => skillName = value);
        command.Add("project=", "Project id, slug, or path for project-local skill roots.", value => project = value);
        command.Add(async (_, _) => await HandleSkillShowAsync(context, skillName, project).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSkillActivateCommand(AltaCommandContext context, string name)
    {
        string? skillName = null;
        string? sessionId = null;
        var command = Leaf(name, name == "skills_activate" ? "Compatibility skill activation command." : "Activate a CodeAlta-managed skill for a session.");
        command.Add("<skill-name>", "Skill name.", value => skillName = value);
        command.Add("session=", "Target session id. Defaults to the calling session.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSkillActivateAsync(context, skillName, sessionId).ConfigureAwait(false));
        return command;
    }

    private static Command CreateToolCommand(AltaCommandContext context)
    {
        var group = Group("tool", "Inspect alta live-tool status, policies, and capabilities.");
        var capability = Group("capability", "Inspect alta capability metadata.");
        capability.Add(CreateToolCapabilityListCommand(context));
        group.Add(CreateToolStatusCommand(context));
        group.Add(CreateToolListCommand(context));
        group.Add(capability);
        AddHelpText(group, "Examples: `alta tool status`; `alta tool capability list` to see host/runtime/provider/plugin availability as JSONL.");
        return group;
    }

    private static Command CreateToolStatusCommand(AltaCommandContext context)
    {
        var command = Leaf("status", "Show live-tool availability and caller/output context.");
        command.Add((_, _) =>
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.tool.status",
                version = 1,
                correlationId = context.CorrelationId,
                available = true,
                inProcess = true,
                caller = context.Caller,
                cwd = context.Cwd,
                maxOutputRecords = context.MaxOutputRecords,
                maxOutputBytes = context.MaxOutputBytes,
            });
            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static Command CreateToolListCommand(AltaCommandContext context)
    {
        var detailed = false;
        var command = Leaf("list", "List built-in alta command policy entries.");
        command.Add("detailed", "Emit one detailed policy record per command instead of the compact paths/classification record.", value => detailed = value is not null);
        command.Add((_, _) =>
        {
            var policies = GetEffectivePolicies(context);
            if (!detailed)
            {
                WriteToolPaths(context, policies);
                return ValueTask.FromResult(AltaExitCodes.Success);
            }

            foreach (var policy in policies.OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase))
            {
                WritePolicy(context, "alta.tool.item", policy);
            }

            WriteSummary(context, "alta.tool.summary", policies.Count, truncated: false);
            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static Command CreateToolCapabilityListCommand(AltaCommandContext context)
    {
        var detailed = false;
        var command = Leaf("list", "List command capability classifications.");
        command.Add("detailed", "Emit legacy per-command/runtime/provider/plugin capability records instead of one compact aggregate record.", value => detailed = value is not null);
        command.Add((_, _) =>
        {
            var policies = GetEffectivePolicies(context);
            if (!detailed)
            {
                WriteToolCapabilities(context, policies);
                return ValueTask.FromResult(AltaExitCodes.Success);
            }

            var recordCount = 0;
            foreach (var policy in policies.OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase))
            {
                WritePolicy(context, "alta.tool.capability", policy);
                recordCount++;
            }

            recordCount += WriteRuntimeCapabilities(context);
            recordCount += WriteProviderCapabilities(context);
            recordCount += WritePluginCapabilities(context, policies);
            WriteSummary(context, "alta.tool.capability.summary", recordCount, truncated: false);
            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static int WriteRuntimeCapabilities(AltaCommandContext context)
    {
        var capabilities = GetRuntimeCapabilities(context);

        foreach (var capability in capabilities)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.tool.runtimeCapability",
                version = 1,
                correlationId = context.CorrelationId,
                capability = capability.Capability,
                capability.Available,
            });
        }

        return capabilities.Length;
    }

    private static int WriteProviderCapabilities(AltaCommandContext context)
    {
        var capabilities = GetProviderCapabilities(context);

        foreach (var capability in capabilities)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                // Compatibility: keep the legacy JSONL record type stable for live-tool clients.
                type = "alta.tool.backendCapability",
                version = 1,
                correlationId = context.CorrelationId,
                providerKey = capability.ProviderKey,
                ProviderId = capability.ProviderId,
                capability.Registered,
                capability.Configured,
                capability.SupportsAltaSessionTool,
            });
        }

        return capabilities.Length;
    }

    private static (string Capability, bool Available)[] GetRuntimeCapabilities(AltaCommandContext context) =>
    [
        ("catalog.project", context.Services.Get<ProjectCatalog>() is not null),
        ("catalog.session", context.Services.Get<SessionViewCatalog>() is not null),
        ("catalog.skill", context.Services.Get<SkillCatalog>() is not null),
        ("runtime.workSession", context.Services.Get<SessionRuntimeService>() is not null),
        ("providers.agentHub", context.Services.Get<AgentHub>() is not null),
        ("plugins.altaCatalog", context.Services.Get<IAltaPluginCatalog>() is not null),
        ("sessionTool.providerPolicy", context.Services.Get<IAltaSessionToolProviderPolicy>() is not null),
    ];

    private static ProviderCapability[] GetProviderCapabilities(AltaCommandContext context)
    {
        var descriptors = GetProviderDescriptors(context);
        var policy = context.Services.Get<IAltaSessionToolProviderPolicy>();
        return GetProviderProviderIds(context, descriptors)
            .Select(ProviderId => new ProviderCapability(
                ProviderId.Value,
                ProviderId.Value,
                descriptors.Any(descriptor => string.Equals(descriptor.ProviderId.Value, ProviderId.Value, StringComparison.OrdinalIgnoreCase)),
                descriptors.Any(descriptor => string.Equals(descriptor.ProviderId.Value, ProviderId.Value, StringComparison.OrdinalIgnoreCase)),
                policy?.SupportsAltaSessionTool(ProviderId.Value) ?? true))
            .ToArray();
    }

    private static ModelProviderId[] GetProviderProviderIds(AltaCommandContext context, IReadOnlyList<ModelProviderDescriptor> descriptors)
    {
        var ProviderIds = descriptors.Select(static descriptor => new ModelProviderId(descriptor.ProviderId.Value)).ToList();
        return ProviderIds.OrderBy(static id => id.Value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int WritePluginCapabilities(AltaCommandContext context, IReadOnlyList<AltaCommandPolicy> policies)
    {
        var capability = GetPluginCapability(context, policies);
        if (capability is null)
        {
            return 0;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.tool.pluginCapability",
            version = 1,
            correlationId = context.CorrelationId,
            capability.Available,
            capability.PluginCount,
            capability.PluginCommandCount,
        });
        return 1;
    }

    private static PluginCapability? GetPluginCapability(AltaCommandContext context, IReadOnlyList<AltaCommandPolicy> policies)
    {
        if (context.Services.Get<IAltaPluginCatalog>() is not { } catalog)
        {
            return null;
        }

        var plugins = catalog.ListPlugins();
        var builtInPolicyPaths = new HashSet<string>(Policies.Select(static policy => policy.Path), StringComparer.OrdinalIgnoreCase);
        var pluginCommandCount = policies.Count(policy => !builtInPolicyPaths.Contains(policy.Path));
        return new PluginCapability(true, plugins.Count, pluginCommandCount);
    }

    private static Command CreateProviderCommand(AltaCommandContext context)
    {
        var group = Group("provider", "Inspect registered providers.");
        var model = Group("model", "Inspect provider models.");
        model.Add(CreateModelListCommand(context, providerGroupAlias: true));
        group.Add(CreateProviderListCommand(context));
        group.Add(model);
        return group;
    }

    private static Command CreateProviderListCommand(AltaCommandContext context)
    {
        var detailed = false;
        var command = Leaf("list", "List registered provider ids.");
        command.Add("detailed", "Emit one detailed metadata record per provider instead of the compact providerKeys array.", value => detailed = value is not null);
        command.Add(async (_, _) => await HandleProviderListAsync(context, detailed).ConfigureAwait(false));
        return command;
    }

    private static Command CreateModelCommand(AltaCommandContext context)
    {
        var group = Group("model", "Inspect and resolve provider model selections.");
        group.Add(CreateModelListCommand(context, providerGroupAlias: false));
        group.Add(CreateModelShowCommand(context));
        group.Add(CreateModelResolveCommand(context));
        AddHelpText(group, "Examples: `alta model list --provider codex`; `alta model resolve --same-model-as <session-id> --reasoning low`.");
        return group;
    }

    private static Command CreateModelListCommand(AltaCommandContext context, bool providerGroupAlias)
    {
        var options = new ModelListOptions();
        var command = Leaf("list", providerGroupAlias ? "List models for registered providers." : "List models for registered providers.");
        command.Add("provider=", "Provider id filter.", value => options.Provider = value);
        command.Add("contains=", "Substring filter over model id, display name, and model ref.", value => options.Contains = value);
        command.Add("reasoning=", "Only include models supporting this reasoning effort.", value => options.ReasoningEffort = ParseReasoningOption(value));
        command.Add("supports-tools", "Only include models that report tool-call support.", value => options.SupportsTools = value is not null);
        command.Add("detailed", "Emit one detailed metadata record per model instead of the compact modelRefs array.", value => options.Detailed = value is not null);
        command.Add("refs", "Compatibility alias for the default compact modelRefs array.", _ => { });
        command.Add(async (_, _) => await HandleModelListAsync(context, options).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta model list --provider anthropic --contains sonnet`; `alta model list --provider codex --reasoning low`; `alta model list --provider anthropic --detailed`.");
        return command;
    }

    private static Command CreateModelShowCommand(AltaCommandContext context)
    {
        string? modelRef = null;
        var command = Leaf("show", "Show one model by compact model ref.");
        command.Add("<model-ref>?", "Model ref: provider:model[@reasoning].", value =>
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                modelRef = value;
            }
        });
        command.Add("model-ref=", "Model ref: provider:model[@reasoning].", value => modelRef = value);
        command.Add(async (_, _) => await HandleModelShowAsync(context, modelRef).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta model show --model-ref copilot:claude-sonnet-4.6@low`.");
        return command;
    }

    private static Command CreateModelResolveCommand(AltaCommandContext context)
    {
        var options = new AltaModelSelectionOptions();
        var command = Leaf("resolve", "Resolve model selection using session-create precedence rules.");
        AddModelSelectionOptions(command, options);
        command.Add(async (_, _) => await HandleModelResolveAsync(context, options).ConfigureAwait(false));
        return command;
    }

    private static Command CreatePromptCommand(AltaCommandContext context)
    {
        var group = Group("prompt", "List, inspect, and manage file-backed agent or system prompts.");
        group.Add(CreatePromptListCommand(context));
        group.Add(CreatePromptShowCommand(context));
        group.Add(CreatePromptCreateCommand(context));
        group.Add(CreatePromptEditCommand(context));
        AddHelpText(
            group,
            "Agent prompts live under prompts/agents/*.prompt.md and can choose a system prompt. System prompts live under prompts/system/*.system-prompt.md. Same-id files replace lower-precedence prompts by default; frontmatter `mode: append` appends instead.",
            "Default target is agent prompts; add `--system` for system prompts. `--scope` accepts global, project, builtin, or all and defaults to all.",
            "LLM workflow: run `alta prompt list` to find an agent prompt-id, optionally `alta prompt show <prompt-id>`, then use it with `alta session set_agent --prompt-id <prompt-id>` or one send via `alta session send <session-id> --prompt-id <prompt-id> ...`.",
            "Examples:",
            "  `alta prompt list --scope all`",
            "  `alta prompt list --system --verbose --scope project`",
            "  `alta prompt show default`",
            "  `alta prompt create reviewer --scope project --name Reviewer --stdin`",
            "  `alta prompt edit my-prompt --scope global` (returns path; add `--stdin` to replace file content)");
        return group;
    }

    private static Command CreatePromptListCommand(AltaCommandContext context)
    {
        var options = new PromptManagementOptions();
        var command = Leaf("list", "Progressively list prompts as JSONL records with prompt-id/id, name, description, source, and scope.");
        AddPromptManagementOptions(command, options, includeVerbose: true, includeContentInput: false);
        command.Add(async (_, _) => await HandlePromptListAsync(context, options).ConfigureAwait(false));
        AddHelpText(command, "Each matching prompt is emitted as one `alta.prompt` record for progressive consumption. Add `--verbose` to include full prompt content.");
        return command;
    }

    private static Command CreatePromptShowCommand(AltaCommandContext context)
    {
        string? promptId = null;
        var options = new PromptManagementOptions { Verbose = true };
        var command = Leaf("show", "Show one prompt by prompt-id/id, including full content.");
        command.Add("<prompt-id>", "Prompt id from `alta prompt list`, for example `default`.", value => promptId = value);
        AddPromptManagementOptions(command, options, includeVerbose: false, includeContentInput: false);
        command.Add(async (_, _) => await HandlePromptShowAsync(context, promptId, options).ConfigureAwait(false));
        return command;
    }

    private static Command CreatePromptCreateCommand(AltaCommandContext context)
    {
        string? promptId = null;
        var options = new PromptManagementOptions { Scope = "global" };
        var command = Leaf("create", "Create a complete global/project agent or system prompt file without overwriting an existing file.");
        command.Add("<prompt-id>", "Prompt id/file stem to create. Do not include directory separators or file suffixes.", value => promptId = value);
        AddPromptManagementOptions(command, options, includeVerbose: false, includeContentInput: true);
        command.Add("name=", "Display name for an agent prompt; defaults to the prompt id. For --system, optional metadata name.", value => options.Name = value);
        command.Add("description=", "Optional prompt description metadata.", value => options.Description = value);
        command.Add("system-prompt-id=", "System prompt id selected by an agent prompt. Defaults to `default`. Ignored with --system.", value => options.SystemPromptId = value);
        command.Add(async (_, _) => await HandlePromptCreateAsync(context, promptId, options).ConfigureAwait(false));
        AddHelpText(command, "Create requires --content or --stdin for the prompt body. Agent prompts write required frontmatter automatically; system prompts write optional name/description frontmatter when provided. Use `alta prompt edit` to replace an existing file.");
        return command;
    }

    private static Command CreatePromptEditCommand(AltaCommandContext context)
    {
        string? promptId = null;
        var options = new PromptManagementOptions { Scope = "global" };
        var command = Leaf("edit", "Return or update the editable global/project prompt file path.");
        command.Add("<prompt-id>", "Prompt id/file stem to edit or create.", value => promptId = value);
        AddPromptManagementOptions(command, options, includeVerbose: false, includeContentInput: true);
        command.Add(async (_, _) => await HandlePromptEditAsync(context, promptId, options).ConfigureAwait(false));
        AddHelpText(command, "Without `--content`/`--stdin`, returns the exact path an external editor should open. With content input, replaces that file. Built-in/all scopes are read-only and invalid for edit.");
        return command;
    }

    private static void AddPromptManagementOptions(Command command, PromptManagementOptions options, bool includeVerbose, bool includeContentInput)
    {
        command.Add("scope=", "Prompt source scope: global, project, builtin, or all. List/show default all; edit default global and only allows global/project.", value => options.Scope = ValidatePromptScope(value));
        command.Add("project=", "Project id, slug, or path for project-local prompts. Defaults to caller project, matching cwd catalog project, or cwd.", value => options.Project = value);
        command.Add("system", "Target system prompts under system/*.system-prompt.md. Omit for agent prompts under agents/*.prompt.md.", value => options.System = value is not null);
        if (includeVerbose)
        {
            command.Add("verbose", "Include full prompt content/body in prompt records.", value => options.Verbose = value is not null);
        }

        if (includeContentInput)
        {
            command.Add("content=", "Replacement file content. Prefer --stdin for multi-line prompt files.", value => options.Content = value);
            command.Add("stdin", "Read replacement file content from stdin.", value => options.UseStdin = value is not null);
        }
    }

    private static Command Group(string name, string description)
    {
        var command = new Command(name, description)
        {
            new CommandUsage(),
            new HelpOption(),
        };
        return command;
    }

    private static Command Leaf(string name, string description)
    {
        var command = new Command(name, description)
        {
            new CommandUsage(),
            new HelpOption(),
        };
        return command;
    }

    private static void AddHelpText(Command command, params string[] lines)
    {
        command.Add("");
        foreach (var line in lines)
        {
            command.Add(line);
        }
    }

    private static string EscapeHelpText(string text)
        => text.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);

    private static void AddMessageOptions(Command command, PromptOptions options)
    {
        command.Add("message=", "Prompt/message text.", value => options.Message = value);
        command.Add("stdin", "Read prompt/message text from stdin.", value => options.UseStdin = value is not null);
    }

    private static void AddModelSelectionOptions(Command command, AltaModelSelectionOptions options)
    {
        command.Add("model-ref=", "Compact model ref provider:model[@reasoning].", value => options.ModelRef = value);
        command.Add("same-model-as=", "Inherit model selection from a session id.", value => options.SameModelAsSessionId = value);
        command.Add("provider=", "Provider id.", value => options.ProviderKey = value);
        command.Add("model=", "Model id.", value => options.ModelId = value);
        command.Add("reasoning=", "Reasoning effort: minimal, low, medium, high, xhigh, max, or none.", value => options.ReasoningEffort = ParseReasoningOption(value));
        command.Add("prompt-id=", "Agent prompt id from `alta prompt list`. Defaults to `default` for new sessions.", value => options.PromptId = value);
        command.Add("tier=", "Tier of models from the settings: complex, standard or quick. Takes its first ready model.", value => options.Tier = ParseTierOption(value));
    }

    private static ModelTier ParseTierOption(string? value)
        => ModelTiers.Parse(value) ?? throw new CommandOptionException(
            $"Tier '{value}' is not valid. Use complex, standard or quick.",
            "--tier");

    private static AgentReasoningEffort? ParseReasoningOption(string? value)
    {
        if (AltaModelRef.TryParseReasoning(value, out var reasoning))
        {
            return reasoning;
        }

        throw new CommandOptionException(
            $"Reasoning effort '{value}' is not valid. Use minimal, low, medium, high, xhigh, max, or none.",
            "--reasoning");
    }

    private static string? ValidateState(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "running" or "idle" or "inactive" or "archived" or "all"
            ? normalized
            : throw new CommandOptionException("State must be running, idle, inactive, archived, or all.", "--state");
    }

    private static string? ValidatePromptScope(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "global" or "project" or "builtin" or "all"
            ? normalized
            : throw new CommandOptionException("Prompt scope must be global, project, builtin, or all.", "--scope");
    }

    private static string? ValidateMessageKind(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "note" or "request" or "handoff" or "answer"
            ? normalized
            : throw new CommandOptionException("Message kind must be note, request, handoff, or answer.", "--kind");
    }

    private static async ValueTask<int> HandleSessionCurrentAsync(AltaCommandContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Caller.SourceSessionId))
        {
            return UsageError(context, "usage.missingCurrentSession", "No current session id is available for this caller.", "alta session current");
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.current",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = context.Caller.SourceSessionId,
            sourceSessionId = context.Caller.SourceSessionId,
            sourceProjectId = context.Caller.SourceProjectId,
            sourceAgentId = context.Caller.SourceAgentId,
            callerKind = context.Caller.Kind,
            worktreeDirectory = await GetSessionWorktreeAsync(context, context.Caller.SourceSessionId).ConfigureAwait(false),
        });
        return AltaExitCodes.Success;
    }

    // The git worktree a session works in, or null: for one that works in the folder of its project, and when no runtime can say.
    private static async Task<string?> GetSessionWorktreeAsync(AltaCommandContext context, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || context.Services.Get<SessionRuntimeService>() is not { } runtime)
        {
            return null;
        }

        try
        {
            return await runtime.GetSessionWorktreeAsync(sessionId.Trim(), context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    // The worktrees of the host, or ones made for this command over the catalog of the host.
    private static GitWorktreeService? GetWorktrees(AltaCommandContext context)
        => context.Services.Get<GitWorktreeService>()
            ?? (context.Services.Get<CatalogOptions>() is { } options ? new GitWorktreeService(options, new CodeAltaConfigStore(options)) : null);

    private static async ValueTask<int> HandleAskAsync(AltaCommandContext context, string? sessionId, bool useStdin)
    {
        if (!useStdin)
        {
            return UsageError(context, "usage.missingStdin", "Ask requires a JSON payload via --stdin.", "alta ask");
        }

        var targetSessionId = FirstNonEmpty(sessionId, context.Caller.SourceSessionId);
        if (string.IsNullOrWhiteSpace(targetSessionId) || (string.IsNullOrWhiteSpace(sessionId) && !string.Equals(context.Caller.Kind, "agent", StringComparison.OrdinalIgnoreCase)))
        {
            return UsageError(context, "usage.missingSession", "A target session id is required outside a session caller. Use --session <session-id>.", "alta ask");
        }

        context.ClaimSessionWork(targetSessionId);
        if (!TryGetAskService(context, out var askService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var stdin = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(stdin))
        {
            return UsageError(context, "usage.missingPayload", "Ask requires a non-empty JSON payload on stdin.", "alta ask");
        }

        AltaAskRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(stdin, AltaAskJsonSerializerContext.Default.AltaAskRequest);
        }
        catch (JsonException ex)
        {
            return UsageError(context, "usage.invalidJson", $"Ask payload is not valid JSON: {ex.Message}", "alta ask");
        }

        if (request is null)
        {
            return UsageError(context, "usage.invalidPayload", "Ask payload must be a JSON object.", "alta ask");
        }

        var roots = await ResolveAskAllowedRootsAsync(context, targetSessionId).ConfigureAwait(false);
        if (roots.ExitCode != AltaExitCodes.Success)
        {
            return roots.ExitCode;
        }

        AltaAskRequest normalized;
        try
        {
            normalized = AltaAskValidator.ValidateAndNormalize(request, roots.AllowedRoots, roots.BaseDirectory);
        }
        catch (ArgumentException ex)
        {
            return UsageError(context, "usage.invalidPayload", ex.Message, "alta ask");
        }

        var result = await context.ObserveOriginalAsync(askService.QueueAsync(normalized, targetSessionId, context.Caller, context.CancellationToken)).ConfigureAwait(false);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.ask.queued",
            version = 1,
            correlationId = context.CorrelationId,
            result.AskId,
            result.SessionId,
            queued = true,
            shouldYield = true,
            recommendedAction = "stop",
            activeWaitAllowed = false,
            shouldPoll = false,
            nextStep = AskNextStep,
        });
        foreach (var error in result.NotificationErrors)
        {
            AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "ask.notificationFailed", $"Ask '{result.AskId}' was queued, but notification failed: {error}");
        }

        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleProjectListAsync(AltaCommandContext context, bool includeArchived, bool detailed, string? spaceRef, bool all)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (space, exitCode) = await ResolveProjectListSpaceAsync(context, spaceRef, all).ConfigureAwait(false);
        if (exitCode != AltaExitCodes.Success)
        {
            return exitCode;
        }

        var spaceId = space?.Id ?? SpaceDescriptor.DefaultId;
        var projects = await catalog.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var filtered = projects
            .Where(project => (includeArchived || !project.Archived) && (space is null || project.Spaces.Contains(space.Id, StringComparer.Ordinal)))
            .OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!detailed)
        {
            WriteProjectRefs(context, filtered, spaceId);
            return AltaExitCodes.Success;
        }

        foreach (var project in filtered)
        {
            WriteProject(context, "alta.project.item", project);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.project.summary",
            version = 1,
            correlationId = context.CorrelationId,
            count = filtered.Length,
            truncated = false,
            spaceId,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleProjectShowAsync(AltaCommandContext context, string? reference)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            return UsageError(context, "usage.missingProject", "Project reference is required.", "alta project show");
        }

        var project = await ResolveProjectAsync(catalog, reference, context, includeArchived: true).ConfigureAwait(false);
        if (project is null)
        {
            return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
        }

        WriteProject(context, "alta.project.detail", project);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleProjectResolveAsync(AltaCommandContext context, string? path)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var resolvedPath = ResolvePath(context, path ?? context.Cwd ?? Environment.CurrentDirectory);
        var project = await catalog.GetByPathAsync(resolvedPath, context.CancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return NotFound(context, "project.notFound", $"No catalog project matches path '{resolvedPath}'.");
        }

        WriteProject(context, "alta.project.resolution", project);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleProjectUpsertAsync(AltaCommandContext context, string? path)
    {
        if (await RefuseSettingsChangeAsync(context, "add a project").ConfigureAwait(false) is { } settingsRefused)
        {
            return settingsRefused;
        }

        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return UsageError(context, "usage.missingPath", "Project path is required.", "alta project upsert");
        }

        var resolvedPath = ResolvePath(context, path);
        if (!Directory.Exists(resolvedPath))
        {
            return NotFound(context, "project.pathNotFound", $"Project path '{resolvedPath}' does not exist.");
        }

        var project = await catalog.UpsertFromPathAsync(resolvedPath, context.CancellationToken).ConfigureAwait(false);
        WriteProject(context, "alta.project.upserted", project);
        NotifySpacesChanged(context);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionListAsync(
        AltaCommandContext context,
        string? projectFilter,
        string? spaceFilter,
        string? stateFilter,
        string? providerFilter,
        int limit,
        bool includeMetrics,
        bool includeArchived = false)
    {
        if (limit <= 0)
        {
            return UsageError(context, "usage.invalidLimit", "--limit must be a positive integer.", "alta session list");
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        ProjectDescriptor? project = null;
        if (!string.IsNullOrWhiteSpace(projectFilter))
        {
            if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
            {
                return AltaExitCodes.ServiceUnavailable;
            }

            project = await ResolveProjectAsync(catalog, projectFilter, context, includeArchived: true).ConfigureAwait(false);
            if (project is null)
            {
                return NotFound(context, "project.notFound", $"Project '{projectFilter}' was not found.");
            }
        }

        Func<AltaSessionInfo, bool> inSpace = static _ => true;
        if (!string.IsNullOrWhiteSpace(spaceFilter))
        {
            if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaceCatalog))
            {
                return AltaExitCodes.ServiceUnavailable;
            }

            var (space, exitCode) = await ResolveSpaceAsync(context, spaceCatalog, spaceFilter, "alta session list").ConfigureAwait(false);
            if (space is null)
            {
                return exitCode;
            }

            var spaces = await spaceCatalog.LoadAsync(context.CancellationToken).ConfigureAwait(false);
            var projects = (await spaceCatalog.Projects.LoadAsync(context.CancellationToken).ConfigureAwait(false))
                .ToDictionary(static value => value.Id, StringComparer.OrdinalIgnoreCase);
            inSpace = info => SpaceCatalog.SessionSpaces(info.Session.SpaceId,
                info.Session.Kind == SessionViewKind.GlobalSession || info.Session.ProjectRef is null ? null : projects.GetValueOrDefault(info.Session.ProjectRef), spaces)
                .Contains(space.Id, StringComparer.Ordinal);
        }

        includeArchived = includeArchived || string.Equals(stateFilter, "archived", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stateFilter, "all", StringComparison.OrdinalIgnoreCase);
        var filtered = infos
            .Where(info => project is null || string.Equals(info.Session.ProjectRef, project.Id, StringComparison.OrdinalIgnoreCase))
            .Where(inSpace)
            .Where(info => string.IsNullOrWhiteSpace(providerFilter) || string.Equals(info.Session.ProviderId, providerFilter, StringComparison.OrdinalIgnoreCase))
            .Where(info => includeArchived || !string.Equals(info.State, "archived", StringComparison.OrdinalIgnoreCase))
            .Where(info => string.IsNullOrWhiteSpace(stateFilter) || string.Equals(stateFilter, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(info.State, stateFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static info => info.Session.LastActiveAt)
            .ThenBy(static info => info.Session.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit + 1)
            .ToArray();

        var truncated = filtered.Length > limit;
        var emitted = truncated ? filtered.Take(limit).ToArray() : filtered;
        var metricsBySessionId = includeMetrics
            ? await BuildCompactMetricsForListAsync(context, emitted).ConfigureAwait(false)
            : null;
        foreach (var info in emitted)
        {
            SessionMetrics? metrics = null;
            metricsBySessionId?.TryGetValue(info.Session.SessionId, out metrics);
            WriteSession(context, "alta.session.item", info, includeChildren: false, infos, metrics);
        }

        WriteSummary(context, "alta.session.summary", emitted.Length, truncated);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionShowAsync(AltaCommandContext context, string? sessionId, string recordType)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session info");
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId,
            includeLocalState: recordType is "alta.session.info" or "alta.session.detail", includeChildren: true).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var info = infoResult.Info!;

        var metrics = recordType is "alta.session.info" or "alta.session.detail"
            ? await BuildCompactMetricsAsync(context, info).ConfigureAwait(false)
            : null;
        WriteSession(context, recordType, info, includeChildren: true, infoResult.Infos, metrics);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionMetricsAsync(AltaCommandContext context, string? sessionId, string? scope)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session metrics");
        }

        var normalizedScope = NormalizeMetricsScope(scope);
        if (normalizedScope is null)
        {
            return UsageError(context, "usage.invalidScope", "--scope must be last-turn or session.", "alta session metrics");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var info = infoResult.Info!;

        var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false);
        var metrics = BuildSessionMetrics(info, history ?? [], normalizedScope.Value);
        WriteSessionMetrics(context, "alta.session.metrics", info, metrics);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionResultAsync(AltaCommandContext context, string? sessionId, string? scope)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session result");
        }

        var normalizedScope = NormalizeMetricsScope(scope);
        if (normalizedScope is null)
        {
            return UsageError(context, "usage.invalidScope", "--scope must be last-turn or session.", "alta session result");
        }

        var result = await BuildSessionResultAsync(context, sessionId, normalizedScope.Value).ConfigureAwait(false);
        if (result.ExitCode != AltaExitCodes.Success)
        {
            return result.ExitCode;
        }

        WriteSessionResult(context, "alta.session.result", result.Info!, result.Result!, includeResult: true, includeMetrics: true);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionReportAsync(
        AltaCommandContext context,
        IReadOnlyCollection<string> sessionIds,
        bool useStdin,
        string? scope,
        string? include)
    {
        var normalizedScope = NormalizeMetricsScope(scope);
        if (normalizedScope is null)
        {
            return UsageError(context, "usage.invalidScope", "--scope must be last-turn or session.", "alta session report");
        }

        var includes = ParseSet(include);
        if (includes.Count == 0)
        {
            includes = ParseSet("result,metrics");
        }

        if (includes.Any(static item => item is not "result" and not "metrics"))
        {
            return UsageError(context, "usage.invalidInclude", "--include values must be result and/or metrics.", "alta session report");
        }

        var ids = new List<string>(sessionIds.Where(static id => !string.IsNullOrWhiteSpace(id)).Select(static id => id.Trim()));
        if (useStdin)
        {
            var stdin = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            foreach (var id in stdin.Split(['\r', '\n', '\t', ' ', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            return UsageError(context, "usage.missingSession", "At least one session id is required.", "alta session report");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var includeResult = includes.Contains("result");
        var includeMetrics = includes.Contains("metrics");
        var reportItems = new SessionReportItem?[ids.Count];
        var buildTasks = new List<Task<SessionReportItem>>(ids.Count);
        for (var index = 0; index < ids.Count; index++)
        {
            var id = ids[index];
            var info = FindSession(infos, id);
            if (info is null)
            {
                reportItems[index] = SessionReportItem.NotFound(index, id);
                continue;
            }

            buildTasks.Add(BuildSessionReportItemAsync(context, runtime, info, normalizedScope.Value, index, id));
        }

        foreach (var item in await Task.WhenAll(buildTasks).ConfigureAwait(false))
        {
            reportItems[item.Index] = item;
        }

        var successCount = 0;
        var diagnosticCount = 0;
        foreach (var item in reportItems)
        {
            if (item is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(item.Diagnostics))
            {
                context.Stderr.Write(item.Diagnostics);
            }

            if (item.Info is null || item.Result is null)
            {
                diagnosticCount++;
                WriteSessionReportDiagnostic(context, item.SessionId, "session.notFound", AltaExitCodes.NotFound, $"Session '{item.SessionId}' was not found.");
                continue;
            }

            successCount++;
            WriteSessionResult(context, "alta.session.report.item", item.Info, item.Result, includeResult, includeMetrics);
        }

        WriteSessionReportSummary(context, ids.Count, successCount, diagnosticCount, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async Task<SessionReportItem> BuildSessionReportItemAsync(
        AltaCommandContext context,
        SessionRuntimeService runtime,
        AltaSessionInfo info,
        SessionMetricsScope scope,
        int index,
        string sessionId)
    {
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        var isolatedContext = context with
        {
            Stdout = TextWriter.Null,
            Stderr = diagnostics,
        };
        var result = await BuildSessionResultAsync(isolatedContext, runtime, info, scope).ConfigureAwait(false);
        return SessionReportItem.Success(index, sessionId, info, result, diagnostics.ToString());
    }

    private static async ValueTask<int> HandleSessionChildrenAsync(AltaCommandContext context, string? sessionId, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session children");
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var parent = FindSession(infos, sessionId);
        if (parent is null)
        {
            return NotFound(context, "session.notFound", $"Session '{sessionId}' was not found.");
        }

        var children = GetChildren(infos, parent, recursive)
            .ToArray();
        foreach (var child in children)
        {
            WriteSession(context, "alta.session.item", child, includeChildren: false, infos);
        }

        WriteSummary(context, "alta.session.children.summary", children.Length, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionModelAsync(AltaCommandContext context, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session model");
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var info = infoResult.Info!;
        WriteModelSelection(context, "alta.model.selection", CreateModelSelection(info.Session, info.Preference), info.Session.SessionId, info.Session.AgentPromptId);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionEventsAsync(
        AltaCommandContext context,
        string? sessionId,
        long? since,
        int limit,
        SessionEventsOptions options,
        bool fromTail)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", fromTail ? "alta session tail" : "alta session events");
        }

        if (limit <= 0)
        {
            return UsageError(context, "usage.invalidLimit", fromTail ? "--last must be positive." : "--limit must be positive.", fromTail ? "alta session tail" : "alta session events");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var info = infoResult.Info!;
        var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false);

        if (history is null)
        {
            WriteSummary(context, fromTail ? "alta.session.tail.summary" : "alta.session.events.summary", 0, truncated: false);
            return AltaExitCodes.Success;
        }

        var includes = ParseSet(options.Include);
        var kinds = ParseSet(options.Kind);
        var fields = ParseSet(options.Fields);
        var records = history
            .Select((agentEvent, index) => (Event: agentEvent, Sequence: (long)index + 1))
            .Where(item => since is null || item.Sequence > since.Value)
            .Where(item => IncludesEvent(item.Event, includes, kinds, options.NoToolOutput))
            .ToArray();
        var selected = fromTail
            ? records.TakeLast(limit + 1).ToArray()
            : records.Take(limit + 1).ToArray();
        var truncated = selected.Length > limit;
        var emitted = truncated
            ? (fromTail ? selected.Skip(1).ToArray() : selected.Take(limit).ToArray())
            : selected;

        foreach (var item in emitted)
        {
            WriteAgentEvent(context, info.Session, item.Event, item.Sequence, fields);
        }

        WriteSummary(context, fromTail ? "alta.session.tail.summary" : "alta.session.events.summary", emitted.Length, truncated);
        return AltaExitCodes.Success;
    }

    private static async Task<IReadOnlyDictionary<string, SessionMetrics>> BuildCompactMetricsForListAsync(
        AltaCommandContext context,
        IReadOnlyList<AltaSessionInfo> infos)
    {
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return new Dictionary<string, SessionMetrics>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, SessionMetrics>(StringComparer.Ordinal);
        foreach (var info in infos)
        {
            var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false);
            result[info.Session.SessionId] = BuildSessionMetrics(info, history ?? [], SessionMetricsScope.LastTurn);
        }

        return result;
    }

    private static async Task<SessionMetrics?> BuildCompactMetricsAsync(AltaCommandContext context, AltaSessionInfo info)
    {
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return null;
        }

        var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false);
        return BuildSessionMetrics(info, history ?? [], SessionMetricsScope.LastTurn);
    }

    private static async Task<SessionResultBuildResult> BuildSessionResultAsync(AltaCommandContext context, string sessionId, SessionMetricsScope scope)
    {
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return SessionResultBuildResult.Fail(AltaExitCodes.ServiceUnavailable);
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return SessionResultBuildResult.Fail(infoResult.ExitCode);
        }

        var info = infoResult.Info!;

        var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false) ?? [];
        var result = BuildSessionResult(info, history, scope);
        return SessionResultBuildResult.Success(info, result);
    }

    private static async Task<SessionResult> BuildSessionResultAsync(
        AltaCommandContext context,
        SessionRuntimeService runtime,
        AltaSessionInfo info,
        SessionMetricsScope scope)
    {
        var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false) ?? [];
        return BuildSessionResult(info, history, scope);
    }

    private static SessionResult BuildSessionResult(AltaSessionInfo info, IReadOnlyList<AgentEvent> history, SessionMetricsScope scope)
    {
        var scoped = SelectMetricScope(history, scope);
        var metrics = BuildSessionMetrics(info, history, scope);
        var finalAssistant = scoped.OfType<AgentContentCompletedEvent>().LastOrDefault(static content => content.Kind == AgentContentKind.Assistant);
        var finalError = scoped.OfType<AgentErrorEvent>().LastOrDefault();
        var status = DetermineResultStatus(info, finalAssistant, finalError);
        var finalAnswer = status == SessionResultStatus.Completed && finalAssistant is not null
            ? finalAssistant.Content
            : null;
        var result = new SessionResult(
            scope,
            status,
            finalAnswer,
            finalAssistant?.Timestamp,
            status is SessionResultStatus.Failed or SessionResultStatus.Cancelled ? CreateFinalError(finalError!) : null,
            metrics);
        return result;
    }

    private static async Task<IReadOnlyList<AgentEvent>?> ReadSessionHistoryAsync(
        AltaCommandContext context,
        SessionRuntimeService runtime,
        AltaSessionInfo info)
    {
        var storedHistoryUnavailable = false;
        var history = await runtime.TryReadStoredHistoryAsync(
                info.Session,
                _ => storedHistoryUnavailable = true,
                context.CancellationToken)
            .ConfigureAwait(false);
        if (storedHistoryUnavailable)
        {
            AltaJsonlWriter.WriteWarning(
                context.Stderr,
                context.CorrelationId,
                "session.historyStoreUnavailable",
                "Stored session history could not be read; provider history fallback will be used when available.");
        }

        if (history is null || history.Count == 0)
        {
            try
            {
                var activeHistory = await runtime.GetHistoryAsync(info.Session.SessionId, context.CancellationToken).ConfigureAwait(false);
                if (activeHistory.Count > 0 || history is null)
                {
                    history = activeHistory;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException)
            {
                AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "session.historyUnavailable", ex.Message);
            }
        }

        return history;
    }

    private static SessionMetricsScope? NormalizeMetricsScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.Equals(scope, "last-turn", StringComparison.OrdinalIgnoreCase) || string.Equals(scope, "turn", StringComparison.OrdinalIgnoreCase))
        {
            return SessionMetricsScope.LastTurn;
        }

        return string.Equals(scope, "session", StringComparison.OrdinalIgnoreCase) || string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase)
            ? SessionMetricsScope.Session
            : null;
    }

    private static SessionMetrics BuildSessionMetrics(AltaSessionInfo info, IReadOnlyList<AgentEvent> history, SessionMetricsScope scope)
    {
        var scoped = SelectMetricScope(history, scope);
        var firstEvent = scoped.FirstOrDefault();
        var lastEvent = scoped.LastOrDefault();
        var firstUser = scoped.OfType<AgentContentCompletedEvent>().FirstOrDefault(static content => content.Kind == AgentContentKind.User);
        var finalAssistant = scoped.OfType<AgentContentCompletedEvent>().LastOrDefault(static content => content.Kind == AgentContentKind.Assistant);
        var startedAt = firstUser?.Timestamp ?? firstEvent?.Timestamp;
        var completedAt = finalAssistant?.Timestamp ?? lastEvent?.Timestamp;
        var finalError = scoped.OfType<AgentErrorEvent>().LastOrDefault();
        var status = DetermineResultStatus(info, finalAssistant, finalError);
        var duration = startedAt is not null && completedAt is not null && completedAt >= startedAt
            ? completedAt.Value - startedAt.Value
            : (TimeSpan?)null;
        var toolCallCount = scoped.OfType<AgentActivityEvent>()
            .Count(static activity => IsToolLikeActivity(activity.Kind) && activity.Phase == AgentActivityPhase.Requested);
        var usageUpdates = scoped.OfType<AgentSessionUpdateEvent>()
            .Where(static update => update.Usage is not null)
            .ToArray();
        var finalUsage = usageUpdates.LastOrDefault()?.Usage;
        var providerTotals = BuildProviderOperationTotals(usageUpdates);
        var finalAnswer = finalAssistant?.Content ?? string.Empty;

        return new SessionMetrics(
            scope,
            scoped.Count,
            scoped.Select(static agentEvent => agentEvent.RunId?.Value).Where(static runId => !string.IsNullOrWhiteSpace(runId)).Distinct(StringComparer.Ordinal).Count(),
            startedAt,
            completedAt,
            duration,
            finalAssistant?.Timestamp,
            CountCompletedContent(scoped, AgentContentKind.User),
            CountCompletedContent(scoped, AgentContentKind.Assistant),
            toolCallCount,
            finalAnswer.Length,
            CountWords(finalAnswer),
            TokenEstimator.Estimate(finalAnswer),
            finalUsage,
            providerTotals,
            StatusWire(status),
            status is SessionResultStatus.Failed or SessionResultStatus.Cancelled ? CreateFinalError(finalError!) : null,
            ToModelSelectionPayload(CreateModelSelection(info.Session, info.Preference), info.Session.AgentPromptId));
    }

    private static SessionResultStatus DetermineResultStatus(AltaSessionInfo info, AgentContentCompletedEvent? finalAssistant, AgentErrorEvent? finalError)
    {
        if (info.IsRunning || string.Equals(info.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            return SessionResultStatus.Running;
        }

        if (finalError is not null && (finalAssistant is null || finalError.Timestamp >= finalAssistant.Timestamp))
        {
            return IsCancellationError(finalError) ? SessionResultStatus.Cancelled : SessionResultStatus.Failed;
        }

        return finalAssistant is not null ? SessionResultStatus.Completed : SessionResultStatus.Unknown;
    }

    private static bool IsCancellationError(AgentErrorEvent error)
        => error.Exception is OperationCanceledException ||
           error.Exception is TaskCanceledException ||
           string.Equals(error.ExceptionInfo?.Type, typeof(OperationCanceledException).FullName, StringComparison.Ordinal) ||
           string.Equals(error.ExceptionInfo?.Type, typeof(TaskCanceledException).FullName, StringComparison.Ordinal) ||
           error.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase);

    private static FinalErrorPayload CreateFinalError(AgentErrorEvent error)
        => new(
            IsCancellationError(error) ? "cancelled" : "failed",
            error.Message,
            error.Timestamp,
            error.RunId?.Value,
            error.ExceptionInfo?.Type,
            error.Failure?.Kind.ToString(),
            CreateErrorDetails(error),
            CreateErrorCauses(error));

    // What the provider said about the failure beyond its message (code, HTTP status, request id, payload) and the
    // exceptions it wrapped, so that a session can be diagnosed from its report.
    private static IReadOnlyDictionary<string, string>? CreateErrorDetails(AgentErrorEvent error)
    {
        if (error.ExceptionInfo is not { } info)
        {
            return null;
        }

        var details = info.CollectData();
        return details.Count == 0 ? null : details;
    }

    // The exceptions the error wraps, outermost first: what CodeAlta said, then what the provider said.
    private static IReadOnlyList<string>? CreateErrorCauses(AgentErrorEvent error)
    {
        var causes = new List<string>();
        for (var inner = error.ExceptionInfo?.InnerException; inner is not null && causes.Count < 4; inner = inner.InnerException)
        {
            causes.Add($"{inner.Type}: {inner.Message}");
        }

        return causes.Count == 0 ? null : causes;
    }

    private static string StatusWire(SessionResultStatus status)
        => status switch
        {
            SessionResultStatus.Completed => "completed",
            SessionResultStatus.Failed => "failed",
            SessionResultStatus.Cancelled => "cancelled",
            SessionResultStatus.Running => "running",
            _ => "unknown",
        };

    private static IReadOnlyList<AgentEvent> SelectMetricScope(IReadOnlyList<AgentEvent> history, SessionMetricsScope scope)
    {
        if (scope == SessionMetricsScope.Session || history.Count == 0)
        {
            return history;
        }

        var lastRunId = history.OfType<AgentContentCompletedEvent>()
            .Where(static content => content.Kind == AgentContentKind.Assistant && content.RunId is not null)
            .Select(static content => content.RunId!.Value.Value)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(lastRunId))
        {
            lastRunId = history.Where(static agentEvent => agentEvent.RunId is not null)
                .Select(static agentEvent => agentEvent.RunId!.Value.Value)
                .LastOrDefault();
        }

        return string.IsNullOrWhiteSpace(lastRunId)
            ? history
            : history.Where(agentEvent => string.Equals(agentEvent.RunId?.Value, lastRunId, StringComparison.Ordinal)).ToArray();
    }

    private static int CountCompletedContent(IReadOnlyList<AgentEvent> events, AgentContentKind kind)
        => events.OfType<AgentContentCompletedEvent>().Count(content => content.Kind == kind);

    private static ProviderOperationTotals BuildProviderOperationTotals(IReadOnlyList<AgentSessionUpdateEvent> usageUpdates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long inputTokens = 0;
        long outputTokens = 0;
        long cachedInputTokens = 0;
        long reasoningTokens = 0;
        long cacheWriteTokens = 0;
        double? cost = null;
        string? costUnit = null;
        var operationCount = 0;

        foreach (var usage in usageUpdates)
        {
            if (usage.Usage?.LastOperation is not { } operation)
            {
                continue;
            }

            var signature = string.Join('\u001f', usage.RunId?.Value, operation.Model, operation.InputTokens, operation.OutputTokens, operation.CachedInputTokens, operation.CacheReadTokens, operation.CacheWriteTokens, operation.ReasoningTokens, operation.ParentToolCallId, operation.Initiator);
            if (!seen.Add(signature))
            {
                continue;
            }

            operationCount++;
            // The input of an operation holds what the cache read and wrote: the cached and written counts are parts
            // of the input, not counts to add to it.
            var input = AgentInputTokenUsage.From(operation);
            inputTokens += input?.Total ?? 0;
            outputTokens += operation.OutputTokens ?? 0;
            cachedInputTokens += input?.CacheRead ?? 0;
            reasoningTokens += operation.ReasoningTokens ?? 0;
            cacheWriteTokens += input?.CacheWrite ?? 0;
            // Costs add up in one unit: the unit of the first operation that reports one.
            if (operation.Cost is { } operationCost && (cost is null || string.Equals(costUnit, operation.CostUnit, StringComparison.Ordinal)))
            {
                cost = (cost ?? 0) + operationCost;
                costUnit = operation.CostUnit;
            }
        }

        return new ProviderOperationTotals(operationCount, inputTokens, outputTokens, cachedInputTokens, reasoningTokens, cacheWriteTokens, cost, costUnit);
    }

    private static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
                continue;
            }

            if (!inWord)
            {
                count++;
                inWord = true;
            }
        }

        return count;
    }

    private static bool IsToolLikeActivity(AgentActivityKind kind)
        => kind is AgentActivityKind.ToolCall or AgentActivityKind.CommandExecution or AgentActivityKind.FileChange or AgentActivityKind.McpToolCall or AgentActivityKind.DynamicToolCall or AgentActivityKind.CollabAgentToolCall;

    private static async ValueTask<int> HandleSessionCreateAsync(AltaCommandContext context, SessionCreateOptions options)
    {
        if (options.Global && !string.IsNullOrWhiteSpace(options.Project))
        {
            return UsageError(context, "usage.projectAndGlobal", "Use either --project or --global, not both.", "alta session create");
        }

        if (!options.Global && string.IsNullOrWhiteSpace(options.Project))
        {
            return UsageError(context, "usage.missingScope", "Session create requires --project <ref> or --global.", "alta session create");
        }

        if (!string.IsNullOrWhiteSpace(options.ParentSessionId) && options.NoParent)
        {
            return UsageError(context, "usage.parentConflict", "Use either --parent or --no-parent, not both.", "alta session create");
        }

        if (options.Worktree && options.NoWorktree)
        {
            return UsageError(context, "usage.worktreeConflict", "Use either --worktree or --no-worktree, not both.", "alta session create");
        }

        if (options.Worktree && options.Global)
        {
            return UsageError(context, "usage.worktreeWithoutProject", "A worktree is a checkout of a project: use --project with --worktree.", "alta session create");
        }

        var worktreeBase = NormalizeOptionalText(options.WorktreeBase);
        if (worktreeBase is not null && (!options.Worktree || !GitWorktreeService.IsReferenceName(worktreeBase)))
        {
            return UsageError(context, "usage.invalidBase", "--base names the branch or commit a new worktree starts from: use it with --worktree.", "alta session create");
        }

        // Project/model inheritance may discover an existing parent only during preparation.
        context.ClaimSessionWork(null);
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime) ||
            !context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        ProjectDescriptor? project = null;
        if (!options.Global)
        {
            project = await ResolveProjectAsync(catalog, options.Project, context, includeArchived: false).ConfigureAwait(false);
            if (project is null)
            {
                return NotFound(context, "project.notFound", $"Project '{options.Project}' was not found.");
            }

        }

        var space = await ResolveNewSessionSpaceAsync(context, options.Space, "alta session create").ConfigureAwait(false);
        if (space.ExitCode != AltaExitCodes.Success)
        {
            return space.ExitCode;
        }

        var parentResolution = await ResolveParentSessionIdAsync(context, project, options).ConfigureAwait(false);
        if (parentResolution.ExitCode != AltaExitCodes.Success)
        {
            return parentResolution.ExitCode;
        }

        var modelSelection = await ResolveModelSelectionAsync(context, options.Model, "alta session create", project?.ProjectPath, sessionPreparation: true, space.SpaceId, project?.Id).ConfigureAwait(false);
        if (modelSelection.ExitCode != AltaExitCodes.Success)
        {
            return modelSelection.ExitCode;
        }

        if (parentResolution.ParentSessionId is { } parentId) context.ClaimSessionWork(parentId);
        // The invocation retains preparation and parent claims through original-fault
        // classification, including unconfirmed dependencies during creation.

        // The prompt asked for, else the one the space of the new session starts its sessions with.
        var newSessionSpace = await ResolveNewSessionSpaceDescriptorAsync(context, space.SpaceId, project).ConfigureAwait(false);
        var promptChoice = ResolveNewSessionPrompt(context, project?.ProjectPath, options.Model.PromptId, newSessionSpace);
        if (promptChoice.ExitCode != AltaExitCodes.Success)
        {
            return promptChoice.ExitCode;
        }

        var promptId = promptChoice.PromptId!;

        // Nor does it have a tool that the session that creates it does without.
        var newSession = new SessionViewDescriptor { ProjectRef = project?.Id, SpaceId = space.SpaceId, AgentPromptId = promptId };
        if (await RefuseWiderToolsAsync(context, runtime => runtime.GetDisabledToolsAsync(newSession, promptId, context.CancellationToken), "the session it creates",
                "Create it with an agent prompt that does without them too (`--prompt-id`), or do the work here.").ConfigureAwait(false) is { } wider)
        {
            return wider;
        }

        // The provider must be one of the space of the new session as well.
        modelSelection = await KeepProviderInNewSessionSpaceAsync(context, options.Model, modelSelection, newSessionSpace, project?.ProjectPath).ConfigureAwait(false);
        if (modelSelection.ExitCode != AltaExitCodes.Success)
        {
            return modelSelection.ExitCode;
        }

        // The mode named is one the provider offers a session, checked before anything is created.
        var permissionMode = NormalizeOptionalText(options.PermissionMode);
        if (permissionMode is not null)
        {
            var offered = runtime.GetSessionPermissionModes(modelSelection.Selection!.ProviderKey);
            if (!offered.Contains(permissionMode, StringComparer.Ordinal))
            {
                return UsageError(context, "usage.invalidPermissionMode", offered.Count == 0
                    ? $"The provider '{modelSelection.Selection.ProviderKey}' offers no permission mode: leave --permission-mode out."
                    : $"--permission-mode is one of the modes of the provider '{modelSelection.Selection.ProviderKey}': {string.Join(", ", offered)}.", "alta session create");
            }

            if (!runtime.MayCreateInPermissionMode(context.Caller.SourceSessionId, permissionMode))
            {
                return PermissionDenied(context, "session.permissionModeDenied",
                    $"The user reviews what this session does: it cannot create a session in '{permissionMode}', which asks less. Leave --permission-mode out, or name a mode that asks at least as much.");
            }
        }

        var workingDirectory = project?.ProjectPath ?? GetGlobalRootOrCwd(context);
        // Where a project session works: a worktree of its own, the worktree of the session that creates it, or
        // the folder of the project.
        string? worktreeDirectory = null;
        GitWorktreeCreation? worktree = null;
        if (options.Worktree)
        {
            var creation = await CreateSessionWorktreeAsync(context, project!, worktreeBase).ConfigureAwait(false);
            if (creation.Worktree is null)
            {
                return creation.ExitCode;
            }

            worktree = creation.Worktree;
            worktreeDirectory = worktree.Folder;
        }
        else if (!options.NoWorktree && project is not null
            && string.Equals(NormalizeOptionalText(context.Caller.SourceProjectId), project.Id, StringComparison.OrdinalIgnoreCase))
        {
            worktreeDirectory = await GetSessionWorktreeAsync(context, context.Caller.SourceSessionId).ConfigureAwait(false);
        }

        string? createdSessionId = null;
        var createdBy = CreateProvenance(context);
        var executionOptions = BuildExecutionOptions(
            context,
            modelSelection.Selection!,
            workingDirectory,
            project is null ? [] : [project.ProjectPath],
            () => createdSessionId,
            project?.Id,
            promptId,
            worktreeDirectory,
            // The mode named, else what the host gives a session that another session creates: by default it does not ask.
            permissionMode ?? runtime.GetCreatedSessionPermissionMode(context.Caller.SourceSessionId, modelSelection.Selection!.ProviderKey));

        SessionViewDescriptor session;
        try
        {
            session = project is null
                ? await runtime.CreateGlobalSessionAsync(executionOptions, options.Title, parentResolution.ParentSessionId, createdBy, space.SpaceId, context.CancellationToken).ConfigureAwait(false)
                : await runtime.CreateProjectSessionAsync(project, executionOptions, options.Title, parentResolution.ParentSessionId, createdBy, worktreeDirectory, space.SpaceId, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (worktree is not null)
        {
            await RemoveUnusedWorktreeAsync(context, project!, worktree).ConfigureAwait(false);
            throw;
        }

        createdSessionId = session.SessionId;
        session.ParentSessionId = parentResolution.ParentSessionId;
        session.CreatedBy = createdBy;
        session.AgentPromptId = promptId;
        await runtime.PersistSessionLocalStateAsync(session, context.CancellationToken).ConfigureAwait(false);
        await PersistSessionPreferenceAsync(context, session, modelSelection.Selection!, promptId).ConfigureAwait(false);
        // A session that runs on a tier falls back along it.
        if (modelSelection.Tier is { } tier)
        {
            await runtime.SetSessionTierAsync(session.SessionId, tier, context.CancellationToken).ConfigureAwait(false);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.created",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.SessionId,
            ProviderId = session.ProviderId,
            providerKey = session.ResolvedProviderKey,
            projectId = session.ProjectRef,
            spaceId = session.SpaceId,
            title = session.Title,
            parentSessionId = session.ParentSessionId,
            createdBy = session.CreatedBy,
            workingDirectory = session.WorkingDirectory,
            worktreeDirectory = session.WorktreeDirectory,
            worktreeBranch = worktree?.Branch,
            worktreeWarning = worktree?.Warning,
            modelSelection = ToModelSelectionPayload(modelSelection.Selection!, promptId),
            tier = modelSelection.Tier is { } chosenTier ? ModelTiers.NameOf(chosenTier) : null,
            permissionMode = session.PermissionMode,
        });
        return AltaExitCodes.Success;
    }

    // A new worktree of a project, for a session that is about to be created; without one, the error is written.
    private static async Task<(int ExitCode, GitWorktreeCreation? Worktree)> CreateSessionWorktreeAsync(
        AltaCommandContext context, ProjectDescriptor project, string? worktreeBase)
    {
        if (GetWorktrees(context) is not { } worktrees)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
                "Required in-process service 'GitWorktreeService' is unavailable.");
            return (AltaExitCodes.ServiceUnavailable, null);
        }

        // The files of the project are the session's to write: a session without the shell has git run none of its hooks.
        var withoutHooks = await ShellTurnedOffAsync(context).ConfigureAwait(false);
        var worktree = await worktrees.CreateAsync(project, worktreeBase, progress: null, withoutHooks, context.CancellationToken).ConfigureAwait(false);
        if (worktree.Succeeded)
        {
            return (AltaExitCodes.Success, worktree);
        }

        var reason = worktree.Status switch
        {
            "not_repository" => $"The folder of project '{project.Id}' is not in a git repository.",
            "no_commit" => "The repository has no commit yet: a worktree starts from one.",
            "invalid" => $"'{worktreeBase}' is not a branch or a commit of the repository.",
            "git_unavailable" => "git is not installed.",
            "timeout" => "git did not answer in time.",
            _ => "git could not create the worktree.",
        };
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "worktree." + worktree.Status, AltaExitCodes.Failure,
            worktree.Message is null ? reason : reason + " " + worktree.Message);
        return (AltaExitCodes.Failure, null);
    }

    // Nothing was written there yet: the checkout that no session will use goes, and the failure stays what it is.
    private static async Task RemoveUnusedWorktreeAsync(AltaCommandContext context, ProjectDescriptor project, GitWorktreeCreation worktree)
    {
        try { await GetWorktrees(context)!.RemoveAsync(project.ProjectPath, worktree.Root!, force: false, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static async ValueTask<int> HandleSessionTurnsAsync(AltaCommandContext context, string? sessionId, int? last)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required outside a session caller.", "alta session turns");
        }

        if (last is <= 0)
        {
            return UsageError(context, "usage.invalidLast", "--last is a number of turns, from 1.", "alta session turns");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var read = await ReadSessionTurnsAsync(context, runtime, sessionId).ConfigureAwait(false);
        if (read.Snapshot is not { } snapshot)
        {
            return read.ExitCode;
        }

        var turns = snapshot.Turns.Turns;
        var first = last is { } count && count < turns.Count ? turns.Count - count : 0;
        for (var index = first; index < turns.Count; index++)
        {
            var turn = turns[index];
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.session.turn",
                version = 1,
                correlationId = context.CorrelationId,
                sessionId = read.Session!.SessionId,
                turn = turn.Number,
                runId = turn.RunId,
                status = string.Equals(turn.RunId, snapshot.ActiveRunId, StringComparison.Ordinal) ? "running" : turn.Status.ToString().ToLowerInvariant(),
                startedAt = turn.StartedAt,
                endedAt = turn.EndedAt,
                prompt = TurnPrompt(turn),
                promptTruncated = IsTurnPromptTruncated(turn),
                sourceSessionId = turn.SourceSessionId,
                startedByProvider = turn.StartedByProvider ? true : (bool?)null,
                steers = turn.SteerCount,
                assistantMessages = turn.AssistantMessageCount,
                toolCalls = turn.ToolCallCount,
                compactions = turn.CompactionCount,
            });
        }

        WriteSummary(context, "alta.session.turns.summary", turns.Count - first, truncated: first > 0);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionForkAsync(AltaCommandContext context, SessionForkOptions options)
    {
        const string CommandPath = "alta session fork";
        var sessionId = FirstNonEmpty(options.SessionId, context.Caller.SourceSessionId);
        if (sessionId is null)
        {
            return UsageError(context, "usage.missingSession", "Session id is required outside a session caller.", CommandPath);
        }

        var before = NormalizeOptionalText(options.Before);
        var after = NormalizeOptionalText(options.After);
        if (before is not null && after is not null)
        {
            return UsageError(context, "usage.turnConflict", "Use either --before or --after, not both.", CommandPath);
        }

        if (options.Worktree && options.NoWorktree)
        {
            return UsageError(context, "usage.worktreeConflict", "Use either --worktree or --no-worktree, not both.", CommandPath);
        }

        var worktreeBase = NormalizeOptionalText(options.WorktreeBase);
        if (worktreeBase is not null && (!options.Worktree || !GitWorktreeService.IsReferenceName(worktreeBase)))
        {
            return UsageError(context, "usage.invalidBase", "--base names the branch or commit a new worktree starts from: use it with --worktree.", CommandPath);
        }

        context.ClaimSessionWork(sessionId);
        var parentSessionId = NormalizeOptionalText(options.ParentSessionId);
        if (parentSessionId is not null) context.ClaimSessionWork(parentSessionId);
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        // A fork keeps the space of its source: an agent forks only the sessions of its own space.
        if (await RefuseSessionOfOtherSpaceAsync(context, sessionId.Trim(), "forks").ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        // And its agent prompt, with the tools that come with it.
        if (await RefuseWiderToolsAsync(context, runtime => runtime.GetDisabledToolsAsync(sessionId.Trim(), context.CancellationToken), "the fork").ConfigureAwait(false) is { } wider)
        {
            return wider;
        }

        var read = await ReadSessionTurnsAsync(context, runtime, sessionId).ConfigureAwait(false);
        if (read.Snapshot is not { } snapshot)
        {
            return read.ExitCode;
        }

        var source = read.Session!;
        var point = ResolveTurnPoint(context, snapshot.Turns.Turns, source.SessionId, before, after);
        if (point.ExitCode != AltaExitCodes.Success)
        {
            return point.ExitCode;
        }

        // A turn that was asked for and that runs is refused, not left out in silence as it is from a fork of every turn.
        if ((before ?? after) is { } asked && snapshot.ActiveRunId is { } active
            && snapshot.Turns.Turns.Take(point.KeptTurnCount).Any(turn => string.Equals(turn.RunId, active, StringComparison.Ordinal)))
        {
            return BranchRefused(context, "turn_running", source.SessionId, "forked", asked);
        }

        if (parentSessionId is not null)
        {
            // A parent is a session of the host, attached or stored.
            var parent = await runtime.TryGetActiveSessionDescriptorAsync(parentSessionId, context.CancellationToken).ConfigureAwait(false)
                ?? (await LoadSessionInfosAsync(context).ConfigureAwait(false) is { } infos ? FindSession(infos, parentSessionId)?.Session : null);
            if (parent is null)
            {
                return NotFound(context, "session.parentNotFound", $"Parent session '{parentSessionId}' was not found.");
            }

            parentSessionId = parent.SessionId;
        }

        ProjectDescriptor? project = null;
        GitWorktreeCreation? worktree = null;
        if (options.Worktree)
        {
            if (string.IsNullOrWhiteSpace(source.ProjectRef))
            {
                return UsageError(context, "usage.worktreeWithoutProject", "A worktree is a checkout of a project: this session has none.", CommandPath);
            }

            if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
            {
                return AltaExitCodes.ServiceUnavailable;
            }

            project = await ResolveProjectAsync(catalog, source.ProjectRef, context, includeArchived: false).ConfigureAwait(false);
            if (project is null)
            {
                return NotFound(context, "project.notFound", $"Project '{source.ProjectRef}' was not found.");
            }

            var creation = await CreateSessionWorktreeAsync(context, project, worktreeBase).ConfigureAwait(false);
            if (creation.Worktree is null)
            {
                return creation.ExitCode;
            }

            worktree = creation.Worktree;
        }

        var forkedBy = CreateProvenance(context);
        SessionBranchOutcome outcome;
        try
        {
            outcome = await runtime.ForkSessionAsync(new SessionForkRequest
            {
                SessionId = source.SessionId,
                BeforeRunId = point.BeforeRunId,
                Title = NormalizeOptionalText(options.Title),
                ParentSessionId = parentSessionId,
                CreatedBy = forkedBy,
                WorktreeDirectory = worktree?.Folder,
                LeaveWorktree = options.NoWorktree,
            }, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (worktree is not null)
        {
            await RemoveUnusedWorktreeAsync(context, project!, worktree).ConfigureAwait(false);
            throw;
        }

        if (outcome.Session is not { } fork)
        {
            if (worktree is not null) await RemoveUnusedWorktreeAsync(context, project!, worktree).ConfigureAwait(false);
            return BranchRefused(context, outcome.Status, source.SessionId, "forked", before ?? after);
        }

        // Without a point, the only turn a fork leaves out is the one that runs.
        var next = outcome.NextTurn;
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.forked",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = fork.SessionId,
            providerKey = fork.ResolvedProviderKey,
            projectId = fork.ProjectRef,
            title = fork.Title,
            parentSessionId = fork.ParentSessionId,
            workingDirectory = fork.WorkingDirectory,
            worktreeDirectory = fork.WorktreeDirectory,
            worktreeBranch = worktree?.Branch,
            worktreeWarning = worktree?.Warning,
            forkedFromSessionId = fork.ForkedFrom?.SessionId ?? source.SessionId,
            forkRootSessionId = fork.ForkedFrom?.RootSessionId ?? source.SessionId,
            turns = outcome.KeptTurnCount,
            sourceTurns = outcome.Turns.Count,
            beforeRunId = next?.RunId,
            excludedRunningTurn = next is not null && (point.BeforeRunId is null || string.Equals(next.RunId, snapshot.ActiveRunId, StringComparison.Ordinal)),
            forkedBy,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionRewindAsync(AltaCommandContext context, string? sessionId, string? before, string? after, bool discard)
    {
        const string CommandPath = "alta session rewind";
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", CommandPath);
        }

        before = NormalizeOptionalText(before);
        after = NormalizeOptionalText(after);
        if (before is not null && after is not null)
        {
            return UsageError(context, "usage.turnConflict", "Use either --before or --after, not both.", CommandPath);
        }

        if (before is null && after is null)
        {
            return UsageError(context, "usage.missingTurn", "--before <turn> or --after <turn> is required: list the turns with `alta session turns`.", CommandPath);
        }

        sessionId = sessionId.Trim();
        context.ClaimSessionWork(sessionId);
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        // The session that calls runs: it is refused as any session that runs, whatever the runtime knows of the caller.
        if (IsTargetingCallingAgentSession(context, sessionId))
        {
            return BranchRefused(context, "session_in_use", sessionId, "rewound");
        }

        var read = await ReadSessionTurnsAsync(context, runtime, sessionId).ConfigureAwait(false);
        if (read.Snapshot is not { } snapshot)
        {
            return read.ExitCode;
        }

        var session = read.Session!;
        var point = ResolveTurnPoint(context, snapshot.Turns.Turns, session.SessionId, before, after);
        if (point.ExitCode != AltaExitCodes.Success)
        {
            return point.ExitCode;
        }

        if (point.BeforeRunId is null)
        {
            return UsageError(context, "usage.nothingToRewind", $"Turn '{after ?? before}' is the last turn of session '{session.SessionId}': there is no turn after it to remove.", CommandPath);
        }

        var rewoundBy = CreateProvenance(context);
        var outcome = await runtime.RewindSessionAsync(new SessionRewindRequest
        {
            SessionId = session.SessionId,
            BeforeRunId = point.BeforeRunId,
            KeepRemovedTurns = !discard,
            CreatedBy = rewoundBy,
        }, context.CancellationToken).ConfigureAwait(false);
        if (outcome.Status != "ok")
        {
            return BranchRefused(context, outcome.Status, session.SessionId, "rewound", before ?? after);
        }

        var removed = outcome.NextTurn;
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.rewound",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.SessionId,
            turns = outcome.KeptTurnCount,
            removedTurns = outcome.Turns.Count - outcome.KeptTurnCount,
            beforeRunId = removed?.RunId ?? point.BeforeRunId,
            removedPrompt = removed is null ? null : TurnPrompt(removed),
            removedPromptTruncated = removed is not null && IsTurnPromptTruncated(removed),
            removedTurnsSessionId = outcome.RemovedTurns?.SessionId,
            rewindCount = outcome.Session?.RewindCount ?? session.RewindCount + 1,
            rewoundBy,
        });
        return AltaExitCodes.Success;
    }

    // The turns of a session that exists. Without them the error is written: the session is not found, or its
    // journal is not one that has turns.
    private static async Task<(int ExitCode, SessionViewDescriptor? Session, SessionTurnsSnapshot? Snapshot)> ReadSessionTurnsAsync(
        AltaCommandContext context, SessionRuntimeService runtime, string sessionId)
    {
        var info = await ResolveSessionInfoAsync(context, sessionId.Trim()).ConfigureAwait(false);
        if (info.ExitCode != AltaExitCodes.Success)
        {
            return (info.ExitCode, null, null);
        }

        var session = info.Info!.Session;
        var snapshot = await runtime.ReadSessionTurnsAsync(session.SessionId, context.CancellationToken).ConfigureAwait(false);
        return snapshot is null
            ? (BranchRefused(context, "unsupported_format", session.SessionId, "read"), session, null)
            : (AltaExitCodes.Success, session, snapshot);
    }

    // The point `--before <turn>` or `--after <turn>` names, as the run of the first turn after it and the number of
    // turns before it: no run for the point after the last turn, which is also the point when neither is given.
    // A turn is named by its number or by its run id; `--after N` is `--before N+1`.
    private static (int ExitCode, string? BeforeRunId, int KeptTurnCount) ResolveTurnPoint(
        AltaCommandContext context, IReadOnlyList<AgentSessionTurn> turns, string sessionId, string? before, string? after)
    {
        if ((before ?? after) is not { } reference)
        {
            return (AltaExitCodes.Success, null, turns.Count);
        }

        var index = -1;
        if (int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            // The number after the last turn names the point after it, as `--after <last turn>` does.
            index = number >= 1 && number <= turns.Count + (before is not null ? 1 : 0) ? number - 1 : -1;
        }

        for (var candidate = 0; candidate < turns.Count && index < 0; candidate++)
        {
            if (string.Equals(turns[candidate].RunId, reference, StringComparison.Ordinal))
            {
                index = candidate;
            }
        }

        if (index < 0)
        {
            return (BranchRefused(context, "turn_not_found", sessionId, "read", reference), null, 0);
        }

        if (after is not null) index++;
        return (AltaExitCodes.Success, index < turns.Count ? turns[index].RunId : null, index);
    }

    private static int BranchRefused(AltaCommandContext context, string status, string sessionId, string done, string? turn = null)
    {
        var (exitCode, code, message) = DescribeBranchRefusal(status, sessionId, done, turn);
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, exitCode, message);
        return exitCode;
    }

    // Why a session was not read, forked or rewound (`done`), as the error of the command: what is not there is not
    // found, what the caller can wait for or do first is a refusal, and the rest is a failure. Every refusal but the
    // one of a turn is the refusal of a rewind.
    internal static (int ExitCode, string Code, string Message) DescribeBranchRefusal(string status, string sessionId, string done, string? turn)
        => status switch
        {
            "session_missing" => (AltaExitCodes.NotFound, "session.notFound", $"Session '{sessionId}' was not found."),
            "turn_not_found" => (AltaExitCodes.NotFound, "session.turnNotFound",
                $"Session '{sessionId}' has no turn '{turn}'. List its turns with `alta session turns {sessionId}`."),
            "turn_running" => (AltaExitCodes.Unsupported, "session.turnRunning",
                $"Turn '{turn}' of session '{sessionId}' is the turn that runs or comes after it: a turn in progress is not part of a fork. Fork before it, or wait for it to end."),
            "session_in_use" => (AltaExitCodes.Unsupported, "session.running",
                $"Session '{sessionId}' is running: a session is rewound when it is idle, and not by itself. Wait for its run to end, or stop it with `alta session abort {sessionId}`, then rewind."),
            "queue_pending" => (AltaExitCodes.Unsupported, "session.queuePending",
                $"Session '{sessionId}' has queued prompts: wait until they are sent, or remove them, then rewind."),
            "interaction_pending" => (AltaExitCodes.Unsupported, "session.interactionPending",
                $"Session '{sessionId}' waits for an answer to a question or to a permission request: answer it, then rewind."),
            "background_work" => (AltaExitCodes.Unsupported, "session.backgroundWork",
                $"Session '{sessionId}' has background work that reports to it: wait for it to end, or cancel it (`alta job list --session {sessionId}`, `alta job cancel <job-id>`), then rewind."),
            "session_archived" => (AltaExitCodes.Unsupported, "session.archived", $"Session '{sessionId}' is archived: restore it with `alta session restore {sessionId}`, then rewind."),
            "session_changed" => (AltaExitCodes.Failure, "session.changed",
                $"Session '{sessionId}' changed while it was being {done}: nothing was done. Run the command again."),
            "unsupported_format" => (AltaExitCodes.Failure, "session.unsupportedFormat",
                $"Session '{sessionId}' cannot be {done}: its journal is not one whose turns CodeAlta reads."),
            "io_failed" => (AltaExitCodes.Failure, "session.ioFailed",
                $"The journal of session '{sessionId}' could not be read or written: the session was not {done}."),
            _ => (AltaExitCodes.Failure, "session.branchFailed", $"Session '{sessionId}' was not {done}: {status}."),
        };

    // What the records of a turn show of its prompt.
    private const int TurnPromptLength = 200;

    private static string TurnPrompt(AgentSessionTurn turn)
    {
        var preview = turn.PromptPreview;
        if (preview.Length <= TurnPromptLength)
        {
            return preview;
        }

        // A character of two code units is not cut in two.
        return preview[..(char.IsHighSurrogate(preview[TurnPromptLength - 1]) ? TurnPromptLength - 1 : TurnPromptLength)];
    }

    private static bool IsTurnPromptTruncated(AgentSessionTurn turn)
        => turn.PromptTruncated || turn.PromptPreview.Length > TurnPromptLength;

    private static async ValueTask<int> HandleSessionSendAsync(
        AltaCommandContext context,
        string? sessionId,
        PromptOptions options,
        PromptDispatchKind kind)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session send");
        }

        var targetSessionId = sessionId.Trim();
        context.ClaimSessionWork(targetSessionId);
        var targetsCallingAgentSession = IsTargetingCallingAgentSession(context, targetSessionId);
        if (targetsCallingAgentSession && kind != PromptDispatchKind.Queue && !options.QueueIfBusy)
        {
            return PermissionDenied(
                context,
                "session.selfSendDenied",
                "A running agent session cannot send a new prompt to itself. Use `alta session queue <session-id> ...` or `alta session send <session-id> --queue-if-busy ...` to enqueue follow-up work, or send to a child/peer session.");
        }

        var promptResult = await ReadPromptAsync(context, options, kind).ConfigureAwait(false);
        if (promptResult.ExitCode != AltaExitCodes.Success)
        {
            return promptResult.ExitCode;
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infoResult = await ResolveSessionInfoAsync(context, targetSessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var info = infoResult.Info!;
        if (await RefuseSessionOfOtherSpaceAsync(context, info.Session.SessionId, "sends prompts to").ConfigureAwait(false) is { } otherSpace)
        {
            return otherSpace;
        }

        if (!runtime.AcceptsPromptFrom(context.Caller.SourceSessionId, info.Session))
        {
            return PromptDenied(context);
        }

        var inputText = kind is PromptDispatchKind.Message or PromptDispatchKind.Request
            ? BuildPeerAgentMessage(context, info.Session, options, promptResult.Prompt!)
            : promptResult.Prompt!;
        if (!string.IsNullOrWhiteSpace(options.PromptId))
        {
            var validationExitCode = await ValidateSessionAgentPromptIdAsync(context, info, options.PromptId).ConfigureAwait(false);
            if (validationExitCode != AltaExitCodes.Success)
            {
                return validationExitCode;
            }
        }

        // What a session is handed, it does with its own tools.
        if (await RefuseWiderToolsAsync(context, runtime => runtime.GetDisabledToolsAsync(info.Session, options.PromptId, context.CancellationToken),
                "the session it hands this prompt to").ConfigureAwait(false) is { } wider)
        {
            return wider;
        }

        var executionOptions = await BuildExecutionOptionsForSessionAsync(context, info, options.PromptId, completeModel: kind != PromptDispatchKind.Queue).ConfigureAwait(false);
        var agentInput = AgentInput.Text(inputText);

        try
        {
            if (kind == PromptDispatchKind.Queue)
            {
                var queueItem = await runtime.QueuePromptAsync(
                    info.Session,
                    inputText,
                    kind.ToString().ToLowerInvariant(),
                    CreateProvenance(context),
                    context.CancellationToken).ConfigureAwait(false);
                WritePromptResult(context, "alta.session.queued", info.Session, null, queueItem.QueueItemId, queued: true, kind, inputText);
                return AltaExitCodes.Success;
            }

            if (options.QueueIfBusy && (targetsCallingAgentSession || await runtime.HasActiveRunAsync(info.Session, context.CancellationToken).ConfigureAwait(false)))
            {
                var queueItem = await runtime.QueuePromptAsync(
                    info.Session,
                    inputText,
                    kind.ToString().ToLowerInvariant(),
                    CreateProvenance(context),
                    context.CancellationToken).ConfigureAwait(false);
                WritePromptResult(context, "alta.session.queued", info.Session, null, queueItem.QueueItemId, queued: true, kind, inputText);
                return AltaExitCodes.Success;
            }

            var augmentation = await BuildPluginAgentRunAugmentationAsync(context, info, executionOptions, agentInput).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(augmentation.CancelReason))
            {
                return PermissionDenied(context, "plugin.runCancelled", augmentation.CancelReason);
            }

            agentInput = augmentation.Input;
            executionOptions = augmentation.ExecutionOptions;

            // Inter-session communication must reach the recipient's current turn, just like
            // automatic child notifications. An ordinary SendAsync waits behind the whole run;
            // its detached acknowledgement is not evidence that this new input was delivered.
            var steerPeer = IsAgentCaller(context) && !targetsCallingAgentSession
                && await runtime.HasActiveRunAsync(info.Session, context.CancellationToken).ConfigureAwait(false);
            if (kind == PromptDispatchKind.Steer || steerPeer)
            {
                AgentRunId? steeredRunId = null;
                try
                {
                    steeredRunId = await context.ObserveOriginalAsync(runtime.SteerAsync(
                        info.Session,
                        executionOptions,
                        new AgentSteerOptions { Input = agentInput },
                        context.CancellationToken)).ConfigureAwait(false);
                }
                catch (Exception failure) when (kind != PromptDispatchKind.Steer
                    && failure is InvalidOperationException or NotSupportedException
                    && !AltaRetentionAdmission.ContainsRetainedDependency(failure))
                {
                    // The turn can finish between the active check and steering. Send normally
                    // in that case; a provider that cannot steer a still-running turn gets an
                    // explicit durable queue item rather than a hidden detached send waiter.
                    if (await runtime.HasActiveRunAsync(info.Session, context.CancellationToken).ConfigureAwait(false))
                    {
                        var queueItem = await runtime.QueuePromptAsync(info.Session, inputText,
                            kind.ToString().ToLowerInvariant(), CreateProvenance(context), context.CancellationToken).ConfigureAwait(false);
                        WritePromptResult(context, "alta.session.queued", info.Session, null, queueItem.QueueItemId, queued: true, kind, inputText);
                        return AltaExitCodes.Success;
                    }
                }

                // Receipt/provenance failure after accepted steering must not deliver the input again.
                if (steeredRunId is { } acceptedRunId)
                {
                    await PersistPromptProvenanceAsync(context, info.Session, acceptedRunId.Value, queued: false, kind, inputText).ConfigureAwait(false);
                    WritePromptResult(context, "alta.session.steered", info.Session, acceptedRunId.Value, null, queued: false, kind, inputText);
                    return AltaExitCodes.Success;
                }
            }

            var sendTask = runtime.SendAsync(
                info.Session,
                executionOptions,
                new AgentSendOptions { Input = agentInput, SourceSessionId = IsAgentCaller(context) ? context.Caller.SourceSessionId : null },
                ShouldDetachPromptSubmission(context) ? CancellationToken.None : context.CancellationToken);

            if (ShouldDetachPromptSubmission(context) && await WaitForAgentSubmissionAckAsync(runtime, info.Session, sendTask).ConfigureAwait(false) && !sendTask.IsCompleted)
            {
                _ = ObserveDetachedPromptSubmissionAsync(sendTask);
                await runtime.PersistSessionLocalStateAsync(info.Session, CancellationToken.None).ConfigureAwait(false);
                await PersistPromptProvenanceAsync(context, info.Session, runId: null, queued: false, kind, inputText).ConfigureAwait(false);
                WritePromptResult(context, kind is PromptDispatchKind.Message or PromptDispatchKind.Request ? "alta.session.message.sent" : "alta.session.submitted", info.Session, runId: null, queueItemId: null, queued: false, kind, inputText);
                return AltaExitCodes.Success;
            }

            var submittedRunId = await context.ObserveOriginalAsync(sendTask).ConfigureAwait(false);
            await runtime.PersistSessionLocalStateAsync(info.Session, context.CancellationToken).ConfigureAwait(false);
            await PersistPromptProvenanceAsync(context, info.Session, submittedRunId.Value, queued: false, kind, inputText).ConfigureAwait(false);
            WritePromptResult(context, kind is PromptDispatchKind.Message or PromptDispatchKind.Request ? "alta.session.message.sent" : "alta.session.submitted", info.Session, submittedRunId.Value, null, queued: false, kind, inputText);
            return AltaExitCodes.Success;
        }
        catch (Exception ex) when (kind == PromptDispatchKind.Steer && ex is InvalidOperationException or NotSupportedException)
        {
            return Unsupported(context, "session.steerUnsupported", ex.Message);
        }
    }

    private static async ValueTask<int> HandleSessionSetAgentAsync(AltaCommandContext context, string? sessionId, string? promptId)
    {
        context.ClaimSessionWork(sessionId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required outside a session caller.", "alta session set_agent");
        }

        if (string.IsNullOrWhiteSpace(promptId))
        {
            return UsageError(context, "usage.missingPrompt", "--prompt-id is required.", "alta session set_agent");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var info = infoResult.Info!;
        var normalizedPromptId = NormalizeOptionalText(promptId) ?? AgentPromptCatalog.DefaultPromptName;
        var validationExitCode = await ValidateSessionAgentPromptIdAsync(context, info, normalizedPromptId).ConfigureAwait(false);
        if (validationExitCode != AltaExitCodes.Success)
        {
            return validationExitCode;
        }

        if (await RefuseSessionOfOtherSpaceAsync(context, info.Session.SessionId, "changes the agent of").ConfigureAwait(false) is { } otherSpace)
        {
            return otherSpace;
        }

        if (await RefuseWiderToolsAsync(context, runtime => runtime.GetDisabledToolsAsync(info.Session, normalizedPromptId, context.CancellationToken),
                $"the session under the agent prompt '{normalizedPromptId}'").ConfigureAwait(false) is { } wider)
        {
            return wider;
        }

        info.Session.AgentPromptId = normalizedPromptId;
        await runtime.PersistSessionLocalStateAsync(info.Session, context.CancellationToken).ConfigureAwait(false);
        await runtime.SetActiveSessionAgentPromptIdAsync(info.Session.SessionId, normalizedPromptId, context.CancellationToken).ConfigureAwait(false);
        await PersistSessionPreferenceAsync(context, info.Session, CreateModelSelection(info.Session, info.Preference), normalizedPromptId).ConfigureAwait(false);

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.agent_set",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = info.Session.SessionId,
            promptId = normalizedPromptId,
            agentPromptId = normalizedPromptId,
            modelSelection = ToModelSelectionPayload(CreateModelSelection(info.Session, info.Preference), normalizedPromptId),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionTitleAsync(AltaCommandContext context, string? sessionId, string? title, string? mode)
    {
        context.ClaimSessionWork(sessionId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required outside a session caller.", "alta session title");
        }

        // `default` is no mode of its own: the session follows the setting again.
        AgentSessionTitleMode? ownMode = null;
        var changeMode = mode is not null;
        if (changeMode && !string.Equals(mode!.Trim(), "default", StringComparison.OrdinalIgnoreCase))
        {
            ownMode = AgentSessionTitles.ParseMode(mode);
            if (ownMode is null)
            {
                return UsageError(context, "usage.invalidTitleMode", "--mode must be first_prompt, last_message, model or default.", "alta session title");
            }
        }

        if (title is not null)
        {
            title = title.Trim();
            if (SessionRuntimeService.SessionTitleError(title) is { } error)
            {
                return UsageError(context, "usage.invalidTitle", error, "alta session title");
            }
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var resolvedSessionId = infoResult.Info!.Session.SessionId;
        var changed = title is not null || changeMode;
        var state = changed
            ? await runtime.SetSessionTitleAsync(resolvedSessionId, title, changeMode, ownMode, context.CancellationToken).ConfigureAwait(false)
            : await runtime.GetSessionTitleAsync(resolvedSessionId, context.CancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return NotFound(context, "session.notFound", $"Session '{sessionId}' was not found.");
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.title",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = state.SessionId,
            title = state.Title,
            titleSource = AgentSessionTitles.NameOf(state.Source),
            titleMode = AgentSessionTitles.NameOf(state.Mode),
            titleModeFrom = state.OwnMode ? "session" : "setting",
            changed,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionAbortAsync(AltaCommandContext context, string? sessionId, string? reason)
    {
        context.ClaimSessionWork(sessionId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session abort");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (info.ExitCode != AltaExitCodes.Success)
        {
            return info.ExitCode;
        }

        try
        {
            await runtime.AbortAsync(sessionId, context.CancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Unsupported(context, "session.abortUnsupported", ex.Message);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.aborted",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId,
            reason,
            abortedBy = CreateProvenance(context),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionCompactAsync(AltaCommandContext context, string? sessionId, bool submit)
    {
        context.ClaimSessionWork(sessionId);
        if (submit)
        {
            return Unsupported(context, "session.compactSubmitUnsupported", "--submit after compaction is not supported by the current runtime.");
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session compact");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (info.ExitCode != AltaExitCodes.Success)
        {
            return info.ExitCode;
        }

        var sessionInfo = info.Info!;
        var executionOptions = await BuildExecutionOptionsForSessionAsync(context, sessionInfo, promptId: null).ConfigureAwait(false);
        await runtime.CompactAsync(sessionInfo.Session, executionOptions, context.CancellationToken).ConfigureAwait(false);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.compacted",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = sessionInfo.Session.SessionId,
            compactedBy = CreateProvenance(context),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleReminderCreateAsync(AltaCommandContext context, ReminderCreateOptions options)
    {
        context.ClaimSessionWork(FirstNonEmpty(options.SessionId, context.Caller.SourceSessionId));
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out _))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (!TryGetReminderService(context, out var reminderService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (!TryParseReminderDuration(options.Duration, out var duration, out var durationError))
        {
            return UsageError(context, "usage.invalidDuration", durationError, "alta reminder create");
        }

        if (!TryParseReminderRepeat(options.Repeat, out var repeat, out var repeatError))
        {
            return UsageError(context, "usage.invalidRepeat", repeatError, "alta reminder create");
        }

        var contentResult = await ReadReminderContentAsync(context, options).ConfigureAwait(false);
        if (contentResult.ExitCode != AltaExitCodes.Success)
        {
            return contentResult.ExitCode;
        }

        var targetSessionId = FirstNonEmpty(options.SessionId, context.Caller.SourceSessionId);
        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            return UsageError(context, "usage.missingSession", "A target session id is required outside a session caller. Use --session <session-id>.", "alta reminder create");
        }

        var infoResult = await ResolveSessionInfoAsync(context, targetSessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        // A reminder for another session is a prompt it is handed.
        if (await RefuseSessionOfOtherSpaceAsync(context, infoResult.Info!.Session.SessionId, "sends prompts to").ConfigureAwait(false) is { } otherSpace)
        {
            return otherSpace;
        }

        if (await RefuseWiderToolsAsync(context, runtime => runtime.GetDisabledToolsAsync(infoResult.Info!.Session, null, context.CancellationToken),
                "the session the reminder is for").ConfigureAwait(false) is { } wider)
        {
            return wider;
        }

        if (context.Services.Get<SessionRuntimeService>()?.AcceptsPromptFrom(context.Caller.SourceSessionId, infoResult.Info!.Session) == false)
        {
            return PromptDenied(context);
        }

        AltaReminderDescriptor descriptor;
        AltaReminderNotificationFailure? notificationFailure;
        try
        {
            descriptor = reminderService.Create(new AltaReminderCreateRequest
            {
                TargetSessionId = infoResult.Info!.Session.SessionId,
                Content = contentResult.Prompt!,
                Duration = duration,
                RepeatCount = repeat,
                SourceSessionId = context.Caller.SourceSessionId,
                SourceAgentId = context.Caller.SourceAgentId,
                SourceProjectId = context.Caller.SourceProjectId,
                PluginRuntimeKey = context.Caller.PluginRuntimeKey,
                Cwd = context.Cwd,
            }, out notificationFailure);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return UsageError(context, "usage.invalidReminder", ex.Message, "alta reminder create");
        }
        catch (ArgumentException ex)
        {
            return UsageError(context, "usage.invalidReminder", ex.Message, "alta reminder create");
        }

        WriteReminder(context, "alta.reminder.created", descriptor);
        WriteReminderNotificationFailure(context, notificationFailure, historical: false);
        return AltaExitCodes.Success;
    }

    private static int HandleReminderList(AltaCommandContext context, string? sessionId, bool includeCompleted)
    {
        if (!TryGetReminderService(context, out var reminderService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var reminders = reminderService.List(sessionId, includeCompleted);
        foreach (var reminder in reminders)
        {
            WriteReminder(context, "alta.reminder.item", reminder);
        }

        WriteSummary(context, "alta.reminder.summary", reminders.Count, truncated: false);
        var notificationFailure = reminderService.GetLastNotificationFailure();
        if (notificationFailure is not null && (string.IsNullOrWhiteSpace(sessionId)
            || string.Equals(notificationFailure.TargetSessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
        {
            WriteReminderNotificationFailure(context, notificationFailure, historical: true);
        }
        return AltaExitCodes.Success;
    }

    private static int HandleReminderDelete(AltaCommandContext context, string? reminderId)
    {
        if (string.IsNullOrWhiteSpace(reminderId))
        {
            return UsageError(context, "usage.missingReminder", "Reminder id is required.", "alta reminder delete");
        }

        if (!TryGetReminderService(context, out var reminderService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (!reminderService.TryDelete(reminderId, out var descriptor, out var notificationFailure))
        {
            return NotFound(context, "reminder.notFound", $"Reminder '{reminderId}' was not found.");
        }

        WriteReminder(context, "alta.reminder.deleted", descriptor!);
        WriteReminderNotificationFailure(context, notificationFailure, historical: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandlePromptListAsync(AltaCommandContext context, PromptManagementOptions options)
    {
        var queryResult = await BuildPromptQueryAsync(context, options.Project).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var catalog = new AgentPromptCatalog();
        // A session lists the agent prompts of its own space.
        var space = options.System ? null : await CallerSpaceFilterAsync(context).ConfigureAwait(false);
        var records = options.System
            ? catalog.ListSystemPrompts(queryResult.Query!).Where(prompt => ScopeMatches(options.Scope, prompt.SourceKind)).Select(prompt => CreateSystemPromptRecord(context, prompt, options.Verbose))
            : AgentPromptCatalog.InSpace(catalog.ListPrompts(queryResult.Query!), space).Where(prompt => ScopeMatches(options.Scope, prompt.SourceKind)).Select(prompt => CreateAgentPromptRecord(context, prompt, options.Verbose));
        var count = 0;
        foreach (var record in records)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, record);
            count++;
        }

        WriteSummary(context, "alta.prompt.summary", count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandlePromptShowAsync(AltaCommandContext context, string? promptId, PromptManagementOptions options)
    {
        if (string.IsNullOrWhiteSpace(promptId))
        {
            return UsageError(context, "usage.missingPrompt", "Prompt id is required.", "alta prompt show");
        }

        var queryResult = await BuildPromptQueryAsync(context, options.Project).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var catalog = new AgentPromptCatalog();
        if (options.System)
        {
            var prompt = SelectPromptForShow(catalog.ListSystemPrompts(queryResult.Query!), promptId.Trim(), options.Scope);
            if (prompt is null)
            {
                return NotFound(context, "prompt.notFound", $"System prompt '{promptId}' was not found in scope '{options.Scope}'.");
            }

            AltaJsonlWriter.WriteRecord(context.Stdout, CreateSystemPromptRecord(context, prompt, includeContent: true));
            return AltaExitCodes.Success;
        }

        var agentPrompt = SelectPromptForShow(catalog.ListPrompts(queryResult.Query!), promptId.Trim(), options.Scope);
        if (agentPrompt is null)
        {
            return NotFound(context, "prompt.notFound", $"Agent prompt '{promptId}' was not found in scope '{options.Scope}'.");
        }

        if (RefuseOutsideCallerSpace(context, await CallerSpaceFilterAsync(context).ConfigureAwait(false), SpaceResourceKind.Prompt, agentPrompt.PromptName, "prompt.notInSpace", out var refused))
        {
            return refused;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, CreateAgentPromptRecord(context, agentPrompt, includeContent: true));
        return AltaExitCodes.Success;
    }

    private static ValueTask<int> HandlePromptCreateAsync(AltaCommandContext context, string? promptId, PromptManagementOptions options)
        => HandlePromptMutationAsync(context, promptId, options, create: true);

    private static ValueTask<int> HandlePromptEditAsync(AltaCommandContext context, string? promptId, PromptManagementOptions options)
        => HandlePromptMutationAsync(context, promptId, options, create: false);

    private static async ValueTask<int> HandlePromptMutationAsync(AltaCommandContext context, string? promptId, PromptManagementOptions options, bool create)
    {
        var commandPath = create ? "alta prompt create" : "alta prompt edit";
        // The `disabled_tools` of an agent prompt are what some sessions do without: a prompt of the same id in a
        // project replaces the global one, and an edit drops them.
        if (await RefuseSettingsChangeAsync(context, create ? "create a prompt" : "edit a prompt").ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (options.Scope == "all")
            {
                return UsageError(context, "usage.invalidScope", "Prompt mutations require --scope global or --scope project.", commandPath);
            }
            return create
                ? await CreatePromptAsync(context, promptId, options).ConfigureAwait(false)
                : await EditPromptAsync(context, promptId, options).ConfigureAwait(false);
        }
        catch (ArgumentException ex) when (ex is not DecoderFallbackException and not EncoderFallbackException)
        {
            if (ex.ParamName == "scope" && options.Scope == "project")
            {
                return UsageError(context, "usage.missingProject", "Project prompt mutation requires a project root. Provide --project or invoke from a project directory.", commandPath);
            }
            return UsageError(context, "usage.invalidPrompt", ex.Message, commandPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or EncoderFallbackException)
        {
            if (ex is UnauthorizedAccessException && options.Scope == "builtin")
            {
                return UsageError(context, "usage.invalidScope", ex.Message, commandPath);
            }
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "prompt.storageFailed", AltaExitCodes.Failure, ex.Message, commandPath);
            return AltaExitCodes.Failure;
        }
    }

    private static PromptResourceStore CreatePromptStore(AltaCommandContext context, AgentPromptCatalogQuery query)
    {
        var roots = new AgentPromptCatalog().ResolveRoots(query);
        return new PromptResourceStore(roots.ShippedPromptRoot, roots.GlobalPromptRoot, roots.ProjectPromptRoot,
            context.Services.Get<TextFileCodec>() ?? new TextFileCodec());
    }

    private static PromptResourceIdentity GetPromptIdentity(string promptId, PromptManagementOptions options)
        => new(options.Scope switch
        {
            "global" => PromptResourceScope.Global,
            "project" => PromptResourceScope.Project,
            "builtin" => PromptResourceScope.BuiltIn,
            _ => throw new ArgumentException("Prompt mutations require --scope global or --scope project."),
        }, options.System ? PromptResourceKind.System : PromptResourceKind.Agent, promptId.Trim());

    private static async ValueTask<int> CreatePromptAsync(AltaCommandContext context, string? promptId, PromptManagementOptions options)
    {
        const string CommandPath = "alta prompt create";
        if (string.IsNullOrWhiteSpace(promptId))
        {
            return UsageError(context, "usage.missingPrompt", "Prompt id is required.", CommandPath);
        }

        if (options.Content is not null && options.UseStdin)
        {
            return UsageError(context, "usage.contentConflict", "Use either --content or --stdin, not both.", CommandPath);
        }

        var queryResult = await BuildPromptQueryAsync(context, options.Project).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var store = CreatePromptStore(context, queryResult.Query!);
        var identity = GetPromptIdentity(promptId, options);
        // Creation always retains Missing through stdin and commit; it never adopts a file.
        var path = store.GetEditablePath(identity);
        string? body = null;
        if (!string.IsNullOrWhiteSpace(options.Content))
        {
            body = options.Content;
        }
        else if (options.UseStdin)
        {
            body = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
        }

        body = NormalizeOptionalText(body);
        if (body is null)
        {
            return UsageError(context, "usage.missingContent", "Prompt create requires a non-empty prompt body via --content or --stdin.", CommandPath);
        }

        var normalizedPromptId = promptId.Trim();
        var name = NormalizeOptionalText(options.Name) ?? normalizedPromptId;
        var description = NormalizeOptionalText(options.Description);
        var systemPromptId = NormalizeOptionalText(options.SystemPromptId) ?? AgentPromptCatalog.DefaultPromptName;
        var content = new PromptFileContent(options.System ? NormalizeOptionalText(options.Name) : name,
            description, options.System ? null : systemPromptId, body, false);
        var saved = await store.CreateAsync(identity, content, context.CancellationToken).ConfigureAwait(false);
        if (saved.IsConflict)
        {
            return UsageError(context, "usage.promptExists", $"Prompt file already exists at '{path}'. Use `alta prompt edit` to replace it.", CommandPath);
        }

        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.prompt.created",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["promptId"] = normalizedPromptId,
            ["id"] = normalizedPromptId,
            ["name"] = name,
            ["description"] = description,
            ["promptKind"] = options.System ? "system" : "user",
            ["scope"] = options.Scope,
            ["source"] = options.Scope,
            ["path"] = path,
            ["created"] = true,
        };
        if (!options.System)
        {
            record["systemPromptId"] = systemPromptId;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> EditPromptAsync(AltaCommandContext context, string? promptId, PromptManagementOptions options)
    {
        if (string.IsNullOrWhiteSpace(promptId))
        {
            return UsageError(context, "usage.missingPrompt", "Prompt id is required.", "alta prompt edit");
        }

        if (options.Content is not null && options.UseStdin)
        {
            return UsageError(context, "usage.contentConflict", "Use either --content or --stdin, not both.", "alta prompt edit");
        }

        var queryResult = await BuildPromptQueryAsync(context, options.Project).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var store = CreatePromptStore(context, queryResult.Query!);
        var identity = GetPromptIdentity(promptId, options);
        var path = store.GetEditablePath(identity);
        var updated = options.Content is not null || options.UseStdin;
        if (updated)
        {
            var document = await store.OpenDocumentAsync(identity, context.CancellationToken).ConfigureAwait(false);
            var content = options.Content ?? await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            var saved = await store.SaveDocumentAsync(document, content, context.CancellationToken).ConfigureAwait(false);
            if (saved.IsConflict)
            {
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "prompt.conflict", AltaExitCodes.Failure,
                    "Prompt file changed after this invocation captured its baseline. No replacement was written.", "alta prompt edit");
                return AltaExitCodes.Failure;
            }
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.prompt.edit",
            version = 1,
            correlationId = context.CorrelationId,
            promptId = promptId.Trim(),
            id = promptId.Trim(),
            promptKind = options.System ? "system" : "user",
            scope = options.Scope,
            source = options.Scope,
            path,
            exists = File.Exists(path),
            updated,
        });
        return AltaExitCodes.Success;
    }

    // A detailed record of a skill takes up to about 1.5 KB with a long description: 30 of them stay under the 64 KB and
    // 200 records the alta tool returns by default, so the summary that says what was left out is not cut itself.
    private const int SkillListDetailedDefaultLimit = 30;

    private static async ValueTask<int> HandleSkillListAsync(AltaCommandContext context, string? projectRef, bool detailed, string? queryText, string? scopeText, string? limitText)
    {
        const string CommandPath = "alta skill list";
        int? limit = detailed ? SkillListDetailedDefaultLimit : null;
        if (NormalizeOptionalText(limitText) is { } limitValue)
        {
            if (!int.TryParse(limitValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit) || parsedLimit < 1)
            {
                return UsageError(context, "usage.invalidLimit", "--limit must be a positive integer.", CommandPath);
            }

            limit = parsedLimit;
        }

        SkillScopeKind? scope = null;
        if (NormalizeOptionalText(scopeText) is { } scopeValue)
        {
            scope = scopeValue.ToLowerInvariant() switch
            {
                "project" => SkillScopeKind.Project,
                "user" => SkillScopeKind.User,
                "builtin" => SkillScopeKind.Builtin,
                "plugin" => SkillScopeKind.Plugin,
                _ => null,
            };
            if (scope is null)
            {
                return UsageError(context, "usage.invalidScope", "--scope is project, user, builtin or plugin.", CommandPath);
            }
        }

        if (!context.TryGetRequired<SkillCatalog>(nameof(SkillCatalog), out var skillCatalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var queryResult = await BuildSkillQueryAsync(context, projectRef).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var query = NormalizeOptionalText(queryText);
        var words = query?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        var skills = await skillCatalog.ListAsync(queryResult.Query!, context.CancellationToken).ConfigureAwait(false);
        var matching = skills
            .Where(skill => scope is null || skill.Scope == scope)
            .Where(skill => words.All(word => ContainsOrdinalIgnoreCase(skill.Name, word) || ContainsOrdinalIgnoreCase(skill.Title, word) || ContainsOrdinalIgnoreCase(skill.Description, word)))
            .ToArray();
        var truncated = limit is { } maximum && matching.Length > maximum;
        var returned = truncated ? matching[..limit!.Value] : matching;
        var next = truncated ? SkillListNext(context, projectRef, detailed, query, scopeText, matching.Length) : null;
        if (!detailed)
        {
            WriteSkillRefs(context, returned, truncated ? matching.Length : null, next);
            return AltaExitCodes.Success;
        }

        foreach (var skill in returned)
        {
            WriteSkill(context, "alta.skill.item", skill, includeDiagnostics: true);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.skill.summary",
            version = 1,
            correlationId = context.CorrelationId,
            count = returned.Length,
            total = matching.Length,
            returned = returned.Length,
            truncated,
            limit,
            query,
            scope = scope?.ToString().ToLowerInvariant(),
            next,
            hint = matching.Length == 0 && (query is not null || scope is not null)
                ? "No skill matched. Try other words or fewer filters" + (context.Services.Get<CodeAlta.Search.SearchService>() is not null ? ", or find skills by what they do with `alta search --kind skill <need>`." : ".")
                : null,
        });
        return AltaExitCodes.Success;
    }

    // The command to go on with when the limit cut the list: with words, the same list with a limit that takes every match;
    // without, a search by purpose where the host keeps the index, else the same list narrowed by words.
    private static string SkillListNext(AltaCommandContext context, string? projectRef, bool detailed, string? query, string? scopeText, int total)
    {
        if (query is null && context.Services.Get<CodeAlta.Search.SearchService>() is not null)
        {
            return "alta search --kind skill \"<what you need>\"";
        }

        var command = new StringBuilder("alta skill list");
        if (NormalizeOptionalText(projectRef) is { } project) command.Append(" --project ").Append(project);
        if (detailed) command.Append(" --detailed");
        if (NormalizeOptionalText(scopeText) is { } scope) command.Append(" --scope ").Append(scope.ToLowerInvariant());
        if (query is null) return command.Append(" --query \"<words>\"").ToString();
        return command.Append(" --query \"").Append(query.Replace("\"", "\\\"", StringComparison.Ordinal)).Append("\" --limit ").Append(total.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    private static async ValueTask<int> HandleSkillShowAsync(AltaCommandContext context, string? skillName, string? projectRef)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return UsageError(context, "usage.missingSkill", "Skill name is required.", "alta skill show");
        }

        if (!context.TryGetRequired<SkillCatalog>(nameof(SkillCatalog), out var skillCatalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var queryResult = await BuildSkillQueryAsync(context, projectRef).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var document = await skillCatalog.GetAsync(queryResult.Query!, skillName, context.CancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return NotFound(context, "skill.notFound", $"Skill '{skillName}' was not found.");
        }

        WriteSkill(context, "alta.skill.detail", document.Descriptor, includeDiagnostics: true, document);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSkillActivateAsync(AltaCommandContext context, string? skillName, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return UsageError(context, "usage.missingSkill", "Skill name is required.", "alta skill activate");
        }

        // A session that activates a skill means itself.
        sessionId = NormalizeOptionalText(sessionId) ?? NormalizeOptionalText(context.Caller.SourceSessionId);
        if (sessionId is null)
        {
            return UsageError(context, "usage.missingSession", "--session <session-id> is required outside a session.", "alta skill activate");
        }

        context.ClaimSessionWork(sessionId);
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (info.ExitCode != AltaExitCodes.Success)
        {
            return info.ExitCode;
        }

        var sessionInfo = info.Info!;
        var executionOptions = await BuildExecutionOptionsForSessionAsync(context, sessionInfo, promptId: null).ConfigureAwait(false);
        try
        {
            if (await runtime.HasActiveRunAsync(sessionInfo.Session, context.CancellationToken).ConfigureAwait(false))
            {
                if (IsSameAgentSourceSession(context, sessionInfo.Session.SessionId))
                {
                    var activation = await runtime.CreateSkillActivationAsync(sessionInfo.Session, executionOptions, skillName, context.CancellationToken).ConfigureAwait(false);
                    WriteSkillActivated(context, activation, sessionInfo.Session.SessionId, runId: null, includePayload: true);
                    return AltaExitCodes.Success;
                }

                return Unsupported(
                    context,
                    "skill.activationSessionBusy",
                    $"Session '{sessionInfo.Session.SessionId}' has an active run. Wait until the session is idle before activating a skill for it.");
            }

            var runId = await runtime.ActivateSkillAsync(sessionInfo.Session, executionOptions, skillName, context.CancellationToken).ConfigureAwait(false);
            WriteSkillActivated(context, activation: null, sessionInfo.Session.SessionId, runId.Value, includePayload: false, skillName);
            return AltaExitCodes.Success;
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(context, "skill.notFound", ex.Message);
        }
        catch (ResourceNotInSpaceException ex)
        {
            return Unsupported(context, "skill.notInSpace", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Unsupported(context, "skill.activationUnsupported", ex.Message);
        }
    }

    private static bool IsSameAgentSourceSession(AltaCommandContext context, string sessionId)
        => string.Equals(context.Caller.Kind, "agent", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(context.Caller.SourceSessionId, sessionId, StringComparison.OrdinalIgnoreCase);

    private static void WriteSkillActivated(
        AltaCommandContext context,
        SkillActivation? activation,
        string sessionId,
        string? runId,
        bool includePayload,
        string? skillName = null)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.skill.activated",
            version = 1,
            correlationId = context.CorrelationId,
            skillName = activation?.Descriptor.Name ?? skillName,
            sessionId,
            runId,
            activatedBy = CreateProvenance(context),
            payload = includePayload ? activation?.Payload : null,
        });
    }

    private static async ValueTask<int> HandleProviderListAsync(AltaCommandContext context, bool detailed)
    {
        var descriptors = GetProviderDescriptors(context);
        var ProviderIds = GetProviderProviderIds(context, descriptors);

        if (!detailed)
        {
            WriteProviderKeys(context, ProviderIds);
            await Task.CompletedTask.ConfigureAwait(false);
            return AltaExitCodes.Success;
        }

        foreach (var ProviderId in ProviderIds)
        {
            var descriptor = descriptors.FirstOrDefault(candidate => string.Equals(candidate.ProviderId.Value, ProviderId.Value, StringComparison.OrdinalIgnoreCase));
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.provider.item",
                version = 1,
                correlationId = context.CorrelationId,
                providerKey = ProviderId.Value,
                ProviderId = ProviderId.Value,
                displayName = descriptor?.DisplayName,
            });
        }

        WriteSummary(context, "alta.provider.summary", ProviderIds.Length, truncated: false);
        await Task.CompletedTask.ConfigureAwait(false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleModelListAsync(AltaCommandContext context, ModelListOptions options)
    {
        if (!context.TryGetRequired<IModelProviderInitializationService>(nameof(IModelProviderInitializationService), out var providerInitializationService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var providerIds = context.Services.Get<IModelProviderRegistry>()?.ListProviders()
            .Select(static descriptor => descriptor.ProviderId)
            ?? providerInitializationService.CurrentStates.Select(static state => state.ProviderId);
        var providers = providerIds
            .Where(id => string.IsNullOrWhiteSpace(options.Provider) || string.Equals(id.Value, options.Provider, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static id => id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(options.Provider) && providers.Length == 0)
        {
            return NotFound(context, "provider.notFound", $"Provider '{options.Provider}' was not found.");
        }

        var count = 0;
        var modelRefs = options.Detailed ? null : new List<string>();
        foreach (var provider in providers)
        {
            IReadOnlyList<AgentModelInfo> models;
            try
            {
                models = await providerInitializationService.GetModelsAsync(provider, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or NotSupportedException)
            {
                AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "model.listUnavailable", $"Provider '{provider.Value}' models are unavailable: {ex.Message}");
                continue;
            }

            foreach (var model in models
                         .Where(model => ModelMatchesFilters(provider.Value, model, options))
                         .OrderBy(static model => model.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (options.Detailed)
                {
                    WriteModelItem(context, provider.Value, model, options.ReasoningEffort);
                }
                else
                {
                    modelRefs!.Add(CreateModelRef(provider.Value, model, options.ReasoningEffort));
                }

                count++;
            }
        }

        if (options.Detailed)
        {
            WriteSummary(context, "alta.model.summary", count, truncated: false);
        }
        else
        {
            WriteModelRefs(context, modelRefs ?? []);
        }

        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleModelShowAsync(AltaCommandContext context, string? modelRef)
    {
        if (string.IsNullOrWhiteSpace(modelRef))
        {
            return UsageError(context, "usage.missingModelRef", "Model ref is required.", "alta model show");
        }

        if (!AltaModelRef.TryParse(modelRef, out var selection, out var error))
        {
            return UsageError(context, "usage.invalidModelRef", error!, "alta model show");
        }

        if (!context.TryGetRequired<IModelProviderInitializationService>(nameof(IModelProviderInitializationService), out var providerInitializationService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var provider = new ModelProviderId(selection.ProviderKey);
        IReadOnlyList<AgentModelInfo> models;
        try
        {
            models = await providerInitializationService.GetModelsAsync(provider, context.CancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(context, "provider.notFound", $"Provider '{selection.ProviderKey}' was not found.");
        }

        var model = models.FirstOrDefault(candidate => string.Equals(candidate.Id, selection.ModelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return NotFound(context, "model.notFound", $"Model '{selection.ModelId}' was not found for provider '{selection.ProviderKey}'.");
        }

        WriteModelItem(context, selection.ProviderKey, model, selection.ReasoningEffort);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleModelResolveAsync(AltaCommandContext context, AltaModelSelectionOptions options)
    {
        var result = await ResolveModelSelectionAsync(context, options, "alta model resolve").ConfigureAwait(false);
        if (result.ExitCode != AltaExitCodes.Success)
        {
            return result.ExitCode;
        }

        WriteModelSelection(context, "alta.model.selection", result.Selection!, sessionId: null, options.PromptId);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleNotesGetAsync(AltaCommandContext context)
    {
        if (!TryGetNotesService(context, out var notesService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        try
        {
            var markdown = await notesService.GetMarkdownAsync(context.Caller, context.CancellationToken).ConfigureAwait(false);
            WriteNotesRecord(context, "alta.notes.current", markdown);
            return AltaExitCodes.Success;
        }
        catch (AltaNotesSessionRequiredException)
        {
            return MissingNotesSession(context);
        }
    }

    private static async ValueTask<int> HandleNotesSetAsync(AltaCommandContext context, bool useStdin)
    {
        context.ClaimSessionWork(context.Caller.SourceSessionId);
        _ = useStdin;
        if (!TryGetNotesService(context, out var notesService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        try
        {
            var caller = notesService.CaptureCaller(context.Caller);
            var markdown = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            await context.ObserveOriginalAsync(notesService.SetMarkdownAsync(markdown, caller, context.CancellationToken).AsTask()).ConfigureAwait(false);
            WriteNotesRecord(context, "alta.notes.updated", markdown);
            return AltaExitCodes.Success;
        }
        catch (AltaNotesSessionRequiredException)
        {
            return MissingNotesSession(context);
        }
    }

    private static async ValueTask<int> HandleNotesClearAsync(AltaCommandContext context)
    {
        context.ClaimSessionWork(context.Caller.SourceSessionId);
        if (!TryGetNotesService(context, out var notesService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        try
        {
            await context.ObserveOriginalAsync(notesService.ClearAsync(context.Caller, context.CancellationToken).AsTask()).ConfigureAwait(false);
            WriteNotesRecord(context, "alta.notes.updated", string.Empty);
            return AltaExitCodes.Success;
        }
        catch (AltaNotesSessionRequiredException)
        {
            return MissingNotesSession(context);
        }
    }

    private static void WriteNotesRecord(AltaCommandContext context, string type, string markdown)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            markdown,
            length = markdown.Length,
            empty = markdown.Length == 0,
        });
    }

    private static int MissingNotesSession(AltaCommandContext context)
        => UsageError(
            context,
            "usage.missingSession",
            "A current session is required for alta notes.",
            "Run `alta notes` from an agent/session context.");

    private static async Task<IReadOnlyList<AltaSessionInfo>?> LoadSessionInfosAsync(AltaCommandContext context)
    {
        var queryService = context.Services.Get<IAltaSessionQueryService>();
        if (queryService is null &&
            context.Services.Get<SessionRuntimeService>() is null &&
            context.Services.Get<SessionViewCatalog>() is null)
        {
            await foreach (var _ in new AltaSessionQueryService().LoadAsync(context).ConfigureAwait(false))
            {
            }

            return null;
        }

        queryService ??= new AltaSessionQueryService();
        var infos = new List<AltaSessionInfo>();
        var enumerator = queryService.LoadAsync(context).GetAsyncEnumerator();
        try
        {
            while (await context.ObserveOriginalAsync(enumerator.MoveNextAsync().AsTask()).ConfigureAwait(false))
                infos.Add(enumerator.Current);
        }
        finally { await context.ObserveOriginalAsync(enumerator.DisposeAsync().AsTask()).ConfigureAwait(false); }

        return infos;
    }

    private static bool TryGetReminderService(AltaCommandContext context, out AltaReminderService reminderService)
    {
        reminderService = context.Services.Get<AltaReminderService>()!;
        if (reminderService is not null)
        {
            return true;
        }

        if (context.Services is AltaServiceCollection services)
        {
            reminderService = new AltaReminderService(services);
            services.Add(reminderService);
            return true;
        }

        AltaJsonlWriter.WriteError(
            context.Stderr,
            context.CorrelationId,
            "service.unavailable",
            AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'AltaReminderService' is unavailable.");
        return false;
    }

    private static bool TryGetAskService(AltaCommandContext context, out IAltaAskService askService)
    {
        askService = context.Services.Get<IAltaAskService>()!;
        if (askService is not null)
        {
            return true;
        }

        AltaJsonlWriter.WriteError(
            context.Stderr,
            context.CorrelationId,
            "service.unavailable",
            AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'IAltaAskService' is unavailable.");
        return false;
    }

    private static bool TryGetNotesService(AltaCommandContext context, out IAltaNotesService notesService)
    {
        notesService = context.Services.Get<IAltaNotesService>()!;
        if (notesService is not null)
        {
            return true;
        }

        AltaJsonlWriter.WriteError(
            context.Stderr,
            context.CorrelationId,
            "service.unavailable",
            AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'IAltaNotesService' is unavailable.");
        return false;
    }

    private static async Task<AskAllowedRootsResult> ResolveAskAllowedRootsAsync(AltaCommandContext context, string sessionId)
    {
        if (context.Services.Get<SessionRuntimeService>() is null &&
            context.Services.Get<SessionViewCatalog>() is null &&
            context.Services.Get<IAltaSessionQueryService>() is null)
        {
            var fallback = !string.IsNullOrWhiteSpace(context.Cwd) ? ResolvePath(context, context.Cwd) : Environment.CurrentDirectory;
            return AskAllowedRootsResult.Success(fallback, [fallback]);
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return AskAllowedRootsResult.Fail(infoResult.ExitCode);
        }

        var roots = new List<string>();
        var workingDirectory = infoResult.Info!.Session.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            roots.Add(ResolvePath(context, workingDirectory));
        }

        if (!string.IsNullOrWhiteSpace(infoResult.Info.Session.ProjectRef) && context.Services.Get<ProjectCatalog>() is { } projectCatalog)
        {
            var project = await ResolveProjectAsync(projectCatalog, infoResult.Info.Session.ProjectRef, context, includeArchived: true).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(project?.ProjectPath))
            {
                roots.Add(ResolvePath(context, project.ProjectPath));
            }
        }

        var normalizedRoots = roots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return AskAllowedRootsResult.Success(normalizedRoots.FirstOrDefault() ?? Environment.CurrentDirectory, normalizedRoots);
    }

    private static async Task<SessionInfoResolutionResult> ResolveSessionInfoAsync(
        AltaCommandContext context, string sessionId, bool includeLocalState = false, bool includeChildren = false)
    {
        if (context.Services.Get<SessionRuntimeService>() is { } runtime)
        {
            var activeSession = await runtime.TryGetActiveSessionDescriptorAsync(sessionId, context.CancellationToken).ConfigureAwait(false);
            if (activeSession is not null)
            {
                var isRunning = await runtime.HasActiveRunAsync(activeSession, context.CancellationToken).ConfigureAwait(false);
                var localState = includeLocalState && context.Services.Get<SessionViewCatalog>() is { } sessionCatalog
                    ? await TryReadLatestSessionStateAsync(sessionCatalog, activeSession, context.CancellationToken).ConfigureAwait(false)
                    : null;
                var activeInfo = new AltaSessionInfo(
                    activeSession,
                    localState,
                    null,
                    isRunning,
                    true,
                    isRunning ? "running" : "idle");
                // Reporting needs the same relationship inventory as `session children`. Delivery/control
                // callers keep the active-session fast path and never scan unrelated session journals.
                var relatedInfos = includeChildren ? await LoadSessionInfosAsync(context).ConfigureAwait(false) : [activeInfo];
                return relatedInfos is null
                    ? SessionInfoResolutionResult.Fail(AltaExitCodes.ServiceUnavailable)
                    : SessionInfoResolutionResult.Success(activeInfo, relatedInfos);
            }
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return SessionInfoResolutionResult.Fail(AltaExitCodes.ServiceUnavailable);
        }

        var info = FindSession(infos, sessionId);
        return info is null
            ? SessionInfoResolutionResult.Fail(NotFound(context, "session.notFound", $"Session '{sessionId}' was not found."))
            : SessionInfoResolutionResult.Success(info, infos);
    }

    private static AltaSessionInfo? FindSession(IReadOnlyList<AltaSessionInfo> infos, string sessionId)
        => infos.FirstOrDefault(info =>
            string.Equals(info.Session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<AltaSessionInfo> GetChildren(IReadOnlyList<AltaSessionInfo> infos, AltaSessionInfo parent, bool recursive)
    {
        var directChildren = infos
            .Where(info => string.Equals(info.Session.ParentSessionId, parent.Session.SessionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static info => info.Session.LastActiveAt)
            .ToArray();
        foreach (var child in directChildren)
        {
            yield return child;
            if (!recursive)
            {
                continue;
            }

            foreach (var descendant in GetChildren(infos, child, recursive: true))
            {
                yield return descendant;
            }
        }
    }

    // Without a host policy, sessions run by alta commands allow every permission once and answer user input
    // with nothing. The instances are shared: the runtime compares a session's handlers by reference.
    private static readonly AltaSessionInteractionDefaults UnattendedDefaults = new(
        static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>(StringComparer.Ordinal))));

    private static IAltaSessionInteractionDefaults InteractionDefaults(AltaCommandContext context)
        => context.Services.Get<IAltaSessionInteractionDefaults>() ?? UnattendedDefaults;

    private static SessionExecutionOptions BuildExecutionOptions(
        AltaCommandContext context,
        AltaModelSelection selection,
        string workingDirectory,
        IReadOnlyList<string> projectRoots,
        Func<string?>? sourceSessionIdProvider,
        string? sourceProjectId,
        string? promptId = null,
        string? worktreeDirectory = null,
        string? permissionMode = null)
        => new()
        {
            ProviderId = new ModelProviderId(selection.ProviderKey),
            ProviderKey = selection.ProviderKey,
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = selection.ModelId,
            ReasoningEffort = selection.ReasoningEffort,
            PermissionMode = permissionMode,
            AgentPromptId = NormalizeOptionalText(promptId),
            // The commands of the session resolve paths from the folder it works in.
            Tools = CreateAltaSessionTools(context, selection.ProviderKey, sourceSessionIdProvider, sourceProjectId, worktreeDirectory ?? workingDirectory, workingDirectory),
            OnPermissionRequest = InteractionDefaults(context).OnPermissionRequest,
            OnUserInputRequest = InteractionDefaults(context).OnUserInputRequest,
        };

    // completeModel: for a send. A send never leaves without a model while the provider of the session lists one, as
    // in the window: a session that has none starts with the provider's.
    private static async Task<SessionExecutionOptions> BuildExecutionOptionsForSessionAsync(AltaCommandContext context, AltaSessionInfo info, string? promptId, bool completeModel = false)
    {
        var modelId = info.Preference?.ModelId ?? info.Session.ModelId;
        var reasoning = info.Preference?.ReasoningEffort ?? info.Session.ReasoningEffort;
        if (completeModel && string.IsNullOrWhiteSpace(modelId))
        {
            (modelId, reasoning) = await FindStartingModelAsync(context, info.Session.ResolvedProviderKey, reasoning).ConfigureAwait(false);
        }

        var projectRoots = new List<string>();
        var workingDirectory = info.Session.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(info.Session.ProjectRef) && context.Services.Get<ProjectCatalog>() is { } catalog)
        {
            var project = await catalog.GetByIdAsync(info.Session.ProjectRef, context.CancellationToken).ConfigureAwait(false);
            if (project is not null)
            {
                workingDirectory = project.ProjectPath;
                projectRoots.Add(project.ProjectPath);
            }
        }

        return new SessionExecutionOptions
        {
            ProviderId = new ModelProviderId(info.Session.ResolvedProviderKey),
            ProviderKey = info.Session.ResolvedProviderKey,
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = modelId,
            ReasoningEffort = reasoning,
            AgentPromptId = NormalizeOptionalText(promptId) ?? NormalizeOptionalText(info.Session.AgentPromptId),
            // The session works where it records: its worktree while that folder exists, the folder of its project otherwise.
            Tools = CreateAltaSessionTools(context, info.Session.ProviderId, () => info.Session.SessionId, info.Session.ProjectRef,
                info.Session.WorktreeDirectory is { Length: > 0 } worktree && Directory.Exists(worktree) ? worktree : workingDirectory, workingDirectory),
            OnPermissionRequest = InteractionDefaults(context).OnPermissionRequest,
            OnUserInputRequest = InteractionDefaults(context).OnUserInputRequest,
        };
    }

    // The same call as the host's own sends, so both produce equal options for a session.
    private static async Task<PluginAugmentedRun> BuildPluginAgentRunAugmentationAsync(
        AltaCommandContext context,
        AltaSessionInfo info,
        SessionExecutionOptions executionOptions,
        AgentInput input)
        => context.Services.Get<PluginOrchestrationBridge>() is { } pluginBridge
            ? await context.ObserveOriginalAsync(pluginBridge.AugmentRunAsync(executionOptions, input, info.Session.ProjectRef, info.Session.SessionId,
                await SessionSpaceIdAsync(context, info.Session.SessionId).ConfigureAwait(false), context.CancellationToken)).ConfigureAwait(false)
            : new PluginAugmentedRun(executionOptions, input);

    // The space of a session, as the host resolves it for its own sends; null where the host keeps no runtime.
    private static async Task<string?> SessionSpaceIdAsync(AltaCommandContext context, string sessionId)
        => context.Services.Get<SessionRuntimeService>() is { } runtime
            ? await runtime.GetSessionSpaceIdAsync(sessionId, context.CancellationToken).ConfigureAwait(false)
            : null;

    private static IReadOnlyList<AgentToolDefinition>? CreateAltaSessionTools(
        AltaCommandContext context,
        string ProviderId,
        Func<string?>? sourceSessionIdProvider,
        string? sourceProjectId,
        string? workingDirectory,
        string? projectDirectory)
    {
        var policy = context.Services.Get<IAltaSessionToolProviderPolicy>();
        if (policy is not null && !policy.SupportsAltaSessionTool(ProviderId))
        {
            return null;
        }

        var dispatcher = context.Services.Get<AltaCommandDispatcher>()
            ?? new AltaCommandDispatcher(new AltaCommandRegistry(), context.Services);
        return
        [
            AltaSessionToolFactory.Create(
                dispatcher,
                new AltaSessionToolOptions
                {
                    SourceSessionIdProvider = sourceSessionIdProvider,
                    SourceProjectId = sourceProjectId,
                    WorkingDirectory = workingDirectory,
                    // The tool outlives a worktree that is removed: its commands then start from the folder of the project.
                    WorkingDirectoryProvider = workingDirectory is null || projectDirectory is null || string.Equals(workingDirectory, projectDirectory, StringComparison.Ordinal)
                        ? null
                        : () => Directory.Exists(workingDirectory) ? workingDirectory : projectDirectory,
                    DefaultMaxOutputRecords = 200,
                    DefaultMaxOutputBytes = 64 * 1024,
                    DefaultTimeout = TimeSpan.FromSeconds(120),
                }),
        ];
    }

    private static Task<ModelResolutionResult> ResolveModelSelectionAsync(AltaCommandContext context, AltaModelSelectionOptions request, string commandPath = "alta model resolve")
        => ResolveModelSelectionAsync(context, request, commandPath, projectRoot: null, sessionPreparation: false, spaceId: null, projectId: null);

    // projectRoot: the folder of the project a session is created in, whose configuration names the default provider first.
    private static async Task<ModelResolutionResult> ResolveModelSelectionAsync(AltaCommandContext context, AltaModelSelectionOptions request, string commandPath, string? projectRoot, bool sessionPreparation,
        string? spaceId, string? projectId)
    {
        if (request.Tier is { } tier)
        {
            if (!string.IsNullOrWhiteSpace(request.ModelRef) || !string.IsNullOrWhiteSpace(request.SameModelAsSessionId)
                || !string.IsNullOrWhiteSpace(request.ProviderKey) || !string.IsNullOrWhiteSpace(request.ModelId))
            {
                return ModelResolutionResult.Fail(UsageError(context, "usage.tierConflict",
                    "Use --tier alone, or name the model with --model-ref, --provider and --model, or --same-model-as. --reasoning may go with --tier.", commandPath));
            }

            return (await ResolveTierSelectionAsync(context, tier, request.ReasoningEffort, commandPath, sessionPreparation, required: true, spaceId, projectId).ConfigureAwait(false))!;
        }

        if (!string.IsNullOrWhiteSpace(request.ModelRef))
        {
            if (!AltaModelRef.TryParse(request.ModelRef, out var parsed, out var error))
            {
                return ModelResolutionResult.Fail(UsageError(context, "usage.invalidModelRef", error!, commandPath));
            }

            return await ValidateAndCompleteModelSelectionAsync(context, parsed, requestedReasoning: parsed.ReasoningEffort, commandPath, sessionPreparation).ConfigureAwait(false);
        }

        AltaModelSelection? inherited = null;
        if (!string.IsNullOrWhiteSpace(request.SameModelAsSessionId))
        {
            var inheritedResult = await ResolveSessionModelSelectionAsync(context, request.SameModelAsSessionId).ConfigureAwait(false);
            if (inheritedResult.ExitCode != AltaExitCodes.Success)
            {
                return ModelResolutionResult.Fail(inheritedResult.ExitCode);
            }

            inherited = inheritedResult.Selection;
            if (!inheritedResult.Found)
            {
                return ModelResolutionResult.Fail(NotFound(context, "session.notFound", $"Session '{request.SameModelAsSessionId}' was not found."));
            }
        }
        else if (!HasCompleteModelSelection(request) && !string.IsNullOrWhiteSpace(context.Caller.SourceSessionId))
        {
            var inheritedResult = await ResolveSessionModelSelectionAsync(context, context.Caller.SourceSessionId).ConfigureAwait(false);
            if (inheritedResult.ExitCode == AltaExitCodes.Success)
            {
                inherited = inheritedResult.Selection;
            }
        }

        // A session that names no model and inherits none starts on the standard tier, when the settings have tiers.
        if (inherited is null && string.IsNullOrWhiteSpace(request.ProviderKey) && string.IsNullOrWhiteSpace(request.ModelId)
            && await ResolveTierSelectionAsync(context, ModelTier.Standard, request.ReasoningEffort, commandPath, sessionPreparation, required: false, spaceId, projectId).ConfigureAwait(false) is { } standard)
        {
            return standard;
        }

        var providerKey = FirstNonEmpty(request.ProviderKey, inherited?.ProviderKey, GetDefaultProviderKey(context, projectRoot));
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return ModelResolutionResult.Fail(NotFound(context, "provider.notFound", "No provider is registered or selected."));
        }

        // A model is inherited with its provider: another provider is asked for its own.
        var sameProvider = inherited is not null && string.Equals(providerKey, inherited.ProviderKey, StringComparison.OrdinalIgnoreCase);
        var modelId = FirstNonEmpty(request.ModelId, sameProvider ? inherited!.ModelId : null);
        var reasoning = request.ReasoningEffort ?? inherited?.ReasoningEffort;
        var selection = new AltaModelSelection
        {
            ProviderKey = providerKey,
            ModelId = modelId,
            ReasoningEffort = reasoning,
            ModelRef = AltaModelRef.Format(providerKey, modelId, reasoning),
        };
        return await ValidateAndCompleteModelSelectionAsync(context, selection, request.ReasoningEffort, commandPath, sessionPreparation).ConfigureAwait(false);
    }

    // The first model of a tier whose provider is enabled and can answer, and lists the model when it lists models.
    // required: the tier was asked for, and its absence is an error; otherwise null lets the caller go on without it.
    private static async Task<ModelResolutionResult?> ResolveTierSelectionAsync(AltaCommandContext context, ModelTier tier,
        AgentReasoningEffort? requestedReasoning, string commandPath, bool sessionPreparation, bool required, string? spaceId, string? projectId)
    {
        // The tiers of the space of the session (its own, or the global ones), with the providers it has. Without a
        // session to create, the space is the one of the caller, else the one the window shows.
        var spaces = context.Services.Get<SpaceCatalog>();
        if (spaceId is null && spaces is not null)
        {
            spaceId = (await ResolveNewSessionSpaceAsync(context, null, commandPath).ConfigureAwait(false)).SpaceId;
        }

        var scope = TierConfigStore(context) is { } store
            ? await ModelTierScope.LoadAsync(store, spaces, spaceId, projectId, context.CancellationToken).ConfigureAwait(false)
            : ModelTierScope.None;
        var tiers = scope.Tiers;
        var name = ModelTiers.NameOf(tier);
        if (tiers.Get(tier).Count == 0)
        {
            return required
                ? ModelResolutionResult.Fail(UsageError(context, "usage.noTier",
                    $"The {name} tier has no model: the user sets the tiers in Settings > Models. Name a model with --model-ref instead.", commandPath))
                : null;
        }

        var states = context.Services.Get<IModelProviderInitializationService>()?.CurrentStates ?? [];
        foreach (var target in tiers.Get(tier))
        {
            if (!scope.IsProviderAvailable(target.ProviderKey)) continue;
            if (FindProviderDescriptor(context, target.ProviderKey) is not { IsEnabled: true }) continue;
            var state = states.FirstOrDefault(state => string.Equals(state.ProviderId.Value, target.ProviderKey, StringComparison.OrdinalIgnoreCase));
            // A provider that was not probed yet may still answer; one that is known not to is skipped.
            if (state is not null && state.Availability is not (ModelProviderAvailability.Ready or ModelProviderAvailability.Unknown or ModelProviderAvailability.Probing)) continue;
            var model = state?.Models.FirstOrDefault(model => string.Equals(model.Id, target.ModelId, StringComparison.OrdinalIgnoreCase));
            if (state is { Availability: ModelProviderAvailability.Ready, Models.Count: > 0 } && model is null) continue;
            var reasoning = requestedReasoning
                ?? (model?.SupportedReasoningEfforts is { Count: > 0 } supported && target.Reasoning.Count > 0
                    ? target.Reasoning.FirstOrDefault(supported.Contains, target.Reasoning[0])
                    : target.PreferredReasoning);
            var selection = new AltaModelSelection
            {
                ProviderKey = target.ProviderKey,
                ModelId = target.ModelId,
                ReasoningEffort = reasoning,
                ModelRef = AltaModelRef.Format(target.ProviderKey, target.ModelId, reasoning),
            };
            var result = await ValidateAndCompleteModelSelectionAsync(context, selection, requestedReasoning, commandPath, sessionPreparation).ConfigureAwait(false);
            return result with { Tier = tier };
        }

        return required
            ? ModelResolutionResult.Fail(NotFound(context, "tier.unavailable",
                $"No model of the {name} tier is ready ({string.Join(", ", tiers.Get(tier).Select(static target => target.Format()))}). Name a model with --model-ref instead."))
            : null;
    }

    // The configuration the tiers are read from; null when the host has none.
    private static CodeAltaConfigStore? TierConfigStore(AltaCommandContext context)
        => context.Services.Get<CodeAltaConfigStore>()
           ?? (context.Services.Get<CatalogOptions>() is { } options ? new CodeAltaConfigStore(options) : null);

    private static bool HasCompleteModelSelection(AltaModelSelectionOptions request)
        => !string.IsNullOrWhiteSpace(request.ProviderKey) &&
           !string.IsNullOrWhiteSpace(request.ModelId) &&
           request.ReasoningEffort is not null;

    private static async Task<ModelResolutionResult> ValidateAndCompleteModelSelectionAsync(AltaCommandContext context, AltaModelSelection selection, AgentReasoningEffort? requestedReasoning, string commandPath, bool sessionPreparation)
    {
        if (context.Services.Get<IModelProviderInitializationService>() is not { } providerInitializationService)
        {
            return ModelResolutionResult.Success(selection);
        }

        IReadOnlyList<AgentModelInfo> models;
        try
        {
            models = sessionPreparation
                ? await GetPreparationModelsAsync(context, providerInitializationService, new ModelProviderId(selection.ProviderKey)).ConfigureAwait(false)
                : await providerInitializationService.GetModelsAsync(new ModelProviderId(selection.ProviderKey), context.CancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return ModelResolutionResult.Fail(NotFound(context, "provider.notFound", $"Provider '{selection.ProviderKey}' was not found."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "model.validationUnavailable", $"Provider '{selection.ProviderKey}' models are unavailable: {ex.Message}");
            return ModelResolutionResult.Success(selection);
        }

        if (string.IsNullOrWhiteSpace(selection.ModelId))
        {
            // No model was named or inherited: the session starts with the provider's, as one created in the window.
            var configured = FindProviderDescriptor(context, selection.ProviderKey);
            var (startingModelId, startingReasoning) = StartingModel(models, configured, selection.ReasoningEffort);
            startingModelId ??= NormalizeOptionalText(configured?.DefaultModelId);
            if (startingModelId is null)
            {
                return ModelResolutionResult.Fail(UsageError(context, "usage.missingModel",
                    $"Provider '{selection.ProviderKey}' lists no model and has no default one: name a model with --model or --model-ref.", commandPath));
            }

            var starting = models.FirstOrDefault(candidate => string.Equals(candidate.Id, startingModelId, StringComparison.Ordinal));
            if (starting is not null && requestedReasoning is { } asked && !ModelSupportsReasoning(starting, asked))
            {
                return ModelResolutionResult.Fail(UsageError(context, "usage.unsupportedReasoning", $"Reasoning effort '{AltaModelRef.ToWireName(asked)}' is not supported by model '{startingModelId}' for provider '{selection.ProviderKey}'.", commandPath));
            }

            // An effort that was asked and is not refused is the one of the session.
            startingReasoning = requestedReasoning ?? startingReasoning;
            return ModelResolutionResult.Success(selection with
            {
                ModelId = startingModelId,
                ReasoningEffort = startingReasoning,
                ModelRef = AltaModelRef.Format(selection.ProviderKey, startingModelId, startingReasoning),
            });
        }

        var model = models.FirstOrDefault(candidate => string.Equals(candidate.Id, selection.ModelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return ModelResolutionResult.Fail(NotFound(context, "model.notFound", $"Model '{selection.ModelId}' was not found for provider '{selection.ProviderKey}'."));
        }

        if (requestedReasoning is { } requested && !ModelSupportsReasoning(model, requested))
        {
            return ModelResolutionResult.Fail(UsageError(context, "usage.unsupportedReasoning", $"Reasoning effort '{AltaModelRef.ToWireName(requested)}' is not supported by model '{selection.ModelId}' for provider '{selection.ProviderKey}'.", commandPath));
        }

        var effectiveReasoning = selection.ReasoningEffort ?? model.DefaultReasoningEffort;
        return ModelResolutionResult.Success(selection with
        {
            ModelId = model.Id,
            ReasoningEffort = effectiveReasoning,
            ModelRef = AltaModelRef.Format(selection.ProviderKey, model.Id, effectiveReasoning),
        });
    }

    // What a session without a model starts with among what its provider lists, as the window completes it: the model
    // configured for the provider when it is listed, else the first one listed. A model that reports its efforts takes
    // the given effort when it supports it, else the provider's configured effort, High, its own default or its first.
    private static (string? ModelId, AgentReasoningEffort? Effort) StartingModel(
        IReadOnlyList<AgentModelInfo> models, ModelProviderDescriptor? configured, AgentReasoningEffort? effort)
    {
        var modelId = AgentModelDefaults.ResolveModelId(models, configured?.DefaultModelId);
        var model = models.FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.Ordinal));
        if (model?.SupportedReasoningEfforts is not { Count: > 0 } supported)
        {
            return (modelId, effort);
        }

        return (modelId, AgentModelDefaults.ResolveReasoningEffort(model,
            effort is { } given && supported.Contains(given) ? given : configured?.DefaultReasoningEffort));
    }

    // For a send: the model is left out when the provider cannot list its models, and the provider then decides.
    private static async Task<(string? ModelId, AgentReasoningEffort? Effort)> FindStartingModelAsync(
        AltaCommandContext context, string providerKey, AgentReasoningEffort? effort)
    {
        if (context.Services.Get<IModelProviderInitializationService>() is not { } providerInitializationService)
        {
            return (null, effort);
        }

        try
        {
            var models = await GetPreparationModelsAsync(context, providerInitializationService, new ModelProviderId(providerKey)).ConfigureAwait(false);
            return StartingModel(models, FindProviderDescriptor(context, providerKey), effort);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return (null, effort);
        }
    }

    private static async Task<IReadOnlyList<AgentModelInfo>> GetPreparationModelsAsync(
        AltaCommandContext context, IModelProviderInitializationService providers, ModelProviderId providerId)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        // Model initialization shares a refresh worker and normally returns a cancelable waiter.
        // Session preparation owns retention until that original refresh actually ends.
        var models = await context.ObserveOriginalAsync(providers.GetModelsAsync(providerId, CancellationToken.None)).ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();
        return models;
    }

    private static ModelProviderDescriptor? FindProviderDescriptor(AltaCommandContext context, string providerKey)
        => GetProviderDescriptors(context).FirstOrDefault(descriptor => string.Equals(descriptor.ProviderId.Value, providerKey, StringComparison.OrdinalIgnoreCase));

    private static async Task<SessionModelSelectionResult> ResolveSessionModelSelectionAsync(AltaCommandContext context, string sessionId)
    {
        if (context.Services.Get<SessionRuntimeService>() is not { } runtime)
        {
            return SessionModelSelectionResult.NotFound();
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SessionViewDescriptor? session = null;
        AltaModelSelection? selection = null;
        var currentSessionId = sessionId;
        while (!string.IsNullOrWhiteSpace(currentSessionId) && visited.Add(currentSessionId))
        {
            session = await runtime.TryGetActiveSessionDescriptorAsync(currentSessionId, context.CancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                break;
            }

            var candidate = CreateModelSelection(session, preference: null);
            selection = MergeModelSelection(selection, candidate);
            if (!string.IsNullOrWhiteSpace(selection.ProviderKey) &&
                !string.IsNullOrWhiteSpace(selection.ModelId) &&
                selection.ReasoningEffort is not null)
            {
                break;
            }

            currentSessionId = session.ParentSessionId;
        }

        return selection is null
            ? SessionModelSelectionResult.NotFound()
            : SessionModelSelectionResult.Success(selection);
    }

    // The provider of a session that names none and inherits none, as the rest of the application picks it: the
    // default provider of the configuration (of the project, then of the user) when it is enabled, otherwise the
    // first enabled provider.
    private static string? GetDefaultProviderKey(AltaCommandContext context, string? projectRoot)
    {
        var descriptors = GetProviderDescriptors(context);
        var enabled = descriptors.Where(static descriptor => descriptor.IsEnabled).ToArray();
        if (GetConfiguredDefaultProvider(context, projectRoot) is { } configured &&
            enabled.FirstOrDefault(descriptor => string.Equals(descriptor.ProviderId.Value, configured, StringComparison.OrdinalIgnoreCase)) is { } chosen)
        {
            return chosen.ProviderId.Value;
        }

        return (enabled.FirstOrDefault() ?? descriptors.FirstOrDefault())?.ProviderId.Value;
    }

    private static string? GetConfiguredDefaultProvider(AltaCommandContext context, string? projectRoot)
    {
        var store = context.Services.Get<CodeAltaConfigStore>()
            ?? (context.Services.Get<CatalogOptions>() is { } options ? new CodeAltaConfigStore(options) : null);
        if (store is null)
        {
            return null;
        }

        try
        {
            return store.GetEffectiveDefaultProvider(projectRoot);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A configuration that cannot be read names no default.
            return null;
        }
    }

    private static IReadOnlyList<ModelProviderDescriptor> GetProviderDescriptors(AltaCommandContext context)
        => context.Services.Get<IReadOnlyList<ModelProviderDescriptor>>() ?? [];

    private static AltaModelSelection CreateModelSelection(SessionViewDescriptor session, SessionViewPreference? preference)
    {
        var providerKey = session.ResolvedProviderKey;
        var modelId = preference?.ModelId ?? session.ModelId;
        var reasoning = preference?.ReasoningEffort ?? session.ReasoningEffort;
        return new AltaModelSelection
        {
            ProviderKey = providerKey,
            ModelId = modelId,
            ReasoningEffort = reasoning,
            ModelRef = AltaModelRef.Format(providerKey, modelId, reasoning),
        };
    }

    private static AltaModelSelection MergeModelSelection(AltaModelSelection? primary, AltaModelSelection fallback)
    {
        if (primary is null)
        {
            return fallback;
        }

        var providerKey = FirstNonEmpty(primary.ProviderKey, fallback.ProviderKey) ?? fallback.ProviderKey;
        var modelId = FirstNonEmpty(primary.ModelId, fallback.ModelId);
        var reasoning = primary.ReasoningEffort ?? fallback.ReasoningEffort;
        return new AltaModelSelection
        {
            ProviderKey = providerKey,
            ModelId = modelId,
            ReasoningEffort = reasoning,
            ModelRef = AltaModelRef.Format(providerKey, modelId, reasoning),
        };
    }

    private static async Task<SkillQueryResult> BuildSkillQueryAsync(AltaCommandContext context, string? projectRef)
    {
        ProjectDescriptor? project = null;
        var sourceProjectId = context.Caller.SourceProjectId;
        if (!string.IsNullOrWhiteSpace(projectRef))
        {
            if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
            {
                return SkillQueryResult.Fail(AltaExitCodes.ServiceUnavailable);
            }

            project = await ResolveProjectAsync(catalog, projectRef, context, includeArchived: false).ConfigureAwait(false);
            if (project is null)
            {
                return SkillQueryResult.Fail(NotFound(context, "project.notFound", $"Project '{projectRef}' was not found."));
            }
        }
        else if (!string.IsNullOrWhiteSpace(sourceProjectId))
        {
            if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
            {
                return SkillQueryResult.Fail(AltaExitCodes.ServiceUnavailable);
            }

            project = await ResolveProjectAsync(catalog, sourceProjectId, context, includeArchived: false).ConfigureAwait(false);
            if (project is null)
            {
                return SkillQueryResult.Fail(NotFound(context, "project.notFound", $"Project '{sourceProjectId}' was not found."));
            }
        }

        var catalogOptions = context.Services.Get<CatalogOptions>();
        var globalRoot = catalogOptions?.GlobalRoot ?? GetGlobalRootOrCwd(context);
        var configStore = context.Services.Get<CodeAltaConfigStore>() ?? new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = globalRoot });
        // A session sees the skills of its own space; the user, outside any session, sees every skill.
        var space = await CallerSpaceFilterAsync(context).ConfigureAwait(false);
        var query = SkillCatalogQuery.ForSession(
            configStore,
            globalRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            project?.ProjectPath,
            otherProjectRoots: null,
            space);
        return SkillQueryResult.Success(query);
    }

    private static async Task<PromptQueryResult> BuildPromptQueryAsync(AltaCommandContext context, string? projectRef)
    {
        var projectRoot = await ResolvePromptProjectRootAsync(context, projectRef).ConfigureAwait(false);
        if (projectRoot.ExitCode != AltaExitCodes.Success)
        {
            return PromptQueryResult.Fail(projectRoot.ExitCode);
        }

        return PromptQueryResult.Success(new AgentPromptCatalogQuery
        {
            ProjectRoot = projectRoot.ProjectRoot,
            ProjectPromptResourcesTrusted = !string.IsNullOrWhiteSpace(projectRoot.ProjectRoot),
            UserCodeAltaRoot = context.Services.Get<CatalogOptions>()?.GlobalRoot ?? GetGlobalRootOrCwd(context),
            UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        });
    }

    private static async Task<PromptProjectRootResult> ResolvePromptProjectRootAsync(AltaCommandContext context, string? projectRef)
    {
        if (!string.IsNullOrWhiteSpace(projectRef))
        {
            if (context.Services.Get<ProjectCatalog>() is { } catalog)
            {
                var project = await ResolveProjectAsync(catalog, projectRef, context, includeArchived: false).ConfigureAwait(false);
                if (project is not null)
                {
                    return PromptProjectRootResult.Success(project.ProjectPath);
                }
            }

            var path = ResolvePath(context, projectRef);
            return Directory.Exists(path)
                ? PromptProjectRootResult.Success(path)
                : PromptProjectRootResult.Fail(NotFound(context, "project.notFound", $"Project '{projectRef}' was not found."));
        }

        if (!string.IsNullOrWhiteSpace(context.Caller.SourceProjectId) && context.Services.Get<ProjectCatalog>() is { } sourceCatalog)
        {
            var project = await ResolveProjectAsync(sourceCatalog, context.Caller.SourceProjectId, context, includeArchived: false).ConfigureAwait(false);
            if (project is not null)
            {
                return PromptProjectRootResult.Success(project.ProjectPath);
            }
        }

        var cwd = context.Cwd ?? Environment.CurrentDirectory;
        if (context.Services.Get<ProjectCatalog>() is { } cwdCatalog)
        {
            var projects = await cwdCatalog.LoadAsync(context.CancellationToken).ConfigureAwait(false);
            var normalizedCwd = NormalizePath(cwd);
            var project = projects
                .Where(static project => !project.Archived)
                .OrderByDescending(static project => NormalizePath(project.ProjectPath).Length)
                .FirstOrDefault(project => IsSameOrDescendantPath(normalizedCwd, NormalizePath(project.ProjectPath)));
            if (project is not null)
            {
                return PromptProjectRootResult.Success(project.ProjectPath);
            }
        }

        return Directory.Exists(cwd) ? PromptProjectRootResult.Success(cwd) : PromptProjectRootResult.Success(null);
    }

    private static bool IsSameOrDescendantPath(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rooted = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(rooted, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> ValidateSessionAgentPromptIdAsync(AltaCommandContext context, AltaSessionInfo info, string? promptId)
    {
        var queryResult = await BuildPromptQueryAsync(context, info.Session.ProjectRef).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var normalized = NormalizeOptionalText(promptId);
        if (normalized is null)
        {
            return AltaExitCodes.Success;
        }

        // A session runs only the prompts of its own space, whoever asks for one.
        var space = context.Services.Get<SessionRuntimeService>() is { } runtime
            ? await runtime.GetSessionSpaceFilterAsync(info.Session.SessionId, context.CancellationToken).ConfigureAwait(false)
            : null;
        return ValidateAgentPromptId(context, new AgentPromptCatalog().ListEffectivePrompts(queryResult.Query!), normalized, space);
    }

    // The prompt of a new session: the one asked for, which must exist in the space of the session, else the one the
    // space starts its sessions with, else `default`.
    private static (string? PromptId, int ExitCode) ResolveNewSessionPrompt(
        AltaCommandContext context, string? projectRoot, string? requested, SpaceDescriptor? space)
    {
        var globalRoot = context.Services.Get<CatalogOptions>()?.GlobalRoot ?? GetGlobalRootOrCwd(context);
        var query = new AgentPromptCatalogQuery
        {
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = !string.IsNullOrWhiteSpace(projectRoot),
            UserCodeAltaRoot = globalRoot,
            UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        var resources = SpaceFilter.Load(context.Services.Get<CodeAltaConfigStore>() ?? new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = globalRoot }), space?.Id);
        var prompts = new AgentPromptCatalog().ListEffectivePrompts(query);
        var available = AgentPromptCatalog.InSpace(prompts, resources).Select(static prompt => prompt.PromptName).ToArray();
        var promptId = AgentPromptCatalog.ResolveNewSessionPromptId(requested, available, space);
        var exitCode = ValidateAgentPromptId(context, prompts, promptId, resources);
        return exitCode == AltaExitCodes.Success ? (promptId, exitCode) : (null, exitCode);
    }

    // Refuses a prompt that does not exist (prompt.notFound) or that the space does not have (prompt.notInSpace).
    private static int ValidateAgentPromptId(AltaCommandContext context, IReadOnlyList<AgentPromptDescriptor> prompts, string promptId, SpaceFilter? space)
    {
        var prompt = prompts.FirstOrDefault(candidate => string.Equals(candidate.PromptName, promptId, StringComparison.OrdinalIgnoreCase));
        if (prompt is null)
        {
            return NotFound(context, "prompt.notFound", $"Agent prompt '{promptId}' was not found. Use `alta prompt list` to discover prompt ids.");
        }

        return space is null || space.Allows(SpaceResourceKind.Prompt, prompt.PromptName)
            ? AltaExitCodes.Success
            : Unsupported(context, "prompt.notInSpace", ResourceNotInSpaceException.MessageOf(SpaceResourceKind.Prompt, prompt.PromptName, space.SpaceId));
    }

    private static Dictionary<string, object?> CreateAgentPromptRecord(AltaCommandContext context, AgentPromptDescriptor prompt, bool includeContent)
    {
        var record = CreatePromptRecord(context, prompt.PromptName, prompt.DisplayName, prompt.Description, "agent", prompt.SourceKind, prompt.SourcePath, prompt.ContentHash, prompt.IsShadowed, prompt.ShadowedByPath);
        record["systemPromptId"] = prompt.SystemPromptName;
        record["mode"] = ToPromptCompositionModeLabel(prompt.Mode);
        if (includeContent)
        {
            record["content"] = prompt.Body;
        }

        return record;
    }

    private static Dictionary<string, object?> CreateSystemPromptRecord(AltaCommandContext context, SystemPromptDescriptor prompt, bool includeContent)
    {
        var metadata = ReadPromptMetadata(prompt.SourcePath);
        var record = CreatePromptRecord(context, prompt.PromptName, metadata.Name ?? prompt.PromptName, metadata.Description, "system", prompt.SourceKind, prompt.SourcePath, prompt.ContentHash, prompt.IsShadowed, prompt.ShadowedByPath);
        record["mode"] = ToPromptCompositionModeLabel(prompt.Mode);
        if (includeContent)
        {
            record["content"] = prompt.Body;
        }

        return record;
    }

    private static Dictionary<string, object?> CreatePromptRecord(AltaCommandContext context, string promptId, string name, string? description, string promptKind, AgentPromptSourceKind sourceKind, string sourcePath, string contentHash, bool isShadowed, string? shadowedByPath)
        => new(StringComparer.Ordinal)
        {
            ["type"] = "alta.prompt",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["promptId"] = promptId,
            ["id"] = promptId,
            ["name"] = name,
            ["description"] = description,
            ["promptKind"] = promptKind,
            ["source"] = ToPromptSourceLabel(sourceKind),
            ["scope"] = ToPromptScopeLabel(sourceKind),
            ["path"] = sourcePath,
            ["contentHash"] = contentHash,
            ["shadowed"] = isShadowed,
            ["shadowedByPath"] = shadowedByPath,
        };

    private static PromptMetadata ReadPromptMetadata(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            {
                return PromptMetadata.Empty;
            }

            var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end < 0)
            {
                return PromptMetadata.Empty;
            }

            string? name = null;
            string? description = null;
            foreach (var rawLine in normalized[4..end].Split('\n'))
            {
                var line = rawLine.Trim();
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                var key = line[..colon].Trim();
                var value = NormalizeOptionalText(line[(colon + 1)..].Trim().Trim('"', '\''));
                if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                {
                    name = value;
                }
                else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
                {
                    description = value;
                }
            }

            return new PromptMetadata(name, description);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PromptMetadata.Empty;
        }
    }

    private static bool ScopeMatches(string? scope, AgentPromptSourceKind sourceKind)
        => (scope ?? "all") switch
        {
            "all" => true,
            "builtin" => sourceKind == AgentPromptSourceKind.BuiltIn,
            "global" => sourceKind == AgentPromptSourceKind.UserGlobal,
            "project" => sourceKind == AgentPromptSourceKind.Project,
            _ => true,
        };

    private static TPrompt? SelectPromptForShow<TPrompt>(IEnumerable<TPrompt> prompts, string promptId, string? scope)
        where TPrompt : class
    {
        var matches = prompts
            .Where(prompt => string.Equals(GetPromptName(prompt), promptId, StringComparison.OrdinalIgnoreCase) && ScopeMatches(scope, GetPromptSourceKind(prompt)))
            .OrderBy(prompt => GetPromptPrecedence(prompt))
            .ToArray();
        return (scope ?? "all") == "all"
            ? matches.LastOrDefault(prompt => !GetPromptIsShadowed(prompt)) ?? matches.LastOrDefault()
            : matches.LastOrDefault();
    }

    private static string GetPromptName<TPrompt>(TPrompt prompt)
        => prompt switch
        {
            AgentPromptDescriptor agentPrompt => agentPrompt.PromptName,
            SystemPromptDescriptor systemPrompt => systemPrompt.PromptName,
            _ => string.Empty,
        };

    private static AgentPromptSourceKind GetPromptSourceKind<TPrompt>(TPrompt prompt)
        => prompt switch
        {
            AgentPromptDescriptor agentPrompt => agentPrompt.SourceKind,
            SystemPromptDescriptor systemPrompt => systemPrompt.SourceKind,
            _ => AgentPromptSourceKind.BuiltIn,
        };

    private static int GetPromptPrecedence<TPrompt>(TPrompt prompt)
        => prompt switch
        {
            AgentPromptDescriptor agentPrompt => agentPrompt.Precedence,
            SystemPromptDescriptor systemPrompt => systemPrompt.Precedence,
            _ => 0,
        };

    private static bool GetPromptIsShadowed<TPrompt>(TPrompt prompt)
        => prompt switch
        {
            AgentPromptDescriptor agentPrompt => agentPrompt.IsShadowed,
            SystemPromptDescriptor systemPrompt => systemPrompt.IsShadowed,
            _ => false,
        };

    private static string ToPromptSourceLabel(AgentPromptSourceKind sourceKind)
        => sourceKind switch
        {
            AgentPromptSourceKind.BuiltIn => "built-in",
            AgentPromptSourceKind.UserGlobal => "user-global",
            AgentPromptSourceKind.Project => "project",
            AgentPromptSourceKind.CopilotUser => "copilot-user",
            AgentPromptSourceKind.CopilotProject => "copilot-project",
            _ => sourceKind.ToString(),
        };

    private static string ToPromptCompositionModeLabel(PromptCompositionMode mode)
        => mode == PromptCompositionMode.Append ? "append" : "replace";

    private static string ToPromptScopeLabel(AgentPromptSourceKind sourceKind)
        => sourceKind switch
        {
            AgentPromptSourceKind.BuiltIn => "builtin",
            AgentPromptSourceKind.UserGlobal => "global",
            AgentPromptSourceKind.Project => "project",
            AgentPromptSourceKind.CopilotUser or AgentPromptSourceKind.CopilotProject => "copilot",
            _ => sourceKind.ToString().ToLowerInvariant(),
        };

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<ProjectDescriptor?> ResolveProjectAsync(ProjectCatalog catalog, string? reference, AltaCommandContext context, bool includeArchived)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var projects = await catalog.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var candidates = includeArchived ? projects : projects.Where(static project => !project.Archived).ToArray();
        var trimmed = reference.Trim();
        var project = candidates.FirstOrDefault(project => string.Equals(project.Id, trimmed, StringComparison.OrdinalIgnoreCase)) ??
                      candidates.FirstOrDefault(project => string.Equals(project.Slug, trimmed, StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            return project;
        }

        try
        {
            var path = ResolvePath(context, trimmed);
            return candidates.FirstOrDefault(project => string.Equals(NormalizePath(project.ProjectPath), NormalizePath(path), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) when (!LooksLikePath(trimmed))
        {
            return null;
        }
    }

    private static bool LooksLikePath(string value)
        => Path.IsPathRooted(value) ||
           value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
           value.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
           value is "." or ".." ||
           value.StartsWith(".", StringComparison.Ordinal);

    private static string ResolvePath(AltaCommandContext context, string path)
    {
        var basePath = context.Cwd;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Environment.CurrentDirectory;
        }

        return Path.IsPathRooted(path)
            ? NormalizePath(path)
            : NormalizePath(Path.Combine(basePath, path));
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = @"\\" + trimmed[8..];
        }
        else if (trimmed.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[4..];
        }

        return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static async Task<PromptReadResult> ReadPromptAsync(AltaCommandContext context, PromptOptions options, PromptDispatchKind kind)
    {
        if (!string.IsNullOrWhiteSpace(options.Message) && options.UseStdin)
        {
            return PromptReadResult.Fail(UsageError(context, "usage.messageConflict", "Use either --message or --stdin, not both.", CommandPathForPrompt(kind)));
        }

        if (!string.IsNullOrWhiteSpace(options.Message))
        {
            return PromptReadResult.Success(options.Message);
        }

        if (options.UseStdin)
        {
            return PromptReadResult.Success(await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false));
        }

        return PromptReadResult.Fail(UsageError(context, "usage.missingMessage", "A message is required. Use --message <text> or --stdin.", CommandPathForPrompt(kind)));
    }

    private static async Task<PromptReadResult> ReadReminderContentAsync(AltaCommandContext context, ReminderCreateOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Content) && options.UseStdin)
        {
            return PromptReadResult.Fail(UsageError(context, "usage.contentConflict", "Use either --content or --stdin, not both.", "alta reminder create"));
        }

        if (!string.IsNullOrWhiteSpace(options.Content))
        {
            return PromptReadResult.Success(options.Content);
        }

        if (options.UseStdin)
        {
            var content = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(content)
                ? PromptReadResult.Fail(UsageError(context, "usage.missingContent", "Reminder content is required. Provide non-empty --content text or non-empty --stdin input.", "alta reminder create"))
                : PromptReadResult.Success(content);
        }

        return PromptReadResult.Fail(UsageError(context, "usage.missingContent", "Reminder content is required. Use --content <text> or --stdin.", "alta reminder create"));
    }

    private static bool TryParseReminderDuration(string? value, out TimeSpan duration, out string error)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "--duration is required.";
            return false;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            if (seconds > 0 && !double.IsInfinity(seconds) && !double.IsNaN(seconds))
            {
                try
                {
                    duration = TimeSpan.FromSeconds(seconds);
                    error = string.Empty;
                    return true;
                }
                catch (OverflowException)
                {
                    error = "--duration is too large.";
                    return false;
                }
            }

            error = "--duration must be greater than zero seconds.";
            return false;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero)
        {
            duration = parsed;
            error = string.Empty;
            return true;
        }

        error = "--duration must be a positive number of seconds or a positive TimeSpan such as 00:01:00.";
        return false;
    }

    private static bool TryParseReminderRepeat(string? value, out int repeat, out string error)
    {
        repeat = 1;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = string.Empty;
            return true;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out repeat) && repeat > 0)
        {
            error = string.Empty;
            return true;
        }

        repeat = 1;
        error = "--repeat must be a positive integer.";
        return false;
    }

    private static string CommandPathForPrompt(PromptDispatchKind kind)
        => kind switch
        {
            PromptDispatchKind.Steer => "alta session steer",
            PromptDispatchKind.Queue => "alta session queue",
            PromptDispatchKind.Message => "alta session message",
            PromptDispatchKind.Request => "alta session request",
            _ => "alta session send",
        };

    private static async Task PersistSessionPreferenceAsync(AltaCommandContext context, SessionViewDescriptor session, AltaModelSelection selection, string? promptId)
    {
        if (context.Services.Get<SessionViewCatalog>() is not { } sessionCatalog)
        {
            return;
        }

        var state = await sessionCatalog.JournalStore
            .ReadLatestStateAsync(session.SessionId, session.CreatedAt, context.CancellationToken)
            .ConfigureAwait(false) ?? new SessionViewLocalState();
        state.ProviderKey = selection.ProviderKey;
        state.ModelId = selection.ModelId;
        state.ReasoningEffort = selection.ReasoningEffort;
        state.AgentPromptId = NormalizeOptionalText(promptId) ?? NormalizeOptionalText(session.AgentPromptId) ?? AgentPromptCatalog.DefaultPromptName;
        await sessionCatalog.JournalStore.AppendStateAsync(session, state, context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task<SessionViewLocalState?> TryReadLatestSessionStateAsync(
        SessionViewCatalog sessionCatalog,
        SessionViewDescriptor session,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sessionCatalog.JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task PersistPromptProvenanceAsync(
        AltaCommandContext context,
        SessionViewDescriptor session,
        string? runId,
        bool queued,
        PromptDispatchKind kind,
        string prompt)
    {
        if (context.Services.Get<SessionViewCatalog>() is not { } sessionCatalog)
        {
            return;
        }

        SessionViewLocalState? localState = null;
        try
        {
            localState = await sessionCatalog.JournalStore
                .ReadLatestStateAsync(session.SessionId, session.CreatedAt, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
        }

        localState ??= new SessionViewLocalState();
        localState.ProviderKey = session.ResolvedProviderKey;
        localState.ModelId = session.ModelId;
        localState.ReasoningEffort = session.ReasoningEffort;
        localState.MessageCount = session.MessageCount;
        localState.ParentSessionId = session.ParentSessionId;
        localState.CreatedBy = session.CreatedBy;
        localState.PromptProvenance ??= [];
        localState.PromptProvenance.Add(new SessionViewPromptProvenance
        {
            PromptId = "prompt-" + Guid.NewGuid().ToString("N"),
            Kind = kind.ToString().ToLowerInvariant(),
            RunId = runId,
            Queued = queued,
            PromptPreview = prompt.Length <= 160 ? prompt : prompt[..160],
            SubmittedBy = CreateProvenance(context),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        const int MaxPromptProvenanceRecords = 200;
        if (localState.PromptProvenance.Count > MaxPromptProvenanceRecords)
        {
            localState.PromptProvenance.RemoveRange(0, localState.PromptProvenance.Count - MaxPromptProvenanceRecords);
        }

        await sessionCatalog.JournalStore.AppendStateAsync(session, localState, context.CancellationToken).ConfigureAwait(false);
    }

    private static bool IsAgentCaller(AltaCommandContext context)
        => string.Equals(context.Caller.Kind, "agent", StringComparison.OrdinalIgnoreCase);

    private static bool IsTargetingCallingAgentSession(AltaCommandContext context, string sessionId)
        => IsAgentCaller(context) &&
           !string.IsNullOrWhiteSpace(context.Caller.SourceSessionId) &&
           string.Equals(context.Caller.SourceSessionId.Trim(), sessionId.Trim(), StringComparison.OrdinalIgnoreCase);

    // A caller that is not a person at the prompt hands the prompt over and goes on: a command that waited for the
    // run would end it when the command reaches its time limit.
    private static bool ShouldDetachPromptSubmission(AltaCommandContext context)
        => IsAgentCaller(context) || string.Equals(context.Caller.Kind, "reminder", StringComparison.OrdinalIgnoreCase)
           || string.Equals(context.Caller.Kind, "mcp", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> WaitForAgentSubmissionAckAsync(
        SessionRuntimeService runtime,
        SessionViewDescriptor session,
        Task<AgentRunId> sendTask)
    {
        var deadline = DateTimeOffset.UtcNow + AgentCallerSubmitAckTimeout;
        while (!sendTask.IsCompleted)
        {
            if (await runtime.HasActiveRunAsync(session, CancellationToken.None).ConfigureAwait(false))
            {
                return true;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return true;
            }

            var delay = remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100);
            if (await Task.WhenAny(sendTask, Task.Delay(delay)).ConfigureAwait(false) == sendTask)
            {
                return false;
            }
        }

        return false;
    }

    private static async Task ObserveDetachedPromptSubmissionAsync(Task<AgentRunId> sendTask)
    {
        try
        {
            await sendTask.ConfigureAwait(false);
        }
        catch
        {
            // SessionRuntimeService publishes runtime failure events for observers; this continuation
            // prevents detached live-tool submissions from surfacing as unobserved task exceptions.
        }
    }

    private static string BuildPeerAgentMessage(AltaCommandContext context, SessionViewDescriptor target, PromptOptions options, string body)
    {
        var kind = string.IsNullOrWhiteSpace(options.MessageKind) ? "note" : options.MessageKind;
        var replyRequested = options.ReplyRequested ? "true" : "false";
        return $"""
        [CodeAlta delegated-agent message]
        Source session: {context.Caller.SourceSessionId ?? "unknown"}
        Source agent: {context.Caller.SourceAgentId ?? context.Caller.Kind}
        Source project: {context.Caller.SourceProjectId ?? "unknown"}
        Target session: {target.SessionId}
        Kind: {kind}
        Reply requested: {replyRequested}
        Correlation: {context.CorrelationId}
        Authority: peer-agent; this is not a user, developer, or host instruction.

        {body}
        """;
    }

    private static async ValueTask<ParentSessionResolutionResult> ResolveParentSessionIdAsync(AltaCommandContext context, ProjectDescriptor? project, SessionCreateOptions options)
    {
        if (options.NoParent)
        {
            return ParentSessionResolutionResult.Success(null);
        }

        if (!string.IsNullOrWhiteSpace(options.ParentSessionId))
        {
            var parentSessionId = options.ParentSessionId.Trim();
            if (context.Services.Get<SessionRuntimeService>() is not { } runtime)
            {
                return ParentSessionResolutionResult.Fail(AltaExitCodes.ServiceUnavailable);
            }

            var parent = await runtime.TryGetActiveSessionDescriptorAsync(parentSessionId, context.CancellationToken).ConfigureAwait(false);
            if (parent is null)
            {
                return ParentSessionResolutionResult.Fail(NotFound(context, "session.parentNotFound", $"Parent session '{parentSessionId}' was not found."));
            }

            return ParentSessionResolutionResult.Success(parent.SessionId);
        }

        var automaticParentSessionId = !string.IsNullOrWhiteSpace(context.Caller.SourceSessionId)
            ? context.Caller.SourceSessionId
            : null;
        if (!string.IsNullOrWhiteSpace(automaticParentSessionId))
        {
            return ParentSessionResolutionResult.Success(automaticParentSessionId);
        }

        return ParentSessionResolutionResult.Success(null);
    }

    private static string GetGlobalRootOrCwd(AltaCommandContext context)
        => context.Services.Get<CatalogOptions>()?.GlobalRoot ?? context.Cwd ?? Environment.CurrentDirectory;

    private static void WriteProject(AltaCommandContext context, string type, ProjectDescriptor project)
        => AltaJsonlWriter.WriteRecord(context.Stdout, ProjectRecord(context, type, project));

    // `spaces` names the spaces of the project besides the default one, which holds every project.
    private static Dictionary<string, object?> ProjectRecord(AltaCommandContext context, string type, ProjectDescriptor project)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["projectId"] = project.Id,
            ["slug"] = project.Slug,
            ["name"] = project.Name,
            ["displayName"] = project.DisplayName,
            ["projectPath"] = project.ProjectPath,
        };
        if (project.DefaultBranch is not null) record["defaultBranch"] = project.DefaultBranch;
        record["archived"] = project.Archived;
        if (project.SourcePath is not null) record["sourcePath"] = project.SourcePath;
        if (project.Description is not null) record["description"] = project.Description;
        record["tags"] = project.Tags;
        record["spaces"] = project.Spaces;
        return record;
    }

    private static void WriteProjectRefs(AltaCommandContext context, IReadOnlyList<ProjectDescriptor> projects, string spaceId)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.project.refs",
            spaceId,
            projects = projects.Select(static project => new[] { project.Slug, project.ProjectPath }).ToArray(),
        });

    private static void WriteSession(AltaCommandContext context, string type, AltaSessionInfo info, bool includeChildren, IReadOnlyList<AltaSessionInfo> infos, SessionMetrics? metrics = null)
    {
        var children = includeChildren ? GetChildren(infos, info, recursive: false).ToArray() : [];
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = info.Session.SessionId,
            kind = SessionKindWire(info.Session.Kind),
            ProviderId = info.Session.ProviderId,
            providerKey = info.Session.ResolvedProviderKey,
            projectId = info.Session.ProjectRef,
            projectRef = info.Session.ProjectRef,
            // The space the session records; absent for one created before sessions had a space.
            spaceId = info.Session.SpaceId,
            parentSessionId = info.Session.ParentSessionId,
            createdBy = info.Session.CreatedBy,
            title = info.Session.Title,
            // Who chose the title, and the mode of the session when it has one of its own (see `session title`).
            titleSource = AgentSessionTitles.NameOf(info.Session.TitleSource),
            titleMode = info.Session.TitleMode,
            state = info.State,
            status = SessionStatusWire(info.Session.Status),
            // When the session was archived; absent for one that is not, or that was archived before the time was kept.
            archivedAt = info.LocalState is { Archived: true, ArchivedAt: { } archivedAt } ? archivedAt : (DateTimeOffset?)null,
            workingDirectory = info.Session.WorkingDirectory,
            // Where the session works: the worktree it records, while that folder is there.
            worktreeDirectory = NormalizeOptionalText(info.Session.WorktreeDirectory) is { } worktreeDirectory && Directory.Exists(worktreeDirectory) ? worktreeDirectory : null,
            latestSummary = info.Session.LatestSummary,
            messageCount = info.Session.MessageCount,
            isRunning = info.IsRunning,
            queuedPromptCount = info.LocalState?.QueuedPrompts.Count(static prompt => IsPendingQueuedPromptState(prompt.State)) ?? 0,
            agentPromptId = NormalizeOptionalText(info.Session.AgentPromptId) ?? AgentPromptCatalog.DefaultPromptName,
            promptId = NormalizeOptionalText(info.Session.AgentPromptId) ?? AgentPromptCatalog.DefaultPromptName,
            modelSelection = ToModelSelectionPayload(CreateModelSelection(info.Session, info.Preference), info.Session.AgentPromptId),
            createdAt = info.Session.CreatedAt,
            updatedAt = info.Session.UpdatedAt,
            lastActiveAt = info.Session.LastActiveAt,
            startedAt = info.Session.StartedAt,
            sourcePath = info.Session.SourcePath,
            metrics = metrics is null ? null : ToCompactMetricsPayload(metrics),
            childCount = includeChildren ? children.Length : (int?)null,
            childSessionIds = includeChildren ? children.Select(static child => child.Session.SessionId).ToArray() : null,
        });
    }

    private static void WriteSessionMetrics(AltaCommandContext context, string type, AltaSessionInfo info, SessionMetrics metrics)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = info.Session.SessionId,
            ProviderId = info.Session.ProviderId,
            providerKey = info.Session.ResolvedProviderKey,
            metrics = ToDetailedMetricsPayload(metrics),
        });
    }

    private static void WriteSessionResult(AltaCommandContext context, string type, AltaSessionInfo info, SessionResult result, bool includeResult, bool includeMetrics)
    {
        var finalAnswer = !includeResult || result.FinalAnswer is null
            ? null
            : new
            {
                text = result.FinalAnswer,
                characters = result.FinalAnswer.Length,
                words = CountWords(result.FinalAnswer),
                estimatedTokens = TokenEstimator.Estimate(result.FinalAnswer),
                completedAt = result.FinalAssistantAt,
            };

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = info.Session.SessionId,
            ProviderId = info.Session.ProviderId,
            providerKey = info.Session.ResolvedProviderKey,
            status = StatusWire(result.Status),
            scope = MetricsScopeWire(result.Scope),
            startedAt = result.Metrics.StartedAt,
            completedAt = result.Metrics.CompletedAt,
            durationMs = ToMilliseconds(result.Metrics.Duration),
            toolCallCount = result.Metrics.ToolCallCount,
            modelSelection = result.Metrics.ModelSelection,
            finalAnswer,
            finalError = includeResult ? result.FinalError : null,
            metrics = includeMetrics ? ToDetailedMetricsPayload(result.Metrics) : null,
        });
    }

    private static void WriteSessionReportDiagnostic(AltaCommandContext context, string requestedSessionId, string code, int exitCode, string message)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.report.item",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = requestedSessionId,
            status = "not_found",
            diagnostic = new
            {
                code,
                exitCode,
                message,
            },
        });
    }

    private static void WriteSessionReportSummary(AltaCommandContext context, int count, int successCount, int diagnosticCount, bool truncated)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.report.summary",
            version = 1,
            correlationId = context.CorrelationId,
            count,
            successCount,
            diagnosticCount,
            truncated,
        });
    }

    private static object ToCompactMetricsPayload(SessionMetrics metrics)
        => new
        {
            scope = MetricsScopeWire(metrics.Scope),
            metrics.Status,
            metrics.EventCount,
            metrics.StartedAt,
            metrics.CompletedAt,
            durationMs = ToMilliseconds(metrics.Duration),
            metrics.ToolCallCount,
            finalAnswer = new
            {
                metrics.FinalAnswerCharacters,
                metrics.FinalAnswerWords,
                estimatedTokens = metrics.FinalAnswerEstimatedTokens,
            },
            usage = ToUsagePayload(metrics.FinalUsage),
            providerOperations = ToProviderOperationTotalsPayload(metrics.ProviderOperations),
            finalError = metrics.FinalError,
        };

    private static object ToDetailedMetricsPayload(SessionMetrics metrics)
        => new
        {
            scope = MetricsScopeWire(metrics.Scope),
            metrics.Status,
            metrics.EventCount,
            metrics.RunCount,
            metrics.StartedAt,
            metrics.CompletedAt,
            durationMs = ToMilliseconds(metrics.Duration),
            metrics.FinalAssistantAt,
            metrics.UserMessageCount,
            metrics.AssistantMessageCount,
            metrics.ToolCallCount,
            finalAnswer = new
            {
                metrics.FinalAnswerCharacters,
                metrics.FinalAnswerWords,
                estimatedTokens = metrics.FinalAnswerEstimatedTokens,
            },
            currentUsage = ToUsagePayload(metrics.FinalUsage),
            providerOperations = ToProviderOperationTotalsPayload(metrics.ProviderOperations),
            finalError = metrics.FinalError,
            metrics.ModelSelection,
        };

    private static object? ToUsagePayload(AgentSessionUsage? usage)
        => usage is null ? null : new
        {
            window = usage.Window is null ? null : new
            {
                usage.Window.CurrentTokens,
                usage.Window.TokenLimit,
                usage.Window.MessageCount,
                usage.Window.Label,
                usage.WindowUsagePercentage,
            },
            lastOperation = usage.LastOperation is null ? null : new
            {
                usage.LastOperation.Model,
                usage.LastOperation.InputTokens,
                usage.LastOperation.OutputTokens,
                usage.LastOperation.CachedInputTokens,
                usage.LastOperation.CacheReadTokens,
                usage.LastOperation.CacheWriteTokens,
                usage.LastOperation.ReasoningTokens,
                usage.LastOperation.DurationMs,
                usage.LastOperation.Cost,
                usage.LastOperation.CostUnit,
                usage.LastOperation.Initiator,
                usage.LastOperation.ParentToolCallId,
                usage.LastOperation.ReasoningEffort,
                usage.LastOperation.Label,
            },
            scope = usage.Scope.ToString(),
            source = usage.Source.ToString(),
            usage.UpdatedAt,
        };

    private static object ToProviderOperationTotalsPayload(ProviderOperationTotals totals)
        => new
        {
            totals.OperationCount,
            totals.InputTokens,
            totals.OutputTokens,
            totals.CachedInputTokens,
            totals.ReasoningTokens,
            totals.CacheWriteTokens,
            totals.Cost,
            totals.CostUnit,
        };

    private static double? ToMilliseconds(TimeSpan? value)
        => value?.TotalMilliseconds;

    private static string MetricsScopeWire(SessionMetricsScope scope)
        => scope == SessionMetricsScope.LastTurn ? "last-turn" : "session";

    private static bool IsPendingQueuedPromptState(string? state)
        => string.Equals(state, "queued", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(state, "submitting", StringComparison.OrdinalIgnoreCase);

    private static void WriteModelSelection(AltaCommandContext context, string type, AltaModelSelection selection, string? sessionId, string? promptId = null)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            sessionId,
            selection.ProviderKey,
            selection.ModelId,
            reasoningEffort = selection.ReasoningEffort is null ? null : AltaModelRef.ToWireName(selection.ReasoningEffort.Value),
            selection.ModelRef,
            promptId = NormalizeOptionalText(promptId) ?? AgentPromptCatalog.DefaultPromptName,
            agentPromptId = NormalizeOptionalText(promptId) ?? AgentPromptCatalog.DefaultPromptName,
        });
    }

    private static object ToModelSelectionPayload(AltaModelSelection selection, string? promptId = null)
        => new
        {
            selection.ProviderKey,
            selection.ModelId,
            reasoningEffort = selection.ReasoningEffort is null ? null : AltaModelRef.ToWireName(selection.ReasoningEffort.Value),
            selection.ModelRef,
            promptId = NormalizeOptionalText(promptId) ?? AgentPromptCatalog.DefaultPromptName,
            agentPromptId = NormalizeOptionalText(promptId) ?? AgentPromptCatalog.DefaultPromptName,
        };

    private static void WriteAgentEvent(AltaCommandContext context, SessionViewDescriptor session, AgentEvent agentEvent, long sequenceNumber, IReadOnlySet<string>? fields = null)
    {
        var mapped = MapAgentEvent(agentEvent);
        if (fields is { Count: > 0 })
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, CreateAgentEventFieldsRecord(context, session, agentEvent, sequenceNumber, mapped, fields));
            return;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.event",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.SessionId,
            sequenceNumber,
            timestamp = agentEvent.Timestamp,
            kind = mapped.Kind,
            role = mapped.Role,
            source = new
            {
                // Compatibility: event-source kind is a live-tool wire value from before provider terminology.
                kind = "backend",
                ProviderId = agentEvent.ProviderId.Value,
                runId = agentEvent.RunId?.Value,
            },
            content = string.IsNullOrEmpty(mapped.Text) ? [] : new[] { new { type = "text", text = mapped.Text } },
            metadata = mapped.Metadata,
        });
    }

    private static Dictionary<string, object?> CreateAgentEventFieldsRecord(
        AltaCommandContext context,
        SessionViewDescriptor session,
        AgentEvent agentEvent,
        long sequenceNumber,
        MappedEvent mapped,
        IReadOnlySet<string> fields)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.session.event",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
        };

        if (ContainsField(fields, "sessionId"))
        {
            record["sessionId"] = session.SessionId;
        }

        if (ContainsField(fields, "sequenceNumber") || ContainsField(fields, "sequence"))
        {
            record["sequenceNumber"] = sequenceNumber;
        }

        if (ContainsField(fields, "timestamp"))
        {
            record["timestamp"] = agentEvent.Timestamp;
        }

        if (ContainsField(fields, "kind"))
        {
            record["kind"] = mapped.Kind;
        }

        if (ContainsField(fields, "role"))
        {
            record["role"] = mapped.Role;
        }

        if (ContainsField(fields, "source"))
        {
            record["source"] = new
            {
                // Compatibility: event-source kind is a live-tool wire value from before provider terminology.
                kind = "backend",
                ProviderId = agentEvent.ProviderId.Value,
                runId = agentEvent.RunId?.Value,
            };
        }

        if (ContainsField(fields, "content"))
        {
            record["content"] = string.IsNullOrEmpty(mapped.Text) ? [] : new[] { new { type = "text", text = mapped.Text } };
        }

        if (ContainsField(fields, "text"))
        {
            record["text"] = mapped.Text;
        }

        if (ContainsField(fields, "metadata"))
        {
            record["metadata"] = mapped.Metadata;
        }

        return record;
    }

    private static bool ContainsField(IReadOnlySet<string> fields, string field)
        => fields.Contains(field);

    private static MappedEvent MapAgentEvent(AgentEvent agentEvent)
        => agentEvent switch
        {
            AgentContentDeltaEvent content => new MappedEvent(ContentKindToEventKind(content.Kind), ContentKindToRole(content.Kind), content.Delta, null),
            AgentContentCompletedEvent content => new MappedEvent(ContentKindToEventKind(content.Kind), ContentKindToRole(content.Kind), content.Content, null),
            AgentActivityEvent activity => new MappedEvent($"activity.{activity.Kind.ToString().ToLowerInvariant()}.{activity.Phase.ToString().ToLowerInvariant()}", "host", activity.Message ?? activity.Name, new { activity.ActivityId, activity.ParentActivityId }),
            AgentSessionUpdateEvent update => new MappedEvent($"host.{update.Kind.ToString().ToLowerInvariant()}", "host", update.Message, update.Kind is AgentSessionUpdateKind.Reconnecting or AgentSessionUpdateKind.Warning ? update.Details : null),
            AgentPlanSnapshotEvent plan => new MappedEvent("assistant.plan", "assistant", plan.Snapshot.Explanation, new { plan.Snapshot.ChangeKind, steps = plan.Snapshot.Steps }),
            AgentInteractionEvent interaction => new MappedEvent($"interaction.{interaction.Kind.ToString().ToLowerInvariant()}", "host", interaction.Message, new { interaction.InteractionId }),
            AgentErrorEvent error => new MappedEvent("error", "host", error.Message, error.ExceptionInfo is null ? null : new { failureKind = error.Failure?.Kind.ToString(), exceptionType = error.ExceptionInfo.Type, details = CreateErrorDetails(error), causes = CreateErrorCauses(error) }),
            AgentRawEvent raw => new MappedEvent("event.raw", "event", raw.BackendEventType, null),
            AgentPermissionRequest permission => new MappedEvent("host.permissionRequest", "host", permission.Kind, new { permission.InteractionId }),
            AgentUserInputRequest request => new MappedEvent("host.userInputRequest", "host", "User input requested.", new { request.InteractionId }),
            _ => new MappedEvent("event", "event", agentEvent.GetType().Name, null),
        };

    private static string ContentKindToEventKind(AgentContentKind kind)
        => kind switch
        {
            AgentContentKind.User => "user.message",
            AgentContentKind.Assistant => "assistant.message",
            AgentContentKind.Reasoning => "assistant.reasoning",
            AgentContentKind.ReasoningSummary => "assistant.reasoningSummary",
            AgentContentKind.Plan => "assistant.plan",
            AgentContentKind.CommandOutput => "tool.commandOutput",
            AgentContentKind.FileChangeOutput => "tool.fileChangeOutput",
            AgentContentKind.ToolOutput => "tool.output",
            AgentContentKind.Notice => "host.notice",
            _ => "event.content",
        };

    private static string ContentKindToRole(AgentContentKind kind)
        => kind switch
        {
            AgentContentKind.User => "user",
            AgentContentKind.Assistant or AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary or AgentContentKind.Plan => "assistant",
            AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput => "tool",
            AgentContentKind.Notice => "host",
            _ => "event",
        };

    private static bool IncludesEvent(AgentEvent agentEvent, IReadOnlySet<string> includes, IReadOnlySet<string> kinds, bool noToolOutput)
    {
        var mapped = MapAgentEvent(agentEvent);
        if (noToolOutput && mapped.Role == "tool")
        {
            return false;
        }

        if (kinds.Count > 0 && !kinds.Contains(mapped.Kind))
        {
            return false;
        }

        if (includes.Count == 0)
        {
            return true;
        }

        return includes.Contains(mapped.Role) ||
               mapped.Kind.Split('.', 2)[0] is { } prefix && includes.Contains(prefix) ||
               (mapped.Role == "host" && includes.Contains("event"));
    }

    private static IReadOnlySet<string> ParseSet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            set.Add(part);
        }

        return set;
    }

    private static void WritePromptResult(AltaCommandContext context, string type, SessionViewDescriptor session, string? runId, string? queueItemId, bool queued, PromptDispatchKind kind, string prompt)
    {
        var parentNotificationExpected = IsAgentCaller(context) && !string.IsNullOrWhiteSpace(session.ParentSessionId);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.SessionId,
            runId,
            queueItemId,
            queued,
            detached = runId is null && queueItemId is null && !queued,
            dispatchKind = kind.ToString().ToLowerInvariant(),
            notificationExpected = parentNotificationExpected ? true : (bool?)null,
            shouldPoll = parentNotificationExpected ? false : (bool?)null,
            shouldYield = parentNotificationExpected ? true : (bool?)null,
            activeWaitAllowed = parentNotificationExpected ? false : (bool?)null,
            waitForCompletion = parentNotificationExpected ? false : (bool?)null,
            followUpMode = parentNotificationExpected ? "notification" : null,
            recommendedAction = parentNotificationExpected ? "stop" : null,
            forbiddenWaitActions = parentNotificationExpected ? NotificationFollowUpForbiddenWaitActions : null,
            nextStep = parentNotificationExpected ? NotificationFollowUpNextStep : null,
            notification = CreatePromptNotificationPayload(context, session),
            submittedBy = CreateProvenance(context),
            promptPreview = prompt.Length <= 160 ? prompt : prompt[..160],
        });
    }

    internal static void WriteReminderNotificationFailure(AltaCommandContext context, AltaReminderNotificationFailure? failure, bool historical)
    {
        if (failure is null)
        {
            return;
        }

        var action = failure.ChangeKind switch
        {
            AltaReminderChangeKind.Created => "was created",
            AltaReminderChangeKind.ContentUpdated => "had its content updated",
            AltaReminderChangeKind.Deleted => "was deleted",
            AltaReminderChangeKind.Fired => "had its delivery result recorded",
            AltaReminderChangeKind.Failed => "had its runner failure recorded",
            _ => "was changed",
        };
        AltaJsonlWriter.WriteRecord(context.Stderr, new
        {
            type = "alta.warning",
            version = 1,
            correlationId = context.CorrelationId,
            code = "reminder.notificationFailed",
            message = (historical ? "Historical notification failure (latest retained): " : string.Empty)
                + $"Reminder '{failure.ReminderId}' {action}, but notification failed. This does not change the committed outcome or delivery result.",
            historical,
            reminderId = failure.ReminderId,
            sessionId = failure.TargetSessionId,
            changeKind = failure.ChangeKind.ToString(),
            firedCount = failure.FiredCount,
            failureCount = failure.FailureCount,
            messages = failure.Messages,
            messagesTruncated = failure.MessagesTruncated,
        });
    }

    private static void WriteReminder(AltaCommandContext context, string type, AltaReminderDescriptor reminder)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            reminderId = reminder.ReminderId,
            sessionId = reminder.TargetSessionId,
            sourceSessionId = reminder.SourceSessionId,
            state = reminder.State,
            durationSeconds = reminder.Duration.TotalSeconds,
            repeat = reminder.RepeatCount,
            firedCount = reminder.FiredCount,
            createdAt = reminder.CreatedAt,
            dueAt = reminder.DueAt,
            lastFiredAt = reminder.LastFiredAt,
            completedAt = reminder.CompletedAt,
            lastExitCode = reminder.LastExitCode,
            lastError = reminder.LastError,
            contentPreview = reminder.ContentPreview,
            lastTranscriptPreview = reminder.LastTranscriptPreview,
        });
    }

    private static object? CreatePromptNotificationPayload(AltaCommandContext context, SessionViewDescriptor session)
        => IsAgentCaller(context) && !string.IsNullOrWhiteSpace(session.ParentSessionId)
            ? new
            {
                parentSessionId = session.ParentSessionId,
                automaticParentNotification = true,
                expected = true,
                shouldPoll = false,
                shouldYield = true,
                activeWaitAllowed = false,
                waitForCompletion = false,
                followUpMode = "notification",
                recommendedAction = "stop",
                forbiddenWaitActions = NotificationFollowUpForbiddenWaitActions,
                nextStep = NotificationFollowUpNextStep,
                guidance = NotificationFollowUpGuidance,
            }
            : null;

    private static void WriteSkill(AltaCommandContext context, string type, SkillDescriptor skill, bool includeDiagnostics, SkillDocument? document = null)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            name = skill.Name,
            skill.Title,
            skill.Description,
            skillRootPath = skill.SkillRootPath,
            skillFilePath = skill.SkillFilePath,
            sourceKind = skill.SourceKind.ToString().ToLowerInvariant(),
            sourceId = skill.SourceId,
            scope = skill.Scope.ToString().ToLowerInvariant(),
            skill.IsTrusted,
            skill.IsValid,
            skill.IsModelVisible,
            skill.IsShadowed,
            skill.ShadowedBySkillFilePath,
            diagnostics = includeDiagnostics ? skill.Diagnostics.Select(static diagnostic => new { severity = diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Code, diagnostic.Message, diagnostic.FieldName, diagnostic.Path }).ToArray() : null,
            body = document?.Body,
        });
    }

    // total and next are written only when a limit cut the names, so the default record stays the list of names.
    private static void WriteSkillRefs(AltaCommandContext context, IReadOnlyList<SkillDescriptor> skills, int? total, string? next)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.skill.refs",
            skills = skills.Select(static skill => skill.Name).ToArray(),
            total,
            truncated = total is null ? (bool?)null : true,
            next,
        });

    private static void WritePolicy(AltaCommandContext context, string type, AltaCommandPolicy policy)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            policy.Path,
            policy.RequiresInProcessRuntime,
            policy.IsMutating,
            policy.IsDisruptive,
            policy.SupportsCatalogOnlyContext,
        });
    }

    private static void WriteToolPaths(AltaCommandContext context, IReadOnlyList<AltaCommandPolicy> policies)
    {
        var ordered = policies.OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.tool.paths",
            paths = ordered.Select(static policy => policy.Path).ToArray(),
            mutating = ordered.Where(static policy => policy.IsMutating).Select(static policy => policy.Path).ToArray(),
            disruptive = ordered.Where(static policy => policy.IsDisruptive).Select(static policy => policy.Path).ToArray(),
        });
    }

    private static void WriteToolCapabilities(AltaCommandContext context, IReadOnlyList<AltaCommandPolicy> policies)
    {
        var ordered = policies.OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var runtimeCapabilities = GetRuntimeCapabilities(context);
        var providerCapabilities = GetProviderCapabilities(context);
        var pluginCapability = GetPluginCapability(context, policies);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.tool.capabilities",
            paths = ordered.Select(static policy => policy.Path).ToArray(),
            mutating = ordered.Where(static policy => policy.IsMutating).Select(static policy => policy.Path).ToArray(),
            disruptive = ordered.Where(static policy => policy.IsDisruptive).Select(static policy => policy.Path).ToArray(),
            catalogOnly = ordered.Where(static policy => policy.SupportsCatalogOnlyContext).Select(static policy => policy.Path).ToArray(),
            outOfProcess = ordered.Where(static policy => !policy.RequiresInProcessRuntime).Select(static policy => policy.Path).ToArray(),
            runtime = new
            {
                available = runtimeCapabilities.Where(static capability => capability.Available).Select(static capability => capability.Capability).ToArray(),
                unavailable = runtimeCapabilities.Where(static capability => !capability.Available).Select(static capability => capability.Capability).ToArray(),
            },
            providers = new
            {
                registered = providerCapabilities.Where(static capability => capability.Registered).Select(static capability => capability.ProviderKey).ToArray(),
                configured = providerCapabilities.Where(static capability => capability.Configured).Select(static capability => capability.ProviderKey).ToArray(),
                sessionTool = providerCapabilities.Where(static capability => capability.SupportsAltaSessionTool).Select(static capability => capability.ProviderKey).ToArray(),
            },
            // Compatibility: keep the legacy aggregate field for existing live-tool clients.
            backends = new
            {
                registered = providerCapabilities.Where(static capability => capability.Registered).Select(static capability => capability.ProviderKey).ToArray(),
                configured = providerCapabilities.Where(static capability => capability.Configured).Select(static capability => capability.ProviderKey).ToArray(),
                sessionTool = providerCapabilities.Where(static capability => capability.SupportsAltaSessionTool).Select(static capability => capability.ProviderKey).ToArray(),
            },
            plugins = pluginCapability,
        });
    }

    private static void WriteModelItem(AltaCommandContext context, string providerKey, AgentModelInfo model, AgentReasoningEffort? requestedReasoning = null)
    {
        var reasoning = requestedReasoning ?? model.DefaultReasoningEffort;
        var reasoningStatus = GetReasoningStatus(model, requestedReasoning, reasoning);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.model.item",
            version = 1,
            correlationId = context.CorrelationId,
            providerKey,
            modelId = model.Id,
            model.DisplayName,
            model.Description,
            model.Provider,
            defaultReasoningEffort = model.DefaultReasoningEffort is null ? null : AltaModelRef.ToWireName(model.DefaultReasoningEffort.Value),
            supportedReasoningEfforts = model.SupportedReasoningEfforts?.Select(AltaModelRef.ToWireName).ToArray(),
            requestedReasoningEffort = requestedReasoning is null ? null : AltaModelRef.ToWireName(requestedReasoning.Value),
            effectiveReasoningEffort = reasoning is null ? null : AltaModelRef.ToWireName(reasoning.Value),
            reasoningStatus,
            modelRef = AltaModelRef.Format(providerKey, model.Id, reasoning),
            capabilities = model.Capabilities,
        });
    }

    private static void WriteModelRefs(AltaCommandContext context, IReadOnlyList<string> modelRefs)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.model.refs",
            modelRefs,
        });

    private static void WriteProviderKeys(AltaCommandContext context, IReadOnlyList<ModelProviderId> ProviderIds)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.provider.keys",
            providerKeys = ProviderIds.Select(static ProviderId => ProviderId.Value).ToArray(),
        });

    private static string CreateModelRef(string providerKey, AgentModelInfo model, AgentReasoningEffort? requestedReasoning)
    {
        var reasoning = requestedReasoning ?? model.DefaultReasoningEffort;
        return AltaModelRef.Format(providerKey, model.Id, reasoning);
    }

    private static bool ModelMatchesFilters(string providerKey, AgentModelInfo model, ModelListOptions options)
    {
        if (options.ReasoningEffort is { } reasoning && !ModelSupportsReasoning(model, reasoning))
        {
            return false;
        }

        if (options.SupportsTools && !ModelSupportsTools(model))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.Contains))
        {
            var effectiveReasoning = options.ReasoningEffort ?? model.DefaultReasoningEffort;
            var modelRef = AltaModelRef.Format(providerKey, model.Id, effectiveReasoning);
            var needle = options.Contains.Trim();
            return ContainsOrdinalIgnoreCase(model.Id, needle) ||
                   ContainsOrdinalIgnoreCase(model.DisplayName, needle) ||
                   ContainsOrdinalIgnoreCase(modelRef, needle);
        }

        return true;
    }

    private static bool ModelSupportsReasoning(AgentModelInfo model, AgentReasoningEffort reasoning)
    {
        if (reasoning == AgentReasoningEffort.None)
        {
            return true;
        }

        return model.SupportedReasoningEfforts is { Count: > 0 }
            ? model.SupportedReasoningEfforts.Contains(reasoning)
            : ModelCapabilityBool(model, "supportsReasoning") != false;
    }

    private static bool ModelSupportsTools(AgentModelInfo model)
        => ModelCapabilityBool(model, "supportsToolCall") == true || ModelCapabilityBool(model, "supportsTools") == true;

    private static bool? ModelCapabilityBool(AgentModelInfo model, string key)
    {
        if (model.Capabilities is null || !model.Capabilities.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            bool boolValue => boolValue,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string GetReasoningStatus(AgentModelInfo model, AgentReasoningEffort? requestedReasoning, AgentReasoningEffort? effectiveReasoning)
    {
        if (requestedReasoning is { } requested)
        {
            return ModelSupportsReasoning(model, requested) ? "applied" : "unsupported";
        }

        return effectiveReasoning is null ? "none" : "defaulted";
    }

    private static bool ContainsOrdinalIgnoreCase(string? value, string text)
        => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private static void WriteSummary(AltaCommandContext context, string type, int count, bool truncated)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            count,
            truncated,
        });
    }

    private static int UsageError(AltaCommandContext context, string code, string message, string commandPath)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.Usage, message, commandPath, $"Use `{commandPath} --help` for usage.");
        return AltaExitCodes.Usage;
    }

    private static int NotFound(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.NotFound, message);
        return AltaExitCodes.NotFound;
    }

    private static int PermissionDenied(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.PolicyDenied, message);
        return AltaExitCodes.PolicyDenied;
    }

    // In a host where a session takes the permission mode of its creator, a session does not hand a prompt to a
    // session that asks less than it does: what the user would have reviewed here would run there without a review.
    private static int PromptDenied(AltaCommandContext context)
        => PermissionDenied(context, "session.promptDenied",
            "The user reviews what this session does, and less of what the target session does: this session cannot hand it a prompt. Do the work here, or in a session you create.");

    // Whether the user reviews the commands of the caller, which then does nothing that runs a command nobody
    // reviewed: a background job, what is typed in a terminal, the command of an automation, a plugin that is built.
    // The session that calls is the one that acts; a command that names another session counts for a caller that is none.
    private static bool ReviewsCommands(AltaCommandContext context, string? targetSessionId = null)
        => context.Services.Get<AltaCommandReviewPolicy>() is { } policy
            && !policy.Accepts(NormalizeOptionalText(context.Caller.SourceSessionId) ?? NormalizeOptionalText(targetSessionId));

    // Whether the calling session, or the one a command is for, does without the shell: it then does nothing that
    // starts a program of its choice either, the same things as a session whose commands are reviewed. The user, with
    // no calling session and naming none, runs commands as before.
    internal static async Task<bool> ShellTurnedOffAsync(AltaCommandContext context, string? targetSessionId = null)
    {
        foreach (var sessionId in new[] { NormalizeOptionalText(context.Caller.SourceSessionId), NormalizeOptionalText(targetSessionId) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (sessionId is not null
                && (await DisabledToolsOfAsync(context, sessionId).ConfigureAwait(false)).Contains(PromptToolConditions.ShellCommand))
                return true;
        }

        return false;
    }

    // The built-in tools a session does without, as the host says them; none where it says nothing.
    private static async Task<IReadOnlySet<string>> DisabledToolsOfAsync(AltaCommandContext context, string sessionId)
        => context.Services.Get<AltaDisabledTools>() is { } tools ? await tools.Of(sessionId, context.CancellationToken).ConfigureAwait(false)
            : context.Services.Get<SessionRuntimeService>() is { } runtime ? await runtime.GetDisabledToolsAsync(sessionId, context.CancellationToken).ConfigureAwait(false)
            : new HashSet<string>();

    internal static string ShellTurnedOffMessage(string what)
        => $"`shell_command` is turned off for this session (in the settings of the user, of its space or of its agent prompt): it cannot {what}. " +
           "Use the other tools of the session, or ask the user to run the command.";

    // Refuses, for a session that does without some built-in tools, what would hand one of them to a session: to
    // itself under another agent prompt, to a session it creates or forks, to a session it hands a prompt to, which
    // would use them for it. `target` says what that session would do without, `what` names it. Null when allowed;
    // the user, with no calling session, decides the tools.
    private static async Task<int?> RefuseWiderToolsAsync(AltaCommandContext context, Func<SessionRuntimeService, Task<IReadOnlySet<string>>> target, string what, string? hint = null)
    {
        if (NormalizeOptionalText(context.Caller.SourceSessionId) is not { } callerId
            || context.Services.Get<SessionRuntimeService>() is not { } runtime)
        {
            return null;
        }

        var own = await runtime.GetDisabledToolsAsync(callerId, context.CancellationToken).ConfigureAwait(false);
        if (own.Count == 0) return null;
        var theirs = await target(runtime).ConfigureAwait(false);
        var wider = own.Where(tool => !theirs.Contains(tool)).Order(StringComparer.Ordinal).ToArray();
        return wider.Length == 0
            ? null
            : PermissionDenied(context, "session.widerTools",
                $"This session does without {string.Join(", ", wider.Select(static tool => $"`{tool}`"))} (in the settings of the user, of its space or of its agent prompt), " +
                $"and {what} would have {(wider.Length == 1 ? "it" : "them")}: a session does not give a tool it does without. {hint ?? "Do the work here, or ask the user."}");
    }

    // Refuses, for a session that does without some built-in tools, what changes the settings its tools come from (agent
    // prompts, spaces, automations that start sessions): it could give itself, or the sessions started from them, what
    // it does without. Null when allowed; the user, with no calling session, changes them.
    private static async Task<int?> RefuseSettingsChangeAsync(AltaCommandContext context, string what)
    {
        if (NormalizeOptionalText(context.Caller.SourceSessionId) is not { } callerId
            || await DisabledToolsOfAsync(context, callerId).ConfigureAwait(false) is not { Count: > 0 } own)
        {
            return null;
        }

        return PermissionDenied(context, "settings.toolsDisabled",
            $"This session does without {string.Join(", ", own.Order(StringComparer.Ordinal).Select(static tool => $"`{tool}`"))} (in the settings of the user, of its space or of its agent prompt): " +
            $"it cannot {what}, which could give those tools back. Ask the user to do it from the CodeAlta window.");
    }

    // Why the caller may not start a program of its choice, or null when it may: what the commands of plugins refuse with.
    internal static async ValueTask<string?> CommandRefusalAsync(AltaCommandContext context, string what)
        => ReviewsCommands(context) ? $"The user reviews the commands of this session: it cannot {what}."
            : await ShellTurnedOffAsync(context).ConfigureAwait(false) ? ShellTurnedOffMessage(what)
            : null;

    private static int ShellTurnedOffDenied(AltaCommandContext context, string code, string what)
        => PermissionDenied(context, code, ShellTurnedOffMessage(what));

    private static int Unsupported(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.Unsupported, message);
        return AltaExitCodes.Unsupported;
    }

    private static AltaActorProvenance CreateProvenance(AltaCommandContext context)
        => new()
        {
            Kind = context.Caller.Kind,
            SourceSessionId = context.Caller.SourceSessionId,
            SourceProjectId = context.Caller.SourceProjectId,
            SourceAgentId = context.Caller.SourceAgentId,
            PluginRuntimeKey = context.Caller.PluginRuntimeKey,
            CorrelationId = context.CorrelationId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string SessionKindWire(SessionViewKind kind)
        => kind switch
        {
            SessionViewKind.GlobalSession => "global_session",
            SessionViewKind.ProjectSession => "project_session",
            SessionViewKind.InternalSession => "internal_session",
            _ => kind.ToString().ToLowerInvariant(),
        };

    private static string SessionStatusWire(SessionViewStatus status)
        => status.ToString().ToLowerInvariant();

    private sealed class AltaModelSelectionOptions
    {
        public string? ModelRef { get; set; }

        public string? SameModelAsSessionId { get; set; }

        public string? ProviderKey { get; set; }

        public string? ModelId { get; set; }

        public AgentReasoningEffort? ReasoningEffort { get; set; }

        public string? PromptId { get; set; }

        public ModelTier? Tier { get; set; }
    }

    private sealed class SessionCreateOptions
    {
        public AltaModelSelectionOptions Model { get; } = new();

        public bool Worktree { get; set; }

        public bool NoWorktree { get; set; }

        public string? WorktreeBase { get; set; }

        public string? Project { get; set; }

        public bool Global { get; set; }

        public string? Title { get; set; }

        public string? ParentSessionId { get; set; }

        public bool NoParent { get; set; }

        public string? Space { get; set; }

        public string? PermissionMode { get; set; }
    }

    private sealed class SessionForkOptions
    {
        public string? SessionId { get; set; }

        public string? Before { get; set; }

        public string? After { get; set; }

        public string? Title { get; set; }

        public string? ParentSessionId { get; set; }

        public bool Worktree { get; set; }

        public bool NoWorktree { get; set; }

        public string? WorktreeBase { get; set; }
    }

    private sealed class SessionEventsOptions
    {
        public string? Include { get; set; }

        public string? Kind { get; set; }

        public string? Fields { get; set; }

        public bool NoToolOutput { get; set; }
    }

    private sealed class ModelListOptions
    {
        public string? Provider { get; set; }

        public string? Contains { get; set; }

        public AgentReasoningEffort? ReasoningEffort { get; set; }

        public bool SupportsTools { get; set; }

        public bool Detailed { get; set; }
    }

    private enum SessionMetricsScope
    {
        LastTurn,
        Session,
    }

    private sealed record ProviderOperationTotals(
        int OperationCount,
        long InputTokens,
        long OutputTokens,
        long CachedInputTokens,
        long ReasoningTokens,
        long CacheWriteTokens = 0,
        double? Cost = null,
        string? CostUnit = null);

    private sealed record ProviderCapability(
        string ProviderKey,
        string ProviderId,
        bool Registered,
        bool Configured,
        bool SupportsAltaSessionTool);

    private sealed record PluginCapability(
        bool Available,
        int PluginCount,
        int PluginCommandCount);

    private sealed record SessionMetrics(
        SessionMetricsScope Scope,
        int EventCount,
        int RunCount,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        TimeSpan? Duration,
        DateTimeOffset? FinalAssistantAt,
        int UserMessageCount,
        int AssistantMessageCount,
        int ToolCallCount,
        int FinalAnswerCharacters,
        int FinalAnswerWords,
        long FinalAnswerEstimatedTokens,
        AgentSessionUsage? FinalUsage,
        ProviderOperationTotals ProviderOperations,
         string Status,
         FinalErrorPayload? FinalError,
        object ModelSelection);

    private sealed record SessionResult(
        SessionMetricsScope Scope,
        SessionResultStatus Status,
        string? FinalAnswer,
        DateTimeOffset? FinalAssistantAt,
        FinalErrorPayload? FinalError,
        SessionMetrics Metrics);

    private sealed record SessionReportItem(
        int Index,
        string SessionId,
        AltaSessionInfo? Info,
        SessionResult? Result,
        string Diagnostics)
    {
        public static SessionReportItem Success(int index, string sessionId, AltaSessionInfo info, SessionResult result, string diagnostics)
            => new(index, sessionId, info, result, diagnostics);

        public static SessionReportItem NotFound(int index, string sessionId)
            => new(index, sessionId, null, null, string.Empty);
    }

    private sealed record FinalErrorPayload(
        string kind,
        string message,
        DateTimeOffset timestamp,
        string? runId,
        string? exceptionType,
        string? failureKind,
        IReadOnlyDictionary<string, string>? details,
        IReadOnlyList<string>? causes);

    private enum SessionResultStatus
    {
        Unknown,
        Running,
        Completed,
        Failed,
        Cancelled,
    }

    private sealed class PromptOptions
    {
        public string? Message { get; set; }

        public bool UseStdin { get; set; }

        public string? PromptId { get; set; }

        public bool QueueIfBusy { get; set; }

        public string? MessageKind { get; set; }

        public bool ReplyRequested { get; set; }
    }

    private sealed class ReminderCreateOptions
    {
        public string? Duration { get; set; }

        public string? Content { get; set; }

        public bool UseStdin { get; set; }

        public string? SessionId { get; set; }

        public string? Repeat { get; set; }
    }

    private sealed class PromptManagementOptions
    {
        public string? Scope { get; set; } = "all";

        public string? Project { get; set; }

        public bool System { get; set; }

        public bool Verbose { get; set; }

        public string? Content { get; set; }

        public bool UseStdin { get; set; }

        public string? Name { get; set; }

        public string? Description { get; set; }

        public string? SystemPromptId { get; set; }
    }

    private sealed record PromptMetadata(string? Name, string? Description)
    {
        public static PromptMetadata Empty { get; } = new(null, null);
    }

    private sealed record MappedEvent(string Kind, string Role, string? Text, object? Metadata);

    private sealed record ModelResolutionResult(int ExitCode, AltaModelSelection? Selection)
    {
        // The tier the selection comes from, when it comes from one.
        public ModelTier? Tier { get; init; }

        public static ModelResolutionResult Success(AltaModelSelection selection) => new(AltaExitCodes.Success, selection);

        public static ModelResolutionResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record SessionResultBuildResult(int ExitCode, AltaSessionInfo? Info, SessionResult? Result)
    {
        public static SessionResultBuildResult Success(AltaSessionInfo info, SessionResult result) => new(AltaExitCodes.Success, info, result);

        public static SessionResultBuildResult Fail(int exitCode) => new(exitCode, null, null);
    }

    private sealed record SessionInfoResolutionResult(int ExitCode, AltaSessionInfo? Info, IReadOnlyList<AltaSessionInfo> Infos)
    {
        public static SessionInfoResolutionResult Success(AltaSessionInfo info, IReadOnlyList<AltaSessionInfo> infos) => new(AltaExitCodes.Success, info, infos);

        public static SessionInfoResolutionResult Fail(int exitCode) => new(exitCode, null, []);
    }

    private sealed record AskAllowedRootsResult(int ExitCode, string BaseDirectory, IReadOnlyList<string> AllowedRoots)
    {
        public static AskAllowedRootsResult Success(string baseDirectory, IReadOnlyList<string> allowedRoots) => new(AltaExitCodes.Success, baseDirectory, allowedRoots);

        public static AskAllowedRootsResult Fail(int exitCode) => new(exitCode, string.Empty, []);
    }

    private sealed record SessionModelSelectionResult(int ExitCode, bool Found, AltaModelSelection? Selection)
    {
        public static SessionModelSelectionResult Success(AltaModelSelection selection) => new(AltaExitCodes.Success, Found: true, selection);

        public static SessionModelSelectionResult NotFound() => new(AltaExitCodes.Success, Found: false, null);

        public static SessionModelSelectionResult Fail(int exitCode) => new(exitCode, Found: false, null);
    }

    private sealed record SkillQueryResult(int ExitCode, SkillCatalogQuery? Query)
    {
        public static SkillQueryResult Success(SkillCatalogQuery query) => new(AltaExitCodes.Success, query);

        public static SkillQueryResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record PromptQueryResult(int ExitCode, AgentPromptCatalogQuery? Query)
    {
        public static PromptQueryResult Success(AgentPromptCatalogQuery query) => new(AltaExitCodes.Success, query);

        public static PromptQueryResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record PromptProjectRootResult(int ExitCode, string? ProjectRoot)
    {
        public static PromptProjectRootResult Success(string? projectRoot) => new(AltaExitCodes.Success, projectRoot);

        public static PromptProjectRootResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record PromptReadResult(int ExitCode, string? Prompt)
    {
        public static PromptReadResult Success(string prompt) => new(AltaExitCodes.Success, prompt);

        public static PromptReadResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record ParentSessionResolutionResult(int ExitCode, string? ParentSessionId)
    {
        public static ParentSessionResolutionResult Success(string? parentSessionId) => new(AltaExitCodes.Success, parentSessionId);

        public static ParentSessionResolutionResult Fail(int exitCode) => new(exitCode, null);
    }

    private enum PromptDispatchKind
    {
        Send,
        Steer,
        Queue,
        Message,
        Request,
    }
}
