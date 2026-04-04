namespace CodeAlta.Search;

/// <summary>
/// Provides bounded recent-usage persistence for project files and directories.
/// </summary>
public interface IProjectFileUsageStore
{
    /// <summary>
    /// Lists recent usage entries for a project root.
    /// </summary>
    /// <param name="projectRoot">Project root path.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recent usage entries.</returns>
    ValueTask<IReadOnlyList<ProjectFileUsageEntry>> GetRecentAsync(
        string projectRoot,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets usage data keyed by normalized relative path.
    /// </summary>
    /// <param name="projectRoot">Project root path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Usage data keyed by relative path.</returns>
    ValueTask<IReadOnlyDictionary<string, ProjectFileUsageEntry>> GetUsageByRelativePathAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a usage event.
    /// </summary>
    /// <param name="usageEvent">Usage event to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RecordAsync(
        ProjectFileUsageEvent usageEvent,
        CancellationToken cancellationToken = default);
}
