namespace CodeAlta.Catalog.Skills;

/// <summary>
/// Controls skill discovery, filtering, and validation.
/// </summary>
public sealed record SkillCatalogQuery
{
    /// <summary>
    /// Gets the discovery context used to resolve roots.
    /// </summary>
    public SkillDiscoveryContext Discovery { get; init; } = new();

    /// <summary>
    /// Gets an optional case-insensitive skill-name filter.
    /// </summary>
    public string? SkillName { get; init; }

    /// <summary>
    /// Gets normalized skill names disabled by global configuration.
    /// </summary>
    public IReadOnlyCollection<string> GlobalDisabledSkillNames { get; init; } = [];

    /// <summary>
    /// Gets normalized skill names disabled by project configuration.
    /// </summary>
    public IReadOnlyCollection<string> ProjectDisabledSkillNames { get; init; } = [];

    /// <summary>
    /// Gets what the space of the session has; <see langword="null"/> to list the skills of every space. A skill the
    /// space does not have is disabled, before and whatever the global and project enablement says, and whatever
    /// folder it comes from: a project cannot bring into a space a skill the space does not have.
    /// </summary>
    public SpaceFilter? Space { get; init; }

    /// <summary>
    /// Gets a value indicating whether disabled skills should be returned.
    /// </summary>
    public bool IncludeDisabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether invalid skills should be returned.
    /// </summary>
    public bool IncludeInvalid { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether shadowed skills should be returned.
    /// </summary>
    public bool IncludeShadowed { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether untrusted skills should be returned.
    /// </summary>
    public bool IncludeUntrusted { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether only model-visible skills should be returned.
    /// </summary>
    public bool ModelVisibleOnly { get; init; }

    /// <summary>
    /// Builds the query of the skills a session can see: those of the user and of its project folders, filtered by
    /// its space, then by the global configuration and by the configuration of its project. Invalid, shadowed and
    /// untrusted skills are listed; what the model is offered narrows it with <see cref="ModelVisibleOnly"/>.
    /// </summary>
    /// <param name="configStore">The configuration store; <see langword="null"/> when the host keeps none, where nothing is disabled.</param>
    /// <param name="userCodeAltaRoot">The CodeAlta folder of the user; <see langword="null"/> to omit its skills.</param>
    /// <param name="userProfileRoot">The profile folder of the user; <see langword="null"/> to omit its common skills.</param>
    /// <param name="projectRoot">The folder of the project of the session, whose configuration disables skills; <see langword="null"/> for none.</param>
    /// <param name="otherProjectRoots">More folders whose skills the session sees; their configuration is not read.</param>
    /// <param name="space">What the space of the session has; <see langword="null"/> to list the skills of every space.</param>
    /// <returns>The query.</returns>
    /// <exception cref="InvalidDataException">A configuration file cannot be parsed.</exception>
    /// <exception cref="IOException">A configuration file cannot be read.</exception>
    public static SkillCatalogQuery ForSession(
        CodeAltaConfigStore? configStore,
        string? userCodeAltaRoot,
        string? userProfileRoot,
        string? projectRoot,
        IEnumerable<string>? otherProjectRoots,
        SpaceFilter? space)
    {
        var roots = new List<string>();
        foreach (var root in (string.IsNullOrWhiteSpace(projectRoot) ? [] : new[] { projectRoot }).Concat(otherProjectRoots ?? []))
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var fullPath = Path.GetFullPath(root);
            if (!roots.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) roots.Add(fullPath);
        }

        return new SkillCatalogQuery
        {
            Discovery = new SkillDiscoveryContext
            {
                ProjectRoots = roots,
                UserCodeAltaRoot = userCodeAltaRoot,
                UserProfileRoot = userProfileRoot,
            },
            Space = space,
            GlobalDisabledSkillNames = (IReadOnlyCollection<string>?)configStore?.LoadGlobalDisabledSkillNames() ?? [],
            ProjectDisabledSkillNames = (IReadOnlyCollection<string>?)configStore?.LoadProjectDisabledSkillNames(string.IsNullOrWhiteSpace(projectRoot) ? roots.FirstOrDefault() : projectRoot) ?? [],
            IncludeInvalid = true,
            IncludeShadowed = true,
            IncludeUntrusted = true,
        };
    }
}
