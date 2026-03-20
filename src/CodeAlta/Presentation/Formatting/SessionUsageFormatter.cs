using System.Globalization;
using System.Text;
using CodeAlta.Agent;

namespace CodeAlta.Presentation.Formatting;

internal static class SessionUsageFormatter
{
    public static string BuildIndicatorMarkup(AgentSessionUsage? usage)
    {
        if (usage?.WindowUsagePercentage is not { } percentage)
        {
            return "[dim]Context --[/]";
        }

        var clampedPercentage = Math.Clamp(percentage, 0d, 999d);
        return FormattableString.Invariant($"[dim]Context[/] [{GetUsageTone(clampedPercentage)}]{clampedPercentage:0}%[/]");
    }

    public static string FormatSummary(AgentSessionUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (usage.Window is not { } window)
        {
            return "Window unavailable";
        }

        var current = FormatNumber(window.CurrentTokens);
        var limit = FormatNumber(window.TokenLimit);
        return usage.WindowUsagePercentage is { } percentage
            ? FormattableString.Invariant($"{current} / {limit} tokens ({percentage:0.#}%)")
            : $"{current} / {limit} tokens";
    }

    public static string BuildMarkdown(AgentSessionUsage? usage, string backendName, string? modelName)
    {
        var builder = new StringBuilder();
        builder.Append("# ")
            .Append(backendName)
            .AppendLine(" context usage");
        builder.AppendLine();
        builder.Append("- Model: ")
            .AppendLine(modelName ?? "(default model)");

        if (usage is null)
        {
            builder.AppendLine("- Status: Waiting for usage data from the active session.");
            return builder.ToString().TrimEnd();
        }

        AppendUsageBreakdownMarkdown(builder, usage);
        AppendLimitsAndQuotasMarkdown(builder, usage);
        AppendBackendSpecificMarkdown(builder, usage);

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
            parts.Add($"{usedPercent}% used");
        }

        if (window.WindowDurationMinutes is { } durationMinutes)
        {
            parts.Add(durationMinutes.ToString(CultureInfo.InvariantCulture) + "m window");
        }

        if (window.ResetsAt is { } resetsAt)
        {
            parts.Add($"resets {resetsAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}");
        }

        return string.Join(" · ", parts);
    }

    public static string FormatCopilotQuotaUsageCell(CopilotRequestQuotaDetails quota)
    {
        if (quota.IsUnlimitedEntitlement == true && quota.UsedRequests is { } unlimitedUsed)
        {
            var unlimitedUsage = $"{FormatNumber(unlimitedUsed)} / unlimited";
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

        return quota.IsUnlimitedEntitlement == true ? "unlimited" : "quota snapshot";
    }

    public static string FormatCopilotQuotaStatusCell(CopilotRequestQuotaDetails quota)
    {
        var parts = new List<string>();
        if (quota.IsUnlimitedEntitlement == true)
        {
            parts.Add("unlimited");
        }

        if (quota.UsageAllowedWithExhaustion is { } usageAllowedWithExhaustion)
        {
            parts.Add(usageAllowedWithExhaustion ? "allowed" : "blocked");
        }

        if (quota.Overage is { } overage && overage > 0)
        {
            parts.Add("overage " + FormatNumber(overage));
        }

        if (quota.ResetDate is { } resetDate)
        {
            parts.Add("reset " + resetDate.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture));
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "-";
    }

    public static string FormatCopilotCompaction(CopilotCompactionUsage usage)
    {
        var parts = new List<string>
        {
            usage.Success ? "successful" : "failed"
        };

        if (usage.PreCompactionTokens is { } preTokens && usage.PostCompactionTokens is { } postTokens)
        {
            parts.Add($"{FormatNumber(preTokens)} -> {FormatNumber(postTokens)} tokens");
        }

        if (usage.TokensRemoved is { } tokensRemoved)
        {
            parts.Add($"{FormatNumber(tokensRemoved)} removed");
        }

        if (usage.MessagesRemoved is { } messagesRemoved)
        {
            parts.Add($"{messagesRemoved} messages removed");
        }

        return string.Join(" · ", parts);
    }

    public static string FormatNumber(long? value)
        => value?.ToString("#,0", CultureInfo.InvariantCulture) ?? "?";

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
        return $"total {FormatNumber(usage.TotalTokens)} · input {FormatNumber(usage.InputTokens)} · output {FormatNumber(usage.OutputTokens)} · cache {FormatNumber(usage.CachedInputTokens)} · reasoning {FormatNumber(usage.ReasoningOutputTokens)}";
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
            parts.Add($"effort {reasoningEffort}");
        }

        if (usage.Initiator is { Length: > 0 } initiator)
        {
            parts.Add($"initiator {initiator}");
        }

        if (usage.InputTokens is not null)
        {
            parts.Add($"input {FormatNumber(usage.InputTokens)}");
        }

        if (usage.OutputTokens is not null)
        {
            parts.Add($"output {FormatNumber(usage.OutputTokens)}");
        }

        if (usage.CacheReadTokens is { } cacheRead)
        {
            parts.Add($"cache read {FormatNumber(cacheRead)}");
        }

        if (usage.CacheWriteTokens is { } cacheWrite)
        {
            parts.Add($"cache write {FormatNumber(cacheWrite)}");
        }

        if (usage.CachedInputTokens is { } cachedInput)
        {
            parts.Add($"cache {FormatNumber(cachedInput)}");
        }

        if (usage.ReasoningTokens is { } reasoningTokens)
        {
            parts.Add($"reasoning {FormatNumber(reasoningTokens)}");
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
            parts.Add($"effort {reasoningEffort}");
        }

        if (usage.Initiator is { Length: > 0 } initiator)
        {
            parts.Add($"initiator {initiator}");
        }

        if (usage.DurationMs is { } durationMs)
        {
            parts.Add(FormattableString.Invariant($"duration {durationMs:0} ms"));
        }

        if (usage.Cost is { } cost)
        {
            parts.Add(FormattableString.Invariant($"cost {cost:0.###}"));
        }

        if (usage.ParentToolCallId is { Length: > 0 } parentToolCallId)
        {
            parts.Add($"parent tool {parentToolCallId}");
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

        parts.Add("updated " + usage.UpdatedAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

        metadataLine = string.Join(" · ", parts);
        return metadataLine.Length > 0;
    }

    private static string FormatUsageScope(AgentUsageScope scope)
    {
        return scope switch
        {
            AgentUsageScope.CurrentWindow => "Current window",
            AgentUsageScope.LastOperation => "Last operation",
            AgentUsageScope.ThreadTotal => "Thread total",
            AgentUsageScope.Compaction => "Compaction",
            AgentUsageScope.Truncation => "Truncation",
            AgentUsageScope.RateLimitOnly => "Rate-limit only",
            _ => "Unknown",
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
                ? FormattableString.Invariant($"## Usage breakdown: {messageCount} messages")
                : "## Usage breakdown")
            .AppendLine();
        if (usage.Window is not null)
        {
            builder.Append("- Window: ").AppendLine(FormatSummary(usage));
        }

        if (usage.LastOperation is { } operation)
        {
            builder.Append("- ")
                .Append(operation.Label ?? "Last operation")
                .Append(": ")
                .AppendLine(FormatOperationUsage(operation));
            if (TryFormatOperationPopupMetadata(operation, out var extras))
            {
                builder.Append("- Operation details: ").AppendLine(extras);
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
                ? "## Limits and quotas"
                : usage.RateLimits is not null
                    ? "## Limits"
                    : "## Quotas");
        if (usage.RateLimits is { } rateLimits)
        {
            builder.Append("- Limits: ")
                .AppendLine($"{rateLimits.Name ?? "Rate limits"} · {rateLimits.PlanType ?? "plan unknown"}");
            if (rateLimits.Primary is not null)
            {
                builder.Append("- Primary: ")
                    .AppendLine(FormatAgentRateLimitWindow(rateLimits.Primary));
            }

            if (rateLimits.Secondary is not null)
            {
                builder.Append("- Secondary: ")
                    .AppendLine(FormatAgentRateLimitWindow(rateLimits.Secondary));
            }
        }

        if (requestQuotas is { Length: > 0 })
        {
            builder.AppendLine()
                .AppendLine("### Copilot quota snapshots")
                .AppendLine()
                .AppendLine("| Quota | Usage | Status |")
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
                .AppendLine("### Raw quota snapshots");
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

    private static void AppendBackendSpecificMarkdown(StringBuilder builder, AgentSessionUsage usage)
    {
        var appended = false;
        if (usage.Details is CodexSessionUsageDetails codex &&
            codex.TotalUsage is not null)
        {
            builder.AppendLine()
                .AppendLine("## Backend-specific details");
            appended = true;
            builder.Append("- Thread total: ")
                .AppendLine(FormatCodexTokenUsage(codex.TotalUsage));
        }

        if (usage.Details is CopilotSessionUsageDetails copilot &&
            (copilot.LastCompaction is not null || copilot.LastAssistantUsage?.TotalNanoAiu is not null || copilot.LastAssistantUsage?.TokenDetails is { Length: > 0 }))
        {
            if (!appended)
            {
                builder.AppendLine()
                    .AppendLine("## Backend-specific details");
            }

            if (copilot.LastCompaction is { } compaction)
            {
                builder.Append("- Last compaction: ")
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
}