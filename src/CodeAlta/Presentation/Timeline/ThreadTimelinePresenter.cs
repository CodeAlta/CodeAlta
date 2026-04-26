using CodeAlta.Threading;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Models;
using CodeAlta.Presentation.Formatting;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Presentation.Timeline;

internal sealed class ThreadTimelinePresenter
{
    private enum DeferredTailScrollMode
    {
        None = 0,
        Auto = 1,
        Reveal = 2,
    }

    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<bool> _isAutoScrollEnabled;
    private readonly Action<Action>? _enqueueDeferredUiAction;
    private readonly Dictionary<string, ChatContentState> _contentStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _activityStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _interactionStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _planStates = new(StringComparer.Ordinal);
    private readonly List<MessageNavigationAnchor> _messageNavigationAnchors = [];
    private List<DocumentFlowItem>? _bufferedHistoryItems;
    private PendingAssistantState? _pendingAssistant;
    private OptimisticUserPromptState? _optimisticUserPrompt;
    private TruncatedHistoryState? _truncatedHistory;
    private bool _hasSeenUserPrompt;
    private bool _deferredTailScrollQueued;
    private DeferredTailScrollMode _deferredTailScrollMode;
    private int? _messageNavigationIndex;
    private string? _localFileRootPath;

    public ThreadTimelinePresenter(
        IUiDispatcher uiDispatcher,
        Func<bool> isAutoScrollEnabled,
        Func<Rectangle?> getDialogBounds,
        string? localFileRootPath = null,
        Action<Action>? enqueueDeferredUiAction = null)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(isAutoScrollEnabled);
        ArgumentNullException.ThrowIfNull(getDialogBounds);

        _uiDispatcher = uiDispatcher;
        _isAutoScrollEnabled = isAutoScrollEnabled;
        _enqueueDeferredUiAction = enqueueDeferredUiAction;
        _localFileRootPath = localFileRootPath;
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
            RequestAutoScrollToTail,
            item => AppendTimelineItem(item, resetActiveToolCallGroup: false),
            getDialogBounds,
            localFileRootPath);
        FileChanges = new FileChangePresenter(
            Flow,
            _uiDispatcher,
            _isAutoScrollEnabled,
            RequestAutoScrollToTail,
            item => AppendTimelineItem(item, resetActiveToolCallGroup: true),
            getDialogBounds,
            localFileRootPath);
    }

    public DocumentFlow Flow { get; }

    public ToolCallPresenter ToolCalls { get; }

    public FileChangePresenter FileChanges { get; }

    public string? LocalFileRootPath => _localFileRootPath;

    public bool HasNavigableMessages => _messageNavigationAnchors.Count > 0;

    internal int? MessageNavigationIndex => _messageNavigationIndex;

    public void SetLocalFileRootPath(string? localFileRootPath)
    {
        if (string.Equals(_localFileRootPath, localFileRootPath, StringComparison.Ordinal))
        {
            return;
        }

        _localFileRootPath = localFileRootPath;
        ToolCalls.SetLocalFileRootPath(localFileRootPath);
        FileChanges.SetLocalFileRootPath(localFileRootPath);
        UiDispatch.Post(_uiDispatcher, () =>
        {
            foreach (var state in _contentStates.Values)
            {
                ChatTimelineVisualFactory.ApplyLocalFileRootPath(state.Markdown, _localFileRootPath);
            }

            foreach (var state in _activityStates.Values)
            {
                ChatTimelineVisualFactory.ApplyLocalFileRootPath(state.Markdown, _localFileRootPath);
            }

            foreach (var state in _interactionStates.Values)
            {
                ChatTimelineVisualFactory.ApplyLocalFileRootPath(state.Markdown, _localFileRootPath);
            }

            foreach (var state in _planStates.Values)
            {
                ChatTimelineVisualFactory.ApplyLocalFileRootPath(state.Markdown, _localFileRootPath);
            }

            if (_pendingAssistant is { } pendingAssistant)
            {
                ChatTimelineVisualFactory.ApplyLocalFileRootPath(pendingAssistant.Markdown, _localFileRootPath);
            }

            if (_optimisticUserPrompt is { } optimisticUserPrompt)
            {
                ChatTimelineVisualFactory.ApplyLocalFileRootPath(optimisticUserPrompt.Entry.Markdown, _localFileRootPath);
            }
        });
    }

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
            RequestAutoScrollToTail();
        });
    }

    public void RenderOptimisticUserPrompt(string prompt, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        RollbackOptimisticUserPrompt();

        var markdown = ChatMarkdownFormatter.FormatChatContentMarkdown(AgentContentKind.User, prompt);
        var entry = ChatTimelineVisualFactory.CreateMarkdownItem(
            markdown,
            ChatTimelineVisualFactory.GetContentTone(AgentContentKind.User),
            headerOverride: ChatTimelineVisualFactory.GetContentHeader(AgentContentKind.User),
            headerSecondary: ChatMarkdownFormatter.GetChatContentHeaderSecondary(AgentContentKind.User, prompt),
            localFileRootPath: _localFileRootPath);
        ChatTimelineVisualFactory.ApplyTimestamp(entry.TimestampText, timestamp);
        var items = ChatTimelineVisualFactory.BuildUserPromptTimelineItems(entry.Item, _hasSeenUserPrompt).ToArray();
        foreach (var item in items)
        {
            AppendTimelineItem(item);
        }

        _hasSeenUserPrompt = true;
        _optimisticUserPrompt = new OptimisticUserPromptState(entry, items, prompt);
        TrackNavigableMessage(entry.Item, AgentContentKind.User);
    }

    public bool TryConsumeOptimisticUserEcho(AgentContentKind kind, string contentId, DateTimeOffset timestamp, bool completed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);

        if (kind != AgentContentKind.User || _optimisticUserPrompt is not { } optimisticUserPrompt)
        {
            return false;
        }

        if (optimisticUserPrompt.EchoContentId is null)
        {
            optimisticUserPrompt.EchoContentId = contentId;
            ChatTimelineVisualFactory.ApplyTimestamp(optimisticUserPrompt.Entry.TimestampText, timestamp);
        }
        else if (!string.Equals(optimisticUserPrompt.EchoContentId, contentId, StringComparison.Ordinal))
        {
            return false;
        }

        if (completed)
        {
            _optimisticUserPrompt = null;
        }

        return true;
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
            RequestAutoScrollToTail();
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
            RequestAutoScrollToTail();
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

        var entry = ChatTimelineVisualFactory.CreateMarkdownItem(message, ChatTimelineTone.Interaction, headerOverride: "Error", localFileRootPath: _localFileRootPath);
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

        var entry = ChatTimelineVisualFactory.CreateMarkdownItem(markdown, ChatTimelineTone.Interaction, headerOverride: "Error", localFileRootPath: _localFileRootPath);
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

    public void RollbackOptimisticUserPrompt()
    {
        if (_optimisticUserPrompt is not { } optimisticUserPrompt)
        {
            return;
        }

        RemoveTimelineItems(optimisticUserPrompt.TimelineItems);
        if (!_contentStates.Keys.Any(static key => key.StartsWith("content:User:", StringComparison.Ordinal)))
        {
            _hasSeenUserPrompt = false;
        }

        _optimisticUserPrompt = null;
    }

    public void FlushBufferedHistoryItems()
    {
        if (_bufferedHistoryItems is not { Count: > 0 } items)
        {
            return;
        }

        _uiDispatcher.Post(
            () =>
            {
                Flow.Items.AddRange(items);
                RequestAutoScrollToTail();
            });
    }

    public void RevealTail()
        => _uiDispatcher.Post(
            () =>
            {
                RequestRevealTail();
            });

    public void ScrollToPreviousMessage()
        => _uiDispatcher.Post(ScrollToPreviousMessageCore);

    public void ScrollToNextMessage()
        => _uiDispatcher.Post(ScrollToNextMessageCore);

    public void ScrollToFirstMessage()
        => _uiDispatcher.Post(ScrollToFirstMessageCore);

    public void ScrollToLastMessage()
        => _uiDispatcher.Post(ScrollToLastMessageCore);

    public void Reset()
    {
        ToolCalls.Reset();
        FileChanges.Reset();
        UiDispatch.Post(_uiDispatcher, () => Flow.Items.Clear());
        _bufferedHistoryItems = null;
        _contentStates.Clear();
        _activityStates.Clear();
        _interactionStates.Clear();
        _planStates.Clear();
        _pendingAssistant = null;
        _optimisticUserPrompt = null;
        _truncatedHistory = null;
        _hasSeenUserPrompt = false;
        _messageNavigationAnchors.Clear();
        _messageNavigationIndex = null;
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
            TrackNavigableMessage(pending.Item, kind);
            return pendingState;
        }

        var entry = ChatTimelineVisualFactory.CreateMarkdownItem(
            ChatMarkdownFormatter.FormatChatContentMarkdown(kind, string.Empty),
            ChatTimelineVisualFactory.GetContentTone(kind),
            headerOverride: ChatTimelineVisualFactory.GetContentHeader(kind),
            headerSecondary: ChatMarkdownFormatter.GetChatContentHeaderSecondary(kind, string.Empty),
            localFileRootPath: _localFileRootPath);
        ChatTimelineVisualFactory.ApplyTimestamp(entry.TimestampText, timestamp);
        var state = new ChatContentState(entry.Item, entry.Markdown, entry.TimestampText, entry.HeaderText, new StringBuilder(), kind);
        _contentStates[key] = state;
        if (kind == AgentContentKind.User)
        {
            TrackNavigableMessage(entry.Item, kind);
            foreach (var item in ChatTimelineVisualFactory.BuildUserPromptTimelineItems(entry.Item, _hasSeenUserPrompt))
            {
                AppendTimelineItem(item);
            }

            _hasSeenUserPrompt = true;
            return state;
        }

        TrackNavigableMessage(entry.Item, kind);
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
            RequestAutoScrollToTail();
        });
    }

    private ChatStatusState CreateChatStatusState(
        string markdown,
        ChatTimelineTone tone,
        DateTimeOffset timestamp,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        var entry = ChatTimelineVisualFactory.CreateMarkdownItem(markdown, tone, headerOverride, headerSecondary, localFileRootPath: _localFileRootPath);
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
            RequestAutoScrollToTail();
        });
    }

    private void RemoveTimelineItems(IReadOnlyList<DocumentFlowItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return;
        }

        RemoveMessageNavigationAnchors(items);
        if (_bufferedHistoryItems is not null)
        {
            foreach (var item in items)
            {
                _ = _bufferedHistoryItems.Remove(item);
            }

            return;
        }

        UiDispatch.Post(_uiDispatcher, () =>
        {
            foreach (var item in items)
            {
                _ = Flow.Items.Remove(item);
            }
        });
    }

    private void TrackNavigableMessage(DocumentFlowItem item, AgentContentKind kind)
    {
        if (kind is not (AgentContentKind.User or AgentContentKind.Assistant))
        {
            return;
        }

        _messageNavigationAnchors.Add(new MessageNavigationAnchor(item, kind));
        if (Flow.FollowTail)
        {
            _messageNavigationIndex = null;
        }
    }

    private void RemoveMessageNavigationAnchors(IReadOnlyList<DocumentFlowItem> items)
    {
        if (items.Count == 0 || _messageNavigationAnchors.Count == 0)
        {
            return;
        }

        for (var anchorIndex = _messageNavigationAnchors.Count - 1; anchorIndex >= 0; anchorIndex--)
        {
            var anchor = _messageNavigationAnchors[anchorIndex];
            if (!ContainsItemByContent(items, anchor.Item))
            {
                continue;
            }

            _messageNavigationAnchors.RemoveAt(anchorIndex);
            if (_messageNavigationIndex is { } currentIndex)
            {
                if (currentIndex == anchorIndex)
                {
                    _messageNavigationIndex = null;
                }
                else if (currentIndex > anchorIndex)
                {
                    _messageNavigationIndex = currentIndex - 1;
                }
            }
        }
    }

    private void ScrollToPreviousMessageCore()
    {
        if (_messageNavigationAnchors.Count == 0)
        {
            return;
        }

        var targetIndex = !Flow.FollowTail && _messageNavigationIndex is { } currentIndex
            ? Math.Max(0, currentIndex - 1)
            : _messageNavigationAnchors.Count - 1;
        ScrollToMessageAnchor(targetIndex);
    }

    private void ScrollToNextMessageCore()
    {
        if (_messageNavigationAnchors.Count == 0)
        {
            return;
        }

        if (Flow.FollowTail ||
            _messageNavigationIndex is not { } currentIndex ||
            currentIndex >= _messageNavigationAnchors.Count - 1)
        {
            ScrollToLastMessageCore();
            return;
        }

        ScrollToMessageAnchor(currentIndex + 1);
    }

    private void ScrollToFirstMessageCore()
    {
        if (_messageNavigationAnchors.Count == 0)
        {
            return;
        }

        ScrollToMessageAnchor(0);
    }

    private void ScrollToLastMessageCore()
    {
        _messageNavigationIndex = null;
        RequestRevealTail();
    }

    private void ScrollToMessageAnchor(int anchorIndex)
    {
        if ((uint)anchorIndex >= (uint)_messageNavigationAnchors.Count)
        {
            return;
        }

        var itemIndex = IndexOfFlowItemByContent(Flow, _messageNavigationAnchors[anchorIndex].Item);
        if (itemIndex < 0 || !Flow.TryScrollToItem(itemIndex))
        {
            return;
        }

        _messageNavigationIndex = anchorIndex;
    }

    private static bool ContainsItemByContent(IReadOnlyList<DocumentFlowItem> items, DocumentFlowItem target)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i].Content, target.Content))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOfFlowItemByContent(DocumentFlow flow, DocumentFlowItem target)
    {
        for (var i = 0; i < flow.Items.Count; i++)
        {
            if (ReferenceEquals(flow.Items[i].Content, target.Content))
            {
                return i;
            }
        }

        return -1;
    }

    private void RequestAutoScrollToTail()
    {
        Flow.ScrollToTailIfEnabled(_isAutoScrollEnabled());
        QueueDeferredTailScroll(DeferredTailScrollMode.Auto);
    }

    private void RequestRevealTail()
    {
        Flow.ScrollToTailIfFollowing();
        QueueDeferredTailScroll(DeferredTailScrollMode.Reveal);
    }

    private void QueueDeferredTailScroll(DeferredTailScrollMode mode)
    {
        if (_enqueueDeferredUiAction is null)
        {
            return;
        }

        if (mode > _deferredTailScrollMode)
        {
            _deferredTailScrollMode = mode;
        }

        if (_deferredTailScrollQueued || mode == DeferredTailScrollMode.None)
        {
            return;
        }

        _deferredTailScrollQueued = true;
        _enqueueDeferredUiAction(DrainDeferredTailScroll);
    }

    private void DrainDeferredTailScroll()
    {
        _deferredTailScrollQueued = false;
        var mode = _deferredTailScrollMode;
        _deferredTailScrollMode = DeferredTailScrollMode.None;
        switch (mode)
        {
            case DeferredTailScrollMode.Auto:
                Flow.ScrollToTailIfEnabled(_isAutoScrollEnabled());
                break;
            case DeferredTailScrollMode.Reveal:
                Flow.ScrollToTailIfFollowing();
                break;
        }
    }

    private readonly record struct MessageNavigationAnchor(DocumentFlowItem Item, AgentContentKind Kind);
}
