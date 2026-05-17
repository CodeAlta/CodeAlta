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
                    ProviderKey = "zai",
                    ModelId = "glm-5.1",
                    ReasoningEffort = AgentReasoningEffort.High,
                    Archived = true,
                    MessageCount = 12,
                },
            },
        };

        coordinator.ApplyRecoveredCatalogState([], [CreateThread("thread-1")]);

        var thread = coordinator.Threads.Single();
        Assert.AreEqual("zai", thread.BackendId);
        Assert.AreEqual("zai", thread.ProviderKey);
        Assert.AreEqual("glm-5.1", thread.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, thread.ReasoningEffort);
        Assert.AreEqual(WorkThreadStatus.Archived, thread.Status);
        Assert.AreEqual(12, thread.MessageCount);
    }

    [TestMethod]
    public async Task PersistThreadLocalStateAsync_AppendsArchivedAndMessageCountToJournalState()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var threadCatalog = new WorkThreadCatalog(options);
        var coordinator = CreateCoordinator(options, threadCatalog);
        coordinator.ViewState = new WorkThreadViewState();

        var thread = CreateThread("thread-1");
        thread.ProviderKey = "zai";
        thread.BackendId = "zai";
        thread.ModelId = "glm-5.1";
        thread.ReasoningEffort = AgentReasoningEffort.High;
        thread.Status = WorkThreadStatus.Archived;
        thread.MessageCount = 6;
        await coordinator.PersistThreadLocalStateAsync(thread).ConfigureAwait(false);

        var reloaded = await threadCatalog.JournalStore.ReadLatestStateAsync(thread.ThreadId, thread.CreatedAt).ConfigureAwait(false);
        Assert.IsNotNull(reloaded);
        Assert.AreEqual("zai", reloaded.ProviderKey);
        Assert.AreEqual("glm-5.1", reloaded.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, reloaded.ReasoningEffort);
        Assert.IsTrue(reloaded.Archived);
        Assert.AreEqual(6, reloaded.MessageCount);
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
    public async Task RegisterCreatedThreadAsync_ReplacesDraftTabWithCreatedThread()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var replacedDraftThreadIds = new List<string>();
        var coordinator = CreateCoordinator(options, replaceDraftTabWithThread: replacedDraftThreadIds.Add);
        var project = CreateProject("project-1", "CodeAlta");
        var thread = CreateThread("thread-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], []);

        await coordinator.RegisterCreatedThreadAsync(thread);

        CollectionAssert.AreEqual(new[] { "thread-1" }, replacedDraftThreadIds.ToArray());
        Assert.AreEqual("thread-1", coordinator.SelectedThreadId);
        CollectionAssert.Contains(coordinator.ViewState.OpenThreadIds, "thread-1");
    }

    [TestMethod]
    public void UpsertRuntimeThread_AddsAgentCreatedProjectThreadWithoutOpeningIt()
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
        var project = CreateProject("project-1", "CodeAlta");
        var thread = CreateThread("agent-child", project.Id);
        thread.CreatedBy = new AltaActorProvenance
        {
            Kind = "agent",
            SourceThreadId = "global-parent",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        coordinator.ApplyRecoveredCatalogState([project], []);

        coordinator.UpsertRuntimeThread(thread);

        Assert.AreEqual(thread.ThreadId, coordinator.Threads.Single().ThreadId);
        Assert.IsFalse(coordinator.ViewState.OpenThreadIds.Contains(thread.ThreadId, StringComparer.OrdinalIgnoreCase));
        Assert.IsNull(coordinator.FindOpenThread(thread.ThreadId));
        Assert.IsTrue(events.OfType<CatalogChangedEvent>().Any());
    }

    [TestMethod]
    public void UpsertRuntimeThread_RemembersParentThreadIdForRecovery()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var parent = CreateThread("thread-parent", project.Id);
        var child = CreateThread("thread-child", project.Id);
        child.ParentThreadId = parent.ThreadId;
        coordinator.ApplyRecoveredCatalogState([project], [parent]);

        coordinator.UpsertRuntimeThread(child);

        Assert.AreEqual(parent.ThreadId, coordinator.ViewState.ThreadStates[child.ThreadId].ParentThreadId);

        var recoveredChild = CreateThread(child.ThreadId, project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [parent, recoveredChild]);

        Assert.AreEqual(parent.ThreadId, coordinator.Threads.Single(thread => thread.ThreadId == child.ThreadId).ParentThreadId);
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
    public async Task CloseThreadTabAsync_DetachesThreadTabWithExplicitCloseReason()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var closeReasons = new List<(string ThreadId, ShellTabCloseReason Reason)>();
        var coordinator = CreateCoordinator(options, removeThreadTabPage: (threadId, reason) => closeReasons.Add((threadId, reason)));
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], [CreateThread("thread-1", project.Id)]);
        coordinator.OpenThread("thread-1");

        await coordinator.CloseThreadTabAsync("thread-1").ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[] { ("thread-1", ShellTabCloseReason.UserDetached) },
            closeReasons.ToArray());
    }

    [TestMethod]
    public async Task CloseThreadTabAsync_SelectsFallbackThreadBeforeRemovingSelectedTabPage()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        string? selectedThreadDuringRemoval = null;
        ShellThreadStateCoordinator? coordinator = null;
        coordinator = CreateCoordinator(
            options,
            getOpenThreadTabIds: static () => ["thread-1", "thread-2"],
            removeThreadTabPage: (_, _) => selectedThreadDuringRemoval = coordinator!.SelectedThreadId);
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState(
            [project],
            [CreateThread("thread-1", project.Id), CreateThread("thread-2", project.Id)]);
        coordinator.OpenThread("thread-1");
        coordinator.OpenThread("thread-2");

        await coordinator.CloseThreadTabAsync("thread-2").ConfigureAwait(false);

        Assert.AreEqual("thread-1", selectedThreadDuringRemoval);
        Assert.AreEqual("thread-1", coordinator.SelectedThreadId);
    }

    [TestMethod]
    public async Task CloseThreadTabAsync_DoesNotMaterializePersistedThreadWhenClosingSelectedTab()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var loadedThreadIds = new List<string>();
        var project = CreateProject("project-1", "CodeAlta");
        var firstThread = CreateThread("thread-1", project.Id);
        var selectedThread = CreateThread("thread-2", project.Id);
        var coordinator = CreateCoordinator(
            options,
            getOpenThreadTabIds: () => [selectedThread.ThreadId],
            ensureThreadHistoryLoadedAsync: (thread, _) =>
            {
                loadedThreadIds.Add(thread.ThreadId);
                return Task.CompletedTask;
            });
        coordinator.ViewState = new WorkThreadViewState
        {
            OpenThreadIds = [firstThread.ThreadId, selectedThread.ThreadId],
            SelectedThreadId = selectedThread.ThreadId,
            Selection = WorkThreadSelectionState.Thread(selectedThread.ThreadId, project.Id),
        };
        coordinator.ApplyRecoveredCatalogState([project], [firstThread, selectedThread]);
        Assert.IsNull(coordinator.FindOpenThread(firstThread.ThreadId));

        await coordinator.CloseThreadTabAsync(selectedThread.ThreadId).ConfigureAwait(false);

        Assert.IsNull(coordinator.SelectedThreadId);
        Assert.IsFalse(coordinator.ViewState.OpenThreadIds.Contains(firstThread.ThreadId, StringComparer.OrdinalIgnoreCase));
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(coordinator.Selection.Target);
        Assert.IsFalse(coordinator.GlobalScopeSelected);
        Assert.AreEqual(project.Id, coordinator.SelectedProjectId);
        Assert.IsNull(coordinator.FindOpenThread(firstThread.ThreadId));
        CollectionAssert.DoesNotContain(loadedThreadIds, firstThread.ThreadId);
    }

    [TestMethod]
    public async Task CloseThreadTabAsync_SelectsDraftBeforeRemovingLastSelectedTabPage()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        string? selectedThreadDuringRemoval = "not-called";
        WorkspaceTarget? targetDuringRemoval = null;
        ShellThreadStateCoordinator? coordinator = null;
        coordinator = CreateCoordinator(
            options,
            removeThreadTabPage: (_, _) =>
            {
                selectedThreadDuringRemoval = coordinator!.SelectedThreadId;
                targetDuringRemoval = coordinator.Selection.Target;
            });
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], [CreateThread("thread-1", project.Id)]);
        coordinator.OpenThread("thread-1");

        await coordinator.CloseThreadTabAsync("thread-1").ConfigureAwait(false);

        Assert.IsNull(selectedThreadDuringRemoval);
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(targetDuringRemoval);
        Assert.IsNull(coordinator.SelectedThreadId);
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(coordinator.Selection.Target);
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
    public void DeleteThreadAndProject_CloseTabsWithExplicitReasons()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var closeReasons = new List<(string ThreadId, ShellTabCloseReason Reason)>();
        var coordinator = CreateCoordinator(options, removeThreadTabPage: (threadId, reason) => closeReasons.Add((threadId, reason)));
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], [CreateThread("thread-1", project.Id), CreateThread("thread-2", project.Id)]);
        coordinator.OpenThread("thread-1");
        coordinator.OpenThread("thread-2");

        coordinator.RemoveDeletedThread("thread-1", fallbackProjectId: project.Id);
        coordinator.RemoveDeletedProject(project.Id, ["thread-2"]);

        CollectionAssert.AreEqual(
            new[]
            {
                ("thread-1", ShellTabCloseReason.ThreadDeleted),
                ("thread-2", ShellTabCloseReason.ProjectClosed),
            },
            closeReasons.ToArray());
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
    public void OpenThread_GlobalThreadDoesNotSelectFirstProject()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var firstProject = CreateProject("project-1", ".azuredevops");
        var globalThread = CreateGlobalThread("global-thread", options.GlobalRoot);
        coordinator.ApplyRecoveredCatalogState([firstProject], [globalThread]);

        coordinator.OpenThread(globalThread.ThreadId);

        Assert.AreEqual(globalThread.ThreadId, coordinator.SelectedThreadId);
        Assert.IsNull(coordinator.SelectedProjectId);
        Assert.IsNull(coordinator.GetSelectedProject());
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
    public void ApplyInitialCatalogState_RestoresLastOpenProjectThreadWhenStartupSelectionIsDraft()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var firstThread = CreateThread("thread-1", project.Id);
        var lastThread = CreateThread("thread-2", project.Id);

        coordinator.ApplyInitialCatalogState(new ShellThreadStateCoordinator.InitialCatalogState(
            [project],
            [firstThread, lastThread],
            new WorkThreadViewState
            {
                OpenThreadIds = [firstThread.ThreadId, lastThread.ThreadId],
                Selection = WorkThreadSelectionState.ProjectDraft(project.Id),
            }));

        Assert.AreEqual(lastThread.ThreadId, coordinator.SelectedThreadId);
        Assert.AreEqual(lastThread.ThreadId, coordinator.PendingStartupThreadRestoreId);
        Assert.AreEqual(WorkThreadSelectionSurface.Thread, coordinator.ViewState.Selection.Surface);
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
            ThemeSchemeName = "Elderberry Dark Soft",
        }).ConfigureAwait(false);

        var viewState = await threadCatalog.LoadViewStateAsync().ConfigureAwait(false);
        Assert.AreEqual(NavigatorProjectSortMode.Date, viewState.Navigator.SortMode);
        Assert.AreEqual(7, viewState.Navigator.RecentThreadsPerProject);
        Assert.AreEqual("Elderberry Dark Soft", viewState.Navigator.ThemeSchemeName);
    }

    [TestMethod]
    public async Task NavigatorThemePreview_ChangesEffectiveThemeWithoutPersisting()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var threadCatalog = new WorkThreadCatalog(options);
        var coordinator = CreateCoordinator(options, threadCatalog);

        await coordinator.SaveNavigatorSettingsAsync(new NavigatorSettings
        {
            SortMode = NavigatorProjectSortMode.Date,
            RecentThreadsPerProject = 7,
            ThemeSchemeName = "Elderberry Dark Soft",
        }).ConfigureAwait(false);

        coordinator.PreviewNavigatorTheme("Blueberry Light");

        Assert.AreEqual("Blueberry Light", coordinator.EffectiveThemeSchemeName);
        var viewState = await threadCatalog.LoadViewStateAsync().ConfigureAwait(false);
        Assert.AreEqual("Elderberry Dark Soft", viewState.Navigator.ThemeSchemeName);

        coordinator.ClearNavigatorThemePreview();

        Assert.AreEqual("Elderberry Dark Soft", coordinator.EffectiveThemeSchemeName);
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
            ThemeSchemeName = "Blueberry Light",
        }).GetAwaiter().GetResult();

        var snapshot = stateStore.Snapshot;
        Assert.AreEqual(1, snapshot.Projects.Count);
        Assert.AreEqual(1, snapshot.Threads.Count);
        Assert.AreEqual("thread-1", snapshot.Selection.SelectedThreadId);
        CollectionAssert.AreEqual(new[] { "thread-1" }, snapshot.OpenThreadIds.ToArray());
        Assert.AreEqual(NavigatorProjectSortMode.Date, snapshot.NavigatorSettings.SortMode);
        Assert.AreEqual(5, snapshot.NavigatorSettings.RecentThreadsPerProject);
        Assert.AreEqual("Blueberry Light", snapshot.NavigatorSettings.ThemeSchemeName);
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
    public void EnsureSelectionDefaults_DoesNotPublishWhenSelectionIsUnchanged()
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

        coordinator.ApplyRecoveredCatalogState([project], []);
        events.Clear();

        coordinator.EnsureSelectionDefaults();

        CollectionAssert.AreEqual(Array.Empty<ShellFrontendEvent>(), events);
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
        Assert.IsFalse(persistedViewState.ThreadStates.ContainsKey("thread-2"));
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
        Action<string>? replaceDraftTabWithThread = null,
        Action<string, ShellTabCloseReason>? removeThreadTabPage = null,
        Func<IReadOnlyList<string>>? getOpenThreadTabIds = null,
        ShellStateStore? stateStore = null,
        FrontendEventPublisher? frontendEvents = null,
        Func<WorkThreadDescriptor, CancellationToken, Task>? ensureThreadHistoryLoadedAsync = null)
    {
        threadCatalog ??= new WorkThreadCatalog(options);
        return TestThreadStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            threadCatalog,
            new InlineUiDispatcher(),
            stateStore ?? new ShellStateStore(new InlineUiDispatcher()),
            loadPromptDraft: loadPromptDraft,
            deletePromptDraft: deletePromptDraft,
            ensureThreadHistoryLoadedAsync: ensureThreadHistoryLoadedAsync,
            getOpenThreadTabIds: getOpenThreadTabIds,
            replaceDraftTabWithThread: replaceDraftTabWithThread,
            removeThreadTabPage: removeThreadTabPage,
            frontendEvents: frontendEvents);
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
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\repo",
            Title = "Test thread",
            Status = WorkThreadStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
        };
    }

    private static WorkThreadDescriptor CreateGlobalThread(string threadId, string globalRoot)
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00");
        return new WorkThreadDescriptor
        {
            ThreadId = threadId,
            Kind = WorkThreadKind.GlobalThread,
            BackendId = AgentBackendIds.Codex.Value,
            WorkingDirectory = globalRoot,
            Title = "Global Thread",
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
