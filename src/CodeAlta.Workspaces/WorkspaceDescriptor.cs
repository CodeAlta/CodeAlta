using System.Text.Json.Serialization;

namespace CodeAlta.Workspaces;

/// <summary>
/// Describes a workspace and its projects.
/// </summary>
public sealed class WorkspaceDescriptor
{
    /// <summary>
    /// Gets or sets the workspace identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the workspace key.
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default checkout root.
    /// </summary>
    [JsonPropertyName("default_checkout_root")]
    public string DefaultCheckoutRoot { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets optional tags.
    /// </summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Gets or sets the projects.
    /// </summary>
    [JsonPropertyName("projects")]
    public List<ProjectDescriptor> Projects { get; set; } = [];

    /// <summary>
    /// Gets the path to <c>workspace.yaml</c> when loaded from disk.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets the parsed identifier.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the identifier is invalid.</exception>
    public WorkspaceId WorkspaceId => WorkspaceId.Parse(Id);

    /// <summary>
    /// Validates the descriptor and all projects.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when required values are missing or invalid.</exception>
    public void Validate()
    {
        if (!WorkspaceId.TryParse(Id, out _))
        {
            throw new ArgumentException($"Workspace '{Key}' has an invalid id '{Id}'.", nameof(Id));
        }

        WorkspaceKeyValidator.Validate(Key, nameof(Key));

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new ArgumentException("Workspace display name is required.", nameof(DisplayName));
        }

        if (string.IsNullOrWhiteSpace(DefaultCheckoutRoot))
        {
            throw new ArgumentException("Workspace default checkout root is required.", nameof(DefaultCheckoutRoot));
        }

        var duplicateKey = Projects
            .GroupBy(static x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(static x => x.Count() > 1)
            .Select(static x => x.Key)
            .FirstOrDefault();

        if (duplicateKey is not null)
        {
            throw new ArgumentException($"Workspace '{Key}' contains duplicate project key '{duplicateKey}'.", nameof(Projects));
        }

        foreach (var project in Projects)
        {
            project.Validate();
        }
    }
}
