using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Catalog;

/// <summary>An entry of the map of a repository, with the folder it names and the project it is now.</summary>
/// <param name="Entry">The entry of the map.</param>
/// <param name="FullPath">The folder it names.</param>
/// <param name="Folder">Whether that folder is there, inside the repository.</param>
/// <param name="Project">The project of the folder: a sub-project of the repository, archived when the user hid it; null for an optional entry nobody added, or a folder that is not there.</param>
/// <param name="ClaimedBy">The repository project whose map is the one that counts for the folder, when it is another one (a nearer repository declares it too).</param>
/// <param name="SubProjects">How many sub-projects the project of the entry has, when it has a map of its own (a nested map).</param>
public sealed record SubProjectStatus(ProjectManifestEntry Entry, string FullPath, ProjectManifestFolderState Folder, ProjectDescriptor? Project, string? ClaimedBy = null,
    int SubProjects = 0);

/// <summary>The map of a repository project as the catalog reads it, with what each entry is.</summary>
/// <param name="Repository">The repository project.</param>
/// <param name="ManifestPath">The file of the map.</param>
/// <param name="Exists">Whether the file exists.</param>
/// <param name="Manifest">The map as read; <see cref="ProjectManifest.Empty"/> without a file.</param>
/// <param name="Entries">Its entries, in the order of the file.</param>
/// <param name="Problems">What was left out: why the map is not read, when it is not, the problems of the file, then those of the folders.</param>
/// <param name="ClaimedBy">
/// The repository whose map declares the folder of this project, when the map of this project is not read: nested deeper
/// than <see cref="ProjectManifest.MaximumDepth"/>, or its folder is, through a link, the one of a repository whose map is
/// read already. Null when the map counts, the map of a sub-project included.
/// </param>
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

    // Where the folders of the nested maps are on the disk, under the same gate and for the same time as _folders.
    private readonly Dictionary<string, (string Real, long CheckedAt)> _realFolders = new(StringComparer.OrdinalIgnoreCase);

    // The maps of sub-projects the last load did not read, by the folder of the sub-project, with why; replaced whole by each load.
    private IReadOnlyDictionary<string, ProjectManifestProblem> _ignoredMaps = new Dictionary<string, ProjectManifestProblem>();

    // The repository whose map won each folder a map declares, as the last load found it, by the folder; replaced whole by
    // each load. An entry that made no project (optional, nobody added it) wins its folder all the same.
    private IReadOnlyDictionary<string, string> _folderOwners = new Dictionary<string, string>();

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
        if (repositorySlug.Length <= room) return $"{repositorySlug}.{key}";
        // Shortened, the slug ends with a hash of the whole of it: two repositories that start alike keep labels of their own.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repositorySlug)))[..6];
        var slug = repositorySlug[..Math.Max(1, room - hash.Length - 1)].TrimEnd('-', '_', '.');
        return $"{slug}-{hash}.{key}";
    }

    /// <summary>
    /// Reads the map of a repository project, with what each of its entries is now: a sub-project shown or hidden, an
    /// optional entry nobody added, or a folder that is not there.
    /// </summary>
    /// <param name="projectId">The identifier of the repository project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The map, or null when no project has this identifier. The map of a sub-project is its own: a nested map, which
    /// counts unless <see cref="RepositoryMap.ClaimedBy"/> says why it does not.
    /// </returns>
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
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            names.TryAdd(project.Id, project.DisplayName);
            if (project.ParentId is { } parentId) counts[parentId] = counts.GetValueOrDefault(parentId) + 1;
        }

        // Why the maps the last load did not read are left out: this one, and those of its entries.
        var ignored = Volatile.Read(ref _ignoredMaps);
        var owners = Volatile.Read(ref _folderOwners);
        var notRead = exists && ignored.TryGetValue(repository.ProjectPath, out var why) ? why : null;
        var entries = new List<SubProjectStatus>(map.Projects.Count);
        var problems = new List<ProjectManifestProblem>(map.Problems.Count + 1);
        if (notRead is not null) problems.Add(notRead);
        problems.AddRange(map.Problems);
        foreach (var entry in map.Projects)
        {
            var (full, state) = folders[entry.Key];
            ProjectDescriptor? project = null;
            string? claimedBy = null;
            ProjectDescriptor? found = null;
            if (state == ProjectManifestFolderState.Ok && byPath.TryGetValue(NormalizePath(full), out found))
            {
                if (found.ParentId == repository.Id) project = found;
                else claimedBy = found.ParentId;
            }
            // A nearer map that declares the folder wins it also when its entry made no project: an optional one nobody added.
            else if (state == ProjectManifestFolderState.Ok && owners.TryGetValue(NormalizePath(full), out var owner)
                && !string.Equals(owner, repository.Id, StringComparison.OrdinalIgnoreCase))
            {
                claimedBy = owner;
            }

            if (state == ProjectManifestFolderState.Missing)
                problems.Add(new(entry.Key, "folder.missing", $"The folder `{entry.Path}` of `{entry.Key}` is not there."));
            else if (state == ProjectManifestFolderState.Outside)
                problems.Add(new(entry.Key, "folder.outside", $"The folder `{entry.Path}` of `{entry.Key}` is a link that leads out of the repository, or into a folder of settings: it is left out."));
            else if (claimedBy is not null && notRead is null)
            {
                var nearer = names.GetValueOrDefault(claimedBy, claimedBy);
                problems.Add(new(entry.Key, "folder.claimed", found is null
                    ? $"The folder `{entry.Path}` of `{entry.Key}` is declared by the map of {nearer} too, which is nearer and wins: it is an optional sub-project of {nearer}, to add there."
                    : $"The folder `{entry.Path}` of `{entry.Key}` is declared by the map of {nearer} too, which is nearer and wins: it is a sub-project of {nearer}."));
            }

            if (project is not null && ignored.TryGetValue(project.ProjectPath, out var nested)) problems.Add(nested with { Key = entry.Key });
            entries.Add(new(entry, full, state, project, claimedBy, project is null ? 0 : counts.GetValueOrDefault(project.Id)));
        }

        return new(repository, path, exists, map, entries, problems, notRead is null ? null : repository.ParentId);
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
        var projects = await LoadAsync(cancellationToken).ConfigureAwait(false);
        // A project file already holds the identity of this entry, in the folder it had before it moved: one identity, one file.
        if (status.Project is null && projects.Any(other => string.Equals(other.Id, DeriveSubProjectId(map.Repository.Id, key), StringComparison.OrdinalIgnoreCase)))
            return (SubProjectShowStatus.Conflict, null);
        var project = status.Project ?? DeclaredProject(map.Repository, status.Entry, NormalizePath(status.FullPath), projects);
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

    /// <summary>
    /// Lists the repository projects whose map counts and declares something: the projects of their own, and the
    /// sub-projects whose map is read (nested maps).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each repository with its map, in the order of the catalog.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<IReadOnlyList<(ProjectDescriptor Repository, ProjectManifest Map)>> LoadRepositoryMapsAsync(CancellationToken cancellationToken = default)
    {
        var maps = new List<(ProjectDescriptor Repository, ProjectManifest Map)>();
        var projects = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var ignored = Volatile.Read(ref _ignoredMaps);
        foreach (var project in projects)
        {
            if (!File.Exists(ProjectManifest.PathOf(project.ProjectPath)) || project.IsSubProject && ignored.ContainsKey(project.ProjectPath)) continue;
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
            var file = new FileInfo(path);
            if (!file.Exists) return ProjectManifest.Empty;
            // A file larger than a map can be is not read at all: the bound holds before anything is allocated.
            if (file.Length > ProjectManifest.MaximumBytes + 4) return ProjectManifest.TooLarge();
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
            // A missing folder too: a load does not probe the many absent folders of a sparse checkout each time.
            if (!recheck && known is not null && known.TryGetValue(entry.Key, out var seen))
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

    // The projects of a load with the sub-projects the maps declare: the maps of the projects, then those of their
    // sub-projects (nested maps), down to ProjectManifest.MaximumDepth. The folder of a project that two maps declare
    // belongs to the nearer repository.
    private async Task<List<ProjectDescriptor>> WithSubProjectsAsync(List<ProjectDescriptor> projects, CancellationToken cancellationToken)
    {
        var ignored = new Dictionary<string, ProjectManifestProblem>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = await WithDeclaredSubProjectsAsync(projects, ignored, owners, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _ignoredMaps, ignored);
        Volatile.Write(ref _folderOwners, owners);
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

    // An entry of a map that declares a folder: the repository whose map it is, its place in the map, and how deep that
    // map is (1 for the map of a project of its own). Order is the place of the repository among those read.
    private readonly record struct MapClaim(ProjectDescriptor Repository, ProjectManifestEntry Entry, int Index, int Depth, int Order);

    // The folders of a load from the outside in: a repository comes before the folders of its map, which are inside it.
    private static readonly Comparer<string> OutsideIn = Comparer<string>.Create(static (left, right)
        => left.Length != right.Length ? left.Length.CompareTo(right.Length) : StringComparer.OrdinalIgnoreCase.Compare(left, right));

    private async Task<List<ProjectDescriptor>> WithDeclaredSubProjectsAsync(List<ProjectDescriptor> projects, Dictionary<string, ProjectManifestProblem> ignored,
        Dictionary<string, string> owners, CancellationToken cancellationToken)
    {
        if (projects.Count == 0) return projects;
        // The folders are taken from the outside in, so that every map that can declare a folder (those of the folders
        // above it) is read before the folder is: the claim of the nearer one stays, and the folder then is a project of
        // that repository. A sub-project with a map of its own is read in its turn, one level deeper.
        var claims = new Dictionary<string, MapClaim>(StringComparer.OrdinalIgnoreCase);
        var queue = new PriorityQueue<string, string>(OutsideIn);
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            if (queued.Add(project.ProjectPath)) queue.Enqueue(project.ProjectPath, project.ProjectPath);
        }

        var byPath = new Dictionary<string, ProjectDescriptor>(projects.Count, StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, ProjectDescriptor>(projects.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            byPath.TryAdd(project.ProjectPath, project);
            byId.TryAdd(project.Id, project);
        }

        HashSet<string>? slugs = null;
        var declared = new List<(int Order, int Index, ProjectDescriptor Project)>();
        // The folders of the nested maps read, as they are on the disk.
        var nestedFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var repositories = 0;
        while (queue.TryDequeue(out var folder, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectDescriptor? repository;
            var depth = 1;
            if (claims.TryGetValue(folder, out var claim))
            {
                repository = Declare(folder, claim);
                if (repository is null) continue;
                depth = claim.Depth + 1;
            }
            // A project file whose folder moved into a folder a map declares is that sub-project now, and no other.
            else if (!byPath.TryGetValue(folder, out repository) || repository.IsSubProject)
            {
                continue;
            }

            if (!File.Exists(ProjectManifest.PathOf(folder))) continue;
            var map = await ReadManifestAsync(folder, cancellationToken).ConfigureAwait(false);
            if (map.Projects.Count == 0) continue;
            if (depth > 1 && NotRead(folder, claim, depth) is { } problem)
            {
                ignored[folder] = problem;
                continue;
            }

            var order = repositories++;
            var folders = Folders(folder, map);
            for (var index = 0; index < map.Projects.Count; index++)
            {
                var entry = map.Projects[index];
                var (full, state) = folders[entry.Key];
                if (state != ProjectManifestFolderState.Ok) continue;
                var target = NormalizePath(full);
                if (string.Equals(target, folder, StringComparison.OrdinalIgnoreCase)) continue;
                // Inside this folder, so not taken yet: a nearer map read later declares it in its place.
                claims[target] = new(repository, entry, index, depth, order);
                if (queued.Add(target)) queue.Enqueue(target, target);
            }
        }

        foreach (var (target, claim) in claims) owners[target] = claim.Repository.Id;
        if (declared.Count == 0) return projects;
        var result = new List<ProjectDescriptor>(projects.Count + declared.Count);
        result.AddRange(projects);
        result.AddRange(declared.OrderBy(static value => value.Order).ThenBy(static value => value.Index).Select(static value => value.Project));
        return result;

        // The project of a folder a map declares: the project of the folder, the project file of the entry whose folder
        // moved, or a sub-project made from the entry; null for an optional entry nobody added.
        ProjectDescriptor? Declare(string full, MapClaim claim)
        {
            var (repository, entry) = (claim.Repository, claim.Entry);
            if (byPath.TryGetValue(full, out var existing))
            {
                Link(existing, repository, entry, claims);
                return existing;
            }

            // A project file of this sub-project whose folder moved: the key names it. Its file is told the new
            // folder before the first edit that needs it (GetEditableAsync).
            // A project added by hand before the map declared its folder keeps its own identifier: its file names the entry.
            var binding = BindingOf(repository, entry.Key);
            if (byId.TryGetValue(DeriveSubProjectId(repository.Id, entry.Key), out var moved)
                || (moved = projects.FirstOrDefault(other => other.SourcePath is not null && string.Equals(other.DeclaredAs, binding, StringComparison.OrdinalIgnoreCase))) is not null)
            {
                if (moved.IsSubProject || claims.ContainsKey(moved.ProjectPath) || Directory.Exists(moved.ProjectPath)) return null; // Two folders, one identity: the file keeps it.
                moved.MovedFrom = moved.ProjectPath;
                moved.ProjectPath = full;
                Link(moved, repository, entry, claims);
                return moved;
            }

            if (entry.Optional) return null;
            slugs ??= projects.Select(static project => project.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var project = DeclaredProject(repository, entry, full, slugs);
            Link(project, repository, entry, claims);
            slugs.Add(project.Slug);
            declared.Add((claim.Order, claim.Index, project));
            return project;
        }

        // Why the map of a sub-project is not read: deeper than maps go, or its folder is, through a link, the folder of
        // a repository above it (a loop) or of another nested map read already. Null when it is read.
        ProjectManifestProblem? NotRead(string folder, MapClaim claim, int depth)
        {
            if (depth > ProjectManifest.MaximumDepth)
            {
                return new(null, "map.tooDeep", $"Its own project map is not read: maps nest {ProjectManifest.MaximumDepth} levels deep at most, and it would be level {depth}. "
                    + "Declare its projects in a map above it.");
            }

            var real = RealFolder(folder);
            var above = claim.Repository;
            for (var level = 0; level < ProjectManifest.MaximumDepth; level++)
            {
                if (string.Equals(RealFolder(above.ProjectPath), real, StringComparison.OrdinalIgnoreCase))
                    return new(null, "map.loop", $"Its own project map is not read: through a link, its folder is the one of {above.DisplayName}, whose map is read already.");
                if (!claims.TryGetValue(above.ProjectPath, out var up)) break;
                above = up.Repository;
            }

            if (nestedFolders.TryGetValue(real, out var first))
                return new(null, "map.loop", $"Its own project map is not read: through a link, its folder is `{first}`, whose map is read already.");
            nestedFolders[real] = folder;
            return null;
        }
    }

    // Where a folder of a nested map, or of a repository above one, is on the disk (ProjectManifest.RealFolder), looked
    // at again after FolderRecheckMilliseconds as the folders of a map are: a load walks no path each time.
    private string RealFolder(string folder)
    {
        var now = Environment.TickCount64;
        lock (_foldersGate)
        {
            if (_realFolders.TryGetValue(folder, out var kept) && now - kept.CheckedAt <= FolderRecheckMilliseconds) return kept.Real;
        }

        var real = ProjectManifest.RealFolder(folder);
        lock (_foldersGate)
        {
            if (_realFolders.Count >= 256 && !_realFolders.ContainsKey(folder)) _realFolders.Clear();
            _realFolders[folder] = (real, now);
        }

        return real;
    }

    // How the file of a project added by hand names the entry of a map it was recognized as.
    private static string BindingOf(ProjectDescriptor repository, string key) => $"{repository.Id}/{key}";

    // Writes in the file of a project added by hand the entry of the map it is, so that a move of its folder keeps it;
    // only the `declared_as` entry of its front matter, every other byte kept. A sub-project the map made needs none.
    private async Task WriteBindingAsync(ProjectDescriptor project, CancellationToken cancellationToken)
    {
        if (IsReadOnly || project is not { IsSubProject: true, SourcePath: { } sourcePath, ParentId: { } parentId, DeclaredKey: { } key }) return;
        var binding = $"{parentId}/{key}";
        if (string.Equals(project.Id, DeriveSubProjectId(parentId, key), StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.DeclaredAs, binding, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var snapshot = await _projectNameFiles.LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (_serializer.DeserializeProjectMarkdown(snapshot.Text).Id != project.Id) return;
            if (ReplaceEntry(snapshot.Text, "declared_as", $"declared_as: \"{binding}\"") is not { } updated) return;
            if (_serializer.DeserializeProjectMarkdown(updated).DeclaredAs != binding) return;
            var result = await SaveSourceAsync(new TextFileSaveRequest(sourcePath, updated, snapshot.Encoding, snapshot.HasByteOrderMark, snapshot.Revision),
                cancellationToken).ConfigureAwait(false);
            if (!result.IsConflict) project.DeclaredAs = binding;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or SharpYaml.YamlException)
        {
            // The binding is a help for a later move: a file that cannot take it now gets it at another edit.
        }
    }

    // An optional sub-project the user adds: what its file holds; the next load links it to the folders above it.
    private static ProjectDescriptor DeclaredProject(ProjectDescriptor repository, ProjectManifestEntry entry, string folder, IReadOnlyList<ProjectDescriptor> projects)
    {
        var project = DeclaredProject(repository, entry, folder, projects.Select(static project => project.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase));
        Link(project, repository, entry, new Dictionary<string, MapClaim>());
        return project;
    }

    private static ProjectDescriptor DeclaredProject(ProjectDescriptor repository, ProjectManifestEntry entry, string folder, HashSet<string> slugs)
    {
        // The key stays whole and room is kept for a number after it: a slug is 64 characters at most.
        var repositoryPart = repository.Slug.Length + entry.Key.Length + 1 <= 58 ? repository.Slug
            : repository.Slug[..Math.Max(1, 58 - entry.Key.Length - 1)].TrimEnd('-', '_', '.');
        var baseSlug = Slugify($"{repositoryPart}-{entry.Key}");
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
    // The repository can be a sub-project itself (a nested map): it was linked first, and what it inherits follows.
    private static void Link(ProjectDescriptor project, ProjectDescriptor repository, ProjectManifestEntry entry, IReadOnlyDictionary<string, MapClaim> claims)
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
        project.InheritedSandbox = repository.EffectiveSandbox;
        project.InheritedSkillFolders = [.. repository.EffectiveSkillFolders];
        project.RepositoryName = repository.DisplayName;
        project.RepositorySlug = repository.Slug;
        project.RepositoryPath = repository.ProjectPath;
        project.OuterRepositories = repository is { ParentId: { } outerId, RepositoryPath: { } outerPath }
            ? [new(outerId, repository.RepositorySlug ?? outerId, repository.RepositoryName ?? Path.GetFileName(outerPath), outerPath), .. repository.OuterRepositories]
            : [];
        // The declared folders of the same repository that hold this one, the nearest first, then the repository, then
        // what the repository inherits when it is a sub-project itself.
        var roots = new List<string>();
        for (var folder = Path.GetDirectoryName(project.ProjectPath); folder is not null && folder.Length > repository.ProjectPath.Length;
             folder = Path.GetDirectoryName(folder))
        {
            if (claims.TryGetValue(folder, out var above) && above.Repository.Id == repository.Id) roots.Add(folder);
        }

        roots.Add(repository.ProjectPath);
        if (repository.IsSubProject) roots.AddRange(repository.InheritedRoots);
        project.InheritedRoots = roots;
    }
}
