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

internal sealed class ThreadRuntimeEventCoordinator
{
    private readonly ShellStateStore _stateStore;
    private readonly Func<string, OpenThreadState?> _findOpenThread;
    private readonly Func<string, bool> _isSelectedThread;
    private readonly IShellStatusPort _statusPort;
    private readonly Func<OpenThreadState, CancellationToken, Task> _drainQueuedPromptAsync;
    private readonly IProjectFileSearchService _projectFileSearchService;
    private readonly Action<WorkThreadDescriptor> _upsertRuntimeThread;
    private readonly IPluginAgentEventObserver _pluginAgentEventObserver;
    private readonly FrontendEventPublisher? _frontendEvents;
    private readonly ThreadRuntimeStateReducer _stateReducer;
    private readonly ThreadRuntimeTimelineRenderer _timelineRenderer;
    private readonly HashSet<string> _runningThreadIds = new(StringComparer.OrdinalIgnoreCase);

    public ThreadRuntimeEventCoordinator(
        ShellStateStore stateStore,
        Func<string, OpenThreadState?> findOpenThread,
        Func<bool> getAutoApproveEnabled,
        Func<string, bool> isSelectedThread,
        IShellStatusPort statusPort,
        Func<OpenThreadState, CancellationToken, Task> drainQueuedPromptAsync,
        IProjectFileSearchService projectFileSearchService,
        Action<WorkThreadDescriptor>? upsertRuntimeThread = null,
        IPluginAgentEventObserver? pluginAgentEventObserver = null,
        FrontendEventPublisher? frontendEvents = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(findOpenThread);
        ArgumentNullException.ThrowIfNull(getAutoApproveEnabled);
        ArgumentNullException.ThrowIfNull(isSelectedThread);
        ArgumentNullException.ThrowIfNull(statusPort);
        ArgumentNullException.ThrowIfNull(drainQueuedPromptAsync);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);

        _stateStore = stateStore;
        _findOpenThread = findOpenThread;
        _isSelectedThread = isSelectedThread;
        _statusPort = statusPort;
        _drainQueuedPromptAsync = drainQueuedPromptAsync;
        _projectFileSearchService = projectFileSearchService;
        _upsertRuntimeThread = upsertRuntimeThread ?? (static _ => { });
        _pluginAgentEventObserver = pluginAgentEventObserver ?? new PluginAgentEventObserver(null);
        _frontendEvents = frontendEvents;
        _stateReducer = new ThreadRuntimeStateReducer();
        _timelineRenderer = new ThreadRuntimeTimelineRenderer(getAutoApproveEnabled);
    }

    public bool IsThreadRunning(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return _runningThreadIds.Contains(threadId);
    }

    public void ApplyRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        if (runtimeEvent is WorkThreadCatalogRuntimeEvent catalogEvent)
        {
            _upsertRuntimeThread(catalogEvent.Thread);
            return;
        }

        var runtimeRunStateChanged = ObserveRuntimeRunState(runtimeEvent);

        var thread = FindThread(runtimeEvent.ThreadId);
        if (thread is null)
        {
            if (runtimeRunStateChanged)
            {
                _frontendEvents?.Publish(new ShellChromeChangedEvent());
            }

            return;
        }

        var tab = _findOpenThread(thread.ThreadId);
        var reduction = _stateReducer.ReduceRuntimeEvent(
            thread,
            tab,
            runtimeEvent,
            _isSelectedThread(thread.ThreadId));
        if (runtimeRunStateChanged && !reduction.ApplyShellChromeProjection)
        {
            reduction = reduction with { ApplyShellChromeProjection = true };
        }

        if (tab is not null)
        {
            switch (runtimeEvent)
            {
                case WorkThreadAgentEvent agentEvent:
                    tab.HistoryEvents ??= [];
                    tab.HistoryEvents.Add(agentEvent.Event);
                    tab.RenderedHistoryEvents.Add(agentEvent.Event);
                    TryRenderInteraction(tab, () => _timelineRenderer.RenderAgentEvent(tab, agentEvent.Event), "agent event");
                    ProjectPluginThreadEvents(thread, tab, tab.RenderedHistoryEvents, isReplay: false);
                    _frontendEvents?.Publish(new RuntimeTimelineChangedEvent(thread.ThreadId));
                    break;

                case WorkThreadHostEvent hostEvent:
                    TryRenderInteraction(tab, () => _timelineRenderer.RenderHostEvent(tab, hostEvent), "host event");
                    _frontendEvents?.Publish(new RuntimeTimelineChangedEvent(thread.ThreadId));
                    break;

                case WorkThreadQueueRuntimeEvent queueEvent:
                    TryRenderInteraction(tab, () => _timelineRenderer.RenderQueueEvent(tab, queueEvent), "queue event");
                    _frontendEvents?.Publish(new RuntimeTimelineChangedEvent(thread.ThreadId));
                    break;
            }
        }

        if (runtimeEvent is WorkThreadAgentEvent agentRuntimeEvent)
        {
            InvalidateProjectFileSearchIfNeeded(thread, agentRuntimeEvent.Event);
            ObservePluginAgentEvent(thread, agentRuntimeEvent.Event);
        }

        ApplyReduction(tab, reduction);
    }

    public void HandleAgentEvent(WorkThreadDescriptor thread, OpenThreadState tab, AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(@event);

        var reduction = _stateReducer.ReduceAgentEvent(thread, tab, @event, _isSelectedThread(thread.ThreadId));
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

        TryRenderInteraction(tab, () => _timelineRenderer.RenderAgentEvent(tab, @event), "agent event");
        ProjectPluginThreadEvents(thread, tab, projectionEvents, isReplay: tab.HistoryLoading);
        _frontendEvents?.Publish(new RuntimeTimelineChangedEvent(thread.ThreadId));
        InvalidateProjectFileSearchIfNeeded(thread, @event);
        ObservePluginAgentEvent(thread, @event);
        ApplyReduction(tab, reduction);
    }

    public async Task HandleAgentEventAsync(WorkThreadDescriptor thread, OpenThreadState tab, AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(@event);

        var reduction = _stateReducer.ReduceAgentEvent(thread, tab, @event, _isSelectedThread(thread.ThreadId));
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

        TryRenderInteraction(tab, () => _timelineRenderer.RenderAgentEvent(tab, @event), "agent event");
        ProjectPluginThreadEvents(thread, tab, projectionEvents, isReplay: tab.HistoryLoading);
        _frontendEvents?.Publish(new RuntimeTimelineChangedEvent(thread.ThreadId));
        InvalidateProjectFileSearchIfNeeded(thread, @event);
        ObservePluginAgentEvent(thread, @event);
        ApplyReduction(tab, reduction);
        await Task.CompletedTask;
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

            _statusPort.SetShellStatus(new ShellStatusUpdate($"Failed to render thread {context}: {ex.Message}", false, StatusTone.Error));
            tab.Timeline.ClearPendingAssistant();
        }
    }

    private void ObservePluginAgentEvent(WorkThreadDescriptor thread, AgentEvent @event)
        => global::CodeAlta.CodeAltaTaskMonitor.Observe(
            _pluginAgentEventObserver.ObserveAgentEventAsync(thread, @event),
            $"Plugin agent event observer for thread {thread.ThreadId}");

    private void ProjectPluginThreadEvents(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
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

                await ProjectPluginThreadEventsAsync(thread, tab, projectionEvents, isReplay, version);
            }),
            $"Plugin thread event projection for thread {thread.ThreadId}");
    }

    private async Task ProjectPluginThreadEventsAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        IReadOnlyList<AgentEvent> events,
        bool isReplay,
        long version)
    {
        var result = await _pluginAgentEventObserver.ProjectThreadEventsAsync(thread, tab, events, isReplay);
        if (Volatile.Read(ref tab.Session.PluginProjectionVersion) != version)
        {
            return;
        }

        if (result.Events.Count == 0)
        {
            return;
        }

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
                            UpdateDynamicProjectionSubscription(thread, tab, unchangedProjection);
                        }
                    }

                    continue;
                }

                if (derivedEvent.Remove)
                {
                    RemoveDynamicProjectionSubscription(tab, derivedEvent.EventId);
                    TryRenderInteraction(tab, () => tab.Timeline.RemovePluginProjection(derivedEvent.EventId), "plugin projection");
                    continue;
                }

                var projection = tab.PluginTransientEvents.Get(derivedEvent.EventId);
                if (projection is null)
                {
                    continue;
                }

                UpdateDynamicProjectionSubscription(thread, tab, projection);
                RenderPluginProjection(tab, projection);
            }
        }

        _frontendEvents?.Publish(new RuntimeTimelineChangedEvent(thread.ThreadId));
    }

    private void RenderPluginProjection(OpenThreadState tab, PluginTransientEventProjection projection)
        => TryRenderInteraction(
            tab,
            () => tab.Timeline.UpsertPluginProjection(
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
                CreatePluginVisualFactory(projection)),
            "plugin projection");

    private static Func<Visual>? CreatePluginVisualFactory(PluginTransientEventProjection projection)
        => projection.VisualFactory is null
            ? null
            : () => projection.VisualFactory(new PluginThreadEventVisualContext
            {
                EventId = projection.EventId,
                RenderTarget = projection.RenderTarget,
                Markdown = projection.Markdown,
                Payload = projection.Payload,
            });

    private static Func<Visual>? CreatePluginDetailVisualFactory(
        PluginTransientEventProjection projection,
        PluginDerivedThreadEventDetailSection section)
        => section.VisualFactory is null
            ? null
            : () => section.VisualFactory(new PluginThreadEventVisualContext
            {
                EventId = projection.EventId,
                RenderTarget = projection.RenderTarget,
                Markdown = section.Markdown,
                Payload = projection.Payload,
                DetailHeader = section.Header,
            });

    private static Func<Visual>? CreatePluginDetailHeaderVisualFactory(
        PluginTransientEventProjection projection,
        PluginDerivedThreadEventDetailSection section)
        => section.HeaderVisualFactory is null
            ? null
            : () => section.HeaderVisualFactory(new PluginThreadEventVisualContext
            {
                EventId = projection.EventId,
                RenderTarget = projection.RenderTarget,
                Markdown = section.Markdown,
                Payload = projection.Payload,
                DetailHeader = section.Header,
            });

    private void UpdateDynamicProjectionSubscription(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
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
            RefreshDynamicPluginProjectionAsync(thread, tab, projection.EventId),
            $"Dynamic plugin projection update for thread {thread.ThreadId}");
        tab.Session.PluginDynamicProjectionSubscriptions[projection.EventId] = new PluginDynamicProjectionSubscription(dynamicContent, handler);
    }

    private void RemoveDynamicProjectionSubscription(OpenThreadState tab, string eventId)
    {
        if (tab.Session.PluginDynamicProjectionSubscriptions.Remove(eventId, out var subscription))
        {
            subscription.Dispose();
        }
    }

    private Task RefreshDynamicPluginProjectionAsync(WorkThreadDescriptor thread, OpenThreadState tab, string eventId)
    {
        lock (tab.Session.PluginProjectionSyncRoot)
        {
            if (!tab.PluginTransientEvents.RefreshDynamic(eventId))
            {
                return Task.CompletedTask;
            }

            var projection = tab.PluginTransientEvents.Get(eventId);
            if (projection is not null)
            {
                RenderPluginProjection(tab, projection);
                _frontendEvents?.Publish(new RuntimeTimelineChangedEvent(thread.ThreadId));
            }
        }

        return Task.CompletedTask;
    }

    public static bool ShouldPromoteAgentEventToThinking(AgentEvent @event)
        => ThreadRuntimeStateReducer.ShouldPromoteAgentEventToThinking(@event);

    public static bool ShouldApplyShellChromeProjectionAfterRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
        => ThreadRuntimeStateReducer.ShouldApplyShellChromeProjectionAfterRuntimeEvent(runtimeEvent);

    public static string SummarizeContent(string content)
        => ThreadRuntimeStateReducer.SummarizeContent(content);

    private bool ObserveRuntimeRunState(WorkThreadRuntimeEvent runtimeEvent)
    {
        switch (runtimeEvent)
        {
            case WorkThreadLifecycleRuntimeEvent { Event.Kind: WorkThreadLifecycleEventKind.RunSubmitted }:
                return _runningThreadIds.Add(runtimeEvent.ThreadId);
            case WorkThreadLifecycleRuntimeEvent
            {
                Event.Kind: WorkThreadLifecycleEventKind.RunCompleted
                or WorkThreadLifecycleEventKind.RunFailed
                or WorkThreadLifecycleEventKind.RunAborted
            }:
                return _runningThreadIds.Remove(runtimeEvent.ThreadId);
            case WorkThreadAgentEvent { Event: AgentErrorEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.Shutdown } }:
                return _runningThreadIds.Remove(runtimeEvent.ThreadId);
            case WorkThreadAgentEvent { Event.RunId: not null } agentEvent
                when ThreadRuntimeStateReducer.ShouldTrackRunId(agentEvent.Event):
                return _runningThreadIds.Add(runtimeEvent.ThreadId);
            default:
                return false;
        }
    }

    private WorkThreadDescriptor? FindThread(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return _stateStore.Snapshot.Threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));
    }

    private void InvalidateProjectFileSearchIfNeeded(WorkThreadDescriptor thread, AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(@event);

        if (!ShouldInvalidateProjectFileSearch(@event) ||
            string.IsNullOrWhiteSpace(thread.WorkingDirectory))
        {
            return;
        }

        _ = InvalidateProjectFileSearchAsync(thread.WorkingDirectory);
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

    private void ApplyReduction(OpenThreadState? tab, ThreadRuntimeReductionResult reduction)
    {
        var focusPromptAfterReduction = false;
        if (tab is not null)
        {
            if (reduction.ClearThreadStatus)
            {
                _statusPort.ClearThreadStatus(tab);
                _frontendEvents?.Publish(new ThreadStatusChangedEvent(tab.Thread.ThreadId));
                focusPromptAfterReduction = _isSelectedThread(tab.Thread.ThreadId);
            }

            if (reduction.ThreadStatus is { } status)
            {
                _statusPort.SetThreadStatus(tab, new ThreadStatusUpdate(status.Message, status.ShowSpinner, status.Tone));
                _frontendEvents?.Publish(new ThreadStatusChangedEvent(tab.Thread.ThreadId));
            }

            if (reduction.DrainQueuedPrompt)
            {
                global::CodeAlta.CodeAltaTaskMonitor.Observe(
                    _drainQueuedPromptAsync(tab, CancellationToken.None),
                    $"Queued prompt drain for thread {tab.Thread.ThreadId}");
            }
        }

        if (reduction.RefreshPromptStrip && tab is not null)
        {
            _frontendEvents?.Publish(new QueuedPromptListChangedEvent(tab.Thread.ThreadId));
        }

        if (reduction.ApplySessionUsageProjection && tab is not null)
        {
            _frontendEvents?.Publish(new SessionUsageChangedEvent(tab.Thread.ThreadId));
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
