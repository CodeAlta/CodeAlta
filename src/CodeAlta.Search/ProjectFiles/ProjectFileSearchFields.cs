namespace CodeAlta.Search;

/// <summary>
/// Stores normalized search fields for project-file matching.
/// </summary>
public sealed record ProjectFileSearchFields
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectFileSearchFields"/> class.
    /// </summary>
    /// <param name="basename">Lowercase basename.</param>
    /// <param name="relativePath">Lowercase normalized relative path.</param>
    /// <param name="pathSegments">Lowercase path segments.</param>
    /// <param name="extension">Lowercase file extension or an empty string.</param>
    /// <exception cref="ArgumentNullException">Thrown when required values are <see langword="null"/>.</exception>
    public ProjectFileSearchFields(
        string basename,
        string relativePath,
        IReadOnlyList<string> pathSegments,
        string extension)
    {
        ArgumentNullException.ThrowIfNull(basename);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(pathSegments);
        ArgumentNullException.ThrowIfNull(extension);

        Basename = basename;
        RelativePath = relativePath;
        PathSegments = pathSegments;
        Extension = extension;
    }

    /// <summary>
    /// Gets the lowercase basename.
    /// </summary>
    public string Basename { get; }

    /// <summary>
    /// Gets the lowercase normalized relative path.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Gets the lowercase path segments.
    /// </summary>
    public IReadOnlyList<string> PathSegments { get; }

    /// <summary>
    /// Gets the lowercase extension or an empty string.
    /// </summary>
    public string Extension { get; }
}
