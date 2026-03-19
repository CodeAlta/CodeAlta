using System.Globalization;
using CodeAlta.Agent;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

internal sealed partial class CodeAltaTerminalUi
{
    private const int UsageTooltipMinWidth = 52;
    private const int UsageTooltipMaxWidth = 76;

    private Visual BuildSessionUsageIndicatorVisual()
    {
        var button = new Button(new Markup(() => BuildSessionUsageIndicatorMarkup(GetSelectedSessionUsage()))
        {
            Wrap = false,
        })
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
        };
        button.Style(ButtonStyle.Default with
        {
            Normal = Style.None,
            Padding = Thickness.Zero,
        });
        button.Click(() => ToggleSessionUsagePopup(button));

        return button;
    }

    private void ToggleSessionUsagePopup(Visual anchor)
    {
        if (_sessionUsagePopupOpen)
        {
            CloseSessionUsagePopup();
            return;
        }

        ShowSessionUsagePopup(anchor);
    }

    private void ShowSessionUsagePopup(Visual anchor)
    {
        _sessionUsagePopup ??= CreateSessionUsagePopup();
        _sessionUsagePopup.Anchor = anchor;
        _sessionUsagePopup.Placement = PopupPlacement.Above;
        _sessionUsagePopup.OffsetY = 0;
        _sessionUsagePopup.Content = CreateComputedVisual(BuildSessionUsagePopupContent);
        _sessionUsagePopup.Show();
        _sessionUsagePopupOpen = true;
    }

    private void CloseSessionUsagePopup()
    {
        _sessionUsagePopup?.Close();
    }

    private Popup CreateSessionUsagePopup()
    {
        var popup = new Popup
        {
            MatchAnchorWidth = false,
            CloseOnTab = false,
        };
        popup.Closed((_, _) => _sessionUsagePopupOpen = false);
        return popup;
    }

    private Visual BuildSessionUsagePopupContent()
    {
        var (backendName, modelName) = GetUsageSelectionContext();
        return BuildSessionUsageDetailsVisual(GetSelectedSessionUsage(), backendName, modelName);
    }

    private void CopySessionUsageMarkdown()
    {
        var (backendName, modelName) = GetUsageSelectionContext();
        var markdown = BuildSessionUsageMarkdown(GetSelectedSessionUsage(), backendName, modelName);
        (_sessionUsagePopup?.App ?? _threadPaneLayout?.App)?.Terminal.Clipboard.TrySetText(markdown);
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
            Window: MergeWindowUsage(current.Window, incoming.Window),
            LastOperation: MergeOperationUsage(current.LastOperation, incoming.LastOperation),
            RateLimits: MergeRateLimitSummary(current.RateLimits, incoming.RateLimits),
            Scope: incoming.Scope,
            Source: incoming.Source,
            UpdatedAt: incoming.UpdatedAt,
            Details: MergeSessionUsageDetails(current.Details, incoming.Details));
    }

    internal static string BuildSessionUsageIndicatorMarkup(AgentSessionUsage? usage)
    {
        if (usage?.WindowUsagePercentage is not { } percentage)
        {
            return "ctx --";
        }

        return FormattableString.Invariant($"ctx {Math.Clamp(percentage, 0d, 999d):0}%");
    }

    internal static string FormatSessionUsageSummary(AgentSessionUsage usage)
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

    private static AgentWindowUsageSnapshot? MergeWindowUsage(AgentWindowUsageSnapshot? current, AgentWindowUsageSnapshot? incoming)
    {
        if (incoming is null)
        {
            return current;
        }

        if (current is null)
        {
            return incoming;
        }

        return current with
        {
            CurrentTokens = incoming.CurrentTokens ?? current.CurrentTokens,
            TokenLimit = incoming.TokenLimit ?? current.TokenLimit,
            MessageCount = incoming.MessageCount ?? current.MessageCount,
            Label = incoming.Label ?? current.Label,
        };
    }

    private static AgentOperationUsageSnapshot? MergeOperationUsage(AgentOperationUsageSnapshot? current, AgentOperationUsageSnapshot? incoming)
    {
        if (incoming is null)
        {
            return current;
        }

        if (current is null)
        {
            return incoming;
        }

        return current with
        {
            Model = incoming.Model ?? current.Model,
            InputTokens = incoming.InputTokens ?? current.InputTokens,
            OutputTokens = incoming.OutputTokens ?? current.OutputTokens,
            CacheReadTokens = incoming.CacheReadTokens ?? current.CacheReadTokens,
            CacheWriteTokens = incoming.CacheWriteTokens ?? current.CacheWriteTokens,
            CachedInputTokens = incoming.CachedInputTokens ?? current.CachedInputTokens,
            ReasoningTokens = incoming.ReasoningTokens ?? current.ReasoningTokens,
            Cost = incoming.Cost ?? current.Cost,
            DurationMs = incoming.DurationMs ?? current.DurationMs,
            Initiator = incoming.Initiator ?? current.Initiator,
            ParentToolCallId = incoming.ParentToolCallId ?? current.ParentToolCallId,
            ReasoningEffort = incoming.ReasoningEffort ?? current.ReasoningEffort,
            Label = incoming.Label ?? current.Label,
        };
    }

    private static AgentRateLimitSummary? MergeRateLimitSummary(AgentRateLimitSummary? current, AgentRateLimitSummary? incoming)
    {
        if (incoming is null)
        {
            return current;
        }

        if (current is null)
        {
            return incoming;
        }

        return current with
        {
            Name = incoming.Name ?? current.Name,
            PlanType = incoming.PlanType ?? current.PlanType,
            Primary = MergeRateLimitWindow(current.Primary, incoming.Primary),
            Secondary = MergeRateLimitWindow(current.Secondary, incoming.Secondary),
            Label = incoming.Label ?? current.Label,
        };
    }

    private static AgentRateLimitWindow? MergeRateLimitWindow(AgentRateLimitWindow? current, AgentRateLimitWindow? incoming)
    {
        if (incoming is null)
        {
            return current;
        }

        if (current is null)
        {
            return incoming;
        }

        return current with
        {
            UsedPercent = incoming.UsedPercent ?? current.UsedPercent,
            ResetsAt = incoming.ResetsAt ?? current.ResetsAt,
            WindowDurationMinutes = incoming.WindowDurationMinutes ?? current.WindowDurationMinutes,
        };
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

    internal static string BuildSessionUsageMarkdown(AgentSessionUsage? usage, string backendName, string? modelName)
    {
        var builder = new System.Text.StringBuilder();
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

    private Visual BuildSessionUsageDetailsVisual(AgentSessionUsage? usage, string backendName, string? modelName)
    {
        var stack = new VStack
        {
            Spacing = 1,
        };

        var copyButton = new Button(new TextBlock($"{NerdFont.MdContentCopy}"))
            .Click(CopySessionUsageMarkdown);
        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose}"))
            .Click(CloseSessionUsagePopup);
        closeButton.Tone = ControlTone.Error;

        stack.Add(new StatusBar()
            .LeftText(new VStack(
                new Markup($"[bold]{AnsiMarkup.Escape(backendName)} context usage[/]"),
                new Markup($"[dim]{AnsiMarkup.Escape(modelName ?? "(default model)")}[/]"))
            {
                Spacing = 0,
            })
            .RightText(new HStack(copyButton, closeButton)
            {
                Spacing = 1,
            }));

        if (usage is null)
        {
            stack.Add(new TextBlock("Waiting for usage data from the active session."));
            return BuildUsagePopupContainer(stack);
        }

        AddUsageBreakdownContent(stack, usage);
        AddLimitsAndQuotasContent(stack, usage);
        AddBackendSpecificContent(stack, usage);

        return BuildUsagePopupContainer(stack);
    }

    private static Visual BuildUsagePopupContainer(Visual content)
    {
        return new Padder(content)
        {
            Padding = new Thickness(1),
            MinWidth = UsageTooltipMinWidth,
            MaxWidth = UsageTooltipMaxWidth,
        };
    }

    private static Visual? BuildContextWindowChart(AgentSessionUsage usage)
    {
        if (usage.Window is not { CurrentTokens: { } current, TokenLimit: { } limit } || limit <= 0)
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

    private static void AddUsageBreakdownContent(VStack stack, AgentSessionUsage usage)
    {
        var hasWindowChart = BuildContextWindowChart(usage) is not null;
        if (!hasWindowChart && usage.LastOperation is null)
        {
            return;
        }

        var sectionTitle = usage.MessageCount is { } messageCount
            ? FormattableString.Invariant($"Usage breakdown: {messageCount} messages")
            : "Usage breakdown";
        AddSectionHeader(stack, sectionTitle);
        if (BuildContextWindowChart(usage) is { } contextChart)
        {
            stack.Add(contextChart);
        }

        if (usage.LastOperation is { } operation)
        {
            stack.Add(new Markup($"[bold]{AnsiMarkup.Escape(operation.Label ?? "Last operation")}[/]"));
            if (BuildOperationUsageChart(operation) is { } operationChart)
            {
                stack.Add(operationChart);
            }
            if (FormatOperationPopupText(operation) is { Length: > 0 } operationText)
            {
                stack.Add(new Markup($"[dim]{AnsiMarkup.Escape(operationText)}[/]"));
            }
        }

        if (TryFormatUsageMetadataLine(usage, out var metadataLine))
        {
            stack.Add(new Markup($"[dim]{AnsiMarkup.Escape(metadataLine)}[/]"));
        }
    }

    private static void AddLimitsAndQuotasContent(VStack stack, AgentSessionUsage usage)
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

        var sectionTitle = usage.RateLimits is not null && (requestQuotas is { Length: > 0 } || opaqueQuotas is { Length: > 0 })
            ? "Limits and quotas"
            : usage.RateLimits is not null
                ? "Limits"
                : "Quotas";
        AddSectionHeader(stack, sectionTitle);
        if (usage.RateLimits is { } rateLimits)
        {
            if (!string.IsNullOrWhiteSpace(rateLimits.Name) || !string.IsNullOrWhiteSpace(rateLimits.PlanType))
            {
                stack.Add(new Markup($"[bold]{AnsiMarkup.Escape($"{rateLimits.Name ?? "Rate limits"} · {rateLimits.PlanType ?? "plan unknown"}")}[/]"));
            }

            if (rateLimits.Primary is not null)
            {
                stack.Add(new Markup(AnsiMarkup.Escape($"Primary: {FormatAgentRateLimitWindow(rateLimits.Primary)}")));
            }

            if (rateLimits.Secondary is not null)
            {
                stack.Add(new Markup(AnsiMarkup.Escape($"Secondary: {FormatAgentRateLimitWindow(rateLimits.Secondary)}")));
            }
        }

        if (requestQuotas is { Length: > 0 })
        {
            stack.Add(new Markup("[bold]Copilot quota snapshots[/]"));
            stack.Add(BuildCopilotQuotaTable(requestQuotas));
        }

        if (opaqueQuotas is { Length: > 0 })
        {
            foreach (var quota in opaqueQuotas)
            {
                AddOpaqueQuotaSnapshotContent(stack, quota);
            }
        }
    }

    private static void AddBackendSpecificContent(VStack stack, AgentSessionUsage usage)
    {
        var added = false;
        if (usage.Details is CodexSessionUsageDetails codex &&
            codex.TotalUsage is not null)
        {
            AddSectionHeader(stack, "Backend-specific details");
            AddCodexUsageContent(stack, codex);
            added = true;
        }

        if (usage.Details is CopilotSessionUsageDetails copilot &&
            (copilot.LastCompaction is not null || copilot.LastAssistantUsage?.TotalNanoAiu is not null || copilot.LastAssistantUsage?.TokenDetails is { Length: > 0 }))
        {
            if (!added)
            {
                AddSectionHeader(stack, "Backend-specific details");
            }

            AddCopilotUsageContent(stack, copilot);
        }
    }

    private static void AddCodexUsageContent(VStack stack, CodexSessionUsageDetails details)
    {
        if (details.TotalUsage is not null)
        {
            stack.Add(new Markup("[bold]Thread total[/]"));
            if (BuildCodexUsageChart(details.TotalUsage) is { } totalChart)
            {
                stack.Add(totalChart);
            }
        }
    }

    private static void AddCopilotUsageContent(VStack stack, CopilotSessionUsageDetails details)
    {
        if (details.LastCompaction is { } compaction)
        {
            stack.Add(new Markup("[bold]Last compaction[/]"));
            stack.Add(new Markup(AnsiMarkup.Escape(FormatCopilotCompaction(compaction))));
            if (BuildCopilotCompactionChart(compaction) is { } compactionChart)
            {
                stack.Add(compactionChart);
            }
        }

        if (details.LastAssistantUsage is { } assistantUsage)
        {
            if (assistantUsage.TotalNanoAiu is { } totalNanoAiu)
            {
                stack.Add(new Markup(AnsiMarkup.Escape(FormattableString.Invariant($"AIU {totalNanoAiu:0}"))));
            }

            if (assistantUsage.TokenDetails is { Length: > 0 } tokenDetails)
            {
                stack.Add(new Markup("[bold]Copilot token details[/]"));
                foreach (var tokenDetail in tokenDetails)
                {
                    stack.Add(new Markup(AnsiMarkup.Escape($"{tokenDetail.TokenType}: {FormatNumber(tokenDetail.TokenCount)}")));
                }
            }
        }
    }

    private static void AddOpaqueQuotaSnapshotContent(VStack stack, CopilotQuotaSnapshot quota)
    {
        stack.Add(new Markup($"[bold]{AnsiMarkup.Escape(quota.Name)}[/]"));
        if (quota.Details is CopilotOpaqueQuotaDetails opaqueQuota)
        {
            stack.Add(CreateUsageMarkdownControl(FormatChatCodeFence(opaqueQuota.Summary, "text")));
        }
    }

    private static MarkdownControl CreateUsageMarkdownControl(string markdown)
    {
        return new MarkdownControl(markdown.Trim())
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Start,
            Options = MarkdownRenderOptions.Default with
            {
                WrapCodeBlocks = true,
                MaxCodeBlockHeight = 12,
            },
        };
    }

    private static void AddSectionHeader(VStack stack, string title)
    {
        stack.Add(new Markup($"[bold]{AnsiMarkup.Escape(title)}[/]"));
    }

    private static Table BuildCopilotQuotaTable(IEnumerable<CopilotQuotaSnapshot> quotas)
    {
        var table = new Table()
            .Headers("Quota", "Usage", "Status")
            .Style(TableStyle.RoundedGrid with { ShowRowSeparators = false });

        foreach (var quota in quotas)
        {
            var requestQuota = (CopilotRequestQuotaDetails)quota.Details;
            table.AddRow(
                new TextBlock(quota.Name),
                new Markup($"[bold]{AnsiMarkup.Escape(FormatCopilotQuotaUsageCell(requestQuota))}[/]"),
                new TextBlock(FormatCopilotQuotaStatusCell(requestQuota))
                {
                    Wrap = true,
                });
        }

        return table;
    }

    private static Visual? BuildOperationUsageChart(AgentOperationUsageSnapshot usage)
    {
        var chart = new BreakdownChart().ShowValues(true).ShowPercentages(true);
        var added = 0;

        added += AddSegment(chart, usage.InputTokens, "Input", Colors.DodgerBlue);
        added += AddSegment(chart, usage.OutputTokens, "Output", Colors.Orange);
        added += AddSegment(chart, usage.CacheReadTokens, "Cache Read", Colors.LimeGreen);
        added += AddSegment(chart, usage.CacheWriteTokens, "Cache Write", Colors.MediumPurple);
        added += AddSegment(chart, usage.CachedInputTokens, "Cache", Colors.LimeGreen);
        added += AddSegment(chart, usage.ReasoningTokens, "Reasoning", Colors.Goldenrod);

        return added > 0
            ? chart.Style(new BreakdownStyle { SegmentGap = 1 })
            : null;
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

    internal static string? FormatOperationPopupText(AgentOperationUsageSnapshot usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (BuildOperationUsageChart(usage) is null)
        {
            var summary = FormatOperationUsage(usage);
            return summary.Length > 0 ? summary : null;
        }

        return TryFormatOperationPopupMetadata(usage, out var metadata)
            ? metadata
            : null;
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

    private static string FormatAgentRateLimitWindow(AgentRateLimitWindow window)
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

    private static string FormatCopilotQuotaDetails(CopilotRequestQuotaDetails quota)
    {
        var parts = new List<string>();
        if (quota.IsUnlimitedEntitlement == true)
        {
            parts.Add("unlimited entitlement");
        }

        if (quota.UsedRequests is { } usedRequests && quota.EntitlementRequests is { } entitlementRequests)
        {
            parts.Add($"{FormatNumber(usedRequests)} / {FormatNumber(entitlementRequests)} requests");
        }
        else if (quota.UsedRequests is { } usedOnly)
        {
            parts.Add($"{FormatNumber(usedOnly)} requests used");
        }

        if (quota.RemainingPercentage is { } remainingPercentage)
        {
            parts.Add(FormattableString.Invariant($"{remainingPercentage:0.#}% remaining"));
        }

        if (quota.Overage is { } overage && overage > 0)
        {
            parts.Add($"{FormatNumber(overage)} overage");
        }

        if (quota.UsageAllowedWithExhaustion is { } usageAllowedWithExhaustion)
        {
            parts.Add(usageAllowedWithExhaustion ? "usage allowed after exhaustion" : "usage blocked at exhaustion");
        }

        if (quota.ResetDate is { } resetDate)
        {
            parts.Add($"resets {resetDate.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "quota snapshot";
    }

    private static string FormatCopilotQuotaUsageCell(CopilotRequestQuotaDetails quota)
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

    private static string FormatCopilotQuotaStatusCell(CopilotRequestQuotaDetails quota)
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

    private static bool TryFormatUsageMetadataLine(AgentSessionUsage usage, out string metadataLine)
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

    private static void AppendUsageBreakdownMarkdown(System.Text.StringBuilder builder, AgentSessionUsage usage)
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
            builder.Append("- Window: ").AppendLine(FormatSessionUsageSummary(usage));
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

    private static void AppendLimitsAndQuotasMarkdown(System.Text.StringBuilder builder, AgentSessionUsage usage)
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

    private static void AppendBackendSpecificMarkdown(System.Text.StringBuilder builder, AgentSessionUsage usage)
    {
        var appended = false;
        if (usage.Details is CodexSessionUsageDetails codex &&
            codex.TotalUsage is not null)
        {
            builder.AppendLine()
                .AppendLine("## Backend-specific details");
            appended = true;
            if (codex.TotalUsage is not null)
            {
                builder.Append("- Thread total: ")
                    .AppendLine(FormatCodexTokenUsage(codex.TotalUsage));
            }
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
