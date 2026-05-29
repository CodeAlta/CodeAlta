using CodeAlta.Agent;
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
            SessionTabVisualFactory.ResolveIndicatorKind(isBusy: true, StatusTone.Ready));
        Assert.AreEqual(
            OpenTabIndicatorKind.Edited,
            SessionTabVisualFactory.ResolveIndicatorKind(isBusy: false, hasPromptDraft: true, StatusTone.Ready));
        Assert.AreEqual(
            OpenTabIndicatorKind.Ready,
            SessionTabVisualFactory.ResolveIndicatorKind(isBusy: false, StatusTone.Ready));
        Assert.AreEqual(
            OpenTabIndicatorKind.Warning,
            SessionTabVisualFactory.ResolveIndicatorKind(isBusy: false, StatusTone.Warning));
        Assert.AreEqual(
            OpenTabIndicatorKind.Error,
            SessionTabVisualFactory.ResolveIndicatorKind(isBusy: false, StatusTone.Error));
    }

    [TestMethod]
    public void CompactTabTitle_DoesNotChangeForSelectionState()
    {
        Assert.AreEqual("Review startup", SessionTabVisualFactory.CompactTitle("Review startup"));
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
    public void Build_IncludesOpenSessionTabsAndSelectedDraftTab()
    {
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor("session-1", ShellTabKind.Session));
        tabs.OpenOrGetTab(CreateDescriptor(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft));
        tabs.SelectTabAsync(new ShellTabId(CodeAltaApp.DraftTabId)).GetAwaiter().GetResult();

        var projection = SessionTabStripProjectionBuilder.Build(tabs.GetTabs());

        CollectionAssert.AreEqual(
            new[] { "session-1", CodeAltaApp.DraftTabId },
            projection.Tabs.Select(static tab => tab.TabId).ToArray());
        Assert.AreEqual(CodeAltaApp.DraftTabId, projection.SelectedTabId);
        Assert.IsTrue(projection.Tabs[1].IsDraft);
    }

    [TestMethod]
    public void Build_SelectsOpenSessionWhenAvailable()
    {
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor("session-1", ShellTabKind.Session));
        tabs.OpenOrGetTab(CreateDescriptor("session-2", ShellTabKind.Session));
        tabs.OpenOrGetTab(CreateDescriptor(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft));
        tabs.SelectTabAsync(new ShellTabId("session-2")).GetAwaiter().GetResult();

        var projection = SessionTabStripProjectionBuilder.Build(tabs.GetTabs());

        Assert.AreEqual("session-2", projection.SelectedTabId);
        Assert.IsFalse(projection.Tabs[1].IsDraft);
        Assert.IsTrue(projection.Tabs[2].IsDraft);
    }

    [TestMethod]
    public void Build_AppendsFileTabs_AndPrefersExplicitSelectedFileTab()
    {
        var fileTabId = "file:C:/code/CodeAlta/readme.md";
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor("session-1", ShellTabKind.Session));
        tabs.OpenOrGetTab(CreateDescriptor(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft));
        tabs.OpenOrGetTab(CreateDescriptor(fileTabId, ShellTabKind.Editor));
        tabs.SelectTabAsync(new ShellTabId(fileTabId)).GetAwaiter().GetResult();

        var projection = SessionTabStripProjectionBuilder.Build(tabs.GetTabs());

        CollectionAssert.AreEqual(
            new[] { "session-1", CodeAltaApp.DraftTabId, "file:C:/code/CodeAlta/readme.md" },
            projection.Tabs.Select(static tab => tab.TabId).ToArray());
        Assert.AreEqual("file:C:/code/CodeAlta/readme.md", projection.SelectedTabId);
        Assert.IsTrue(projection.Tabs[2].IsFile);
    }

    [TestMethod]
    public void Build_IncludesPluginTabsFromShellTabSnapshots()
    {
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor("plugin:stats:main", ShellTabKind.Plugin));

        var projection = SessionTabStripProjectionBuilder.Build(tabs.GetTabs());

        Assert.AreEqual("plugin:stats:main", projection.Tabs[0].TabId);
        Assert.IsTrue(projection.Tabs[0].IsPlugin);
        Assert.AreEqual("plugin:stats:main", projection.SelectedTabId);
    }

    [TestMethod]
    public void CanCloseTab_HidesCloseButtonForOnlyDraftTab()
    {
        Assert.IsFalse(SessionTabStripCoordinator.CanCloseTab(
            new SessionTabStripItemProjection(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft, CanClose: false),
            totalTabCount: 1));
    }

    [TestMethod]
    public void CanCloseTab_AllowsClosingDraftWhenMultipleTabsExist()
    {
        Assert.IsTrue(SessionTabStripCoordinator.CanCloseTab(
            new SessionTabStripItemProjection(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft, CanClose: true),
            totalTabCount: 2));
    }

    [TestMethod]
    public void CanCloseTab_AllowsClosingSessionTabsEvenWhenLast()
    {
        Assert.IsTrue(SessionTabStripCoordinator.CanCloseTab(
            new SessionTabStripItemProjection("session-1", ShellTabKind.Session, CanClose: true),
            totalTabCount: 1));
    }

    [TestMethod]
    public async Task CloseSelectedTabAsync_ClosesSessionThroughTabStripLifecyclePath()
    {
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor("session-1", ShellTabKind.Session));
        var closedSessions = new List<string>();
        var coordinator = CreateCoordinator(tabs, closeSessionTab: sessionId =>
        {
            closedSessions.Add(sessionId);
            tabs.CloseTabAsync(new ShellTabId(sessionId), ShellTabCloseReason.UserDetached).GetAwaiter().GetResult();
        });

        var closed = await coordinator.CloseSelectedTabAsync();

        Assert.IsTrue(closed);
        CollectionAssert.AreEqual(new[] { "session-1" }, closedSessions.ToArray());
        Assert.IsFalse(tabs.TryGetTab(new ShellTabId("session-1"), out _));
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
    public void ReplaceDraftTabWithSession_ClosesDraftAndSelectsCreatedSession()
    {
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft));
        tabs.OpenOrGetTab(CreateDescriptor("session-1", ShellTabKind.Session));
        tabs.SelectTabAsync(new ShellTabId(CodeAltaApp.DraftTabId)).GetAwaiter().GetResult();
        var coordinator = CreateCoordinator(tabs);

        var replaced = coordinator.ReplaceDraftTabWithSession("session-1");

        Assert.IsTrue(replaced);
        Assert.IsFalse(tabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out _));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId("session-1"), out var sessionTab));
        Assert.IsTrue(sessionTab.IsSelected);
    }

    [TestMethod]
    public void SyncControl_RestoresPersistedSelectedSessionTab()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new SessionViewCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher));
        var project = CreateProject("project-1", "CodeAlta");
        sessionState.ViewState = new SessionViewViewState
        {
            OpenSessionIds = ["session-1", "session-2"],
            Selection = SessionViewSelectionState.Session("session-2", project.Id),
            SelectedSessionId = "session-2",
        };
        sessionState.ApplyRecoveredCatalogState(
            [project],
            [CreateSession("session-1", project.Id), CreateSession("session-2", project.Id)]);
        var workspaceView = CreateSessionWorkspaceView();
        var tabs = new InMemoryShellTabService();
        var coordinator = CreateCoordinator(tabs, sessionState, workspaceView, dispatcher);

        coordinator.SyncControl();

        Assert.IsTrue(tabs.TryGetTab(new ShellTabId("session-1"), out var firstTab));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId("session-2"), out var selectedTab));
        Assert.IsFalse(firstTab.IsSelected);
        Assert.IsTrue(selectedTab.IsSelected);
        Assert.AreEqual(1, workspaceView.SessionTabControl.SelectedIndex);
    }

    [TestMethod]
    public void SyncControl_PreservesSelectedDraftAfterStartupDraftWasCreated()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new SessionViewCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher));
        var workspaceView = CreateSessionWorkspaceView();
        var tabs = new InMemoryShellTabService();
        var coordinator = CreateCoordinator(tabs, sessionState, workspaceView, dispatcher);

        coordinator.SyncControl();
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out var draftTab));
        Assert.IsTrue(draftTab.IsSelected);

        var project = CreateProject("project-1", "CodeAlta");
        var firstSession = CreateSession("session-1", project.Id);
        var lastSession = CreateSession("session-2", project.Id);
        sessionState.ApplyInitialCatalogState(new ShellSessionStateCoordinator.InitialCatalogState(
            [project],
            [firstSession, lastSession],
            new SessionViewViewState
            {
                OpenSessionIds = [firstSession.SessionId, lastSession.SessionId],
                Selection = SessionViewSelectionState.ProjectDraft(project.Id),
            }));

        coordinator.SyncControl();

        Assert.IsTrue(tabs.TryGetTab(new ShellTabId(firstSession.SessionId), out _));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId(lastSession.SessionId), out _));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out var selectedTab));
        Assert.IsTrue(selectedTab.IsSelected);
        Assert.AreEqual(0, workspaceView.SessionTabControl.SelectedIndex);
    }

    [TestMethod]
    public async Task SyncControl_ReplacesLastClosedSessionWithDraftDuringCloseProjectionRefresh()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var workspaceView = CreateSessionWorkspaceView();
        var tabs = new InMemoryShellTabService();
        SessionTabStripCoordinator? coordinator = null;
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new SessionViewCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher),
            getOpenSessionTabIds: () => tabs.GetTabs()
                .Where(static tab => tab.Kind == ShellTabKind.Session)
                .Select(static tab => tab.TabId.Value)
                .ToArray(),
            removeSessionTabPage: (sessionId, reason) =>
            {
                tabs.CloseTabAsync(new ShellTabId(sessionId), reason).GetAwaiter().GetResult();
                workspaceView.RemoveTabPage(sessionId);
                coordinator?.SyncControl();
            });
        var project = CreateProject("project-1", "CodeAlta");
        sessionState.ApplyRecoveredCatalogState([project], [CreateSession("session-1", project.Id)]);
        sessionState.OpenSession("session-1");
        coordinator = CreateCoordinator(tabs, sessionState, workspaceView, dispatcher);
        coordinator.SyncControl();

        await sessionState.CloseSessionTabAsync("session-1").ConfigureAwait(false);
        coordinator.SyncControl();

        Assert.IsFalse(tabs.TryGetTab(new ShellTabId("session-1"), out _));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out var draftTab));
        Assert.IsTrue(draftTab.IsSelected);
        CollectionAssert.AreEqual(
            new[] { CodeAltaApp.DraftTabId },
            workspaceView.SessionTabControl.Tabs
                .Select(GetTabPageId)
                .ToArray());
        Assert.AreEqual(0, workspaceView.SessionTabControl.SelectedIndex);
    }

    [TestMethod]
    public async Task SyncControl_SelectsFallbackSessionAndActivatesTimelineAfterSelectedTabClose()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var workspaceView = CreateSessionWorkspaceView();
        var tabs = new InMemoryShellTabService();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new SessionViewCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher),
            getOpenSessionTabIds: () => tabs.GetTabs()
                .Where(static tab => tab.Kind == ShellTabKind.Session)
                .Select(static tab => tab.TabId.Value)
                .ToArray(),
            removeSessionTabPage: (sessionId, reason) =>
            {
                tabs.CloseTabAsync(new ShellTabId(sessionId), reason).GetAwaiter().GetResult();
                workspaceView.RemoveTabPage(sessionId);
            });
        var project = CreateProject("project-1", "CodeAlta");
        sessionState.ApplyRecoveredCatalogState(
            [project],
            [CreateSession("session-1", project.Id), CreateSession("session-2", project.Id)]);
        sessionState.OpenSession("session-1");
        sessionState.OpenSession("session-2");
        tabs.OpenOrGetTab(CreateDescriptor(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft));
        var coordinator = CreateCoordinator(tabs, sessionState, workspaceView, dispatcher);
        coordinator.SyncControl();

        await sessionState.CloseSessionTabAsync("session-2").ConfigureAwait(false);
        coordinator.SyncControl();

        Assert.IsFalse(tabs.TryGetTab(new ShellTabId("session-2"), out _));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId("session-1"), out var fallbackTab));
        Assert.IsTrue(fallbackTab.IsSelected);
        CollectionAssert.AreEqual(
            new[] { CodeAltaApp.DraftTabId, "session-1" },
            workspaceView.SessionTabControl.Tabs
                .Select(GetTabPageId)
                .ToArray());
        Assert.AreEqual(1, workspaceView.SessionTabControl.SelectedIndex);
        Assert.IsTrue(workspaceView.TryGetTabPage("session-1", out var page));
        var sessionContent = Assert.IsInstanceOfType<VSplitter>(page.Content);
        Assert.AreSame(workspaceView.SessionBottomPanel, sessionContent.Second);
    }

    [TestMethod]
    public void SyncControl_PrunesSessionShellTabsMissingFromCatalog()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var workspaceView = CreateSessionWorkspaceView();
        var tabs = new InMemoryShellTabService();
        tabs.OpenOrGetTab(CreateDescriptor("missing-session", ShellTabKind.Session));
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new SessionViewCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher));
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("session-1", project.Id);
        sessionState.ApplyRecoveredCatalogState([project], [session]);
        sessionState.OpenSession(session.SessionId);
        var coordinator = CreateCoordinator(tabs, sessionState, workspaceView, dispatcher);

        coordinator.SyncControl();

        Assert.IsFalse(tabs.TryGetTab(new ShellTabId("missing-session"), out _));
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId(session.SessionId), out var selectedTab));
        Assert.IsTrue(selectedTab.IsSelected);
        CollectionAssert.AreEqual(
            new[] { session.SessionId },
            workspaceView.SessionTabControl.Tabs
                .Select(GetTabPageId)
                .ToArray());
    }

    [TestMethod]
    public void SyncControl_IgnoresReentrantShellTabEventsWhileOpeningSessionTabs()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var workspaceView = CreateSessionWorkspaceView();
        var tabs = new InMemoryShellTabService();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new SessionViewCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher));
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("session-1", project.Id);
        sessionState.ApplyRecoveredCatalogState([project], [session]);
        sessionState.OpenSession(session.SessionId);
        var coordinator = CreateCoordinator(tabs, sessionState, workspaceView, dispatcher);
        tabs.TabsChanged += (_, _) => coordinator.SyncControl();

        coordinator.SyncControl();

        Assert.IsTrue(tabs.TryGetTab(new ShellTabId(session.SessionId), out var selectedTab));
        Assert.IsTrue(selectedTab.IsSelected);
        CollectionAssert.AreEqual(
            new[] { session.SessionId },
            workspaceView.SessionTabControl.Tabs
                .Select(GetTabPageId)
                .ToArray());
    }

    [TestMethod]
    public void SessionTabSelection_KeepsPromptPanelAttachedAfterFileEditorTab()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        SessionTabStripCoordinator? coordinator = null;
        var workspaceView = CreateSessionWorkspaceView(index => coordinator?.OnSelectionChanged(index));
        var tabs = new InMemoryShellTabService();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new SessionViewCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher));
        var project = CreateProject("project-1", "CodeAlta");
        sessionState.ApplyRecoveredCatalogState([project], [CreateSession("session-1", project.Id)]);
        sessionState.OpenSession("session-1");
        coordinator = CreateCoordinator(tabs, sessionState, workspaceView, dispatcher);
        coordinator.SyncControl();

        var fileTabId = "file:C:/code/CodeAlta/readme.md";
        tabs.OpenOrGetTab(CreateDescriptor(fileTabId, ShellTabKind.Editor));
        tabs.SelectTabAsync(new ShellTabId(fileTabId)).GetAwaiter().GetResult();
        coordinator.SyncControl();

        Assert.IsTrue(workspaceView.TryGetTabPage("session-1", out var sessionPage));
        var sessionContent = Assert.IsInstanceOfType<VSplitter>(sessionPage.Content);
        Assert.IsNotNull(sessionContent.Second, "The session prompt panel should stay attached to its session tab content while a file editor tab is active.");
        var stablePromptPanel = sessionContent.Second;
        var sessionIndex = Array.FindIndex(
            workspaceView.SessionTabControl.Tabs.Select(GetTabPageId).ToArray(),
            static tabId => string.Equals(tabId, "session-1", StringComparison.Ordinal));
        Assert.AreNotEqual(-1, sessionIndex);

        workspaceView.SessionTabControl.SelectedIndex = sessionIndex;

        Assert.AreSame(
            workspaceView.SessionBottomPanel,
            sessionContent.Second,
            "Selecting the already-open session tab should activate its existing prompt panel on the first switch.");
        Assert.AreSame(stablePromptPanel, sessionContent.Second);
        Assert.IsTrue(tabs.TryGetTab(new ShellTabId("session-1"), out var selectedSessionTab));
        Assert.IsTrue(selectedSessionTab.IsSelected);
    }

    [TestMethod]
    public void GetAdjacentTabIndex_WrapsLeftFromFirstTab()
    {
        Assert.AreEqual(2, SessionTabStripCoordinator.GetAdjacentTabIndex(selectedIndex: 0, tabCount: 3, delta: -1));
    }

    [TestMethod]
    public void GetAdjacentTabIndex_WrapsRightFromLastTab()
    {
        Assert.AreEqual(0, SessionTabStripCoordinator.GetAdjacentTabIndex(selectedIndex: 2, tabCount: 3, delta: 1));
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
                new ShellSessionRef.Draft(new SessionDraftId(tabId)),
                new ModelProviderId("provider-1"))),
            ShellTabKind.Session => new ShellTabAssociation.Session(
                tabId,
                new PromptSessionId(tabId),
                projectId,
                new ModelProviderId("provider-1")),
            ShellTabKind.Editor => new ShellTabAssociation.Editor(projectId, tabId["file:".Length..]),
            ShellTabKind.Plugin => new ShellTabAssociation.Plugin("stats", "main"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static SessionTabStripCoordinator CreateCoordinator(
        IShellTabService tabs,
        Action<string>? closeSessionTab = null,
        Action<string>? closeFileTab = null)
    {
        var options = new CatalogOptions
        {
            GlobalRoot = Path.Combine(Path.GetTempPath(), "CodeAltaTests", Guid.NewGuid().ToString("N")),
        };
        var dispatcher = new InlineUiDispatcher();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new SessionViewCatalog(options),
            dispatcher,
            new ShellStateStore(dispatcher));

        var selection = new SessionSelectionContext(
            sessionState,
            static (_, _) => Task.CompletedTask,
            static _ => false);
        var context = new SessionTabContext(
            new DelegatingSessionTabSurfacePort(
                static () => null,
                static () => null,
                static build => new ComputedVisual(build),
                dispatcher),
            new DelegatingSessionTabLifecyclePort(
                static () => { },
                static () => { },
                closeSessionTab ?? (static _ => { }),
                static () => { },
                static _ => { }),
            new DelegatingFileEditorTabPort(
                static _ => null,
                static _ => { },
                closeFileTab ?? (static _ => { })));
        return new SessionTabStripCoordinator(selection, context, tabs, new State<float>(0));
    }

    private static SessionTabStripCoordinator CreateCoordinator(
        IShellTabService tabs,
        ShellSessionStateCoordinator sessionState,
        SessionWorkspaceView workspaceView,
        IUiDispatcher dispatcher)
    {
        var selection = new SessionSelectionContext(
            sessionState,
            static (_, _) => Task.CompletedTask,
            sessionId => string.Equals(sessionState.SelectedSessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        var context = new SessionTabContext(
            new DelegatingSessionTabSurfacePort(
                () => workspaceView.SessionTabControl,
                () => workspaceView,
                static build => new ComputedVisual(build),
                dispatcher),
            new DelegatingSessionTabLifecyclePort(
                static () => { },
                static () => { },
                sessionId => sessionState.CloseSessionTabAsync(sessionId).GetAwaiter().GetResult(),
                static () => { },
                sessionId => _ = sessionState.OpenSession(sessionId)),
            new DelegatingFileEditorTabPort(
                static _ => null,
                static _ => { },
                static _ => { }));
        return new SessionTabStripCoordinator(selection, context, tabs, new State<float>(0));
    }

    private static SessionWorkspaceView CreateSessionWorkspaceView(Action<int>? selectTab = null)
        => new(
            new CodeAltaShellViewModel(),
            new SessionWorkspaceViewModel(),
            new PromptComposerViewModel(),
            TestShellCommandSurface.Create(),
            SessionWorkspaceChromeController.Empty,
            PromptComposerViewController.Create(static _ => { }, static () => { }, static () => { }, static () => { }, static () => { }),
            QueuedPromptStripController.Create(
                static _ => { },
                static _ => { },
                static _ => { },
                static _ => { },
                static (_, _) => { },
                static (_, _) => { },
                static (onAccepted, placeholder) => SessionWorkspaceView.CreateStyledPromptEditor(onAccepted, null, null, placeholder)),
            ModelProviderSelectorController.Create(static _ => { }, static _ => { }, static _ => { }, static () => { }),
            SessionTabHostController.Create(selectTab ?? (static _ => { })),
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

    private static SessionViewDescriptor CreateSession(string sessionId, string projectId)
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00");
        return new SessionViewDescriptor
        {
            SessionId = sessionId,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = "codex",
            ProjectRef = projectId,
            WorkingDirectory = @"C:\repo",
            Title = sessionId,
            Status = SessionViewStatus.Active,
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
