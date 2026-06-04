namespace CodeAlta.Catalog.Skills;

/// <summary>
/// Represents skill metadata without loading the body into prompt context.
/// </summary>
public sealed record SkillDescriptor
{
    /// <summary>
    /// Gets the declared skill name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the normalized lookup key.
    /// </summary>
    public required string NormalizedName { get; init; }

    /// <summary>
    /// Gets the display title derived from the body heading when present.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the short routing description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the absolute skill root directory.
    /// </summary>
    public required string SkillRootPath { get; init; }

    /// <summary>
    /// Gets the absolute <c>SKILL.md</c> path.
    /// </summary>
    public required string SkillFilePath { get; init; }

    /// <summary>
    /// Gets the source kind.
    /// </summary>
    public required SkillSourceKind SourceKind { get; init; }

    /// <summary>
    /// Gets the opaque source identifier.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Gets the scope.
    /// </summary>
    public required SkillScopeKind Scope { get; init; }

    /// <summary>
    /// Gets the root precedence where lower values win.
    /// </summary>
    public required int Precedence { get; init; }

    /// <summary>
    /// Gets the parsed frontmatter.
    /// </summary>
    public required SkillFrontmatter Frontmatter { get; init; }

    /// <summary>
    /// Gets the validation diagnostics.
    /// </summary>
    public IReadOnlyList<SkillValidationDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the skill is shadowed by a higher-precedence skill.
    /// </summary>
    public bool IsShadowed { get; init; }

    /// <summary>
    /// Gets the higher-precedence skill path when shadowed.
    /// </summary>
    public string? ShadowedBySkillFilePath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the root is trusted for model advertisement.
    /// </summary>
    public bool IsTrusted { get; init; }

    /// <summary>
    /// Gets a value indicating whether the descriptor passed validation.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Gets a value indicating whether the skill is enabled for the current global/project scope.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether global configuration disables this skill.
    /// </summary>
    public bool IsDisabledGlobally { get; init; }

    /// <summary>
    /// Gets a value indicating whether project configuration disables this skill.
    /// </summary>
    public bool IsDisabledForProject { get; init; }

    /// <summary>
    /// Gets a value indicating whether the skill should be advertised to the model.
    /// </summary>
    public bool IsModelVisible { get; init; }
}
