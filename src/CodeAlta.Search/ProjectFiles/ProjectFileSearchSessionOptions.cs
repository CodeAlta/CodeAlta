namespace CodeAlta.Search;

/// <summary>
/// Provides configuration for a project-file search session.
/// </summary>
public sealed record ProjectFileSearchSessionOptions
{
    /// <summary>
    /// Gets the project root path.
    /// </summary>
    public required string ProjectRoot { get; init; }

    /// <summary>
    /// Gets the initial query text.
    /// </summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>
    /// Gets the maximum number of published results.
    /// </summary>
    public int MaximumResults { get; init; } = 25;

    /// <summary>
    /// Gets the maximum number of recent items seeded immediately.
    /// </summary>
    public int RecentItemLimit { get; init; } = 5;

    /// <summary>
    /// Gets the preferred incremental batch size for background refresh.
    /// </summary>
    public int RefreshBatchSize { get; init; } = 256;
}
