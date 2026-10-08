using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>
/// Describes a project in the global catalog.
/// </summary>
public sealed class ProjectDescriptor
{
    /// <summary>
    /// Gets or sets the project identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the project slug.
    /// </summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the project name used for checkout directories.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local project path.
    /// </summary>
    [JsonPropertyName("path")]
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default branch.
    /// </summary>
    [JsonPropertyName("default_branch")]
    public string DefaultBranch { get; set; } = "main";

    /// <summary>
    /// Gets or sets the optional description shown to users.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets optional tags.
    /// </summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Gets or sets the identifiers of the spaces the project belongs to, besides the default space,
    /// which holds every project. An identifier no space has is kept and means nothing.
    /// </summary>
    [JsonPropertyName("spaces")]
    public List<string> Spaces { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the project is archived.
    /// </summary>
    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    /// <summary>
    /// Gets or sets checkout settings.
    /// </summary>
    [JsonPropertyName("checkout")]
    public CheckoutRule Checkout { get; set; } = new();

    /// <summary>
    /// Gets the path to the source markdown file when loaded from disk.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the markdown body loaded from disk.
    /// </summary>
    public string? MarkdownBody { get; set; }

    /// <summary>
    /// Gets the parsed identifier.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the identifier is invalid.</exception>
    public ProjectId ProjectId => ProjectId.Parse(Id);

    /// <summary>
    /// Validates the descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when required values are missing or invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            DisplayName = ProjectPathNameFormatter.InferDisplayName(ProjectPath);
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = ProjectPathNameFormatter.InferName(ProjectPath, DisplayName);
        }

        if (!ProjectId.TryParse(Id, out _))
        {
            throw new ArgumentException($"Project '{Slug}' has an invalid id '{Id}'.", nameof(Id));
        }

        CatalogSlugValidator.Validate(Slug, nameof(Slug));

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Project name is required.", nameof(Name));
        }

        if (!ProjectPathNameFormatter.IsValidProjectName(Name))
        {
            throw new ArgumentException("Project name must be a valid single directory name.", nameof(Name));
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new ArgumentException("Project display name is required.", nameof(DisplayName));
        }

        if (string.IsNullOrWhiteSpace(ProjectPath))
        {
            throw new ArgumentException("Project path is required.", nameof(ProjectPath));
        }

        if (string.IsNullOrWhiteSpace(DefaultBranch))
        {
            throw new ArgumentException("Project default branch is required.", nameof(DefaultBranch));
        }
    }

}

