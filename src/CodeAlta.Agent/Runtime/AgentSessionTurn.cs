namespace CodeAlta.Agent.Runtime;

/// <summary>How a turn of a session ended, as far as its journal says.</summary>
public enum AgentSessionTurnStatus
{
    /// <summary>The journal holds no end of the turn: it runs, or the application stopped while it ran.</summary>
    Unfinished,

    /// <summary>The turn ended and the session became idle.</summary>
    Completed,

    /// <summary>The turn ended with an error or was stopped.</summary>
    Failed,
}

/// <summary>
/// One turn of a session as its journal records it: the records of one run, from the first one that carries its
/// identifier. The start of a turn is a point a session can be forked at or rewound to.
/// </summary>
public sealed record AgentSessionTurn
{
    /// <summary>Gets the position of the turn in the session, from 1.</summary>
    public required int Number { get; init; }

    /// <summary>Gets the identifier of the run, which names the turn whatever is written before or after it.</summary>
    public required string RunId { get; init; }

    /// <summary>Gets the byte offset of the first record of the turn: where the session is cut to remove it.</summary>
    public required long StartOffset { get; init; }

    /// <summary>Gets the exclusive byte offset of the end of the last record of the turn.</summary>
    public required long EndOffset { get; init; }

    /// <summary>Gets the time of the first record of the turn.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Gets the time the turn ended; null when <see cref="Status"/> is <see cref="AgentSessionTurnStatus.Unfinished"/>.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Gets how the turn ended.</summary>
    public AgentSessionTurnStatus Status { get; init; }

    /// <summary>Gets the byte offset of the record of the prompt that started the turn; null for a turn without one.</summary>
    public long? PromptOffset { get; init; }

    /// <summary>Gets the content identifier of the prompt; null for a turn without one.</summary>
    public string? PromptContentId { get; init; }

    /// <summary>Gets the start of the text of the prompt, at most <see cref="PromptPreviewLength"/> characters.</summary>
    public string PromptPreview { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the prompt is longer than <see cref="PromptPreview"/>.</summary>
    public bool PromptTruncated { get; init; }

    /// <summary>Gets the session of the agent that sent the prompt; null for a prompt of a person or of the host.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Gets the question the prompt answers, when it answers one.</summary>
    public string? AskId { get; init; }

    /// <summary>Gets the number of messages sent while the turn ran, after its prompt.</summary>
    public int SteerCount { get; init; }

    /// <summary>Gets the number of assistant messages of the turn.</summary>
    public int AssistantMessageCount { get; init; }

    /// <summary>Gets the number of tool calls of the turn.</summary>
    public int ToolCallCount { get; init; }

    /// <summary>Gets the number of compactions that completed during the turn or after it, before the next turn.</summary>
    public int CompactionCount { get; init; }

    /// <summary>The most characters <see cref="PromptPreview"/> holds.</summary>
    public const int PromptPreviewLength = 2000;
}

/// <summary>The turns of a session, read from one revision of its journal.</summary>
/// <param name="Revision">The journal the turns were read from: its length and last-write time.</param>
/// <param name="Turns">The turns in journal order.</param>
/// <param name="EndOffset">The exclusive byte offset of the last complete record: the point after the last turn.</param>
public sealed record AgentSessionTurns(AgentHistoryRevision Revision, IReadOnlyList<AgentSessionTurn> Turns, long EndOffset);
