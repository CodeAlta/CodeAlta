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

internal sealed partial class CodeAltaApp
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
            tab.Flow.ScrollToTailIfEnabled(tab.AutoScroll);
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
        AppendThreadTimelineItem(tab, group.Item, resetActiveToolCallGroup: false);

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
            entry.SummaryText.Text = ToolCallSummaryFormatter.BuildSummaryMarkup(entry);
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
            group.SummaryText.Text = ToolCallSummaryFormatter.BuildGroupSummaryMarkup(group);
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
            var dialogContentGrid = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) });
            dialogContentGrid.Cell(detailsGroup, 0, 0);
            dialogContentGrid.Cell(outputGroup, 1, 0);

            dialog = new Dialog()
                .Title($"{ResolveToolDisplayName(entry.ActivityKind, entry.DisplayName)}")
                .TopRightText(closeButton)
                .BottomLeftText(statsText)
                .BottomRightText(new Markup("[dim]Ctrl+F Search[/]"))
                .IsModal(true)
                .Padding(1)
                .Width(dialogWidth)
                .Height(dialogHeight)
                .Content(dialogContentGrid);
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
            entry.DetailMetadata.Markdown = ToolCallSummaryFormatter.BuildDetailMarkdown(entry);
            entry.DetailStatsText.Text = ToolCallSummaryFormatter.BuildStatsMarkup(entry);
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

        var tokens = TokenizeCommandDisplayName(command);
        if (tokens.Count == 0)
        {
            return "command";
        }

        var executable = NormalizeCommandToken(tokens[0]);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return "command";
        }

        if (!ShouldIncludeSubcommand(executable))
        {
            return executable;
        }

        var firstSubcommandIndex = FindDisplayableSubcommandIndex(tokens, startIndex: 1);
        if (firstSubcommandIndex < 0)
        {
            return executable;
        }

        var firstSubcommand = NormalizeCommandToken(tokens[firstSubcommandIndex]);
        if (string.IsNullOrWhiteSpace(firstSubcommand))
        {
            return executable;
        }

        var builder = new StringBuilder()
            .Append(executable)
            .Append(' ')
            .Append(firstSubcommand);

        if (ShouldIncludeSecondSubcommand(executable, firstSubcommand))
        {
            var secondSubcommandIndex = FindDisplayableSubcommandIndex(tokens, firstSubcommandIndex + 1);
            if (secondSubcommandIndex >= 0)
            {
                var secondSubcommand = NormalizeCommandToken(tokens[secondSubcommandIndex]);
                if (!string.IsNullOrWhiteSpace(secondSubcommand))
                {
                    builder.Append(' ').Append(secondSubcommand);
                }
            }
        }

        return builder.ToString();
    }

    private static List<string> TokenizeCommandDisplayName(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        foreach (var ch in command)
        {
            if (quote is { } activeQuote)
            {
                if (ch == activeQuote)
                {
                    quote = null;
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string NormalizeCommandToken(string token)
    {
        var trimmed = token.Trim().Trim('"', '\'', '`');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Contains('/', StringComparison.Ordinal))
        {
            trimmed = Path.GetFileName(trimmed);
        }

        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    private static int FindDisplayableSubcommandIndex(IReadOnlyList<string> tokens, int startIndex)
    {
        var skipNext = false;
        for (var index = startIndex; index < tokens.Count; index++)
        {
            var token = tokens[index].Trim();
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            if (IsOptionToken(token))
            {
                skipNext = true;
                continue;
            }

            if (!IsDisplayableSubcommandToken(token))
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private static bool ShouldIncludeSubcommand(string executable)
    {
        return executable switch
        {
            "dotnet" or "git" or "cargo" or "npm" or "pnpm" or "yarn" or "bun" or "uv" or "docker" or "kubectl" or "gh" or "brew" => true,
            _ => false,
        };
    }

    private static bool ShouldIncludeSecondSubcommand(string executable, string subcommand)
    {
        return executable switch
        {
            "dotnet" => subcommand is "tool" or "nuget" or "workload" or "package" or "reference" or "user-secrets",
            "git" => subcommand is "remote" or "branch" or "stash" or "worktree" or "submodule" or "config",
            "docker" => subcommand is "compose" or "image" or "container" or "builder" or "buildx" or "volume" or "network" or "system",
            _ => false,
        };
    }

    private static bool IsDisplayableSubcommandToken(string token)
    {
        var trimmed = token.Trim().Trim('"', '\'', '`');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed is "|" or "||" or "&&" or ";" or ">" or ">>" or "<")
        {
            return false;
        }

        if (trimmed.Contains('='))
        {
            return false;
        }

        if (trimmed.Contains('\\', StringComparison.Ordinal) ||
            trimmed.Contains('/', StringComparison.Ordinal) ||
            trimmed.StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        return !IsOptionToken(trimmed);
    }

    private static bool IsOptionToken(string token)
    {
        var trimmed = token.Trim();
        return trimmed.StartsWith("-", StringComparison.Ordinal) ||
               trimmed.StartsWith("/", StringComparison.Ordinal);
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

    private static string PrettyPrintJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
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

}
