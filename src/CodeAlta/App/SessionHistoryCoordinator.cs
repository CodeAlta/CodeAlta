using System.Text.Json;
using CodeAlta.App.State;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Formatting;
using CodeAlta.Presentation.Timeline;
using CodeAlta.Presentation.Usage;
using CodeAlta.Views;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.App;

internal sealed class SessionHistoryCoordinator
{
    private readonly SessionRuntimeService _runtimeService;
    private readonly Func<SessionViewDescriptor, OpenSessionState> _ensureSessionTab;
    private readonly Func<string, SessionViewDescriptor?> _findSession;
    private readonly Func<string, OpenSessionState?> _findOpenSession;
    private readonly Func<SessionViewDescriptor, bool> _canLoadHistory;
    private readonly Func<SessionViewDescriptor, OpenSessionState, SessionExecutionOptions> _buildExecutionOptions;
    private readonly Action<OpenSessionState, string, bool, StatusTone> _setSessionStatus;
    private readonly Action<OpenSessionState> _clearSessionStatus;
    private readonly Action<OpenSessionState> _resetSessionTab;
    private readonly Func<SessionViewDescriptor, OpenSessionState, AgentEvent, Task> _handleAgentEventAsync;
    private readonly Func<SessionViewDescriptor, Task> _persistSessionLocalStateAsync;
    private readonly Action<OpenSessionState> _notifySessionUsageChanged;
    private readonly Action<SessionViewDescriptor, OpenSessionState, IReadOnlyList<AgentEvent>> _projectLoadedHistory;
    private readonly Func<Func<Task>, Task> _dispatchToUiAsync;

    public SessionHistoryCoordinator(
        SessionRuntimeService runtimeService,
        Func<SessionViewDescriptor, OpenSessionState> ensureSessionTab,
        Func<string, SessionViewDescriptor?> findSession,
        Func<string, OpenSessionState?> findOpenSession,
        Func<SessionViewDescriptor, bool> canLoadHistory,
        Func<SessionViewDescriptor, OpenSessionState, SessionExecutionOptions> buildExecutionOptions,
        Action<OpenSessionState, string, bool, StatusTone> setSessionStatus,
        Action<OpenSessionState> clearSessionStatus,
        Action<OpenSessionState> resetSessionTab,
        Func<SessionViewDescriptor, OpenSessionState, AgentEvent, Task> handleAgentEventAsync,
        Func<SessionViewDescriptor, Task> persistSessionLocalStateAsync,
        Action<OpenSessionState>? notifySessionUsageChanged = null,
        Action<SessionViewDescriptor, OpenSessionState, IReadOnlyList<AgentEvent>>? projectLoadedHistory = null,
        Func<Func<Task>, Task>? dispatchToUiAsync = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(ensureSessionTab);
        ArgumentNullException.ThrowIfNull(findSession);
        ArgumentNullException.ThrowIfNull(findOpenSession);
        ArgumentNullException.ThrowIfNull(canLoadHistory);
        ArgumentNullException.ThrowIfNull(buildExecutionOptions);
        ArgumentNullException.ThrowIfNull(setSessionStatus);
        ArgumentNullException.ThrowIfNull(clearSessionStatus);
        ArgumentNullException.ThrowIfNull(resetSessionTab);
        ArgumentNullException.ThrowIfNull(handleAgentEventAsync);
        ArgumentNullException.ThrowIfNull(persistSessionLocalStateAsync);

        _runtimeService = runtimeService;
        _ensureSessionTab = ensureSessionTab;
        _findSession = findSession;
        _findOpenSession = findOpenSession;
        _canLoadHistory = canLoadHistory;
        _buildExecutionOptions = buildExecutionOptions;
        _setSessionStatus = setSessionStatus;
        _clearSessionStatus = clearSessionStatus;
        _resetSessionTab = resetSessionTab;
        _handleAgentEventAsync = handleAgentEventAsync;
        _persistSessionLocalStateAsync = persistSessionLocalStateAsync;
        _notifySessionUsageChanged = notifySessionUsageChanged ?? (static _ => { });
        _projectLoadedHistory = projectLoadedHistory ?? (static (_, _, _) => { });
        _dispatchToUiAsync = dispatchToUiAsync ?? (static action => action());
    }

    public async Task EnsureLoadedAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!_canLoadHistory(session))
        {
            return;
        }

        var tab = _ensureSessionTab(session);
        var loadTask = GetOrStartLoadTask(tab, session, cancellationToken);
        await loadTask;
    }

    public async Task LoadEarlierAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = _findSession(sessionId);
        var tab = _findOpenSession(sessionId);
        if (session is null || tab is null || !tab.Timeline.HasLoadableTruncatedHistory)
        {
            return;
        }

        tab.Timeline.ReplaceTruncatedHistoryLoadButton();
        await Task.Run(
            () => RebuildAsync(
                session,
                tab,
                loadOnlyFromLastUserPrompt: false,
                preferCachedHistory: true,
                CancellationToken.None));
    }

    public static bool CanLoadSessionHistory(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.StartedAt is not null)
        {
            return true;
        }

        return session.Status != SessionViewStatus.Draft &&
               !string.IsNullOrWhiteSpace(session.SessionId);
    }

    public static SessionHistoryLoadPlan CreateInitialLoadPlan(IReadOnlyList<AgentEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var startIndex = FindInitialStartIndex(history);
        if (startIndex <= 0 || startIndex >= history.Count)
        {
            return new SessionHistoryLoadPlan(history, OmittedMessageCount: 0);
        }

        var pinnedPrefixIndexes = FindPinnedPrefixEventIndexes(history, startIndex);
        var pinnedPrefixIndexSet = pinnedPrefixIndexes.ToHashSet();
        var eventsToRender = pinnedPrefixIndexes
            .Select(index => history[index])
            .Concat(history.Skip(startIndex))
            .ToArray();
        return new SessionHistoryLoadPlan(
            eventsToRender,
            CountRenderableMessages(history.Take(startIndex).Where((_, index) => !pinnedPrefixIndexSet.Contains(index))));
    }

    public static AgentSessionUsage? RecoverUsageFromHistory(IReadOnlyList<AgentEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        AgentSessionUsage? usage = null;
        foreach (var @event in history)
        {
            if (@event is AgentSessionUpdateEvent { Usage: { } updateUsage })
            {
                usage = SessionUsageAggregator.Merge(usage, updateUsage);
            }
        }

        return usage;
    }

    public static ModelProviderPreference? RecoverModelProviderPreferenceFromHistory(IReadOnlyList<AgentEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (history[index] is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.ModelChanged } update &&
                TryReadModelSelection(update, out var modelId, out var reasoningEffort))
            {
                return new ModelProviderPreference(
                    new ModelProviderId(update.ProviderId.Value),
                    modelId,
                    reasoningEffort);
            }
        }

        return null;
    }

    public static string RecoverNotesMarkdownFromHistory(IReadOnlyList<AgentEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (history[index] is AgentNotesEvent notesEvent)
            {
                return notesEvent.Markdown;
            }
        }

        return string.Empty;
    }

    private static bool ApplyRecoveredModelProviderPreference(
        SessionViewDescriptor session,
        OpenSessionState tab,
        ModelProviderPreference? preference)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);

        if (preference is null)
        {
            return false;
        }

        var normalized = preference.Normalize();
        var changed = false;
        if (!string.Equals(tab.ProviderId.Value, normalized.ModelProviderId.Value, StringComparison.OrdinalIgnoreCase))
        {
            tab.ProviderId = normalized.ModelProviderId;
            changed = true;
        }

        if (!string.Equals(session.ProviderKey, normalized.ModelProviderId.Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(session.ProviderId, normalized.ModelProviderId.Value, StringComparison.OrdinalIgnoreCase))
        {
            session.ProviderKey = normalized.ModelProviderId.Value;
            session.ProviderId = normalized.ModelProviderId.Value;
            changed = true;
        }

        if (!string.Equals(tab.ModelId, normalized.ModelId, StringComparison.Ordinal))
        {
            tab.ModelId = normalized.ModelId;
            changed = true;
        }

        if (!string.Equals(session.ModelId, normalized.ModelId, StringComparison.Ordinal))
        {
            session.ModelId = normalized.ModelId;
            changed = true;
        }

        if (tab.ReasoningEffort != normalized.ReasoningEffort)
        {
            tab.ReasoningEffort = normalized.ReasoningEffort;
            changed = true;
        }

        if (session.ReasoningEffort != normalized.ReasoningEffort)
        {
            session.ReasoningEffort = normalized.ReasoningEffort;
            changed = true;
        }

        return changed;
    }

    private static bool TryReadModelSelection(
        AgentSessionUpdateEvent update,
        out string? modelId,
        out AgentReasoningEffort? reasoningEffort)
    {
        modelId = null;
        reasoningEffort = null;
        if (update.Details is not { ValueKind: JsonValueKind.Object } details)
        {
            return false;
        }

        if (details.TryGetProperty("modelId", out var modelProperty) &&
            modelProperty.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(modelProperty.GetString()))
        {
            modelId = modelProperty.GetString()!.Trim();
        }

        if (details.TryGetProperty("reasoningEffort", out var reasoningProperty) &&
            reasoningProperty.ValueKind == JsonValueKind.String &&
            Enum.TryParse<AgentReasoningEffort>(reasoningProperty.GetString(), ignoreCase: true, out var parsedReasoningEffort))
        {
            reasoningEffort = parsedReasoningEffort;
        }

        return !string.IsNullOrWhiteSpace(modelId) || reasoningEffort is not null;
    }

    private static IReadOnlyList<int> FindPinnedPrefixEventIndexes(IReadOnlyList<AgentEvent> history, int endIndex)
    {
        var latestSystemPromptIndex = -1;
        var latestModelChangedIndex = -1;
        for (var index = 0; index < endIndex; index++)
        {
            switch (history[index])
            {
                case AgentSystemPromptEvent:
                    latestSystemPromptIndex = index;
                    break;
                case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.ModelChanged }:
                    latestModelChangedIndex = index;
                    break;
            }
        }

        return new[] { latestSystemPromptIndex, latestModelChangedIndex }
            .Where(static index => index >= 0)
            .Distinct()
            .Order()
            .ToArray();
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

    public static AgentSystemPromptEvent? FindPriorSystemPromptForFirstRenderedSystemPrompt(
        IReadOnlyList<AgentEvent> history,
        IReadOnlyList<AgentEvent> eventsToRender)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(eventsToRender);

        var firstRenderedSystemPrompt = eventsToRender.OfType<AgentSystemPromptEvent>().FirstOrDefault();
        if (firstRenderedSystemPrompt is null)
        {
            return null;
        }

        var firstRenderedIndex = -1;
        for (var index = 0; index < history.Count; index++)
        {
            if (ReferenceEquals(history[index], firstRenderedSystemPrompt))
            {
                firstRenderedIndex = index;
                break;
            }
        }

        if (firstRenderedIndex <= 0)
        {
            return null;
        }

        for (var index = firstRenderedIndex - 1; index >= 0; index--)
        {
            if (history[index] is AgentSystemPromptEvent systemPrompt)
            {
                return systemPrompt;
            }
        }

        return null;
    }

    public static int CountRenderableMessages(IEnumerable<AgentEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var contentKeys = new HashSet<string>(StringComparer.Ordinal);
        var activityIds = new HashSet<string>(StringComparer.Ordinal);
        var interactionIds = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        var hasPendingFileChangeRecap = false;

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

                case AgentActivityEvent activity:
                    if (activity.Kind == AgentActivityKind.FileChange &&
                        activity.Phase is not (AgentActivityPhase.Failed or AgentActivityPhase.Canceled))
                    {
                        hasPendingFileChangeRecap = true;
                    }

                    if (ChatMarkdownFormatter.ShouldDisplayActivity(activity) && activityIds.Add(activity.ActivityId))
                    {
                        count++;
                    }

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

                case AgentSystemPromptEvent:
                    count++;
                    break;

                case AgentSessionUpdateEvent update:
                    if (update.Kind == AgentSessionUpdateKind.DiffUpdated)
                    {
                        hasPendingFileChangeRecap = true;
                    }

                    if (update.Kind is AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.Shutdown)
                    {
                        if (hasPendingFileChangeRecap)
                        {
                            count++;
                            hasPendingFileChangeRecap = false;
                        }
                    }

                    if (update.Kind != AgentSessionUpdateKind.Idle && ChatMarkdownFormatter.ShouldDisplaySessionUpdate(update))
                    {
                        count++;
                    }

                    break;

                case AgentErrorEvent:
                    count++;
                    break;
            }
        }

        return count;
    }

    private Task GetOrStartLoadTask(
        OpenSessionState tab,
        SessionViewDescriptor session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(session);

        if (tab.HistoryLoaded)
        {
            return Task.CompletedTask;
        }

        if (tab.HistoryLoadTask is { } existingTask)
        {
            return existingTask.WaitAsync(cancellationToken);
        }

        var loadTask = Task.Run(() => LoadCoreAsync(session, tab, cancellationToken));
        tab.HistoryLoadTask = loadTask;
        return loadTask.WaitAsync(cancellationToken);
    }

    private async Task LoadCoreAsync(
        SessionViewDescriptor session,
        OpenSessionState tab,
        CancellationToken cancellationToken)
    {
        await RebuildAsync(
                session,
                tab,
                loadOnlyFromLastUserPrompt: true,
                preferCachedHistory: false,
                cancellationToken)
            ;
    }

    private async Task RebuildAsync(
        SessionViewDescriptor session,
        OpenSessionState tab,
        bool loadOnlyFromLastUserPrompt,
        bool preferCachedHistory,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentEvent>? cachedHistory = null;
        SessionExecutionOptions? executionOptions = null;
        try
        {
            await _dispatchToUiAsync(
                    () =>
                    {
                        tab.HistoryLoading = true;
                        _setSessionStatus(
                            tab,
                            loadOnlyFromLastUserPrompt
                                ? $"Loading session '{session.Title}'..."
                                : $"Loading previous messages from '{session.Title}'...",
                            true,
                            StatusTone.Info);
                        if (preferCachedHistory && tab.HistoryEvents is { Count: > 0 } historyEvents)
                        {
                            cachedHistory = historyEvents.ToList();
                        }

                        executionOptions = _buildExecutionOptions(session, tab);
                        return Task.CompletedTask;
                    })
                .ConfigureAwait(false);

            var history = await GetHistoryAsync(session, cachedHistory, executionOptions!, cancellationToken).ConfigureAwait(false);
            session.MessageCount = CountRenderableMessages(history);
            await _persistSessionLocalStateAsync(session).ConfigureAwait(false);
            var recoveredUsage = RecoverUsageFromHistory(history);
            var recoveredModelPreference = RecoverModelProviderPreferenceFromHistory(history);
            var recoveredNotesMarkdown = RecoverNotesMarkdownFromHistory(history);
            var plan = loadOnlyFromLastUserPrompt
                ? CreateInitialLoadPlan(history)
                : new SessionHistoryLoadPlan(history, OmittedMessageCount: 0);
            var recoveredModelPreferenceChanged = false;
            await _dispatchToUiAsync(
                    async () =>
                    {
                        recoveredModelPreferenceChanged = ApplyRecoveredModelProviderPreference(session, tab, recoveredModelPreference);
                        tab.HistoryEvents = history.ToList();
                        var previousUsage = tab.Usage;
                        _resetSessionTab(tab);
                        tab.Usage = recoveredUsage;
                        tab.NotesMarkdown = recoveredNotesMarkdown;
                        var usageChanged = !Equals(previousUsage, recoveredUsage);

                        tab.Session.LastRenderedSystemPromptEvent = FindPriorSystemPromptForFirstRenderedSystemPrompt(history, plan.EventsToRender);
                        DocumentFlowItem? truncatedHistoryItem = null;
                        if (plan.OmittedMessageCount > 0)
                        {
                            truncatedHistoryItem = tab.Timeline.CreateTruncatedHistoryItem(
                                plan.OmittedMessageCount,
                                () => _ = LoadEarlierAsync(session.SessionId));
                        }

                        tab.Timeline.BeginBufferedHistoryLoad();
                        var renderedEventCount = 0;
                        foreach (var @event in plan.EventsToRender)
                        {
                            await _handleAgentEventAsync(session, tab, @event);
                            renderedEventCount++;
                            if (renderedEventCount % 25 == 0)
                            {
                                await Task.Yield();
                            }
                        }

                        tab.Timeline.CompleteInitialBufferedHistory(truncatedHistoryItem);
                        tab.Timeline.FlushBufferedHistoryItems();
                        tab.HistoryLoaded = true;
                        if (usageChanged)
                        {
                            _notifySessionUsageChanged(tab);
                        }
                        _clearSessionStatus(tab);
                    })
                .ConfigureAwait(false);
            if (recoveredModelPreferenceChanged)
            {
                await _persistSessionLocalStateAsync(session).ConfigureAwait(false);
            }

            _projectLoadedHistory(session, tab, plan.EventsToRender);
        }
        catch (Exception ex)
        {
            CodeAltaApp.UiLogger.Error(ex, $"Failed to load history for session {session.SessionId}");

            await _dispatchToUiAsync(
                    () =>
                    {
                        _resetSessionTab(tab);
                        tab.Timeline.FlushBufferedHistoryItems();
                        tab.Timeline.RenderFailure($"Failed to load history: {ex.Message}");
                        _setSessionStatus(tab, $"Failed to load '{session.Title}': {ex.Message}", false, StatusTone.Error);
                        return Task.CompletedTask;
                    })
                .ConfigureAwait(false);
        }
        finally
        {
            await _dispatchToUiAsync(
                    () =>
                    {
                        tab.HistoryLoading = false;
                        tab.HistoryLoadTask = null;
                        tab.Timeline.ClearBufferedHistory();
                        return Task.CompletedTask;
                    })
                .ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(
        SessionViewDescriptor session,
        IReadOnlyList<AgentEvent>? cachedHistory,
        SessionExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        if (cachedHistory is not null)
        {
            return cachedHistory;
        }

        ArgumentNullException.ThrowIfNull(executionOptions);
        try
        {
            return (await _runtimeService.GetOrResumeHistoryAsync(session, executionOptions, cancellationToken).ConfigureAwait(false)).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            if (await _runtimeService.TryReadStoredHistoryAsync(session, cancellationToken).ConfigureAwait(false) is { } storedHistory)
            {
                return storedHistory.ToList();
            }

            throw;
        }
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
