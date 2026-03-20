using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Tabs;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;

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
    public void CreateThreadTabPageContentPlaceholder_ReturnsHiddenDetachedVisual()
    {
        var first = CodeAltaApp.CreateThreadTabPageContentPlaceholder();
        var second = CodeAltaApp.CreateThreadTabPageContentPlaceholder();

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsFalse(first.IsVisible);
        Assert.IsFalse(second.IsVisible);
        Assert.IsNull(first.Parent);
        Assert.IsNull(second.Parent);
        Assert.AreNotSame(first, second);
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
        var projection = ThreadTabStripProjectionBuilder.Build(
            openThreadIds: ["thread-1", "missing-thread"],
            availableThreadIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "thread-1" },
            draftTabOpen: true,
            draftTabId: CodeAltaApp.DraftTabId,
            selectedThreadId: null);

        CollectionAssert.AreEqual(
            new[] { "thread-1", CodeAltaApp.DraftTabId },
            projection.Tabs.Select(static tab => tab.TabId).ToArray());
        Assert.AreEqual(CodeAltaApp.DraftTabId, projection.SelectedTabId);
        Assert.IsTrue(projection.Tabs[1].IsDraft);
    }

    [TestMethod]
    public void Build_SelectsOpenThreadWhenAvailable()
    {
        var projection = ThreadTabStripProjectionBuilder.Build(
            openThreadIds: ["thread-1", "thread-2"],
            availableThreadIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "thread-1", "thread-2" },
            draftTabOpen: true,
            draftTabId: CodeAltaApp.DraftTabId,
            selectedThreadId: "thread-2");

        Assert.AreEqual("thread-2", projection.SelectedTabId);
        Assert.IsFalse(projection.Tabs[1].IsDraft);
        Assert.IsTrue(projection.Tabs[2].IsDraft);
    }
}
