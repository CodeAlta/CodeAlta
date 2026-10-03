using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

// In-memory publication/observation fixtures plus a named-checkout source guard; no host, profiles or providers.
[TestClass]
public sealed class RuntimeDisplayProjectionTests
{
    [TestMethod]
    public async Task Publication_CommitsBeforeOriginalDelivery_AndSurvivesLoss()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        var original = Text("one");
        Assert.IsTrue(publisher.TryPublish(original));
        await using var events = publisher.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.AreSame(original, events.Current);
        Assert.AreEqual("one", publisher.Display.GetSnapshot().Sessions.Single().Text.Single().Text);
        publisher.TryPublish(Text("two"));
        Assert.IsFalse(publisher.TryPublish(Text("three")));
        Assert.AreEqual(1L, publisher.DroppedCount);
        Assert.AreEqual("onetwothree", publisher.Display.GetSnapshot().Sessions.Single().Text.Single().Text);
        publisher.Complete();
    }

    [TestMethod]
    public async Task SubscribePublicationRace_HasAtomicBaselineOrReplacement()
    {
        for (var i = 0; i < 100; i++)
        {
            var publisher = new SessionRuntimeEventPublisher(1);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var observer = publisher.Display.ObserveAsync(timeout.Token).GetAsyncEnumerator();
            var admission = Task.Run(() => publisher.TryPublish(Text("admitted")));
            Assert.IsTrue(await observer.MoveNextAsync());
            var initial = observer.Current;
            Assert.IsTrue(initial.IsInitial);
            await admission;
            if (initial.Snapshot.Revision == 0)
            {
                Assert.IsTrue(await observer.MoveNextAsync());
                Assert.AreEqual(0L, observer.Current.PreviousRevision);
            }
            Assert.AreEqual(1L, observer.Current.Snapshot.Revision);
            Assert.AreEqual("admitted", observer.Current.Snapshot.Sessions.Single().Text.Single().Text);
            publisher.Complete();
        }
    }

    [TestMethod]
    public async Task SlowObserver_GetsExplicitGapAndLatestTerminalReplacement()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        await using var observer = publisher.Display.ObserveAsync().GetAsyncEnumerator();
        Assert.IsTrue(await observer.MoveNextAsync());
        for (var i = 0; i < 1000; i++) publisher.TryPublish(Text("x"));
        publisher.TryPublish(new SessionLifecycleRuntimeEvent("session", DateTimeOffset.UtcNow,
            new SessionLifecycleEvent { SessionId = "session", Kind = SessionLifecycleEventKind.RunCompleted, RunId = "run" }));
        publisher.Complete();
        Assert.AreEqual(0, publisher.Display.SubscriberCount);
        Assert.IsTrue(await observer.MoveNextAsync());
        Assert.IsTrue(observer.Current.HasGap);
        Assert.IsTrue(observer.Current.Snapshot.IsClosed);
        Assert.AreEqual(SessionLifecycleEventKind.RunCompleted, observer.Current.Snapshot.Sessions.Single().Lifecycle!.Value.Kind);
        Assert.IsFalse(await observer.MoveNextAsync());
        Assert.IsFalse(publisher.TryPublish(Text("late")));
        Assert.AreEqual(1002L, publisher.Display.GetSnapshot().Revision);
        await using var late = publisher.Display.ObserveAsync().GetAsyncEnumerator();
        Assert.IsTrue(await late.MoveNextAsync());
        Assert.IsTrue(late.Current.IsInitial);
        Assert.IsTrue(late.Current.Snapshot.IsClosed);
        Assert.IsFalse(await late.MoveNextAsync());
    }

    [TestMethod]
    public async Task CancellationAndDisposal_ReleaseEvenSuspendedSubscriptions()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        using var cancellation = new CancellationTokenSource();
        await using var observer = publisher.Display.ObserveAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await observer.MoveNextAsync());
        Assert.AreEqual(1, publisher.Display.SubscriberCount);
        cancellation.Cancel();
        Assert.AreEqual(0, publisher.Display.SubscriberCount);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await observer.MoveNextAsync());
        var detached = publisher.Display.ObserveAsync().GetAsyncEnumerator();
        Assert.IsTrue(await detached.MoveNextAsync());
        await detached.DisposeAsync();
        Assert.AreEqual(0, publisher.Display.SubscriberCount);
        publisher.Complete();
    }

    [TestMethod]
    public async Task SlowSubscription_DoesNotDelayAnother_AndPendingCancellationReleases()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        using var cancellation = new CancellationTokenSource();
        await using var fast = publisher.Display.ObserveAsync(cancellation.Token).GetAsyncEnumerator();
        await using var slow = publisher.Display.ObserveAsync().GetAsyncEnumerator();
        Assert.IsTrue(await fast.MoveNextAsync());
        Assert.IsTrue(await slow.MoveNextAsync());
        for (var i = 0; i < 20; i++)
        {
            publisher.TryPublish(Text("x", i % 2 == 0 ? "a" : "b"));
            Assert.IsTrue(await fast.MoveNextAsync());
            Assert.IsFalse(fast.Current.HasGap);
            Assert.AreEqual(i + 1L, fast.Current.Snapshot.Revision);
        }
        Assert.IsTrue(await slow.MoveNextAsync());
        Assert.IsTrue(slow.Current.HasGap);
        Assert.AreEqual(fast.Current.Snapshot.Revision, slow.Current.Snapshot.Revision);
        var pending = fast.MoveNextAsync().AsTask();
        Assert.IsFalse(pending.IsCompleted);
        cancellation.Cancel();
        Assert.AreEqual(1, publisher.Display.SubscriberCount);
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        publisher.Complete();
        Assert.AreEqual(0, publisher.Display.SubscriberCount);
    }

    [TestMethod]
    public async Task ParallelSessions_OrderRevisions_AndKeepIndependentImmutableState()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        publisher.TryPublish(Text("before", "a"));
        var before = publisher.Display.GetSnapshot();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(session => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) publisher.TryPublish(Text("x", session.ToString()));
        })));
        var after = publisher.Display.GetSnapshot();
        Assert.AreEqual(801L, after.Revision);
        Assert.AreEqual(before.Epoch, after.Epoch);
        Assert.AreEqual(9, after.Sessions.Length);
        Assert.AreEqual(1, before.Sessions.Length);
        Assert.AreEqual("before", before.Sessions.Single().Text.Single().Text);
        Assert.IsTrue(after.Sessions.Where(s => s.SessionId != "a").All(s => s.Text.Single().Text == new string('x', 100)));
        Assert.AreNotEqual(after.Epoch, new SessionRuntimeEventPublisher(1).Display.GetSnapshot().Epoch);
        publisher.Complete();
    }

    [TestMethod]
    public void Bounds_KeepLatestStatus_AndReportOmissionWithoutRetainingGraphs()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        publisher.TryPublish(new SessionQueueRuntimeEvent("session", DateTimeOffset.UtcNow, 7, null, new string('q', 10000), true));
        publisher.TryPublish(new SessionAgentConfigurationRuntimeEvent("session", DateTimeOffset.UtcNow, "provider", "key", "model", null, "prompt"));
        for (var i = 0; i < RuntimeDisplayProjection.MaxTextItemsPerSession + 1; i++)
            publisher.TryPublish(Text(new string('x', RuntimeDisplayProjection.MaxTextCharacters + 100), contentId: i.ToString()));
        var session = publisher.Display.GetSnapshot().Sessions.Single();
        Assert.AreEqual(7, session.QueuedPromptCount);
        Assert.AreEqual("model", session.Configuration!.Value.ModelId);
        Assert.AreEqual(1L, session.EvictedTextItems);
        Assert.AreEqual(RuntimeDisplayProjection.MaxTextItemsPerSession, session.Text.Length);
        Assert.IsTrue(session.Text.All(item => item.IsTruncated && item.Text.Length == RuntimeDisplayProjection.MaxTextCharacters));
        publisher.TryPublish(Text("not retained", contentId: new string('i', RuntimeDisplayProjection.MaxIdentifierCharacters + 1)));
        Assert.AreEqual(1L, publisher.Display.GetSnapshot().Sessions.Single().UnsupportedEvents);
        publisher.TryPublish(Text("not retained", session: new string('s', RuntimeDisplayProjection.MaxIdentifierCharacters + 1)));
        Assert.AreEqual(1L, publisher.Display.GetSnapshot().OmittedSessionEvents);
        for (var i = 0; i < RuntimeDisplayProjection.MaxSessions; i++) publisher.TryPublish(Text("x", session: i.ToString()));
        var snapshot = publisher.Display.GetSnapshot();
        Assert.AreEqual(RuntimeDisplayProjection.MaxSessions, snapshot.Sessions.Length);
        Assert.AreEqual(1L, snapshot.EvictedSessions);
        publisher.Complete();
    }

    [TestMethod]
    public async Task SubscriberAdmission_IsBounded()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        var observers = new List<IAsyncEnumerator<RuntimeDisplayReplacement>>();
        try
        {
            for (var i = 0; i < RuntimeDisplayProjection.MaxSubscribers; i++)
            {
                var observer = publisher.Display.ObserveAsync().GetAsyncEnumerator();
                observers.Add(observer);
                Assert.IsTrue(await observer.MoveNextAsync());
            }
            await using var excess = publisher.Display.ObserveAsync().GetAsyncEnumerator();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await excess.MoveNextAsync());
        }
        finally
        {
            foreach (var observer in observers) await observer.DisposeAsync();
            publisher.Complete();
        }
        Assert.AreEqual(0, publisher.Display.SubscriberCount);
    }

    [TestMethod]
    public void CompletionReplacesTruncatedPrefix_AndLateDeltaCannotUnfinalize()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        publisher.TryPublish(Text(new string('x', RuntimeDisplayProjection.MaxTextCharacters - 1) + "😀"));
        publisher.TryPublish(Text("not the missing surrogate"));
        var before = publisher.Display.GetSnapshot();
        Assert.AreEqual(RuntimeDisplayProjection.MaxTextCharacters - 1, before.Sessions.Single().Text.Single().Text.Length);
        publisher.TryPublish(new SessionAgentEvent("session", new AgentContentCompletedEvent(new ModelProviderId("fake"),
            "session", DateTimeOffset.UtcNow, new AgentRunId("run"), AgentContentKind.Assistant, "content", null, "final")));
        publisher.TryPublish(Text("late"));
        var after = publisher.Display.GetSnapshot().Sessions.Single();
        Assert.AreEqual("final", after.Text.Single().Text);
        Assert.IsTrue(after.Text.Single().IsComplete);
        Assert.IsFalse(after.Text.Single().IsTruncated);
        Assert.IsFalse(after.Text.Single().StartedWithDelta);
        Assert.AreEqual(1L, after.UnsupportedEvents);
        Assert.IsTrue(before.Sessions.Single().Text.Single().IsTruncated);
        publisher.Complete();
    }

    [TestMethod]
    public void CatalogAndMetadata_AreCopiedAsValues_NotMutableRecordsOrJsonGraphs()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        var descriptor = new CodeAlta.Catalog.SessionViewDescriptor { SessionId = "session", ProviderId = "fake", ModelId = "before" };
        publisher.TryPublish(new SessionCatalogRuntimeEvent("session", DateTimeOffset.UtcNow, descriptor));
        var before = publisher.Display.GetSnapshot();
        descriptor.ModelId = "mutated";
        Assert.AreEqual("before", publisher.Display.GetSnapshot().Sessions.Single().Configuration!.Value.ModelId);
        publisher.TryPublish(new SessionHostEvent("session", DateTimeOffset.UtcNow, AgentSessionUpdateKind.Warning,
            new string('w', RuntimeDisplayProjection.MaxMetadataCharacters + 1)));
        var after = publisher.Display.GetSnapshot().Sessions.Single();
        Assert.IsTrue(after.MetadataTruncated);
        Assert.AreEqual(RuntimeDisplayProjection.MaxMetadataCharacters, after.StatusMessage!.Length);
        Assert.IsFalse(before.Sessions.Single().MetadataTruncated);
        publisher.Complete();
    }

    [TestMethod]
    public void RendererContracts_ContainOnlyImmutableTypedValues()
    {
        // Current architecture guard, not a frozen source/hash reconstruction. Check the entire DTO property closure.
        var pending = new Queue<Type>([typeof(RuntimeDisplayReplacement)]);
        var visited = new HashSet<Type>();
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(type)) continue;
            // DateTimeOffset is the only BCL value besides string/Guid/primitives: an immutable source-event timestamp.
            if (type == typeof(string) || type == typeof(Guid) || type == typeof(DateTimeOffset) || type.IsPrimitive || type.IsEnum) continue;
            if (Nullable.GetUnderlyingType(type) is { } underlying) { pending.Enqueue(underlying); continue; }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Collections.Immutable.ImmutableArray<>))
            { pending.Enqueue(type.GenericTypeArguments[0]); continue; }
            Assert.IsTrue(type.IsValueType && type.IsDefined(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute), false), type.FullName);
            Assert.AreEqual(typeof(RuntimeDisplayReplacement).Namespace, type.Namespace);
            foreach (var property in type.GetProperties()) pending.Enqueue(property.PropertyType);
        }
    }

    [TestMethod]
    public async Task MalformedDisplayFields_AreCountedWithoutBreakingOriginalPublication()
    {
        var publisher = new SessionRuntimeEventPublisher(8);
        publisher.TryPublish(Text("retained"));
        var malformed = Text(null!);
        var lifecycle = new SessionLifecycleRuntimeEvent("session", DateTimeOffset.UtcNow, null!);
        var catalog = new SessionCatalogRuntimeEvent("session", DateTimeOffset.UtcNow, null!);
        Assert.IsTrue(publisher.TryPublish(malformed));
        Assert.IsTrue(publisher.TryPublish(lifecycle));
        Assert.IsTrue(publisher.TryPublish(catalog));
        var snapshot = publisher.Display.GetSnapshot();
        Assert.AreEqual(4L, snapshot.Revision);
        Assert.AreEqual(3L, snapshot.Sessions.Single().UnsupportedEvents);
        Assert.AreEqual("retained", snapshot.Sessions.Single().Text.Single().Text);
        await using var originals = publisher.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await originals.MoveNextAsync());
        Assert.IsTrue(await originals.MoveNextAsync());
        Assert.AreSame(malformed, originals.Current);
        Assert.IsTrue(await originals.MoveNextAsync());
        Assert.AreSame(lifecycle, originals.Current);
        Assert.IsTrue(await originals.MoveNextAsync());
        Assert.AreSame(catalog, originals.Current);
        publisher.Complete();
    }

    [TestMethod]
    public void RunAndContentIdentity_AreIndependent_AndFinalDoesNotReplaceOtherItems()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        publisher.TryPublish(Text("run one", contentId: "shared"));
        publisher.TryPublish(new SessionAgentEvent("session", new AgentContentDeltaEvent(new ModelProviderId("fake"),
            "session", DateTimeOffset.UtcNow, new AgentRunId("second-run"), AgentContentKind.Assistant, "shared", null, "run two")));
        publisher.TryPublish(Text("other item", contentId: "other"));
        publisher.TryPublish(new SessionAgentEvent("session", new AgentContentCompletedEvent(new ModelProviderId("fake"),
            "session", DateTimeOffset.UtcNow, new AgentRunId("run"), AgentContentKind.Assistant, "shared", null, "first final")));
        var items = publisher.Display.GetSnapshot().Sessions.Single().Text;
        Assert.AreEqual(3, items.Length);
        Assert.AreEqual("first final", items.Single(i => i.RunId == "run" && i.ContentId == "shared").Text);
        Assert.AreEqual("run two", items.Single(i => i.RunId == "second-run").Text);
        Assert.AreEqual("other item", items.Single(i => i.ContentId == "other").Text);
        publisher.Complete();
    }

    [TestMethod]
    public async Task MalformedStableUtf16Identities_AreOmittedWithoutChangingOriginalEffects()
    {
        foreach (var malformed in new[] { "\ud800", "\udc00", "x\ud800x", "x\udc00x", "\ud800\ud800", "\udc00\ud800" })
        {
            var publisher = new SessionRuntimeEventPublisher(8);
            SessionRuntimeEvent[] invalid = [Text("bad session", malformed), Text("bad content", contentId: malformed),
                new SessionAgentEvent("session", new AgentContentDeltaEvent(new ModelProviderId("fake"), "session", DateTimeOffset.UtcNow,
                    new AgentRunId(malformed), AgentContentKind.Assistant, "content", null, "bad run"))];
            foreach (var original in invalid) Assert.IsTrue(publisher.TryPublish(original));
            var snapshot = publisher.Display.GetSnapshot();
            Assert.AreEqual(1L, snapshot.OmittedSessionEvents);
            Assert.AreEqual(2L, snapshot.Sessions.Single().UnsupportedEvents);
            Assert.AreEqual(0, snapshot.Sessions.Single().Text.Length);
            await using var reader = publisher.ReadAllAsync().GetAsyncEnumerator();
            foreach (var original in invalid)
            {
                Assert.IsTrue(await reader.MoveNextAsync());
                Assert.AreSame(original, reader.Current);
            }
            publisher.TryPublish(Text("valid pair", "session", "😀"));
            Assert.AreEqual("😀", publisher.Display.GetSnapshot().Sessions.Single().Text.Single().ContentId);
            var pairedIdentity = new string('x', RuntimeDisplayProjection.MaxIdentifierCharacters - 2) + "😀";
            publisher.TryPublish(new SessionAgentEvent(pairedIdentity, new AgentContentDeltaEvent(new ModelProviderId("fake"),
                pairedIdentity, DateTimeOffset.UtcNow, new AgentRunId(pairedIdentity), AgentContentKind.Assistant,
                pairedIdentity, null, "valid paired identities at bound")));
            var paired = publisher.Display.GetSnapshot().Sessions.Single(session => session.SessionId == pairedIdentity).Text.Single();
            Assert.AreEqual(pairedIdentity, paired.RunId);
            Assert.AreEqual(pairedIdentity, paired.ContentId);
            publisher.Complete();
        }
    }

    private static SessionAgentEvent Text(string text, string session = "session", string contentId = "content") => new(session,
        new AgentContentDeltaEvent(new ModelProviderId("fake"), session, DateTimeOffset.UtcNow, new AgentRunId("run"),
            AgentContentKind.Assistant, contentId, null, text));
}
