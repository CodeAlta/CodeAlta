using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The list of sessions is kept in memory once read: a listing reads again from the database only the rows this instance
/// wrote since, and all of them when another writer changed the rows.
/// </summary>
[TestClass]
public sealed class SessionListCopyTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AListing_ReadsAgainOnlyTheRowsWrittenSince_AndAllOfThemAfterAnotherWriter()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        const int Sessions = 30;
        var descriptors = new List<SessionViewDescriptor>();
        for (var index = 0; index < Sessions; index++) descriptors.Add(await CreateSessionAsync(options, $"session-{index:00}"));

        var journal = new SessionViewCatalog(options).JournalStore;
        var cache = (SessionJournalSqliteCache)journal.ProjectionCache;
        var store = journal.CreateSessionStore();

        // The first listing reads every row; the next one, with nothing written, reads none.
        await AssertListedAsFreshAsync(options, store);
        var reads = cache.RowReadCount;
        Assert.IsGreaterThanOrEqualTo(Sessions, reads);
        await AssertListedAsFreshAsync(options, store);
        Assert.AreEqual(reads, cache.RowReadCount, "nothing was written: nothing is read again");

        // A state this instance writes: that row alone is read again, and the listing shows it.
        await journal.AppendStateAsync(descriptors[7], new SessionViewLocalState { ProviderKey = "openai", ModelId = "model-7" });
        reads = cache.RowReadCount;
        var listed = await AssertListedAsFreshAsync(options, store);
        Assert.AreEqual(reads + 1, cache.RowReadCount, "one row was written: one row is read again");
        Assert.AreEqual("model-7", listed.Single(session => session.SessionId == "session-07").ViewState!.ModelId);

        // A removal: the session is gone from the next listing, without reading the others.
        reads = cache.RowReadCount;
        await cache.RemoveSessionAsync("session-03");
        listed = await AssertListedAsFreshAsync(options, store);
        Assert.IsFalse(listed.Any(static session => session.SessionId == "session-03"));
        Assert.IsLessThanOrEqualTo(reads + 1, cache.RowReadCount);

        // Another writer of the same database: this instance does not know what it changed, and reads every row again.
        await new SessionViewCatalog(options).JournalStore.AppendStateAsync(descriptors[12], new SessionViewLocalState { ProviderKey = "openai", ModelId = "model-12" });
        reads = cache.RowReadCount;
        listed = await AssertListedAsFreshAsync(options, store);
        Assert.IsGreaterThanOrEqualTo(reads + Sessions - 1, cache.RowReadCount);
        Assert.AreEqual("model-12", listed.Single(session => session.SessionId == "session-12").ViewState!.ModelId);
    }

    // The listing of the store, compared with the one of a new instance, which reads every row from the database.
    private static async Task<AgentSessionMetadata[]> AssertListedAsFreshAsync(CatalogOptions options, FileSystemAgentSessionStore store)
    {
        var listed = await store.ListSessionsAsync().ToArrayAsync();
        var fresh = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync();
        CollectionAssert.AreEqual(fresh.Select(static session => session.SessionId).ToArray(), listed.Select(static session => session.SessionId).ToArray());
        CollectionAssert.AreEqual(fresh.Select(static session => session.ViewState?.ModelId).ToArray(), listed.Select(static session => session.ViewState?.ModelId).ToArray());
        return listed;
    }

    private static async Task<SessionViewDescriptor> CreateSessionAsync(CatalogOptions options, string sessionId)
    {
        var catalog = new SessionViewCatalog(options);
        var session = new SessionViewDescriptor
        {
            SessionId = sessionId,
            Kind = SessionViewKind.GlobalSession,
            ProviderId = "openai",
            ProviderKey = "openai",
            WorkingDirectory = Path.GetTempPath(),
            Title = sessionId,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
        };
        await catalog.JournalStore.EnsureHeaderAsync(session);
        await catalog.JournalStore.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = new ModelProviderId("openai"),
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ModelId = "model-1",
            WorkingDirectory = Path.GetTempPath(),
            Title = sessionId,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
        });
        await catalog.JournalStore.AppendStateAsync(session, new SessionViewLocalState { ProviderKey = "openai", ModelId = "model-1" });
        return session;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-list-copy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
