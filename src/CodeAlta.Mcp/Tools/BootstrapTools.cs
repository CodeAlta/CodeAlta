using System.ComponentModel;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Bootstrap;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeAlta.Mcp.Tools;

/// <summary>
/// MCP tools for bootstrapping the global repo and project checkouts.
/// </summary>
[McpServerToolType]
public sealed class BootstrapTools
{
    private readonly ProjectCatalog _catalog;
    private readonly ProjectResolver _resolver;
    private readonly GlobalRepoBootstrapper _globalRepoBootstrapper;
    private readonly GlobalRepoSyncService _globalRepoSync;
    private readonly ProjectBootstrapper _projectBootstrapper;

    /// <summary>
    /// Initializes a new instance of the <see cref="BootstrapTools"/> class.
    /// </summary>
    public BootstrapTools(
        ProjectCatalog catalog,
        ProjectResolver resolver,
        GlobalRepoBootstrapper globalRepoBootstrapper,
        GlobalRepoSyncService globalRepoSync,
        ProjectBootstrapper projectBootstrapper)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(globalRepoBootstrapper);
        ArgumentNullException.ThrowIfNull(globalRepoSync);
        ArgumentNullException.ThrowIfNull(projectBootstrapper);

        _catalog = catalog;
        _resolver = resolver;
        _globalRepoBootstrapper = globalRepoBootstrapper;
        _globalRepoSync = globalRepoSync;
        _projectBootstrapper = projectBootstrapper;
    }

    /// <summary>
    /// Ensures the global CodeAlta repo exists.
    /// </summary>
    [McpServerTool(Name = "codealta.bootstrap.ensure_global_repo"), Description("Ensures the global knowledge repo exists and is initialized.")]
    public async Task<string> EnsureGlobalRepoAsync(
        [Description("Optional override for global repo root. Default is ~/.alta/repo.")] string? globalRepoRoot = null,
        [Description("Optional remote URL to clone or set as origin.")] string? remoteUrl = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = string.IsNullOrWhiteSpace(globalRepoRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta", "repo")
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
        [Description("Optional override for global repo root. Default is ~/.alta/repo.")] string? globalRepoRoot = null,
        [Description("Optional commit message used for debounced sync commits.")] string? commitMessage = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = string.IsNullOrWhiteSpace(globalRepoRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta", "repo")
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
    /// Ensures projects are checked out under the resolved scope.
    /// </summary>
    [McpServerTool(Name = "codealta.bootstrap.ensure_projects_checked_out"), Description("Clones missing repos and optionally updates existing ones for a scope.")]
    public async Task<string> EnsureProjectsCheckedOutAsync(
        [Description("Scope kind: global|project.")] string kind,
        [Description("Project slug for project scope.")] string? projectSlug = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        [Description("Whether to pull updates for existing checkouts.")] bool updateExisting = true,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selector = ParseSelector(kind, projectSlug);
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
            var execution = await _projectBootstrapper.EnsureCheckedOutAsync(
                resolution,
                updateExisting,
                sink,
                cancellationToken).ConfigureAwait(false);

            results.Add(
                new
                {
                    kind = resolution.Kind.ToString().ToLowerInvariant(),
                    projectSlug = resolution.SelectedProject?.Slug,
                    projects = execution.Select(static x => new
                    {
                        projectSlug = x.ProjectSlug,
                        checkoutPath = x.CheckoutPath,
                        action = x.Action.ToString().ToLowerInvariant(),
                        success = x.Success,
                        message = x.Message,
                    }).ToArray(),
                });
        }

        return McpToolJson.Serialize(results.ToArray());
    }

    private static ScopeSelector ParseSelector(string kind, string? projectSlug)
    {
        return kind.Trim().ToLowerInvariant() switch
        {
            "global" => ScopeSelector.Global(),
            "project" => ScopeSelector.Project(projectSlug ?? string.Empty),
            _ => throw new ArgumentException("kind must be one of global, project.", nameof(kind)),
        };
    }
}

