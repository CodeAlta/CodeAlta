using GitHub.Copilot.SDK;

namespace CodeNoesis.Agent.Copilot;

/// <summary>
/// Options used to create a <see cref="CopilotAgentBackend"/>.
/// </summary>
public sealed class CopilotAgentBackendOptions
{
    /// <summary>
    /// Gets or initializes options used to create the underlying <see cref="CopilotClient"/>.
    /// </summary>
    public CopilotClientOptions ClientOptions { get; init; } = new();
}
