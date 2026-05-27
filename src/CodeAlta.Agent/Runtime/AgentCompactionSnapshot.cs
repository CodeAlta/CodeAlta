namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Represents a persisted local compaction snapshot stored in the canonical event log.
/// </summary>
public sealed record AgentCompactionSnapshot
{
    /// <summary>
    /// Gets or initializes the canonical event count included by the snapshot.
    /// </summary>
    public required int IncludedEventCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of conversation messages summarized by the snapshot.
    /// </summary>
    public required int SummarizedMessageCount { get; init; }

    /// <summary>
    /// Gets or initializes the synthetic replay message that represents the compacted history.
    /// </summary>
    public required AgentConversationMessage SummaryMessage { get; init; }
}
