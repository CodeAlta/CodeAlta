using System.ComponentModel;
using CodeAlta.Workspaces;
using CodeAlta.Workspaces.Roles;
using ModelContextProtocol.Server;

namespace CodeAlta.Mcp.Tools;

/// <summary>
/// MCP tools for role profile discovery.
/// </summary>
[McpServerToolType]
public sealed class RolesTools
{
    private readonly WorkspaceCatalog _catalog;
    private readonly WorkspaceResolver _resolver;
    private readonly RoleProfileStore _roles;

    /// <summary>
    /// Initializes a new instance of the <see cref="RolesTools"/> class.
    /// </summary>
    public RolesTools(WorkspaceCatalog catalog, WorkspaceResolver resolver, RoleProfileStore roles)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(roles);

        _catalog = catalog;
        _resolver = resolver;
        _roles = roles;
    }

    /// <summary>
    /// Lists discovered role profiles for the provided scope.
    /// </summary>
    [McpServerTool(Name = "codealta.roles.list"), Description("Lists discovered role profiles under a scope.")]
    public async Task<string> ListAsync(
        [Description("Scope kind: global|workspace|project.")] string kind = "global",
        [Description("Workspace key for workspace scope.")] string? workspaceKey = null,
        [Description("Project key for project scope.")] string? projectKey = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        [Description("Whether to include user-level role roots under ~/.codealta/agents.")] bool includeUserRoots = false,
        CancellationToken cancellationToken = default)
    {
        var roots = await ResolveRoleRootsAsync(kind, workspaceKey, projectKey, machineId, includeUserRoots, cancellationToken)
            .ConfigureAwait(false);
        var roles = await _roles.LoadAsync(roots, cancellationToken).ConfigureAwait(false);

        return McpToolJson.Serialize(
            roles.Select(static role => new
            {
                roleId = role.RoleId,
                name = role.Name,
                description = role.Description,
                sourcePath = role.SourcePath,
                defaultBackend = role.DefaultBackend,
                defaultModel = role.DefaultModel,
            }).ToArray());
    }

    /// <summary>
    /// Gets a role profile by id.
    /// </summary>
    [McpServerTool(Name = "codealta.roles.get"), Description("Gets a role profile by id.")]
    public async Task<string> GetAsync(
        [Description("Role id.")] string roleId,
        [Description("Scope kind: global|workspace|project.")] string kind = "global",
        [Description("Workspace key for workspace scope.")] string? workspaceKey = null,
        [Description("Project key for project scope.")] string? projectKey = null,
        [Description("Optional machine id for applying machine profile overrides.")] string? machineId = null,
        [Description("Whether to include user-level role roots under ~/.codealta/agents.")] bool includeUserRoots = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roleId))
        {
            throw new ArgumentException("Role id is required.", nameof(roleId));
        }

        var roots = await ResolveRoleRootsAsync(kind, workspaceKey, projectKey, machineId, includeUserRoots, cancellationToken)
            .ConfigureAwait(false);
        var role = await _roles.GetByIdAsync(roots, roleId.Trim(), cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            throw new InvalidOperationException($"Role '{roleId}' was not found.");
        }

        return McpToolJson.Serialize(
            new
            {
                roleId = role.RoleId,
                name = role.Name,
                description = role.Description,
                instructions = role.Instructions,
                tools = new
                {
                    allowed = role.ToolsPolicy.Allowed,
                    denied = role.ToolsPolicy.Denied,
                },
                sourcePath = role.SourcePath,
            });
    }

    private async Task<IReadOnlyList<string>> ResolveRoleRootsAsync(
        string kind,
        string? workspaceKey,
        string? projectKey,
        string? machineId,
        bool includeUserRoots,
        CancellationToken cancellationToken)
    {
        var selector = ParseSelector(kind, workspaceKey, projectKey);
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
                var checkout = project.CheckoutPath;
                roots.Add(Path.Combine(project.CodeAltaRoot, "roles"));
                roots.Add(Path.Combine(project.CodeAltaRoot, "agents"));
                roots.Add(Path.Combine(checkout, ".github", "agents"));
            }
        }

        if (includeUserRoots)
        {
            roots.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codealta",
                "agents"));
        }

        return roots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ScopeSelector ParseSelector(string kind, string? workspaceKey, string? projectKey)
    {
        return kind.Trim().ToLowerInvariant() switch
        {
            "global" => ScopeSelector.Global(),
            "workspace" => ScopeSelector.Workspace(workspaceKey ?? string.Empty),
            "project" => ScopeSelector.Project(projectKey ?? string.Empty),
            _ => throw new ArgumentException("kind must be one of global, workspace, project.", nameof(kind)),
        };
    }
}
