namespace CodeAlta.Agent;

/// <summary>
/// Options for steering an already running agent turn.
/// </summary>
public sealed class AgentSteerOptions
{
    /// <summary>
    /// Gets or initializes the input to append to the active run.
    /// </summary>
    public required AgentInput Input { get; init; }

    /// <summary>
    /// Gets or initializes the expected active run identifier.
    /// </summary>
    /// <remarks>
    /// When specified, the adapter should only steer the matching in-flight run.
    /// When omitted, the adapter may use its current active run if the provider supports that behavior.
    /// </remarks>
    public AgentRunId? ExpectedRunId { get; init; }

    /// <summary>
    /// Gets the session of the agent that steers the turn: a parent that redirects its sub-agent, or a peer.
    /// Null for a person or the host. As for <see cref="AgentSendOptions.SourceSessionId"/>, it is recorded with
    /// the user message, so the timeline does not show the message as the user's, and changes nothing of what the
    /// model receives.
    /// </summary>
    public string? SourceSessionId { get; init; }

    /// <summary>
    /// Gets the provenance of a prompt the host steers into the turn, nobody having typed it. Null for a prompt the
    /// user typed. As for <see cref="AgentSendOptions.Provenance"/>, it is recorded with the user message and changes
    /// nothing of what the model receives.
    /// </summary>
    public string? Provenance { get; init; }

    /// <summary>
    /// Gets what is called when the run that accepted the input ended without giving it to the model: the run failed
    /// or was aborted before its next request. The input is then neither in the conversation nor in a later run, and
    /// the caller sends it again (queues it as a prompt of the session). It is called at most once, after the run
    /// released the session, never for an input the run took, and must not wait. A session that cannot tell what its
    /// run took never calls it.
    /// </summary>
    public Action? NotTaken { get; init; }
}
