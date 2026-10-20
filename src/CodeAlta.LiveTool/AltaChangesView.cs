namespace CodeAlta.LiveTool;

/// <summary>
/// Shows the changed files of a project to the user, in a host that has a view for them. A host without one
/// registers no such service, and <c>alta diff</c> is then not part of its commands.
/// </summary>
public interface IAltaChangesView
{
    /// <summary>Asks the host to show the changes of a project's work tree.</summary>
    /// <param name="projectId">The id of the project.</param>
    /// <param name="path">The changed file to select, relative to the repository; null to select none.</param>
    /// <param name="worktree">
    /// The folder of the project in a git worktree of its repository, to show the changes of that checkout; null
    /// for the folder of the project.
    /// </param>
    /// <returns>True when a window received the request; false when none is there to show it.</returns>
    /// <exception cref="ArgumentException"><paramref name="projectId"/> is null or blank.</exception>
    bool Show(string projectId, string? path, string? worktree = null);

    /// <summary>
    /// Asks the host to show the changes of a project's work tree for a session: a host with several windows shows them
    /// in the window of that session when no window has the changes of the project open.
    /// </summary>
    /// <param name="projectId">The id of the project.</param>
    /// <param name="path">The changed file to select, relative to the repository; null to select none.</param>
    /// <param name="worktree">The folder of the project in a git worktree of its repository; null for the folder of the project.</param>
    /// <param name="callerSessionId">The session that asks; null for none.</param>
    /// <returns>True when a window received the request; false when none is there to show it.</returns>
    /// <exception cref="ArgumentException"><paramref name="projectId"/> is null or blank.</exception>
    bool Show(string projectId, string? path, string? worktree, string? callerSessionId) => Show(projectId, path, worktree);
}
