using System.ComponentModel;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using ModelContextProtocol.Server;

namespace CodeAlta.Mcp.Tools;

/// <summary>
/// MCP tools for skill discovery and retrieval.
/// </summary>
[McpServerToolType]
public sealed class SkillsTools
{
    private readonly ProjectCatalog _catalog;
    private readonly ProjectResolver _resolver;
    private readonly SkillCatalog _skills;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillsTools"/> class.
    /// </summary>
    public SkillsTools(ProjectCatalog catalog, ProjectResolver resolver, SkillCatalog skills)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(skills);

        _catalog = catalog;
        _resolver = resolver;
        _skills = skills;
    }

    /// <summary>
    /// Lists discovered skills for the provided scope.
    /// </summary>
    [McpServerTool(Name = "codealta.skills.list"), Description("Lists discovered skills under a scope.")]
    public async Task<string> ListAsync(
        [Description("Scope kind: global|project|combined.")] string kind = "combined",
        [Description("Project slug for project scope.")] string? projectSlug = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        [Description("Whether to include user-level roots when using project scope.")] bool includeUserRoots = false,
        [Description("Whether to include invalid skills.")] bool includeInvalid = true,
        [Description("Whether to include shadowed skills.")] bool includeShadowed = true,
        [Description("Whether to include only model-visible skills.")] bool modelVisibleOnly = false,
        CancellationToken cancellationToken = default)
    {
        var query = await ResolveSkillQueryAsync(
                kind,
                projectSlug,
                machineId,
                includeUserRoots,
                includeInvalid,
                includeShadowed,
                modelVisibleOnly,
                cancellationToken)
            .ConfigureAwait(false);
        var skills = await _skills.ListAsync(query, cancellationToken).ConfigureAwait(false);

        return McpToolJson.Serialize(skills.Select(ToDescriptorPayload).ToArray());
    }

    /// <summary>
    /// Gets a skill by name and returns raw <c>SKILL.md</c> content.
    /// </summary>
    [McpServerTool(Name = "codealta.skills.get"), Description("Gets a skill SKILL.md by skill name.")]
    public async Task<string> GetAsync(
        [Description("Skill name.")] string skillName,
        [Description("Scope kind: global|project|combined.")] string kind = "combined",
        [Description("Project slug for project scope.")] string? projectSlug = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        [Description("Whether to include user-level roots when using project scope.")] bool includeUserRoots = false,
        CancellationToken cancellationToken = default)
    {
        var query = await ResolveSkillQueryAsync(
                kind,
                projectSlug,
                machineId,
                includeUserRoots,
                includeInvalid: true,
                includeShadowed: true,
                modelVisibleOnly: false,
                cancellationToken)
            .ConfigureAwait(false);
        var skill = await _skills.GetAsync(query, skillName, cancellationToken).ConfigureAwait(false);
        if (skill is null)
        {
            throw new InvalidOperationException($"Skill '{skillName}' was not found.");
        }

        return McpToolJson.Serialize(
            new
            {
                descriptor = ToDescriptorPayload(skill.Descriptor),
                name = skill.Name,
                skillRootPath = skill.Path,
                skillMdPath = skill.Descriptor.SkillFilePath,
                rawFrontmatter = skill.RawFrontmatter,
                rawContent = skill.RawContent,
                body = skill.Body,
                content = skill.Content,
                frontmatter = new
                {
                    name = skill.Frontmatter.Name,
                    description = skill.Frontmatter.Description,
                    license = skill.Frontmatter.License,
                    compatibility = skill.Frontmatter.Compatibility,
                    metadata = skill.Frontmatter.Metadata,
                    allowedTools = skill.Frontmatter.AllowedTools,
                    unknownTopLevelFields = skill.Frontmatter.UnknownTopLevelFields,
                },
            });
    }

    /// <summary>
    /// Reads a resource file under a skill root using safe relative-path rules.
    /// </summary>
    [McpServerTool(Name = "codealta.skills.get_resource"), Description("Reads a resource file under a skill root.")]
    public async Task<string> GetResourceAsync(
        [Description("Skill name.")] string skillName,
        [Description("Relative path under the skill root.")] string relativePath,
        [Description("Scope kind: global|project|combined.")] string kind = "combined",
        [Description("Project slug for project scope.")] string? projectSlug = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        [Description("Whether to include user-level roots when using project scope.")] bool includeUserRoots = false,
        CancellationToken cancellationToken = default)
    {
        var query = await ResolveSkillQueryAsync(
                kind,
                projectSlug,
                machineId,
                includeUserRoots,
                includeInvalid: true,
                includeShadowed: true,
                modelVisibleOnly: false,
                cancellationToken)
            .ConfigureAwait(false);
        var resource = await _skills.ReadResourceAsync(query, skillName, relativePath, cancellationToken).ConfigureAwait(false);
        var text = TryDecodeUtf8(resource.Content);

        return McpToolJson.Serialize(
            new
            {
                descriptor = ToDescriptorPayload(resource.Descriptor),
                relativePath = resource.RelativePath,
                fullPath = resource.FullPath,
                text,
                contentBase64 = Convert.ToBase64String(resource.Content),
            });
    }

    /// <summary>
    /// Validates one skill or a scope and returns structured diagnostics.
    /// </summary>
    [McpServerTool(Name = "codealta.skills.validate"), Description("Validates skills and returns structured diagnostics.")]
    public async Task<string> ValidateAsync(
        [Description("Optional skill name filter.")] string? skillName = null,
        [Description("Scope kind: global|project|combined.")] string kind = "combined",
        [Description("Project slug for project scope.")] string? projectSlug = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        [Description("Whether to include user-level roots when using project scope.")] bool includeUserRoots = false,
        CancellationToken cancellationToken = default)
    {
        var query = await ResolveSkillQueryAsync(
                kind,
                projectSlug,
                machineId,
                includeUserRoots,
                includeInvalid: true,
                includeShadowed: true,
                modelVisibleOnly: false,
                cancellationToken)
            .ConfigureAwait(false);
        query = query with { SkillName = string.IsNullOrWhiteSpace(skillName) ? null : skillName.Trim() };
        var descriptors = await _skills.ValidateAsync(query, cancellationToken).ConfigureAwait(false);

        return McpToolJson.Serialize(
            descriptors.Select(static descriptor => new
            {
                descriptor = ToDescriptorPayload(descriptor),
                diagnostics = descriptor.Diagnostics.Select(static diagnostic => new
                {
                    severity = diagnostic.Severity.ToString(),
                    code = diagnostic.Code,
                    message = diagnostic.Message,
                    fieldName = diagnostic.FieldName,
                    path = diagnostic.Path,
                }).ToArray(),
            }).ToArray());
    }

    /// <summary>
    /// Activates a skill and returns the canonical activation payload.
    /// </summary>
    [McpServerTool(Name = "codealta.skills.activate"), Description("Activates a skill and returns the canonical payload.")]
    public async Task<string> ActivateAsync(
        [Description("Skill name.")] string skillName,
        [Description("Scope kind: global|project|combined.")] string kind = "combined",
        [Description("Project slug for project scope.")] string? projectSlug = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        [Description("Whether to include user-level roots when using project scope.")] bool includeUserRoots = false,
        CancellationToken cancellationToken = default)
    {
        var query = await ResolveSkillQueryAsync(
                kind,
                projectSlug,
                machineId,
                includeUserRoots,
                includeInvalid: true,
                includeShadowed: true,
                modelVisibleOnly: false,
                cancellationToken)
            .ConfigureAwait(false);
        var activation = await _skills.ActivateAsync(query, skillName, cancellationToken).ConfigureAwait(false);
        if (activation is null)
        {
            throw new InvalidOperationException($"Skill '{skillName}' was not found.");
        }

        return McpToolJson.Serialize(
            new
            {
                descriptor = ToDescriptorPayload(activation.Descriptor),
                skillName = activation.Descriptor.Name,
                source = activation.Descriptor.SourceKind.ToString(),
                sourceId = activation.Descriptor.SourceId,
                skillMdPath = activation.Descriptor.SkillFilePath,
                skillRootPath = activation.Descriptor.SkillRootPath,
                body = activation.Document.Body,
                rawContent = activation.Document.RawContent,
                baseDirectoryUri = activation.BaseDirectoryUri,
                files = activation.Files,
                payload = activation.Payload,
            });
    }

    private async Task<SkillCatalogQuery> ResolveSkillQueryAsync(
        string kind,
        string? projectSlug,
        string? machineId,
        bool includeUserRoots,
        bool includeInvalid,
        bool includeShadowed,
        bool modelVisibleOnly,
        CancellationToken cancellationToken)
    {
        var normalizedKind = kind.Trim().ToLowerInvariant();
        MachineProfile? machineProfile = null;
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            machineProfile = await _catalog.LoadMachineProfileAsync(machineId, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<string> projectRoots = normalizedKind switch
        {
            "project" => await ResolveProjectRootsAsync(ScopeSelector.Project(projectSlug ?? string.Empty), machineProfile, cancellationToken).ConfigureAwait(false),
            "combined" => string.IsNullOrWhiteSpace(projectSlug)
                ? await ResolveProjectRootsAsync(ScopeSelector.Global(), machineProfile, cancellationToken).ConfigureAwait(false)
                : await ResolveProjectRootsAsync(ScopeSelector.Project(projectSlug ?? string.Empty), machineProfile, cancellationToken).ConfigureAwait(false),
            "global" => [],
            _ => throw new ArgumentException("kind must be one of global, project, combined.", nameof(kind)),
        };

        var includeResolvedUserRoots = normalizedKind is "global" or "combined" || includeUserRoots;
        return new SkillCatalogQuery
        {
            Discovery = new SkillDiscoveryContext
            {
                ProjectRoots = projectRoots,
                UserCodeAltaRoot = includeResolvedUserRoots ? _catalog.Options.GlobalRoot : null,
                UserProfileRoot = includeResolvedUserRoots ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : null,
            },
            IncludeInvalid = includeInvalid,
            IncludeShadowed = includeShadowed,
            IncludeUntrusted = true,
            ModelVisibleOnly = modelVisibleOnly,
        };
    }

    private async Task<IReadOnlyList<string>> ResolveProjectRootsAsync(
        ScopeSelector selector,
        MachineProfile? machineProfile,
        CancellationToken cancellationToken)
    {
        var resolutions = await _resolver.ResolveAsync(selector, machineProfile, cancellationToken).ConfigureAwait(false);
        return resolutions
            .SelectMany(static resolution => resolution.Projects)
            .Select(static project => Path.GetFullPath(project.CheckoutPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static object ToDescriptorPayload(SkillDescriptor descriptor)
    {
        return new
        {
            name = descriptor.Name,
            normalizedName = descriptor.NormalizedName,
            title = descriptor.Title,
            description = descriptor.Description,
            skillRootPath = descriptor.SkillRootPath,
            skillMdPath = descriptor.SkillFilePath,
            path = descriptor.SkillFilePath,
            sourceKind = descriptor.SourceKind.ToString(),
            sourceId = descriptor.SourceId,
            scope = descriptor.Scope.ToString(),
            precedence = descriptor.Precedence,
            isShadowed = descriptor.IsShadowed,
            shadowedBySkillMdPath = descriptor.ShadowedBySkillFilePath,
            isTrusted = descriptor.IsTrusted,
            isValid = descriptor.IsValid,
            isModelVisible = descriptor.IsModelVisible,
            frontmatter = new
            {
                name = descriptor.Frontmatter.Name,
                description = descriptor.Frontmatter.Description,
                license = descriptor.Frontmatter.License,
                compatibility = descriptor.Frontmatter.Compatibility,
                metadata = descriptor.Frontmatter.Metadata,
                allowedTools = descriptor.Frontmatter.AllowedTools,
                unknownTopLevelFields = descriptor.Frontmatter.UnknownTopLevelFields,
            },
            diagnostics = descriptor.Diagnostics.Select(static diagnostic => new
            {
                severity = diagnostic.Severity.ToString(),
                code = diagnostic.Code,
                message = diagnostic.Message,
                fieldName = diagnostic.FieldName,
                path = diagnostic.Path,
            }).ToArray(),
        };
    }

    private static string? TryDecodeUtf8(byte[] content)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(content);
            return text.Contains('\0', StringComparison.Ordinal) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}

