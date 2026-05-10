using GitHub.Copilot.SDK;

namespace CodeAlta.Agent.Copilot;

/// <summary>
/// Options used to create a <see cref="CopilotAgentBackend"/>.
/// </summary>
public sealed class CopilotAgentBackendOptions
{
    /// <summary>
    /// Gets or initializes options used to create the underlying <see cref="CopilotClient"/>.
    /// </summary>
    public CopilotClientOptions ClientOptions { get; init; } = new();

    /// <summary>
    /// Gets or initializes options used when CodeAlta installs the pinned Copilot CLI on demand.
    /// This is ignored when <see cref="CopilotClientOptions.CliPath"/> or <see cref="CopilotClientOptions.CliUrl"/> is set.
    /// </summary>
    public CopilotCliInstallOptions? CliInstallOptions { get; init; }
}
