namespace CodeAlta.Persistence;

/// <summary>
/// Represents persisted recent-usage data for a project file or directory.
/// </summary>
public sealed record ProjectFileUsageRecord
{
    /// <summary>
    /// Gets the project root path associated with the usage record.
    /// </summary>
    public required string ProjectRoot { get; init; }

    /// <summary>
    /// Gets the normalized relative path within the project root.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// Gets the persisted item kind.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the last time the item was accessed.
    /// </summary>
    public required DateTimeOffset LastAccessedAt { get; init; }

    /// <summary>
    /// Gets the bounded access count for the item.
    /// </summary>
    public required long AccessCount { get; init; }

    /// <summary>
    /// Gets the last recorded access source when available.
    /// </summary>
    public string? LastAccessKind { get; init; }
}
