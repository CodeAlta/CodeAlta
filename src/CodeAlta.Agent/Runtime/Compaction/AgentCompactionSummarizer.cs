using System.Text.Json;

namespace CodeAlta.Agent.Runtime.Compaction;

internal sealed class AgentCompactionSummarizer(IAgentCompactionSummaryExecutor executor)
{
    private const int RecursiveChunkPassLimit = 4;

    private const string SummarySystemPromptTemplate =
        """
        You are the CodeAlta compaction summarizer.

        Summarize the supplied conversation state for future continuation by another model call.
        Do not continue the conversation. Do not answer the user's task. Do not invent work.

        Return only markdown with exactly these top-level sections in this order:
        ## Objective
        ## Active User Request
        ## Constraints
        ## Progress
        ### Done
        ### In Progress
        ### Blocked
        ## Decisions
        ## Next Steps
        ## Critical Context
        ## Relevant Files

        Preserve exact file paths, identifiers, tool names, and critical error text when present.
        Keep the summary concise, continuation-oriented, and optimized for replay.
        When a previous summary is provided, update it by preserving still-relevant facts and retiring stale implementation detail; do not append indefinitely.
        Keep Done milestone-level rather than changelog-like. Keep exact commit hashes only when the next agent must reference them.
        Prefer current state, unresolved blockers, verification status, next steps, and active files over exhaustive historical progress.
        Replace stale file lists with current dirty, modified, read, or otherwise active files when newer file activity is provided.
        """;

    private const string ShrinkSystemPromptTemplate =
        """
        You are the CodeAlta compaction summary shrinker.

        Rewrite the supplied compaction checkpoint into a smaller continuation handoff.
        Do not continue the conversation. Do not answer the user's task. Do not invent work.

        Return only markdown with exactly these top-level sections in this order:
        ## Objective
        ## Active User Request
        ## Constraints
        ## Progress
        ### Done
        ### In Progress
        ### Blocked
        ## Decisions
        ## Next Steps
        ## Critical Context
        ## Relevant Files

        Preserve only continuation-critical facts: active objective, explicit constraints, current state, unresolved blockers, next steps, current verification status, active files, exact paths, identifiers, commands, and critical error text.
        Retire stale completed details, old exploratory file lists, old commit hashes unless needed next, and archival narration.
        Prefer compact bullets over prose.
        """;

    private const string OversizedAnchorSystemPromptTemplate =
        """
        You are the CodeAlta oversized-anchor reducer.

        Distill the supplied latest user input into a compact continuation anchor for later compaction.
        Do not answer the user's request. Do not continue the conversation.

        Preserve exact file paths, identifiers, commands, numbered requirements, and critical error text when present.
        Prefer bullets over prose. Keep only continuation-critical details.

        Return only markdown with exactly these top-level sections in this order:
        ## Task
        ## Explicit Requirements
        ## Files and Identifiers
        ## Exact Literals and Errors

        When a previous anchor synopsis is provided, update it rather than rewriting from scratch.
        """;

    private readonly IAgentCompactionSummaryExecutor _executor = executor ?? throw new ArgumentNullException(nameof(executor));

    public async Task<AgentCompactionResult> SummarizeAsync(
        ModelProviderId ProviderId,
        ModelProviderRuntimeDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        AgentSessionState state,
        AgentCompactionPreparation preparation,
        IReadOnlyList<AgentEvent> history,
        string? latestUserRequest,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(history);

        var settings = provider.Compaction ?? AgentCompactionSettings.Default;
        var fileActivity = ExtractFileActivity(history);
        var modelVisibleFileActivity = BudgetFileActivityForSummary(fileActivity, latestUserRequest, settings, maxOutputTokens);
        string? oversizedAnchorSynopsis = null;
        var oversizedAnchorInvocationCount = 0;
        if (preparation.OversizedAnchorMessage is not null)
        {
            (oversizedAnchorSynopsis, oversizedAnchorInvocationCount) = await ReduceOversizedAnchorAsync(
                    ProviderId,
                    provider,
                    sessionId,
                    modelId,
                    modelInfo,
                    workingDirectory,
                    state,
                    preparation.OversizedAnchorMessage,
                    settings,
                    maxOutputTokens,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await SummarizePreparationAsync(
                ProviderId,
                provider,
                sessionId,
                modelId,
                modelInfo,
                workingDirectory,
                state,
                preparation,
                latestUserRequest,
                maxOutputTokens,
                settings,
                fileActivity,
                modelVisibleFileActivity,
                oversizedAnchorSynopsis,
                oversizedAnchorInvocationCount,
                currentPass: 1,
                cancellationToken)
            .ConfigureAwait(false);
        return result with
        {
            ReadFiles = fileActivity.ReadFiles,
            ModifiedFiles = fileActivity.ModifiedFiles,
            ModelVisibleReadFileCount = modelVisibleFileActivity.ReadFiles.Count,
            ModelVisibleModifiedFileCount = modelVisibleFileActivity.ModifiedFiles.Count,
        };
    }

    public async Task<AgentCompactionResult> ShrinkSummaryAsync(
        ModelProviderId ProviderId,
        ModelProviderRuntimeDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        AgentSessionState state,
        AgentCompactionResult result,
        string? latestUserRequest,
        long checkpointTargetTokens,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(result);

        var settings = provider.Compaction ?? AgentCompactionSettings.Default;
        var completeFileActivity = new FileActivity(result.ReadFiles, result.ModifiedFiles);
        var modelVisibleFileActivity = BudgetFileActivityForSummary(completeFileActivity, latestUserRequest, settings, maxOutputTokens);
        var userMessage = BuildShrinkRequestBody(result.Summary, latestUserRequest, modelVisibleFileActivity, checkpointTargetTokens);
        var response = await ExecuteSummaryRequestAsync(
                ProviderId,
                provider,
                sessionId,
                modelId,
                modelInfo,
                workingDirectory,
                state,
                CreateShrinkSystemPrompt(maxOutputTokens, checkpointTargetTokens),
                userMessage,
                Math.Max(maxOutputTokens, 1),
                cancellationToken)
            .ConfigureAwait(false);

        var normalizedSummary = NormalizeSummary(
            response.Summary,
            latestUserRequest,
            result.Summary,
            modelVisibleFileActivity,
            maxOutputTokens);
        ValidateSummaryShape(normalizedSummary);

        return result with
        {
            Summary = normalizedSummary,
            SummaryCallCount = result.SummaryCallCount + 1,
            SummaryPromptInputTokens = result.SummaryPromptInputTokens + AgentTokenEstimator.EstimateTextTokens(userMessage),
            SummaryMaxOutputTokens = maxOutputTokens,
            ModelVisibleReadFileCount = modelVisibleFileActivity.ReadFiles.Count,
            ModelVisibleModifiedFileCount = modelVisibleFileActivity.ModifiedFiles.Count,
        };
    }

    private async Task<AgentCompactionResult> SummarizePreparationAsync(
        ModelProviderId ProviderId,
        ModelProviderRuntimeDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        AgentSessionState state,
        AgentCompactionPreparation preparation,
        string? latestUserRequest,
        int maxOutputTokens,
        AgentCompactionSettings settings,
        FileActivity fileActivity,
        FileActivity modelVisibleFileActivity,
        string? oversizedAnchorSynopsis,
        int additionalSummaryCallCount,
        int currentPass,
        CancellationToken cancellationToken)
    {
        var serialization = AgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest,
            modelVisibleFileActivity.ReadFiles,
            modelVisibleFileActivity.ModifiedFiles,
            settings,
            oversizedAnchorSynopsis,
            preparation.OversizedAnchorMessage is not null);

        var chunks = GetChunksIfNeeded(
            preparation,
            latestUserRequest,
            modelVisibleFileActivity,
            settings,
            modelInfo,
            oversizedAnchorSynopsis,
            maxOutputTokens);

        var summaryInputLimit = GetSummaryInputLimit(modelInfo, settings, maxOutputTokens);
        if (summaryInputLimit is > 0 &&
            serialization.EstimatedInputTokens > summaryInputLimit.Value &&
            currentPass < RecursiveChunkPassLimit &&
            chunks.Count > 1)
        {
            return await SummarizeChunkedAsync(
                    ProviderId,
                    provider,
                    sessionId,
                    modelId,
                    modelInfo,
                    workingDirectory,
                    state,
                    preparation,
                    latestUserRequest,
                    maxOutputTokens,
                    settings,
                    fileActivity,
                    modelVisibleFileActivity,
                    oversizedAnchorSynopsis,
                    additionalSummaryCallCount,
                    currentPass,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var response = await ExecuteSummaryRequestAsync(
                ProviderId,
                provider,
                sessionId,
                modelId,
                modelInfo,
                workingDirectory,
                state,
                CreateSystemPrompt(maxOutputTokens),
                serialization.UserMessage,
                maxOutputTokens,
                cancellationToken)
            .ConfigureAwait(false);
        var normalizedSummary = NormalizeSummary(
            response.Summary,
            latestUserRequest,
            preparation.PreviousSummary,
            modelVisibleFileActivity,
            maxOutputTokens);
        ValidateSummaryShape(normalizedSummary);

        return new AgentCompactionResult(
            Summary: normalizedSummary,
            AnchorContentId: preparation.AnchorContentId,
            IsSplitTurn: preparation.IsSplitTurn,
            OversizedAnchorReduced: serialization.Statistics.ReducedOversizedAnchor,
            TokensBefore: preparation.TokensBefore.Tokens,
            TokensAfter: null,
            MessagesSummarized: preparation.MessagesToSummarize.Count,
            ChunkCount: 1,
            SummaryCallCount: additionalSummaryCallCount + 1,
            SummaryMaxOutputTokens: maxOutputTokens,
            SummaryPromptInputTokens: serialization.EstimatedInputTokens,
            SummaryPromptIncludedMessages: serialization.IncludedMessageCount,
            SummaryPromptTotalMessages: serialization.TotalMessageCount,
            CompressionRatio: null,
            SerializerStatistics: serialization.Statistics,
            ReadFiles: fileActivity.ReadFiles,
            ModifiedFiles: fileActivity.ModifiedFiles,
            ModelVisibleReadFileCount: modelVisibleFileActivity.ReadFiles.Count,
            ModelVisibleModifiedFileCount: modelVisibleFileActivity.ModifiedFiles.Count);
    }

    private async Task<AgentCompactionResult> SummarizeChunkedAsync(
        ModelProviderId ProviderId,
        ModelProviderRuntimeDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        AgentSessionState state,
        AgentCompactionPreparation preparation,
        string? latestUserRequest,
        int maxOutputTokens,
        AgentCompactionSettings settings,
        FileActivity fileActivity,
        FileActivity modelVisibleFileActivity,
        string? oversizedAnchorSynopsis,
        int additionalSummaryCallCount,
        int currentPass,
        CancellationToken cancellationToken)
    {
        var chunks = GetChunksIfNeeded(preparation, latestUserRequest, modelVisibleFileActivity, settings, modelInfo, oversizedAnchorSynopsis, maxOutputTokens);
        if (chunks.Count <= 1)
        {
            chunks = [preparation.MessagesToSummarize];
        }

        var rollingSummary = preparation.PreviousSummary;
        var aggregatedStatistics = new AgentCompactionSerializerStatistics(
            OmittedToolResultCount: 0,
            OmittedReasoningCount: 0,
            OmittedAttachmentCount: 0,
            DroppedMessageCount: 0,
            SerializedToolResultCharacters: 0,
            SerializedReasoningCharacters: 0,
            ReducedOversizedAnchor: false);
        AgentCompactionResult? finalResult = null;
        var totalChunkCount = 0;
        var totalSummaryCallCount = additionalSummaryCallCount;
        long totalSummaryPromptInputTokens = 0;
        var totalSummaryPromptIncludedMessages = 0;
        var totalSummaryPromptMessages = 0;

        for (var index = 0; index < chunks.Count; index++)
        {
            var chunkPreparation = preparation with
            {
                MessagesToSummarize = chunks[index],
                TurnPrefixMessages = [],
                MessagesToKeep = [],
                PreviousSummary = rollingSummary,
            };

            var chunkResult = await SummarizePreparationAsync(
                    ProviderId,
                    provider,
                    sessionId,
                    modelId,
                    modelInfo,
                    workingDirectory,
                    state,
                    chunkPreparation,
                    latestUserRequest,
                    maxOutputTokens,
                    settings,
                    fileActivity,
                    modelVisibleFileActivity,
                    oversizedAnchorSynopsis,
                    additionalSummaryCallCount: 0,
                    currentPass + 1,
                    cancellationToken)
                .ConfigureAwait(false);

            rollingSummary = chunkResult.Summary;
            aggregatedStatistics = MergeStatistics(aggregatedStatistics, chunkResult.SerializerStatistics);
            totalChunkCount += chunkResult.ChunkCount;
            totalSummaryCallCount += chunkResult.SummaryCallCount;
            totalSummaryPromptInputTokens += chunkResult.SummaryPromptInputTokens;
            totalSummaryPromptIncludedMessages += chunkResult.SummaryPromptIncludedMessages;
            totalSummaryPromptMessages += chunkResult.SummaryPromptTotalMessages;
            finalResult = chunkResult;
        }

        if (finalResult is not null &&
            (preparation.TurnPrefixMessages.Count > 0 || preparation.MessagesToKeep.Count > 0))
        {
            var mergePreparation = preparation with
            {
                MessagesToSummarize = [],
                PreviousSummary = rollingSummary,
            };

            var mergeResult = await SummarizePreparationAsync(
                    ProviderId,
                    provider,
                    sessionId,
                    modelId,
                    modelInfo,
                    workingDirectory,
                    state,
                    mergePreparation,
                    latestUserRequest,
                    maxOutputTokens,
                    settings,
                    fileActivity,
                    modelVisibleFileActivity,
                    oversizedAnchorSynopsis,
                    additionalSummaryCallCount: 0,
                    currentPass + 1,
                    cancellationToken)
                .ConfigureAwait(false);

            rollingSummary = mergeResult.Summary;
            aggregatedStatistics = MergeStatistics(aggregatedStatistics, mergeResult.SerializerStatistics);
            totalChunkCount += mergeResult.ChunkCount;
            totalSummaryCallCount += mergeResult.SummaryCallCount;
            totalSummaryPromptInputTokens += mergeResult.SummaryPromptInputTokens;
            totalSummaryPromptIncludedMessages += mergeResult.SummaryPromptIncludedMessages;
            totalSummaryPromptMessages += mergeResult.SummaryPromptTotalMessages;
            finalResult = mergeResult;
        }

        return finalResult is null
            ? throw new InvalidOperationException("Chunked compaction did not produce a final summary.")
            : finalResult with
            {
                ChunkCount = Math.Max(totalChunkCount, chunks.Count),
                SummaryCallCount = Math.Max(totalSummaryCallCount, 1),
                SummaryPromptInputTokens = totalSummaryPromptInputTokens,
                SummaryPromptIncludedMessages = totalSummaryPromptIncludedMessages,
                SummaryPromptTotalMessages = totalSummaryPromptMessages,
                SerializerStatistics = aggregatedStatistics,
            };
    }

    private IReadOnlyList<IReadOnlyList<AgentConversationMessage>> GetChunksIfNeeded(
        AgentCompactionPreparation preparation,
        string? latestUserRequest,
        FileActivity modelVisibleFileActivity,
        AgentCompactionSettings settings,
        AgentModelInfo? modelInfo,
        string? oversizedAnchorSynopsis,
        int maxOutputTokens)
        => AgentCompactionChunker.CreateChunks(
            preparation.MessagesToSummarize,
            (int)Math.Clamp(GetSummaryInputLimit(modelInfo, settings, maxOutputTokens) ?? long.MaxValue, 1L, int.MaxValue),
            chunkMessages => AgentCompactionSerializer.BuildSummaryRequestBody(
                    preparation with
                    {
                        MessagesToSummarize = chunkMessages,
                        TurnPrefixMessages = [],
                        MessagesToKeep = [],
                    },
                    latestUserRequest,
                    modelVisibleFileActivity.ReadFiles,
                    modelVisibleFileActivity.ModifiedFiles,
                    settings,
                    oversizedAnchorSynopsis,
                    preparation.OversizedAnchorMessage is not null)
                .EstimatedInputTokens);

    private static long? GetSummaryInputLimit(AgentModelInfo? modelInfo, AgentCompactionSettings settings, int maxOutputTokens)
    {
        var budget = AgentTokenBudgetResolver.Resolve(modelInfo, settings);
        var inputLimit = budget.InputContextLimit;
        if (budget.TotalContextEnvelope is > 0)
        {
            var envelopeInputLimit = Math.Max(budget.TotalContextEnvelope.Value - Math.Max(maxOutputTokens, 0), 1L);
            inputLimit = inputLimit is > 0
                ? Math.Min(inputLimit.Value, envelopeInputLimit)
                : envelopeInputLimit;
        }

        return inputLimit;
    }

    private async Task<(string Synopsis, int InvocationCount)> ReduceOversizedAnchorAsync(
        ModelProviderId ProviderId,
        ModelProviderRuntimeDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        AgentSessionState state,
        AgentConversationMessage oversizedAnchorMessage,
        AgentCompactionSettings settings,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var serializedAnchor = SerializeOversizedAnchorMessage(oversizedAnchorMessage);
        if (string.IsNullOrWhiteSpace(serializedAnchor))
        {
            throw new InvalidOperationException("The oversized latest user message could not be reduced because it had no serializable content.");
        }

        return await ReduceOversizedAnchorTextAsync(
                ProviderId,
                provider,
                sessionId,
                modelId,
                modelInfo,
                workingDirectory,
                state,
                serializedAnchor,
                previousSynopsis: null,
                settings,
                Math.Max(maxOutputTokens / 2, 1),
                currentPass: 1,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(string Synopsis, int InvocationCount)> ReduceOversizedAnchorTextAsync(
        ModelProviderId ProviderId,
        ModelProviderRuntimeDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        AgentSessionState state,
        string serializedAnchor,
        string? previousSynopsis,
        AgentCompactionSettings settings,
        int maxOutputTokens,
        int currentPass,
        CancellationToken cancellationToken)
    {
        var requestBody = BuildOversizedAnchorRequestBody(serializedAnchor, previousSynopsis);
        var requestTokens = AgentTokenEstimator.EstimateTextTokens(requestBody);
        var summaryInputLimit = GetSummaryInputLimit(modelInfo, settings, maxOutputTokens);
        if (summaryInputLimit is > 0 &&
            requestTokens > summaryInputLimit.Value &&
            currentPass < RecursiveChunkPassLimit)
        {
            var overheadTokens = AgentTokenEstimator.EstimateTextTokens(BuildOversizedAnchorRequestBody(string.Empty, previousSynopsis));
            var availableChunkTokens = Math.Max((int)Math.Min(summaryInputLimit.Value, int.MaxValue) - (int)overheadTokens, 32);
            var chunkTexts = SplitTextByBudget(serializedAnchor, Math.Max(availableChunkTokens * 4, 128));
            if (chunkTexts.Count <= 1 && serializedAnchor.Length > 1)
            {
                chunkTexts = SplitTextByBudget(serializedAnchor, Math.Max(serializedAnchor.Length / 2, 64));
            }

            if (chunkTexts.Count > 1)
            {
                var rollingSynopsis = previousSynopsis;
                var totalInvocations = 0;
                foreach (var chunkText in chunkTexts)
                {
                    var (chunkSynopsis, invocationCount) = await ReduceOversizedAnchorTextAsync(
                            ProviderId,
                            provider,
                            sessionId,
                            modelId,
                            modelInfo,
                            workingDirectory,
                            state,
                            chunkText,
                            rollingSynopsis,
                            settings,
                            maxOutputTokens,
                            currentPass + 1,
                            cancellationToken)
                        .ConfigureAwait(false);
                    rollingSynopsis = chunkSynopsis;
                    totalInvocations += invocationCount;
                }

                return (rollingSynopsis ?? throw new InvalidOperationException("Oversized-anchor reduction did not produce a synopsis."), totalInvocations);
            }
        }

        var response = await ExecuteSummaryRequestAsync(
                ProviderId,
                provider,
                sessionId,
                modelId,
                modelInfo,
                workingDirectory,
                state,
                CreateOversizedAnchorSystemPrompt(maxOutputTokens),
                requestBody,
                maxOutputTokens,
                cancellationToken)
            .ConfigureAwait(false);
        var normalizedSynopsis = NormalizeOversizedAnchorSynopsis(response.Summary, serializedAnchor, previousSynopsis);
        return (normalizedSynopsis, 1);
    }

    private async Task<AgentCompactionSummaryResponse> ExecuteSummaryRequestAsync(
        ModelProviderId ProviderId,
        ModelProviderRuntimeDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        AgentSessionState state,
        string systemMessage,
        string userMessage,
        int maxOutputTokens,
        CancellationToken cancellationToken)
        => await _executor.ExecuteAsync(
                new AgentCompactionSummaryRequest(
                    ProviderId: ProviderId,
                    Provider: provider,
                    SessionId: sessionId,
                    ModelId: modelId,
                    ModelInfo: modelInfo,
                    WorkingDirectory: workingDirectory,
                    State: state,
                    SystemMessage: systemMessage,
                    UserMessage: userMessage,
                    MaxOutputTokens: maxOutputTokens),
                cancellationToken)
            .ConfigureAwait(false);

    private static void ValidateSummaryShape(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new InvalidOperationException("The compaction summarizer returned an empty summary.");
        }

        if (!HasRequiredSummarySections(summary))
        {
            throw new InvalidOperationException("The compaction summarizer returned a malformed structured summary.");
        }
    }

    private static string NormalizeSummary(
        string summary,
        string? latestUserRequest,
        string? previousSummary,
        FileActivity fileActivity,
        int maxOutputTokens)
    {
        var trimmedSummary = summary.Trim();

        if (HasRequiredSummarySections(trimmedSummary))
        {
            return trimmedSummary;
        }

        var currentSections = ParseMarkdownSections(trimmedSummary);
        var previousSections = string.IsNullOrWhiteSpace(previousSummary)
            ? null
            : ParseMarkdownSections(previousSummary);

        var objective = FirstNonEmpty(
            GetSection(currentSections, "## Objective"),
            GetSection(previousSections, "## Objective"),
            ExtractLeadParagraph(trimmedSummary, 320),
            "Continue the conversation safely.");
        var activeUserRequest = FirstNonEmpty(
            NormalizeMultiline(latestUserRequest, 640),
            GetSection(currentSections, "## Active User Request"),
            GetSection(previousSections, "## Active User Request"),
            "- Not explicitly captured.");
        var constraints = FirstNonEmpty(
            GetSection(currentSections, "## Constraints"),
            GetSection(previousSections, "## Constraints"),
            "- None explicitly captured.");
        var done = FirstNonEmpty(
            GetSection(currentSections, "### Done"),
            GetSection(previousSections, "### Done"),
            "- None recorded.");
        var inProgress = FirstNonEmpty(
            GetSection(currentSections, "### In Progress"),
            GetSection(previousSections, "### In Progress"),
            "- None recorded.");
        var blocked = FirstNonEmpty(
            GetSection(currentSections, "### Blocked"),
            GetSection(previousSections, "### Blocked"),
            "- None recorded.");
        var decisions = FirstNonEmpty(
            GetSection(currentSections, "## Decisions"),
            GetSection(previousSections, "## Decisions"),
            "- None recorded.");
        var nextSteps = FirstNonEmpty(
            GetSection(currentSections, "## Next Steps"),
            GetSection(previousSections, "## Next Steps"),
            "- Resume from the latest retained context.");
        var relevantFiles = FirstNonEmpty(
            GetSection(currentSections, "## Relevant Files"),
            BuildRelevantFilesSection(fileActivity),
            GetSection(previousSections, "## Relevant Files"),
            "- None tracked.");

        var criticalContext = FirstNonEmpty(
            GetSection(currentSections, "## Critical Context"),
            GetSection(previousSections, "## Critical Context"),
            BuildFallbackCriticalContext(trimmedSummary, maxOutputTokens),
            "- Original draft summary was unavailable.");

        var builder = new System.Text.StringBuilder();
        AppendSummarySection(builder, "## Objective", objective);
        AppendSummarySection(builder, "## Active User Request", activeUserRequest);
        AppendSummarySection(builder, "## Constraints", constraints);
        builder.AppendLine("## Progress");
        builder.AppendLine("### Done");
        builder.AppendLine(done);
        builder.AppendLine();
        builder.AppendLine("### In Progress");
        builder.AppendLine(inProgress);
        builder.AppendLine();
        builder.AppendLine("### Blocked");
        builder.AppendLine(blocked);
        builder.AppendLine();
        AppendSummarySection(builder, "## Decisions", decisions);
        AppendSummarySection(builder, "## Next Steps", nextSteps);
        AppendSummarySection(builder, "## Critical Context", criticalContext);
        AppendSummarySection(builder, "## Relevant Files", relevantFiles, includeTrailingBlankLine: false);
        return builder.ToString().Trim();
    }

    private static string NormalizeOversizedAnchorSynopsis(
        string synopsis,
        string serializedAnchor,
        string? previousSynopsis)
    {
        var trimmedSynopsis = synopsis.Trim();
        if (HasRequiredOversizedAnchorSynopsisSections(trimmedSynopsis))
        {
            return trimmedSynopsis;
        }

        var currentSections = ParseOversizedAnchorSections(trimmedSynopsis);
        var previousSections = string.IsNullOrWhiteSpace(previousSynopsis)
            ? null
            : ParseOversizedAnchorSections(previousSynopsis);

        var task = FirstNonEmpty(
            GetSection(currentSections, "## Task"),
            GetSection(previousSections, "## Task"),
            ExtractLeadParagraph(serializedAnchor, 480),
            "- Continue from the oversized latest user request.");
        var explicitRequirements = FirstNonEmpty(
            GetSection(currentSections, "## Explicit Requirements"),
            GetSection(previousSections, "## Explicit Requirements"),
            "- Preserve continuation-critical details from the oversized latest user request.");
        var filesAndIdentifiers = FirstNonEmpty(
            GetSection(currentSections, "## Files and Identifiers"),
            GetSection(previousSections, "## Files and Identifiers"),
            "- None explicitly captured.");
        var exactLiteralsAndErrors = FirstNonEmpty(
            GetSection(currentSections, "## Exact Literals and Errors"),
            GetSection(previousSections, "## Exact Literals and Errors"),
            NormalizeMultiline(trimmedSynopsis, 1200),
            "- None explicitly captured.");

        var builder = new System.Text.StringBuilder();
        AppendSummarySection(builder, "## Task", task);
        AppendSummarySection(builder, "## Explicit Requirements", explicitRequirements);
        AppendSummarySection(builder, "## Files and Identifiers", filesAndIdentifiers);
        AppendSummarySection(builder, "## Exact Literals and Errors", exactLiteralsAndErrors, includeTrailingBlankLine: false);
        return builder.ToString().Trim();
    }

    private static AgentCompactionSerializerStatistics MergeStatistics(
        AgentCompactionSerializerStatistics left,
        AgentCompactionSerializerStatistics right)
        => new(
            OmittedToolResultCount: left.OmittedToolResultCount + right.OmittedToolResultCount,
            OmittedReasoningCount: left.OmittedReasoningCount + right.OmittedReasoningCount,
            OmittedAttachmentCount: left.OmittedAttachmentCount + right.OmittedAttachmentCount,
            DroppedMessageCount: left.DroppedMessageCount + right.DroppedMessageCount,
            SerializedToolResultCharacters: left.SerializedToolResultCharacters + right.SerializedToolResultCharacters,
            SerializedReasoningCharacters: left.SerializedReasoningCharacters + right.SerializedReasoningCharacters,
            ReducedOversizedAnchor: left.ReducedOversizedAnchor || right.ReducedOversizedAnchor,
            TotalToolCallCount: left.TotalToolCallCount + right.TotalToolCallCount,
            SerializedToolCallCount: left.SerializedToolCallCount + right.SerializedToolCallCount,
            CollapsedToolCallCount: left.CollapsedToolCallCount + right.CollapsedToolCallCount,
            TotalToolResultCount: left.TotalToolResultCount + right.TotalToolResultCount,
            SerializedToolResultCount: left.SerializedToolResultCount + right.SerializedToolResultCount,
            SerializedToolResultExcerptCount: left.SerializedToolResultExcerptCount + right.SerializedToolResultExcerptCount,
            TotalReasoningCount: left.TotalReasoningCount + right.TotalReasoningCount,
            SerializedReasoningCount: left.SerializedReasoningCount + right.SerializedReasoningCount,
            TotalAttachmentCount: left.TotalAttachmentCount + right.TotalAttachmentCount,
            SerializedAttachmentCount: left.SerializedAttachmentCount + right.SerializedAttachmentCount);

    private static FileActivity ExtractFileActivity(IReadOnlyList<AgentEvent> history)
    {
        var readFiles = new List<string>();
        var modifiedFiles = new List<string>();
        var seenReadFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenModifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var activity in history.OfType<AgentActivityEvent>().Reverse())
        {
            if (activity.Kind is not AgentActivityKind.ToolCall || activity.Details is not { } details)
            {
                continue;
            }

            AddPaths(details, "modifiedFiles", modifiedFiles, seenModifiedFiles);
            AddPaths(details, "readFiles", readFiles, seenReadFiles);
        }

        return new FileActivity(readFiles, modifiedFiles);
    }

    private static FileActivity BudgetFileActivityForSummary(
        FileActivity fileActivity,
        string? latestUserRequest,
        AgentCompactionSettings settings,
        int maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(fileActivity);
        ArgumentNullException.ThrowIfNull(settings);

        if (fileActivity.ReadFiles.Count == 0 && fileActivity.ModifiedFiles.Count == 0)
        {
            return fileActivity;
        }

        var share = settings.FileContextShareOfSummaryTarget >= 0
            ? Math.Min(settings.FileContextShareOfSummaryTarget, AgentCompactionSettings.MaxFileContextShareOfSummaryTarget)
            : AgentCompactionSettings.DefaultFileContextShareOfSummaryTarget;
        if (share <= 0)
        {
            return new FileActivity([], []);
        }

        var budgetTokens = Math.Max((long)Math.Floor(Math.Max(maxOutputTokens, 1) * share), 1L);
        var latestRequest = latestUserRequest ?? string.Empty;
        var modified = OrderPathsForSummary(fileActivity.ModifiedFiles, latestRequest).ToArray();
        var modifiedSet = new HashSet<string>(modified, StringComparer.OrdinalIgnoreCase);
        var read = OrderPathsForSummary(
                fileActivity.ReadFiles.Where(path => !modifiedSet.Contains(path)),
                latestRequest)
            .ToArray();

        var selectedModified = new List<string>();
        var selectedRead = new List<string>();

        foreach (var path in modified)
        {
            var candidateModified = selectedModified.Concat([path]).ToArray();
            var candidate = new FileActivity(selectedRead, candidateModified);
            var candidateRendered = BuildRelevantFilesSection(candidate);
            if (AgentTokenEstimator.EstimateTextTokens(candidateRendered) > budgetTokens && selectedModified.Count + selectedRead.Count > 0)
            {
                break;
            }

            selectedModified.Add(path);
        }

        foreach (var path in read)
        {
            var candidateRead = selectedRead.Concat([path]).ToArray();
            var candidate = new FileActivity(candidateRead, selectedModified);
            var candidateRendered = BuildRelevantFilesSection(candidate);
            if (AgentTokenEstimator.EstimateTextTokens(candidateRendered) > budgetTokens && selectedModified.Count + selectedRead.Count > 0)
            {
                break;
            }

            selectedRead.Add(path);
        }

        return new FileActivity(selectedRead, selectedModified);
    }

    private static IEnumerable<string> OrderPathsForSummary(IEnumerable<string> paths, string latestUserRequest)
    {
        return paths
            .Select((path, index) => new
            {
                Path = path,
                Index = index,
                Mentioned = IsPathMentioned(latestUserRequest, path),
            })
            .OrderByDescending(static item => item.Mentioned)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Path);
    }

    private static bool IsPathMentioned(string latestUserRequest, string path)
    {
        if (string.IsNullOrWhiteSpace(latestUserRequest) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (latestUserRequest.Contains(path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = System.IO.Path.GetFileName(path);
        return !string.IsNullOrWhiteSpace(fileName) &&
            latestUserRequest.Contains(fileName, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPaths(
        JsonElement details,
        string propertyName,
        ICollection<string> target,
        ISet<string> seenPaths)
    {
        if (!details.TryGetProperty(propertyName, out var property) || property.ValueKind is not JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in property.EnumerateArray())
        {
            var path = item.GetString();
            if (!string.IsNullOrWhiteSpace(path) && seenPaths.Add(path))
            {
                target.Add(path);
            }
        }
    }

    private static string CreateSystemPrompt(int maxOutputTokens)
        => $"""
            {SummarySystemPromptTemplate}

            Keep the output under roughly {maxOutputTokens} tokens.
            """;

    private static string CreateShrinkSystemPrompt(int maxOutputTokens, long checkpointTargetTokens)
        => $"""
            {ShrinkSystemPromptTemplate}

            Target roughly {checkpointTargetTokens} tokens when possible and never exceed roughly {maxOutputTokens} tokens.
            """;

    private static string CreateOversizedAnchorSystemPrompt(int maxOutputTokens)
        => $"""
            {OversizedAnchorSystemPromptTemplate}

            Keep the output under roughly {maxOutputTokens} tokens.
            """;

    private static string BuildShrinkRequestBody(
        string summary,
        string? latestUserRequest,
        FileActivity modelVisibleFileActivity,
        long checkpointTargetTokens)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("""<codealta-compaction-shrink-request version="1">""");
        AppendTag(builder, "target-tokens", checkpointTargetTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendTag(builder, "active-user-request", latestUserRequest?.Trim());
        AppendTag(builder, "relevant-files", BuildRelevantFilesSection(modelVisibleFileActivity));
        AppendTag(builder, "current-summary", summary);
        builder.Append("""</codealta-compaction-shrink-request>""");
        return builder.ToString();
    }

    private static void AppendTag(System.Text.StringBuilder builder, string tagName, string? value)
    {
        builder.Append('<').Append(tagName).AppendLine(">");
        builder.AppendLine(System.Security.SecurityElement.Escape(value ?? string.Empty) ?? string.Empty);
        builder.Append("</").Append(tagName).AppendLine(">");
    }

    private static bool HasRequiredSummarySections(string summary)
        => summary.Contains("## Objective", StringComparison.Ordinal) &&
           summary.Contains("## Active User Request", StringComparison.Ordinal) &&
           summary.Contains("## Constraints", StringComparison.Ordinal) &&
           summary.Contains("## Progress", StringComparison.Ordinal) &&
           summary.Contains("### Done", StringComparison.Ordinal) &&
           summary.Contains("### In Progress", StringComparison.Ordinal) &&
           summary.Contains("### Blocked", StringComparison.Ordinal) &&
           summary.Contains("## Decisions", StringComparison.Ordinal) &&
           summary.Contains("## Next Steps", StringComparison.Ordinal) &&
           summary.Contains("## Critical Context", StringComparison.Ordinal) &&
           summary.Contains("## Relevant Files", StringComparison.Ordinal);

    private static bool HasRequiredOversizedAnchorSynopsisSections(string synopsis)
        => synopsis.Contains("## Task", StringComparison.Ordinal) &&
           synopsis.Contains("## Explicit Requirements", StringComparison.Ordinal) &&
           synopsis.Contains("## Files and Identifiers", StringComparison.Ordinal) &&
           synopsis.Contains("## Exact Literals and Errors", StringComparison.Ordinal);

    private static Dictionary<string, string> ParseMarkdownSections(string summary)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentHeading = null;
        var builder = new System.Text.StringBuilder();

        foreach (var rawLine in summary.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.TrimEnd();
            if (IsSupportedSummaryHeading(line))
            {
                FlushSection(sections, currentHeading, builder);
                currentHeading = line.Trim();
                builder.Clear();
                continue;
            }

            if (currentHeading is not null)
            {
                builder.AppendLine(line);
            }
        }

        FlushSection(sections, currentHeading, builder);
        return sections;

        static void FlushSection(IDictionary<string, string> sections, string? heading, System.Text.StringBuilder content)
        {
            if (heading is null)
            {
                return;
            }

            var text = content.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sections[heading] = text;
            }
        }
    }

    private static Dictionary<string, string> ParseOversizedAnchorSections(string synopsis)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentHeading = null;
        var builder = new System.Text.StringBuilder();

        foreach (var rawLine in synopsis.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.TrimEnd();
            if (IsSupportedOversizedAnchorHeading(line))
            {
                FlushSection(sections, currentHeading, builder);
                currentHeading = line.Trim();
                builder.Clear();
                continue;
            }

            if (currentHeading is not null)
            {
                builder.AppendLine(line);
            }
        }

        FlushSection(sections, currentHeading, builder);
        return sections;

        static void FlushSection(IDictionary<string, string> sections, string? heading, System.Text.StringBuilder content)
        {
            if (heading is null)
            {
                return;
            }

            var text = content.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sections[heading] = text;
            }
        }
    }

    private static bool IsSupportedSummaryHeading(string line)
        => line is "## Objective"
            or "## Active User Request"
            or "## Constraints"
            or "## Progress"
            or "### Done"
            or "### In Progress"
            or "### Blocked"
            or "## Decisions"
            or "## Next Steps"
            or "## Critical Context"
            or "## Relevant Files";

    private static bool IsSupportedOversizedAnchorHeading(string line)
        => line is "## Task"
            or "## Explicit Requirements"
            or "## Files and Identifiers"
            or "## Exact Literals and Errors";

    private static string? GetSection(IReadOnlyDictionary<string, string>? sections, string heading)
        => sections is not null && sections.TryGetValue(heading, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim()
           ?? string.Empty;

    private static string ExtractLeadParagraph(string text, int maxCharacters)
    {
        var normalized = NormalizeMultiline(text, maxCharacters);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var breakIndex = normalized.IndexOfAny(['.', '\n']);
        if (breakIndex > 0 && breakIndex < maxCharacters / 2)
        {
            return normalized[..(breakIndex + 1)].Trim();
        }

        return normalized;
    }

    private static string NormalizeMultiline(string? text, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var normalized = string.Join(Environment.NewLine, lines);
        if (normalized.Length <= maxCharacters)
        {
            return normalized;
        }

        return normalized[..Math.Max(maxCharacters - 3, 1)].TrimEnd() + "...";
    }

    private static string BuildRelevantFilesSection(FileActivity fileActivity)
    {
        var lines = new List<string>();
        foreach (var path in fileActivity.ModifiedFiles)
        {
            lines.Add($"- Modified: {path}");
        }

        foreach (var path in fileActivity.ReadFiles.Where(path => !fileActivity.ModifiedFiles.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            lines.Add($"- Read: {path}");
        }

        return lines.Count == 0 ? "- None tracked." : string.Join(Environment.NewLine, lines);
    }

    private static string BuildFallbackCriticalContext(string summary, int maxOutputTokens)
    {
        var maxCharacters = Math.Max(Math.Min(maxOutputTokens * 6, 2400), 480);
        var normalized = NormalizeMultiline(summary, maxCharacters);
        return string.IsNullOrWhiteSpace(normalized)
            ? "- Original draft summary was unavailable."
            : $"- Original draft summary:{Environment.NewLine}{normalized}";
    }

    private static void AppendSummarySection(
        System.Text.StringBuilder builder,
        string heading,
        string content,
        bool includeTrailingBlankLine = true)
    {
        builder.AppendLine(heading);
        builder.AppendLine(string.IsNullOrWhiteSpace(content) ? "- None recorded." : content.Trim());
        if (includeTrailingBlankLine)
        {
            builder.AppendLine();
        }
    }

    private static string BuildOversizedAnchorRequestBody(string serializedAnchor, string? previousSynopsis)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("""<codealta-oversized-anchor-request version="1">""");
        AppendTag(builder, "mode", string.IsNullOrWhiteSpace(previousSynopsis) ? "initial" : "update");
        if (!string.IsNullOrWhiteSpace(previousSynopsis))
        {
            AppendTag(builder, "previous-anchor-synopsis", previousSynopsis);
        }

        AppendTag(builder, "latest-user-message", serializedAnchor);
        builder.Append("""</codealta-oversized-anchor-request>""");
        return builder.ToString();
    }

    private static string SerializeOversizedAnchorMessage(AgentConversationMessage message)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case AgentMessagePart.Text text when !string.IsNullOrWhiteSpace(text.Value):
                    builder.AppendLine($"[User] {text.Value.Trim()}");
                    break;
                case AgentMessagePart.Uri uri:
                    builder.AppendLine($"[Attachment] {uri.Name ?? uri.MediaType ?? "uri"}: {uri.Value}");
                    break;
                case AgentMessagePart.Data data:
                    builder.AppendLine($"[Attachment] inline {data.Name ?? data.MediaType}; base64 omitted ({data.Base64Data.Length} chars)");
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    private static IReadOnlyList<string> SplitTextByBudget(string text, int maxChunkCharacters)
    {
        if (string.IsNullOrWhiteSpace(text) || maxChunkCharacters <= 0 || text.Length <= maxChunkCharacters)
        {
            return [text];
        }

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(maxChunkCharacters, text.Length - start);
            var end = start + length;
            if (end < text.Length)
            {
                var breakIndex = text.LastIndexOfAny(['\n', '\r', ' ', '\t'], end - 1, length);
                if (breakIndex > start + (length / 2))
                {
                    end = breakIndex + 1;
                }
            }

            var chunk = text[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                chunks.Add(chunk);
            }

            start = end;
        }

        return chunks;
    }

    private sealed record FileActivity(IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles);
}
