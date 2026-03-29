using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaShellControllerTests
{
    [TestMethod]
    public async Task ReloadCatalogAsync_UpdatesStatusAndAppliesRecoveredState()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var importer = new FakeImporter(log);
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var projectCatalog = new FakeProjectCatalogStore(log, [project]);
        var threads = new FakeRecoverableThreadSource(log, [CreateThread("thread-1")]);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(shell, importer, projectCatalog, threads, new FakeWorkThreadDeleter(log));
        controller.AttachUiDispatcher(dispatcher);

        await controller.ReloadCatalogAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                "Shell.Status:Refreshing project and thread catalog...:True:Info",
                "Importer.Import",
                "ProjectCatalog.Load",
                "ThreadSource.List",
                "Shell.ApplyRecoveredCatalogState:1:1",
                "Shell.SetReadyStatus",
            },
            log);
        Assert.AreEqual(2, dispatcher.InvokeCallCount);
    }

    [TestMethod]
    public async Task ReloadCatalogAsync_FailureSetsErrorStatus()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var importer = new FakeImporter(log) { ImportException = new InvalidOperationException("boom") };
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(
            shell,
            importer,
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableThreadSource(log, []),
            new FakeWorkThreadDeleter(log));
        controller.AttachUiDispatcher(dispatcher);

        await controller.ReloadCatalogAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                "Shell.Status:Refreshing project and thread catalog...:True:Info",
                "Importer.Import",
                "Shell.Status:Failed to refresh catalog: boom:False:Error",
            },
            log);
    }

    [TestMethod]
    public async Task InitializeAsync_RefreshesCatalogAndSchedulesStartupRestore()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, [new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" }]),
            new FakeRecoverableThreadSource(log, [CreateThread("thread-1")]),
            new FakeWorkThreadDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        await controller.InitializeAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                "Shell.InitializeChatBackends",
                "Importer.Import",
                "ProjectCatalog.Load",
                "ThreadSource.List",
                "Shell.ApplyRecoveredCatalogState:1:1",
                "Shell.RefreshCatalogAndThreadWorkspace",
                "Shell.SetReadyStatus",
                "Shell.SetInitialized:True",
                "Shell.TrySchedulePendingStartupThreadRestore",
            },
            log);
    }

    [TestMethod]
    public async Task ApplyRuntimeEventAsync_RoutesEventThroughDispatcher()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableThreadSource(log, []),
            new FakeWorkThreadDeleter(log));
        controller.AttachUiDispatcher(dispatcher);
        var runtimeEvent = CreateHostEvent("thread-1");

        await controller.ApplyRuntimeEventAsync(runtimeEvent, CancellationToken.None);

        Assert.AreSame(runtimeEvent, shell.LastRuntimeEvent);
        Assert.AreEqual(1, dispatcher.InvokeCallCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "Shell.HandleRuntimeEvent:thread-1",
            },
            log);
    }

    [TestMethod]
    public void QueueRuntimeEvent_AutoDrainAppliesQueuedEventsWhenDispatcherIsAttached()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableThreadSource(log, []),
            new FakeWorkThreadDeleter(log));
        controller.AttachUiDispatcher(dispatcher);

        controller.QueueRuntimeEvent(CreateHostEvent("thread-1"), CancellationToken.None);
        controller.QueueRuntimeEvent(CreateHostEvent("thread-2"), CancellationToken.None);
        Assert.AreEqual(0, dispatcher.InvokeCallCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "Shell.HandleRuntimeEvent:thread-1",
                "Shell.HandleRuntimeEvent:thread-2",
            },
            log);
    }

    [TestMethod]
    public void DrainPendingRuntimeEvents_MergesCompatibleContentDeltas()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableThreadSource(log, []),
            new FakeWorkThreadDeleter(log));

        controller.QueueRuntimeEvent(CreateToolDeltaEvent("thread-1", "tool-1", "alpha"), CancellationToken.None);
        controller.QueueRuntimeEvent(CreateToolDeltaEvent("thread-1", "tool-1", "beta"), CancellationToken.None);

        var drained = controller.DrainPendingRuntimeEvents();

        Assert.AreEqual(2, drained);
        Assert.AreEqual(1, log.Count);
        var runtimeEvent = Assert.IsInstanceOfType<WorkThreadAgentEvent>(shell.LastRuntimeEvent);
        var delta = Assert.IsInstanceOfType<AgentContentDeltaEvent>(runtimeEvent.Event);
        Assert.AreEqual("alphabeta", delta.Delta);
    }

    [TestMethod]
    public async Task SelectGlobalScopeAsync_RoutesSelectionThroughDispatcher()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableThreadSource(log, []),
            new FakeWorkThreadDeleter(log));
        controller.AttachUiDispatcher(dispatcher);

        await controller.SelectGlobalScopeAsync(CancellationToken.None);

        Assert.AreEqual(1, dispatcher.InvokeCallCount);
        CollectionAssert.AreEqual(new[] { "Shell.SelectGlobalScope" }, log);
    }

    [TestMethod]
    public async Task SelectProjectScopeAsync_RoutesProjectScopeThroughDispatcher()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableThreadSource(log, []),
            new FakeWorkThreadDeleter(log));
        controller.AttachUiDispatcher(dispatcher);

        await controller.SelectProjectScopeAsync("project-1", CancellationToken.None);

        Assert.AreEqual(1, dispatcher.InvokeCallCount);
        CollectionAssert.AreEqual(new[] { "Shell.SelectProjectScope:project-1" }, log);
    }

    [TestMethod]
    public async Task OpenThreadAsync_RoutesThreadSelectionThroughDispatcher()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableThreadSource(log, []),
            new FakeWorkThreadDeleter(log));
        controller.AttachUiDispatcher(dispatcher);

        await controller.OpenThreadAsync("thread-1", CancellationToken.None);

        Assert.AreEqual(1, dispatcher.InvokeCallCount);
        CollectionAssert.AreEqual(new[] { "Shell.OpenThread:thread-1" }, log);
    }

    [TestMethod]
    public async Task LoadProjectThreadsAsync_ReturnsProjectThreadsOrderedByRecency()
    {
        var log = new List<string>();
        var threads = new[]
        {
            CreateThread("thread-1"),
            CreateThread("thread-2"),
            CreateThread("thread-3", projectId: "project-2"),
        };
        threads[0].LastActiveAt = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        threads[1].LastActiveAt = DateTimeOffset.Parse("2026-03-29T11:00:00+00:00");
        var controller = new CodeAltaShellController(
            new FakeShell(log),
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableThreadSource(log, threads),
            new FakeWorkThreadDeleter(log));

        var projectThreads = await controller.LoadProjectThreadsAsync("project-1", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "thread-2", "thread-1" }, projectThreads.Select(static thread => thread.ThreadId).ToArray());
    }

    [TestMethod]
    public async Task SaveProjectAsync_PersistsAndReloadsCatalog()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var catalog = new FakeProjectCatalogStore(log, [project]);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            catalog,
            new FakeRecoverableThreadSource(log, [CreateThread("thread-1")]),
            new FakeWorkThreadDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        project.DisplayName = "CodeAlta UI";
        await controller.SaveProjectAsync(project, CancellationToken.None);

        Assert.AreEqual("CodeAlta UI", catalog.Projects.Single().DisplayName);
        CollectionAssert.Contains(log, "ProjectCatalog.Save:project-1");
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:1");
    }

    [TestMethod]
    public async Task DeleteThreadAsync_DeletesThreadAndReloadsCatalog()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var thread = CreateThread("thread-1");
        var deleter = new FakeWorkThreadDeleter(log) { DeleteResult = true };
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableThreadSource(log, [thread]),
            deleter);
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var deletedByBackend = await controller.DeleteThreadAsync(thread.ThreadId, CancellationToken.None);

        Assert.IsTrue(deletedByBackend);
        Assert.AreEqual(thread.ThreadId, deleter.DeletedThreadIds.Single());
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:0:1");
    }

    [TestMethod]
    public async Task DeleteProjectAsync_DeletesThreadsMarksProjectArchivedAndReloadsCatalog()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var catalog = new FakeProjectCatalogStore(log, [project]);
        var deleter = new FakeWorkThreadDeleter(log);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            catalog,
            new FakeRecoverableThreadSource(log, [CreateThread("thread-1"), CreateThread("thread-2")]),
            deleter);
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var result = await controller.DeleteProjectAsync(project.Id, CancellationToken.None);

        Assert.IsTrue(catalog.Projects.Single().Archived);
        CollectionAssert.AreEquivalent(new[] { "thread-1", "thread-2" }, deleter.DeletedThreadIds.ToArray());
        CollectionAssert.AreEquivalent(new[] { "thread-1", "thread-2" }, result.DeletedThreadIds.ToArray());
        CollectionAssert.Contains(log, "ProjectCatalog.Save:project-1");
    }

    private static WorkThreadHostEvent CreateHostEvent(string threadId)
        => new(threadId, DateTimeOffset.UtcNow, AgentSessionUpdateKind.Info, "Updated");

    private static WorkThreadAgentEvent CreateToolDeltaEvent(string threadId, string contentId, string delta)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new WorkThreadAgentEvent(
            threadId,
            new AgentContentDeltaEvent(
                AgentBackendIds.Codex,
                "session-1",
                timestamp,
                null,
                AgentContentKind.ToolOutput,
                contentId,
                "activity-1",
                delta));
    }

    private static WorkThreadDescriptor CreateThread(string threadId, string projectId = "project-1")
    {
        return new WorkThreadDescriptor
        {
            ThreadId = threadId,
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Codex.Value,
            BackendSessionId = $"session-{threadId}",
            ProjectRef = projectId,
            WorkingDirectory = @"C:\repo",
            Title = "Test thread",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };
    }

    private sealed class FakeShell(List<string> log) : ICodeAltaShell
    {
        public WorkThreadRuntimeEvent? LastRuntimeEvent { get; private set; }

        public Task InitializeChatBackendsAsync(CancellationToken cancellationToken)
        {
            log.Add("Shell.InitializeChatBackends");
            return Task.CompletedTask;
        }

        public void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
            => log.Add($"Shell.Status:{message}:{showSpinner}:{tone}");

        public void ApplyRecoveredCatalogState(IReadOnlyList<ProjectDescriptor> projects, IReadOnlyList<WorkThreadDescriptor> threads)
            => log.Add($"Shell.ApplyRecoveredCatalogState:{projects.Count}:{threads.Count}");

        public void SetReadyStatusForCurrentSelection()
            => log.Add("Shell.SetReadyStatus");

        public void HandleRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
        {
            LastRuntimeEvent = runtimeEvent;
            log.Add($"Shell.HandleRuntimeEvent:{runtimeEvent.ThreadId}");
        }

        public void RefreshCatalogAndThreadWorkspace()
            => log.Add("Shell.RefreshCatalogAndThreadWorkspace");

        public void TrySchedulePendingStartupThreadRestore(CancellationToken cancellationToken)
            => log.Add("Shell.TrySchedulePendingStartupThreadRestore");

        public void SelectGlobalScope()
            => log.Add("Shell.SelectGlobalScope");

        public void SelectProjectScope(string projectId)
            => log.Add($"Shell.SelectProjectScope:{projectId}");

        public void OpenThread(string threadId)
            => log.Add($"Shell.OpenThread:{threadId}");

        public void SetInitialized(bool isInitialized)
            => log.Add($"Shell.SetInitialized:{isInitialized}");
    }

    private sealed class FakeImporter(List<string> log) : IKnownProjectImporter
    {
        public Exception? ImportException { get; set; }

        public Task ImportAsync(CancellationToken cancellationToken)
        {
            log.Add("Importer.Import");
            if (ImportException is not null)
            {
                throw ImportException;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeProjectCatalogStore(List<string> log, IReadOnlyList<ProjectDescriptor> projects) : IProjectCatalogStore
    {
        public List<ProjectDescriptor> Projects { get; } = projects.ToList();

        public Task<IReadOnlyList<ProjectDescriptor>> LoadAsync(CancellationToken cancellationToken)
        {
            log.Add("ProjectCatalog.Load");
            return Task.FromResult<IReadOnlyList<ProjectDescriptor>>(Projects.ToArray());
        }

        public Task<ProjectDescriptor?> GetByIdAsync(string projectId, CancellationToken cancellationToken)
        {
            log.Add($"ProjectCatalog.GetById:{projectId}");
            return Task.FromResult(Projects.FirstOrDefault(project => string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase)));
        }

        public Task SaveAsync(ProjectDescriptor project, CancellationToken cancellationToken)
        {
            log.Add($"ProjectCatalog.Save:{project.Id}");
            Projects.RemoveAll(existing => string.Equals(existing.Id, project.Id, StringComparison.OrdinalIgnoreCase));
            Projects.Add(project);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRecoverableThreadSource(List<string> log, IReadOnlyList<WorkThreadDescriptor> threads) : IRecoverableThreadSource
    {
        public Task<IReadOnlyList<WorkThreadDescriptor>> ListRecoverableThreadsAsync(CancellationToken cancellationToken)
        {
            log.Add("ThreadSource.List");
            return Task.FromResult(threads);
        }
    }

    private sealed class FakeWorkThreadDeleter(List<string> log) : IWorkThreadDeleter
    {
        public List<string> DeletedThreadIds { get; } = [];

        public bool DeleteResult { get; set; }

        public Task<bool> DeleteThreadAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken)
        {
            log.Add($"ThreadDeleter.Delete:{thread.ThreadId}");
            DeletedThreadIds.Add(thread.ThreadId);
            thread.Status = WorkThreadStatus.Archived;
            return Task.FromResult(DeleteResult);
        }
    }

    private sealed class FakeUiDispatcher : IUiDispatcher
    {
        public int InvokeCallCount { get; private set; }

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
            InvokeCallCount++;
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvokeCallCount++;
            return Task.FromResult(action());
        }
    }
}
