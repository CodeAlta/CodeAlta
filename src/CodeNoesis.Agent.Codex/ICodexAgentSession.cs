namespace CodeNoesis.Agent.Codex;

/// <summary>
/// Codex-specific agent session contract.
/// </summary>
public interface ICodexAgentSession : IAgentSession
{
    /// <summary>
    /// Gets the Codex thread identifier.
    /// </summary>
    string ThreadId { get; }
}
