namespace CodeAlta.Search;

/// <summary>
/// Represents a searchable file or directory within a project.
/// </summary>
public sealed record ProjectFileSearchItem
{
    /// <summary>
    /// Gets the item kind.
    /// </summary>
    public required ProjectFileSearchItemKind Kind { get; init; }

    /// <summary>
    /// Gets the project root path that owns the item.
    /// </summary>
    public required string ProjectRoot { get; init; }

    /// <summary>
    /// Gets the normalized project-relative path using <c>/</c> separators.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// Gets the absolute path on disk.
    /// </summary>
    public required string FullPath { get; init; }

    /// <summary>
    /// Gets the display basename for the item.
    /// </summary>
    public required string Basename { get; init; }

    /// <summary>
    /// Gets the display parent directory text.
    /// </summary>
    public required string ParentPath { get; init; }

    /// <summary>
    /// Gets the file extension including the leading dot, or an empty string for directories.
    /// </summary>
    public required string Extension { get; init; }

    /// <summary>
    /// Gets the last write timestamp when cheaply available.
    /// </summary>
    public DateTimeOffset? LastWriteTimeUtc { get; init; }

    /// <summary>
    /// Gets normalized search fields used by ranking logic.
    /// </summary>
    public required ProjectFileSearchFields SearchFields { get; init; }

    /// <summary>
    /// Gets recent-usage metadata when available.
    /// </summary>
    public ProjectFileUsageEntry? Usage { get; init; }
}
