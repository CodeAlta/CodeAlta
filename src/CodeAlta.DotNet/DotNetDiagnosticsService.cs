using System.Diagnostics;
using CodeAlta.Persistence;
using CodeAlta.Search;

namespace CodeAlta.DotNet;

/// <summary>
/// Runs .NET diagnostics/build checks and persists results as artifacts.
/// </summary>
public sealed class DotNetDiagnosticsService
{
    private readonly ArtifactStore _artifactStore;
    private readonly ArtifactRepository _artifactRepository;
    private readonly Indexer _indexer;
    private readonly DotNetOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotNetDiagnosticsService"/> class.
    /// </summary>
    /// <param name="artifactStore">Artifact store.</param>
    /// <param name="artifactRepository">Artifact repository.</param>
    /// <param name="indexer">Search indexer.</param>
    /// <param name="options">DotNet options.</param>
    /// <exception cref="ArgumentNullException">Thrown when required dependencies are <see langword="null"/>.</exception>
    public DotNetDiagnosticsService(
        ArtifactStore artifactStore,
        ArtifactRepository artifactRepository,
        Indexer indexer,
        DotNetOptions options)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(artifactRepository);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(options);

        _artifactStore = artifactStore;
        _artifactRepository = artifactRepository;
        _indexer = indexer;
        _options = options;
    }

    /// <summary>
    /// Runs <c>dotnet build</c> and persists diagnostics output.
    /// </summary>
    /// <param name="targetPath">Repository root, solution path, or project path.</param>
    /// <param name="projectId">Optional project id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Diagnostics result.</returns>
    public async Task<DotNetDiagnosticsResult> RunBuildAsync(
        string targetPath,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("Target path is required.", nameof(targetPath));
        }

        var normalizedTargetPath = Path.GetFullPath(targetPath);
        var workingDirectory = Directory.Exists(normalizedTargetPath)
            ? normalizedTargetPath
            : Path.GetDirectoryName(normalizedTargetPath) ?? Environment.CurrentDirectory;

        var argumentTarget = Directory.Exists(normalizedTargetPath)
            ? string.Empty
            : $" \"{normalizedTargetPath}\"";
        var processStartInfo = new ProcessStartInfo("dotnet", $"build{argumentTarget} -nologo")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process
        {
            StartInfo = processStartInfo,
            EnableRaisingEvents = false,
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var output = await outputTask.ConfigureAwait(false);
        var errors = await errorTask.ConfigureAwait(false);
        var combined = string.Join(
            "\n",
            new[] { output, errors }.Where(static x => !string.IsNullOrWhiteSpace(x)));

        var artifactRoot = ResolveArtifactRoot(workingDirectory);
        Directory.CreateDirectory(artifactRoot);

        var artifactId = ArtifactId.NewVersion7();
        var now = DateTimeOffset.UtcNow;
        var artifactPath = Path.Combine(
            artifactRoot,
            "diagnostics",
            $"{now:yyyyMMddTHHmmssfffZ}_{artifactId}.md");
        var markdown =
            $"""
            # dotnet build diagnostics

            - Target: `{normalizedTargetPath}`
            - ExitCode: `{process.ExitCode}`
            - Timestamp: `{now:O}`

            ```text
            {combined.Trim()}
            ```
            """;

        await _artifactStore.WriteMarkdownAsync(
            artifactPath,
            new ArtifactDocument
            {
                Frontmatter = new ArtifactFrontmatter
                {
                    Id = artifactId.ToString(),
                    Type = "diagnostics.build",
                    ProjectId = projectId,
                    Title = $"dotnet build diagnostics ({now:O})",
                    Tags = ["dotnet", "diagnostics"],
                },
                Body = markdown,
            },
            cancellationToken).ConfigureAwait(false);

        var uri = $"artifact://dotnet/diagnostics/{artifactId}";
        await _artifactRepository.UpsertAsync(
            new ArtifactRecord
            {
                ArtifactId = artifactId,
                Uri = uri,
                ProjectId = projectId,
                Type = "diagnostics.build",
                Path = Path.GetFullPath(artifactPath),
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken).ConfigureAwait(false);

        await _indexer.EnqueueAsync(
            new IndexingJob
            {
                Documents =
                [
                    new DocumentInput
                    {
                        SourceKind = "artifact",
                        SourceId = uri,
                        ProjectId = projectId,
                        Title = "dotnet build diagnostics",
                        MimeType = "text/markdown",
                        Text = markdown,
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);
        await _indexer.ProcessNextAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        return new DotNetDiagnosticsResult
        {
            ExitCode = process.ExitCode,
            Success = process.ExitCode == 0,
            ArtifactId = artifactId,
            ArtifactPath = Path.GetFullPath(artifactPath),
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
}
