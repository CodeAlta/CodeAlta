using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentSessionCatalogTests
{
    [TestMethod]
    public async Task ListSessionsAsync_ConcurrentCallersShareOneStoreLoad()
    {
        var session = new AgentSessionMetadata(
            "session-shared-load",
            DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
            DateTimeOffset.Parse("2026-04-06T11:00:00+00:00"));
        var store = new BlockingSessionStore([session]);
        var catalog = new AgentSessionCatalog(store);

        var firstTask = Task.Run(async () => await catalog.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false));
        var secondTask = Task.Run(async () => await catalog.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false));

        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);

        Assert.AreEqual(1, store.LoadCount);

        store.ReleaseLoad.SetResult();
        var results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        Assert.AreEqual(1, store.LoadCount);
        CollectionAssert.AreEqual(new[] { "session-shared-load" }, results[0].Select(static item => item.SessionId).ToArray());
        CollectionAssert.AreEqual(new[] { "session-shared-load" }, results[1].Select(static item => item.SessionId).ToArray());
    }

    [TestMethod]
    public async Task ListSessionsAsync_YieldsDiscoveredSessionsBeforeStoreCompletes()
    {
        var first = new AgentSessionMetadata(
            "session-first",
            DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
            DateTimeOffset.Parse("2026-04-06T11:00:00+00:00"));
        var second = new AgentSessionMetadata(
            "session-second",
            DateTimeOffset.Parse("2026-04-06T10:05:00+00:00"),
            DateTimeOffset.Parse("2026-04-06T11:05:00+00:00"));
        var store = new PartiallyBlockingSessionStore(first, second);
        var catalog = new AgentSessionCatalog(store);

        await using var enumerator = catalog.ListSessionsAsync().GetAsyncEnumerator();

        Assert.IsTrue(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
        Assert.AreEqual("session-first", enumerator.Current.SessionId);
        await store.WaitingAfterFirst.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Assert.AreEqual(1, store.LoadCount);

        var secondMove = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(100).ConfigureAwait(false);
        Assert.IsFalse(secondMove.IsCompleted, "The store is still blocked after the first yielded session.");

        store.ReleaseRemaining.SetResult();
        Assert.IsTrue(await secondMove.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
        Assert.AreEqual("session-second", enumerator.Current.SessionId);
    }

    [TestMethod]
    public async Task ListSessionsAsync_StreamsCachedSnapshotUntilInvalidated()
    {
        var store = new BlockingSessionStore(
            [
                new AgentSessionMetadata(
                    "session-1",
                    DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
                    DateTimeOffset.Parse("2026-04-06T11:00:00+00:00")),
            ],
            initiallyReleased: true);
        var catalog = new AgentSessionCatalog(store);

        _ = await catalog.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        _ = await catalog.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(1, store.LoadCount);

        store.Sessions =
        [
            new AgentSessionMetadata(
                "session-2",
                DateTimeOffset.Parse("2026-04-07T10:00:00+00:00"),
                DateTimeOffset.Parse("2026-04-07T11:00:00+00:00")),
        ];
        await catalog.NotifySessionUpdatedAsync("session-1").ConfigureAwait(false);
        var refreshed = await catalog.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(2, store.LoadCount);
        CollectionAssert.AreEqual(new[] { "session-2" }, refreshed.Select(static item => item.SessionId).ToArray());
    }

    [TestMethod]
    public async Task FileSystemStoreAndCatalog_ListSessionsWithoutRegisteredProvider()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new AgentRuntimePathLayout(temp.Path);
        var store = new FileSystemAgentSessionStore(layout);
        await store.UpsertSessionAsync(CreateSession("session-missing-provider", "missing-provider")).ConfigureAwait(false);
        await store.UpsertSessionAsync(CreateSession("session-disabled-provider", "disabled-provider")).ConfigureAwait(false);
        var catalog = new AgentSessionCatalog(store);

        var storeSessions = await ((IAgentSessionStore)store).ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        var catalogSessions = await catalog.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        CollectionAssert.AreEquivalent(
            new[] { "session-missing-provider", "session-disabled-provider" },
            storeSessions.Select(static session => session.SessionId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "session-missing-provider", "session-disabled-provider" },
            catalogSessions.Select(static session => session.SessionId).ToArray());
        Assert.IsTrue(storeSessions.All(static session => session.ProviderKey is "missing-provider" or "disabled-provider"));
    }

    [TestMethod]
    public async Task FileSystemStore_ListSessionsAsync_SkipsCorruptSessionFiles()
    {
        using var temp = TestTempDirectory.Create();
        var layout = new AgentRuntimePathLayout(temp.Path);
        var store = new FileSystemAgentSessionStore(layout);
        await store.UpsertSessionAsync(CreateSession("session-valid", "missing-provider")).ConfigureAwait(false);

        var corruptDirectory = Path.Combine(layout.SessionsRootPath, "corrupt");
        Directory.CreateDirectory(corruptDirectory);
        await File.WriteAllTextAsync(Path.Combine(corruptDirectory, "corrupt.jsonl"), "not-json").ConfigureAwait(false);

        var sessions = await ((IAgentSessionStore)store).ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { "session-valid" }, sessions.Select(static session => session.SessionId).ToArray());
    }

    private static AgentSessionSummary CreateSession(string sessionId, string providerKey)
    {
        var createdAt = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00");
        return new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = new ModelProviderId(providerKey),
            ProtocolFamily = "openai-responses",
            ProviderKey = providerKey,
            ModelId = "gpt-5.4",
            WorkingDirectory = @"C:\repo\sample",
            Title = "Sample session",
            Summary = "Assistant summary",
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddMinutes(5),
        };
    }

    private sealed class BlockingSessionStore : IAgentSessionStore
    {
        public BlockingSessionStore(IReadOnlyList<AgentSessionMetadata> sessions, bool initiallyReleased = false)
        {
            Sessions = sessions;
            if (initiallyReleased)
            {
                ReleaseLoad.SetResult();
            }
        }

        public IReadOnlyList<AgentSessionMetadata> Sessions { get; set; }

        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCount { get; private set; }

        public async IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LoadCount++;
            LoadStarted.TrySetResult();
            await ReleaseLoad.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            foreach (var session in Sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (filter is null || string.IsNullOrWhiteSpace(filter.Cwd) ||
                    string.Equals(session.Context?.Cwd ?? session.WorkspacePath, filter.Cwd, StringComparison.OrdinalIgnoreCase))
                {
                    yield return session;
                }
            }
        }

        public Task<AgentSessionMetadata?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(Sessions.FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<AgentEvent>> ReadEventsAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEvent>>([]);

        public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class PartiallyBlockingSessionStore(
        AgentSessionMetadata first,
        AgentSessionMetadata second) : IAgentSessionStore
    {
        public TaskCompletionSource WaitingAfterFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRemaining { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCount { get; private set; }

        public async IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LoadCount++;
            yield return first;
            WaitingAfterFirst.TrySetResult();
            await ReleaseRemaining.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return second;
        }

        public Task<AgentSessionMetadata?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(
                string.Equals(sessionId, first.SessionId, StringComparison.OrdinalIgnoreCase)
                    ? first
                    : string.Equals(sessionId, second.SessionId, StringComparison.OrdinalIgnoreCase)
                        ? second
                        : null);

        public Task<IReadOnlyList<AgentEvent>> ReadEventsAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEvent>>([]);

        public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
