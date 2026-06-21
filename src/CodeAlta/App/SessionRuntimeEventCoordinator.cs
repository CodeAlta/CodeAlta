using CodeAlta.App.State;
using CodeAlta.App.Events;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Shell;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Views;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI;

namespace CodeAlta.App;

internal sealed class SessionRuntimeEventCoordinator
{
    private readonly ShellStateStore _stateStore;
    private readonly Func<string, OpenSessionState?> _findOpenSession;
    private readonly Func<string, bool> _isSelectedSession;
    private readonly IShellStatusPort _statusPort;
    private readonly Func<OpenSessionState, CancellationToken, Task> _drainQueuedPromptAsync;
    private readonly IProjectFileSearchService _projectFileSearchService;
    private readonly Action<SessionViewDescriptor> _upsertRuntimeSession;
    private readonly IPluginAgentEventObserver _pluginAgentEventObserver;
    private readonly FrontendEventPublisher? _frontendEvents;
    private readonly SessionRuntimeStateReducer _stateReducer;
    private readonly SessionRuntimeTimelineRenderer _timelineRenderer;
    private readonly HashSet<string> _runningSessionIds = new(StringComparer.OrdinalIgnoreCase);

    public SessionRuntimeEventCoordinator(
        ShellStateStore stateStore,
        Func<string, OpenSessionState?> findOpenSession,
        Func<bool> getAutoApproveEnabled,
        Func<string, bool> isSelectedSession,
        IShellStatusPort statusPort,
        Func<OpenSessionState, CancellationToken, Task> drainQueuedPromptAsync,
        IProjectFileSearchService projectFileSearchService,
        Action<SessionViewDescriptor>? upsertRuntimeSession = null,
        IPluginAgentEventObserver? pluginAgentEventObserver = null,
        FrontendEventPublisher? frontendEvents = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(findOpenSession);
        ArgumentNullException.ThrowIfNull(getAutoApproveEnabled);
        ArgumentNullException.ThrowIfNull(isSelectedSession);
        ArgumentNullException.ThrowIfNull(statusPort);
        ArgumentNullException.ThrowIfNull(drainQueuedPromptAsync);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);

        _stateStore = stateStore;
        _findOpenSession = findOpenSession;
        _isSelectedSession = isSelectedSession;
        _statusPort = statusPort;
        _drainQueuedPromptAsync = drainQueuedPromptAsync;
        _projectFileSearchService = projectFileSearchService;
        _upsertRuntimeSession = upsertRuntimeSession ?? (static _ => { });
        _pluginAgentEventObserver = pluginAgentEventObserver ?? new PluginAgentEventObserver(null);
        _frontendEvents = frontendEvents;
        _stateReducer = new SessionRuntimeStateReducer();
        _timelineRenderer = new SessionRuntimeTimelineRenderer(getAutoApproveEnabled);
    }

    public bool IsSessionRunning(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _runningSessionIds.Contains(sessionId);
    }

    public void ApplyRuntimeEvent(SessionRuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        if (runtimeEvent is SessionCatalogRuntimeEvent catalogEvent)
        {
            _upsertRuntimeSession(catalogEvent.Session);
            return;
        }

        if (runtimeEvent is SessionAgentConfigurationRuntimeEvent configurationEvent)
        {
            ApplySessionAgentConfiguration(configurationEvent);
            return;
        }

        var runtimeRunStateChanged = ObserveRuntimeRunState(runtimeEvent);

        var session = FindSession(runtimeEvent.SessionId);
        if (session is null)
        {
            if (runtimeRunStateChanged)
            {
                _frontendEvents?.Publish(new ShellChromeChangedEvent());
            }

            return;
        }

        var tab = _findOpenSession(session.SessionId);
        var reduction = _stateReducer.ReduceRuntimeEvent(
            session,
            tab,
            runtimeEvent,
            _isSelectedSession(session.SessionId));
        if (runtimeRunStateChanged && !reduction.ApplyShellChromeProjection)
        {
            reduction = reduction with { ApplyShellChromeProjection = true };
        }

        if (tab is not null)
        {
            switch (runtimeEvent)
            {
                case SessionAgentEvent agentEvent:
                    if (agentEvent.Event is AgentNotesEvent notesEvent)
                    {
                        tab.NotesMarkdown = notesEvent.Markdown;
                    }

                    tab.HistoryEvents ??= [];
                    tab.HistoryEvents.Add(agentEvent.Event);
                    tab.RenderedHistoryEvents.Add(agentEvent.Event);
                    TryRenderInteraction(tab, () => _timelineRenderer.RenderAgentEvent(tab, agentEvent.Event), SR.T("agent event"));
                    ProjectPluginSessionEvents(session, tab, tab.RenderedHistoryEvents, isReplay: false);
                    PublishRuntimeTimelineChanged(session, tab);
                    break;

                case SessionHostEvent hostEvent:
                    TryRenderInteraction(tab, () => _timelineRenderer.RenderHostEvent(tab, hostEvent), SR.T("host event"));
                    PublishRuntimeTimelineChanged(session, tab);
                    break;

                case SessionQueueRuntimeEvent queueEvent:
                    if (SessionRuntimeTimelineRenderer.ShouldRenderQueueEvent(queueEvent))
                    {
                        TryRenderInteraction(tab, () => _timelineRenderer.RenderQueueEvent(tab, queueEvent), SR.T("queue event"));
                        PublishRuntimeTimelineChanged(session, tab);
                    }

                    break;
            }
        }

        if (runtimeEvent is SessionAgentEvent agentRuntimeEvent)
        {
            InvalidateProjectFileSearchIfNeeded(session, agentRuntimeEvent.Event);
            ObservePluginAgentEvent(session, agentRuntimeEvent.Event);
        }

        ApplyReduction(tab, reduction);
    }

    public void HandleAgentEvent(SessionViewDescriptor session, OpenSessionState tab, AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(@event);

        var reduction = _stateReducer.ReduceAgentEvent(session, tab, @event, _isSelectedSession(session.SessionId));
        if (!tab.HistoryLoading && @event is AgentNotesEvent notesEvent)
        {
            tab.NotesMarkdown = notesEvent.Markdown;
        }

        IReadOnlyList<AgentEvent> projectionEvents;
        if (!tab.HistoryLoading)
        {
            tab.HistoryEvents ??= [];
            tab.HistoryEvents.Add(@event);
            tab.RenderedHistoryEvents.Add(@event);
            projectionEvents = tab.RenderedHistoryEvents;
        }
        else
        {
            tab.RenderedHistoryEvents.Add(@event);
            projectionEvents = tab.RenderedHistoryEvents;
        }

        TryRenderInteraction(tab, () => _timelineRenderer.RenderAgentEvent(tab, @event), SR.T("agent event"));
        if (!tab.HistoryLoading)
        {
            ProjectPluginSessionEvents(session, tab, projectionEvents, isReplay: false);
        }

        PublishRuntimeTimelineChanged(session, tab);
        InvalidateProjectFileSearchIfNeeded(session, @event);
        ObservePluginAgentEvent(session, @event);
        ApplyReduction(tab, reduction);
    }

    public async Task HandleAgentEventAsync(SessionViewDescriptor session, OpenSessionState tab, AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(@event);

        var reduction = _stateReducer.ReduceAgentEvent(session, tab, @event, _isSelectedSession(session.SessionId));
        if (!tab.HistoryLoading && @event is AgentNotesEvent notesEvent)
        {
            tab.NotesMarkdown = notesEvent.Markdown;
        }

        IReadOnlyList<AgentEvent> projectionEvents;
        if (!tab.HistoryLoading)
        {
            tab.HistoryEvents ??= [];
            tab.HistoryEvents.Add(@event);
            tab.RenderedHistoryEvents.Add(@event);
            projectionEvents = tab.RenderedHistoryEvents;
        }
        else
        {
            tab.RenderedHistoryEvents.Add(@event);
            projectionEvents = tab.RenderedHistoryEvents;
        }

        TryRenderInteraction(tab, () => _timelineRenderer.RenderAgentEvent(tab, @event), SR.T("agent event"));
        if (!tab.HistoryLoading)
        {
            ProjectPluginSessionEvents(session, tab, projectionEvents, isReplay: false);
        }

        PublishRuntimeTimelineChanged(session, tab);
        InvalidateProjectFileSearchIfNeeded(session, @event);
        ObservePluginAgentEvent(session, @event);
        ApplyReduction(tab, reduction);
        await Task.CompletedTask;
    }

    public void ProjectLoadedHistory(SessionViewDescriptor session, OpenSessionState tab, IReadOnlyList<AgentEvent> events)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(events);

        ProjectPluginSessionEvents(session, tab, events, isReplay: true);
        _frontendEvents?.Publish(new SelectionChangedEvent());
        _frontendEvents?.Publish(new RuntimeTimelineChangedEvent(session.SessionId));
    }

    private void PublishRuntimeTimelineChanged(SessionViewDescriptor session, OpenSessionState tab)
    {
        if (!tab.HistoryLoading)
        {
            _frontendEvents?.Publish(new RuntimeTimelineChangedEvent(session.SessionId));
        }
    }

    public void TryRenderInteraction(OpenSessionState tab, Action action, string context)
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
            CodeAltaApp.UiLogger.Error(ex, $"Failed to render session {context}");

            _statusPort.SetShellStatus(new ShellStatusUpdate(SR.T("Failed to render session {0}: {1}", context, ex.Message), false, StatusTone.Error));
            tab.Timeline.ClearPendingAssistant();
        }
    }

    private void ObservePluginAgentEvent(SessionViewDescriptor session, AgentEvent @event)
        => global::CodeAlta.CodeAltaTaskMonitor.Observe(
            _pluginAgentEventObserver.ObserveAgentEventAsync(session, @event),
            $"Plugin agent event observer for session {session.SessionId}");

    private void ProjectPluginSessionEvents(
        SessionViewDescriptor session,
        OpenSessionState tab,
        IReadOnlyList<AgentEvent> events,
        bool isReplay)
    {
        var projectionEvents = events.ToArray();
        var version = Interlocked.Increment(ref tab.Session.PluginProjectionVersion);
        global::CodeAlta.CodeAltaTaskMonitor.Observe(
            Task.Run(async () =>
            {
                await Task.Delay(25);
                if (Volatile.Read(ref tab.Session.PluginProjectionVersion) != version)
                {
                    return;
                }

                await ProjectPluginSessionEventsAsync(session, tab, projectionEvents, isReplay, version);
            }),
            $"Plugin session event projection for session {session.SessionId}");
    }

    private async Task ProjectPluginSessionEventsAsync(
        SessionViewDescriptor session,
        OpenSessionState tab,
        IReadOnlyList<AgentEvent> events,
        bool isReplay,
        long version)
    {
        var result = await _pluginAgentEventObserver.ProjectSessionEventsAsync(session, tab, events, isReplay);
        if (Volatile.Read(ref tab.Session.PluginProjectionVersion) != version)
        {
            return;
        }

        if (result.Events.Count == 0)
        {
            return;
        }

        var projectionsToRender = new List<PluginTransientEventProjection>();
        var projectionIdsToRemove = new List<string>();
        foreach (var derivedEvent in result.Events)
        {
            lock (tab.Session.PluginProjectionSyncRoot)
            {
                var changed = tab.PluginTransientEvents.Apply(derivedEvent);
                if (!changed)
                {
                    if (!derivedEvent.Remove)
                    {
                        var unchangedProjection = tab.PluginTransientEvents.Get(derivedEvent.EventId);
                        if (unchangedProjection is not null)
                        {
                            UpdateDynamicProjectionSubscription(session, tab, unchangedProjection);
                        }
                    }

                    continue;
                }

                if (derivedEvent.Remove)
                {
                    RemoveDynamicProjectionSubscription(tab, derivedEvent.EventId);
                    projectionIdsToRemove.Add(derivedEvent.EventId);
                    continue;
                }

                var projection = tab.PluginTransientEvents.Get(derivedEvent.EventId);
                if (projection is null)
                {
                    continue;
                }

                UpdateDynamicProjectionSubscription(session, tab, projection);
                projectionsToRender.Add(projection);
            }
        }

        foreach (var eventId in projectionIdsToRemove)
        {
            RemovePluginProjection(tab, eventId, version);
        }

        foreach (var projection in projectionsToRender)
        {
            RenderPluginProjection(tab, projection, version);
        }

        _frontendEvents?.Publish(new RuntimeTimelineChangedEvent(session.SessionId));
    }

    private void RemovePluginProjection(OpenSessionState tab, string eventId, long expectedVersion)
        => TryRenderInteraction(
            tab,
            () =>
            {
                if (!IsPluginProjectionVersionCurrent(tab, expectedVersion))
                {
                    return;
                }

                tab.Timeline.RemovePluginProjection(eventId, () => IsPluginProjectionVersionCurrent(tab, expectedVersion));
            },
            SR.T("plugin projection"));

    private void RenderPluginProjection(OpenSessionState tab, PluginTransientEventProjection projection, long expectedVersion)
        => TryRenderInteraction(
            tab,
            () =>
            {
                if (!IsPluginProjectionVersionCurrent(tab, expectedVersion))
                {
                    return;
                }

                tab.Timeline.UpsertPluginProjection(
                    projection.EventId,
                    projection.Timestamp ?? DateTimeOffset.UtcNow,
                    projection.Markdown,
                    projection.RenderTarget,
                    projection.DetailSections
                        .Select(section => new ChatCollapsibleMarkdownSection(
                            section.Header,
                            section.Markdown,
                            CreatePluginDetailVisualFactory(projection, section),
                            CreatePluginDetailHeaderVisualFactory(projection, section)))
                        .ToArray(),
                    CreatePluginVisualFactory(projection),
                    () => IsPluginProjectionVersionCurrent(tab, expectedVersion));
            },
            SR.T("plugin projection"));

    private static bool IsPluginProjectionVersionCurrent(OpenSessionState tab, long expectedVersion)
        => Volatile.Read(ref tab.Session.PluginProjectionVersion) == expectedVersion;

    private static Func<Visual>? CreatePluginVisualFactory(PluginTransientEventProjection projection)
        => projection.VisualFactory is null
            ? null
            : () => projection.VisualFactory(new PluginSessionEventVisualContext
            {
                EventId = projection.EventId,
                RenderTarget = projection.RenderTarget,
                Markdown = projection.Markdown,
                Payload = projection.Payload,
            });

    private static Func<Visual>? CreatePluginDetailVisualFactory(
        PluginTransientEventProjection projection,
        PluginDerivedSessionEventDetailSection section)
        => section.VisualFactory is null
            ? null
            : () => section.VisualFactory(new PluginSessionEventVisualContext
            {
                EventId = projection.EventId,
                RenderTarget = projection.RenderTarget,
                Markdown = section.Markdown,
                Payload = projection.Payload,
                DetailHeader = section.Header,
            });

    private static Func<Visual>? CreatePluginDetailHeaderVisualFactory(
        PluginTransientEventProjection projection,
        PluginDerivedSessionEventDetailSection section)
        => section.HeaderVisualFactory is null
            ? null
            : () => section.HeaderVisualFactory(new PluginSessionEventVisualContext
            {
                EventId = projection.EventId,
                RenderTarget = projection.RenderTarget,
                Markdown = section.Markdown,
                Payload = projection.Payload,
                DetailHeader = section.Header,
            });

    private void UpdateDynamicProjectionSubscription(
        SessionViewDescriptor session,
        OpenSessionState tab,
        PluginTransientEventProjection projection)
    {
        if (projection.DynamicContent is not { } dynamicContent)
        {
            RemoveDynamicProjectionSubscription(tab, projection.EventId);
            return;
        }

        if (tab.Session.PluginDynamicProjectionSubscriptions.TryGetValue(projection.EventId, out var existing) &&
            ReferenceEquals(existing.Content, dynamicContent))
        {
            return;
        }

        RemoveDynamicProjectionSubscription(tab, projection.EventId);
        EventHandler handler = (_, _) => global::CodeAlta.CodeAltaTaskMonitor.Observe(
            RefreshDynamicPluginProjectionAsync(session, tab, projection.EventId),
            $"Dynamic plugin projection update for session {session.SessionId}");
        tab.Session.PluginDynamicProjectionSubscriptions[projection.EventId] = new PluginDynamicProjectionSubscription(dynamicContent, handler);
    }

    private void RemoveDynamicProjectionSubscription(OpenSessionState tab, string eventId)
    {
        if (tab.Session.PluginDynamicProjectionSubscriptions.Remove(eventId, out var subscription))
        {
            subscription.Dispose();
        }
    }

    private Task RefreshDynamicPluginProjectionAsync(SessionViewDescriptor session, OpenSessionState tab, string eventId)
    {
        PluginTransientEventProjection? projection = null;
        long expectedVersion;
        lock (tab.Session.PluginProjectionSyncRoot)
        {
            if (!tab.PluginTransientEvents.RefreshDynamic(eventId))
            {
                return Task.CompletedTask;
            }

            expectedVersion = Volatile.Read(ref tab.Session.PluginProjectionVersion);
            projection = tab.PluginTransientEvents.Get(eventId);
        }

        if (projection is not null)
        {
            RenderPluginProjection(tab, projection, expectedVersion);
            _frontendEvents?.Publish(new RuntimeTimelineChangedEvent(session.SessionId));
        }

        return Task.CompletedTask;
    }

    public static bool ShouldPromoteAgentEventToThinking(AgentEvent @event)
        => SessionRuntimeStateReducer.ShouldPromoteAgentEventToThinking(@event);

    public static bool ShouldApplyShellChromeProjectionAfterRuntimeEvent(SessionRuntimeEvent runtimeEvent)
        => SessionRuntimeStateReducer.ShouldApplyShellChromeProjectionAfterRuntimeEvent(runtimeEvent);

    public static string SummarizeContent(string content)
        => SessionRuntimeStateReducer.SummarizeContent(content);

    private bool ObserveRuntimeRunState(SessionRuntimeEvent runtimeEvent)
    {
        switch (runtimeEvent)
        {
            case SessionLifecycleRuntimeEvent { Event.Kind: SessionLifecycleEventKind.RunSubmitted }:
                return _runningSessionIds.Add(runtimeEvent.SessionId);
            case SessionLifecycleRuntimeEvent
            {
                Event.Kind: SessionLifecycleEventKind.RunCompleted
                or SessionLifecycleEventKind.RunFailed
                or SessionLifecycleEventKind.RunAborted
            }:
                return _runningSessionIds.Remove(runtimeEvent.SessionId);
            case SessionAgentEvent { Event: AgentErrorEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.Shutdown } }:
                return _runningSessionIds.Remove(runtimeEvent.SessionId);
            case SessionAgentEvent { Event.RunId: not null } agentEvent
                when SessionRuntimeStateReducer.ShouldTrackRunId(agentEvent.Event):
                return _runningSessionIds.Add(runtimeEvent.SessionId);
            default:
                return false;
        }
    }

    private SessionViewDescriptor? FindSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _stateStore.Snapshot.Sessions.FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplySessionAgentConfiguration(SessionAgentConfigurationRuntimeEvent configurationEvent)
    {
        var session = FindSession(configurationEvent.SessionId);
        if (session is not null)
        {
            ApplySessionAgentConfiguration(session, configurationEvent);
        }

        var tab = _findOpenSession(configurationEvent.SessionId);
        if (tab is not null)
        {
            ApplySessionAgentConfiguration(tab.SessionView, configurationEvent);
            if (!string.IsNullOrWhiteSpace(configurationEvent.ProviderKey ?? configurationEvent.ProviderId))
            {
                tab.ProviderId = new ModelProviderId((configurationEvent.ProviderKey ?? configurationEvent.ProviderId)!.Trim());
            }

            if (!string.IsNullOrWhiteSpace(configurationEvent.ModelId))
            {
                tab.ModelId = configurationEvent.ModelId.Trim();
            }

            if (configurationEvent.ReasoningEffort is { } reasoningEffort)
            {
                tab.ReasoningEffort = reasoningEffort;
            }

            if (!string.IsNullOrWhiteSpace(configurationEvent.AgentPromptId))
            {
                tab.AgentPromptId = configurationEvent.AgentPromptId.Trim();
            }
        }

        if (session is not null || tab is not null)
        {
            _stateStore.Mutate(snapshot => snapshot with { Sessions = snapshot.Sessions.ToArray() });
        }

        if ((session is not null || tab is not null) && _isSelectedSession(configurationEvent.SessionId))
        {
            _frontendEvents?.Publish(new HeaderChangedEvent());
        }
    }

    private static void ApplySessionAgentConfiguration(SessionViewDescriptor session, SessionAgentConfigurationRuntimeEvent configurationEvent)
    {
        if (!string.IsNullOrWhiteSpace(configurationEvent.ProviderId))
        {
            session.ProviderId = configurationEvent.ProviderId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(configurationEvent.ProviderKey))
        {
            session.ProviderKey = configurationEvent.ProviderKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(configurationEvent.ModelId))
        {
            session.ModelId = configurationEvent.ModelId.Trim();
        }

        if (configurationEvent.ReasoningEffort is { } reasoningEffort)
        {
            session.ReasoningEffort = reasoningEffort;
        }

        if (!string.IsNullOrWhiteSpace(configurationEvent.AgentPromptId))
        {
            session.AgentPromptId = configurationEvent.AgentPromptId.Trim();
        }
    }

    private void InvalidateProjectFileSearchIfNeeded(SessionViewDescriptor session, AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(@event);

        if (!ShouldInvalidateProjectFileSearch(@event) ||
            string.IsNullOrWhiteSpace(session.WorkingDirectory))
        {
            return;
        }

        _ = InvalidateProjectFileSearchAsync(session.WorkingDirectory);
    }

    private async Task InvalidateProjectFileSearchAsync(string projectRoot)
    {
        try
        {
            await _projectFileSearchService.InvalidateAsync(projectRoot, ProjectFileInvalidationReason.FileSystemWrite);
        }
        catch
        {
        }
    }

    private static bool ShouldInvalidateProjectFileSearch(AgentEvent @event)
        => @event switch
        {
            AgentActivityEvent { Kind: AgentActivityKind.FileChange } => true,
            AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.DiffUpdated } => true,
            _ => false,
        };

    private void ApplyReduction(OpenSessionState? tab, SessionRuntimeReductionResult reduction)
    {
        var focusPromptAfterReduction = false;
        if (tab is not null)
        {
            if (reduction.ClearSessionStatus)
            {
                _statusPort.ClearSessionStatus(tab);
                _frontendEvents?.Publish(new SessionStatusChangedEvent(tab.SessionView.SessionId));
                focusPromptAfterReduction = _isSelectedSession(tab.SessionView.SessionId);
            }

            if (reduction.SessionStatus is { } status)
            {
                _statusPort.SetSessionStatus(tab, new SessionStatusUpdate(status.Message, status.ShowSpinner, status.Tone));
                _frontendEvents?.Publish(new SessionStatusChangedEvent(tab.SessionView.SessionId));
            }

            if (reduction.DrainQueuedPrompt)
            {
                global::CodeAlta.CodeAltaTaskMonitor.Observe(
                    _drainQueuedPromptAsync(tab, CancellationToken.None),
                    $"Queued prompt drain for session {tab.SessionView.SessionId}");
            }
        }

        if (reduction.RefreshPromptStrip && tab is not null)
        {
            _frontendEvents?.Publish(new QueuedPromptListChangedEvent(tab.SessionView.SessionId));
        }

        if (reduction.ApplySessionUsageProjection && tab is not null)
        {
            _frontendEvents?.Publish(new SessionUsageChangedEvent(tab.SessionView.SessionId));
        }

        if (reduction.ApplyShellChromeProjection)
        {
            _frontendEvents?.Publish(new ShellChromeChangedEvent());
        }

        if (focusPromptAfterReduction)
        {
            _frontendEvents?.Publish(new PromptFocusRequestedEvent());
        }
    }
}
