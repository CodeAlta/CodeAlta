using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Captures portable session execution policy from explicit, trusted catalog and preference inputs.
/// Performs no filesystem, provider, or discovery I/O. Callers must authorize renderer requests
/// before supplying descriptors or roots; this policy does not grant access or approve tools.
/// </summary>
public static class SessionExecutionPolicy
{
    /// <summary>Captures draft choices with an explicit project, or an unscoped global directory.</summary>
    /// <exception cref="ArgumentNullException">The roots are null.</exception>
    /// <exception cref="ArgumentException">The directory is empty, or roots lack an explicit project.</exception>
    public static SessionExecutionRequest CapturePreferred(
        ModelProviderId providerId, string workingDirectory, IReadOnlyList<string> projectRoots,
        ProjectDescriptor? project, string? model, AgentReasoningEffort? reasoningEffort, string? agentPromptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(projectRoots);
        if (project is null && projectRoots.Count != 0)
        {
            throw new ArgumentException("Project roots require an explicit project descriptor.", nameof(project));
        }

        return new SessionExecutionRequest(
            project is null ? SessionViewKind.GlobalSession : SessionViewKind.ProjectSession,
            null, project?.Id, workingDirectory, projectRoots, providerId, providerId.Value, model, reasoningEffort, agentPromptId);
    }

    /// <summary>
    /// Captures an existing session using its own resolved project, never a selected-tab fallback.
    /// A missing project retains the stored working directory without project overlays.
    /// </summary>
    /// <exception cref="ArgumentNullException">The session is null.</exception>
    /// <exception cref="ArgumentException">The global root or fallback provider key is empty, or the resolved project does not match the session reference.</exception>
    public static SessionExecutionRequest CaptureSession(
        SessionViewDescriptor session, ProjectDescriptor? resolvedProject, string globalRoot,
        ModelProviderId providerOverride, string? model, AgentReasoningEffort? reasoningEffort, string? agentPromptOverride)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRoot);
        if (resolvedProject is not null && !string.Equals(session.ProjectRef, resolvedProject.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The resolved project must match the session's project reference.", nameof(resolvedProject));
        }

        var isGlobal = session.Kind == SessionViewKind.GlobalSession;
        var providerKey = providerOverride.IsEmpty ? session.ResolvedProviderKey : providerOverride.Value;
        var workingDirectory = session.Kind switch
        {
            SessionViewKind.GlobalSession => globalRoot,
            SessionViewKind.ProjectSession when resolvedProject is not null => resolvedProject.ProjectPath,
            _ => session.WorkingDirectory,
        };
        return new SessionExecutionRequest(
            session.Kind, session.SessionId, isGlobal ? null : session.ProjectRef, workingDirectory,
            !isGlobal && resolvedProject is not null ? [resolvedProject.ProjectPath] : [],
            new ModelProviderId(providerKey), providerKey, model, reasoningEffort, agentPromptOverride ?? session.AgentPromptId);
    }

    /// <summary>Assembles options from captured policy and host-specific tools and interaction adapters.</summary>
    /// <exception cref="ArgumentNullException">The request or permission handler is null.</exception>
    public static SessionExecutionOptions BuildOptions(
        SessionExecutionRequest request, IReadOnlyList<AgentToolDefinition>? tools,
        AgentPermissionRequestHandler onPermissionRequest, AgentUserInputRequestHandler? onUserInputRequest)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onPermissionRequest);
        return new SessionExecutionOptions
        {
            ProviderId = request.ProviderId,
            ProviderKey = request.ProviderKey,
            WorkingDirectory = request.WorkingDirectory,
            ProjectRoots = request.ProjectRoots,
            Model = request.Model,
            ReasoningEffort = request.ReasoningEffort,
            AgentPromptId = request.AgentPromptId,
            Tools = tools is null ? null : Array.AsReadOnly(tools.ToArray()),
            OnPermissionRequest = onPermissionRequest,
            OnUserInputRequest = onUserInputRequest,
        };
    }
}
