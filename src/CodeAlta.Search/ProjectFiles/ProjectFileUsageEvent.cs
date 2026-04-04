namespace CodeAlta.Search;

/// <summary>
/// Represents a recent-usage event for a project file or directory.
/// </summary>
public sealed record ProjectFileUsageEvent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectFileUsageEvent"/> class.
    /// </summary>
    /// <param name="projectRoot">Project root path.</param>
    /// <param name="relativePath">Normalized relative path.</param>
    /// <param name="kind">File or directory kind.</param>
    /// <param name="accessedAt">Access timestamp.</param>
    /// <param name="accessKind">Optional access source.</param>
    /// <exception cref="ArgumentException">Thrown when required values are empty.</exception>
    public ProjectFileUsageEvent(
        string projectRoot,
        string relativePath,
        ProjectFileSearchItemKind kind,
        DateTimeOffset accessedAt,
        ProjectFileUsageAccessKind? accessKind = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root is required.", nameof(projectRoot));
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path is required.", nameof(relativePath));
        }

        ProjectRoot = projectRoot;
        RelativePath = relativePath;
        Kind = kind;
        AccessedAt = accessedAt;
        AccessKind = accessKind;
    }

    /// <summary>
    /// Gets the project root path.
    /// </summary>
    public string ProjectRoot { get; }

    /// <summary>
    /// Gets the normalized relative path.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Gets the file or directory kind.
    /// </summary>
    public ProjectFileSearchItemKind Kind { get; }

    /// <summary>
    /// Gets the access timestamp.
    /// </summary>
    public DateTimeOffset AccessedAt { get; }

    /// <summary>
    /// Gets the access source when available.
    /// </summary>
    public ProjectFileUsageAccessKind? AccessKind { get; }
}
