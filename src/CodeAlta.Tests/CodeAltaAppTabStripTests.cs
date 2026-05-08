using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Tabs;
using CodeAlta.Views;
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
}
