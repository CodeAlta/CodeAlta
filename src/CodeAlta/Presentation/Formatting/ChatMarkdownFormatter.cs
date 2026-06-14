using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Diffing;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;

namespace CodeAlta.Presentation.Formatting;

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
                .Append("- Name: `")
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
                .Append("- Detail: ")
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
        builder.Append("Model used: provider `")
            .Append(providerKey)
            .Append("`, model ")
            .Append(string.IsNullOrWhiteSpace(modelId) ? "provider default" : $"`{modelId}`");
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            builder.Append(", reasoning: `")
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
        builder.Append("System Prompt ")
            .Append(promptEvent.Change.Kind == "initial" ? "recorded" : "changed")
            .Append(": `")
            .Append(promptEvent.EffectivePromptHash)
            .AppendLine("`");
        builder.Append("- Mapping: ").AppendLine(promptEvent.ProviderPayloadSummary.ChannelMapping);
        AppendAgentPromptLine(builder, promptEvent);
        builder.Append("- Tokens: ")
            .Append(promptEvent.Statistics.TotalApproxTokens)
            .Append(" approx total (`system` ")
            .Append(promptEvent.Statistics.SystemApproxTokens)
            .Append(", `developer` ")
            .Append(promptEvent.Statistics.DeveloperApproxTokens)
            .AppendLine(")");
        if (promptEvent.ProviderPayloadSummary.Lossy)
        {
            builder.AppendLine("- Warning: provider mapping is lossy.");
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

        builder.Append("- Agent Prompt: ").Append(EscapeMarkdownInlineText(promptName));
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

        builder.AppendLine("**Efficiency**");
        if (tokensBefore is not null && tokensAfter is not null)
        {
            var removedText = tokensRemoved is null
                ? string.Empty
                : $", removed {FormatCompactNumber(tokensRemoved.Value)}";
            var ratioText = compressionRatio is null
                ? string.Empty
                : $", ratio {FormatPercent(compressionRatio.Value)}";
            builder.Append("- Context: ")
                .Append(FormatCompactNumber(tokensBefore.Value))
                .Append(" → ")
                .Append(FormatCompactNumber(tokensAfter.Value))
                .Append(" tokens")
                .Append(removedText)
                .AppendLine(ratioText);
        }
        else if (tokensBefore is not null)
        {
            builder.Append("- Context before: ")
                .Append(FormatCompactNumber(tokensBefore.Value))
                .AppendLine(" tokens");
        }

        if (targetTokens is not null || targetRatio is not null || targetMet is not null)
        {
            builder.Append("- Target: ");
            if (targetTokens is not null)
            {
                builder.Append(FormatCompactNumber(targetTokens.Value)).Append(" tokens");
            }
            else
            {
                builder.Append("unknown tokens");
            }

            if (targetRatio is not null)
            {
                builder.Append(" (").Append(FormatPercent(targetRatio.Value)).Append(" of input limit)");
            }

            if (postCompactionInputRatio is not null)
            {
                builder.Append(", actual ").Append(FormatPercent(postCompactionInputRatio.Value)).Append(" of input limit");
            }

            if (targetMet is not null)
            {
                builder.Append(targetMet.Value ? ", met" : ", missed");
            }

            if (targetMet is false && !string.IsNullOrWhiteSpace(targetMissReason) && !string.Equals(targetMissReason, "none", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(" (").Append(FormatTargetMissReason(targetMissReason)).Append(')');
            }

            if (planningAttemptCount is > 1)
            {
                builder.Append(", ").Append(planningAttemptCount.Value).Append(" planning attempts");
            }

            builder.AppendLine();
        }

        builder.Append("- Messages: summarized ")
            .Append(FormatNullableNumber(summarizedMessages))
            .Append(", kept ")
            .Append(FormatNullableNumber(keptMessages))
            .Append(", after ")
            .AppendLine(FormatNullableNumber(messagesAfter));

        builder.Append("- Summarizer: ")
            .Append(FormatNullableNumber(summaryCalls))
            .Append(summaryCalls == 1 ? " call" : " calls")
            .Append(", ")
            .Append(FormatNullableNumber(chunkCount))
            .Append(chunkCount == 1 ? " chunk" : " chunks")
            .Append(", input ~")
            .Append(FormatNullableNumber(summaryPromptTokens))
            .Append(" tokens, output budget ")
            .Append(FormatNullableNumber(summaryMaxOutputTokens))
            .AppendLine(" tokens");
        builder.AppendLine();

        builder.AppendLine("**What fed the summarizer**");
        builder.Append("- Messages serialized: ")
            .Append(FormatNullableNumber(summaryIncludedMessages))
            .Append("/")
            .Append(FormatNullableNumber(summaryTotalMessages))
            .Append(" considered");
        if (droppedMessages is > 0)
        {
            builder.Append(", ").Append(droppedMessages.Value).Append(" dropped as empty/unserializable");
        }

        builder.AppendLine();
        builder.Append("- Tool calls: ")
            .Append(FormatNullableNumber(serializedToolCalls))
            .Append("/")
            .Append(FormatNullableNumber(totalToolCalls))
            .Append(" serialized");
        if (collapsedToolCalls is > 0)
        {
            builder.Append(", ").Append(collapsedToolCalls.Value).Append(" repeated calls collapsed");
        }

        builder.AppendLine();
        builder.Append("- Tool outputs: ")
            .Append(FormatNullableNumber(toolResultExcerpts))
            .Append("/")
            .Append(FormatNullableNumber(totalToolResults))
            .Append(" with excerpts, ")
            .Append(FormatNullableNumber(serializedToolResults))
            .Append(" result summaries, ")
            .Append(FormatNullableNumber(omittedToolResults))
            .Append(" omitted/truncated bulk outputs, ")
            .Append(FormatNullableNumber(toolResultCharacters))
            .AppendLine(" chars included");
        builder.Append("- Reasoning: ")
            .Append(FormatNullableNumber(serializedReasoning))
            .Append("/")
            .Append(FormatNullableNumber(totalReasoning))
            .Append(" excerpts, ")
            .Append(FormatNullableNumber(omittedReasoning))
            .Append(" omitted, ")
            .Append(FormatNullableNumber(reasoningCharacters))
            .AppendLine(" chars included");
        builder.Append("- Attachments/files: ")
            .Append(FormatNullableNumber(omittedAttachments))
            .Append(" inline attachments omitted; ")
            .Append(modifiedFiles)
            .Append(" modified files and ")
            .Append(readFiles)
            .AppendLine(" read files tracked");

        if (GetBoolProperty(details, "oversizedAnchorReduced") is true || GetBoolProperty(details, "isSplitTurn") is true)
        {
            builder.AppendLine();
            builder.AppendLine("**Special handling**");
            if (GetBoolProperty(details, "isSplitTurn") is true)
            {
                builder.AppendLine("- Compaction split an in-progress turn and retained a turn prefix.");
            }

            if (GetBoolProperty(details, "oversizedAnchorReduced") is true)
            {
                builder.AppendLine("- The oversized latest user message was reduced before summarization.");
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
        => value is null ? "unknown" : FormatCompactNumber(value.Value);

    private static string FormatNullableNumber(int? value)
        => value is null ? "unknown" : value.Value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatCompactNumber(long value)
        => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatPercent(double value)
        => value.ToString("P1", CultureInfo.InvariantCulture);

    private static string FormatTargetMissReason(string reason)
    {
        return reason switch
        {
            "fixed_prompt" => "fixed prompt exceeded target",
            "oversized_anchor_reduced" => "latest user anchor required reduction",
            "latest_user_anchor" => "latest user anchor exceeded target",
            "summary_size" => "checkpoint summary exceeded target",
            "retained_suffix" => "retained suffix exceeded target",
            "input_fit_only" => "accepted to fit the input limit",
            _ => reason.Replace('_', ' '),
        };
    }

    public static string GetSessionUpdateHeader(AgentSessionUpdateKind kind)
    {
        return kind switch
        {
            AgentSessionUpdateKind.Info => $"{TerminalIcons.CodInfo} Info",
            AgentSessionUpdateKind.Warning => $"{TerminalIcons.CodWarning} Warning",
            AgentSessionUpdateKind.Reconnecting => $"{TerminalIcons.MdServerNetwork} Reconnecting",
            AgentSessionUpdateKind.ModelChanged => $"{TerminalIcons.MdChat} Model Used",
            AgentSessionUpdateKind.ModeChanged => $"{TerminalIcons.MdCubeOutline} Mode Changed",
            AgentSessionUpdateKind.TitleChanged => $"{TerminalIcons.MdRenameBox} Title Changed",
            AgentSessionUpdateKind.ContextChanged => $"{TerminalIcons.MdFolder} Context Changed",
            AgentSessionUpdateKind.PlanUpdated => $"{TerminalIcons.MdProgressWrench} Plan Updated",
            AgentSessionUpdateKind.UsageUpdated => $"{TerminalIcons.MdPacMan} Usage Updated",
            AgentSessionUpdateKind.CompactionStarted => $"{TerminalIcons.MdSelectCompare} Compaction Started",
            AgentSessionUpdateKind.CompactionCompleted => $"{TerminalIcons.MdShieldPlusOutline} Compaction Completed",
            AgentSessionUpdateKind.Handoff => $"{TerminalIcons.MdServerNetwork} Handoff",
            AgentSessionUpdateKind.Truncated => $"{TerminalIcons.MdDelete} Session Truncated",
            AgentSessionUpdateKind.Shutdown => $"{TerminalIcons.MdClose} Session Shutdown",
            AgentSessionUpdateKind.TaskCompleted => $"{TerminalIcons.MdCheck} Task Completed",
            AgentSessionUpdateKind.DiffUpdated => $"{TerminalIcons.CodEdit} Diff Updated",
            AgentSessionUpdateKind.Started => $"{TerminalIcons.MdTimerOutline} Session Started",
            AgentSessionUpdateKind.Resumed => $"{TerminalIcons.MdAccountArrowRight} Session Resumed",
            AgentSessionUpdateKind.Idle => $"{TerminalIcons.MdCat} Agent Idle",
            _ => SplitPascalCase(kind.ToString()),
        };
    }

    public static string FormatChatPermissionRequestMarkdown(AgentPermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder("_The agent is blocked until this permission request is resolved._");

        switch (request)
        {
            case AgentCommandPermissionRequest command:
                builder.AppendLine()
                    .AppendLine()
                    .Append("- Kind: command execution");

                if (!string.IsNullOrWhiteSpace(command.Command))
                {
                    builder.AppendLine()
                        .AppendLine()
                        .Append(FormatCodeFence(command.Command, "shell"));
                }

                AppendBullet(builder, "Working directory", command.WorkingDirectory, code: true);
                AppendBullet(builder, "Reason", command.Reason);

                if (command.Actions is { Count: > 0 } actions)
                {
                    builder.AppendLine().AppendLine().AppendLine("**Actions**");
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
                    AppendBullet(builder, "Network", $"{network.Protocol}://{network.Host}");
                }

                break;

            case AgentFileChangePermissionRequest fileChange:
                builder.AppendLine()
                    .AppendLine()
                    .Append("- Kind: file change");
                AppendBullet(builder, "Grant root", fileChange.GrantRoot, code: true);
                AppendBullet(builder, "Reason", fileChange.Reason);
                break;

            case AgentGenericPermissionRequest generic:
                builder.AppendLine().AppendLine().Append("- Kind: ").Append(generic.Kind);
                if (TryGetStringProperty(generic.Raw, "toolName", out var toolName))
                {
                    builder.AppendLine().Append("- Tool: `").Append(toolName).Append('`');
                }

                builder.AppendLine()
                    .AppendLine()
                    .Append(FormatCodeFence(generic.Raw.GetRawText(), "json"));
                break;

            default:
                builder.AppendLine().AppendLine().Append("- Kind: ").Append(request.Kind);
                break;
        }

        return builder.ToString();
    }

    public static string FormatChatRawEventMarkdown(AgentRawEvent raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var builder = new StringBuilder()
            .AppendLine($"- Event: `{raw.BackendEventType}`");

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
            AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary => !string.IsNullOrWhiteSpace(completed.Content),
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
                ? "_The agent asked a question. CodeAlta will prefer continue/inspect-style choices or use a neutral fallback answer so the run can continue._"
                : "_The agent asked a question. Terminal question prompts are not implemented yet, so CodeAlta returns empty answers for now._");

        for (var index = 0; index < request.Form.Prompts.Count; index++)
        {
            var prompt = request.Form.Prompts[index];
            builder.AppendLine()
                .AppendLine()
                .Append("**Question ")
                .Append(index + 1)
                .Append("**");

            AppendBullet(builder, "Id", prompt.Id, code: true);
            if (!string.IsNullOrWhiteSpace(prompt.Header))
            {
                builder.AppendLine().Append("- Header: ").Append(prompt.Header);
            }

            builder.AppendLine().Append("- Question: ").Append(prompt.Question);

            if (prompt.Options is { Count: > 0 } options)
            {
                builder.AppendLine().AppendLine().Append("**Choices**");
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
                .Append("- Freeform: ")
                .Append(prompt.AllowFreeform ? "allowed" : "disabled");

            if (prompt.IsSecret)
            {
                builder.AppendLine().Append("- Input: secret");
            }
        }

        return builder.ToString();
    }

    public static string FormatChatInteractionResolutionMarkdown(AgentInteractionEvent interaction, bool includeHeading)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        var label = interaction.Kind switch
        {
            AgentInteractionKind.PermissionResolved => "Permission Resolved",
            AgentInteractionKind.UserInputResolved => "User Input Resolved",
            _ => interaction.Kind.ToString(),
        };
        var detailsMarkdown = BuildChatInteractionResolutionDetailsMarkdown(interaction);

        if (!includeHeading)
        {
            if (string.IsNullOrWhiteSpace(detailsMarkdown))
            {
                return string.IsNullOrWhiteSpace(interaction.Message)
                    ? "_Status:_ resolved"
                    : $"_Status:_ {interaction.Message}";
            }

            return string.IsNullOrWhiteSpace(interaction.Message)
                ? $"_Status:_ resolved\n\n{detailsMarkdown}"
                : $"_Status:_ {interaction.Message}\n\n{detailsMarkdown}";
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
            ? "CodeAlta response: auto-approved this request."
            : "CodeAlta response: denied this request because interactive approval UI is not implemented yet.";
        return $"_Status:_ {reason}\n\n- Decision: {SplitPascalCase(decision.Kind.ToString())}";
    }

    public static string FormatChatImmediateUserInputResponseMarkdown(AgentUserInputResponse response, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(response);

        var builder = new StringBuilder();
        builder.Append(
            autoApprove
                ? "_Status:_ CodeAlta auto-answered the question."
                : "_Status:_ CodeAlta returned an empty answer because terminal question prompts are not implemented yet.");

        foreach (var answer in response.Answers)
        {
            builder.AppendLine()
                .AppendLine()
                .Append("- `")
                .Append(answer.Key)
                .Append("`: ");
            if (string.IsNullOrWhiteSpace(answer.Value))
            {
                builder.Append("_empty_");
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
            AgentActivityPhase.Requested or AgentActivityPhase.Started => $"Calling {label}",
            AgentActivityPhase.Completed => $"{label} Result",
            AgentActivityPhase.Failed => $"{label} Failed",
            AgentActivityPhase.Canceled => $"{label} Canceled",
            AgentActivityPhase.Progressed => $"{label} Update",
            AgentActivityPhase.Selected => $"{label} Selected",
            AgentActivityPhase.Deselected => $"{label} Deselected",
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
        var changed = false;

        foreach (var line in normalizedLines)
        {
            var trimmed = line.Trim();
            if (string.Equals(trimmed, "<image>", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                if (lines.Count == 0 || !string.Equals(lines[^1], "Inline Image", StringComparison.Ordinal))
                {
                    lines.Add("Inline Image");
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
            AgentActivityPhase.Requested => "Requested",
            AgentActivityPhase.Started => "Started",
            AgentActivityPhase.Progressed => "In Progress",
            AgentActivityPhase.Completed => "Completed",
            AgentActivityPhase.Failed => "Failed",
            AgentActivityPhase.Canceled => "Canceled",
            AgentActivityPhase.Selected => "Selected",
            AgentActivityPhase.Deselected => "Deselected",
            _ => phase.ToString(),
        };
    }

    private static string GetActivityKindLabel(AgentActivityKind kind)
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
                ? $"Output omitted ({lineCount} lines, {normalized.Length} chars)."
                : $"{firstLine} _(output omitted: {lineCount} lines, {normalized.Length} chars)_";
        }

        return normalized;
    }

    private static string ToDisplayLabel(AgentCommandPreviewKind kind)
    {
        return kind switch
        {
            AgentCommandPreviewKind.ListFiles => "List Files",
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
                    builder.Append("- Decision: ").Append(SplitPascalCase(decisionKind!));
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
                                ? $"- `{answer.Name}`: _empty_"
                                : $"- `{answer.Name}`: `{answer.Value.GetString()}`");
                    }

                    if (answerLines.Count == 0)
                    {
                        builder.Append("- Answers: _empty_");
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

                        builder.Append("- Note: Terminal question prompts are not implemented yet.");
                    }
                }
                break;
        }

        return builder.ToString();
    }

}
