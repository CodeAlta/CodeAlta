using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Tabs;
using CodeAlta.Threading;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaAppTabStripTests
{
    [TestMethod]
    public void ResolveOpenTabIndicatorKind_PrefersRunningAndMapsTone()
    {
        Assert.AreEqual(
            OpenTabIndicatorKind.Running,
            ThreadTabVisualFactory.ResolveIndicatorKind(isBusy: true, StatusTone.Ready));
        Assert.AreEqual(
            OpenTabIndicatorKind.Edited,
            ThreadTabVisualFactory.ResolveIndicatorKind(isBusy: false, hasPromptDraft: true, StatusTone.Ready));
        Assert.AreEqual(
            OpenTabIndicatorKind.Ready,
            ThreadTabVisualFactory.ResolveIndicatorKind(isBusy: false, StatusTone.Ready));
        Assert.AreEqual(
            OpenTabIndicatorKind.Warning,
            ThreadTabVisualFactory.ResolveIndicatorKind(isBusy: false, StatusTone.Warning));
        Assert.AreEqual(
            OpenTabIndicatorKind.Error,
            ThreadTabVisualFactory.ResolveIndicatorKind(isBusy: false, StatusTone.Error));
    }

    [TestMethod]
    public void CompactTabTitle_DoesNotChangeForSelectionState()
    {
        Assert.AreEqual("Review startup", ThreadTabVisualFactory.CompactTitle("Review startup"));
    }

    [TestMethod]
    public void BuildDraftTabTitle_ReflectsScope()
    {
        Assert.AreEqual("Global draft", ShellTextFormatter.BuildDraftTabTitle(selectedProject: null, globalScopeSelected: true));
        Assert.AreEqual(
            "CodeAlta draft",
            ShellTextFormatter.BuildDraftTabTitle(
                new ProjectDescriptor { DisplayName = "CodeAlta" },
                globalScopeSelected: false));
    }

    [TestMethod]
    public void Build_IncludesOpenThreadTabsAndSelectedDraftTab()
    {
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor("thread-1", ShellTabKind.Thread));
        tabs.OpenOrGetTab(CreateDescriptor(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft));
        tabs.SelectTabAsync(new ShellTabId(CodeAltaApp.DraftTabId)).GetAwaiter().GetResult();

        var projection = ThreadTabStripProjectionBuilder.Build(tabs.GetTabs());

        CollectionAssert.AreEqual(
            new[] { "thread-1", CodeAltaApp.DraftTabId },
            projection.Tabs.Select(static tab => tab.TabId).ToArray());
        Assert.AreEqual(CodeAltaApp.DraftTabId, projection.SelectedTabId);
        Assert.IsTrue(projection.Tabs[1].IsDraft);
    }

    [TestMethod]
    public void Build_SelectsOpenThreadWhenAvailable()
    {
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor("thread-1", ShellTabKind.Thread));
        tabs.OpenOrGetTab(CreateDescriptor("thread-2", ShellTabKind.Thread));
        tabs.OpenOrGetTab(CreateDescriptor(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft));
        tabs.SelectTabAsync(new ShellTabId("thread-2")).GetAwaiter().GetResult();

        var projection = ThreadTabStripProjectionBuilder.Build(tabs.GetTabs());

        Assert.AreEqual("thread-2", projection.SelectedTabId);
        Assert.IsFalse(projection.Tabs[1].IsDraft);
        Assert.IsTrue(projection.Tabs[2].IsDraft);
    }

    [TestMethod]
    public void Build_AppendsFileTabs_AndPrefersExplicitSelectedFileTab()
    {
        var fileTabId = "file:C:/code/CodeAlta/readme.md";
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor("thread-1", ShellTabKind.Thread));
        tabs.OpenOrGetTab(CreateDescriptor(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft));
        tabs.OpenOrGetTab(CreateDescriptor(fileTabId, ShellTabKind.Editor));
        tabs.SelectTabAsync(new ShellTabId(fileTabId)).GetAwaiter().GetResult();

        var projection = ThreadTabStripProjectionBuilder.Build(tabs.GetTabs());

        CollectionAssert.AreEqual(
            new[] { "thread-1", CodeAltaApp.DraftTabId, "file:C:/code/CodeAlta/readme.md" },
            projection.Tabs.Select(static tab => tab.TabId).ToArray());
        Assert.AreEqual("file:C:/code/CodeAlta/readme.md", projection.SelectedTabId);
        Assert.IsTrue(projection.Tabs[2].IsFile);
    }

    [TestMethod]
    public void Build_IncludesPluginTabsFromShellTabSnapshots()
    {
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor("plugin:stats:main", ShellTabKind.Plugin));

        var projection = ThreadTabStripProjectionBuilder.Build(tabs.GetTabs());

        Assert.AreEqual("plugin:stats:main", projection.Tabs[0].TabId);
        Assert.IsTrue(projection.Tabs[0].IsPlugin);
        Assert.AreEqual("plugin:stats:main", projection.SelectedTabId);
    }

    [TestMethod]
    public void CanCloseTab_HidesCloseButtonForOnlyDraftTab()
    {
        Assert.IsFalse(ThreadTabStripCoordinator.CanCloseTab(
            new ThreadTabStripItemProjection(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft, CanClose: false),
            totalTabCount: 1));
    }

    [TestMethod]
    public void CanCloseTab_AllowsClosingDraftWhenMultipleTabsExist()
    {
        Assert.IsTrue(ThreadTabStripCoordinator.CanCloseTab(
            new ThreadTabStripItemProjection(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft, CanClose: true),
            totalTabCount: 2));
    }

    [TestMethod]
    public void CanCloseTab_AllowsClosingThreadTabsEvenWhenLast()
    {
        Assert.IsTrue(ThreadTabStripCoordinator.CanCloseTab(
            new ThreadTabStripItemProjection("thread-1", ShellTabKind.Thread, CanClose: true),
            totalTabCount: 1));
    }

    [TestMethod]
    public async Task CloseSelectedTabAsync_ClosesThreadThroughTabStripLifecyclePath()
    {
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor("thread-1", ShellTabKind.Thread));
        var closedThreads = new List<string>();
        var coordinator = CreateCoordinator(tabs, closeThreadTab: threadId =>
        {
            closedThreads.Add(threadId);
            tabs.CloseTabAsync(new ShellTabId(threadId), ShellTabCloseReason.UserDetached).GetAwaiter().GetResult();
        });

        var closed = await coordinator.CloseSelectedTabAsync();

        Assert.IsTrue(closed);
        CollectionAssert.AreEqual(new[] { "thread-1" }, closedThreads.ToArray());
        Assert.IsFalse(tabs.TryGetTab(new ShellTabId("thread-1"), out _));
    }

    [TestMethod]
    public async Task CloseSelectedTabAsync_ClosesEditorThroughTabStripLifecyclePath()
    {
        var tabId = "file:C:/code/CodeAlta/readme.md";
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor(tabId, ShellTabKind.Editor));
        var closedFiles = new List<string>();
        var coordinator = CreateCoordinator(tabs, closeFileTab: closedFiles.Add);

        var closed = await coordinator.CloseSelectedTabAsync();

        Assert.IsTrue(closed);
        CollectionAssert.AreEqual(new[] { tabId }, closedFiles.ToArray());
    }

    [TestMethod]
    public void ReplaceDraftTabWithThread_ClosesDraftAndSelectsCreatedThread()
    {
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft));
        tabs.OpenOrGetTab(CreateDescriptor("thread-1", ShellTabKind.Thread));
        tabs.SelectTabAsync(new ShellTabId(CodeAltaApp.DraftTabId)).GetAwaiter().GetResult();
        var coordinator = CreateCoordinator(tabs);

        var replaced = coordinator.ReplaceDraftTabWithThread("thread-1");

        Assert.IsTrue(replaced);
        Assert.IsFalse(tabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out _));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId("thread-1"), out var threadTab));
        Assert.IsTrue(threadTab.IsSelected);
    }

    [TestMethod]
    public void SyncControl_RestoresPersistedSelectedThreadTab()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var threadState = TestThreadStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new WorkThreadCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher));
        var project = CreateProject("project-1", "CodeAlta");
        threadState.ViewState = new WorkThreadViewState
        {
            OpenThreadIds = ["thread-1", "thread-2"],
            Selection = WorkThreadSelectionState.Thread("thread-2", project.Id),
            SelectedThreadId = "thread-2",
        };
        threadState.ApplyRecoveredCatalogState(
            [project],
            [CreateThread("thread-1", project.Id), CreateThread("thread-2", project.Id)]);
        var workspaceView = CreateThreadWorkspaceView();
        var tabs = new InMemoryShellTabService();
        var coordinator = CreateCoordinator(tabs, threadState, workspaceView, dispatcher);

        coordinator.SyncControl();

        Assert.IsTrue(tabs.TryGetTab(new ShellTabId("thread-1"), out var firstTab));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId("thread-2"), out var selectedTab));
        Assert.IsFalse(firstTab.IsSelected);
        Assert.IsTrue(selectedTab.IsSelected);
        Assert.AreEqual(1, workspaceView.ThreadTabControl.SelectedIndex);
    }

    [TestMethod]
    public void SyncControl_RestoresSelectedThreadAfterStartupDraftWasCreated()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var threadState = TestThreadStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new WorkThreadCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher));
        var workspaceView = CreateThreadWorkspaceView();
        var tabs = new InMemoryShellTabService();
        var coordinator = CreateCoordinator(tabs, threadState, workspaceView, dispatcher);

        coordinator.SyncControl();
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out var draftTab));
        Assert.IsTrue(draftTab.IsSelected);

        var project = CreateProject("project-1", "CodeAlta");
        var firstThread = CreateThread("thread-1", project.Id);
        var lastThread = CreateThread("thread-2", project.Id);
        threadState.ApplyInitialCatalogState(new ShellThreadStateCoordinator.InitialCatalogState(
            [project],
            [firstThread, lastThread],
            new WorkThreadViewState
            {
                OpenThreadIds = [firstThread.ThreadId, lastThread.ThreadId],
                Selection = WorkThreadSelectionState.ProjectDraft(project.Id),
            }));

        coordinator.SyncControl();

        Assert.IsTrue(tabs.TryGetTab(new ShellTabId(firstThread.ThreadId), out _));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId(lastThread.ThreadId), out var selectedTab));
        Assert.IsTrue(selectedTab.IsSelected);
        Assert.AreEqual(2, workspaceView.ThreadTabControl.SelectedIndex);
    }

    [TestMethod]
    public async Task SyncControl_ReplacesLastClosedThreadWithDraftDuringCloseProjectionRefresh()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var workspaceView = CreateThreadWorkspaceView();
        var tabs = new InMemoryShellTabService();
        ThreadTabStripCoordinator? coordinator = null;
        var threadState = TestThreadStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new WorkThreadCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher),
            removeThreadTabPage: (threadId, reason) =>
            {
                tabs.CloseTabAsync(new ShellTabId(threadId), reason).GetAwaiter().GetResult();
                workspaceView.RemoveTabPage(threadId);
                coordinator?.SyncControl();
            });
        var project = CreateProject("project-1", "CodeAlta");
        threadState.ApplyRecoveredCatalogState([project], [CreateThread("thread-1", project.Id)]);
        threadState.OpenThread("thread-1");
        coordinator = CreateCoordinator(tabs, threadState, workspaceView, dispatcher);
        coordinator.SyncControl();

        await threadState.CloseThreadTabAsync("thread-1").ConfigureAwait(false);
        coordinator.SyncControl();

        Assert.IsFalse(tabs.TryGetTab(new ShellTabId("thread-1"), out _));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out var draftTab));
        Assert.IsTrue(draftTab.IsSelected);
        CollectionAssert.AreEqual(
            new[] { CodeAltaApp.DraftTabId },
            workspaceView.ThreadTabControl.Tabs
                .Select(GetTabPageId)
                .ToArray());
        Assert.AreEqual(0, workspaceView.ThreadTabControl.SelectedIndex);
    }

    [TestMethod]
    public async Task SyncControl_SelectsFallbackThreadAndActivatesTimelineAfterSelectedTabClose()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var workspaceView = CreateThreadWorkspaceView();
        var tabs = new InMemoryShellTabService();
        var threadState = TestThreadStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new WorkThreadCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher),
            removeThreadTabPage: (threadId, reason) =>
            {
                tabs.CloseTabAsync(new ShellTabId(threadId), reason).GetAwaiter().GetResult();
                workspaceView.RemoveTabPage(threadId);
            });
        var project = CreateProject("project-1", "CodeAlta");
        threadState.ApplyRecoveredCatalogState(
            [project],
            [CreateThread("thread-1", project.Id), CreateThread("thread-2", project.Id)]);
        threadState.OpenThread("thread-1");
        threadState.OpenThread("thread-2");
        tabs.OpenOrGetTab(CreateDescriptor(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft));
        var coordinator = CreateCoordinator(tabs, threadState, workspaceView, dispatcher);
        coordinator.SyncControl();

        await threadState.CloseThreadTabAsync("thread-2").ConfigureAwait(false);
        coordinator.SyncControl();

        Assert.IsFalse(tabs.TryGetTab(new ShellTabId("thread-2"), out _));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId("thread-1"), out var fallbackTab));
        Assert.IsTrue(fallbackTab.IsSelected);
        CollectionAssert.AreEqual(
            new[] { CodeAltaApp.DraftTabId, "thread-1" },
            workspaceView.ThreadTabControl.Tabs
                .Select(GetTabPageId)
                .ToArray());
        Assert.AreEqual(1, workspaceView.ThreadTabControl.SelectedIndex);
        Assert.IsTrue(workspaceView.TryGetTabPage("thread-1", out var page));
        var threadContent = Assert.IsInstanceOfType<VSplitter>(page.Content);
        Assert.AreSame(workspaceView.ThreadBottomPanel, threadContent.Second);
    }

    [TestMethod]
    public void ThreadTabSelection_KeepsPromptPanelAttachedAfterFileEditorTab()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        ThreadTabStripCoordinator? coordinator = null;
        var workspaceView = CreateThreadWorkspaceView(index => coordinator?.OnSelectionChanged(index));
        var tabs = new InMemoryShellTabService();
        var threadState = TestThreadStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new WorkThreadCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher));
        var project = CreateProject("project-1", "CodeAlta");
        threadState.ApplyRecoveredCatalogState([project], [CreateThread("thread-1", project.Id)]);
        threadState.OpenThread("thread-1");
        coordinator = CreateCoordinator(tabs, threadState, workspaceView, dispatcher);
        coordinator.SyncControl();

        var fileTabId = "file:C:/code/CodeAlta/readme.md";
        tabs.OpenOrGetTab(CreateDescriptor(fileTabId, ShellTabKind.Editor));
        tabs.SelectTabAsync(new ShellTabId(fileTabId)).GetAwaiter().GetResult();
        coordinator.SyncControl();

        Assert.IsTrue(workspaceView.TryGetTabPage("thread-1", out var threadPage));
        var threadContent = Assert.IsInstanceOfType<VSplitter>(threadPage.Content);
        Assert.IsNotNull(threadContent.Second, "The thread prompt panel should stay attached to its thread tab content while a file editor tab is active.");
        var stablePromptPanel = threadContent.Second;
        var threadIndex = Array.FindIndex(
            workspaceView.ThreadTabControl.Tabs.Select(GetTabPageId).ToArray(),
            static tabId => string.Equals(tabId, "thread-1", StringComparison.Ordinal));
        Assert.AreNotEqual(-1, threadIndex);

        workspaceView.ThreadTabControl.SelectedIndex = threadIndex;

        Assert.AreSame(
            workspaceView.ThreadBottomPanel,
            threadContent.Second,
            "Selecting the already-open thread tab should activate its existing prompt panel on the first switch.");
        Assert.AreSame(stablePromptPanel, threadContent.Second);
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId("thread-1"), out var selectedThreadTab));
        Assert.IsTrue(selectedThreadTab.IsSelected);
    }

    [TestMethod]
    public void GetAdjacentTabIndex_WrapsLeftFromFirstTab()
    {
        Assert.AreEqual(2, ThreadTabStripCoordinator.GetAdjacentTabIndex(selectedIndex: 0, tabCount: 3, delta: -1));
    }

    [TestMethod]
    public void GetAdjacentTabIndex_WrapsRightFromLastTab()
    {
        Assert.AreEqual(0, ThreadTabStripCoordinator.GetAdjacentTabIndex(selectedIndex: 2, tabCount: 3, delta: 1));
    }

    private static ShellTabDescriptor CreateDescriptor(string tabId, ShellTabKind kind)
        => new()
        {
            TabId = new ShellTabId(tabId),
            Kind = kind,
            Association = CreateAssociation(tabId, kind),
            Header = new TextBlock(tabId),
            Content = new TextBlock($"content:{tabId}"),
            ViewModel = new object(),
        };

    private static ShellTabAssociation CreateAssociation(string tabId, ShellTabKind kind)
    {
        var projectId = ProjectId.NewVersion7();
        return kind switch
        {
            ShellTabKind.PromptDraft => new ShellTabAssociation.PromptDraft(new PromptSessionBinding(
                new PromptSessionId(tabId),
                projectId,
                new ShellThreadRef.Draft(new ThreadDraftId(tabId)),
                new ModelProviderId("provider-1"))),
            ShellTabKind.Thread => new ShellTabAssociation.Thread(
                tabId,
                new PromptSessionId(tabId),
                projectId,
                new ModelProviderId("provider-1")),
            ShellTabKind.Editor => new ShellTabAssociation.Editor(projectId, tabId["file:".Length..]),
            ShellTabKind.Plugin => new ShellTabAssociation.Plugin("stats", "main"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static ThreadTabStripCoordinator CreateCoordinator(
        IShellTabService tabs,
        Action<string>? closeThreadTab = null,
        Action<string>? closeFileTab = null)
    {
        var options = new CatalogOptions
        {
            GlobalRoot = Path.Combine(Path.GetTempPath(), "CodeAltaTests", Guid.NewGuid().ToString("N")),
        };
        var dispatcher = new InlineUiDispatcher();
        var threadState = TestThreadStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new WorkThreadCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher));

        var selection = new ThreadSelectionContext(
            threadState,
            static (_, _) => Task.CompletedTask,
            static _ => false);
        var context = new ThreadTabContext(
            new DelegatingThreadTabSurfacePort(
                static () => null,
                static () => null,
                static build => new ComputedVisual(build),
                dispatcher),
            new DelegatingThreadTabLifecyclePort(
                static () => { },
                static () => { },
                closeThreadTab ?? (static _ => { }),
                static () => { },
                static _ => { }),
            new DelegatingFileEditorTabPort(
                static _ => null,
                static _ => { },
                closeFileTab ?? (static _ => { })));
        return new ThreadTabStripCoordinator(selection, context, tabs);
    }

    private static ThreadTabStripCoordinator CreateCoordinator(
        IShellTabService tabs,
        ShellThreadStateCoordinator threadState,
        ThreadWorkspaceView workspaceView,
        IUiDispatcher dispatcher)
    {
        var selection = new ThreadSelectionContext(
            threadState,
            static (_, _) => Task.CompletedTask,
            threadId => string.Equals(threadState.SelectedThreadId, threadId, StringComparison.OrdinalIgnoreCase));
        var context = new ThreadTabContext(
            new DelegatingThreadTabSurfacePort(
                () => workspaceView.ThreadTabControl,
                () => workspaceView,
                static build => new ComputedVisual(build),
                dispatcher),
            new DelegatingThreadTabLifecyclePort(
                static () => { },
                static () => { },
                threadId => threadState.CloseThreadTabAsync(threadId).GetAwaiter().GetResult(),
                static () => { },
                threadId => _ = threadState.OpenThread(threadId)),
            new DelegatingFileEditorTabPort(
                static _ => null,
                static _ => { },
                static _ => { }));
        return new ThreadTabStripCoordinator(selection, context, tabs);
    }

    private static ThreadWorkspaceView CreateThreadWorkspaceView(Action<int>? selectTab = null)
        => new(
            new CodeAltaShellViewModel(),
            new ThreadWorkspaceViewModel(),
            new PromptComposerViewModel(),
            Array.Empty<ThreadWorkspaceCommandBinding>(),
            ThreadWorkspaceChromeController.Empty,
            PromptComposerViewController.Create(static _ => { }, static () => { }, static () => { }, static () => { }, static () => { }),
            QueuedPromptStripController.Create(
                static _ => { },
                static _ => { },
                static _ => { },
                static _ => { },
                static (_, _) => { },
                static (_, _) => { },
                static (onAccepted, placeholder) => ThreadWorkspaceView.CreateStyledPromptEditor(onAccepted, null, null, placeholder)),
            ModelProviderSelectorController.Create(static _ => { }, static _ => { }, static _ => { }, static () => { }),
            ThreadTabHostController.Create(selectTab ?? (static _ => { })),
            NullProjectFileSearchService.Instance,
            static () => null,
            static (_, _) => new PromptComposerSessionBinding(new State<string?>(string.Empty)),
            new State<float>(0));

    private static string GetTabPageId(TabPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var tabId = page.Data?.GetType().GetProperty("TabId")?.GetValue(page.Data) as string;
        return tabId ?? string.Empty;
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

    private static WorkThreadDescriptor CreateThread(string threadId, string projectId)
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00");
        return new WorkThreadDescriptor
        {
            ThreadId = threadId,
            Kind = WorkThreadKind.ProjectThread,
            BackendId = "codex",
            BackendSessionId = $"session-{threadId}",
            ProjectRef = projectId,
            WorkingDirectory = @"C:\repo",
            Title = threadId,
            Status = WorkThreadStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
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
