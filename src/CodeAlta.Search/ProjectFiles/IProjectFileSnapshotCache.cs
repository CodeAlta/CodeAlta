namespace CodeAlta.Search;

/// <summary>
/// Provides shared stale-while-revalidate snapshot caching per project root.
/// </summary>
public interface IProjectFileSnapshotCache
{
    /// <summary>
    /// Gets the cached snapshot entry for a project root when one exists.
    /// </summary>
    /// <param name="projectRoot">Project root path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cache entry or <see langword="null"/> when absent.</returns>
    ValueTask<ProjectFileSnapshotCacheEntry?> GetAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a refreshed snapshot and clears the dirty marker.
    /// </summary>
    /// <param name="snapshot">Snapshot to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SetAsync(ProjectFileSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a project snapshot as dirty without discarding the last snapshot.
    /// </summary>
    /// <param name="projectRoot">Project root path.</param>
    /// <param name="reason">Invalidation reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask MarkDirtyAsync(
        string projectRoot,
        ProjectFileInvalidationReason reason,
        CancellationToken cancellationToken = default);
}
