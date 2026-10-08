namespace CodeAlta.Catalog.Skills;

/// <summary>Owns skill management discovery, authoring, related-file queries and config enablement.</summary>
/// <remarks>
/// Roots are explicit; a null profile omits user-common skills and never resolves the process home.
/// Creation rejects observed linked/reparse components and publishes a staged directory without overwrite.
/// These checks are not a sandbox against external path replacement races. Config writes use the existing
/// config owner, not a multi-file transaction or compare-and-swap against external writers.
/// </remarks>
public sealed class SkillManagementService
{
    private readonly SkillCatalog _catalog;
    private readonly CodeAltaConfigStore _config;
    private readonly string _globalRoot;
    private readonly string? _userProfileRoot;

    /// <summary>Creates a management service using explicit global and optional common-skill home roots.</summary>
    /// <param name="catalog">Existing skill catalog, including registered plugin root providers.</param>
    /// <param name="globalRoot">Absolute existing CodeAlta data directory.</param>
    /// <param name="userProfileRoot">Absolute existing profile directory, or null to omit user-common skills.</param>
    /// <exception cref="ArgumentNullException">The catalog is null.</exception>
    /// <exception cref="ArgumentException">The global root is empty.</exception>
    public SkillManagementService(SkillCatalog catalog, string globalRoot, string? userProfileRoot)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _globalRoot = globalRoot;
        _userProfileRoot = userProfileRoot;
        _config = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = globalRoot });
    }

    /// <summary>Lists management descriptors including disabled, invalid, shadowed and untrusted skills.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The scope is invalid.</exception>
    /// <exception cref="ArgumentException">A supplied root is not an absolute valid path.</exception>
    /// <exception cref="InvalidOperationException">A required root is absent.</exception>
    /// <exception cref="DirectoryNotFoundException">A supplied root is unavailable.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public Task<IReadOnlyList<SkillDescriptor>> LoadAsync(
        SkillListingScope scope, string? projectRoot, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        SkillAuthoring.ValidateRoot(_globalRoot);
        if (projectRoot is not null)
        {
            SkillAuthoring.ValidateRoot(projectRoot);
        }

        if (scope == SkillListingScope.Project)
        {
            SkillAuthoring.ValidateRoot(projectRoot);
        }

        var includeUser = scope is SkillListingScope.User or SkillListingScope.Combined;
        if (includeUser && _userProfileRoot is not null)
        {
            SkillAuthoring.ValidateRoot(_userProfileRoot);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _catalog.ListAsync(new SkillCatalogQuery
        {
            Discovery = new SkillDiscoveryContext
            {
                ProjectRoots = scope != SkillListingScope.User && projectRoot is not null ? [projectRoot] : [],
                UserCodeAltaRoot = includeUser ? _globalRoot : null,
                UserProfileRoot = includeUser ? _userProfileRoot : null,
            },
            GlobalDisabledSkillNames = _config.LoadGlobalDisabledSkillNames(),
            ProjectDisabledSkillNames = _config.LoadProjectDisabledSkillNames(projectRoot),
            IncludeDisabled = true,
            IncludeInvalid = true,
            IncludeShadowed = true,
            IncludeUntrusted = true,
        }, cancellationToken);
    }

    /// <summary>
    /// Lists the folders the skills of the user and of a project are read from, whether they exist or not: the
    /// CodeAlta folder first, then the common one and the one of GitHub Copilot. The built-in skills and those a
    /// plugin brings are not in a folder of the user.
    /// </summary>
    /// <param name="projectRoot">The folder of the project whose skill folders are listed too, or null.</param>
    /// <returns>The folders of the user, then those of the project.</returns>
    public IReadOnlyList<SkillRootLocation> GetRoots(string? projectRoot)
    {
        var roots = new List<SkillRootLocation> { new(SkillSourceKind.UserAlta, Path.Combine(_globalRoot, "skills")) };
        if (_userProfileRoot is not null)
        {
            roots.Add(new(SkillSourceKind.UserCommon, Path.Combine(_userProfileRoot, ".agents", "skills")));
            roots.Add(new(SkillSourceKind.UserCopilot, Path.Combine(_userProfileRoot, ".copilot", "skills")));
        }

        if (projectRoot is not null)
        {
            roots.Add(new(SkillSourceKind.ProjectAlta, Path.Combine(projectRoot, ".alta", "skills")));
            roots.Add(new(SkillSourceKind.ProjectCommon, Path.Combine(projectRoot, ".agents", "skills")));
            roots.Add(new(SkillSourceKind.ProjectCopilot, Path.Combine(projectRoot, ".github", "skills")));
        }

        return roots;
    }

    /// <summary>Creates a complete SKILL.md scaffold at an explicit writable CodeAlta location.</summary>
    /// <exception cref="ArgumentException">The target, name, description or root is invalid.</exception>
    /// <exception cref="InvalidOperationException">A required root is absent or the skill already exists.</exception>
    /// <exception cref="IOException">A root is unavailable, a path is linked, a collision occurs, or storage fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Storage access is denied.</exception>
    /// <exception cref="OperationCanceledException">Canceled before publication; no final skill is published.</exception>
    public Task<SkillCreationResult> CreateSkillAsync(
        SkillCreationTargetKind target, string? projectRoot, string? name, string? description,
        CancellationToken cancellationToken = default)
        => SkillAuthoring.CreateAsync(_globalRoot, projectRoot, target, name, description, cancellationToken);

    /// <summary>Sets one skill's name-based enablement in the requested configuration files.</summary>
    /// <exception cref="ArgumentException">A name, scope or root is invalid.</exception>
    /// <exception cref="InvalidOperationException">A required root is absent.</exception>
    /// <exception cref="IOException">A root is unavailable or config I/O fails.</exception>
    public SkillEnablementUpdateResult SetSkillEnabled(SkillEnablementScope scope, string? projectRoot, string name, bool enabled)
        => SetSkillsEnabled(scope, projectRoot, [name], enabled);

    /// <summary>Sets all names after validating every scope and required root, before any config write.</summary>
    /// <exception cref="ArgumentException">A name, scope or root is invalid.</exception>
    /// <exception cref="InvalidOperationException">A required root is absent.</exception>
    /// <exception cref="IOException">A root is unavailable or config I/O fails.</exception>
    public SkillEnablementUpdateResult SetSkillsEnabled(SkillEnablementScope scope, string? projectRoot, IReadOnlyList<string> names, bool enabled)
        => UpdateEnablement(scope, projectRoot, names, enabled);

    /// <summary>Inverts each name independently in each requested config file.</summary>
    /// <exception cref="ArgumentException">A name, scope or root is invalid.</exception>
    /// <exception cref="InvalidOperationException">A required root is absent.</exception>
    /// <exception cref="IOException">A root is unavailable or config I/O fails.</exception>
    public SkillEnablementUpdateResult InvertSkillsEnabled(SkillEnablementScope scope, string? projectRoot, IReadOnlyList<string> names)
        => UpdateEnablement(scope, projectRoot, names, null);

    /// <summary>Lists up to 128 conventional authoring resources using the catalog's ignore rules and limits.</summary>
    /// <remarks>Catalog Git discovery can read ancestor and user Git rules; callers must account for that in isolated fixtures.</remarks>
    /// <exception cref="ArgumentNullException">The descriptor is null.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public IReadOnlyList<SkillRelatedFile> ListRelatedFiles(SkillDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.SkillRootPath) || !Directory.Exists(descriptor.SkillRootPath))
        {
            return [];
        }

        var root = Path.GetFullPath(descriptor.SkillRootPath);
        var files = new List<SkillRelatedFile>();
        foreach (var relative in _catalog.ListFiles(descriptor, cancellationToken))
        {
            var separator = relative.IndexOf('/');
            if (separator < 0)
            {
                continue;
            }

            var category = relative[..separator].ToLowerInvariant();
            if (category is not ("scripts" or "references" or "assets"))
            {
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, comparison))
            {
                files.Add(new SkillRelatedFile(category, relative, full));
            }
        }

        return files.OrderBy(static file => file.Category switch { "scripts" => 0, "references" => 1, _ => 2 })
            .ThenBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
    }

    /// <summary>Resolves a skill file or conventional related resource to a backend-owned editor policy.</summary>
    /// <remarks>
    /// Re-discovers the exact skill path in the captured context; no caller-supplied descriptor or
    /// source flag grants a path. Related paths must exactly match a listed resource and contain no
    /// rooted, traversal, stream, or alternate-separator components. Observed links are rejected;
    /// external root/link replacement races remain outside this trusted workflow's guarantees.
    /// Plugin source alone does not establish bundled immutability, so plugin skills remain writable.
    /// </remarks>
    /// <exception cref="ArgumentException">The skill is not discovered or the resource request is malformed/unlisted.</exception>
    /// <exception cref="InvalidOperationException">A required discovery root is absent.</exception>
    /// <exception cref="IOException">Discovery failed or the document has a linked path.</exception>
    /// <exception cref="UnauthorizedAccessException">Storage access is denied.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<TextFileDocument> GetFileDocumentAsync(string skillFilePath, string? relatedPath, string? projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillFilePath);
        if (relatedPath is not null && (relatedPath.Contains('\\') || relatedPath.Contains(':') ||
            relatedPath.Split('/').Any(static part => part is "" or "." or "..")))
        {
            throw new ArgumentException("The related skill resource path is malformed.", nameof(relatedPath));
        }

        var fullPath = Path.GetFullPath(skillFilePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var descriptors = await LoadAsync(SkillListingScope.Combined, projectRoot, cancellationToken).ConfigureAwait(false);
        var descriptor = descriptors.FirstOrDefault(candidate => string.Equals(candidate.SkillFilePath, fullPath, comparison))
            ?? throw new ArgumentException("The skill file is not available in this context.", nameof(skillFilePath));
        var path = relatedPath is null ? descriptor.SkillFilePath :
            ListRelatedFiles(descriptor, cancellationToken).FirstOrDefault(file => string.Equals(file.RelativePath, relatedPath, StringComparison.Ordinal))?.FullPath
            ?? throw new ArgumentException("The related skill resource is not available.", nameof(relatedPath));
        var document = new TextFileDocument(path, descriptor.SourceKind, descriptor.SourceId);
        document.ValidatePath();
        return document;
    }

    private SkillEnablementUpdateResult UpdateEnablement(SkillEnablementScope scope, string? projectRoot, IReadOnlyList<string> names, bool? enabled)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        SkillAuthoring.ValidateRoot(_globalRoot);
        if (scope is SkillEnablementScope.Project or SkillEnablementScope.Both)
        {
            SkillAuthoring.ValidateRoot(projectRoot);
        }

        var normalized = names.Select(static name => SkillAuthoring.NormalizeName(name?.ToLowerInvariant(), portable: false))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var globalChanged = 0;
        var projectChanged = 0;
        if (scope is SkillEnablementScope.Global or SkillEnablementScope.Both)
        {
            var disabled = new HashSet<string>(_config.LoadGlobalDisabledSkillNames(), StringComparer.OrdinalIgnoreCase);
            globalChanged = ApplyChanges(disabled, normalized, enabled);
            if (globalChanged > 0)
            {
                _config.SaveGlobalDisabledSkillNames(disabled);
            }
        }

        if (scope is SkillEnablementScope.Project or SkillEnablementScope.Both)
        {
            var disabled = new HashSet<string>(_config.LoadProjectDisabledSkillNames(projectRoot), StringComparer.OrdinalIgnoreCase);
            projectChanged = ApplyChanges(disabled, normalized, enabled);
            if (projectChanged > 0)
            {
                _config.SaveProjectDisabledSkillNames(projectRoot!, disabled);
            }
        }

        return new SkillEnablementUpdateResult(globalChanged, projectChanged);
    }

    private static int ApplyChanges(HashSet<string> disabled, IReadOnlyList<string> names, bool? enabled)
    {
        var changed = 0;
        foreach (var name in names)
        {
            var target = enabled ?? disabled.Contains(name);
            if (target ? disabled.Remove(name) : disabled.Add(name))
            {
                changed++;
            }
        }

        return changed;
    }
}
