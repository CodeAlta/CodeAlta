namespace CodeAlta.Agent;

/// <summary>
/// Ends a run whose prompt was never given to the provider: the run waited for another program to release the
/// conversation of the provider (a Claude Code conversation open in VS Code or a terminal), and that program added turns
/// to it meanwhile. The session adds those turns before its next prompt; the prompt of the run is to be sent again.
/// </summary>
/// <remarks>
/// The error record of the run names this type: a window shows the prompt of the run as not sent, and gives it back to
/// its composer.
/// </remarks>
public sealed class AgentConversationChangedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="AgentConversationChangedException"/> class.</summary>
    /// <param name="message">What happened, for the user.</param>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty.</exception>
    public AgentConversationChangedException(string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
    }
}
