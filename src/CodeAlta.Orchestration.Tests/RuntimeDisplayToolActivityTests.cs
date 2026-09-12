using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

// Source-first regressions: no runtime host, provider, observation subscription or forwarding owner.
[TestClass]
public sealed class RuntimeDisplayToolActivityTests
{
    [TestMethod]
    public void MatchingUpdates_MoveToNewest_ThirdIdentityEvictsOldest_WithoutMutatingSnapshots()
    {
        var display = new RuntimeDisplayProjection();
        Publish(display, Activity("a", phase: AgentActivityPhase.Completed));
        Publish(display, Activity("b"));
        var captured = display.GetSnapshot();
        Publish(display, Activity("a", phase: AgentActivityPhase.Started)); // Reported phase may regress.
        var updated = Session(display);
        CollectionAssert.AreEqual(new[] { "b", "a" }, updated.ToolActivities.Select(row => row.ActivityId).ToArray());
        Assert.AreEqual(AgentActivityPhase.Started, updated.ToolActivities[1].Phase);
        Assert.AreEqual(0L, updated.EvictedToolActivities);
        Publish(display, Activity("c"));
        var evicted = Session(display);
        CollectionAssert.AreEqual(new[] { "a", "c" }, evicted.ToolActivities.Select(row => row.ActivityId).ToArray());
        Assert.AreEqual(1L, evicted.EvictedToolActivities);
        CollectionAssert.AreEqual(new[] { "a", "b" }, captured.Sessions.Single().ToolActivities.Select(row => row.ActivityId).ToArray());
        Assert.AreEqual(AgentActivityPhase.Completed, captured.Sessions.Single().ToolActivities[0].Phase);
        Assert.AreEqual(0L, captured.Sessions.Single().EvictedToolActivities);
        Assert.AreEqual(0L, updated.EvictedToolActivities);
        display.Complete();
        Assert.AreEqual(evicted.ToolActivities, Session(display).ToolActivities);
    }

    [TestMethod]
    public void Identity_UsesExactProviderNullableRunAndActivity_InExistingSessionContainer()
    {
        foreach (var other in new[] { Activity(provider: "Provider"), Activity(run: "Run"), Activity(run: null),
            Activity("Tool"), Activity(" tool "), Activity(run: " run ") })
        {
            var display = new RuntimeDisplayProjection();
            Publish(display, Activity());
            Publish(display, other);
            Assert.AreEqual(2, Session(display).ToolActivities.Length);
            var second = Session(display).ToolActivities[1];
            Assert.AreEqual(other.ProviderId.Value, second.ProviderId);
            Assert.AreEqual(other.RunId?.Value, second.RunId);
            Assert.AreEqual(other.ActivityId, second.ActivityId);
            Publish(display, other with { Phase = AgentActivityPhase.Failed }, "SESSION");
            Assert.AreEqual(1, display.GetSnapshot().Sessions.Length); // Existing session comparer, unchanged.
            Assert.AreEqual(0L, Session(display).EvictedToolActivities);
            Publish(display, Activity(), "another");
            Assert.AreEqual(2, display.GetSnapshot().Sessions.Length);
            Assert.AreEqual(2, display.GetSnapshot().Sessions.Single(s => s.SessionId == "session").ToolActivities.Length);
        }
        var bounded = new string('x', 254) + "😀";
        var exact = new RuntimeDisplayProjection();
        Publish(exact, Activity(bounded, bounded, bounded));
        var retained = Session(exact).ToolActivities.Single();
        Assert.AreEqual(bounded, retained.ProviderId);
        Assert.AreEqual(bounded, retained.RunId);
        Assert.AreEqual(bounded, retained.ActivityId);
        Publish(exact, Activity("\ufeff", "\ufeff", "\ufeff"));
        Assert.AreEqual("\ufeff", Session(exact).ToolActivities[1].ProviderId); // Not .NET whitespace; no JS trim normalization either.
    }

    [TestMethod]
    public void InvalidIdentitiesKindsAndPhases_OmitWithoutReplacingOrEvictingValidRows()
    {
        var invalid = new List<AgentActivityEvent> { Activity() with { ProviderId = default },
            Activity() with { RunId = default(AgentRunId) } };
        foreach (var value in new[] { "", " \t", new string('x', 257), "\ud800", "\udc00", "x\ud800x", "\ud800\ud800", "\udc00\ud800" })
        {
            invalid.Add(Activity() with { ActivityId = value });
            invalid.Add(Activity() with { RunId = new AgentRunId(value) });
            if (!string.IsNullOrWhiteSpace(value)) invalid.Add(Activity() with { ProviderId = new ModelProviderId(value) });
        }
        invalid.Add(Activity() with { ActivityId = null! });
        foreach (var kind in Enum.GetValues<AgentActivityKind>().Where(kind => kind != AgentActivityKind.ToolCall).Append((AgentActivityKind)int.MaxValue))
            invalid.Add(Activity() with { Kind = kind });
        foreach (var phase in new[] { AgentActivityPhase.Selected, AgentActivityPhase.Deselected, (AgentActivityPhase)(-1), (AgentActivityPhase)int.MaxValue })
            invalid.Add(Activity() with { Phase = phase });
        var display = new RuntimeDisplayProjection();
        Publish(display, Activity());
        Publish(display, Activity("second"));
        var original = Session(display);
        foreach (var activity in invalid) Publish(display, activity);
        var current = Session(display);
        Assert.AreEqual(original.ToolActivities, current.ToolActivities);
        Assert.AreEqual(0L, current.EvictedToolActivities);
        Assert.AreEqual((long)invalid.Count, current.UnsupportedEvents);
        Assert.AreEqual(0L, original.UnsupportedEvents);
        Assert.AreEqual(original.Revision + invalid.Count, current.Revision);
    }

    [TestMethod]
    public void SupportedPhases_AreLatestReports_AndOtherRetainedStateIsUnchanged()
    {
        var display = new RuntimeDisplayProjection();
        display.Commit(new SessionLifecycleRuntimeEvent("session", DateTimeOffset.UnixEpoch,
            new SessionLifecycleEvent { SessionId = "session", Kind = SessionLifecycleEventKind.RunCompleted, RunId = "life", Message = "status" }));
        display.Commit(new SessionQueueRuntimeEvent("session", DateTimeOffset.UnixEpoch, 3, null, null, true));
        display.Commit(new SessionAgentConfigurationRuntimeEvent("session", DateTimeOffset.UnixEpoch, "provider", "key", "model", AgentReasoningEffort.High, "prompt"));
        display.Commit(new SessionHostEvent("session", DateTimeOffset.UnixEpoch, AgentSessionUpdateKind.Warning, new string('x', 513)));
        display.Commit(new SessionAgentEvent("session", new AgentContentDeltaEvent(new ModelProviderId("provider"), "session",
            DateTimeOffset.UnixEpoch, new AgentRunId("text-run"), AgentContentKind.Assistant, "content", null, "text")));
        var before = Session(display);
        foreach (var phase in new[] { AgentActivityPhase.Requested, AgentActivityPhase.Started, AgentActivityPhase.Progressed,
            AgentActivityPhase.Completed, AgentActivityPhase.Failed, AgentActivityPhase.Canceled, AgentActivityPhase.Started })
        {
            Publish(display, Activity(phase: phase, run: null));
            var after = Session(display);
            Assert.AreEqual(phase, after.ToolActivities.Single().Phase);
            Assert.IsNull(after.ToolActivities.Single().RunId);
            Assert.AreEqual(before with { Revision = after.Revision, ToolActivities = after.ToolActivities }, after);
        }
    }

    [TestMethod]
    public void Names_AreOptionalSurrogateSafePrefixes_AndMalformedNamesOmitTheEntireReport()
    {
        var display = new RuntimeDisplayProjection();
        foreach (var name in new string?[] { null, "", "tool", new string('x', 128), new string('x', 126) + "😀", new string('x', 129), new string('x', 127) + "😀" })
        {
            Publish(display, Activity(name: name));
            var row = Session(display).ToolActivities.Single();
            var expectedLength = name is null ? 0 : name.Length <= 128 ? name.Length : name[127] == '\ud83d' ? 127 : 128;
            Assert.AreEqual(name is null ? null : name[..expectedLength], row.Name);
            Assert.AreEqual(name?.Length > 128, row.IsNameTruncated);
        }
        var before = Session(display);
        foreach (var malformed in new[] { "\ud800", "\udc00", "x\ud800x", new string('x', 129) + "\ud800" })
            Publish(display, Activity(name: malformed)); // Validate the whole name, including beyond the prefix.
        var after = Session(display);
        Assert.AreEqual(before.ToolActivities, after.ToolActivities);
        Assert.AreEqual(before.UnsupportedEvents + 4, after.UnsupportedEvents);
        Assert.AreEqual(before.MetadataTruncated, after.MetadataTruncated);
        Assert.AreEqual(0L, after.EvictedToolActivities);
    }

    [TestMethod]
    public async Task OriginalDelivery_PreservesReferences_WithoutInspectingDisposedDetails()
    {
        var document = JsonDocument.Parse("{\"arguments\":{\"secret\":true},\"result\":[],\"workingDirectory\":\"private\"}");
        var details = document.RootElement;
        document.Dispose(); // Any traversal/serialization/clone of Details now throws.
        var publisher = new SessionRuntimeEventPublisher(3);
        SessionRuntimeEvent[] originals = [new SessionAgentEvent("session", Activity() with { Details = details, Message = "SECRET", ParentActivityId = "PRIVATE" }),
            new SessionAgentEvent("session", Activity("invalid") with { Details = details, ProviderId = default }),
            new SessionAgentEvent("session", Activity("unsupported") with { Details = details, Phase = AgentActivityPhase.Selected })];
        try { foreach (var original in originals) Assert.IsTrue(publisher.TryPublish(original)); }
        finally { publisher.Complete(); } // One completed, prebuffered publisher; no producer or subscriber race.
        Assert.AreEqual(1, publisher.Display.GetSnapshot().Sessions.Single().ToolActivities.Length);
        Assert.AreEqual(2L, publisher.Display.GetSnapshot().Sessions.Single().UnsupportedEvents);
        Assert.AreEqual(0L, publisher.DroppedCount);
        var cancellation = new CancellationTokenSource();
        var reader = publisher.ReadAllAsync(cancellation.Token).GetAsyncEnumerator();
        var retained = new List<Task>();
        var uncertain = false;
        async Task JoinAsync(Task original)
        {
            retained.Add(original);
            var observer = ObserveFailureAsync(original);
            retained.Add(observer);
            var bounded = original.WaitAsync(TimeSpan.FromSeconds(5));
            retained.Add(bounded);
            try { await bounded; }
            catch (TimeoutException error)
            {
                uncertain = true; // Permanent, even if original later completes. No disposal races with it.
                cancellation.Cancel();
                error.Data["Retained uncertain display fixture"] = new { reader, cancellation, retained };
                throw;
            }
            finally { if (!uncertain) await observer; }
        }
        try
        {
            foreach (var original in originals)
            {
                var move = reader.MoveNextAsync().AsTask();
                var joined = JoinAsync(move);
                retained.Add(joined);
                await joined;
                Assert.IsTrue(await move);
                Assert.AreSame(original, reader.Current);
            }
            var end = reader.MoveNextAsync().AsTask();
            var endJoin = JoinAsync(end);
            retained.Add(endJoin);
            await endJoin;
            Assert.IsFalse(await end);
        }
        finally
        {
            cancellation.Cancel();
            if (!uncertain)
            {
                try
                {
                    var returned = reader.DisposeAsync().AsTask();
                    var returnJoin = JoinAsync(returned);
                    retained.Add(returnJoin);
                    await returnJoin;
                }
                finally { if (!uncertain) cancellation.Dispose(); }
            }
        }
    }

    private static async Task ObserveFailureAsync(Task original)
    {
        try { await original; } catch { /* Retained observer prevents an unobserved late rejection after a deadline. */ }
    }

    private static RuntimeDisplaySession Session(RuntimeDisplayProjection display) => display.GetSnapshot().Sessions.Single();
    private static void Publish(RuntimeDisplayProjection display, AgentActivityEvent activity, string session = "session")
        => display.Commit(new SessionAgentEvent(session, activity));
    private static AgentActivityEvent Activity(string activity = "tool", string provider = "provider", string? run = "run",
        AgentActivityPhase phase = AgentActivityPhase.Started, string? name = "name")
        => new(new ModelProviderId(provider), "session", DateTimeOffset.UnixEpoch, run is null ? null : new AgentRunId(run),
            AgentActivityKind.ToolCall, phase, activity, null, name, null);
}
