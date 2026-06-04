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
}
