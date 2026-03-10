namespace CodeAlta.Workspaces;

/// <summary>
/// Identifies the durable thread type.
/// </summary>
public enum WorkThreadKind
{
    /// <summary>
    /// The global cross-workspace thread.
    /// </summary>
    Global,

    /// <summary>
    /// A workspace-owned thread.
    /// </summary>
    WorkspaceThread,
}
