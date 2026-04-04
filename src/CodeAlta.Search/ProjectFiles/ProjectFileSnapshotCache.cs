using System.Collections.Concurrent;

namespace CodeAlta.Search;

/// <summary>
/// Provides an in-memory shared snapshot cache for project-file search.
/// </summary>
public sealed class ProjectFileSnapshotCache : IProjectFileSnapshotCache
{
    private readonly ConcurrentDictionary<string, ProjectFileSnapshotCacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ValueTask<ProjectFileSnapshotCacheEntry?> GetAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root is required.", nameof(projectRoot));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _entries.TryGetValue(NormalizeProjectRoot(projectRoot), out var entry)
                ? entry
                : null);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(ProjectFileSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        _entries[NormalizeProjectRoot(snapshot.ProjectRoot)] = new ProjectFileSnapshotCacheEntry(
            snapshot,
            IsDirty: false,
            LastInvalidatedAt: null,
            LastInvalidationReason: null);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask MarkDirtyAsync(
        string projectRoot,
        ProjectFileInvalidationReason reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root is required.", nameof(projectRoot));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = DateTimeOffset.UtcNow;
        var normalized = NormalizeProjectRoot(projectRoot);
        _entries.AddOrUpdate(
            normalized,
            _ => new ProjectFileSnapshotCacheEntry(
                Snapshot: null,
                IsDirty: true,
                LastInvalidatedAt: timestamp,
                LastInvalidationReason: reason),
            (_, existing) => existing with
            {
                IsDirty = true,
                LastInvalidatedAt = timestamp,
                LastInvalidationReason = reason,
            });

        return ValueTask.CompletedTask;
    }

    private static string NormalizeProjectRoot(string projectRoot)
    {
        var fullPath = Path.GetFullPath(projectRoot);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
