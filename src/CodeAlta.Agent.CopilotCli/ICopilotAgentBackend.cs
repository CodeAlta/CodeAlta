using GitHub.Copilot.SDK;

namespace CodeAlta.Agent.CopilotCli;

/// <summary>
/// Copilot-specific backend contract exposing the underlying <see cref="CopilotClient"/>.
/// </summary>
public interface ICopilotAgentBackend : IAgentBackend
{
    /// <summary>
    /// Gets the underlying Copilot client.
    /// </summary>
    CopilotClient Client { get; }
}

