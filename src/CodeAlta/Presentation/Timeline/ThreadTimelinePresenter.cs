using System.Text;
using CodeAlta.Agent;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

internal sealed class ThreadTimelinePresenter
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<bool> _isAutoScrollEnabled;
    private readonly Dictionary<string, ChatContentState> _contentStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _activityStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _interactionStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _planStates = new(StringComparer.Ordinal);
    private List<DocumentFlowItem>? _bufferedHistoryItems;
    private PendingAssistantState? _pendingAssistant;
    private TruncatedHistoryState? _truncatedHistory;
    private bool _hasSeenUserPrompt;

    public ThreadTimelinePresenter(
        IUiDispatcher uiDispatcher,
        Func<bool> isAutoScrollEnabled,
        Func<Rectangle?> getDialogBounds)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(isAutoScrollEnabled);
        ArgumentNullException.ThrowIfNull(getDialogBounds);

        _uiDispatcher = uiDispatcher;
        _isAutoScrollEnabled = isAutoScrollEnabled;
        Flow = UiDispatch.Invoke(
            _uiDispatcher,
            static () => new DocumentFlow
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
                ItemPadding = new Thickness(1, 0, 0, 0),
                ItemSpacing = 0,
            });
        ToolCalls = new ToolCallPresenter(
            Flow,
            _uiDispatcher,
            _isAutoScrollEnabled,
            item => AppendTimelineItem(item, resetActiveToolCallGroup: false),
            getDialogBounds);
    }

    public DocumentFlow Flow { get; }

    public ToolCallPresenter ToolCalls { get; }

    public void BeginBufferedHistoryLoad()
        => _bufferedHistoryItems = [];

    public void CompleteInitialBufferedHistory(DocumentFlowItem? truncatedHistoryItem)
    {
        if (_bufferedHistoryItems is { } bufferedHistoryItems)
        {
            _bufferedHistoryItems = BuildInitialThreadHistoryItems(bufferedHistoryItems, truncatedHistoryItem);
        }
    }

    public void ClearBufferedHistory()
        => _bufferedHistoryItems = null;

    public DocumentFlowItem CreateTruncatedHistoryItem(int omittedMessageCount, Action onLoad)
    {
        _truncatedHistory = CreateTruncatedHistoryState(omittedMessageCount, onLoad);
        return _truncatedHistory.Item;
    }

    public bool HasLoadableTruncatedHistory
        => _truncatedHistory is { CanLoad: true };

    public void ReplaceTruncatedHistoryLoadButton()
    {
        if (_truncatedHistory is not { CanLoad: true } truncatedHistory)
        {
            return;
        }

        truncatedHistory.CanLoad = false;
        UiDispatch.Post(
            _uiDispatcher,
            () =>
            {
                truncatedHistory.Rule.CenterLabel = new TextBlock(ChatTimelineVisualFactory.BuildTruncatedHistorySummaryText(truncatedHistory.OmittedMessageCount))
                {
                    Wrap = false,
                };
            });
    }

    public void AppendContent(AgentContentDeltaEvent delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        if (string.IsNullOrEmpty(delta.Delta))
        {
            return;
        }

        var state = GetOrCreateContentState(delta.Kind, delta.ContentId, delta.Timestamp);
        state.Buffer.Append(delta.Delta);
        var content = state.Buffer.ToString();
        var markdown = ChatMarkdownFormatter.FormatChatContentMarkdown(delta.Kind, content);
        var headerSecondary = ChatMarkdownFormatter.GetChatContentHeaderSecondary(delta.Kind, content);
        UiDispatch.Post(_uiDispatcher, () =>
        {
            ChatTimelineVisualFactory.ApplyHeader(
                state.HeaderText,
                ChatTimelineVisualFactory.GetContentTone(delta.Kind),
                ChatTimelineVisualFactory.GetContentHeader(delta.Kind),
                headerSecondary);
            state.Markdown.Markdown = markdown;
            Flow.ScrollToTailIfEnabled(_isAutoScrollEnabled());
        });
    }

    public void FinalizeContent(AgentContentCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        var state = GetOrCreateContentState(completed.Kind, completed.ContentId, completed.Timestamp);
        var content = ResolveCompletedContent(completed.Content, state.Buffer);
        state.Buffer.Clear();
        state.Buffer.Append(content);
        var markdown = ChatMarkdownFormatter.FormatChatContentMarkdown(completed.Kind, content);
        var headerSecondary = ChatMarkdownFormatter.GetChatContentHeaderSecondary(completed.Kind, content);
        UiDispatch.Post(_uiDispatcher, () =>
        {
            ChatTimelineVisualFactory.ApplyHeader(
                state.HeaderText,
                ChatTimelineVisualFactory.GetContentTone(completed.Kind),
                ChatTimelineVisualFactory.GetContentHeader(completed.Kind),
                headerSecondary);
            state.Markdown.Markdown = markdown;
            Flow.ScrollToTailIfEnabled(_isAutoScrollEnabled());
        });
    }

    public bool ShouldSkipEmptyAssistantCompletion(AgentContentCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        if (completed.Kind != AgentContentKind.Assistant || !string.IsNullOrWhiteSpace(completed.Content))
        {
            return false;
        }

        var key = ChatTimelineVisualFactory.CreateContentKey(completed.Kind, completed.ContentId);
        return _contentStates.TryGetValue(key, out var existing)
            ? existing.Buffer.Length == 0
            : true;
    }

    public void UpsertPlanStatus(
        string key,
        DateTimeOffset timestamp,
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
        => UpsertStatus(_planStates, key, timestamp, markdown, tone, headerOverride, headerSecondary);

    public void UpsertActivityStatus(
        string key,
        DateTimeOffset timestamp,
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
        => UpsertStatus(_activityStates, key, timestamp, markdown, tone, headerOverride, headerSecondary);

    public void AddStatus(
        DateTimeOffset timestamp,
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
        => UpsertStatus(dictionary: null, key: null, timestamp, markdown, tone, headerOverride, headerSecondary);

    public void UpsertInteraction(
        string interactionId,
        DateTimeOffset timestamp,
        string? baseMarkdown,
        string? statusMarkdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        if (!_interactionStates.TryGetValue(interactionId, out var state))
        {
            state = CreateChatStatusState(baseMarkdown ?? statusMarkdown ?? string.Empty, tone, timestamp, headerOverride, headerSecondary);
            _interactionStates[interactionId] = state;
            AppendTimelineItem(state.Item);
        }

        if (!string.IsNullOrWhiteSpace(baseMarkdown))
        {
            state.BaseMarkdown = baseMarkdown;
        }

        if (!string.IsNullOrWhiteSpace(statusMarkdown))
        {
            state.StatusMarkdown = statusMarkdown;
        }

        UiDispatch.Post(_uiDispatcher, () =>
        {
            ChatTimelineVisualFactory.ApplyTimestamp(state.TimestampText, timestamp);
            state.Markdown.Markdown = state.MarkdownValue;
            Flow.ScrollToTailIfEnabled(_isAutoScrollEnabled());
        });
    }

    public void RenderError(string message, DateTimeOffset timestamp)
    {
        var pendingAssistant = _pendingAssistant;
        if (pendingAssistant is not null)
        {
            pendingAssistant.Buffer.Append(message);
            UiDispatch.Invoke(
                _uiDispatcher,
                static state =>
                {
                    state.pendingAssistant.Markdown.Markdown = state.message;
                    ChatTimelineVisualFactory.ApplyTimestamp(state.pendingAssistant.TimestampText, state.timestamp);
                    return 0;
                },
                (pendingAssistant, message, timestamp));
            _pendingAssistant = null;
            return;
        }

        var entry = ChatTimelineVisualFactory.CreateMarkdownItem(message, ChatTimelineTone.Interaction, headerOverride: "Error");
        ChatTimelineVisualFactory.ApplyTimestamp(entry.TimestampText, timestamp);
        UiDispatch.Invoke(
            _uiDispatcher,
            static state =>
            {
                state.flow.Items.Add(state.entry.Item);
                return 0;
            },
            (flow: Flow, entry));
    }

    public void RenderFailure(string markdown)
    {
        var pendingAssistant = _pendingAssistant;
        if (pendingAssistant is not null)
        {
            pendingAssistant.Buffer.Append(markdown);
            UiDispatch.Invoke(
                _uiDispatcher,
                static state =>
                {
                    state.pendingAssistant.Markdown.Markdown = state.markdown;
                    return 0;
                },
                (pendingAssistant, markdown));
            _pendingAssistant = null;
            return;
        }

        var entry = ChatTimelineVisualFactory.CreateMarkdownItem(markdown, ChatTimelineTone.Interaction, headerOverride: "Error");
        UiDispatch.Invoke(
            _uiDispatcher,
            static state =>
            {
                state.flow.Items.Add(state.entry.Item);
                return 0;
            },
            (flow: Flow, entry));
    }

    public void ClearPendingAssistant()
        => _pendingAssistant = null;

    public void FlushBufferedHistoryItems()
    {
        if (_bufferedHistoryItems is not { Count: > 0 } items)
        {
            return;
        }

        UiDispatch.Post(
            _uiDispatcher,
            () =>
            {
                Flow.Items.AddRange(items);
                Flow.ScrollToTailIfEnabled(_isAutoScrollEnabled());
            });
    }

    public void Reset()
    {
        ToolCalls.Reset();
        UiDispatch.Post(_uiDispatcher, () => Flow.Items.Clear());
        _bufferedHistoryItems = null;
        _contentStates.Clear();
        _activityStates.Clear();
        _interactionStates.Clear();
        _planStates.Clear();
        _pendingAssistant = null;
        _truncatedHistory = null;
        _hasSeenUserPrompt = false;
    }

    internal static string ResolveCompletedContent(string completedContent, StringBuilder bufferedContent)
    {
        ArgumentNullException.ThrowIfNull(bufferedContent);

        return completedContent.Length > 0
            ? completedContent
            : bufferedContent.ToString();
    }

    internal static TruncatedHistoryState CreateTruncatedHistoryState(int omittedMessageCount, Action onLoad)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(omittedMessageCount);
        ArgumentNullException.ThrowIfNull(onLoad);

        return UiDispatch.InvokeCurrent(
            static state => CreateTruncatedHistoryStateCore(state.omittedMessageCount, state.onLoad),
            (omittedMessageCount, onLoad));
    }

    internal static List<DocumentFlowItem> BuildInitialThreadHistoryItems(
        IReadOnlyList<DocumentFlowItem> renderedItems,
        DocumentFlowItem? truncatedHistoryItem)
    {
        ArgumentNullException.ThrowIfNull(renderedItems);

        if (truncatedHistoryItem is null)
        {
            return [.. renderedItems];
        }

        var items = new List<DocumentFlowItem>(renderedItems.Count + 1);
        items.Add(truncatedHistoryItem.Value);
        items.AddRange(renderedItems);
        return items;
    }

    private static TruncatedHistoryState CreateTruncatedHistoryStateCore(int omittedMessageCount, Action onLoad)
    {
        var button = new Button(new TextBlock(ChatTimelineVisualFactory.BuildTruncatedHistoryLoadButtonText(omittedMessageCount)))
            .Click(ChatTimelineVisualFactory.CreateDeferredUiAction(onLoad));
        var rule = new Rule()
            .CenterLabel(button);
        var item = new DocumentFlowItem
        {
            Content = new FlowDocument().Add(rule),
            Alignment = DocumentFlowAlignment.Stretch,
            Padding = new Thickness(0, 1, 0, 0),
        };
        return new TruncatedHistoryState(item, rule, omittedMessageCount);
    }

    private ChatContentState GetOrCreateContentState(AgentContentKind kind, string contentId, DateTimeOffset timestamp)
    {
        var key = ChatTimelineVisualFactory.CreateContentKey(kind, contentId);
        if (_contentStates.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (kind == AgentContentKind.Assistant && _pendingAssistant is { ContentId: null } pending)
        {
            pending.ContentId = contentId;
            ChatTimelineVisualFactory.ApplyTimestamp(pending.TimestampText, timestamp);
            _pendingAssistant = null;
            var pendingState = new ChatContentState(pending.Item, pending.Markdown, pending.TimestampText, pending.HeaderText, pending.Buffer, kind);
            _contentStates[key] = pendingState;
            return pendingState;
        }

        var entry = ChatTimelineVisualFactory.CreateMarkdownItem(
            ChatMarkdownFormatter.FormatChatContentMarkdown(kind, string.Empty),
            ChatTimelineVisualFactory.GetContentTone(kind),
            headerOverride: ChatTimelineVisualFactory.GetContentHeader(kind),
            headerSecondary: ChatMarkdownFormatter.GetChatContentHeaderSecondary(kind, string.Empty));
        ChatTimelineVisualFactory.ApplyTimestamp(entry.TimestampText, timestamp);
        var state = new ChatContentState(entry.Item, entry.Markdown, entry.TimestampText, entry.HeaderText, new StringBuilder(), kind);
        _contentStates[key] = state;
        if (kind == AgentContentKind.User)
        {
            foreach (var item in ChatTimelineVisualFactory.BuildUserPromptTimelineItems(entry.Item, _hasSeenUserPrompt))
            {
                AppendTimelineItem(item);
            }

            _hasSeenUserPrompt = true;
            return state;
        }

        AppendTimelineItem(entry.Item);
        return state;
    }

    private void UpsertStatus(
        Dictionary<string, ChatStatusState>? dictionary,
        string? key,
        DateTimeOffset timestamp,
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        if (dictionary is null || key is null)
        {
            var state = CreateChatStatusState(markdown, tone, timestamp, headerOverride, headerSecondary);
            AppendTimelineItem(state.Item);
            return;
        }

        if (!dictionary.TryGetValue(key, out var stateEntry))
        {
            stateEntry = CreateChatStatusState(markdown, tone, timestamp, headerOverride, headerSecondary);
            dictionary[key] = stateEntry;
            AppendTimelineItem(stateEntry.Item);
        }

        stateEntry.BaseMarkdown = markdown;
        UiDispatch.Post(_uiDispatcher, () =>
        {
            ChatTimelineVisualFactory.ApplyTimestamp(stateEntry.TimestampText, timestamp);
            stateEntry.Markdown.Markdown = stateEntry.MarkdownValue;
            Flow.ScrollToTailIfEnabled(_isAutoScrollEnabled());
        });
    }

    private static ChatStatusState CreateChatStatusState(
        string markdown,
        ChatTimelineTone tone,
        DateTimeOffset timestamp,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        var entry = ChatTimelineVisualFactory.CreateMarkdownItem(markdown, tone, headerOverride, headerSecondary);
        ChatTimelineVisualFactory.ApplyTimestamp(entry.TimestampText, timestamp);
        return new ChatStatusState(entry.Item, entry.Markdown, entry.TimestampText)
        {
            BaseMarkdown = markdown,
        };
    }

    private void AppendTimelineItem(DocumentFlowItem item, bool resetActiveToolCallGroup = true)
    {
        if (resetActiveToolCallGroup)
        {
            ToolCalls.OnNonToolTimelineItemAppended();
        }

        if (_bufferedHistoryItems is not null)
        {
            _bufferedHistoryItems.Add(item);
            return;
        }

        UiDispatch.Post(_uiDispatcher, () =>
        {
            Flow.Items.Add(item);
            Flow.ScrollToTailIfEnabled(_isAutoScrollEnabled());
        });
    }
}
