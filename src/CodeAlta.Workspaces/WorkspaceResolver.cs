namespace CodeAlta.Workspaces;

/// <summary>
/// Resolves scope selectors into concrete project checkouts.
/// </summary>
public sealed class WorkspaceResolver
{
    private readonly WorkspaceCatalog _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceResolver"/> class.
    /// </summary>
    /// <param name="catalog">The workspace catalog.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="catalog"/> is <see langword="null"/>.</exception>
    public WorkspaceResolver(WorkspaceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <summary>
    /// Resolves a selector into concrete workspace resolutions.
    /// </summary>
    /// <param name="selector">The scope selector.</param>
    /// <param name="machineProfile">Optional machine profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved scopes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the selector does not match known workspaces or projects.</exception>
    public async Task<IReadOnlyList<WorkspaceResolution>> ResolveAsync(
        ScopeSelector selector,
        MachineProfile? machineProfile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var workspaces = await _catalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        return selector.Kind switch
        {
            ScopeKind.Global => workspaces
                .Select(workspace => ResolveWorkspace(workspace, machineProfile))
                .ToArray(),
            ScopeKind.Workspace => ResolveWorkspaceBySlug(workspaces, selector.WorkspaceSlug, machineProfile),
            ScopeKind.Project => ResolveWorkspaceByProjectSlug(workspaces, selector.ProjectSlug, machineProfile),
            _ => throw new InvalidOperationException($"Unsupported scope kind '{selector.Kind}'."),
        };
    }

    private static IReadOnlyList<WorkspaceResolution> ResolveWorkspaceBySlug(
        IReadOnlyList<WorkspaceDescriptor> workspaces,
        string? workspaceSlug,
        MachineProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(workspaceSlug))
        {
            throw new InvalidOperationException("Workspace selector is missing a workspace slug.");
        }

        var workspace = workspaces.FirstOrDefault(x =>
            string.Equals(x.Slug, workspaceSlug, StringComparison.OrdinalIgnoreCase));

        if (workspace is null)
        {
            throw new InvalidOperationException($"Workspace '{workspaceSlug}' was not found.");
        }

        return [ResolveWorkspace(workspace, profile)];
    }

    private static IReadOnlyList<WorkspaceResolution> ResolveWorkspaceByProjectSlug(
        IReadOnlyList<WorkspaceDescriptor> workspaces,
        string? projectSlug,
        MachineProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(projectSlug))
        {
            throw new InvalidOperationException("Project selector is missing a project slug.");
        }

        var matching = workspaces
            .SelectMany(workspace => workspace.Projects.Select(project => (workspace, project)))
            .Where(x => string.Equals(x.project.Slug, projectSlug, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matching.Length == 0)
        {
            throw new InvalidOperationException($"Project '{projectSlug}' was not found.");
        }

        return matching
            .Select(x =>
            {
                var resolution = ResolveWorkspace(x.workspace, profile);
                var project = resolution.Projects.Single(y =>
                    string.Equals(y.Project.Slug, x.project.Slug, StringComparison.OrdinalIgnoreCase));

                return new WorkspaceResolution
                {
                    Workspace = x.workspace,
                    Projects = [project],
                    CodeAltaRoots = [project.CodeAltaRoot],
                };
            })
            .ToArray();
    }

    private static WorkspaceResolution ResolveWorkspace(WorkspaceDescriptor workspace, MachineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var baseRoot = workspace.DefaultCheckoutRoot;
        if (profile is not null &&
            profile.WorkspaceCheckoutRoots.TryGetValue(workspace.Slug, out var overrideRoot) &&
            !string.IsNullOrWhiteSpace(overrideRoot))
        {
            baseRoot = overrideRoot;
        }

        var resolvedProjects = new List<ResolvedProject>();
        foreach (var project in workspace.Projects)
        {
            if (profile is not null &&
                profile.ProjectOverrides.TryGetValue(project.Slug, out var overrideValue) &&
                overrideValue.Disabled)
            {
                continue;
            }

            var checkoutPath = ResolveProjectPath(workspace, project, profile, baseRoot);
            resolvedProjects.Add(new ResolvedProject
            {
                Project = project,
                CheckoutPath = checkoutPath,
                CodeAltaRoot = Path.Combine(checkoutPath, ".codealta"),
            });
        }

        return new WorkspaceResolution
        {
            Workspace = workspace,
            Projects = resolvedProjects,
            CodeAltaRoots = resolvedProjects
                .Select(x => x.CodeAltaRoot)
                .ToArray(),
        };
    }

    private static string ResolveProjectPath(
        WorkspaceDescriptor workspace,
        ProjectDescriptor project,
        MachineProfile? profile,
        string baseRoot)
    {
        if (profile is not null &&
            profile.ProjectOverrides.TryGetValue(project.Slug, out var projectOverride) &&
            !string.IsNullOrWhiteSpace(projectOverride.CheckoutPath))
        {
            return Path.GetFullPath(projectOverride.CheckoutPath);
        }

        var pathTemplate = project.Checkout.PathTemplate;
        if (string.IsNullOrWhiteSpace(pathTemplate))
        {
            pathTemplate = "{workspaceKey}\\{projectKey}";
        }

        var context = new PathTemplateContext
        {
            WorkspaceSlug = workspace.Slug,
            ProjectSlug = project.Slug,
            RepoName = GetRepositoryName(project.RepoUrl),
            MachineId = profile?.MachineId ?? string.Empty,
            WorkspaceId = workspace.WorkspaceId,
            ProjectId = project.ProjectId,
            BaseRoot = baseRoot,
        };

        return PathTemplateResolver.Resolve(pathTemplate, context);
    }

    private static string GetRepositoryName(string repoUrl)
    {
        if (Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return Path.GetFileNameWithoutExtension(repoUrl.TrimEnd('/', '\\'));
    }
}
