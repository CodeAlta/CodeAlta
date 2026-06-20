using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
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
        var sessions = new FakeRecoverableSessionSource(log, [CreateSession("session-1")]);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(shell, importer, projectCatalog, sessions, new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(dispatcher);

        await controller.ReloadCatalogAsync(CancellationToken.None);

        CollectionAssert.Contains(log, "Shell.Status:Refreshing project and session catalog...:True:Info");
        CollectionAssert.Contains(log, "Importer.Import");
        CollectionAssert.Contains(log, "ProjectCatalog.Load");
        CollectionAssert.Contains(log, "SessionSource.List");
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
                "Shell.Status:Refreshing project and session catalog...:True:Info",
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
            new FakeRecoverableSessionSource(log, [CreateSession("session-1")]),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        await controller.InitializeAsync(CancellationToken.None);

        CollectionAssert.Contains(log, "Shell.InitializeModelProviders");
        CollectionAssert.Contains(log, "Shell.PublishStartupCatalogProjectionReady");
        CollectionAssert.Contains(log, "Shell.SetReadyStatus");
        CollectionAssert.Contains(log, "Shell.SetInitialized:True");
        CollectionAssert.Contains(log, "Importer.Import");
        CollectionAssert.Contains(log, "ProjectCatalog.Load");
        CollectionAssert.Contains(log, "SessionSource.List");
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:1");
        CollectionAssert.Contains(log, "Shell.TrySchedulePendingStartupSessionRestore");
    }

    [TestMethod]
    public async Task InitializeAsync_RethrowsLockedSessionCacheFailure()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log) { ImportException = new AgentSessionCacheLockedException("locked") },
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableSessionSource(log, []),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        await Assert.ThrowsExactlyAsync<AgentSessionCacheLockedException>(() => controller.InitializeAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task InitializeAsync_ShowsSessionsBeforeProviderInitializationCompletes()
    {
        var log = new List<string>();
        var shell = new FakeShell(log)
        {
            InitializeModelProvidersCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, [new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" }]),
            new FakeRecoverableSessionSource(log, [CreateSession("session-1")]),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var initializationTask = controller.InitializeAsync(CancellationToken.None);

        await WaitUntilAsync(() => log.Contains("Shell.ApplyRecoveredCatalogState:1:1")).ConfigureAwait(false);
        await WaitUntilAsync(() => log.Contains("Importer.Import")).ConfigureAwait(false);

        Assert.IsFalse(initializationTask.IsCompleted, "Provider initialization should still be running.");
        CollectionAssert.Contains(log, "Shell.InitializeModelProviders");
        CollectionAssert.Contains(log, "Importer.Import");
        CollectionAssert.Contains(log, "SessionSource.List");
        CollectionAssert.Contains(log, "Shell.TrySchedulePendingStartupSessionRestore");

        shell.InitializeModelProvidersCompletion.SetResult(true);
        await initializationTask.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InitializeAsync_ShowsSessionsBeforeKnownProjectImportCompletes()
    {
        var log = new List<string>();
        var importer = new FakeImporter(log)
        {
            ImportCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var controller = new CodeAltaShellController(
            new FakeShell(log),
            importer,
            new FakeProjectCatalogStore(log, [new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" }]),
            new FakeRecoverableSessionSource(log, [CreateSession("session-1")]),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var initializationTask = controller.InitializeAsync(CancellationToken.None);

        await WaitUntilAsync(() => log.Contains("Shell.ApplyRecoveredCatalogState:1:1")).ConfigureAwait(false);
        await WaitUntilAsync(() => log.Contains("Importer.Import")).ConfigureAwait(false);

        Assert.IsFalse(initializationTask.IsCompleted, "Startup should not wait for project import before applying recovered sessions.");
        Assert.IsTrue(
            log.IndexOf("Shell.ApplyRecoveredCatalogState:1:1") < log.IndexOf("Importer.Import"),
            "Recovered sessions should be applied before known-project import begins.");

        importer.ImportCompletion.SetResult(false);
        await initializationTask.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InitializeAsync_StartsProviderInitializationBeforeSlowSessionScanCompletes()
    {
        var log = new List<string>();
        var shell = new FakeShell(log)
        {
            InitializeModelProvidersCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var sessionSource = new BlockingRecoverableSessionSource(
            log,
            [
                CreateSession("session-1"),
            ]);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, [project]),
            sessionSource,
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var initializationTask = controller.InitializeAsync(CancellationToken.None);

        await WaitUntilAsync(() => sessionSource.ListStarted.Task.IsCompleted).ConfigureAwait(false);

        CollectionAssert.Contains(log, "Shell.InitializeModelProviders");
        Assert.IsFalse(log.Any(static entry => entry.StartsWith("Shell.ApplyRecoveredCatalogState:", StringComparison.Ordinal)));

        sessionSource.AllowCompletion();
        shell.InitializeModelProvidersCompletion.SetResult(true);
        await initializationTask.ConfigureAwait(false);

        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:1");
    }

    [TestMethod]
    public async Task DisposeAsync_CancelsStartupInitializationWaitingForUiDispatcher()
    {
        var log = new List<string>();
        var controller = new CodeAltaShellController(
            new FakeShell(log),
            new FakeImporter(log),
            new FakeProjectCatalogStore(
                log,
                [
                    new ProjectDescriptor
                    {
                        Id = "project-1",
                        DisplayName = "CodeAlta",
                        ProjectPath = @"C:\repo",
                        Slug = "codealta",
                    },
                ]),
            new FakeRecoverableSessionSource(log, [CreateSession("session-1")]),
            new FakeSessionDeleter(log));
        var dispatcher = new BlockingUiDispatcher();
        controller.AttachUiDispatcher(dispatcher);

        controller.StartInitialization(CancellationToken.None);
        await dispatcher.WaitForInvokeAsync().ConfigureAwait(false);

        await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        Assert.IsTrue(dispatcher.InvokeCallCount > 0);
    }

    [TestMethod]
    public async Task InitializeAsync_AppliesRecoverableSessionsProgressivelyFromOneStream()
    {
        var log = new List<string>();
        var codexProviderId = new ModelProviderId("codex");
        var slowProviderId = new ModelProviderId("slow");
        var shell = new FakeShell(log);
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var sessionSource = new FakeRecoverableSessionSource(
            log,
            [
                CreateSession("session-codex", ProviderId: codexProviderId.Value),
                CreateSession("session-slow", ProviderId: slowProviderId.Value),
            ]);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, [project]),
            sessionSource,
            new FakeSessionDeleter(log),
            [
                new ModelProviderDescriptor(codexProviderId, "Codex"),
                new ModelProviderDescriptor(slowProviderId, "Slow"),
            ]);
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        await controller.InitializeAsync(CancellationToken.None);

        CollectionAssert.Contains(log, "SessionSource.List");
        Assert.AreEqual(1, log.Count(static entry => entry == "SessionSource.List"));
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:1:KeepMissing");
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:2:KeepMissing");
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:2");
    }

    [TestMethod]
    public async Task InitializeAsync_LoadsManySessionsAcrossProvidersThroughSingleRecoverableSource()
    {
        var log = new List<string>();
        var codexProviderId = new ModelProviderId("codex");
        var slowProviderId = new ModelProviderId("slow");
        var shell = new FakeShell(log);
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var parent = CreateSession("session-parent", ProviderId: codexProviderId.Value);
        var providerIds = new[]
        {
            codexProviderId,
            slowProviderId,
            new ModelProviderId("anthropic"),
            new ModelProviderId("openai"),
        };
        var children = Enumerable.Range(1, 64)
            .Select(index =>
            {
                var providerId = providerIds[index % providerIds.Length];
                var child = CreateSession($"session-child-{index}", ProviderId: providerId.Value);
                child.ParentSessionId = parent.SessionId;
                child.LastActiveAt = parent.LastActiveAt.AddMinutes(index);
                return child;
            })
            .ToArray();
        var completeSessions = new[] { parent }.Concat(children).ToArray();
        var sessionSource = new FakeRecoverableSessionSource(log, completeSessions);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, [project]),
            sessionSource,
            new FakeSessionDeleter(log),
            providerIds.Select(providerId => new ModelProviderDescriptor(providerId, providerId.Value)).ToArray());
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        await controller.InitializeAsync(CancellationToken.None);

        CollectionAssert.Contains(log, "SessionSource.List");
        Assert.AreEqual(1, log.Count(static entry => entry == "SessionSource.List"));
        Assert.AreEqual(1, log.Count(static entry => entry == "Shell.InitializeModelProviders"));
        var finalCatalogApplication = log.Last(entry => entry.StartsWith("Shell.ApplyRecoveredCatalogState:", StringComparison.Ordinal));
        Assert.AreEqual("Shell.ApplyRecoveredCatalogState:1:65", finalCatalogApplication);
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
        var runtimeEvent = CreateHostEvent("session-1");

        await controller.ApplyRuntimeEventAsync(runtimeEvent, CancellationToken.None);

        Assert.AreSame(runtimeEvent, shell.LastRuntimeEvent);
        Assert.AreEqual(1, dispatcher.InvokeCallCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "Shell.HandleRuntimeEvent:session-1",
            },
            log);
    }

    [TestMethod]
    public void QueueRuntimeEvent_PostsWakeAndDrainsOnUiFrame()
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

        controller.QueueRuntimeEvent(CreateHostEvent("session-1"), CancellationToken.None);
        controller.QueueRuntimeEvent(CreateHostEvent("session-2"), CancellationToken.None);
        Assert.AreEqual(0, dispatcher.InvokeCallCount);
        Assert.AreEqual(1, dispatcher.PostCallCount);
        Assert.AreEqual(0, log.Count);

        var drained = controller.DrainPendingRuntimeEventsForUiFrame();

        Assert.AreEqual(2, drained);
        CollectionAssert.AreEqual(
            new[]
            {
                "Shell.HandleRuntimeEvent:session-1",
                "Shell.HandleRuntimeEvent:session-2",
            },
            log);
    }

    [TestMethod]
    public void DrainPendingRuntimeEventsForUiFrame_LimitsBatchAndSchedulesRemaining()
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

        controller.QueueRuntimeEvent(CreateHostEvent("session-1"), CancellationToken.None);
        controller.QueueRuntimeEvent(CreateHostEvent("session-2"), CancellationToken.None);

        Assert.AreEqual(1, dispatcher.PostCallCount);
        var firstDrain = controller.DrainPendingRuntimeEventsForUiFrame(maxEvents: 1);

        Assert.AreEqual(1, firstDrain);
        Assert.AreEqual(2, dispatcher.PostCallCount);
        CollectionAssert.AreEqual(new[] { "Shell.HandleRuntimeEvent:session-1" }, log);

        var secondDrain = controller.DrainPendingRuntimeEventsForUiFrame(maxEvents: 1);

        Assert.AreEqual(1, secondDrain);
        Assert.AreEqual(2, dispatcher.PostCallCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "Shell.HandleRuntimeEvent:session-1",
                "Shell.HandleRuntimeEvent:session-2",
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

        controller.QueueRuntimeEvent(CreateToolDeltaEvent("session-1", "tool-1", "alpha"), CancellationToken.None);
        controller.QueueRuntimeEvent(CreateToolDeltaEvent("session-1", "tool-1", "beta"), CancellationToken.None);

        var drained = controller.DrainPendingRuntimeEvents();

        Assert.AreEqual(2, drained);
        Assert.AreEqual(1, log.Count);
        var runtimeEvent = Assert.IsInstanceOfType<SessionAgentEvent>(shell.LastRuntimeEvent);
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
    public async Task OpenSessionAsync_RoutesSessionSelectionThroughDispatcher()
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

        await controller.OpenSessionAsync("session-1", CancellationToken.None);

        Assert.AreEqual(1, dispatcher.InvokeCallCount);
        CollectionAssert.AreEqual(new[] { "Shell.OpenSession:session-1", "Shell.FocusPromptEditor" }, log);
    }

    [TestMethod]
    public async Task OpenFolderAsync_UpsertsProjectAndSelectsProjectWithoutReloadingSessions()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var catalog = new FakeProjectCatalogStore(log, []);
        var dispatcher = new FakeUiDispatcher();
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            catalog,
            new FakeRecoverableSessionSource(log, [CreateSession("session-1")]),
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
            new FakeRecoverableSessionSource(log, [CreateSession("session-1")]),
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
            new FakeRecoverableSessionSource(log, [CreateSession("session-1")]),
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
            new FakeRecoverableSessionSource(log, [CreateSession("session-1")]),
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
    public async Task LoadProjectSessionsAsync_ReturnsProjectSessionsOrderedByRecency()
    {
        var log = new List<string>();
        var sessions = new[]
        {
            CreateSession("session-1"),
            CreateSession("session-2"),
            CreateSession("session-3", projectId: "project-2"),
        };
        sessions[0].LastActiveAt = DateTimeOffset.Parse("2026-03-29T10:00:00+00:00");
        sessions[1].LastActiveAt = DateTimeOffset.Parse("2026-03-29T11:00:00+00:00");
        var controller = new CodeAltaShellController(
            new FakeShell(log),
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableSessionSource(log, sessions),
            new FakeSessionDeleter(log));

        var projectSessions = await controller.LoadProjectSessionsAsync("project-1", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "session-2", "session-1" }, projectSessions.Select(static session => session.SessionId).ToArray());
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
            new FakeRecoverableSessionSource(log, [CreateSession("session-1")]),
            new FakeSessionDeleter(log));
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        project.DisplayName = "CodeAlta UI";
        await controller.SaveProjectAsync(project, CancellationToken.None);

        Assert.AreEqual("CodeAlta UI", catalog.Projects.Single().DisplayName);
        CollectionAssert.Contains(log, "ProjectCatalog.Save:project-1");
        CollectionAssert.Contains(log, "Shell.ApplyRecoveredCatalogState:1:1");
    }

    [TestMethod]
    public async Task DeleteSessionAsync_DeletesSessionWithoutReloadingCatalog()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var session = CreateSession("session-1");
        var deleter = new FakeSessionDeleter(log) { DeleteResult = true };
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            new FakeProjectCatalogStore(log, []),
            new FakeRecoverableSessionSource(log, [session]),
            deleter);
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var result = await controller.DeleteSessionAsync(session, [session], CancellationToken.None);

        Assert.IsTrue(result.DeletedFromSessionStore);
        CollectionAssert.AreEqual(new[] { session.SessionId }, result.DeletedSessionIds.ToArray());
        Assert.AreEqual(session.SessionId, deleter.DeletedSessionIds.Single());
        Assert.IsFalse(log.Contains("Importer.Import"));
        Assert.IsFalse(log.Contains("ProjectCatalog.Load"));
        Assert.IsFalse(log.Contains("SessionSource.List"));
        Assert.IsFalse(log.Contains("SessionSource.List"));
    }

    [TestMethod]
    public async Task DeleteSessionAsync_DeletesChildSessionsBeforeParent()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var parent = CreateSession("session-parent");
        var child = CreateSession("session-child");
        var grandchild = CreateSession("session-grandchild");
        var sibling = CreateSession("session-sibling");
        child.ParentSessionId = parent.SessionId;
        grandchild.ParentSessionId = child.SessionId;
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
            new[] { grandchild.SessionId, child.SessionId, parent.SessionId },
            result.DeletedSessionIds.ToArray());
        CollectionAssert.AreEqual(result.DeletedSessionIds.ToArray(), deleter.DeletedSessionIds.ToArray());
        Assert.IsFalse(deleter.DeletedSessionIds.Contains(sibling.SessionId));
    }

    [TestMethod]
    public async Task DeleteProjectAsync_DeletesSessionsAndProjectWithoutReloadingCatalog()
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
            new FakeRecoverableSessionSource(log, [CreateSession("session-1"), CreateSession("session-2")]),
            deleter);
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var sessions = new[] { CreateSession("session-1"), CreateSession("session-2") };
        var result = await controller.DeleteProjectAsync(project, sessions, CancellationToken.None);

        Assert.IsFalse(catalog.Projects.Any(project => string.Equals(project.Id, "project-1", StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.AreEquivalent(new[] { "session-1", "session-2" }, deleter.DeletedSessionIds.ToArray());
        CollectionAssert.AreEquivalent(new[] { "session-1", "session-2" }, result.DeletedSessionIds.ToArray());
        CollectionAssert.Contains(log, "ProjectCatalog.Delete:project-1");
        Assert.IsFalse(log.Contains("Importer.Import"));
        Assert.IsFalse(log.Contains("ProjectCatalog.Load"));
        Assert.IsFalse(log.Contains("SessionSource.List"));
    }

    [TestMethod]
    public async Task DeleteProjectAsync_ProjectIdDeletesAllRecoverableProjectSessions()
    {
        var log = new List<string>();
        var shell = new FakeShell(log);
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var otherProject = new ProjectDescriptor { Id = "project-2", DisplayName = "Other", ProjectPath = @"C:\other", Slug = "other" };
        var archivedSession = CreateSession("session-archived");
        archivedSession.Status = SessionViewStatus.Archived;
        var otherSession = CreateSession("session-other", otherProject.Id);
        var catalog = new FakeProjectCatalogStore(log, [project, otherProject]);
        var deleter = new FakeSessionDeleter(log);
        var controller = new CodeAltaShellController(
            shell,
            new FakeImporter(log),
            catalog,
            new FakeRecoverableSessionSource(log, [CreateSession("session-visible"), archivedSession, otherSession]),
            deleter);
        controller.AttachUiDispatcher(new FakeUiDispatcher());

        var result = await controller.DeleteProjectAsync(project.Id, CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { "session-visible", "session-archived" }, deleter.DeletedSessionIds.ToArray());
        CollectionAssert.AreEquivalent(new[] { "session-visible", "session-archived" }, result.DeletedSessionIds.ToArray());
        Assert.IsTrue(catalog.Projects.Any(existing => string.Equals(existing.Id, otherProject.Id, StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(catalog.Projects.Any(existing => string.Equals(existing.Id, project.Id, StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.Contains(log, "ProjectCatalog.GetById:project-1");
        CollectionAssert.Contains(log, "SessionSource.List");
        CollectionAssert.Contains(log, "ProjectCatalog.Delete:project-1");
        Assert.IsFalse(deleter.DeletedSessionIds.Contains(otherSession.SessionId));
    }

    private static SessionHostEvent CreateHostEvent(string sessionId)
        => new(sessionId, DateTimeOffset.UtcNow, AgentSessionUpdateKind.Info, "Updated");

    private static SessionAgentEvent CreateToolDeltaEvent(string sessionId, string contentId, string delta)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new SessionAgentEvent(
            sessionId,
            new AgentContentDeltaEvent(
                ModelProviderIds.Codex,
                "session-1",
                timestamp,
                null,
                AgentContentKind.ToolOutput,
                contentId,
                "activity-1",
                delta));
    }

    private static SessionViewDescriptor CreateSession(
        string sessionId,
        string projectId = "project-1",
        string? ProviderId = null)
    {
        return new SessionViewDescriptor
        {
            SessionId = sessionId,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ProviderId ?? ModelProviderIds.Codex.Value,
            ProjectRef = projectId,
            WorkingDirectory = @"C:\repo",
            Title = "Test session",
            Status = SessionViewStatus.Active,
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
        private readonly Dictionary<string, TaskCompletionSource<bool>> _providerInitializationCompletions = new(StringComparer.OrdinalIgnoreCase);

        public SessionRuntimeEvent? LastRuntimeEvent { get; private set; }

        public List<string?> ProviderSessionLoadStatuses { get; } = [];

        public TaskCompletionSource<bool>? InitializeModelProvidersCompletion { get; init; }

        public Task InitializeModelProvidersAsync(CancellationToken cancellationToken)
        {
            log.Add("Shell.InitializeModelProviders");
            return InitializeModelProvidersCompletion?.Task ?? Task.CompletedTask;
        }

        public Task InitializeModelProviderAsync(ModelProviderId providerId, CancellationToken cancellationToken)
        {
            log.Add($"Shell.InitializeModelProvider:{providerId.Value}");
            return _providerInitializationCompletions.TryGetValue(providerId.Value, out var completion)
                ? completion.Task.WaitAsync(cancellationToken)
                : Task.CompletedTask;
        }

        public void BlockProviderInitialization(ModelProviderId ProviderId)
            => _providerInitializationCompletions[ProviderId.Value] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteProviderInitialization(ModelProviderId ProviderId)
            => _providerInitializationCompletions[ProviderId.Value].TrySetResult(true);

        public void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
            => log.Add($"Shell.Status:{message}:{showSpinner}:{tone}");

        public void SetProviderSessionLoadStatus(string? message)
        {
            ProviderSessionLoadStatuses.Add(message);
        }

        public void ApplyRecoveredCatalogState(
            IReadOnlyList<ProjectDescriptor> projects,
            IReadOnlyList<SessionViewDescriptor> sessions,
            bool pruneMissingSessions = true)
            => log.Add($"Shell.ApplyRecoveredCatalogState:{projects.Count}:{sessions.Count}" + (pruneMissingSessions ? string.Empty : ":KeepMissing"));

        public void UpsertProject(ProjectDescriptor project)
            => log.Add($"Shell.UpsertProject:{project.Id}");

        public void SetReadyStatusForCurrentSelection()
            => log.Add("Shell.SetReadyStatus");

        public void HandleRuntimeEvent(SessionRuntimeEvent runtimeEvent)
        {
            LastRuntimeEvent = runtimeEvent;
            log.Add($"Shell.HandleRuntimeEvent:{runtimeEvent.SessionId}");
        }

        public void PublishStartupCatalogProjectionReady()
            => log.Add("Shell.PublishStartupCatalogProjectionReady");

        public void TrySchedulePendingStartupSessionRestore(CancellationToken cancellationToken)
            => log.Add("Shell.TrySchedulePendingStartupSessionRestore");

        public void SelectGlobalScope()
            => log.Add("Shell.SelectGlobalScope");

        public void SelectProjectScope(string projectId)
            => log.Add($"Shell.SelectProjectScope:{projectId}");

        public void OpenSession(string sessionId)
            => log.Add($"Shell.OpenSession:{sessionId}");

        public void FocusPromptEditor()
            => log.Add("Shell.FocusPromptEditor");

        public void SetInitialized(bool isInitialized)
            => log.Add($"Shell.SetInitialized:{isInitialized}");
    }

    private sealed class FakeImporter(List<string> log) : IKnownProjectImporter
    {
        public Exception? ImportException { get; set; }

        public TaskCompletionSource<bool>? ImportCompletion { get; set; }

        public bool Changed { get; set; }

        public async Task<bool> ImportAsync(CancellationToken cancellationToken)
        {
            log.Add("Importer.Import");
            if (ImportException is not null)
            {
                throw ImportException;
            }

            if (ImportCompletion is not null)
            {
                await ImportCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return Changed;
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

        public Task<bool> DeleteAsync(ProjectDescriptor project, CancellationToken cancellationToken)
        {
            log.Add($"ProjectCatalog.Delete:{project.Id}");
            var removed = Projects.RemoveAll(existing => string.Equals(existing.Id, project.Id, StringComparison.OrdinalIgnoreCase)) > 0;
            return Task.FromResult(removed);
        }
    }

    private sealed class FakeRecoverableSessionSource(
        List<string> log,
        IReadOnlyList<SessionViewDescriptor> sessions) : IRecoverableSessionSource
    {
        public async IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            log.Add("SessionSource.List");
            foreach (var session in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return session;
            }
        }

        public Task<bool> ReconcileRecoverableSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class BlockingRecoverableSessionSource(
        List<string> log,
        IReadOnlyList<SessionViewDescriptor> sessions) : IRecoverableSessionSource
    {
        private readonly TaskCompletionSource<bool> _allowCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ListStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            log.Add("SessionSource.List");
            ListStarted.TrySetResult(true);
            await _allowCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var session in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return session;
            }
        }

        public void AllowCompletion()
            => _allowCompletion.TrySetResult(true);

        public Task<bool> ReconcileRecoverableSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class FakeSessionDeleter(List<string> log) : ISessionDeleter
    {
        public List<string> DeletedSessionIds { get; } = [];

        public bool DeleteResult { get; set; }

        public Task<bool> DeleteSessionAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
        {
            log.Add($"SessionDeleter.Delete:{session.SessionId}");
            DeletedSessionIds.Add(session.SessionId);
            session.Status = SessionViewStatus.Archived;
            return Task.FromResult(DeleteResult);
        }
    }

    private sealed class FakeUiDispatcher : IUiDispatcher
    {
        public int InvokeCallCount { get; private set; }

        public int PostCallCount { get; private set; }

        public bool CheckAccess()
            => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            PostCallCount++;
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

    private sealed class BlockingUiDispatcher : IUiDispatcher
    {
        private readonly TaskCompletionSource<bool> _invokeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvokeCallCount { get; private set; }

        public bool CheckAccess()
            => false;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvokeCallCount++;
            _invokeStarted.TrySetResult(true);
            return _blocked.Task;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvokeCallCount++;
            _invokeStarted.TrySetResult(true);
            return _blocked.Task.ContinueWith(
                static (_, state) => ((Func<T>)state!)(),
                action,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public Task WaitForInvokeAsync()
            => _invokeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
