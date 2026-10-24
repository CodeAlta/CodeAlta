using System.Text;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Harness;

/// <summary>
/// Renders the conversation of a CodeAlta session for an agent harness that keeps its own conversation (an agent CLI)
/// and joins a session another provider answered: it is given what was said before as context of its next prompt.
/// </summary>
public static class HarnessHistory
{
    private const int HistoryLimit = 120_000;
    private const int ToolArgumentsLimit = 600;
    private const int ToolResultLimit = 1_500;

    /// <summary>The tag that opens the rendering of the earlier conversation.</summary>
    public const string PreviousConversationOpening = "<codealta_previous_conversation>";

    /// <summary>
    /// Renders the part of a session the harness did not take part in, to give it as context with the next prompt:
    /// the texts, the tool calls with their arguments and the tool results, shortened, the most recent kept when the
    /// whole is too long.
    /// </summary>
    /// <param name="history">The messages of the session before the prompt that starts.</param>
    /// <returns>The rendering in a <c>&lt;codealta_previous_conversation&gt;</c> block, or <see langword="null" /> when there is nothing to tell.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="history" /> is <see langword="null" />.</exception>
    public static string? CreatePreviousConversation(IReadOnlyList<AgentConversationMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var builder = new StringBuilder();
        foreach (var message in history)
        {
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case AgentMessagePart.Text text when !string.IsNullOrWhiteSpace(text.Value):
                        builder.Append(message.Role switch
                        {
                            AgentConversationRole.User => "[user]\n",
                            AgentConversationRole.Assistant => "[assistant]\n",
                            AgentConversationRole.System => "[system]\n",
                            _ => "[tool]\n",
                        });
                        builder.AppendLine(text.Value.Trim());
                        builder.AppendLine();
                        break;
                    case AgentMessagePart.ToolCall toolCall:
                        builder.Append("[assistant called tool ").Append(toolCall.Name).Append("]\n");
                        builder.AppendLine(Truncate(toolCall.Arguments.GetRawText(), ToolArgumentsLimit));
                        builder.AppendLine();
                        break;
                    case AgentMessagePart.ToolResult toolResult:
                        builder.Append(toolResult.Result.Success ? "[tool result]\n" : "[tool error]\n");
                        builder.AppendLine(Truncate(RenderToolResult(toolResult.Result), ToolResultLimit));
                        builder.AppendLine();
                        break;
                    case AgentMessagePart.Data or AgentMessagePart.Uri:
                        builder.AppendLine(message.Role is AgentConversationRole.User ? "[user attached a file]" : "[attachment]");
                        builder.AppendLine();
                        break;
                }
            }
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var transcript = builder.ToString().TrimEnd();
        if (transcript.Length > HistoryLimit)
        {
            transcript = "(the beginning of the conversation is omitted)\n\n" + transcript[^HistoryLimit..];
        }

        return PreviousConversationOpening + "\n" +
               "The conversation below took place earlier in this CodeAlta session, before you joined it. It is context for the request that follows, not a request by itself.\n\n" +
               transcript +
               "\n</codealta_previous_conversation>";
    }

    /// <summary>Joins the text of a tool result; an image is named, not included.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The text, or the error of the result when it has no text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> is <see langword="null" />.</exception>
    public static string RenderToolResult(AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        foreach (var item in result.Items)
        {
            switch (item)
            {
                case AgentToolResultItem.Text text:
                    AppendLine(builder, text.Value);
                    break;
                case AgentToolResultItem.ImageUrl imageUrl:
                    AppendLine(builder, imageUrl.Url);
                    break;
                case AgentToolResultItem.Image image:
                    AppendLine(builder, $"[image {image.MediaType}]");
                    break;
                case AgentToolResultItem.LocalImage localImage:
                    AppendLine(builder, $"[image {localImage.Path}]");
                    break;
            }
        }

        return builder.Length == 0 ? result.Error ?? string.Empty : builder.ToString();
    }

    /// <summary>
    /// Returns the conversation without the user messages at its end: the ones of the prompt that starts.
    /// </summary>
    /// <param name="conversation">The conversation.</param>
    /// <returns>The messages before the trailing user messages.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="conversation" /> is <see langword="null" />.</exception>
    public static IReadOnlyList<AgentConversationMessage> WithoutTrailingUserMessages(IReadOnlyList<AgentConversationMessage> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var count = conversation.Count;
        while (count > 0 && conversation[count - 1].Role is AgentConversationRole.User)
        {
            count--;
        }

        return [.. conversation.Take(count)];
    }

    private static void AppendLine(StringBuilder builder, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(value);
    }

    private static string Truncate(string value, int limit)
        => value.Length <= limit ? value : value[..limit] + " …";
}
