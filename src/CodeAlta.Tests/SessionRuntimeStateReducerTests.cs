using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.Presentation.Shell;
using CodeAlta.Tui.Presentation.Timeline;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionRuntimeStateReducerTests
{
    [TestMethod]
    public void ReduceAgentEvent_TracksActiveRunIdAndClearsItWhenSessionBecomesIdle()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var reducer = new SessionRuntimeStateReducer();

        reducer.ReduceAgentEvent(
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
                "Working..."),
            isSelectedSession: false);

        Assert.AreEqual("run-1", tab.ActiveRunId?.Value);

        var reduction = reducer.ReduceAgentEvent(
            session,
            tab,
            new AgentSessionUpdateEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentSessionUpdateKind.Idle,
                "Idle"),
            isSelectedSession: false);

        Assert.IsNull(tab.ActiveRunId);
        Assert.IsTrue(reduction.ClearSessionStatus);
    }

    [TestMethod]
    public void ReduceAgentEvent_RemovesPendingSteerOnFirstLiveUserContentOnly()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.PendingSteers.Add(new PendingSteerPrompt("First steer"));
        tab.PendingSteers.Add(new PendingSteerPrompt("Second steer"));
        var reducer = new SessionRuntimeStateReducer();

        reducer.ReduceAgentEvent(
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
                "First steer"),
            isSelectedSession: false);
        reducer.ReduceAgentEvent(
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
                "First steer"),
            isSelectedSession: false);

        Assert.AreEqual(1, tab.PendingSteers.Count);
        Assert.AreEqual("Second steer", tab.PendingSteers[0].Text);
    }

    [TestMethod]
    public void ReduceAgentEvent_DoesNotConsumePendingSteerDuringHistoryReplay()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.HistoryLoading = true;
        tab.PendingSteers.Add(new PendingSteerPrompt("Pending steer"));
        var reducer = new SessionRuntimeStateReducer();

        reducer.ReduceAgentEvent(
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
                "Pending steer"),
            isSelectedSession: false);

        Assert.AreEqual(1, tab.PendingSteers.Count);
    }

    [TestMethod]
    public void ReduceAgentEvent_MergesUsageAndRequestsInvalidationForSelectedSession()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var reducer = new SessionRuntimeStateReducer();

        var reduction = reducer.ReduceAgentEvent(
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
                    UpdatedAt: DateTimeOffset.UtcNow)),
            isSelectedSession: true);

        Assert.IsNotNull(tab.Usage);
        Assert.IsTrue(reduction.ApplySessionUsageProjection);
    }

    [TestMethod]
    public void ReduceAgentEvent_ReconnectingSessionUpdateSetsBusyStatus()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var reducer = new SessionRuntimeStateReducer();

        var reduction = reducer.ReduceAgentEvent(
            session,
            tab,
            new AgentSessionUpdateEvent(
                ModelProviderIds.OpenAIResponses,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentSessionUpdateKind.Reconnecting,
                "Reconnecting to ChatGPT/Codex... 1/5"),
            isSelectedSession: true);

        Assert.IsNotNull(reduction.SessionStatus);
        Assert.AreEqual("Reconnecting to ChatGPT/Codex... 1/5", reduction.SessionStatus.Value.Message);
        Assert.IsTrue(reduction.SessionStatus.Value.ShowSpinner);
        Assert.AreEqual(StatusTone.Info, reduction.SessionStatus.Value.Tone);
    }

    [TestMethod]
    public void ReduceAgentEvent_InfoSessionUpdateSetsBusyStatus()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        var reducer = new SessionRuntimeStateReducer();

        var reduction = reducer.ReduceAgentEvent(
            session,
            tab,
            new AgentSessionUpdateEvent(
                ModelProviderIds.OpenAIResponses,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentSessionUpdateKind.Info,
                "Provider notice."),
            isSelectedSession: true);

        Assert.IsNotNull(reduction.SessionStatus);
        Assert.AreEqual("Provider notice.", reduction.SessionStatus.Value.Message);
        Assert.IsTrue(reduction.SessionStatus.Value.ShowSpinner);
        Assert.AreEqual(StatusTone.Info, reduction.SessionStatus.Value.Tone);
    }

    [TestMethod]
    public void ReduceRuntimeEvent_HostCompactionCompletionClearsPendingManualCompactionStatus()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.PendingManualCompaction = true;
        var reducer = new SessionRuntimeStateReducer();

        var reduction = reducer.ReduceRuntimeEvent(
            session,
            tab,
            new SessionHostEvent(
                session.SessionId,
                DateTimeOffset.UtcNow,
                AgentSessionUpdateKind.CompactionCompleted,
                "Manual compaction completed."),
            isSelectedSession: false);

        Assert.IsFalse(tab.PendingManualCompaction);
        Assert.IsTrue(reduction.ClearSessionStatus);
        Assert.IsTrue(reduction.ApplyShellChromeProjection);
    }

    [TestMethod]
    public void ReduceAgentEvent_DelayedIdleCompactionEventsDoNotReviveThinkingStatus()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.PendingManualCompaction = true;
        var reducer = new SessionRuntimeStateReducer();

        _ = reducer.ReduceRuntimeEvent(
            session,
            tab,
            new SessionHostEvent(
                session.SessionId,
                DateTimeOffset.UtcNow,
                AgentSessionUpdateKind.CompactionCompleted,
                "Manual compaction completed."),
            isSelectedSession: false);

        var startedReduction = reducer.ReduceAgentEvent(
            session,
            tab,
            new AgentSessionUpdateEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("compaction-run-1"),
                AgentSessionUpdateKind.CompactionStarted,
                "Compaction started."),
            isSelectedSession: true);
        var activityReduction = reducer.ReduceAgentEvent(
            session,
            tab,
            new AgentActivityEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("compaction-run-1"),
                AgentActivityKind.Compaction,
                AgentActivityPhase.Completed,
                "activity-1",
                null,
                "context compaction",
                "Compaction completed."),
            isSelectedSession: true);
        var completedReduction = reducer.ReduceAgentEvent(
            session,
            tab,
            new AgentSessionUpdateEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("compaction-run-1"),
                AgentSessionUpdateKind.CompactionCompleted,
                "Compaction completed."),
            isSelectedSession: true);

        Assert.IsFalse(tab.PendingManualCompaction);
        Assert.IsNull(tab.ActiveRunId);
        Assert.IsNull(tab.ActiveRunStartedAt);
        Assert.IsNull(startedReduction.SessionStatus);
        Assert.IsNull(activityReduction.SessionStatus);
        Assert.IsNull(completedReduction.SessionStatus);
    }

    [TestMethod]
    public void ReduceAgentEvent_RunningSessionCompactionKeepsActiveRun()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.ActiveRunId = new AgentRunId("run-1");
        tab.ActiveRunStartedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        var reducer = new SessionRuntimeStateReducer();

        var reduction = reducer.ReduceAgentEvent(
            session,
            tab,
            new AgentSessionUpdateEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentSessionUpdateKind.CompactionCompleted,
                "Compaction completed."),
            isSelectedSession: true);

        Assert.AreEqual("run-1", tab.ActiveRunId?.Value);
        Assert.IsNotNull(tab.ActiveRunStartedAt);
        Assert.IsNull(reduction.SessionStatus);
    }

    [TestMethod]
    public void ReduceAgentEvent_IdleRequestsQueuedPromptDrainWithoutUiControls()
    {
        var session = CreateSession();
        var tab = CreateOpenSessionState(session);
        tab.QueuedPrompts.Add(new QueuedSessionPrompt("queued prompt"));
        var reducer = new SessionRuntimeStateReducer();

        var reduction = reducer.ReduceAgentEvent(
            session,
            tab,
            new AgentSessionUpdateEvent(
                ModelProviderIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.Idle,
                "Idle"),
            isSelectedSession: false);

        Assert.IsTrue(reduction.DrainQueuedPrompt);
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
            StartedAt = DateTimeOffset.UtcNow,
        };
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

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
}
