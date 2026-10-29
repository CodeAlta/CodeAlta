using System.Buffers;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using CodeAlta.CodexAppServer.Protocol;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>A tool call of Codex, as the session records it.</summary>
/// <param name="CallId">The identifier of the item.</param>
/// <param name="Name">The name of the tool.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="CodeAlta">Whether the tool is one of CodeAlta, which the session runs through its MCP server.</param>
internal sealed record CodexCliToolCall(string CallId, string Name, JsonElement Arguments, bool CodeAlta);

/// <summary>Maps the items of a Codex turn to the tool calls and the results a CodeAlta session records.</summary>
internal static class CodexCliToolCalls
{
    /// <summary>The most text a result keeps; the rest is cut.</summary>
    private const int ResultLimit = 60_000;

    /// <summary>The largest generated image read for the session, as the session reads any image of a tool.</summary>
    private const long GeneratedImageLimit = 64L * 1024 * 1024;

    /// <summary>
    /// Maps an item to the tool call the session records, or returns <see langword="null"/> for an item that is not a
    /// tool call (a message, reasoning, a plan, a compaction). A tool of CodeAlta keeps its name and arguments; a tool
    /// of Codex is named as the window shows a command, an edit or a search.
    /// </summary>
    public static CodexCliToolCall? Map(ThreadItem item, IReadOnlyDictionary<string, AgentToolDefinition> codeAltaTools, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(codeAltaTools);
        switch (item)
        {
            case ThreadItem.CommandExecutionThreadItem command:
                return new CodexCliToolCall(command.Id, "shell", Build(writer =>
                {
                    writer.WriteString("command", command.Command);
                    writer.WriteString("cwd", command.Cwd.Value);
                }), false);
            case ThreadItem.FileChangeThreadItem change:
                var paths = change.Changes.Select(entry => Full(entry.Path, workingDirectory)).ToArray();
                return new CodexCliToolCall(change.Id, paths.Length == 1 ? "Edit" : "edit", Build(writer =>
                {
                    if (paths.Length > 0)
                    {
                        // The name the file tools of CodeAlta read the path of a change from: the session follows the file.
                        writer.WriteString("file_path", paths[0]);
                    }

                    writer.WriteStartArray("changes");
                    foreach (var entry in change.Changes)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("path", Full(entry.Path, workingDirectory));
                        writer.WriteString("kind", KindOf(entry.Kind));
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }), false);
            case ThreadItem.McpToolCallThreadItem mcp when string.Equals(mcp.Server, CodexCliPrompts.McpServerName, StringComparison.Ordinal) && codeAltaTools.ContainsKey(mcp.Tool):
                return new CodexCliToolCall(mcp.Id, mcp.Tool, Arguments(mcp.Arguments), true);
            case ThreadItem.McpToolCallThreadItem mcp:
                return new CodexCliToolCall(mcp.Id, HarnessToolCallBridge.SanitizeName($"{mcp.Server}__{mcp.Tool}"), Arguments(mcp.Arguments), false);
            case ThreadItem.DynamicToolCallThreadItem dynamic:
                return new CodexCliToolCall(dynamic.Id, HarnessToolCallBridge.SanitizeName(dynamic.Tool), Arguments(dynamic.Arguments), false);
            case ThreadItem.WebSearchThreadItem search:
                return new CodexCliToolCall(search.Id, "web_search", Build(writer => writer.WriteString("query", search.Query)), false);
            case ThreadItem.ImageViewThreadItem image:
                return new CodexCliToolCall(image.Id, "view_image", Build(writer => writer.WriteString("path", Full(image.Path.Value, workingDirectory))), false);
            case ThreadItem.ImageGenerationThreadItem generation:
                // The call is told when it starts, before Codex has its revised prompt: the result gives it.
                return new CodexCliToolCall(generation.Id, "image_generation", Build(writer =>
                {
                    if (!string.IsNullOrWhiteSpace(generation.RevisedPrompt))
                    {
                        writer.WriteString("prompt", generation.RevisedPrompt);
                    }
                }), false);
            default:
                return null;
        }
    }

    /// <summary>Gets the result of a completed tool item, or <see langword="null"/> while it runs.</summary>
    public static AgentToolResult? Result(ThreadItem item)
    {
        switch (item)
        {
            case ThreadItem.CommandExecutionThreadItem command:
                if (command.Status is CommandExecutionStatus.InProgress)
                {
                    return null;
                }

                if (command.Status is CommandExecutionStatus.Declined)
                {
                    return Failure("The command was declined.");
                }

                var output = Limit(command.AggregatedOutput ?? string.Empty);
                var exit = command.ExitCode;
                var text = exit is null or 0 ? (output.Length == 0 ? "(no output)" : output) : $"{output}{(output.Length == 0 ? string.Empty : "\n")}Exit code: {exit}";
                var succeeded = command.Status is CommandExecutionStatus.Completed && exit is null or 0;
                return new AgentToolResult(succeeded, [new AgentToolResultItem.Text(text)], succeeded ? null : text);
            case ThreadItem.FileChangeThreadItem change:
                if (change.Status is PatchApplyStatus.InProgress)
                {
                    return null;
                }

                if (change.Status is PatchApplyStatus.Declined)
                {
                    return Failure("The change was declined.");
                }

                var summary = new StringBuilder();
                foreach (var entry in change.Changes)
                {
                    summary.Append(KindOf(entry.Kind) switch { "add" => "Created ", "delete" => "Deleted ", _ => "Edited " }).AppendLine(entry.Path);
                    if (!string.IsNullOrWhiteSpace(entry.Diff))
                    {
                        summary.AppendLine(entry.Diff.TrimEnd());
                    }
                }

                var changed = Limit(summary.ToString().TrimEnd());
                return change.Status is PatchApplyStatus.Completed
                    ? new AgentToolResult(true, [new AgentToolResultItem.Text(changed.Length == 0 ? "(no change)" : changed)])
                    : Failure(changed.Length == 0 ? "The change failed." : $"The change failed.\n{changed}");
            case ThreadItem.McpToolCallThreadItem mcp:
                if (mcp.Status is McpToolCallStatus.InProgress)
                {
                    return null;
                }

                if (mcp.Error is { } error)
                {
                    return Failure(error.Message);
                }

                return new AgentToolResult(mcp.Status is McpToolCallStatus.Completed, [new AgentToolResultItem.Text(Limit(RenderContent(mcp.Result)))]);
            case ThreadItem.DynamicToolCallThreadItem dynamic:
                if (dynamic.Status is DynamicToolCallStatus.InProgress)
                {
                    return null;
                }

                var content = dynamic.ContentItems is { Count: > 0 } items ? string.Join("\n", items.Select(static item => JsonSerializer.Serialize(item, CodexProtocolJsonContext.Default.DynamicToolCallOutputContentItem))) : "(no output)";
                return new AgentToolResult(dynamic.Success != false, [new AgentToolResultItem.Text(Limit(content))]);
            case ThreadItem.WebSearchThreadItem search:
                return new AgentToolResult(true, [new AgentToolResultItem.Text($"Searched the web: {search.Query}")]);
            case ThreadItem.ImageViewThreadItem image:
                return new AgentToolResult(true, [new AgentToolResultItem.Text($"Viewed {image.Path.Value}")]);
            case ThreadItem.ImageGenerationThreadItem generation:
                return generation.Failure is null ? GeneratedImage(generation.SavedPath?.Value, generation.RevisedPrompt) : Failure("The image could not be generated.");
            default:
                return null;
        }
    }

    /// <summary>Renders a plan of a turn as a checklist.</summary>
    public static string RenderPlan(TurnPlanUpdatedNotification plan)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(plan.Explanation))
        {
            builder.AppendLine(plan.Explanation.Trim());
        }

        foreach (var step in plan.Plan)
        {
            builder.Append(step.Status switch { TurnPlanStepStatus.Completed => "- [x] ", TurnPlanStepStatus.InProgress => "- [~] ", _ => "- [ ] " }).AppendLine(step.Step);
        }

        return builder.Length == 0 ? "Plan updated." : builder.ToString().TrimEnd();
    }

    // The image Codex saved in its home, as an image of the result: the session keeps a copy beside its journal and
    // shows it. Only the text is left when the file cannot be read.
    private static AgentToolResult GeneratedImage(string? path, string? revisedPrompt)
    {
        var prompt = string.IsNullOrWhiteSpace(revisedPrompt) ? string.Empty : $"\nPrompt: {revisedPrompt.Trim()}";
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AgentToolResult(true, [new AgentToolResultItem.Text("Generated an image." + prompt)]);
        }

        var text = new AgentToolResultItem.Text($"Generated {path}{prompt}");
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length is > 0 and <= GeneratedImageLimit)
            {
                return new AgentToolResult(true, [text, new AgentToolResultItem.Image(Convert.ToBase64String(File.ReadAllBytes(path)), "image/png", file.Name)]);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
        }

        return new AgentToolResult(true, [text]);
    }

    private static string RenderContent(McpToolCallResult? result)
    {
        if (result is null || result.Content.Count == 0)
        {
            return result?.StructuredContent is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } structured ? structured.GetRawText() : "(no output)";
        }

        var builder = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString() switch
                {
                    "text" when block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String => text.GetString(),
                    "image" => "[image]",
                    "resource_link" when block.TryGetProperty("uri", out var uri) => uri.GetString(),
                    _ => block.GetRawText(),
                }
                : block.GetRawText());
        }

        return builder.ToString();
    }

    private static string KindOf(PatchChangeKind kind) => kind switch
    {
        PatchChangeKind.AddPatchChangeKind => "add",
        PatchChangeKind.DeletePatchChangeKind => "delete",
        _ => "update",
    };

    private static string Full(string path, string workingDirectory)
    {
        try
        {
            return Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Combine(workingDirectory, path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string Limit(string text) => text.Length <= ResultLimit ? text : text[..ResultLimit] + $"\n[{text.Length - ResultLimit} more characters cut]";

    private static AgentToolResult Failure(string message) => new(false, [new AgentToolResultItem.Text(message)], message);

    private static JsonElement Arguments(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.Object ? arguments.Clone() : Build(writer =>
        {
            if (arguments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                writer.WritePropertyName("input");
                arguments.WriteTo(writer);
            }
        });

    private static JsonElement Build(Action<Utf8JsonWriter> writeProperties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
