using System.Text.Json.Serialization;

namespace CodeAlta.Workspaces;

/// <summary>
/// Describes machine-specific workspace overrides.
/// </summary>
public sealed class MachineProfile
{
    /// <summary>
    /// Gets or sets the machine identifier.
    /// </summary>
    [JsonPropertyName("machine_id")]
    public string MachineId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets workspace checkout root overrides by workspace slug.
    /// </summary>
    [JsonPropertyName("workspace_checkout_roots")]
    public Dictionary<string, string> WorkspaceCheckoutRoots { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets project-level overrides by project slug.
    /// </summary>
    [JsonPropertyName("project_overrides")]
    public Dictionary<string, MachineProjectOverride> ProjectOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Validates the machine profile.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the profile is invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MachineId))
        {
            throw new ArgumentException("Machine id is required.", nameof(MachineId));
        }

        foreach (var key in WorkspaceCheckoutRoots.Keys)
        {
            WorkspaceKeyValidator.Validate(key, nameof(WorkspaceCheckoutRoots));
        }

        foreach (var key in ProjectOverrides.Keys)
        {
            WorkspaceKeyValidator.Validate(key, nameof(ProjectOverrides));
        }
    }
}
