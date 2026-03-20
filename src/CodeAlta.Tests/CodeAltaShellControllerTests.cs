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
        var projectCatalog = new FakeProjectCatalogLoader(log, [project]);
        var threads = new FakeRecoverableThreadSource(log, [CreateThread("thread-1")]);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(shell, importer, projectCatalog, threads);
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
            new FakeProjectCatalogLoader(log, []),
            new FakeRecoverableThreadSource(log, []));
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
            new FakeProjectCatalogLoader(log, [new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" }]),
            new FakeRecoverableThreadSource(log, [CreateThread("thread-1")]));
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
            new FakeProjectCatalogLoader(log, []),
            new FakeRecoverableThreadSource(log, []));
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
    public async Task SelectGlobalScopeAsync_RoutesSelectionThroughDispatcher()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogLoader(log, []),
            new FakeRecoverableThreadSource(log, []));
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
            new FakeProjectCatalogLoader(log, []),
            new FakeRecoverableThreadSource(log, []));
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
            new FakeProjectCatalogLoader(log, []),
            new FakeRecoverableThreadSource(log, []));
        controller.AttachUiDispatcher(dispatcher);

        await controller.OpenThreadAsync("thread-1", CancellationToken.None);

        Assert.AreEqual(1, dispatcher.InvokeCallCount);
        CollectionAssert.AreEqual(new[] { "Shell.OpenThread:thread-1" }, log);
    }

    private static WorkThreadHostEvent CreateHostEvent(string threadId)
        => new(threadId, DateTimeOffset.UtcNow, AgentSessionUpdateKind.Info, "Updated");

    private static WorkThreadDescriptor CreateThread(string threadId)
    {
        return new WorkThreadDescriptor
        {
            ThreadId = threadId,
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Codex.Value,
            BackendSessionId = $"session-{threadId}",
            ProjectRef = "project-1",
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

    private sealed class FakeProjectCatalogLoader(List<string> log, IReadOnlyList<ProjectDescriptor> projects) : IProjectCatalogLoader
    {
        public Task<IReadOnlyList<ProjectDescriptor>> LoadAsync(CancellationToken cancellationToken)
        {
            log.Add("ProjectCatalog.Load");
            return Task.FromResult(projects);
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
