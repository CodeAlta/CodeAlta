using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugin.GitHub;
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
            Id = "github", DisplayName = "GitHub",
            Description = "Exposes the GitHub CLI to sessions when it is available.",
            PluginType = typeof(GitHubPlugin), Factory = static () => new GitHubPlugin(),
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
}

/// <summary>
/// The services the desktop host gives its plugins: the <c>alta</c> commands, and a workspace whose
/// "selected project" is the project of the session whose tool call is running.
/// </summary>
/// <remarks>
/// The window shows several sessions at once and runs them concurrently, so no project is selected for the
/// host as a whole. Outside a tool call the workspace names no project, and a plugin uses the project its
/// operation context carries.
/// </remarks>
internal sealed class DesktopPluginServices(IPluginAltaService alta) : IPluginServices
{
    private readonly NoopPluginServices _inner = NoopPluginServices.Create();

    public Logger Logger => _inner.Logger;

    public IPluginUiService Ui => _inner.Ui;

    public IPluginStateStore State => _inner.State;

    public IPluginWorkspaceService Workspace { get; } = new RunWorkspace();

    public IPluginSessionService Sessions => _inner.Sessions;

    public IPluginPromptService Prompts => _inner.Prompts;

    public IPluginAgentService Agents => _inner.Agents;

    public IPluginTaskService Tasks => _inner.Tasks;

    public IPluginAltaService Alta { get; } = alta;

    private sealed class RunWorkspace : IPluginWorkspaceService
    {
        public string? SelectedProjectId => PluginOrchestrationBridge.CurrentToolOperation?.ProjectId;

        public string? SelectedProjectPath => PluginOrchestrationBridge.CurrentToolOperation?.ProjectPath;

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
