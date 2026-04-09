using System.Text;

namespace CodeAlta.Agent.LocalRuntime.Compaction;

internal static class LocalAgentTokenEstimator
{
    public static LocalAgentTokenEstimate EstimatePromptTokens(
        string? systemMessage,
        string? developerInstructions,
        IReadOnlyList<LocalAgentConversationMessage> conversation,
        AgentSessionUsage? usage)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var expectedMessageCount = conversation.Count;
        if (usage?.Window is { CurrentTokens: > 0 } window &&
            window.MessageCount == expectedMessageCount)
        {
            return new LocalAgentTokenEstimate(window.CurrentTokens.Value, "provider-window", IsEstimated: false);
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

        return new LocalAgentTokenEstimate(Math.Max(estimatedTokens, 1), "local-heuristic", IsEstimated: true);
    }

    public static long EstimateMessage(LocalAgentConversationMessage message)
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

    private static long EstimatePart(LocalAgentMessagePart part)
    {
        return part switch
        {
            LocalAgentMessagePart.Text text => EstimateText(text.Value) + 4,
            LocalAgentMessagePart.Reasoning reasoning => EstimateText(reasoning.Value) + EstimateText(reasoning.ProtectedData) + 8,
            LocalAgentMessagePart.ToolCall toolCall => EstimateText(toolCall.Name) + EstimateText(toolCall.Arguments.GetRawText()) + 16,
            LocalAgentMessagePart.ToolResult toolResult => EstimateToolResult(toolResult.Result) + 16,
            LocalAgentMessagePart.Uri uri => EstimateText(uri.Value) + EstimateText(uri.MediaType) + EstimateText(uri.Name) + 8,
            LocalAgentMessagePart.Data data => EstimateText(data.Name) + EstimateText(data.MediaType) + Math.Max(data.Base64Data.Length / 8, 32),
            _ => 4,
        };
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
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var condensedLength = text.Trim().Length;
        return Math.Max((condensedLength + 3) / 4, 1);
    }
}
