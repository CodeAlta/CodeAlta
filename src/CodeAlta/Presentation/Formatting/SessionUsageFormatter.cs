using System.Globalization;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Presentation.Formatting;

internal static class SessionUsageFormatter
{
    public static string BuildIndicatorMarkup(AgentSessionUsage? usage)
    {
        if (usage?.WindowUsagePercentage is not { } percentage)
        {
            return usage?.CurrentTokens is { } currentTokens
                ? $"[dim]{SR.T("Context")}[/] [dim]{FormatCompactNumber(currentTokens)} {SR.T("tok")}[/]"
                : $"[dim]{SR.T("Context")} --[/]";
        }

        var clampedPercentage = Math.Clamp(percentage, 0d, 100d);
        return FormattableString.Invariant($"[dim]{SR.T("Context")}[/] [{GetUsageTone(clampedPercentage)}]{clampedPercentage:0}%[/]");
    }

    public static string FormatSummary(AgentSessionUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (usage.Window is not { } window)
        {
            return SR.T("Window unavailable");
        }

        if (window.TokenLimit is not { } tokenLimit || tokenLimit <= 0)
        {
            var currentWithoutLimit = FormatNumber(window.CurrentTokens);
            return window.MessageCount is { } messageCount
                ? SR.T("{0} tokens · {1} messages", currentWithoutLimit, messageCount)
                : SR.T("{0} tokens", currentWithoutLimit);
        }

        var current = window.CurrentTokens is { } currentTokens && currentTokens > tokenLimit
            ? "≥" + FormatNumber(tokenLimit)
            : FormatNumber(window.CurrentTokens);
        var limit = FormatNumber(window.TokenLimit);
        return usage.WindowUsagePercentage is { } percentage
            ? SR.T("{0} / {1} input tokens ({2}%)", current, limit, FormattableString.Invariant($"{Math.Clamp(percentage, 0d, 100d):0.#}"))
            : SR.T("{0} / {1} input tokens", current, limit);
    }

    public static string BuildMarkdown(AgentSessionUsage? usage, string providerName, string? modelName)
    {
        var builder = new StringBuilder();
        builder.Append("# ")
            .Append(providerName)
            .Append(' ')
            .AppendLine(SR.T("context usage"));
        builder.AppendLine();
        builder.Append("- ").Append(SR.T("Model")).Append(": ")
            .AppendLine(modelName ?? SR.T("(default model)"));

        if (usage is null)
        {
            builder.Append("- ").Append(SR.T("Status")).Append(": ").AppendLine(SR.T("Waiting for usage data from the active session."));
            return builder.ToString().TrimEnd();
        }

        AppendUsageBreakdownMarkdown(builder, usage);
        AppendLimitsAndQuotasMarkdown(builder, usage);
        AppendProviderSpecificMarkdown(builder, usage);

        return builder.ToString().TrimEnd();
    }

    public static string? FormatOperationPopupText(AgentOperationUsageSnapshot usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (!HasOperationUsageChartData(usage))
        {
            var summary = FormatOperationUsage(usage);
            return summary.Length > 0 ? summary : null;
        }

        return TryFormatOperationPopupMetadata(usage, out var metadata)
            ? metadata
            : null;
    }

    public static string FormatAgentRateLimitWindow(AgentRateLimitWindow window)
    {
        var parts = new List<string>();
        if (window.UsedPercent is { } usedPercent)
        {
            parts.Add(SR.T("{0}% used", usedPercent));
        }

        if (window.WindowDurationMinutes is { } durationMinutes)
        {
            parts.Add(SR.T("{0}m window", durationMinutes.ToString(CultureInfo.InvariantCulture)));
        }

        if (window.ResetsAt is { } resetsAt)
        {
            parts.Add(SR.T("resets {0}", resetsAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        return string.Join(" · ", parts);
    }

    public static string FormatCopilotQuotaUsageCell(CopilotRequestQuotaDetails quota)
    {
        if (quota.IsUnlimitedEntitlement == true && quota.UsedRequests is { } unlimitedUsed)
        {
            var unlimitedUsage = SR.T("{0} / unlimited", FormatNumber(unlimitedUsed));
            if (TryGetQuotaRemainingPercentage(quota, out var unlimitedRemaining))
            {
                unlimitedUsage += FormattableString.Invariant($" ({unlimitedRemaining:0.#}%)");
            }

            return unlimitedUsage;
        }

        if (quota.UsedRequests is { } usedRequests && quota.EntitlementRequests is { } entitlementRequests)
        {
            var usage = $"{FormatNumber(usedRequests)} / {FormatNumber(entitlementRequests)}";
            if (TryGetQuotaRemainingPercentage(quota, out var remainingPercentage))
            {
                usage += FormattableString.Invariant($" ({remainingPercentage:0.#}%)");
            }

            return usage;
        }

        if (quota.UsedRequests is { } usedOnly)
        {
            return FormatNumber(usedOnly);
        }

        return quota.IsUnlimitedEntitlement == true ? SR.T("unlimited") : SR.T("quota snapshot");
    }

    public static string FormatCopilotQuotaStatusCell(CopilotRequestQuotaDetails quota)
    {
        var parts = new List<string>();
        if (quota.IsUnlimitedEntitlement == true)
        {
            parts.Add(SR.T("unlimited"));
        }

        if (quota.UsageAllowedWithExhaustion is { } usageAllowedWithExhaustion)
        {
            parts.Add(usageAllowedWithExhaustion ? SR.T("allowed") : SR.T("blocked"));
        }

        if (quota.Overage is { } overage && overage > 0)
        {
            parts.Add(SR.T("overage {0}", FormatNumber(overage)));
        }

        if (quota.ResetDate is { } resetDate)
        {
            parts.Add(SR.T("reset {0}", resetDate.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "-";
    }

    public static string FormatCopilotCompaction(CopilotCompactionUsage usage)
    {
        var parts = new List<string>
        {
            usage.Success ? SR.T("successful") : SR.T("failed")
        };

        if (usage.PreCompactionTokens is { } preTokens && usage.PostCompactionTokens is { } postTokens)
        {
            parts.Add(SR.T("{0} -> {1} tokens", FormatNumber(preTokens), FormatNumber(postTokens)));
        }

        if (usage.TokensRemoved is { } tokensRemoved)
        {
            parts.Add(SR.T("{0} removed", FormatNumber(tokensRemoved)));
        }

        if (usage.MessagesRemoved is { } messagesRemoved)
        {
            parts.Add(SR.T("{0} messages removed", messagesRemoved));
        }

        return string.Join(" · ", parts);
    }

    public static string FormatNumber(long? value)
        => value?.ToString("#,0", CultureInfo.InvariantCulture) ?? "?";

    private static string FormatCompactNumber(long value)
    {
        if (value >= 1_000_000)
        {
            return FormattableString.Invariant($"{value / 1_000_000d:0.#}M");
        }

        if (value >= 1_000)
        {
            return FormattableString.Invariant($"{value / 1_000d:0.#}k");
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryFormatUsageMetadataLine(AgentSessionUsage usage, out string metadataLine)
        => TryFormatUsageMetadataLineCore(usage, out metadataLine);

    private static bool HasOperationUsageChartData(AgentOperationUsageSnapshot usage)
    {
        return usage.InputTokens is > 0 ||
               usage.OutputTokens is > 0 ||
               usage.CacheReadTokens is > 0 ||
               usage.CacheWriteTokens is > 0 ||
               usage.CachedInputTokens is > 0 ||
               usage.ReasoningTokens is > 0;
    }

    private static string FormatCodexTokenUsage(CodexTokenUsage usage)
    {
        return string.Join(" · ",
            SR.T("total {0}", FormatNumber(usage.TotalTokens)),
            SR.T("input {0}", FormatNumber(usage.InputTokens)),
            SR.T("output {0}", FormatNumber(usage.OutputTokens)),
            SR.T("cache {0}", FormatNumber(usage.CachedInputTokens)),
            SR.T("reasoning {0}", FormatNumber(usage.ReasoningOutputTokens)));
    }

    private static string FormatOperationUsage(AgentOperationUsageSnapshot usage)
    {
        var parts = new List<string>();
        if (usage.Model is { Length: > 0 } model)
        {
            parts.Add(model);
        }

        if (usage.ReasoningEffort is { Length: > 0 } reasoningEffort)
        {
            parts.Add(SR.T("effort {0}", reasoningEffort));
        }

        if (usage.Initiator is { Length: > 0 } initiator)
        {
            parts.Add(SR.T("initiator {0}", initiator));
        }

        if (usage.InputTokens is not null)
        {
            parts.Add(SR.T("input {0}", FormatNumber(usage.InputTokens)));
        }

        if (usage.OutputTokens is not null)
        {
            parts.Add(SR.T("output {0}", FormatNumber(usage.OutputTokens)));
        }

        if (usage.CacheReadTokens is { } cacheRead)
        {
            parts.Add(SR.T("cache read {0}", FormatNumber(cacheRead)));
        }

        if (usage.CacheWriteTokens is { } cacheWrite)
        {
            parts.Add(SR.T("cache write {0}", FormatNumber(cacheWrite)));
        }

        if (usage.CachedInputTokens is { } cachedInput)
        {
            parts.Add(SR.T("cache {0}", FormatNumber(cachedInput)));
        }

        if (usage.ReasoningTokens is { } reasoningTokens)
        {
            parts.Add(SR.T("reasoning {0}", FormatNumber(reasoningTokens)));
        }

        return string.Join(" · ", parts);
    }

    private static bool TryFormatOperationPopupMetadata(AgentOperationUsageSnapshot usage, out string metadata)
    {
        var parts = new List<string>();
        if (usage.Model is { Length: > 0 } model)
        {
            parts.Add(model);
        }

        if (usage.ReasoningEffort is { Length: > 0 } reasoningEffort)
        {
            parts.Add(SR.T("effort {0}", reasoningEffort));
        }

        if (usage.Initiator is { Length: > 0 } initiator)
        {
            parts.Add(SR.T("initiator {0}", initiator));
        }

        if (usage.DurationMs is { } durationMs)
        {
            parts.Add(SR.T("duration {0} ms", FormattableString.Invariant($"{durationMs:0}")));
        }

        if (usage.Cost is { } cost)
        {
            parts.Add(SR.T("cost {0}", FormattableString.Invariant($"{cost:0.###}")));
        }

        if (usage.ParentToolCallId is { Length: > 0 } parentToolCallId)
        {
            parts.Add(SR.T("parent tool {0}", parentToolCallId));
        }

        metadata = string.Join(" · ", parts);
        return parts.Count > 0;
    }

    private static bool TryGetQuotaRemainingPercentage(CopilotRequestQuotaDetails quota, out double remainingPercentage)
    {
        if (quota.RemainingPercentage is { } concreteRemainingPercentage)
        {
            remainingPercentage = concreteRemainingPercentage;
            return true;
        }

        if (quota.EntitlementRequests is > 0 && quota.UsedRequests is { } usedRequests)
        {
            remainingPercentage = Math.Max(0d, 100d - ((usedRequests * 100d) / quota.EntitlementRequests.Value));
            return true;
        }

        remainingPercentage = default;
        return false;
    }

    private static bool TryFormatUsageMetadataLineCore(AgentSessionUsage usage, out string metadataLine)
    {
        var parts = new List<string>();
        if (usage.Window?.Label is { Length: > 0 } windowLabel &&
            !string.Equals(windowLabel, "Active context window", StringComparison.Ordinal))
        {
            parts.Add(windowLabel);
        }

        if (usage.Scope is AgentUsageScope.Compaction or AgentUsageScope.Truncation or AgentUsageScope.RateLimitOnly)
        {
            parts.Add(FormatUsageScope(usage.Scope));
        }

        parts.Add(SR.T("updated {0}", usage.UpdatedAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));

        metadataLine = string.Join(" · ", parts);
        return metadataLine.Length > 0;
    }

    private static string FormatUsageScope(AgentUsageScope scope)
    {
        return scope switch
        {
            AgentUsageScope.CurrentWindow => SR.T("Current window"),
            AgentUsageScope.LastOperation => SR.T("Last operation"),
            AgentUsageScope.SessionTotal => SR.T("Session total"),
            AgentUsageScope.Compaction => SR.T("Compaction"),
            AgentUsageScope.Truncation => SR.T("Truncation"),
            AgentUsageScope.RateLimitOnly => SR.T("Rate-limit only"),
            _ => SR.T("Unknown"),
        };
    }

    private static string GetUsageTone(double percentage)
    {
        return percentage switch
        {
            < 75 => "success",
            < 90 => "warning",
            _ => "error",
        };
    }

    private static void AppendUsageBreakdownMarkdown(StringBuilder builder, AgentSessionUsage usage)
    {
        if (usage.LastOperation is null && usage.Window is null)
        {
            return;
        }

        builder.AppendLine()
            .Append(usage.MessageCount is { } messageCount
                ? "## " + SR.T("Context usage: {0} messages", messageCount)
                : "## " + SR.T("Context usage"))
            .AppendLine();
        if (usage.Window is not null)
        {
            builder.Append("- ").Append(SR.T("Compaction pressure")).Append(": ").AppendLine(FormatSummary(usage));
            if (TryFormatModelEnvelope(usage.Window, out var modelEnvelope))
            {
                builder.Append("- ").Append(SR.T("Indicative model limits")).Append(": ").AppendLine(modelEnvelope);
            }
        }

        if (usage.LastOperation is { } operation)
        {
            builder.Append("- ")
                .Append(operation.Label ?? SR.T("Last operation"))
                .Append(": ")
                .AppendLine(FormatOperationUsage(operation));
            if (TryFormatOperationPopupMetadata(operation, out var extras))
            {
                builder.Append("- ").Append(SR.T("Operation details")).Append(": ").AppendLine(extras);
            }
        }

        if (TryFormatUsageMetadataLine(usage, out var metadataLine))
        {
            builder.Append("- ").AppendLine(metadataLine);
        }
    }

    private static void AppendLimitsAndQuotasMarkdown(StringBuilder builder, AgentSessionUsage usage)
    {
        var quotaSnapshots = (usage.Details as CopilotSessionUsageDetails)?.QuotaSnapshots;
        var requestQuotas = quotaSnapshots?
            .Where(static quota => quota.Details is CopilotRequestQuotaDetails)
            .ToArray();
        var opaqueQuotas = quotaSnapshots?
            .Where(static quota => quota.Details is CopilotOpaqueQuotaDetails)
            .ToArray();

        if (usage.RateLimits is null &&
            requestQuotas is not { Length: > 0 } &&
            opaqueQuotas is not { Length: > 0 })
        {
            return;
        }

        builder.AppendLine()
            .AppendLine(usage.RateLimits is not null && (requestQuotas is { Length: > 0 } || opaqueQuotas is { Length: > 0 })
                ? "## " + SR.T("Limits and quotas")
                : usage.RateLimits is not null
                    ? "## " + SR.T("Limits")
                    : "## " + SR.T("Quotas"));
        if (usage.RateLimits is { } rateLimits)
        {
            builder.Append("- ").Append(SR.T("Limits")).Append(": ")
                .AppendLine($"{rateLimits.Name ?? SR.T("Rate limits")} · {rateLimits.PlanType ?? SR.T("plan unknown")}");
            if (rateLimits.Primary is not null)
            {
                builder.Append("- ").Append(SR.T("Primary")).Append(": ")
                    .AppendLine(FormatAgentRateLimitWindow(rateLimits.Primary));
            }

            if (rateLimits.Secondary is not null)
            {
                builder.Append("- ").Append(SR.T("Secondary")).Append(": ")
                    .AppendLine(FormatAgentRateLimitWindow(rateLimits.Secondary));
            }
        }

        if (requestQuotas is { Length: > 0 })
        {
            builder.AppendLine()
                .Append("### ").AppendLine(SR.T("Copilot quota snapshots"))
                .AppendLine()
                .AppendLine(SR.T("| Quota | Usage | Status |"))
                .AppendLine("| --- | --- | --- |");

            foreach (var quota in requestQuotas)
            {
                var requestQuota = (CopilotRequestQuotaDetails)quota.Details;
                builder.Append("| ")
                    .Append(quota.Name)
                    .Append(" | ")
                    .Append(FormatCopilotQuotaUsageCell(requestQuota))
                    .Append(" | ")
                    .AppendLine(FormatCopilotQuotaStatusCell(requestQuota) + " |");
            }
        }

        if (opaqueQuotas is { Length: > 0 })
        {
            builder.AppendLine()
                .Append("### ").AppendLine(SR.T("Raw quota snapshots"));
            foreach (var quota in opaqueQuotas)
            {
                builder.Append("- ")
                    .Append(quota.Name)
                    .Append(": ");
                var opaqueQuota = (CopilotOpaqueQuotaDetails)quota.Details;
                builder.AppendLine(opaqueQuota.Summary);
            }
        }
    }

    private static void AppendProviderSpecificMarkdown(StringBuilder builder, AgentSessionUsage usage)
    {
        var appended = false;
        if (usage.Details is CodexSessionUsageDetails codex &&
            codex.TotalUsage is not null)
        {
            builder.AppendLine()
                .Append("## ").AppendLine(SR.T("Provider-specific details"));
            appended = true;
            builder.Append("- ").Append(SR.T("Session total")).Append(": ")
                .AppendLine(FormatCodexTokenUsage(codex.TotalUsage));
        }

        if (usage.Details is CopilotSessionUsageDetails copilot &&
            (copilot.LastCompaction is not null || copilot.LastAssistantUsage?.TotalNanoAiu is not null || copilot.LastAssistantUsage?.TokenDetails is { Length: > 0 }))
        {
            if (!appended)
            {
                builder.AppendLine()
                    .Append("## ").AppendLine(SR.T("Provider-specific details"));
            }

            if (copilot.LastCompaction is { } compaction)
            {
                builder.Append("- ").Append(SR.T("Last compaction")).Append(": ")
                    .AppendLine(FormatCopilotCompaction(compaction));
            }

            if (copilot.LastAssistantUsage?.TotalNanoAiu is { } totalNanoAiu)
            {
                builder.Append("- AIU: ")
                    .AppendLine(FormattableString.Invariant($"{totalNanoAiu:0}"));
            }

            if (copilot.LastAssistantUsage?.TokenDetails is { Length: > 0 } tokenDetails)
            {
                foreach (var tokenDetail in tokenDetails)
                {
                    builder.Append("- ")
                        .Append(tokenDetail.TokenType)
                        .Append(": ")
                        .AppendLine(FormatNumber(tokenDetail.TokenCount));
                }
            }
        }
    }

    public static bool TryFormatModelEnvelope(AgentWindowUsageSnapshot window, out string modelEnvelope)
    {
        var parts = new List<string>();
        if (window.TotalContextEnvelope is { } totalContextEnvelope)
        {
            parts.Add(SR.T("context window {0} tokens", FormatNumber(totalContextEnvelope)));
        }

        if (window.MaxOutputTokens is { } maxOutputTokens)
        {
            parts.Add(SR.T("max output {0} tokens", FormatNumber(maxOutputTokens)));
        }

        modelEnvelope = string.Join("; ", parts);
        return parts.Count > 0;
    }
}
