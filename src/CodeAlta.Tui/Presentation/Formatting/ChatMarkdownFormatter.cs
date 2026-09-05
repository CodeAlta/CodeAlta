using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Diffing;
using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;
using CodeAlta.Tui.Presentation.Styling;

namespace CodeAlta.Tui.Presentation.Formatting;

internal static class ChatMarkdownFormatter
{
    public static string FormatChatContentMarkdown(AgentContentKind kind, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        content = SanitizeInlineImageContent(content);

        return kind switch
        {
            AgentContentKind.User => content,
            AgentContentKind.Assistant => content,
            AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary => TrimReasoningContent(content),
            AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput => FormatChatOutputMarkdown(content),
            _ => content,
        };
    }

    public static string? GetChatContentHeaderSecondary(AgentContentKind kind, string content)
    {
        return kind switch
        {
            AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary => BuildReasoningSummary(content),
            _ => null,
        };
    }

    public static string FormatCompletedChatContentMarkdown(AgentContentCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        return FormatCompletedChatContentMarkdown(completed, completed.Content);
    }

    public static string FormatCompletedChatContentMarkdown(
        AgentContentCompletedEvent completed,
        string resolvedContent)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(resolvedContent);
        return FormatChatContentMarkdown(
            completed.Kind,
            GetCompletedContentForPresentation(completed, resolvedContent));
    }

    public static string? GetCompletedChatContentHeaderSecondary(AgentContentCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        return GetCompletedChatContentHeaderSecondary(completed, completed.Content);
    }

    public static string? GetCompletedChatContentHeaderSecondary(
        AgentContentCompletedEvent completed,
        string resolvedContent)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(resolvedContent);

        if (completed.Kind is AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary &&
            TryGetReasoningSummaryParts(completed.Details, out var summaryParts))
        {
            return GetReasoningSummaryPartHeader(BuildReasoningSummaryPartPresentation(summaryParts));
        }

        return GetChatContentHeaderSecondary(
            completed.Kind,
            GetCompletedContentForPresentation(completed, resolvedContent));
    }

    public static string FormatStreamingReasoningSummaryMarkdown(IReadOnlyList<string> summaryParts)
    {
        ArgumentNullException.ThrowIfNull(summaryParts);
        var presentation = BuildReasoningSummaryPartPresentation(summaryParts);
        return TrimReasoningContent(presentation.Content);
    }

    public static string? GetStreamingReasoningSummaryHeaderSecondary(IReadOnlyList<string> summaryParts)
    {
        ArgumentNullException.ThrowIfNull(summaryParts);
        return GetReasoningSummaryPartHeader(BuildReasoningSummaryPartPresentation(summaryParts));
    }

    public static string FormatChatPlanMarkdown(AgentPlanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var builder = new StringBuilder();
        if (snapshot.ChangeKind is { } changeKind)
        {
            builder.Append("_").Append(SplitPascalCase(changeKind.ToString())).Append("._");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Explanation))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append(snapshot.Explanation);
        }

        if (snapshot.Steps is { Count: > 0 } steps)
        {
            foreach (var step in steps)
            {
                builder.AppendLine()
                    .Append("- ")
                    .Append(FormatPlanStepStatus(step.Status))
                    .Append(step.Text);
            }
        }

        return builder.ToString();
    }

    public static string FormatChatActivityMarkdown(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var builder = new StringBuilder();
        var displayName = ResolveActivityDisplayName(activity);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            builder.AppendLine()
                .Append("- ")
                .Append(SR.T("Name"))
                .Append(": `")
                .Append(displayName)
                .Append('`');
        }

        if (!string.IsNullOrWhiteSpace(activity.Message))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder
                .Append("- ")
                .Append(SR.T("Detail"))
                .Append(": ")
                .Append(SummarizeActivityMessage(activity));
        }

        return builder.ToString();
    }

    public static string FormatChatSessionUpdateMarkdown(AgentSessionUpdateEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (TryGetModelSelectionMarkdown(update, out var modelSelectionMarkdown))
        {
            return modelSelectionMarkdown;
        }

        if (TryGetLocalCompactionDetails(update, out var details))
        {
            return FormatLocalCompactionMarkdown(update.Message, details);
        }

        return update.Message ?? string.Empty;
    }

    private static bool TryGetModelSelectionMarkdown(AgentSessionUpdateEvent update, out string markdown)
    {
        markdown = string.Empty;
        if (update.Kind != AgentSessionUpdateKind.ModelChanged ||
            update.Details is not { } details ||
            !TryGetStringProperty(details, "providerKey", out var providerKey) ||
            !TryGetModelId(details, out var modelId))
        {
            return false;
        }

        _ = TryGetStringProperty(details, "reasoningEffort", out var reasoningEffort);
        markdown = FormatModelSelectionMarkdown(providerKey!, modelId, NormalizeReasoningEffort(reasoningEffort));
        return true;
    }

    private static bool TryGetModelId(JsonElement details, out string? modelId)
    {
        modelId = null;
        if (details.ValueKind != JsonValueKind.Object ||
            !details.TryGetProperty("modelId", out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            modelId = property.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(modelId))
            {
                modelId = null;
            }

            return true;
        }

        return false;
    }

    private static string? NormalizeReasoningEffort(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        return Enum.TryParse<AgentReasoningEffort>(reasoningEffort, ignoreCase: true, out var parsed)
            ? parsed.ToString()
            : reasoningEffort.Trim();
    }

    private static string FormatModelSelectionMarkdown(string providerKey, string? modelId, string? reasoningEffort)
    {
        var builder = new StringBuilder();
        builder.Append(SR.T("Model used"))
            .Append(": ")
            .Append(SR.T("provider"))
            .Append(" `")
            .Append(providerKey)
            .Append("`, ")
            .Append(SR.T("model"))
            .Append(' ')
            .Append(string.IsNullOrWhiteSpace(modelId) ? SR.T("provider default") : $"`{modelId}`");
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            builder.Append(", ")
                .Append(SR.T("reasoning"))
                .Append(": `")
                .Append(reasoningEffort)
                .Append('`');
        }

        builder.Append('.');
        return builder.ToString();
    }

    public static string FormatSystemPromptSummaryMarkdown(AgentSystemPromptEvent promptEvent)
    {
        ArgumentNullException.ThrowIfNull(promptEvent);
        var builder = new StringBuilder();
        builder.Append(SR.T("System Prompt"))
            .Append(' ')
            .Append(promptEvent.Change.Kind == "initial" ? SR.T("recorded") : SR.T("changed"))
            .Append(": `")
            .Append(promptEvent.EffectivePromptHash)
            .AppendLine("`");
        builder.Append("- ").Append(SR.T("Mapping")).Append(": ").AppendLine(promptEvent.ProviderPayloadSummary.ChannelMapping);
        AppendAgentPromptLine(builder, promptEvent);
        builder.Append("- ").Append(SR.T("Tokens")).Append(": ")
            .Append(promptEvent.Statistics.TotalApproxTokens)
            .Append(' ')
            .Append(SR.T("approx total"))
            .Append(" (`system` ")
            .Append(promptEvent.Statistics.SystemApproxTokens)
            .Append(", `developer` ")
            .Append(promptEvent.Statistics.DeveloperApproxTokens)
            .AppendLine(")");
        if (promptEvent.ProviderPayloadSummary.Lossy)
        {
            builder.AppendLine("- " + SR.T("Warning: provider mapping is lossy."));
        }

        return builder.ToString();
    }

    private static void AppendAgentPromptLine(StringBuilder builder, AgentSystemPromptEvent promptEvent)
    {
        var promptName = NormalizeOptionalText(promptEvent.AgentPromptUsage?.PromptName)
            ?? NormalizeOptionalText(promptEvent.AgentPromptId);
        if (promptName is null)
        {
            return;
        }

        builder.Append("- ").Append(SR.T("Agent Prompt")).Append(": ").Append(EscapeMarkdownInlineText(promptName));
        var displayName = NormalizeOptionalText(promptEvent.AgentPromptUsage?.DisplayName);
        var sourcePath = NormalizeOptionalText(promptEvent.AgentPromptUsage?.SourcePath);
        if (displayName is not null || sourcePath is not null)
        {
            builder.Append(" (");
            if (displayName is not null)
            {
                builder.Append(EscapeMarkdownInlineText(displayName));
                if (sourcePath is not null)
                {
                    builder.Append(" - ");
                }
            }

            if (sourcePath is not null)
            {
                builder.Append('`').Append(EscapeMarkdownInlineText(sourcePath)).Append('`');
            }

            builder.Append(')');
        }

        builder.AppendLine();
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeMarkdownInlineText(string value)
        => value.Replace("`", "'", StringComparison.Ordinal);

    public static string FormatSystemPromptVerbatimMarkdown(AgentSystemPromptEvent promptEvent)
    {
        ArgumentNullException.ThrowIfNull(promptEvent);
        return FormatSystemPromptPartsMarkdown(promptEvent.SystemMessage, promptEvent.DeveloperInstructions);
    }

    public static string FormatSystemPromptDiffMarkdown(AgentSystemPromptEvent previousPromptEvent, AgentSystemPromptEvent promptEvent)
    {
        ArgumentNullException.ThrowIfNull(previousPromptEvent);
        ArgumentNullException.ThrowIfNull(promptEvent);

        var previousPrompt = FormatSystemPromptVerbatimMarkdown(previousPromptEvent);
        var currentPrompt = FormatSystemPromptVerbatimMarkdown(promptEvent);
        var diff = UnifiedDiffBuilder.CreateUnifiedDiff(
            previousPrompt,
            currentPrompt,
            $"system-prompt/{previousPromptEvent.EffectivePromptHash}",
            $"system-prompt/{promptEvent.EffectivePromptHash}");

        return string.IsNullOrWhiteSpace(diff)
            ? string.Empty
            : DiffDisplayFormatter.CreateDiffCodeBlock(diff);
    }

    private static string FormatSystemPromptPartsMarkdown(string? systemMessage, string? developerInstructions)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!-- SystemMessage -->");
        if (!string.IsNullOrWhiteSpace(systemMessage))
        {
            builder.AppendLine(systemMessage.Trim());
        }

        builder.AppendLine();
        builder.AppendLine("<!-- DeveloperInstructions -->");
        if (!string.IsNullOrWhiteSpace(developerInstructions))
        {
            builder.AppendLine(developerInstructions.Trim());
        }

        return builder.ToString();
    }

    public static bool TryGetCompactionSummaryMarkdown(AgentSessionUpdateEvent update, out string summaryMarkdown)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (TryGetLocalCompactionDetails(update, out var details) &&
            TryGetStringProperty(details, "summaryMarkdown", out var summary) &&
            !string.IsNullOrWhiteSpace(summary))
        {
            summaryMarkdown = summary;
            return true;
        }

        summaryMarkdown = string.Empty;
        return false;
    }

    private static string FormatLocalCompactionMarkdown(string? message, JsonElement details)
    {
        var tokensBefore = GetLongProperty(details, "tokensBefore");
        var tokensAfter = GetLongProperty(details, "tokensAfter");
        var tokensRemoved = GetLongProperty(details, "tokensRemoved");
        var compressionRatio = GetDoubleProperty(details, "compressionRatio");
        var targetRatio = GetDoubleProperty(details, "targetRatio");
        var targetTokens = GetLongProperty(details, "targetTokens");
        var targetMet = GetBoolProperty(details, "targetMet");
        _ = TryGetStringProperty(details, "targetMissReason", out var targetMissReason);
        var planningAttemptCount = GetIntProperty(details, "planningAttemptCount");
        var postCompactionInputRatio = GetDoubleProperty(details, "postCompactionInputRatio");
        var summarizedMessages = GetIntProperty(details, "summarizedMessageCount");
        var keptMessages = GetIntProperty(details, "keptMessageCount");
        var messagesAfter = GetIntProperty(details, "messagesAfter");
        var summaryCalls = GetIntProperty(details, "summaryCallCount");
        var chunkCount = GetIntProperty(details, "chunkCount");
        var summaryPromptTokens = GetLongProperty(details, "summaryPromptInputTokens");
        var summaryIncludedMessages = GetIntProperty(details, "summaryPromptIncludedMessageCount");
        var summaryTotalMessages = GetIntProperty(details, "summaryPromptTotalMessageCount");
        var summaryMaxOutputTokens = GetIntProperty(details, "summaryMaxOutputTokens");
        var totalToolCalls = GetIntProperty(details, "totalToolCallCount");
        var serializedToolCalls = GetIntProperty(details, "serializedToolCallCount");
        var collapsedToolCalls = GetIntProperty(details, "collapsedToolCallCount");
        var totalToolResults = GetIntProperty(details, "totalToolResultCount");
        var serializedToolResults = GetIntProperty(details, "serializedToolResultCount");
        var toolResultExcerpts = GetIntProperty(details, "serializedToolResultExcerptCount");
        var omittedToolResults = GetIntProperty(details, "omittedToolResultCount");
        var toolResultCharacters = GetIntProperty(details, "serializedToolResultCharacters");
        var totalReasoning = GetIntProperty(details, "totalReasoningCount");
        var serializedReasoning = GetIntProperty(details, "serializedReasoningCount");
        var omittedReasoning = GetIntProperty(details, "omittedReasoningCount");
        var reasoningCharacters = GetIntProperty(details, "serializedReasoningCharacters");
        var omittedAttachments = GetIntProperty(details, "omittedAttachmentCount");
        var droppedMessages = GetIntProperty(details, "droppedMessageCount");
        var readFiles = CountArrayProperty(details, "readFiles");
        var modifiedFiles = CountArrayProperty(details, "modifiedFiles");

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(message))
        {
            builder.AppendLine(message.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("**" + SR.T("Efficiency") + "**");
        if (tokensBefore is not null && tokensAfter is not null)
        {
            var removedText = tokensRemoved is null
                ? string.Empty
                : SR.T(", removed {0}", FormatCompactNumber(tokensRemoved.Value));
            var ratioText = compressionRatio is null
                ? string.Empty
                : SR.T(", ratio {0}", FormatPercent(compressionRatio.Value));
            builder.Append("- ").Append(SR.T("Context")).Append(": ")
                .Append(FormatCompactNumber(tokensBefore.Value))
                .Append(" → ")
                .Append(FormatCompactNumber(tokensAfter.Value))
                .Append(' ')
                .Append(SR.T("tokens"))
                .Append(removedText)
                .AppendLine(ratioText);
        }
        else if (tokensBefore is not null)
        {
            builder.Append("- ").Append(SR.T("Context before")).Append(": ")
                .Append(FormatCompactNumber(tokensBefore.Value))
                .Append(' ')
                .AppendLine(SR.T("tokens"));
        }

        if (targetTokens is not null || targetRatio is not null || targetMet is not null)
        {
            builder.Append("- ").Append(SR.T("Target")).Append(": ");
            if (targetTokens is not null)
            {
                builder.Append(FormatCompactNumber(targetTokens.Value)).Append(' ').Append(SR.T("tokens"));
            }
            else
            {
                builder.Append(SR.T("unknown tokens"));
            }

            if (targetRatio is not null)
            {
                builder.Append(" (").Append(SR.T("{0} of input limit", FormatPercent(targetRatio.Value))).Append(')');
            }

            if (postCompactionInputRatio is not null)
            {
                builder.Append(", ").Append(SR.T("actual {0} of input limit", FormatPercent(postCompactionInputRatio.Value)));
            }

            if (targetMet is not null)
            {
                builder.Append(targetMet.Value ? SR.T(", met") : SR.T(", missed"));
            }

            if (targetMet is false && !string.IsNullOrWhiteSpace(targetMissReason) && !string.Equals(targetMissReason, "none", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(" (").Append(FormatTargetMissReason(targetMissReason)).Append(')');
            }

            if (planningAttemptCount is > 1)
            {
                builder.Append(", ").Append(SR.T("{0} planning attempts", planningAttemptCount.Value));
            }

            builder.AppendLine();
        }

        builder.Append("- ").Append(SR.T("Messages")).Append(": ").Append(SR.T("summarized"))
            .Append(' ')
            .Append(FormatNullableNumber(summarizedMessages))
            .Append(", ").Append(SR.T("kept")).Append(' ')
            .Append(FormatNullableNumber(keptMessages))
            .Append(", ").Append(SR.T("after"))
            .Append(' ')
            .AppendLine(FormatNullableNumber(messagesAfter));

        builder.Append("- ").Append(SR.T("Summarizer")).Append(": ")
            .Append(FormatNullableNumber(summaryCalls))
            .Append(' ')
            .Append(summaryCalls == 1 ? SR.T("call") : SR.T("calls"))
            .Append(", ")
            .Append(FormatNullableNumber(chunkCount))
            .Append(' ')
            .Append(chunkCount == 1 ? SR.T("chunk") : SR.T("chunks"))
            .Append(", ").Append(SR.T("input ~"))
            .Append(FormatNullableNumber(summaryPromptTokens))
            .Append(' ').Append(SR.T("tokens")).Append(", ").Append(SR.T("output budget"))
            .Append(' ')
            .Append(FormatNullableNumber(summaryMaxOutputTokens))
            .Append(' ')
            .AppendLine(SR.T("tokens"));
        builder.AppendLine();

        builder.AppendLine("**" + SR.T("What fed the summarizer") + "**");
        builder.Append("- ").Append(SR.T("Messages serialized")).Append(": ")
            .Append(FormatNullableNumber(summaryIncludedMessages))
            .Append("/")
            .Append(FormatNullableNumber(summaryTotalMessages))
            .Append(' ')
            .Append(SR.T("considered"));
        if (droppedMessages is > 0)
        {
            builder.Append(", ").Append(SR.T("{0} dropped as empty/unserializable", droppedMessages.Value));
        }

        builder.AppendLine();
        builder.Append("- ").Append(SR.T("Tool calls")).Append(": ")
            .Append(FormatNullableNumber(serializedToolCalls))
            .Append("/")
            .Append(FormatNullableNumber(totalToolCalls))
            .Append(' ')
            .Append(SR.T("serialized"));
        if (collapsedToolCalls is > 0)
        {
            builder.Append(", ").Append(SR.T("{0} repeated calls collapsed", collapsedToolCalls.Value));
        }

        builder.AppendLine();
        builder.Append("- ").Append(SR.T("Tool outputs")).Append(": ")
            .Append(FormatNullableNumber(toolResultExcerpts))
            .Append("/")
            .Append(FormatNullableNumber(totalToolResults))
            .Append(' ')
            .Append(SR.T("with excerpts"))
            .Append(", ")
            .Append(FormatNullableNumber(serializedToolResults))
            .Append(' ')
            .Append(SR.T("result summaries"))
            .Append(", ")
            .Append(FormatNullableNumber(omittedToolResults))
            .Append(' ')
            .Append(SR.T("omitted/truncated bulk outputs"))
            .Append(", ")
            .Append(FormatNullableNumber(toolResultCharacters))
            .Append(' ')
            .AppendLine(SR.T("chars included"));
        builder.Append("- ").Append(SR.T("Reasoning")).Append(": ")
            .Append(FormatNullableNumber(serializedReasoning))
            .Append("/")
            .Append(FormatNullableNumber(totalReasoning))
            .Append(' ')
            .Append(SR.T("excerpts"))
            .Append(", ")
            .Append(FormatNullableNumber(omittedReasoning))
            .Append(' ')
            .Append(SR.T("omitted"))
            .Append(", ")
            .Append(FormatNullableNumber(reasoningCharacters))
            .Append(' ')
            .AppendLine(SR.T("chars included"));
        builder.Append("- ").Append(SR.T("Attachments/files")).Append(": ")
            .Append(FormatNullableNumber(omittedAttachments))
            .Append(' ')
            .Append(SR.T("inline attachments omitted"))
            .Append("; ")
            .Append(modifiedFiles)
            .Append(' ')
            .Append(SR.T("modified files and"))
            .Append(' ')
            .Append(readFiles)
            .Append(' ')
            .AppendLine(SR.T("read files tracked"));

        if (GetBoolProperty(details, "oversizedAnchorReduced") is true || GetBoolProperty(details, "isSplitTurn") is true)
        {
            builder.AppendLine();
            builder.AppendLine("**" + SR.T("Special handling") + "**");
            if (GetBoolProperty(details, "isSplitTurn") is true)
            {
                builder.AppendLine("- " + SR.T("Compaction split an in-progress turn and retained a turn prefix."));
            }

            if (GetBoolProperty(details, "oversizedAnchorReduced") is true)
            {
                builder.AppendLine("- " + SR.T("The oversized latest user message was reduced before summarization."));
            }
        }

        return builder.ToString().Trim();
    }

    private static bool TryGetLocalCompactionDetails(AgentSessionUpdateEvent update, out JsonElement details)
    {
        if (update.Kind == AgentSessionUpdateKind.CompactionCompleted &&
            update.Details is { ValueKind: JsonValueKind.Object } candidate &&
            TryGetStringProperty(candidate, "schema", out var schema) &&
            string.Equals(schema, "codealta.localCompaction.v1", StringComparison.Ordinal))
        {
            details = candidate;
            return true;
        }

        details = default;
        return false;
    }

    private static long? GetLongProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static int? GetIntProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static double? GetDoubleProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out var value)
            ? value
            : null;
    }

    private static bool? GetBoolProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
    }

    private static int CountArrayProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.GetArrayLength()
            : 0;

    private static string FormatNullableNumber(long? value)
        => value is null ? SR.T("unknown") : FormatCompactNumber(value.Value);

    private static string FormatNullableNumber(int? value)
        => value is null ? SR.T("unknown") : value.Value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatCompactNumber(long value)
        => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatPercent(double value)
        => value.ToString("P1", CultureInfo.InvariantCulture);

    private static string FormatTargetMissReason(string reason)
    {
        return reason switch
        {
            "fixed_prompt" => SR.T("fixed prompt exceeded target"),
            "oversized_anchor_reduced" => SR.T("latest user anchor required reduction"),
            "latest_user_anchor" => SR.T("latest user anchor exceeded target"),
            "summary_size" => SR.T("checkpoint summary exceeded target"),
            "retained_suffix" => SR.T("retained suffix exceeded target"),
            "input_fit_only" => SR.T("accepted to fit the input limit"),
            _ => reason.Replace('_', ' '),
        };
    }

    public static string GetSessionUpdateHeader(AgentSessionUpdateKind kind)
    {
        return kind switch
        {
            AgentSessionUpdateKind.Info => $"{TerminalIcons.CodInfo} {SR.T("Info")}",
            AgentSessionUpdateKind.Warning => $"{TerminalIcons.CodWarning} {SR.T("Warning")}",
            AgentSessionUpdateKind.Reconnecting => $"{TerminalIcons.MdServerNetwork} {SR.T("Reconnecting")}",
            AgentSessionUpdateKind.ModelChanged => $"{TerminalIcons.MdChat} {SR.T("Model Used")}",
            AgentSessionUpdateKind.ModeChanged => $"{TerminalIcons.MdCubeOutline} {SR.T("Mode Changed")}",
            AgentSessionUpdateKind.TitleChanged => $"{TerminalIcons.MdRenameBox} {SR.T("Title Changed")}",
            AgentSessionUpdateKind.ContextChanged => $"{TerminalIcons.MdFolder} {SR.T("Context Changed")}",
            AgentSessionUpdateKind.PlanUpdated => $"{TerminalIcons.MdProgressWrench} {SR.T("Plan Updated")}",
            AgentSessionUpdateKind.UsageUpdated => $"{TerminalIcons.MdPacMan} {SR.T("Usage Updated")}",
            AgentSessionUpdateKind.CompactionStarted => $"{TerminalIcons.MdSelectCompare} {SR.T("Compaction Started")}",
            AgentSessionUpdateKind.CompactionCompleted => $"{TerminalIcons.MdShieldPlusOutline} {SR.T("Compaction Completed")}",
            AgentSessionUpdateKind.Handoff => $"{TerminalIcons.MdServerNetwork} {SR.T("Handoff")}",
            AgentSessionUpdateKind.Truncated => $"{TerminalIcons.MdDelete} {SR.T("Session Truncated")}",
            AgentSessionUpdateKind.Shutdown => $"{TerminalIcons.MdClose} {SR.T("Session Shutdown")}",
            AgentSessionUpdateKind.TaskCompleted => $"{TerminalIcons.MdCheck} {SR.T("Task Completed")}",
            AgentSessionUpdateKind.DiffUpdated => $"{TerminalIcons.CodEdit} {SR.T("Diff Updated")}",
            AgentSessionUpdateKind.Started => $"{TerminalIcons.MdTimerOutline} {SR.T("Session Started")}",
            AgentSessionUpdateKind.Resumed => $"{TerminalIcons.MdAccountArrowRight} {SR.T("Session Resumed")}",
            AgentSessionUpdateKind.Idle => $"{TerminalIcons.MdCat} {SR.T("Agent Idle")}",
            _ => SplitPascalCase(kind.ToString()),
        };
    }

    public static string FormatChatPermissionRequestMarkdown(AgentPermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder("_" + SR.T("The agent is blocked until this permission request is resolved.") + "_");

        switch (request)
        {
            case AgentCommandPermissionRequest command:
                builder.AppendLine()
                    .AppendLine()
                    .Append("- ")
                    .Append(SR.T("Kind"))
                    .Append(": ")
                    .Append(SR.T("command execution"));

                if (!string.IsNullOrWhiteSpace(command.Command))
                {
                    builder.AppendLine()
                        .AppendLine()
                        .Append(FormatCodeFence(command.Command, "shell"));
                }

                AppendBullet(builder, SR.T("Working directory"), command.WorkingDirectory, code: true);
                AppendBullet(builder, SR.T("Reason"), command.Reason);

                if (command.Actions is { Count: > 0 } actions)
                {
                    builder.AppendLine().AppendLine().AppendLine("**" + SR.T("Actions") + "**");
                    foreach (var action in actions)
                    {
                        builder.Append("- ")
                            .Append(ToDisplayLabel(action.Kind));

                        if (!string.IsNullOrWhiteSpace(action.Path))
                        {
                            builder.Append(": `").Append(action.Path).Append('`');
                        }
                        else if (!string.IsNullOrWhiteSpace(action.Query))
                        {
                            builder.Append(": `").Append(action.Query).Append('`');
                        }

                        builder.AppendLine();
                    }
                }

                if (command.Network is { } network)
                {
                    AppendBullet(builder, SR.T("Network"), $"{network.Protocol}://{network.Host}");
                }

                break;

            case AgentFileChangePermissionRequest fileChange:
                builder.AppendLine()
                    .AppendLine()
                    .Append("- ")
                    .Append(SR.T("Kind"))
                    .Append(": ")
                    .Append(SR.T("file change"));
                AppendBullet(builder, SR.T("Grant root"), fileChange.GrantRoot, code: true);
                AppendBullet(builder, SR.T("Reason"), fileChange.Reason);
                break;

            case AgentGenericPermissionRequest generic:
                builder.AppendLine().AppendLine().Append("- ").Append(SR.T("Kind")).Append(": ").Append(generic.Kind);
                if (TryGetStringProperty(generic.Raw, "toolName", out var toolName))
                {
                    builder.AppendLine().Append("- ").Append(SR.T("Tool")).Append(": `").Append(toolName).Append('`');
                }

                builder.AppendLine()
                    .AppendLine()
                    .Append(FormatCodeFence(generic.Raw.GetRawText(), "json"));
                break;

            default:
                builder.AppendLine().AppendLine().Append("- ").Append(SR.T("Kind")).Append(": ").Append(request.Kind);
                break;
        }

        return builder.ToString();
    }

    public static string FormatChatRawEventMarkdown(AgentRawEvent raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var builder = new StringBuilder()
            .AppendLine($"- {SR.T("Event")}: `{raw.BackendEventType}`");

        var payload = raw.Raw.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : raw.Raw.GetRawText();

        builder
            .AppendLine()
            .AppendLine("```json")
            .AppendLine(payload)
            .Append("```");

        return builder.ToString();
    }

    public static bool ShouldDisplayActivity(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.Kind == AgentActivityKind.Turn)
        {
            return false;
        }

        if (activity.Kind is AgentActivityKind.ToolCall
            or AgentActivityKind.CommandExecution
            or AgentActivityKind.FileChange
            or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall
            or AgentActivityKind.CollabAgentToolCall
            or AgentActivityKind.Subagent
            or AgentActivityKind.Hook
            or AgentActivityKind.Skill
            or AgentActivityKind.WebSearch
            or AgentActivityKind.ImageGeneration)
        {
            return false;
        }

        return activity.Phase switch
        {
            AgentActivityPhase.Requested => false,
            _ => true,
        };
    }

    public static bool ShouldDisplayRawEvent(AgentRawEvent raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return false;
    }

    public static bool ShouldDisplayCompletedContent(AgentContentCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        return completed.Kind switch
        {
            AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary =>
                ShouldDisplayCompletedReasoning(completed),
            AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput or AgentContentKind.Notice => false,
            _ => true,
        };
    }

    public static bool ShouldDisplayContentDelta(AgentContentDeltaEvent delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        return delta.Kind switch
        {
            AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput or AgentContentKind.Notice => false,
            _ => true,
        };
    }

    public static bool ShouldDisplaySessionUpdate(AgentSessionUpdateEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return update.Kind is AgentSessionUpdateKind.Warning
            or AgentSessionUpdateKind.Reconnecting
            or AgentSessionUpdateKind.ModelChanged
            or AgentSessionUpdateKind.CompactionStarted
            or AgentSessionUpdateKind.CompactionCompleted;
    }

    public static bool ShouldDisplayPermissionRequest(bool autoApproveEnabled)
        => !autoApproveEnabled;

    public static bool ShouldDisplayInteraction(AgentInteractionEvent interaction, bool autoApproveEnabled)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        if (interaction.Kind == AgentInteractionKind.PermissionResolved && autoApproveEnabled)
        {
            return false;
        }

        return true;
    }

    public static string FormatChatUserInputRequestMarkdown(AgentUserInputRequest request, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder(
            autoApprove
                ? "_" + SR.T("The agent asked a question. CodeAlta will prefer continue/inspect-style choices or use a neutral fallback answer so the run can continue.") + "_"
                : "_" + SR.T("The agent asked a question. Terminal question prompts are not implemented yet, so CodeAlta returns empty answers for now.") + "_");

        for (var index = 0; index < request.Form.Prompts.Count; index++)
        {
            var prompt = request.Form.Prompts[index];
            builder.AppendLine()
                .AppendLine()
                .Append("**")
                .Append(SR.T("Question"))
                .Append(' ')
                .Append(index + 1)
                .Append("**");

            AppendBullet(builder, SR.T("Id"), prompt.Id, code: true);
            if (!string.IsNullOrWhiteSpace(prompt.Header))
            {
                builder.AppendLine().Append("- ").Append(SR.T("Header")).Append(": ").Append(prompt.Header);
            }

            builder.AppendLine().Append("- ").Append(SR.T("Question")).Append(": ").Append(prompt.Question);

            if (prompt.Options is { Count: > 0 } options)
            {
                builder.AppendLine().AppendLine().Append("**").Append(SR.T("Choices")).Append("**");
                foreach (var option in options)
                {
                    builder.AppendLine().Append("- ").Append(option.Label);
                    if (!string.IsNullOrWhiteSpace(option.Description))
                    {
                        builder.Append(": ").Append(option.Description);
                    }
                }
            }

            builder.AppendLine()
                .Append("- ").Append(SR.T("Freeform")).Append(": ")
                .Append(prompt.AllowFreeform ? SR.T("allowed") : SR.T("disabled"));

            if (prompt.IsSecret)
            {
                builder.AppendLine().Append("- ").Append(SR.T("Input")).Append(": ").Append(SR.T("secret"));
            }
        }

        return builder.ToString();
    }

    public static string FormatChatInteractionResolutionMarkdown(AgentInteractionEvent interaction, bool includeHeading)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        var label = interaction.Kind switch
        {
            AgentInteractionKind.PermissionResolved => SR.T("Permission Resolved"),
            AgentInteractionKind.UserInputResolved => SR.T("User Input Resolved"),
            _ => interaction.Kind.ToString(),
        };
        var detailsMarkdown = BuildChatInteractionResolutionDetailsMarkdown(interaction);

        if (!includeHeading)
        {
            if (string.IsNullOrWhiteSpace(detailsMarkdown))
            {
                return string.IsNullOrWhiteSpace(interaction.Message)
                    ? $"_{SR.T("Status")}:_ {SR.T("resolved")}"
                    : $"_{SR.T("Status")}:_ {interaction.Message}";
            }

            return string.IsNullOrWhiteSpace(interaction.Message)
                ? $"_{SR.T("Status")}:_ {SR.T("resolved")}\n\n{detailsMarkdown}"
                : $"_{SR.T("Status")}:_ {interaction.Message}\n\n{detailsMarkdown}";
        }

        if (string.IsNullOrWhiteSpace(interaction.Message))
        {
            return string.IsNullOrWhiteSpace(detailsMarkdown)
                ? $"**{TerminalIcons.CodArrowRight} {label}**"
                : $"**{TerminalIcons.CodArrowRight} {label}**\n\n{detailsMarkdown}";
        }

        return string.IsNullOrWhiteSpace(detailsMarkdown)
            ? $"**{TerminalIcons.CodArrowRight} {label}**\n\n{interaction.Message}"
            : $"**{TerminalIcons.CodArrowRight} {label}**\n\n{interaction.Message}\n\n{detailsMarkdown}";
    }

    public static string FormatChatImmediatePermissionDecisionMarkdown(AgentPermissionDecision decision, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var reason = autoApprove
            ? SR.T("CodeAlta response: auto-approved this request.")
            : decision.Kind switch
            {
                AgentPermissionDecisionKind.AllowOnce => SR.T("CodeAlta response: approved this request once."),
                AgentPermissionDecisionKind.AllowForSession => SR.T("CodeAlta response: approved this request for the session."),
                AgentPermissionDecisionKind.Deny => SR.T("CodeAlta response: denied this request."),
                _ => SR.T("CodeAlta response: cancelled this request."),
            };
        return $"_{SR.T("Status")}:_ {reason}\n\n- {SR.T("Decision")}: {SplitPascalCase(decision.Kind.ToString())}";
    }

    public static string FormatChatImmediateUserInputResponseMarkdown(AgentUserInputResponse response, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(response);

        var builder = new StringBuilder();
        builder.Append(
            autoApprove
                ? $"_{SR.T("Status")}:_ {SR.T("CodeAlta auto-answered the question.")}"
                : $"_{SR.T("Status")}:_ {SR.T("CodeAlta returned an empty answer because terminal question prompts are not implemented yet.")}");

        foreach (var answer in response.Answers)
        {
            builder.AppendLine()
                .AppendLine()
                .Append("- `")
                .Append(answer.Key)
                .Append("`: ");
            if (string.IsNullOrWhiteSpace(answer.Value))
            {
                builder.Append('_').Append(SR.T("empty")).Append('_');
            }
            else
            {
                builder.Append('`').Append(answer.Value).Append('`');
            }
        }

        return builder.ToString();
    }

    public static string GetActivityHeadline(AgentActivityKind kind, AgentActivityPhase phase)
    {
        var label = GetActivityKindLabel(kind);
        return phase switch
        {
            AgentActivityPhase.Requested or AgentActivityPhase.Started => SR.T("Calling {0}", label),
            AgentActivityPhase.Completed => SR.T("{0} Result", label),
            AgentActivityPhase.Failed => SR.T("{0} Failed", label),
            AgentActivityPhase.Canceled => SR.T("{0} Canceled", label),
            AgentActivityPhase.Progressed => SR.T("{0} Update", label),
            AgentActivityPhase.Selected => SR.T("{0} Selected", label),
            AgentActivityPhase.Deselected => SR.T("{0} Deselected", label),
            _ => $"{label} · {GetActivityPhaseLabel(phase)}",
        };
    }

    private static string SanitizeInlineImageContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var normalizedLines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lines = new List<string>(normalizedLines.Length);
        var inlineImageText = SR.T("Inline Image");
        var changed = false;

        foreach (var line in normalizedLines)
        {
            var trimmed = line.Trim();
            if (string.Equals(trimmed, "<image>", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                if (lines.Count == 0 || !string.Equals(lines[^1], inlineImageText, StringComparison.Ordinal))
                {
                    lines.Add(inlineImageText);
                }

                changed = true;
                continue;
            }

            lines.Add(line);
        }

        return changed
            ? string.Join(Environment.NewLine, lines)
            : content;
    }

    private static string TrimReasoningContent(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (TryExtractReasoningHeading(normalized, out _, out var remainder) &&
            !string.IsNullOrWhiteSpace(remainder))
        {
            return remainder;
        }

        return normalized;
    }

    private static string? BuildReasoningSummary(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (TryExtractReasoningHeading(normalized, out var heading, out _))
        {
            return TrimSummaryText(heading);
        }

        var firstSentenceEnd = normalized.IndexOf('.');
        var firstLineEnd = normalized.IndexOf('\n');
        var end = firstSentenceEnd >= 0 && firstLineEnd >= 0
            ? Math.Min(firstSentenceEnd, firstLineEnd)
            : Math.Max(firstSentenceEnd, firstLineEnd);
        var summary = end >= 0 ? normalized[..end] : normalized;
        return TrimSummaryText(summary);
    }

    private static string GetCompletedContentForPresentation(
        AgentContentCompletedEvent completed,
        string resolvedContent)
    {
        if (completed.Kind is not (AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary) ||
            !TryGetReasoningSummaryParts(completed.Details, out var summaryParts))
        {
            return resolvedContent;
        }

        return BuildReasoningSummaryPartPresentation(summaryParts).Content;
    }

    private static bool TryGetReasoningSummaryParts(JsonElement? details, out IReadOnlyList<string> summaryParts)
    {
        if (details is not { ValueKind: JsonValueKind.Object } detailsValue ||
            !detailsValue.TryGetProperty("summaryParts", out var summaryPartsElement) ||
            summaryPartsElement.ValueKind is not JsonValueKind.Array)
        {
            summaryParts = [];
            return false;
        }

        var parts = new List<string>(summaryPartsElement.GetArrayLength());
        foreach (var part in summaryPartsElement.EnumerateArray())
        {
            if (part.ValueKind is not JsonValueKind.String)
            {
                summaryParts = [];
                return false;
            }

            parts.Add(part.GetString() ?? string.Empty);
        }

        summaryParts = parts;
        return true;
    }

    private static bool TryGetLeadingBoldHeadingEnd(string content, out int headingEnd)
    {
        headingEnd = 0;
        if (!content.StartsWith("**", StringComparison.Ordinal))
        {
            return false;
        }

        var closingIndex = content.IndexOf("**", 2, StringComparison.Ordinal);
        if (closingIndex <= 2)
        {
            return false;
        }

        headingEnd = closingIndex + 2;
        return true;
    }

    private static bool ShouldDisplayCompletedReasoning(AgentContentCompletedEvent completed)
    {
        if (!TryGetReasoningSummaryParts(completed.Details, out var summaryParts))
        {
            return !string.IsNullOrWhiteSpace(completed.Content);
        }

        var presentation = BuildReasoningSummaryPartPresentation(summaryParts);
        return !string.IsNullOrWhiteSpace(presentation.Content) || presentation.HeadingOnly is not null;
    }

    private static ReasoningSummaryPartPresentation BuildReasoningSummaryPartPresentation(
        IReadOnlyList<string> summaryParts)
    {
        var visibleParts = new List<string>(summaryParts.Count);
        foreach (var summaryPart in summaryParts)
        {
            var part = NormalizeReasoningSummaryPart(summaryPart);
            if (part.Length == 0)
            {
                continue;
            }

            visibleParts.Add(part);
        }

        if (visibleParts.Count == 1 && TryGetHeadingOnly(visibleParts[0], out var headingOnly))
        {
            return new ReasoningSummaryPartPresentation(string.Empty, headingOnly);
        }

        return new ReasoningSummaryPartPresentation(
            string.Join("\n\n", visibleParts),
            null);
    }

    private static string NormalizeReasoningSummaryPart(string summaryPart)
    {
        const string emptyHtmlComment = "<!-- -->";
        var normalizedLines = summaryPart.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var visibleLines = new List<string>(normalizedLines.Length);
        var fencedLines = GetFencedLines(normalizedLines);
        var skipBlankAfterPlaceholder = false;

        for (var lineIndex = 0; lineIndex < normalizedLines.Length; lineIndex++)
        {
            var line = normalizedLines[lineIndex];
            var trimmedLine = line.Trim();
            if (fencedLines[lineIndex])
            {
                visibleLines.Add(line);
                skipBlankAfterPlaceholder = false;
                continue;
            }

            if (string.Equals(trimmedLine, emptyHtmlComment, StringComparison.Ordinal) &&
                ShouldRemoveStandalonePlaceholder(normalizedLines, fencedLines, lineIndex))
            {
                skipBlankAfterPlaceholder = true;
                continue;
            }

            if (skipBlankAfterPlaceholder && trimmedLine.Length == 0)
            {
                continue;
            }

            skipBlankAfterPlaceholder = false;
            line = StripPlaceholderPrefixFromHeading(line);
            visibleLines.Add(line);
        }

        return string.Join("\n", visibleLines).Trim();
    }

    private static bool[] GetFencedLines(IReadOnlyList<string> lines)
    {
        var fencedLines = new bool[lines.Count];
        string? fenceMarker = null;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var trimmedLine = lines[lineIndex].Trim();
            if (fenceMarker is not null)
            {
                fencedLines[lineIndex] = true;
                if (IsClosingFence(trimmedLine, fenceMarker))
                {
                    fenceMarker = null;
                }

                continue;
            }

            if (TryGetFenceMarker(trimmedLine, out fenceMarker))
            {
                fencedLines[lineIndex] = true;
            }
        }

        return fencedLines;
    }

    private static bool ShouldRemoveStandalonePlaceholder(
        IReadOnlyList<string> lines,
        IReadOnlyList<bool> fencedLines,
        int placeholderIndex)
    {
        var previous = FindAdjacentSummaryLine(lines, fencedLines, placeholderIndex, -1);
        var next = FindAdjacentSummaryLine(lines, fencedLines, placeholderIndex, 1);
        return (previous is null && next is null) ||
               (previous is not null && IsBoldHeadingLine(previous)) ||
               (next is not null && IsBoldHeadingLine(next));
    }

    private static string? FindAdjacentSummaryLine(
        IReadOnlyList<string> lines,
        IReadOnlyList<bool> fencedLines,
        int placeholderIndex,
        int direction)
    {
        const string emptyHtmlComment = "<!-- -->";
        for (var lineIndex = placeholderIndex + direction;
             lineIndex >= 0 && lineIndex < lines.Count;
             lineIndex += direction)
        {
            if (fencedLines[lineIndex])
            {
                return lines[lineIndex].Trim();
            }

            var line = lines[lineIndex].Trim();
            if (line.Length == 0 || string.Equals(line, emptyHtmlComment, StringComparison.Ordinal))
            {
                continue;
            }

            return line;
        }

        return null;
    }

    private static bool IsBoldHeadingLine(string line)
    {
        var normalizedLine = StripPlaceholderPrefixFromHeading(line);
        return TryGetLeadingBoldHeadingEnd(normalizedLine, out var headingEnd) &&
               string.IsNullOrWhiteSpace(normalizedLine[headingEnd..]);
    }

    private static string StripPlaceholderPrefixFromHeading(string line)
    {
        const string emptyHtmlComment = "<!-- -->";
        var indentationLength = line.Length - line.TrimStart().Length;
        var remainder = line[indentationLength..];
        var removedPlaceholder = false;
        while (remainder.StartsWith(emptyHtmlComment, StringComparison.Ordinal))
        {
            removedPlaceholder = true;
            remainder = remainder[emptyHtmlComment.Length..].TrimStart();
        }

        return removedPlaceholder && TryGetLeadingBoldHeadingEnd(remainder, out _)
            ? string.Concat(line.AsSpan(0, indentationLength), remainder)
            : line;
    }

    private static bool TryGetFenceMarker(string trimmedLine, out string? marker)
    {
        marker = null;
        if (trimmedLine.Length < 3 || trimmedLine[0] is not ('`' or '~'))
        {
            return false;
        }

        var markerCharacter = trimmedLine[0];
        var markerLength = 1;
        while (markerLength < trimmedLine.Length && trimmedLine[markerLength] == markerCharacter)
        {
            markerLength++;
        }

        if (markerLength < 3)
        {
            return false;
        }

        marker = trimmedLine[..markerLength];
        return true;
    }

    private static bool IsClosingFence(string trimmedLine, string fenceMarker)
    {
        if (!trimmedLine.StartsWith(fenceMarker, StringComparison.Ordinal))
        {
            return false;
        }

        return trimmedLine.AsSpan(fenceMarker.Length).Trim().IsEmpty;
    }

    private static bool TryGetHeadingOnly(string content, out string heading)
    {
        if (TryGetLeadingBoldHeadingEnd(content, out var headingEnd) &&
            string.IsNullOrWhiteSpace(content[headingEnd..]))
        {
            heading = content[2..(headingEnd - 2)].Trim();
            return true;
        }

        heading = string.Empty;
        return false;
    }

    private static string? GetReasoningSummaryPartHeader(ReasoningSummaryPartPresentation presentation)
        => presentation.HeadingOnly ?? BuildReasoningSummary(presentation.Content);

    private readonly record struct ReasoningSummaryPartPresentation(string Content, string? HeadingOnly);

    private static bool TryExtractReasoningHeading(string content, out string? heading, out string? remainder)
    {
        heading = null;
        remainder = null;
        var normalized = content.TrimStart();
        if (normalized.StartsWith("**", StringComparison.Ordinal))
        {
            var closingIndex = normalized.IndexOf("**", 2, StringComparison.Ordinal);
            if (closingIndex > 2)
            {
                heading = normalized[2..closingIndex].Trim();
                remainder = normalized[(closingIndex + 2)..].TrimStart('\n', '\r', ' ');
                return !string.IsNullOrWhiteSpace(heading);
            }
        }

        if (normalized.StartsWith("#", StringComparison.Ordinal))
        {
            var lineEnd = normalized.IndexOf('\n');
            var line = (lineEnd >= 0 ? normalized[..lineEnd] : normalized).Trim();
            heading = line.TrimStart('#', ' ').Trim();
            remainder = lineEnd >= 0 ? normalized[(lineEnd + 1)..].TrimStart() : string.Empty;
            return !string.IsNullOrWhiteSpace(heading);
        }

        return false;
    }

    private static string? TrimSummaryText(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var normalized = summary.Trim();
        const int maxLength = 44;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd() + "...";
    }

    private static string FormatPlanStepStatus(AgentPlanStepStatus? status)
    {
        return status switch
        {
            AgentPlanStepStatus.Pending => "[ ] ",
            AgentPlanStepStatus.InProgress => "[~] ",
            AgentPlanStepStatus.Completed => "[x] ",
            _ => string.Empty,
        };
    }

    private static string? ResolveActivityDisplayName(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.Details is { } details &&
            TryGetStringProperty(details, "command", out var command) &&
            !string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        return activity.Name;
    }

    private static string GetActivityPhaseLabel(AgentActivityPhase phase)
    {
        return phase switch
        {
            AgentActivityPhase.Requested => SR.T("Requested"),
            AgentActivityPhase.Started => SR.T("Started"),
            AgentActivityPhase.Progressed => SR.T("In Progress"),
            AgentActivityPhase.Completed => SR.T("Completed"),
            AgentActivityPhase.Failed => SR.T("Failed"),
            AgentActivityPhase.Canceled => SR.T("Canceled"),
            AgentActivityPhase.Selected => SR.T("Selected"),
            AgentActivityPhase.Deselected => SR.T("Deselected"),
            _ => phase.ToString(),
        };
    }

    private static string GetActivityKindLabel(AgentActivityKind kind)
    {
        return kind switch
        {
            AgentActivityKind.Turn => SR.T("Turn"),
            AgentActivityKind.ToolCall => SR.T("Tool Call"),
            AgentActivityKind.CommandExecution => SR.T("Command Execution"),
            AgentActivityKind.FileChange => SR.T("File Change"),
            AgentActivityKind.McpToolCall => SR.T("MCP Tool Call"),
            AgentActivityKind.DynamicToolCall => SR.T("Dynamic Tool Call"),
            AgentActivityKind.CollabAgentToolCall => SR.T("Collab Agent Tool Call"),
            AgentActivityKind.Subagent => SR.T("Subagent"),
            AgentActivityKind.Hook => SR.T("Hook"),
            AgentActivityKind.Skill => SR.T("Skill"),
            AgentActivityKind.Compaction => SR.T("Compaction"),
            AgentActivityKind.WebSearch => SR.T("Web Search"),
            AgentActivityKind.ImageGeneration => SR.T("Image Generation"),
            _ => SplitPascalCase(kind.ToString()),
        };
    }

    private static string SummarizeActivityMessage(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (string.IsNullOrWhiteSpace(activity.Message))
        {
            return string.Empty;
        }

        var normalized = activity.Message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        var lineCount = normalized.Count(static ch => ch == '\n') + 1;
        var shouldCompact = activity.Kind is AgentActivityKind.ToolCall
            or AgentActivityKind.CommandExecution
            or AgentActivityKind.FileChange
            or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall
            or AgentActivityKind.CollabAgentToolCall
            or AgentActivityKind.Hook
            or AgentActivityKind.Skill
            or AgentActivityKind.Subagent;

        if (shouldCompact && (normalized.Length > 220 || lineCount > 6))
        {
            var firstLine = normalized.Split('\n')[0].Trim();
            if (firstLine.Length > 120)
            {
                firstLine = firstLine[..117].TrimEnd() + "...";
            }

            return string.IsNullOrWhiteSpace(firstLine)
                ? SR.T("Output omitted ({0} lines, {1} chars).", lineCount, normalized.Length)
                : $"{firstLine} _({SR.T("output omitted: {0} lines, {1} chars", lineCount, normalized.Length)})_";
        }

        return normalized;
    }

    private static string ToDisplayLabel(AgentCommandPreviewKind kind)
    {
        return kind switch
        {
            AgentCommandPreviewKind.ListFiles => SR.T("List Files"),
            _ => SplitPascalCase(kind.ToString()),
        };
    }

    private static string FormatChatOutputMarkdown(string content)
        => string.IsNullOrWhiteSpace(content) ? string.Empty : FormatCodeFence(content, "text");

    internal static string FormatCodeFence(string content, string language)
    {
        var fence = content.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        return $"{fence}{language}\n{content}\n{fence}";
    }

    private static void AppendBullet(StringBuilder builder, string label, string? value, bool code = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine().Append("- ").Append(label).Append(": ");
        if (code)
        {
            builder.Append('`').Append(value).Append('`');
        }
        else
        {
            builder.Append(value);
        }
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

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string? value)
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

    private static string BuildChatInteractionResolutionDetailsMarkdown(AgentInteractionEvent interaction)
    {
        if (interaction.Details is not { } details)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        switch (interaction.Kind)
        {
            case AgentInteractionKind.PermissionResolved:
                if (TryGetStringProperty(details, "decisionKind", out var decisionKind))
                {
                    builder.Append("- ").Append(SR.T("Decision")).Append(": ").Append(SplitPascalCase(decisionKind!));
                }
                break;

            case AgentInteractionKind.UserInputResolved:
                if (details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("answers", out var answers) &&
                    answers.ValueKind == JsonValueKind.Object)
                {
                    var answerLines = new List<string>();
                    foreach (var answer in answers.EnumerateObject())
                    {
                        answerLines.Add(
                            string.IsNullOrWhiteSpace(answer.Value.GetString())
                                ? $"- `{answer.Name}`: _{SR.T("empty")}_"
                                : $"- `{answer.Name}`: `{answer.Value.GetString()}`");
                    }

                    if (answerLines.Count == 0)
                    {
                        builder.Append("- ").Append(SR.T("Answers")).Append(": _").Append(SR.T("empty")).Append('_');
                    }
                    else
                    {
                        builder.Append(string.Join(Environment.NewLine, answerLines));
                    }

                    if (answerLines.Count > 0 && answerLines.All(static line => line.EndsWith("_empty_", StringComparison.Ordinal)))
                    {
                        if (builder.Length > 0)
                        {
                            builder.AppendLine();
                        }

                        builder.Append("- ").Append(SR.T("Note: Terminal question prompts are not implemented yet."));
                    }
                }
                break;
        }

        return builder.ToString();
    }

}
