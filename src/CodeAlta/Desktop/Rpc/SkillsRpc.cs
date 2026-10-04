using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Lists the skills discovered for the global scope and an optional project, saves their name-based
/// enablement in the global or project configuration, and scaffolds new skills: the operations of the
/// terminal's skills manager.
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
    private readonly string? _epoch;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal SkillsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="projects">The host's project catalog; its global root holds the user skills and configuration.</param>
    /// <param name="catalog">The host's skill catalog, including its built-in and plugin root providers.</param>
    /// <param name="userProfileRoot">The profile holding the common <c>.agents/skills</c> root, or null to omit it.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> or <paramref name="catalog"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal SkillsService(ProjectCatalog projects, SkillCatalog catalog, string? userProfileRoot, string epoch)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _projects = projects;
        // A platform without a profile folder reports it as empty: that omits the common root, it is not an error.
        _management = new SkillManagementService(catalog, projects.Options.GlobalRoot, string.IsNullOrWhiteSpace(userProfileRoot) ? null : userProfileRoot);
        _epoch = epoch;
    }

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
            var descriptors = await _management.LoadAsync(SkillListingScope.Combined, project.Root, cancellationToken).ConfigureAwait(false);
            var skills = descriptors.Take(MaximumSkills).Select(static skill => new SkillsEntry(
                Bound(skill.Name, MaximumNameLength), Bound(skill.Title, MaximumNameLength), Bound(skill.Description, MaximumDescriptionLength),
                skill.SourceKind.ToString(), skill.Scope.ToString(), !skill.IsDisabledGlobally, !skill.IsDisabledForProject,
                skill.IsEnabled, skill.IsValid, skill.IsShadowed, skill.IsTrusted)).ToArray();
            return new("ok", request.ProjectId, skills, descriptors.Count - skills.Length);
        }
        catch (InvalidDataException)
        {
            return Failed("config_invalid"); // A configuration file that does not parse hides the enablement.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("read_failed");
        }
    }

    /// <summary>
    /// Describes one listed skill: where it lives, why it is or is not offered to the model, its validation
    /// diagnostics, its related files and the text of its <c>SKILL.md</c>.
    /// </summary>
    [NeoRpcMethod("detail")]
    public async Task<SkillsDetailResponse> DetailAsync(SkillsDetailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SkillsDetailResponse Failed(string status) => new(status, request.Name, request.Source, null, null, null, null, false, null, null, null, null, false, [], [], 0);
        if (_projects is null || _management is null) return Failed("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failed("stale_epoch");
        if (request.Name is not { Length: > 0 and <= MaximumNameLength } || request.Source is not { Length: > 0 and <= 64 }) return Failed("invalid");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return Failed(project.Status);
        try
        {
            var descriptors = await _management.LoadAsync(SkillListingScope.Combined, project.Root, cancellationToken).ConfigureAwait(false);
            // A shadowed skill shares its name with the one that hides it; the source tells them apart.
            var skill = descriptors.FirstOrDefault(candidate => string.Equals(candidate.Name, request.Name, StringComparison.Ordinal)
                && string.Equals(candidate.SourceKind.ToString(), request.Source, StringComparison.Ordinal));
            if (skill is null) return Failed("not_found");
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
                Math.Max(0, related.Count - MaximumRelatedFiles));
        }
        catch (InvalidDataException)
        {
            return Failed("config_invalid");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
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

    /// <summary>Creates a new skill folder with a <c>SKILL.md</c> scaffold in the global or project skills root.</summary>
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
            return new("ok", created.Name, null);
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
internal sealed record SkillsListResponse(string Status, string? ProjectId, IReadOnlyList<SkillsEntry> Skills, int Omitted);

/// <summary>
/// One discovered skill. The source is <c>ProjectAlta</c>, <c>ProjectCommon</c>, <c>UserAlta</c>, <c>UserCommon</c>,
/// <c>Plugin</c> or <c>Builtin</c>; the scope is <c>Project</c>, <c>User</c>, <c>Plugin</c> or <c>Builtin</c>.
/// </summary>
internal sealed record SkillsEntry(string Name, string Title, string Description, string Source, string Scope, bool EnabledGlobal,
    bool EnabledProject, bool Enabled, bool Valid, bool Shadowed, bool Trusted);
/// <summary>Names one listed skill by its name and source.</summary>
internal sealed record SkillsDetailRequest(string? ExpectedEpoch, string? ProjectId, string? Name, string? Source);

/// <summary>
/// The detail of one skill. <c>Content</c> is the <c>SKILL.md</c> text, cut at 64 Ki characters
/// (<c>ContentTruncated</c>) and null when the file is larger than 256 KiB or gone.
/// </summary>
internal sealed record SkillsDetailResponse(string Status, string? Name, string? Source, string? SkillFilePath, string? SkillRootPath, string? SourceId,
    string? ShadowedBy, bool ModelVisible, string? License, string? Compatibility, string? AllowedTools, string? Content, bool ContentTruncated,
    IReadOnlyList<SkillsRelatedFile> RelatedFiles, IReadOnlyList<SkillsDiagnostic> Diagnostics, int RelatedFilesOmitted);
internal sealed record SkillsRelatedFile(string Category, string Path);
internal sealed record SkillsDiagnostic(string Severity, string Code, string Message);
internal sealed record SkillsSetEnabledRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Name, bool Enabled);
internal sealed record SkillsSetAllEnabledRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, IReadOnlyList<string>? Names, bool Enabled);
internal sealed record SkillsMutationResponse(string Status, int Changed, string? Message);
internal sealed record SkillsCreateRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Name, string? Description);
internal sealed record SkillsCreateResponse(string Status, string? Name, string? Message);
