namespace CodeAlta.Catalog;

/// <summary>
/// Represents a fully-resolved project scope.
/// </summary>
public sealed record ProjectScopeResolution
{
    /// <summary>
    /// Gets the scope kind.
    /// </summary>
    public required ScopeKind Kind { get; init; }

    /// <summary>
    /// Gets the selected project when the scope targets a single project.
    /// </summary>
    public ProjectDescriptor? SelectedProject { get; init; }

    /// <summary>
    /// Gets the resolved projects.
    /// </summary>
    public required IReadOnlyList<ResolvedProject> Projects { get; init; }

    /// <summary>
    /// Gets all relevant <c>.alta</c> roots for this scope.
    /// </summary>
    public required IReadOnlyList<string> CodeAltaRoots { get; init; }
}
