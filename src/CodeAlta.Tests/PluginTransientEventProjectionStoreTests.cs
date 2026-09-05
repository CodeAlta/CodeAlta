using CodeAlta.Tui.App;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PluginTransientEventProjectionStoreTests
{
    [TestMethod]
    public void Apply_UpsertsStableMarkdownProjection()
    {
        var store = new PluginTransientEventProjectionStore();

        var changed = store.Apply(new PluginDerivedSessionEvent
        {
            EventId = "plugin:event-1",
            Markdown = "### Stats",
        });
        var unchanged = store.Apply(new PluginDerivedSessionEvent
        {
            EventId = "plugin:event-1",
            Markdown = "### Stats",
        });

        Assert.IsTrue(changed);
        Assert.IsFalse(unchanged);
        Assert.AreEqual("### Stats", store.Snapshot.Single().Markdown);
    }

    [TestMethod]
    public void Apply_RemoveDeletesExistingProjection()
    {
        var store = new PluginTransientEventProjectionStore();
        store.Apply(new PluginDerivedSessionEvent { EventId = "plugin:event-1", Markdown = "text" });

        var removed = store.Apply(new PluginDerivedSessionEvent { EventId = "plugin:event-1", Remove = true });

        Assert.IsTrue(removed);
        Assert.AreEqual(0, store.Snapshot.Count);
    }

    [TestMethod]
    public void Apply_UsesDefaultMarkdownWhenPluginDoesNotProvideText()
    {
        var store = new PluginTransientEventProjectionStore();

        store.Apply(new PluginDerivedSessionEvent
        {
            EventId = "plugin:event-1",
            RenderTarget = "stats",
        });

        StringAssert.Contains(store.Snapshot.Single().Markdown, "plugin:event-1");
        StringAssert.Contains(store.Snapshot.Single().Markdown, "stats");
    }

    [TestMethod]
    public void RefreshDynamic_UpdatesStoredMarkdownAndDetails()
    {
        var store = new PluginTransientEventProjectionStore();
        var dynamicContent = new TestDynamicContent("computing", []);
        store.Apply(new PluginDerivedSessionEvent
        {
            EventId = "plugin:event-1",
            DynamicContent = dynamicContent,
        });

        dynamicContent.Update(
            "done",
            [new PluginDerivedSessionEventDetailSection { Header = "Details", Markdown = "ready" }]);
        var changed = store.RefreshDynamic("plugin:event-1");

        Assert.IsTrue(changed);
        var projection = store.Snapshot.Single();
        Assert.AreEqual("done", projection.Markdown);
        Assert.AreEqual("ready", projection.DetailSections.Single().Markdown);
    }

    private sealed class TestDynamicContent(
        string markdown,
        IReadOnlyList<PluginDerivedSessionEventDetailSection> detailSections) : PluginDynamicDerivedSessionEventContent
    {
        private string _markdown = markdown;
        private IReadOnlyList<PluginDerivedSessionEventDetailSection> _detailSections = detailSections;

        public override string Markdown => _markdown;

        public override IReadOnlyList<PluginDerivedSessionEventDetailSection> DetailSections => _detailSections;

        public void Update(string newMarkdown, IReadOnlyList<PluginDerivedSessionEventDetailSection> newDetailSections)
        {
            _markdown = newMarkdown;
            _detailSections = newDetailSections;
            NotifyChanged();
        }
    }
}
