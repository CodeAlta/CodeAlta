using System.ComponentModel;
using CodeAlta.Workspaces;
using CodeAlta.Workspaces.Bootstrap;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeAlta.Mcp.Tools;

/// <summary>
/// MCP tools for bootstrapping the global repo and workspace checkouts.
/// </summary>
[McpServerToolType]
public sealed class BootstrapTools
{
    private readonly WorkspaceCatalog _catalog;
    private readonly WorkspaceResolver _resolver;
    private readonly GlobalRepoBootstrapper _globalRepoBootstrapper;
    private readonly GlobalRepoSyncService _globalRepoSync;
    private readonly WorkspaceBootstrapper _workspaceBootstrapper;

    /// <summary>
    /// Initializes a new instance of the <see cref="BootstrapTools"/> class.
    /// </summary>
    public BootstrapTools(
        WorkspaceCatalog catalog,
        WorkspaceResolver resolver,
        GlobalRepoBootstrapper globalRepoBootstrapper,
        GlobalRepoSyncService globalRepoSync,
        WorkspaceBootstrapper workspaceBootstrapper)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(globalRepoBootstrapper);
        ArgumentNullException.ThrowIfNull(globalRepoSync);
        ArgumentNullException.ThrowIfNull(workspaceBootstrapper);

        _catalog = catalog;
        _resolver = resolver;
        _globalRepoBootstrapper = globalRepoBootstrapper;
        _globalRepoSync = globalRepoSync;
        _workspaceBootstrapper = workspaceBootstrapper;
    }

    /// <summary>
    /// Ensures the global CodeAlta repo exists.
    /// </summary>
    [McpServerTool(Name = "codealta.bootstrap.ensure_global_repo"), Description("Ensures the global knowledge repo exists and is initialized.")]
    public async Task<string> EnsureGlobalRepoAsync(
        [Description("Optional override for global repo root. Default is ~/.codealta/repo.")] string? globalRepoRoot = null,
        [Description("Optional remote URL to clone or set as origin.")] string? remoteUrl = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = string.IsNullOrWhiteSpace(globalRepoRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codealta", "repo")
            : globalRepoRoot;

        var sink = progress is null ? null : new Progress<string>(message =>
            progress.Report(
                new ProgressNotificationValue
                {
                    Progress = 0,
                    Total = 0,
                    Message = message,
                }));

        var result = await _globalRepoBootstrapper.EnsureAsync(
            root,
            remoteUrl,
            sink,
            cancellationToken).ConfigureAwait(false);

        return McpToolJson.Serialize(
            new
            {
                globalRepoRoot = result.GlobalRepoRoot,
                createdDirectory = result.CreatedDirectory,
                initializedRepository = result.InitializedRepository,
                clonedRepository = result.ClonedRepository,
                originRemoteUrl = result.OriginRemoteUrl,
            });
    }

    /// <summary>
    /// Syncs the global CodeAlta repo (pull/commit/push).
    /// </summary>
    [McpServerTool(Name = "codealta.bootstrap.sync"), Description("Syncs the global knowledge repo (pull/commit/push).")]
    public async Task<string> SyncAsync(
        [Description("Optional override for global repo root. Default is ~/.codealta/repo.")] string? globalRepoRoot = null,
        [Description("Optional commit message used for debounced sync commits.")] string? commitMessage = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = string.IsNullOrWhiteSpace(globalRepoRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codealta", "repo")
            : globalRepoRoot;

        var sink = progress is null ? null : new Progress<string>(message =>
            progress.Report(
                new ProgressNotificationValue
                {
                    Progress = 0,
                    Total = 0,
                    Message = message,
                }));

        var result = await _globalRepoSync.SyncAsync(
            root,
            string.IsNullOrWhiteSpace(commitMessage) ? "CodeAlta sync" : commitMessage,
            sink,
            cancellationToken).ConfigureAwait(false);

        return McpToolJson.Serialize(
            new
            {
                committedChanges = result.CommittedChanges,
                pulled = result.Pulled,
                pushed = result.Pushed,
            });
    }

    /// <summary>
    /// Ensures workspace projects are checked out under the resolved scope.
    /// </summary>
    [McpServerTool(Name = "codealta.bootstrap.ensure_workspace_checked_out"), Description("Clones missing repos and optionally updates existing ones for a scope.")]
    public async Task<string> EnsureWorkspaceCheckedOutAsync(
        [Description("Scope kind: global|workspace|project.")] string kind,
        [Description("Workspace key for workspace scope.")] string? workspaceKey = null,
        [Description("Project key for project scope.")] string? projectKey = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        [Description("Whether to pull updates for existing checkouts.")] bool updateExisting = true,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selector = ParseSelector(kind, workspaceKey, projectKey);
        MachineProfile? machineProfile = null;
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            machineProfile = await _catalog.LoadMachineProfileAsync(machineId, cancellationToken).ConfigureAwait(false);
        }

        var resolutions = await _resolver.ResolveAsync(selector, machineProfile, cancellationToken).ConfigureAwait(false);

        var sink = progress is null ? null : new Progress<string>(message =>
            progress.Report(
                new ProgressNotificationValue
                {
                    Progress = 0,
                    Total = 0,
                    Message = message,
                }));

        var results = new List<object>();
        foreach (var resolution in resolutions)
        {
            var execution = await _workspaceBootstrapper.EnsureCheckedOutAsync(
                resolution,
                updateExisting,
                sink,
                cancellationToken).ConfigureAwait(false);

            results.Add(
                new
                {
                    workspaceKey = resolution.Workspace.Key,
                    projects = execution.Select(static x => new
                    {
                        projectKey = x.ProjectKey,
                        checkoutPath = x.CheckoutPath,
                        action = x.Action.ToString().ToLowerInvariant(),
                        success = x.Success,
                        message = x.Message,
                    }).ToArray(),
                });
        }

        return McpToolJson.Serialize(results.ToArray());
    }

    private static ScopeSelector ParseSelector(string kind, string? workspaceKey, string? projectKey)
    {
        return kind.Trim().ToLowerInvariant() switch
        {
            "global" => ScopeSelector.Global(),
            "workspace" => ScopeSelector.Workspace(workspaceKey ?? string.Empty),
            "project" => ScopeSelector.Project(projectKey ?? string.Empty),
            _ => throw new ArgumentException("kind must be one of global, workspace, project.", nameof(kind)),
        };
    }
}
