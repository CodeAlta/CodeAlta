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
    /// <remarks>
    /// The folder of the sub-project is looked at again here, as a session starts: what the catalog keeps of the folders
    /// of a map could be older than a link put in place of one, which must not widen what the session writes.
    /// </remarks>
    public static IReadOnlyList<string> WriteRoots(ProjectDescriptor? project)
        => project is { IsSubProject: true, WriteScope: ProjectWriteScope.Repository, RepositoryPath: { } repository, SubPath: { } subPath }
            && ProjectManifest.ResolveFolder(repository, subPath).State == ProjectManifestFolderState.Ok ? [repository] : [];

    /// <summary>
    /// Gets the folders a session of a sub-project writes besides its own folder when it works in a git worktree: the
    /// folder of its repository in that checkout, as <see cref="WriteRoots"/> gives it in the folder of the project. A
    /// sub-project that is a git submodule has a worktree of its own repository: it writes the root of that checkout.
    /// </summary>
    /// <param name="project">The project; null for none.</param>
    /// <param name="worktreeFolder">The folder the session works in: the folder of the sub-project in its worktree.</param>
    /// <returns>The folders; empty for a project that is no sub-project, one that writes its own folder only, or a folder in no checkout.</returns>
    public static IReadOnlyList<string> WorktreeWriteRoots(ProjectDescriptor? project, string? worktreeFolder)
    {
        if (project is not { IsSubProject: true, WriteScope: ProjectWriteScope.Repository, RepositoryPath: { } repository } || string.IsNullOrWhiteSpace(worktreeFolder))
            return [];
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreeFolder));
        if (Worktrees.GitWorktreeService.FindCheckoutRoot(folder) is not { } found) return [];
        var checkout = Path.TrimEndingDirectorySeparator(Path.GetFullPath(found));
        // Where the repository is in this checkout: at the same place from the folder of the sub-project.
        var mapped = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(folder, Path.GetRelativePath(project.ProjectPath, repository))));
        var root = IsSameOrInside(mapped, checkout) ? mapped : checkout;
        return PathsEqual(root, folder) ? [] : [root];
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(left, right, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsSameOrInside(string path, string root)
        => PathsEqual(path, root) || path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
