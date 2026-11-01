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
    private const string InstructionsUpdateOpening = "<codealta_instructions_update>";

    /// <summary>The command gateway of CodeAlta, which its instructions name.</summary>
    public const string GatewayTool = "alta";

    /// <summary>
    /// The tools of CodeAlta that Claude Code has its own version of: they are not offered to it a second time.
    /// </summary>
    public static FrozenSet<string> ReplacedTools { get; } = CodeAlta.Agent.Runtime.Tools.AgentBuiltInToolFactory.BuiltInToolNames.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Gets why a script may not call a tool of CodeAlta in a permission mode.
    /// </summary>
    /// <param name="permissionMode">The mode of the session or of its provider, or null for none.</param>
    /// <param name="toolName">The name of the tool.</param>
    /// <param name="commandsThroughCodeAlta">Whether the commands of the session run with the <c>shell_command</c> of CodeAlta.</param>
    /// <returns>What the script is answered, or null when the call runs.</returns>
    public static string? ScriptRefusal(string? permissionMode, string toolName, bool commandsThroughCodeAlta)
    {
        // In these modes the CLI decides what its tools do, and it allows the tools of CodeAlta by name: a call of a
        // script would not reach that decision. In the others, the review of the host is what the mode promises.
        if (!CliDecidesInMode(permissionMode) || !ToolsThatChange.Contains(toolName) || (commandsThroughCodeAlta && toolName == "shell_command"))
        {
            return null;
        }

        var own = toolName switch
        {
            "write_file" => "Write",
            "replace_in_file" or "apply_patch" => "Edit",
            _ => commandsThroughCodeAlta ? $"`{ClaudeCodeLauncher.McpToolPrefix}shell_command`" : "Bash or PowerShell",
        };
        return $"`tools.{toolName}` is refused: this session is in the `{permissionMode}` permission mode of Claude Code, which does not judge the calls of a script. Do it outside the script with your own tool: {own}. The calls this script made before this one are not undone.";
    }

    /// <summary>Gets whether, in a permission mode, the CLI decides what its tools do: scripts then leave changes to those tools.</summary>
    /// <param name="permissionMode">The mode of the session or of its provider, or null for none.</param>
    /// <returns>True for <c>auto</c> and <c>dontAsk</c>.</returns>
    public static bool CliDecidesInMode(string? permissionMode) => permissionMode is "auto" or "dontAsk";

    // The tools of CodeAlta that change files or run commands.
    private static readonly FrozenSet<string> ToolsThatChange = FrozenSet.Create(StringComparer.Ordinal,
        "write_file", "replace_in_file", "delete_file_or_dir", "rename_file_or_dir", "apply_patch", "shell_command");

    /// <summary>
    /// Creates the text added to the system prompt of Claude Code, which stays its own: how the instructions
    /// CodeAlta composed for the session, the same for every provider, apply to Claude Code, then those
    /// instructions.
    /// </summary>
    /// <param name="developerInstructions">The instructions CodeAlta composed for the session.</param>
    /// <param name="hasGateway">Whether the session has the <c>alta</c> live tool.</param>
    public static string CreateAppendSystemPrompt(string? developerInstructions, bool hasGateway)
        => CreateAppendSystemPrompt(developerInstructions, hasGateway, hasShell: true);

    /// <summary>
    /// Writes what the CLI appends to its own system prompt, for a session that may do without the shell.
    /// </summary>
    /// <param name="developerInstructions">The instructions CodeAlta composed for the session.</param>
    /// <param name="hasGateway">Whether the session has the <c>alta</c> live tool.</param>
    /// <param name="hasShell">Whether the session runs commands; false when it does without <c>shell_command</c>.</param>
    public static string CreateAppendSystemPrompt(string? developerInstructions, bool hasGateway, bool hasShell)
        => CreateAppendSystemPrompt(developerInstructions, hasGateway, hasShell, shellOfCodeAlta: false);

    /// <summary>
    /// Writes what the CLI appends to its own system prompt, for a session that may do without the shell, or whose
    /// commands run with the <c>shell_command</c> of CodeAlta instead of its own shell tools (a sandbox on Windows).
    /// </summary>
    /// <param name="developerInstructions">The instructions CodeAlta composed for the session.</param>
    /// <param name="hasGateway">Whether the session has the <c>alta</c> live tool.</param>
    /// <param name="hasShell">Whether the session runs commands; false when it does without <c>shell_command</c>.</param>
    /// <param name="shellOfCodeAlta">Whether its commands run with the <c>shell_command</c> of CodeAlta.</param>
    public static string CreateAppendSystemPrompt(string? developerInstructions, bool hasGateway, bool hasShell, bool shellOfCodeAlta)
        => CreateAppendSystemPrompt(developerInstructions, hasGateway, hasShell, shellOfCodeAlta, scriptsRefuseChanges: false);

    /// <summary>
    /// Writes what the CLI appends to its own system prompt, for a session that may have scripts of <c>exec</c> whose calls
    /// that change files or run commands are refused (<see cref="ScriptRefusal"/>). In any other mode they stay what the
    /// instructions say, the efficient way to batch edits, and the prompt does not mention them.
    /// </summary>
    /// <param name="developerInstructions">The instructions CodeAlta composed for the session.</param>
    /// <param name="hasGateway">Whether the session has the <c>alta</c> live tool.</param>
    /// <param name="hasShell">Whether the session runs commands; false when it does without <c>shell_command</c>.</param>
    /// <param name="shellOfCodeAlta">Whether its commands run with the <c>shell_command</c> of CodeAlta.</param>
    /// <param name="scriptsRefuseChanges">Whether the session has <c>exec</c>, in a mode where the CLI decides what runs.</param>
    public static string CreateAppendSystemPrompt(string? developerInstructions, bool hasGateway, bool hasShell, bool shellOfCodeAlta, bool scriptsRefuseChanges)
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
        if (hasShell && shellOfCodeAlta)
        {
            builder.AppendLine("- CodeAlta's own file, search, web and question tools (`read_file`, `view_image`, `list_dir`, `grep`, `webget`, `write_file`, `replace_in_file`, `delete_file_or_dir`, `rename_file_or_dir`, `apply_patch`, `request_user_input`) are not part of this session. Where the instructions mention them, do the same work with your own tools.");
            builder.AppendLine($"- Commands: your own shell tools are not part of this session, which runs in a sandbox. Run every command, PowerShell on Windows, with `{Prefix}shell_command`, as the instructions describe `shell_command`.");
        }
        else
        {
            builder.AppendLine("- CodeAlta's own file, search, web, shell and question tools (`read_file`, `view_image`, `list_dir`, `grep`, `webget`, `shell_command`, `write_file`, `replace_in_file`, `delete_file_or_dir`, `rename_file_or_dir`, `apply_patch`, `request_user_input`) are not part of this session. Where the instructions mention them, do the same work with your own tools.");
        }

        if (!hasShell)
        {
            builder.AppendLine("- Shell commands are turned off for this session: run none, not even in the background. Do the work with the other tools, or ask the user to run a command.");
        }

        if (scriptsRefuseChanges)
        {
            // The permission mode of the CLI judges its own tools only: the calls of a script never reach it.
            var ownCommands = hasShell && !shellOfCodeAlta;
            builder.AppendLine(ownCommands
                ? "- Scripts of `exec`, in the permission mode this session has now: use them for reads, searches, `alta` and sub-agents. Make each file change and run each command with your own tools, even where the instructions batch edits in one `exec`: this mode judges your own tools, and it refuses `tools.write_file`, `tools.replace_in_file`, `tools.delete_file_or_dir`, `tools.rename_file_or_dir`, `tools.apply_patch` and `tools.shell_command` in a script."
                : "- Scripts of `exec`, in the permission mode this session has now: use them for reads, searches, `alta` and sub-agents. Make each file change with your own tools, even where the instructions batch edits in one `exec`: this mode judges your own tools, and it refuses `tools.write_file`, `tools.replace_in_file`, `tools.delete_file_or_dir`, `tools.rename_file_or_dir` and `tools.apply_patch` in a script.");
        }

        if (hasGateway)
        {
            // Claude Code has a tool of its own for most of what CodeAlta does with its sessions, questions,
            // skills, plans and notes. The instructions name the way of CodeAlta without saying it wins. Its
            // subagent tool is the one it is started without (ClaudeCodeLauncher): what it delegates is a session.
            builder.AppendLine($"- What CodeAlta has its own way for, do its way: the user sees and manages it in the CodeAlta window. That is `{GatewayTool} session` for everything you delegate (a sub-agent is a child session of CodeAlta, as the instructions describe, also for a search or a review of your own: this session has no subagent tool of Claude Code), `{GatewayTool} ask` for questions to the user, `{GatewayTool} skill` for the skills the instructions list, the plan mode and the plan files as the instructions describe them, `{GatewayTool} notes` and `{GatewayTool} reminder`.");
            // The CLI tells the model that a background command starts it again when it ends. CodeAlta shows that
            // turn as a run, but only a process that is still there starts it: a reminder does not depend on it.
            if (hasShell)
            {
                builder.AppendLine($"- A background command of yours that ends after your turn, or a wake-up of yours that fires, starts a turn the user sees as a run of the session, as long as the session stays open in CodeAlta. A command started with `{GatewayTool} job start` is run by CodeAlta itself: the user sees what it writes and can stop it, and its result is sent to you as a prompt when it ends, so prefer it to a background command of your own for what takes long. For what has to bring you back in any case, set a `{GatewayTool} reminder` as the instructions say.");
            }

            builder.AppendLine($"- Your own question tool reaches the user only in a run CodeAlta lets ask that way. When it is refused, `{GatewayTool} ask` is the way to ask, within the rules the instructions give for asking.");
        }

        builder.AppendLine("- Where the instructions differ from your defaults, for example on when to commit or on the lines that end a commit message, follow the instructions: they are what the user set up in CodeAlta.");
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
        => Harness.HarnessInstructions.CreateUpdate(previous, current, "the `# CodeAlta` part of your system prompt");

    /// <summary>
    /// Creates the note that gives the instructions of CodeAlta to a conversation started outside CodeAlta (in VS Code
    /// or a terminal) and imported: its system prompt has no part of CodeAlta, so they are all new to it.
    /// </summary>
    /// <param name="current">The instructions of the prompt that starts.</param>
    /// <returns>The note, given before the prompt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    public static string CreateInstructionsForImportedConversation(string current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var builder = new StringBuilder();
        builder.AppendLine(InstructionsUpdateOpening);
        builder.AppendLine("This conversation was started outside CodeAlta and is now continued in CodeAlta, an application for agentic coding. This note is not a request of the user. You were not given the instructions of CodeAlta before: they are below, and apply to what follows, with your system prompt:");
        builder.AppendLine();
        builder.AppendLine(current.Trim());
        builder.Append("</codealta_instructions_update>");
        return builder.ToString();
    }

    /// <summary>The value by which two versions of the instructions are told apart.</summary>
    public static string HashInstructions(string instructions)
        => Harness.HarnessInstructions.Hash(instructions);

    /// <summary>
    /// Renders the part of a session Claude Code did not take part in (another provider answered it), to give
    /// it as context with the next prompt.
    /// </summary>
    public static string? CreateHistoryPreamble(IReadOnlyList<AgentConversationMessage> history)
        => Harness.HarnessHistory.CreatePreviousConversation(history);

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
        => Harness.HarnessHistory.RenderToolResult(result);

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

}
