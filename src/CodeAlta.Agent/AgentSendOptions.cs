namespace CodeAlta.Agent;

/// <summary>
/// Options for starting a new run in a session.
/// </summary>
public sealed class AgentSendOptions
{
    /// <summary>
    /// Gets or initializes the input to send.
    /// </summary>
    public required AgentInput Input { get; init; }

    /// <summary>
    /// Gets the optional ask identifier associated with this user prompt.
    /// </summary>
    public string? AskId { get; init; }

    /// <summary>
    /// Gets the session of the agent that sends this prompt: a parent that gives work to a sub-agent, or a peer.
    /// Null for a prompt of a person or of the host. It is recorded with the user message, and changes nothing
    /// of what the model receives.
    /// </summary>
    public string? SourceSessionId { get; init; }

    /// <summary>
    /// Gets or initializes an optional permission callback for this send.
    /// </summary>
    /// <remarks>
    /// The in-process <see cref="Runtime.AgentSession"/> uses this callback when constructing this
    /// send's built-in tools, falling back to <see cref="AgentSessionCreateOptions.OnPermissionRequest"/>
    /// when null. Custom tool definitions and user-input handling are unchanged. Other provider session
    /// implementations must explicitly support this option; setting it does not replace their existing
    /// session-level callback automatically. This option selects a callback only: it provides no automatic
    /// lifecycle cancellation, approval, stale-callback rejection, or recovery. Retained tool definitions
    /// retain their selected callback after the send returns.
    /// </remarks>
    public AgentPermissionRequestHandler? OnPermissionRequest { get; init; }

    /// <summary>Gets the optional user-input callback selected when constructing this send's built-in tools.</summary>
    /// <remarks>Null falls back to the session callback. Retained tools retain this selection; custom
    /// tools are unchanged. Other providers must explicitly support this option and the run lifecycle.
    /// Selection alone confers no authority, cancellation or recovery guarantee.</remarks>
    public AgentUserInputRequestHandler? OnUserInputRequest { get; init; }

    /// <summary>Gets explicit activation of the user-input built-in for this send; defaults to false.</summary>
    /// <remarks>Requires a selected callback and respects provider profile opt-out. Neither callback presence
    /// nor a profile override activates the tool. Other providers must explicitly support this option.</remarks>
    public bool EnableUserInputTool { get; init; }

    /// <summary>Gets or initializes the optional authoritative lifecycle observer for this send.</summary>
    /// <remarks>The in-process session awaits this observer outside state gates before permission-capable
    /// work and during closing before source release. Other providers must explicitly support it; an ignored
    /// option supplies no run binding or cancellation guarantee. It never grants permission automatically.</remarks>
    public AgentRunLifecycle? RunLifecycle { get; init; }

    /// <summary>Gets additional tools used only by this send, empty by default.</summary>
    /// <remarks>The in-process session rejects collisions with actual registered tool aliases.
    /// Session tools are not replaced. Other session implementations must explicitly support this
    /// option. Tool handlers must enforce their own lifetime; this option confers no authority.</remarks>
    public IReadOnlyList<AgentToolDefinition>? AdditionalTools { get; init; }
}
