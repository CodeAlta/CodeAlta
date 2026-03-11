namespace CodeAlta.Workspaces.Bootstrap;

/// <summary>
/// Plans workspace checkout operations without network side effects.
/// </summary>
public sealed class WorkspaceBootstrapPlanner
{
    /// <summary>
    /// Creates checkout plans for a resolved workspace.
    /// </summary>
    /// <param name="resolution">The resolved workspace.</param>
    /// <returns>The planned operations.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="resolution"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<WorkspaceCheckoutPlan> Plan(WorkspaceResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        var plans = new List<WorkspaceCheckoutPlan>(resolution.Projects.Count);
        foreach (var project in resolution.Projects)
        {
            var action = Directory.Exists(project.CheckoutPath)
                ? CheckoutAction.Update
                : CheckoutAction.Clone;

            plans.Add(new WorkspaceCheckoutPlan
            {
                WorkspaceSlug = resolution.Workspace.Slug,
                ProjectSlug = project.Project.Slug,
                RepoUrl = project.Project.RepoUrl,
                CheckoutPath = project.CheckoutPath,
                Action = action,
            });
        }

        return plans;
    }
}
