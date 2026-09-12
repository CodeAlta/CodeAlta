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

    /// <summary>Gets or initializes the optional authoritative lifecycle observer for this send.</summary>
    /// <remarks>The in-process session awaits this observer outside state gates before permission-capable
    /// work and during closing before source release. Other providers must explicitly support it; an ignored
    /// option supplies no run binding or cancellation guarantee. It never grants permission automatically.</remarks>
    public AgentRunLifecycle? RunLifecycle { get; init; }
}
