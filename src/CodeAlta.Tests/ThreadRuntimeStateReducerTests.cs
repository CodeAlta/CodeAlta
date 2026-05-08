using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Timeline;
using CodeAlta.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ThreadRuntimeStateReducerTests
{
    [TestMethod]
    public void ReduceAgentEvent_TracksActiveRunIdAndClearsItWhenSessionBecomesIdle()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        var reducer = new ThreadRuntimeStateReducer();

        reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentContentDeltaEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentContentKind.Assistant,
                "assistant-1",
                null,
                "Working..."),
            isSelectedThread: false);

        Assert.AreEqual("run-1", tab.ActiveRunId?.Value);

        var reduction = reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentSessionUpdateEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentSessionUpdateKind.Idle,
                "Idle"),
            isSelectedThread: false);

        Assert.IsNull(tab.ActiveRunId);
        Assert.IsTrue(reduction.ClearThreadStatus);
    }

    [TestMethod]
    public void ReduceAgentEvent_RemovesPendingSteerOnFirstLiveUserContentOnly()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.PendingSteers.Add(new PendingSteerPrompt("First steer"));
        tab.PendingSteers.Add(new PendingSteerPrompt("Second steer"));
        var reducer = new ThreadRuntimeStateReducer();

        reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentContentDeltaEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.User,
                "user-1",
                null,
                "First steer"),
            isSelectedThread: false);
        reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentContentCompletedEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.User,
                "user-1",
                null,
                "First steer"),
            isSelectedThread: false);

        Assert.AreEqual(1, tab.PendingSteers.Count);
        Assert.AreEqual("Second steer", tab.PendingSteers[0].Text);
    }

    [TestMethod]
    public void ReduceAgentEvent_DoesNotConsumePendingSteerDuringHistoryReplay()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.HistoryLoading = true;
        tab.PendingSteers.Add(new PendingSteerPrompt("Pending steer"));
        var reducer = new ThreadRuntimeStateReducer();

        reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentContentCompletedEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.User,
                "user-1",
                null,
                "Pending steer"),
            isSelectedThread: false);

        Assert.AreEqual(1, tab.PendingSteers.Count);
    }

    [TestMethod]
    public void ReduceAgentEvent_MergesUsageAndRequestsInvalidationForSelectedThread()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        var reducer = new ThreadRuntimeStateReducer();

        var reduction = reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentSessionUpdateEvent(
                AgentBackendIds.Copilot,
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
            isSelectedThread: true);

        Assert.IsNotNull(tab.Usage);
        Assert.IsTrue(reduction.ApplySessionUsageProjection);
    }

    [TestMethod]
    public void ReduceAgentEvent_ReconnectingSessionUpdateSetsBusyStatus()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        var reducer = new ThreadRuntimeStateReducer();

        var reduction = reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentSessionUpdateEvent(
                AgentBackendIds.OpenAIResponses,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentSessionUpdateKind.Reconnecting,
                "Reconnecting to ChatGPT/Codex... 1/5"),
            isSelectedThread: true);

        Assert.IsNotNull(reduction.ThreadStatus);
        Assert.AreEqual("Reconnecting to ChatGPT/Codex... 1/5", reduction.ThreadStatus.Value.Message);
        Assert.IsTrue(reduction.ThreadStatus.Value.ShowSpinner);
        Assert.AreEqual(StatusTone.Info, reduction.ThreadStatus.Value.Tone);
    }

    [TestMethod]
    public void ReduceAgentEvent_InfoSessionUpdateSetsBusyStatus()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        var reducer = new ThreadRuntimeStateReducer();

        var reduction = reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentSessionUpdateEvent(
                AgentBackendIds.OpenAIResponses,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentSessionUpdateKind.Info,
                "Backend notice."),
            isSelectedThread: true);

        Assert.IsNotNull(reduction.ThreadStatus);
        Assert.AreEqual("Backend notice.", reduction.ThreadStatus.Value.Message);
        Assert.IsTrue(reduction.ThreadStatus.Value.ShowSpinner);
        Assert.AreEqual(StatusTone.Info, reduction.ThreadStatus.Value.Tone);
    }

    [TestMethod]
    public void ReduceRuntimeEvent_HostCompactionCompletionClearsPendingManualCompactionStatus()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.PendingManualCompaction = true;
        var reducer = new ThreadRuntimeStateReducer();

        var reduction = reducer.ReduceRuntimeEvent(
            thread,
            tab,
            new WorkThreadHostEvent(
                thread.ThreadId,
                DateTimeOffset.UtcNow,
                AgentSessionUpdateKind.CompactionCompleted,
                "Manual compaction completed."),
            isSelectedThread: false);

        Assert.IsFalse(tab.PendingManualCompaction);
        Assert.IsTrue(reduction.ClearThreadStatus);
        Assert.IsTrue(reduction.ApplyShellChromeProjection);
    }

    [TestMethod]
    public void ReduceAgentEvent_DelayedIdleCompactionEventsDoNotReviveThinkingStatus()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.PendingManualCompaction = true;
        var reducer = new ThreadRuntimeStateReducer();

        _ = reducer.ReduceRuntimeEvent(
            thread,
            tab,
            new WorkThreadHostEvent(
                thread.ThreadId,
                DateTimeOffset.UtcNow,
                AgentSessionUpdateKind.CompactionCompleted,
                "Manual compaction completed."),
            isSelectedThread: false);

        var startedReduction = reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentSessionUpdateEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("compaction-run-1"),
                AgentSessionUpdateKind.CompactionStarted,
                "Compaction started."),
            isSelectedThread: true);
        var activityReduction = reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentActivityEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("compaction-run-1"),
                AgentActivityKind.Compaction,
                AgentActivityPhase.Completed,
                "activity-1",
                null,
                "context compaction",
                "Compaction completed."),
            isSelectedThread: true);
        var completedReduction = reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentSessionUpdateEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("compaction-run-1"),
                AgentSessionUpdateKind.CompactionCompleted,
                "Compaction completed."),
            isSelectedThread: true);

        Assert.IsFalse(tab.PendingManualCompaction);
        Assert.IsNull(tab.ActiveRunId);
        Assert.IsNull(tab.ActiveRunStartedAt);
        Assert.IsNull(startedReduction.ThreadStatus);
        Assert.IsNull(activityReduction.ThreadStatus);
        Assert.IsNull(completedReduction.ThreadStatus);
    }

    [TestMethod]
    public void ReduceAgentEvent_RunningThreadCompactionKeepsActiveRun()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.ActiveRunId = new AgentRunId("run-1");
        tab.ActiveRunStartedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        var reducer = new ThreadRuntimeStateReducer();

        var reduction = reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentSessionUpdateEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentSessionUpdateKind.CompactionCompleted,
                "Compaction completed."),
            isSelectedThread: true);

        Assert.AreEqual("run-1", tab.ActiveRunId?.Value);
        Assert.IsNotNull(tab.ActiveRunStartedAt);
        Assert.IsNull(reduction.ThreadStatus);
    }

    [TestMethod]
    public void ReduceAgentEvent_IdleRequestsQueuedPromptDrainWithoutUiControls()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.QueuedPrompts.Add(new QueuedThreadPrompt("queued prompt"));
        var reducer = new ThreadRuntimeStateReducer();

        var reduction = reducer.ReduceAgentEvent(
            thread,
            tab,
            new AgentSessionUpdateEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.Idle,
                "Idle"),
            isSelectedThread: false);

        Assert.IsTrue(reduction.DrainQueuedPrompt);
    }

    private static OpenThreadState CreateOpenThreadState(WorkThreadDescriptor thread)
    {
        var timeline = new ThreadTimelinePresenter(new InlineUiDispatcher(), static () => null);
        return new OpenThreadState(thread, timeline);
    }

    private static WorkThreadDescriptor CreateThread()
    {
        return new WorkThreadDescriptor
        {
            ThreadId = "thread-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Copilot.Value,
            BackendSessionId = "backend-thread-1",
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = WorkThreadStatus.Active,
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
