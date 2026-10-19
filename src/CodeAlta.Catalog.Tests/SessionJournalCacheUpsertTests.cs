using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog.Tests;

// Upserts of the session list that wait for a write are written together; a session removed meanwhile is not written again.
[TestClass]
public sealed class SessionJournalCacheUpsertTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task UpsertSessionAsync_WhileAWriteWaits_GathersTheNextUpserts_AndRemovalDropsTheOneOfItsSession()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = options.ApplicationDatabasePath,
            BusyTimeout = TimeSpan.FromSeconds(30),
        });
        var journal = new SessionViewJournalStore(options, database);
        var store = journal.CreateSessionStore();
        var cache = (SessionJournalSqliteCache)journal.ProjectionCache;
        string[] ids = ["session-first", "session-removed", "session-last"];
        var projections = new Dictionary<string, AgentSessionCacheProjection>();
        foreach (var id in ids)
        {
            var summary = CreateSummary(id);
            await store.UpsertSessionAsync(summary);
            var path = Path.GetFullPath(new AgentRuntimePathLayout(options.StateRoot).GetSessionFilePath(id, CreatedAt));
            var info = new FileInfo(path);
            projections[id] = new AgentSessionCacheProjection(path, new AgentSessionCacheFileStamp(info.LastWriteTimeUtc, info.Length),
                summary with { Title = "pending" }, null);
        }

        Task first, removed, last, removal;
        await using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.ApplicationDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            await holder.OpenAsync();
            await ExecuteAsync(holder, "BEGIN EXCLUSIVE;");

            // The first upsert is written alone and waits for the lock; the next ones wait for that write.
            first = cache.UpsertSessionAsync(projections["session-first"]);
            await WaitUntilAsync(() => cache.IsWritingUpserts && cache.PendingUpsertCount == 0);
            removed = cache.UpsertSessionAsync(projections["session-removed"]);
            last = cache.UpsertSessionAsync(projections["session-last"]);
            await WaitUntilAsync(() => cache.PendingUpsertCount == 2);
            removal = cache.RemoveSessionAsync("session-removed");
            await removed.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, cache.PendingUpsertCount, "The upsert of the removed session no longer waits.");
            Assert.IsFalse(first.IsCompleted, "The lock holds the first write.");

            await ExecuteAsync(holder, "ROLLBACK;");
        }

        await Task.WhenAll(first, last, removal).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual("pending", ReadTitle(options.ApplicationDatabasePath, "session-first"));
        Assert.AreEqual("pending", ReadTitle(options.ApplicationDatabasePath, "session-last"));
        Assert.IsNull(ReadTitle(options.ApplicationDatabasePath, "session-removed"), "The removed session is not written again.");
    }

    [TestMethod]
    public async Task UpsertSessionAsync_SameSessionWhileAWriteWaits_WritesTheLastProjectionOnce()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = options.ApplicationDatabasePath,
            BusyTimeout = TimeSpan.FromSeconds(30),
        });
        var journal = new SessionViewJournalStore(options, database);
        var cache = (SessionJournalSqliteCache)journal.ProjectionCache;
        var summary = CreateSummary("session-merged");
        await journal.CreateSessionStore().UpsertSessionAsync(summary);
        var path = Path.GetFullPath(new AgentRuntimePathLayout(options.StateRoot).GetSessionFilePath(summary.SessionId, CreatedAt));
        var info = new FileInfo(path);
        AgentSessionCacheProjection Projection(string title)
            => new(path, new AgentSessionCacheFileStamp(info.LastWriteTimeUtc, info.Length), summary with { Title = title }, null);

        var other = CreateSummary("session-other");
        await journal.CreateSessionStore().UpsertSessionAsync(other);
        var otherPath = Path.GetFullPath(new AgentRuntimePathLayout(options.StateRoot).GetSessionFilePath(other.SessionId, CreatedAt));
        var otherInfo = new FileInfo(otherPath);

        Task blocking, older, newer;
        await using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.ApplicationDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            await holder.OpenAsync();
            await ExecuteAsync(holder, "BEGIN EXCLUSIVE;");
            blocking = cache.UpsertSessionAsync(new AgentSessionCacheProjection(otherPath,
                new AgentSessionCacheFileStamp(otherInfo.LastWriteTimeUtc, otherInfo.Length), other, null));
            await WaitUntilAsync(() => cache.IsWritingUpserts && cache.PendingUpsertCount == 0);
            older = cache.UpsertSessionAsync(Projection("older"));
            await WaitUntilAsync(() => cache.PendingUpsertCount == 1);
            newer = cache.UpsertSessionAsync(Projection("newer"));
            await Task.Delay(50);
            Assert.AreEqual(1, cache.PendingUpsertCount, "One upsert waits per session.");
            await ExecuteAsync(holder, "ROLLBACK;");
        }

        await Task.WhenAll(blocking, older, newer).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual("newer", ReadTitle(options.ApplicationDatabasePath, summary.SessionId), "Both callers are answered by the write of the last one.");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private static string? ReadTitle(string databasePath, string sessionId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM session_projection_cache WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        return command.ExecuteScalar() as string;
    }

    private static AgentSessionSummary CreateSummary(string sessionId)
        => new()
        {
            SessionId = sessionId,
            ProviderId = new ModelProviderId("openai"),
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ModelId = "gpt-5",
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "cache-upsert-tests"),
            Title = "written",
            Summary = "summary",
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt.AddMinutes(1),
        };

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodeAlta.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for temporary test files.
            }
        }
    }
}
