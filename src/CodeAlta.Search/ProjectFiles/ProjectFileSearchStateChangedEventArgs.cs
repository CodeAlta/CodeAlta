namespace CodeAlta.Search;

/// <summary>
/// Provides event data for published project-file search state changes.
/// </summary>
public sealed class ProjectFileSearchStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectFileSearchStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="state">Published state.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="state"/> is <see langword="null"/>.</exception>
    public ProjectFileSearchStateChangedEventArgs(ProjectFileSearchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
    }

    /// <summary>
    /// Gets the published search state.
    /// </summary>
    public ProjectFileSearchState State { get; }
}
