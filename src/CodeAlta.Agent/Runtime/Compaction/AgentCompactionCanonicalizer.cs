using System.Text.Json;

namespace CodeAlta.Agent.Runtime.Compaction;

internal static class AgentCompactionCanonicalizer
{
    public static IReadOnlyList<AgentCompactionUnit> Normalize(IReadOnlyList<AgentConversationMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var units = BuildUnits(messages);
        if (units.Count <= 1)
        {
            return units;
        }

        return CollapseRepeatedLowValueUnits(units);
    }

    private static IReadOnlyList<AgentCompactionUnit> BuildUnits(IReadOnlyList<AgentConversationMessage> messages)
    {
        var units = new List<AgentCompactionUnit>();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role is AgentConversationRole.Assistant &&
                message.Parts.OfType<AgentMessagePart.ToolCall>().Any())
            {
                var toolMessages = new List<AgentConversationMessage>();
                var expectedCallIds = message.Parts
                    .OfType<AgentMessagePart.ToolCall>()
                    .Select(static part => part.CallId)
                    .ToHashSet(StringComparer.Ordinal);

                var nextIndex = index + 1;
                while (nextIndex < messages.Count &&
                       messages[nextIndex].Role is AgentConversationRole.Tool &&
                       messages[nextIndex].Parts.OfType<AgentMessagePart.ToolResult>().Any(part => expectedCallIds.Contains(part.CallId)))
                {
                    toolMessages.Add(messages[nextIndex]);
                    nextIndex++;
                }

                units.Add(new AgentCompactionToolInteractionUnit(message, toolMessages));
                index = nextIndex - 1;
                continue;
            }

            units.Add(new AgentCompactionMessageUnit(message));
        }

        return units;
    }

    private static IReadOnlyList<AgentCompactionUnit> CollapseRepeatedLowValueUnits(IReadOnlyList<AgentCompactionUnit> units)
    {
        var normalized = new List<AgentCompactionUnit>(units.Count);
        for (var index = 0; index < units.Count; index++)
        {
            if (units[index] is not AgentCompactionToolInteractionUnit candidate ||
                !TryGetLowValueCollapseKey(candidate, out var collapseKey))
            {
                normalized.Add(units[index]);
                continue;
            }

            var run = new List<AgentCompactionToolInteractionUnit> { candidate };
            var nextIndex = index + 1;
            while (nextIndex < units.Count &&
                   units[nextIndex] is AgentCompactionToolInteractionUnit nextCandidate &&
                   TryGetLowValueCollapseKey(nextCandidate, out var nextKey) &&
                   string.Equals(collapseKey, nextKey, StringComparison.Ordinal))
            {
                run.Add(nextCandidate);
                nextIndex++;
            }

            if (run.Count == 1)
            {
                normalized.Add(candidate);
                continue;
            }

            var latest = run[^1];
            normalized.Add(new AgentCompactionToolInteractionUnit(
                latest.AssistantMessage,
                latest.ToolMessages,
                RepeatCount: run.Count,
                IsCollapsed: true,
                CollapseKey: collapseKey));
            index = nextIndex - 1;
        }

        return normalized;
    }

    private static bool TryGetLowValueCollapseKey(AgentCompactionToolInteractionUnit unit, out string key)
    {
        key = string.Empty;
        if (unit.ToolCalls.Count != 1 ||
            unit.ToolResults.Count != 1 ||
            unit.ToolMessages.Count != 1 ||
            unit.RepeatCount != 1)
        {
            return false;
        }

        if (!unit.ToolResults[0].Result.Success ||
            HasHighSignalToolOutput(unit.ToolResults[0].Result))
        {
            return false;
        }

        if (unit.AssistantMessage.Parts.Any(static part => part is not AgentMessagePart.ToolCall))
        {
            return false;
        }

        var toolCall = unit.ToolCalls[0];
        var signature = TryGetToolSignature(toolCall.Name, toolCall.Arguments);
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        key = signature;
        return true;
    }

    private static bool HasHighSignalToolOutput(AgentToolResult result)
    {
        var rendered = string.Join(
            "\n",
            result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));
        return !string.IsNullOrWhiteSpace(result.Error) ||
               ContainsHighSignal(rendered);
    }

    private static string? TryGetToolSignature(string toolName, JsonElement arguments)
    {
        static string? GetString(JsonElement args, string propertyName)
            => args.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.String
                ? property.GetString()
                : null;

        var normalized = toolName switch
        {
            "read_file" or "list_dir" => GetString(arguments, "path"),
            "grep" => $"{GetString(arguments, "path")}|{GetString(arguments, "pattern") ?? GetString(arguments, "query")}",
            "shell_command" => GetString(arguments, "command"),
            "write_file" or "replace_in_file" or "delete_file_or_dir" => GetString(arguments, "path"),
            "rename_file_or_dir" => $"{GetString(arguments, "old_path")}|{GetString(arguments, "new_path")}",
            "webget" => GetString(arguments, "url"),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return $"{toolName}|{normalized.Trim()}";
    }

    private static bool ContainsHighSignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] keywords =
        [
            "error",
            "exception",
            "fail",
            "failed",
            "traceback",
            "fatal",
            "warning",
            "assert",
            "timed out",
            "timeout",
        ];

        return keywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
