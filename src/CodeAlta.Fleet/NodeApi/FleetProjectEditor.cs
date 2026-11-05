using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.Moves;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// The projects of a node as its owner manages them (<c>altanode projects</c>): listed, added from a folder, cloned into
/// the clone root, removed from the catalog. The running node does it for its local control (<c>control.projects.*</c>);
/// without a running node, <c>altanode</c> does it on the catalog files of the profile, through the same catalog.
/// </summary>
/// <remarks>What is refused is a <see cref="FleetException"/> with the reason for people, as the node API answers it.</remarks>
public sealed class FleetProjectEditor
{
    private readonly ProjectCatalog _projects;
    private readonly SpaceCatalog _spaces;

    /// <summary>Creates the editor of a catalog.</summary>
    /// <param name="spaces">The spaces of the catalog, and through them its projects.</param>
    /// <param name="cloneRoot">Where a repository is cloned (<c>&lt;root&gt;/&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;</c>); null clones nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="spaces"/> is null.</exception>
    public FleetProjectEditor(SpaceCatalog spaces, string? cloneRoot)
    {
        ArgumentNullException.ThrowIfNull(spaces);
        _spaces = spaces;
        _projects = spaces.Projects;
        CloneRoot = string.IsNullOrWhiteSpace(cloneRoot) ? null : Path.GetFullPath(cloneRoot);
    }

    /// <summary>Gets where a repository is cloned; null when none is.</summary>
    public string? CloneRoot { get; }

    /// <summary>Lists the projects of the catalog, archived ones included, and the files that could not be read.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The list.</returns>
    public async Task<FleetProjectList> ListAsync(CancellationToken cancellationToken)
    {
        var load = await _projects.LoadReadableAsync(cancellationToken).ConfigureAwait(false);
        return new FleetProjectList([.. load.Projects.Select(static project => Describe(project))], [.. load.Unreadable.Select(Diagnose)]);
    }

    /// <summary>Adds a folder of this machine to the catalog (or brings back its archived project), and to a space.</summary>
    /// <param name="path">The folder, an absolute path.</param>
    /// <param name="space">The space, by id or name; null for the default space only.</param>
    /// <param name="cancellationToken">Cancels before a file is written.</param>
    /// <returns>The project.</returns>
    /// <exception cref="FleetException">The path is not an existing folder, or the space is unknown.</exception>
    public async Task<FleetProjectChange> AddAsync(string path, string? space, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, $"A project folder is an absolute path: '{path}'.");
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        if (!Directory.Exists(folder)) throw new FleetException(FleetErrorCodes.InvalidParams, null, $"{folder} is not a folder of this machine.");
        var target = space is null ? null : await ResolveSpaceAsync(space, cancellationToken).ConfigureAwait(false);
        return await AddFolderAsync(folder, target, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clones a repository into <c>&lt;clone root&gt;/&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;</c> and adds it to the catalog, and to a
    /// space; a checkout of the same repository already there is added as it is.
    /// </summary>
    /// <param name="url">The URL of the repository: https, http, ssh, git, or <c>user@host:owner/repo</c>.</param>
    /// <param name="space">The space, by id or name; null for the default space only.</param>
    /// <param name="cancellationToken">Stops git, or cancels before a file is written.</param>
    /// <returns>The project, with where it was cloned.</returns>
    /// <exception cref="FleetException">The URL cannot be cloned here, the space is unknown, or git failed.</exception>
    public async Task<FleetProjectChange> CloneAsync(string url, string? space, CancellationToken cancellationToken)
    {
        var trimmed = url?.Trim() ?? "";
        if (CloneRoot is null) throw new FleetException(FleetNodeErrorCodes.Unavailable, null, "This node clones no repository.");
        var key = RepositoryKey.Normalize(trimmed);
        if (FleetRepositoryClone.PathFor(CloneRoot, trimmed, key) is not { } path)
            throw new FleetException(FleetErrorCodes.InvalidParams, null,
                $"'{trimmed}' is not the URL of a repository this machine can clone: https, ssh or git, to a host/owner/repo.");
        var target = space is null ? null : await ResolveSpaceAsync(space, cancellationToken).ConfigureAwait(false);
        var reused = Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        if (await FleetRepositoryClone.CloneAsync(trimmed, path, key!, cancellationToken).ConfigureAwait(false) is { } failure)
            throw new FleetException(FleetNodeErrorCodes.CloneFailed, null, $"{trimmed} could not be cloned into {path}: {failure}");
        return await AddFolderAsync(path, target, reused ? $"{path} held a checkout of {key} already: it was added as it is." : $"Cloned into {path}.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a project from the catalog; its folder and files stay as they are.</summary>
    /// <param name="reference">The project: its id, its slug or its name.</param>
    /// <param name="countSessions">Counts the sessions of the project, which keep it from being removed; null when they cannot be known.</param>
    /// <param name="cancellationToken">Cancels before the file is deleted.</param>
    /// <returns>The project as it was.</returns>
    /// <exception cref="FleetException">No project or several have that name, or the project has sessions.</exception>
    public async Task<FleetProjectChange> RemoveAsync(string reference, Func<ProjectDescriptor, CancellationToken, Task<int>>? countSessions,
        CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(reference, cancellationToken).ConfigureAwait(false);
        if (countSessions is not null && await countSessions(project, cancellationToken).ConfigureAwait(false) is > 0 and var count)
            throw new FleetException(FleetNodeErrorCodes.ProjectHasSessions, null,
                $"{Name(project)} has {count} session(s) on this machine and was not removed: they would stay with no project to show them in.");
        if (!await _projects.DeleteAsync(project, cancellationToken).ConfigureAwait(false))
            throw new FleetException(FleetNodeErrorCodes.ProjectNotFound, null, $"No catalog file was found for {Name(project)}: nothing was removed.");
        return new FleetProjectChange(Describe(project), true, $"Its folder {project.ProjectPath} was left as it is.");
    }

    /// <summary>Describes a project as the node API does.</summary>
    /// <param name="project">The project.</param>
    /// <param name="location">Where its folder is in its repository; null when unknown.</param>
    /// <returns>The description.</returns>
    public static FleetProjectInfo Describe(ProjectDescriptor project, RepositoryLocation? location = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new FleetProjectInfo(project.Id, Name(project), project.ProjectPath, [.. project.Spaces])
        {
            RepositoryKey = location?.Key,
            RepositoryPath = location?.Path,
            Archived = project.Archived,
        };
    }

    /// <summary>The diagnostic of a project file that could not be read.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The diagnostic.</returns>
    public static FleetNodeDiagnostic Diagnose(UnreadableProjectFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new FleetNodeDiagnostic(FleetNodeDiagnostic.UnreadableProject,
            $"The project file {file.SourcePath} cannot be read, and its project is not listed: {file.Message}", file.SourcePath);
    }

    private static string Name(ProjectDescriptor project) => string.IsNullOrWhiteSpace(project.DisplayName) ? project.Name : project.DisplayName;

    private async Task<FleetProjectChange> AddFolderAsync(string folder, SpaceDescriptor? space, string? note, CancellationToken cancellationToken)
    {
        var existing = await _projects.GetByPathAsync(folder, cancellationToken).ConfigureAwait(false);
        var project = await _projects.UpsertFromPathAsync(folder, cancellationToken).ConfigureAwait(false);
        var changed = existing is null || existing.Archived;
        // Every project is in the default space already.
        if (space is { IsDefault: false } && !project.Spaces.Contains(space.Id, StringComparer.Ordinal))
        {
            var assigned = await _spaces.AssignAsync(project.Id, [space.Id], null, cancellationToken).ConfigureAwait(false);
            if (!assigned.Succeeded)
                throw new FleetException(FleetErrorCodes.InvalidRequest, null,
                    $"{Name(project)} is in the catalog, but it did not join the space {space.Name}: {assigned.Message ?? assigned.Status.ToString()}");
            project = await _projects.GetByIdAsync(project.Id, cancellationToken).ConfigureAwait(false) ?? project;
            changed = true;
        }

        return new FleetProjectChange(Describe(project), changed, note);
    }

    private async Task<SpaceDescriptor> ResolveSpaceAsync(string reference, CancellationToken cancellationToken)
    {
        var wanted = reference.Trim();
        var spaces = await _spaces.LoadAsync(cancellationToken).ConfigureAwait(false);
        var matches = spaces.Where(space => string.Equals(space.Id, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(space.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new FleetException(FleetErrorCodes.InvalidParams, null,
                $"No space is named '{wanted}': {string.Join(", ", spaces.Select(static space => space.Name))}."),
            _ => throw new FleetException(FleetErrorCodes.InvalidParams, null,
                $"'{wanted}' names several spaces ({string.Join(", ", matches.Select(static space => space.Id))}): give the space id."),
        };
    }

    private async Task<ProjectDescriptor> ResolveProjectAsync(string reference, CancellationToken cancellationToken)
    {
        var wanted = reference?.Trim() ?? "";
        if (wanted.Length == 0) throw new FleetException(FleetErrorCodes.InvalidParams, null, "Name the project: its id, its slug or its name.");
        var projects = (await _projects.LoadReadableAsync(cancellationToken).ConfigureAwait(false)).Projects;
        var matches = projects.Where(project => string.Equals(project.Id, wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            matches = [.. projects.Where(project => string.Equals(project.Slug, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Name(project), wanted, StringComparison.OrdinalIgnoreCase))];
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new FleetException(FleetNodeErrorCodes.ProjectNotFound, null, $"No project is named '{wanted}' (altanode projects list lists them)."),
            _ => throw new FleetException(FleetErrorCodes.InvalidParams, null,
                $"'{wanted}' names several projects ({string.Join(", ", matches.Select(static project => project.Id))}): give the project id."),
        };
    }
}
