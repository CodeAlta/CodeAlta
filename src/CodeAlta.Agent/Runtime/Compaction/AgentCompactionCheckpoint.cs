using System.Text;

namespace CodeAlta.Agent.Runtime.Compaction;

/// <summary>
/// Represents a persisted local compaction checkpoint.
/// </summary>
public sealed record AgentCompactionCheckpoint
{
    /// <summary>
    /// Gets or initializes the checkpoint schema version.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Gets or initializes the checkpoint content identifier.
    /// </summary>
    public required string ContentId { get; init; }

    /// <summary>
    /// Gets or initializes the compaction trigger.
    /// </summary>
    public required string Trigger { get; init; }

    /// <summary>
    /// Gets or initializes the summary text.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Gets or initializes the first kept event offset when known.
    /// </summary>
    public long? FirstKeptEventOffset { get; init; }

    /// <summary>
    /// Gets or initializes the anchor content identifier when known.
    /// </summary>
    public string? AnchorContentId { get; init; }

    /// <summary>
    /// Gets or initializes whether compaction split an in-progress turn.
    /// </summary>
    public bool IsSplitTurn { get; init; }

    /// <summary>
    /// Gets or initializes the token count before compaction.
    /// </summary>
    public long TokensBefore { get; init; }

    /// <summary>
    /// Gets or initializes the token count after compaction when known.
    /// </summary>
    public long? TokensAfter { get; init; }

    /// <summary>
    /// Gets or initializes the realized post-compaction compression ratio.
    /// </summary>
    public double? CompressionRatio { get; init; }

    /// <summary>
    /// Gets or initializes the summarized message count.
    /// </summary>
    public int SummarizedMessageCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of original messages retained verbatim after compaction.
    /// </summary>
    public int KeptMessageCount { get; init; }

    /// <summary>
    /// Gets or initializes the estimated total input tokens sent to summarizer calls.
    /// </summary>
    public long SummaryPromptInputTokens { get; init; }

    /// <summary>
    /// Gets or initializes the number of messages serialized into summarizer requests.
    /// </summary>
    public int SummaryPromptIncludedMessageCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of messages considered for summarizer requests before serialization drops.
    /// </summary>
    public int SummaryPromptTotalMessageCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of local AI summarizer calls used to produce this checkpoint.
    /// </summary>
    public int SummaryCallCount { get; init; }

    /// <summary>
    /// Gets or initializes the maximum output tokens requested from the summarizer.
    /// </summary>
    public int SummaryMaxOutputTokens { get; init; }

    /// <summary>
    /// Gets or initializes the verbatim kept suffix captured at compaction time.
    /// </summary>
    public IReadOnlyList<AgentConversationMessage> KeptMessages { get; init; } = [];

    /// <summary>
    /// Gets or initializes the best-effort tracked read files.
    /// </summary>
    public IReadOnlyList<string> ReadFiles { get; init; } = [];

    /// <summary>
    /// Gets or initializes the best-effort tracked modified files.
    /// </summary>
    public IReadOnlyList<string> ModifiedFiles { get; init; } = [];

    /// <summary>
    /// Gets or initializes how many tool-result excerpts were omitted during serialization.
    /// </summary>
    public int OmittedToolResultCount { get; init; }

    /// <summary>
    /// Gets or initializes how many reasoning excerpts were omitted during serialization.
    /// </summary>
    public int OmittedReasoningCount { get; init; }

    /// <summary>
    /// Gets or initializes how many inline attachments were omitted during serialization.
    /// </summary>
    public int OmittedAttachmentCount { get; init; }

    /// <summary>
    /// Gets or initializes how many messages had no serializable content for summarization.
    /// </summary>
    public int DroppedMessageCount { get; init; }

    /// <summary>
    /// Gets or initializes how many tool-result characters were serialized for summarization.
    /// </summary>
    public int SerializedToolResultCharacters { get; init; }

    /// <summary>
    /// Gets or initializes how many reasoning characters were serialized for summarization.
    /// </summary>
    public int SerializedReasoningCharacters { get; init; }

    /// <summary>
    /// Gets or initializes the number of tool calls represented in summarizer input.
    /// </summary>
    public int TotalToolCallCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of tool-call lines serialized for summarization.
    /// </summary>
    public int SerializedToolCallCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of repeated tool calls collapsed into compact summaries.
    /// </summary>
    public int CollapsedToolCallCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of tool results represented in summarizer input.
    /// </summary>
    public int TotalToolResultCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of tool-result summary lines serialized for summarization.
    /// </summary>
    public int SerializedToolResultCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of tool results that included output excerpts.
    /// </summary>
    public int SerializedToolResultExcerptCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of reasoning parts represented in summarizer input.
    /// </summary>
    public int TotalReasoningCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of reasoning excerpts serialized for summarization.
    /// </summary>
    public int SerializedReasoningCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of attachments represented in summarizer input.
    /// </summary>
    public int TotalAttachmentCount { get; init; }

    /// <summary>
    /// Gets or initializes the number of attachment references serialized for summarization.
    /// </summary>
    public int SerializedAttachmentCount { get; init; }

    /// <summary>
    /// Gets or initializes how many recursive chunks were used to create the checkpoint.
    /// </summary>
    public int ChunkCount { get; init; } = 1;

    /// <summary>
    /// Gets or initializes whether the latest oversized anchor was reduced.
    /// </summary>
    public bool OversizedAnchorReduced { get; init; }

    /// <summary>
    /// Gets or initializes the preferred post-compaction active-context target ratio.
    /// </summary>
    public double? TargetRatio { get; init; }

    /// <summary>
    /// Gets or initializes the preferred post-compaction active-context token target.
    /// </summary>
    public long? TargetTokens { get; init; }

    /// <summary>
    /// Gets or initializes whether the realized post-compaction context met the preferred target.
    /// </summary>
    public bool? TargetMet { get; init; }

    /// <summary>
    /// Gets or initializes the reason the preferred target was missed, when known.
    /// </summary>
    public string? TargetMissReason { get; init; }

    /// <summary>
    /// Gets or initializes the number of bounded planning attempts used before accepting the checkpoint.
    /// </summary>
    public int? PlanningAttemptCount { get; init; }

    /// <summary>
    /// Gets or initializes the realized post-compaction active-context ratio against the input-context limit.
    /// </summary>
    public double? PostCompactionInputRatio { get; init; }

    /// <summary>
    /// Gets or initializes the estimated checkpoint token count.
    /// </summary>
    public long? CheckpointTokens { get; init; }

    /// <summary>
    /// Gets or initializes the estimated fixed system/developer prompt token count included in target planning.
    /// </summary>
    public long? FixedPromptTokens { get; init; }

    /// <summary>
    /// Gets or initializes the estimated token count of retained messages after compaction.
    /// </summary>
    public long? RetainedMessageTokens { get; init; }

    /// <summary>
    /// Gets or initializes how many read files were exposed to the model-visible summary prompt.
    /// </summary>
    public int? ModelVisibleReadFileCount { get; init; }

    /// <summary>
    /// Gets or initializes how many modified files were exposed to the model-visible summary prompt.
    /// </summary>
    public int? ModelVisibleModifiedFileCount { get; init; }

    /// <summary>
    /// Creates the synthetic replay message for the checkpoint.
    /// </summary>
    /// <returns>The replay message.</returns>
    public AgentConversationMessage CreateMessage()
        => new(
            AgentConversationRole.User,
            [new AgentMessagePart.Text(WrapSummary(Summary))]);

    /// <summary>
    /// Wraps summary text in the canonical checkpoint envelope.
    /// </summary>
    /// <param name="summary">The summary text.</param>
    /// <returns>The wrapped text.</returns>
    public static string WrapSummary(string summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<codealta-compaction-checkpoint version="2">""");
        builder.AppendLine(summary.Trim());
        builder.Append("""</codealta-compaction-checkpoint>""");
        return builder.ToString();
    }

    /// <summary>
    /// Attempts to extract a wrapped summary from a replay message.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    /// <returns>The unwrapped summary when present; otherwise <see langword="null" />.</returns>
    public static string? TryExtractSummary(AgentConversationMessage message)
    {
        if (message.Role is not AgentConversationRole.User)
        {
            return null;
        }

        var text = message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        const string prefixV2 = """<codealta-compaction-checkpoint version="2">""";
        const string prefixV1 = """<codealta-compaction-checkpoint version="1">""";
        const string suffix = "</codealta-compaction-checkpoint>";
        var prefix = prefixV2;
        var start = text.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            prefix = prefixV1;
            start = text.IndexOf(prefix, StringComparison.Ordinal);
        }

        var end = text.LastIndexOf(suffix, StringComparison.Ordinal);
        if (start < 0 || end < 0 || end <= start)
        {
            return null;
        }

        start += prefix.Length;
        var summary = text[start..end].Trim();
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }
}
