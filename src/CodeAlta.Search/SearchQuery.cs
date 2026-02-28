namespace CodeAlta.Search;

/// <summary>
/// Represents a search query request.
/// </summary>
public sealed record SearchQuery
{
    /// <summary>
    /// Gets or sets query text.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional workspace filter.
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// Gets or sets optional project filter.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Gets or sets maximum result count.
    /// </summary>
    public int Limit { get; set; } = 10;

    /// <summary>
    /// Gets or sets FTS prefilter limit before vector reranking.
    /// </summary>
    public int PrefilterLimit { get; set; } = 50;
}
