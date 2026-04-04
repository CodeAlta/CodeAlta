using CodeAlta.Persistence;

namespace CodeAlta.Search;

/// <summary>
/// Persists recent project-file usage in SQLite.
/// </summary>
public sealed class PersistentProjectFileUsageStore : IProjectFileUsageStore
{
    private readonly ProjectFileUsageRepository _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistentProjectFileUsageStore"/> class.
    /// </summary>
    /// <param name="repository">Usage repository.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="repository"/> is <see langword="null"/>.</exception>
    public PersistentProjectFileUsageStore(ProjectFileUsageRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ProjectFileUsageEntry>> GetRecentAsync(
        string projectRoot,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var records = await _repository.ListRecentAsync(projectRoot, limit, cancellationToken).ConfigureAwait(false);
        return records.Select(ToEntry).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, ProjectFileUsageEntry>> GetUsageByRelativePathAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var records = await _repository.ListByProjectAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<string, ProjectFileUsageEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            results[record.RelativePath] = ToEntry(record);
        }

        return results;
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(
        ProjectFileUsageEvent usageEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);

        await _repository.RecordAccessAsync(
                usageEvent.ProjectRoot,
                usageEvent.RelativePath,
                ToRepositoryKind(usageEvent.Kind),
                usageEvent.AccessedAt,
                usageEvent.AccessKind is null ? null : ToRepositoryAccessKind(usageEvent.AccessKind.Value),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ProjectFileUsageEntry ToEntry(ProjectFileUsageRecord record)
    {
        return new ProjectFileUsageEntry(
            record.ProjectRoot,
            record.RelativePath,
            ParseKind(record.Kind),
            record.LastAccessedAt,
            record.AccessCount,
            ParseAccessKind(record.LastAccessKind));
    }

    private static string ToRepositoryKind(ProjectFileSearchItemKind kind)
        => kind switch
        {
            ProjectFileSearchItemKind.File => "file",
            ProjectFileSearchItemKind.Directory => "directory",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static ProjectFileSearchItemKind ParseKind(string kind)
        => kind switch
        {
            "file" => ProjectFileSearchItemKind.File,
            "directory" => ProjectFileSearchItemKind.Directory,
            _ => throw new InvalidDataException($"Unknown project file usage kind '{kind}'."),
        };

    private static string ToRepositoryAccessKind(ProjectFileUsageAccessKind accessKind)
        => accessKind switch
        {
            ProjectFileUsageAccessKind.PopupAccepted => "popup_accepted",
            ProjectFileUsageAccessKind.PromptInserted => "prompt_inserted",
            ProjectFileUsageAccessKind.EditorOpened => "editor_opened",
            ProjectFileUsageAccessKind.CommandOpened => "command_opened",
            _ => throw new ArgumentOutOfRangeException(nameof(accessKind)),
        };

    private static ProjectFileUsageAccessKind? ParseAccessKind(string? accessKind)
        => accessKind switch
        {
            null => null,
            "popup_accepted" => ProjectFileUsageAccessKind.PopupAccepted,
            "prompt_inserted" => ProjectFileUsageAccessKind.PromptInserted,
            "editor_opened" => ProjectFileUsageAccessKind.EditorOpened,
            "command_opened" => ProjectFileUsageAccessKind.CommandOpened,
            _ => throw new InvalidDataException($"Unknown project file access kind '{accessKind}'."),
        };
}
