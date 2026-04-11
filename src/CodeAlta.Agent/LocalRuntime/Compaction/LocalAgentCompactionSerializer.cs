using System.Security;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.LocalRuntime.Compaction;

internal static class LocalAgentCompactionSerializer
{
    private static readonly string[] HighSignalKeywords =
    [
        "error",
        "exception",
        "fail",
        "failed",
        "traceback",
        "fatal",
        "warning",
        "test",
        "build",
        "assert",
        "denied",
        "timed out",
        "timeout",
    ];

    public static LocalAgentCompactionSerializationResult BuildSummaryRequestBody(
        LocalAgentCompactionPreparation preparation,
        string? latestUserRequest,
        IReadOnlyList<string> readFiles,
        IReadOnlyList<string> modifiedFiles,
        LocalAgentCompactionSettings settings,
        string? oversizedAnchorSynopsis = null,
        bool oversizedAnchorReduced = false)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(readFiles);
        ArgumentNullException.ThrowIfNull(modifiedFiles);
        ArgumentNullException.ThrowIfNull(settings);

        var summarizedUnits = LocalAgentCompactionCanonicalizer.Normalize(preparation.MessagesToSummarize);
        var retainedPrefixUnits = LocalAgentCompactionCanonicalizer.Normalize(preparation.TurnPrefixMessages);
        var retainedSuffixUnits = LocalAgentCompactionCanonicalizer.Normalize(preparation.MessagesToKeep);

        var state = new SerializationState(settings, oversizedAnchorReduced);
        AllocateExpensiveParts(summarizedUnits, retainedPrefixUnits, retainedSuffixUnits, state);

        var builder = new StringBuilder();
        builder.AppendLine("""<codealta-compaction-request version="2">""");
        AppendTag(builder, "mode", preparation.PreviousSummary is null ? "initial" : "update");
        AppendTag(builder, "trigger", preparation.Trigger.ToString().ToLowerInvariant());
        AppendTag(builder, "split-turn", preparation.IsSplitTurn ? "true" : "false");
        AppendTag(
            builder,
            "active-user-request",
            string.IsNullOrWhiteSpace(oversizedAnchorSynopsis) ? latestUserRequest?.Trim() : oversizedAnchorSynopsis.Trim());

        if (!string.IsNullOrWhiteSpace(oversizedAnchorSynopsis))
        {
            AppendTag(builder, "oversized-anchor-synopsis", oversizedAnchorSynopsis);
        }

        if (!string.IsNullOrWhiteSpace(preparation.PreviousSummary))
        {
            AppendTag(builder, "previous-summary", preparation.PreviousSummary);
        }

        AppendTag(builder, "conversation", SerializeUnits(summarizedUnits, state));

        if (retainedPrefixUnits.Count > 0)
        {
            AppendTag(builder, "retained-prefix", SerializeUnits(retainedPrefixUnits, state));
        }

        if (retainedSuffixUnits.Count > 0)
        {
            AppendTag(builder, "retained-suffix", SerializeUnits(retainedSuffixUnits, state));
        }

        AppendTag(builder, "relevant-files", RenderFileActivity(readFiles, modifiedFiles));
        builder.Append("""</codealta-compaction-request>""");

        var body = builder.ToString();
        var totalMessages = preparation.MessagesToSummarize.Count + preparation.TurnPrefixMessages.Count + preparation.MessagesToKeep.Count;
        var statistics = state.BuildStatistics();
        return new LocalAgentCompactionSerializationResult(
            UserMessage: body,
            EstimatedInputTokens: LocalAgentTokenEstimator.EstimateTextTokens(body),
            IncludedMessageCount: totalMessages - statistics.DroppedMessageCount,
            TotalMessageCount: totalMessages,
            Statistics: statistics);
    }

    private static void AllocateExpensiveParts(
        IReadOnlyList<LocalAgentCompactionUnit> summarizedUnits,
        IReadOnlyList<LocalAgentCompactionUnit> retainedPrefixUnits,
        IReadOnlyList<LocalAgentCompactionUnit> retainedSuffixUnits,
        SerializationState state)
    {
        var rankedUnits = new List<RankedUnit>(summarizedUnits.Count + retainedPrefixUnits.Count + retainedSuffixUnits.Count);
        var recency = 0;
        AddRankedUnits(rankedUnits, summarizedUnits, SectionRank.Summarized, ref recency);
        AddRankedUnits(rankedUnits, retainedPrefixUnits, SectionRank.RetainedPrefix, ref recency);
        AddRankedUnits(rankedUnits, retainedSuffixUnits, SectionRank.RetainedSuffix, ref recency);

        foreach (var rankedUnit in OrderRankedUnits(rankedUnits, state.Settings))
        {
            if (rankedUnit.Unit is LocalAgentCompactionToolInteractionUnit { IsCollapsed: true })
            {
                continue;
            }

            foreach (var message in rankedUnit.Unit.SourceMessages)
            {
                for (var partIndex = 0; partIndex < message.Parts.Count; partIndex++)
                {
                    switch (message.Parts[partIndex])
                    {
                        case LocalAgentMessagePart.ToolResult toolResult:
                            AllocateToolResult(message, partIndex, toolResult, state);
                            break;
                        case LocalAgentMessagePart.Reasoning reasoning:
                            AllocateReasoning(message, partIndex, reasoning, state);
                            break;
                        case LocalAgentMessagePart.Data:
                            state.OmittedAttachmentCount++;
                            break;
                    }
                }
            }
        }
    }

    private static void AddRankedUnits(
        ICollection<RankedUnit> target,
        IReadOnlyList<LocalAgentCompactionUnit> units,
        SectionRank sectionRank,
        ref int recency)
    {
        foreach (var unit in units)
        {
            target.Add(new RankedUnit(unit, ComputePriority(unit, sectionRank), recency++));
        }
    }

    private static IOrderedEnumerable<RankedUnit> OrderRankedUnits(
        IEnumerable<RankedUnit> rankedUnits,
        LocalAgentCompactionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rankedUnits);
        ArgumentNullException.ThrowIfNull(settings);

        var ordered = rankedUnits.OrderByDescending(static item => item.Priority);
        if (settings.PreferRecentToolOutputs)
        {
            ordered = ordered.ThenByDescending(static item => item.ContainsToolResult ? item.Recency : int.MinValue);
        }

        if (settings.PreferRecentMessages)
        {
            ordered = ordered.ThenByDescending(static item => item.Recency);
        }

        return ordered;
    }

    private static int ComputePriority(LocalAgentCompactionUnit unit, SectionRank sectionRank)
    {
        var sectionWeight = sectionRank switch
        {
            SectionRank.RetainedSuffix => 300,
            SectionRank.RetainedPrefix => 200,
            _ => 100,
        };

        var roleWeight = unit.Role switch
        {
            LocalAgentConversationRole.User => 40,
            LocalAgentConversationRole.Assistant => 30,
            LocalAgentConversationRole.Tool => 20,
            _ => 10,
        };

        var failureWeight = unit.SourceMessages
            .SelectMany(static message => message.Parts.OfType<LocalAgentMessagePart.ToolResult>())
            .Any(static part => !part.Result.Success || !string.IsNullOrWhiteSpace(part.Result.Error))
            ? 25
            : 0;
        return sectionWeight + roleWeight + failureWeight;
    }

    private static void AllocateToolResult(LocalAgentConversationMessage message, int partIndex, LocalAgentMessagePart.ToolResult toolResult, SerializationState state)
    {
        if (state.Settings.ToolResultCharsPerItem <= 0 || state.RemainingToolCharacters <= 0)
        {
            state.OmittedToolResultCount++;
            return;
        }

        var rendered = RenderToolResult(toolResult.Result);
        var excerpt = CreateToolExcerpt(rendered, state.Settings.ToolResultCharsPerItem);
        if (string.IsNullOrWhiteSpace(excerpt))
        {
            state.OmittedToolResultCount++;
            return;
        }

        var allocated = TrimToLength(excerpt, state.RemainingToolCharacters);
        if (string.IsNullOrWhiteSpace(allocated))
        {
            state.OmittedToolResultCount++;
            return;
        }

        state.SetToolResultExcerpt(message, partIndex, allocated);
        state.SerializedToolResultCharacters += allocated.Length;
        state.RemainingToolCharacters = Math.Max(state.RemainingToolCharacters - allocated.Length, 0);
        if (allocated.Length < excerpt.Length)
        {
            state.OmittedToolResultCount++;
        }
    }

    private static void AllocateReasoning(LocalAgentConversationMessage message, int partIndex, LocalAgentMessagePart.Reasoning reasoning, SerializationState state)
    {
        if (state.Settings.ReasoningMode is LocalAgentCompactionReasoningMode.None ||
            !string.IsNullOrWhiteSpace(reasoning.ProtectedData) ||
            string.IsNullOrWhiteSpace(reasoning.Value))
        {
            if (!string.IsNullOrWhiteSpace(reasoning.Value) || !string.IsNullOrWhiteSpace(reasoning.ProtectedData))
            {
                state.OmittedReasoningCount++;
            }

            return;
        }

        if (state.Settings.ReasoningCharsPerItem <= 0 || state.RemainingReasoningCharacters <= 0)
        {
            state.OmittedReasoningCount++;
            return;
        }

        var excerpt = CreateReasoningExcerpt(reasoning.Value!, state.Settings.ReasoningCharsPerItem, state.Settings.ReasoningMode);
        if (string.IsNullOrWhiteSpace(excerpt))
        {
            state.OmittedReasoningCount++;
            return;
        }

        var allocated = TrimToLength(excerpt, state.RemainingReasoningCharacters);
        if (string.IsNullOrWhiteSpace(allocated))
        {
            state.OmittedReasoningCount++;
            return;
        }

        state.SetReasoningExcerpt(message, partIndex, allocated);
        state.SerializedReasoningCharacters += allocated.Length;
        state.RemainingReasoningCharacters = Math.Max(state.RemainingReasoningCharacters - allocated.Length, 0);
        if (allocated.Length < excerpt.Length)
        {
            state.OmittedReasoningCount++;
        }
    }

    private static string SerializeUnits(IReadOnlyList<LocalAgentCompactionUnit> units, SerializationState state)
    {
        if (units.Count == 0)
        {
            return "(none)";
        }

        var builder = new StringBuilder();
        for (var unitIndex = 0; unitIndex < units.Count; unitIndex++)
        {
            foreach (var line in SerializeUnit(units[unitIndex], state))
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString().Trim();
    }

    private static IEnumerable<string> SerializeUnit(LocalAgentCompactionUnit unit, SerializationState state)
    {
        switch (unit)
        {
            case LocalAgentCompactionMessageUnit messageUnit:
                foreach (var line in SerializeMessage(messageUnit.Message, state))
                {
                    yield return line;
                }

                break;
            case LocalAgentCompactionToolInteractionUnit { IsCollapsed: true } collapsedUnit:
                foreach (var line in SerializeCollapsedToolInteraction(collapsedUnit))
                {
                    yield return line;
                }

                break;
            case LocalAgentCompactionToolInteractionUnit toolInteractionUnit:
                foreach (var line in SerializeMessage(toolInteractionUnit.AssistantMessage, state))
                {
                    yield return line;
                }

                foreach (var toolMessage in toolInteractionUnit.ToolMessages)
                {
                    foreach (var line in SerializeMessage(toolMessage, state))
                    {
                        yield return line;
                    }
                }

                break;
        }
    }

    private static IEnumerable<string> SerializeMessage(LocalAgentConversationMessage message, SerializationState state)
    {
        var emitted = false;
        for (var partIndex = 0; partIndex < message.Parts.Count; partIndex++)
        {
            switch (message.Parts[partIndex])
            {
                case LocalAgentMessagePart.Text text when !string.IsNullOrWhiteSpace(text.Value):
                    emitted = true;
                    yield return $"[{GetRoleLabel(message.Role)}] {text.Value.Trim()}";
                    break;
                case LocalAgentMessagePart.Reasoning:
                    if (state.TryGetReasoningExcerpt(message, partIndex, out var reasoningExcerpt))
                    {
                        emitted = true;
                        yield return $"[Assistant reasoning summary] {reasoningExcerpt}";
                    }

                    break;
                case LocalAgentMessagePart.ToolCall toolCall:
                    emitted = true;
                    yield return $"[Assistant tool calls] {toolCall.Name} {SummarizeArguments(toolCall.Arguments)}";
                    break;
                case LocalAgentMessagePart.ToolResult toolResult:
                    emitted = true;
                    var descriptor = BuildToolDescriptor(toolResult);
                    if (state.TryGetToolResultExcerpt(message, partIndex, out var toolExcerpt))
                    {
                        yield return $"[Tool result summary] {descriptor}; excerpt: {toolExcerpt}";
                    }
                    else
                    {
                        yield return $"[Tool result summary] {descriptor}; bulk output omitted";
                    }

                    break;
                case LocalAgentMessagePart.Uri uri:
                    emitted = true;
                    yield return $"[Attachment] {uri.Name ?? uri.MediaType ?? "uri"}: {uri.Value}";
                    break;
                case LocalAgentMessagePart.Data data:
                    emitted = true;
                    yield return $"[Attachment] inline {data.Name ?? data.MediaType}; base64 omitted ({data.Base64Data.Length} chars)";
                    break;
            }
        }

        if (!emitted)
        {
            state.DroppedMessageCount++;
        }
    }

    private static IEnumerable<string> SerializeCollapsedToolInteraction(LocalAgentCompactionToolInteractionUnit unit)
    {
        var toolCall = unit.ToolCalls.Single();
        var toolResult = unit.ToolResults.Single();
        var descriptor = BuildToolDescriptor(toolResult);
        yield return $"[Assistant tool calls] {toolCall.Name} {SummarizeArguments(toolCall.Arguments)} repeated {unit.RepeatCount} times";
        yield return $"[Tool result summary] repeated successful {toolCall.Name} activity ({unit.RepeatCount}x); latest {descriptor}; bulk output omitted";
    }

    private static string GetRoleLabel(LocalAgentConversationRole role)
        => role switch
        {
            LocalAgentConversationRole.User => "User",
            LocalAgentConversationRole.Assistant => "Assistant",
            LocalAgentConversationRole.Tool => "Tool result",
            LocalAgentConversationRole.System => "System",
            _ => "Message",
        };

    private static string BuildToolDescriptor(LocalAgentMessagePart.ToolResult toolResult)
    {
        var rendered = RenderToolResult(toolResult.Result);
        return $"callId={toolResult.CallId}, status={(toolResult.Result.Success ? "success" : "failed")}, approxChars={rendered.Length}";
    }

    private static string CreateToolExcerpt(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || maxLength <= 0)
        {
            return string.Empty;
        }

        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
        {
            return TrimToLength(text.Trim(), maxLength);
        }

        var highSignalLines = lines
            .Where(line => HighSignalKeywords.Any(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();
        if (highSignalLines.Count == 0)
        {
            highSignalLines.AddRange(lines.Take(3));
            if (lines.Length > 3)
            {
                highSignalLines.Add(lines[^1]);
            }
        }
        else
        {
            foreach (var line in lines)
            {
                if (highSignalLines.Count >= 4)
                {
                    break;
                }

                if (!highSignalLines.Contains(line, StringComparer.Ordinal))
                {
                    highSignalLines.Add(line);
                }
            }
        }

        return TrimToLength(string.Join(" | ", highSignalLines), maxLength);
    }

    private static string CreateReasoningExcerpt(string text, int maxLength, LocalAgentCompactionReasoningMode mode)
    {
        var normalized = string.Join(
            " ",
            text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (mode is LocalAgentCompactionReasoningMode.SummaryOnly)
        {
            var sentence = normalized
                .Split(['.', '!', '?'], 2, StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            normalized = string.IsNullOrWhiteSpace(sentence) ? normalized : sentence;
        }

        return TrimToLength(normalized, maxLength);
    }

    private static string SummarizeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "{}";
        }

        if (arguments.ValueKind is not JsonValueKind.Object)
        {
            return TrimToLength(arguments.GetRawText(), 240);
        }

        var segments = new List<string>();
        var propertyCount = 0;
        foreach (var property in arguments.EnumerateObject())
        {
            propertyCount++;
            if (propertyCount > 8)
            {
                segments.Add("...");
                break;
            }

            segments.Add($"{property.Name}={SummarizeJsonValue(property.Name, property.Value)}");
        }

        return "{ " + string.Join(", ", segments) + " }";
    }

    private static string SummarizeJsonValue(string propertyName, JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => SummarizeJsonString(propertyName, value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => $"[{value.GetArrayLength()} items]",
            JsonValueKind.Object => "{...}",
            _ => TrimToLength(value.GetRawText(), 120),
        };
    }

    private static string SummarizeJsonString(string propertyName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        if (string.Equals(propertyName, "input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "patch", StringComparison.OrdinalIgnoreCase) ||
            value.Length > 200)
        {
            return QuoteJsonString($"(omitted, {value.Length} chars)");
        }

        return QuoteJsonString(value.Length <= 120 ? value : value[..117] + "...");
    }

    private static string RenderToolResult(AgentToolResult result)
    {
        var segments = result.Items.Select(static item => item switch
        {
            AgentToolResultItem.Text text => text.Value,
            AgentToolResultItem.ImageUrl imageUrl => imageUrl.Url,
            _ => string.Empty,
        });
        var rendered = string.Join(Environment.NewLine, segments.Where(static value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(rendered) ? (result.Error ?? "(no output)") : rendered;
    }

    private static string TrimToLength(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || maxLength <= 0)
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..Math.Max(maxLength - 3, 0)] + "...";
    }

    private static string QuoteJsonString(string value)
        => "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            + "\"";

    private static void AppendTag(StringBuilder builder, string tagName, string? value)
    {
        builder.Append('<').Append(tagName).AppendLine(">");
        builder.AppendLine(SecurityElement.Escape(value ?? string.Empty) ?? string.Empty);
        builder.Append("</").Append(tagName).AppendLine(">");
    }

    private static string RenderFileActivity(
        IReadOnlyList<string> readFiles,
        IReadOnlyList<string> modifiedFiles)
    {
        if (readFiles.Count == 0 && modifiedFiles.Count == 0)
        {
            return "- None tracked.";
        }

        var builder = new StringBuilder();
        if (modifiedFiles.Count > 0)
        {
            builder.AppendLine("### Modified");
            foreach (var path in modifiedFiles)
            {
                builder.Append("- ").AppendLine(path);
            }
        }

        if (readFiles.Count > 0)
        {
            builder.AppendLine("### Read");
            foreach (var path in readFiles)
            {
                builder.Append("- ").AppendLine(path);
            }
        }

        return builder.ToString().Trim();
    }

    private sealed class SerializationState(LocalAgentCompactionSettings settings, bool reducedOversizedAnchor)
    {
        public LocalAgentCompactionSettings Settings { get; } = settings;

        public Dictionary<LocalAgentConversationMessage, Dictionary<int, string>> ToolResultExcerpts { get; }
            = new(ReferenceEqualityComparer.Instance);

        public Dictionary<LocalAgentConversationMessage, Dictionary<int, string>> ReasoningExcerpts { get; }
            = new(ReferenceEqualityComparer.Instance);

        public int RemainingToolCharacters { get; set; } = Math.Max(settings.ToolResultCharsTotal, 0);

        public int RemainingReasoningCharacters { get; set; } = Math.Max(settings.ReasoningCharsTotal, 0);

        public int OmittedToolResultCount { get; set; }

        public int OmittedReasoningCount { get; set; }

        public int OmittedAttachmentCount { get; set; }

        public int DroppedMessageCount { get; set; }

        public int SerializedToolResultCharacters { get; set; }

        public int SerializedReasoningCharacters { get; set; }

        public bool ReducedOversizedAnchor { get; } = reducedOversizedAnchor;

        public void SetToolResultExcerpt(LocalAgentConversationMessage message, int partIndex, string value)
        {
            if (!ToolResultExcerpts.TryGetValue(message, out var parts))
            {
                parts = [];
                ToolResultExcerpts[message] = parts;
            }

            parts[partIndex] = value;
        }

        public void SetReasoningExcerpt(LocalAgentConversationMessage message, int partIndex, string value)
        {
            if (!ReasoningExcerpts.TryGetValue(message, out var parts))
            {
                parts = [];
                ReasoningExcerpts[message] = parts;
            }

            parts[partIndex] = value;
        }

        public bool TryGetToolResultExcerpt(LocalAgentConversationMessage message, int partIndex, out string value)
            => TryGetExcerpt(ToolResultExcerpts, message, partIndex, out value);

        public bool TryGetReasoningExcerpt(LocalAgentConversationMessage message, int partIndex, out string value)
            => TryGetExcerpt(ReasoningExcerpts, message, partIndex, out value);

        private static bool TryGetExcerpt(
            IReadOnlyDictionary<LocalAgentConversationMessage, Dictionary<int, string>> source,
            LocalAgentConversationMessage message,
            int partIndex,
            out string value)
        {
            if (source.TryGetValue(message, out var parts) &&
                parts.TryGetValue(partIndex, out value!))
            {
                return true;
            }

            value = string.Empty;
            return false;
        }

        public LocalAgentCompactionSerializerStatistics BuildStatistics()
            => new(
                OmittedToolResultCount,
                OmittedReasoningCount,
                OmittedAttachmentCount,
                DroppedMessageCount,
                SerializedToolResultCharacters,
                SerializedReasoningCharacters,
                ReducedOversizedAnchor);
    }

    private readonly record struct RankedUnit(LocalAgentCompactionUnit Unit, int Priority, int Recency)
    {
        public bool ContainsToolResult => Unit.SourceMessages.Any(static message => message.Parts.Any(static part => part is LocalAgentMessagePart.ToolResult));
    }

    private enum SectionRank
    {
        Summarized,
        RetainedPrefix,
        RetainedSuffix,
    }
}
