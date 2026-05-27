namespace CodeAlta.Agent.Runtime.Compaction;

internal enum AgentCompactionTrigger
{
    Manual,
    Threshold,
    Overflow,
}

internal sealed record AgentTokenBudget(
    long? TotalContextEnvelope,
    long? InputContextLimit,
    long? MaxOutputTokens);

internal sealed record AgentTokenEstimate(
    long Tokens,
    string Source,
    bool IsEstimated);

internal sealed record AgentCompactionPreparation(
    AgentCompactionTrigger Trigger,
    IReadOnlyList<AgentConversationMessage> MessagesToSummarize,
    IReadOnlyList<AgentConversationMessage> TurnPrefixMessages,
    IReadOnlyList<AgentConversationMessage> MessagesToKeep,
    string? AnchorContentId,
    bool IsSplitTurn,
    AgentTokenEstimate TokensBefore,
    string? PreviousSummary,
    AgentConversationMessage? OversizedAnchorMessage = null);

internal sealed record AgentCompactionResult(
    string Summary,
    string? AnchorContentId,
    bool IsSplitTurn,
    bool OversizedAnchorReduced,
    long TokensBefore,
    long? TokensAfter,
    int MessagesSummarized,
    int ChunkCount,
    int SummaryCallCount,
    int SummaryMaxOutputTokens,
    long SummaryPromptInputTokens,
    int SummaryPromptIncludedMessages,
    int SummaryPromptTotalMessages,
    double? CompressionRatio,
    AgentCompactionSerializerStatistics SerializerStatistics,
    IReadOnlyList<string> ReadFiles,
    IReadOnlyList<string> ModifiedFiles,
    double? TargetRatio = null,
    long? TargetTokens = null,
    bool? TargetMet = null,
    string? TargetMissReason = null,
    int? PlanningAttemptCount = null,
    double? PostCompactionInputRatio = null,
    long? CheckpointTokens = null,
    long? FixedPromptTokens = null,
    long? RetainedMessageTokens = null,
    int? ModelVisibleReadFileCount = null,
    int? ModelVisibleModifiedFileCount = null);

internal sealed record AgentCompactionSummaryRequest(
    ModelProviderId ProviderId,
    ModelProviderRuntimeDescriptor Provider,
    string SessionId,
    string? ModelId,
    AgentModelInfo? ModelInfo,
    string? WorkingDirectory,
    AgentSessionState State,
    string SystemMessage,
    string UserMessage,
    int MaxOutputTokens);

internal sealed record AgentCompactionSummaryResponse(
    string Summary,
    AgentSessionUsage? Usage);

internal sealed record AgentCompactionSerializerStatistics(
    int OmittedToolResultCount,
    int OmittedReasoningCount,
    int OmittedAttachmentCount,
    int DroppedMessageCount,
    int SerializedToolResultCharacters,
    int SerializedReasoningCharacters,
    bool ReducedOversizedAnchor,
    int TotalToolCallCount = 0,
    int SerializedToolCallCount = 0,
    int CollapsedToolCallCount = 0,
    int TotalToolResultCount = 0,
    int SerializedToolResultCount = 0,
    int SerializedToolResultExcerptCount = 0,
    int TotalReasoningCount = 0,
    int SerializedReasoningCount = 0,
    int TotalAttachmentCount = 0,
    int SerializedAttachmentCount = 0);

internal sealed record AgentCompactionSerializationResult(
    string UserMessage,
    long EstimatedInputTokens,
    int IncludedMessageCount,
    int TotalMessageCount,
    AgentCompactionSerializerStatistics Statistics);

internal interface IAgentCompactionSummaryExecutor
{
    Task<AgentCompactionSummaryResponse> ExecuteAsync(
        AgentCompactionSummaryRequest request,
        CancellationToken cancellationToken = default);
}
