namespace CodeAlta.Catalog;

/// <summary>
/// Identifies the durable thread type.
/// </summary>
public enum WorkThreadKind
{
    /// <summary>
    /// A user-facing global thread rooted at <c>~/.alta/</c>.
    /// </summary>
    GlobalThread,

    /// <summary>
    /// A user-facing thread scoped to exactly one project.
    /// </summary>
    ProjectThread,

    /// <summary>
    /// A host-owned delegated child thread.
    /// </summary>
    InternalThread,
}

