namespace CodeAlta.Search;

/// <summary>
/// Represents an indexing job.
/// </summary>
public sealed record IndexingJob
{
    /// <summary>
    /// Gets or sets job identifier.
    /// </summary>
    public string JobId { get; set; } = Guid.CreateVersion7().ToString();

    /// <summary>
    /// Gets or sets documents to index.
    /// </summary>
    public IReadOnlyList<DocumentInput> Documents { get; set; } = [];
}

/// <summary>
/// Represents indexing progress details.
/// </summary>
public sealed record IndexingProgress
{
    /// <summary>
    /// Gets or sets the current job id.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Gets or sets number of processed documents.
    /// </summary>
    public required int Processed { get; init; }

    /// <summary>
    /// Gets or sets total documents in job.
    /// </summary>
    public required int Total { get; init; }
}

/// <summary>
/// Represents indexing queue status.
/// </summary>
public sealed record IndexingStatus
{
    /// <summary>
    /// Gets or sets current queue depth.
    /// </summary>
    public int QueueDepth { get; init; }

    /// <summary>
    /// Gets or sets timestamp of last completed job in UTC.
    /// </summary>
    public DateTimeOffset? LastCompletedAt { get; init; }
}
