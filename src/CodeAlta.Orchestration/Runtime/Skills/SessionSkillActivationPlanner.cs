namespace CodeAlta.Orchestration.Runtime.Skills;

/// <summary>
/// Plans CodeAlta-managed skill activation without frontend status side effects.
/// </summary>
public sealed class SessionSkillActivationPlanner
{
    /// <summary>
    /// Plans whether a skill activation command may run for a session view.
    /// </summary>
    /// <param name="session">The target session snapshot, or <see langword="null"/> when no session is selected.</param>
    /// <param name="isModelProviderReady">Whether the selected model provider/runtime is ready.</param>
    /// <param name="isSessionBusy">Whether the target session is currently busy.</param>
    /// <returns>The skill activation decision.</returns>
    public SessionViewSkillActivationDecision Plan(
        SessionViewDescriptorSnapshot? session,
        bool isModelProviderReady,
        bool isSessionBusy)
    {
        if (session is null)
        {
            return new SessionViewSkillActivationDecision(
                SessionViewSkillActivationDecisionKind.RejectNoSession,
                "Open a local/raw model-provider session before activating a CodeAlta-managed skill.");
        }

        if (!isModelProviderReady)
        {
            return new SessionViewSkillActivationDecision(
                SessionViewSkillActivationDecisionKind.RejectProviderNotReady,
                "The selected model provider is not ready.");
        }

        if (isSessionBusy)
        {
            return new SessionViewSkillActivationDecision(
                SessionViewSkillActivationDecisionKind.RejectSessionBusy,
                $"Wait for '{session.Title}' to become idle before activating a skill.");
        }

        return new SessionViewSkillActivationDecision(SessionViewSkillActivationDecisionKind.Activate);
    }
}

/// <summary>
/// Classifies skill activation planning decisions.
/// </summary>
public enum SessionViewSkillActivationDecisionKind
{
    /// <summary>Run the skill activation command.</summary>
    Activate,

    /// <summary>Reject because no explicit session is available.</summary>
    RejectNoSession,

    /// <summary>Reject because the provider/runtime is not ready.</summary>
    RejectProviderNotReady,

    /// <summary>Reject because the target session is busy.</summary>
    RejectSessionBusy,
}

/// <summary>
/// Describes a skill activation planning decision.
/// </summary>
/// <param name="Kind">The decision kind.</param>
/// <param name="Message">An optional status/diagnostic message.</param>
public sealed record SessionViewSkillActivationDecision(
    SessionViewSkillActivationDecisionKind Kind,
    string? Message = null);
