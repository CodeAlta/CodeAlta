namespace CodeAlta.Catalog;

/// <summary>
/// Resolves scope selectors into concrete project checkouts.
/// </summary>
public sealed class ProjectResolver
{
    private readonly ProjectCatalog _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectResolver"/> class.
    /// </summary>
    /// <param name="catalog">The project catalog.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="catalog"/> is <see langword="null"/>.</exception>
    public ProjectResolver(ProjectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <summary>
    /// Resolves a selector into concrete project scope resolutions.
    /// </summary>
    /// <param name="selector">The scope selector.</param>
    /// <param name="machineProfile">Optional machine profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved scopes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the selector does not match known projects.</exception>
    public async Task<IReadOnlyList<ProjectScopeResolution>> ResolveAsync(
        ScopeSelector selector,
        MachineProfile? machineProfile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var projects = await _catalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        return selector.Kind switch
        {
            ScopeKind.Global => [ResolveGlobal(projects, machineProfile, _catalog.Options.CheckoutsRoot)],
            ScopeKind.Project => [ResolveProjectBySlug(projects, selector.ProjectSlug, machineProfile, _catalog.Options.CheckoutsRoot)],
            _ => throw new InvalidOperationException($"Unsupported scope kind '{selector.Kind}'."),
        };
    }

    private static ProjectScopeResolution ResolveGlobal(
        IReadOnlyList<ProjectDescriptor> projects,
        MachineProfile? profile,
        string defaultCheckoutRoot)
    {
        var resolvedProjects = ResolveProjects(projects, profile, defaultCheckoutRoot);
        return new ProjectScopeResolution
        {
            Kind = ScopeKind.Global,
            Projects = resolvedProjects,
            CodeAltaRoots = resolvedProjects.Select(static x => x.CodeAltaRoot).ToArray(),
        };
    }

    private static ProjectScopeResolution ResolveProjectBySlug(
        IReadOnlyList<ProjectDescriptor> projects,
        string? projectSlug,
        MachineProfile? profile,
        string defaultCheckoutRoot)
    {
        if (string.IsNullOrWhiteSpace(projectSlug))
        {
            throw new InvalidOperationException("Project selector is missing a project slug.");
        }

        var project = projects.FirstOrDefault(x =>
            string.Equals(x.Slug, projectSlug, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            throw new InvalidOperationException($"Project '{projectSlug}' was not found.");
        }

        var resolvedProject = ResolveProjects([project], profile, defaultCheckoutRoot).Single();
        return new ProjectScopeResolution
        {
            Kind = ScopeKind.Project,
            SelectedProject = project,
            Projects = [resolvedProject],
            CodeAltaRoots = [resolvedProject.CodeAltaRoot],
        };
    }

    private static IReadOnlyList<ResolvedProject> ResolveProjects(
        IEnumerable<ProjectDescriptor> projects,
        MachineProfile? profile,
        string defaultCheckoutRoot)
    {
        var resolvedProjects = new List<ResolvedProject>();
        foreach (var project in projects)
        {
            if (profile is not null &&
                profile.ProjectOverrides.TryGetValue(project.Slug, out var projectOverride) &&
                projectOverride.Disabled)
            {
                continue;
            }

            var checkoutPath = ResolveProjectPath(project, profile, defaultCheckoutRoot);
            resolvedProjects.Add(new ResolvedProject
            {
                Project = project,
                CheckoutPath = checkoutPath,
                CodeAltaRoot = Path.Combine(checkoutPath, ".alta"),
            });
        }

        return resolvedProjects;
    }

    private static string ResolveProjectPath(
        ProjectDescriptor project,
        MachineProfile? profile,
        string defaultCheckoutRoot)
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
            pathTemplate = "{projectName}";
        }

        var context = new PathTemplateContext
        {
            ProjectSlug = project.Slug,
            ProjectName = project.Name,
            RepoName = GetRepositoryName(project.ProjectPath),
            MachineId = profile?.MachineId ?? string.Empty,
            ProjectId = project.ProjectId,
            BaseRoot = string.IsNullOrWhiteSpace(profile?.CheckoutRoot)
                ? defaultCheckoutRoot
                : profile.CheckoutRoot,
        };

        return PathTemplateResolver.Resolve(pathTemplate, context);
    }

    private static string GetRepositoryName(string projectPath)
    {
        if (Uri.TryCreate(projectPath, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return Path.GetFileName(projectPath.TrimEnd('/', '\\'));
    }
}
