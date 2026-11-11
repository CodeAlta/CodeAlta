using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The sub-projects of a repository for the page: its project map (<c>.alta/projects.toml</c>) with what each entry is,
/// the folders that could be added, and the changes of the map and of what the user shows. Every change asks every
/// window to read the projects and the labels again.
/// </summary>
[NeoRpcService("subProjects", Version = 1)]
internal sealed class SubProjectsService
{
    private readonly ProjectCatalog? _catalog;
    private readonly DesktopSpaceView? _view;
    private readonly string? _epoch;

    /// <summary>Creates the service of a window whose host keeps no projects: it answers <c>unavailable</c>.</summary>
    public SubProjectsService()
    {
    }

    /// <summary>Creates the service of an owned host.</summary>
    /// <param name="catalog">The projects.</param>
    /// <param name="view">What asks the windows to read the projects again.</param>
    /// <param name="epoch">The host epoch.</param>
    internal SubProjectsService(ProjectCatalog catalog, DesktopSpaceView view, string epoch)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_catalog, _view, _epoch) = (catalog, view, epoch);
    }

    /// <summary>Reads the map of a repository project, with what each of its entries is.</summary>
    [NeoRpcMethod("map")]
    public async Task<SubProjectsMapResponse> MapAsync(SubProjectsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (string.IsNullOrWhiteSpace(request.ProjectId)) return new("not_found", null);
        try
        {
            var map = await _catalog!.ReadRepositoryMapAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
            return map is null ? new("not_found", null) : new("ok", View(map));
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            return new("unreadable", null);
        }
    }

    /// <summary>
    /// Tells what a folder is for the maps: the sub-projects its own map declares, and whether it looks like a monorepo
    /// without a map. Read when a folder is added as a project.
    /// </summary>
    [NeoRpcMethod("inspect")]
    public async Task<SubProjectsInspectResponse> InspectAsync(SubProjectsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, 0, 0, false, null, []);
        if (string.IsNullOrWhiteSpace(request.ProjectId)) return new("not_found", 0, 0, false, null, []);
        try
        {
            var map = await _catalog!.ReadRepositoryMapAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
            if (map is null) return new("not_found", 0, 0, false, null, []);
            var declared = map.Entries.Count(static entry => entry.Folder == ProjectManifestFolderState.Ok);
            var optional = map.Entries.Count(static entry => entry.Entry.Optional && entry.Project is null);
            if (map.Exists || map.ClaimedBy is not null) return new("ok", declared, optional, false, map.ClaimedBy, []);
            // Without a map: a quick look at the root and two levels below, for what says it is a monorepo.
            var scan = await Task.Run(() => ProjectManifestScanner.Scan(map.Repository.ProjectPath, map.Manifest, [], [], 2, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return new("ok", 0, 0, scan.LooksLikeMonorepo, null, [.. scan.WorkspaceFiles.Select(static file => file.Kind).Distinct().Take(12)]);
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            return new("unreadable", 0, 0, false, null, []);
        }
    }

    /// <summary>Lists the folders of the repository that could be added to its map. Writes nothing.</summary>
    [NeoRpcMethod("scan")]
    public async Task<SubProjectsScanResponse> ScanAsync(SubProjectsScanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, [], false, null);
        try
        {
            var projects = await _catalog!.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (projects.FirstOrDefault(project => project.Id == request.ProjectId) is not { IsSubProject: false } repository) return new("not_found", [], false, null);
            var map = await _catalog.ReadManifestAsync(repository.ProjectPath, cancellationToken).ConfigureAwait(false);
            var globs = request.Glob is { Length: > 0 and <= 256 } glob ? new[] { glob } : [];
            var scan = await Task.Run(() => ProjectManifestScanner.Scan(repository.ProjectPath, map, globs, [], 3, cancellationToken), cancellationToken).ConfigureAwait(false);
            return new("ok", [.. scan.Candidates.Take(500).Select(static candidate => new SubProjectCandidateView(candidate.Path, candidate.Key, [.. candidate.Markers],
                candidate.Submodule, candidate.Declared))], scan.Truncated || scan.Candidates.Count > 500, null);
        }
        catch (ArgumentException exception)
        {
            return new("invalid", [], false, exception.Message);
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            return new("unreadable", [], false, null);
        }
    }

    /// <summary>Shows or hides a sub-project for the user, or adds an optional one. The map is not changed.</summary>
    [NeoRpcMethod("show")]
    public async Task<SubProjectsChangeResponse> ShowAsync(SubProjectsShowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (string.IsNullOrWhiteSpace(request.ProjectId) || string.IsNullOrWhiteSpace(request.Key)) return new("not_found", null, null);
        if (_catalog!.IsReadOnly) return new("invalid", "The projects are read-only in this process.", null);
        try
        {
            var (status, project) = await _catalog.SetSubProjectShownAsync(request.ProjectId, request.Key, request.Shown, cancellationToken).ConfigureAwait(false);
            if (status == SubProjectShowStatus.Updated) _view!.NotifyChanged();
            return status switch
            {
                SubProjectShowStatus.Updated => new("ok", null, project?.Id),
                SubProjectShowStatus.NotFound => new("not_found", null, null),
                SubProjectShowStatus.FolderUnavailable => new("invalid", "The folder of this sub-project is not there.", null),
                _ => new("conflict", null, null),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null, null);
        }
    }

    /// <summary>Declares a folder of the repository as a sub-project, in its map.</summary>
    [NeoRpcMethod("add")]
    public Task<SubProjectsChangeResponse> AddAsync(SubProjectsEntryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken, root => ProjectManifestEditor.AddProject(root,
            string.IsNullOrWhiteSpace(request.Key) ? null : request.Key.Trim(), Edit(request) with { Path = request.Path ?? string.Empty }));
    }

    /// <summary>Changes an entry of the map; a value left null stays.</summary>
    [NeoRpcMethod("update")]
    public Task<SubProjectsChangeResponse> UpdateAsync(SubProjectsEntryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken, root => ProjectManifestEditor.SetProject(root, request.Key ?? string.Empty, Edit(request)));
    }

    /// <summary>Removes an entry of the map. Its folder and its sessions stay.</summary>
    [NeoRpcMethod("remove")]
    public Task<SubProjectsChangeResponse> RemoveAsync(SubProjectsEntryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken, root => ProjectManifestEditor.RemoveProject(root, request.Key ?? string.Empty));
    }

    /// <summary>Creates, changes or removes a label of the map.</summary>
    [NeoRpcMethod("label")]
    public Task<SubProjectsChangeResponse> LabelAsync(SubProjectsLabelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken, root => request.Remove
            ? ProjectManifestEditor.RemoveLabel(root, request.Key ?? string.Empty)
            : ProjectManifestEditor.SetLabel(root, request.Key ?? string.Empty, new ProjectManifestLabelEdit(request.Name, request.Parent, request.Icon, request.Color, request.Description)));
    }

    private static ProjectManifestEdit Edit(SubProjectsEntryRequest request)
        => new(request.Path, request.Name, request.Description, request.Labels is { } labels ? [.. labels.Take(64)] : null, request.Optional,
            request.Write switch { "folder" => ProjectWriteScope.Folder, "repository" => ProjectWriteScope.Repository, _ => null });

    private async Task<SubProjectsChangeResponse> ChangeAsync(string? expectedEpoch, string? projectId, CancellationToken cancellationToken,
        Func<string, ProjectManifestChange> change)
    {
        if (Refuse(expectedEpoch) is { } refused) return new(refused, null, null);
        if (string.IsNullOrWhiteSpace(projectId)) return new("not_found", null, null);
        try
        {
            var projects = await _catalog!.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (projects.FirstOrDefault(project => project.Id == projectId) is not { IsSubProject: false } repository) return new("not_found", null, null);
            var result = await Task.Run(() => change(repository.ProjectPath), CancellationToken.None).ConfigureAwait(false);
            if (result.Status == ProjectManifestChangeStatus.Updated) _view!.NotifyChanged();
            return result.Status switch
            {
                ProjectManifestChangeStatus.Updated or ProjectManifestChangeStatus.Unchanged => new("ok", null, result.Key),
                ProjectManifestChangeStatus.NotFound => new("not_found", result.Message, null),
                ProjectManifestChangeStatus.Unsupported => new("unsupported", result.Message, null),
                _ => new("invalid", result.Message, null),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null, null);
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            return new("unreadable", null, null);
        }
    }

    private static SubProjectsMapView View(RepositoryMap map)
    {
        var slug = map.Repository.Slug;
        return new(map.Repository.Id, map.Repository.DisplayName, map.Repository.ProjectPath, map.ManifestPath, map.Exists, map.ClaimedBy,
            [.. map.Manifest.Labels.Select(label => new SubProjectLabelView(label.Key, ProjectCatalog.DeclaredLabelId(slug, label.Key), label.Name, label.Parent,
                label.Icon, label.Color, label.Description))],
            [.. map.Entries.Select(static entry => new SubProjectEntryView(entry.Entry.Key, entry.Entry.Path, entry.Entry.Name, entry.Entry.Description, [.. entry.Entry.Labels],
                entry.Entry.Optional, entry.Entry.Write == ProjectWriteScope.Folder ? "folder" : "repository",
                entry.Folder switch { ProjectManifestFolderState.Ok => "ok", ProjectManifestFolderState.Outside => "outside", _ => "missing" },
                entry.Folder == ProjectManifestFolderState.Ok && File.Exists(Path.Combine(entry.FullPath, ".git")),
                entry.Project?.Id, entry.Project?.DisplayName, entry.Project?.Archived == true, entry.ClaimedBy))],
            [.. map.Problems.Take(200).Select(static problem => new SubProjectProblemView(problem.Key, problem.Code, problem.Message))]);
    }

    private string? Refuse(string? expectedEpoch)
        => _catalog is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static bool Unreadable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or SharpYaml.YamlException;
}

/// <summary>Names a repository project.</summary>
internal sealed record SubProjectsRequest(string ExpectedEpoch, string? ProjectId);

/// <summary>Status is <c>ok</c>, <c>not_found</c>, <c>unreadable</c>, <c>unavailable</c> or <c>stale_epoch</c>.</summary>
internal sealed record SubProjectsMapResponse(string Status, SubProjectsMapView? Map);

/// <summary>The map of a repository.</summary>
/// <param name="ProjectId">The repository project.</param>
/// <param name="Name">Its name.</param>
/// <param name="Path">Its folder.</param>
/// <param name="ManifestPath">The file of its map.</param>
/// <param name="Exists">Whether the file exists.</param>
/// <param name="ClaimedBy">The repository whose map declares this project's folder, which makes its own map ignored; null otherwise.</param>
/// <param name="Labels">The labels of the map, in the order of their tree.</param>
/// <param name="Entries">The projects of the map, in the order of the file.</param>
/// <param name="Problems">What was left out, and why.</param>
internal sealed record SubProjectsMapView(string ProjectId, string Name, string Path, string ManifestPath, bool Exists, string? ClaimedBy,
    IReadOnlyList<SubProjectLabelView> Labels, IReadOnlyList<SubProjectEntryView> Entries, IReadOnlyList<SubProjectProblemView> Problems);

/// <summary>A label of a map: its key, the identifier the labels of the window give it, and its look.</summary>
internal sealed record SubProjectLabelView(string Key, string Id, string Name, string? Parent, string? Icon, string? Color, string? Description);

/// <summary>
/// An entry of a map. Folder is <c>ok</c>, <c>missing</c> or <c>outside</c>; ProjectId names the project once it is listed,
/// Hidden says the user hid it; Submodule says its folder is a git submodule.
/// </summary>
internal sealed record SubProjectEntryView(string Key, string Path, string? Name, string? Description, IReadOnlyList<string> Labels, bool Optional, string Write,
    string Folder, bool Submodule, string? ProjectId, string? ProjectName, bool Hidden, string? ClaimedBy);

/// <summary>A problem of a map: the key at fault (null for the file), its code and its message.</summary>
internal sealed record SubProjectProblemView(string? Key, string Code, string Message);

/// <summary>
/// What a folder is for the maps. Declared counts the sub-projects its map declares, Optional those listed only for
/// those who add them; Monorepo says that a folder without a map looks like one, from its Workspaces (<c>pnpm</c>,
/// <c>cargo</c>, <c>git-submodules</c>).
/// </summary>
internal sealed record SubProjectsInspectResponse(string Status, int Declared, int Optional, bool Monorepo, string? ClaimedBy, IReadOnlyList<string> Workspaces);

/// <summary>Asks for the folders that could be added, matching a pattern (<c>services/*</c>) or every folder three levels down.</summary>
internal sealed record SubProjectsScanRequest(string ExpectedEpoch, string? ProjectId, string? Glob);

/// <summary>Status is <c>ok</c>, <c>invalid</c> (a pattern, with a message), <c>not_found</c>, <c>unreadable</c>, <c>unavailable</c> or <c>stale_epoch</c>.</summary>
internal sealed record SubProjectsScanResponse(string Status, IReadOnlyList<SubProjectCandidateView> Candidates, bool Truncated, string? Message);

/// <summary>A folder that could be a sub-project: its path, its key, what marks it, whether it is a submodule and the key that declares it already.</summary>
internal sealed record SubProjectCandidateView(string Path, string Key, IReadOnlyList<string> Markers, bool Submodule, string? Declared);

/// <summary>Shows or hides a sub-project of a repository.</summary>
internal sealed record SubProjectsShowRequest(string ExpectedEpoch, string? ProjectId, string? Key, bool Shown);

/// <summary>An entry to add or change: what is null stays; an empty name or description removes it.</summary>
internal sealed record SubProjectsEntryRequest(string ExpectedEpoch, string? ProjectId, string? Key, string? Path, string? Name, string? Description,
    IReadOnlyList<string>? Labels, bool? Optional, string? Write);

/// <summary>A label of the map to create, change or remove.</summary>
internal sealed record SubProjectsLabelRequest(string ExpectedEpoch, string? ProjectId, string? Key, string? Name, string? Parent, string? Icon, string? Color,
    string? Description, bool Remove);

/// <summary>
/// Status is <c>ok</c>, <c>invalid</c> (with a message), <c>not_found</c>, <c>unsupported</c> (the map is written in a form
/// that is not edited here), <c>conflict</c>, <c>write_failed</c>, <c>unreadable</c>, <c>unavailable</c> or <c>stale_epoch</c>.
/// </summary>
/// <param name="Status">How the change ended.</param>
/// <param name="Message">What was wrong, for a change that was not made.</param>
/// <param name="Key">The key of the entry or of the label, or the project shown.</param>
internal sealed record SubProjectsChangeResponse(string Status, string? Message, string? Key);
