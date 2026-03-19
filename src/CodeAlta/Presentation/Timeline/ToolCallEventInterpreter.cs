using System.IO;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;

internal static class ToolCallEventInterpreter
{
    private const int ToolCallPreviewLimit = 16;

    public static bool IsToolTimelineActivity(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return activity.Kind is AgentActivityKind.ToolCall
            or AgentActivityKind.CommandExecution
            or AgentActivityKind.FileChange
            or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall
            or AgentActivityKind.CollabAgentToolCall
            or AgentActivityKind.Subagent
            or AgentActivityKind.Hook
            or AgentActivityKind.Skill
            or AgentActivityKind.WebSearch
            or AgentActivityKind.ImageGeneration;
    }

    public static bool IsToolTimelineContent(AgentContentKind kind)
        => kind is AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput;

    public static string ResolveToolDisplayName(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.Kind == AgentActivityKind.CommandExecution && !string.IsNullOrWhiteSpace(ResolveToolCommandText(activity)))
        {
            return ExtractCommandDisplayName(ResolveToolCommandText(activity)!);
        }

        if (activity.Kind == AgentActivityKind.ToolCall &&
            string.Equals(activity.Name, "shell_command", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(ResolveToolCommandText(activity)))
        {
            return ExtractCommandDisplayName(ResolveToolCommandText(activity)!);
        }

        if (activity.Kind == AgentActivityKind.FileChange)
        {
            return "file_change";
        }

        if (!string.IsNullOrWhiteSpace(activity.Name))
        {
            return activity.Kind == AgentActivityKind.Subagent && activity.Details is { } subagentDetails &&
                ToolCallSummaryFormatter.TryGetStringProperty(subagentDetails, "agentName", out var agentName)
                ? agentName!
                : activity.Name!;
        }

        if (activity.Details is { } details)
        {
            if (ToolCallSummaryFormatter.TryGetStringProperty(details, "toolName", out var toolName) ||
                ToolCallSummaryFormatter.TryGetStringProperty(details, "mcpToolName", out toolName) ||
                ToolCallSummaryFormatter.TryGetStringProperty(details, "tool", out toolName) ||
                ToolCallSummaryFormatter.TryGetStringProperty(details, "name", out toolName) ||
                ToolCallSummaryFormatter.TryGetStringProperty(details, "agentName", out toolName) ||
                ToolCallSummaryFormatter.TryGetStringProperty(details, "agentDisplayName", out toolName))
            {
                return toolName!;
            }

            if (activity.Kind == AgentActivityKind.ToolCall && TryInferCopilotToolName(details, out var inferredToolName))
            {
                return inferredToolName!;
            }
        }

        return ToolCallSummaryFormatter.GetActivityKindLabel(activity.Kind);
    }

    public static string ResolveToolDisplayName(AgentActivityKind kind, string? displayName)
        => !string.IsNullOrWhiteSpace(displayName)
            ? displayName!
            : kind switch
            {
                AgentActivityKind.CommandExecution => "command",
                AgentActivityKind.FileChange => "file_change",
                _ => ToolCallSummaryFormatter.GetActivityKindLabel(kind),
            };

    public static string? ResolveToolCommandText(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.Details is { } details && ToolCallSummaryFormatter.TryGetStringProperty(details, "command", out var command))
        {
            return command;
        }

        if (activity.Details is { ValueKind: JsonValueKind.Object } detailObject)
        {
            if (detailObject.TryGetProperty("arguments", out var arguments) &&
                arguments.ValueKind == JsonValueKind.Object &&
                ToolCallSummaryFormatter.TryGetStringProperty(arguments, "command", out command))
            {
                return command;
            }

            if (detailObject.TryGetProperty("input", out var input) &&
                input.ValueKind == JsonValueKind.Object &&
                ToolCallSummaryFormatter.TryGetStringProperty(input, "command", out command))
            {
                return command;
            }
        }

        return activity.Kind == AgentActivityKind.CommandExecution ? activity.Name : null;
    }

    public static string? ResolveToolArgumentText(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.Details is not { } details || details.ValueKind != JsonValueKind.Object)
        {
            return activity.Kind is AgentActivityKind.WebSearch or AgentActivityKind.ImageGeneration ? activity.Message : null;
        }

        var parts = new List<string>();
        if (details.TryGetProperty("arguments", out var rawArguments))
        {
            parts.Add(FormatToolArguments(rawArguments));
        }

        AppendPart(parts, details, "cwd", static value => $"cwd: {value}");
        AppendPart(parts, details, "query");
        AppendPart(parts, details, "prompt");
        AppendPart(parts, details, "path");
        if (ToolCallSummaryFormatter.TryGetStringProperty(details, "server", out var server) ||
            ToolCallSummaryFormatter.TryGetStringProperty(details, "mcpServerName", out server))
        {
            parts.Add($"server: {server}");
        }

        AppendPart(parts, details, "agentDescription");

        if (TryResolveNestedString(details, out var detailedContent, "result", "detailedContent") && detailedContent is not null && TryExtractDiffPath(detailedContent, out var diffPath))
        {
            parts.Add(diffPath!);
        }

        if (TryResolveNestedString(details, out var outputContent, "result", "content") && outputContent is not null)
        {
            if (TryExtractFirstPath(outputContent, out var pathFromOutput))
            {
                parts.Add(pathFromOutput!);
            }
            else if (TryExtractCommonDirectory(outputContent, out var commonDirectory))
            {
                parts.Add(commonDirectory!);
            }
        }

        if (parts.Count == 0 && activity.Kind is AgentActivityKind.WebSearch or AgentActivityKind.ImageGeneration)
        {
            parts.Add(activity.Message ?? string.Empty);
        }

        if (parts.Count == 0 && activity.Kind == AgentActivityKind.ToolCall && TryResolveNestedString(details, out var copilotResultDetail, "result", "detailedContent"))
        {
            parts.Add(copilotResultDetail!);
        }

        return parts.Count == 0 ? null : string.Join($"{Environment.NewLine}{Environment.NewLine}", parts.Where(static part => !string.IsNullOrWhiteSpace(part)).Distinct(StringComparer.Ordinal));
    }

    public static string? ResolveToolOutput(AgentActivityEvent activity)
    {
        if (activity.Details is { } details)
        {
            if (ToolCallSummaryFormatter.TryGetStringProperty(details, "aggregatedOutput", out var aggregatedOutput))
            {
                return aggregatedOutput;
            }

            if (TryResolveNestedString(details, out var nestedOutput, "result", "content") ||
                TryResolveNestedString(details, out nestedOutput, "error", "message") ||
                TryResolveNestedString(details, out nestedOutput, "output", "body"))
            {
                return nestedOutput;
            }

            if (details.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("detailedContent", out var detailedContent))
            {
                return detailedContent.ValueKind == JsonValueKind.String ? detailedContent.GetString() : detailedContent.GetRawText();
            }
        }

        return activity.Kind == AgentActivityKind.CommandExecution || activity.Phase is AgentActivityPhase.Completed or AgentActivityPhase.Failed
            ? activity.Message
            : null;
    }

    public static bool TryInferCopilotToolName(JsonElement details, out string? toolName)
    {
        toolName = null;
        if (details.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryResolveNestedString(details, out var content, "result", "content") && content is not null)
        {
            if (string.Equals(content, "Intent logged", StringComparison.Ordinal))
            {
                toolName = "report_intent";
                return true;
            }

            if (LooksLikeReadOutput(content))
            {
                toolName = "read";
                return true;
            }

            if (LooksLikePathList(content))
            {
                toolName = "glob";
                return true;
            }
        }

        if (TryResolveNestedString(details, out var detailedContent, "result", "detailedContent") &&
            detailedContent is not null &&
            (TryExtractDiffPath(detailedContent, out _) || detailedContent.Contains("diff --git", StringComparison.Ordinal)))
        {
            toolName = "read";
            return true;
        }

        return false;
    }

    public static string ExtractCommandDisplayName(string commandText)
    {
        var command = NormalizeToolOutput(commandText).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return "command";
        }

        var tokens = TokenizeCommandDisplayName(command);
        if (tokens.Count == 0)
        {
            return "command";
        }

        var executable = NormalizeCommandToken(tokens[0]);
        if (string.IsNullOrWhiteSpace(executable) || !ShouldIncludeSubcommand(executable))
        {
            return string.IsNullOrWhiteSpace(executable) ? "command" : executable;
        }

        var firstSubcommandIndex = FindDisplayableSubcommandIndex(tokens, 1);
        if (firstSubcommandIndex < 0)
        {
            return executable;
        }

        var firstSubcommand = NormalizeCommandToken(tokens[firstSubcommandIndex]);
        if (string.IsNullOrWhiteSpace(firstSubcommand))
        {
            return executable;
        }

        var builder = new StringBuilder().Append(executable).Append(' ').Append(firstSubcommand);
        if (ShouldIncludeSecondSubcommand(executable, firstSubcommand))
        {
            var secondSubcommandIndex = FindDisplayableSubcommandIndex(tokens, firstSubcommandIndex + 1);
            if (secondSubcommandIndex >= 0)
            {
                var secondSubcommand = NormalizeCommandToken(tokens[secondSubcommandIndex]);
                if (!string.IsNullOrWhiteSpace(secondSubcommand))
                {
                    builder.Append(' ').Append(secondSubcommand);
                }
            }
        }

        return builder.ToString();
    }

    public static string PreferToolDisplayName(string? existing, string candidate, AgentActivityEvent activity)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return existing ?? ResolveToolDisplayName(activity.Kind, null);
        }

        if (string.IsNullOrWhiteSpace(existing) || HasExplicitToolDisplayName(activity.Details) || IsGenericToolDisplayName(existing, activity.Kind) || string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        return existing;
    }

    public static string? BuildToolPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeToolOutput(text);
        var lastLine = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        var preview = string.IsNullOrWhiteSpace(lastLine) ? normalized.Trim() : lastLine;
        return preview.Length <= ToolCallPreviewLimit ? preview : preview[..ToolCallPreviewLimit].TrimEnd() + "...";
    }

    public static string NormalizeToolOutput(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');

    public static IReadOnlyList<string> SplitToolOutputLines(string text)
        => string.IsNullOrEmpty(text) ? [] : NormalizeToolOutput(text).Split('\n');

    public static int CountLines(string text)
        => string.IsNullOrEmpty(text) ? 0 : NormalizeToolOutput(text).Count(static ch => ch == '\n') + 1;

    public static bool IsRedundantStatusDetail(string? statusDetail, string output)
    {
        if (string.IsNullOrWhiteSpace(statusDetail) || string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var normalizedStatus = NormalizeToolOutput(statusDetail).Trim();
        var normalizedOutput = NormalizeToolOutput(output).Trim();
        return normalizedOutput.StartsWith(normalizedStatus, StringComparison.Ordinal);
    }

    private static void AppendPart(List<string> parts, JsonElement details, string propertyName, Func<string, string>? formatter = null)
    {
        if (ToolCallSummaryFormatter.TryGetStringProperty(details, propertyName, out var value))
        {
            parts.Add(formatter is null ? value! : formatter(value!));
        }
    }

    private static bool TryResolveNestedString(JsonElement root, out string? value, params string[] path)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        value = current.ValueKind == JsonValueKind.String ? current.GetString() : current.ValueKind is JsonValueKind.Array or JsonValueKind.Object ? current.GetRawText() : current.ToString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string FormatToolArguments(JsonElement arguments)
        => arguments.ValueKind switch
        {
            JsonValueKind.String => arguments.GetString() ?? string.Empty,
            JsonValueKind.Object or JsonValueKind.Array => PrettyPrintJson(arguments),
            _ => arguments.ToString(),
        };

    private static string PrettyPrintJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool LooksLikeReadOutput(string text)
    {
        var lines = SplitToolOutputLines(text);
        var numberedLines = lines.Count(static line =>
        {
            var trimmed = line.TrimStart();
            var dotIndex = trimmed.IndexOf('.');
            return dotIndex > 0 && int.TryParse(trimmed[..dotIndex], out _);
        });

        return lines.Count > 0 && numberedLines >= Math.Min(3, lines.Count);
    }

    private static bool LooksLikePathList(string text)
    {
        var lines = SplitToolOutputLines(text).Where(static line => !string.IsNullOrWhiteSpace(line)).Take(12).ToArray();
        if (lines.Length < 2)
        {
            return false;
        }

        var matchingPathLines = lines.Count(static line =>
        {
            var trimmed = line.Trim().Trim('"', '\'', '`');
            if (trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Contains('/', StringComparison.Ordinal))
            {
                return true;
            }

            return !trimmed.Contains(" ", StringComparison.Ordinal) &&
                   (trimmed.StartsWith(".", StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(Path.GetExtension(trimmed)));
        });

        return matchingPathLines >= Math.Max(2, lines.Length - 1);
    }

    private static bool TryExtractDiffPath(string text, out string? path)
    {
        path = null;
        foreach (var line in SplitToolOutputLines(text))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                path = trimmed["+++ b/".Length..];
                return true;
            }

            if (trimmed.StartsWith("diff --git a/", StringComparison.Ordinal))
            {
                var separatorIndex = trimmed.IndexOf(" b/", StringComparison.Ordinal);
                if (separatorIndex >= 0)
                {
                    path = trimmed[(separatorIndex + 3)..];
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryExtractFirstPath(string text, out string? path)
    {
        path = SplitToolOutputLines(text)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line) && (line.Contains(':', StringComparison.Ordinal) || line.Contains('\\', StringComparison.Ordinal) || line.Contains('/', StringComparison.Ordinal)));
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryExtractCommonDirectory(string text, out string? directory)
    {
        directory = Path.GetDirectoryName(SplitToolOutputLines(text).Select(static line => line.Trim()).FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line) && line.Contains('\\', StringComparison.Ordinal)));
        return !string.IsNullOrWhiteSpace(directory);
    }

    private static List<string> TokenizeCommandDisplayName(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var ch in command)
        {
            if (quote is { } activeQuote)
            {
                if (ch == activeQuote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string NormalizeCommandToken(string token)
    {
        var trimmed = token.Trim().Trim('"', '\'', '`');
        if (trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Contains('/', StringComparison.Ordinal))
        {
            trimmed = Path.GetFileName(trimmed);
        }

        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    private static int FindDisplayableSubcommandIndex(IReadOnlyList<string> tokens, int startIndex)
    {
        var skipNext = false;
        for (var index = startIndex; index < tokens.Count; index++)
        {
            var token = tokens[index].Trim();
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            if (IsOptionToken(token))
            {
                skipNext = true;
                continue;
            }

            if (IsDisplayableSubcommandToken(token))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool ShouldIncludeSubcommand(string executable)
        => executable is "dotnet" or "git" or "cargo" or "npm" or "pnpm" or "yarn" or "bun" or "uv" or "docker" or "kubectl" or "gh" or "brew";

    private static bool ShouldIncludeSecondSubcommand(string executable, string subcommand)
        => executable switch
        {
            "dotnet" => subcommand is "tool" or "nuget" or "workload" or "package" or "reference" or "user-secrets",
            "git" => subcommand is "remote" or "branch" or "stash" or "worktree" or "submodule" or "config",
            "docker" => subcommand is "compose" or "image" or "container" or "builder" or "buildx" or "volume" or "network" or "system",
            _ => false,
        };

    private static bool IsDisplayableSubcommandToken(string token)
    {
        var trimmed = token.Trim().Trim('"', '\'', '`');
        return !string.IsNullOrWhiteSpace(trimmed) &&
               trimmed is not "|" and not "||" and not "&&" and not ";" and not ">" and not ">>" and not "<" &&
               !trimmed.Contains('=') &&
               !trimmed.Contains('\\', StringComparison.Ordinal) &&
               !trimmed.Contains('/', StringComparison.Ordinal) &&
               !trimmed.StartsWith(".", StringComparison.Ordinal) &&
               !IsOptionToken(trimmed);
    }

    private static bool IsOptionToken(string token)
        => token.Trim().StartsWith("-", StringComparison.Ordinal) || token.Trim().StartsWith("/", StringComparison.Ordinal);

    private static bool HasExplicitToolDisplayName(JsonElement? details)
        => details is { ValueKind: JsonValueKind.Object } element &&
           (ToolCallSummaryFormatter.TryGetStringProperty(element, "toolName", out _) ||
            ToolCallSummaryFormatter.TryGetStringProperty(element, "mcpToolName", out _) ||
            ToolCallSummaryFormatter.TryGetStringProperty(element, "tool", out _) ||
            ToolCallSummaryFormatter.TryGetStringProperty(element, "name", out _) ||
            ToolCallSummaryFormatter.TryGetStringProperty(element, "agentName", out _) ||
            ToolCallSummaryFormatter.TryGetStringProperty(element, "agentDisplayName", out _));

    private static bool IsGenericToolDisplayName(string candidate, AgentActivityKind kind)
        => string.Equals(candidate, ResolveToolDisplayName(kind, null), StringComparison.OrdinalIgnoreCase);
}
