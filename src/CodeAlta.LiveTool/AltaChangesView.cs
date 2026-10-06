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
    /// <returns>True when a window received the request; false when none is there to show it.</returns>
    /// <exception cref="ArgumentException"><paramref name="projectId"/> is null or blank.</exception>
    bool Show(string projectId, string? path);
}
