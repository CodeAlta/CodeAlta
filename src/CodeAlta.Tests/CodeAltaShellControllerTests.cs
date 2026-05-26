using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using System.IO;

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
        var threads = new FakeRecoverableSessionSource(log, [CreateThread("thread-1")]);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(shell, importer, projectCatalog, threads, new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(dispatcher);

        await controller.ReloadCatalogAsync(CancellationToken.None);

        CollectionAssert.Contains(log, "Shell.Status:Refreshing project and thread catalog...:True:Info");
        CollectionAssert.Contains(log, "Importer.Import");
        CollectionAssert.Contains(log, "ProjectCatalog.Load");
        CollectionAssert.Contains(log, "ThreadSource.List");
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:1:KeepMissing");
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:1");
        CollectionAssert.Contains(log, "Shell.SetReadyStatus");
        Assert.IsTrue(dispatcher.InvokeCallCount >= 4);
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
            new FakeRecoverableSessionSource(log, []),
            new FakeSessionDeleter(log));
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
            new FakeRecoverableSessionSource(log, [CreateThread("thread-1")]),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        await controller.InitializeAsync(CancellationToken.None);

        CollectionAssert.Contains(log, "Shell.InitializeChatBackends");
        CollectionAssert.Contains(log, "Shell.PublishStartupCatalogProjectionReady");
        CollectionAssert.Contains(log, "Shell.SetReadyStatus");
        CollectionAssert.Contains(log, "Shell.SetInitialized:True");
        CollectionAssert.Contains(log, "Importer.Import");
        CollectionAssert.Contains(log, "ProjectCatalog.Load");
        CollectionAssert.Contains(log, "ThreadSource.List");
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:1");
        CollectionAssert.Contains(log, "Shell.TrySchedulePendingStartupThreadRestore");
    }

    [TestMethod]
    public async Task InitializeAsync_ShowsSessionsBeforeProviderInitializationCompletes()
    {
        var log = new List<string>();
        var shell = new FakeShell(log)
        {
            InitializeChatBackendsCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, [new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" }]),
            new FakeRecoverableSessionSource(log, [CreateThread("thread-1")]),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var initializationTask = controller.InitializeAsync(CancellationToken.None);

        await WaitUntilAsync(() => log.Contains("Shell.ApplyRecoveredCatalogState:1:1")).ConfigureAwait(false);

        Assert.IsFalse(initializationTask.IsCompleted, "Provider initialization should still be running.");
        CollectionAssert.Contains(log, "Shell.InitializeChatBackends");
        CollectionAssert.Contains(log, "Importer.Import");
        CollectionAssert.Contains(log, "ThreadSource.List");
        CollectionAssert.Contains(log, "Shell.TrySchedulePendingStartupThreadRestore");

        shell.InitializeChatBackendsCompletion.SetResult(true);
        await initializationTask.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InitializeAsync_StartsProviderInitializationBeforeSlowSessionScanCompletes()
    {
        var log = new List<string>();
        var shell = new FakeShell(log)
        {
            InitializeChatBackendsCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var threadSource = new BlockingRecoverableSessionSource(
            log,
            [
                CreateThread("thread-1"),
            ]);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, [project]),
            threadSource,
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var initializationTask = controller.InitializeAsync(CancellationToken.None);

        await WaitUntilAsync(() => threadSource.ListStarted.Task.IsCompleted).ConfigureAwait(false);

        CollectionAssert.Contains(log, "Shell.InitializeChatBackends");
        Assert.IsFalse(log.Any(static entry => entry.StartsWith("Shell.ApplyRecoveredCatalogState:", StringComparison.Ordinal)));

        threadSource.AllowCompletion();
        shell.InitializeChatBackendsCompletion.SetResult(true);
        await initializationTask.ConfigureAwait(false);

        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:1");
    }

    [TestMethod]
    public async Task InitializeAsync_AppliesRecoverableSessionsProgressivelyFromOneStream()
    {
        var log = new List<string>();
        var codexBackendId = new AgentBackendId("codex");
        var slowBackendId = new AgentBackendId("slow");
        var shell = new FakeShell(log);
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var threadSource = new FakeRecoverableSessionSource(
            log,
            [
                CreateThread("thread-codex", backendId: codexBackendId.Value),
                CreateThread("thread-slow", backendId: slowBackendId.Value),
            ]);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, [project]),
            threadSource,
            new FakeSessionDeleter(log),
            [
                new ModelProviderDescriptor(codexBackendId, "Codex"),
                new ModelProviderDescriptor(slowBackendId, "Slow"),
            ]);
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        await controller.InitializeAsync(CancellationToken.None);

        CollectionAssert.Contains(log, "ThreadSource.List");
        Assert.AreEqual(1, log.Count(static entry => entry == "ThreadSource.List"));
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:1:KeepMissing");
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:2:KeepMissing");
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:2");
    }

    [TestMethod]
    public async Task InitializeAsync_LoadsCompleteSessionCatalogThroughSingleRecoverableSource()
    {
        var log = new List<string>();
        var codexBackendId = new AgentBackendId("codex");
        var slowBackendId = new AgentBackendId("slow");
        var shell = new FakeShell(log);
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var parent = CreateThread("thread-parent", backendId: codexBackendId.Value);
        var children = Enumerable.Range(1, 8)
            .Select(index =>
            {
                var child = CreateThread($"thread-child-{index}", backendId: codexBackendId.Value);
                child.ParentThreadId = parent.ThreadId;
                child.LastActiveAt = parent.LastActiveAt.AddMinutes(index);
                return child;
            })
            .ToArray();
        var completeThreads = new[] { parent }.Concat(children).ToArray();
        var threadSource = new FakeRecoverableSessionSource(log, completeThreads);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, [project]),
            threadSource,
            new FakeSessionDeleter(log),
            [
                new ModelProviderDescriptor(codexBackendId, "Codex"),
                new ModelProviderDescriptor(slowBackendId, "Slow"),
            ]);
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        await controller.InitializeAsync(CancellationToken.None);

        CollectionAssert.Contains(log, "ThreadSource.List");
        Assert.AreEqual(1, log.Count(static entry => entry == "ThreadSource.List"));
        var finalCatalogApplication = log.Last(entry => entry.StartsWith("Shell.ApplyRecoveredCatalogState:", StringComparison.Ordinal));
        Assert.AreEqual("Shell.ApplyRecoveredCatalogState:1:9", finalCatalogApplication);
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
            new FakeRecoverableSessionSource(log, []),
            new FakeSessionDeleter(log));
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
            new FakeRecoverableSessionSource(log, []),
            new FakeSessionDeleter(log));
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
            new FakeRecoverableSessionSource(log, []),
            new FakeSessionDeleter(log));

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
            new FakeRecoverableSessionSource(log, []),
            new FakeSessionDeleter(log));
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
            new FakeRecoverableSessionSource(log, []),
            new FakeSessionDeleter(log));
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
            new FakeRecoverableSessionSource(log, []),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(dispatcher);

        await controller.OpenThreadAsync("thread-1", CancellationToken.None);

        Assert.AreEqual(1, dispatcher.InvokeCallCount);
        CollectionAssert.AreEqual(new[] { "Shell.OpenThread:thread-1", "Shell.FocusPromptEditor" }, log);
    }

    [TestMethod]
    public async Task OpenFolderAsync_UpsertsProjectAndSelectsProjectWithoutReloadingThreads()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var catalog = new FakeProjectCatalogStore(log, []);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            catalog,
            new FakeRecoverableSessionSource(log, [CreateThread("thread-1")]),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(dispatcher);

        var projectPath = Path.Combine(Path.GetTempPath(), "codealta-open-folder-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectPath);

        try
        {
            var project = await controller.OpenFolderAsync(projectPath, CancellationToken.None);

            Assert.AreEqual(projectPath, project.ProjectPath);
            CollectionAssert.AreEqual(
                new[]
                {
                    $"Shell.Status:Opening '{projectPath}'...:True:Info",
                    $"ProjectCatalog.UpsertFromPath:{projectPath}",
                    $"Shell.UpsertProject:{project.Id}",
                    $"Shell.SelectProjectScope:{project.Id}",
                    "Shell.SetReadyStatus",
                    "Shell.FocusPromptEditor",
                },
                log);
            Assert.AreEqual(2, dispatcher.InvokeCallCount);
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task OpenFolderAsync_ProjectReference_SelectsExistingProjectAndFocusesPrompt()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var existingProject = new ProjectDescriptor
        {
            Id = "project-1",
            Slug = "codealta",
            Name = "CodeAlta",
            DisplayName = "CodeAlta",
            ProjectPath = @"C:\repo",
            DefaultBranch = "main",
        };
        var catalog = new FakeProjectCatalogStore(log, [existingProject]);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            catalog,
            new FakeRecoverableSessionSource(log, [CreateThread("thread-1")]),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(dispatcher);

        var project = await controller.OpenFolderAsync("CodeAlta", CancellationToken.None);

        Assert.AreSame(existingProject, project);
        CollectionAssert.AreEqual(
            new[]
            {
                "Shell.Status:Opening 'CodeAlta'...:True:Info",
                "ProjectCatalog.Load",
                "Shell.UpsertProject:project-1",
                "Shell.SelectProjectScope:project-1",
                "Shell.SetReadyStatus",
                "Shell.FocusPromptEditor",
            },
            log);
    }

    [TestMethod]
    public async Task OpenFolderAsync_HiddenProjectReference_RemainsHiddenUnlessIncluded()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var hiddenProject = new ProjectDescriptor
        {
            Id = "project-1",
            Slug = "codealta",
            Name = "CodeAlta",
            DisplayName = "CodeAlta",
            ProjectPath = @"C:\repo",
            DefaultBranch = "main",
            Archived = true,
        };
        var catalog = new FakeProjectCatalogStore(log, [hiddenProject]);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            catalog,
            new FakeRecoverableSessionSource(log, [CreateThread("thread-1")]),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => controller.OpenFolderAsync("CodeAlta", includeHidden: false, CancellationToken.None));
        Assert.IsTrue(hiddenProject.Archived);

        log.Clear();
        var reopenedProject = await controller.OpenFolderAsync("CodeAlta", includeHidden: true, CancellationToken.None);

        Assert.AreSame(hiddenProject, reopenedProject);
        Assert.IsFalse(hiddenProject.Archived);
        CollectionAssert.Contains(log, "ProjectCatalog.Save:project-1");
        CollectionAssert.Contains(log, "Shell.FocusPromptEditor");
    }

    [TestMethod]
    public async Task OpenFolderAsync_TildePath_ExpandsHomeDirectoryBeforeUpsert()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.IsFalse(string.IsNullOrWhiteSpace(homeDirectory));

        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableSessionSource(log, [CreateThread("thread-1")]),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var tempProject = Path.Combine(homeDirectory, "codealta-open-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProject);

        try
        {
            var relativeHomePath = "~" + tempProject[homeDirectory.Length..];
            var project = await controller.OpenFolderAsync(relativeHomePath, CancellationToken.None);

            Assert.AreEqual(tempProject, project.ProjectPath);
            CollectionAssert.Contains(log, $"ProjectCatalog.UpsertFromPath:{tempProject}");
            CollectionAssert.Contains(log, "Shell.FocusPromptEditor");
        }
        finally
        {
            Directory.Delete(tempProject, recursive: true);
        }
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
            new FakeRecoverableSessionSource(log, threads),
            new FakeSessionDeleter(log));

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
            new FakeRecoverableSessionSource(log, [CreateThread("thread-1")]),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        project.DisplayName = "CodeAlta UI";
        await controller.SaveProjectAsync(project, CancellationToken.None);

        Assert.AreEqual("CodeAlta UI", catalog.Projects.Single().DisplayName);
        CollectionAssert.Contains(log, "ProjectCatalog.Save:project-1");
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:1");
    }

    [TestMethod]
    public async Task DeleteSessionAsync_DeletesThreadWithoutReloadingCatalog()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var thread = CreateThread("thread-1");
        var deleter = new FakeSessionDeleter(log) { DeleteResult = true };
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableSessionSource(log, [thread]),
            deleter);
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var result = await controller.DeleteSessionAsync(thread, [thread], CancellationToken.None);

        Assert.IsTrue(result.DeletedByBackend);
        CollectionAssert.AreEqual(new[] { thread.ThreadId }, result.DeletedThreadIds.ToArray());
        Assert.AreEqual(thread.ThreadId, deleter.DeletedThreadIds.Single());
        Assert.IsFalse(log.Contains("Importer.Import"));
        Assert.IsFalse(log.Contains("ProjectCatalog.Load"));
        Assert.IsFalse(log.Contains("ThreadSource.List"));
        Assert.IsFalse(log.Contains("ThreadSource.List"));
    }

    [TestMethod]
    public async Task DeleteSessionAsync_DeletesChildThreadsBeforeParent()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var parent = CreateThread("thread-parent");
        var child = CreateThread("thread-child");
        var grandchild = CreateThread("thread-grandchild");
        var sibling = CreateThread("thread-sibling");
        child.ParentThreadId = parent.ThreadId;
        grandchild.ParentThreadId = child.ThreadId;
        var deleter = new FakeSessionDeleter(log) { DeleteResult = true };
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableSessionSource(log, [parent, child, grandchild, sibling]),
            deleter);
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var result = await controller.DeleteSessionAsync(parent, [parent, child, grandchild, sibling], CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { grandchild.ThreadId, child.ThreadId, parent.ThreadId },
            result.DeletedThreadIds.ToArray());
        CollectionAssert.AreEqual(result.DeletedThreadIds.ToArray(), deleter.DeletedThreadIds.ToArray());
        Assert.IsFalse(deleter.DeletedThreadIds.Contains(sibling.ThreadId));
    }

    [TestMethod]
    public async Task DeleteProjectAsync_DeletesThreadsMarksProjectArchivedWithoutReloadingCatalog()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var catalog = new FakeProjectCatalogStore(log, [project]);
        var deleter = new FakeSessionDeleter(log);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            catalog,
            new FakeRecoverableSessionSource(log, [CreateThread("thread-1"), CreateThread("thread-2")]),
            deleter);
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var threads = new[] { CreateThread("thread-1"), CreateThread("thread-2") };
        var result = await controller.DeleteProjectAsync(project, threads, CancellationToken.None);

        Assert.IsTrue(catalog.Projects.Single().Archived);
        CollectionAssert.AreEquivalent(new[] { "thread-1", "thread-2" }, deleter.DeletedThreadIds.ToArray());
        CollectionAssert.AreEquivalent(new[] { "thread-1", "thread-2" }, result.DeletedThreadIds.ToArray());
        CollectionAssert.Contains(log, "ProjectCatalog.Save:project-1");
        Assert.IsFalse(log.Contains("Importer.Import"));
        Assert.IsFalse(log.Contains("ProjectCatalog.Load"));
        Assert.IsFalse(log.Contains("ThreadSource.List"));
        Assert.IsFalse(log.Contains("ThreadSource.List"));
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

    private static SessionViewDescriptor CreateThread(
        string threadId,
        string projectId = "project-1",
        string? backendId = null)
    {
        return new SessionViewDescriptor
        {
            ThreadId = threadId,
            Kind = WorkThreadKind.ProjectThread,
            BackendId = backendId ?? AgentBackendIds.Codex.Value,
            ProjectRef = projectId,
            WorkingDirectory = @"C:\repo",
            Title = "Test thread",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("The expected condition was not reached before the timeout elapsed.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private sealed class FakeShell(List<string> log) : ICodeAltaShell
    {
        private readonly Dictionary<string, TaskCompletionSource<bool>> _backendInitializationCompletions = new(StringComparer.OrdinalIgnoreCase);

        public WorkThreadRuntimeEvent? LastRuntimeEvent { get; private set; }

        public List<string?> ProviderSessionLoadStatuses { get; } = [];

        public TaskCompletionSource<bool>? InitializeChatBackendsCompletion { get; init; }

        public Task InitializeChatBackendsAsync(CancellationToken cancellationToken)
        {
            log.Add("Shell.InitializeChatBackends");
            return InitializeChatBackendsCompletion?.Task ?? Task.CompletedTask;
        }

        public Task InitializeChatBackendAsync(AgentBackendId backendId, CancellationToken cancellationToken)
        {
            log.Add($"Shell.InitializeChatBackend:{backendId.Value}");
            return _backendInitializationCompletions.TryGetValue(backendId.Value, out var completion)
                ? completion.Task.WaitAsync(cancellationToken)
                : Task.CompletedTask;
        }

        public void BlockBackendInitialization(AgentBackendId backendId)
            => _backendInitializationCompletions[backendId.Value] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteBackendInitialization(AgentBackendId backendId)
            => _backendInitializationCompletions[backendId.Value].TrySetResult(true);

        public void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
            => log.Add($"Shell.Status:{message}:{showSpinner}:{tone}");

        public void SetProviderSessionLoadStatus(string? message)
        {
            ProviderSessionLoadStatuses.Add(message);
        }

        public void ApplyRecoveredCatalogState(
            IReadOnlyList<ProjectDescriptor> projects,
            IReadOnlyList<SessionViewDescriptor> threads,
            bool pruneMissingThreads = true)
            => log.Add($"Shell.ApplyRecoveredCatalogState:{projects.Count}:{threads.Count}" + (pruneMissingThreads ? string.Empty : ":KeepMissing"));

        public void UpsertProject(ProjectDescriptor project)
            => log.Add($"Shell.UpsertProject:{project.Id}");

        public void SetReadyStatusForCurrentSelection()
            => log.Add("Shell.SetReadyStatus");

        public void HandleRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
        {
            LastRuntimeEvent = runtimeEvent;
            log.Add($"Shell.HandleRuntimeEvent:{runtimeEvent.ThreadId}");
        }

        public void PublishStartupCatalogProjectionReady()
            => log.Add("Shell.PublishStartupCatalogProjectionReady");

        public void TrySchedulePendingStartupThreadRestore(CancellationToken cancellationToken)
            => log.Add("Shell.TrySchedulePendingStartupThreadRestore");

        public void SelectGlobalScope()
            => log.Add("Shell.SelectGlobalScope");

        public void SelectProjectScope(string projectId)
            => log.Add($"Shell.SelectProjectScope:{projectId}");

        public void OpenThread(string threadId)
            => log.Add($"Shell.OpenThread:{threadId}");

        public void FocusPromptEditor()
            => log.Add("Shell.FocusPromptEditor");

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

        public Task<ProjectDescriptor> UpsertFromPathAsync(string projectPath, CancellationToken cancellationToken)
        {
            log.Add($"ProjectCatalog.UpsertFromPath:{projectPath}");
            var existing = Projects.FirstOrDefault(project =>
                string.Equals(project.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Archived = false;
                return Task.FromResult(existing);
            }

            var created = new ProjectDescriptor
            {
                Id = $"project-{Projects.Count + 1}",
                Slug = $"project-{Projects.Count + 1}",
                Name = Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                DisplayName = Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                ProjectPath = projectPath,
                DefaultBranch = "main",
            };
            Projects.Add(created);
            return Task.FromResult(created);
        }

        public Task SaveAsync(ProjectDescriptor project, CancellationToken cancellationToken)
        {
            log.Add($"ProjectCatalog.Save:{project.Id}");
            Projects.RemoveAll(existing => string.Equals(existing.Id, project.Id, StringComparison.OrdinalIgnoreCase));
            Projects.Add(project);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRecoverableSessionSource(
        List<string> log,
        IReadOnlyList<SessionViewDescriptor> threads) : IRecoverableSessionSource
    {
        public async IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            log.Add("ThreadSource.List");
            foreach (var thread in threads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return thread;
            }
        }
    }

    private sealed class BlockingRecoverableSessionSource(
        List<string> log,
        IReadOnlyList<SessionViewDescriptor> threads) : IRecoverableSessionSource
    {
        private readonly TaskCompletionSource<bool> _allowCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ListStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            log.Add("ThreadSource.List");
            ListStarted.TrySetResult(true);
            await _allowCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var thread in threads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return thread;
            }
        }

        public void AllowCompletion()
            => _allowCompletion.TrySetResult(true);
    }

    private sealed class FakeSessionDeleter(List<string> log) : ISessionDeleter
    {
        public List<string> DeletedThreadIds { get; } = [];

        public bool DeleteResult { get; set; }

        public Task<bool> DeleteSessionAsync(SessionViewDescriptor thread, CancellationToken cancellationToken)
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
