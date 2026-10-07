using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugin.Mcp;
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
        },
        new()
        {
            Id = Rpc.ComposerStatusService.McpPluginId, DisplayName = "MCP",
            Description = "Connects CodeAlta to configured Model Context Protocol servers.",
            PluginType = typeof(McpPlugin), Factory = static () => new McpPlugin(),
        },
        new()
        {
            Id = "statistics", DisplayName = "Statistics",
            Description = "Projects transient per-turn and session statistics from normalized agent events.",
            PluginType = typeof(StatisticsPlugin), Factory = static () => new StatisticsPlugin(),
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
    /// <exception cref="ArgumentNullException"><paramref name="ui"/> or <paramref name="sessions"/> is null.</exception>
    internal static IReadOnlyList<BuiltInPluginDefinition> ForWindow(Ui.IDesktopUi ui, Ui.DesktopUiSessions sessions, bool reviewsCommands)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(sessions);
        return reviewsCommands ? BuiltIns : [.. BuiltIns, Ui.DesktopUiPlugin.Definition(ui, sessions)];
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
internal sealed class DesktopPluginServices(IPluginAltaService alta, DesktopPluginUi ui) : IPluginServices
{
    private readonly NoopPluginServices _inner = NoopPluginServices.Create();
    private readonly DesktopPluginUi _ui = ui ?? throw new ArgumentNullException(nameof(ui));

    public Logger Logger => _inner.Logger;

    public IPluginUiService Ui => _ui;

    public IPluginStateStore State => _inner.State;

    public IPluginWorkspaceService Workspace { get; } = new RunWorkspace(ui);

    public IPluginSessionService Sessions => _ui;

    public IPluginPromptService Prompts => _ui;

    public IPluginAgentService Agents => _inner.Agents;

    public IPluginTaskService Tasks => _inner.Tasks;

    public IPluginAltaService Alta { get; } = alta;

    private sealed class RunWorkspace(DesktopPluginUi ui) : IPluginWorkspaceService
    {
        public string? SelectedProjectId => PluginOrchestrationBridge.CurrentToolOperation?.ProjectId ?? ui.Scope?.ProjectId;

        public string? SelectedProjectPath => PluginOrchestrationBridge.CurrentToolOperation?.ProjectPath ?? ui.Scope?.ProjectPath;

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
