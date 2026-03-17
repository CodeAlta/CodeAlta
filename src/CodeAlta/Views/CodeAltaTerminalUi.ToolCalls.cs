using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

internal sealed partial class CodeAltaTerminalUi
{
    private const int ToolCallPreviewLimit = 16;
    private const int ToolCallDialogLogCapacity = 20000;

    private bool TryHandleToolTimelineActivity(ThreadTabState tab, AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(activity);

        if (!IsToolTimelineActivity(activity))
        {
            return false;
        }

        var entry = GetOrCreateToolCallEntry(tab, activity.ActivityId, activity.Timestamp, MapToolActivityKind(activity.Kind));
        UpdateToolCallFromActivity(entry, activity);
        UpdateToolCallEntryVisual(entry);
        UpdateToolCallGroupVisual(entry.Group);
        return true;
    }

    private bool TryHandleToolTimelineContent(ThreadTabState tab, AgentContentDeltaEvent delta)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(delta);

        if (!IsToolTimelineContent(delta.Kind))
        {
            return false;
        }

        var entry = GetOrCreateToolCallEntryFromContent(tab, delta.ContentId, delta.ParentActivityId, delta.Kind, delta.Timestamp);
        AppendToolCallOutput(entry, delta.Delta, delta.Timestamp);
        return true;
    }

    private bool TryHandleToolTimelineContent(ThreadTabState tab, AgentContentCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(completed);

        if (!IsToolTimelineContent(completed.Kind))
        {
            return false;
        }

        var entry = GetOrCreateToolCallEntryFromContent(tab, completed.ContentId, completed.ParentActivityId, completed.Kind, completed.Timestamp);
        ReplaceToolCallOutput(entry, completed.Content, completed.Timestamp);
        return true;
    }

    private ToolCallEntryState GetOrCreateToolCallEntryFromContent(
        ThreadTabState tab,
        string contentId,
        string? parentActivityId,
        AgentContentKind kind,
        DateTimeOffset timestamp)
    {
        var toolCallId = ResolveToolCallId(tab, contentId, parentActivityId);
        return GetOrCreateToolCallEntry(tab, toolCallId, timestamp, MapToolContentKind(kind));
    }

    private ToolCallEntryState GetOrCreateToolCallEntry(
        ThreadTabState tab,
        string toolCallId,
        DateTimeOffset timestamp,
        AgentActivityKind activityKind)
    {
        if (tab.ToolCallStates.TryGetValue(toolCallId, out var existing))
        {
            existing.LastUpdatedAt = timestamp;
            existing.FirstSeenAt = existing.FirstSeenAt == default ? timestamp : existing.FirstSeenAt;
            return existing;
        }

        var group = GetOrCreateToolCallGroup(tab, timestamp);
        var entry = RunOnUiThread(
            static state =>
            {
                var summaryText = new Markup(string.Empty)
                {
                    Wrap = false,
                };

                var button = new Button(summaryText)
                {
                    MinWidth = 16,
                    MaxWidth = 28,
                    HorizontalAlignment = Align.Start,
                    VerticalAlignment = Align.Start,
                };
                button.SetStyle(ButtonStyle.Key, UiPalette.GetToolChipButtonStyle(ToolCallDisplayStatus.Pending));

                return new ToolCallEntryState(state.toolCallId, button, summaryText)
                {
                    Group = state.group,
                    ActivityKind = state.activityKind,
                    DisplayName = GetActivityKindLabel(state.activityKind),
                    FirstSeenAt = state.timestamp,
                    LastUpdatedAt = state.timestamp,
                };
            },
            (toolCallId, group, activityKind, timestamp));

        entry.Button.Click(() => OpenToolCallDialog(entry));
        group.ToolCalls[toolCallId] = entry;
        tab.ToolCallStates[toolCallId] = entry;

        PostToUi(() =>
        {
            group.ItemsHost.Children.Add(entry.Button);
            tab.Flow.ScrollToTail();
        });

        return entry;
    }

    private ToolCallGroupState GetOrCreateToolCallGroup(ThreadTabState tab, DateTimeOffset timestamp)
    {
        if (tab.ActiveToolCallGroup is { } existing)
        {
            existing.LastUpdatedAt = timestamp;
            return existing;
        }

        var group = RunOnUiThread(
            static state =>
            {
                var headerText = new Markup($"[{UiPalette.MutedMarkup}]{NerdFont.CodTools}[/] [bold]Tool Calls[/]");
                var summaryText = new Markup("[dim]Waiting for tool activity...[/]");
                var timestampText = new Markup(string.Empty);
                var itemsHost = new WrapHStack
                {
                    Spacing = 1,
                    RunSpacing = 0,
                    HorizontalAlignment = Align.Stretch,
                    VerticalAlignment = Align.Start,
                };
                var card = new Group(headerText, itemsHost)
                    .TopRightText(summaryText)
                    .BottomRightText(timestampText)
                    .Style(UiPalette.GetToolCallGroupStyle())
                    .HorizontalAlignment(Align.Stretch)
                    .VerticalAlignment(Align.Start);

                var item = new DocumentFlowItem
                {
                    Content = new FlowDocument().Add(card),
                    Alignment = DocumentFlowAlignment.Stretch,
                };

                return new ToolCallGroupState(item, itemsHost, headerText, summaryText, timestampText)
                {
                    LastUpdatedAt = state,
                };
            },
            (timestamp));

        tab.ActiveToolCallGroup = group;
        PostToUi(() =>
        {
            tab.Flow.Items.Add(group.Item);
            tab.Flow.ScrollToTail();
        });

        ApplyChatCardTimestamp(group.TimestampText, timestamp);
        UpdateToolCallGroupVisual(group);
        return group;
    }

    private void UpdateToolCallFromActivity(ToolCallEntryState entry, AgentActivityEvent activity)
    {
        entry.ActivityKind = PreferActivityKind(entry.ActivityKind, activity.Kind);
        entry.Status = ToToolCallDisplayStatus(activity.Phase);
        entry.DisplayName = PreferToolDisplayName(entry.DisplayName, ResolveToolDisplayName(activity), activity);
        entry.ParentToolCallId = PreferLongerText(entry.ParentToolCallId, activity.ParentActivityId);
        entry.StatusMessage = PreferLongerText(entry.StatusMessage, activity.Message);
        entry.CommandText = PreferLongerText(entry.CommandText, ResolveToolCommandText(activity));
        entry.ArgumentText = PreferLongerText(entry.ArgumentText, ResolveToolArgumentText(activity));
        entry.ArgumentPreview = BuildToolPreview(entry.ArgumentText);
        entry.Details = activity.Details;
        entry.FirstSeenAt = entry.FirstSeenAt == default ? activity.Timestamp : entry.FirstSeenAt;
        entry.LastUpdatedAt = activity.Timestamp;
        entry.CompletedAt = IsCompletedStatus(entry.Status) ? activity.Timestamp : null;

        var extractedOutput = ResolveToolOutput(activity);
        if (!string.IsNullOrWhiteSpace(extractedOutput))
        {
            ReplaceToolCallOutput(entry, extractedOutput!, activity.Timestamp, updateStatusOnly: true);
        }

        if (IsRedundantStatusDetail(entry.StatusMessage, entry.OutputBuffer.ToString()))
        {
            entry.StatusMessage = null;
        }
    }

    private static AgentActivityKind PreferActivityKind(AgentActivityKind existing, AgentActivityKind candidate)
    {
        if (candidate == AgentActivityKind.ToolCall &&
            existing is AgentActivityKind.CommandExecution
                or AgentActivityKind.FileChange
                or AgentActivityKind.McpToolCall
                or AgentActivityKind.WebSearch
                or AgentActivityKind.ImageGeneration
                or AgentActivityKind.Subagent)
        {
            return existing;
        }

        return candidate;
    }

    private void AppendToolCallOutput(ToolCallEntryState entry, string? delta, DateTimeOffset timestamp)
    {
        if (!string.IsNullOrEmpty(delta))
        {
            entry.OutputBuffer.Append(delta);
        }

        entry.LastUpdatedAt = timestamp;
        if (entry.Status == ToolCallDisplayStatus.Pending)
        {
            entry.Status = ToolCallDisplayStatus.Running;
        }

        RefreshToolCallOutputState(entry);
    }

    private void ReplaceToolCallOutput(
        ToolCallEntryState entry,
        string? output,
        DateTimeOffset timestamp,
        bool updateStatusOnly = false)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            var normalizedOutput = NormalizeToolOutput(output);
            if (normalizedOutput.Length >= entry.OutputBuffer.Length || entry.OutputBuffer.Length == 0)
            {
                entry.OutputBuffer.Clear();
                entry.OutputBuffer.Append(normalizedOutput);
            }
        }

        entry.LastUpdatedAt = timestamp;
        if (!updateStatusOnly && entry.Status == ToolCallDisplayStatus.Pending)
        {
            entry.Status = ToolCallDisplayStatus.Running;
        }

        RefreshToolCallOutputState(entry);
    }

    private void RefreshToolCallOutputState(ToolCallEntryState entry)
    {
        var output = entry.OutputBuffer.ToString();
        entry.OutputLineCount = CountLines(output);
        entry.OutputByteCount = Encoding.UTF8.GetByteCount(output);
        entry.OutputPreview = BuildToolPreview(output);
        UpdateToolCallEntryVisual(entry);
        UpdateToolCallGroupVisual(entry.Group);
        UpdateToolCallDialogVisual(entry);
    }

    private void UpdateToolCallEntryVisual(ToolCallEntryState entry)
    {
        PostToUi(() =>
        {
            entry.SummaryText.Text = BuildToolCallSummaryMarkup(entry);
            entry.Button.Tone = ControlTone.Default;
            entry.Button.SetStyle(ButtonStyle.Key, UiPalette.GetToolChipButtonStyle(entry.Status));
        });
    }

    private void UpdateToolCallGroupVisual(ToolCallGroupState? group)
    {
        if (group is null)
        {
            return;
        }

        PostToUi(() =>
        {
            group.SummaryText.Text = BuildToolCallGroupSummaryMarkup(group);
            ApplyChatCardTimestamp(group.TimestampText, group.LastUpdatedAt);
        });
    }

    private void OpenToolCallDialog(ToolCallEntryState entry)
    {
        PostToUi(() =>
        {
            if (entry.DetailDialog is { App: not null })
            {
                return;
            }

            var metadata = new MarkdownControl(string.Empty)
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Start,
                Options = XenoAtom.Terminal.UI.Extensions.Markdown.MarkdownRenderOptions.Default with
                {
                    WrapCodeBlocks = true,
                    MaxCodeBlockHeight = 5,
                },
            };

            var wrapText = new State<bool>(true);
            var log = new LogControl
            {
                MaxCapacity = ToolCallDialogLogCapacity,
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }.WrapText(wrapText);

            var statsText = new Markup(string.Empty);

            var detailsGroup = new Group("Details", metadata)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Start);

            var outputGroup = new Group("Output", log)
                .TopRightText(new CheckBox("Wrap").IsChecked(wrapText))
                .Padding(0)
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch);

            var bounds = _threadPaneLayout?.GetAbsoluteBounds() ?? entry.Button.GetAbsoluteBounds();
            var dialogWidth = Math.Max(56, (int)Math.Round(bounds.Width * 0.8, MidpointRounding.AwayFromZero));
            var dialogHeight = Math.Max(16, (int)Math.Round(bounds.Height * 0.8, MidpointRounding.AwayFromZero));

            Dialog? dialog = null;
            var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
            {
                HorizontalAlignment = Align.End,
                VerticalAlignment = Align.Start,
                Tone = ControlTone.Error,
            };
            closeButton.Click(() => CloseToolCallDialog(entry));
            dialog = new Dialog()
                .Title($"{ResolveToolDisplayName(entry.ActivityKind, entry.DisplayName)}")
                .TopRightText(closeButton)
                .BottomLeftText(statsText)
                .BottomRightText(new Markup("[dim]Ctrl+F Search[/]"))
                .IsModal(true)
                .Padding(1)
                .Width(dialogWidth)
                .Height(dialogHeight)
                .Content(new DockLayout(
                    top: detailsGroup,
                    content: outputGroup,
                    bottom: null));
            dialog.AddCommand(new Command
            {
                Id = "ToolCallDialog.Close",
                LabelMarkup = "Close",
                DescriptionMarkup = "Close tool call details.",
                Gesture = new KeyGesture(TerminalKey.Escape),
                Importance = CommandImportance.Primary,
                Execute = _ => CloseToolCallDialog(entry),
            });
            dialog.KeyDown((_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    CloseToolCallDialog(entry);
                    e.Handled = true;
                }
            });

            entry.DetailDialog = dialog;
            entry.DetailMetadata = metadata;
            entry.DetailLog = log;
            entry.DetailStatsText = statsText;
            UpdateToolCallDialogVisual(entry);
            dialog.Show();
        });
    }

    private void UpdateToolCallDialogVisual(ToolCallEntryState entry)
    {
        if (entry.DetailDialog is null || entry.DetailMetadata is null || entry.DetailLog is null || entry.DetailStatsText is null)
        {
            return;
        }

        PostToUi(() =>
        {
            entry.DetailMetadata.Markdown = BuildToolCallDetailMarkdown(entry);
            entry.DetailStatsText.Text = BuildToolCallStatsMarkup(entry);
            entry.DetailLog.Clear();
            foreach (var line in SplitToolOutputLines(entry.OutputBuffer.ToString()))
            {
                entry.DetailLog.AppendLine(line);
            }

            entry.DetailLog.ScrollToTail();
        });
    }

    private void CloseToolCallDialogs(ThreadTabState tab)
    {
        foreach (var toolCall in tab.ToolCallStates.Values)
        {
            CloseToolCallDialog(toolCall);
        }
    }

    private void CloseToolCallDialog(ToolCallEntryState entry)
    {
        var dialog = entry.DetailDialog;
        entry.DetailDialog = null;
        entry.DetailMetadata = null;
        entry.DetailLog = null;
        entry.DetailStatsText = null;

        if (dialog is not null)
        {
            PostToUi(dialog.Close);
        }
    }

    private static bool IsToolTimelineActivity(AgentActivityEvent activity)
    {
        return activity.Kind is AgentActivityKind.ToolCall
            or AgentActivityKind.CommandExecution
            or AgentActivityKind.FileChange
            or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall
            or AgentActivityKind.CollabAgentToolCall
            or AgentActivityKind.Subagent
            or AgentActivityKind.Hook
            or AgentActivityKind.Skill
            or AgentActivityKind.WebSearch
            or AgentActivityKind.ImageGeneration;
    }

    private static bool IsToolTimelineContent(AgentContentKind kind)
    {
        return kind is AgentContentKind.CommandOutput
            or AgentContentKind.FileChangeOutput
            or AgentContentKind.ToolOutput;
    }

    private static AgentActivityKind MapToolActivityKind(AgentActivityKind kind) => kind;

    private static AgentActivityKind MapToolContentKind(AgentContentKind kind)
    {
        return kind switch
        {
            AgentContentKind.CommandOutput => AgentActivityKind.CommandExecution,
            AgentContentKind.FileChangeOutput => AgentActivityKind.FileChange,
            AgentContentKind.ToolOutput => AgentActivityKind.ToolCall,
            _ => AgentActivityKind.ToolCall,
        };
    }

    private static string ResolveToolCallId(ThreadTabState tab, string contentId, string? parentActivityId)
    {
        if (!string.IsNullOrWhiteSpace(contentId) && tab.ToolCallStates.ContainsKey(contentId))
        {
            return contentId;
        }

        if (!string.IsNullOrWhiteSpace(parentActivityId) && tab.ToolCallStates.ContainsKey(parentActivityId))
        {
            return parentActivityId;
        }

        return !string.IsNullOrWhiteSpace(contentId)
            ? contentId
            : parentActivityId ?? $"tool:{Guid.CreateVersion7()}";
    }

    private static ToolCallDisplayStatus ToToolCallDisplayStatus(AgentActivityPhase phase)
    {
        return phase switch
        {
            AgentActivityPhase.Requested => ToolCallDisplayStatus.Pending,
            AgentActivityPhase.Started or AgentActivityPhase.Progressed or AgentActivityPhase.Selected => ToolCallDisplayStatus.Running,
            AgentActivityPhase.Completed or AgentActivityPhase.Deselected => ToolCallDisplayStatus.Completed,
            AgentActivityPhase.Failed => ToolCallDisplayStatus.Failed,
            AgentActivityPhase.Canceled => ToolCallDisplayStatus.Canceled,
            _ => ToolCallDisplayStatus.Pending,
        };
    }

    private static bool IsCompletedStatus(ToolCallDisplayStatus status)
        => status is ToolCallDisplayStatus.Completed or ToolCallDisplayStatus.Failed or ToolCallDisplayStatus.Canceled;

    private static string BuildToolCallSummaryMarkup(ToolCallEntryState entry)
    {
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

    private static string BuildToolCallGroupSummaryMarkup(ToolCallGroupState group)
    {
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

    private static string BuildToolCallDetailMarkdown(ToolCallEntryState entry)
    {
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

    private static string BuildToolCallStatsMarkup(ToolCallEntryState entry)
    {
        var duration = (entry.CompletedAt ?? entry.LastUpdatedAt) - entry.FirstSeenAt;
        return $"[dim]{entry.OutputLineCount.ToString(CultureInfo.InvariantCulture)} lines · {FormatToolCallKilobytes(entry.OutputByteCount)} · {FormatToolCallDuration(duration)}[/]";
    }

    private static string GetToolStatusIconMarkup(ToolCallDisplayStatus status)
        => $"[{UiPalette.GetToolStatusMarkup(status)}]●[/]";

    private static string ResolveToolDisplayName(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.Kind == AgentActivityKind.CommandExecution &&
            !string.IsNullOrWhiteSpace(ResolveToolCommandText(activity)))
        {
            return ExtractCommandDisplayName(ResolveToolCommandText(activity)!);
        }

        if (activity.Kind == AgentActivityKind.ToolCall &&
            string.Equals(activity.Name, "shell_command", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(ResolveToolCommandText(activity)))
        {
            return ExtractCommandDisplayName(ResolveToolCommandText(activity)!);
        }

        if (activity.Kind == AgentActivityKind.FileChange)
        {
            return "file_change";
        }

        if (!string.IsNullOrWhiteSpace(activity.Name))
        {
            return activity.Kind == AgentActivityKind.Subagent && activity.Details is { } subagentDetails &&
                TryGetStringProperty(subagentDetails, "agentName", out var agentName)
                ? agentName!
                : activity.Name!;
        }

        if (activity.Details is { } details)
        {
            if (TryGetStringProperty(details, "toolName", out var toolName) ||
                TryGetStringProperty(details, "mcpToolName", out toolName) ||
                TryGetStringProperty(details, "tool", out toolName) ||
                TryGetStringProperty(details, "name", out toolName) ||
                TryGetStringProperty(details, "agentName", out toolName) ||
                TryGetStringProperty(details, "agentDisplayName", out toolName))
            {
                return toolName!;
            }

            if (activity.Kind == AgentActivityKind.ToolCall &&
                TryInferCopilotToolName(details, out var inferredToolName))
            {
                return inferredToolName!;
            }
        }

        return GetActivityKindLabel(activity.Kind);
    }

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

    private static string? ResolveToolCommandText(AgentActivityEvent activity)
    {
        if (activity.Details is { } details && TryGetStringProperty(details, "command", out var command))
        {
            return command;
        }

        if (activity.Details is { ValueKind: JsonValueKind.Object } detailObject)
        {
            if (detailObject.TryGetProperty("arguments", out var arguments) &&
                arguments.ValueKind == JsonValueKind.Object &&
                TryGetStringProperty(arguments, "command", out command))
            {
                return command;
            }

            if (detailObject.TryGetProperty("input", out var input) &&
                input.ValueKind == JsonValueKind.Object &&
                TryGetStringProperty(input, "command", out command))
            {
                return command;
            }
        }

        if (activity.Kind == AgentActivityKind.CommandExecution)
        {
            return activity.Name;
        }

        return null;
    }

    private static string? ResolveToolArgumentText(AgentActivityEvent activity)
    {
        if (activity.Details is not { } details || details.ValueKind != JsonValueKind.Object)
        {
            return activity.Kind is AgentActivityKind.WebSearch or AgentActivityKind.ImageGeneration
                ? activity.Message
                : null;
        }

        var parts = new List<string>();

        if (details.TryGetProperty("arguments", out var rawArguments))
        {
            parts.Add(FormatToolArguments(rawArguments));
        }

        if (TryGetStringProperty(details, "cwd", out var cwd))
        {
            parts.Add($"cwd: {cwd}");
        }

        if (TryGetStringProperty(details, "query", out var query))
        {
            parts.Add(query!);
        }

        if (TryGetStringProperty(details, "prompt", out var prompt))
        {
            parts.Add(prompt!);
        }

        if (TryGetStringProperty(details, "path", out var path))
        {
            parts.Add(path!);
        }

        if (TryGetStringProperty(details, "server", out var server) ||
            TryGetStringProperty(details, "mcpServerName", out server))
        {
            parts.Add($"server: {server}");
        }

        if (TryGetStringProperty(details, "agentDescription", out var agentDescription))
        {
            parts.Add(agentDescription!);
        }

        if (TryResolveNestedString(details, out var detailedContent, "result", "detailedContent") &&
            detailedContent is not null &&
            TryExtractDiffPath(detailedContent, out var diffPath))
        {
            parts.Add(diffPath!);
        }

        if (TryResolveNestedString(details, out var outputContent, "result", "content") &&
            outputContent is not null)
        {
            if (TryExtractFirstPath(outputContent, out var pathFromOutput))
            {
                parts.Add(pathFromOutput!);
            }
            else if (TryExtractCommonDirectory(outputContent, out var commonDirectory))
            {
                parts.Add(commonDirectory!);
            }
        }

        if (parts.Count == 0 && activity.Kind is AgentActivityKind.WebSearch or AgentActivityKind.ImageGeneration)
        {
            parts.Add(activity.Message ?? string.Empty);
        }

        if (parts.Count == 0 &&
            activity.Kind == AgentActivityKind.ToolCall &&
            TryResolveNestedString(details, out var copilotResultDetail, "result", "detailedContent"))
        {
            parts.Add(copilotResultDetail!);
        }

        return parts.Count == 0
            ? null
            : string.Join(
                $"{Environment.NewLine}{Environment.NewLine}",
                parts
                    .Where(static part => !string.IsNullOrWhiteSpace(part))
                    .Distinct(StringComparer.Ordinal));
    }

    private static string? ResolveToolOutput(AgentActivityEvent activity)
    {
        if (activity.Details is { } details)
        {
            if (TryGetStringProperty(details, "aggregatedOutput", out var aggregatedOutput))
            {
                return aggregatedOutput;
            }

            if (TryResolveNestedString(details, out var nestedOutput, "result", "content") ||
                TryResolveNestedString(details, out nestedOutput, "error", "message") ||
                TryResolveNestedString(details, out nestedOutput, "output", "body"))
            {
                return nestedOutput;
            }

            if (details.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("detailedContent", out var detailedContent))
            {
                return detailedContent.ValueKind == JsonValueKind.String
                    ? detailedContent.GetString()
                    : detailedContent.GetRawText();
            }
        }

        if (activity.Kind == AgentActivityKind.CommandExecution ||
            activity.Phase is AgentActivityPhase.Completed or AgentActivityPhase.Failed)
        {
            return activity.Message;
        }

        return null;
    }

    private static bool TryResolveNestedString(JsonElement root, out string? value, params string[] path)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        value = current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : current.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                ? current.GetRawText()
                : current.ToString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryInferCopilotToolName(JsonElement details, out string? toolName)
    {
        toolName = null;
        if (details.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryResolveNestedString(details, out var content, "result", "content") &&
            content is not null)
        {
            if (string.Equals(content, "Intent logged", StringComparison.Ordinal))
            {
                toolName = "report_intent";
                return true;
            }

            if (LooksLikeReadOutput(content))
            {
                toolName = "read";
                return true;
            }

            if (LooksLikePathList(content))
            {
                toolName = "glob";
                return true;
            }
        }

        if (TryResolveNestedString(details, out var detailedContent, "result", "detailedContent") &&
            detailedContent is not null &&
            (TryExtractDiffPath(detailedContent, out _) || detailedContent.Contains("diff --git", StringComparison.Ordinal)))
        {
            toolName = "read";
            return true;
        }

        return false;
    }

    private static string ExtractCommandDisplayName(string commandText)
    {
        var command = NormalizeToolOutput(commandText).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return "command";
        }

        var firstToken = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstToken)
            ? command
            : firstToken;
    }

    private static string? BuildCompactToolContext(ToolCallEntryState entry)
    {
        if (entry.Details is { } details &&
            TryBuildCompactContextFromDetails(details, out var detailPreview))
        {
            return detailPreview;
        }

        if (TryBuildCompactContextPreview(entry.ArgumentText, out var argumentPreview))
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

    private static string PreferToolDisplayName(string? existing, string candidate, AgentActivityEvent activity)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return existing ?? ResolveToolDisplayName(activity.Kind, null);
        }

        if (string.IsNullOrWhiteSpace(existing))
        {
            return candidate;
        }

        if (HasExplicitToolDisplayName(activity.Details))
        {
            return candidate;
        }

        if (IsGenericToolDisplayName(existing, activity.Kind) || string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        return existing;
    }

    private static bool HasExplicitToolDisplayName(JsonElement? details)
    {
        return details is { ValueKind: JsonValueKind.Object } element &&
               (TryGetStringProperty(element, "toolName", out _) ||
                TryGetStringProperty(element, "mcpToolName", out _) ||
                TryGetStringProperty(element, "tool", out _) ||
                TryGetStringProperty(element, "name", out _) ||
                TryGetStringProperty(element, "agentName", out _) ||
                TryGetStringProperty(element, "agentDisplayName", out _));
    }

    private static bool IsGenericToolDisplayName(string candidate, AgentActivityKind kind)
        => string.Equals(candidate, ResolveToolDisplayName(kind, null), StringComparison.OrdinalIgnoreCase);

    private static string FormatToolArguments(JsonElement arguments)
    {
        return arguments.ValueKind switch
        {
            JsonValueKind.String => arguments.GetString() ?? string.Empty,
            JsonValueKind.Object or JsonValueKind.Array => PrettyPrintJson(arguments),
            _ => arguments.ToString(),
        };
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

    private static string PrettyPrintJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
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

    private static bool LooksLikeReadOutput(string text)
    {
        var lines = SplitToolOutputLines(text);
        if (lines.Count == 0)
        {
            return false;
        }

        var numberedLines = lines.Count(static line =>
        {
            var trimmed = line.TrimStart();
            var dotIndex = trimmed.IndexOf('.');
            return dotIndex > 0 && int.TryParse(trimmed[..dotIndex], out _);
        });

        return numberedLines >= Math.Min(3, lines.Count);
    }

    private static bool LooksLikePathList(string text)
    {
        var lines = SplitToolOutputLines(text)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Take(12)
            .ToArray();
        if (lines.Length < 2)
        {
            return false;
        }

        var matchingPathLines = lines.Count(static line =>
        {
            var trimmed = line.Trim().Trim('"', '\'', '`');
            if (trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Contains('/', StringComparison.Ordinal))
            {
                return true;
            }

            if (trimmed.Contains(" ", StringComparison.Ordinal))
            {
                return false;
            }

            return trimmed.StartsWith('.') ||
                   !string.IsNullOrWhiteSpace(Path.GetExtension(trimmed));
        });

        return matchingPathLines >= Math.Max(2, lines.Length - 1);
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

    private static bool TryExtractFirstPath(string text, out string? path)
    {
        path = null;
        foreach (var line in SplitToolOutputLines(text))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.Contains(':', StringComparison.Ordinal) ||
                trimmed.Contains('\\', StringComparison.Ordinal) ||
                trimmed.Contains('/', StringComparison.Ordinal))
            {
                path = trimmed;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractCommonDirectory(string text, out string? directory)
    {
        directory = null;
        var firstPath = SplitToolOutputLines(text)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line) && line.Contains('\\', StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(firstPath))
        {
            return false;
        }

        directory = Path.GetDirectoryName(firstPath);
        return !string.IsNullOrWhiteSpace(directory);
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

    private static string? PreferLongerText(string? existing, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(existing) || candidate.Length >= existing.Length)
        {
            return candidate;
        }

        return existing;
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

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return NormalizeToolOutput(text).Count(static ch => ch == '\n') + 1;
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
}
