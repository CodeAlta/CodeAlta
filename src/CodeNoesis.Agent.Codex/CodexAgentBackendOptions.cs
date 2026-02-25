using CodeNoesis.CodexSdk;
using CodeNoesis.CodexSdk.V2;
using V2AskForApproval = CodeNoesis.CodexSdk.V2.AskForApproval;

namespace CodeNoesis.Agent.Codex;

/// <summary>
/// Options used to create and start a <see cref="CodexAgentBackend"/>.
/// </summary>
public sealed class CodexAgentBackendOptions
{
    /// <summary>
    /// Gets or initializes the client information used during Codex initialize handshake.
    /// </summary>
    public ClientInfo ClientInfo { get; init; } = new()
    {
        Name = "CodeNoesis.Agent.Codex",
        Title = "CodeNoesis Codex Adapter",
        Version = "1.0.0"
    };

    /// <summary>
    /// Gets or initializes whether to opt into Codex experimental API.
    /// </summary>
    public bool ExperimentalApi { get; init; }

    /// <summary>
    /// Gets or initializes the optional list of notification method names to suppress.
    /// </summary>
    public IReadOnlyList<string>? OptOutNotificationMethods { get; init; }

    /// <summary>
    /// Gets or initializes process startup options for launching Codex.
    /// </summary>
    public CodexProcessOptions? ProcessOptions { get; init; }

    /// <summary>
    /// Gets or initializes the default approval policy used when creating/resuming threads.
    /// </summary>
    public V2AskForApproval ApprovalPolicy { get; init; } = V2AskForApproval.OnRequest;
}
