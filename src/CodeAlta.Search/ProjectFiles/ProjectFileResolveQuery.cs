namespace CodeAlta.Search;

/// <summary>
/// Represents a request to resolve a normalized project-relative reference.
/// </summary>
public sealed record ProjectFileResolveQuery
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectFileResolveQuery"/> class.
    /// </summary>
    /// <param name="projectRoot">Project root path.</param>
    /// <param name="referenceText">Normalized project-relative path text without the leading <c>@</c>.</param>
    /// <param name="lineRange">Optional requested line range.</param>
    /// <exception cref="ArgumentException">Thrown when required values are empty.</exception>
    public ProjectFileResolveQuery(string projectRoot, string referenceText, ProjectFileLineRange? lineRange = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root is required.", nameof(projectRoot));
        }

        if (string.IsNullOrWhiteSpace(referenceText))
        {
            throw new ArgumentException("Reference text is required.", nameof(referenceText));
        }

        ProjectRoot = projectRoot;
        ReferenceText = referenceText;
        LineRange = lineRange;
    }

    /// <summary>
    /// Gets the project root path.
    /// </summary>
    public string ProjectRoot { get; }

    /// <summary>
    /// Gets the normalized project-relative path text without the leading <c>@</c>.
    /// </summary>
    public string ReferenceText { get; }

    /// <summary>
    /// Gets the requested line range when present.
    /// </summary>
    public ProjectFileLineRange? LineRange { get; }
}
