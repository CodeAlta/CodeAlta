namespace CodeAlta.Search;

/// <summary>
/// Represents a search result row.
/// </summary>
public sealed record SearchResult
{
    /// <summary>
    /// Gets or sets indexed document identifier.
    /// </summary>
    public required long DocumentId { get; set; }

    /// <summary>
    /// Gets or sets source kind.
    /// </summary>
    public required string SourceKind { get; set; }

    /// <summary>
    /// Gets or sets source identifier.
    /// </summary>
    public required string SourceId { get; set; }

    /// <summary>
    /// Gets or sets stable source link URI.
    /// </summary>
    public required string LinkUri { get; set; }

    /// <summary>
    /// Gets or sets optional title.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets optional snippet.
    /// </summary>
    public string? Snippet { get; set; }

    /// <summary>
    /// Gets or sets optional FTS score.
    /// </summary>
    public double? FtsScore { get; set; }

    /// <summary>
    /// Gets or sets optional vector score.
    /// </summary>
    public double? VectorScore { get; set; }

    /// <summary>
    /// Gets or sets combined ranking score.
    /// </summary>
    public double CombinedScore { get; set; }
}
