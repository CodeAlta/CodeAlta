using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

// Actual generated-channel service, entirely in-memory. No host/profile/provider/native construction.
[TestClass]
public sealed class SessionDisplayRpcTests
{
    private const string HostEpoch = "00000000-0000-0000-0000-000000000001";

    [TestMethod]
    public async Task InvalidRequests_AreRejectedBeforeRuntimeObservation_EvenAtCapacity()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        var service = new SessionDisplayService(publisher.Display, HostEpoch);
        var holders = new List<IAsyncEnumerator<RuntimeDisplayReplacement>>();
        try
        {
            for (var i = 0; i < RuntimeDisplayProjection.MaxSubscribers; i++)
            {
                var holder = publisher.Display.ObserveAsync().GetAsyncEnumerator();
                holders.Add(holder);
                Assert.IsTrue(await holder.MoveNextAsync());
            }
            foreach (var request in new SessionDisplayRequest?[] { null, new(HostEpoch, ""), new(HostEpoch, " padded "),
                new(HostEpoch, new string('x', 257)), new(HostEpoch, "\ud800") })
            {
                await using var channel = service.Observe(request!, default);
                await using var reader = channel.Items.GetAsyncEnumerator();
                Assert.IsTrue(await reader.MoveNextAsync());
                Assert.AreEqual("invalid_request", reader.Current.Status);
                Assert.IsNull(reader.Current.SessionId);
                Assert.IsFalse(await reader.MoveNextAsync());
            }
            await using var stale = service.Observe(new("old", "session"), default);
            await using var staleReader = stale.Items.GetAsyncEnumerator();
            Assert.IsTrue(await staleReader.MoveNextAsync());
            Assert.AreEqual("stale_epoch", staleReader.Current.Status);
            await using var full = service.Observe(new(HostEpoch, "session"), default);
            await using var fullReader = full.Items.GetAsyncEnumerator();
            Assert.IsTrue(await fullReader.MoveNextAsync());
            Assert.AreEqual("capacity", fullReader.Current.Status);
            Assert.IsFalse(await fullReader.MoveNextAsync());
            Assert.AreEqual(RuntimeDisplayProjection.MaxSubscribers, publisher.Display.SubscriberCount);
        }
        finally
        {
            foreach (var holder in holders) await holder.DisposeAsync();
            publisher.Complete();
        }
    }

    [TestMethod]
    public async Task SelectedSessionOnly_AbsenceReplacementGapAndClosure_AreExplicit()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        var service = new SessionDisplayService(publisher.Display, HostEpoch);
        await using var channel = service.Observe(new(HostEpoch, "selected"), default);
        await using var reader = channel.Items.GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.IsTrue(reader.Current.IsInitial);
        Assert.IsNull(reader.Current.Session);
        Assert.IsTrue(reader.Current.IsPartial);
        Assert.AreEqual("0", reader.Current.Revision);
        publisher.TryPublish(Text("other", "private other session"));
        publisher.TryPublish(Text("selected", "selected text"));
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.IsTrue(reader.Current.HasGap);
        Assert.AreEqual("0", reader.Current.PreviousRevision);
        Assert.AreEqual("selected text", reader.Current.Session!.Text.Single().Text);
        Assert.IsFalse(JsonSerializer.Serialize(reader.Current, channel.ItemTypeInfo).Contains("private other session", StringComparison.Ordinal));
        var captured = reader.Current;
        for (var i = 0; i < RuntimeDisplayProjection.MaxSessions; i++) publisher.TryPublish(Text(i.ToString(), "evict"));
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.IsNull(reader.Current.Session);
        Assert.AreNotEqual("0", reader.Current.EvictedSessions);
        Assert.AreEqual("selected text", captured.Session.Text.Single().Text);
        publisher.Complete();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.IsTrue(reader.Current.IsClosed);
        Assert.IsNull(reader.Current.Session);
        Assert.AreEqual(HostEpoch, reader.Current.HostEpoch);
        Assert.AreEqual(publisher.Display.GetSnapshot().Epoch.ToString("D"), reader.Current.ProjectionEpoch);
        Assert.IsFalse(await reader.MoveNextAsync());
        Assert.AreEqual(0, publisher.Display.SubscriberCount);
        await using var late = service.Observe(new(HostEpoch, "selected"), default);
        await using var lateReader = late.Items.GetAsyncEnumerator();
        Assert.IsTrue(await lateReader.MoveNextAsync());
        Assert.IsTrue(lateReader.Current.IsInitial && lateReader.Current.IsClosed);
        Assert.IsFalse(await lateReader.MoveNextAsync());
    }

    [TestMethod]
    public async Task EventsOfOtherSessions_DoNotReachTheReaderOfASession()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        var service = new SessionDisplayService(publisher.Display, HostEpoch, TimeSpan.Zero);
        await using var channel = service.Observe(new(HostEpoch, "selected"), default);
        await using var reader = channel.Items.GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        publisher.TryPublish(Text("selected", "one"));
        Assert.IsTrue(await reader.MoveNextAsync());
        var shown = reader.Current;
        Assert.AreEqual("one", shown.Session!.Text.Single().Text);
        // Another session streams: each of its events is a revision of the projection, and none is one of this session.
        for (var i = 0; i < 40; i++)
        {
            publisher.TryPublish(Text("other", "x"));
            await Task.Yield();
        }
        var pending = reader.MoveNextAsync().AsTask();
        await Task.Delay(100);
        Assert.IsFalse(pending.IsCompleted);
        publisher.TryPublish(Text("selected", " two"));
        Assert.IsTrue(await pending);
        Assert.AreEqual("one two", reader.Current.Session!.Text.Single().Text);
        // The reader is told what it last got, and that revisions lie between.
        Assert.AreEqual(shown.Revision, reader.Current.PreviousRevision);
        Assert.IsTrue(reader.Current.HasGap);
        Assert.AreEqual("42", reader.Current.Revision);
        publisher.Complete();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.IsTrue(reader.Current.IsClosed);
    }

    [TestMethod]
    public async Task Replacements_AreSpaced_AndTheNextOneHoldsWhatWasCommittedMeanwhile()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        var interval = TimeSpan.FromMilliseconds(300);
        var service = new SessionDisplayService(publisher.Display, HostEpoch, interval);
        await using var channel = service.Observe(new(HostEpoch, "selected"), default);
        await using var reader = channel.Items.GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        publisher.TryPublish(Text("selected", "a"));
        Assert.IsTrue(await reader.MoveNextAsync());
        var sent = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var delta in new[] { "b", "c", "d" }) publisher.TryPublish(Text("selected", delta));
        Assert.IsTrue(await reader.MoveNextAsync());
        // One replacement for the three events, and not sooner than the interval allows (the timer is coarse).
        Assert.IsTrue(System.Diagnostics.Stopwatch.GetElapsedTime(sent) >= interval - TimeSpan.FromMilliseconds(60));
        Assert.AreEqual("abcd", reader.Current.Session!.Text.Single().Text);
        publisher.Complete();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.IsTrue(reader.Current.IsClosed);
        Assert.IsFalse(await reader.MoveNextAsync());
    }

    [TestMethod]
    public async Task CancellationAndEnumeratorReturn_ReleaseOnlyObservation()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        var service = new SessionDisplayService(publisher.Display, HostEpoch);
        using var cancellation = new CancellationTokenSource();
        await using var channel = service.Observe(new(HostEpoch, "session"), cancellation.Token);
        await using var reader = channel.Items.GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(1, publisher.Display.SubscriberCount);
        cancellation.Cancel();
        Assert.AreEqual(0, publisher.Display.SubscriberCount);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
        await using var second = service.Observe(new(HostEpoch, "session"), default);
        var secondReader = second.Items.GetAsyncEnumerator();
        Assert.IsTrue(await secondReader.MoveNextAsync());
        await secondReader.DisposeAsync();
        Assert.AreEqual(0, publisher.Display.SubscriberCount);
        Assert.IsFalse(publisher.Display.GetSnapshot().IsClosed);
        Assert.IsTrue(publisher.TryPublish(Text("session", "still running")));
        publisher.Complete();
    }

    [TestMethod]
    public void MaximumEscapedSelectedPayload_FitsBudget_AndInt64RevisionsAreStrings()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        try
        {
            var id = new string('"', RuntimeDisplayProjection.MaxIdentifierCharacters);
            var label = new string('\0', RuntimeDisplayProjection.MaxMetadataCharacters);
            publisher.TryPublish(new SessionLifecycleRuntimeEvent(id, DateTimeOffset.UtcNow,
                new SessionLifecycleEvent { SessionId = id, RunId = label, Message = label,
                    Kind = Enum.GetValues<SessionLifecycleEventKind>().MaxBy(value => value.ToString().Length) }));
            publisher.TryPublish(new SessionAgentConfigurationRuntimeEvent(id, DateTimeOffset.UtcNow, label, label, label,
                Enum.GetValues<AgentReasoningEffort>().Append((AgentReasoningEffort)int.MinValue).MaxBy(value => value.ToString().Length), label)); // Existing numeric enum fallback is longer.
            publisher.TryPublish(new SessionHostEvent(id, DateTimeOffset.UtcNow,
                Enum.GetValues<AgentSessionUpdateKind>().MaxBy(value => value.ToString().Length), label));
            publisher.TryPublish(new SessionQueueRuntimeEvent(id, DateTimeOffset.UtcNow, int.MinValue, null, null, true)); // Longest typed Int32, conservatively.
            for (var i = 0; i < RuntimeDisplayProjection.MaxTextItemsPerSession; i++)
                publisher.TryPublish(new SessionAgentEvent(id, new AgentContentCompletedEvent(new ModelProviderId("fake"), id,
                    DateTimeOffset.UtcNow, new AgentRunId(id), AgentContentKind.ReasoningSummary, id[..^1] + (char)(0x80 + i), null,
                    new string('\0', RuntimeDisplayProjection.MaxTextCharacters + 1))));
            for (var i = 0; i < RuntimeDisplayProjection.MaxToolActivitiesPerSession; i++)
                publisher.TryPublish(new SessionAgentEvent(id, new AgentActivityEvent(new ModelProviderId(id), id,
                    DateTimeOffset.UtcNow, new AgentRunId(id), AgentActivityKind.ToolCall, AgentActivityPhase.Progressed,
                    id[..^1] + (char)(0x80 + i), null, new string('\0', RuntimeDisplayProjection.MaxToolNameCharacters), null)));
            var snapshot = publisher.Display.GetSnapshot() with { Revision = long.MaxValue, EvictedSessions = long.MaxValue, OmittedSessionEvents = long.MaxValue };
            snapshot = snapshot with { Sessions = [snapshot.Sessions.Single() with { Revision = long.MaxValue,
                EvictedTextItems = long.MaxValue, UnsupportedEvents = long.MaxValue, EvictedToolActivities = long.MaxValue }] };
            var item = SessionDisplayService.Project(HostEpoch, id, new(snapshot, false, long.MaxValue - 1, false));
            Assert.AreEqual("9223372036854775807", item.Revision);
            Assert.AreEqual("9223372036854775806", item.PreviousRevision);
            Assert.IsTrue(item.Session!.TransportTruncated);
            Assert.IsTrue(item.Session.Text.All(text => text.IsTruncated && text.IsComplete));
            Assert.IsTrue(item.Session.Text.All(text => text.Text.Length == 4096 && text.ContentId.Length == 256 && text.RunId!.Length == 256));
            Assert.AreEqual(2, item.Session.ToolActivities.Length);
            Assert.IsTrue(item.Session.ToolActivities.All(activity => activity.ProviderId.Length == 256 && activity.RunId!.Length == 256 &&
                activity.ActivityId.Length == 256 && activity.Name!.Length == 128 && activity.Phase == "Progressed"));
            // Maximize even the boolean encodings (false is longer), without reducing any retained string/counter.
            item = item with { Session = item.Session with { MetadataTruncated = false, TransportTruncated = false,
                Text = item.Session.Text.Select(text => text with { IsComplete = false, IsTruncated = false, StartedWithDelta = false }).ToArray() } };
            var json = JsonSerializer.SerializeToUtf8Bytes(item, DesktopJsonContext.Default.SessionDisplayItem);
            Console.WriteLine($"Worst-case escaped selected item: {json.Length} bytes; with framing allowance: {json.Length + 4096}; budget: {SessionDisplayService.MaximumItemBytes}.");
            Assert.IsTrue(json.Length + 4096 < SessionDisplayService.MaximumItemBytes, $"Escaped item plus framing allowance: {json.Length + 4096}");
            using var parsed = JsonDocument.Parse(json);
            Assert.AreEqual(JsonValueKind.String, parsed.RootElement.GetProperty("revision").ValueKind);
            foreach (var field in new[] { "revision", "evictedSessions", "omittedSessionEvents" })
                Assert.AreEqual("9223372036854775807", parsed.RootElement.GetProperty(field).GetString());
            Assert.AreEqual("9223372036854775806", parsed.RootElement.GetProperty("previousRevision").GetString());
            foreach (var field in new[] { "revision", "evictedTextItems", "unsupportedEvents", "evictedToolActivities" })
                Assert.AreEqual("9223372036854775807", parsed.RootElement.GetProperty("session").GetProperty(field).GetString());
            Assert.AreEqual(RuntimeDisplayProjection.MaxTextItemsPerSession, parsed.RootElement.GetProperty("session").GetProperty("text").GetArrayLength());
            Assert.AreEqual(2, parsed.RootElement.GetProperty("session").GetProperty("toolActivities").GetArrayLength());
            Assert.AreEqual(JsonValueKind.String, parsed.RootElement.GetProperty("session").GetProperty("evictedToolActivities").ValueKind);
            Assert.AreEqual("9223372036854775807", parsed.RootElement.GetProperty("session").GetProperty("evictedToolActivities").GetString());
        }
        finally { publisher.Complete(); }
    }

    [TestMethod]
    public void Project_MapsOnlySelectedReportedToolWindow_AndReplacesAbsence()
    {
        var display = new RuntimeDisplayProjection();
        foreach (var session in new[] { "selected", "other" })
        {
            display.Commit(new SessionAgentEvent(session, new AgentActivityEvent(new ModelProviderId("Provider"), session,
                DateTimeOffset.UnixEpoch, null, AgentActivityKind.ToolCall, AgentActivityPhase.Completed, "same", null, null, "PRIVATE")));
            display.Commit(new SessionAgentEvent(session, new AgentActivityEvent(new ModelProviderId("provider"), session,
                DateTimeOffset.UnixEpoch, new AgentRunId("run"), AgentActivityKind.ToolCall, AgentActivityPhase.Started, "same", null,
                new string('x', 129), "PRIVATE")));
        }
        var snapshot = display.GetSnapshot();
        var item = SessionDisplayService.Project(HostEpoch, "SELECTED", new(snapshot, true, null, false));
        Assert.AreEqual("SELECTED", item.SessionId);
        Assert.AreEqual("selected", item.Session!.SessionId);
        Assert.AreEqual(2, item.Session.ToolActivities.Length);
        // Each activity keeps the timestamp and per-session publication order of its first retained source event.
        Assert.AreEqual(new SessionDisplayToolActivity("Provider", null, "same", "Completed", null, false)
            { Timestamp = DateTimeOffset.UnixEpoch, Sequence = "1" }, item.Session.ToolActivities[0]);
        Assert.AreEqual(new SessionDisplayToolActivity("provider", "run", "same", "Started", new string('x', 128), true)
            { Timestamp = DateTimeOffset.UnixEpoch, Sequence = "2" }, item.Session.ToolActivities[1]);
        Assert.AreEqual("0", item.Session.EvictedToolActivities);
        Assert.IsTrue(item.IsPartial);
        var absent = SessionDisplayService.Project(HostEpoch, "SELECTED", new(snapshot with { Sessions = [], IsClosed = true }, false, 0, true));
        Assert.IsNull(absent.Session);
        Assert.IsTrue(absent.HasGap && absent.IsClosed && absent.IsPartial);
        Assert.AreEqual(2, item.Session.ToolActivities.Length); // No mutation of the earlier replacement.
    }

    private static SessionAgentEvent Text(string session, string text) => new(session,
        new AgentContentDeltaEvent(new ModelProviderId("fake"), session, DateTimeOffset.UtcNow, new AgentRunId("run"),
            AgentContentKind.Assistant, "content", null, text));
}
