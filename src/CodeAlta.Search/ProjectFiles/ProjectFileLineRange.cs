namespace CodeAlta.Search;

/// <summary>
/// Represents a 1-based inclusive line range associated with a file reference.
/// </summary>
public sealed record ProjectFileLineRange
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectFileLineRange"/> class.
    /// </summary>
    /// <param name="startLine">The first line in the range.</param>
    /// <param name="endLine">The last line in the range.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the range is invalid.</exception>
    public ProjectFileLineRange(int startLine, int endLine)
    {
        if (startLine <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startLine), "Start line must be positive.");
        }

        if (endLine < startLine)
        {
            throw new ArgumentOutOfRangeException(nameof(endLine), "End line must be greater than or equal to the start line.");
        }

        StartLine = startLine;
        EndLine = endLine;
    }

    /// <summary>
    /// Gets the first line in the range.
    /// </summary>
    public int StartLine { get; }

    /// <summary>
    /// Gets the last line in the range.
    /// </summary>
    public int EndLine { get; }
}
