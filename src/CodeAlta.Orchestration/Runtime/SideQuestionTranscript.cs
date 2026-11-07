using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Writes the conversation of a session as the text a side question is asked with: what the user and the assistant
/// wrote, the tools called and the beginning of what they gave, the latest part of it when all does not fit.
/// </summary>
internal static class SideQuestionTranscript
{
    // About four characters a token; the transcript takes half of the context, the rest is for the answer.
    internal const int DefaultBudget = 200_000;
    internal const int MaximumBudget = 800_000;
    internal const int ToolArgumentsLength = 300;
    internal const int ToolResultLength = 1_500;

    /// <summary>The characters a transcript may hold for a model with that context, in tokens; null when it is not known.</summary>
    internal static int BudgetFor(long? contextTokens)
        => contextTokens is > 0 ? (int)(Math.Min(contextTokens.Value, MaximumBudget / 2) * 2) : DefaultBudget;

    /// <summary>The prompt of the request: the transcript, then the question.</summary>
    internal static string Prompt(string transcript, string question)
        => $"""
            <session_transcript>
            {transcript}
            </session_transcript>

            Side question: {question}
            """;

    /// <summary>Writes the conversation, keeping its latest messages within <paramref name="budget"/> characters.</summary>
    internal static string Build(IReadOnlyList<AgentConversationMessage> conversation, int budget)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        var blocks = new List<string>();
        var length = 0;
        for (var i = conversation.Count - 1; i >= 0; i--)
        {
            var block = Render(conversation[i]);
            if (block.Length == 0) continue;
            var separator = blocks.Count == 0 ? 0 : 2;
            if (block.Length > budget - length - separator)
            {
                // Keep the end of the latest message even when it alone exceeds the budget.
                if (blocks.Count == 0)
                {
                    blocks.Add(budget == 1 ? "…" : "…" + block[^(budget - 1)..]);
                }
                else
                {
                    const string omitted = "[earlier messages omitted]";
                    if (omitted.Length + 2 <= budget - length) blocks.Add(omitted);
                }
                break;
            }

            blocks.Add(block);
            length += block.Length + separator;
        }

        blocks.Reverse();
        return string.Join("\n\n", blocks);
    }

    private static string Render(AgentConversationMessage message)
    {
        var text = new StringBuilder();
        foreach (var part in message.Parts)
        {
            var line = part switch
            {
                AgentMessagePart.Text value => value.Value.Trim(),
                AgentMessagePart.ToolCall call => $"[tool call {call.Name}: {Cut(call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "" : call.Arguments.GetRawText(), ToolArgumentsLength)}]",
                AgentMessagePart.ToolResult result => $"[tool result{(result.Result.Success ? "" : " (failed)")}: {Cut(ResultText(result.Result), ToolResultLength)}]",
                AgentMessagePart.Data data => $"[{data.MediaType}{(data.Name is null ? "" : " " + data.Name)}]",
                AgentMessagePart.Uri uri => $"[{uri.Name ?? uri.Value}]",
                // Reasoning is the model's own: the side question is about what was said and done.
                _ => "",
            };
            if (line.Length == 0) continue;
            if (text.Length > 0) text.Append('\n');
            text.Append(line);
        }

        if (text.Length == 0) return "";
        var role = message.Role switch
        {
            AgentConversationRole.User => "User",
            AgentConversationRole.Assistant => "Assistant",
            AgentConversationRole.Tool => "Tool",
            _ => "System",
        };
        return $"### {role}\n{text}";
    }

    private static string ResultText(AgentToolResult result)
    {
        var text = string.Join("\n", result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));
        return text.Length > 0 ? text : result.Error ?? "";
    }

    private static string Cut(string value, int maximum)
    {
        value = value.Trim();
        return value.Length <= maximum ? value : value[..maximum] + "…";
    }
}
