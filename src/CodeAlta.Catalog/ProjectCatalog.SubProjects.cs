using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Catalog;

/// <summary>An entry of the map of a repository, with the folder it names and the project it is now.</summary>
/// <param name="Entry">The entry of the map.</param>
/// <param name="FullPath">The folder it names.</param>
/// <param name="Folder">Whether that folder is there, inside the repository.</param>
/// <param name="Project">The project of the folder: a sub-project of the repository, archived when the user hid it; null for an optional entry nobody added, or a folder that is not there.</param>
/// <param name="ClaimedBy">The repository project whose map is the one that counts for the folder, when it is another one (a nearer repository declares it too).</param>
public sealed record SubProjectStatus(ProjectManifestEntry Entry, string FullPath, ProjectManifestFolderState Folder, ProjectDescriptor? Project, string? ClaimedBy = null);

/// <summary>The map of a repository project as the catalog reads it, with what each entry is.</summary>
/// <param name="Repository">The repository project.</param>
/// <param name="ManifestPath">The file of the map.</param>
/// <param name="Exists">Whether the file exists.</param>
/// <param name="Manifest">The map as read; <see cref="ProjectManifest.Empty"/> without a file.</param>
/// <param name="Entries">Its entries, in the order of the file.</param>
/// <param name="Problems">What was left out: the problems of the file, then those of the folders.</param>
/// <param name="ClaimedBy">The repository whose map declares the folder of this project, which makes its own map ignored; null otherwise.</param>
public sealed record RepositoryMap(ProjectDescriptor Repository, string ManifestPath, bool Exists, ProjectManifest Manifest,
    IReadOnlyList<SubProjectStatus> Entries, IReadOnlyList<ProjectManifestProblem> Problems, string? ClaimedBy = null);

/// <summary>How showing or hiding a sub-project ended.</summary>
public enum SubProjectShowStatus
{
    /// <summary>The sub-project is shown, or hidden, as asked.</summary>
    Updated,
    /// <summary>No repository project or no entry of its map has this identifier or key.</summary>
    NotFound,
    /// <summary>The folder of the entry is not there, or leads out of the repository.</summary>
    FolderUnavailable,
    /// <summary>The file of the project could not be edited, or kept changing.</summary>
    Conflict,
}

public sealed partial class ProjectCatalog
{
    // The maps of the repositories as this catalog read them, parsed again only when a file changes.
    private readonly ParsedFileCache<ProjectManifest> _manifests = new();

    // Where the folders of each map are, as the file system said for the map as it was read: a map read again as it was
    // keeps them, and only the folders that were missing are looked at again, so that a load walks no repository.
    private readonly object _foldersGate = new();

    // How long the state of the folders of a map is trusted before every folder is looked at again.
    private const long FolderRecheckMilliseconds = 30_000;
    private readonly Dictionary<string, (ProjectManifest Map, Dictionary<string, (string FullPath, ProjectManifestFolderState State)> Folders, long CheckedAt)> _folders
        = new(StringComparer.Ordinal);

    // The folders each sub-project inherits from, as the last load found them, replaced whole by each load: for the readers
    // that hold an identifier of a project and no descriptor.
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _inheritedRoots = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Gets the folders a project inherits its setup from, the nearest first (<see cref="ProjectResourceRoots.Inherited"/>),
    /// as the last load of this catalog found them, without reading anything: for a reader that holds the identifier of a
    /// project and no descriptor. A reader that has the descriptor takes them from it.
    /// </summary>
    /// <param name="projectId">The identifier of the project; null for none.</param>
    /// <returns>The folders; empty for a project that is no sub-project, or before the first load.</returns>
    public IReadOnlyList<string> GetInheritedRoots(string? projectId)
        => projectId is not null && Volatile.Read(ref _inheritedRoots).TryGetValue(projectId, out var roots) ? roots : [];

    // The same, by the folder of the project.
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _inheritedRootsOfFolders = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Gets the folders a project inherits its setup from, as <see cref="GetInheritedRoots"/> does, by the folder of the
    /// project: for a reader that holds a folder and no identifier, such as a plugin.
    /// </summary>
    /// <param name="projectPath">The folder of the project; null for none.</param>
    /// <returns>The folders; empty for a folder that is no sub-project, or before the first load.</returns>
    public IReadOnlyList<string> GetInheritedRootsOfFolder(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return [];
        string folder;
        try
        {
            folder = NormalizePath(projectPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }

        return Volatile.Read(ref _inheritedRootsOfFolders).TryGetValue(folder, out var roots) ? roots : [];
    }

    /// <summary>Works out the identifier of a sub-project from its repository project and its key in the map.</summary>
    /// <param name="repositoryId">The identifier of the repository project.</param>
    /// <param name="key">The key of the sub-project in the map.</param>
    /// <returns>A GUID that is the same on every load and every machine where the repository project has this identifier.</returns>
    /// <exception cref="ArgumentException">An argument is blank.</exception>
    public static string DeriveSubProjectId(string repositoryId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"codealta.subproject\n{repositoryId.Trim().ToLowerInvariant()}\n{key}"), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80); // Version 8: a GUID of its own scheme.
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); // The variant of RFC 9562.
        return new Guid(bytes, bigEndian: true).ToString();
    }

    /// <summary>Works out the identifier of a label of a repository map.</summary>
    /// <param name="repositorySlug">The slug of the repository project.</param>
    /// <param name="key">The key of the label in the map.</param>
    /// <returns><c>&lt;slug&gt;.&lt;key&gt;</c>, the slug shortened when the whole would be longer than an identifier of a label can be.</returns>
    /// <exception cref="ArgumentException">An argument is blank.</exception>
    public static string DeclaredLabelId(string repositorySlug, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositorySlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var room = 64 - key.Length - 1;
        var slug = repositorySlug.Length <= room ? repositorySlug : repositorySlug[..Math.Max(1, room)].TrimEnd('-', '_', '.');
        return $"{slug}.{key}";
    }

    /// <summary>
    /// Reads the map of a repository project, with what each of its entries is now: a sub-project shown or hidden, an
    /// optional entry nobody added, or a folder that is not there.
    /// </summary>
    /// <param name="projectId">The identifier of the repository project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The map, or null when no project has this identifier or the project is itself a sub-project.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<RepositoryMap?> ReadRepositoryMapAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var projects = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var repository = projects.FirstOrDefault(project => string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (repository is null) return null;
        var path = ProjectManifest.PathOf(repository.ProjectPath);
        var exists = File.Exists(path);
        var map = exists ? await ReadManifestAsync(repository.ProjectPath, cancellationToken).ConfigureAwait(false) : ProjectManifest.Empty;
        var folders = Folders(repository.ProjectPath, map);
        var byPath = projects.ToDictionary(static project => project.ProjectPath, StringComparer.OrdinalIgnoreCase);
        var entries = new List<SubProjectStatus>(map.Projects.Count);
        var problems = new List<ProjectManifestProblem>(map.Problems);
        foreach (var entry in map.Projects)
        {
            var (full, state) = folders[entry.Key];
            ProjectDescriptor? project = null;
            string? claimedBy = null;
            if (state == ProjectManifestFolderState.Ok && byPath.TryGetValue(NormalizePath(full), out var found))
            {
                if (found.ParentId == repository.Id) project = found;
                else claimedBy = found.ParentId;
            }

            if (state == ProjectManifestFolderState.Missing)
                problems.Add(new(entry.Key, "folder.missing", $"The folder `{entry.Path}` of `{entry.Key}` is not there."));
            else if (state == ProjectManifestFolderState.Outside)
                problems.Add(new(entry.Key, "folder.outside", $"The folder `{entry.Path}` of `{entry.Key}` is a link that leads out of the repository: it is left out."));
            entries.Add(new(entry, full, state, project, claimedBy));
        }

        return new(repository, path, exists, map, entries, problems, repository.ParentId);
    }

    /// <summary>
    /// Shows or hides a sub-project of a repository for the user: hiding archives it in the user's file of the project,
    /// showing brings it back, or adds an optional one. The map of the repository is not changed.
    /// </summary>
    /// <param name="repositoryId">The identifier of the repository project.</param>
    /// <param name="key">The key of the sub-project in the map.</param>
    /// <param name="shown">Whether it is shown.</param>
    /// <param name="cancellationToken">Cancels before a file is written.</param>
    /// <returns>How it ended, with the sub-project when there is one.</returns>
    /// <exception cref="InvalidOperationException">The catalog is read-only (<see cref="IsReadOnly"/>).</exception>
    /// <exception cref="IOException">A file could not be written.</exception>
    public async Task<(SubProjectShowStatus Status, ProjectDescriptor? Project)> SetSubProjectShownAsync(string repositoryId, string key, bool shown,
        CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var map = await ReadRepositoryMapAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        if (map is null || map.ClaimedBy is not null || map.Entries.FirstOrDefault(entry => entry.Entry.Key == key) is not { } status)
            return (SubProjectShowStatus.NotFound, null);
        if (status.Folder != ProjectManifestFolderState.Ok || status.ClaimedBy is not null) return (SubProjectShowStatus.FolderUnavailable, null);
        var project = status.Project ?? DeclaredProject(map.Repository, status.Entry, NormalizePath(status.FullPath), await LoadAsync(cancellationToken).ConfigureAwait(false));
        if (project.SourcePath is null)
        {
            if (!shown && project.Archived) return (SubProjectShowStatus.Updated, project);
            project.Archived = !shown;
            await SaveAsync(project, ProjectOrigins.Window, cancellationToken).ConfigureAwait(false);
            return (SubProjectShowStatus.Updated, project);
        }

        if (project.Archived != shown) return (SubProjectShowStatus.Updated, project);
        var snapshot = await ReadArchiveAsync(project.Id, project.ProjectPath, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return (SubProjectShowStatus.Conflict, project);
        var result = await SetArchivedAsync(project.Id, project.ProjectPath, snapshot.SourcePath, snapshot.Revision, snapshot.Archived, !shown, cancellationToken)
            .ConfigureAwait(false);
        if (result != ProjectDisplayNameRenameStatus.Updated) return (SubProjectShowStatus.Conflict, project);
        project.Archived = !shown;
        return (SubProjectShowStatus.Updated, project);
    }

    /// <summary>Lists the repository projects whose map counts: projects that no other map declares, whose map declares something.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each repository with its map, in the order of the catalog.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<IReadOnlyList<(ProjectDescriptor Repository, ProjectManifest Map)>> LoadRepositoryMapsAsync(CancellationToken cancellationToken = default)
    {
        var maps = new List<(ProjectDescriptor Repository, ProjectManifest Map)>();
        foreach (var project in await LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (project.IsSubProject || !File.Exists(ProjectManifest.PathOf(project.ProjectPath))) continue;
            var map = await ReadManifestAsync(project.ProjectPath, cancellationToken).ConfigureAwait(false);
            if (map.Projects.Count > 0 || map.Labels.Count > 0) maps.Add((project, map));
        }

        return maps;
    }

    /// <summary>Reads the map of a repository folder as this catalog keeps it. A file that cannot be read is a map with that problem.</summary>
    /// <param name="repositoryPath">The folder of the repository project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The map; <see cref="ProjectManifest.Empty"/> without a file.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<ProjectManifest> ReadManifestAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var path = ProjectManifest.PathOf(repositoryPath);
        try
        {
            if (!File.Exists(path)) return ProjectManifest.Empty;
            return await _manifests.ReadAsync(path, static (_, text) => ProjectManifest.Parse(text), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ProjectManifest.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return ProjectManifest.Unreadable(exception.Message);
        }
    }

    // The folders of the entries of a map, looked at once for each text of the map; the missing ones each time.
    private Dictionary<string, (string FullPath, ProjectManifestFolderState State)> Folders(string repositoryPath, ProjectManifest map)
    {
        var path = ProjectManifest.PathOf(repositoryPath);
        Dictionary<string, (string FullPath, ProjectManifestFolderState State)>? known = null;
        var now = Environment.TickCount64;
        var recheck = false;
        lock (_foldersGate)
        {
            if (_folders.TryGetValue(path, out var kept) && ReferenceEquals(kept.Map, map))
            {
                known = kept.Folders;
                // A folder found inside the repository is looked at again from time to time: a link put in its place
                // could lead out of it.
                recheck = now - kept.CheckedAt > FolderRecheckMilliseconds;
            }
        }

        var folders = new Dictionary<string, (string FullPath, ProjectManifestFolderState State)>(map.Projects.Count, StringComparer.Ordinal);
        var changed = known is null;
        foreach (var entry in map.Projects)
        {
            if (!recheck && known is not null && known.TryGetValue(entry.Key, out var seen) && seen.State != ProjectManifestFolderState.Missing)
            {
                folders[entry.Key] = seen;
                continue;
            }

            var resolved = ProjectManifest.ResolveFolder(repositoryPath, entry.Path);
            changed |= known is null || !known.TryGetValue(entry.Key, out var before) || before != resolved;
            folders[entry.Key] = resolved;
        }

        if (changed || recheck)
        {
            lock (_foldersGate)
            {
                if (_folders.Count >= 256 && !_folders.ContainsKey(path)) _folders.Clear();
                _folders[path] = (map, folders, now);
            }
        }

        return folders;
    }

    // The projects of a load with the sub-projects the maps of the others declare. A map is read for a project that no
    // other map declares; the folder of a project that two maps declare belongs to the nearer repository.
    private async Task<List<ProjectDescriptor>> WithSubProjectsAsync(List<ProjectDescriptor> projects, CancellationToken cancellationToken)
    {
        var result = await WithDeclaredSubProjectsAsync(projects, cancellationToken).ConfigureAwait(false);
        var inherited = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var ofFolders = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in result)
        {
            if (!project.IsSubProject) continue;
            IReadOnlyList<string> roots = [.. project.InheritedRoots];
            inherited[project.Id] = roots;
            ofFolders[project.ProjectPath] = roots;
        }

        Volatile.Write(ref _inheritedRoots, inherited);
        Volatile.Write(ref _inheritedRootsOfFolders, ofFolders);
        return result;
    }

    private async Task<List<ProjectDescriptor>> WithDeclaredSubProjectsAsync(List<ProjectDescriptor> projects, CancellationToken cancellationToken)
    {
        if (projects.Count == 0) return projects;
        var claims = new Dictionary<string, (ProjectDescriptor Repository, ProjectManifestEntry Entry)>(StringComparer.OrdinalIgnoreCase);
        var repositories = new List<(ProjectDescriptor Repository, ProjectManifest Map)>();
        foreach (var project in projects.OrderBy(static project => project.ProjectPath.Length).ThenBy(static project => project.ProjectPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (claims.ContainsKey(project.ProjectPath) || !File.Exists(ProjectManifest.PathOf(project.ProjectPath))) continue;
            var map = await ReadManifestAsync(project.ProjectPath, cancellationToken).ConfigureAwait(false);
            if (map.Projects.Count == 0) continue;
            repositories.Add((project, map));
            var folders = Folders(project.ProjectPath, map);
            foreach (var entry in map.Projects)
            {
                var (full, state) = folders[entry.Key];
                if (state != ProjectManifestFolderState.Ok) continue;
                var folder = NormalizePath(full);
                if (!string.Equals(folder, project.ProjectPath, StringComparison.OrdinalIgnoreCase)) claims[folder] = (project, entry);
            }
        }

        if (claims.Count == 0) return projects;
        var byPath = projects.ToDictionary(static project => project.ProjectPath, StringComparer.OrdinalIgnoreCase);
        var byId = projects.ToDictionary(static project => project.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<ProjectDescriptor>(projects.Count + claims.Count);
        result.AddRange(projects);
        var slugs = projects.Select(static project => project.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (repository, map) in repositories)
        {
            foreach (var entry in map.Projects)
            {
                var full = NormalizePath(Path.Combine(repository.ProjectPath, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!claims.TryGetValue(full, out var claim) || claim.Repository.Id != repository.Id || claim.Entry.Key != entry.Key) continue;
                if (byPath.TryGetValue(full, out var existing))
                {
                    Link(existing, repository, entry, claims);
                    continue;
                }

                // A project file of this sub-project whose folder moved: the key names it. Its file is told the new
                // folder before the first edit that needs it (GetEditableAsync).
                if (byId.TryGetValue(DeriveSubProjectId(repository.Id, entry.Key), out var moved))
                {
                    if (claims.ContainsKey(moved.ProjectPath) || Directory.Exists(moved.ProjectPath)) continue; // Two folders, one identity: the file keeps it.
                    moved.MovedFrom = moved.ProjectPath;
                    moved.ProjectPath = full;
                    Link(moved, repository, entry, claims);
                    continue;
                }

                if (entry.Optional) continue;
                var declared = DeclaredProject(repository, entry, full, slugs);
                Link(declared, repository, entry, claims);
                slugs.Add(declared.Slug);
                result.Add(declared);
            }
        }

        return result;
    }

    // An optional sub-project the user adds: what its file holds; the next load links it to the folders above it.
    private static ProjectDescriptor DeclaredProject(ProjectDescriptor repository, ProjectManifestEntry entry, string folder, IReadOnlyList<ProjectDescriptor> projects)
    {
        var project = DeclaredProject(repository, entry, folder, projects.Select(static project => project.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase));
        Link(project, repository, entry, new Dictionary<string, (ProjectDescriptor Repository, ProjectManifestEntry Entry)>());
        return project;
    }

    private static ProjectDescriptor DeclaredProject(ProjectDescriptor repository, ProjectManifestEntry entry, string folder, HashSet<string> slugs)
    {
        var baseSlug = Slugify($"{repository.Slug}-{entry.Key}");
        var slug = baseSlug;
        for (var suffix = 2; slugs.Contains(slug); suffix++) slug = $"{baseSlug}-{suffix}";
        var displayName = ProjectPathNameFormatter.InferDisplayName(folder);
        return new ProjectDescriptor
        {
            Id = DeriveSubProjectId(repository.Id, entry.Key),
            Slug = slug,
            Name = ProjectPathNameFormatter.InferName(folder, entry.Key),
            DisplayName = displayName,
            ProjectPath = folder,
            DefaultBranch = repository.DefaultBranch,
            Archived = repository.Archived,
            MarkdownBody = $"# {displayName}\n\nA project of the repository {repository.DisplayName}, declared in its `.alta/{ProjectManifest.FileName}`.",
        };
    }

    // Makes a project the sub-project an entry of a map declares: what the map says shows over what its file does not
    // say, and its file keeps its own values (ProjectDescriptor.ForFile).
    private static void Link(ProjectDescriptor project, ProjectDescriptor repository, ProjectManifestEntry entry,
        IReadOnlyDictionary<string, (ProjectDescriptor Repository, ProjectManifestEntry Entry)> claims)
    {
        project.FileSpaces = [.. project.Spaces];
        project.FileDisplayName = project.SourcePath is null ? null : project.DisplayName;
        project.FileDescription = project.Description;
        project.Spaces = [.. repository.Spaces];
        if (entry.Name is { } name && string.Equals(project.DisplayName, ProjectPathNameFormatter.InferDisplayName(project.ProjectPath), StringComparison.Ordinal))
            project.DisplayName = name;
        project.Description ??= entry.Description;
        project.ParentId = repository.Id;
        project.SubPath = entry.Path;
        project.DeclaredKey = entry.Key;
        project.DeclaredLabels = [.. entry.Labels.Select(key => DeclaredLabelId(repository.Slug, key))];
        project.DeclaredOptional = entry.Optional;
        project.WriteScope = entry.Write;
        project.InheritedSandbox = repository.Sandbox;
        project.InheritedSkillFolders = [.. repository.SkillFolders];
        project.RepositoryName = repository.DisplayName;
        project.RepositorySlug = repository.Slug;
        // The declared folders of the same repository that hold this one, the nearest first, then the repository.
        var roots = new List<string>();
        for (var folder = Path.GetDirectoryName(project.ProjectPath); folder is not null && folder.Length > repository.ProjectPath.Length;
             folder = Path.GetDirectoryName(folder))
        {
            if (claims.TryGetValue(folder, out var above) && above.Repository.Id == repository.Id) roots.Add(folder);
        }

        roots.Add(repository.ProjectPath);
        project.InheritedRoots = roots;
    }
}
