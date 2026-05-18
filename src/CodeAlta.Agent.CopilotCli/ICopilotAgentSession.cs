using GitHub.Copilot.SDK;

namespace CodeAlta.Agent.CopilotCli;

/// <summary>
/// Copilot-specific session contract exposing the underlying <see cref="CopilotSession"/>.
/// </summary>
public interface ICopilotAgentSession : IAgentSession
{
    /// <summary>
    /// Gets the underlying Copilot session.
    /// </summary>
    CopilotSession Session { get; }
}

