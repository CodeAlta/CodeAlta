using System.Text;

namespace CodeAlta.Agent.Runtime.Compaction;

internal static class AgentTokenEstimator
{
    public static AgentTokenEstimate EstimatePromptTokens(
        string? systemMessage,
        string? developerInstructions,
        IReadOnlyList<AgentConversationMessage> conversation,
        AgentSessionUsage? usage)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (TryGetWindowSnapshotEstimate(conversation, usage, out var windowEstimate))
        {
            return windowEstimate;
        }

        if (!HasLeadingCheckpoint(conversation) &&
            usage?.LastOperation is { } lastOperation &&
            TryGetLastOperationWindowEstimate(conversation, lastOperation, out var lastOperationEstimate))
        {
            return lastOperationEstimate;
        }

        var estimatedTokens = 0L;
        if (!string.IsNullOrWhiteSpace(systemMessage))
        {
            estimatedTokens += EstimateText(systemMessage) + 8;
        }

        if (!string.IsNullOrWhiteSpace(developerInstructions))
        {
            estimatedTokens += EstimateText(developerInstructions) + 8;
        }

        foreach (var message in conversation)
        {
            estimatedTokens += EstimateMessage(message);
        }

        return new AgentTokenEstimate(Math.Max(estimatedTokens, 1), "local-heuristic", IsEstimated: true);
    }

    public static long EstimateMessage(AgentConversationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var total = 6L;
        foreach (var part in message.Parts)
        {
            total += EstimatePart(part);
        }

        return total;
    }

    public static long EstimateCheckpointTokens(string summary)
        => EstimateText(summary) + 16;

    public static long EstimateTextTokens(string? text)
        => EstimateText(text);

    private static bool HasLeadingCheckpoint(IReadOnlyList<AgentConversationMessage> conversation)
        => conversation.Count > 0 && AgentCompactionCheckpoint.TryExtractSummary(conversation[0]) is not null;

    private static bool TryGetWindowSnapshotEstimate(
        IReadOnlyList<AgentConversationMessage> conversation,
        AgentSessionUsage? usage,
        out AgentTokenEstimate estimate)
    {
        if (usage?.Window is not { CurrentTokens: > 0, MessageCount: >= 0 } window)
        {
            estimate = default!;
            return false;
        }

        var messageCount = window.MessageCount!.Value;
        if (messageCount > conversation.Count)
        {
            estimate = default!;
            return false;
        }

        var trailingTokens = 0L;
        for (var index = messageCount; index < conversation.Count; index++)
        {
            trailingTokens += EstimateMessage(conversation[index]);
        }

        var isAuthoritativeWindow = string.Equals(window.Label, "Active context window", StringComparison.Ordinal);
        var source = trailingTokens == 0
            ? (isAuthoritativeWindow ? "provider-window" : "window-snapshot")
            : (isAuthoritativeWindow ? "provider-window+local-tail" : "window-snapshot+local-tail");
        estimate = new AgentTokenEstimate(
            window.CurrentTokens!.Value + trailingTokens,
            source,
            IsEstimated: trailingTokens > 0 || !isAuthoritativeWindow);
        return true;
    }

    private static bool TryGetLastOperationWindowEstimate(
        IReadOnlyList<AgentConversationMessage> conversation,
        AgentOperationUsageSnapshot lastOperation,
        out AgentTokenEstimate estimate)
    {
        var baselineTokens = Sum(lastOperation.InputTokens, lastOperation.OutputTokens);
        if (baselineTokens is not > 0)
        {
            estimate = default!;
            return false;
        }

        var lastAssistantIndex = FindLastAssistantMessageIndex(conversation);
        if (lastAssistantIndex < 0)
        {
            estimate = default!;
            return false;
        }

        var trailingTokens = 0L;
        for (var index = lastAssistantIndex + 1; index < conversation.Count; index++)
        {
            trailingTokens += EstimateMessage(conversation[index]);
        }

        estimate = new AgentTokenEstimate(
            baselineTokens.Value + trailingTokens,
            "provider-last-operation+local-tail",
            IsEstimated: true);
        return true;
    }

    private static long EstimatePart(AgentMessagePart part)
    {
        return part switch
        {
            AgentMessagePart.Text text => EstimateText(text.Value) + 4,
            AgentMessagePart.Reasoning reasoning => EstimateText(reasoning.Value) + EstimateText(reasoning.ProtectedData) + 8,
            AgentMessagePart.ToolCall toolCall => EstimateText(toolCall.Name) + EstimateText(toolCall.Arguments.GetRawText()) + 16,
            AgentMessagePart.ToolResult toolResult => EstimateToolResult(toolResult.Result) + 16,
            AgentMessagePart.Uri uri => EstimateText(uri.Value) + EstimateText(uri.MediaType) + EstimateText(uri.Name) + 8,
            AgentMessagePart.Data data => EstimateData(data),
            _ => 4,
        };
    }

    private static long EstimateData(AgentMessagePart.Data data)
    {
        var metadataTokens = EstimateText(data.Name) + EstimateText(data.MediaType) + 8;
        if (AgentMediaCompaction.IsImage(data.MediaType))
        {
            return metadataTokens + 1_024;
        }

        return metadataTokens + Math.Max(data.Base64Data.Length / 8, 32);
    }

    private static long EstimateToolResult(AgentToolResult result)
    {
        var builder = new StringBuilder();
        foreach (var item in result.Items)
        {
            switch (item)
            {
                case AgentToolResultItem.Text text:
                    builder.AppendLine(text.Value);
                    break;
                case AgentToolResultItem.ImageUrl imageUrl:
                    builder.AppendLine(imageUrl.Url);
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            builder.AppendLine(result.Error);
        }

        return EstimateText(builder.ToString());
    }

    private static long EstimateText(string? text)
        => string.IsNullOrWhiteSpace(text) ? 0 : TokenEstimator.Estimate(text.AsSpan().Trim());

    private static long? Sum(long? left, long? right)
        => left.HasValue || right.HasValue ? (left ?? 0) + (right ?? 0) : null;

    private static int FindLastAssistantMessageIndex(IReadOnlyList<AgentConversationMessage> conversation)
    {
        for (var index = conversation.Count - 1; index >= 0; index--)
        {
            if (conversation[index].Role is AgentConversationRole.Assistant)
            {
                return index;
            }
        }

        return -1;
    }
}
