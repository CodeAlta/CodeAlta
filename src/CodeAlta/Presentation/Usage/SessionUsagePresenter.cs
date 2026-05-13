using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Presentation.Formatting;
using CodeAlta.ViewModels;
using CodeAlta.Presentation.Controls;
using CodeAlta.Views;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Presentation.Usage;

internal sealed class SessionUsagePresenter
{
    private const int UsageTooltipMinWidth = 52;
    private const int UsageTooltipMaxWidth = 76;

    private readonly SessionUsageViewModel _viewModel;
    private readonly Action<string> _copyMarkdown;
    private readonly Func<Func<Visual>, Visual> _createComputedVisual;
    private readonly Action _focusPromptEditor;
    private AnchoredPopupView? _popupView;
    private Visual? _indicatorAnchor;

    public SessionUsagePresenter(
        SessionUsageViewModel viewModel,
        Action<string> copyMarkdown,
        Func<Func<Visual>, Visual> createComputedVisual,
        Action focusPromptEditor)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(copyMarkdown);
        ArgumentNullException.ThrowIfNull(createComputedVisual);
        ArgumentNullException.ThrowIfNull(focusPromptEditor);

        _viewModel = viewModel;
        _copyMarkdown = copyMarkdown;
        _createComputedVisual = createComputedVisual;
        _focusPromptEditor = focusPromptEditor;
    }

    public Visual BuildIndicatorVisual()
    {
        var button = new Button(new Markup(() => SessionUsageFormatter.BuildIndicatorMarkup(_viewModel.Usage))
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
        button.Click(() => TogglePopup(button));
        var buttonHost = button.Tooltip(new TextBlock($"Show context usage ({ThreadWorkspaceView.SessionUsageShortcutSequence})."));
        _indicatorAnchor = buttonHost;
        return buttonHost;
    }

    public void TogglePopupFromIndicator()
    {
        if (_indicatorAnchor is null)
        {
            return;
        }

        TogglePopup(_indicatorAnchor);
    }

    public void ClosePopup()
    {
        _popupView?.Close();
    }

    private void TogglePopup(Visual anchor)
    {
        if (_popupView?.IsOpen == true)
        {
            ClosePopup();
            return;
        }

        ShowPopup(anchor);
    }

    private void ShowPopup(Visual anchor)
    {
        _popupView ??= new AnchoredPopupView(() => _createComputedVisual(BuildPopupContent), _focusPromptEditor);
        _popupView.Show(anchor);
    }

    private Visual BuildPopupContent()
    {
        return BuildDetailsVisual(_viewModel.Usage, _viewModel.BackendName, _viewModel.ModelName, _viewModel.PluginTransientEvents);
    }

    private void CopyMarkdown()
    {
        var markdown = SessionUsageFormatter.BuildMarkdown(_viewModel.Usage, _viewModel.BackendName, _viewModel.ModelName);
        _copyMarkdown(markdown);
    }

    private Visual BuildDetailsVisual(
        AgentSessionUsage? usage,
        string backendName,
        string? modelName,
        IReadOnlyList<PluginTransientEventProjection> pluginTransientEvents)
    {
        var stack = new VStack
        {
            Spacing = 1,
        };

        var copyButton = new Button(new TextBlock($"{NerdFont.MdContentCopy}"))
            .Click(CopyMarkdown);
        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose}"))
            .Click(ClosePopup);
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
            return BuildPopupContainer(stack);
        }

        AddUsageBreakdownContent(stack, usage);
        AddLimitsAndQuotasContent(stack, usage);
        AddBackendSpecificContent(stack, usage);
        AddPluginProjectionContent(stack, pluginTransientEvents);

        return BuildPopupContainer(stack);
    }

    private static Visual BuildPopupContainer(Visual content)
    {
        return new Padder(content)
        {
            Padding = new Thickness(1),
            MinWidth = UsageTooltipMinWidth,
            MaxWidth = UsageTooltipMaxWidth,
        };
    }

    private static Visual? BuildInputContextPressureChart(AgentSessionUsage usage)
    {
        if (usage.Window is not { CurrentTokens: { } current, TokenLimit: { } inputLimit } || inputLimit <= 0)
        {
            return null;
        }

        var used = Math.Clamp(current, 0, inputLimit);
        var remaining = Math.Max(0, inputLimit - used);

        return new BreakdownChart()
            .ShowValues(true)
            .ShowPercentages(true)
            .Segment(used, new TextBlock("Active context"), Colors.DodgerBlue)
            .Segment(remaining, new TextBlock("Input headroom"), Colors.LimeGreen)
            .Style(new BreakdownStyle { SegmentGap = 1 });
    }

    private static void AddUsageBreakdownContent(VStack stack, AgentSessionUsage usage)
    {
        if (usage.Window is null && usage.LastOperation is null)
        {
            return;
        }

        var sectionTitle = usage.MessageCount is { } messageCount
            ? FormattableString.Invariant($"Context usage: {messageCount} messages")
            : "Context usage";
        AddSectionHeader(stack, sectionTitle);
        if (usage.Window is not null)
        {
            stack.Add(new Markup($"[bold]Compaction pressure[/] [dim]{AnsiMarkup.Escape(SessionUsageFormatter.FormatSummary(usage))}[/]"));
            if (BuildInputContextPressureChart(usage) is { } pressureChart)
            {
                stack.Add(pressureChart);
            }

            if (SessionUsageFormatter.TryFormatModelEnvelope(usage.Window, out var modelEnvelope))
            {
                stack.Add(new Markup($"[dim]Indicative model limits: {AnsiMarkup.Escape(modelEnvelope)}[/]"));
            }
        }

        if (usage.LastOperation is { } operation)
        {
            stack.Add(new Markup($"[bold]{AnsiMarkup.Escape(operation.Label ?? "Last operation")}[/]"));
            if (BuildOperationUsageChart(operation) is { } operationChart)
            {
                stack.Add(operationChart);
            }

            if (SessionUsageFormatter.FormatOperationPopupText(operation) is { Length: > 0 } operationText)
            {
                stack.Add(new Markup($"[dim]{AnsiMarkup.Escape(operationText)}[/]"));
            }
        }

        if (SessionUsageFormatter.TryFormatUsageMetadataLine(usage, out var metadataLine))
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
                stack.Add(new Markup(AnsiMarkup.Escape($"Primary: {SessionUsageFormatter.FormatAgentRateLimitWindow(rateLimits.Primary)}")));
            }

            if (rateLimits.Secondary is not null)
            {
                stack.Add(new Markup(AnsiMarkup.Escape($"Secondary: {SessionUsageFormatter.FormatAgentRateLimitWindow(rateLimits.Secondary)}")));
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

    private static void AddPluginProjectionContent(VStack stack, IReadOnlyList<PluginTransientEventProjection> projections)
    {
        if (projections.Count == 0)
        {
            return;
        }

        AddSectionHeader(stack, "Plugin statistics");
        foreach (var projection in projections)
        {
            if (string.IsNullOrWhiteSpace(projection.Markdown))
            {
                continue;
            }

            stack.Add(CreateUsageMarkdownControl(projection.Markdown));
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
            stack.Add(new Markup(AnsiMarkup.Escape(SessionUsageFormatter.FormatCopilotCompaction(compaction))));
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
                    stack.Add(new Markup(AnsiMarkup.Escape($"{tokenDetail.TokenType}: {SessionUsageFormatter.FormatNumber(tokenDetail.TokenCount)}")));
                }
            }
        }
    }

    private static void AddOpaqueQuotaSnapshotContent(VStack stack, CopilotQuotaSnapshot quota)
    {
        stack.Add(new Markup($"[bold]{AnsiMarkup.Escape(quota.Name)}[/]"));
        if (quota.Details is CopilotOpaqueQuotaDetails opaqueQuota)
        {
            stack.Add(CreateUsageMarkdownControl(ChatMarkdownFormatter.FormatCodeFence(opaqueQuota.Summary, "text")));
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
                new Markup($"[bold]{AnsiMarkup.Escape(SessionUsageFormatter.FormatCopilotQuotaUsageCell(requestQuota))}[/]"),
                new TextBlock(SessionUsageFormatter.FormatCopilotQuotaStatusCell(requestQuota))
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
        added += AddSegment(chart, GetNonReasoningOutputTokens(usage.OutputTokens, usage.ReasoningTokens), "Output", Colors.Orange);
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
        added += AddSegment(chart, GetNonReasoningOutputTokens(usage.OutputTokens, usage.ReasoningOutputTokens), "Output", Colors.Orange);
        added += AddSegment(chart, usage.CachedInputTokens, "Cache", Colors.LimeGreen);
        added += AddSegment(chart, usage.ReasoningOutputTokens, "Reasoning", Colors.MediumPurple);

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

    private static long? GetNonReasoningOutputTokens(long? outputTokens, long? reasoningTokens)
        => outputTokens is { } output
            ? Math.Max(0, output - (reasoningTokens ?? 0))
            : null;

    private static int AddSegment(BreakdownChart chart, long? value, string label, Color color)
    {
        if (value is not > 0)
        {
            return 0;
        }

        chart.Segment(value.Value, new TextBlock(label), color);
        return 1;
    }
}
