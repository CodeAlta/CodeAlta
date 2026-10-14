namespace CodeAlta.Agent.Runtime;

/// <summary>
/// A turn executor whose provider keeps the conversation of a session where other programs continue it (Claude Code: a
/// conversation of the CLI, which VS Code and a terminal resume): it says which conversation a session continues, how
/// to open it there, and whether another program holds it now. Nothing of the provider is started.
/// </summary>
internal interface IAgentProviderSharedConversation
{
    /// <summary>Describes the conversation a session continues, as the state the session recorded names it.</summary>
    /// <param name="state">The state the session recorded.</param>
    /// <param name="workingDirectory">The folder the session works in, where the conversation is resumed from.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>The conversation, or null when the state names none this provider can open elsewhere.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="workingDirectory"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was stopped.</exception>
    Task<AgentSharedConversation?> DescribeSharedConversationAsync(AgentSessionState state, string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>The conversation of a session that other programs of its provider can continue.</summary>
/// <param name="ConversationId">The identifier the provider gives the conversation (the session of the CLI).</param>
/// <param name="ResumeCommand">The command that continues it in a terminal opened in <paramref name="WorkingDirectory"/>.</param>
/// <param name="WorkingDirectory">The folder the conversation is resumed from.</param>
/// <param name="EditorUri">The address that opens it in the editor extension of the provider (VS Code), or null when it has none.</param>
/// <param name="Holder">Another live program that holds the conversation now (CodeAlta's own processes are not), or null.</param>
internal sealed record AgentSharedConversation(
    string ConversationId,
    string ResumeCommand,
    string WorkingDirectory,
    string? EditorUri,
    AgentConversationHolder? Holder);
