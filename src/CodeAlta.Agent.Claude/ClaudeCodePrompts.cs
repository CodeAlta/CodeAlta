using System.Collections.Frozen;
using System.Security.Cryptography;
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

    private const string InstructionsUpdateOpening = "<codealta_instructions_update>";
    private const int RemovedParagraphPreview = 120;

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
    /// Creates the text added to the system prompt of Claude Code, which stays its own: how the instructions
    /// CodeAlta composed for the session, the same for every provider, apply to Claude Code, then those
    /// instructions.
    /// </summary>
    /// <param name="developerInstructions">The instructions CodeAlta composed for the session.</param>
    /// <param name="hasGateway">Whether the session has the <c>alta</c> live tool.</param>
    public static string CreateAppendSystemPrompt(string? developerInstructions, bool hasGateway)
    {
        const string Prefix = ClaudeCodeLauncher.McpToolPrefix;
        var builder = new StringBuilder();
        builder.AppendLine("# CodeAlta");
        builder.AppendLine();
        builder.AppendLine("This Claude Code session is driven by CodeAlta, a coding-agent application. The user reads your answers and follows your tool calls in the CodeAlta window, not in a terminal.");
        builder.AppendLine();
        builder.AppendLine("The instructions of CodeAlta for this session follow this list. They are the ones CodeAlta gives every model it drives; this is how they apply to you:");
        builder.AppendLine();
        builder.Append($"- Tools of CodeAlta: a tool the instructions name `<name>` is `{Prefix}<name>` here.");
        if (hasGateway)
        {
            builder.Append($" The `{GatewayTool}` live tool is `{Prefix}{GatewayTool}`, and `{GatewayTool} <command> ...` in the instructions is a call of that tool with those words as its `args` (what a command reads with `--stdin` goes in `stdin`), never a shell command.");
        }

        builder.AppendLine(" A tool that joins while a turn runs may not be in your tool list yet: your tool search loads it by that name.");
        builder.AppendLine("- CodeAlta's own file, search, web, shell and question tools (`read_file`, `view_image`, `list_dir`, `grep`, `webget`, `shell_command`, `write_file`, `replace_in_file`, `delete_file_or_dir`, `rename_file_or_dir`, `apply_patch`, `request_user_input`) are not part of this session. Where the instructions mention them, do the same work with your own tools.");
        if (hasGateway)
        {
            // Claude Code has a tool of its own for most of what CodeAlta does with its sessions, questions,
            // skills, plans and notes. The instructions name the way of CodeAlta without saying it wins.
            builder.AppendLine($"- What CodeAlta has its own way for, do its way: the user sees and manages it in the CodeAlta window. That is `{GatewayTool} session` for the child sessions and the delegation the user asks for (your own subagents stay yours, for your own work), `{GatewayTool} ask` for questions to the user, `{GatewayTool} skill` for the skills the instructions list, the plan mode and the plan files as the instructions describe them, `{GatewayTool} notes` and `{GatewayTool} reminder`.");
            // The CLI tells the model that a background command starts it again when it ends. CodeAlta shows that
            // turn as a run, but only a process that is still there starts it: a reminder does not depend on it.
            builder.AppendLine($"- A background command of yours that ends after your turn, or a wake-up of yours that fires, starts a turn the user sees as a run of the session, as long as the session stays open in CodeAlta. For what has to bring you back in any case, set a `{GatewayTool} reminder` as the instructions say.");
            builder.AppendLine($"- Your own question tool reaches the user only in a run CodeAlta lets ask that way. When it is refused, `{GatewayTool} ask` is the way to ask, within the rules the instructions give for asking.");
        }

        builder.AppendLine("- Where the instructions differ from your defaults, for example on when to commit, follow the instructions: they are what the user set up in CodeAlta.");
        builder.AppendLine("- Permission prompts are answered by the user or by the policy of CodeAlta.");

        var instructions = developerInstructions?.Trim();
        if (!string.IsNullOrEmpty(instructions))
        {
            builder.AppendLine();
            builder.AppendLine("The instructions of CodeAlta for this session:");
            builder.AppendLine();
            builder.AppendLine(instructions);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Creates the message of the run that shows a turn Claude Code started by itself. The session records it in
    /// the place of a prompt; Claude Code is not sent it.
    /// </summary>
    /// <param name="summary">What started the turn, as the CLI says it (the background command that ended), or <see langword="null" />.</param>
    public static string CreateOwnTurnNotice(string? summary)
    {
        summary = summary?.Trim();
        return string.IsNullOrEmpty(summary)
            ? "Claude Code started a turn by itself."
            : $"Claude Code started a turn by itself: {summary}";
    }

    /// <summary>
    /// Creates the note that tells a conversation what changed in the instructions of CodeAlta since it was
    /// given them. Claude Code keeps the system prompt a conversation started with, so a change (another agent
    /// prompt, an activated skill, tools that were turned on) is given with the prompt that follows it.
    /// </summary>
    /// <param name="previous">The instructions the conversation was given, or <see langword="null" /> when they are not known.</param>
    /// <param name="current">The instructions of the prompt that starts.</param>
    public static string CreateInstructionsUpdate(string? previous, string current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var builder = new StringBuilder();
        builder.AppendLine(InstructionsUpdateOpening);
        if (previous is null)
        {
            builder.AppendLine("The instructions of CodeAlta for this session are no longer the ones this conversation was given. This note is not a request of the user. The text below replaces all the instructions of CodeAlta you were given before, the `# CodeAlta` part of your system prompt and earlier notes like this one:");
            builder.AppendLine();
            builder.AppendLine(current.Trim());
        }
        else
        {
            // The parts are the paragraphs: what the text is made of does not have to be known to compare it.
            var before = SplitParagraphs(previous);
            var after = SplitParagraphs(current);
            var kept = before.ToHashSet(StringComparer.Ordinal);
            var present = after.ToHashSet(StringComparer.Ordinal);
            var removed = before.Where(paragraph => !present.Contains(paragraph)).ToArray();
            var added = after.Where(paragraph => !kept.Contains(paragraph)).ToArray();

            builder.AppendLine("The instructions of CodeAlta for this session changed. This note is not a request of the user: it updates the `# CodeAlta` part of your system prompt, and earlier notes like this one, for what follows. What it does not name still applies.");
            if (removed.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("These parts no longer apply (each is named by how it starts):");
                foreach (var paragraph in removed)
                {
                    builder.Append("- ").AppendLine(FirstWords(paragraph));
                }
            }

            if (added.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine(removed.Length > 0 ? "These parts are new, or replace the ones above:" : "These parts are new:");
                foreach (var paragraph in added)
                {
                    builder.AppendLine();
                    builder.AppendLine(paragraph);
                }
            }
        }

        builder.Append("</codealta_instructions_update>");
        return builder.ToString();
    }

    /// <summary>The value by which two versions of the instructions are told apart.</summary>
    public static string HashInstructions(string instructions)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions)), 0, 8);

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

    private static string[] SplitParagraphs(string text)
        => [.. text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(static paragraph => paragraph.Trim()).Where(static paragraph => paragraph.Length > 0)];

    private static string FirstWords(string paragraph)
    {
        var line = paragraph.AsSpan();
        var end = line.IndexOf('\n');
        if (end >= 0)
        {
            line = line[..end];
        }

        return line.Length <= RemovedParagraphPreview ? line.ToString() : string.Concat(line[..RemovedParagraphPreview], " …");
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
