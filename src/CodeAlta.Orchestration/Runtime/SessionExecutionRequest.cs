using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Immutable execution context and choices captured by <see cref="SessionExecutionPolicy"/>.
/// This is trusted backend input, not renderer authorization or a new tool permission grant.
/// </summary>
public sealed class SessionExecutionRequest
{
    internal SessionExecutionRequest(
        SessionViewKind kind, string? sessionId, string? projectId, string workingDirectory,
        IReadOnlyList<string> projectRoots, ModelProviderId providerId, string providerKey, string? model,
        AgentReasoningEffort? reasoningEffort, string? agentPromptId)
    {
        Kind = kind;
        SessionId = sessionId;
        ProjectId = projectId;
        WorkingDirectory = workingDirectory;
        ProjectRoots = Array.AsReadOnly(projectRoots.ToArray());
        ProviderId = providerId;
        ProviderKey = providerKey;
        Model = model;
        ReasoningEffort = reasoningEffort;
        AgentPromptId = string.IsNullOrWhiteSpace(agentPromptId) ? null : agentPromptId.Trim();
    }

    /// <summary>Gets the requested session scope.</summary>
    public SessionViewKind Kind { get; }

    /// <summary>Gets the captured canonical session id, or null before creation.</summary>
    public string? SessionId { get; }

    /// <summary>Gets the captured project association; global sessions are always unscoped.</summary>
    public string? ProjectId { get; }

    /// <summary>Gets the resolved working directory shared by execution and tool adapters.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets a read-only copy of the active project roots.</summary>
    public IReadOnlyList<string> ProjectRoots { get; }

    /// <summary>Gets the selected provider, without probing or requiring a model catalog.</summary>
    public ModelProviderId ProviderId { get; }

    /// <summary>Gets the captured runtime provider key, retaining stored fallback spelling.</summary>
    public string ProviderKey { get; }

    /// <summary>Gets the captured model preference.</summary>
    public string? Model { get; }

    /// <summary>Gets the captured reasoning preference.</summary>
    public AgentReasoningEffort? ReasoningEffort { get; }

    /// <summary>Gets the normalized agent prompt preference.</summary>
    public string? AgentPromptId { get; }
}
