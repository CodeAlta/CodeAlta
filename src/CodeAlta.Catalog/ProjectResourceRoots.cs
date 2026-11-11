namespace CodeAlta.Catalog;

/// <summary>
/// The folders a project reads its setup from (skills, prompts, MCP servers, configuration, issue views, plugins), the
/// nearest first: the folder of the project, then, for a sub-project, the declared sub-projects whose folder holds its
/// own, then the folder of its repository (<see cref="ProjectDescriptor.InheritedRoots"/>). They come from the
/// descriptor as the catalog computed it: nothing is looked for on the disk. Plans, tasks and automations are not
/// inherited: they stay those of the folder of the project.
/// </summary>
public static class ProjectResourceRoots
{
    /// <summary>Gets the folders of a project, the nearest first: its own, then those it inherits from.</summary>
    /// <param name="project">The project; null for none.</param>
    /// <returns>The folders; empty for no project.</returns>
    public static IReadOnlyList<string> Of(ProjectDescriptor? project)
        => project is null || string.IsNullOrWhiteSpace(project.ProjectPath) ? [] : [project.ProjectPath, .. Inherited(project)];

    /// <summary>Gets the folders a project inherits its setup from, the nearest first; empty for a project that is no sub-project.</summary>
    /// <param name="project">The project; null for none.</param>
    /// <returns>The folders.</returns>
    public static IReadOnlyList<string> Inherited(ProjectDescriptor? project)
        => project is { IsSubProject: true } ? [.. project.InheritedRoots.Where(static root => !string.IsNullOrWhiteSpace(root))] : [];

    /// <summary>
    /// Gets the folders a session of a project writes besides the folder of its project, when it works in that folder
    /// (not in a worktree): the folder of the repository of a sub-project, since a change in a monorepo often crosses
    /// folders, unless its map narrows it to its own folder (<see cref="ProjectWriteScope.Folder"/>). Its settings, and
    /// those of the other projects of the repository, stay protected.
    /// </summary>
    /// <param name="project">The project; null for none.</param>
    /// <returns>The folders; empty for a project that is no sub-project, or one that writes its own folder only.</returns>
    public static IReadOnlyList<string> WriteRoots(ProjectDescriptor? project)
        => project is { IsSubProject: true, WriteScope: ProjectWriteScope.Repository, RepositoryPath: { } repository } ? [repository] : [];
}
