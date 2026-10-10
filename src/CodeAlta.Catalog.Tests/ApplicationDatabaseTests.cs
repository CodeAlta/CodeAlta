using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ApplicationDatabaseTests
{
    [TestMethod]
    public async Task Open_TurnsOnTheWriteAheadLogAndCreatesTheFileUnderData()
    {
        using var temp = TempFolder.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        await using var database = ApplicationDatabase.Create(options);

        var mode = await database.ReadAsync("test", (connection, token) => ScalarAsync<string>(connection, "PRAGMA journal_mode;", token));

        Assert.AreEqual("wal", mode);
        Assert.AreEqual(Path.Combine(temp.Path, "data", "alta.sqlite3"), database.DatabasePath);
        Assert.IsTrue(File.Exists(database.DatabasePath));
    }

    [TestMethod]
    public async Task Write_CommitsWhenItReturnsAndRollsBackWhenItThrows()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER PRIMARY KEY, name TEXT);", token));

        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "INSERT INTO t_data (name) VALUES ('kept');", token));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await database.WriteAsync("test", async (connection, token) =>
            {
                await ExecuteAsync(connection, "INSERT INTO t_data (name) VALUES ('lost');", token);
                throw new InvalidOperationException("stop");
            }));

        var names = await database.ReadAsync("test", async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM t_data ORDER BY id;";
            var result = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) result.Add(reader.GetString(0));
            return result;
        });
        CollectionAssert.AreEqual(new[] { "kept" }, names);
    }

    [TestMethod]
    public async Task ALongWrite_DoesNotBlockAReader()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.WriteAsync("test", async (connection, token) =>
        {
            await ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER PRIMARY KEY, name TEXT);", token);
            await ExecuteAsync(connection, "INSERT INTO t_data (name) VALUES ('before');", token);
        });

        var writing = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var write = database.WriteAsync("test", async (connection, token) =>
        {
            await ExecuteAsync(connection, "INSERT INTO t_data (name) VALUES ('during');", token);
            writing.SetResult();
            await release.Task;
        }).AsTask();
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The writer holds the write lock of the file; the reader goes through it.
        var count = await database.ReadAsync("test", (connection, token) => ScalarAsync<long>(connection, "SELECT COUNT(*) FROM t_data;", token))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1L, count, "A reader sees the data committed before the long write, and does not wait for it.");

        release.SetResult();
        await write;
        Assert.AreEqual(2L, await database.ReadAsync("test", (connection, token) => ScalarAsync<long>(connection, "SELECT COUNT(*) FROM t_data;", token)));
    }

    [TestMethod]
    public async Task TwoWriters_RunOneAfterTheOtherInTheOrderTheyAsked()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var running = 0;
        var maximum = 0;
        var order = new List<int>();
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        async ValueTask Write(int number, SqliteConnection connection, CancellationToken token)
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref running));
            order.Add(number);
            if (number == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }

            await Task.Delay(20, token);
            Interlocked.Decrement(ref running);
        }

        var first = database.WriteAsync("one", (connection, token) => Write(1, connection, token)).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = database.WriteAsync("two", (connection, token) => Write(2, connection, token)).AsTask();
        var third = database.WriteAsync("three", (connection, token) => Write(3, connection, token)).AsTask();
        releaseFirst.SetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(1, maximum);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order);
    }

    [TestMethod]
    public async Task ACanceledQueuedWrite_NeverRuns()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var ran = false;
        var first = database.WriteAsync("one", async (connection, token) =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
        }).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var cancellation = new CancellationTokenSource();
        var queued = database.WriteAsync("two", (connection, token) =>
        {
            ran = true;
            return ValueTask.CompletedTask;
        }, cancellation.Token).AsTask();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        releaseFirst.SetResult();
        await first;
        await database.WriteAsync("three", (connection, token) => ValueTask.CompletedTask);

        Assert.IsFalse(ran);
    }

    [TestMethod]
    public async Task AWriteThatStartsAnotherWrite_IsRefusedInsteadOfWaitingForItself()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER);", token));

        // The inner write would wait for the queue the outer one holds: forever.
        var nestedWrite = database.WriteAsync("outer", async (connection, token) =>
        {
            await ExecuteAsync(connection, "INSERT INTO t_data VALUES (1);", token);
            await database.WriteAsync("inner", (inner, innerToken) => ExecuteAsync(inner, "INSERT INTO t_data VALUES (2);", innerToken), token);
        }).AsTask();
        var nestedMigration = database.WriteAsync("outer", async (connection, token) =>
            await database.MigrateAsync("plugin:stats", "stats_", 1, (inner, from, to, innerToken) => ExecuteAsync(inner, "CREATE TABLE stats_day (id INTEGER);", innerToken), token)).AsTask();
        var nestedDrop = database.WriteAsync("outer", async (connection, token) =>
            await database.DropTablesAsync("plugin:stats", "stats_", token)).AsTask();

        foreach (var nested in new[] { nestedWrite, nestedMigration, nestedDrop })
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => nested.WaitAsync(TimeSpan.FromSeconds(10)));
            StringAssert.Contains(failure.Message, "outer");
        }

        // The outer writes were rolled back, and the queue is free.
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "INSERT INTO t_data VALUES (3);", token)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(3L, await database.ReadAsync("test", (connection, token) => ScalarAsync<long>(connection, "SELECT SUM(id) FROM t_data;", token)));
        Assert.AreEqual(0, await database.GetVersionAsync("plugin:stats"));
    }

    [TestMethod]
    public async Task AWrite_MayReadAndMayStartWhatWritesAfterIt()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER); INSERT INTO t_data VALUES (1);", token));
        Task? later = null;
        var outerEnded = new TaskCompletionSource();
        var seen = -1L;

        await database.WriteAsync("outer", async (connection, token) =>
        {
            await ExecuteAsync(connection, "INSERT INTO t_data VALUES (2);", token);

            // A read has a connection of its own: it sees what is committed, and waits for nobody.
            seen = await database.ReadAsync("outer", (reader, readToken) => ScalarAsync<long>(reader, "SELECT COUNT(*) FROM t_data;", readToken), token);

            // Work the write starts, and that writes once the write is over, is not part of it.
            later = Task.Run(async () =>
            {
                await outerEnded.Task;
                await database.WriteAsync("later", (inner, innerToken) => ExecuteAsync(inner, "INSERT INTO t_data VALUES (3);", innerToken));
            }, token);
        });
        outerEnded.SetResult();
        await later!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1L, seen);
        Assert.AreEqual(3L, await database.ReadAsync("test", (connection, token) => ScalarAsync<long>(connection, "SELECT COUNT(*) FROM t_data;", token)));
    }

    [TestMethod]
    public async Task TwoDatabasesOnTheSameFile_WriteWithoutALockedError()
    {
        using var temp = TempFolder.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        await using var first = ApplicationDatabase.Create(options);
        await using var second = ApplicationDatabase.Create(options);
        await first.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER PRIMARY KEY, who TEXT);", token));

        var writes = Enumerable.Range(0, 20).Select(index => (index % 2 == 0 ? first : second).WriteAsync(
            "test",
            async (connection, token) =>
            {
                await ExecuteAsync(connection, $"INSERT INTO t_data (who) VALUES ('{index}');", token);
                await Task.Delay(5, token);
            }).AsTask()).ToArray();
        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(20L, await first.ReadAsync("test", (connection, token) => ScalarAsync<long>(connection, "SELECT COUNT(*) FROM t_data;", token)));
    }

    [TestMethod]
    public async Task AWriteBlockedByAnotherProcess_FailsAsLockedAfterTheBusyTimeout()
    {
        using var temp = TempFolder.Create();
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3"),
            BusyTimeout = TimeSpan.FromMilliseconds(300),
        });
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER);", token));

        await using (var other = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString()))
        {
            await other.OpenAsync();
            await ExecuteAsync(other, "BEGIN IMMEDIATE;", CancellationToken.None);

            var failure = await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
                await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "INSERT INTO t_data VALUES (1);", token)));
            Assert.IsTrue(ApplicationDatabase.IsLocked(failure));

            // A reader is not stopped by that lock.
            Assert.AreEqual(0L, await database.ReadAsync("test", (connection, token) => ScalarAsync<long>(connection, "SELECT COUNT(*) FROM t_data;", token)));
            await ExecuteAsync(other, "ROLLBACK;", CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task Migrate_RunsEachVersionOnceAndRecordsIt()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var calls = new List<(int From, int To)>();
        ValueTask Migrate(SqliteConnection connection, int from, int to, CancellationToken token)
        {
            calls.Add((from, to));
            return from < 1
                ? ExecuteAsync(connection, "CREATE TABLE stats_day (id INTEGER PRIMARY KEY); CREATE INDEX stats_day_ix ON stats_day(id);", token)
                : ExecuteAsync(connection, "ALTER TABLE stats_day ADD COLUMN extra TEXT;", token);
        }

        await database.MigrateAsync("plugin:stats", "stats_", 1, Migrate);
        await database.MigrateAsync("plugin:stats", "stats_", 1, Migrate);
        Assert.AreEqual(1, await database.GetVersionAsync("plugin:stats"));
        await database.MigrateAsync("plugin:stats", "stats_", 2, Migrate);
        await database.MigrateAsync("plugin:stats", "stats_", 2, Migrate);

        CollectionAssert.AreEqual(new[] { (0, 1), (1, 2) }, calls);
        Assert.AreEqual(2, await database.GetVersionAsync("plugin:stats"));
        Assert.AreEqual(0, await database.GetVersionAsync("plugin:other"));
    }

    [TestMethod]
    public async Task Migrate_ToAnOlderVersionThanRecorded_ThrowsAndChangesNothing()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.MigrateAsync("plugin:stats", "stats_", 3, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE stats_day (id INTEGER);", token));

        var failure = await Assert.ThrowsExactlyAsync<ApplicationDatabaseNewerVersionException>(async () =>
            await database.MigrateAsync("plugin:stats", "stats_", 2, (connection, from, to, token) => ExecuteAsync(connection, "DROP TABLE stats_day;", token)));

        // An owner that can make its tables again tells this failure from the others; the others still catch InvalidOperationException.
        Assert.IsInstanceOfType<InvalidOperationException>(failure);
        Assert.AreEqual("plugin:stats", failure.Owner);
        Assert.AreEqual(3, failure.RecordedVersion);
        Assert.AreEqual(2, failure.RequestedVersion);
        StringAssert.Contains(failure.Message, "newer");
        Assert.AreEqual(3, await database.GetVersionAsync("plugin:stats"));
        CollectionAssert.AreEqual(new[] { "stats_day" }, (await database.ListTablesAsync("stats_")).ToArray());
    }

    [TestMethod]
    public async Task Migrate_ThatFails_RollsBackItsTablesAndDoesNotRecordTheVersion()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);

        await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            await database.MigrateAsync("plugin:stats", "stats_", 1, async (connection, from, to, token) =>
            {
                await ExecuteAsync(connection, "CREATE TABLE stats_day (id INTEGER);", token);
                await ExecuteAsync(connection, "NOT SQL;", token);
            }));

        Assert.AreEqual(0, await database.GetVersionAsync("plugin:stats"));
        Assert.AreEqual(0, (await database.ListTablesAsync("stats_")).Count);
    }

    [TestMethod]
    public async Task Migrate_RefusesToCreateOrDropObjectsOutsideItsPrefix()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.MigrateAsync("session_cache", null, 1, (connection, from, to, token) =>
            ExecuteAsync(connection, "CREATE TABLE session_projection_cache (id INTEGER); CREATE INDEX ix_session_projection_cache_id ON session_projection_cache(id);", token));
        await database.MigrateAsync("plugin:git", "git_", 1, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE git_notes (id INTEGER);", token));

        // A table of another owner, a table of the application, a new name outside the prefix.
        foreach (var sql in new[]
                 {
                     "DROP TABLE git_notes;",
                     "DROP TABLE session_projection_cache;",
                     "ALTER TABLE git_notes ADD COLUMN extra TEXT;",
                     "CREATE TABLE elsewhere (id INTEGER);",
                     "CREATE TABLE stats_day (id INTEGER); CREATE TABLE app_meta2 (id INTEGER);",
                     "CREATE TABLE stats_day (id INTEGER); CREATE INDEX ix_session_projection_cache_x ON session_projection_cache(id);",
                 })
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await database.MigrateAsync("plugin:stats", "stats_", 1, (connection, from, to, token) => ExecuteAsync(connection, sql, token)), sql);
            StringAssert.Contains(failure.Message, "stats_");
        }

        Assert.AreEqual(0, await database.GetVersionAsync("plugin:stats"));
        Assert.AreEqual(0, (await database.ListTablesAsync("stats_")).Count);
        CollectionAssert.AreEqual(new[] { "git_notes" }, (await database.ListTablesAsync("git_")).ToArray());
        CollectionAssert.AreEqual(new[] { "session_projection_cache" }, (await database.ListTablesAsync("session_")).ToArray());
    }

    [TestMethod]
    public async Task Migrate_RefusesAnIndexOrATriggerOnATableOfAnotherOwner_WhateverItsName()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.MigrateAsync("session_cache", null, 1, (connection, from, to, token) =>
            ExecuteAsync(connection, "CREATE TABLE session_projection_cache (id INTEGER);", token));
        await database.MigrateAsync("plugin:git", "git_", 1, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE git_notes (id INTEGER);", token));

        // The name starts with the prefix of the plugin, the table it is on does not.
        foreach (var sql in new[]
                 {
                     "CREATE INDEX stats_ix ON git_notes(id);",
                     "CREATE UNIQUE INDEX stats_ux ON session_projection_cache(id);",
                     "CREATE TRIGGER stats_trg AFTER INSERT ON git_notes BEGIN DELETE FROM git_notes; END;",
                     "CREATE TRIGGER stats_trg AFTER DELETE ON session_projection_cache BEGIN SELECT 1; END;",
                 })
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await database.MigrateAsync("plugin:stats", "stats_", 1, (connection, from, to, token) =>
                    ExecuteAsync(connection, "CREATE TABLE stats_day (id INTEGER); " + sql, token)), sql);
            StringAssert.Contains(failure.Message, "stats_", sql);
        }

        Assert.AreEqual(0, await database.GetVersionAsync("plugin:stats"));
        Assert.AreEqual(0L, await database.ReadAsync("test", (connection, token) =>
            ScalarAsync<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'stats%';", token)));
    }

    [TestMethod]
    public async Task Migrate_AllowsIndexesAndTriggersOnItsOwnTables()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);

        // A trigger may bear the name of a table: SQLite keeps the two apart.
        await database.MigrateAsync("plugin:stats", "stats_", 1, (connection, from, to, token) => ExecuteAsync(
            connection,
            """
            CREATE TABLE stats_day (id INTEGER PRIMARY KEY, tokens INTEGER);
            CREATE TABLE stats_log (id INTEGER);
            CREATE INDEX stats_day_tokens ON stats_day(tokens);
            CREATE VIEW stats_view AS SELECT id FROM stats_day;
            CREATE TRIGGER stats_day_log AFTER INSERT ON stats_day BEGIN INSERT INTO stats_log VALUES (new.id); END;
            CREATE TRIGGER stats_log AFTER DELETE ON stats_day BEGIN DELETE FROM stats_log WHERE id = old.id; END;
            """,
            token));

        Assert.AreEqual(1, await database.GetVersionAsync("plugin:stats"));
    }

    [TestMethod]
    public async Task Migrate_AllowsTheAutomaticObjectsOfItsTables()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);

        await database.MigrateAsync("plugin:stats", "stats_", 1, (connection, from, to, token) => ExecuteAsync(
            connection,
            "CREATE TABLE stats_run (id INTEGER PRIMARY KEY AUTOINCREMENT, key TEXT UNIQUE, name TEXT NOT NULL);",
            token));

        Assert.AreEqual(1, await database.GetVersionAsync("plugin:stats"));
    }

    [TestMethod]
    public async Task DropTables_RemovesTheObjectsOfOneOwnerAndItsVersion()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.MigrateAsync("plugin:stats", "stats_", 1, (connection, from, to, token) => ExecuteAsync(
            connection, "CREATE TABLE stats_day (id INTEGER); CREATE INDEX stats_day_ix ON stats_day(id); CREATE VIEW stats_view AS SELECT id FROM stats_day;", token));
        await database.MigrateAsync("plugin:git", "git_", 1, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE git_notes (id INTEGER);", token));

        var dropped = await database.DropTablesAsync("plugin:stats", "stats_");

        CollectionAssert.AreEqual(new[] { "stats_day" }, dropped.ToArray());
        Assert.AreEqual(0, await database.GetVersionAsync("plugin:stats"));
        Assert.AreEqual(0, (await database.ListTablesAsync("stats_")).Count);
        Assert.AreEqual(1, await database.GetVersionAsync("plugin:git"));
        CollectionAssert.AreEqual(new[] { "git_notes" }, (await database.ListTablesAsync("git_")).ToArray());
    }

    [TestMethod]
    public async Task DropTables_AlsoRemovesWhatTheOwnerNamedOnTheTablesOfAnother()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.MigrateAsync("plugin:git", "git_", 1, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE git_notes (id INTEGER);", token));
        await database.MigrateAsync("plugin:stats", "stats_", 1, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE stats_day (id INTEGER);", token));

        // A migration cannot make these any more; a database written before that check may hold them.
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(
            connection,
            "CREATE INDEX stats_ix ON git_notes(id); CREATE TRIGGER stats_trg AFTER INSERT ON git_notes BEGIN SELECT 1; END;",
            token));

        await database.DropTablesAsync("plugin:stats", "stats_");

        Assert.AreEqual(0L, await database.ReadAsync("test", (connection, token) =>
            ScalarAsync<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'stats%';", token)));
        CollectionAssert.AreEqual(new[] { "git_notes" }, (await database.ListTablesAsync("git_")).ToArray());
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "INSERT INTO git_notes VALUES (1);", token));
    }

    [TestMethod]
    public async Task TheOldSessionCacheFile_IsMovedWithItsDataWhenTheNewFileDoesNotExist()
    {
        using var temp = TempFolder.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        Directory.CreateDirectory(Path.GetDirectoryName(options.LegacySessionCacheDatabasePath)!);
        await using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.LegacySessionCacheDatabasePath, Pooling = false }.ToString()))
        {
            await old.OpenAsync();
            await ExecuteAsync(old, "CREATE TABLE session_projection_cache (session_id TEXT); INSERT INTO session_projection_cache VALUES ('kept-session');", CancellationToken.None);
        }

        await using var database = ApplicationDatabase.Create(options);
        var value = await database.ReadAsync("test", (connection, token) => ScalarAsync<string>(connection, "SELECT session_id FROM session_projection_cache;", token));

        Assert.AreEqual("kept-session", value);
        Assert.IsFalse(File.Exists(options.LegacySessionCacheDatabasePath), "The file moved: it is not copied.");
        Assert.IsTrue(File.Exists(options.ApplicationDatabasePath));
        Assert.IsNull(database.LastRecovery);
    }

    [TestMethod]
    public async Task TheOldSessionCacheFile_IsLeftAloneWhenTheNewFileExists()
    {
        using var temp = TempFolder.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        Directory.CreateDirectory(Path.GetDirectoryName(options.LegacySessionCacheDatabasePath)!);
        await File.WriteAllTextAsync(options.LegacySessionCacheDatabasePath, "old file");
        await using (var first = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = options.ApplicationDatabasePath }))
        {
            await first.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER); INSERT INTO t_data VALUES (7);", token));
        }

        await using var database = ApplicationDatabase.Create(options);
        var value = await database.ReadAsync("test", (connection, token) => ScalarAsync<long>(connection, "SELECT id FROM t_data;", token));

        Assert.AreEqual(7L, value);
        Assert.AreEqual("old file", await File.ReadAllTextAsync(options.LegacySessionCacheDatabasePath));
    }

    [TestMethod]
    public async Task TheStateRootOfTheOtherInstance_IsNeverTouched()
    {
        using var temp = TempFolder.Create();
        var normal = new CatalogOptions { GlobalRoot = temp.Path };
        var developer = new CatalogOptions { GlobalRoot = temp.Path, StateRoot = Path.Combine(temp.Path, "dev") };
        Directory.CreateDirectory(Path.GetDirectoryName(normal.LegacySessionCacheDatabasePath)!);
        await File.WriteAllTextAsync(normal.LegacySessionCacheDatabasePath, "the file of the other instance");

        await using (var database = ApplicationDatabase.Create(developer))
        {
            await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER);", token));
        }

        Assert.AreEqual(Path.Combine(temp.Path, "dev", "data", "alta.sqlite3"), developer.ApplicationDatabasePath);
        Assert.IsTrue(File.Exists(developer.ApplicationDatabasePath));
        Assert.IsFalse(File.Exists(normal.ApplicationDatabasePath));
        Assert.AreEqual("the file of the other instance", await File.ReadAllTextAsync(normal.LegacySessionCacheDatabasePath));
    }

    [TestMethod]
    public async Task AFileThatIsNotADatabase_IsMovedAsideAndReplacedByAnEmptyOne()
    {
        using var temp = TempFolder.Create();
        var path = Path.Combine(temp.Path, "data", "alta.sqlite3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not a sqlite database at all, just text");
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path });

        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER);", token));

        Assert.AreEqual(1, database.Generation);
        var recovery = database.LastRecovery;
        Assert.IsNotNull(recovery);
        Assert.IsNull(recovery.RestoredFromCopy);
        Assert.IsNotNull(recovery.MovedAsidePath);
        Assert.AreEqual("not a sqlite database at all, just text", await File.ReadAllTextAsync(recovery.MovedAsidePath), "The damaged file is kept, not deleted.");
        Assert.AreEqual(0L, await database.ReadAsync("test", (connection, token) => ScalarAsync<long>(connection, "SELECT COUNT(*) FROM t_data;", token)));
    }

    [TestMethod]
    public async Task ADamagedFile_IsRestoredFromTheLatestCopyAndKeepsThePluginData()
    {
        using var temp = TempFolder.Create();
        var path = Path.Combine(temp.Path, "data", "alta.sqlite3");
        string movedAside;
        await using (var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path }))
        {
            await database.MigrateAsync("plugin:stats", "stats_", 1, (connection, from, to, token) => ExecuteAsync(
                connection, "CREATE TABLE stats_day (id INTEGER, tokens INTEGER); INSERT INTO stats_day VALUES (1, 4200);", token));
            Assert.IsNotNull(await database.BackupAsync());
        }

        // The header of the file is overwritten: every operation now fails with "not a database".
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(new byte[4096].Select(static (_, index) => (byte)(index % 251)).ToArray());
        }

        await using (var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path }))
        {
            var tokens = await database.ReadAsync("plugin:stats", (connection, token) => ScalarAsync<long>(connection, "SELECT tokens FROM stats_day;", token));

            Assert.AreEqual(4200L, tokens, "The data of the plugin comes back from the copy.");
            Assert.AreEqual(1, await database.GetVersionAsync("plugin:stats"), "And so does its version, so it does not migrate again.");
            var recovery = database.LastRecovery;
            Assert.IsNotNull(recovery);
            Assert.IsNotNull(recovery.RestoredFromCopy);
            movedAside = recovery.MovedAsidePath!;
        }

        Assert.IsTrue(File.Exists(movedAside));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.restore").Length, "The copy that was made ready is the database now.");
    }

    [TestMethod]
    public async Task AMissingFile_IsRestoredFromTheNewestValidCopy()
    {
        using var temp = TempFolder.Create();
        var path = Path.Combine(temp.Path, "data", "alta.sqlite3");
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        string older;
        string newest;
        await using (var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path, TimeProvider = clock }))
        {
            await database.MigrateAsync("plugin:stats", "stats_", 1, (connection, from, to, token) => ExecuteAsync(
                connection, "CREATE TABLE stats_day (id INTEGER, tokens INTEGER); INSERT INTO stats_day VALUES (1, 4200);", token));
            older = (await database.BackupAsync())!;
            clock.Advance(TimeSpan.FromDays(1));
            await database.WriteAsync("plugin:stats", (connection, token) => ExecuteAsync(connection, "INSERT INTO stats_day VALUES (2, 800);", token));
            newest = (await database.BackupAsync())!;
        }

        // The file is gone (deleted, or a replacement of the damaged file that stopped half way) and what stays of
        // its log is not the log of the copy. The newest copy is not a database any more.
        File.Delete(path);
        await File.WriteAllTextAsync(path + "-wal", "the log of the file that is gone");
        await File.WriteAllBytesAsync(newest, new byte[4096].Select(static (_, index) => (byte)(index % 251)).ToArray());

        await using (var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path, TimeProvider = clock }))
        {
            var tokens = await database.ReadAsync("plugin:stats", (connection, token) => ScalarAsync<long>(connection, "SELECT SUM(tokens) FROM stats_day;", token));

            Assert.AreEqual(4200L, tokens, "An empty database here would be copied in its turn, and push the valid copies out.");
            Assert.AreEqual(1, await database.GetVersionAsync("plugin:stats"));
            Assert.AreEqual(1, database.Generation, "The owners learn that the rows are those of a copy.");
            var recovery = database.LastRecovery;
            Assert.IsNotNull(recovery);
            Assert.AreEqual(older, recovery.RestoredFromCopy);
            Assert.IsNull(recovery.MovedAsidePath);
        }

        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.restore").Length);
        Assert.IsTrue(File.Exists(older), "A copy is copied, not moved.");
    }

    [TestMethod]
    public async Task AFileDeletedWhileInUse_ComesBackFromItsCopyWhenThereIsOne()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.MigrateAsync("plugin:stats", "stats_", 1, (connection, from, to, token) => ExecuteAsync(
            connection, "CREATE TABLE stats_day (id INTEGER); INSERT INTO stats_day VALUES (1);", token));
        Assert.IsNotNull(await database.BackupAsync());
        await database.WriteAsync("plugin:stats", (connection, token) => ExecuteAsync(connection, "INSERT INTO stats_day VALUES (2);", token));

        File.Delete(database.DatabasePath);
        var rows = await database.ReadAsync("plugin:stats", (connection, token) => ScalarAsync<long>(connection, "SELECT COUNT(*) FROM stats_day;", token));

        Assert.AreEqual(1L, rows, "What was written since the copy is lost; the rest is not.");
        Assert.AreEqual(1, database.Generation);
        Assert.IsNotNull(database.LastRecovery?.RestoredFromCopy);
    }

    [TestMethod]
    public async Task ADatabaseWithoutAUsableCopy_StartsEmptyAndTheOwnersMigrateAgain()
    {
        using var temp = TempFolder.Create();
        var path = Path.Combine(temp.Path, "data", "alta.sqlite3");
        await using (var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path }))
        {
            await database.MigrateAsync("plugin:stats", "stats_", 1, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE stats_day (id INTEGER);", token));
        }

        await File.WriteAllBytesAsync(path, new byte[2048].Select(static (_, index) => (byte)(index % 7 + 65)).ToArray());
        await using (var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path }))
        {
            var version = await database.GetVersionAsync("plugin:stats");
            Assert.AreEqual(0, version, "The plugin starts empty: its migration runs again.");
            Assert.IsNull(database.LastRecovery!.RestoredFromCopy);
        }
    }

    [TestMethod]
    public async Task AValidFile_IsNeverReplaced()
    {
        using var temp = TempFolder.Create();
        var path = Path.Combine(temp.Path, "data", "alta.sqlite3");
        await using (var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path }))
        {
            await database.MigrateAsync("plugin:stats", "stats_", 1, (connection, from, to, token) => ExecuteAsync(connection, "CREATE TABLE stats_day (id INTEGER); INSERT INTO stats_day VALUES (9);", token));
        }

        await using (var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path }))
        {
            Assert.AreEqual(9L, await database.ReadAsync("test", (connection, token) => ScalarAsync<long>(connection, "SELECT id FROM stats_day;", token)));
            Assert.IsNull(database.LastRecovery);
            Assert.AreEqual(0, database.Generation);
        }

        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*").Length);
    }

    [TestMethod]
    public async Task Backup_IsAValidDatabaseAndOnlyTheLatestTwoAreKept()
    {
        using var temp = TempFolder.Create();
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3"),
            TimeProvider = clock,
        });
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER); INSERT INTO t_data VALUES (1);", token));

        var copies = new List<string>();
        for (var day = 0; day < 3; day++)
        {
            clock.Advance(TimeSpan.FromDays(1));
            await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, $"INSERT INTO t_data VALUES ({day + 2});", token));
            copies.Add((await database.BackupAsync())!);
        }

        var kept = database.GetBackupPaths();
        Assert.AreEqual(2, kept.Count);
        CollectionAssert.AreEqual(new[] { copies[2], copies[1] }, kept.ToArray());
        Assert.IsFalse(File.Exists(copies[0]));
        Assert.AreEqual(0, Directory.GetFiles(database.BackupDirectory, "*.tmp").Length);
        await using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copies[2], Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await copy.OpenAsync();
        Assert.AreEqual(4L, await ScalarAsync<long>(copy, "SELECT COUNT(*) FROM t_data;", CancellationToken.None));
        Assert.AreEqual("delete", await ScalarAsync<string>(copy, "PRAGMA journal_mode;", CancellationToken.None), "A copy is one file.");
    }

    [TestMethod]
    public async Task BackupIfDue_MakesACopyOnlyWhenTheLatestIsOlderThanTheInterval()
    {
        using var temp = TempFolder.Create();
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3"),
            TimeProvider = clock,
        });
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER);", token));

        Assert.IsNotNull(await database.BackupIfDueAsync(), "No copy yet.");
        clock.Advance(TimeSpan.FromHours(23));
        Assert.IsNull(await database.BackupIfDueAsync());
        clock.Advance(TimeSpan.FromHours(2));
        Assert.IsNotNull(await database.BackupIfDueAsync());
        Assert.AreEqual(2, database.GetBackupPaths().Count);
    }

    [TestMethod]
    public async Task Maintenance_MakesTheFirstCopyAfterItsDelay()
    {
        using var temp = TempFolder.Create();
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3"),
            MaintenanceStartDelay = TimeSpan.FromMilliseconds(10),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER);", token));

        database.StartMaintenance();
        database.StartMaintenance();
        for (var attempt = 0; attempt < 200 && database.GetBackupPaths().Count == 0; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.AreEqual(1, database.GetBackupPaths().Count);
    }

    [TestMethod]
    public async Task Dispose_WaitsForTheWritesThatWereAdmitted()
    {
        using var temp = TempFolder.Create();
        var database = CreateDatabase(temp);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var completed = false;
        var write = database.WriteAsync("test", async (connection, token) =>
        {
            started.SetResult();
            await release.Task;
            completed = true;
        }).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var disposing = database.DisposeAsync().AsTask();
        await Task.Delay(100);
        Assert.IsFalse(disposing.IsCompleted, "The shutdown waits for the write that holds the queue.");
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await database.WriteAsync("test", (connection, token) => ValueTask.CompletedTask));
        release.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        await write;

        Assert.IsTrue(completed);
    }

    [TestMethod]
    public async Task ACommandOfAWrite_NeedsNoTransactionObject()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);

        await database.WriteAsync("test", async (connection, token) =>
        {
            await ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER);", token);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO t_data VALUES ($id);";
            command.Parameters.AddWithValue("$id", 5);
            await command.ExecuteNonQueryAsync(token);
        });

        Assert.AreEqual(5L, await database.ReadAsync("test", (connection, token) => ScalarAsync<long>(connection, "SELECT id FROM t_data;", token)));
    }

    [TestMethod]
    public async Task AFileDeletedWhileInUse_IsMadeAgainAsANewGeneration()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER);", token));
        Assert.AreEqual(0, database.Generation);

        File.Delete(database.DatabasePath);
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_other (id INTEGER);", token));

        Assert.AreEqual(1, database.Generation);
        Assert.AreEqual(0, (await database.ListTablesAsync("t_data")).Count);
        Assert.AreEqual(1, (await database.ListTablesAsync("t_other")).Count);
    }

    [TestMethod]
    public async Task AFileThatGoesJustBeforeAWriteOpensIt_IsMadeAgainByTheServiceNotByTheConnection()
    {
        using var temp = TempFolder.Create();
        var clock = new HookClock();
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3"),
            TimeProvider = clock,
        });
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER);", token));

        // The file goes after the service saw it, and before the connection of the write opens: a connection that
        // made the file would leave one without the write-ahead log, without the versions, and nobody would know.
        clock.OnTimestamp = () => File.Delete(database.DatabasePath);
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_other (id INTEGER);", token));

        Assert.AreEqual(1, database.Generation, "The owners learn that the file is a new one.");
        Assert.AreEqual("wal", await database.ReadAsync("test", (connection, token) => ScalarAsync<string>(connection, "PRAGMA journal_mode;", token)));
        Assert.AreEqual(0, await database.GetVersionAsync("plugin:stats"));
        Assert.AreEqual(0, (await database.ListTablesAsync("t_data")).Count);
        Assert.AreEqual(1, (await database.ListTablesAsync("t_other")).Count);
    }

    [TestMethod]
    public async Task AReaderOfAFileThatIsGone_FailsAndMakesNoFile()
    {
        using var temp = TempFolder.Create();
        var clock = new HookClock();
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3"),
            TimeProvider = clock,
        });
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER); INSERT INTO t_data VALUES (1);", token));

        // The copy reads the database: its connection opens after the service saw the file.
        clock.OnUtcNow = () => File.Delete(database.DatabasePath);
        var failure = await Assert.ThrowsExactlyAsync<SqliteException>(async () => await database.BackupAsync());

        Assert.AreEqual(14, failure.SqliteErrorCode, "SQLite cannot open the file.");
        Assert.IsFalse(File.Exists(database.DatabasePath), "A reader does not make the file.");
        Assert.AreEqual(0, database.GetBackupPaths().Count, "And an empty database is not kept as a copy.");
        Assert.AreEqual(0, (await database.ListTablesAsync("t_data")).Count, "The service makes the file again.");
        Assert.AreEqual(1, database.Generation);
    }

    [TestMethod]
    public async Task AReader_CannotWrite()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        await database.WriteAsync("test", (connection, token) => ExecuteAsync(connection, "CREATE TABLE t_data (id INTEGER);", token));

        await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            await database.ReadAsync("test", async (connection, token) =>
            {
                await ExecuteAsync(connection, "INSERT INTO t_data VALUES (1);", token);
                return 0;
            }));
    }

    private static ApplicationDatabase CreateDatabase(TempFolder temp)
        => new(new ApplicationDatabaseOptions { DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3") });

    private static async ValueTask ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<T> ScalarAsync<T>(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync(cancellationToken))!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan time) => _now += time;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    // A clock that runs an action once, the next time the service asks it something: the place of a test in the
    // middle of an operation.
    private sealed class HookClock : TimeProvider
    {
        public Action? OnTimestamp { get; set; }

        public Action? OnUtcNow { get; set; }

        public override long GetTimestamp()
        {
            var action = OnTimestamp;
            OnTimestamp = null;
            action?.Invoke();
            return base.GetTimestamp();
        }

        public override DateTimeOffset GetUtcNow()
        {
            var action = OnUtcNow;
            OnUtcNow = null;
            action?.Invoke();
            return base.GetUtcNow();
        }
    }

    private sealed class TempFolder : IDisposable
    {
        private TempFolder(string path) => Path = path;

        public string Path { get; }

        public static TempFolder Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alta-appdb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempFolder(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
