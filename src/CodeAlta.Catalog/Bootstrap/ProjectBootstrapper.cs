namespace CodeAlta.Catalog.Bootstrap;

/// <summary>
/// Applies project checkout plans to ensure repositories exist on disk.
/// </summary>
public sealed class ProjectBootstrapper
{
    private readonly ProjectBootstrapPlanner _planner;
    private readonly GitService _git;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectBootstrapper"/> class.
    /// </summary>
    /// <param name="planner">Planner used to compute checkout actions.</param>
    /// <param name="git">Git service used to apply clone/pull actions.</param>
    public ProjectBootstrapper(ProjectBootstrapPlanner planner, GitService git)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(git);

        _planner = planner;
        _git = git;
    }

    /// <summary>
    /// Ensures repositories for the resolved scope are checked out.
    /// </summary>
    /// <param name="resolution">Project scope resolution.</param>
    /// <param name="updateExisting">Whether existing checkouts should be updated via <c>git pull</c>.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Execution results.</returns>
    public async Task<IReadOnlyList<ProjectCheckoutExecutionResult>> EnsureCheckedOutAsync(
        ProjectScopeResolution resolution,
        bool updateExisting = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        var plans = _planner.Plan(resolution);
        var results = new List<ProjectCheckoutExecutionResult>(plans.Count);

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var checkoutPath = Path.GetFullPath(plan.CheckoutPath);
            var success = true;
            string? message = null;

            try
            {
                switch (plan.Action)
                {
                    case CheckoutAction.Clone:
                        Directory.CreateDirectory(Path.GetDirectoryName(checkoutPath) ?? ".");
                        var clone = await _git.CloneAsync(plan.ProjectPath, checkoutPath, progress, cancellationToken)
                            .ConfigureAwait(false);
                        success = clone.Success;
                        message = clone.CombinedOutput;
                        break;

                    case CheckoutAction.Update:
                        if (updateExisting && Directory.Exists(checkoutPath))
                        {
                            var pull = await _git.PullAsync(checkoutPath, progress, cancellationToken).ConfigureAwait(false);
                            success = pull.Success;
                            message = pull.CombinedOutput;
                        }
                        break;

                    case CheckoutAction.Skip:
                        break;
                }

                if (success)
                {
                    Directory.CreateDirectory(Path.Combine(checkoutPath, ".alta"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                success = false;
                message = ex.Message;
            }

            results.Add(
                new ProjectCheckoutExecutionResult
                {
                    ProjectSlug = plan.ProjectSlug,
                    CheckoutPath = checkoutPath,
                    Action = plan.Action,
                    Success = success,
                    Message = message,
                });
        }

        return results;
    }
}
