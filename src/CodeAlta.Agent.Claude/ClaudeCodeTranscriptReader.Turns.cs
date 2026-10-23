using System.Text;
using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

// The entries of the chain, cut into turns of the messages a session of CodeAlta records.
internal static partial class ClaudeCodeTranscriptReader
{
    private sealed class AssistantBuilder(string? messageId)
    {
        public string? MessageId { get; } = messageId;

        public List<AgentMessagePart> Parts { get; } = [];
    }

    private sealed class TurnBuilder(Node first, string? entrypoint, string? originKind)
    {
        /// <summary><see cref="AgentConversationMessage"/>s, and <see cref="AssistantBuilder"/>s still gathering lines.</summary>
        public List<object> Items { get; } = [];

        public string? Entrypoint { get; } = entrypoint;

        public string? OriginKind { get; } = originKind;

        public Node First { get; } = first;

        public string LastUuid { get; set; } = first.Uuid;

        /// <summary>The text of the prompt the CLI wrote to start the turn by itself.</summary>
        public string? TriggerText { get; init; }
    }

    // A prompt the CLI wrote itself (the notification of a background task, ...) starts a turn of its own, as in a
    // session of CodeAlta: it is not a user message.
    private static bool IsStartedByTheCli(string? originKind, string? commandMode)
        => (originKind is not null && originKind != "human") || commandMode == "task-notification";

    private static void StartTurn(
        List<ClaudeCodeTranscriptTurn> turns,
        ref TurnBuilder? turn,
        ref AssistantBuilder? assistant,
        Node node,
        string? entrypoint,
        string? originKind,
        string? commandMode,
        List<AgentMessagePart> parts)
    {
        Flush(turns, turn);
        assistant = null;
        if (IsStartedByTheCli(originKind, commandMode))
        {
            turn = new TurnBuilder(node, entrypoint, originKind ?? commandMode)
            {
                TriggerText = string.Join("\n", parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value)),
            };
            return;
        }

        turn = new TurnBuilder(node, entrypoint, originKind);
        turn.Items.Add(new AgentConversationMessage(AgentConversationRole.User, parts));
    }

    private static List<ClaudeCodeTranscriptTurn> BuildTurns(
        byte[] buffer,
        List<Node> entries,
        string folder,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var turns = new List<ClaudeCodeTranscriptTurn>();
        TurnBuilder? turn = null;
        AssistantBuilder? assistant = null;
        foreach (var node in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Type is not ("user" or "assistant" or "attachment" or "system") ||
                (node.Type == "system" && !node.IsCompactBoundary && node.Subtype != "local_command") ||
                (node.Type == "attachment" && node.AttachmentType != "queued_command") ||
                (node.Type == "user" && (node.IsMeta || node.IsCompactSummary)))
            {
                continue;
            }

            using var document = TryParse(buffer, node.Start, node.Length);
            if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root)
            {
                continue;
            }

            var entrypoint = ClaudeCodeJson.GetString(root, "entrypoint");
            switch (node.Type)
            {
                case "system" when node.Subtype == "local_command":
                    // The output of a command the CLI ran itself, as some versions write it.
                    if (ClaudeCodeJson.GetString(root, "content") is { } localOutput &&
                        ReadCommandLine([new AgentMessagePart.Text(localOutput)]) is (CommandLine.CommandOutput, { Length: > 0 } systemOutput))
                    {
                        AddCommandOutput(ref turn, ref assistant, node, entrypoint, systemOutput);
                    }

                    break;
                case "system":
                    Flush(turns, turn);
                    turn = null;
                    assistant = null;
                    turns.Add(new ClaudeCodeTranscriptTurn(
                        [],
                        ReadCompaction(root),
                        entrypoint,
                        ToOrigin(entrypoint),
                        null,
                        ParseTimestamp(node.Timestamp),
                        node.Uuid,
                        node.Uuid));
                    break;
                case "attachment":
                    if (ClaudeCodeJson.TryGetObject(root, "attachment", out var attachment) &&
                        attachment.TryGetProperty("prompt", out var queued) &&
                        ReadUserParts(queued) is { Count: > 0 } queuedParts)
                    {
                        var queuedOrigin = (ClaudeCodeJson.TryGetObject(attachment, "origin", out var attachmentOrigin) ? ClaudeCodeJson.GetString(attachmentOrigin, "kind") : null) ??
                                           ReadOriginKind(root);
                        StartTurn(turns, ref turn, ref assistant, node, entrypoint, queuedOrigin, ClaudeCodeJson.GetString(attachment, "commandMode"), queuedParts);
                    }

                    break;
                case "user":
                    if (!ClaudeCodeJson.TryGetObject(root, "message", out var userMessage) ||
                        !userMessage.TryGetProperty("content", out var userContent))
                    {
                        break;
                    }

                    if (node.ToolResultIds.Length > 0)
                    {
                        turn ??= new TurnBuilder(node, entrypoint, null);
                        foreach (var block in userContent.EnumerateArray())
                        {
                            if (ClaudeCodeJson.GetString(block, "type") == "tool_result" &&
                                ClaudeCodeJson.GetString(block, "tool_use_id") is { Length: > 0 } callId)
                            {
                                var result = ResolvePersistedOutput(ClaudeCodeSession.ReadToolResult(block), folder, sessionId);
                                turn.Items.Add(new AgentConversationMessage(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult(callId, result)]));
                            }
                        }

                        turn.LastUuid = node.Uuid;
                        break;
                    }

                    if (ReadUserParts(userContent) is not { Count: > 0 } parts || IsInterruption(parts))
                    {
                        break;
                    }

                    switch (ReadCommandLine(parts))
                    {
                        case (CommandLine.Command or CommandLine.Shell, var command):
                            StartTurn(turns, ref turn, ref assistant, node, entrypoint, ReadOriginKind(root), null, [new AgentMessagePart.Text(command)]);
                            break;
                        case (CommandLine.CommandOutput, var output):
                            if (output.Length > 0)
                            {
                                AddCommandOutput(ref turn, ref assistant, node, entrypoint, output);
                            }

                            break;
                        case (CommandLine.ShellOutput, var output):
                            if (output.Length == 0)
                            {
                                break;
                            }

                            if (turn?.Items is [.., AgentConversationMessage { Role: AgentConversationRole.User, Parts: [AgentMessagePart.Text { Value: var input }] } shell] &&
                                input.StartsWith("! ", StringComparison.Ordinal))
                            {
                                // The model is given the output with the command, in the user's message.
                                turn.Items[^1] = shell with { Parts = [.. shell.Parts, new AgentMessagePart.Text(output)] };
                                turn.LastUuid = node.Uuid;
                            }
                            else
                            {
                                StartTurn(turns, ref turn, ref assistant, node, entrypoint, ReadOriginKind(root), null, [new AgentMessagePart.Text(output)]);
                            }

                            break;
                        default:
                            StartTurn(turns, ref turn, ref assistant, node, entrypoint, ReadOriginKind(root), null, parts);
                            break;
                    }

                    break;
                case "assistant":
                    if (!ClaudeCodeJson.TryGetObject(root, "message", out var message))
                    {
                        break;
                    }

                    turn ??= new TurnBuilder(node, entrypoint, null);
                    // The lines of one model message share its identifier: they make one assistant message, also when
                    // the results of its first tool calls were written between them.
                    if (assistant is null || node.MessageId is null || !string.Equals(assistant.MessageId, node.MessageId, StringComparison.Ordinal))
                    {
                        assistant = new AssistantBuilder(node.MessageId);
                        turn.Items.Add(assistant);
                    }

                    // The CLI writes a failed request as an assistant text: it is an error, not an answer.
                    var failed = ClaudeCodeJson.GetBoolean(root, "isApiErrorMessage") || ClaudeCodeJson.GetString(root, "error") is not null;
                    if (ClaudeCodeJson.TryGetArray(message, "content", out var content))
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            AddAssistantPart(assistant.Parts, block, failed);
                        }
                    }

                    turn.LastUuid = node.Uuid;
                    break;
            }
        }

        Flush(turns, turn);
        return turns;
    }

    /// <summary>What a user line of a command holds.</summary>
    private enum CommandLine
    {
        /// <summary>Not a command: a prompt.</summary>
        None,

        /// <summary>A slash command (<c>&lt;command-name&gt;</c>): the CLI runs it, or sends the model the prompt it stands for.</summary>
        Command,

        /// <summary>The output of a slash command the CLI ran itself (<c>&lt;local-command-stdout&gt;</c>).</summary>
        CommandOutput,

        /// <summary>A shell command (<c>!</c>, <c>&lt;bash-input&gt;</c>).</summary>
        Shell,

        /// <summary>The output of a shell command (<c>&lt;bash-stdout&gt;</c>).</summary>
        ShellOutput,
    }

    // The CLI writes a command typed by the user as user lines with tags, as it gives them to the model (with a caveat
    // when the model is not to answer them). They are shown as CodeAlta records a command typed in it, which it sends
    // to the CLI as a user message and whose output it records as the answer: a slash command is a user message
    // "/name args", the output of a command the CLI ran itself an assistant message. A shell command and its output
    // make one user message: the model is given them together, as context.
    private static (CommandLine Kind, string Text) ReadCommandLine(List<AgentMessagePart> parts)
    {
        if (parts.Any(static part => part is not AgentMessagePart.Text))
        {
            return (CommandLine.None, string.Empty);
        }

        var text = string.Concat(parts.Cast<AgentMessagePart.Text>().Select(static part => part.Value)).Trim();
        if (Between(text, "<command-name>", "</command-name>") is { } name)
        {
            name = name.Trim();
            if (!name.StartsWith('/'))
            {
                name = "/" + name;
            }

            var arguments = Between(text, "<command-args>", "</command-args>")?.Trim();
            return (CommandLine.Command, string.IsNullOrEmpty(arguments) ? name : $"{name} {arguments}");
        }

        if (Between(text, "<bash-input>", "</bash-input>") is { } input)
        {
            return (CommandLine.Shell, $"! {input.Trim()}");
        }

        if (text.StartsWith("<local-command-stdout>", StringComparison.Ordinal) || text.StartsWith("<local-command-stderr>", StringComparison.Ordinal))
        {
            return (CommandLine.CommandOutput, ReadOutput(text, "local-command-stdout", "local-command-stderr"));
        }

        if (text.StartsWith("<bash-stdout>", StringComparison.Ordinal) || text.StartsWith("<bash-stderr>", StringComparison.Ordinal))
        {
            return (CommandLine.ShellOutput, ReadOutput(text, "bash-stdout", "bash-stderr"));
        }

        return (CommandLine.None, text);
    }

    private static string ReadOutput(string text, string output, string error)
    {
        var builder = new StringBuilder();
        foreach (var tag in (ReadOnlySpan<string>)[output, error])
        {
            if (Between(text, $"<{tag}>", $"</{tag}>") is { } value && !string.IsNullOrWhiteSpace(value))
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(value.Trim());
            }
        }

        return builder.ToString();
    }

    private static void AddCommandOutput(ref TurnBuilder? turn, ref AssistantBuilder? assistant, Node node, string? entrypoint, string output)
    {
        turn ??= new TurnBuilder(node, entrypoint, null);
        turn.Items.Add(new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(output)]));
        turn.LastUuid = node.Uuid;
        assistant = null;
    }

    private static void Flush(List<ClaudeCodeTranscriptTurn> turns, TurnBuilder? turn)
    {
        if (turn is null)
        {
            return;
        }

        var messages = new List<AgentConversationMessage>(turn.Items.Count);
        foreach (var item in turn.Items)
        {
            switch (item)
            {
                case AgentConversationMessage message:
                    messages.Add(message);
                    break;
                case AssistantBuilder { Parts.Count: > 0 } assistant:
                    messages.Add(new AgentConversationMessage(AgentConversationRole.Assistant, [.. assistant.Parts]));
                    break;
            }
        }

        if (messages.Count == 0)
        {
            return;
        }

        turns.Add(new ClaudeCodeTranscriptTurn(
            messages,
            null,
            turn.Entrypoint,
            ToOrigin(turn.Entrypoint),
            turn.OriginKind,
            ParseTimestamp(turn.First.Timestamp),
            turn.First.Uuid,
            turn.LastUuid,
            turn.TriggerText));
    }

    private static string? ReadOriginKind(JsonElement line)
        => ClaudeCodeJson.TryGetObject(line, "origin", out var origin) ? ClaudeCodeJson.GetString(origin, "kind") : null;

    private static void AddAssistantPart(List<AgentMessagePart> parts, JsonElement block, bool failed)
    {
        switch (ClaudeCodeJson.GetString(block, "type"))
        {
            case "text":
                if (!failed && ClaudeCodeJson.GetString(block, "text") is { } text && !string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(new AgentMessagePart.Text(text));
                }

                break;
            case "thinking":
                // The signature of the block belongs to the conversation of the CLI: it is not kept.
                if (ClaudeCodeJson.GetString(block, "thinking") is { } thinking && !string.IsNullOrWhiteSpace(thinking))
                {
                    parts.Add(new AgentMessagePart.Reasoning(thinking));
                }

                break;
            case "redacted_thinking":
                // Reasoning with nothing to show, as in a turn of CodeAlta.
                break;
            case "tool_use":
                if (ClaudeCodeJson.GetString(block, "id") is { Length: > 0 } id &&
                    ClaudeCodeJson.GetString(block, "name") is { Length: > 0 } name)
                {
                    var arguments = block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                        ? input.Clone()
                        : ClaudeCodeJson.EmptyObject;
                    // A tool of CodeAlta is named as CodeAlta names it, as in a turn of CodeAlta.
                    var shownName = name.StartsWith(ClaudeCodeLauncher.McpToolPrefix, StringComparison.Ordinal)
                        ? name[ClaudeCodeLauncher.McpToolPrefix.Length..]
                        : name;
                    parts.Add(new AgentMessagePart.ToolCall(id, shownName, arguments));
                }

                break;
            case { } other:
                parts.Add(new AgentMessagePart.Text(Placeholder(other)));
                break;
        }
    }

    // The content of a user message or of a queued prompt: a string, or blocks.
    private static List<AgentMessagePart> ReadUserParts(JsonElement content)
    {
        var parts = new List<AgentMessagePart>();
        if (content.ValueKind == JsonValueKind.String)
        {
            if (ClaudeCodeJson.ReadString(content) is var text && !string.IsNullOrWhiteSpace(text))
            {
                parts.Add(new AgentMessagePart.Text(text));
            }

            return parts;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return parts;
        }

        foreach (var block in content.EnumerateArray())
        {
            switch (ClaudeCodeJson.GetString(block, "type"))
            {
                case "text":
                    if (ClaudeCodeJson.GetString(block, "text") is { } text && !string.IsNullOrWhiteSpace(text))
                    {
                        parts.Add(new AgentMessagePart.Text(text));
                    }

                    break;
                case "image" when ClaudeCodeJson.TryGetObject(block, "source", out var source):
                    if (ClaudeCodeJson.GetString(source, "data") is { Length: > 0 } data)
                    {
                        parts.Add(new AgentMessagePart.Data(data, ClaudeCodeJson.GetString(source, "media_type") ?? "image/png"));
                    }
                    else if (ClaudeCodeJson.GetString(source, "url") is { Length: > 0 } url)
                    {
                        parts.Add(new AgentMessagePart.Uri(url, ClaudeCodeJson.GetString(source, "media_type")));
                    }
                    else
                    {
                        parts.Add(new AgentMessagePart.Text(Placeholder("image")));
                    }

                    break;
                case { } other:
                    parts.Add(new AgentMessagePart.Text(Placeholder(other)));
                    break;
            }
        }

        return parts;
    }

    private static string Placeholder(string blockType) => $"[Claude Code {blockType} block]";

    // The CLI writes the stop of a turn by the user as a user message: it is not a prompt.
    private static bool IsInterruption(List<AgentMessagePart> parts)
        => parts is [AgentMessagePart.Text { Value: var text }] &&
           text.Trim() is var trimmed &&
           trimmed.StartsWith("[Request interrupted by user", StringComparison.Ordinal) &&
           trimmed.EndsWith(']');

    private static ClaudeCodeTranscriptCompaction ReadCompaction(JsonElement boundary)
    {
        ClaudeCodeJson.TryGetObject(boundary, "compactMetadata", out var metadata);
        return new ClaudeCodeTranscriptCompaction(
            ClaudeCodeJson.GetString(metadata, "trigger"),
            ClaudeCodeJson.GetInt64(metadata, "preTokens"),
            ClaudeCodeJson.GetInt64(metadata, "postTokens"));
    }

    // The CLI replaces a large tool result with a preview and the path of the file it saved beside the transcript
    // (`<id>/tool-results/<file>`): the result is read from that file when it is still there.
    private static AgentToolResult ResolvePersistedOutput(AgentToolResult result, string folder, string sessionId)
    {
        if (!result.Items.Any(static item => item is AgentToolResultItem.Text { Value: var text } && text.Contains("<persisted-output>", StringComparison.Ordinal)))
        {
            return result;
        }

        var items = result.Items
            .Select(item => item is AgentToolResultItem.Text { Value: var text } && ReadPersistedOutput(text, folder, sessionId) is { } full
                ? new AgentToolResultItem.Text(full)
                : item)
            .ToList();
        return result with { Items = items };
    }

    private static string? ReadPersistedOutput(string text, string folder, string sessionId)
    {
        const string SavedTo = "saved to: ";
        var start = text.IndexOf("<persisted-output>", StringComparison.Ordinal);
        var marker = start < 0 ? -1 : text.IndexOf(SavedTo, start, StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var from = marker + SavedTo.Length;
        var end = text.IndexOfAny(['\r', '\n'], from);
        var savedPath = text[from..(end < 0 ? text.Length : end)].Trim();
        // Only the name of the file is used: the folder is the one of this transcript, wherever it was when written.
        var name = savedPath[(savedPath.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        try
        {
            var path = Path.Combine(folder, sessionId, "tool-results", name);
            return File.Exists(path) && ReadSmallFile(path, MaxPersistedOutputBytes) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
