using CodeAlta.App.State;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Formatting;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Usage;
using CodeAlta.Views;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class ThreadRuntimeEventCoordinator
{
    private readonly Func<string, WorkThreadDescriptor?> _findThread;
    private readonly Func<string, OpenThreadState?> _findOpenThread;
    private readonly Func<bool> _getAutoApproveEnabled;
    private readonly Func<string, bool> _isSelectedThread;
    private readonly Action _invalidateSelectedSessionUsage;
    private readonly Action _refreshShellChrome;
    private readonly Action<string, bool, StatusTone> _setShellStatus;
    private readonly Action<OpenThreadState, string, bool, StatusTone> _setThreadStatus;
    private readonly Action<OpenThreadState> _clearThreadStatus;
    private readonly Func<OpenThreadState, string, bool> _consumePendingSteerForLiveUserContent;
    private readonly Action<OpenThreadState> _clearPendingSteers;
    private readonly Func<OpenThreadState, CancellationToken, Task> _drainQueuedPromptAsync;

    public ThreadRuntimeEventCoordinator(
        Func<string, WorkThreadDescriptor?> findThread,
        Func<string, OpenThreadState?> findOpenThread,
        Func<bool> getAutoApproveEnabled,
        Func<string, bool> isSelectedThread,
        Action invalidateSelectedSessionUsage,
        Action refreshShellChrome,
        Action<string, bool, StatusTone> setShellStatus,
        Action<OpenThreadState, string, bool, StatusTone> setThreadStatus,
        Action<OpenThreadState> clearThreadStatus,
        Func<OpenThreadState, string, bool> consumePendingSteerForLiveUserContent,
        Action<OpenThreadState> clearPendingSteers,
        Func<OpenThreadState, CancellationToken, Task> drainQueuedPromptAsync)
    {
        ArgumentNullException.ThrowIfNull(findThread);
        ArgumentNullException.ThrowIfNull(findOpenThread);
        ArgumentNullException.ThrowIfNull(getAutoApproveEnabled);
        ArgumentNullException.ThrowIfNull(isSelectedThread);
        ArgumentNullException.ThrowIfNull(invalidateSelectedSessionUsage);
        ArgumentNullException.ThrowIfNull(refreshShellChrome);
        ArgumentNullException.ThrowIfNull(setShellStatus);
        ArgumentNullException.ThrowIfNull(setThreadStatus);
        ArgumentNullException.ThrowIfNull(clearThreadStatus);
        ArgumentNullException.ThrowIfNull(consumePendingSteerForLiveUserContent);
        ArgumentNullException.ThrowIfNull(clearPendingSteers);
        ArgumentNullException.ThrowIfNull(drainQueuedPromptAsync);

        _findThread = findThread;
        _findOpenThread = findOpenThread;
        _getAutoApproveEnabled = getAutoApproveEnabled;
        _isSelectedThread = isSelectedThread;
        _invalidateSelectedSessionUsage = invalidateSelectedSessionUsage;
        _refreshShellChrome = refreshShellChrome;
        _setShellStatus = setShellStatus;
        _setThreadStatus = setThreadStatus;
        _clearThreadStatus = clearThreadStatus;
        _consumePendingSteerForLiveUserContent = consumePendingSteerForLiveUserContent;
        _clearPendingSteers = clearPendingSteers;
        _drainQueuedPromptAsync = drainQueuedPromptAsync;
    }

    public void ApplyRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        var thread = _findThread(runtimeEvent.ThreadId);
        if (thread is null)
        {
            return;
        }

        switch (runtimeEvent)
        {
            case WorkThreadAgentEvent agentEvent:
                UpdateThreadFromAgentEvent(thread, agentEvent.Event);
                if (_findOpenThread(thread.ThreadId) is { } tab)
                {
                    tab.HistoryEvents?.Add(agentEvent.Event);
                    TryRenderInteraction(tab, () => HandleAgentEvent(thread, tab, agentEvent.Event), "agent event");
                }

                break;

            case WorkThreadHostEvent hostEvent:
                UpdateThreadSummary(thread, hostEvent.Message, hostEvent.Timestamp);
                if (_findOpenThread(thread.ThreadId) is { } hostTab)
                {
                    if (hostEvent.Kind == AgentSessionUpdateKind.CompactionStarted && hostTab.PendingManualCompaction)
                    {
                        _setThreadStatus(hostTab, $"Compacting '{thread.Title}'...", true, StatusTone.Info);
                    }

                    if (hostEvent.Kind == AgentSessionUpdateKind.CompactionCompleted && hostTab.PendingManualCompaction)
                    {
                        hostTab.PendingManualCompaction = false;
                        _clearThreadStatus(hostTab);
                    }

                    TryRenderInteraction(
                        hostTab,
                        () => hostTab.Timeline.AddStatus(
                            hostEvent.Timestamp,
                            markdown: hostEvent.Message,
                            tone: ChatTimelineTone.Notice,
                            headerOverride: "Notice",
                            headerSecondary: ChatMarkdownFormatter.GetSessionUpdateHeader(hostEvent.Kind)),
                        "host event");
                }

                break;
        }

        if (ShouldRefreshShellChromeAfterRuntimeEvent(runtimeEvent))
        {
            _refreshShellChrome();
        }
    }

    public void HandleAgentEvent(WorkThreadDescriptor thread, OpenThreadState tab, AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(@event);

        ObserveActiveRun(tab, @event);

        if (!tab.HistoryLoading && !tab.PendingManualCompaction && ShouldPromoteAgentEventToThinking(@event))
        {
            _setThreadStatus(tab, StatusVisualFormatter.BuildThinkingStatusText(), true, StatusTone.Info);
        }

        switch (@event)
        {
            case AgentContentDeltaEvent delta:
                ConsumePendingSteerIfMaterialized(tab, delta.Kind, delta.ContentId);
                if (tab.Timeline.ToolCalls.TryHandleContent(delta))
                {
                    break;
                }

                if (!ChatMarkdownFormatter.ShouldDisplayContentDelta(delta))
                {
                    break;
                }

                tab.Timeline.AppendContent(delta);
                break;

            case AgentContentCompletedEvent completed:
                ConsumePendingSteerIfMaterialized(tab, completed.Kind, completed.ContentId);
                if (tab.Timeline.ToolCalls.TryHandleContent(completed))
                {
                    break;
                }

                if (tab.Timeline.ShouldSkipEmptyAssistantCompletion(completed))
                {
                    break;
                }

                if (!ChatMarkdownFormatter.ShouldDisplayCompletedContent(completed))
                {
                    break;
                }

                tab.Timeline.FinalizeContent(completed);
                if (completed.Kind == AgentContentKind.Assistant && !string.IsNullOrWhiteSpace(completed.Content))
                {
                    thread.LatestSummary = SummarizeContent(completed.Content);
                }

                break;

            case AgentPlanSnapshotEvent planEvent:
                tab.Timeline.UpsertPlanStatus(
                    "plan",
                    planEvent.Timestamp,
                    ChatMarkdownFormatter.FormatChatPlanMarkdown(planEvent.Snapshot),
                    ChatTimelineTone.Notice,
                    headerOverride: "Plan");
                break;

            case AgentActivityEvent activity:
                tab.Timeline.FileChanges.ObserveActivity(activity);
                if (tab.Timeline.ToolCalls.TryHandleActivity(activity))
                {
                    break;
                }

                if (!ChatMarkdownFormatter.ShouldDisplayActivity(activity))
                {
                    break;
                }

                tab.Timeline.UpsertActivityStatus(
                    activity.ActivityId,
                    activity.Timestamp,
                    ChatMarkdownFormatter.FormatChatActivityMarkdown(activity),
                    ChatTimelineTone.Activity,
                    headerOverride: ChatMarkdownFormatter.GetActivityHeadline(activity.Kind, activity.Phase));
                break;

            case AgentRawEvent raw:
                if (!ChatMarkdownFormatter.ShouldDisplayRawEvent(raw))
                {
                    break;
                }

                tab.Timeline.AddStatus(
                    raw.Timestamp,
                    ChatMarkdownFormatter.FormatChatRawEventMarkdown(raw),
                    ChatTimelineTone.Activity,
                    headerOverride: "Raw Event");
                break;

            case AgentPermissionRequest permissionRequest:
                if (!ChatMarkdownFormatter.ShouldDisplayPermissionRequest(_getAutoApproveEnabled()))
                {
                    break;
                }

                tab.PermissionRequests[permissionRequest.InteractionId] = permissionRequest;
                tab.Timeline.UpsertInteraction(
                    permissionRequest.InteractionId,
                    permissionRequest.Timestamp,
                    ChatMarkdownFormatter.FormatChatPermissionRequestMarkdown(permissionRequest),
                    null,
                    ChatTimelineTone.Interaction,
                    "Action Required",
                    "Permission Request");
                break;

            case AgentUserInputRequest userInputRequest:
                var autoApproveEnabled = _getAutoApproveEnabled();
                tab.UserInputRequests[userInputRequest.InteractionId] = userInputRequest;
                tab.Timeline.UpsertInteraction(
                    userInputRequest.InteractionId,
                    userInputRequest.Timestamp,
                    ChatMarkdownFormatter.FormatChatUserInputRequestMarkdown(userInputRequest, autoApproveEnabled),
                    null,
                    ChatTimelineTone.Interaction,
                    "Action Required",
                    "User Input Request");
                break;

            case AgentInteractionEvent interaction:
                if (!ChatMarkdownFormatter.ShouldDisplayInteraction(interaction, _getAutoApproveEnabled()))
                {
                    tab.PermissionRequests.Remove(interaction.InteractionId);
                    tab.UserInputRequests.Remove(interaction.InteractionId);
                    break;
                }

                tab.Timeline.UpsertInteraction(
                    interaction.InteractionId,
                    interaction.Timestamp,
                    null,
                    ChatMarkdownFormatter.FormatChatInteractionResolutionMarkdown(interaction, includeHeading: false),
                    ChatTimelineTone.Interaction);
                tab.PermissionRequests.Remove(interaction.InteractionId);
                tab.UserInputRequests.Remove(interaction.InteractionId);
                break;

            case AgentSessionUpdateEvent update:
                tab.Timeline.FileChanges.ObserveSessionUpdate(update);
                if (update.Usage is { } usage)
                {
                    tab.Usage = SessionUsageAggregator.Merge(tab.Usage, usage);
                    if (_isSelectedThread(thread.ThreadId))
                    {
                        _invalidateSelectedSessionUsage();
                    }
                }

                if (update.Kind == AgentSessionUpdateKind.CompactionStarted && tab.PendingManualCompaction)
                {
                    _setThreadStatus(tab, $"Compacting '{thread.Title}'...", true, StatusTone.Info);
                }

                if (update.Kind == AgentSessionUpdateKind.CompactionCompleted && tab.PendingManualCompaction)
                {
                    tab.PendingManualCompaction = false;
                    _clearThreadStatus(tab);
                }

                if (update.Kind == AgentSessionUpdateKind.Idle)
                {
                    tab.PendingManualCompaction = false;
                    _clearPendingSteers(tab);
                    _clearThreadStatus(tab);
                    if (tab.QueuedPrompts.Count > 0)
                    {
                        _ = _drainQueuedPromptAsync(tab, CancellationToken.None);
                    }

                    break;
                }

                if (update.Kind == AgentSessionUpdateKind.Shutdown)
                {
                    _clearPendingSteers(tab);
                }

                if (!ChatMarkdownFormatter.ShouldDisplaySessionUpdate(update))
                {
                    break;
                }

                tab.Timeline.AddStatus(
                    update.Timestamp,
                    ChatMarkdownFormatter.FormatChatSessionUpdateMarkdown(update),
                    update.Kind == AgentSessionUpdateKind.Warning ? ChatTimelineTone.Interaction : ChatTimelineTone.Notice,
                    headerOverride: "Notice",
                    headerSecondary: ChatMarkdownFormatter.GetSessionUpdateHeader(update.Kind));
                if (!string.IsNullOrWhiteSpace(update.Message))
                {
                    thread.LatestSummary = SummarizeContent(update.Message);
                }

                break;

            case AgentErrorEvent error:
                tab.PendingManualCompaction = false;
                _clearPendingSteers(tab);
                tab.Timeline.RenderError(error.Message, error.Timestamp);
                thread.LatestSummary = SummarizeContent(error.Message);
                _setThreadStatus(tab, error.Message, false, StatusTone.Error);
                break;
        }
    }

    private void ConsumePendingSteerIfMaterialized(OpenThreadState tab, AgentContentKind kind, string contentId)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);

        if (tab.HistoryLoading || kind != AgentContentKind.User)
        {
            return;
        }

        _ = _consumePendingSteerForLiveUserContent(tab, contentId);
    }

    private static void ObserveActiveRun(OpenThreadState tab, AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(@event);

        if (@event.RunId is { } runId)
        {
            tab.ActiveRunId = runId;
        }

        if (@event is AgentErrorEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.Shutdown })
        {
            tab.ActiveRunId = null;
        }
    }

    public void TryRenderInteraction(OpenThreadState tab, Action action, string context)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && CodeAltaApp.UiLogger.IsEnabled(LogLevel.Error))
            {
                CodeAltaApp.UiLogger.Error(ex, $"Failed to render thread {context}");
            }

            _setShellStatus($"Failed to render thread {context}: {ex.Message}", false, StatusTone.Error);
            tab.Timeline.ClearPendingAssistant();
        }
    }

    public static bool ShouldPromoteAgentEventToThinking(AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return @event switch
        {
            AgentContentDeltaEvent
            {
                Delta.Length: > 0,
                Kind: not (AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput)
            } => true,
            AgentContentCompletedEvent completed
                when !string.IsNullOrWhiteSpace(completed.Content) &&
                     completed.Kind is not (AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput) => true,
            AgentPlanSnapshotEvent => true,
            AgentActivityEvent { Phase: AgentActivityPhase.Requested or AgentActivityPhase.Started or AgentActivityPhase.Progressed or AgentActivityPhase.Completed } => true,
            AgentSessionUpdateEvent
            {
                Kind: AgentSessionUpdateKind.Started
                or AgentSessionUpdateKind.Resumed
                or AgentSessionUpdateKind.PlanUpdated
                or AgentSessionUpdateKind.CompactionStarted
            } => true,
            _ => false,
        };
    }

    public static bool ShouldRefreshShellChromeAfterRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        return runtimeEvent switch
        {
            WorkThreadHostEvent => true,
            WorkThreadAgentEvent
            {
                Event: AgentContentCompletedEvent
                {
                    Kind: AgentContentKind.Assistant,
                    Content.Length: > 0
                }
            } => true,
            WorkThreadAgentEvent
            {
                Event: AgentSessionUpdateEvent
                {
                    Kind: not AgentSessionUpdateKind.UsageUpdated
                }
            } => true,
            WorkThreadAgentEvent { Event: AgentErrorEvent } => true,
            _ => false,
        };
    }

    public static string SummarizeContent(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length <= 120)
        {
            return normalized;
        }

        return normalized[..117].TrimEnd() + "...";
    }

    private static void UpdateThreadFromAgentEvent(WorkThreadDescriptor thread, AgentEvent @event)
    {
        thread.UpdatedAt = @event.Timestamp;
        thread.LastActiveAt = @event.Timestamp;

        switch (@event)
        {
            case AgentContentCompletedEvent { Kind: AgentContentKind.Assistant } completed when !string.IsNullOrWhiteSpace(completed.Content):
                thread.LatestSummary = SummarizeContent(completed.Content);
                break;
            case AgentSessionUpdateEvent update when !string.IsNullOrWhiteSpace(update.Message):
                if (update.Kind == AgentSessionUpdateKind.UsageUpdated)
                {
                    break;
                }

                thread.LatestSummary = SummarizeContent(update.Message);
                break;
            case AgentErrorEvent error when !string.IsNullOrWhiteSpace(error.Message):
                thread.LatestSummary = SummarizeContent(error.Message);
                break;
        }
    }

    private static void UpdateThreadSummary(WorkThreadDescriptor thread, string message, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        thread.UpdatedAt = timestamp;
        thread.LastActiveAt = timestamp;
        thread.LatestSummary = SummarizeContent(message);
    }
}
