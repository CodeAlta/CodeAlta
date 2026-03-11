namespace CodeAlta.Workspaces;

/// <summary>
/// Context values used for checkout path template expansion.
/// </summary>
public sealed class PathTemplateContext
{
    /// <summary>
    /// Gets or sets the workspace slug.
    /// </summary>
    public string WorkspaceSlug { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the project slug.
    /// </summary>
    public string ProjectSlug { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the repository name.
    /// </summary>
    public string RepoName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the machine id.
    /// </summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the workspace id.
    /// </summary>
    public WorkspaceId WorkspaceId { get; set; }

    /// <summary>
    /// Gets or sets the project id.
    /// </summary>
    public ProjectId ProjectId { get; set; }

    /// <summary>
    /// Gets or sets an optional base root used for safe path normalization.
    /// </summary>
    public string? BaseRoot { get; set; }
}
