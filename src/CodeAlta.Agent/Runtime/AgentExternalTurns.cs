using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// A turn executor whose provider keeps its own copy of the conversation of a session, which other programs can
/// continue (the transcript of Claude Code, which VS Code and the CLI in a terminal write too): it reads the turns
/// written there since the session last recorded the conversation. Nothing of the provider is started.
/// </summary>
internal interface IAgentProviderExternalTurns
{
    /// <summary>Reads the turns added to the conversation of a session outside CodeAlta.</summary>
    /// <param name="state">The state the session recorded last.</param>
    /// <param name="workingDirectory">The folder the session works in, where the provider keeps the conversation.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>What was found; <see cref="AgentExternalTurnsStatus.None"/> when the state names no conversation of this provider.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="workingDirectory"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    Task<AgentExternalTurns> ReadExternalTurnsAsync(AgentSessionState state, string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// A turn executor whose provider moves the conversation of a session forward outside the answers it returns: a turn
/// stopped or failed after its prompt was sent, a compaction. The session records the state it gives after each of
/// them, as it records the state of an answer, so that what the provider wrote then is not taken later for turns made
/// outside CodeAlta.
/// </summary>
internal interface IAgentProviderStateUpdates
{
    /// <summary>
    /// Returns the provider state of a session that changed since the last state the session was given, and forgets it:
    /// a later call returns null until it changes again.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The state to record, or null when there is nothing new.</returns>
    JsonElement? TakeProviderState(string sessionId);
}

/// <summary>What a read of the turns made outside CodeAlta found.</summary>
internal enum AgentExternalTurnsStatus
{
    /// <summary>The state names no conversation this provider keeps, or no point of it to follow from: nothing to do.</summary>
    None,

    /// <summary>Nothing was written since the state was recorded.</summary>
    Unchanged,

    /// <summary>Turns were read; there may be none (only lines the session does not show were written).</summary>
    Read,

    /// <summary>
    /// The conversation was changed outside CodeAlta in a way that cannot be followed (rewound, or resumed at an earlier
    /// point): nothing is appended, and the provider, which has the whole conversation, goes on from where it is now.
    /// </summary>
    CannotFollow,

    /// <summary>The conversation the state names cannot be read where the provider would look for it.</summary>
    Missing,

    /// <summary>
    /// Another program is working on the conversation now (a turn of VS Code in progress): nothing is read, since half a
    /// turn written now would split what follows it. The next read, once that program is idle, finds the whole turn.
    /// </summary>
    Held,
}

/// <summary>Where a turn made outside CodeAlta was made.</summary>
internal enum AgentExternalTurnOrigin
{
    /// <summary>The provider did not say.</summary>
    Unknown,

    /// <summary>VS Code.</summary>
    VsCode,

    /// <summary>A terminal.</summary>
    Terminal,

    /// <summary>Another application, which <see cref="AgentExternalTurn.Entrypoint"/> names.</summary>
    OtherApplication,

    /// <summary>The cloud: a cloud session of the provider, brought back to this computer.</summary>
    Cloud,
}

/// <summary>A turn made outside CodeAlta: a prompt and what followed it, or a compaction.</summary>
/// <param name="Messages">
/// The messages of the turn in the shapes a session records: one user message for the prompt (none for a turn whose
/// prompt is before the point read from, or that the provider started by itself), one assistant message per model
/// message, one tool message per tool result. Empty for a compaction.
/// </param>
/// <param name="Origin">Where the turn was made.</param>
/// <param name="Entrypoint">The name the provider gives the program that made it, as written.</param>
/// <param name="Timestamp">When the turn started, when known.</param>
/// <param name="IsCompaction">Whether the turn is a compaction of the conversation, which has no messages.</param>
/// <param name="TriggerText">For a turn the provider started by itself: the text of what started it; otherwise null.</param>
internal sealed record AgentExternalTurn(
    IReadOnlyList<AgentConversationMessage> Messages,
    AgentExternalTurnOrigin Origin,
    string? Entrypoint,
    DateTimeOffset? Timestamp,
    bool IsCompaction = false,
    string? TriggerText = null);

/// <summary>The turns made outside CodeAlta, and the provider state to record with them.</summary>
/// <param name="Status">What the read found.</param>
/// <param name="Turns">The turns, in order; empty unless <paramref name="Status"/> is <see cref="AgentExternalTurnsStatus.Read"/>.</param>
/// <param name="ProviderStateAfter">
/// The provider state to record after the first <c>turns</c> turns were written, given the user and assistant messages
/// written for them: <c>(turns, users, assistants) =&gt; state</c>. The counts are those of what was written, never
/// computed again from the copy of the provider. Null when nothing is to be recorded
/// (<see cref="AgentExternalTurnsStatus.None"/>, <see cref="AgentExternalTurnsStatus.Unchanged"/>,
/// <see cref="AgentExternalTurnsStatus.Missing"/>).
/// </param>
internal sealed record AgentExternalTurns(
    AgentExternalTurnsStatus Status,
    IReadOnlyList<AgentExternalTurn> Turns,
    Func<int, int, int, JsonElement>? ProviderStateAfter)
{
    /// <summary>Nothing to do.</summary>
    public static AgentExternalTurns None { get; } = new(AgentExternalTurnsStatus.None, [], null);

    /// <summary>Nothing was written since.</summary>
    public static AgentExternalTurns Unchanged { get; } = new(AgentExternalTurnsStatus.Unchanged, [], null);

    /// <summary>The conversation cannot be read.</summary>
    public static AgentExternalTurns Missing { get; } = new(AgentExternalTurnsStatus.Missing, [], null);

    /// <summary>Another program is working on the conversation now: nothing was read.</summary>
    public static AgentExternalTurns Held { get; } = new(AgentExternalTurnsStatus.Held, [], null);
}
