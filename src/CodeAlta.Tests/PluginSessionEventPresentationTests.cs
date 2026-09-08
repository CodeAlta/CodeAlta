using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using CodeAlta.Tui.App;

namespace CodeAlta.Tests;

/// <summary>Literal data, admitted in-memory projection storage and deferred callbacks only; no visual or runtime acquisition.</summary>
[TestClass]
public sealed class PluginSessionEventPresentationTests
{
    [TestMethod]
    public void NeutralProjection_PreservesDataWithoutInvokingNativeFactories()
    {
        var payload = new object();
        var dynamic = new DynamicContent();
        var detail = new PluginTerminalDerivedSessionEventDetailSection
        {
            Header = "Detail", Markdown = "detail text", VisualFactory = NeverRender, HeaderVisualFactory = NeverRender,
        };
        PluginDerivedSessionEvent value = new PluginTerminalDerivedSessionEvent
        {
            EventId = "event", Markdown = "portable", Timestamp = DateTimeOffset.UnixEpoch,
            RenderTarget = "target", Payload = payload, DetailSections = [detail],
            DynamicContent = dynamic, VisualFactory = NeverRender,
        };
        Assert.AreEqual("portable", value.Markdown);
        Assert.AreEqual(DateTimeOffset.UnixEpoch, value.Timestamp);
        Assert.AreSame(payload, value.Payload);
        Assert.AreSame(detail, value.DetailSections.Single());
        Assert.AreSame(dynamic, value.DynamicContent);
        var clone = value with { Remove = true };
        Assert.IsTrue(clone.Remove);
        Assert.AreSame(payload, clone.Payload);
        Assert.IsInstanceOfType<PluginTerminalDerivedSessionEvent>(clone);
        Assert.IsNull(dynamic.VisualFactory); // The optional dynamic base retains its native-null default.
    }

    [TestMethod]
    public void Apply_PrefersDynamicFactoryAndRetainsStaticFallback()
    {
        var store = new PluginTransientEventProjectionStore();
        PluginSessionEventVisualFactory native = NeverRender;
        PluginSessionEventVisualFactory fallback = _ => throw new InvalidOperationException("static factory must remain deferred");
        var dynamic = new RecordingContent { Factory = native };
        var value = Event(dynamic, fallback);
        Assert.IsTrue(store.Apply(value));
        Assert.AreSame(native, store.Get("event")!.VisualFactory);
        dynamic.Factory = null;
        Assert.IsTrue(store.Apply(value));
        Assert.AreSame(fallback, store.Get("event")!.VisualFactory);
        var neutralDynamic = new NeutralDynamicContent();
        store.Apply(value with { DynamicContent = neutralDynamic });
        Assert.AreSame(fallback, store.Get("event")!.VisualFactory);
    }

    [TestMethod]
    public void Apply_PreservesDynamicGetterOrderAndFailure()
    {
        var store = new PluginTransientEventProjectionStore();
        var dynamic = new RecordingContent();
        var value = Event(dynamic, NeverRender);
        store.Apply(value);
        CollectionAssert.AreEqual(new[] { "markdown", "details", "factory" }, dynamic.Reads.ToArray());
        var original = store.Get("event");
        var order = new[] { "markdown", "details", "factory" };
        foreach (var member in order)
        {
            dynamic.Reads.Clear();
            dynamic.FailAt = member;
            var failure = Assert.ThrowsExactly<InvalidOperationException>(() => store.Apply(value));
            Assert.AreSame(dynamic.Failure, failure);
            CollectionAssert.AreEqual(order[..(Array.IndexOf(order, member) + 1)], dynamic.Reads.ToArray());
            Assert.AreSame(original, store.Get("event"));
        }
    }

    [TestMethod]
    public void RefreshDynamic_NullFactoryClearsStaticFallback()
    {
        var store = new PluginTransientEventProjectionStore();
        var dynamic = new RecordingContent();
        store.Apply(Event(dynamic, NeverRender));
        Assert.IsNotNull(store.Get("event")!.VisualFactory);
        Assert.IsTrue(store.RefreshDynamic("event"));
        Assert.IsNull(store.Get("event")!.VisualFactory);
        Assert.IsFalse(store.RefreshDynamic("event"));
        dynamic.Factory = NeverRender;
        Assert.IsTrue(store.RefreshDynamic("event"));
        Assert.AreSame(dynamic.Factory, store.Get("event")!.VisualFactory);
        // Neutral dynamic content has no native factory either; refresh must still clear static fallback.
        store.Apply(Event(new NeutralDynamicContent(), NeverRender));
        Assert.IsTrue(store.RefreshDynamic("event"));
        Assert.IsNull(store.Get("event")!.VisualFactory);
    }

    [TestMethod]
    public void RefreshDynamic_PreservesGetterOrderAndFailure()
    {
        var store = new PluginTransientEventProjectionStore();
        var dynamic = new RecordingContent { Factory = NeverRender };
        store.Apply(Event(dynamic, NeverRender));
        dynamic.Reads.Clear();
        Assert.IsFalse(store.RefreshDynamic("event"));
        CollectionAssert.AreEqual(new[] { "markdown", "details", "factory" }, dynamic.Reads.ToArray());
        var original = store.Get("event");
        var order = new[] { "markdown", "details", "factory" };
        foreach (var member in order)
        {
            dynamic.Reads.Clear();
            dynamic.FailAt = member;
            var failure = Assert.ThrowsExactly<InvalidOperationException>(() => store.RefreshDynamic("event"));
            Assert.AreSame(dynamic.Failure, failure);
            CollectionAssert.AreEqual(order[..(Array.IndexOf(order, member) + 1)], dynamic.Reads.ToArray());
            Assert.AreSame(original, store.Get("event"));
        }
    }

    [TestMethod]
    public void ProjectionStore_PreservesUpsertRemoveAndEquality()
    {
        var store = new PluginTransientEventProjectionStore();
        var value = Event(null, NeverRender);
        Assert.IsTrue(store.Apply(value));
        Assert.IsFalse(store.Apply(value with { }));
        Assert.IsTrue(store.Apply(value with { Markdown = "changed portable text" }));
        Assert.AreEqual("changed portable text", store.Snapshot.Single().Markdown);
        Assert.IsTrue(store.Apply(value with { Remove = true }));
        Assert.IsFalse(store.Apply(value with { Remove = true }));
        Assert.AreEqual(0, store.Snapshot.Count);
        Assert.IsFalse(store.RefreshDynamic("absent"));
    }

    [TestMethod]
    public void DynamicContent_PreservesNotificationsAndSubscriptionDisposal()
    {
        var content = new DynamicContent();
        var calls = 0;
        object? sender = null;
        EventArgs? args = null;
        using (var subscription = new PluginDynamicProjectionSubscription(content, (actualSender, actualArgs) =>
               { calls++; sender = actualSender; args = actualArgs; }))
        {
            Assert.AreSame(content, subscription.Content);
            content.Raise();
            Assert.AreEqual(1, calls);
            Assert.AreSame(content, sender);
            Assert.AreSame(EventArgs.Empty, args);
        }
        content.Raise();
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void CardFactory_DefersInvocationAndPreservesContext()
    {
        PluginSessionEventVisualContext? captured = null;
        var failure = new InvalidOperationException("literal card result");
        var projection = Projection(context => { captured = context; throw failure; });
        var callback = SessionRuntimeEventCoordinator.CreatePluginVisualFactory(projection);
        Assert.IsNull(captured);
        Assert.IsNotNull(callback);
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => callback()));
        AssertContext(captured!, projection, "card markdown", null);
    }

    [TestMethod]
    public void DetailFactory_DefersInvocationAndPreservesSectionContext()
    {
        PluginSessionEventVisualContext? captured = null;
        var failure = new InvalidOperationException("literal detail result");
        var projection = Projection(NeverRender);
        var section = new PluginTerminalDerivedSessionEventDetailSection
        {
            Header = "Detail header", Markdown = "detail markdown",
            VisualFactory = context => { captured = context; throw failure; }, HeaderVisualFactory = NeverRender,
        };
        var callback = SessionRuntimeEventCoordinator.CreatePluginDetailVisualFactory(projection, section);
        Assert.IsNull(captured);
        Assert.IsNotNull(callback);
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => callback()));
        AssertContext(captured!, projection, section.Markdown, section.Header);
    }

    [TestMethod]
    public void HeaderFactory_DefersInvocationAndPreservesSectionContext()
    {
        PluginSessionEventVisualContext? captured = null;
        var failure = new InvalidOperationException("literal header result");
        var projection = Projection(NeverRender);
        var section = new PluginTerminalDerivedSessionEventDetailSection
        {
            Header = "Header", Markdown = "section markdown", VisualFactory = NeverRender,
            HeaderVisualFactory = context => { captured = context; throw failure; },
        };
        var callback = SessionRuntimeEventCoordinator.CreatePluginDetailHeaderVisualFactory(projection, section);
        Assert.IsNull(captured);
        Assert.IsNotNull(callback);
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => callback()));
        AssertContext(captured!, projection, section.Markdown, section.Header);
    }

    [TestMethod]
    public void FactoryAbsence_ReturnsNullWithoutConstructingContext()
    {
        var projection = Projection(null);
        Assert.IsNull(SessionRuntimeEventCoordinator.CreatePluginVisualFactory(projection));
        foreach (var section in new PluginDerivedSessionEventDetailSection[]
        {
            new() { Header = "Neutral", Markdown = "portable" },
            new PluginTerminalDerivedSessionEventDetailSection { Header = "Native absent", Markdown = "portable" },
        })
        {
            Assert.IsNull(SessionRuntimeEventCoordinator.CreatePluginDetailVisualFactory(projection, section));
            Assert.IsNull(SessionRuntimeEventCoordinator.CreatePluginDetailHeaderVisualFactory(projection, section));
        }
    }

    [TestMethod]
    public void FactoryFailure_PropagatesWithoutFallback()
    {
        var failure = new InvalidOperationException("native failure");
        var calls = 0;
        var projection = Projection(_ => { calls++; throw failure; });
        var callback = SessionRuntimeEventCoordinator.CreatePluginVisualFactory(projection)!;
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => callback()));
        Assert.AreEqual(1, calls);
        // Deliberately violate the native non-null return contract with an inert sentinel. The adapter
        // must forward it, not construct a fallback Visual. Actual visual consumers remain unexecuted.
        var absent = SessionRuntimeEventCoordinator.CreatePluginVisualFactory(Projection(_ => null!))!;
        Assert.IsNull(absent());
    }

    private static PluginTerminalDerivedSessionEvent Event(PluginDynamicDerivedSessionEventContent? dynamic, PluginSessionEventVisualFactory factory) =>
        new() { EventId = "event", Markdown = "explicit portable text", DynamicContent = dynamic, VisualFactory = factory };

    private static PluginTransientEventProjection Projection(PluginSessionEventVisualFactory? factory) =>
        new("event", "card markdown", DateTimeOffset.UnixEpoch, "target", new object(), [], factory, null);

    private static void AssertContext(PluginSessionEventVisualContext context, PluginTransientEventProjection projection, string markdown, string? header)
    {
        Assert.AreEqual(projection.EventId, context.EventId);
        Assert.AreEqual(projection.RenderTarget, context.RenderTarget);
        Assert.AreSame(projection.Payload, context.Payload);
        Assert.AreEqual(markdown, context.Markdown);
        Assert.AreEqual(header, context.DetailHeader);
    }

    private static XenoAtom.Terminal.UI.Visual NeverRender(PluginSessionEventVisualContext context) =>
        throw new AssertFailedException("Native factory must remain deferred.");

    private sealed class DynamicContent : PluginTerminalDynamicDerivedSessionEventContent
    {
        public override string Markdown => "explicit dynamic markdown";
        public void Raise() => NotifyChanged();
    }

    private sealed class NeutralDynamicContent : PluginDynamicDerivedSessionEventContent
    {
        public override string Markdown => "explicit neutral dynamic markdown";
    }

    private sealed class RecordingContent : PluginTerminalDynamicDerivedSessionEventContent
    {
        internal List<string> Reads { get; } = [];
        internal InvalidOperationException Failure { get; } = new("literal getter failure");
        internal string? FailAt { get; set; }
        internal PluginSessionEventVisualFactory? Factory { get; set; }
        private readonly IReadOnlyList<PluginDerivedSessionEventDetailSection> _details =
            [new PluginDerivedSessionEventDetailSection { Header = "Detail", Markdown = "explicit detail markdown" }];
        public override string Markdown { get { Read("markdown"); return "explicit dynamic markdown"; } }
        public override IReadOnlyList<PluginDerivedSessionEventDetailSection> DetailSections { get { Read("details"); return _details; } }
        public override PluginSessionEventVisualFactory? VisualFactory { get { Read("factory"); return Factory; } }
        private void Read(string member) { Reads.Add(member); if (FailAt == member) throw Failure; }
    }
}
