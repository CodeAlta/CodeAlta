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
        [Description("Scope kind: global|project.")] string kind = "global",
        [Description("Project slug for project scope.")] string? projectSlug = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        [Description("Whether to include user-level skill roots under ~/.alta/skills.")] bool includeUserRoots = false,
        CancellationToken cancellationToken = default)
    {
        var roots = await ResolveSkillRootsAsync(kind, projectSlug, machineId, includeUserRoots, cancellationToken)
            .ConfigureAwait(false);
        var skills = await _skills.ListAsync(roots, cancellationToken).ConfigureAwait(false);

        return McpToolJson.Serialize(
            skills.Select(static skill => new
            {
                name = skill.Name,
                title = skill.Title,
                description = skill.Description,
                path = skill.Path,
            }).ToArray());
    }

    /// <summary>
    /// Gets a skill by name and returns raw <c>SKILL.md</c> content.
    /// </summary>
    [McpServerTool(Name = "codealta.skills.get"), Description("Gets a skill SKILL.md by skill name.")]
    public async Task<string> GetAsync(
        [Description("Skill name (folder name).")] string skillName,
        [Description("Scope kind: global|project.")] string kind = "global",
        [Description("Project slug for project scope.")] string? projectSlug = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        [Description("Whether to include user-level skill roots under ~/.alta/skills.")] bool includeUserRoots = false,
        CancellationToken cancellationToken = default)
    {
        var roots = await ResolveSkillRootsAsync(kind, projectSlug, machineId, includeUserRoots, cancellationToken)
            .ConfigureAwait(false);
        var skill = await _skills.GetAsync(roots, skillName, cancellationToken).ConfigureAwait(false);
        if (skill is null)
        {
            throw new InvalidOperationException($"Skill '{skillName}' was not found.");
        }

        return McpToolJson.Serialize(
            new
            {
                name = skill.Name,
                path = skill.Path,
                content = skill.Content,
            });
    }

    private async Task<IReadOnlyList<string>> ResolveSkillRootsAsync(
        string kind,
        string? projectSlug,
        string? machineId,
        bool includeUserRoots,
        CancellationToken cancellationToken)
    {
        var selector = ParseSelector(kind, projectSlug);
        MachineProfile? machineProfile = null;
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            machineProfile = await _catalog.LoadMachineProfileAsync(machineId, cancellationToken).ConfigureAwait(false);
        }

        var resolutions = await _resolver.ResolveAsync(selector, machineProfile, cancellationToken).ConfigureAwait(false);
        var roots = new List<string>();

        foreach (var resolution in resolutions)
        {
            foreach (var project in resolution.Projects)
            {
                roots.Add(Path.Combine(project.CodeAltaRoot, "skills"));
            }
        }

        if (includeUserRoots)
        {
            roots.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".alta",
                "skills"));
        }

        return roots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ScopeSelector ParseSelector(string kind, string? projectSlug)
    {
        return kind.Trim().ToLowerInvariant() switch
        {
            "global" => ScopeSelector.Global(),
            "project" => ScopeSelector.Project(projectSlug ?? string.Empty),
            _ => throw new ArgumentException("kind must be one of global, project.", nameof(kind)),
        };
    }
}

