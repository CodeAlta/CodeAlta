using System.Text.Json.Serialization;

namespace CodeAlta.Persistence;

/// <summary>
/// Defines YAML frontmatter metadata for markdown artifacts.
/// </summary>
public sealed class ArtifactFrontmatter
{
    /// <summary>
    /// Gets or sets artifact identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets artifact type.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets optional workspace identifier.
    /// </summary>
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// Gets or sets optional workspace key.
    /// </summary>
    [JsonPropertyName("workspace_key")]
    public string? WorkspaceKey { get; set; }

    /// <summary>
    /// Gets or sets optional project identifier.
    /// </summary>
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    /// <summary>
    /// Gets or sets optional project key.
    /// </summary>
    [JsonPropertyName("project_key")]
    public string? ProjectKey { get; set; }

    /// <summary>
    /// Gets or sets source metadata.
    /// </summary>
    [JsonPropertyName("source")]
    public ArtifactSourceInfo? Source { get; set; }

    /// <summary>
    /// Gets or sets tags.
    /// </summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Gets or sets structured links.
    /// </summary>
    [JsonPropertyName("links")]
    public ArtifactLinks? Links { get; set; }

    /// <summary>
    /// Gets or sets creation timestamp in UTC.
    /// </summary>
    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    /// <summary>
    /// Gets or sets last update timestamp in UTC.
    /// </summary>
    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

/// <summary>
/// Describes the producer of an artifact.
/// </summary>
public sealed class ArtifactSourceInfo
{
    /// <summary>
    /// Gets or sets source kind.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>
    /// Gets or sets optional agent identifier.
    /// </summary>
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; set; }
}

/// <summary>
/// Represents structured links in artifact frontmatter.
/// </summary>
public sealed class ArtifactLinks
{
    /// <summary>
    /// Gets or sets linked task identifiers.
    /// </summary>
    [JsonPropertyName("tasks")]
    public List<string> Tasks { get; set; } = [];

    /// <summary>
    /// Gets or sets linked file references.
    /// </summary>
    [JsonPropertyName("files")]
    public List<ArtifactFileLink> Files { get; set; } = [];
}

/// <summary>
/// Represents a linked file entry.
/// </summary>
public sealed class ArtifactFileLink
{
    /// <summary>
    /// Gets or sets file path.
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional line range.
    /// </summary>
    [JsonPropertyName("range")]
    public ArtifactLineRange? Range { get; set; }
}

/// <summary>
/// Represents a source line range.
/// </summary>
public sealed class ArtifactLineRange
{
    /// <summary>
    /// Gets or sets start line (1-based).
    /// </summary>
    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }

    /// <summary>
    /// Gets or sets end line (1-based).
    /// </summary>
    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }
}
