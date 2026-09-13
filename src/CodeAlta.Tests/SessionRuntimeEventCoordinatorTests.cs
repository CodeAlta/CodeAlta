using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.Events;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Presentation.Timeline;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using CodeAlta.Tui.Threading;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionRuntimeEventCoordinatorTests
{
    [TestMethod]
    public void ShouldPromoteAgentEventToThinking_IgnoresToolOutputDeltas()
    {
        var delta = new AgentContentDeltaEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentContentKind.ToolOutput,
            "tool-1",
            "activity-1",
            "line 1");

        Assert.IsFalse(SessionRuntimeEventCoordinator.ShouldPromoteAgentEventToThinking(delta));
    }

    [TestMethod]
    public void ShouldApplyShellChromeProjectionAfterRuntimeEvent_IgnoresToolOutputDeltas()
    {
        var runtimeEvent = new SessionAgentEvent(
            "session-1",
            new AgentContentDeltaEvent(
                ModelProviderIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.ToolOutput,
                "tool-1",
                "activity-1",
                "line 1"));

        Assert.IsFalse(SessionRuntimeEventCoordinator.ShouldApplyShellChromeProjectionAfterRuntimeEvent(runtimeEvent));
    }

    [TestMethod]
    public void ShouldApplyShellChromeProjectionAfterRuntimeEvent_RefreshesForAssistantCompletion()
    {
        var runtimeEvent = new SessionAgentEvent(
            "session-1",
            new AgentContentCompletedEvent(
                ModelProviderIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.Assistant,
                "assistant-1",
                null,
                "Final answer"));

        Assert.IsTrue(SessionRuntimeEventCoordinator.ShouldApplyShellChromeProjectionAfterRuntimeEvent(runtimeEvent));
    }

    [TestMethod]
    public void ShouldApplyShellChromeProjectionAfterRuntimeEvent_RefreshesForQueueEvents()
    {
        var runtimeEvent = new SessionQueueRuntimeEvent(
            "session-1",
            DateTimeOffset.UtcNow,
            QueuedPromptCount: 1,
            QueueItemId: "queue-1",
            PromptPreview: "queued prompt",
            IsEnqueued: true);

        Assert.IsTrue(SessionRuntimeEventCoordinator.ShouldApplyShellChromeProjectionAfterRuntimeEvent(runtimeEvent));
    }

    [TestMethod]
    public void ApplyRuntimeEvent_ForwardsAgentEventsToPluginObserver()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var observer = new RecordingPluginAgentEventObserver();
        var coordinator = CreateCoordinator(session, tab, pluginAgentEventObserver: observer);
        var agentEvent = new AgentContentCompletedEvent(
            ModelProviderIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentContentKind.Assistant,
            "assistant-1",
            null,
            "Final answer");

        coordinator.ApplyRuntimeEvent(new SessionAgentEvent(session.SessionId, agentEvent));

        Assert.AreSame(session, observer.ObservedSession);
        Assert.AreSame(agentEvent, observer.ObservedEvent);
    }

    [TestMethod]
    public async Task HandleAgentEventAsync_ReplayProjectsOnlyRenderedHistoryEvents()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.HistoryLoading = true;
        var hiddenEvent = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow.AddMinutes(-10),
            null,
            AgentContentKind.Assistant,
            "hidden-assistant",
            null,
            "Earlier answer");
        var displayedEvent = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentContentKind.Assistant,
            "displayed-assistant",
            null,
            "Displayed answer");
        tab.HistoryEvents = [hiddenEvent, displayedEvent];
        var observer = new RecordingPluginAgentEventObserver();
        var coordinator = CreateCoordinator(session, tab, pluginAgentEventObserver: observer);

        await coordinator.HandleAgentEventAsync(session, tab, displayedEvent);
        coordinator.ProjectLoadedHistory(session, tab, tab.RenderedHistoryEvents);
        await observer.WaitForProjectionAsync();

        CollectionAssert.AreEqual(new[] { displayedEvent }, observer.ProjectedEvents?.ToArray());
        Assert.IsTrue(observer.ProjectedIsReplay);
    }

    [TestMethod]
    public void ApplyRuntimeEvent_AfterReplayProjectsVisibleHistoryPlusLiveEvents()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var hiddenEvent = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow.AddMinutes(-10),
            null,
            AgentContentKind.Assistant,
            "hidden-assistant",
            null,
            "Earlier answer");
        var displayedEvent = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            null,
            AgentContentKind.Assistant,
            "displayed-assistant",
            null,
            "Displayed answer");
        var liveEvent = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentContentKind.Assistant,
            "live-assistant",
            null,
            "Live answer");
        tab.HistoryEvents = [hiddenEvent, displayedEvent];
        tab.RenderedHistoryEvents.Add(displayedEvent);
        var observer = new RecordingPluginAgentEventObserver();
        var coordinator = CreateCoordinator(session, tab, pluginAgentEventObserver: observer);

        coordinator.ApplyRuntimeEvent(new SessionAgentEvent(session.SessionId, liveEvent));
        observer.WaitForProjectionAsync().GetAwaiter().GetResult();

        CollectionAssert.AreEqual(new AgentEvent[] { displayedEvent, liveEvent }, observer.ProjectedEvents?.ToArray());
        Assert.IsFalse(observer.ProjectedIsReplay);
    }

    [TestMethod]
    public void HandleAgentEvent_KeepsManualCompactionStatusWhileProgressEventsArrive()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.PendingManualCompaction = true;
        tab.StatusMessage = $"Compacting '{session.Title}'...";
        tab.StatusBusy = true;
        tab.StatusTone = StatusTone.Info;
        tab.HasCustomStatus = true;

        var coordinator = CreateCoordinator(session, tab);
        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentActivityEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentActivityKind.ToolCall,
                AgentActivityPhase.Started,
                "activity-1",
                null,
                "compact",
                "Compacting session..."));

        Assert.AreEqual($"Compacting '{session.Title}'...", tab.StatusMessage);
        Assert.IsTrue(tab.StatusBusy);
        Assert.AreEqual(StatusTone.Info, tab.StatusTone);
    }

    [TestMethod]
    public void ApplyRuntimeEvent_HostCompactionCompletionClearsPendingManualCompactionStatus()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.PendingManualCompaction = true;
        tab.StatusMessage = $"Compacting '{session.Title}'...";
        tab.StatusBusy = true;
        tab.StatusTone = StatusTone.Info;
        tab.HasCustomStatus = true;

        var coordinator = CreateCoordinator(session, tab);
        coordinator.ApplyRuntimeEvent(
            new SessionHostEvent(
                session.SessionId,
                DateTimeOffset.UtcNow,
                AgentSessionUpdateKind.CompactionCompleted,
                "Manual compaction completed."));

        Assert.IsFalse(tab.PendingManualCompaction);
        Assert.IsFalse(tab.HasCustomStatus);
        Assert.IsFalse(tab.StatusBusy);
        Assert.IsNull(tab.StatusMessage);
    }

    [TestMethod]
    public void RenderAgentEvent_SystemPromptChangeAddsPromptDiffSection()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var renderer = new SessionRuntimeTimelineRenderer(static () => false);
        var timestamp = DateTimeOffset.UtcNow;
        var initial = CreateSystemPromptEvent(timestamp, "sha256:old", "system\nold", "developer", "initial");
        var changed = CreateSystemPromptEvent(timestamp.AddSeconds(1), "sha256:new", "system\nnew", "developer\nmore", "changed");

        renderer.RenderAgentEvent(tab, initial);
        renderer.RenderAgentEvent(tab, changed);

        Assert.AreSame(changed, tab.Session.LastRenderedSystemPromptEvent);
        Assert.AreEqual(2, tab.Timeline.Flow.Items.Count);
        var firstStack = GetChatCardStack(tab.Timeline.Flow.Items[0]);
        var changedStack = GetChatCardStack(tab.Timeline.Flow.Items[1]);

        Assert.AreEqual(2, firstStack.Children.Count);
        Assert.IsInstanceOfType<Collapsible>(firstStack.Children[1]);
        Assert.AreEqual(3, changedStack.Children.Count);
        Assert.IsInstanceOfType<Collapsible>(changedStack.Children[1]);
        Assert.IsInstanceOfType<Collapsible>(changedStack.Children[2]);
    }

    [TestMethod]
    public void RenderAgentEvent_SystemPromptChangeUsesSeededPriorPromptForDiffSection()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var renderer = new SessionRuntimeTimelineRenderer(static () => false);
        var timestamp = DateTimeOffset.UtcNow;
        var initial = CreateSystemPromptEvent(timestamp, "sha256:old", "system\nold", "developer", "initial");
        var changed = CreateSystemPromptEvent(timestamp.AddSeconds(1), "sha256:new", "system\nnew", "developer", "changed");
        tab.Session.LastRenderedSystemPromptEvent = initial;

        renderer.RenderAgentEvent(tab, changed);

        Assert.AreSame(changed, tab.Session.LastRenderedSystemPromptEvent);
        Assert.AreEqual(1, tab.Timeline.Flow.Items.Count);
        var changedStack = GetChatCardStack(tab.Timeline.Flow.Items[0]);
        Assert.AreEqual(3, changedStack.Children.Count);
        Assert.IsInstanceOfType<Collapsible>(changedStack.Children[1]);
        Assert.IsInstanceOfType<Collapsible>(changedStack.Children[2]);
    }

    [TestMethod]
    public void HandleAgentEvent_RemovesPendingSteerOnFirstLiveUserContentOnly()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.PendingSteers.Add(new PendingSteerPrompt("First steer"));
        tab.PendingSteers.Add(new PendingSteerPrompt("Second steer"));

        var coordinator = CreateCoordinator(session, tab);
        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentContentDeltaEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.User,
                "user-1",
                null,
                "First steer"));
        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentContentCompletedEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.User,
                "user-1",
                null,
                "First steer"));

        Assert.AreEqual(1, tab.PendingSteers.Count);
        Assert.AreEqual("Second steer", tab.PendingSteers[0].Text);
    }

    [TestMethod]
    public void HandleAgentEvent_PublishesQueuedPromptListChangedWhenPendingSteerIsConsumed()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.PendingSteers.Add(new PendingSteerPrompt("First steer"));
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);

        var coordinator = CreateCoordinator(
            session,
            tab,
            frontendEvents: publisher);
        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentContentCompletedEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.User,
                "user-1",
                null,
                "First steer"));

        Assert.AreEqual(0, tab.PendingSteers.Count);
        Assert.AreEqual(1, events.OfType<QueuedPromptListChangedEvent>().Count(@event => @event.SessionId == session.SessionId));
    }

    [TestMethod]
    public void HandleAgentEvent_DoesNotConsumePendingSteerDuringHistoryReplay()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.HistoryLoading = true;
        tab.PendingSteers.Add(new PendingSteerPrompt("Pending steer"));

        var coordinator = CreateCoordinator(session, tab);
        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentContentCompletedEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.User,
                "user-1",
                null,
                "Pending steer"));

        Assert.AreEqual(1, tab.PendingSteers.Count);
    }

    [TestMethod]
    public void HandleAgentEvent_ClearsPendingSteersWhenSessionBecomesIdle()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.PendingSteers.Add(new PendingSteerPrompt("Pending steer"));

        var coordinator = CreateCoordinator(session, tab);
        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentSessionUpdateEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.Idle,
                "Idle"));

        Assert.AreEqual(0, tab.PendingSteers.Count);
    }

    [TestMethod]
    public void HandleAgentEvent_PublishesQueuedPromptListChangedWhenPendingSteersAreCleared()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.PendingSteers.Add(new PendingSteerPrompt("Pending steer"));
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);

        var coordinator = CreateCoordinator(
            session,
            tab,
            frontendEvents: publisher);
        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentSessionUpdateEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.Idle,
                "Idle"));

        Assert.AreEqual(0, tab.PendingSteers.Count);
        Assert.AreEqual(1, events.OfType<QueuedPromptListChangedEvent>().Count(@event => @event.SessionId == session.SessionId));
    }

    [TestMethod]
    public void HandleAgentEvent_TracksActiveRunIdAndClearsItWhenSessionBecomesIdle()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var coordinator = CreateCoordinator(session, tab);

        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentContentDeltaEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentContentKind.Assistant,
                "assistant-1",
                null,
                "Working..."));

        Assert.AreEqual("run-1", tab.ActiveRunId?.Value);

        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentSessionUpdateEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentSessionUpdateKind.Idle,
                "Idle"));

        Assert.IsNull(tab.ActiveRunId);
    }

    [TestMethod]
    public void HandleAgentEvent_DoesNotInvalidateRuntimeOwnedCacheWhenFileChangesArrive()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var searchService = new FakeProjectFileSearchService();
        var coordinator = CreateCoordinator(session, tab, projectFileSearchService: searchService);

        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentActivityEvent(
                ModelProviderIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentActivityKind.FileChange,
                AgentActivityPhase.Completed,
                "activity-1",
                null,
                "write_file",
                "Updated Program.cs"));

        Assert.AreEqual(0, searchService.Invalidations.Count);
    }

    [TestMethod]
    public void ApplyRuntimeEvent_PublishesTypedProjectionEvents()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var dispatcher = new InlineUiDispatcher();
        var publisher = new FrontendEventPublisher(dispatcher);
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = CreateCoordinator(session, tab, frontendEvents: publisher);

        coordinator.ApplyRuntimeEvent(new SessionAgentEvent(
            session.SessionId,
            new AgentContentCompletedEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.Assistant,
                "assistant-1",
                null,
                "Done")));

        Assert.IsTrue(events.OfType<RuntimeTimelineChangedEvent>().Any(@event => @event.SessionId == session.SessionId));
        Assert.IsTrue(events.OfType<ShellChromeChangedEvent>().Any());
    }

    [TestMethod]
    public void ApplyRuntimeEvent_QueueEventAddsTimelineNoticeAndProjectionEvents()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = CreateCoordinator(session, tab, frontendEvents: publisher);

        coordinator.ApplyRuntimeEvent(new SessionQueueRuntimeEvent(
            session.SessionId,
            DateTimeOffset.UtcNow,
            QueuedPromptCount: 1,
            QueueItemId: "queue-1",
            PromptPreview: "queued prompt",
            IsEnqueued: true));

        Assert.AreEqual(1, tab.Timeline.Flow.Items.Count);
        Assert.IsTrue(events.OfType<RuntimeTimelineChangedEvent>().Any(@event => @event.SessionId == session.SessionId));
        Assert.IsTrue(events.OfType<ShellChromeChangedEvent>().Any());
    }

    [TestMethod]
    public void ApplyRuntimeEvent_ParentNotificationQueueEventSkipsTimelineNotice()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = CreateCoordinator(session, tab, frontendEvents: publisher);

        coordinator.ApplyRuntimeEvent(new SessionQueueRuntimeEvent(
            session.SessionId,
            DateTimeOffset.UtcNow,
            QueuedPromptCount: 1,
            QueueItemId: "queue-1",
            PromptPreview: "[CodeAlta delegated-agent message]",
            IsEnqueued: true)
        { QueueKind = "parent-notify" });

        Assert.AreEqual(0, tab.Timeline.Flow.Items.Count);
        Assert.IsFalse(events.OfType<RuntimeTimelineChangedEvent>().Any(@event => @event.SessionId == session.SessionId));
        Assert.IsTrue(events.OfType<ShellChromeChangedEvent>().Any());
    }

    [TestMethod]
    public void ApplyRuntimeEvent_CatalogEventUpsertsRuntimeSession()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        SessionViewDescriptor? upserted = null;
        var coordinator = CreateCoordinator(session, tab, upsertRuntimeSession: descriptor => upserted = descriptor);

        coordinator.ApplyRuntimeEvent(new SessionCatalogRuntimeEvent(session.SessionId, DateTimeOffset.UtcNow, session));

        Assert.AreSame(session, upserted);
    }

    [TestMethod]
    public void ApplyRuntimeEvent_AgentConfigurationEventUpdatesOpenSessionAndHeaderOnly()
    {
        var session = CreateSession();
        session.AgentPromptId = "default";
        var tab = CreateOpenSessionState(session);
        tab.AgentPromptId = "default";
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = CreateCoordinator(session, tab, frontendEvents: publisher);

        coordinator.ApplyRuntimeEvent(new SessionAgentConfigurationRuntimeEvent(
            session.SessionId,
            DateTimeOffset.UtcNow,
            ProviderId: "codex",
            ProviderKey: "codex",
            ModelId: "gpt-5.5",
            ReasoningEffort: AgentReasoningEffort.Low,
            AgentPromptId: "plan"));

        Assert.AreEqual("plan", session.AgentPromptId);
        Assert.AreEqual("plan", tab.AgentPromptId);
        Assert.AreEqual("plan", tab.SessionView.AgentPromptId);
        Assert.AreEqual("codex", tab.ProviderId.Value);
        Assert.AreEqual("gpt-5.5", tab.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Low, tab.ReasoningEffort);
        Assert.IsTrue(events.OfType<HeaderChangedEvent>().Any());
        Assert.IsFalse(events.OfType<CatalogChangedEvent>().Any());
    }

    [TestMethod]
    public void ApplyRuntimeEvent_TracksRunningStateForNonOpenRuntimeSession()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = CreateCoordinator(session, tab, frontendEvents: publisher, exposeOpenSession: false);
        var runId = "run-1";

        coordinator.ApplyRuntimeEvent(new SessionLifecycleRuntimeEvent(
            session.SessionId,
            DateTimeOffset.UtcNow,
            new SessionLifecycleEvent
            {
                SessionId = session.SessionId,
                Kind = SessionLifecycleEventKind.RunSubmitted,
                RunId = runId,
            }));

        Assert.IsTrue(coordinator.IsSessionRunning(session.SessionId));
        Assert.IsTrue(events.OfType<ShellChromeChangedEvent>().Any());
        events.Clear();

        coordinator.ApplyRuntimeEvent(new SessionAgentEvent(
            session.SessionId,
            new AgentSessionUpdateEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId(runId),
                AgentSessionUpdateKind.Idle,
                "idle")));

        Assert.IsFalse(coordinator.IsSessionRunning(session.SessionId));
        Assert.IsTrue(events.OfType<ShellChromeChangedEvent>().Any());
    }

    [TestMethod]
    public void HandleAgentEvent_PublishesSessionUsageChangedWhenSelectedUsageChanges()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = CreateCoordinator(session, tab, frontendEvents: publisher);

        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentSessionUpdateEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.UsageUpdated,
                "usage",
                Usage: new AgentSessionUsage(
                    Window: new AgentWindowUsageSnapshot(1200, 8000, 3, "window"),
                    Scope: AgentUsageScope.CurrentWindow,
                    Source: AgentUsageSource.CopilotSessionUsageInfo,
                    UpdatedAt: DateTimeOffset.UtcNow)));

        Assert.IsTrue(events.OfType<SessionUsageChangedEvent>().Any(@event => @event.SessionId == session.SessionId));
    }

    [TestMethod]
    public void ApplyRuntimeEvent_PublishesPromptFocusRequestAfterShellChromeWhenSelectedSessionBecomesIdle()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = CreateCoordinator(session, tab, frontendEvents: publisher);

        coordinator.ApplyRuntimeEvent(
            new SessionAgentEvent(
                session.SessionId,
                new AgentSessionUpdateEvent(
                    ModelProviderIds.Copilot,
                    "session-1",
                    DateTimeOffset.UtcNow,
                    null,
                    AgentSessionUpdateKind.Idle,
                    "idle")));

        var shellChromeIndex = events.FindIndex(static @event => @event is ShellChromeChangedEvent);
        var promptFocusIndex = events.FindIndex(static @event => @event is PromptFocusRequestedEvent);

        Assert.IsTrue(shellChromeIndex >= 0, "The idle runtime update should refresh shell chrome before focus is restored.");
        Assert.IsTrue(promptFocusIndex > shellChromeIndex, "Prompt focus should be requested after shell chrome refreshes so later projection updates do not steal focus.");
    }

    [TestMethod]
    public async Task HandleAgentEvent_RendersPluginProjectionOutsidePluginProjectionLock()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var renderCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new DerivedProjectionObserver(
            () => new PluginTerminalDerivedSessionEvent
            {
                EventId = "stats",
                Markdown = "statistics",
                VisualFactory = _ =>
                {
                    renderCompleted.TrySetResult(Monitor.IsEntered(tab.Session.PluginProjectionSyncRoot));
                    return new TextBlock("statistics");
                },
            });
        var coordinator = CreateCoordinator(session, tab, pluginAgentEventObserver: observer);

        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentContentCompletedEvent(
                ModelProviderIds.Copilot,
                session.SessionId,
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.Assistant,
                "assistant-1",
                null,
                "Done"));

        var renderedWhileLockHeld = await renderCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(renderedWhileLockHeld, "Plugin projection visuals must not be rendered while the projection lock is held because UI dispatch can re-enter session reset.");
    }

    [TestMethod]
    public async Task DynamicPluginProjectionRefresh_RendersOutsidePluginProjectionLock()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var dynamicContent = new RecordingDynamicProjectionContent(tab);
        var observer = new DerivedProjectionObserver(
            () => new PluginDerivedSessionEvent
            {
                EventId = "stats",
                DynamicContent = dynamicContent,
            });
        var coordinator = CreateCoordinator(session, tab, pluginAgentEventObserver: observer);

        coordinator.HandleAgentEvent(
            session,
            tab,
            new AgentContentCompletedEvent(
                ModelProviderIds.Copilot,
                session.SessionId,
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.Assistant,
                "assistant-1",
                null,
                "Done"));
        await dynamicContent.WaitForRenderAsync().ConfigureAwait(false);

        dynamicContent.ResetRenderSignal();
        dynamicContent.NotifyChangedForTest();

        var renderedWhileLockHeld = await dynamicContent.WaitForRenderAsync().ConfigureAwait(false);

        Assert.IsFalse(renderedWhileLockHeld, "Dynamic plugin projection refreshes must not render while the projection lock is held because UI dispatch can re-enter session reset.");
    }

    [TestMethod]
    public Task HistoryRebuild_DoesNotInvalidateCache_PreservesHistoryLoadingPluginObservation()
        => new HistoryCacheFixture().Run();

    // Cached LoadEarlierAsync exercises the real rebuild without providers, journal access, plugin
    // instances, or background projection. The fake observer returns already-completed tasks.
    private sealed class HistoryCacheFixture
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-cache-history-" + Guid.NewGuid().ToString("N"));
        private readonly List<Task> _originals = [];
        private readonly List<Task<Exception?>> _outcomes = [];
        private Task? _original;
        private Task<Exception?>? _outcome;
        private CodeAlta.Agent.ModelProviderRegistry? _registry;
        private CodeAlta.Orchestration.Runtime.AgentHub? _hub;
        private SessionRuntimeService? _runtime;

        internal async Task Run()
        {
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _original = Core(launch.Task); _outcome = Observe(_original); launch.SetResult();
            try { await _original.WaitAsync(TimeSpan.FromSeconds(30)); await _outcome; }
            catch (Exception ex) { ex.Data["RetainedFixture"] = this; ex.Data["RetainedRoot"] = _root; throw; }
            finally { Console.WriteLine("Retained cache history fixture root: " + _root); }
        }

        private async Task Core(Task launch)
        {
            await launch;
            Exception? primary = null, cleanup = null;
            try { await Keep(Exercise); }
            catch (Exception ex) { primary = ex; }
            // No held gates, external callbacks or cancellation dependencies: the original rebuild
            // must actually return before releasing its borrowed runtime. A deadline never releases it.
            try
            {
                if (_runtime is not null) await Keep(() => _runtime.DisposeAsync().AsTask());
                if (_hub is not null) await Keep(() => _hub.DisposeAsync().AsTask());
                if (_registry is not null) await Keep(() => _registry.DisposeAsync().AsTask());
            }
            catch (Exception ex) { cleanup = ex; }
            await Task.WhenAll(_outcomes);
            if (primary is not null || cleanup is not null)
            {
                var failure = new AggregateException(new[] { primary, cleanup }.OfType<Exception>());
                failure.Data["Primary"] = primary; failure.Data["Cleanup"] = cleanup;
                throw failure;
            }
        }

        private Task Keep(Func<Task> operation)
        {
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var original = Invoke(); _originals.Add(original); _outcomes.Add(Observe(original)); launch.SetResult();
            return original;
            async Task Invoke() { await launch.Task.ConfigureAwait(false); await operation().ConfigureAwait(false); }
        }
        private static async Task<Exception?> Observe(Task original)
        { try { await original.ConfigureAwait(false); return null; } catch (Exception ex) { return ex; } }

        private async Task Exercise()
        {
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(_root)!); parent is not null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse fixture ancestry.");
            if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root exists.");
            Directory.CreateDirectory(_root);
            var options = new CatalogOptions { GlobalRoot = Path.Combine(_root, "global") };
            var views = new SessionViewCatalog(options);
            var forbidden = new NoHistoryDiscovery();
            var skills = new CodeAlta.Catalog.Skills.SkillCatalog([forbidden]);
            _registry = new(); // Empty registry; no provider registration, probing or execution.
            _hub = new(_registry, options.GlobalRoot);
            _runtime = new(_hub, new CodeAlta.Agent.AgentSessionCatalog(views.JournalStore.CreateSessionStore()),
                new ProjectCatalog(options), views,
                new AgentInstructionTemplateProvider(skills, options, forbidden, null, new(Path.Combine(_root, "home"), _root)), options, skills);
            var session = CreateSession();
            session.WorkingDirectory = _root; session.ProviderId = "cache-history-inert"; session.ProviderKey = "cache-history-inert";
            session.Kind = SessionViewKind.GlobalSession; session.ProjectRef = null;
            var tab = CreateOpenSessionState(session);
            AgentEvent[] events =
            [
                new AgentActivityEvent(new("cache-history-inert"), session.SessionId, DateTimeOffset.UnixEpoch, null,
                    AgentActivityKind.FileChange, AgentActivityPhase.Completed, "file", null, "fixture", "fixture"),
                new AgentSessionUpdateEvent(new("cache-history-inert"), session.SessionId, DateTimeOffset.UnixEpoch, null, AgentSessionUpdateKind.DiffUpdated, null),
            ];
            tab.HistoryEvents = events.ToList();
            tab.Timeline.CreateTruncatedHistoryItem(1, static () => { });
            var search = new FakeProjectFileSearchService();
            var observer = new RecordingPluginAgentEventObserver();
            var coordinator = CreateCoordinator(session, tab, search, observer);
            var observations = new List<(AgentEvent? Event, bool Loading, bool ProjectionSuppressed)>();
            var projectedLoadedHistory = false;
            var history = new SessionHistoryCoordinator(_runtime, _ => tab, _ => session, _ => tab,
                _ => true, (_, _) => new SessionExecutionOptions { ProviderId = new("cache-history-inert"), WorkingDirectory = _root, OnPermissionRequest = _runtime.Permissions.OwnedDefaultPermissionHandler },
                (_, _, _, _) => { }, _ => { }, t => t.RenderedHistoryEvents.Clear(),
                async (s, t, e) =>
                {
                    await coordinator.HandleAgentEventAsync(s, t, e);
                    observations.Add((observer.ObservedEvent, t.HistoryLoading, observer.ProjectedEvents is null));
                }, _ => Task.CompletedTask,
                projectLoadedHistory: (_, _, loaded) => projectedLoadedHistory = loaded.SequenceEqual(events));
            await Keep(() => history.LoadEarlierAsync(session.SessionId));
            Assert.IsTrue(tab.HistoryLoaded);
            Assert.IsFalse(tab.HistoryLoading);
            Assert.IsTrue(projectedLoadedHistory);
            Assert.AreEqual(events.Length, observations.Count);
            for (var i = 0; i < events.Length; i++)
            {
                Assert.AreSame(events[i], observations[i].Event);
                Assert.IsTrue(observations[i].Loading);
                Assert.IsTrue(observations[i].ProjectionSuppressed);
            }
            Assert.AreEqual(0, search.Invalidations.Count);
        }

        private sealed class NoHistoryDiscovery : CodeAlta.Catalog.Skills.ISkillRootProvider, ISystemPromptContentLocator
        {
            public ValueTask<IReadOnlyList<CodeAlta.Catalog.Skills.SkillRootRegistration>> GetRootsAsync(CodeAlta.Catalog.Skills.SkillDiscoveryContext context, CancellationToken cancellationToken = default)
                => throw new AssertFailedException("No history skill discovery.");
            public SystemPromptContentRoots GetRoots(SystemPromptDiscoveryContext context) => throw new AssertFailedException("No history prompt discovery.");
            public string ResolveBuiltInPromptPath(string relativePromptPath) => throw new AssertFailedException("No history prompts.");
            public string ResolveBuiltInDocPath(string fileName) => throw new AssertFailedException("No history docs.");
        }
    }

    private static SessionRuntimeEventCoordinator CreateCoordinator(
        SessionViewDescriptor session,
        OpenSessionState? tab,
        IProjectFileSearchService? projectFileSearchService = null,
        IPluginAgentEventObserver? pluginAgentEventObserver = null,
        FrontendEventPublisher? frontendEvents = null,
        Action<SessionViewDescriptor>? upsertRuntimeSession = null,
        bool exposeOpenSession = true)
    {
        var stateStore = new ShellStateStore(new InlineUiDispatcher());
        stateStore.Mutate(snapshot => snapshot.SetCatalog([], [session]));
        return new SessionRuntimeEventCoordinator(
            stateStore: stateStore,
            findOpenSession: id => exposeOpenSession && tab is not null && id == tab.SessionView.SessionId ? tab : null,
            getAutoApproveEnabled: static () => false,
            isSelectedSession: id => id == session.SessionId,
            statusPort: new TestShellStatusPort(),
            drainQueuedPromptAsync: static (_, _) => Task.CompletedTask,
            projectFileSearchService: projectFileSearchService ?? NullProjectFileSearchService.Instance,
            upsertRuntimeSession: upsertRuntimeSession,
            pluginAgentEventObserver: pluginAgentEventObserver,
            frontendEvents: frontendEvents);
    }

    private sealed class RecordingPluginAgentEventObserver : IPluginAgentEventObserver
    {
        public SessionViewDescriptor? ObservedSession { get; private set; }

        public AgentEvent? ObservedEvent { get; private set; }

        public IReadOnlyList<AgentEvent>? ProjectedEvents { get; private set; }

        public bool ProjectedIsReplay { get; private set; }

        private TaskCompletionSource _projectionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ObserveAgentEventAsync(SessionViewDescriptor session, AgentEvent agentEvent, CancellationToken cancellationToken = default)
        {
            ObservedSession = session;
            ObservedEvent = agentEvent;
            return Task.CompletedTask;
        }

        public Task<SessionViewPluginDerivedEventProjectionResult> ProjectSessionEventsAsync(
            SessionViewDescriptor session,
            OpenSessionState tab,
            IReadOnlyList<AgentEvent> events,
            bool isReplay,
            CancellationToken cancellationToken = default)
        {
            ProjectedEvents = events;
            ProjectedIsReplay = isReplay;
            _projectionCompletion.TrySetResult();
            return Task.FromResult(new SessionViewPluginDerivedEventProjectionResult([], []));
        }

        public Task WaitForProjectionAsync()
            => _projectionCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class DerivedProjectionObserver(Func<PluginDerivedSessionEvent> createEvent) : IPluginAgentEventObserver
    {
        public Task ObserveAgentEventAsync(SessionViewDescriptor session, AgentEvent agentEvent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SessionViewPluginDerivedEventProjectionResult> ProjectSessionEventsAsync(
            SessionViewDescriptor session,
            OpenSessionState tab,
            IReadOnlyList<AgentEvent> events,
            bool isReplay,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionViewPluginDerivedEventProjectionResult([createEvent()], []));
    }

    private sealed class RecordingDynamicProjectionContent(OpenSessionState tab) : PluginTerminalDynamicDerivedSessionEventContent
    {
        private TaskCompletionSource<bool> _renderCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _version;

        public override string Markdown => $"statistics {_version}";

        public override PluginSessionEventVisualFactory? VisualFactory => _ =>
        {
            _renderCompleted.TrySetResult(Monitor.IsEntered(tab.Session.PluginProjectionSyncRoot));
            return new TextBlock($"statistics {_version}");
        };

        public Task<bool> WaitForRenderAsync()
            => _renderCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        public void ResetRenderSignal()
            => _renderCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public void NotifyChangedForTest()
        {
            _version++;
            NotifyChanged();
        }
    }

    private sealed class TestShellStatusPort : IShellStatusPort
    {
        public void SetShellStatus(ShellStatusUpdate update)
        {
        }

        public void SetSessionStatus(OpenSessionState session, SessionStatusUpdate update)
        {
            session.StatusMessage = update.Message;
            session.StatusBusy = update.ShowSpinner;
            session.StatusTone = update.Tone;
            session.HasCustomStatus = true;
        }

        public void ClearSessionStatus(OpenSessionState session)
        {
            session.StatusMessage = null;
            session.StatusBusy = false;
            session.StatusTone = StatusTone.Info;
            session.HasCustomStatus = false;
        }

        public void SetProviderSessionLoadStatus(string? message)
        {
        }
    }

    private static OpenSessionState CreateOpenSessionState(SessionViewDescriptor session)
    {
        var timeline = new SessionTimelinePresenter(new InlineUiDispatcher(), static () => null);
        return new OpenSessionState(session, timeline);
    }

    private static SessionViewDescriptor CreateSession()
    {
        return new SessionViewDescriptor
        {
            SessionId = "session-1",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ModelProviderIds.Copilot.Value,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow
        };
    }

    private static AgentSystemPromptEvent CreateSystemPromptEvent(
        DateTimeOffset timestamp,
        string hash,
        string systemMessage,
        string developerInstructions,
        string changeKind)
        => new(
            ModelProviderIds.Copilot,
            "session-1",
            timestamp,
            null,
            "session_start",
            hash,
            "default",
            systemMessage,
            developerInstructions,
            new AgentSystemPromptProviderPayloadSummary("native-system-and-developer", true, false),
            null,
            new AgentSystemPromptStatistics(1, 1, 2, 6, 9),
            new AgentSystemPromptChangeSummary(changeKind, ["base/default"], [], []));

    private static VStack GetChatCardStack(DocumentFlowItem item)
    {
        var document = Assert.IsInstanceOfType<FlowDocument>(item.Content);
        Assert.AreEqual(1, document.BlockCount);
        var block = Assert.IsInstanceOfType<VisualDocumentFlowBlock>(document.GetBlock(0));
        var group = Assert.IsInstanceOfType<Group>(block.CreateVisual());
        return Assert.IsInstanceOfType<VStack>(group.Content);
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess()
            => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Task.FromResult(action());
        }
    }

    private sealed class FakeProjectFileSearchService : IProjectFileSearchService
    {
        public List<(string ProjectRoot, ProjectFileInvalidationReason Reason)> Invalidations { get; } = [];

        public ValueTask<IProjectFileSearchSession> CreateSessionAsync(
            ProjectFileSearchSessionOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ProjectFileResolution> ResolveAsync(
            ProjectFileResolveQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask RecordUsageAsync(
            ProjectFileUsageEvent usageEvent,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask InvalidateAsync(
            string projectRoot,
            ProjectFileInvalidationReason reason,
            CancellationToken cancellationToken = default)
        {
            Invalidations.Add((projectRoot, reason));
            return ValueTask.CompletedTask;
        }
    }
}
