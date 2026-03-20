using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Formatting;
using CodeAlta.Presentation.Timeline;
using CodeAlta.Views;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.App;

internal sealed class ThreadHistoryCoordinator
{
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly Func<WorkThreadDescriptor, OpenThreadState> _ensureThreadTab;
    private readonly Func<string, WorkThreadDescriptor?> _findThread;
    private readonly Func<string, OpenThreadState?> _findOpenThread;
    private readonly Func<WorkThreadDescriptor, bool> _canLoadHistory;
    private readonly Func<WorkThreadDescriptor, OpenThreadState, WorkThreadExecutionOptions> _buildExecutionOptions;
    private readonly Action<OpenThreadState, string, bool, StatusTone> _setThreadStatus;
    private readonly Action<OpenThreadState> _clearThreadStatus;
    private readonly Action<OpenThreadState> _resetThreadTab;
    private readonly Action<WorkThreadDescriptor, OpenThreadState, AgentEvent> _handleAgentEvent;

    public ThreadHistoryCoordinator(
        WorkThreadRuntimeService runtimeService,
        Func<WorkThreadDescriptor, OpenThreadState> ensureThreadTab,
        Func<string, WorkThreadDescriptor?> findThread,
        Func<string, OpenThreadState?> findOpenThread,
        Func<WorkThreadDescriptor, bool> canLoadHistory,
        Func<WorkThreadDescriptor, OpenThreadState, WorkThreadExecutionOptions> buildExecutionOptions,
        Action<OpenThreadState, string, bool, StatusTone> setThreadStatus,
        Action<OpenThreadState> clearThreadStatus,
        Action<OpenThreadState> resetThreadTab,
        Action<WorkThreadDescriptor, OpenThreadState, AgentEvent> handleAgentEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(ensureThreadTab);
        ArgumentNullException.ThrowIfNull(findThread);
        ArgumentNullException.ThrowIfNull(findOpenThread);
        ArgumentNullException.ThrowIfNull(canLoadHistory);
        ArgumentNullException.ThrowIfNull(buildExecutionOptions);
        ArgumentNullException.ThrowIfNull(setThreadStatus);
        ArgumentNullException.ThrowIfNull(clearThreadStatus);
        ArgumentNullException.ThrowIfNull(resetThreadTab);
        ArgumentNullException.ThrowIfNull(handleAgentEvent);

        _runtimeService = runtimeService;
        _ensureThreadTab = ensureThreadTab;
        _findThread = findThread;
        _findOpenThread = findOpenThread;
        _canLoadHistory = canLoadHistory;
        _buildExecutionOptions = buildExecutionOptions;
        _setThreadStatus = setThreadStatus;
        _clearThreadStatus = clearThreadStatus;
        _resetThreadTab = resetThreadTab;
        _handleAgentEvent = handleAgentEvent;
    }

    public async Task EnsureLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);

        if (!_canLoadHistory(thread))
        {
            return;
        }

        var tab = _ensureThreadTab(thread);
        var loadTask = GetOrStartLoadTask(tab, thread, cancellationToken);
        await loadTask.ConfigureAwait(false);
    }

    public async Task LoadEarlierAsync(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var thread = _findThread(threadId);
        var tab = _findOpenThread(threadId);
        if (thread is null || tab is null || !tab.Timeline.HasLoadableTruncatedHistory)
        {
            return;
        }

        tab.Timeline.ReplaceTruncatedHistoryLoadButton();
        await RebuildAsync(
                thread,
                tab,
                loadOnlyFromLastUserPrompt: false,
                preferCachedHistory: true,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    public static bool CanLoadThreadHistory(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        if (thread.StartedAt is not null)
        {
            return true;
        }

        return thread.Status != WorkThreadStatus.Draft &&
               !string.IsNullOrWhiteSpace(thread.BackendSessionId);
    }

    public static ThreadHistoryLoadPlan CreateInitialLoadPlan(IReadOnlyList<AgentEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var startIndex = FindInitialStartIndex(history);
        if (startIndex <= 0 || startIndex >= history.Count)
        {
            return new ThreadHistoryLoadPlan(history, OmittedMessageCount: 0);
        }

        var eventsToRender = history.Skip(startIndex).ToArray();
        return new ThreadHistoryLoadPlan(
            eventsToRender,
            CountRenderableMessages(history.Take(startIndex)));
    }

    public static int FindInitialStartIndex(IReadOnlyList<AgentEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var lastUserContentId = default(string);
        var lastUserIndex = -1;
        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (TryGetUserContentId(history[index], out var contentId))
            {
                lastUserContentId = contentId;
                lastUserIndex = index;
                break;
            }
        }

        if (lastUserIndex <= 0 || string.IsNullOrWhiteSpace(lastUserContentId))
        {
            return 0;
        }

        var startIndex = lastUserIndex;
        while (startIndex > 0 &&
               TryGetUserContentId(history[startIndex - 1], out var previousContentId) &&
               string.Equals(previousContentId, lastUserContentId, StringComparison.Ordinal))
        {
            startIndex--;
        }

        return startIndex;
    }

    public static int CountRenderableMessages(IEnumerable<AgentEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var contentKeys = new HashSet<string>(StringComparer.Ordinal);
        var activityIds = new HashSet<string>(StringComparer.Ordinal);
        var interactionIds = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;

        foreach (var @event in history)
        {
            switch (@event)
            {
                case AgentContentDeltaEvent delta when ChatMarkdownFormatter.ShouldDisplayContentDelta(delta):
                    if (contentKeys.Add(ChatTimelineVisualFactory.CreateContentKey(delta.Kind, delta.ContentId)))
                    {
                        count++;
                    }

                    break;

                case AgentContentCompletedEvent completed when ShouldDisplayCompletedHistoryContent(completed):
                    if (contentKeys.Add(ChatTimelineVisualFactory.CreateContentKey(completed.Kind, completed.ContentId)))
                    {
                        count++;
                    }

                    break;

                case AgentPlanSnapshotEvent:
                    count++;
                    break;

                case AgentActivityEvent activity when ChatMarkdownFormatter.ShouldDisplayActivity(activity) && activityIds.Add(activity.ActivityId):
                    count++;
                    break;

                case AgentRawEvent raw when ChatMarkdownFormatter.ShouldDisplayRawEvent(raw):
                    count++;
                    break;

                case AgentPermissionRequest permissionRequest when interactionIds.Add(permissionRequest.InteractionId):
                    count++;
                    break;

                case AgentUserInputRequest userInputRequest when interactionIds.Add(userInputRequest.InteractionId):
                    count++;
                    break;

                case AgentInteractionEvent interaction when interactionIds.Add(interaction.InteractionId):
                    count++;
                    break;

                case AgentSessionUpdateEvent update when update.Kind != AgentSessionUpdateKind.Idle && ChatMarkdownFormatter.ShouldDisplaySessionUpdate(update):
                    count++;
                    break;

                case AgentErrorEvent:
                    count++;
                    break;
            }
        }

        return count;
    }

    private Task GetOrStartLoadTask(
        OpenThreadState tab,
        WorkThreadDescriptor thread,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(thread);

        if (tab.HistoryLoaded)
        {
            return Task.CompletedTask;
        }

        if (tab.HistoryLoadTask is { } existingTask)
        {
            return existingTask.WaitAsync(cancellationToken);
        }

        var loadTask = LoadCoreAsync(thread, tab, cancellationToken);
        tab.HistoryLoadTask = loadTask;
        return loadTask.WaitAsync(cancellationToken);
    }

    private async Task LoadCoreAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        CancellationToken cancellationToken)
    {
        await RebuildAsync(
                thread,
                tab,
                loadOnlyFromLastUserPrompt: true,
                preferCachedHistory: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RebuildAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        bool loadOnlyFromLastUserPrompt,
        bool preferCachedHistory,
        CancellationToken cancellationToken)
    {
        tab.HistoryLoading = true;
        try
        {
            _setThreadStatus(
                tab,
                loadOnlyFromLastUserPrompt
                    ? $"Loading thread '{thread.Title}'..."
                    : $"Loading previous messages from '{thread.Title}'...",
                true,
                StatusTone.Info);

            var history = await GetHistoryAsync(thread, tab, preferCachedHistory, cancellationToken).ConfigureAwait(false);
            _resetThreadTab(tab);

            var plan = loadOnlyFromLastUserPrompt
                ? CreateInitialLoadPlan(history)
                : new ThreadHistoryLoadPlan(history, OmittedMessageCount: 0);
            DocumentFlowItem? truncatedHistoryItem = null;
            if (plan.OmittedMessageCount > 0)
            {
                truncatedHistoryItem = tab.Timeline.CreateTruncatedHistoryItem(
                    plan.OmittedMessageCount,
                    () => _ = LoadEarlierAsync(thread.ThreadId));
            }

            tab.Timeline.BeginBufferedHistoryLoad();
            foreach (var @event in plan.EventsToRender)
            {
                _handleAgentEvent(thread, tab, @event);
            }

            tab.Timeline.CompleteInitialBufferedHistory(truncatedHistoryItem);
            tab.Timeline.FlushBufferedHistoryItems();
            tab.HistoryLoaded = true;
            _clearThreadStatus(tab);
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && CodeAltaApp.UiLogger.IsEnabled(LogLevel.Error))
            {
                CodeAltaApp.UiLogger.Error(ex, $"Failed to load history for thread {thread.ThreadId}");
            }

            _resetThreadTab(tab);
            tab.Timeline.FlushBufferedHistoryItems();
            tab.Timeline.RenderFailure($"Failed to load history: {ex.Message}");
            _setThreadStatus(tab, $"Failed to load '{thread.Title}': {ex.Message}", false, StatusTone.Error);
        }
        finally
        {
            tab.HistoryLoading = false;
            tab.HistoryLoadTask = null;
            tab.Timeline.ClearBufferedHistory();
        }
    }

    private async Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        bool preferCachedHistory,
        CancellationToken cancellationToken)
    {
        if (preferCachedHistory && tab.HistoryEvents is { Count: > 0 } cachedHistory)
        {
            return cachedHistory;
        }

        var executionOptions = _buildExecutionOptions(thread, tab);
        await _runtimeService.EnsureCoordinatorSessionAsync(thread, executionOptions, cancellationToken).ConfigureAwait(false);
        var history = (await _runtimeService.GetHistoryAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false)).ToList();
        tab.HistoryEvents = history;
        return history;
    }

    private static bool ShouldDisplayCompletedHistoryContent(AgentContentCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        if (!ChatMarkdownFormatter.ShouldDisplayCompletedContent(completed))
        {
            return false;
        }

        return completed.Kind != AgentContentKind.Assistant || !string.IsNullOrWhiteSpace(completed.Content);
    }

    private static bool TryGetUserContentId(AgentEvent @event, out string? contentId)
    {
        switch (@event)
        {
            case AgentContentDeltaEvent { Kind: AgentContentKind.User } delta:
                contentId = delta.ContentId;
                return !string.IsNullOrWhiteSpace(contentId);
            case AgentContentCompletedEvent { Kind: AgentContentKind.User } completed:
                contentId = completed.ContentId;
                return !string.IsNullOrWhiteSpace(contentId);
            default:
                contentId = null;
                return false;
        }
    }
}