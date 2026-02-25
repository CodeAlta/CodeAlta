using CodeNoesis.CodexSdk;

namespace CodeNoesis.Agent.Codex;

/// <summary>
/// Codex-specific agent backend contract exposing the underlying <see cref="CodexClient"/>.
/// </summary>
public interface ICodexAgentBackend : IAgentBackend
{
    /// <summary>
    /// Gets the underlying initialized Codex client.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the backend is not started.</exception>
    CodexClient Client { get; }
}
