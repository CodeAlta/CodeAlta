using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class FileSystemAgentSessionStoreTests
{
    [TestMethod]
    public async Task UpsertSessionAndStateAsync_PersistsSessionJournalSnapshotsAndListsMostRecentFirst()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new AgentRuntimePathLayout(temp.Path);
        var store = new FileSystemAgentSessionStore(layout);

        var olderSession = CreateSession("session-1", createdAt: "2026-04-05T10:00:00+00:00", updatedAt: "2026-04-05T11:00:00+00:00");
        var newerSession = CreateSession("session-2", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T11:00:00+00:00");
        await store.UpsertSessionAsync(olderSession).ConfigureAwait(false);
        await store.UpsertSessionAsync(newerSession).ConfigureAwait(false);

        var state = new AgentSessionState
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
        var sessions = await store.ListSessionSummariesAsync("openai", "openai").ToArrayAsync().ConfigureAwait(false);

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
        var layout = new AgentRuntimePathLayout(temp.Path);
        var store = new FileSystemAgentSessionStore(layout);
        var session = CreateSession("session-events", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T10:00:00+00:00");

        await store.UpsertSessionAsync(session).ConfigureAwait(false);

        AgentEvent[] events =
        [
            new AgentSessionUpdateEvent(
                ModelProviderIds.OpenAIResponses,
                session.SessionId,
                DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
                new AgentRunId("run_1"),
                AgentSessionUpdateKind.Started,
                "Session started"),
            new AgentContentCompletedEvent(
                ModelProviderIds.OpenAIResponses,
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
        var layout = new AgentRuntimePathLayout(temp.Path);
        var store = new FileSystemAgentSessionStore(layout);
        var session = CreateSession("session-truncated", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T10:00:00+00:00");

        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        await store.AppendEventsAsync(
                "openai",
                "openai",
                session.SessionId,
                [
                    new AgentSessionUpdateEvent(
                        ModelProviderIds.OpenAIResponses,
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
        var layout = new AgentRuntimePathLayout(temp.Path);
        var firstStore = new FileSystemAgentSessionStore(layout);
        var secondStore = new FileSystemAgentSessionStore(layout);
        var session = CreateSession("session-cache", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T10:00:00+00:00");

        await firstStore.UpsertSessionAsync(session).ConfigureAwait(false);
        _ = await firstStore.ListSessionSummariesAsync("openai", "openai").ToArrayAsync().ConfigureAwait(false);

        await secondStore.UpsertStateAsync(
                new AgentSessionState
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

    [TestMethod]
    public async Task UpsertStateAsync_WaitsForJournalPathLockBeforeReadingMetadata()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new AgentRuntimePathLayout(temp.Path);
        var journalFile = new AgentSessionJournalFile();
        var store = new FileSystemAgentSessionStore(layout, journalFile);
        var session = CreateSession("session-locked-read", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T10:00:00+00:00");
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        var sessionFile = layout.GetSessionFilePath(session.SessionId, session.CreatedAt);
        var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lockTask = journalFile.WithPathLockAsync(
            sessionFile,
            async () =>
            {
                lockEntered.SetResult();
                await releaseLock.Task.ConfigureAwait(false);
            },
            CancellationToken.None);
        await lockEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        var upsertTask = store.UpsertStateAsync(CreateState(session, "resp_after_lock"));

        await Task.Delay(100).ConfigureAwait(false);
        Assert.IsFalse(upsertTask.IsCompleted);

        releaseLock.SetResult();
        await Task.WhenAll(lockTask, upsertTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        var persistedState = await store.GetStateAsync(session.ProtocolFamily, session.ProviderKey, session.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.AreEqual("resp_after_lock", persistedState.ProviderSessionId);
    }

    [TestMethod]
    public async Task AppendEventsAsync_DoesNotWaitForDifferentSessionPathLock()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new AgentRuntimePathLayout(temp.Path);
        var journalFile = new AgentSessionJournalFile();
        var store = new FileSystemAgentSessionStore(layout, journalFile);
        var lockedSession = CreateSession("session-locked-append", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T10:00:00+00:00");
        var otherSession = CreateSession("session-independent-append", createdAt: "2026-04-06T10:05:00+00:00", updatedAt: "2026-04-06T10:05:00+00:00");
        await store.UpsertSessionAsync(lockedSession).ConfigureAwait(false);
        await store.UpsertSessionAsync(otherSession).ConfigureAwait(false);
        var lockedSessionFile = layout.GetSessionFilePath(lockedSession.SessionId, lockedSession.CreatedAt);
        var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lockTask = journalFile.WithPathLockAsync(
            lockedSessionFile,
            async () =>
            {
                lockEntered.SetResult();
                await releaseLock.Task.ConfigureAwait(false);
            },
            CancellationToken.None);
        await lockEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        await store.AppendEventsAsync(
                otherSession.ProtocolFamily,
                otherSession.ProviderKey,
                otherSession.SessionId,
                [
                    new AgentSessionUpdateEvent(
                        ModelProviderIds.OpenAIResponses,
                        otherSession.SessionId,
                        DateTimeOffset.Parse("2026-04-06T10:05:01+00:00"),
                        new AgentRunId("run_1"),
                        AgentSessionUpdateKind.Started,
                        "Session started"),
                ])
            .WaitAsync(TimeSpan.FromSeconds(1))
            .ConfigureAwait(false);

        releaseLock.SetResult();
        await lockTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        var persistedEvents = await store.ReadEventsAsync(otherSession.ProtocolFamily, otherSession.ProviderKey, otherSession.SessionId).ConfigureAwait(false);

        Assert.AreEqual(1, persistedEvents.Count);
    }

    [TestMethod]
    public async Task UpsertStateAsync_RetriesWhenSessionFileIsTemporarilyLocked()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new AgentRuntimePathLayout(temp.Path);
        var store = new FileSystemAgentSessionStore(layout);
        var session = CreateSession("session-sharing-violation", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T10:00:00+00:00");
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        var sessionFile = layout.GetSessionFilePath(session.SessionId, session.CreatedAt);

        await using var exclusiveStream = new FileStream(sessionFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 4096, useAsync: true);
        var upsertTask = store.UpsertStateAsync(CreateState(session, "resp_after_retry"));

        await Task.Delay(100).ConfigureAwait(false);
        Assert.IsFalse(upsertTask.IsCompleted);

        await exclusiveStream.DisposeAsync().ConfigureAwait(false);
        await upsertTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        var persistedState = await store.GetStateAsync(session.ProtocolFamily, session.ProviderKey, session.SessionId).ConfigureAwait(false);
        Assert.IsNotNull(persistedState);
        Assert.AreEqual("resp_after_retry", persistedState.ProviderSessionId);
    }

    [TestMethod]
    public async Task ListSessionsAsync_UsesMetadataProbeWithoutParsingWholeJournal()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new AgentRuntimePathLayout(temp.Path);
        var store = new FileSystemAgentSessionStore(layout);
        var session = CreateSession("session-metadata-probe", createdAt: "2026-04-06T10:00:00+00:00", updatedAt: "2026-04-06T10:00:00+00:00");
        var sessionFile = layout.GetSessionFilePath(session.SessionId, session.CreatedAt);
        Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);
        await File.WriteAllTextAsync(
                sessionFile,
                $"{{\"$type\":\"raw\",\"backendEventType\":\"codealta.sessionHeader\",\"raw\":{{\"session_id\":\"{session.SessionId}\"}}}}{Environment.NewLine}")
            .ConfigureAwait(false);
        await store.UpsertSessionAsync(session).ConfigureAwait(false);

        var padding = new string('x', 300 * 1024);
        await File.AppendAllTextAsync(
                sessionFile,
                $"{{not-json}}{Environment.NewLine}{{\"$type\":\"raw\",\"backendEventType\":\"padding\",\"raw\":{{\"value\":\"{padding}\"}}}}{Environment.NewLine}")
            .ConfigureAwait(false);

        var updatedSession = session with { UpdatedAt = DateTimeOffset.Parse("2026-04-06T12:00:00+00:00") };
        await store.UpsertSessionAsync(updatedSession).ConfigureAwait(false);

        var sessions = await store.ListSessionSummariesAsync("openai", "openai").ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(1, sessions.Length);
        Assert.AreEqual("session-metadata-probe", sessions[0].SessionId);
        Assert.AreEqual(DateTimeOffset.Parse("2026-04-06T12:00:00+00:00"), sessions[0].UpdatedAt);
    }

    private static AgentSessionSummary CreateSession(string sessionId, string createdAt, string updatedAt)
    {
        using var metadata = JsonDocument.Parse("""{"profile":"default"}""");
        return new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = ModelProviderIds.OpenAIResponses,
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

    private static AgentSessionState CreateState(AgentSessionSummary session, string providerSessionId)
        => new()
        {
            SessionId = session.SessionId,
            ProtocolFamily = session.ProtocolFamily,
            ProviderKey = session.ProviderKey,
            ProviderSessionId = providerSessionId,
            UpdatedAt = session.UpdatedAt.AddMinutes(1),
        };
}
