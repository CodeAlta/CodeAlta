namespace CodeAlta.Search;

/// <summary>
/// Represents a cached snapshot of discovered project files and directories.
/// </summary>
public sealed record ProjectFileSnapshot
{
    /// <summary>
    /// Gets the project root path.
    /// </summary>
    public required string ProjectRoot { get; init; }

    /// <summary>
    /// Gets a value indicating whether Git-aware repository discovery succeeded.
    /// </summary>
    public required bool IsGitAware { get; init; }

    /// <summary>
    /// Gets the snapshot generation number.
    /// </summary>
    public required long SnapshotGeneration { get; init; }

    /// <summary>
    /// Gets the timestamp when the snapshot was built.
    /// </summary>
    public required DateTimeOffset BuiltAt { get; init; }

    /// <summary>
    /// Gets the discovered search items.
    /// </summary>
    public required IReadOnlyList<ProjectFileSearchItem> Items { get; init; }
}
