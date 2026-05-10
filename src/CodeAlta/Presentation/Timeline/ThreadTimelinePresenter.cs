using CodeAlta.Threading;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Models;
using CodeAlta.Presentation.Formatting;
using CodeAlta.Presentation.Prompting;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Presentation.Timeline;

internal sealed class ThreadTimelinePresenter
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Dictionary<string, ChatContentState> _contentStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _activityStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _interactionStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _planStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _pluginProjectionStates = new(StringComparer.Ordinal);
    private readonly List<MessageNavigationAnchor> _messageNavigationAnchors = [];
    private List<DocumentFlowItem>? _bufferedHistoryItems;
    private PendingAssistantState? _pendingAssistant;
    private OptimisticUserPromptState? _optimisticUserPrompt;
    private TruncatedHistoryState? _truncatedHistory;
    private bool _hasSeenUserPrompt;
    private int? _messageNavigationIndex;
    private string? _localFileRootPath;

    public ThreadTimelinePresenter(
        IUiDispatcher uiDispatcher,
        Func<Rectangle?> getDialogBounds,
        string? localFileRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(getDialogBounds);

        _uiDispatcher = uiDispatcher;
        _getDialogBounds = getDialogBounds;
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
            item => AppendTimelineItem(item, resetActiveToolCallGroup: false),
            getDialogBounds,
            localFileRootPath);
        FileChanges = new FileChangePresenter(
            Flow,
            _uiDispatcher,
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

            foreach (var state in _pluginProjectionStates.Values)
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
        if (TryGetDraftAttemptId(delta.Details, out var draftAttemptId))
        {
            state.DraftAttemptId = draftAttemptId;
        }

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
        });
    }

    public void DiscardDraftContent(AgentSessionUpdateEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (!TryGetDiscardDraftAttemptId(update.Details, out var draftAttemptId))
        {
            return;
        }

        var discardedItems = new List<DocumentFlowItem>();
        foreach (var entry in _contentStates.ToArray())
        {
            if (!string.Equals(entry.Value.DraftAttemptId, draftAttemptId, StringComparison.Ordinal))
            {
                continue;
            }

            _contentStates.Remove(entry.Key);
            discardedItems.Add(entry.Value.Item);
        }

        RemoveTimelineItems(discardedItems);
    }

    public void RenderOptimisticUserPrompt(string prompt, DateTimeOffset timestamp)
        => RenderOptimisticUserPrompt(prompt, [], timestamp);

    public void RenderOptimisticUserPrompt(
        string prompt,
        IReadOnlyList<PromptImageAttachmentReference> imageAttachments,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(imageAttachments);
        if (string.IsNullOrWhiteSpace(prompt) && imageAttachments.Count == 0)
        {
            return;
        }

        RollbackOptimisticUserPrompt();

        var markdown = ChatMarkdownFormatter.FormatChatContentMarkdown(AgentContentKind.User, prompt);
        var entry = ChatTimelineVisualFactory.CreateMarkdownItem(
            markdown,
            ChatTimelineVisualFactory.GetContentTone(AgentContentKind.User),
            headerOverride: ChatTimelineVisualFactory.GetContentHeader(AgentContentKind.User),
            headerSecondary: ChatMarkdownFormatter.GetChatContentHeaderSecondary(AgentContentKind.User, prompt),
            localFileRootPath: _localFileRootPath,
            imageAttachments: imageAttachments,
            getDialogBounds: _getDialogBounds);
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

        var imageAttachments = completed.Kind == AgentContentKind.User
            ? ExtractUserImageAttachments(completed.Details)
            : [];
        var state = GetOrCreateContentState(completed.Kind, completed.ContentId, completed.Timestamp, imageAttachments);
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

    public void UpsertPluginProjection(
        string key,
        DateTimeOffset timestamp,
        string markdown,
        string? headerSecondary = null,
        IReadOnlyList<ChatCollapsibleMarkdownSection>? detailSections = null,
        Func<Visual>? visualFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(markdown);

        detailSections ??= [];
        if (!_pluginProjectionStates.TryGetValue(key, out var stateEntry))
        {
            stateEntry = CreateChatStatusState(markdown, ChatTimelineTone.Notice, timestamp, "Plugin", headerSecondary, detailSections, visualFactory);
            _pluginProjectionStates[key] = stateEntry;
            AppendTimelineItem(stateEntry.Item);
        }
        else if (RequiresPluginProjectionRecreate(stateEntry, detailSections, visualFactory))
        {
            var previousItem = stateEntry.Item;
            stateEntry = CreateChatStatusState(markdown, ChatTimelineTone.Notice, timestamp, "Plugin", headerSecondary, detailSections, visualFactory);
            _pluginProjectionStates[key] = stateEntry;
            ReplaceTimelineItem(previousItem, stateEntry.Item);
        }

        stateEntry.BaseMarkdown = markdown;
        stateEntry.DetailSections = detailSections;
        stateEntry.CopyState.DetailSections = detailSections;
        UiDispatch.Post(_uiDispatcher, () =>
        {
            ChatTimelineVisualFactory.ApplyTimestamp(stateEntry.TimestampText, timestamp);
            stateEntry.Markdown.Markdown = stateEntry.MarkdownValue;
            for (var index = 0; index < stateEntry.DetailMarkdownControls.Count && index < stateEntry.DetailMarkdownSectionIndexes.Count; index++)
            {
                var sectionIndex = stateEntry.DetailMarkdownSectionIndexes[index];
                if (sectionIndex < detailSections.Count)
                {
                    stateEntry.DetailMarkdownControls[index].Markdown = detailSections[sectionIndex].Markdown.Trim();
                }
            }
        });
    }

    public void RemovePluginProjection(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!_pluginProjectionStates.Remove(key, out var state))
        {
            return;
        }

        RemoveTimelineItems([state.Item]);
    }

    public void AddStatus(
        DateTimeOffset timestamp,
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
        => UpsertStatus(dictionary: null, key: null, timestamp, markdown, tone, headerOverride, headerSecondary);

    public void AddCollapsibleStatus(
        DateTimeOffset timestamp,
        string markdown,
        string collapsibleHeader,
        string collapsibleMarkdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        var entry = ChatTimelineVisualFactory.CreateCollapsibleMarkdownItem(
            markdown,
            collapsibleHeader,
            collapsibleMarkdown,
            tone,
            headerOverride,
            headerSecondary,
            localFileRootPath: _localFileRootPath);
        ChatTimelineVisualFactory.ApplyTimestamp(entry.TimestampText, timestamp);
        AppendTimelineItem(entry.Item);
    }

    public void AddCollapsibleStatus(
        DateTimeOffset timestamp,
        string markdown,
        IReadOnlyList<ChatCollapsibleMarkdownSection> collapsibleSections,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        ArgumentNullException.ThrowIfNull(collapsibleSections);

        var entry = ChatTimelineVisualFactory.CreateCollapsibleMarkdownItem(
            markdown,
            collapsibleSections,
            tone,
            headerOverride,
            headerSecondary,
            localFileRootPath: _localFileRootPath);
        ChatTimelineVisualFactory.ApplyTimestamp(entry.TimestampText, timestamp);
        AppendTimelineItem(entry.Item);
    }

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
                AddFlowItemIfAbsent(state.flow, state.entry.Item);
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
                AddFlowItemIfAbsent(state.flow, state.entry.Item);
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
        if (_bufferedHistoryItems is not { } items)
        {
            return;
        }

        _bufferedHistoryItems = null;
        if (items.Count == 0)
        {
            return;
        }

        var itemsToFlush = items.ToArray();

        _uiDispatcher.Post(
            () =>
            {
                AddFlowItemsIfAbsent(Flow, itemsToFlush);
            });
    }

    public void RevealTail()
        => _uiDispatcher.Post(Flow.ScrollToTail);

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
        _pluginProjectionStates.Clear();
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

    private ChatContentState GetOrCreateContentState(
        AgentContentKind kind,
        string contentId,
        DateTimeOffset timestamp,
        IReadOnlyList<PromptImageAttachmentReference>? imageAttachments = null)
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
            localFileRootPath: _localFileRootPath,
            imageAttachments: kind == AgentContentKind.User ? imageAttachments : null,
            getDialogBounds: _getDialogBounds);
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
        });
    }

    private ChatStatusState CreateChatStatusState(
        string markdown,
        ChatTimelineTone tone,
        DateTimeOffset timestamp,
        string? headerOverride = null,
        string? headerSecondary = null,
        IReadOnlyList<ChatCollapsibleMarkdownSection>? detailSections = null,
        Func<Visual>? visualFactory = null)
    {
        var entry = detailSections is { Count: > 0 } || visualFactory is not null
            ? ChatTimelineVisualFactory.CreateCollapsibleMarkdownItem(markdown, detailSections ?? [], tone, headerOverride, headerSecondary, localFileRootPath: _localFileRootPath, contentVisualFactory: visualFactory)
            : ChatTimelineVisualFactory.CreateMarkdownItem(markdown, tone, headerOverride, headerSecondary, localFileRootPath: _localFileRootPath);
        ChatTimelineVisualFactory.ApplyTimestamp(entry.TimestampText, timestamp);
        return new ChatStatusState(entry.Item, entry.Markdown, entry.TimestampText, entry.DetailMarkdownControls, entry.DetailMarkdownSectionIndexes, entry.CopyState)
        {
            BaseMarkdown = markdown,
            DetailSections = detailSections ?? [],
            HasContentVisualFactory = visualFactory is not null,
        };
    }

    private static bool RequiresPluginProjectionRecreate(
        ChatStatusState existingState,
        IReadOnlyList<ChatCollapsibleMarkdownSection> updated,
        Func<Visual>? visualFactory)
    {
        if (existingState.HasContentVisualFactory || visualFactory is not null)
        {
            return true;
        }

        var existing = existingState.DetailSections;
        if (existing.Count != updated.Count)
        {
            return true;
        }

        for (var index = 0; index < existing.Count; index++)
        {
            if (!string.Equals(existing[index].Header, updated[index].Header, StringComparison.Ordinal))
            {
                return true;
            }

            if (existing[index].VisualFactory is not null || updated[index].VisualFactory is not null)
            {
                return true;
            }
        }

        return false;
    }

    private void ReplaceTimelineItem(DocumentFlowItem oldItem, DocumentFlowItem newItem)
    {
        RemoveMessageNavigationAnchors([oldItem]);
        ToolCalls.OnNonToolTimelineItemAppended();
        if (_bufferedHistoryItems is not null)
        {
            var index = _bufferedHistoryItems.IndexOf(oldItem);
            if (index >= 0)
            {
                _bufferedHistoryItems[index] = newItem;
            }
            else if (!ContainsItemByContent(_bufferedHistoryItems, newItem))
            {
                _bufferedHistoryItems.Add(newItem);
            }

            return;
        }

        UiDispatch.Post(_uiDispatcher, () =>
        {
            var index = Flow.Items.IndexOf(oldItem);
            if (index >= 0)
            {
                Flow.Items[index] = newItem;
            }
            else if (IndexOfFlowItemByContent(Flow, newItem) < 0)
            {
                Flow.Items.Add(newItem);
            }
        });
    }

    private void AppendTimelineItem(DocumentFlowItem item, bool resetActiveToolCallGroup = true)
    {
        if (resetActiveToolCallGroup)
        {
            ToolCalls.OnNonToolTimelineItemAppended();
        }

        if (_bufferedHistoryItems is not null)
        {
            if (ContainsItemByContent(_bufferedHistoryItems, item))
            {
                return;
            }

            _bufferedHistoryItems.Add(item);
            return;
        }

        UiDispatch.Post(_uiDispatcher, () =>
        {
            AddFlowItemIfAbsent(Flow, item);
        });
    }

    private static void AddFlowItemsIfAbsent(DocumentFlow flow, IReadOnlyList<DocumentFlowItem> items)
    {
        for (var index = 0; index < items.Count; index++)
        {
            AddFlowItemIfAbsent(flow, items[index]);
        }
    }

    private static void AddFlowItemIfAbsent(DocumentFlow flow, DocumentFlowItem item)
    {
        if (IndexOfFlowItemByContent(flow, item) >= 0)
        {
            return;
        }

        flow.Items.Add(item);
    }

    private static IReadOnlyList<PromptImageAttachmentReference> ExtractUserImageAttachments(System.Text.Json.JsonElement? details)
    {
        if (details is not { ValueKind: System.Text.Json.JsonValueKind.Object } root)
        {
            return [];
        }

        var images = new List<PromptImageAttachmentReference>();
        if (TryGetProperty(root, "items", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !TryGetStringProperty(item, "$type", out var type) ||
                    !string.Equals(type, "localImage", StringComparison.OrdinalIgnoreCase) ||
                    !TryGetStringProperty(item, "path", out var path) ||
                    string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                _ = TryGetStringProperty(item, "displayName", out var displayName);
                _ = TryGetStringProperty(item, "mediaType", out var mediaType);
                images.Add(new PromptImageAttachmentReference(
                    string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(path) : displayName,
                    path,
                    string.IsNullOrWhiteSpace(mediaType) ? "image/*" : mediaType));
            }
        }

        if (TryGetProperty(root, "attachments", out var attachments) && attachments.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                if (attachment.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !TryGetStringProperty(attachment, "path", out var path) ||
                    string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                _ = TryGetStringProperty(attachment, "title", out var title);
                _ = TryGetStringProperty(attachment, "mediaType", out var mediaType);
                images.Add(new PromptImageAttachmentReference(
                    string.IsNullOrWhiteSpace(title) ? Path.GetFileName(path) : title,
                    path,
                    string.IsNullOrWhiteSpace(mediaType) ? "image/*" : mediaType));
            }
        }

        return images;
    }

    private static bool TryGetStringProperty(System.Text.Json.JsonElement element, string propertyName, out string value)
    {
        if (TryGetProperty(element, propertyName, out var property) && property.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetProperty(System.Text.Json.JsonElement element, string propertyName, out System.Text.Json.JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetDraftAttemptId(System.Text.Json.JsonElement? details, out string attemptId)
    {
        attemptId = string.Empty;
        if (details is not { ValueKind: System.Text.Json.JsonValueKind.Object } root ||
            TryGetBooleanProperty(root, "draft", out var isDraft) && !isDraft)
        {
            return false;
        }

        return TryGetStringProperty(root, "attemptId", out attemptId) &&
               !string.IsNullOrWhiteSpace(attemptId);
    }

    private static bool TryGetDiscardDraftAttemptId(System.Text.Json.JsonElement? details, out string attemptId)
    {
        attemptId = string.Empty;
        if (details is not { ValueKind: System.Text.Json.JsonValueKind.Object } root ||
            !TryGetBooleanProperty(root, "discardDraft", out var discardDraft) ||
            !discardDraft)
        {
            return false;
        }

        return TryGetStringProperty(root, "draftAttemptId", out attemptId) &&
               !string.IsNullOrWhiteSpace(attemptId);
    }

    private static bool TryGetBooleanProperty(System.Text.Json.JsonElement element, string propertyName, out bool value)
    {
        if (TryGetProperty(element, propertyName, out var property) &&
            property.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
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
        if (IsFlowFollowingTail())
        {
            _messageNavigationIndex = null;
        }
    }

    private bool IsFlowFollowingTail()
        => UiDispatch.Invoke(_uiDispatcher, static flow => flow.FollowTail, Flow);

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
        Flow.ScrollToTail();
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

    private readonly record struct MessageNavigationAnchor(DocumentFlowItem Item, AgentContentKind Kind);
}
