namespace CodeAlta.Orchestration;

/// <summary>
/// Represents orchestration service options.
/// </summary>
public sealed class OrchestrationOptions
{
    /// <summary>
    /// Gets or sets the root folder used for persisted orchestration artifacts.
    /// </summary>
    public string ArtifactRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".alta",
        "orchestration");
}
