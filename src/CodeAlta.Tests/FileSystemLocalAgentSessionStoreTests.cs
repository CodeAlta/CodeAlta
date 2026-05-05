using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class FileSystemLocalAgentSessionStoreTests
{
    [TestMethod]
    public async Task UpsertSessionAndStateAsync_PersistsSessionJournalSnapshotsAndListsMostRecentFirst()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new LocalAgentRuntimePathLayout(temp.Path);
        var store = new FileSystemLocalAgentSessionStore(layout);

        var olderSession = CreateSession("session-1", createdAt: "2026-04-05T10:00:00+00:00", updatedAt: "2026-04-05T11:00:00+00:00");
        var newerSession = CreateSession("session-2", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T11:00:00+00:00");
        await store.UpsertSessionAsync(olderSession).ConfigureAwait(false);
        await store.UpsertSessionAsync(newerSession).ConfigureAwait(false);

        var state = new LocalAgentSessionState
        {
            SessionId = newerSession.SessionId,
            ProtocolFamily = newerSession.ProtocolFamily,
            ProviderKey = newerSession.ProviderKey,
            ProviderSessionId = "resp_123",
            InstructionHash = "sha256:abc",
            UpdatedAt = DateTimeOffset.Parse("2026-04-06T11:05:00+00:00"),
        };
        await store.UpsertStateAsync(state).ConfigureAwait(false);

        var persistedSession = await store.GetSessionAsync("openai", "openai", "session-2").ConfigureAwait(false);
        var persistedState = await store.GetStateAsync("openai", "openai", "session-2").ConfigureAwait(false);
        var sessions = await store.ListSessionsAsync("openai", "openai").ConfigureAwait(false);

        Assert.IsNotNull(persistedSession);
        Assert.AreEqual("gpt-5.4", persistedSession.ModelId);
        Assert.IsNotNull(persistedState);
        Assert.AreEqual("resp_123", persistedState.ProviderSessionId);
        CollectionAssert.AreEqual(new[] { "session-2", "session-1" }, sessions.Select(static session => session.SessionId).ToArray());

        var persistedJournal = layout.GetSessionFilePath(newerSession.SessionId, newerSession.CreatedAt);
        Assert.IsTrue(File.Exists(persistedJournal));
        Assert.AreEqual(2, Directory.EnumerateFiles(layout.SessionsRootPath, "*.jsonl", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task AppendEventsAsync_ReadEventsAsync_RoundTripsCanonicalEventLog()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new LocalAgentRuntimePathLayout(temp.Path);
        var store = new FileSystemLocalAgentSessionStore(layout);
        var session = CreateSession("session-events", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T10:00:00+00:00");

        await store.UpsertSessionAsync(session).ConfigureAwait(false);

        AgentEvent[] events =
        [
            new AgentSessionUpdateEvent(
                AgentBackendIds.OpenAIResponses,
                session.SessionId,
                DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
                new AgentRunId("run_1"),
                AgentSessionUpdateKind.Started,
                "Session started"),
            new AgentContentCompletedEvent(
                AgentBackendIds.OpenAIResponses,
                session.SessionId,
                DateTimeOffset.Parse("2026-04-06T10:00:01+00:00"),
                new AgentRunId("run_1"),
                AgentContentKind.Assistant,
                "content_1",
                null,
                "Hello from the assistant."),
        ];

        await store.AppendEventsAsync("openai", "openai", session.SessionId, events).ConfigureAwait(false);
        var persistedEvents = await store.ReadEventsAsync("openai", "openai", session.SessionId).ConfigureAwait(false);

        Assert.AreEqual(2, persistedEvents.Count);
        Assert.IsInstanceOfType<AgentSessionUpdateEvent>(persistedEvents[0]);
        Assert.IsInstanceOfType<AgentContentCompletedEvent>(persistedEvents[1]);
        Assert.AreEqual("Hello from the assistant.", ((AgentContentCompletedEvent)persistedEvents[1]).Content);
    }

    [TestMethod]
    public async Task ReadEventsAsync_IgnoresTruncatedFinalLine()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new LocalAgentRuntimePathLayout(temp.Path);
        var store = new FileSystemLocalAgentSessionStore(layout);
        var session = CreateSession("session-truncated", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T10:00:00+00:00");

        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        await store.AppendEventsAsync(
                "openai",
                "openai",
                session.SessionId,
                [
                    new AgentSessionUpdateEvent(
                        AgentBackendIds.OpenAIResponses,
                        session.SessionId,
                        DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
                        new AgentRunId("run_1"),
                        AgentSessionUpdateKind.Started,
                        "Session started"),
                ])
            .ConfigureAwait(false);

        var sessionFile = layout.GetSessionFilePath(session.SessionId, session.CreatedAt);
        await File.AppendAllTextAsync(sessionFile, """{"$type":"contentCompleted","broken":""").ConfigureAwait(false);

        var persistedEvents = await store.ReadEventsAsync("openai", "openai", session.SessionId).ConfigureAwait(false);

        Assert.AreEqual(1, persistedEvents.Count);
        Assert.IsInstanceOfType<AgentSessionUpdateEvent>(persistedEvents[0]);
    }

    [TestMethod]
    public async Task MetadataReads_RefreshCachedProjectionWhenJournalChanges()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new LocalAgentRuntimePathLayout(temp.Path);
        var firstStore = new FileSystemLocalAgentSessionStore(layout);
        var secondStore = new FileSystemLocalAgentSessionStore(layout);
        var session = CreateSession("session-cache", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T10:00:00+00:00");

        await firstStore.UpsertSessionAsync(session).ConfigureAwait(false);
        _ = await firstStore.ListSessionsAsync("openai", "openai").ConfigureAwait(false);

        await secondStore.UpsertStateAsync(
                new LocalAgentSessionState
                {
                    SessionId = session.SessionId,
                    ProtocolFamily = session.ProtocolFamily,
                    ProviderKey = session.ProviderKey,
                    ProviderSessionId = "resp_cached",
                    UpdatedAt = DateTimeOffset.Parse("2026-04-06T10:01:00+00:00"),
                })
            .ConfigureAwait(false);

        var refreshedState = await firstStore.GetStateAsync("openai", "openai", session.SessionId).ConfigureAwait(false);

        Assert.IsNotNull(refreshedState);
        Assert.AreEqual("resp_cached", refreshedState.ProviderSessionId);
    }

    private static LocalAgentSessionSummary CreateSession(string sessionId, string createdAt, string updatedAt)
    {
        using var metadata = JsonDocument.Parse("""{"profile":"default"}""");
        return new LocalAgentSessionSummary
        {
            SessionId = sessionId,
            BackendId = AgentBackendIds.OpenAIResponses,
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ModelId = "gpt-5.4",
            WorkingDirectory = @"C:\repo\sample",
            Title = "Sample session",
            Summary = "Assistant summary",
            CreatedAt = DateTimeOffset.Parse(createdAt),
            UpdatedAt = DateTimeOffset.Parse(updatedAt),
            Metadata = metadata.RootElement.Clone(),
        };
    }
}
