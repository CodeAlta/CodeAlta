using System.ComponentModel;
using CodeAlta.Catalog;
using ModelContextProtocol.Server;

namespace CodeAlta.Mcp.Tools;

/// <summary>
/// MCP tools for project catalog and scope resolution operations.
/// </summary>
[McpServerToolType]
public sealed class ProjectsTools
{
    private readonly ProjectCatalog _catalog;
    private readonly ProjectResolver _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectsTools"/> class.
    /// </summary>
    /// <param name="catalog">Project catalog.</param>
    /// <param name="resolver">Project resolver.</param>
    /// <exception cref="ArgumentNullException">Thrown when required dependencies are <see langword="null"/>.</exception>
    public ProjectsTools(ProjectCatalog catalog, ProjectResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);

        _catalog = catalog;
        _resolver = resolver;
    }

    /// <summary>
    /// Lists known projects.
    /// </summary>
    [McpServerTool(Name = "codealta.projects.list"), Description("Lists all known projects.")]
    public async Task<string> ListAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _catalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        return McpToolJson.Serialize(projects.Select(static project => new
        {
            projectId = project.ProjectId.ToString(),
            slug = project.Slug,
            name = project.Name,
            displayName = project.DisplayName,
            path = project.ProjectPath,
            defaultBranch = project.DefaultBranch,
        }).ToArray());
    }

    /// <summary>
    /// Gets a project by slug.
    /// </summary>
    [McpServerTool(Name = "codealta.projects.get"), Description("Gets a project descriptor by slug.")]
    public async Task<string> GetAsync(
        [Description("Project slug.")] string projectSlug,
        CancellationToken cancellationToken = default)
    {
        var project = await _catalog.GetBySlugAsync(projectSlug, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            throw new InvalidOperationException($"Project '{projectSlug}' was not found.");
        }

        return McpToolJson.Serialize(
            new
            {
                projectId = project.ProjectId.ToString(),
                slug = project.Slug,
                name = project.Name,
                displayName = project.DisplayName,
                path = project.ProjectPath,
                defaultBranch = project.DefaultBranch,
                description = project.Description,
            });
    }

    /// <summary>
    /// Resolves a scope selector into concrete checkout and .alta roots.
    /// </summary>
    [McpServerTool(Name = "codealta.projects.resolve_scope"), Description("Resolves a scope selector into concrete project roots.")]
    public async Task<string> ResolveScopeAsync(
        [Description("Scope kind: global|project.")] string kind,
        [Description("Project slug for project scope.")] string? projectSlug = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        CancellationToken cancellationToken = default)
    {
        var selector = ParseSelector(kind, projectSlug);
        MachineProfile? machineProfile = null;
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            machineProfile = await _catalog.LoadMachineProfileAsync(machineId, cancellationToken).ConfigureAwait(false);
        }

        var resolutions = await _resolver.ResolveAsync(
            selector,
            machineProfile,
            cancellationToken).ConfigureAwait(false);

        return McpToolJson.Serialize(resolutions.Select(static resolution => new
        {
            kind = resolution.Kind.ToString().ToLowerInvariant(),
            selectedProject = resolution.SelectedProject is null
                ? null
                : new
                {
                    projectId = resolution.SelectedProject.ProjectId.ToString(),
                    slug = resolution.SelectedProject.Slug,
                    name = resolution.SelectedProject.Name,
                    displayName = resolution.SelectedProject.DisplayName,
                },
            projects = resolution.Projects.Select(static project => new
            {
                projectId = project.Project.ProjectId.ToString(),
                slug = project.Project.Slug,
                name = project.Project.Name,
                displayName = project.Project.DisplayName,
                path = project.Project.ProjectPath,
                checkoutPath = project.CheckoutPath,
                codeAltaRoot = project.CodeAltaRoot,
            }).ToArray(),
            codeAltaRoots = resolution.CodeAltaRoots,
        }).ToArray());
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
