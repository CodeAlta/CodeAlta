using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Usage;

namespace CodeAlta.App;

internal readonly record struct SessionRuntimeStatusUpdate(
    string Message,
    bool ShowSpinner,
    StatusTone Tone);

internal readonly record struct SessionRuntimeReductionResult(
    bool ApplyShellChromeProjection,
    bool ApplySessionUsageProjection,
    bool ClearSessionStatus,
    bool DrainQueuedPrompt,
    bool RefreshPromptStrip,
    SessionRuntimeStatusUpdate? SessionStatus);

internal sealed class SessionRuntimeStateReducer
{
    public SessionRuntimeReductionResult ReduceRuntimeEvent(
        SessionViewDescriptor session,
        OpenSessionState? tab,
        SessionRuntimeEvent runtimeEvent,
        bool isSelectedSession)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        return runtimeEvent switch
        {
            SessionAgentEvent agentEvent => ReduceAgentEvent(session, tab, agentEvent.Event, isSelectedSession, ShouldApplyShellChromeProjectionAfterRuntimeEvent(runtimeEvent)),
            SessionHostEvent hostEvent => ReduceHostEvent(session, tab, hostEvent),
            SessionLifecycleRuntimeEvent => new SessionRuntimeReductionResult(true, false, false, false, false, null),
            SessionQueueRuntimeEvent => new SessionRuntimeReductionResult(true, false, false, false, false, null),
            _ => default,
        };
    }

    public SessionRuntimeReductionResult ReduceAgentEvent(
        SessionViewDescriptor session,
        OpenSessionState tab,
        AgentEvent @event,
        bool isSelectedSession)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(@event);

        return ReduceAgentEvent(session, tab, @event, isSelectedSession, refreshShellChrome: false);
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

    public static bool ShouldApplyShellChromeProjectionAfterRuntimeEvent(SessionRuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        return runtimeEvent switch
        {
            SessionHostEvent => true,
            SessionLifecycleRuntimeEvent => true,
            SessionCatalogRuntimeEvent => true,
            SessionQueueRuntimeEvent => true,
            SessionAgentEvent
            {
                Event: AgentContentCompletedEvent
                {
                    Kind: AgentContentKind.Assistant,
                    Content.Length: > 0
                }
            } => true,
            SessionAgentEvent
            {
                Event: AgentSessionUpdateEvent
                {
                    Kind: not AgentSessionUpdateKind.UsageUpdated
                }
            } => true,
            SessionAgentEvent { Event: AgentErrorEvent } => true,
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

    private static SessionRuntimeReductionResult ReduceHostEvent(
        SessionViewDescriptor session,
        OpenSessionState? tab,
        SessionHostEvent hostEvent)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(hostEvent);

        UpdateSessionSummary(session, hostEvent.Message, hostEvent.Timestamp);
        SessionRuntimeStatusUpdate? sessionStatus = null;
        var clearSessionStatus = false;
        if (tab is not null)
        {
            if (hostEvent.Kind == AgentSessionUpdateKind.CompactionStarted && tab.PendingManualCompaction)
            {
                sessionStatus = new SessionRuntimeStatusUpdate(SR.T("Compacting '{0}'...", session.Title), true, StatusTone.Info);
            }

            if (hostEvent.Kind == AgentSessionUpdateKind.CompactionCompleted && tab.PendingManualCompaction)
            {
                tab.PendingManualCompaction = false;
                clearSessionStatus = true;
            }
        }

        return new SessionRuntimeReductionResult(
            ApplyShellChromeProjection: true,
            ApplySessionUsageProjection: false,
            ClearSessionStatus: clearSessionStatus,
            DrainQueuedPrompt: false,
            RefreshPromptStrip: false,
            SessionStatus: sessionStatus);
    }

    private static SessionRuntimeReductionResult ReduceAgentEvent(
        SessionViewDescriptor session,
        OpenSessionState? tab,
        AgentEvent @event,
        bool isSelectedSession,
        bool refreshShellChrome)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(@event);

        UpdateSessionFromAgentEvent(session, @event);
        if (tab is null)
        {
            return new SessionRuntimeReductionResult(refreshShellChrome, false, false, false, false, null);
        }

        ObserveActiveRun(tab, @event);

        SessionRuntimeStatusUpdate? sessionStatus = null;
        var clearSessionStatus = false;
        var invalidateSelectedUsage = false;
        var drainQueuedPrompt = false;
        var refreshPromptStrip = false;

        if (!tab.HistoryLoading && !tab.PendingManualCompaction && ShouldPromoteAgentEventToThinking(@event))
        {
            sessionStatus = new SessionRuntimeStatusUpdate(StatusVisualFormatter.BuildThinkingStatusText(), true, StatusTone.Info);
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
                    session.LatestSummary = SummarizeContent(completed.Content);
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
                    invalidateSelectedUsage = isSelectedSession;
                }

                if (update.Kind == AgentSessionUpdateKind.CompactionStarted && tab.PendingManualCompaction)
                {
                    sessionStatus = new SessionRuntimeStatusUpdate(SR.T("Compacting '{0}'...", session.Title), true, StatusTone.Info);
                }

                if (update.Kind == AgentSessionUpdateKind.Reconnecting && !string.IsNullOrWhiteSpace(update.Message))
                {
                    sessionStatus = new SessionRuntimeStatusUpdate(update.Message, true, StatusTone.Info);
                }

                if (update.Kind is AgentSessionUpdateKind.Info or AgentSessionUpdateKind.Warning &&
                    !string.IsNullOrWhiteSpace(update.Message))
                {
                    sessionStatus = new SessionRuntimeStatusUpdate(
                        update.Message,
                        true,
                        update.Kind == AgentSessionUpdateKind.Warning ? StatusTone.Warning : StatusTone.Info);
                }

                if (update.Kind == AgentSessionUpdateKind.CompactionCompleted && tab.PendingManualCompaction)
                {
                    tab.PendingManualCompaction = false;
                    clearSessionStatus = true;
                }

                if (update.Kind == AgentSessionUpdateKind.Idle)
                {
                    tab.PendingManualCompaction = false;
                    refreshPromptStrip = ClearPendingSteers(tab) || refreshPromptStrip;
                    clearSessionStatus = true;
                    drainQueuedPrompt = tab.QueuedPrompts.Count > 0;
                }
                else if (update.Kind == AgentSessionUpdateKind.Shutdown)
                {
                    refreshPromptStrip = ClearPendingSteers(tab) || refreshPromptStrip;
                }

                if (!string.IsNullOrWhiteSpace(update.Message) &&
                    update.Kind != AgentSessionUpdateKind.UsageUpdated)
                {
                    session.LatestSummary = SummarizeContent(update.Message);
                }

                break;

            case AgentErrorEvent error:
                tab.PendingManualCompaction = false;
                refreshPromptStrip = ClearPendingSteers(tab) || refreshPromptStrip;
                session.LatestSummary = SummarizeContent(error.Message);
                sessionStatus = new SessionRuntimeStatusUpdate(error.Message, false, StatusTone.Error);
                break;
        }

        return new SessionRuntimeReductionResult(
            ApplyShellChromeProjection: refreshShellChrome,
            ApplySessionUsageProjection: invalidateSelectedUsage,
            ClearSessionStatus: clearSessionStatus,
            DrainQueuedPrompt: drainQueuedPrompt,
            RefreshPromptStrip: refreshPromptStrip,
            SessionStatus: sessionStatus);
    }

    private static bool ConsumePendingSteerIfMaterialized(OpenSessionState tab, AgentContentKind kind, string contentId)
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

    private static bool ClearPendingSteers(OpenSessionState tab)
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

    private static void ObserveActiveRun(OpenSessionState tab, AgentEvent @event)
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

    internal static bool ShouldTrackRunId(AgentEvent @event)
    {
        if (@event is not AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.CompactionStarted or AgentSessionUpdateKind.CompactionCompleted } &&
            @event is not AgentActivityEvent { Kind: AgentActivityKind.Compaction })
        {
            return true;
        }

        // Some providers report manual idle compaction with a turn/run id even though it
        // does not leave an agent run in flight. Do not let those compaction lifecycle
        // updates replace or revive the active run; if a real run is already active, its
        // existing tracking remains in place so running-session compaction stays busy.
        return false;
    }

    private static void UpdateSessionFromAgentEvent(SessionViewDescriptor session, AgentEvent @event)
    {
        session.UpdatedAt = @event.Timestamp;
        session.LastActiveAt = @event.Timestamp;

        switch (@event)
        {
            case AgentContentCompletedEvent { Kind: AgentContentKind.Assistant } completed when !string.IsNullOrWhiteSpace(completed.Content):
                session.LatestSummary = SummarizeContent(completed.Content);
                break;
            case AgentSessionUpdateEvent update when !string.IsNullOrWhiteSpace(update.Message):
                if (update.Kind == AgentSessionUpdateKind.UsageUpdated)
                {
                    break;
                }

                session.LatestSummary = SummarizeContent(update.Message);
                break;
            case AgentErrorEvent error when !string.IsNullOrWhiteSpace(error.Message):
                session.LatestSummary = SummarizeContent(error.Message);
                break;
        }
    }

    private static void UpdateSessionSummary(SessionViewDescriptor session, string message, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        session.UpdatedAt = timestamp;
        session.LastActiveAt = timestamp;
        session.LatestSummary = SummarizeContent(message);
    }
}
