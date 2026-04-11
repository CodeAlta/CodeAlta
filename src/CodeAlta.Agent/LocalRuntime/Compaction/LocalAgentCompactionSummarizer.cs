using System.Text.Json;

namespace CodeAlta.Agent.LocalRuntime.Compaction;

internal sealed class LocalAgentCompactionSummarizer(ILocalAgentCompactionSummaryExecutor executor)
{
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
        When a previous summary is provided, update it rather than rewriting from scratch.
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

    private readonly ILocalAgentCompactionSummaryExecutor _executor = executor ?? throw new ArgumentNullException(nameof(executor));

    public async Task<LocalAgentCompactionResult> SummarizeAsync(
        AgentBackendId backendId,
        LocalAgentProviderDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        LocalAgentSessionState state,
        LocalAgentCompactionPreparation preparation,
        IReadOnlyList<AgentEvent> history,
        string? latestUserRequest,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(history);

        var settings = provider.Compaction ?? LocalAgentCompactionSettings.Default;
        var fileActivity = ExtractFileActivity(history);
        string? oversizedAnchorSynopsis = null;
        var oversizedAnchorInvocationCount = 0;
        if (preparation.OversizedAnchorMessage is not null)
        {
            if (!settings.AllowOversizedAnchorReduction)
            {
                throw new InvalidOperationException("The latest user message exceeds the resolved prompt budget and oversized-anchor reduction is disabled.");
            }

            (oversizedAnchorSynopsis, oversizedAnchorInvocationCount) = await ReduceOversizedAnchorAsync(
                    backendId,
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
                backendId,
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
                oversizedAnchorSynopsis,
                oversizedAnchorInvocationCount,
                currentPass: 1,
                cancellationToken)
            .ConfigureAwait(false);
        return result with
        {
            ReadFiles = fileActivity.ReadFiles,
            ModifiedFiles = fileActivity.ModifiedFiles,
        };
    }

    private async Task<LocalAgentCompactionResult> SummarizePreparationAsync(
        AgentBackendId backendId,
        LocalAgentProviderDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        LocalAgentSessionState state,
        LocalAgentCompactionPreparation preparation,
        string? latestUserRequest,
        int maxOutputTokens,
        LocalAgentCompactionSettings settings,
        FileActivity fileActivity,
        string? oversizedAnchorSynopsis,
        int additionalSummaryCallCount,
        int currentPass,
        CancellationToken cancellationToken)
    {
        var serialization = LocalAgentCompactionSerializer.BuildSummaryRequestBody(
            preparation,
            latestUserRequest,
            fileActivity.ReadFiles,
            fileActivity.ModifiedFiles,
            settings,
            oversizedAnchorSynopsis,
            preparation.OversizedAnchorMessage is not null);

        var chunks = GetChunksIfNeeded(
            preparation,
            latestUserRequest,
            fileActivity,
            settings,
            oversizedAnchorSynopsis);

        if (serialization.EstimatedInputTokens > settings.SummaryInputTokens &&
            currentPass < settings.MaxChunkPasses &&
            chunks.Count > 1)
        {
            return await SummarizeChunkedAsync(
                    backendId,
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
                    oversizedAnchorSynopsis,
                    additionalSummaryCallCount,
                    currentPass,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (serialization.EstimatedInputTokens > settings.SummaryInputTokens)
        {
            throw new InvalidOperationException("Compaction input exceeded the configured summary-input budget after bounded chunking.");
        }

        var response = await ExecuteSummaryRequestAsync(
                backendId,
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
        ValidateSummary(response.Summary, maxOutputTokens);

        return new LocalAgentCompactionResult(
            Summary: response.Summary,
            AnchorContentId: preparation.AnchorContentId,
            IsSplitTurn: preparation.IsSplitTurn,
            OversizedAnchorReduced: serialization.Statistics.ReducedOversizedAnchor,
            TokensBefore: preparation.TokensBefore.Tokens,
            TokensAfter: null,
            MessagesSummarized: preparation.MessagesToSummarize.Count,
            ChunkCount: Math.Max(1 + additionalSummaryCallCount, 1),
            CompressionRatio: null,
            SerializerStatistics: serialization.Statistics,
            ReadFiles: fileActivity.ReadFiles,
            ModifiedFiles: fileActivity.ModifiedFiles);
    }

    private async Task<LocalAgentCompactionResult> SummarizeChunkedAsync(
        AgentBackendId backendId,
        LocalAgentProviderDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        LocalAgentSessionState state,
        LocalAgentCompactionPreparation preparation,
        string? latestUserRequest,
        int maxOutputTokens,
        LocalAgentCompactionSettings settings,
        FileActivity fileActivity,
        string? oversizedAnchorSynopsis,
        int additionalSummaryCallCount,
        int currentPass,
        CancellationToken cancellationToken)
    {
        var chunks = GetChunksIfNeeded(preparation, latestUserRequest, fileActivity, settings, oversizedAnchorSynopsis);
        if (chunks.Count <= 1)
        {
            throw new InvalidOperationException("Compaction input exceeded the summary-input budget, but chunking could not reduce it further.");
        }

        var rollingSummary = preparation.PreviousSummary;
        var aggregatedStatistics = new LocalAgentCompactionSerializerStatistics(
            OmittedToolResultCount: 0,
            OmittedReasoningCount: 0,
            OmittedAttachmentCount: 0,
            DroppedMessageCount: 0,
            SerializedToolResultCharacters: 0,
            SerializedReasoningCharacters: 0,
            ReducedOversizedAnchor: false);
        LocalAgentCompactionResult? finalResult = null;
        var totalChunkCount = 0;

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
                    backendId,
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
                    oversizedAnchorSynopsis,
                    additionalSummaryCallCount: 0,
                    currentPass + 1,
                    cancellationToken)
                .ConfigureAwait(false);

            rollingSummary = chunkResult.Summary;
            aggregatedStatistics = MergeStatistics(aggregatedStatistics, chunkResult.SerializerStatistics);
            totalChunkCount += chunkResult.ChunkCount;
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
                    backendId,
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
                    oversizedAnchorSynopsis,
                    additionalSummaryCallCount: 0,
                    currentPass + 1,
                    cancellationToken)
                .ConfigureAwait(false);

            rollingSummary = mergeResult.Summary;
            aggregatedStatistics = MergeStatistics(aggregatedStatistics, mergeResult.SerializerStatistics);
            totalChunkCount += mergeResult.ChunkCount;
            finalResult = mergeResult;
        }

        return finalResult is null
            ? throw new InvalidOperationException("Chunked compaction did not produce a final summary.")
            : finalResult with
            {
                ChunkCount = Math.Max(totalChunkCount + additionalSummaryCallCount, chunks.Count + additionalSummaryCallCount),
                SerializerStatistics = aggregatedStatistics,
            };
    }

    private IReadOnlyList<IReadOnlyList<LocalAgentConversationMessage>> GetChunksIfNeeded(
        LocalAgentCompactionPreparation preparation,
        string? latestUserRequest,
        FileActivity fileActivity,
        LocalAgentCompactionSettings settings,
        string? oversizedAnchorSynopsis)
        => LocalAgentCompactionChunker.CreateChunks(
            preparation.MessagesToSummarize,
            settings.SummaryInputTokens,
            chunkMessages => LocalAgentCompactionSerializer.BuildSummaryRequestBody(
                    preparation with
                    {
                        MessagesToSummarize = chunkMessages,
                        TurnPrefixMessages = [],
                        MessagesToKeep = [],
                    },
                    latestUserRequest,
                    fileActivity.ReadFiles,
                    fileActivity.ModifiedFiles,
                    settings,
                    oversizedAnchorSynopsis,
                    preparation.OversizedAnchorMessage is not null)
                .EstimatedInputTokens);

    private async Task<(string Synopsis, int InvocationCount)> ReduceOversizedAnchorAsync(
        AgentBackendId backendId,
        LocalAgentProviderDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        LocalAgentSessionState state,
        LocalAgentConversationMessage oversizedAnchorMessage,
        LocalAgentCompactionSettings settings,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var serializedAnchor = SerializeOversizedAnchorMessage(oversizedAnchorMessage);
        if (string.IsNullOrWhiteSpace(serializedAnchor))
        {
            throw new InvalidOperationException("The oversized latest user message could not be reduced because it had no serializable content.");
        }

        return await ReduceOversizedAnchorTextAsync(
                backendId,
                provider,
                sessionId,
                modelId,
                modelInfo,
                workingDirectory,
                state,
                serializedAnchor,
                previousSynopsis: null,
                settings,
                Math.Min(maxOutputTokens, Math.Max(settings.SummaryOutputTokens / 2, 256)),
                currentPass: 1,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(string Synopsis, int InvocationCount)> ReduceOversizedAnchorTextAsync(
        AgentBackendId backendId,
        LocalAgentProviderDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        LocalAgentSessionState state,
        string serializedAnchor,
        string? previousSynopsis,
        LocalAgentCompactionSettings settings,
        int maxOutputTokens,
        int currentPass,
        CancellationToken cancellationToken)
    {
        var requestBody = BuildOversizedAnchorRequestBody(serializedAnchor, previousSynopsis);
        var requestTokens = LocalAgentTokenEstimator.EstimateTextTokens(requestBody);
        if (requestTokens > settings.SummaryInputTokens)
        {
            if (currentPass >= settings.MaxChunkPasses)
            {
                throw new InvalidOperationException("Oversized-anchor reduction exceeded the configured summary-input budget after bounded chunking.");
            }

            var overheadTokens = LocalAgentTokenEstimator.EstimateTextTokens(BuildOversizedAnchorRequestBody(string.Empty, previousSynopsis));
            var availableChunkTokens = Math.Max(settings.SummaryInputTokens - (int)overheadTokens, 32);
            var chunkTexts = SplitTextByBudget(serializedAnchor, Math.Max(availableChunkTokens * 4, 128));
            if (chunkTexts.Count <= 1 && serializedAnchor.Length > 1)
            {
                chunkTexts = SplitTextByBudget(serializedAnchor, Math.Max(serializedAnchor.Length / 2, 64));
            }

            if (chunkTexts.Count <= 1)
            {
                throw new InvalidOperationException("Oversized-anchor reduction could not split the latest user input enough to fit the configured summary-input budget.");
            }

            var rollingSynopsis = previousSynopsis;
            var totalInvocations = 0;
            foreach (var chunkText in chunkTexts)
            {
                var (chunkSynopsis, invocationCount) = await ReduceOversizedAnchorTextAsync(
                        backendId,
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

        var response = await ExecuteSummaryRequestAsync(
                backendId,
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
        ValidateOversizedAnchorSynopsis(response.Summary, maxOutputTokens);
        return (response.Summary, 1);
    }

    private async Task<LocalAgentCompactionSummaryResponse> ExecuteSummaryRequestAsync(
        AgentBackendId backendId,
        LocalAgentProviderDescriptor provider,
        string sessionId,
        string? modelId,
        AgentModelInfo? modelInfo,
        string? workingDirectory,
        LocalAgentSessionState state,
        string systemMessage,
        string userMessage,
        int maxOutputTokens,
        CancellationToken cancellationToken)
        => await _executor.ExecuteAsync(
                new LocalAgentCompactionSummaryRequest(
                    BackendId: backendId,
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

    private static void ValidateSummary(string summary, int maxOutputTokens)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new InvalidOperationException("The compaction summarizer returned an empty summary.");
        }

        var estimatedTokens = LocalAgentTokenEstimator.EstimateTextTokens(summary);
        if (estimatedTokens > Math.Max(maxOutputTokens * 2L, 256L))
        {
            throw new InvalidOperationException("The compaction summarizer returned a summary that exceeds the configured checkpoint budget.");
        }

        if (!summary.Contains("## Objective", StringComparison.Ordinal) ||
            !summary.Contains("## Active User Request", StringComparison.Ordinal) ||
            !summary.Contains("## Relevant Files", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The compaction summarizer returned a malformed structured summary.");
        }
    }

    private static void ValidateOversizedAnchorSynopsis(string synopsis, int maxOutputTokens)
    {
        if (string.IsNullOrWhiteSpace(synopsis))
        {
            throw new InvalidOperationException("The oversized-anchor reducer returned an empty synopsis.");
        }

        var estimatedTokens = LocalAgentTokenEstimator.EstimateTextTokens(synopsis);
        if (estimatedTokens > Math.Max(maxOutputTokens * 2L, 192L))
        {
            throw new InvalidOperationException("The oversized-anchor reducer returned a synopsis that exceeds the configured checkpoint budget.");
        }

        if (!synopsis.Contains("## Task", StringComparison.Ordinal) ||
            !synopsis.Contains("## Explicit Requirements", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The oversized-anchor reducer returned a malformed synopsis.");
        }
    }

    private static LocalAgentCompactionSerializerStatistics MergeStatistics(
        LocalAgentCompactionSerializerStatistics left,
        LocalAgentCompactionSerializerStatistics right)
        => new(
            OmittedToolResultCount: left.OmittedToolResultCount + right.OmittedToolResultCount,
            OmittedReasoningCount: left.OmittedReasoningCount + right.OmittedReasoningCount,
            OmittedAttachmentCount: left.OmittedAttachmentCount + right.OmittedAttachmentCount,
            DroppedMessageCount: left.DroppedMessageCount + right.DroppedMessageCount,
            SerializedToolResultCharacters: left.SerializedToolResultCharacters + right.SerializedToolResultCharacters,
            SerializedReasoningCharacters: left.SerializedReasoningCharacters + right.SerializedReasoningCharacters,
            ReducedOversizedAnchor: left.ReducedOversizedAnchor || right.ReducedOversizedAnchor);

    private static FileActivity ExtractFileActivity(IReadOnlyList<AgentEvent> history)
    {
        var readFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var modifiedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var activity in history.OfType<AgentActivityEvent>())
        {
            if (activity.Kind is not AgentActivityKind.ToolCall || activity.Details is not { } details)
            {
                continue;
            }

            AddPaths(details, "readFiles", readFiles);
            AddPaths(details, "modifiedFiles", modifiedFiles);
        }

        return new FileActivity([.. readFiles], [.. modifiedFiles]);
    }

    private static void AddPaths(JsonElement details, string propertyName, ISet<string> target)
    {
        if (!details.TryGetProperty(propertyName, out var property) || property.ValueKind is not JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in property.EnumerateArray())
        {
            var path = item.GetString();
            if (!string.IsNullOrWhiteSpace(path))
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

    private static string CreateOversizedAnchorSystemPrompt(int maxOutputTokens)
        => $"""
            {OversizedAnchorSystemPromptTemplate}

            Keep the output under roughly {maxOutputTokens} tokens.
            """;

    private static void AppendTag(System.Text.StringBuilder builder, string tagName, string? value)
    {
        builder.Append('<').Append(tagName).AppendLine(">");
        builder.AppendLine(System.Security.SecurityElement.Escape(value ?? string.Empty) ?? string.Empty);
        builder.Append("</").Append(tagName).AppendLine(">");
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

    private static string SerializeOversizedAnchorMessage(LocalAgentConversationMessage message)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case LocalAgentMessagePart.Text text when !string.IsNullOrWhiteSpace(text.Value):
                    builder.AppendLine($"[User] {text.Value.Trim()}");
                    break;
                case LocalAgentMessagePart.Uri uri:
                    builder.AppendLine($"[Attachment] {uri.Name ?? uri.MediaType ?? "uri"}: {uri.Value}");
                    break;
                case LocalAgentMessagePart.Data data:
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
