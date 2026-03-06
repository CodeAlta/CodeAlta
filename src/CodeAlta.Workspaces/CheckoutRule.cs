using System.Text.Json.Serialization;

namespace CodeAlta.Workspaces;

/// <summary>
/// Defines checkout behavior for a project.
/// </summary>
public sealed class CheckoutRule
{
    /// <summary>
    /// Gets or sets the path template.
    /// </summary>
    [JsonPropertyName("path_template")]
    public string PathTemplate { get; set; } = "{workspaceKey}\\{projectKey}";

    /// <summary>
    /// Gets or sets the optional clone depth.
    /// </summary>
    [JsonPropertyName("depth")]
    public int? Depth { get; set; }

    /// <summary>
    /// Gets or sets whether submodules are enabled.
    /// </summary>
    [JsonPropertyName("submodules")]
    public bool? Submodules { get; set; }
}
