using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Timeline;
using CodeAlta.Search;
using CodeAlta.Threading;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ThreadRuntimeEventCoordinatorTests
{
    [TestMethod]
    public void ShouldPromoteAgentEventToThinking_IgnoresToolOutputDeltas()
    {
        var delta = new AgentContentDeltaEvent(
            AgentBackendIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentContentKind.ToolOutput,
            "tool-1",
            "activity-1",
            "line 1");

        Assert.IsFalse(ThreadRuntimeEventCoordinator.ShouldPromoteAgentEventToThinking(delta));
    }

    [TestMethod]
    public void ShouldRefreshShellChromeAfterRuntimeEvent_IgnoresToolOutputDeltas()
    {
        var runtimeEvent = new WorkThreadAgentEvent(
            "thread-1",
            new AgentContentDeltaEvent(
                AgentBackendIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.ToolOutput,
                "tool-1",
                "activity-1",
                "line 1"));

        Assert.IsFalse(ThreadRuntimeEventCoordinator.ShouldRefreshShellChromeAfterRuntimeEvent(runtimeEvent));
    }

    [TestMethod]
    public void ShouldRefreshShellChromeAfterRuntimeEvent_RefreshesForAssistantCompletion()
    {
        var runtimeEvent = new WorkThreadAgentEvent(
            "thread-1",
            new AgentContentCompletedEvent(
                AgentBackendIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentContentKind.Assistant,
                "assistant-1",
                null,
                "Final answer"));

        Assert.IsTrue(ThreadRuntimeEventCoordinator.ShouldRefreshShellChromeAfterRuntimeEvent(runtimeEvent));
    }

    [TestMethod]
    public void HandleAgentEvent_KeepsManualCompactionStatusWhileProgressEventsArrive()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.PendingManualCompaction = true;
        tab.StatusMessage = $"Compacting '{thread.Title}'...";
        tab.StatusBusy = true;
        tab.StatusTone = StatusTone.Info;
        tab.HasCustomStatus = true;

        var coordinator = CreateCoordinator(thread, tab);
        coordinator.HandleAgentEvent(
            thread,
            tab,
            new AgentActivityEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentActivityKind.ToolCall,
                AgentActivityPhase.Started,
                "activity-1",
                null,
                "compact",
                "Compacting session..."));

        Assert.AreEqual($"Compacting '{thread.Title}'...", tab.StatusMessage);
        Assert.IsTrue(tab.StatusBusy);
        Assert.AreEqual(StatusTone.Info, tab.StatusTone);
    }

    [TestMethod]
    public void ApplyRuntimeEvent_HostCompactionCompletionClearsPendingManualCompactionStatus()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.PendingManualCompaction = true;
        tab.StatusMessage = $"Compacting '{thread.Title}'...";
        tab.StatusBusy = true;
        tab.StatusTone = StatusTone.Info;
        tab.HasCustomStatus = true;

        var coordinator = CreateCoordinator(thread, tab);
        coordinator.ApplyRuntimeEvent(
            new WorkThreadHostEvent(
                thread.ThreadId,
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
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        var renderer = new ThreadRuntimeTimelineRenderer(static () => false);
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
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        var renderer = new ThreadRuntimeTimelineRenderer(static () => false);
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
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.PendingSteers.Add(new PendingSteerPrompt("First steer"));
        tab.PendingSteers.Add(new PendingSteerPrompt("Second steer"));

        var coordinator = CreateCoordinator(thread, tab);
        coordinator.HandleAgentEvent(
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
                "First steer"));
        coordinator.HandleAgentEvent(
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
                "First steer"));

        Assert.AreEqual(1, tab.PendingSteers.Count);
        Assert.AreEqual("Second steer", tab.PendingSteers[0].Text);
    }

    [TestMethod]
    public void HandleAgentEvent_RefreshesQueuedPromptListWhenPendingSteerIsConsumed()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.PendingSteers.Add(new PendingSteerPrompt("First steer"));
        var refreshQueuedPromptListCount = 0;

        var coordinator = CreateCoordinator(
            thread,
            tab,
            refreshQueuedPromptList: () => refreshQueuedPromptListCount++);
        coordinator.HandleAgentEvent(
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
                "First steer"));

        Assert.AreEqual(0, tab.PendingSteers.Count);
        Assert.AreEqual(1, refreshQueuedPromptListCount);
    }

    [TestMethod]
    public void HandleAgentEvent_DoesNotConsumePendingSteerDuringHistoryReplay()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.HistoryLoading = true;
        tab.PendingSteers.Add(new PendingSteerPrompt("Pending steer"));

        var coordinator = CreateCoordinator(thread, tab);
        coordinator.HandleAgentEvent(
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
                "Pending steer"));

        Assert.AreEqual(1, tab.PendingSteers.Count);
    }

    [TestMethod]
    public void HandleAgentEvent_ClearsPendingSteersWhenSessionBecomesIdle()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.PendingSteers.Add(new PendingSteerPrompt("Pending steer"));

        var coordinator = CreateCoordinator(thread, tab);
        coordinator.HandleAgentEvent(
            thread,
            tab,
            new AgentSessionUpdateEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.Idle,
                "Idle"));

        Assert.AreEqual(0, tab.PendingSteers.Count);
    }

    [TestMethod]
    public void HandleAgentEvent_RefreshesQueuedPromptListWhenPendingSteersAreCleared()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        tab.PendingSteers.Add(new PendingSteerPrompt("Pending steer"));
        var refreshQueuedPromptListCount = 0;

        var coordinator = CreateCoordinator(
            thread,
            tab,
            refreshQueuedPromptList: () => refreshQueuedPromptListCount++);
        coordinator.HandleAgentEvent(
            thread,
            tab,
            new AgentSessionUpdateEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.Idle,
                "Idle"));

        Assert.AreEqual(0, tab.PendingSteers.Count);
        Assert.AreEqual(1, refreshQueuedPromptListCount);
    }

    [TestMethod]
    public void HandleAgentEvent_TracksActiveRunIdAndClearsItWhenSessionBecomesIdle()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        var coordinator = CreateCoordinator(thread, tab);

        coordinator.HandleAgentEvent(
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
                "Working..."));

        Assert.AreEqual("run-1", tab.ActiveRunId?.Value);

        coordinator.HandleAgentEvent(
            thread,
            tab,
            new AgentSessionUpdateEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                new AgentRunId("run-1"),
                AgentSessionUpdateKind.Idle,
                "Idle"));

        Assert.IsNull(tab.ActiveRunId);
    }

    [TestMethod]
    public void HandleAgentEvent_InvalidatesProjectFileSearchWhenFileChangesArrive()
    {
        var thread = CreateThread();
        var tab = CreateOpenThreadState(thread);
        var searchService = new FakeProjectFileSearchService();
        var coordinator = CreateCoordinator(thread, tab, projectFileSearchService: searchService);

        coordinator.HandleAgentEvent(
            thread,
            tab,
            new AgentActivityEvent(
                AgentBackendIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentActivityKind.FileChange,
                AgentActivityPhase.Completed,
                "activity-1",
                null,
                "write_file",
                "Updated Program.cs"));

        Assert.AreEqual(1, searchService.Invalidations.Count);
        Assert.AreEqual(thread.WorkingDirectory, searchService.Invalidations[0].ProjectRoot);
        Assert.AreEqual(ProjectFileInvalidationReason.FileSystemWrite, searchService.Invalidations[0].Reason);
    }

    private static ThreadRuntimeEventCoordinator CreateCoordinator(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        Action? refreshQueuedPromptList = null,
        IProjectFileSearchService? projectFileSearchService = null)
    {
        return new ThreadRuntimeEventCoordinator(
            findThread: id => id == thread.ThreadId ? thread : null,
            findOpenThread: id => id == tab.Thread.ThreadId ? tab : null,
            getAutoApproveEnabled: static () => false,
            isSelectedThread: id => id == thread.ThreadId,
            invalidateSelectedSessionUsage: static () => { },
            refreshShellChrome: static () => { },
            setShellStatus: static (_, _, _) => { },
            setThreadStatus: static (state, message, busy, tone) =>
            {
                state.StatusMessage = message;
                state.StatusBusy = busy;
                state.StatusTone = tone;
                state.HasCustomStatus = true;
            },
            clearThreadStatus: static state =>
            {
                state.StatusMessage = null;
                state.StatusBusy = false;
                state.StatusTone = StatusTone.Info;
                state.HasCustomStatus = false;
            },
            refreshQueuedPromptList: refreshQueuedPromptList ?? (() => { }),
            drainQueuedPromptAsync: static (_, _) => Task.CompletedTask,
            projectFileSearchService: projectFileSearchService ?? NullProjectFileSearchService.Instance);
    }

    private static OpenThreadState CreateOpenThreadState(WorkThreadDescriptor thread)
    {
        var timeline = new ThreadTimelinePresenter(new InlineUiDispatcher(), () => true, static () => null);
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
            AgentBackendIds.Copilot,
            "session-1",
            timestamp,
            null,
            "session_start",
            hash,
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
