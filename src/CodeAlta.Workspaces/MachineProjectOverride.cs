using System.Text.Json.Serialization;

namespace CodeAlta.Workspaces;

/// <summary>
/// Defines machine-specific project behavior.
/// </summary>
public sealed class MachineProjectOverride
{
    /// <summary>
    /// Gets or sets a value indicating whether checkout is disabled.
    /// </summary>
    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    /// <summary>
    /// Gets or sets an absolute checkout path override.
    /// </summary>
    [JsonPropertyName("checkout_path")]
    public string? CheckoutPath { get; set; }
}
