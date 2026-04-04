namespace CodeAlta.Search;

/// <summary>
/// Provides session-based project file and directory search.
/// </summary>
public interface IProjectFileSearchService
{
    /// <summary>
    /// Creates a new incremental search session for the specified project root.
    /// </summary>
    /// <param name="options">Session creation options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created search session.</returns>
    ValueTask<IProjectFileSearchSession> CreateSessionAsync(
        ProjectFileSearchSessionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a normalized project-relative reference to a file or directory.
    /// </summary>
    /// <param name="query">Resolution request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolution result.</returns>
    ValueTask<ProjectFileResolution> ResolveAsync(
        ProjectFileResolveQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records recent usage for a file or directory.
    /// </summary>
    /// <param name="usageEvent">Usage event to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RecordUsageAsync(
        ProjectFileUsageEvent usageEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates cached snapshot data for the specified project.
    /// </summary>
    /// <param name="projectRoot">Project root path.</param>
    /// <param name="reason">Invalidation reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask InvalidateAsync(
        string projectRoot,
        ProjectFileInvalidationReason reason,
        CancellationToken cancellationToken = default);
}
