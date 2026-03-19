using System.Text;
using CodeAlta.Agent;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

internal sealed class ToolCallPresenter
{
    private const int ToolCallDialogLogCapacity = 20000;

    private readonly DocumentFlow _flow;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<bool> _isAutoScrollEnabled;
    private readonly Action<DocumentFlowItem> _appendTimelineItem;
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Dictionary<string, ToolCallEntryState> _toolCalls = new(StringComparer.Ordinal);
    private ToolCallGroupState? _activeGroup;

    public ToolCallPresenter(
        DocumentFlow flow,
        IUiDispatcher uiDispatcher,
        Func<bool> isAutoScrollEnabled,
        Action<DocumentFlowItem> appendTimelineItem,
        Func<Rectangle?> getDialogBounds)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(isAutoScrollEnabled);
        ArgumentNullException.ThrowIfNull(appendTimelineItem);
        ArgumentNullException.ThrowIfNull(getDialogBounds);

        _flow = flow;
        _uiDispatcher = uiDispatcher;
        _isAutoScrollEnabled = isAutoScrollEnabled;
        _appendTimelineItem = appendTimelineItem;
        _getDialogBounds = getDialogBounds;
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
        entry.Status = ToDisplayStatus(activity.Phase);
        entry.DisplayName = ToolCallEventInterpreter.PreferToolDisplayName(entry.DisplayName, ToolCallEventInterpreter.ResolveToolDisplayName(activity), activity);
        entry.ParentToolCallId = PreferLongerText(entry.ParentToolCallId, activity.ParentActivityId);
        entry.StatusMessage = PreferLongerText(entry.StatusMessage, activity.Message);
        entry.CommandText = PreferLongerText(entry.CommandText, ToolCallEventInterpreter.ResolveToolCommandText(activity));
        entry.ArgumentText = PreferLongerText(entry.ArgumentText, ToolCallEventInterpreter.ResolveToolArgumentText(activity));
        entry.ArgumentPreview = ToolCallEventInterpreter.BuildToolPreview(entry.ArgumentText);
        entry.Details = activity.Details;
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

        var entry = GetOrCreateToolCallEntry(ResolveToolCallId(delta.ContentId, delta.ParentActivityId), delta.Timestamp, MapContentKind(delta.Kind));
        if (!string.IsNullOrEmpty(delta.Delta))
        {
            entry.OutputBuffer.Append(delta.Delta);
        }

        entry.LastUpdatedAt = delta.Timestamp;
        if (entry.Status == ToolCallDisplayStatus.Pending)
        {
            entry.Status = ToolCallDisplayStatus.Running;
        }

        RefreshOutputState(entry);
        return true;
    }

    public bool TryHandleContent(AgentContentCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        if (!ToolCallEventInterpreter.IsToolTimelineContent(completed.Kind))
        {
            return false;
        }

        var entry = GetOrCreateToolCallEntry(ResolveToolCallId(completed.ContentId, completed.ParentActivityId), completed.Timestamp, MapContentKind(completed.Kind));
        ReplaceOutput(entry, completed.Content, completed.Timestamp);
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
        var entry = RunOnUiThread(
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
                button.SetStyle(ButtonStyle.Key, CodeAltaApp.GetToolChipButtonStyle(ToolCallDisplayStatus.Pending));
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
        _toolCalls[toolCallId] = entry;
        PostToUi(() =>
        {
            group.ItemsHost.Children.Add(entry.Button);
            _flow.ScrollToTailIfEnabled(_isAutoScrollEnabled());
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

        var group = RunOnUiThread(
            static timestampValue =>
            {
                var headerText = new Markup($"[{CodeAltaApp.MutedMarkup}]{NerdFont.CodTools}[/] [bold]Tool Calls[/]");
                var summaryText = new Markup("[dim]Waiting for tool activity...[/]");
                var timestampText = new Markup(string.Empty);
                var itemsHost = new WrapHStack { Spacing = 1, RunSpacing = 0, HorizontalAlignment = Align.Stretch, VerticalAlignment = Align.Start };
                var card = new Group(headerText, itemsHost)
                    .TopRightText(summaryText)
                    .BottomRightText(timestampText)
                    .Style(CodeAltaApp.GetToolCallGroupStyle())
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

    private void RefreshOutputState(ToolCallEntryState entry)
    {
        var output = entry.OutputBuffer.ToString();
        entry.OutputLineCount = ToolCallEventInterpreter.CountLines(output);
        entry.OutputByteCount = Encoding.UTF8.GetByteCount(output);
        entry.OutputPreview = ToolCallEventInterpreter.BuildToolPreview(output);
        UpdateEntryVisual(entry);
        UpdateGroupVisual(entry.Group);
        UpdateDialogVisual(entry);
    }

    private void ReplaceOutput(ToolCallEntryState entry, string? output, DateTimeOffset timestamp, bool updateStatusOnly = false)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            var normalizedOutput = ToolCallEventInterpreter.NormalizeToolOutput(output);
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

        RefreshOutputState(entry);
    }

    private void UpdateEntryVisual(ToolCallEntryState entry)
    {
        PostToUi(() =>
        {
            entry.SummaryText.Text = ToolCallSummaryFormatter.BuildSummaryMarkup(entry);
            entry.Button.Tone = ControlTone.Default;
            entry.Button.SetStyle(ButtonStyle.Key, CodeAltaApp.GetToolChipButtonStyle(entry.Status));
        });
    }

    private void UpdateGroupVisual(ToolCallGroupState? group)
    {
        if (group is null)
        {
            return;
        }

        PostToUi(() =>
        {
            group.SummaryText.Text = ToolCallSummaryFormatter.BuildGroupSummaryMarkup(group);
            ChatTimelineVisualFactory.ApplyTimestamp(group.TimestampText, group.LastUpdatedAt);
        });
    }

    private void OpenDialog(ToolCallEntryState entry)
    {
        PostToUi(() =>
        {
            if (entry.DetailDialog is { App: not null })
            {
                return;
            }

            var metadata = new MarkdownControl(string.Empty) { HorizontalAlignment = Align.Stretch, VerticalAlignment = Align.Start, Options = XenoAtom.Terminal.UI.Extensions.Markdown.MarkdownRenderOptions.Default with { WrapCodeBlocks = true, MaxCodeBlockHeight = 5 } };
            var wrapText = new State<bool>(true);
            var log = new LogControl { MaxCapacity = ToolCallDialogLogCapacity, HorizontalAlignment = Align.Stretch, VerticalAlignment = Align.Stretch }.WrapText(wrapText);
            var statsText = new Markup(string.Empty);
            var detailsGroup = new Group("Details", metadata).Padding(new Thickness(1, 0, 1, 0)).HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Start);
            var outputGroup = new Group("Output", log).TopRightText(new CheckBox("Wrap").IsChecked(wrapText)).Padding(0).HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Stretch);
            var bounds = _getDialogBounds() ?? entry.Button.GetAbsoluteBounds();
            var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close")) { HorizontalAlignment = Align.End, VerticalAlignment = Align.Start, Tone = ControlTone.Error };
            closeButton.Click(() => CloseDialog(entry));
            var dialog = new Dialog()
                .Title(ToolCallEventInterpreter.ResolveToolDisplayName(entry.ActivityKind, entry.DisplayName))
                .TopRightText(closeButton)
                .BottomLeftText(statsText)
                .BottomRightText(new Markup("[dim]Ctrl+F Search[/]"))
                .IsModal(true)
                .Padding(1)
                .Width(Math.Max(56, (int)Math.Round(bounds.Width * 0.8, MidpointRounding.AwayFromZero)))
                .Height(Math.Max(16, (int)Math.Round(bounds.Height * 0.8, MidpointRounding.AwayFromZero)))
                .Content(new Grid().Rows(new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Star(1) }).Columns(new ColumnDefinition { Width = GridLength.Star(1) }).Cell(detailsGroup, 0, 0).Cell(outputGroup, 1, 0));
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

        PostToUi(() =>
        {
            entry.DetailMetadata.Markdown = ToolCallSummaryFormatter.BuildDetailMarkdown(entry);
            entry.DetailStatsText.Text = ToolCallSummaryFormatter.BuildStatsMarkup(entry);
            entry.DetailLog.Clear();
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
            PostToUi(dialog.Close);
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

    private void PostToUi(Action action)
    {
        if (_uiDispatcher.CheckAccess())
        {
            action();
            return;
        }

        _uiDispatcher.Post(action);
    }

    private T RunOnUiThread<T>(Func<T> action)
        => _uiDispatcher.CheckAccess() ? action() : _uiDispatcher.InvokeAsync(action).GetAwaiter().GetResult();

    private T RunOnUiThread<TState, T>(Func<TState, T> action, TState state)
        => _uiDispatcher.CheckAccess() ? action(state) : _uiDispatcher.InvokeAsync(() => action(state)).GetAwaiter().GetResult();
}
