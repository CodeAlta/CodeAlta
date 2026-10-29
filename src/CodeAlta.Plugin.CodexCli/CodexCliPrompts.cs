using System.Collections.Frozen;
using System.Text;
using CodeAlta.Agent.Runtime;
using CodeAlta.CodexAppServer.Protocol;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>The texts CodeAlta gives Codex, and the input of its turns.</summary>
internal static class CodexCliPrompts
{
    /// <summary>The name of the MCP server of the tools of CodeAlta, as Codex is given it.</summary>
    public const string McpServerName = "codealta";

    /// <summary>The command gateway of CodeAlta, which its instructions name.</summary>
    public const string GatewayTool = "alta";

    /// <summary>The tools of CodeAlta Codex has its own version of: they are not offered to it a second time.</summary>
    public static FrozenSet<string> ReplacedTools { get; } = CodeAlta.Agent.Runtime.Tools.AgentBuiltInToolFactory.BuiltInToolNames.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Creates the developer instructions of a thread: how the instructions CodeAlta composed for the session apply
    /// to Codex, which keeps its own coding tools and loop, then those instructions. They are added to the prompt of
    /// Codex, never in its place.
    /// </summary>
    /// <param name="developerInstructions">The instructions CodeAlta composed for the session.</param>
    /// <param name="hasGateway">Whether the session has the <c>alta</c> live tool.</param>
    /// <param name="webSearch">Whether Codex has its web search in this session.</param>
    public static string CreateInstructions(string? developerInstructions, bool hasGateway, bool webSearch)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# CodeAlta");
        builder.AppendLine();
        builder.AppendLine("This Codex session is driven by CodeAlta, a coding-agent application. The user reads your answers and follows your tool calls in the CodeAlta window, not in a terminal.");
        builder.AppendLine();
        builder.AppendLine("The instructions of CodeAlta for this session follow this list. They are the ones CodeAlta gives every model it drives; this is how they apply to you:");
        builder.AppendLine();
        builder.Append($"- Tools of CodeAlta: they are the tools of the MCP server named `{McpServerName}`. A tool the instructions name `<name>` is the tool `<name>` of that server; your tool list shows it with the name of the server before it (`mcp__{McpServerName}__<name>`).");
        if (hasGateway)
        {
            builder.Append($" The `{GatewayTool}` live tool is the `{GatewayTool}` tool of that server, and `{GatewayTool} <command> ...` in the instructions is a call of that tool with those words as its `args` (what a command reads with `--stdin` goes in `stdin`), never a shell command.");
        }

        builder.AppendLine();
        builder.Append("- CodeAlta's own file, search, web, shell and question tools (`read_file`, `view_image`, `list_dir`, `grep`, `webget`, `shell_command`, `write_file`, `replace_in_file`, `delete_file_or_dir`, `rename_file_or_dir`, `apply_patch`, `request_user_input`) are not part of this session. Where the instructions mention them, do the same work with your own tools: your shell, `apply_patch` and `view_image`");
        builder.AppendLine(webSearch ? ", and your web search." : "; you have no web search in this session.");
        if (hasGateway)
        {
            builder.AppendLine($"- What CodeAlta has its own way for, do its way: the user sees and manages it in the CodeAlta window. That is `{GatewayTool} session` for everything you delegate (a sub-agent is a child session of CodeAlta, as the instructions describe), `{GatewayTool} ask` for questions to the user, `{GatewayTool} skill` for the skills the instructions list, the plan mode and the plan files as the instructions describe them, `{GatewayTool} notes` and `{GatewayTool} reminder`.");
            builder.AppendLine($"- A command you leave running in the background does not bring you back when it ends. For long work, start it with `{GatewayTool} job start`: CodeAlta runs it and sends you its result as a prompt when it ends. For what has to bring you back in any case, set a `{GatewayTool} reminder` as the instructions say.");
        }

        builder.AppendLine("- Where the instructions differ from your defaults, for example on when to commit or on the lines that end a commit message, follow the instructions: they are what the user set up in CodeAlta.");
        // The workspace of a ChatGPT sign-in can turn on the attribution of Codex, which no setting of Codex turns off:
        // as with Claude Code, it stays, and the trailer of CodeAlta, when the setting asks for it, goes beside it.
        builder.AppendLine("- Commit attribution: when your workspace asks for the attribution of Codex and the instructions ask for the trailer of CodeAlta, a commit message ends with both trailers, `Co-authored-by: Codex <noreply@openai.com>` and the one of CodeAlta, each once, in its last paragraph with the other trailers, and a pull request body has the attribution lines of both: neither in place of the other.");
        builder.AppendLine("- Your commands and edits are approved by the user or by the policy of CodeAlta; when CodeAlta runs this session in a sandbox, its container is what limits them.");

        var instructions = developerInstructions?.Trim();
        if (!string.IsNullOrEmpty(instructions))
        {
            builder.AppendLine();
            builder.AppendLine("The instructions of CodeAlta for this session:");
            builder.AppendLine();
            builder.AppendLine(instructions);
        }

        // One kind of line ending on every system, as the input of the turns has (AppendLine writes \r\n on Windows).
        return builder.ToString().ReplaceLineEndings("\n").TrimEnd();
    }

    /// <summary>Where Codex was given the instructions, as the note of a change names it.</summary>
    public const string GivenAt = "the `# CodeAlta` part of your developer instructions";

    /// <summary>
    /// Creates the input of a turn: the texts CodeAlta adds before it, then the parts of the user messages. An image is
    /// given as a data address; other attachments are named.
    /// </summary>
    public static List<UserInput> CreateInput(IReadOnlyList<string> preambles, IReadOnlyList<AgentConversationMessage> messages)
    {
        var input = new List<UserInput>();
        var text = new StringBuilder();
        foreach (var preamble in preambles)
        {
            AppendBlock(text, preamble);
        }

        foreach (var message in messages)
        {
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case AgentMessagePart.Text value:
                        AppendLine(text, value.Value);
                        break;
                    case AgentMessagePart.Data data when data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                        input.Add(new UserInput.ImageUserInput { Url = $"data:{data.MediaType};base64,{data.Base64Data}" });
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

        input.Insert(0, new UserInput.TextUserInput { Text = text.Length == 0 ? "(no text)" : text.ToString() });
        return input;
    }

    private static void AppendBlock(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append("\n\n");
        }

        builder.Append(value.Trim());
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
}
