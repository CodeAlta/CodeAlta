namespace CodeAlta.Agent.Claude;

/// <summary>
/// How a conversation of the CLI is opened outside CodeAlta: the command that resumes it in a terminal, and the address
/// the Claude Code extension of VS Code opens it from (its URI handler: path <c>/open</c>, parameter <c>session</c>).
/// </summary>
internal static class ClaudeCodeConversationLinks
{
    /// <summary>
    /// Whether an identifier is one the CLI gives a conversation: a GUID in its <c>D</c> format. Only such an identifier
    /// is put in a command or an address, so that nothing recorded can add words to either.
    /// </summary>
    public static bool IsConversationId(string? conversationId)
        => conversationId is { Length: 36 } && Guid.TryParseExact(conversationId, "D", out _);

    /// <summary>The command that resumes a conversation in a terminal opened in its folder.</summary>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is not an identifier of the CLI.</exception>
    public static string ResumeCommand(string conversationId)
        => $"claude --resume {Checked(conversationId)}";

    /// <summary>The address that opens a conversation in the Claude Code extension of VS Code.</summary>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is not an identifier of the CLI.</exception>
    public static string VsCodeUri(string conversationId)
        => $"vscode://anthropic.claude-code/open?session={Checked(conversationId)}";

    private static string Checked(string conversationId)
        => IsConversationId(conversationId)
            ? conversationId
            : throw new ArgumentException("A conversation of the Claude Code CLI is named by a GUID.", nameof(conversationId));
}
