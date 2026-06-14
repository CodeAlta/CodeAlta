using CodeAlta.Threading;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Models;
using CodeAlta.Presentation.Formatting;
using CodeAlta.Presentation.Styling;
using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Presentation.Timeline;

internal sealed class ToolCallPresenter
{
    private const int ToolCallDialogLogCapacity = 20000;

    private readonly IUiDispatcher _uiDispatcher;
    private readonly Action<DocumentFlowItem> _appendTimelineItem;
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Dictionary<string, ToolCallEntryState> _toolCalls = new(StringComparer.Ordinal);
    private ToolCallGroupState? _activeGroup;
    private string? _localFileRootPath;

    public ToolCallPresenter(
        DocumentFlow flow,
        IUiDispatcher uiDispatcher,
        Action<DocumentFlowItem> appendTimelineItem,
        Func<Rectangle?> getDialogBounds,
        string? localFileRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(appendTimelineItem);
        ArgumentNullException.ThrowIfNull(getDialogBounds);

        _uiDispatcher = uiDispatcher;
        _appendTimelineItem = appendTimelineItem;
        _getDialogBounds = getDialogBounds;
        _localFileRootPath = localFileRootPath;
    }

    public void SetLocalFileRootPath(string? localFileRootPath)
    {
        if (string.Equals(_localFileRootPath, localFileRootPath, StringComparison.Ordinal))
        {
            return;
        }

        _localFileRootPath = localFileRootPath;
        UiDispatch.Post(_uiDispatcher, () =>
        {
            foreach (var entry in _toolCalls.Values)
            {
                if (entry.DetailMetadata is { } metadata)
                {
                    ChatTimelineVisualFactory.ApplyLocalFileRootPath(metadata, _localFileRootPath);
                }
            }
        });
    }

    public bool TryHandleActivity(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (!ToolCallEventInterpreter.IsToolTimelineActivity(activity))
        {
            return false;
        }

        var entry = GetOrCreateToolCallEntry(activity.ActivityId, activity.Timestamp, activity.Kind);
        entry.ActivityKind = PreferActivityKind(entry.ActivityKind, activity.Kind);
        UpdateStatus(entry, ToDisplayStatus(activity.Phase));
        entry.DisplayName = ToolCallEventInterpreter.PreferToolDisplayName(entry.DisplayName, ToolCallEventInterpreter.ResolveToolDisplayName(activity), activity);
        entry.ParentToolCallId = PreferLongerText(entry.ParentToolCallId, activity.ParentActivityId);
        entry.StatusMessage = PreferLongerText(entry.StatusMessage, activity.Message);
        entry.CommandText = PreferLongerText(entry.CommandText, ToolCallEventInterpreter.ResolveToolCommandText(activity));
        entry.ArgumentText = PreferLongerText(entry.ArgumentText, ToolCallEventInterpreter.ResolveToolArgumentText(activity));
        entry.ArgumentPreview = ToolCallEventInterpreter.BuildToolPreview(entry.ArgumentText);
        entry.Details = activity.Details;
        entry.DiffText = PreferLongerText(entry.DiffText, ToolCallEventInterpreter.ResolveToolDiff(activity.Details));
        entry.FirstSeenAt = entry.FirstSeenAt == default ? activity.Timestamp : entry.FirstSeenAt;
        entry.LastUpdatedAt = activity.Timestamp;
        entry.CompletedAt = IsCompletedStatus(entry.Status) ? activity.Timestamp : null;

        var output = ToolCallEventInterpreter.ResolveToolOutput(activity);
        if (!string.IsNullOrWhiteSpace(output))
        {
            ReplaceOutput(entry, output, activity.Timestamp, updateStatusOnly: true);
        }

        if (ToolCallEventInterpreter.IsRedundantStatusDetail(entry.StatusMessage, entry.OutputBuffer.ToString()))
        {
            entry.StatusMessage = null;
        }

        UpdateEntryVisual(entry);
        UpdateGroupVisual(entry.Group);
        return true;
    }

    public bool TryHandleContent(AgentContentDeltaEvent delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        if (!ToolCallEventInterpreter.IsToolTimelineContent(delta.Kind))
        {
            return false;
        }

        var toolCallId = ResolveToolCallId(delta.ContentId, delta.ParentActivityId);
        var isNewEntry = !_toolCalls.ContainsKey(toolCallId);
        var entry = GetOrCreateToolCallEntry(toolCallId, delta.Timestamp, MapContentKind(delta.Kind));
        var refreshGroupSummary = isNewEntry || entry.Status == ToolCallDisplayStatus.Pending;
        if (!string.IsNullOrEmpty(delta.Delta))
        {
            AppendOutputDelta(entry, delta.Delta);
        }

        entry.DiffText = PreferLongerText(entry.DiffText, ToolCallEventInterpreter.ResolveToolDiff(delta.Details));
        entry.LastUpdatedAt = delta.Timestamp;
        if (entry.Status == ToolCallDisplayStatus.Pending)
        {
            UpdateStatus(entry, ToolCallDisplayStatus.Running);
        }

        UpdateEntryVisual(entry);
        UpdateGroupVisual(entry.Group, refreshSummary: refreshGroupSummary);
        UpdateDialogVisual(entry);
        return true;
    }

    public bool TryHandleContent(AgentContentCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        if (!ToolCallEventInterpreter.IsToolTimelineContent(completed.Kind))
        {
            return false;
        }

        var toolCallId = ResolveToolCallId(completed.ContentId, completed.ParentActivityId);
        var isNewEntry = !_toolCalls.ContainsKey(toolCallId);
        var entry = GetOrCreateToolCallEntry(toolCallId, completed.Timestamp, MapContentKind(completed.Kind));
        var refreshGroupSummary = isNewEntry || entry.Status == ToolCallDisplayStatus.Pending;
        ReplaceOutput(entry, completed.Content, completed.Timestamp);
        entry.DiffText = PreferLongerText(entry.DiffText, ToolCallEventInterpreter.ResolveToolDiff(completed.Details));
        UpdateEntryVisual(entry);
        UpdateGroupVisual(entry.Group, refreshSummary: refreshGroupSummary);
        UpdateDialogVisual(entry);
        return true;
    }

    public void OnNonToolTimelineItemAppended()
        => _activeGroup = null;

    public void Reset()
    {
        foreach (var toolCall in _toolCalls.Values)
        {
            CloseDialog(toolCall);
        }

        _toolCalls.Clear();
        _activeGroup = null;
    }

    private ToolCallEntryState GetOrCreateToolCallEntry(string toolCallId, DateTimeOffset timestamp, AgentActivityKind activityKind)
    {
        if (_toolCalls.TryGetValue(toolCallId, out var existing))
        {
            existing.LastUpdatedAt = timestamp;
            existing.FirstSeenAt = existing.FirstSeenAt == default ? timestamp : existing.FirstSeenAt;
            return existing;
        }

        var group = GetOrCreateGroup(timestamp);
        var entry = UiDispatch.Invoke(
            _uiDispatcher,
            static state =>
            {
                var summaryText = new Markup(string.Empty) { Wrap = false };
                var button = new Button(summaryText)
                {
                    MinWidth = 16,
                    MaxWidth = 28,
                    HorizontalAlignment = Align.Start,
                    VerticalAlignment = Align.Start,
                };
                button.SetStyle(ButtonStyle.Key, () => UiPalette.GetToolChipButtonStyle(button.GetTheme(), ToolCallDisplayStatus.Pending));
                return new ToolCallEntryState(state.toolCallId, button, summaryText)
                {
                    Group = state.group,
                    ActivityKind = state.activityKind,
                    DisplayName = ToolCallSummaryFormatter.GetActivityKindLabel(state.activityKind),
                    FirstSeenAt = state.timestamp,
                    LastUpdatedAt = state.timestamp,
                };
            },
            (toolCallId, group, activityKind, timestamp));

        entry.Button.Click(() => OpenDialog(entry));
        group.ToolCalls[toolCallId] = entry;
        AdjustGroupStatusCount(group, entry.Status, 1);
        _toolCalls[toolCallId] = entry;
        UiDispatch.Post(_uiDispatcher, () =>
        {
            group.ItemsHost.Children.Add(entry.Button);
        });
        return entry;
    }

    private ToolCallGroupState GetOrCreateGroup(DateTimeOffset timestamp)
    {
        if (_activeGroup is { } existing)
        {
            existing.LastUpdatedAt = timestamp;
            return existing;
        }

        var group = UiDispatch.Invoke(
            _uiDispatcher,
            static timestampValue =>
            {
                var headerText = new Markup($"[{UiPalette.MutedMarkup}]{TerminalIcons.CodTools}[/] [bold]Tool Calls[/]");
                var summaryText = new Markup("[dim]Waiting for tool activity...[/]");
                var timestampText = new Markup(string.Empty);
                var itemsHost = new WrapHStack { Spacing = 1, RunSpacing = 0 };
                Group? card = null;
                card = new Group(headerText, itemsHost)
                    .TopRightText(summaryText)
                    .BottomRightText(timestampText)
                    .Style(() => UiPalette.GetToolCallGroupStyle(card!.GetTheme()))
                    .HorizontalAlignment(Align.Stretch)
                    .VerticalAlignment(Align.Start);
                var item = new DocumentFlowItem { Content = new FlowDocument().Add(card), Alignment = DocumentFlowAlignment.Stretch };
                return new ToolCallGroupState(item, itemsHost, headerText, summaryText, timestampText) { LastUpdatedAt = timestampValue };
            },
            timestamp);

        _activeGroup = group;
        _appendTimelineItem(group.Item);
        ChatTimelineVisualFactory.ApplyTimestamp(group.TimestampText, timestamp);
        UpdateGroupVisual(group);
        return group;
    }

    private void ReplaceOutput(ToolCallEntryState entry, string? output, DateTimeOffset timestamp, bool updateStatusOnly = false)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            var normalizedOutput = ToolCallEventInterpreter.NormalizeToolOutput(output);
            if (normalizedOutput.Length >= entry.OutputBuffer.Length || entry.OutputBuffer.Length == 0)
            {
                ResetOutputState(entry, normalizedOutput);
            }
        }

        entry.LastUpdatedAt = timestamp;
        if (!updateStatusOnly && entry.Status == ToolCallDisplayStatus.Pending)
        {
            UpdateStatus(entry, ToolCallDisplayStatus.Running);
        }
    }

    private void UpdateEntryVisual(ToolCallEntryState entry)
    {
        UiDispatch.Post(_uiDispatcher, () =>
        {
            entry.SummaryText.Text = ToolCallSummaryFormatter.BuildSummaryMarkup(entry);
            entry.Button.Tone = ControlTone.Default;
            entry.Button.SetStyle(ButtonStyle.Key, () => UiPalette.GetToolChipButtonStyle(entry.Button.GetTheme(), entry.Status));
        });
    }

    private void UpdateGroupVisual(ToolCallGroupState? group, bool refreshSummary = true)
    {
        if (group is null)
        {
            return;
        }

        UiDispatch.Post(_uiDispatcher, () =>
        {
            if (refreshSummary)
            {
                group.SummaryText.Text = ToolCallSummaryFormatter.BuildGroupSummaryMarkup(group);
            }

            ChatTimelineVisualFactory.ApplyTimestamp(group.TimestampText, group.LastUpdatedAt);
        });
    }

    private void OpenDialog(ToolCallEntryState entry)
    {
        UiDispatch.Post(_uiDispatcher, () =>
        {
            if (entry.DetailDialog is { App: not null })
            {
                return;
            }

            var metadata = new MarkdownControl(string.Empty) { HorizontalAlignment = Align.Stretch, VerticalAlignment = Align.Start, Options = ChatTimelineVisualFactory.CreateSessionMarkdownOptions(5, _localFileRootPath) };
            var wrapText = new State<bool>(true);
            var log = new LogControl { MaxCapacity = ToolCallDialogLogCapacity, HorizontalAlignment = Align.Stretch, VerticalAlignment = Align.Stretch }.WrapText(wrapText);
            var statsText = new Markup(string.Empty);
            var detailsGroup = new Group("Details", metadata).Padding(new Thickness(1, 0, 1, 0)).HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Start);
            var outputGroup = new Group("Diff / Output", log).TopRightText(new CheckBox("Wrap").IsChecked(wrapText)).Padding(0).HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Stretch);
            var bounds = _getDialogBounds() ?? entry.Button.GetAbsoluteBounds();
            var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} Close")) { HorizontalAlignment = Align.End, VerticalAlignment = Align.Start, Tone = ControlTone.Error };
            closeButton.Click(() => CloseDialog(entry));
            var dialog = new Dialog()
                .Title(ToolCallEventInterpreter.ResolveToolDisplayName(entry.ActivityKind, entry.DisplayName))
                .TopRightText(closeButton)
                .BottomLeftText(statsText)
                .BottomRightText(new Markup("[dim]Ctrl+F Search[/]"))
                .IsModal(true)
                .Padding(1)
                .Content(new Grid().Rows(new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Star(1) }).Columns(new ColumnDefinition { Width = GridLength.Star(1) }).Cell(detailsGroup, 0, 0).Cell(outputGroup, 1, 0));
            ResponsiveDialogSize.Apply(dialog, bounds, minWidth: 56, minHeight: 16);
            dialog.AddCommand(new Command { Id = "ToolCallDialog.Close", LabelMarkup = "Close", DescriptionMarkup = "Close tool call details.", Gesture = new KeyGesture(TerminalKey.Escape), Importance = CommandImportance.Primary, Execute = _ => CloseDialog(entry) });
            dialog.KeyDown((_, e) => { if (e.Key == TerminalKey.Escape) { CloseDialog(entry); e.Handled = true; } });
            entry.DetailDialog = dialog;
            entry.DetailMetadata = metadata;
            entry.DetailLog = log;
            entry.DetailStatsText = statsText;
            UpdateDialogVisual(entry);
            dialog.Show();
        });
    }

    private void UpdateDialogVisual(ToolCallEntryState entry)
    {
        if (entry.DetailDialog is null || entry.DetailMetadata is null || entry.DetailLog is null || entry.DetailStatsText is null)
        {
            return;
        }

        UiDispatch.Post(_uiDispatcher, () =>
        {
            entry.DetailMetadata.Markdown = ToolCallSummaryFormatter.BuildDetailMarkdown(entry);
            entry.DetailStatsText.Text = ToolCallSummaryFormatter.BuildStatsMarkup(entry);
            entry.DetailLog.Clear();
            if (!string.IsNullOrWhiteSpace(entry.DiffText))
            {
                entry.DetailLog.AppendMarkupLine($"[{UiPalette.MutedMarkup}]Diff[/]");
                foreach (var line in ToolCallEventInterpreter.SplitToolOutputLines(entry.DiffText!))
                {
                    entry.DetailLog.AppendMarkupLine(FileChangeSummaryFormatter.GetDiffLineMarkup(line));
                }

                if (entry.OutputBuffer.Length > 0)
                {
                    entry.DetailLog.AppendLine(string.Empty);
                    entry.DetailLog.AppendMarkupLine($"[{UiPalette.MutedMarkup}]Output[/]");
                }
            }

            foreach (var line in ToolCallEventInterpreter.SplitToolOutputLines(entry.OutputBuffer.ToString()))
            {
                entry.DetailLog.AppendLine(line);
            }

            entry.DetailLog.ScrollToTail();
        });
    }

    private void CloseDialog(ToolCallEntryState entry)
    {
        var dialog = entry.DetailDialog;
        entry.DetailDialog = null;
        entry.DetailMetadata = null;
        entry.DetailLog = null;
        entry.DetailStatsText = null;
        if (dialog is not null)
        {
            UiDispatch.Post(_uiDispatcher, dialog.Close);
        }
    }

    private static void ResetOutputState(ToolCallEntryState entry, string output)
    {
        entry.OutputBuffer.Clear();
        entry.OutputBuffer.Append(output);
        entry.CurrentOutputLineBuffer.Clear();
        entry.OutputLineCount = 0;
        entry.OutputByteCount = 0;
        entry.OutputNewlineCount = 0;
        entry.OutputTrailingNewlineCount = 0;
        entry.OutputNonNewlineCharacterCount = 0;
        entry.OutputPreview = null;
        entry.SkipLeadingLineFeed = false;
        ApplyOutputDeltaState(entry, output);
    }

    private static void AppendOutputDelta(ToolCallEntryState entry, string delta)
    {
        var normalizedDelta = NormalizeOutputDelta(entry, delta);
        if (normalizedDelta.Length == 0)
        {
            return;
        }

        entry.OutputBuffer.Append(normalizedDelta);
        ApplyOutputDeltaState(entry, normalizedDelta);
    }

    private static string NormalizeOutputDelta(ToolCallEntryState entry, string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(delta.Length);
        var skipLeadingLineFeed = entry.SkipLeadingLineFeed;
        entry.SkipLeadingLineFeed = false;

        for (var index = 0; index < delta.Length; index++)
        {
            var ch = delta[index];
            if (skipLeadingLineFeed)
            {
                skipLeadingLineFeed = false;
                if (ch == '\n')
                {
                    continue;
                }
            }

            if (ch == '\r')
            {
                builder.Append('\n');
                if (index + 1 < delta.Length)
                {
                    if (delta[index + 1] == '\n')
                    {
                        index++;
                    }
                }
                else
                {
                    entry.SkipLeadingLineFeed = true;
                }

                continue;
            }

            builder.Append(ch);
        }

        if (skipLeadingLineFeed)
        {
            entry.SkipLeadingLineFeed = true;
        }

        return builder.ToString();
    }

    private static void ApplyOutputDeltaState(ToolCallEntryState entry, string delta)
    {
        entry.OutputByteCount += Encoding.UTF8.GetByteCount(delta);

        foreach (var ch in delta)
        {
            if (ch == '\n')
            {
                entry.OutputNewlineCount++;
                entry.OutputTrailingNewlineCount++;
                UpdateOutputPreviewFromLine(entry);
                entry.CurrentOutputLineBuffer.Clear();
                continue;
            }

            entry.OutputNonNewlineCharacterCount++;
            entry.OutputTrailingNewlineCount = 0;
            entry.CurrentOutputLineBuffer.Append(ch);
        }

        UpdateOutputPreviewFromLine(entry);
        entry.OutputLineCount = entry.OutputNonNewlineCharacterCount == 0
            ? 0
            : entry.OutputNewlineCount - entry.OutputTrailingNewlineCount + 1;
    }

    private static void UpdateOutputPreviewFromLine(ToolCallEntryState entry)
    {
        if (entry.CurrentOutputLineBuffer.Length == 0)
        {
            return;
        }

        var preview = ToolCallEventInterpreter.BuildToolPreview(entry.CurrentOutputLineBuffer.ToString());
        if (!string.IsNullOrWhiteSpace(preview))
        {
            entry.OutputPreview = preview;
        }
    }

    private string ResolveToolCallId(string contentId, string? parentActivityId)
    {
        if (!string.IsNullOrWhiteSpace(contentId) && _toolCalls.ContainsKey(contentId))
        {
            return contentId;
        }

        if (!string.IsNullOrWhiteSpace(parentActivityId) && _toolCalls.ContainsKey(parentActivityId))
        {
            return parentActivityId;
        }

        return !string.IsNullOrWhiteSpace(contentId) ? contentId : parentActivityId ?? $"tool:{Guid.CreateVersion7()}";
    }

    private static AgentActivityKind MapContentKind(AgentContentKind kind)
        => kind switch
        {
            AgentContentKind.CommandOutput => AgentActivityKind.CommandExecution,
            AgentContentKind.FileChangeOutput => AgentActivityKind.FileChange,
            _ => AgentActivityKind.ToolCall,
        };

    private static ToolCallDisplayStatus ToDisplayStatus(AgentActivityPhase phase)
        => phase switch
        {
            AgentActivityPhase.Requested => ToolCallDisplayStatus.Pending,
            AgentActivityPhase.Started or AgentActivityPhase.Progressed or AgentActivityPhase.Selected => ToolCallDisplayStatus.Running,
            AgentActivityPhase.Completed or AgentActivityPhase.Deselected => ToolCallDisplayStatus.Completed,
            AgentActivityPhase.Failed => ToolCallDisplayStatus.Failed,
            AgentActivityPhase.Canceled => ToolCallDisplayStatus.Canceled,
            _ => ToolCallDisplayStatus.Pending,
        };

    private static AgentActivityKind PreferActivityKind(AgentActivityKind existing, AgentActivityKind candidate)
        => candidate == AgentActivityKind.ToolCall &&
           existing is AgentActivityKind.CommandExecution or AgentActivityKind.FileChange or AgentActivityKind.McpToolCall or AgentActivityKind.WebSearch or AgentActivityKind.ImageGeneration or AgentActivityKind.Subagent
            ? existing
            : candidate;

    private static bool IsCompletedStatus(ToolCallDisplayStatus status)
        => status is ToolCallDisplayStatus.Completed or ToolCallDisplayStatus.Failed or ToolCallDisplayStatus.Canceled;

    private static string? PreferLongerText(string? existing, string? candidate)
        => string.IsNullOrWhiteSpace(candidate) ? existing : string.IsNullOrWhiteSpace(existing) || candidate.Length >= existing.Length ? candidate : existing;

    private static void UpdateStatus(ToolCallEntryState entry, ToolCallDisplayStatus status)
    {
        if (entry.Status == status)
        {
            return;
        }

        if (entry.Group is { } group)
        {
            AdjustGroupStatusCount(group, entry.Status, -1);
            AdjustGroupStatusCount(group, status, 1);
        }

        entry.Status = status;
    }

    private static void AdjustGroupStatusCount(ToolCallGroupState group, ToolCallDisplayStatus status, int delta)
    {
        switch (status)
        {
            case ToolCallDisplayStatus.Pending:
                group.PendingCount += delta;
                break;
            case ToolCallDisplayStatus.Running:
                group.RunningCount += delta;
                break;
            case ToolCallDisplayStatus.Completed:
                group.CompletedCount += delta;
                break;
            case ToolCallDisplayStatus.Failed:
                group.FailedCount += delta;
                break;
            case ToolCallDisplayStatus.Canceled:
                group.CanceledCount += delta;
                break;
        }
    }

}
