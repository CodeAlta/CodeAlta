using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Usage;

namespace CodeAlta.App;

internal readonly record struct ThreadRuntimeStatusUpdate(
    string Message,
    bool ShowSpinner,
    StatusTone Tone);

internal readonly record struct ThreadRuntimeReductionResult(
    bool ApplyShellChromeProjection,
    bool ApplySessionUsageProjection,
    bool ClearThreadStatus,
    bool DrainQueuedPrompt,
    bool RefreshPromptStrip,
    ThreadRuntimeStatusUpdate? ThreadStatus);

internal sealed class ThreadRuntimeStateReducer
{
    public ThreadRuntimeReductionResult ReduceRuntimeEvent(
        WorkThreadDescriptor thread,
        OpenThreadState? tab,
        WorkThreadRuntimeEvent runtimeEvent,
        bool isSelectedThread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        return runtimeEvent switch
        {
            WorkThreadAgentEvent agentEvent => ReduceAgentEvent(thread, tab, agentEvent.Event, isSelectedThread, ShouldApplyShellChromeProjectionAfterRuntimeEvent(runtimeEvent)),
            WorkThreadHostEvent hostEvent => ReduceHostEvent(thread, tab, hostEvent),
            _ => default,
        };
    }

    public ThreadRuntimeReductionResult ReduceAgentEvent(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        AgentEvent @event,
        bool isSelectedThread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(@event);

        return ReduceAgentEvent(thread, tab, @event, isSelectedThread, refreshShellChrome: false);
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
            AgentActivityEvent
            {
                Kind: not AgentActivityKind.Compaction,
                Phase: AgentActivityPhase.Requested or AgentActivityPhase.Started or AgentActivityPhase.Progressed or AgentActivityPhase.Completed
            } => true,
            AgentSessionUpdateEvent
            {
                Kind: AgentSessionUpdateKind.Started
                or AgentSessionUpdateKind.Resumed
                or AgentSessionUpdateKind.PlanUpdated
            } => true,
            _ => false,
        };
    }

    public static bool ShouldApplyShellChromeProjectionAfterRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
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

    private static ThreadRuntimeReductionResult ReduceHostEvent(
        WorkThreadDescriptor thread,
        OpenThreadState? tab,
        WorkThreadHostEvent hostEvent)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(hostEvent);

        UpdateThreadSummary(thread, hostEvent.Message, hostEvent.Timestamp);
        ThreadRuntimeStatusUpdate? threadStatus = null;
        var clearThreadStatus = false;
        if (tab is not null)
        {
            if (hostEvent.Kind == AgentSessionUpdateKind.CompactionStarted && tab.PendingManualCompaction)
            {
                threadStatus = new ThreadRuntimeStatusUpdate($"Compacting '{thread.Title}'...", true, StatusTone.Info);
            }

            if (hostEvent.Kind == AgentSessionUpdateKind.CompactionCompleted && tab.PendingManualCompaction)
            {
                tab.PendingManualCompaction = false;
                clearThreadStatus = true;
            }
        }

        return new ThreadRuntimeReductionResult(
            ApplyShellChromeProjection: true,
            ApplySessionUsageProjection: false,
            ClearThreadStatus: clearThreadStatus,
            DrainQueuedPrompt: false,
            RefreshPromptStrip: false,
            ThreadStatus: threadStatus);
    }

    private static ThreadRuntimeReductionResult ReduceAgentEvent(
        WorkThreadDescriptor thread,
        OpenThreadState? tab,
        AgentEvent @event,
        bool isSelectedThread,
        bool refreshShellChrome)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(@event);

        UpdateThreadFromAgentEvent(thread, @event);
        if (tab is null)
        {
            return new ThreadRuntimeReductionResult(refreshShellChrome, false, false, false, false, null);
        }

        ObserveActiveRun(tab, @event);

        ThreadRuntimeStatusUpdate? threadStatus = null;
        var clearThreadStatus = false;
        var invalidateSelectedUsage = false;
        var drainQueuedPrompt = false;
        var refreshPromptStrip = false;

        if (!tab.HistoryLoading && !tab.PendingManualCompaction && ShouldPromoteAgentEventToThinking(@event))
        {
            threadStatus = new ThreadRuntimeStatusUpdate(StatusVisualFormatter.BuildThinkingStatusText(), true, StatusTone.Info);
        }

        switch (@event)
        {
            case AgentContentDeltaEvent delta:
                refreshPromptStrip = ConsumePendingSteerIfMaterialized(tab, delta.Kind, delta.ContentId) || refreshPromptStrip;
                break;

            case AgentContentCompletedEvent completed:
                refreshPromptStrip = ConsumePendingSteerIfMaterialized(tab, completed.Kind, completed.ContentId) || refreshPromptStrip;
                if (completed.Kind == AgentContentKind.Assistant && !string.IsNullOrWhiteSpace(completed.Content))
                {
                    thread.LatestSummary = SummarizeContent(completed.Content);
                }

                break;

            case AgentPermissionRequest permissionRequest:
                tab.PermissionRequests[permissionRequest.InteractionId] = permissionRequest;
                break;

            case AgentUserInputRequest userInputRequest:
                tab.UserInputRequests[userInputRequest.InteractionId] = userInputRequest;
                break;

            case AgentInteractionEvent interaction:
                tab.PermissionRequests.Remove(interaction.InteractionId);
                tab.UserInputRequests.Remove(interaction.InteractionId);
                break;

            case AgentSessionUpdateEvent update:
                if (update.Usage is { } usage)
                {
                    tab.Usage = SessionUsageAggregator.Merge(tab.Usage, usage);
                    invalidateSelectedUsage = isSelectedThread;
                }

                if (update.Kind == AgentSessionUpdateKind.CompactionStarted && tab.PendingManualCompaction)
                {
                    threadStatus = new ThreadRuntimeStatusUpdate($"Compacting '{thread.Title}'...", true, StatusTone.Info);
                }

                if (update.Kind == AgentSessionUpdateKind.Reconnecting && !string.IsNullOrWhiteSpace(update.Message))
                {
                    threadStatus = new ThreadRuntimeStatusUpdate(update.Message, true, StatusTone.Info);
                }

                if (update.Kind is AgentSessionUpdateKind.Info or AgentSessionUpdateKind.Warning &&
                    !string.IsNullOrWhiteSpace(update.Message))
                {
                    threadStatus = new ThreadRuntimeStatusUpdate(
                        update.Message,
                        true,
                        update.Kind == AgentSessionUpdateKind.Warning ? StatusTone.Warning : StatusTone.Info);
                }

                if (update.Kind == AgentSessionUpdateKind.CompactionCompleted && tab.PendingManualCompaction)
                {
                    tab.PendingManualCompaction = false;
                    clearThreadStatus = true;
                }

                if (update.Kind == AgentSessionUpdateKind.Idle)
                {
                    tab.PendingManualCompaction = false;
                    refreshPromptStrip = ClearPendingSteers(tab) || refreshPromptStrip;
                    clearThreadStatus = true;
                    drainQueuedPrompt = tab.QueuedPrompts.Count > 0;
                }
                else if (update.Kind == AgentSessionUpdateKind.Shutdown)
                {
                    refreshPromptStrip = ClearPendingSteers(tab) || refreshPromptStrip;
                }

                if (!string.IsNullOrWhiteSpace(update.Message) &&
                    update.Kind != AgentSessionUpdateKind.UsageUpdated)
                {
                    thread.LatestSummary = SummarizeContent(update.Message);
                }

                break;

            case AgentErrorEvent error:
                tab.PendingManualCompaction = false;
                refreshPromptStrip = ClearPendingSteers(tab) || refreshPromptStrip;
                thread.LatestSummary = SummarizeContent(error.Message);
                threadStatus = new ThreadRuntimeStatusUpdate(error.Message, false, StatusTone.Error);
                break;
        }

        return new ThreadRuntimeReductionResult(
            ApplyShellChromeProjection: refreshShellChrome,
            ApplySessionUsageProjection: invalidateSelectedUsage,
            ClearThreadStatus: clearThreadStatus,
            DrainQueuedPrompt: drainQueuedPrompt,
            RefreshPromptStrip: refreshPromptStrip,
            ThreadStatus: threadStatus);
    }

    private static bool ConsumePendingSteerIfMaterialized(OpenThreadState tab, AgentContentKind kind, string contentId)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);

        if (tab.HistoryLoading || kind != AgentContentKind.User)
        {
            return false;
        }

        if (string.Equals(tab.LastObservedPendingSteerUserContentId, contentId, StringComparison.Ordinal))
        {
            return false;
        }

        tab.LastObservedPendingSteerUserContentId = contentId;
        if (tab.PendingSteers.Count == 0)
        {
            return false;
        }

        tab.PendingSteers.RemoveAt(0);
        if (tab.PendingSteers.Count == 0)
        {
            tab.LastObservedPendingSteerUserContentId = null;
        }

        return true;
    }

    private static bool ClearPendingSteers(OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.PendingSteers.Count == 0)
        {
            tab.LastObservedPendingSteerUserContentId = null;
            return false;
        }

        tab.PendingSteers.Clear();
        tab.LastObservedPendingSteerUserContentId = null;
        return true;
    }

    private static void ObserveActiveRun(OpenThreadState tab, AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(@event);

        if (@event.RunId is { } runId && ShouldTrackRunId(@event))
        {
            if (tab.ActiveRunId != runId)
            {
                tab.ActiveRunStartedAt = @event.Timestamp;
            }

            tab.ActiveRunId = runId;
            tab.ActiveRunStartedAt ??= @event.Timestamp;
        }

        if (@event is AgentErrorEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.Shutdown })
        {
            tab.ActiveRunId = null;
            tab.ActiveRunStartedAt = null;
        }
    }

    private static bool ShouldTrackRunId(AgentEvent @event)
    {
        if (@event is not AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.CompactionStarted or AgentSessionUpdateKind.CompactionCompleted } &&
            @event is not AgentActivityEvent { Kind: AgentActivityKind.Compaction })
        {
            return true;
        }

        // Some backends report manual idle compaction with a turn/run id even though it
        // does not leave an agent run in flight. Do not let those compaction lifecycle
        // updates replace or revive the active run; if a real run is already active, its
        // existing tracking remains in place so running-thread compaction stays busy.
        return false;
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
