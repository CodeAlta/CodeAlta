namespace CodeAlta.Search;

/// <summary>
/// Represents a source document ready for indexing.
/// </summary>
public sealed record DocumentInput
{
    /// <summary>
    /// Gets or sets source kind (for example <c>artifact</c> or <c>file</c>).
    /// </summary>
    public string SourceKind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets source identifier.
    /// </summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional workspace identifier.
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// Gets or sets optional project identifier.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Gets or sets optional title.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets optional mime type.
    /// </summary>
    public string? MimeType { get; set; }

    /// <summary>
    /// Gets or sets extracted plain text.
    /// </summary>
    public string Text { get; set; } = string.Empty;
}
