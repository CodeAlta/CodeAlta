using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Models;
using CodeAlta.Presentation.Styling;
using XenoAtom.Ansi;

namespace CodeAlta.Presentation.Formatting;

internal static class ToolCallSummaryFormatter
{
    private const int ToolCallPreviewLimit = 16;

    public static string BuildSummaryMarkup(ToolCallEntryState entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var primaryLabel = BuildPrimaryToolLabel(entry);
        var secondaryLabel = BuildToolSecondaryLabel(entry);
        var detailLine = BuildToolCardDetailLine(entry);
        var builder = new StringBuilder();
        builder.Append(GetToolStatusIconMarkup(entry.Status))
            .Append(' ')
            .Append("[bold][")
            .Append(UiPalette.GetToolStatusMarkup(entry.Status))
            .Append("]")
            .Append(AnsiMarkup.Escape(primaryLabel))
            .Append("[/][/]");

        if (!string.IsNullOrWhiteSpace(secondaryLabel))
        {
            builder.Append(" [dim]")
                .Append(AnsiMarkup.Escape(secondaryLabel))
                .Append("[/]");
        }

        builder.AppendLine()
            .Append("[dim]")
            .Append(AnsiMarkup.Escape(detailLine))
            .Append("[/]")
            .Append("[/]");

        return builder.ToString();
    }

    public static string BuildGroupSummaryMarkup(ToolCallGroupState group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var total = group.ToolCalls.Count;
        var running = group.ToolCalls.Values.Count(static entry => entry.Status == ToolCallDisplayStatus.Running);
        var completed = group.ToolCalls.Values.Count(static entry => entry.Status == ToolCallDisplayStatus.Completed);
        var failed = group.ToolCalls.Values.Count(static entry => entry.Status == ToolCallDisplayStatus.Failed);
        var canceled = group.ToolCalls.Values.Count(static entry => entry.Status == ToolCallDisplayStatus.Canceled);
        var pending = group.ToolCalls.Values.Count(static entry => entry.Status == ToolCallDisplayStatus.Pending);

        var parts = new List<string>
        {
            $"{total.ToString(CultureInfo.InvariantCulture)} call(s)",
        };

        if (running > 0)
        {
            parts.Add($"[{UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Running)}]{running.ToString(CultureInfo.InvariantCulture)} running[/]");
        }

        if (pending > 0)
        {
            parts.Add($"[{UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Pending)}]{pending.ToString(CultureInfo.InvariantCulture)} pending[/]");
        }

        if (completed > 0)
        {
            parts.Add($"[{UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Completed)}]{completed.ToString(CultureInfo.InvariantCulture)} done[/]");
        }

        if (failed > 0)
        {
            parts.Add($"[{UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Failed)}]{failed.ToString(CultureInfo.InvariantCulture)} failed[/]");
        }

        if (canceled > 0)
        {
            parts.Add($"[{UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Canceled)}]{canceled.ToString(CultureInfo.InvariantCulture)} canceled[/]");
        }

        return $"[{UiPalette.MutedMarkup}]" + string.Join(" · ", parts) + "[/]";
    }

    public static string BuildDetailMarkdown(ToolCallEntryState entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var builder = new StringBuilder();
        builder.Append("- Tool: ").AppendLine(BuildPrimaryToolLabel(entry))
            .Append("- Kind: ").AppendLine(GetActivityKindLabel(entry.ActivityKind))
            .Append("- Status: ").AppendLine(SplitPascalCase(entry.Status.ToString()))
            .Append("- First Seen: `").Append(FormatChatCardTimestamp(entry.FirstSeenAt)).AppendLine("`")
            .Append("- Last Updated: `").Append(FormatChatCardTimestamp(entry.LastUpdatedAt)).AppendLine("`");

        if (!string.IsNullOrWhiteSpace(entry.ParentToolCallId))
        {
            builder.Append("- Parent: `").Append(entry.ParentToolCallId).AppendLine("`");
        }

        if (!string.IsNullOrWhiteSpace(entry.CommandText))
        {
            builder.AppendLine()
                .AppendLine("**Command**")
                .AppendLine()
                .AppendLine(FormatChatCodeFence(entry.CommandText, "text"));
        }

        if (!string.IsNullOrWhiteSpace(entry.ArgumentText))
        {
            builder.AppendLine()
                .AppendLine("**Arguments**")
                .AppendLine()
                .AppendLine(FormatChatCodeFence(entry.ArgumentText, IsJsonPayload(entry.ArgumentText) ? "json" : "text"));
        }

        return builder.ToString();
    }

    public static string BuildStatsMarkup(ToolCallEntryState entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var duration = (entry.CompletedAt ?? entry.LastUpdatedAt) - entry.FirstSeenAt;
        return $"[dim]{entry.OutputLineCount.ToString(CultureInfo.InvariantCulture)} lines · {FormatToolCallKilobytes(entry.OutputByteCount)} · {FormatToolCallDuration(duration)}[/]";
    }

    private static string BuildPrimaryToolLabel(ToolCallEntryState entry)
    {
        if (entry.ActivityKind == AgentActivityKind.CommandExecution &&
            !string.IsNullOrWhiteSpace(entry.CommandText))
        {
            return ExtractCommandDisplayName(entry.CommandText!);
        }

        return ResolveToolDisplayName(entry.ActivityKind, entry.DisplayName);
    }

    private static string? BuildToolSecondaryLabel(ToolCallEntryState entry)
    {
        var contextLabel = BuildCompactToolContext(entry);
        if (string.IsNullOrWhiteSpace(contextLabel))
        {
            return null;
        }

        return string.Equals(contextLabel, BuildPrimaryToolLabel(entry), StringComparison.OrdinalIgnoreCase)
            ? null
            : contextLabel;
    }

    private static string BuildToolCardDetailLine(ToolCallEntryState entry)
    {
        var contextLabel = BuildCompactToolContext(entry);
        var prefix = !string.IsNullOrWhiteSpace(entry.OutputPreview) &&
                     !string.Equals(entry.OutputPreview, contextLabel, StringComparison.OrdinalIgnoreCase)
            ? entry.OutputPreview!
            : !string.IsNullOrWhiteSpace(entry.StatusMessage) && !IsRedundantStatusDetail(entry.StatusMessage, entry.OutputBuffer.ToString())
                ? BuildToolPreview(entry.StatusMessage) ?? SplitPascalCase(entry.Status.ToString())
                : SplitPascalCase(entry.Status.ToString());

        return string.Equals(prefix, SplitPascalCase(entry.Status.ToString()), StringComparison.OrdinalIgnoreCase) && entry.OutputLineCount > 0
            ? $"{entry.OutputLineCount.ToString(CultureInfo.InvariantCulture)}L · {FormatToolCallKilobytes(entry.OutputByteCount)}"
            : $"{prefix} · {entry.OutputLineCount.ToString(CultureInfo.InvariantCulture)}L · {FormatToolCallKilobytes(entry.OutputByteCount)}";
    }

    private static string GetToolStatusIconMarkup(ToolCallDisplayStatus status)
        => $"[{UiPalette.GetToolStatusMarkup(status)}]●[/]";

    private static string ResolveToolDisplayName(AgentActivityKind kind, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName!;
        }

        return kind switch
        {
            AgentActivityKind.CommandExecution => "command",
            AgentActivityKind.FileChange => "file_change",
            _ => GetActivityKindLabel(kind),
        };
    }

    private static string ExtractCommandDisplayName(string commandText)
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
        if (string.IsNullOrWhiteSpace(executable))
        {
            return "command";
        }

        if (!ShouldIncludeSubcommand(executable))
        {
            return executable;
        }

        var firstSubcommandIndex = FindDisplayableSubcommandIndex(tokens, startIndex: 1);
        if (firstSubcommandIndex < 0)
        {
            return executable;
        }

        var firstSubcommand = NormalizeCommandToken(tokens[firstSubcommandIndex]);
        if (string.IsNullOrWhiteSpace(firstSubcommand))
        {
            return executable;
        }

        var builder = new StringBuilder()
            .Append(executable)
            .Append(' ')
            .Append(firstSubcommand);

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
                    continue;
                }

                current.Append(ch);
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
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Contains('/', StringComparison.Ordinal))
        {
            trimmed = Path.GetFileName(trimmed);
        }

        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
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

            if (!IsDisplayableSubcommandToken(token))
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private static bool ShouldIncludeSubcommand(string executable)
    {
        return executable switch
        {
            "dotnet" or "git" or "cargo" or "npm" or "pnpm" or "yarn" or "bun" or "uv" or "docker" or "kubectl" or "gh" or "brew" => true,
            _ => false,
        };
    }

    private static bool ShouldIncludeSecondSubcommand(string executable, string subcommand)
    {
        return executable switch
        {
            "dotnet" => subcommand is "tool" or "nuget" or "workload" or "package" or "reference" or "user-secrets",
            "git" => subcommand is "remote" or "branch" or "stash" or "worktree" or "submodule" or "config",
            "docker" => subcommand is "compose" or "image" or "container" or "builder" or "buildx" or "volume" or "network" or "system",
            _ => false,
        };
    }

    private static bool IsDisplayableSubcommandToken(string token)
    {
        var trimmed = token.Trim().Trim('"', '\'', '`');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed is "|" or "||" or "&&" or ";" or ">" or ">>" or "<")
        {
            return false;
        }

        if (trimmed.Contains('='))
        {
            return false;
        }

        if (trimmed.Contains('\\', StringComparison.Ordinal) ||
            trimmed.Contains('/', StringComparison.Ordinal) ||
            trimmed.StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        return !IsOptionToken(trimmed);
    }

    private static bool IsOptionToken(string token)
    {
        var trimmed = token.Trim();
        return trimmed.StartsWith("-", StringComparison.Ordinal) ||
               trimmed.StartsWith("/", StringComparison.Ordinal);
    }

    private static string? BuildCompactToolContext(ToolCallEntryState entry)
    {
        if (entry.Details is { } details &&
            TryBuildCompactContextFromDetails(details, out var detailPreview))
        {
            return detailPreview;
        }

        if (!IsJsonPayload(entry.ArgumentText) &&
            TryBuildCompactContextPreview(entry.ArgumentText, out var argumentPreview))
        {
            return argumentPreview;
        }

        if (entry.ActivityKind == AgentActivityKind.CommandExecution &&
            TryExtractCommandArgument(entry.CommandText, out var commandArgument) &&
            TryBuildCompactContextPreview(commandArgument, out var commandPreview))
        {
            return commandPreview;
        }

        return null;
    }

    private static bool TryBuildCompactContextPreview(string? source, out string? preview)
    {
        preview = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        foreach (var line in SplitToolOutputLines(source))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("cwd:", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed["cwd:".Length..].Trim();
            }
            else if (trimmed.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed["server:".Length..].Trim();
            }

            if (TryExtractPathLeaf(trimmed, out var pathLeaf))
            {
                preview = BuildToolPreview(pathLeaf);
                return !string.IsNullOrWhiteSpace(preview);
            }

            preview = BuildToolPreview(trimmed);
            return !string.IsNullOrWhiteSpace(preview);
        }

        return false;
    }

    private static bool TryBuildCompactContextFromDetails(JsonElement details, out string? preview)
    {
        preview = null;
        if (details.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!details.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var propertyName in new[] { "path", "pattern", "query", "intent", "description", "database", "command" })
        {
            if (TryGetStringProperty(arguments, propertyName, out var value) &&
                TryBuildCompactContextPreview(value, out preview))
            {
                return true;
            }
        }

        if (details.TryGetProperty("input", out var input) &&
            input.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "path", "pattern", "query", "intent", "description", "database", "command" })
            {
                if (TryGetStringProperty(input, propertyName, out var value) &&
                    TryBuildCompactContextPreview(value, out preview))
                {
                    return true;
                }
            }
        }

        if (arguments.TryGetProperty("view_range", out var viewRange) &&
            viewRange.ValueKind == JsonValueKind.Array &&
            viewRange.GetArrayLength() >= 2)
        {
            var start = viewRange[0].ToString();
            var end = viewRange[1].ToString();
            preview = $"{start}-{end}";
            return true;
        }

        return false;
    }

    private static bool TryExtractCommandArgument(string? commandText, out string? argument)
    {
        argument = null;
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return false;
        }

        var command = NormalizeToolOutput(commandText)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var firstSpace = command.IndexOf(' ');
        if (firstSpace < 0 || firstSpace >= command.Length - 1)
        {
            return false;
        }

        argument = command[(firstSpace + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(argument);
    }

    private static bool IsJsonPayload(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static bool TryExtractPathLeaf(string text, out string? leaf)
    {
        leaf = null;
        var trimmed = text.Trim().Trim('"', '\'', '`');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        var candidate = trimmed;
        if (trimmed.StartsWith("+++ b/", StringComparison.Ordinal))
        {
            candidate = trimmed["+++ b/".Length..];
        }
        else if (trimmed.StartsWith("diff --git a/", StringComparison.Ordinal) &&
                 TryExtractDiffPath(trimmed, out var diffPath))
        {
            candidate = diffPath!;
        }

        if (!(candidate.Contains('\\', StringComparison.Ordinal) || candidate.Contains('/', StringComparison.Ordinal)))
        {
            return false;
        }

        leaf = Path.GetFileName(candidate);
        if (string.IsNullOrWhiteSpace(leaf))
        {
            var directory = candidate.TrimEnd('\\', '/');
            leaf = Path.GetFileName(directory);
        }

        return !string.IsNullOrWhiteSpace(leaf);
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
                var separator = " b/";
                var separatorIndex = trimmed.IndexOf(separator, StringComparison.Ordinal);
                if (separatorIndex >= 0)
                {
                    path = trimmed[(separatorIndex + separator.Length)..];
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsRedundantStatusDetail(string? statusDetail, string output)
    {
        if (string.IsNullOrWhiteSpace(statusDetail) || string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var normalizedStatus = NormalizeToolOutput(statusDetail).Trim();
        var normalizedOutput = NormalizeToolOutput(output).Trim();
        return normalizedOutput.StartsWith(normalizedStatus, StringComparison.Ordinal);
    }

    private static string? BuildToolPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeToolOutput(text);
        var lastLine = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?
            .Trim();
        var preview = string.IsNullOrWhiteSpace(lastLine) ? normalized.Trim() : lastLine;
        if (preview.Length <= ToolCallPreviewLimit)
        {
            return preview;
        }

        return preview[..ToolCallPreviewLimit].TrimEnd() + "...";
    }

    private static string NormalizeToolOutput(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');

    private static IReadOnlyList<string> SplitToolOutputLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return NormalizeToolOutput(text).Split('\n');
    }

    private static string FormatToolCallKilobytes(int byteCount)
        => $"{(byteCount / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} KB";

    private static string FormatToolCallDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
        }

        if (duration.TotalMinutes >= 1)
        {
            return duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }

        return $"{duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
    }

    private static string FormatChatCardTimestamp(DateTimeOffset timestamp)
        => timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatChatCodeFence(string content, string language)
    {
        var fence = content.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        return $"{fence}{language}\n{content}\n{fence}";
    }

    internal static string GetActivityKindLabel(AgentActivityKind kind)
    {
        return kind switch
        {
            AgentActivityKind.Turn => "Turn",
            AgentActivityKind.ToolCall => "Tool Call",
            AgentActivityKind.CommandExecution => "Command Execution",
            AgentActivityKind.FileChange => "File Change",
            AgentActivityKind.McpToolCall => "MCP Tool Call",
            AgentActivityKind.DynamicToolCall => "Dynamic Tool Call",
            AgentActivityKind.CollabAgentToolCall => "Collab Agent Tool Call",
            AgentActivityKind.Subagent => "Subagent",
            AgentActivityKind.Hook => "Hook",
            AgentActivityKind.Skill => "Skill",
            AgentActivityKind.Compaction => "Compaction",
            AgentActivityKind.WebSearch => "Web Search",
            AgentActivityKind.ImageGeneration => "Image Generation",
            _ => SplitPascalCase(kind.ToString()),
        };
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (index > 0 && char.IsUpper(ch) && !char.IsWhiteSpace(value[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    internal static bool TryGetStringProperty(JsonElement element, string propertyName, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }
}