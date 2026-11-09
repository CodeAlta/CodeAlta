namespace CodeAlta.Catalog;

public sealed partial class ProjectCatalog
{
    /// <summary>
    /// Tells the project a project session belongs to, from the project and the folder its journal records: the project
    /// recorded, when the catalog has it in that folder; otherwise, when no project of the catalog has the identifier
    /// recorded, the project of the catalog in that folder. A session written where the folder was no project of the
    /// catalog (the transient project of <c>altacli</c>, or a project removed and added again) is so adopted by the project
    /// added for its folder, as the runtime binds it by its folder. A session that records another project of the catalog
    /// is never given to the project of its folder.
    /// </summary>
    /// <param name="recordedProjectId">The project the journal of the session records.</param>
    /// <param name="workingDirectory">The folder the journal of the session records, compared exactly with the folders of the projects.</param>
    /// <param name="projects">The projects of the catalog.</param>
    /// <returns>The project of the session, or <see langword="null"/> when none fits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is <see langword="null"/>.</exception>
    public static ProjectDescriptor? ResolveSessionProject(string? recordedProjectId, string? workingDirectory, IReadOnlyList<ProjectDescriptor> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        if (string.IsNullOrWhiteSpace(recordedProjectId) || string.IsNullOrEmpty(workingDirectory)) return null;
        // An identifier the catalog has in another case is the identifier of another project all the same.
        if (projects.FirstOrDefault(project => string.Equals(project.Id, recordedProjectId, StringComparison.OrdinalIgnoreCase)) is { } recorded)
        {
            return recorded.Id == recordedProjectId && recorded.ProjectPath == workingDirectory ? recorded : null;
        }

        return projects.FirstOrDefault(project => project.ProjectPath == workingDirectory);
    }

    /// <summary>
    /// Tells the project a project session belongs to when the folder of that project changed since the session was
    /// recorded: the session records a project of the catalog, which is not archived and now has another folder, the
    /// folder the session records no longer exists, and the folder of the project does. Such a session is listed under
    /// no project (<see cref="ResolveSessionProject"/>) until it is bound to the new folder. A folder that still exists
    /// may be another checkout the session works in: its session is left as it is.
    /// </summary>
    /// <param name="recordedProjectId">The project the journal of the session records.</param>
    /// <param name="workingDirectory">The folder the journal of the session records.</param>
    /// <param name="projects">The projects of the catalog.</param>
    /// <param name="folderExists">Tells whether a folder exists.</param>
    /// <returns>The project whose folder moved, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> or <paramref name="folderExists"/> is <see langword="null"/>.</exception>
    public static ProjectDescriptor? FindMovedSessionProject(string? recordedProjectId, string? workingDirectory, IReadOnlyList<ProjectDescriptor> projects,
        Func<string, bool> folderExists)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(folderExists);
        if (string.IsNullOrWhiteSpace(recordedProjectId) || string.IsNullOrWhiteSpace(workingDirectory)) return null;
        var project = projects.FirstOrDefault(candidate => candidate.Id == recordedProjectId);
        if (project is null || project.Archived || string.IsNullOrWhiteSpace(project.ProjectPath) || project.ProjectPath == workingDirectory) return null;
        return !folderExists(workingDirectory) && folderExists(project.ProjectPath) ? project : null;
    }
}
