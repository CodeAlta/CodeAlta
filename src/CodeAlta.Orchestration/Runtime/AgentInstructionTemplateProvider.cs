using System.Text;
using CodeAlta.Workspaces;
using CodeAlta.Workspaces.Roles;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Composes the canonical CodeAlta instruction bundles for agent sessions.
/// </summary>
public sealed class AgentInstructionTemplateProvider
{
    private const string GeneralBaseInstructions =
        """
        You are a CodeAlta agent. You and the user share the same workspace and collaborate to achieve the user's goals.

        # Personality
        You are a deeply pragmatic, effective software engineer. You take engineering quality seriously and communicate directly and concisely.

        # General
        - Build context from the codebase and provided artifacts before making assumptions.
        - Keep edits focused and non-destructive.
        - Respect dirty worktrees and never revert user changes unless explicitly instructed.
        - Persist until the assigned scoped task is handled end-to-end when feasible.
        - If other work is needed, report it clearly for the host orchestrator instead of launching agents yourself.
        """;

    private const string CoordinatorBaseInstructions =
        """
        You are the CodeAlta Coordinator.

        You are the single top-level planning session in a host-orchestrated coding system. The host orchestrator, not you, launches and supervises worker sessions.

        # Responsibilities
        - Understand the user's request in the current scope.
        - Decide whether the request should be handled directly or through coordinated work.
        - When coordinated work is needed, emit exactly one fenced `codealta_schedule` YAML block.
        - Keep schedules simple, valid, and easy for the host to execute.
        - Do not directly launch or supervise other agents.
        """;

    /// <summary>
    /// Builds the instruction bundle for a coordinator session.
    /// </summary>
    /// <param name="thread">The active work thread.</param>
    /// <param name="workspace">The owning workspace, if any.</param>
    /// <param name="projects">The active projects.</param>
    /// <param name="profile">The profile providing backend and role-specific defaults.</param>
    /// <returns>The composed instruction bundle.</returns>
    public AgentInstructionBundle BuildCoordinatorInstructions(
        WorkThreadDescriptor thread,
        WorkspaceDescriptor? workspace,
        IReadOnlyList<ProjectDescriptor> projects,
        RoleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(profile);

        var developer = new StringBuilder();
        developer.AppendLine("You are operating inside a durable CodeAlta work thread.");
        AppendThreadScope(developer, thread, workspace, projects);
        developer.AppendLine("Scheduling contract:");
        developer.AppendLine("- Answer directly when no coordination is needed.");
        developer.AppendLine("- Emit exactly one fenced `codealta_schedule` YAML block when coordinated work is needed.");
        developer.AppendLine("- Do not include raw schedule content in narrative text outside that fenced block.");
        AppendRoleProfile(developer, profile);

        return new AgentInstructionBundle
        {
            SystemMessage = CoordinatorBaseInstructions,
            DeveloperInstructions = developer.ToString().Trim(),
        };
    }

    /// <summary>
    /// Builds the instruction bundle for a general scoped agent session.
    /// </summary>
    /// <param name="thread">The active work thread.</param>
    /// <param name="workspace">The owning workspace, if any.</param>
    /// <param name="projects">The active projects.</param>
    /// <param name="profile">The profile providing backend and role-specific defaults.</param>
    /// <returns>The composed instruction bundle.</returns>
    public AgentInstructionBundle BuildGeneralInstructions(
        WorkThreadDescriptor thread,
        WorkspaceDescriptor? workspace,
        IReadOnlyList<ProjectDescriptor> projects,
        RoleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(profile);

        var developer = new StringBuilder();
        developer.AppendLine("You are operating inside a durable CodeAlta work thread.");
        AppendThreadScope(developer, thread, workspace, projects);
        developer.AppendLine("You are not the coordinator for this thread.");
        developer.AppendLine("Handle the assigned work directly and report concrete results and blockers.");
        AppendRoleProfile(developer, profile);

        return new AgentInstructionBundle
        {
            SystemMessage = GeneralBaseInstructions,
            DeveloperInstructions = developer.ToString().Trim(),
        };
    }

    private static void AppendThreadScope(
        StringBuilder builder,
        WorkThreadDescriptor thread,
        WorkspaceDescriptor? workspace,
        IReadOnlyList<ProjectDescriptor> projects)
    {
        builder.Append("Thread: ").AppendLine(thread.Title);
        builder.Append("Thread Kind: ").AppendLine(thread.Kind.ToString());

        if (workspace is not null)
        {
            builder.Append("Workspace: ").AppendLine($"{workspace.DisplayName} ({workspace.Slug})");
        }

        if (thread.ScopeMode == WorkThreadScopeMode.AllProjects)
        {
            builder.AppendLine("Project Scope: all projects in the workspace.");
            return;
        }

        if (projects.Count == 0)
        {
            builder.AppendLine("Project Scope: no projects selected yet.");
            return;
        }

        builder.AppendLine("Project Scope:");
        foreach (var project in projects)
        {
            builder.Append("- ").Append(project.DisplayName).Append(" (").Append(project.Slug).AppendLine(")");
        }
    }

    private static void AppendRoleProfile(StringBuilder builder, RoleProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Description))
        {
            builder.Append("Role: ").AppendLine(profile.Description);
        }

        if (profile.ToolsPolicy.Allowed.Count > 0)
        {
            builder.Append("Allowed Tools: ").AppendLine(string.Join(", ", profile.ToolsPolicy.Allowed));
        }

        if (!string.IsNullOrWhiteSpace(profile.Instructions))
        {
            builder.AppendLine("Role Instructions:");
            builder.AppendLine(profile.Instructions.Trim());
        }
    }
}
