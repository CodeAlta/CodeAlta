using CodeAlta.Catalog;
using CodeAlta.Catalog.Roles;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Produces optional orchestration instruction overrides for agent sessions.
/// </summary>
public sealed class AgentInstructionTemplateProvider
{
    /// <summary>
    /// Builds the instruction bundle for a coordinator session.
    /// </summary>
    /// <param name="thread">The active work thread.</param>
    /// <param name="project">The owning project, if any.</param>
    /// <param name="profile">The profile providing backend and role-specific defaults.</param>
    /// <returns>
    /// An instruction bundle containing no overrides so backend defaults remain active
    /// while orchestration-specific prompting is disabled.
    /// </returns>
    public AgentInstructionBundle BuildCoordinatorInstructions(
        WorkThreadDescriptor thread,
        ProjectDescriptor? project,
        RoleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(profile);

        return AgentInstructionBundle.Empty;
    }

    /// <summary>
    /// Builds the instruction bundle for a general scoped agent session.
    /// </summary>
    /// <param name="thread">The active work thread.</param>
    /// <param name="project">The owning project, if any.</param>
    /// <param name="profile">The profile providing backend and role-specific defaults.</param>
    /// <returns>
    /// An instruction bundle containing no overrides so backend defaults remain active
    /// while orchestration-specific prompting is disabled.
    /// </returns>
    public AgentInstructionBundle BuildGeneralInstructions(
        WorkThreadDescriptor thread,
        ProjectDescriptor? project,
        RoleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(profile);

        return AgentInstructionBundle.Empty;
    }
}
