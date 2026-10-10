using System.Security.Cryptography;
using System.Text;
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
internal sealed class SkillsService
{
    /// <summary>Largest number of skills returned by one listing or changed by one bulk request.</summary>
    internal const int MaximumSkills = 512;

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
        _management = new SkillManagementService(catalog, projects.Options.GlobalRoot, string.IsNullOrWhiteSpace(userProfileRoot) ? null : userProfileRoot);
        _folders = new SkillFolders(projects, _management);
        _epoch = epoch;
    }

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
            var listing = await _management.LoadListingAsync(SkillListingScope.Combined, project.Root, cancellationToken).ConfigureAwait(false);
            var descriptors = listing.Skills;
            var skills = descriptors.Take(MaximumSkills).Select(skill => new SkillsEntry(
                IdOf(skill), Bound(skill.Name, MaximumNameLength), Bound(skill.Title, MaximumNameLength), Bound(skill.Description, MaximumDescriptionLength),
                skill.SourceKind.ToString(), skill.Scope.ToString(), !skill.IsDisabledGlobally, !skill.IsDisabledForProject,
                skill.IsEnabled, skill.IsValid, skill.IsShadowed, skill.IsTrusted,
                // The row opens the folder of its skill in the code editor: the id the editor names it by, and its path.
                EditorFolderOf(skill, descriptors, request.ProjectId, project.Root), Bound(skill.SkillRootPath, MaximumPathLength))).ToArray();
            return new("ok", request.ProjectId, skills, descriptors.Count - skills.Length)
            {
                Problems = [.. listing.Problems.Select(static problem => new PluginsProblem("config", Bound(problem.Path, MaximumPathLength), Clean(problem.Message), problem.IsProject ? "Project" : "Global"))],
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
            var descriptors = (await _management.LoadListingAsync(SkillListingScope.Combined, project.Root, cancellationToken).ConfigureAwait(false)).Skills;
            var (status, skill) = FindSkill(descriptors, request, source);
            if (skill is null) return Failed(status);
            // The document lookup refuses a skill file reached through a link.
            var document = await _management.GetFileDocumentAsync(skill.SkillFilePath, null, project.Root, cancellationToken).ConfigureAwait(false);
            string? content = null;
            var truncated = false;
            var file = new FileInfo(document.FullPath);
            if (file.Exists && file.Length <= MaximumSkillFileBytes)
            {
                content = await File.ReadAllTextAsync(document.FullPath, cancellationToken).ConfigureAwait(false);
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
                Math.Max(0, related.Count - MaximumRelatedFiles), EditorFolderOf(skill, descriptors, request.ProjectId, project.Root));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log(exception, "The details of a skill could not be read.");
            return Failed("read_failed"); // Includes a skill file that is reached through a link.
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

    private async Task<SkillsMutationResponse> SetAsync(string? expectedEpoch, string? projectId, string? scope, IReadOnlyList<string>? names,
        bool enabled, CancellationToken cancellationToken)
    {
        if (_projects is null || _management is null) return new("unavailable", 0, null);
        if (!string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", 0, null);
        if (!TryScope(scope, out var projectScope)) return new("invalid", 0, ScopeRule);
        if (names is not { Count: > 0 and <= MaximumSkills } || names.Any(static name => name is not { Length: > 0 and <= MaximumNameLength }))
            return new("invalid", 0, "Between 1 and 512 skill names are required.");
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

    private static string? EditorFolderOf(SkillDescriptor skill, IReadOnlyList<SkillDescriptor> descriptors, string? projectId, string? projectRoot)
        // Editor folder handles are still name-based: do not offer a handle that could open another row's files.
        => descriptors.Count(candidate => candidate.SourceKind == skill.SourceKind && string.Equals(candidate.Name, skill.Name, StringComparison.Ordinal)) == 1
            ? SkillFolders.IdOf(skill, projectId, projectRoot) : null;

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

internal sealed record SkillsListRequest(string? ExpectedEpoch, string? ProjectId);
internal sealed record SkillsListResponse(string Status, string? ProjectId, IReadOnlyList<SkillsEntry> Skills, int Omitted)
{
    /// <summary>
    /// The configuration files that could not be read or parsed (kind <c>config</c>, with the scope of the file): the
    /// skills are listed as if such a file disabled none, so a skill it disables is shown as enabled.
    /// </summary>
    public IReadOnlyList<PluginsProblem> Problems { get; init; } = [];
}

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
internal sealed record SkillsDetailRequest(string? ExpectedEpoch, string? ProjectId, string? Name, string? Source, string? Id = null);

/// <summary>
/// The detail of one skill. <c>Content</c> is the <c>SKILL.md</c> text, cut at 64 Ki characters
/// (<c>ContentTruncated</c>) and null when the file is larger than 256 KiB or gone. <c>Folder</c> is the id that
/// names the folder of the skill (<c>SkillRootPath</c>) where the code editor names a project; the folder of a
/// skill whose source is <c>Builtin</c> or <c>Plugin</c> is only read.
/// </summary>
internal sealed record SkillsDetailResponse(string Status, string? Name, string? Source, string? SkillFilePath, string? SkillRootPath, string? SourceId,
    string? ShadowedBy, bool ModelVisible, string? License, string? Compatibility, string? AllowedTools, string? Content, bool ContentTruncated,
    IReadOnlyList<SkillsRelatedFile> RelatedFiles, IReadOnlyList<SkillsDiagnostic> Diagnostics, int RelatedFilesOmitted, string? Folder = null);
internal sealed record SkillsRelatedFile(string Category, string Path);
internal sealed record SkillsDiagnostic(string Severity, string Code, string Message);
internal sealed record SkillsSetEnabledRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Name, bool Enabled);
internal sealed record SkillsSetAllEnabledRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, IReadOnlyList<string>? Names, bool Enabled);
internal sealed record SkillsMutationResponse(string Status, int Changed, string? Message);
internal sealed record SkillsCreateRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Name, string? Description);
/// <summary>The skill that was created: its name, and the id and the path of its folder for the code editor.</summary>
internal sealed record SkillsCreateResponse(string Status, string? Name, string? Message, string? Folder = null, string? Path = null);
