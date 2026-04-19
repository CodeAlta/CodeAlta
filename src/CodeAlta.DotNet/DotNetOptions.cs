namespace CodeAlta.DotNet;

/// <summary>
/// Represents options for .NET first-class services.
/// </summary>
public sealed class DotNetOptions
{
    /// <summary>
    /// Gets or sets the root directory used to persist .NET knowledge artifacts.
    /// </summary>
    /// <remarks>
    /// When empty, services default to writing under the target repository's <c>.alta/knowledge/dotnet</c> folder.
    /// </remarks>
    public string ArtifactRoot { get; set; } = string.Empty;
}
