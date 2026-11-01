using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugin.Acp;
using CodeAlta.Plugin.Browser;
using CodeAlta.Plugin.CodexCli;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugin.Mobile;
using CodeAlta.Plugin.Jira;
using CodeAlta.Plugin.Statistics;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// The plugins that ship with the desktop host: the same three as the terminal UI, under the same ids, so
/// one <c>[plugins.&lt;id&gt;]</c> configuration applies to both.
/// </summary>
/// <remarks>
/// Each plugin is its backend only. What the terminal adds to them is terminal presentation (the
/// <c>/mcp</c> dialog, the <c>#</c> issue picker, the statistics visuals); the window has its own Settings
/// page, issue picker and statistics for those.
/// </remarks>
internal static class DesktopPlugins
{
    /// <summary>The built-in plugin definitions, in activation order.</summary>
    internal static IReadOnlyList<BuiltInPluginDefinition> BuiltIns { get; } =
    [
        new()
        {
            Id = "git", DisplayName = "Git",
            Description = "Exposes the GitHub, GitLab and Azure DevOps CLIs (gh, glab, az) to sessions when they are available.",
            PluginType = typeof(GitPlugin), Factory = static () => new GitPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        },
        new()
        {
            Id = "jira", DisplayName = "Jira",
            Description = "The issues of a Jira project, for the projects whose configuration names one.",
            PluginType = typeof(JiraPlugin), Factory = static () => new JiraPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        },
        new()
        {
            Id = Rpc.ComposerStatusService.McpPluginId, DisplayName = "MCP",
            Description = "Connects CodeAlta to configured Model Context Protocol servers.",
            // The window shows the views of MCP Apps where it keeps them away from its bridge, and loads the skills of
            // the servers: a session asks the user on its card before one is loaded.
            PluginType = typeof(McpPlugin),
            Factory = static () => new McpPlugin(new McpPluginOptions { ShowsApps = DesktopMcpAppViews.IsSupported, LoadsSkills = true }),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        },
        new()
        {
            Id = "statistics", DisplayName = "Statistics",
            Description = "Projects transient per-turn and session statistics from normalized agent events.",
            PluginType = typeof(StatisticsPlugin), Factory = static () => new StatisticsPlugin(),
        },
        new()
        {
            Id = AcpPlugin.PluginId, DisplayName = "ACP",
            Description = "Runs the agents of the Agent Client Protocol (ACP) as model providers: the agents of the ACP registry, installed in one step.",
            PluginType = typeof(AcpPlugin), Factory = static () => new AcpPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        },
        new()
        {
            Id = CodexCliPlugin.PluginId, DisplayName = "Codex CLI",
            Description = "Runs OpenAI's Codex app-server as a model provider, signed in with Codex's own ChatGPT sign-in.",
            PluginType = typeof(CodexCliPlugin), Factory = static () => new CodexCliPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        },
    ];

    /// <summary>
    /// The built-in plugins of a window: the ones of both applications, and the one that gives a session the
    /// tools that see and drive the window.
    /// </summary>
    /// <param name="ui">The tools of the window.</param>
    /// <param name="sessions">The sessions that have them.</param>
    /// <param name="reviewsCommands">
    /// Whether the user reviews the commands of the sessions. Such a host gives no session the tools that drive
    /// the window: a session could answer the review itself.
    /// </param>
    /// <param name="journals">
    /// The journals of the session store of this instance. The Statistics plugin of the window reads them to keep the
    /// statistics of every session; without them it keeps the cards of the timeline and reads nothing.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="ui"/> or <paramref name="sessions"/> is null.</exception>
    /// <param name="archives">The dedicated archive of the owning StateRoot, borrowed by the same Statistics plugin.</param>
    /// <param name="search">Where the Search plugin forwards the events of the sessions; without it the window has no Search plugin.</param>
    /// <param name="browser">What the browser plugin is given; without it the window has no browser plugin.</param>
    internal static IReadOnlyList<BuiltInPluginDefinition> ForWindow(Ui.IDesktopUi ui, Ui.DesktopUiSessions sessions, bool reviewsCommands,
        ISessionJournalCatalog? journals = null, CodeAlta.Catalog.StatisticsArchiveStore? archives = null, Search.DesktopSearchRelay? search = null,
        BrowserPluginHost? browser = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(sessions);
        var builtIns = journals is null ? BuiltIns : [.. BuiltIns.Select(definition => definition.Id == "statistics" ? StatisticsDefinition(journals, archives) : definition)];
        if (browser is not null) builtIns = [.. builtIns, BrowserDefinition(browser, PluginFrontends.Desktop)];
        // The landing page is the window's: the terminal application has no such plugin.
        builtIns = [.. builtIns, DesktopLandingPlugin.Definition()];
        // The mobile view is the window's too: a phone connects to the desktop application.
        builtIns = [.. builtIns, MobileDefinition];
        if (search is not null) builtIns = [.. builtIns, Search.DesktopSearchPlugin.Definition(search)];
        return reviewsCommands ? builtIns : [.. builtIns, Ui.DesktopUiPlugin.Definition(ui, sessions)];
    }

    /// <summary>The plugin that gives a session the tools of Chrome DevTools MCP over a web browser of this computer.</summary>
    /// <param name="host">What the application gives the plugin.</param>
    /// <param name="frontends">The applications it runs in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    internal static BuiltInPluginDefinition BrowserDefinition(BrowserPluginHost host, PluginFrontends frontends)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new()
        {
            Id = BrowserPlugin.Id, DisplayName = "Browser", Description = BrowserPlugin.Description,
            PluginType = typeof(BrowserPlugin), Factory = () => new BrowserPlugin(host), Frontends = frontends,
        };
    }

    /// <summary>
    /// The plugin that serves the sessions to a phone and the QR code a phone connects with. It is off until the user turns it
    /// on: it publishes this computer on the tailnet.
    /// </summary>
    internal static BuiltInPluginDefinition MobileDefinition { get; } = new()
    {
        Id = "mobile", DisplayName = "Mobile",
        Description = "The sessions of this computer on a phone, through Tailscale.",
        EnabledByDefault = false, Frontends = PluginFrontends.Desktop,
        PluginType = typeof(MobilePlugin), Factory = static () => new MobilePlugin(),
    };

    /// <summary>The Statistics plugin that keeps the statistics of the sessions of the journals it is given.</summary>
    /// <param name="journals">The journals of the session store of this instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="journals"/> is null.</exception>
    internal static BuiltInPluginDefinition StatisticsDefinition(ISessionJournalCatalog journals)
        => StatisticsDefinition(journals, null);

    private static BuiltInPluginDefinition StatisticsDefinition(ISessionJournalCatalog journals, CodeAlta.Catalog.StatisticsArchiveStore? archives)
    {
        ArgumentNullException.ThrowIfNull(journals);
        return new()
        {
            Id = "statistics", DisplayName = "Statistics",
            Description = "Projects transient per-turn and session statistics from normalized agent events, and keeps the statistics of every session.",
            PluginType = typeof(StatisticsPlugin), Factory = () => archives is null
                ? StatisticsPlugin.CreateForDesktop(journals) : StatisticsPlugin.CreateForDesktop(journals, archives),
        };
    }

    /// <summary>The journals of the session store of an instance: the folder its sessions are written to.</summary>
    /// <param name="catalog">The folders of the instance; the developer instance has a state root of its own.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    internal static ISessionJournalCatalog CreateJournalCatalog(CodeAlta.Catalog.CatalogOptions catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new CodeAlta.Agent.Runtime.FileSystemSessionJournalCatalog(new CodeAlta.Agent.Runtime.AgentRuntimePathLayout(catalog.StateRoot));
    }

    /// <summary>
    /// What <c>alta plugin</c> does with the source plugins of a window: it builds one again while the host runs,
    /// and shows the folder of one in the code editor.
    /// </summary>
    /// <param name="runtime">The plugin runtime of the host.</param>
    /// <param name="editor">Where the window is asked to open its code editor.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> or <paramref name="editor"/> is null.</exception>
    internal static CodeAlta.LiveTool.AltaPluginWorkshop Workshop(PluginRuntimeManager runtime, DesktopEditorView editor)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(editor);
        return new(runtime)
        {
            OpenEditor = (package, file, line, column) => editor.OpenFolder(PluginFolder.Of(package, projectId: null), package.PackageDirectory, file, line, column),
        };
    }

    /// <summary>
    /// Whether the user turned plugins off for this launch (<c>CODEALTA_DISABLE_PLUGINS=1</c>): no plugin is
    /// built, loaded or activated, the built-in ones included.
    /// </summary>
    internal static bool SafeMode => PluginRuntimeConfigResolver.IsSafeModeEnabled([]);

    /// <summary>
    /// Writes what went wrong while the plugins started (a source plugin that did not build or load, an
    /// invalid configuration) to the application log, which the window shows under Application Logs.
    /// </summary>
    /// <param name="runtime">The host's plugin runtime, after it started.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    internal static void LogStartupDiagnostics(PluginRuntimeManager runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var logger = LogManager.GetLogger("CodeAlta.Desktop.Plugins");
        foreach (var diagnostic in runtime.Diagnostics)
        {
            var subject = diagnostic.RuntimeKey ?? diagnostic.PackageId;
            var text = subject is null ? diagnostic.Message : $"{subject}: {diagnostic.Message}";
            if (diagnostic.Severity >= PluginDiagnosticSeverity.Error) logger.Error($"Plugin {diagnostic.Source} failed: {text}");
            else if (diagnostic.Severity == PluginDiagnosticSeverity.Warning) logger.Warn($"Plugin {diagnostic.Source}: {text}");
        }
    }

    /// <summary>
    /// The notice the window shows when plugins failed to build or start: it names them and says where the
    /// reason is. Null when none failed.
    /// </summary>
    /// <param name="diagnostics">The diagnostics of the plugin runtime after it started.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is null.</exception>
    internal static string? DescribeStartupFailures(IReadOnlyList<PluginRuntimeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var failed = diagnostics.Where(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.PackageId ?? diagnostic.RuntimeKey)
            .Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return failed.Length switch
        {
            0 => null,
            1 => $"The plugin {failed[0]} could not be started. Settings > Plugins shows why.",
            <= 3 => $"The plugins {string.Join(", ", failed)} could not be started. Settings > Plugins shows why.",
            _ => $"{failed.Length} plugins could not be started. Settings > Plugins shows why.",
        };
    }
}

/// <summary>
/// The services the desktop host gives its plugins: the <c>alta</c> commands, the window (dialogs,
/// notifications, the prompt and the session of the pane a command was started from), and a workspace whose
/// "selected project" is the project of the running operation.
/// </summary>
/// <remarks>
/// The window shows several sessions at once and runs them concurrently, so no project or session is
/// selected for the host as a whole. They are those of the session whose tool call is running, or of the
/// pane where a plugin command was started; outside both there is none, and a plugin uses the project its
/// operation context carries.
/// </remarks>
internal sealed class DesktopPluginServices(IPluginAltaService alta, DesktopPluginUi ui, DesktopCanvases? canvases = null, DesktopPluginIdentities? identities = null,
    IPluginAppFileService? appFiles = null, DesktopPluginAsks? asks = null) : IPluginServices
{
    private readonly NoopPluginServices _inner = NoopPluginServices.Create();
    private readonly DesktopPluginUi _ui = ui ?? throw new ArgumentNullException(nameof(ui));

    public Logger Logger => _inner.Logger;

    public IPluginUiService Ui => _ui;

    public IPluginStateStore State => _inner.State;

    public IPluginDatabase Database => _inner.Database;

    public IPluginWorkspaceService Workspace { get; } = new RunWorkspace(ui);

    public IPluginSessionService Sessions => _ui;

    public IPluginPromptService Prompts => _ui;

    public IPluginAgentService Agents => _inner.Agents;

    public IPluginTaskService Tasks => _inner.Tasks;

    public IPluginAltaService Alta { get; } = alta;

    // The tabs plugins provide; a host started without them has no window to show any.
    public IPluginCanvasService Canvases { get; } = (IPluginCanvasService?)canvases ?? NoopPluginCanvasService.Instance;

    // The identity of each session, of the space shown and of a project, once the host runs.
    public IPluginIdentityService Identity { get; } = (IPluginIdentityService?)identities ?? NoopPluginIdentityService.Instance;

    // The files of the window's page, which a plugin serves to a browser of its own (the page of the phones).
    public IPluginAppFileService AppFiles { get; } = appFiles ?? NoopPluginAppFileService.Instance;

    // The questions and the review requests of the sessions, once the host runs, which the page of the phones answers.
    public IPluginAskService Asks { get; } = (IPluginAskService?)asks ?? NoopPluginAskService.Instance;

    private sealed class RunWorkspace(DesktopPluginUi ui) : IPluginWorkspaceService
    {
        public string? SelectedProjectId => PluginOrchestrationBridge.CurrentToolOperation?.ProjectId ?? ui.Scope?.ProjectId;

        public string? SelectedProjectPath => PluginOrchestrationBridge.CurrentToolOperation?.ProjectPath ?? ui.Scope?.ProjectPath;

        public string? SelectedSpaceId => ui.Scope?.SpaceId;

        public IReadOnlyList<string> ProjectPaths => SelectedProjectPath is { } path ? [path] : [];

        public string? GetSelectedProjectPath(string relativePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            return SelectedProjectPath is { } root ? Path.GetFullPath(Path.Combine(root, relativePath)) : null;
        }

        public bool IsInsideSelectedProject(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (SelectedProjectPath is not { } root) return false;
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// The identities the plugins of the window work under: the one of a session's space as the session attached with
/// it, the one of the space the window shows, and the one of the space the new sessions of a project go to. Until
/// the host is attached (while the plugins start), every work has the usual settings.
/// </summary>
internal sealed class DesktopPluginIdentities : IPluginIdentityService
{
    private Func<string?, CancellationToken, Task<CodeAlta.Orchestration.Runtime.SessionIdentity>>? _session;
    private Func<string, CancellationToken, Task<CodeAlta.Orchestration.Runtime.SessionIdentity>>? _project;
    private Func<string, CancellationToken, Task<CodeAlta.Orchestration.Runtime.SessionIdentity>>? _window;

    /// <summary>Gives the identities of the host once it runs.</summary>
    /// <param name="session">Gives the identity of a session, or of the space the window shows for null.</param>
    /// <param name="project">Gives the identity of the space the new sessions of a project, by its folder, go to.</param>
    /// <param name="window">
    /// Gives the identity of the space the window shows with the CLI profiles of a project, by its folder; null for the
    /// one of the window alone.
    /// </param>
    internal void Attach(Func<string?, CancellationToken, Task<CodeAlta.Orchestration.Runtime.SessionIdentity>> session,
        Func<string, CancellationToken, Task<CodeAlta.Orchestration.Runtime.SessionIdentity>> project,
        Func<string, CancellationToken, Task<CodeAlta.Orchestration.Runtime.SessionIdentity>>? window = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(project);
        Volatile.Write(ref _window, window);
        Volatile.Write(ref _project, project);
        Volatile.Write(ref _session, session);
    }

    /// <inheritdoc />
    public async ValueTask<PluginIdentity> GetIdentityAsync(string? sessionId, CancellationToken cancellationToken)
        => Volatile.Read(ref _session) is { } session ? ToPlugin(await session(sessionId, cancellationToken).ConfigureAwait(false)) : PluginIdentity.Usual;

    /// <inheritdoc />
    public async ValueTask<PluginIdentity> GetProjectIdentityAsync(string projectPath, CancellationToken cancellationToken)
        => Volatile.Read(ref _project) is { } project && !string.IsNullOrWhiteSpace(projectPath)
            ? ToPlugin(await project(projectPath, cancellationToken).ConfigureAwait(false))
            : PluginIdentity.Usual;

    /// <inheritdoc />
    public async ValueTask<PluginIdentity> GetWindowIdentityAsync(string projectPath, CancellationToken cancellationToken)
        => Volatile.Read(ref _window) is { } window && !string.IsNullOrWhiteSpace(projectPath)
            ? ToPlugin(await window(projectPath, cancellationToken).ConfigureAwait(false))
            : await GetIdentityAsync(null, cancellationToken).ConfigureAwait(false);

    // What a plugin is given: references only, as the space has them.
    internal static PluginIdentity ToPlugin(CodeAlta.Orchestration.Runtime.SessionIdentity identity)
        => identity.IsUsual
            ? PluginIdentity.Usual with { SpaceId = identity.SpaceId }
            : new PluginIdentity
            {
                SpaceId = identity.SpaceId,
                Environment = identity.Environment,
                JiraSite = identity.Identity?.Jira?.Site,
                JiraEmail = identity.Identity?.Jira?.Email,
                JiraTokenVariable = identity.Identity?.Jira?.TokenEnv,
            };
}
