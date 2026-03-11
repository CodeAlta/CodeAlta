namespace CodeAlta.Workspaces;

/// <summary>
/// Represents a user-facing scope selector.
/// </summary>
public sealed record ScopeSelector
{
    /// <summary>
    /// Gets the scope kind.
    /// </summary>
    public ScopeKind Kind { get; init; }

    /// <summary>
    /// Gets the optional workspace slug.
    /// </summary>
    public string? WorkspaceSlug { get; init; }

    /// <summary>
    /// Gets the optional project slug.
    /// </summary>
    public string? ProjectSlug { get; init; }

    /// <summary>
    /// Creates a selector for the global scope.
    /// </summary>
    /// <returns>A selector targeting all workspaces.</returns>
    public static ScopeSelector Global() => new() { Kind = ScopeKind.Global };

    /// <summary>
    /// Creates a selector for a workspace slug.
    /// </summary>
    /// <param name="workspaceSlug">The workspace slug.</param>
    /// <returns>A workspace selector.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="workspaceSlug"/> is invalid.</exception>
    public static ScopeSelector Workspace(string workspaceSlug)
    {
        WorkspaceKeyValidator.Validate(workspaceSlug, nameof(workspaceSlug));
        return new ScopeSelector { Kind = ScopeKind.Workspace, WorkspaceSlug = workspaceSlug };
    }

    /// <summary>
    /// Creates a selector for a project slug.
    /// </summary>
    /// <param name="projectSlug">The project slug.</param>
    /// <returns>A project selector.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="projectSlug"/> is invalid.</exception>
    public static ScopeSelector Project(string projectSlug)
    {
        WorkspaceKeyValidator.Validate(projectSlug, nameof(projectSlug));
        return new ScopeSelector { Kind = ScopeKind.Project, ProjectSlug = projectSlug };
    }
}
