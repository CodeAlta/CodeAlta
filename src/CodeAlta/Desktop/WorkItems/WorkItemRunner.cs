using CodeAlta.Catalog;
using CodeAlta.Catalog.WorkItems;

namespace CodeAlta.Desktop.WorkItems;

/// <summary>What starting a task or a plan in a new session gave.</summary>
/// <param name="SessionId">The session that was created; null when none was.</param>
/// <param name="Problem">Why the work did not start; null when it did.</param>
/// <param name="Reason">A short code for the page when the problem is one it has a text for.</param>
internal sealed record WorkItemStartResult(string? SessionId, string? Problem, string? Reason = null);

/// <summary>Starts the work of a task or a plan in a new session.</summary>
internal interface IWorkItemRunner
{
    /// <summary>Creates a session of the project, in its folder or in a new git worktree, and sends it the item.</summary>
    /// <param name="project">The project.</param>
    /// <param name="kind">One of <see cref="WorkItemKinds"/>.</param>
    /// <param name="id">The id of the item.</param>
    /// <param name="worktree">Whether the session works in a new git worktree.</param>
    /// <param name="likeSessionId">A session whose provider, model and effort the new one takes; null when no session shows the item.</param>
    /// <param name="asked">The provider, the model and the effort the user chose for the session; null to take those of the session that shows the item, then those recorded with the item, then the defaults.</param>
    /// <returns>The session, or why the work did not start. Failures are results, not exceptions.</returns>
    Task<WorkItemStartResult> StartAsync(ProjectDescriptor project, string kind, string id, bool worktree, string? likeSessionId, WorkItemSelection? asked);
}

/// <summary>
/// Starts a task or a plan through the <see cref="ISessionStarter"/> of the host, and records the session that
/// carries the item out.
/// </summary>
internal sealed class WorkItemRunner : IWorkItemRunner
{
    private readonly ISessionStarter _starter;
    private readonly WorkItemService _items;

    internal WorkItemRunner(ISessionStarter starter, WorkItemService items)
    {
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentNullException.ThrowIfNull(items);
        (_starter, _items) = (starter, items);
    }

    /// <inheritdoc />
    public async Task<WorkItemStartResult> StartAsync(ProjectDescriptor project, string kind, string id, bool worktree, string? likeSessionId, WorkItemSelection? asked)
    {
        ArgumentNullException.ThrowIfNull(project);
        var task = kind == WorkItemKinds.Task ? _items.GetTask(project, id) : null;
        var plan = kind == WorkItemKinds.Plan ? _items.GetPlan(project, id) : null;
        if (task is null && plan is null) return new(null, "The item is no longer there.", "not_found");

        var started = await _starter.StartAsync(project, WorkItemPrompts.SessionTitle(task?.Title ?? plan!.Title),
            folder => task is not null ? WorkItemPrompts.ForTask(task) : WorkItemPrompts.ForPlan(plan!, project.ProjectPath, folder),
            worktree, likeSessionId, "workitem", new(asked, _items.GetLink(project.Id, kind, id)?.RunsWith)).ConfigureAwait(false);
        if (started is { Problem: null, SessionId: { } sessionId }) _items.SetRunner(project.Id, kind, id, sessionId);
        return new(started.SessionId, started.Problem, started.Reason);
    }
}
