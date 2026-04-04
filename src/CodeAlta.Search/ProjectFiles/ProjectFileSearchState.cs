namespace CodeAlta.Search;

/// <summary>
/// Represents the published state of an incremental project-file search session.
/// </summary>
public sealed record ProjectFileSearchState
{
    /// <summary>
    /// Gets the current query text.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Gets the currently visible ranked results.
    /// </summary>
    public required IReadOnlyList<ProjectFileSearchResult> Results { get; init; }

    /// <summary>
    /// Gets a value indicating whether a background refresh is running.
    /// </summary>
    public required bool IsRefreshing { get; init; }

    /// <summary>
    /// Gets a value indicating whether the state is backed by a snapshot.
    /// </summary>
    public required bool HasSnapshot { get; init; }

    /// <summary>
    /// Gets the latest published refresh generation.
    /// </summary>
    public required long RefreshGeneration { get; init; }

    /// <summary>
    /// Gets the generation of the snapshot used for this state.
    /// </summary>
    public required long SnapshotGeneration { get; init; }

    /// <summary>
    /// Gets the number of currently known candidates.
    /// </summary>
    public required int CandidateCount { get; init; }

    /// <summary>
    /// Gets the timestamp when the state was published.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Creates an empty search state.
    /// </summary>
    /// <param name="query">Current query text.</param>
    /// <returns>An empty search state.</returns>
    public static ProjectFileSearchState CreateEmpty(string query = "")
    {
        return new ProjectFileSearchState
        {
            Query = query ?? string.Empty,
            Results = [],
            IsRefreshing = false,
            HasSnapshot = false,
            RefreshGeneration = 0,
            SnapshotGeneration = 0,
            CandidateCount = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }
}
