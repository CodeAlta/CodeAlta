using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

/// <summary>Where a turn of a Claude Code conversation was made, from the <c>entrypoint</c> the CLI writes.</summary>
internal enum ClaudeCodeTranscriptOrigin
{
    /// <summary>The line names no entrypoint.</summary>
    Unknown,

    /// <summary>The Claude Code extension of VS Code (<c>claude-vscode</c>).</summary>
    VsCode,

    /// <summary>The CLI in a terminal (<c>cli</c>).</summary>
    Terminal,

    /// <summary>A program through the SDK (<c>sdk-cli</c>, <c>sdk-ts</c>, <c>sdk-py</c>): CodeAlta is one.</summary>
    Programmatic,

    /// <summary>Another application; the entrypoint says which.</summary>
    Other,
}

/// <summary>How a read of a transcript ended.</summary>
internal enum ClaudeCodeTranscriptReadStatus
{
    /// <summary>The transcript was read: the turns are those after the entry asked for, or all of them.</summary>
    Read,

    /// <summary>Nothing was written since the position given: no turns.</summary>
    Unchanged,

    /// <summary>
    /// The entry asked for is not an ancestor of the current leaf (the conversation was rewound or resumed at an
    /// earlier point): no turns.
    /// </summary>
    CannotFollow,

    /// <summary>
    /// The entry asked for is not in the transcript, and no entry names it as its parent: the CLI never wrote it, and
    /// nothing written tells where the conversation went on from it. No turns.
    /// </summary>
    LeafNotFound,

    /// <summary>The transcript does not exist or cannot be read (also a file over 2 GB).</summary>
    Missing,
}

/// <summary>A compaction of the conversation, as its <c>compact_boundary</c> line describes it.</summary>
/// <param name="Trigger">What started it (<c>auto</c>, <c>manual</c>), when written.</param>
/// <param name="PreTokens">The tokens of the context before, when written.</param>
/// <param name="PostTokens">The tokens of the context after, when written.</param>
internal sealed record ClaudeCodeTranscriptCompaction(string? Trigger, long? PreTokens, long? PostTokens);

/// <summary>
/// A turn of a Claude Code conversation: a user prompt and what followed it, or a compaction.
/// </summary>
/// <remarks>
/// <see cref="Messages"/> are in the shapes the session of CodeAlta records: the prompt is one
/// <see cref="AgentConversationRole.User"/> message; each model message (all the lines the CLI wrote with one
/// <c>message.id</c>) is one <see cref="AgentConversationRole.Assistant"/> message, tool steps included; each tool
/// result is one <see cref="AgentConversationRole.Tool"/> message with a single
/// <see cref="AgentMessagePart.ToolResult"/>. A turn without a prompt has no user message: one the CLI started by
/// itself (a prompt it wrote with an <c>origin.kind</c> other than <c>human</c>, such as the notification of a
/// background task, whose text is <see cref="TriggerText"/>), or one whose prompt is before the point read from.
/// <para>
/// A command is shown as CodeAlta records one the user types: a slash command as a user message
/// (<c>/name args</c>) and, for a command the CLI runs itself, its output as an assistant message; a shell command
/// (<c>!</c>) as one user message with the command and its output, which the model is given as context.
/// </para>
/// </remarks>
/// <param name="Messages">The messages of the turn, in order; empty for a compaction.</param>
/// <param name="Compaction">The compaction this entry is, or null for a turn of messages.</param>
/// <param name="Entrypoint">The <c>entrypoint</c> of the first line of the turn, as written.</param>
/// <param name="Origin">Where the turn was made, from <paramref name="Entrypoint"/>.</param>
/// <param name="OriginKind">The <c>origin.kind</c> of the prompt (e.g. <c>human</c>, <c>task-notification</c>), when written.</param>
/// <param name="Timestamp">The time of the first line of the turn, when written.</param>
/// <param name="FirstUuid">The uuid of the first entry of the turn.</param>
/// <param name="LastUuid">The uuid of the last entry of the turn.</param>
/// <param name="TriggerText">For a turn the CLI started by itself: the text of the prompt it wrote; otherwise null.</param>
internal sealed record ClaudeCodeTranscriptTurn(
    IReadOnlyList<AgentConversationMessage> Messages,
    ClaudeCodeTranscriptCompaction? Compaction,
    string? Entrypoint,
    ClaudeCodeTranscriptOrigin Origin,
    string? OriginKind,
    DateTimeOffset? Timestamp,
    string FirstUuid,
    string LastUuid,
    string? TriggerText = null)
{
    // These counts are those of the messages read, which are what a catch-up or an import writes: the provider state
    // it records counts what it wrote. They are not those a turn of CodeAlta counts for the same lines (a block this
    // reader does not know is a placeholder part here and nothing in a turn, and a model message of that block alone
    // counts 1 here and 0 there), so they must never be computed again from the transcript for turns CodeAlta made.

    /// <summary>The user messages of the turn: what the provider state counts as <c>Users</c> for what is written of it.</summary>
    public int Users => Messages.Count(static message => message.Role is AgentConversationRole.User);

    /// <summary>The assistant messages of the turn: what the provider state counts as <c>Assistants</c> for what is written of it.</summary>
    public int Assistants => Messages.Count(static message => message.Role is AgentConversationRole.Assistant);
}

/// <summary>
/// Where a read of a transcript stopped, to read only what is written after it next time. The caller keeps it as it
/// is and gives it back.
/// </summary>
/// <param name="Offset">The byte offset of the end of the last complete line read.</param>
/// <param name="Length">The length of the file when it was read.</param>
/// <param name="LastWriteTime">The last write time of the file when it was read (UTC).</param>
/// <param name="FirstLineHash">A hash of the first line: another one means another file, which is read again whole.</param>
/// <param name="Leaf">The leaf found by that read.</param>
/// <param name="Tail">
/// The newest entry below <paramref name="Leaf"/> (an attachment or a system line the next prompt hangs from), when
/// nothing shown lies between them; null otherwise.
/// </param>
/// <param name="LeafMessageId">
/// The <c>message.id</c> of <paramref name="Leaf"/> when it is an assistant line: more lines of that model message
/// after the position mean the whole file is read, so that the message stays one.
/// </param>
/// <param name="LastLines">
/// The last line of the conversation (not of a subagent) each writer wrote before the position, which a line whose
/// parent the CLI did not write is put below; ordered by entrypoint.
/// </param>
internal sealed record ClaudeCodeTranscriptPosition(
    long Offset,
    long Length,
    DateTime LastWriteTime,
    string FirstLineHash,
    string? Leaf,
    string? Tail,
    string? LeafMessageId = null,
    IReadOnlyList<ClaudeCodeTranscriptLastLine>? LastLines = null);

/// <summary>The last line of the conversation one writer wrote before a position.</summary>
/// <param name="Entrypoint">The writer: the <c>entrypoint</c> of its lines, or null when they name none.</param>
/// <param name="Uuid">The uuid of the line.</param>
/// <param name="HasChild">Whether a line written after it names it as its parent.</param>
internal sealed record ClaudeCodeTranscriptLastLine(string? Entrypoint, string Uuid, bool HasChild);

/// <summary>What a read of a transcript found.</summary>
/// <param name="Status">How the read ended.</param>
/// <param name="SessionId">The session of the CLI: the name of the file.</param>
/// <param name="Turns">The turns read, in order; empty unless <paramref name="Status"/> is <see cref="ClaudeCodeTranscriptReadStatus.Read"/>.</param>
/// <param name="Leaf">
/// The current leaf of the conversation (the uuid of its last user or assistant entry), the one to resume at; the entry
/// given when nothing new was found.
/// </param>
/// <param name="TotalCostUsd">The <c>totalCostUSD</c> of the last <c>cost-state</c> line read, or null when the lines read have none.</param>
/// <param name="Title">The title of the session, by the priority of the CLI.</param>
/// <param name="Entrypoint">The <c>entrypoint</c> of the first entry of the file.</param>
/// <param name="Origin">Where the session was started, from <paramref name="Entrypoint"/>.</param>
/// <param name="Cwd">The <c>cwd</c> of the first entry of the file.</param>
/// <param name="Position">Where the read stopped, to give back to the next read; null when the file is missing.</param>
/// <param name="FullRead">Whether the whole file was read (the position given could not be used, or none was).</param>
internal sealed record ClaudeCodeTranscriptReadResult(
    ClaudeCodeTranscriptReadStatus Status,
    string SessionId,
    IReadOnlyList<ClaudeCodeTranscriptTurn> Turns,
    string? Leaf,
    double? TotalCostUsd,
    string? Title,
    string? Entrypoint,
    ClaudeCodeTranscriptOrigin Origin,
    string? Cwd,
    ClaudeCodeTranscriptPosition? Position,
    bool FullRead)
{
    /// <summary>The user messages of all the turns read.</summary>
    public int Users => Turns.Sum(static turn => turn.Users);

    /// <summary>The assistant messages of all the turns read.</summary>
    public int Assistants => Turns.Sum(static turn => turn.Assistants);
}

/// <summary>
/// What the first and the last 64 KB of a transcript say of its session, read as the session list of VS Code reads them.
/// </summary>
/// <param name="SessionId">The session of the CLI: the name of the file.</param>
/// <param name="Cwd">The first <c>cwd</c> of the file (the one of its first entry).</param>
/// <param name="Entrypoint">The first <c>entrypoint</c> of the head, else the last of the tail.</param>
/// <param name="Origin">Where the session was started, from <paramref name="Entrypoint"/>.</param>
/// <param name="SessionKind">The <c>sessionKind</c> of the first entry of the head, when written.</param>
/// <param name="IsSidechain">Whether the first line is a sidechain (the transcript of a subagent).</param>
/// <param name="ContinuedIn">The session this one continues in (<c>continued-in</c>), when no message follows that record.</param>
/// <param name="ContinuedInExists">Whether the transcript of <paramref name="ContinuedIn"/> exists beside this one.</param>
/// <param name="Title">The title by the priority of the CLI, the first prompt included; null when it has none.</param>
/// <param name="LastWriteTime">The last write time of the file (UTC).</param>
/// <param name="Length">The length of the file.</param>
internal sealed record ClaudeCodeTranscriptHeader(
    string SessionId,
    string? Cwd,
    string? Entrypoint,
    ClaudeCodeTranscriptOrigin Origin,
    string? SessionKind,
    bool IsSidechain,
    string? ContinuedIn,
    bool ContinuedInExists,
    string? Title,
    DateTime LastWriteTime,
    long Length)
{
    /// <summary>
    /// Whether a program started the session (<c>sdk-*</c>, CodeAlta included) or it is a daemon session: VS Code
    /// does not list those.
    /// </summary>
    public bool IsProgrammatic =>
        Origin is ClaudeCodeTranscriptOrigin.Programmatic || SessionKind is "daemon" or "daemon-worker";

    /// <summary>
    /// Whether the session list of VS Code shows the session: not programmatic, not a sidechain, not continued in an
    /// existing transcript, and with a title or a prompt.
    /// </summary>
    public bool IsListedByVsCode =>
        !IsProgrammatic && !IsSidechain && !(ContinuedIn is not null && ContinuedInExists) && Title is not null;
}
