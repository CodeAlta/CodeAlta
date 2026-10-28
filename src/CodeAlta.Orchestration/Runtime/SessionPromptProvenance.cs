using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Says which <see cref="AgentPromptProvenance"/> a prompt the host sends is recorded with, from what the host knows of
/// it: the provenance its sender names, the kind it is queued or dispatched as, the kind of actor that sent it.
/// </summary>
public static class SessionPromptProvenance
{
    /// <summary>The kind a notification of a sub-agent to its parent is queued and recorded with.</summary>
    public const string ParentNotifyKind = "parent-notify";

    /// <summary>The kind the prompt of a reminder that fired is dispatched and recorded with.</summary>
    public const string ReminderPromptKind = "reminder";

    /// <summary>Gets the provenance of a prompt, or null for a prompt of the user.</summary>
    /// <param name="named">The provenance the sender names, which wins when it is well-formed.</param>
    /// <param name="dispatchKind">The kind the prompt is queued or dispatched as: <c>job</c>, <c>reminder</c>, <c>parent-notify</c>, <c>send</c>...</param>
    /// <param name="actorKind">The kind of actor that sent it: <c>agent</c>, <c>reminder</c>, <c>automation</c>, <c>plugin</c>, <c>user</c>, <c>host</c>...</param>
    /// <returns>The provenance, or null when nothing says the host sent the prompt.</returns>
    public static string? Of(string? named, string? dispatchKind, string? actorKind)
        => AgentPromptProvenance.Normalize(named) ?? OfDispatchKind(dispatchKind) ?? OfActorKind(actorKind);

    /// <summary>Gets the provenance of a queued or dispatched prompt from the actor that submitted it.</summary>
    /// <param name="dispatchKind">The kind the prompt is queued or dispatched as.</param>
    /// <param name="submittedBy">The actor that submitted it, when known; one that acted for the user gives no provenance of its kind.</param>
    /// <returns>The provenance, or null for a prompt of the user.</returns>
    public static string? Of(string? dispatchKind, AltaActorProvenance? submittedBy)
        => Of(submittedBy?.PromptProvenance, dispatchKind, submittedBy is { OnBehalfOfUser: true } ? null : submittedBy?.Kind);

    private static string? OfDispatchKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        SessionRuntimeService.JobPromptKind => AgentPromptProvenance.BackgroundJob,
        ReminderPromptKind => AgentPromptProvenance.Reminder,
        ParentNotifyKind => AgentPromptProvenance.AgentSession,
        _ => null,
    };

    private static string? OfActorKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "agent" => AgentPromptProvenance.AgentSession,
        "reminder" => AgentPromptProvenance.Reminder,
        AltaActorProvenance.AutomationKind => AgentPromptProvenance.Automation,
        AltaActorProvenance.WorkflowKind => AgentPromptProvenance.Workflow,
        "plugin" => AgentPromptProvenance.Plugin,
        _ => null,
    };
}
