using System.Text;
using CodeAlta.Persistence;
using CodeAlta.Search;

namespace CodeAlta.DotNet;

/// <summary>
/// Refreshes .NET knowledge artifacts and search index entries.
/// </summary>
public sealed class DotNetIndexService
{
    private readonly DotNetWorkspaceService _workspaceService;
    private readonly SymbolIndexService _symbolIndexService;
    private readonly ArtifactStore _artifactStore;
    private readonly ArtifactRepository _artifactRepository;
    private readonly Indexer _indexer;
    private readonly DotNetOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotNetIndexService"/> class.
    /// </summary>
    /// <param name="workspaceService">Workspace discovery service.</param>
    /// <param name="symbolIndexService">Symbol index service.</param>
    /// <param name="artifactStore">Artifact store.</param>
    /// <param name="artifactRepository">Artifact repository.</param>
    /// <param name="indexer">Search indexer.</param>
    /// <param name="options">DotNet options.</param>
    public DotNetIndexService(
        DotNetWorkspaceService workspaceService,
        SymbolIndexService symbolIndexService,
        ArtifactStore artifactStore,
        ArtifactRepository artifactRepository,
        Indexer indexer,
        DotNetOptions options)
    {
        ArgumentNullException.ThrowIfNull(workspaceService);
        ArgumentNullException.ThrowIfNull(symbolIndexService);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(artifactRepository);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(options);

        _workspaceService = workspaceService;
        _symbolIndexService = symbolIndexService;
        _artifactStore = artifactStore;
        _artifactRepository = artifactRepository;
        _indexer = indexer;
        _options = options;
    }

    /// <summary>
    /// Refreshes .NET project graph/symbol artifacts and indexes them.
    /// </summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="projectId">Optional project id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Refresh result summary.</returns>
    public async Task<DotNetIndexRefreshResult> RefreshIndexAsync(
        string repoRoot,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var artifactRoot = ResolveArtifactRoot(repoRoot);
        Directory.CreateDirectory(artifactRoot);

        var snapshot = await _workspaceService.LoadAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        var symbols = await _symbolIndexService.BuildIndexAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var graphArtifactId = ArtifactId.NewVersion7();
        var graphPath = Path.Combine(
            artifactRoot,
            "project-graph.md");
        var graphMarkdown = BuildProjectGraphMarkdown(snapshot);

        await _artifactStore.WriteMarkdownAsync(
            graphPath,
            new ArtifactDocument
            {
                Frontmatter = new ArtifactFrontmatter
                {
                    Id = graphArtifactId.ToString(),
                    Type = "knowledge.dotnet.project-graph",
                    ProjectId = projectId,
                    Title = "DotNet project graph",
                    Tags = ["dotnet", "project-graph"],
                },
                Body = graphMarkdown,
            },
            cancellationToken).ConfigureAwait(false);
        await _artifactRepository.UpsertAsync(
            new ArtifactRecord
            {
                ArtifactId = graphArtifactId,
                Uri = "artifact://dotnet/project-graph",
                ProjectId = projectId,
                Type = "knowledge.dotnet.project-graph",
                Path = Path.GetFullPath(graphPath),
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken).ConfigureAwait(false);

        var documents = new List<DocumentInput>
        {
            new()
            {
                SourceKind = "artifact",
                SourceId = "artifact://dotnet/project-graph",
                ProjectId = projectId,
                Title = "DotNet project graph",
                MimeType = "text/markdown",
                Text = graphMarkdown,
            },
        };

        foreach (var symbol in symbols.Take(500))
        {
            var symbolId = ArtifactId.NewVersion7();
            var symbolPath = Path.Combine(
                artifactRoot,
                "symbols",
                $"{SanitizeFileName(symbol.FullyQualifiedName)}_{symbolId}.md");
            var symbolMarkdown =
                $"""
                # {symbol.FullyQualifiedName}

                - Kind: `{symbol.Kind}`
                - File: `{symbol.FilePath}`
                - Range: `{symbol.StartLine}-{symbol.EndLine}`
                - Source: `file://{symbol.FilePath}#L{symbol.StartLine}`

                {symbol.Summary}
                """;
            await _artifactStore.WriteMarkdownAsync(
                symbolPath,
                new ArtifactDocument
                {
                    Frontmatter = new ArtifactFrontmatter
                    {
                        Id = symbolId.ToString(),
                        Type = "knowledge.dotnet.symbol",
                        ProjectId = projectId,
                        Title = symbol.FullyQualifiedName,
                        Tags = ["dotnet", "symbol"],
                    },
                    Body = symbolMarkdown,
                },
                cancellationToken).ConfigureAwait(false);
            var uri = $"artifact://dotnet/symbol/{symbolId}";
            await _artifactRepository.UpsertAsync(
                new ArtifactRecord
                {
                    ArtifactId = symbolId,
                    Uri = uri,
                    ProjectId = projectId,
                    Type = "knowledge.dotnet.symbol",
                    Path = Path.GetFullPath(symbolPath),
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                cancellationToken).ConfigureAwait(false);

            documents.Add(
                new DocumentInput
                {
                    SourceKind = "artifact",
                    SourceId = uri,
                    ProjectId = projectId,
                    Title = symbol.FullyQualifiedName,
                    MimeType = "text/markdown",
                    Text = symbolMarkdown,
                });
        }

        await _indexer.EnqueueAsync(
            new IndexingJob
            {
                Documents = documents,
            },
            cancellationToken).ConfigureAwait(false);
        await _indexer.ProcessNextAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        return new DotNetIndexRefreshResult
        {
            ProjectGraphArtifactId = graphArtifactId,
            SymbolCount = symbols.Count,
            IndexedDocumentCount = documents.Count,
        };
    }

    private string ResolveArtifactRoot(string repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(_options.ArtifactRoot))
        {
            return Path.GetFullPath(_options.ArtifactRoot);
        }

        return Path.Combine(
            Path.GetFullPath(repoRoot),
            ".alta",
            "knowledge",
            "dotnet");
    }

    private static string BuildProjectGraphMarkdown(DotNetWorkspaceSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# DotNet Project Graph");
        builder.AppendLine();
        builder.AppendLine($"- RepoRoot: `{snapshot.RepoRoot}`");
        builder.AppendLine($"- Solutions: `{snapshot.SolutionPaths.Count}`");
        builder.AppendLine($"- Projects: `{snapshot.Projects.Count}`");
        builder.AppendLine();
        builder.AppendLine("## Solutions");
        builder.AppendLine();
        foreach (var solution in snapshot.SolutionPaths)
        {
            builder.AppendLine($"- `{solution}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Projects");
        builder.AppendLine();
        foreach (var project in snapshot.Projects)
        {
            builder.AppendLine($"- `{project.Name}` ({project.Language}) `{project.RelativePath}`");
        }

        return builder.ToString();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray());
        return sanitized.Length > 80 ? sanitized[..80] : sanitized;
    }
}
