namespace CodeAlta.Agent.Runtime;

/// <summary>
/// A turn executor whose provider keeps conversations that other programs start (Claude Code: the sessions started in
/// VS Code or by the CLI in a terminal): it lists those that no session of CodeAlta continues, with the folders they
/// started and ended in, and reads one whole to import it as a new session, or describes one to open it in those
/// programs. Nothing of the provider is started; the conversations are only read.
/// </summary>
/// <remarks>
/// The provider lists the conversations of every folder; which project shows one is decided by CodeAlta from those
/// folders. A conversation is then named by its identifier alone.
/// </remarks>
internal interface IAgentProviderExternalConversations
{
    /// <summary>Lists the conversations started outside CodeAlta, in every folder, the most recently changed first.</summary>
    /// <param name="isKnown">Whether a session of CodeAlta already continues the conversation of a given identifier: such a one is not listed.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>
    /// The conversations, with the folders they started and ended in; whether another program holds one is not looked up
    /// (see <see cref="FindExternalConversationHoldersAsync"/>).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="isKnown"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The listing was stopped.</exception>
    Task<IReadOnlyList<AgentExternalConversation>> ListExternalConversationsAsync(Func<string, bool> isKnown, CancellationToken cancellationToken);

    /// <summary>Tells which conversations of a listing a live program holds now (open in VS Code, a CLI in a terminal).</summary>
    /// <param name="conversations">Conversations of a listing: the page shown.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>The same conversations, in the same order, with <see cref="AgentExternalConversation.IsHeld"/> and its status.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="conversations"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The request was stopped.</exception>
    Task<IReadOnlyList<AgentExternalConversation>> FindExternalConversationHoldersAsync(IReadOnlyList<AgentExternalConversation> conversations,
        CancellationToken cancellationToken);

    /// <summary>Reads a whole conversation started outside CodeAlta, to write it in the journal of a new session.</summary>
    /// <param name="conversationId">The identifier of the conversation, as the listing gave it.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The conversation, or null when it is not one the listing gives (missing, started by a program, empty).</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    Task<AgentExternalConversationImport?> ReadExternalConversationAsync(string conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a whole conversation started outside CodeAlta to show it, without importing it: as
    /// <see cref="ReadExternalConversationAsync"/> reads it, for a transcript of at most <paramref name="maxBytes"/>. The
    /// last reads are kept while their transcript does not change: the pages of one preview read it once.
    /// </summary>
    /// <param name="conversationId">The identifier of the conversation, as the listing gave it.</param>
    /// <param name="maxBytes">The largest transcript read; a larger one is <see cref="AgentExternalConversationPreviewStatus.TooLarge"/>.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The conversation and the revision of its transcript, or why there is none to show.</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    Task<AgentExternalConversationPreview> PreviewExternalConversationAsync(string conversationId, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Describes a conversation started outside CodeAlta, to open it in those programs (VS Code, the CLI in a terminal):
    /// the command and the address that open it, and the program that holds it now. Only a conversation the listing
    /// gives is described.
    /// </summary>
    /// <param name="conversationId">The identifier of the conversation, as the listing gave it.</param>
    /// <param name="workingDirectory">The folder the command that opens it is run in.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>The conversation, or null when it is not one the listing gives.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was stopped.</exception>
    Task<AgentSharedConversation?> DescribeExternalConversationAsync(string conversationId, string workingDirectory, CancellationToken cancellationToken);
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
/// <param name="StartDirectory">
/// The folder the conversation started in, as a path of this system when it is one; a folder of another system (WSL) is
/// given as written.
/// </param>
/// <param name="EndDirectory">The folder the conversation was last at work in; the start folder when it never left it.</param>
internal sealed record AgentExternalConversation(
    string ConversationId,
    string Title,
    DateTimeOffset LastChangedAt,
    AgentExternalTurnOrigin Origin,
    string? Entrypoint,
    bool IsHeld,
    string? HolderStatus = null,
    string? StartDirectory = null,
    string? EndDirectory = null);

/// <summary>A whole conversation started outside CodeAlta, to import as a new session.</summary>
/// <param name="Conversation">The conversation as a listing shows it.</param>
/// <param name="Turns">
/// Its turns, with <see cref="AgentExternalTurnsStatus.Read"/>, and the provider state of the new session after the
/// first turns are written: what the next prompt resumes the conversation with.
/// </param>
internal sealed record AgentExternalConversationImport(AgentExternalConversation Conversation, AgentExternalTurns Turns);

/// <summary>What a read of a conversation started outside CodeAlta, to show it, found.</summary>
internal enum AgentExternalConversationPreviewStatus
{
    /// <summary>The conversation was read.</summary>
    Read,

    /// <summary>The listing does not give the conversation (missing, started by a program, empty).</summary>
    NotFound,

    /// <summary>The transcript is larger than what a preview reads.</summary>
    TooLarge,
}

/// <summary>A conversation started outside CodeAlta, read to show it.</summary>
/// <param name="Status">What the read found.</param>
/// <param name="Conversation">The conversation, with <see cref="AgentExternalConversationPreviewStatus.Read"/>.</param>
/// <param name="Length">The length of the transcript read, in bytes; 0 when none was found.</param>
/// <param name="LastWriteTimeUtc">When the transcript read was last written.</param>
internal sealed record AgentExternalConversationPreview(AgentExternalConversationPreviewStatus Status, AgentExternalConversationImport? Conversation,
    long Length, DateTime LastWriteTimeUtc)
{
    /// <summary>Nothing to show.</summary>
    public static AgentExternalConversationPreview NotFound { get; } = new(AgentExternalConversationPreviewStatus.NotFound, null, 0, default);
}
