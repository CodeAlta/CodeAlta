namespace CodeAlta.Agent.Runtime;

/// <summary>
/// A turn executor whose provider keeps conversations that other programs start (Claude Code: the sessions started in
/// VS Code or by the CLI in a terminal): it lists those of a folder that no session of CodeAlta continues, and reads one
/// whole to import it as a new session, or describes one to open it in those programs. Nothing of the provider is
/// started; the conversations are only read.
/// </summary>
internal interface IAgentProviderExternalConversations
{
    /// <summary>Lists the conversations started outside CodeAlta in a folder, the most recently changed first.</summary>
    /// <param name="workingDirectory">The folder the conversations were started in: the folder of a project.</param>
    /// <param name="isKnown">Whether a session of CodeAlta already continues the conversation of a given identifier: such a one is not listed.</param>
    /// <param name="offset">How many of the conversations, in that order, are skipped: the page starts after them.</param>
    /// <param name="count">The most conversations returned; <see cref="AgentExternalConversationList.Total"/> counts them all.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The conversations of the page, newest first, and how many there are in all.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="isKnown"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="workingDirectory"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative, or <paramref name="count"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException">The listing was stopped.</exception>
    Task<AgentExternalConversationList> ListExternalConversationsAsync(string workingDirectory, Func<string, bool> isKnown, int offset, int count,
        CancellationToken cancellationToken);

    /// <summary>Reads a whole conversation started outside CodeAlta, to write it in the journal of a new session.</summary>
    /// <param name="workingDirectory">The folder the conversation was started in.</param>
    /// <param name="conversationId">The identifier of the conversation, as the listing gave it.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>
    /// The conversation, or null when it is not one the listing gives for that folder (missing, started there by a
    /// program, of another folder, empty).
    /// </returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    Task<AgentExternalConversationImport?> ReadExternalConversationAsync(string workingDirectory, string conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Describes a conversation started outside CodeAlta, to open it in those programs (VS Code, the CLI in a terminal):
    /// the command and the address that open it, and the program that holds it now. Only a conversation the listing
    /// gives for that folder is described.
    /// </summary>
    /// <param name="workingDirectory">The folder the conversation was started in.</param>
    /// <param name="conversationId">The identifier of the conversation, as the listing gave it.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>The conversation, or null when it is not one the listing gives for that folder.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was stopped.</exception>
    Task<AgentSharedConversation?> DescribeExternalConversationAsync(string workingDirectory, string conversationId, CancellationToken cancellationToken);
}

/// <summary>A conversation started outside CodeAlta, as a listing shows it.</summary>
/// <param name="ConversationId">The identifier the provider gives the conversation (the session of the CLI).</param>
/// <param name="Title">The title of the conversation, as the program that started it shows it.</param>
/// <param name="LastChangedAt">When the conversation was last written.</param>
/// <param name="Origin">Where it was started.</param>
/// <param name="Entrypoint">The name the provider gives the program that started it, as written.</param>
/// <param name="IsHeld">Whether another live program holds the conversation now (open in VS Code, a CLI in a terminal).</param>
/// <param name="HolderStatus">
/// What the program that holds it says it does (Claude Code: <c>busy</c>, <c>idle</c>, <c>waiting</c>, ...), when it is
/// held and says it; a conversation merely open in a VS Code panel is <c>idle</c>.
/// </param>
internal sealed record AgentExternalConversation(
    string ConversationId,
    string Title,
    DateTimeOffset LastChangedAt,
    AgentExternalTurnOrigin Origin,
    string? Entrypoint,
    bool IsHeld,
    string? HolderStatus = null);

/// <summary>The conversations started outside CodeAlta in a folder.</summary>
/// <param name="Conversations">The conversations of the page asked for, the most recently changed first.</param>
/// <param name="Total">How many conversations there are, also those outside the page.</param>
internal sealed record AgentExternalConversationList(IReadOnlyList<AgentExternalConversation> Conversations, int Total)
{
    /// <summary>No conversation.</summary>
    public static AgentExternalConversationList Empty { get; } = new([], 0);
}

/// <summary>A whole conversation started outside CodeAlta, to import as a new session.</summary>
/// <param name="Conversation">The conversation as a listing shows it.</param>
/// <param name="Turns">
/// Its turns, with <see cref="AgentExternalTurnsStatus.Read"/>, and the provider state of the new session after the
/// first turns are written: what the next prompt resumes the conversation with.
/// </param>
internal sealed record AgentExternalConversationImport(AgentExternalConversation Conversation, AgentExternalTurns Turns);
