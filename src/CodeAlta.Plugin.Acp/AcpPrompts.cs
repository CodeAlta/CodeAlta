using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Plugin.Acp;

/// <summary>The texts CodeAlta gives an ACP agent, and the content blocks of its prompts.</summary>
internal static class AcpPrompts
{
    /// <summary>The name of the MCP server of the tools of CodeAlta, as the agent is given it.</summary>
    public const string McpServerName = "codealta";

    /// <summary>The command gateway of CodeAlta, which its instructions name.</summary>
    public const string GatewayTool = "alta";

    /// <summary>The tag that opens the block of the instructions of CodeAlta in a prompt.</summary>
    public const string HarnessOpening = "<codealta_harness>";

    /// <summary>
    /// The tools of CodeAlta an agent has its own version of: they are not offered to it a second time.
    /// </summary>
    public static FrozenSet<string> ReplacedTools { get; } = CodeAlta.Agent.Runtime.Tools.AgentBuiltInToolFactory.BuiltInToolNames.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Creates the instructions an agent is given: how the instructions CodeAlta composed for the session, the same
    /// for every provider, apply to an agent that keeps its own tools and loop, then those instructions.
    /// </summary>
    /// <param name="agentName">The name of the agent, such as "Copilot".</param>
    /// <param name="developerInstructions">The instructions CodeAlta composed for the session.</param>
    /// <param name="hasTools">Whether the agent reaches the tools of CodeAlta through its MCP server.</param>
    /// <param name="hasGateway">Whether the session has the <c>alta</c> live tool.</param>
    public static string CreateInstructions(string agentName, string? developerInstructions, bool hasTools, bool hasGateway)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# CodeAlta");
        builder.AppendLine();
        builder.AppendLine($"This {agentName} session is driven by CodeAlta, a coding-agent application. The user reads your answers and follows your tool calls in the CodeAlta window, not in a terminal.");
        builder.AppendLine();
        builder.AppendLine("The instructions of CodeAlta for this session follow this list. They are the ones CodeAlta gives every model it drives; this is how they apply to you:");
        builder.AppendLine();
        if (hasTools)
        {
            builder.Append($"- Tools of CodeAlta: they are the tools of the MCP server named `{McpServerName}`. A tool the instructions name `<name>` is the tool `<name>` of that server; your tool list may show it with the name of the server before it.");
            if (hasGateway)
            {
                builder.Append($" The `{GatewayTool}` live tool is the `{GatewayTool}` tool of that server, and `{GatewayTool} <command> ...` in the instructions is a call of that tool with those words as its `args` (what a command reads with `--stdin` goes in `stdin`), never a shell command.");
            }

            builder.AppendLine();
        }
        else
        {
            builder.AppendLine($"- The tools of CodeAlta (`{GatewayTool}` and the others the instructions name) are not available in this session. What the instructions do with them cannot be done here: do the work with your own tools, and say so when something the instructions ask needs them.");
        }

        builder.AppendLine("- CodeAlta's own file, search, web, shell and question tools (`read_file`, `view_image`, `list_dir`, `grep`, `webget`, `shell_command`, `write_file`, `replace_in_file`, `delete_file_or_dir`, `rename_file_or_dir`, `apply_patch`, `request_user_input`) are not part of this session. Where the instructions mention them, do the same work with your own tools.");
        if (hasTools && hasGateway)
        {
            builder.AppendLine($"- What CodeAlta has its own way for, do its way: the user sees and manages it in the CodeAlta window. That is `{GatewayTool} session` for everything you delegate (a sub-agent is a child session of CodeAlta, as the instructions describe), `{GatewayTool} ask` for questions to the user, `{GatewayTool} skill` for the skills the instructions list, the plan mode and the plan files as the instructions describe them, `{GatewayTool} notes` and `{GatewayTool} reminder`.");
            builder.AppendLine($"- A command you leave running in the background does not bring you back when it ends. For long work, start it with `{GatewayTool} job start`: CodeAlta runs it and sends you its result as a prompt when it ends. For what has to bring you back in any case, set a `{GatewayTool} reminder` as the instructions say.");
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
    /// Wraps the instructions in the block given with the first prompt, for an agent that takes no system prompt from
    /// its client.
    /// </summary>
    public static string CreateHarnessBlock(string instructions)
        => HarnessOpening + "\n" +
           "These are the instructions of the application that drives this session, not a request of the user. Keep them for the whole session, also after your context is compacted: they apply to every later prompt.\n\n" +
           instructions.Trim() +
           "\n</codealta_harness>";

    /// <summary>Where an agent was given the instructions, as the note of a change names it.</summary>
    public static string GivenAt(bool systemPrompt)
        => systemPrompt ? "the `# CodeAlta` part of your system prompt" : "the `<codealta_harness>` block of an earlier prompt";

    /// <summary>Creates a text content block.</summary>
    public static ContentBlock Text(string text)
        => new()
        {
            Value = AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text);
                writer.WriteEndObject();
            }),
        };

    /// <summary>
    /// Creates the content blocks of a prompt: the texts CodeAlta adds before it, then the parts of the user messages.
    /// An image is given as such to an agent that takes images; other attachments are named.
    /// </summary>
    public static List<ContentBlock> CreatePrompt(IReadOnlyList<string> preambles, IReadOnlyList<AgentConversationMessage> messages, bool images)
    {
        var blocks = new List<ContentBlock>();
        foreach (var preamble in preambles)
        {
            if (!string.IsNullOrWhiteSpace(preamble))
            {
                blocks.Add(Text(preamble));
            }
        }

        var text = new StringBuilder();
        foreach (var message in messages)
        {
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case AgentMessagePart.Text value:
                        AppendLine(text, value.Value);
                        break;
                    case AgentMessagePart.Data data when images && data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                        blocks.Add(new ContentBlock
                        {
                            Value = AcpJson.Build(writer =>
                            {
                                writer.WriteStartObject();
                                writer.WriteString("type", "image");
                                writer.WriteString("data", data.Base64Data);
                                writer.WriteString("mimeType", data.MediaType);
                                writer.WriteEndObject();
                            }),
                        });
                        break;
                    case AgentMessagePart.Data data:
                        AppendLine(text, $"[An attachment of type {data.MediaType}{(string.IsNullOrWhiteSpace(data.Name) ? string.Empty : $" named {data.Name}")} was not passed on.]");
                        break;
                    case AgentMessagePart.Uri uri:
                        AppendLine(text, uri.Value);
                        break;
                }
            }
        }

        blocks.Add(Text(text.Length == 0 ? "(no text)" : text.ToString()));
        return blocks;
    }

    /// <summary>Joins the text of the content of a tool call or of a message chunk; diffs are summarized.</summary>
    public static string RenderToolContent(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            switch (AcpJson.GetString(item, "type"))
            {
                case "content" when AcpJson.TryGetObject(item, "content", out var block):
                    AppendLine(builder, RenderContentBlock(block));
                    break;
                case "diff":
                    var path = AcpJson.GetString(item, "path") ?? "a file";
                    var newText = AcpJson.GetString(item, "newText");
                    var oldText = AcpJson.GetString(item, "oldText");
                    AppendLine(builder, oldText is null ? $"Created {path}." : newText is null ? $"Deleted {path}." : $"Edited {path}.");
                    break;
                case "terminal":
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Returns the text of a content block, or a short note for content that is not text.</summary>
    public static string RenderContentBlock(JsonElement block)
        => AcpJson.GetString(block, "type") switch
        {
            "text" => AcpJson.GetString(block, "text") ?? string.Empty,
            "image" => "[image]",
            "audio" => "[audio]",
            "resource_link" => AcpJson.GetString(block, "uri") ?? "[resource]",
            "resource" when AcpJson.TryGetObject(block, "resource", out var resource) => AcpJson.GetString(resource, "text") ?? AcpJson.GetString(resource, "uri") ?? "[resource]",
            _ => string.Empty,
        };

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
}
