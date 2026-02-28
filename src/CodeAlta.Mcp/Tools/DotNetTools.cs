using System.ComponentModel;
using CodeAlta.DotNet;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeAlta.Mcp.Tools;

/// <summary>
/// MCP tools exposing .NET first-class services.
/// </summary>
[McpServerToolType]
public sealed class DotNetTools
{
    private readonly DotNetWorkspaceService _workspaceService;
    private readonly SymbolIndexService _symbols;
    private readonly DotNetContextProvider _context;
    private readonly DotNetIndexService _index;
    private readonly DotNetDiagnosticsService _diagnostics;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotNetTools"/> class.
    /// </summary>
    public DotNetTools(
        DotNetWorkspaceService workspaceService,
        SymbolIndexService symbols,
        DotNetContextProvider context,
        DotNetIndexService index,
        DotNetDiagnosticsService diagnostics)
    {
        ArgumentNullException.ThrowIfNull(workspaceService);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _workspaceService = workspaceService;
        _symbols = symbols;
        _context = context;
        _index = index;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Lists discovered .NET projects under a repository root.
    /// </summary>
    [McpServerTool(Name = "codealta.dotnet.list_projects"), Description("Lists discovered .NET projects under a repository root.")]
    public async Task<string> ListProjectsAsync(
        [Description("Repository root path.")] string repoRoot,
        CancellationToken cancellationToken = default)
    {
        var projects = await _workspaceService.ListProjectsAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        return McpToolJson.Serialize(projects.Select(static x => new
        {
            name = x.Name,
            language = x.Language,
            projectPath = x.ProjectPath,
            relativePath = x.RelativePath,
        }).ToArray());
    }

    /// <summary>
    /// Returns basic solution/project graph metadata for a repository root.
    /// </summary>
    [McpServerTool(Name = "codealta.dotnet.project_graph"), Description("Returns discovered solutions/projects for a repository root.")]
    public async Task<string> ProjectGraphAsync(
        [Description("Repository root path.")] string repoRoot,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _workspaceService.LoadAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        return McpToolJson.Serialize(
            new
            {
                repoRoot = snapshot.RepoRoot,
                solutions = snapshot.SolutionPaths,
                projects = snapshot.Projects.Select(static x => new
                {
                    name = x.Name,
                    language = x.Language,
                    projectPath = x.ProjectPath,
                    relativePath = x.RelativePath,
                }).ToArray(),
            });
    }

    /// <summary>
    /// Searches for symbols by name.
    /// </summary>
    [McpServerTool(Name = "codealta.dotnet.symbol_search"), Description("Searches for symbols by name under a repository root.")]
    public async Task<string> SymbolSearchAsync(
        [Description("Repository root path.")] string repoRoot,
        [Description("Symbol query text.")] string query,
        [Description("Maximum results.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        }

        var snapshot = await _workspaceService.LoadAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        var symbols = await _symbols.BuildIndexAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var matches = symbols
            .Where(x =>
                x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.FullyQualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(static x => new
            {
                kind = x.Kind,
                name = x.Name,
                fullyQualifiedName = x.FullyQualifiedName,
                filePath = x.FilePath,
                startLine = x.StartLine,
                endLine = x.EndLine,
                summary = x.Summary,
            }).ToArray();

        return McpToolJson.Serialize(matches);
    }

    /// <summary>
    /// Returns compact context snippets for a symbol query.
    /// </summary>
    [McpServerTool(Name = "codealta.dotnet.symbol_context"), Description("Returns compact context snippets for a symbol query.")]
    public async Task<string> SymbolContextAsync(
        [Description("Repository root path.")] string repoRoot,
        [Description("Symbol query text.")] string query,
        [Description("Maximum snippets.")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var snippets = await _context.SymbolContextAsync(repoRoot, query, limit, cancellationToken).ConfigureAwait(false);
        return McpToolJson.Serialize(snippets.Select(static x => new
        {
            title = x.Title,
            content = x.Content,
            sourceUri = x.SourceUri,
        }).ToArray());
    }

    /// <summary>
    /// Runs <c>dotnet build</c> and persists diagnostics output as artifacts.
    /// </summary>
    [McpServerTool(Name = "codealta.dotnet.run_diagnostics"), Description("Runs dotnet build and persists diagnostics output as artifacts.")]
    public async Task<string> RunDiagnosticsAsync(
        [Description("Repository root, solution path, or project path.")] string targetPath,
        [Description("Optional workspace id for artifact tagging.")] string? workspaceId = null,
        [Description("Optional project id for artifact tagging.")] string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _diagnostics.RunBuildAsync(targetPath, workspaceId, projectId, cancellationToken).ConfigureAwait(false);
        return McpToolJson.Serialize(
            new
            {
                success = result.Success,
                exitCode = result.ExitCode,
                artifactId = result.ArtifactId.ToString(),
                artifactPath = result.ArtifactPath,
            });
    }

    /// <summary>
    /// Refreshes indexed .NET artifacts and search documents.
    /// </summary>
    [McpServerTool(Name = "codealta.dotnet.refresh_index"), Description("Refreshes .NET artifacts and updates the search index for a repository root.")]
    public async Task<string> RefreshIndexAsync(
        [Description("Repository root path.")] string repoRoot,
        [Description("Optional workspace id for artifact tagging.")] string? workspaceId = null,
        [Description("Optional project id for artifact tagging.")] string? projectId = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(
            new ProgressNotificationValue
            {
                Progress = 0,
                Total = 0,
                Message = "Refreshing .NET index...",
            });
        var result = await _index.RefreshIndexAsync(repoRoot, workspaceId, projectId, cancellationToken).ConfigureAwait(false);
        progress?.Report(
            new ProgressNotificationValue
            {
                Progress = 0,
                Total = 0,
                Message = $"Indexed {result.IndexedDocumentCount} documents.",
            });

        return McpToolJson.Serialize(
            new
            {
                projectGraphArtifactId = result.ProjectGraphArtifactId.ToString(),
                symbolCount = result.SymbolCount,
                indexedDocumentCount = result.IndexedDocumentCount,
            });
    }
}
