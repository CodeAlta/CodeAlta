using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Lists the skills discovered for the global scope and an optional project, saves their name-based
/// enablement in the global or project configuration, and scaffolds new skills: the operations of the
/// terminal's skills manager. A skill is also named by the id of its folder, which the code editor opens on, and
/// a skill of the user or of a project is removed: its folder is moved to the trash of the system.
/// </summary>
[NeoRpcService("skills", Version = 1)]
internal sealed partial class SkillsService
{
    /// <summary>
    /// Largest number of skills returned by one listing or named by one bulk request. A profile with more skills is
    /// listed up to it, with the number left out; the switches of a filter act on every match (<c>setMatchingEnabled</c>).
    /// </summary>
    internal const int MaximumSkills = 4096;

    private const int MaximumNameLength = 128;
    private const int MaximumDescriptionLength = 1024;
    private const int MaximumMessageLength = 512;
    private const int MaximumPathLength = 1024;
    private const int MaximumRelatedFiles = 64;
    private const int MaximumDiagnostics = 32;
    private const int MaximumSkillFileBytes = 256 * 1024;

    /// <summary>Largest number of characters of a <c>SKILL.md</c> returned by <c>detail</c>.</summary>
    internal const int MaximumContentLength = 64 * 1024;
    private readonly ProjectCatalog? _projects;
    private readonly SkillManagementService? _management;
    private readonly SkillFolders? _folders;
    private readonly IDesktopFileTrash _trash;
    private readonly string? _epoch;
    private readonly string? _home;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal SkillsService()
    {
        _trash = new DesktopFileTrash();
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="projects">The host's project catalog; its global root holds the user skills and configuration.</param>
    /// <param name="catalog">The host's skill catalog, including its built-in and plugin root providers.</param>
    /// <param name="userProfileRoot">The profile holding the common <c>.agents/skills</c> root, or null to omit it.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="trash">Where the folder of a removed skill goes; the trash of the system by default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> or <paramref name="catalog"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal SkillsService(ProjectCatalog projects, SkillCatalog catalog, string? userProfileRoot, string epoch, IDesktopFileTrash? trash = null)
    {
        _trash = trash ?? new DesktopFileTrash();
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _projects = projects;
        // A platform without a profile folder reports it as empty: that omits the common root, it is not an error.
        _home = string.IsNullOrWhiteSpace(userProfileRoot) ? null : userProfileRoot;
        _management = new SkillManagementService(catalog, projects.Options.GlobalRoot, _home);
        _folders = new SkillFolders(projects, _management) { Contexts = AllFolderContextsAsync };
        _epoch = epoch;
    }

    /// <summary>
    /// Gets the spaces of the host, whose files hold the folders of skills added for a space; null where the host keeps
    /// none, where no folder is added for a space.
    /// </summary>
    internal SpaceCatalog? Spaces { get; init; }

    /// <summary>Finds the folders of the skills this service lists; null without an owned host.</summary>
    internal SkillFolders? Folders => _folders;

    /// <summary>The folders the skills of the user and of a project are read from; none without an owned host.</summary>
    /// <param name="projectRoot">The folder of the project whose skill folders are listed too, or null.</param>
    internal IReadOnlyList<SkillRootLocation> Roots(string? projectRoot) => _management?.GetRoots(projectRoot) ?? [];

    /// <summary>Lists every discovered skill, including disabled, invalid and shadowed ones.</summary>
    [NeoRpcMethod("list")]
    public async Task<SkillsListResponse> ListAsync(SkillsListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SkillsListResponse Failed(string status) => new(status, request.ProjectId, [], 0);
        if (_projects is null || _management is null) return Failed("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failed("stale_epoch");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return Failed(project.Status);
        try
        {
            // A configuration file that cannot be read hides nothing: the skills are listed as if it disabled none,
            // and the file is named above the list.
            var context = await FolderContextAsync(request.ProjectId, project.Root, request.SpaceId, cancellationToken).ConfigureAwait(false);
            var listing = await _management.LoadListingAsync(SkillListingScope.Combined, project.Root, context, cancellationToken).ConfigureAwait(false);
            var descriptors = listing.Skills;
            var skills = descriptors.Take(MaximumSkills).Select(skill => new SkillsEntry(
                IdOf(skill), Bound(skill.Name, MaximumNameLength), Bound(skill.Title, MaximumNameLength), Bound(skill.Description, MaximumDescriptionLength),
                skill.SourceKind.ToString(), skill.Scope.ToString(), !skill.IsDisabledGlobally, !skill.IsDisabledForProject,
                skill.IsEnabled, skill.IsValid, skill.IsShadowed, skill.IsTrusted,
                // The row opens the folder of its skill in the code editor: the id the editor names it by, and its path.
                EditorFolderOf(skill, descriptors, request.ProjectId, project.Root, context.SpaceId), Bound(skill.SkillRootPath, MaximumPathLength))).ToArray();
            return new("ok", request.ProjectId, skills, descriptors.Count - skills.Length)
            {
                Problems = [.. listing.Problems.Select(static problem => new PluginsProblem("config", Bound(problem.Path, MaximumPathLength), Clean(problem.Message), problem.IsProject ? "Project" : "Global"))],
                Folders = FoldersOf(context, project.Root is not null, descriptors),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log(exception, "The skills could not be listed.");
            return Failed("read_failed");
        }
    }

    /// <summary>
    /// Describes one listed skill: where it lives, why it is or is not offered to the model, its validation
    /// diagnostics, its related files, the text of its <c>SKILL.md</c>, and the id of its folder for the code editor.
    /// </summary>
    [NeoRpcMethod("detail")]
    public async Task<SkillsDetailResponse> DetailAsync(SkillsDetailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SkillsDetailResponse Failed(string status) => new(status, request.Name, request.Source, null, null, null, null, false, null, null, null, null, false, [], [], 0);
        if (_projects is null || _management is null) return Failed("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failed("stale_epoch");
        if (!ValidIdentity(request, out var source)) return Failed("invalid");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return Failed(project.Status);
        try
        {
            var context = await FolderContextAsync(request.ProjectId, project.Root, request.SpaceId, cancellationToken).ConfigureAwait(false);
            var descriptors = (await _management.LoadListingAsync(SkillListingScope.Combined, project.Root, context, cancellationToken).ConfigureAwait(false)).Skills;
            var (status, skill) = FindSkill(descriptors, request, source);
            if (skill is null) return Failed(status);
            // A skill reached through a link (a library shared with other tools) is shown, read where the link leads; the
            // document lookup of the editor refuses it, so it is not given to the code editor: it is edited in its own folder.
            var linked = false;
            string path;
            try
            {
                path = (await _management.GetFileDocumentAsync(skill.SkillFilePath, null, project.Root, context, cancellationToken).ConfigureAwait(false)).FullPath;
            }
            catch (IOException)
            {
                (linked, path) = (true, skill.SkillFilePath);
            }

            string? content = null;
            var truncated = false;
            var file = new FileInfo(path);
            if (file.LinkTarget is not null && file.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target) file = target;
            if (file.Exists && file.Length <= MaximumSkillFileBytes)
            {
                content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                truncated = content.Length > MaximumContentLength;
                if (truncated) content = content[..(char.IsHighSurrogate(content[MaximumContentLength - 1]) ? MaximumContentLength - 1 : MaximumContentLength)];
            }

            var related = _management.ListRelatedFiles(skill, cancellationToken);
            return new("ok", skill.Name, skill.SourceKind.ToString(), Bound(skill.SkillFilePath, MaximumPathLength), Bound(skill.SkillRootPath, MaximumPathLength),
                Bound(skill.SourceId, MaximumNameLength), skill.ShadowedBySkillFilePath is { } shadow ? Bound(shadow, MaximumPathLength) : null, skill.IsModelVisible,
                Optional(skill.Frontmatter.License), Optional(skill.Frontmatter.Compatibility), Optional(skill.Frontmatter.AllowedTools),
                content, truncated || content is null && file.Exists,
                [.. related.Take(MaximumRelatedFiles).Select(static item => new SkillsRelatedFile(Bound(item.Category, 64), Bound(item.RelativePath, 512)))],
                [.. skill.Diagnostics.Take(MaximumDiagnostics).Select(static item => new SkillsDiagnostic(item.Severity.ToString(), Bound(item.Code, 64), Bound(item.Message, MaximumMessageLength)))],
                Math.Max(0, related.Count - MaximumRelatedFiles), linked ? null : EditorFolderOf(skill, descriptors, request.ProjectId, project.Root, context.SpaceId)) { Linked = linked };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log(exception, "The details of a skill could not be read.");
            return Failed("read_failed");
        }
    }

    /// <summary>Enables or disables one skill by name in the global or project configuration.</summary>
    [NeoRpcMethod("setEnabled")]
    public Task<SkillsMutationResponse> SetEnabledAsync(SkillsSetEnabledRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SetAsync(request.ExpectedEpoch, request.ProjectId, request.Scope, request.Name is null ? null : [request.Name], request.Enabled, cancellationToken);
    }

    /// <summary>Enables or disables the listed skills in one configuration write; no name is changed when one is invalid.</summary>
    [NeoRpcMethod("setAllEnabled")]
    public Task<SkillsMutationResponse> SetAllEnabledAsync(SkillsSetAllEnabledRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SetAsync(request.ExpectedEpoch, request.ProjectId, request.Scope, request.Names, request.Enabled, cancellationToken);
    }

    /// <summary>
    /// Enables or disables, in one configuration write, every skill whose name, title or description holds the text
    /// of the query, in any case, as the filter of the page shows them: all the skills when the query is empty. The
    /// skills are those the host lists now, whatever their number, so a bulk switch is never cut at the listing limit.
    /// A skill whose name cannot be written to a configuration is left out, as the page leaves out its switch.
    /// </summary>
    [NeoRpcMethod("setMatchingEnabled")]
    public async Task<SkillsMutationResponse> SetMatchingEnabledAsync(SkillsSetMatchingEnabledRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null || _management is null) return new("unavailable", 0, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", 0, null);
        if (!TryScope(request.Scope, out var projectScope)) return new("invalid", 0, ScopeRule);
        if (request.Query is { Length: > MaximumNameLength }) return new("invalid", 0, "The filter is too long.");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, 0, null);
        if (projectScope && project.Root is null) return new("invalid", 0, ProjectRequired);
        string[] names;
        try
        {
            var context = await FolderContextAsync(request.ProjectId, project.Root, request.SpaceId, cancellationToken).ConfigureAwait(false);
            var descriptors = (await _management.LoadListingAsync(SkillListingScope.Combined, project.Root, context, cancellationToken).ConfigureAwait(false)).Skills;
            var query = request.Query?.Trim() ?? string.Empty;
            names = [.. descriptors.Where(skill => MatchesFilter(skill, query) && Toggleable(skill.Name)).Select(static skill => skill.Name).Distinct(StringComparer.Ordinal)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log(exception, "The skills could not be listed.");
            return new("read_failed", 0, null);
        }

        // Nothing matches: nothing to write, and no configuration file is touched.
        return names.Length == 0 ? new("ok", 0, null) : await SetAsync(request.ExpectedEpoch, request.ProjectId, request.Scope, names, request.Enabled, cancellationToken, maximum: int.MaxValue).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new skill folder with a <c>SKILL.md</c> scaffold in the global or project skills root, and names
    /// that folder for the code editor.
    /// </summary>
    [NeoRpcMethod("create")]
    public async Task<SkillsCreateResponse> CreateAsync(SkillsCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null || _management is null) return new("unavailable", null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, null);
        if (!TryScope(request.Scope, out var projectScope)) return new("invalid", null, ScopeRule);
        if (request.Name is not { Length: <= MaximumNameLength } || request.Description is not { Length: <= 4 * MaximumDescriptionLength })
            return new("invalid", null, "A skill name and a description are required.");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, null, null);
        if (projectScope && project.Root is null) return new("invalid", null, ProjectRequired);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var created = await _management.CreateSkillAsync(projectScope ? SkillCreationTargetKind.ProjectCodeAlta : SkillCreationTargetKind.UserCodeAlta,
                projectScope ? project.Root : null, request.Name, request.Description, CancellationToken.None).ConfigureAwait(false);
            var folder = new SkillFolder(projectScope ? request.ProjectId : null, projectScope ? SkillSourceKind.ProjectAlta : SkillSourceKind.UserAlta, created.Name);
            return new("ok", created.Name, null, folder.Id, Path.GetDirectoryName(created.SkillFilePath));
        }
        catch (ArgumentException exception)
        {
            return new("invalid", null, Reason(exception));
        }
        catch (InvalidOperationException)
        {
            return new("conflict", null, null); // The skill folder already exists; the message would name its path.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new("write_failed", null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes a skill of the user or of a project: its folder is moved to the trash of the system. Only a skill
    /// of a CodeAlta folder or of the common folder (<c>UserAlta</c>, <c>ProjectAlta</c>, <c>UserCommon</c>,
    /// <c>ProjectCommon</c>) is removed; any other source answers <c>read_only</c>. The folder must be the one
    /// directly inside the folder its source reads: nothing else is ever moved.
    /// </summary>
    [NeoRpcMethod("delete", TimeoutMilliseconds = 120_000)]
    public async Task<SkillsMutationResponse> DeleteAsync(SkillsDetailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null || _management is null) return new("unavailable", 0, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", 0, null);
        if (!ValidIdentity(request, out var source)) return new("invalid", 0, null);
        if (source is not (SkillSourceKind.UserAlta or SkillSourceKind.ProjectAlta or SkillSourceKind.UserCommon or SkillSourceKind.ProjectCommon)) return new("read_only", 0, null);
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, 0, null);
        if (!_trash.Available) return new("trash_unavailable", 0, null);
        string folder;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Removing a skill does not depend on what a configuration file says of it.
            var descriptors = (await _management.LoadListingAsync(SkillListingScope.Combined, project.Root, cancellationToken).ConfigureAwait(false)).Skills;
            var (status, skill) = FindSkill(descriptors, request, source);
            if (skill is null) return new(status, 0, null);
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(skill.SkillRootPath));
            // The folder of one skill, directly in the folder of its source: never that folder itself, nor one above it.
            var root = _management.GetRoots(project.Root).FirstOrDefault(candidate => candidate.Source == source)?.RootPath;
            if (root is null || !PathComparer.Equals(Path.GetDirectoryName(folder), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
                || !File.Exists(Path.Combine(folder, "SKILL.md")))
            {
                return new("read_only", 0, null);
            }

            // As for the details, the document lookup refuses a skill reached through a link: discovery follows links,
            // management does not act through them.
            _ = await _management.GetFileDocumentAsync(skill.SkillFilePath, null, project.Root, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return new("config_invalid", 0, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new("read_failed", 0, null);
        }
        finally
        {
            _gate.Release();
        }

        // Not under the gate: the system may ask a question before it moves something.
        return await _trash.MoveAsync(folder, CancellationToken.None).ConfigureAwait(false) ? new("ok", 1, null) : new("trash_failed", 0, null);
    }

    /// <summary>
    /// Adds a folder of skills for every space (<c>Global</c>), for a space (<c>Space</c>, the one the request names, the
    /// default space without one) or for the project of the request (<c>Project</c>): its skills are read where they are.
    /// The folder must exist; a disk, the home folder or the folder of CodeAlta is refused (<c>folder_refused</c>, with
    /// the reason as its message), and so is a folder the list has already (<c>folder_listed</c>).
    /// </summary>
    [NeoRpcMethod("addFolder")]
    public Task<SkillsMutationResponse> AddFolderAsync(SkillsFolderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeFolderAsync(request, add: true, cancellationToken);
    }

    /// <summary>
    /// Removes a folder of skills from the list of its scope, as <c>addFolder</c> names it. Nothing of the folder is
    /// changed on the disk: its skills are no longer read, and stay where they are.
    /// </summary>
    [NeoRpcMethod("removeFolder")]
    public Task<SkillsMutationResponse> RemoveFolderAsync(SkillsFolderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeFolderAsync(request, add: false, cancellationToken);
    }

    private async Task<SkillsMutationResponse> ChangeFolderAsync(SkillsFolderRequest request, bool add, CancellationToken cancellationToken)
    {
        if (_projects is null || _management is null) return new("unavailable", 0, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", 0, null);
        var scope = request.Scope;
        if (scope is not ("Global" or "Space" or "Project")) return new("invalid", 0, "The scope must be Global, Space or Project.");
        if (request.Path is not { Length: > 0 and <= AddedSkillFolders.MaximumPathLength } path || path.Any(char.IsControl))
            return new("invalid", 0, "The path of a folder is required.");
        if (scope == "Space" && (Spaces is null || request.SpaceId is { Length: > 128 })) return new(Spaces is null ? "unavailable" : "invalid", 0, null);
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, 0, null);
        if (scope == "Project" && (project.Root is null || request.ProjectId is null)) return new("invalid", 0, ProjectRequired);
        var globalRoot = _projects.Options.GlobalRoot;
        if (add)
        {
            var problem = AddedSkillFolders.Check(path, _home, globalRoot);
            if (problem != AddedSkillFolderProblem.None) return new("folder_refused", 0, problem.ToString());
            if (!Directory.Exists(AddedSkillFolders.Resolve(path, _home))) return new("folder_missing", 0, null);
        }

        // The answer when the list does not change: a folder added twice, or one removed that is not there.
        string? unchanged = null;
        IReadOnlyList<string> Changed(IReadOnlyList<string> current)
        {
            var next = AddedSkillFolders.Change(current, path, add, _home);
            if (add && next.Count == current.Count) unchanged = "folder_listed";
            else if (add && next.Count > AddedSkillFolders.MaximumFolders) unchanged = "folder_limit";
            else if (!add && next.Count == current.Count) unchanged = "not_found";
            return unchanged is null ? next : current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (scope)
            {
                case "Global":
                {
                    // One read and write under the lock of the file: another window that adds a folder meanwhile keeps it.
                    _ = new CodeAltaConfigStore(_projects.Options).UpdateGlobalSkillFolders(current =>
                    {
                        var next = Changed(current);
                        return unchanged is null ? next : null;
                    });
                    return unchanged is not null ? new(unchanged, 0, null) : new("ok", 1, null);
                }

                case "Space":
                {
                    var spaceId = string.IsNullOrWhiteSpace(request.SpaceId) ? SpaceDescriptor.DefaultId : request.SpaceId.Trim();
                    var change = await Spaces!.UpdateSettingsAsync(spaceId, settings =>
                    {
                        var next = Changed(settings.Skills?.Folders ?? []);
                        return unchanged is not null ? settings : settings with { Skills = next.Count == 0 ? null : new SpaceSkills(next) };
                    }, cancellationToken).ConfigureAwait(false);
                    if (unchanged is not null) return new(unchanged, 0, null);
                    return change.Status switch
                    {
                        SpaceChangeStatus.Ok => new("ok", 1, null),
                        SpaceChangeStatus.NotFound => new("not_found", 0, null),
                        SpaceChangeStatus.Conflict => new("conflict", 0, null),
                        _ => new("invalid", 0, change.Message),
                    };
                }

                default:
                {
                    var status = await _projects.UpdateSkillFoldersAsync(request.ProjectId!, Changed, cancellationToken).ConfigureAwait(false);
                    if (unchanged is not null) return new(unchanged, 0, null);
                    return status switch
                    {
                        ProjectSpacesStatus.Updated => new("ok", 1, null),
                        ProjectSpacesStatus.Unchanged => new("ok", 0, null),
                        ProjectSpacesStatus.NotFound => new("not_found", 0, null),
                        ProjectSpacesStatus.Conflict => new("conflict", 0, null),
                        _ => new("write_failed", 0, null),
                    };
                }
            }
        }
        catch (InvalidDataException)
        {
            return new("config_invalid", 0, null); // The configuration file does not parse and is left as it is.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log(exception, "A folder of skills could not be saved.");
            return new("write_failed", 0, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The folders added for the space the page shows and for its project. A space that is not known adds none.
    private async Task<SkillFolderContext> FolderContextAsync(string? projectId, string? projectRoot, string? spaceId, CancellationToken cancellationToken)
    {
        var spaceKey = string.IsNullOrWhiteSpace(spaceId) ? SpaceDescriptor.DefaultId : spaceId.Trim();
        var project = projectId is null || projectRoot is null || _projects is null ? null : await _projects.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        var space = Spaces is null ? null
            : (await Spaces.LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => string.Equals(candidate.Id, spaceKey, StringComparison.Ordinal));
        return new SkillFolderContext(spaceKey, space?.Settings.Skills?.Folders ?? [], project?.SkillFolders ?? []);
    }

    // The contexts of every space, for a folder of the code editor, whose id does not name the space of its skill.
    private async Task<IReadOnlyList<SkillFolderContext>> AllFolderContextsAsync(string? projectId, string? projectRoot, CancellationToken cancellationToken)
    {
        var project = projectId is null || projectRoot is null || _projects is null ? null : await _projects.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SpaceDescriptor> spaces = Spaces is null ? [] : await Spaces.LoadAsync(cancellationToken).ConfigureAwait(false);
        return spaces.Count == 0
            ? [new SkillFolderContext(null, [], project?.SkillFolders ?? [])]
            : [.. spaces.Select(space => new SkillFolderContext(space.Id, space.Settings.Skills?.Folders ?? [], project?.SkillFolders ?? []))];
    }

    // The folders added for every space, for the space and for the project, with what each is on the disk and how many
    // skills it brings: most specific first, as they are read.
    private IReadOnlyList<SkillsFolder> FoldersOf(SkillFolderContext context, bool hasProject, IReadOnlyList<SkillDescriptor> skills)
    {
        IReadOnlyList<string> global;
        try { global = new CodeAltaConfigStore(_projects!.Options).LoadGlobalSkillFolders(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException) { global = []; }
        var globalRoot = _projects!.Options.GlobalRoot;
        var folders = new List<SkillsFolder>();
        void Add(string scope, IEnumerable<string> written, SkillSourceKind kind)
        {
            foreach (var path in written.Take(AddedSkillFolders.MaximumFolders))
            {
                var (state, full) = AddedSkillFolders.Inspect(path, _home, globalRoot);
                var sourceId = full is null ? null : AddedSkillFolders.SourceIdOf(kind, context.SpaceId, full);
                var count = sourceId is null ? 0 : skills.Count(skill => skill.SourceKind == kind && AddedSkillFolders.PathComparer.Equals(skill.SourceId, sourceId));
                folders.Add(new SkillsFolder(scope, Bound(path, MaximumPathLength), full is null ? null : Bound(full, MaximumPathLength),
                    state.ToString().ToLowerInvariant(), count));
            }
        }

        if (hasProject) Add("Project", context.ProjectFolders, SkillSourceKind.ProjectFolder);
        Add("Space", context.SpaceFolders, SkillSourceKind.SpaceFolder);
        Add("Global", global, SkillSourceKind.UserFolder);
        return folders;
    }

    private async Task<SkillsMutationResponse> SetAsync(string? expectedEpoch, string? projectId, string? scope, IReadOnlyList<string>? names,
        bool enabled, CancellationToken cancellationToken, int maximum = MaximumSkills)
    {
        if (_projects is null || _management is null) return new("unavailable", 0, null);
        if (!string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", 0, null);
        if (!TryScope(scope, out var projectScope)) return new("invalid", 0, ScopeRule);
        if (names is null || names.Count == 0 || names.Count > maximum || names.Any(static name => name is not { Length: > 0 and <= MaximumNameLength }))
            return new("invalid", 0, $"Between 1 and {MaximumSkills.ToString(System.Globalization.CultureInfo.InvariantCulture)} skill names are required.");
        var project = await SettingsProjectScope.ResolveAsync(_projects, projectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, 0, null);
        if (projectScope && project.Root is null) return new("invalid", 0, ProjectRequired);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = _management.SetSkillsEnabled(projectScope ? SkillEnablementScope.Project : SkillEnablementScope.Global,
                projectScope ? project.Root : null, names, enabled);
            return new("ok", result.TotalChanged, null);
        }
        catch (ArgumentException exception)
        {
            return new("invalid", 0, Reason(exception));
        }
        catch (InvalidDataException)
        {
            return new("config_invalid", 0, null); // The configuration file does not parse and is left as it is.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new("write_failed", 0, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The filter of the page: the text in the name, the title or the description as the listing sends them, in any case.
    private static bool MatchesFilter(SkillDescriptor skill, string query)
        => query.Length == 0 || $"{Bound(skill.Name, MaximumNameLength)} {Bound(skill.Title, MaximumNameLength)} {Bound(skill.Description, MaximumDescriptionLength)}"
            .Contains(query, StringComparison.OrdinalIgnoreCase);

    // A name a configuration can disable, as the switch of the page accepts it (`skillToggleable`).
    private static bool Toggleable(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        return normalized.Length is > 0 and <= 64 && ToggleableName().IsMatch(normalized);
    }

    [GeneratedRegex(@"^[\p{L}\p{Nd}]+(?:-[\p{L}\p{Nd}]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ToggleableName();

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Identity belongs to the listed file, never to its YAML name (which may be empty or shared). An id is only
    // compared with a fresh, project-scoped listing; it is never decoded into a client-supplied filesystem path.
    private static string IdOf(SkillDescriptor skill)
    {
        var path = Path.GetFullPath(skill.SkillFilePath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{skill.SourceKind}\n{path}")));
    }

    private static bool ValidIdentity(SkillsDetailRequest request, out SkillSourceKind source)
    {
        source = default;
        // The name of a source as the listing gives it: a number is not one.
        return request.Source is { Length: > 0 and <= 64 } && request.Source.All(char.IsAsciiLetter)
            && Enum.TryParse(request.Source, ignoreCase: false, out source) && Enum.IsDefined(source)
            && (request.Id is not null ? request.Id.Length == 64 && request.Id.All(char.IsAsciiHexDigit)
                : request.Name is { Length: > 0 and <= MaximumNameLength });
    }

    private static (string Status, SkillDescriptor? Skill) FindSkill(IReadOnlyList<SkillDescriptor> descriptors, SkillsDetailRequest request, SkillSourceKind source)
    {
        SkillDescriptor? found = null;
        foreach (var skill in descriptors)
        {
            if (skill.SourceKind != source || !(request.Id is not null
                ? string.Equals(IdOf(skill), request.Id, StringComparison.Ordinal)
                : string.Equals(skill.Name, request.Name, StringComparison.Ordinal))) continue;
            // Old clients can still name an unambiguous skill. They must never select the first of duplicates,
            // and an unknown id must never fall back to a name, even when that name is present in the listing.
            if (found is not null) return ("invalid", null);
            found = skill;
        }
        return found is null ? ("not_found", null) : ("ok", found);
    }

    private static string? EditorFolderOf(SkillDescriptor skill, IReadOnlyList<SkillDescriptor> descriptors, string? projectId, string? projectRoot, string? spaceId)
        // Editor folder handles are still name-based: do not offer a handle that could open another row's files.
        => descriptors.Count(candidate => candidate.SourceKind == skill.SourceKind && string.Equals(candidate.Name, skill.Name, StringComparison.Ordinal)) == 1
            ? SkillFolders.IdOf(skill, projectId, projectRoot, spaceId) : null;

    // What a parser or the system said, on one line.
    private static string Clean(string message)
        => Bound(new string([.. message.Where(static character => !char.IsControl(character))]).Trim(), MaximumMessageLength);

    // The exception only: its message may name a path, and nothing of a file is written.
    private static void Log(Exception exception, string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Error(exception, message);
    }
    private const string ScopeRule = "The scope must be Global or Project.";
    private const string ProjectRequired = "The project scope requires a project.";

    private static bool TryScope(string? value, out bool project)
    {
        project = string.Equals(value, "Project", StringComparison.OrdinalIgnoreCase);
        return project || string.Equals(value, "Global", StringComparison.OrdinalIgnoreCase);
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : Bound(value.Trim(), MaximumMessageLength);

    // The validation sentence without the runtime's parameter-name suffix.
    private static string Reason(ArgumentException exception)
    {
        var suffix = exception.Message.IndexOf(" (Parameter '", StringComparison.Ordinal);
        return Bound(suffix > 0 ? exception.Message[..suffix] : exception.Message, MaximumMessageLength);
    }
}

/// <param name="SpaceId">The space the page shows, whose added folders are read too; null for the default space.</param>
internal sealed record SkillsListRequest(string? ExpectedEpoch, string? ProjectId, string? SpaceId = null);
internal sealed record SkillsListResponse(string Status, string? ProjectId, IReadOnlyList<SkillsEntry> Skills, int Omitted)
{
    /// <summary>
    /// The configuration files that could not be read or parsed (kind <c>config</c>, with the scope of the file): the
    /// skills are listed as if such a file disabled none, so a skill it disables is shown as enabled.
    /// </summary>
    public IReadOnlyList<PluginsProblem> Problems { get; init; } = [];

    /// <summary>
    /// The folders of skills the user added that the listing read: those of the project, of the space, then of every
    /// space, each in the order of its list.
    /// </summary>
    public IReadOnlyList<SkillsFolder> Folders { get; init; } = [];
}

/// <summary>A folder of skills the user added.</summary>
/// <param name="Scope"><c>Global</c> for every space, <c>Space</c> for the space of the listing, <c>Project</c> for its project.</param>
/// <param name="Path">The folder as its list writes it: what <c>removeFolder</c> names.</param>
/// <param name="FullPath">The full path it is read at; null when it is not a full path.</param>
/// <param name="State"><c>ready</c>, <c>missing</c> (nothing there, or a file), <c>unreadable</c>, or <c>invalid</c> (a path that is not read).</param>
/// <param name="Skills">How many skills of the listing it brings.</param>
internal sealed record SkillsFolder(string Scope, string Path, string? FullPath, string State, int Skills);

/// <summary>Names a folder of skills to add or remove.</summary>
/// <param name="Scope"><c>Global</c>, <c>Space</c> or <c>Project</c>.</param>
/// <param name="SpaceId">The space of a <c>Space</c> folder; null for the default space.</param>
/// <param name="Path">The folder: a full path, or one that starts with <c>~</c>.</param>
internal sealed record SkillsFolderRequest(string? ExpectedEpoch, string? ProjectId, string? SpaceId, string? Scope, string? Path);

/// <summary>
/// One discovered skill. The source is <c>ProjectAlta</c>, <c>ProjectCommon</c>, <c>UserAlta</c>, <c>UserCommon</c>,
/// <c>Plugin</c> or <c>Builtin</c>; the scope is <c>Project</c>, <c>User</c>, <c>Plugin</c> or <c>Builtin</c>.
/// </summary>
/// <param name="Folder">The id that names the folder of the skill to the code editor; null for an ambiguous name or one that cannot be part of an id.</param>
/// <param name="Path">The path of the folder of the skill.</param>
/// <param name="Id">An opaque, metadata-independent identity of this source and skill file, used for details and removal.</param>
internal sealed record SkillsEntry(string Id, string Name, string Title, string Description, string Source, string Scope, bool EnabledGlobal,
    bool EnabledProject, bool Enabled, bool Valid, bool Shadowed, bool Trusted, string? Folder = null, string? Path = null);
/// <summary>Names one listed skill by its id and source. Without an id, a legacy name must match exactly one skill.</summary>
/// <param name="Id">The id returned by the listing; when supplied, the name is ignored and is never a fallback.</param>
/// <param name="SpaceId">The space the page shows, whose added folders are read too; null for the default space.</param>
internal sealed record SkillsDetailRequest(string? ExpectedEpoch, string? ProjectId, string? Name, string? Source, string? Id = null, string? SpaceId = null);

/// <summary>
/// The detail of one skill. <c>Content</c> is the <c>SKILL.md</c> text, cut at 64 Ki characters
/// (<c>ContentTruncated</c>) and null when the file is larger than 256 KiB or gone. <c>Folder</c> is the id that
/// names the folder of the skill (<c>SkillRootPath</c>) where the code editor names a project; the folder of a
/// skill whose source is <c>Builtin</c> or <c>Plugin</c> is only read.
/// </summary>
internal sealed record SkillsDetailResponse(string Status, string? Name, string? Source, string? SkillFilePath, string? SkillRootPath, string? SourceId,
    string? ShadowedBy, bool ModelVisible, string? License, string? Compatibility, string? AllowedTools, string? Content, bool ContentTruncated,
    IReadOnlyList<SkillsRelatedFile> RelatedFiles, IReadOnlyList<SkillsDiagnostic> Diagnostics, int RelatedFilesOmitted, string? Folder = null)
{
    // The skill is reached through a symbolic link: it is shown, and edited or removed in its own folder only.
    public bool Linked { get; init; }
}
internal sealed record SkillsRelatedFile(string Category, string Path);
internal sealed record SkillsDiagnostic(string Severity, string Code, string Message);
internal sealed record SkillsSetEnabledRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Name, bool Enabled);
internal sealed record SkillsSetAllEnabledRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, IReadOnlyList<string>? Names, bool Enabled);
/// <summary>Names the skills a bulk switch acts on by the text of the filter of the page; empty or null for every skill.</summary>
/// <param name="SpaceId">The space the page shows, whose added folders are read too; null for the default space.</param>
internal sealed record SkillsSetMatchingEnabledRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Query, bool Enabled, string? SpaceId = null);
internal sealed record SkillsMutationResponse(string Status, int Changed, string? Message);
internal sealed record SkillsCreateRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Name, string? Description);
/// <summary>The skill that was created: its name, and the id and the path of its folder for the code editor.</summary>
internal sealed record SkillsCreateResponse(string Status, string? Name, string? Message, string? Folder = null, string? Path = null);
