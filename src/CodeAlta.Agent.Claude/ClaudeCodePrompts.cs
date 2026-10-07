using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// The texts and the message contents CodeAlta gives the CLI.
/// </summary>
internal static class ClaudeCodePrompts
{
    private const int HistoryPreambleLimit = 120_000;
    private const int HistoryToolArgumentsLimit = 600;
    private const int HistoryToolResultLimit = 1_500;

    /// <summary>The command gateway of CodeAlta, which its instructions name.</summary>
    public const string GatewayTool = "alta";

    /// <summary>
    /// The tools of CodeAlta that Claude Code has its own version of: they are not offered to it a second time.
    /// </summary>
    public static FrozenSet<string> ReplacedTools { get; } = new[]
    {
        "read_file",
        "view_image",
        "list_dir",
        "grep",
        "webget",
        "shell_command",
        "write_file",
        "replace_in_file",
        "delete_file_or_dir",
        "rename_file_or_dir",
        "apply_patch",
        "request_user_input",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Creates the text added to the system prompt of Claude Code, which stays its own: where the session runs,
    /// how the tools of CodeAlta are named there, and the instructions CodeAlta composed for the session.
    /// </summary>
    public static string CreateAppendSystemPrompt(string? developerInstructions, bool hasTools)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# CodeAlta");
        builder.AppendLine();
        builder.AppendLine("This Claude Code session is driven by CodeAlta, a coding-agent application. The user reads your answers and follows your tool calls in the CodeAlta window, not in a terminal.");
        builder.AppendLine();
        if (hasTools)
        {
            builder.AppendLine($"- The tools of CodeAlta are available through the MCP server `{ClaudeCodeLauncher.McpServerName}`: a tool the instructions below name `<name>` is `{ClaudeCodeLauncher.McpToolPrefix}<name>` here. For example the `alta` live tool is `{ClaudeCodeLauncher.McpToolPrefix}alta`. They are tools, not programs: `alta` cannot be run in a shell. A tool of that server that is not loaded yet is found with your tool search.");
        }

        builder.AppendLine("- CodeAlta's own file, search, web, shell and question tools (`read_file`, `view_image`, `list_dir`, `grep`, `webget`, `shell_command`, `write_file`, `replace_in_file`, `delete_file_or_dir`, `rename_file_or_dir`, `apply_patch`, `request_user_input`) are not part of this session. Where the instructions below mention them, use your own tools instead (Read, Glob, Grep, WebFetch, Bash, Edit, Write, AskUserQuestion).");
        builder.AppendLine("- Permission prompts and questions are answered by the user or by the policy of CodeAlta.");

        var instructions = developerInstructions?.Trim();
        if (!string.IsNullOrEmpty(instructions))
        {
            builder.AppendLine();
            builder.AppendLine("The instructions of CodeAlta for this session follow.");
            builder.AppendLine();
            builder.AppendLine(instructions);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Renders the part of a session Claude Code did not take part in (another provider answered it), to give
    /// it as context with the next prompt.
    /// </summary>
    public static string? CreateHistoryPreamble(IReadOnlyList<AgentConversationMessage> history)
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
                        builder.AppendLine(Truncate(toolCall.Arguments.GetRawText(), HistoryToolArgumentsLimit));
                        builder.AppendLine();
                        break;
                    case AgentMessagePart.ToolResult toolResult:
                        builder.Append(toolResult.Result.Success ? "[tool result]\n" : "[tool error]\n");
                        builder.AppendLine(Truncate(RenderToolResult(toolResult.Result), HistoryToolResultLimit));
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
        if (transcript.Length > HistoryPreambleLimit)
        {
            transcript = "(the beginning of the conversation is omitted)\n\n" + transcript[^HistoryPreambleLimit..];
        }

        return "<codealta_previous_conversation>\n" +
               "The conversation below took place earlier in this CodeAlta session, before you joined it. It is context for the request that follows, not a request by itself.\n\n" +
               transcript +
               "\n</codealta_previous_conversation>";
    }

    /// <summary>
    /// Writes the <c>content</c> array of a user message: the attachments, then one text block. Claude Code
    /// reads the prompt, and a slash command, from the last text block.
    /// </summary>
    public static void WriteUserContent(Utf8JsonWriter writer, AgentConversationMessage message, string? preamble)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);

        writer.WriteStartArray("content");
        if (!string.IsNullOrWhiteSpace(preamble))
        {
            WriteTextBlock(writer, preamble);
        }

        var text = new StringBuilder();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case AgentMessagePart.Text value:
                    AppendLine(text, value.Value);
                    break;
                case AgentMessagePart.Data data when IsImage(data.MediaType):
                    WriteSourceBlock(writer, "image", "base64", data.MediaType, data.Base64Data);
                    break;
                case AgentMessagePart.Data data when string.Equals(data.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase):
                    WriteSourceBlock(writer, "document", "base64", data.MediaType, data.Base64Data);
                    break;
                case AgentMessagePart.Data data:
                    AppendLine(text, $"[An attachment of type {data.MediaType}{(string.IsNullOrWhiteSpace(data.Name) ? string.Empty : $" named {data.Name}")} was not passed on.]");
                    break;
                case AgentMessagePart.Uri uri when IsWebUri(uri.Value) && (uri.MediaType is null || IsImage(uri.MediaType)):
                    writer.WriteStartObject();
                    writer.WriteString("type", "image");
                    writer.WriteStartObject("source");
                    writer.WriteString("type", "url");
                    writer.WriteString("url", uri.Value);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
                case AgentMessagePart.Uri uri:
                    AppendLine(text, uri.Value);
                    break;
            }
        }

        WriteTextBlock(writer, text.Length == 0 ? "(no text)" : text.ToString());
        writer.WriteEndArray();
    }

    /// <summary>Joins the text of a tool result; an image is named, not included.</summary>
    public static string RenderToolResult(AgentToolResult result)
    {
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

    private static void WriteTextBlock(Utf8JsonWriter writer, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text);
        writer.WriteEndObject();
    }

    private static void WriteSourceBlock(Utf8JsonWriter writer, string type, string sourceType, string mediaType, string data)
    {
        writer.WriteStartObject();
        writer.WriteString("type", type);
        writer.WriteStartObject("source");
        writer.WriteString("type", sourceType);
        writer.WriteString("media_type", mediaType);
        writer.WriteString("data", data);
        writer.WriteEndObject();
        writer.WriteEndObject();
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

    private static bool IsImage(string? mediaType)
        => mediaType is not null && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool IsWebUri(string value)
        => value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
           value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int limit)
        => value.Length <= limit ? value : value[..limit] + " …";
}
