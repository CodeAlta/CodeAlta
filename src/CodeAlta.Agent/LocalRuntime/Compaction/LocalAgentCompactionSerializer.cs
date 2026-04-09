using System.Text;

namespace CodeAlta.Agent.LocalRuntime.Compaction;

internal static class LocalAgentCompactionSerializer
{
    private const int MaxToolResultCharacters = 2000;

    public static string SerializeForSummary(IReadOnlyList<LocalAgentConversationMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            foreach (var line in SerializeMessage(message))
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString().Trim();
    }

    private static IEnumerable<string> SerializeMessage(LocalAgentConversationMessage message)
    {
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case LocalAgentMessagePart.Text text:
                    yield return $"[{GetRoleLabel(message.Role)}] {text.Value.Trim()}";
                    break;
                case LocalAgentMessagePart.Reasoning reasoning when !string.IsNullOrWhiteSpace(reasoning.Value):
                    yield return $"[Assistant reasoning] {reasoning.Value!.Trim()}";
                    break;
                case LocalAgentMessagePart.ToolCall toolCall:
                    yield return $"[Assistant tool calls] {toolCall.Name} {toolCall.Arguments.GetRawText()}";
                    break;
                case LocalAgentMessagePart.ToolResult toolResult:
                    yield return $"[Tool result] {Truncate(RenderToolResult(toolResult.Result), MaxToolResultCharacters)}";
                    break;
                case LocalAgentMessagePart.Uri uri:
                    yield return $"[Attachment] {uri.Value}";
                    break;
                case LocalAgentMessagePart.Data data:
                    yield return $"[Attachment] {data.Name ?? data.MediaType}";
                    break;
            }
        }
    }

    private static string GetRoleLabel(LocalAgentConversationRole role)
        => role switch
        {
            LocalAgentConversationRole.User => "User",
            LocalAgentConversationRole.Assistant => "Assistant",
            LocalAgentConversationRole.Tool => "Tool result",
            LocalAgentConversationRole.System => "System",
            _ => "Message",
        };

    private static string RenderToolResult(AgentToolResult result)
    {
        var segments = result.Items.Select(static item => item switch
        {
            AgentToolResultItem.Text text => text.Value,
            AgentToolResultItem.ImageUrl imageUrl => imageUrl.Url,
            _ => string.Empty,
        });
        var rendered = string.Join(Environment.NewLine, segments.Where(static value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(rendered) ? (result.Error ?? "(no output)") : rendered;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}

