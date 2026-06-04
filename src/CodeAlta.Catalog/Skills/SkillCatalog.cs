using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Unicode;
using SharpYaml;
using XenoAtom.Glob.Git;
using XenoAtom.Glob.IO;
using XenoAtom.Glob.Ignore;

namespace CodeAlta.Catalog.Skills;

/// <summary>
/// Discovers and reads skills stored on disk.
/// </summary>
public sealed class SkillCatalog
{
    private static readonly IgnoreRuleSet FixedExclusionRules = IgnoreRuleSet.ParseGitIgnore(
        """
        .git/
        .hg/
        .svn/
        .jj/
        .sl/
        """);

    private static readonly FrozenSet<string> AllowedTopLevelFields = new[]
    {
        "name",
        "description",
        "license",
        "compatibility",
        "metadata",
        "allowed-tools",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly FileTreeWalker _walker = new();
    private readonly IReadOnlyList<ISkillRootProvider> _rootProviders;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillCatalog"/> class.
    /// </summary>
    /// <param name="rootProviders">Optional root providers. Built-in providers are used when omitted.</param>
    public SkillCatalog(IReadOnlyList<ISkillRootProvider>? rootProviders = null)
    {
        _rootProviders = rootProviders is { Count: > 0 }
            ? rootProviders
            : [new ProjectCodeAltaSkillRootProvider(), new ProjectCommonSkillRootProvider(), new UserCodeAltaSkillRootProvider(), new UserCommonSkillRootProvider(), new BuiltInCodeAltaSkillRootProvider()];
    }

    /// <summary>
    /// Lists discovered skills under the provided root directories using the legacy metadata shape.
    /// </summary>
    /// <param name="roots">Root directories that contain skill folders (each with a <c>SKILL.md</c> file).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Discovered visible skills.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="roots"/> is <see langword="null"/>.</exception>
    public async Task<IReadOnlyList<SkillInfo>> ListAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var descriptors = await ListAsync(
                CreateLegacyQuery(roots, includeShadowed: false, includeInvalid: false),
                cancellationToken)
            .ConfigureAwait(false);

        return descriptors
            .Select(static descriptor => new SkillInfo
            {
                Name = descriptor.Name,
                Title = descriptor.Title,
                Description = descriptor.Description,
                Path = descriptor.SkillRootPath,
            })
            .ToArray();
    }

    /// <summary>
    /// Lists discovered skills with metadata, provenance, and diagnostics.
    /// </summary>
    /// <param name="query">Discovery query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Discovered skill descriptors.</returns>
    public async Task<IReadOnlyList<SkillDescriptor>> ListAsync(
        SkillCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var roots = await ResolveRootsAsync(query.Discovery, cancellationToken).ConfigureAwait(false);
        var discovered = new List<SkillDescriptor>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            discovered.AddRange(await ScanRootAsync(root, cancellationToken).ConfigureAwait(false));
        }

        var annotated = ApplyEnablement(ApplyShadowing(discovered), query);
        return ApplyQuery(annotated, query);
    }

    /// <summary>
    /// Validates skills for the provided query and returns their descriptors and diagnostics.
    /// </summary>
    /// <param name="query">Discovery query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validated descriptors.</returns>
    public Task<IReadOnlyList<SkillDescriptor>> ValidateAsync(
        SkillCatalogQuery query,
        CancellationToken cancellationToken = default)
        => ListAsync(query, cancellationToken);

    /// <summary>
    /// Reads the <c>SKILL.md</c> contents for a discovered skill using the legacy roots overload.
    /// </summary>
    /// <param name="roots">Roots used for discovery.</param>
    /// <param name="skillName">Skill name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The skill document when found; otherwise <see langword="null"/>.</returns>
    public Task<SkillDocument?> GetAsync(
        IReadOnlyList<string> roots,
        string skillName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return GetAsync(
            CreateLegacyQuery(roots, includeShadowed: true, includeInvalid: true) with { SkillName = skillName },
            skillName,
            cancellationToken);
    }

    /// <summary>
    /// Reads the <c>SKILL.md</c> contents for a discovered skill.
    /// </summary>
    /// <param name="query">Discovery query.</param>
    /// <param name="skillName">Skill name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The skill document when found; otherwise <see langword="null"/>.</returns>
    public async Task<SkillDocument?> GetAsync(
        SkillCatalogQuery query,
        string skillName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(skillName))
        {
            throw new ArgumentException("Skill name is required.", nameof(skillName));
        }

        var descriptor = await ResolveDescriptorAsync(query, skillName, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return null;
        }

        return await LoadDocumentAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Safely resolves and reads a resource file under a discovered skill root.
    /// </summary>
    /// <param name="query">Discovery query.</param>
    /// <param name="skillName">Skill name.</param>
    /// <param name="relativePath">Relative resource path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved resource.</returns>
    public async Task<SkillResource> ReadResourceAsync(
        SkillCatalogQuery query,
        string skillName,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(skillName))
        {
            throw new ArgumentException("Skill name is required.", nameof(skillName));
        }

        var descriptor = await ResolveDescriptorAsync(query, skillName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Skill '{skillName}' was not found.");
        var resolvedPath = ResolveResourcePath(descriptor.SkillRootPath, relativePath);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException("Skill resource was not found.", resolvedPath);
        }

        return new SkillResource
        {
            Descriptor = descriptor,
            RelativePath = NormalizeRelativePath(relativePath),
            FullPath = resolvedPath,
            Content = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Lists non-ignored resource files under a discovered skill root.
    /// </summary>
    /// <param name="descriptor">Skill descriptor whose root should be scanned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Relative file paths under the skill root, excluding <c>SKILL.md</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<string> ListFiles(
        SkillDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return EnumerateSkillFiles(descriptor.SkillRootPath, maxCount: 256, cancellationToken);
    }

    /// <summary>
    /// Reads a resource file under a discovered skill directory.
    /// </summary>
    /// <param name="roots">Roots used for discovery.</param>
    /// <param name="skillName">Skill name.</param>
    /// <param name="relativePath">Relative path under the skill directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>File bytes.</returns>
    public async Task<byte[]> GetResourceAsync(
        IReadOnlyList<string> roots,
        string skillName,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var resource = await ReadResourceAsync(
                CreateLegacyQuery(roots, includeShadowed: true, includeInvalid: true),
                skillName,
                relativePath,
                cancellationToken)
            .ConfigureAwait(false);
        return resource.Content;
    }

    /// <summary>
    /// Activates a skill and produces the canonical payload described by the skills spec.
    /// </summary>
    /// <param name="query">Discovery query.</param>
    /// <param name="skillName">Skill name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activation payload when found; otherwise <see langword="null"/>.</returns>
    public async Task<SkillActivation?> ActivateAsync(
        SkillCatalogQuery query,
        string skillName,
        CancellationToken cancellationToken = default)
    {
        var document = await GetAsync(query, skillName, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        if (!document.Descriptor.IsEnabled)
        {
            return null;
        }

        var files = EnumerateSkillFiles(document.Descriptor.SkillRootPath, maxCount: 64, cancellationToken);
        var baseDirectoryUri = new Uri(AppendDirectorySeparator(document.Descriptor.SkillRootPath)).AbsoluteUri;
        var payload = BuildActivationPayload(document, document.Descriptor, baseDirectoryUri, files);
        return new SkillActivation
        {
            Descriptor = document.Descriptor,
            Document = document,
            BaseDirectoryUri = baseDirectoryUri,
            Files = files,
            Payload = payload,
        };
    }

    private async Task<IReadOnlyList<SkillRootRegistration>> ResolveRootsAsync(
        SkillDiscoveryContext discovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        var roots = new List<SkillRootRegistration>();
        if (discovery.UseBuiltInRoots)
        {
            foreach (var provider in _rootProviders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                roots.AddRange(await provider.GetRootsAsync(discovery, cancellationToken).ConfigureAwait(false));
            }
        }

        roots.AddRange(discovery.AdditionalRoots);
        return roots
            .Where(static root => !string.IsNullOrWhiteSpace(root.RootPath))
            .Select(static root => root with { RootPath = Path.GetFullPath(root.RootPath) })
            .GroupBy(static root => root.RootPath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderBy(static root => root.Precedence).First())
            .OrderBy(static root => root.Precedence)
            .ThenBy(static root => root.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<SkillDescriptor>> ScanRootAsync(
        SkillRootRegistration root,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root.RootPath))
        {
            return [];
        }

        var skillFilePaths = DiscoverSkillFiles(root.RootPath, cancellationToken);
        var descriptors = new List<SkillDescriptor>(skillFilePaths.Count);
        foreach (var skillFilePath in skillFilePaths)
        {
            descriptors.Add(await BuildDescriptorAsync(root, skillFilePath, cancellationToken).ConfigureAwait(false));
        }

        return descriptors;
    }

    private IReadOnlyList<string> DiscoverSkillFiles(string rootPath, CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.GetFullPath(rootPath);
        var discovered = new List<string>();
        var walkOptions = CreateWalkOptions(normalizedRoot, cancellationToken);

        foreach (var entry in _walker.Enumerate(normalizedRoot, walkOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory ||
                !string.Equals(entry.Name, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            discovered.Add(Path.GetFullPath(entry.FullPath));
        }

        discovered.Sort(static (left, right) =>
        {
            var depthComparison = GetPathDepth(left).CompareTo(GetPathDepth(right));
            return depthComparison != 0
                ? depthComparison
                : StringComparer.OrdinalIgnoreCase.Compare(left, right);
        });

        var selected = new List<string>(discovered.Count);
        var selectedRoots = new List<string>(discovered.Count);
        foreach (var skillFile in discovered)
        {
            var skillRoot = Path.GetDirectoryName(skillFile)
                ?? throw new InvalidOperationException($"Skill file '{skillFile}' did not resolve to a directory.");
            if (selectedRoots.Any(existing => IsUnderRoot(skillRoot, existing)))
            {
                continue;
            }

            selected.Add(skillFile);
            selectedRoots.Add(skillRoot);
        }

        return selected;
    }

    private FileTreeWalkOptions CreateWalkOptions(string rootPath, CancellationToken cancellationToken)
    {
        var isGitAware = RepositoryDiscovery.TryDiscover(rootPath, out var repositoryContext);
        return new FileTreeWalkOptions
        {
            CancellationToken = cancellationToken,
            RepositoryContext = repositoryContext,
            AdditionalRuleSets = isGitAware
                ? [FixedExclusionRules]
                : BuildNonGitRuleSets(rootPath, cancellationToken),
        };
    }

    private IReadOnlyList<IgnoreRuleSet> BuildNonGitRuleSets(string rootPath, CancellationToken cancellationToken)
    {
        var ruleSets = new List<IgnoreRuleSet> { FixedExclusionRules };
        var discoveryOptions = new FileTreeWalkOptions
        {
            CancellationToken = cancellationToken,
            AdditionalRuleSets = [FixedExclusionRules],
        };

        foreach (var entry in _walker.Enumerate(rootPath, discoveryOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory || !string.Equals(entry.Name, ".gitignore", StringComparison.Ordinal))
            {
                continue;
            }

            var baseDirectory = Path.GetDirectoryName(entry.RelativePath)?.Replace('\\', '/') ?? string.Empty;
            ruleSets.Add(IgnoreRuleSet.ParseGitIgnore(File.ReadAllText(entry.FullPath), baseDirectory: baseDirectory, sourcePath: entry.FullPath));
        }

        return ruleSets;
    }

    private async Task<SkillDescriptor> BuildDescriptorAsync(
        SkillRootRegistration root,
        string skillFilePath,
        CancellationToken cancellationToken)
    {
        var skillRootPath = Path.GetDirectoryName(skillFilePath)
            ?? throw new InvalidOperationException($"Skill file '{skillFilePath}' did not resolve to a directory.");
        var diagnostics = new List<SkillValidationDiagnostic>();
        string rawContent;
        try
        {
            rawContent = await File.ReadAllTextAsync(skillFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "skill-unreadable", $"Unable to read '{skillFilePath}': {ex.Message}", path: skillFilePath));
            return CreateDescriptor(
                root,
                skillRootPath,
                skillFilePath,
                Path.GetFileName(skillRootPath),
                Path.GetFileName(skillRootPath),
                description: string.Empty,
                frontmatter: new SkillFrontmatter(),
                diagnostics,
                isTrusted: root.IsTrusted);
        }

        var rawFrontmatter = string.Empty;
        var body = rawContent;
        var frontmatter = new SkillFrontmatter();
        Dictionary<string, object?>? frontmatterMap = null;

        if (!TrySplitFrontmatter(rawContent, out rawFrontmatter, out body))
        {
            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "frontmatter-missing", "SKILL.md must start with YAML frontmatter.", path: skillFilePath));
        }
        else
        {
            try
            {
                frontmatterMap = YamlSerializer.Deserialize<Dictionary<string, object?>>(rawFrontmatter)
                    ?? new Dictionary<string, object?>(StringComparer.Ordinal);
                frontmatter = ParseFrontmatter(frontmatterMap, diagnostics, skillFilePath);
            }
            catch (Exception ex)
            {
                diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "frontmatter-invalid", $"SKILL.md frontmatter could not be parsed: {ex.Message}", path: skillFilePath));
            }
        }

        var name = Normalize(frontmatter.Name);
        var normalizedName = string.IsNullOrWhiteSpace(name)
            ? Normalize(Path.GetFileName(skillRootPath))
            : name;
        var description = Normalize(frontmatter.Description) ?? string.Empty;
        var title = ParseTitle(body) ?? normalizedName;

        ValidateName(name, skillRootPath, diagnostics, skillFilePath);
        ValidateDescription(description, diagnostics, skillFilePath);
        ValidateCompatibility(frontmatter.Compatibility, diagnostics, skillFilePath);
        ValidateUnknownFields(frontmatter.UnknownTopLevelFields, diagnostics, skillFilePath);
        ValidateSkillSize(rawContent, diagnostics, skillFilePath);

        return CreateDescriptor(
            root,
            skillRootPath,
            skillFilePath,
            name ?? string.Empty,
            title ?? normalizedName ?? name ?? string.Empty,
            description,
            frontmatter,
            diagnostics,
            root.IsTrusted,
            normalizedName);
    }

    private static SkillDescriptor CreateDescriptor(
        SkillRootRegistration root,
        string skillRootPath,
        string skillFilePath,
        string name,
        string title,
        string description,
        SkillFrontmatter frontmatter,
        IReadOnlyList<SkillValidationDiagnostic> diagnostics,
        bool isTrusted,
        string? normalizedName = null,
        bool isShadowed = false,
        string? shadowedBySkillFilePath = null)
    {
        var effectiveNormalizedName = Normalize(name) ?? normalizedName ?? string.Empty;
        var hasErrors = diagnostics.Any(static diagnostic => diagnostic.Severity == SkillValidationSeverity.Error);
        return new SkillDescriptor
        {
            Name = name,
            NormalizedName = effectiveNormalizedName,
            Title = string.IsNullOrWhiteSpace(title) ? name : title,
            Description = description,
            SkillRootPath = skillRootPath,
            SkillFilePath = skillFilePath,
            SourceKind = root.SourceKind,
            SourceId = root.SourceId,
            Scope = root.Scope,
            Precedence = root.Precedence,
            Frontmatter = frontmatter,
            Diagnostics = diagnostics,
            IsShadowed = isShadowed,
            ShadowedBySkillFilePath = shadowedBySkillFilePath,
            IsTrusted = isTrusted,
            IsValid = !hasErrors && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description),
            IsEnabled = true,
            IsModelVisible = !hasErrors && !isShadowed && isTrusted && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description),
        };
    }

    private static SkillFrontmatter ParseFrontmatter(
        IReadOnlyDictionary<string, object?> frontmatterMap,
        List<SkillValidationDiagnostic> diagnostics,
        string skillFilePath)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> unknownFields = [];
        string? GetString(string key)
        {
            if (!frontmatterMap.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            if (value is string text)
            {
                return Normalize(text);
            }

            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "frontmatter-type", $"Frontmatter field '{key}' must be a string.", key, skillFilePath));
            return null;
        }

        foreach (var key in frontmatterMap.Keys)
        {
            if (!AllowedTopLevelFields.Contains(key))
            {
                unknownFields.Add(key);
            }
        }

        if (frontmatterMap.TryGetValue("metadata", out var metadataValue) && metadataValue is not null)
        {
            if (metadataValue is Dictionary<object, object> yamlDictionary)
            {
                foreach (var entry in yamlDictionary)
                {
                    if (entry.Key is not string metadataKey || entry.Value is not string metadataText)
                    {
                        diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "metadata-invalid", "metadata must be a map<string,string>.", "metadata", skillFilePath));
                        metadata.Clear();
                        break;
                    }

                    metadata[metadataKey] = metadataText;
                }
            }
            else if (metadataValue is Dictionary<string, object?> objectDictionary)
            {
                foreach (var entry in objectDictionary)
                {
                    if (entry.Value is not string metadataText)
                    {
                        diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "metadata-invalid", "metadata must be a map<string,string>.", "metadata", skillFilePath));
                        metadata.Clear();
                        break;
                    }

                    metadata[entry.Key] = metadataText;
                }
            }
            else
            {
                diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "metadata-invalid", "metadata must be a map<string,string>.", "metadata", skillFilePath));
            }
        }

        return new SkillFrontmatter
        {
            Name = GetString("name"),
            Description = GetString("description"),
            License = GetString("license"),
            Compatibility = GetString("compatibility"),
            Metadata = metadata,
            AllowedTools = GetString("allowed-tools"),
            UnknownTopLevelFields = unknownFields,
        };
    }

    private static IReadOnlyList<SkillDescriptor> ApplyShadowing(IReadOnlyList<SkillDescriptor> descriptors)
    {
        var ordered = descriptors
            .OrderBy(static descriptor => descriptor.Precedence)
            .ThenBy(static descriptor => descriptor.SkillFilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var winners = new Dictionary<string, SkillDescriptor>(StringComparer.OrdinalIgnoreCase);
        var results = new List<SkillDescriptor>(ordered.Length);
        foreach (var descriptor in ordered)
        {
            if (string.IsNullOrWhiteSpace(descriptor.NormalizedName))
            {
                results.Add(descriptor);
                continue;
            }

            if (!winners.TryGetValue(descriptor.NormalizedName, out var winner))
            {
                winners.Add(descriptor.NormalizedName, descriptor);
                results.Add(descriptor);
                continue;
            }

            var diagnostics = descriptor.Diagnostics.Concat(
            [
                CreateDiagnostic(
                    SkillValidationSeverity.Error,
                    "skill-shadowed",
                    $"Skill '{descriptor.Name}' is shadowed by higher-precedence skill '{winner.Name}'.",
                    path: descriptor.SkillFilePath),
            ]).ToArray();
            results.Add(descriptor with
            {
                Diagnostics = diagnostics,
                IsShadowed = true,
                ShadowedBySkillFilePath = winner.SkillFilePath,
                IsValid = false,
                IsModelVisible = false,
            });
        }

        return results
            .OrderBy(static descriptor => descriptor.Precedence)
            .ThenBy(static descriptor => descriptor.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static descriptor => descriptor.SkillFilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<SkillDescriptor> ApplyEnablement(
        IReadOnlyList<SkillDescriptor> descriptors,
        SkillCatalogQuery query)
    {
        var globallyDisabled = CreateDisabledSet(query.GlobalDisabledSkillNames);
        var projectDisabled = CreateDisabledSet(query.ProjectDisabledSkillNames);
        if (globallyDisabled.Count == 0 && projectDisabled.Count == 0)
        {
            return descriptors;
        }

        return descriptors
            .Select(descriptor =>
            {
                var key = Normalize(descriptor.NormalizedName) ?? Normalize(descriptor.Name) ?? string.Empty;
                var disabledGlobally = globallyDisabled.Contains(key);
                var disabledForProject = projectDisabled.Contains(key);
                var isEnabled = !disabledGlobally && !disabledForProject;
                return descriptor with
                {
                    IsDisabledGlobally = disabledGlobally,
                    IsDisabledForProject = disabledForProject,
                    IsEnabled = isEnabled,
                    IsModelVisible = descriptor.IsModelVisible && isEnabled,
                };
            })
            .ToArray();
    }

    private static HashSet<string> CreateDisabledSet(IReadOnlyCollection<string>? names)
        => names is null || names.Count == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : names
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<SkillDescriptor> ApplyQuery(
        IReadOnlyList<SkillDescriptor> descriptors,
        SkillCatalogQuery query)
    {
        IEnumerable<SkillDescriptor> filtered = descriptors;
        if (!string.IsNullOrWhiteSpace(query.SkillName))
        {
            filtered = filtered.Where(descriptor =>
                string.Equals(descriptor.Name, query.SkillName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(descriptor.NormalizedName, query.SkillName, StringComparison.OrdinalIgnoreCase));
        }

        if (!query.IncludeDisabled)
        {
            filtered = filtered.Where(static descriptor => descriptor.IsEnabled);
        }

        if (query.ModelVisibleOnly)
        {
            filtered = filtered.Where(static descriptor => descriptor.IsModelVisible);
        }
        else
        {
            if (!query.IncludeInvalid)
            {
                filtered = filtered.Where(static descriptor => descriptor.IsValid);
            }

            if (!query.IncludeShadowed)
            {
                filtered = filtered.Where(static descriptor => !descriptor.IsShadowed);
            }

            if (!query.IncludeUntrusted)
            {
                filtered = filtered.Where(static descriptor => descriptor.IsTrusted);
            }
        }

        return filtered.ToArray();
    }

    private async Task<SkillDescriptor?> ResolveDescriptorAsync(
        SkillCatalogQuery query,
        string skillName,
        CancellationToken cancellationToken)
    {
        var descriptors = await ListAsync(
                query with
                {
                    SkillName = skillName,
                    IncludeInvalid = true,
                    IncludeShadowed = true,
                    IncludeUntrusted = true,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return descriptors.FirstOrDefault(descriptor =>
            string.Equals(descriptor.Name, skillName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(descriptor.NormalizedName, skillName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<SkillDocument> LoadDocumentAsync(
        SkillDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var rawContent = await File.ReadAllTextAsync(descriptor.SkillFilePath, cancellationToken).ConfigureAwait(false);
        var rawFrontmatter = string.Empty;
        var body = rawContent;
        _ = TrySplitFrontmatter(rawContent, out rawFrontmatter, out body);
        return new SkillDocument
        {
            Descriptor = descriptor,
            Frontmatter = descriptor.Frontmatter,
            RawFrontmatter = rawFrontmatter,
            RawContent = rawContent,
            Body = body,
        };
    }

    private static string ResolveResourcePath(string skillRootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path is required.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Relative path must not be rooted.", nameof(relativePath));
        }

        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        var combinedPath = Path.GetFullPath(Path.Combine(skillRootPath, normalizedRelativePath));
        var expectedRoot = AppendDirectorySeparator(Path.GetFullPath(skillRootPath));
        if (!combinedPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Relative path must not escape the skill directory.", nameof(relativePath));
        }

        return combinedPath;
    }

    private IReadOnlyList<string> EnumerateSkillFiles(
        string skillRootPath,
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(skillRootPath))
        {
            return [];
        }

        var normalizedRoot = Path.GetFullPath(skillRootPath);
        var walkOptions = CreateWalkOptions(normalizedRoot, cancellationToken);
        var files = new List<string>();
        foreach (var entry in _walker.Enumerate(normalizedRoot, walkOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory ||
                string.Equals(entry.Name, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add(Path.GetRelativePath(normalizedRoot, entry.FullPath).Replace('\\', '/'));
        }

        return files
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToArray();
    }

    private static string BuildActivationPayload(
        SkillDocument document,
        SkillDescriptor descriptor,
        string baseDirectoryUri,
        IReadOnlyList<string> files)
    {
        var builder = new StringBuilder();
        builder.Append("<skill_content name=\"")
            .Append(EscapeXml(descriptor.Name))
            .Append("\" source=\"")
            .Append(EscapeXml(ToSourceLabel(descriptor.SourceKind)))
            .Append("\" source_kind=\"")
            .Append(EscapeXml(descriptor.SourceKind.ToString()))
            .Append("\" source_id=\"")
            .Append(EscapeXml(descriptor.SourceId))
            .Append("\" path=\"")
            .Append(EscapeXml(descriptor.SkillFilePath))
            .Append("\" root=\"")
            .Append(EscapeXml(descriptor.SkillRootPath))
            .Append("\" base_directory=\"")
            .Append(EscapeXml(baseDirectoryUri))
            .AppendLine("\">");
        builder.Append("# Skill: ")
            .AppendLine(descriptor.Name)
            .AppendLine();
        builder.AppendLine(document.Body.Trim());
        builder.AppendLine();
        builder.Append("Base directory: ").AppendLine(baseDirectoryUri);
        builder.AppendLine("Relative paths in this skill resolve against this directory.");
        builder.AppendLine();
        builder.AppendLine("<skill_files>");
        foreach (var file in files)
        {
            builder.Append("  <file>").Append(EscapeXml(file)).AppendLine("</file>");
        }

        builder.AppendLine("</skill_files>");
        builder.Append("</skill_content>");
        return builder.ToString();
    }

    private static string ToSourceLabel(SkillSourceKind sourceKind)
    {
        return sourceKind switch
        {
            SkillSourceKind.ProjectAlta or SkillSourceKind.ProjectCommon => "project",
            SkillSourceKind.UserAlta or SkillSourceKind.UserCommon => "user",
            SkillSourceKind.Plugin => "plugin",
            SkillSourceKind.Builtin => "builtin",
            _ => "temporary",
        };
    }

    private static string EscapeXml(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static bool TrySplitFrontmatter(string contents, out string frontmatter, out string body)
    {
        const string delimiter = "---";
        frontmatter = string.Empty;
        body = contents;

        if (!contents.StartsWith(delimiter, StringComparison.Ordinal))
        {
            return false;
        }

        using var reader = new StringReader(contents);
        _ = reader.ReadLine();

        var builder = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.Equals(line.Trim(), delimiter, StringComparison.Ordinal))
            {
                frontmatter = string.Join('\n', builder);
                body = reader.ReadToEnd() ?? string.Empty;
                return true;
            }

            builder.Add(line);
        }

        return false;
    }

    private static string? ParseTitle(string markdown)
    {
        foreach (var line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                return trimmed[2..].Trim();
            }
        }

        return null;
    }

    private static void ValidateName(
        string? name,
        string skillRootPath,
        List<SkillValidationDiagnostic> diagnostics,
        string skillFilePath)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "name-missing", "Skill frontmatter must declare a non-empty 'name'.", "name", skillFilePath));
            return;
        }

        if (name.Length > 64)
        {
            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "name-too-long", "Skill name must be 64 characters or fewer.", "name", skillFilePath));
        }

        if (name.StartsWith("-", StringComparison.Ordinal) ||
            name.EndsWith("-", StringComparison.Ordinal) ||
            name.Contains("--", StringComparison.Ordinal))
        {
            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "name-format", "Skill name may not start or end with '-' and may not contain consecutive hyphens.", "name", skillFilePath));
        }

        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Value == '-')
            {
                continue;
            }

            if (!Rune.IsLetterOrDigit(rune))
            {
                diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "name-format", "Skill name must contain only lowercase Unicode alphanumeric characters and hyphens.", "name", skillFilePath));
                break;
            }

            if (Rune.IsLetter(rune) && Rune.ToLowerInvariant(rune) != rune)
            {
                diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "name-format", "Skill name must use lowercase characters.", "name", skillFilePath));
                break;
            }
        }

        var directoryName = Path.GetFileName(skillRootPath);
        if (!string.Equals(directoryName, name, StringComparison.Ordinal))
        {
            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "name-directory-mismatch", $"Skill directory '{directoryName}' must match declared name '{name}'.", "name", skillFilePath));
        }
    }

    private static void ValidateDescription(
        string? description,
        List<SkillValidationDiagnostic> diagnostics,
        string skillFilePath)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "description-missing", "Skill frontmatter must declare a non-empty 'description'.", "description", skillFilePath));
            return;
        }

        if (description.Length > 1024)
        {
            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "description-too-long", "Skill description must be 1024 characters or fewer.", "description", skillFilePath));
        }
    }

    private static void ValidateCompatibility(
        string? compatibility,
        List<SkillValidationDiagnostic> diagnostics,
        string skillFilePath)
    {
        if (!string.IsNullOrWhiteSpace(compatibility) && compatibility.Length > 500)
        {
            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "compatibility-too-long", "compatibility must be 500 characters or fewer.", "compatibility", skillFilePath));
        }
    }

    private static void ValidateUnknownFields(
        IReadOnlyList<string> unknownFields,
        List<SkillValidationDiagnostic> diagnostics,
        string skillFilePath)
    {
        foreach (var unknownField in unknownFields)
        {
            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Error, "unknown-frontmatter-field", $"Unknown top-level frontmatter field '{unknownField}'.", unknownField, skillFilePath));
        }
    }

    private static void ValidateSkillSize(
        string rawContent,
        List<SkillValidationDiagnostic> diagnostics,
        string skillFilePath)
    {
        var lineCount = rawContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
        if (lineCount > 500 || rawContent.Length > 20_000)
        {
            diagnostics.Add(CreateDiagnostic(SkillValidationSeverity.Warning, "skill-large", "SKILL.md is unusually large relative to progressive-disclosure guidance.", path: skillFilePath));
        }
    }

    private static SkillValidationDiagnostic CreateDiagnostic(
        SkillValidationSeverity severity,
        string code,
        string message,
        string? fieldName = null,
        string? path = null)
        => new()
        {
            Severity = severity,
            Code = code,
            Message = message,
            FieldName = fieldName,
            Path = path,
        };

    private static SkillCatalogQuery CreateLegacyQuery(
        IReadOnlyList<string> roots,
        bool includeShadowed,
        bool includeInvalid)
    {
        return new SkillCatalogQuery
        {
            Discovery = new SkillDiscoveryContext
            {
                UseBuiltInRoots = false,
                AdditionalRoots = roots
                    .Where(static root => !string.IsNullOrWhiteSpace(root))
                    .Select(static root => new SkillRootRegistration
                    {
                        RootPath = root,
                        SourceKind = SkillSourceKind.Temporary,
                        SourceId = $"temporary:{Path.GetFullPath(root)}",
                        Scope = SkillScopeKind.Temporary,
                        Precedence = 0,
                    })
                    .ToArray(),
            },
            IncludeShadowed = includeShadowed,
            IncludeInvalid = includeInvalid,
            IncludeUntrusted = true,
        };
    }

    private static bool IsUnderRoot(string candidatePath, string rootPath)
    {
        var normalizedCandidate = AppendDirectorySeparator(Path.GetFullPath(candidatePath));
        var normalizedRoot = AppendDirectorySeparator(Path.GetFullPath(rootPath));
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPathDepth(string path)
        => path.Count(static character => character is '\\' or '/');

    private static string NormalizeRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim();
        if (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Relative path must not be rooted.", nameof(relativePath));
        }

        if (normalized.Split('/').Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            throw new ArgumentException("Relative path must not escape the skill directory.", nameof(relativePath));
        }

        return normalized;
    }

    private static string AppendDirectorySeparator(string path)
        => (path.Length > 0 && (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar))
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}


