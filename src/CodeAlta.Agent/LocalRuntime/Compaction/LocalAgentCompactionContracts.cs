namespace CodeAlta.Agent.LocalRuntime.Compaction;

internal enum LocalAgentCompactionTrigger
{
    Manual,
    Threshold,
    Overflow,
}

internal sealed record LocalAgentTokenBudget(
    long? TotalContextEnvelope,
    long? InputContextLimit,
    long? MaxOutputTokens);

internal sealed record LocalAgentTokenEstimate(
    long Tokens,
    string Source,
    bool IsEstimated);

internal sealed record LocalAgentCompactionPreparation(
    LocalAgentCompactionTrigger Trigger,
    IReadOnlyList<LocalAgentConversationMessage> MessagesToSummarize,
    IReadOnlyList<LocalAgentConversationMessage> TurnPrefixMessages,
    IReadOnlyList<LocalAgentConversationMessage> MessagesToKeep,
    string? AnchorContentId,
    bool IsSplitTurn,
    LocalAgentTokenEstimate TokensBefore,
    string? PreviousSummary,
    LocalAgentConversationMessage? OversizedAnchorMessage = null);

internal sealed record LocalAgentCompactionResult(
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
    LocalAgentCompactionSerializerStatistics SerializerStatistics,
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

internal sealed record LocalAgentCompactionSummaryRequest(
    AgentBackendId BackendId,
    ModelProviderRuntimeDescriptor Provider,
    string SessionId,
    string? ModelId,
    AgentModelInfo? ModelInfo,
    string? WorkingDirectory,
    LocalAgentSessionState State,
    string SystemMessage,
    string UserMessage,
    int MaxOutputTokens);

internal sealed record LocalAgentCompactionSummaryResponse(
    string Summary,
    AgentSessionUsage? Usage);

internal sealed record LocalAgentCompactionSerializerStatistics(
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

internal sealed record LocalAgentCompactionSerializationResult(
    string UserMessage,
    long EstimatedInputTokens,
    int IncludedMessageCount,
    int TotalMessageCount,
    LocalAgentCompactionSerializerStatistics Statistics);

internal interface ILocalAgentCompactionSummaryExecutor
{
    Task<LocalAgentCompactionSummaryResponse> ExecuteAsync(
        LocalAgentCompactionSummaryRequest request,
        CancellationToken cancellationToken = default);
}
