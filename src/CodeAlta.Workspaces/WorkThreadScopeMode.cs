namespace CodeAlta.Workspaces;

/// <summary>
/// Describes how a thread focuses projects within its owning workspace.
/// </summary>
public enum WorkThreadScopeMode
{
    /// <summary>
    /// The thread targets a single project.
    /// </summary>
    SingleProject,

    /// <summary>
    /// The thread targets multiple selected projects.
    /// </summary>
    MultiProject,

    /// <summary>
    /// The thread targets all projects in the owning workspace.
    /// </summary>
    AllProjects,
}
