using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Events;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellThreadStateCoordinatorTests
{
    [TestMethod]
    public void ApplyRecoveredCatalogState_AppliesPersistedThreadLocalState()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        coordinator.ViewState = new WorkThreadViewState
        {
            ThreadStates = new Dictionary<string, WorkThreadLocalState>(StringComparer.OrdinalIgnoreCase)
            {
                ["thread-1"] = new WorkThreadLocalState
                {
                    Archived = true,
                    MessageCount = 12,
                },
            },
        };

        coordinator.ApplyRecoveredCatalogState([], [CreateThread("thread-1")]);

        var thread = coordinator.Threads.Single();
        Assert.AreEqual(WorkThreadStatus.Archived, thread.Status);
        Assert.AreEqual(12, thread.MessageCount);
    }

    [TestMethod]
    public async Task PersistThreadLocalStateAsync_StoresArchivedAndMessageCountInViewState()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var threadCatalog = new WorkThreadCatalog(options);
        var coordinator = CreateCoordinator(options, threadCatalog);
        coordinator.ViewState = new WorkThreadViewState();

        var thread = CreateThread("thread-1");
        thread.Status = WorkThreadStatus.Archived;
        thread.MessageCount = 6;
        await coordinator.PersistThreadLocalStateAsync(thread).ConfigureAwait(false);

        var reloaded = await threadCatalog.LoadViewStateAsync().ConfigureAwait(false);
        Assert.IsTrue(reloaded.ThreadStates["thread-1"].Archived);
        Assert.AreEqual(6, reloaded.ThreadStates["thread-1"].MessageCount);
    }

    [TestMethod]
    public async Task CloseThreadTabAsync_LastSelectedProjectThreadFallsBackToProjectScope()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], [CreateThread("thread-1", project.Id)]);
        coordinator.OpenThread("thread-1");

        var closeResult = await coordinator.CloseThreadTabAsync("thread-1").ConfigureAwait(false);

        Assert.AreEqual(TabCloseResult.Closed, closeResult);
        Assert.IsTrue(coordinator.DraftTabOpen);
        Assert.IsFalse(coordinator.GlobalScopeSelected);
        Assert.AreEqual(project.Id, coordinator.SelectedProjectId);
        Assert.IsNull(coordinator.SelectedThreadId);
        Assert.AreEqual(ShellSurface.DraftWorkspace, coordinator.Selection.Surface);
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(coordinator.Selection.Target);
    }

    [TestMethod]
    public async Task CloseThreadTabAsync_RetainsThreadStateForReopen()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var thread = CreateThread("thread-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [thread]);
        coordinator.OpenThread(thread.ThreadId);

        var tab = coordinator.FindOpenThread(thread.ThreadId);
        Assert.IsNotNull(tab);
        tab.Session.PromptDraftText = "keep this draft";

        var closeResult = await coordinator.CloseThreadTabAsync(thread.ThreadId).ConfigureAwait(false);

        Assert.AreEqual(TabCloseResult.Closed, closeResult);
        var retained = coordinator.FindOpenThread(thread.ThreadId);
        Assert.IsNotNull(retained);
        Assert.AreEqual("keep this draft", retained.Session.PromptDraftText);
        Assert.IsFalse(coordinator.ViewState.OpenThreadIds.Contains(thread.ThreadId, StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ClosingThreadTab_DoesNotStopThread()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var thread = CreateThread("thread-1", project.Id);
        thread.Status = WorkThreadStatus.Active;
        coordinator.ApplyRecoveredCatalogState([project], [thread]);
        coordinator.OpenThread(thread.ThreadId);

        var closeResult = await coordinator.CloseThreadTabAsync(thread.ThreadId).ConfigureAwait(false);

        Assert.AreEqual(TabCloseResult.Closed, closeResult);
        Assert.AreSame(thread, coordinator.FindThread(thread.ThreadId));
        Assert.AreEqual(WorkThreadStatus.Active, thread.Status);
        Assert.IsFalse(coordinator.ViewState.OpenThreadIds.Contains(thread.ThreadId, StringComparer.OrdinalIgnoreCase));

        coordinator.OpenThread(thread.ThreadId);

        Assert.IsTrue(coordinator.ViewState.OpenThreadIds.Contains(thread.ThreadId, StringComparer.OrdinalIgnoreCase));
        Assert.IsNotNull(coordinator.FindOpenThread(thread.ThreadId));
        Assert.AreEqual(thread.ThreadId, coordinator.SelectedThreadId);
    }

    [TestMethod]
    public void EnsureThreadTab_LoadsPersistedPromptDraft()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options, loadPromptDraft: static threadId => threadId == "thread-1" ? "saved prompt" : null);
        var project = CreateProject("project-1", "CodeAlta");
        var thread = CreateThread("thread-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [thread]);

        var tab = coordinator.EnsureThreadTab(thread);

        Assert.AreEqual("saved prompt", tab.Session.PromptDraftText);
    }

    [TestMethod]
    public void OpenThread_ReturnsTypedLifecycleResult()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var thread = CreateThread("thread-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [thread]);

        var opened = coordinator.OpenThread(thread.ThreadId);
        var alreadyOpen = coordinator.OpenThread(thread.ThreadId);
        var missing = coordinator.OpenThread("missing-thread");

        Assert.AreEqual(OpenThreadResult.Opened, opened);
        Assert.AreEqual(OpenThreadResult.AlreadyOpen, alreadyOpen);
        Assert.AreEqual(OpenThreadResult.NotFound, missing);
    }

    [TestMethod]
    public async Task CloseThreadTabAsync_ReturnsNotOpenForMissingOpenTab()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);

        var result = await coordinator.CloseThreadTabAsync("thread-1").ConfigureAwait(false);

        Assert.AreEqual(TabCloseResult.NotOpen, result);
    }

    [TestMethod]
    public void RemoveDeletedProject_SelectedProjectScopeFallsBackToGlobal()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], [CreateThread("thread-1", project.Id)]);
        coordinator.SelectProjectScope(project.Id);

        coordinator.RemoveDeletedProject(project.Id, ["thread-1"]);

        Assert.IsTrue(coordinator.DraftTabOpen);
        Assert.IsTrue(coordinator.GlobalScopeSelected);
        Assert.IsNull(coordinator.SelectedThreadId);
        Assert.AreEqual(ShellSurface.DraftWorkspace, coordinator.Selection.Surface);
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(coordinator.Selection.Target);
    }

    [TestMethod]
    public void SelectProjectScope_ReturnsTypedSelectionChangeResult()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], []);

        var changed = coordinator.SelectProjectScope(project.Id);
        var unchanged = coordinator.SelectProjectScope(project.Id);

        Assert.AreEqual(SelectionChangeResult.Changed, changed);
        Assert.AreEqual(SelectionChangeResult.Unchanged, unchanged);
    }

    [TestMethod]
    public void ApplyRecoveredCatalogState_PreservesPersistedDraftSelectionEvenWhenOpenThreadsExist()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ViewState = new WorkThreadViewState
        {
            OpenThreadIds = ["thread-1"],
            Selection = WorkThreadSelectionState.ProjectDraft(project.Id),
        };

        coordinator.ApplyRecoveredCatalogState([project], [CreateThread("thread-1", project.Id)]);

        Assert.AreEqual(ShellSurface.DraftWorkspace, coordinator.Selection.Surface);
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(coordinator.Selection.Target);
        Assert.IsFalse(coordinator.GlobalScopeSelected);
        Assert.AreEqual(project.Id, coordinator.SelectedProjectId);
        Assert.IsNull(coordinator.SelectedThreadId);
    }

    [TestMethod]
    public void ApplyRecoveredCatalogState_PartialRecoveryPreservesPendingStartupRestore()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var fastThread = CreateThread("thread-fast", project.Id);
        var slowThread = CreateThread("thread-slow", project.Id);
        coordinator.ViewState = new WorkThreadViewState
        {
            OpenThreadIds = [slowThread.ThreadId],
            SelectedThreadId = slowThread.ThreadId,
            Selection = WorkThreadSelectionState.Thread(slowThread.ThreadId, project.Id),
        };
        coordinator.PendingStartupThreadRestoreId = slowThread.ThreadId;

        coordinator.ApplyRecoveredCatalogState([project], [fastThread], pruneMissingThreads: false);

        Assert.AreEqual(slowThread.ThreadId, coordinator.PendingStartupThreadRestoreId);
        CollectionAssert.Contains(coordinator.ViewState.OpenThreadIds, slowThread.ThreadId);

        coordinator.ApplyRecoveredCatalogState([project], [fastThread, slowThread], pruneMissingThreads: false);

        Assert.AreEqual(slowThread.ThreadId, coordinator.SelectedThreadId);
        CollectionAssert.Contains(coordinator.ViewState.OpenThreadIds, slowThread.ThreadId);
    }

    [TestMethod]
    public async Task SaveNavigatorSettingsAsync_PersistsUpdatedSettings()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var threadCatalog = new WorkThreadCatalog(options);
        var coordinator = CreateCoordinator(options, threadCatalog);

        await coordinator.SaveNavigatorSettingsAsync(new NavigatorSettings
        {
            SortMode = NavigatorProjectSortMode.Date,
            RecentThreadsPerProject = 7,
        }).ConfigureAwait(false);

        var viewState = await threadCatalog.LoadViewStateAsync().ConfigureAwait(false);
        Assert.AreEqual(NavigatorProjectSortMode.Date, viewState.Navigator.SortMode);
        Assert.AreEqual(7, viewState.Navigator.RecentThreadsPerProject);
    }

    [TestMethod]
    public void CatalogSelectionAndNavigatorMutations_UpdateShellStateStore()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var stateStore = new ShellStateStore(new InlineUiDispatcher());
        var coordinator = CreateCoordinator(options, stateStore: stateStore);
        var project = CreateProject("project-1", "Project 1");
        var thread = CreateThread("thread-1");

        coordinator.ApplyRecoveredCatalogState([project], [thread]);
        coordinator.OpenThread("thread-1");
        coordinator.SaveNavigatorSettingsAsync(new NavigatorSettings
        {
            SortMode = NavigatorProjectSortMode.Date,
            RecentThreadsPerProject = 5,
        }).GetAwaiter().GetResult();

        var snapshot = stateStore.Snapshot;
        Assert.AreEqual(1, snapshot.Projects.Count);
        Assert.AreEqual(1, snapshot.Threads.Count);
        Assert.AreEqual("thread-1", snapshot.Selection.SelectedThreadId);
        CollectionAssert.AreEqual(new[] { "thread-1" }, snapshot.OpenThreadIds.ToArray());
        Assert.AreEqual(NavigatorProjectSortMode.Date, snapshot.NavigatorSettings.SortMode);
        Assert.AreEqual(5, snapshot.NavigatorSettings.RecentThreadsPerProject);
    }

    [TestMethod]
    public void CatalogAndSelectionMutations_PublishTypedFrontendEvents()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var publisher = new FrontendEventPublisher(dispatcher);
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = CreateCoordinator(
            options,
            stateStore: new ShellStateStore(dispatcher),
            frontendEvents: publisher);
        var project = CreateProject("project-1", "Project 1");
        var thread = CreateThread("thread-1");

        coordinator.ApplyRecoveredCatalogState([project], [thread]);
        coordinator.OpenThread(thread.ThreadId);

        Assert.IsTrue(events.OfType<CatalogChangedEvent>().Any());
        Assert.IsTrue(events.OfType<SelectionChangedEvent>().Any(selection => selection.Snapshot?.Selection.SelectedThreadId == thread.ThreadId));
    }

    [TestMethod]
    public async Task RemoveDeletedThreadArtifactsAsync_RemovesPersistedThreadStateAndPendingRestore()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var threadCatalog = new WorkThreadCatalog(options);
        var deletedPromptDrafts = new List<string>();
        var coordinator = CreateCoordinator(
            options,
            threadCatalog,
            deletePromptDraft: deletedPromptDrafts.Add);
        coordinator.ViewState = new WorkThreadViewState
        {
            ThreadStates = new Dictionary<string, WorkThreadLocalState>(StringComparer.OrdinalIgnoreCase)
            {
                ["thread-1"] = new WorkThreadLocalState
                {
                    Archived = true,
                    MessageCount = 4,
                },
                ["thread-2"] = new WorkThreadLocalState
                {
                    MessageCount = 1,
                },
            },
        };
        coordinator.PendingStartupThreadRestoreId = "thread-1";

        await coordinator.RemoveDeletedThreadArtifactsAsync(["thread-1"]).ConfigureAwait(false);

        Assert.IsNull(coordinator.PendingStartupThreadRestoreId);
        CollectionAssert.AreEqual(new[] { "thread-1" }, deletedPromptDrafts);
        Assert.IsFalse(coordinator.ViewState.ThreadStates.ContainsKey("thread-1"));
        Assert.IsTrue(coordinator.ViewState.ThreadStates.ContainsKey("thread-2"));

        var persistedViewState = await threadCatalog.LoadViewStateAsync().ConfigureAwait(false);
        Assert.IsFalse(persistedViewState.ThreadStates.ContainsKey("thread-1"));
        Assert.IsTrue(persistedViewState.ThreadStates.ContainsKey("thread-2"));
    }

    [TestMethod]
    public void RekeyThreadIdentity_MovesOpenThreadSelectionAndPreferences()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var thread = CreateThread("openai:session-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [thread]);
        coordinator.ViewState.OpenThreadIds.Add(thread.ThreadId);
        coordinator.ViewState.SelectedThreadId = thread.ThreadId;
        coordinator.ViewState.Selection = WorkThreadSelectionState.Thread(thread.ThreadId, project.Id);
        coordinator.ViewState.ThreadStates[thread.ThreadId] = new WorkThreadLocalState { MessageCount = 9 };
        coordinator.ViewState.ThreadPreferences[thread.ThreadId] = new WorkThreadPreference
        {
            ModelId = "gpt-4.1",
            ReasoningEffort = AgentReasoningEffort.High,
        };
        coordinator.PendingStartupThreadRestoreId = thread.ThreadId;
        coordinator.OpenThread(thread.ThreadId);

        var tab = coordinator.FindOpenThread(thread.ThreadId);
        Assert.IsNotNull(tab);
        tab.Session.PromptDraftText = "preserve me";

        thread.ThreadId = "anthropic:session-1";
        thread.BackendId = "anthropic";
        thread.ProviderKey = "anthropic";
        coordinator.RekeyThreadIdentity("openai:session-1", thread);

        Assert.AreEqual("anthropic:session-1", coordinator.SelectedThreadId);
        Assert.AreEqual("anthropic:session-1", coordinator.ViewState.SelectedThreadId);
        Assert.AreEqual("anthropic:session-1", coordinator.ViewState.Selection.ThreadId);
        CollectionAssert.AreEqual(new[] { "anthropic:session-1" }, coordinator.ViewState.OpenThreadIds);
        Assert.IsFalse(coordinator.ViewState.ThreadStates.ContainsKey("openai:session-1"));
        Assert.IsTrue(coordinator.ViewState.ThreadStates.ContainsKey("anthropic:session-1"));
        Assert.IsFalse(coordinator.ViewState.ThreadPreferences.ContainsKey("openai:session-1"));
        Assert.IsTrue(coordinator.ViewState.ThreadPreferences.ContainsKey("anthropic:session-1"));
        Assert.AreEqual("anthropic:session-1", coordinator.PendingStartupThreadRestoreId);

        var reboundTab = coordinator.FindOpenThread("anthropic:session-1");
        Assert.IsNotNull(reboundTab);
        Assert.AreEqual("preserve me", reboundTab.Session.PromptDraftText);
        Assert.IsNull(coordinator.FindOpenThread("openai:session-1"));
    }

    private static ShellThreadStateCoordinator CreateCoordinator(
        CatalogOptions options,
        WorkThreadCatalog? threadCatalog = null,
        Func<string, string?>? loadPromptDraft = null,
        Action<string>? deletePromptDraft = null,
        ShellStateStore? stateStore = null,
        FrontendEventPublisher? frontendEvents = null)
    {
        threadCatalog ??= new WorkThreadCatalog(options);
        return new ShellThreadStateCoordinator(
            new ProjectCatalog(options),
            threadCatalog,
            new InlineUiDispatcher(),
            stateStore ?? new ShellStateStore(new InlineUiDispatcher()),
            new TestThreadStateFrontendPort(
                loadPromptDraft: loadPromptDraft,
                deletePromptDraft: deletePromptDraft),
            frontendEvents);
    }

    private static WorkThreadDescriptor CreateThread(string threadId)
        => CreateThread(threadId, "project-1");

    private static WorkThreadDescriptor CreateThread(string threadId, string projectId)
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00");
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
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
        };
    }

    private static ProjectDescriptor CreateProject(string id, string displayName)
    {
        return new ProjectDescriptor
        {
            Id = id,
            Slug = displayName.ToLowerInvariant(),
            Name = displayName,
            DisplayName = displayName,
            ProjectPath = $@"C:\repo\{displayName}",
            DefaultBranch = "main",
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

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
