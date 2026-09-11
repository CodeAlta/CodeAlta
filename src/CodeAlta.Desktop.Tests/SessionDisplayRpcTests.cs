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
        var id = new string('"', RuntimeDisplayProjection.MaxIdentifierCharacters);
        var label = new string('\0', RuntimeDisplayProjection.MaxMetadataCharacters);
        publisher.TryPublish(new SessionLifecycleRuntimeEvent(id, DateTimeOffset.UtcNow,
            new SessionLifecycleEvent { SessionId = id, RunId = label, Message = label, Kind = SessionLifecycleEventKind.RunCompleted }));
        publisher.TryPublish(new SessionAgentConfigurationRuntimeEvent(id, DateTimeOffset.UtcNow, label, label, label, AgentReasoningEffort.High, label));
        publisher.TryPublish(new SessionHostEvent(id, DateTimeOffset.UtcNow, AgentSessionUpdateKind.Warning, label));
        publisher.TryPublish(new SessionQueueRuntimeEvent(id, DateTimeOffset.UtcNow, int.MaxValue, null, null, true));
        for (var i = 0; i < RuntimeDisplayProjection.MaxTextItemsPerSession; i++)
            publisher.TryPublish(new SessionAgentEvent(id, new AgentContentCompletedEvent(new ModelProviderId("fake"), id,
                DateTimeOffset.UtcNow, new AgentRunId(id), AgentContentKind.ReasoningSummary, id[..^1] + i, null,
                new string('\0', RuntimeDisplayProjection.MaxTextCharacters + 1))));
        var snapshot = publisher.Display.GetSnapshot() with { Revision = 9007199254740993L, EvictedSessions = long.MaxValue, OmittedSessionEvents = long.MaxValue };
        var item = SessionDisplayService.Project(HostEpoch, id, new(snapshot, false, 9007199254740991L, true));
        Assert.AreEqual("9007199254740993", item.Revision);
        Assert.AreEqual("9007199254740991", item.PreviousRevision);
        Assert.IsTrue(item.Session!.TransportTruncated);
        Assert.IsTrue(item.Session.Text.All(text => text.IsTruncated && text.IsComplete));
        var json = JsonSerializer.SerializeToUtf8Bytes(item, DesktopJsonContext.Default.SessionDisplayItem);
        Console.WriteLine($"Worst-case escaped selected item: {json.Length} bytes; with framing allowance: {json.Length + 4096}; budget: {SessionDisplayService.MaximumItemBytes}.");
        Assert.IsTrue(json.Length + 4096 < SessionDisplayService.MaximumItemBytes, $"Escaped item plus framing allowance: {json.Length + 4096}");
        using var parsed = JsonDocument.Parse(json);
        Assert.AreEqual(JsonValueKind.String, parsed.RootElement.GetProperty("revision").ValueKind);
        Assert.AreEqual(RuntimeDisplayProjection.MaxTextItemsPerSession, parsed.RootElement.GetProperty("session").GetProperty("text").GetArrayLength());
        publisher.Complete();
    }

    [TestMethod]
    public void GeneratedChannel_IsOwnedOnly_AndUsesDisplayWithoutOriginalEventOrStoreReads()
    {
        string Read(string path) => File.ReadAllText(Path.Combine(DesktopArchitectureTests.SourceRoot, "CodeAlta", path));
        var app = Read("Desktop/DesktopApplication.cs");
        var registration = "builder.AddSessionDisplayService(new SessionDisplayService(host.RuntimeService.Display, epoch));";
        Assert.AreEqual(1, app.Split(registration, StringSplitOptions.None).Length - 1);
        var ownedStart = app.IndexOf("private async ValueTask RunOwnedAsync", StringComparison.Ordinal);
        var readOnlyStart = app.IndexOf("private async ValueTask RunAsync", StringComparison.Ordinal);
        Assert.IsTrue(app.IndexOf(registration, StringComparison.Ordinal) > ownedStart);
        Assert.IsTrue(app.IndexOf(registration, StringComparison.Ordinal) < readOnlyStart);
        StringAssert.Contains(app, "MaximumChannelsPerSession = 2, MaximumUnacknowledgedChannelItems = 2");
        var service = Read("Desktop/Rpc/SessionDisplayRpc.cs");
        StringAssert.Contains(service, "display.ObserveAsync(cancellationToken)");
        foreach (var forbidden in new[] { "StreamEventsAsync", "SessionRuntimeEvent", "CodeAltaHost", "File.", "Directory.", "ReadHistory", "AdmitSend", "Exception.Message" })
            Assert.IsFalse(service.Contains(forbidden, StringComparison.Ordinal), forbidden);
        using var manifest = JsonDocument.Parse(Read("obj/neoastra/neoastra.manifest.json"));
        var command = manifest.RootElement.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "display")
            .GetProperty("commands").EnumerateArray().Single();
        Assert.AreEqual("observe", command.GetProperty("name").GetString());
        Assert.IsTrue(command.GetProperty("channel").GetBoolean());
        Assert.AreEqual(typeof(SessionDisplayItem).FullName, command.GetProperty("response").GetString());
        var generated = Read("obj/neoastra/neoastra.ts");
        StringAssert.Contains(generated, "Promise<AsyncIterable<SessionDisplayItem>>");
        StringAssert.Contains(generated, "readonly \"revision\": string | null");
        StringAssert.Contains(Read("frontend/src/main.tsx"), "createSessionDisplayStore(sessionDisplay.observe)");
        StringAssert.Contains(Read("frontend/src/OwnedSessionPanel.tsx"), "<LiveSessionPanel store={display} hostEpoch={epoch} sessionId={sessionId} />");
        StringAssert.Contains(Read("frontend/src/LiveSessionPanel.tsx"), "Reload the Desktop UI before continuing");
        StringAssert.Contains(Read("frontend/src/LiveSessionPanel.tsx"), "disabled={state?.code === \"stale_epoch\"}");
    }

    private static SessionAgentEvent Text(string session, string text) => new(session,
        new AgentContentDeltaEvent(new ModelProviderId("fake"), session, DateTimeOffset.UtcNow, new AgentRunId("run"),
            AgentContentKind.Assistant, "content", null, text));
}
