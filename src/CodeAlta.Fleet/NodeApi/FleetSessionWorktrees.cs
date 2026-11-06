using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// The folder a session started on a node works in (<c>doc/fleet/node.md</c>, "Workspaces"): a worktree of its own for a
/// project in a git repository, so that two sessions of a machine never edit, build and commit in the same checkout, and
/// a person's checkout there is never changed under them; the folder of the project otherwise.
/// </summary>
/// <remarks>
/// A worktree is made as CodeAlta Desktop makes the worktree of a new session: where the settings of the profile put
/// worktrees, on a new <c>alta/</c> branch that starts from the commit the checkout is on. Git runs none of the hooks of
/// the repository there: the session may be sandboxed, which is only settled once it exists.
/// </remarks>
public sealed class FleetSessionWorktrees
{
    private readonly GitWorktreeService _worktrees;

    /// <summary>Creates the worktrees of the sessions of a profile.</summary>
    /// <param name="options">The catalog roots of the profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public FleetSessionWorktrees(CatalogOptions options)
        : this(new GitWorktreeService(options ?? throw new ArgumentNullException(nameof(options)), new CodeAltaConfigStore(options)))
    {
    }

    /// <summary>Creates the worktrees of the sessions with a worktree service.</summary>
    /// <param name="worktrees">The service that makes and removes them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="worktrees"/> is null.</exception>
    public FleetSessionWorktrees(GitWorktreeService worktrees)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        _worktrees = worktrees;
    }

    /// <summary>Gives a new session of a project the folder it works in.</summary>
    /// <param name="project">The project of the session; null for a chat.</param>
    /// <param name="cancellationToken">Stops the creation; a worktree git had added is taken away.</param>
    /// <returns>
    /// The worktree; <see cref="FleetSessionWorktree.None"/> for a chat, a project in no git repository or a repository
    /// without a commit yet, which work in the folder of the project; or why none could be made.
    /// </returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<FleetSessionWorktree> PrepareAsync(ProjectDescriptor? project, CancellationToken cancellationToken)
    {
        if (project is null || GitWorktreeService.FindCheckoutRoot(project.ProjectPath) is null) return FleetSessionWorktree.None;
        var created = await _worktrees.CreateAsync(project, baseReference: null, branchName: null, progress: null, withoutHooks: true, cancellationToken).ConfigureAwait(false);
        if (created.Succeeded) return new(created.Folder, created.Root, null);
        // A repository with no commit has nothing to check out: its sessions work in its folder, as before its first commit.
        if (created.Status is "no_commit" or "not_repository") return FleetSessionWorktree.None;
        return new(null, null, $"A worktree of its own could not be made for the session in {project.Name} ({created.Status})"
            + (created.Message is { Length: > 0 } why ? $": {why}" : "."));
    }

    /// <summary>Takes away a worktree no session came of, with its branch; what git cannot remove stays among the worktrees of the project.</summary>
    /// <param name="project">The project of the worktree; null for a chat, which has none.</param>
    /// <param name="worktree">What <see cref="PrepareAsync"/> gave.</param>
    /// <returns>A task that completes once it is removed, or could not be.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="worktree"/> is null.</exception>
    public async Task RemoveUnusedAsync(ProjectDescriptor? project, FleetSessionWorktree worktree)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        if (project is null || worktree.Root is not { } root) return;
        try
        {
            await _worktrees.RemoveAsync(project.ProjectPath, root, force: false, deleteBranch: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // It stays listed among the worktrees of the project, where it can be removed.
        }
    }
}

/// <summary>The worktree a session started on a node works in.</summary>
/// <param name="Folder">The folder the session works in, inside the worktree; null for the folder of its project.</param>
/// <param name="Root">The root of the worktree; null when none was made.</param>
/// <param name="Problem">Why no worktree could be made for a project that needs one; null when nothing went wrong.</param>
public sealed record FleetSessionWorktree(string? Folder, string? Root, string? Problem)
{
    /// <summary>Gets the answer for a session that works in the folder of its project.</summary>
    public static FleetSessionWorktree None { get; } = new(null, null, null);
}
