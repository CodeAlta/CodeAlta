using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

internal sealed partial class CodeAltaTerminalUi
{
    private const int UsageTooltipMinWidth = 52;
    private const int UsageTooltipMaxWidth = 76;

    private Visual BuildSessionUsageIndicatorVisual()
    {
        var indicator = new Markup(() => BuildSessionUsageIndicatorMarkup(GetSelectedSessionUsage()))
        {
            Wrap = false,
        };

        return indicator
            .Tooltip(CreateComputedVisual(BuildSessionUsageTooltipVisual))
            .Placement(PopupPlacement.Above)
            .OffsetY(0)
            .ShowDelayMilliseconds(150);
    }

    private Visual BuildSessionUsageTooltipVisual()
    {
        var (backendName, modelName) = GetUsageSelectionContext();
        return BuildSessionUsageTooltipContent(GetSelectedSessionUsage(), backendName, modelName);
    }

    private AgentSessionUsage? GetSelectedSessionUsage()
    {
        var selectedThread = GetSelectedThread();
        return selectedThread is null
            ? null
            : EnsureThreadTab(selectedThread).Usage;
    }

    private (string BackendName, string? ModelName) GetUsageSelectionContext()
    {
        var selectedThread = GetSelectedThread();
        if (selectedThread is not null)
        {
            var tab = EnsureThreadTab(selectedThread);
            var backendState = _chatBackendStates[tab.BackendId.Value];
            return (backendState.DisplayName, tab.ModelId ?? backendState.SelectedModelId);
        }

        var backendId = GetPreferredBackendId();
        var draftBackendState = _chatBackendStates[backendId.Value];
        return (draftBackendState.DisplayName, draftBackendState.SelectedModelId);
    }

    internal static AgentSessionUsage MergeSessionUsage(AgentSessionUsage? current, AgentSessionUsage incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (current is null)
        {
            return incoming;
        }

        return new AgentSessionUsage(
            CurrentTokens: incoming.CurrentTokens ?? current.CurrentTokens,
            TokenLimit: incoming.TokenLimit ?? current.TokenLimit,
            MessageCount: incoming.MessageCount ?? current.MessageCount,
            UpdatedAt: incoming.UpdatedAt,
            Details: MergeSessionUsageDetails(current.Details, incoming.Details));
    }

    internal static string BuildSessionUsageIndicatorMarkup(AgentSessionUsage? usage)
    {
        if (usage?.WindowUsagePercentage is not { } percentage)
        {
            return "[dim]ctx --[/]";
        }

        var tone = GetUsageTone(percentage);
        return FormattableString.Invariant($"[{tone}]ctx {Math.Clamp(percentage, 0d, 999d):0}%[/]");
    }

    internal static string FormatSessionUsageSummary(AgentSessionUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var current = FormatNumber(usage.CurrentTokens);
        var limit = FormatNumber(usage.TokenLimit);
        return usage.WindowUsagePercentage is { } percentage
            ? FormattableString.Invariant($"{current} / {limit} tokens ({percentage:0.#}%)")
            : $"{current} / {limit} tokens";
    }

    private static AgentSessionUsageDetails? MergeSessionUsageDetails(AgentSessionUsageDetails? current, AgentSessionUsageDetails? incoming)
    {
        if (incoming is null)
        {
            return current;
        }

        if (current is null)
        {
            return incoming;
        }

        return (current, incoming) switch
        {
            (CodexSessionUsageDetails currentCodex, CodexSessionUsageDetails incomingCodex) => currentCodex with
            {
                LastTurnUsage = incomingCodex.LastTurnUsage ?? currentCodex.LastTurnUsage,
                TotalUsage = incomingCodex.TotalUsage ?? currentCodex.TotalUsage,
                ModelContextWindow = incomingCodex.ModelContextWindow ?? currentCodex.ModelContextWindow,
                RateLimits = incomingCodex.RateLimits ?? currentCodex.RateLimits,
            },
            (CopilotSessionUsageDetails currentCopilot, CopilotSessionUsageDetails incomingCopilot) => currentCopilot with
            {
                LastAssistantUsage = incomingCopilot.LastAssistantUsage ?? currentCopilot.LastAssistantUsage,
                LastCompaction = incomingCopilot.LastCompaction ?? currentCopilot.LastCompaction,
                QuotaSnapshots = incomingCopilot.QuotaSnapshots ?? currentCopilot.QuotaSnapshots,
            },
            _ => incoming
        };
    }

    private static Visual BuildSessionUsageTooltipContent(AgentSessionUsage? usage, string backendName, string? modelName)
    {
        var stack = new VStack
        {
            Spacing = 1,
        };

        stack.Add(new Markup($"[bold]{AnsiMarkup.Escape(backendName)} context usage[/]"));
        stack.Add(new Markup($"[dim]{AnsiMarkup.Escape(modelName ?? "(default model)")}[/]"));

        if (usage is null)
        {
            stack.Add(new TextBlock("Waiting for usage data from the active session."));
            return BuildUsageTooltipContainer(stack);
        }

        stack.Add(new Markup($"[bold]{AnsiMarkup.Escape(FormatSessionUsageSummary(usage))}[/]"));

        if (BuildContextWindowChart(usage) is { } contextChart)
        {
            stack.Add(contextChart);
        }

        if (usage.CurrentTokens is { } current &&
            usage.TokenLimit is { } limit &&
            limit > 0 &&
            current > limit)
        {
            stack.Add(new Markup($"[warning]{FormatNumber(current - limit)} tokens over the advertised window.[/]"));
        }

        if (usage.MessageCount is { } messageCount)
        {
            stack.Add(new Markup($"Messages: [bold]{messageCount.ToString(CultureInfo.InvariantCulture)}[/]"));
        }

        stack.Add(new Markup($"[dim]Updated {usage.UpdatedAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}[/]"));

        switch (usage.Details)
        {
            case CodexSessionUsageDetails codex:
                AddCodexUsageContent(stack, codex);
                break;
            case CopilotSessionUsageDetails copilot:
                AddCopilotUsageContent(stack, copilot);
                break;
        }

        return BuildUsageTooltipContainer(stack);
    }

    private static Visual BuildUsageTooltipContainer(Visual content)
    {
        return new Border(content)
        {
            Padding = new Thickness(1),
            MinWidth = UsageTooltipMinWidth,
            MaxWidth = UsageTooltipMaxWidth,
        };
    }

    private static Visual? BuildContextWindowChart(AgentSessionUsage usage)
    {
        if (usage.CurrentTokens is not { } current ||
            usage.TokenLimit is not { } limit ||
            limit <= 0)
        {
            return null;
        }

        var used = Math.Clamp(current, 0, limit);
        var remaining = Math.Max(0, limit - used);

        return new BreakdownChart()
            .ShowValues(true)
            .ShowPercentages(true)
            .Segment(used, new TextBlock("Used"), Colors.DodgerBlue)
            .Segment(remaining, new TextBlock("Free"), Colors.LimeGreen)
            .Style(new BreakdownStyle { SegmentGap = 1 });
    }

    private static void AddCodexUsageContent(VStack stack, CodexSessionUsageDetails details)
    {
        if (details.LastTurnUsage is not null)
        {
            stack.Add(new Markup("[bold]Last turn[/]"));
            if (BuildCodexUsageChart(details.LastTurnUsage) is { } lastChart)
            {
                stack.Add(lastChart);
            }

            stack.Add(new Markup(AnsiMarkup.Escape(FormatCodexTokenUsage(details.LastTurnUsage))));
        }

        if (details.TotalUsage is not null)
        {
            stack.Add(new Markup("[bold]Thread total[/]"));
            if (BuildCodexUsageChart(details.TotalUsage) is { } totalChart)
            {
                stack.Add(totalChart);
            }

            stack.Add(new Markup(AnsiMarkup.Escape(FormatCodexTokenUsage(details.TotalUsage))));
        }

        if (details.RateLimits is { } rateLimits)
        {
            stack.Add(new Markup("[bold]Rate limits[/]"));
            if (!string.IsNullOrWhiteSpace(rateLimits.LimitName) || !string.IsNullOrWhiteSpace(rateLimits.PlanType))
            {
                stack.Add(new Markup(AnsiMarkup.Escape($"{rateLimits.LimitName ?? rateLimits.LimitId ?? "Codex"} · {rateLimits.PlanType ?? "plan unknown"}")));
            }

            if (rateLimits.Primary is not null)
            {
                stack.Add(new Markup(AnsiMarkup.Escape($"Primary: {FormatCodexRateLimitWindow(rateLimits.Primary)}")));
            }

            if (rateLimits.Secondary is not null)
            {
                stack.Add(new Markup(AnsiMarkup.Escape($"Secondary: {FormatCodexRateLimitWindow(rateLimits.Secondary)}")));
            }
        }
    }

    private static void AddCopilotUsageContent(VStack stack, CopilotSessionUsageDetails details)
    {
        if (details.LastAssistantUsage is { } assistantUsage)
        {
            stack.Add(new Markup("[bold]Last API call[/]"));
            stack.Add(new Markup(AnsiMarkup.Escape(FormatCopilotAssistantUsage(assistantUsage))));
            if (BuildCopilotUsageChart(assistantUsage) is { } assistantChart)
            {
                stack.Add(assistantChart);
            }

            if (assistantUsage.DurationMs is not null || assistantUsage.Cost is not null || assistantUsage.TotalNanoAiu is not null)
            {
                var extra = new List<string>();
                if (assistantUsage.DurationMs is { } concreteDuration)
                {
                    extra.Add(FormattableString.Invariant($"duration {concreteDuration:0} ms"));
                }

                if (assistantUsage.Cost is { } concreteCost)
                {
                    extra.Add(FormattableString.Invariant($"cost {concreteCost:0.###}"));
                }

                if (assistantUsage.TotalNanoAiu is { } concreteNanoAiu)
                {
                    extra.Add(FormattableString.Invariant($"AIU {concreteNanoAiu:0}"));
                }

                if (extra.Count > 0)
                {
                    stack.Add(new Markup($"[dim]{AnsiMarkup.Escape(string.Join(" · ", extra))}[/]"));
                }
            }
        }

        if (details.LastCompaction is { } compaction)
        {
            stack.Add(new Markup("[bold]Last compaction[/]"));
            stack.Add(new Markup(AnsiMarkup.Escape(FormatCopilotCompaction(compaction))));
            if (BuildCopilotCompactionChart(compaction) is { } compactionChart)
            {
                stack.Add(compactionChart);
            }
        }

        if (details.QuotaSnapshots is { Length: > 0 } quotaSnapshots)
        {
            stack.Add(new Markup("[bold]Quota snapshots[/]"));
            foreach (var quota in quotaSnapshots)
            {
                stack.Add(new Markup(AnsiMarkup.Escape($"{quota.Name}: {SummarizeJson(quota.Payload)}")));
            }
        }
    }

    private static Visual? BuildCodexUsageChart(CodexTokenUsage usage)
    {
        var chart = new BreakdownChart().ShowValues(true).ShowPercentages(true);
        var added = 0;

        added += AddSegment(chart, usage.InputTokens, "Input", Colors.DodgerBlue);
        added += AddSegment(chart, usage.OutputTokens, "Output", Colors.Orange);
        added += AddSegment(chart, usage.CachedInputTokens, "Cache", Colors.LimeGreen);
        added += AddSegment(chart, usage.ReasoningOutputTokens, "Reasoning", Colors.MediumPurple);

        return added > 0
            ? chart.Style(new BreakdownStyle { SegmentGap = 1 })
            : null;
    }

    private static Visual? BuildCopilotUsageChart(CopilotAssistantUsage usage)
    {
        var chart = new BreakdownChart().ShowValues(true).ShowPercentages(true);
        var added = 0;

        added += AddSegment(chart, usage.InputTokens, "Input", Colors.DodgerBlue);
        added += AddSegment(chart, usage.OutputTokens, "Output", Colors.Orange);
        added += AddSegment(chart, usage.CacheReadTokens, "Cache Read", Colors.LimeGreen);
        added += AddSegment(chart, usage.CacheWriteTokens, "Cache Write", Colors.MediumPurple);

        return added > 0
            ? chart.Style(new BreakdownStyle { SegmentGap = 1 })
            : null;
    }

    private static Visual? BuildCopilotCompactionChart(CopilotCompactionUsage usage)
    {
        if (usage.TokensUsed is not { } tokens)
        {
            return null;
        }

        var chart = new BreakdownChart()
            .ShowValues(true)
            .ShowPercentages(true);

        var added = 0;
        added += AddSegment(chart, tokens.InputTokens, "Input", Colors.DodgerBlue);
        added += AddSegment(chart, tokens.OutputTokens, "Output", Colors.Orange);
        added += AddSegment(chart, tokens.CachedInputTokens, "Cache", Colors.LimeGreen);

        return added > 0
            ? chart.Style(new BreakdownStyle { SegmentGap = 1 })
            : null;
    }

    private static int AddSegment(BreakdownChart chart, long? value, string label, Color color)
    {
        if (value is not > 0)
        {
            return 0;
        }

        chart.Segment(value.Value, new TextBlock(label), color);
        return 1;
    }

    private static string FormatCodexTokenUsage(CodexTokenUsage usage)
    {
        return $"total {FormatNumber(usage.TotalTokens)} · input {FormatNumber(usage.InputTokens)} · output {FormatNumber(usage.OutputTokens)} · cache {FormatNumber(usage.CachedInputTokens)} · reasoning {FormatNumber(usage.ReasoningOutputTokens)}";
    }

    private static string FormatCodexRateLimitWindow(CodexRateLimitWindow window)
    {
        var parts = new List<string> { $"{window.UsedPercent}% used" };
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

    private static string FormatCopilotAssistantUsage(CopilotAssistantUsage usage)
    {
        var parts = new List<string> { usage.Model };
        if (usage.ReasoningEffort is { Length: > 0 } reasoningEffort)
        {
            parts.Add($"effort {reasoningEffort}");
        }

        if (usage.Initiator is { Length: > 0 } initiator)
        {
            parts.Add($"initiator {initiator}");
        }

        parts.Add($"input {FormatNumber(usage.InputTokens)}");
        parts.Add($"output {FormatNumber(usage.OutputTokens)}");

        if (usage.CacheReadTokens is { } cacheRead)
        {
            parts.Add($"cache read {FormatNumber(cacheRead)}");
        }

        if (usage.CacheWriteTokens is { } cacheWrite)
        {
            parts.Add($"cache write {FormatNumber(cacheWrite)}");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatCopilotCompaction(CopilotCompactionUsage usage)
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

    private static string SummarizeJson(JsonElement payload)
    {
        var raw = payload.GetRawText();
        return raw.Length <= 96
            ? raw
            : raw[..93] + "...";
    }

    private static string GetUsageTone(double percentage)
    {
        return percentage switch
        {
            < 70 => "success",
            < 90 => "warning",
            _ => "error",
        };
    }

    private static string FormatNumber(long? value)
        => value?.ToString("#,0", CultureInfo.InvariantCulture) ?? "?";
}
