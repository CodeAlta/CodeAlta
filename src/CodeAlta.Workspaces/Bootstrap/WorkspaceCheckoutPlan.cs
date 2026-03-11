namespace CodeAlta.Workspaces.Bootstrap;

/// <summary>
/// Represents a planned repository checkout operation.
/// </summary>
public sealed record WorkspaceCheckoutPlan
{
    /// <summary>
    /// Gets the workspace slug.
    /// </summary>
    public required string WorkspaceSlug { get; init; }

    /// <summary>
    /// Gets the project slug.
    /// </summary>
    public required string ProjectSlug { get; init; }

    /// <summary>
    /// Gets the repository URL.
    /// </summary>
    public required string RepoUrl { get; init; }

    /// <summary>
    /// Gets the checkout path.
    /// </summary>
    public required string CheckoutPath { get; init; }

    /// <summary>
    /// Gets the planned action.
    /// </summary>
    public required CheckoutAction Action { get; init; }
}
