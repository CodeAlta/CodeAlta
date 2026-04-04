namespace CodeAlta.Search;

/// <summary>
/// Represents a live incremental project file search session.
/// </summary>
public interface IProjectFileSearchSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the current published state for the session.
    /// </summary>
    ProjectFileSearchState Current { get; }

    /// <summary>
    /// Raised when the session publishes a new state.
    /// </summary>
    event EventHandler<ProjectFileSearchStateChangedEventArgs>? Updated;

    /// <summary>
    /// Updates the active query text for the session.
    /// </summary>
    /// <param name="query">Query text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SetQueryAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts or restarts a background refresh for the session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RefreshAsync(CancellationToken cancellationToken = default);
}
