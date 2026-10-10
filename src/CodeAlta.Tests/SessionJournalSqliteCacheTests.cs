using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionJournalSqliteCacheTests
{
    [TestMethod]
    public async Task ListSessionsAsync_RebuildsMissingDatabaseAndUsesHotCacheWithoutParsingJournal()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var catalog = new SessionViewCatalog(options);
        var store = catalog.JournalStore.CreateSessionStore();
        var session = CreateSummary("session-hot", updatedAt: "2026-06-18T10:00:00+00:00");
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        await store.UpsertStateAsync(CreateState(session, "resp_hot")).ConfigureAwait(false);
        File.Delete(options.ApplicationDatabasePath);

        var rebuiltCatalog = new SessionViewCatalog(options);
        var rebuiltStore = rebuiltCatalog.JournalStore.CreateSessionStore();
        var rebuilt = await rebuiltStore.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(1, rebuilt.Length);
        Assert.AreEqual("session-hot", rebuilt[0].SessionId);
        Assert.IsTrue(File.Exists(options.ApplicationDatabasePath));

        var journalPath = new AgentRuntimePathLayout(temp.Path).GetSessionFilePath(session.SessionId, session.CreatedAt);
        await File.AppendAllTextAsync(journalPath, Environment.NewLine + "{not-json" + Environment.NewLine).ConfigureAwait(false);

        var hotCatalog = new SessionViewCatalog(options);
        var hotStore = hotCatalog.JournalStore.CreateSessionStore();
        var hot = await hotStore.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(1, hot.Length, "A healthy hot cache should not parse the changed journal before reconciliation.");
        Assert.AreEqual("resp_hot", ((RawApiSessionMetadataDetails?)hot[0].Details)?.ProviderSessionId);
    }

    [TestMethod]
    public async Task ListSessionsAsync_RecreatesCorruptDatabaseFromJournals()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var session = CreateSummary("session-corrupt-db", updatedAt: "2026-06-18T11:00:00+00:00");
        var uncachedStore = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await uncachedStore.UpsertSessionAsync(session).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(options.ApplicationDatabasePath)!);
        await File.WriteAllTextAsync(options.ApplicationDatabasePath, "not a sqlite database").ConfigureAwait(false);

        var catalog = new SessionViewCatalog(options);
        var sessions = await catalog.JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(1, sessions.Length);
        Assert.AreEqual("session-corrupt-db", sessions[0].SessionId);
    }

    [TestMethod]
    public async Task ListSessionsAsync_ReadsTheHotCacheWhileAnotherConnectionWrites_AndAWriteFailsAsLockedAfterTheTimeout()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = options.ApplicationDatabasePath,
            BusyTimeout = TimeSpan.FromMilliseconds(300),
        });
        var catalog = new SessionViewCatalog(options);
        var summary = CreateSummary("session-locked", updatedAt: "2026-06-18T10:30:00+00:00");
        await new SessionViewJournalStore(options, database).CreateSessionStore().UpsertSessionAsync(summary).ConfigureAwait(false);
        var lockedStore = new SessionViewJournalStore(options, database).CreateSessionStore();
        Assert.AreEqual(1, (await lockedStore.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);

        await using (var lockConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.ApplicationDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString()))
        {
            await lockConnection.OpenAsync().ConfigureAwait(false);
            await using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN EXCLUSIVE;";
            await lockCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

            // A reader does not wait for the writer: the hot list is still there.
            Assert.AreEqual(1, (await lockedStore.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);

            // A write waits for the lock, then reports it.
            await Assert.ThrowsExactlyAsync<AgentSessionCacheLockedException>(async () =>
                await lockedStore.UpsertSessionAsync(CreateSummary("session-locked-2", updatedAt: "2026-06-18T10:31:00+00:00")).ConfigureAwait(false)).ConfigureAwait(false);

            await using var rollbackCommand = lockConnection.CreateCommand();
            rollbackCommand.CommandText = "ROLLBACK;";
            await rollbackCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await lockedStore.UpsertSessionAsync(CreateSummary("session-locked-2", updatedAt: "2026-06-18T10:31:00+00:00")).ConfigureAwait(false);
        Assert.AreEqual(2, (await lockedStore.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
        Assert.IsTrue(File.Exists(options.ApplicationDatabasePath));
        Assert.IsNotNull(catalog);
    }

    [TestMethod]
    public async Task ListSessionsAsync_MovesTheCacheOfTheOldLocationInsteadOfReadingTheJournalsAgain()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var store = new SessionViewCatalog(options).JournalStore.CreateSessionStore();
        var session = CreateSummary("session-legacy", updatedAt: "2026-06-18T10:40:00+00:00");
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        await store.UpsertStateAsync(CreateState(session, "resp_legacy")).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);

        // The state of a version before the application database: the cache lives under cache/.
        Directory.CreateDirectory(Path.GetDirectoryName(options.LegacySessionCacheDatabasePath)!);
        File.Move(options.ApplicationDatabasePath, options.LegacySessionCacheDatabasePath);
        var journalPath = new AgentRuntimePathLayout(temp.Path).GetSessionFilePath(session.SessionId, session.CreatedAt);
        await File.AppendAllTextAsync(journalPath, Environment.NewLine + "{not-json" + Environment.NewLine).ConfigureAwait(false);

        var sessions = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(1, sessions.Length);
        Assert.AreEqual("resp_legacy", ((RawApiSessionMetadataDetails?)sessions[0].Details)?.ProviderSessionId, "The rows moved: the changed journal was not parsed.");
        Assert.IsFalse(File.Exists(options.LegacySessionCacheDatabasePath));
        Assert.IsTrue(File.Exists(options.ApplicationDatabasePath));
    }

    [TestMethod]
    public async Task ListSessionsAsync_AfterTheFileIsDamaged_RebuildsTheListAndKeepsThePluginTablesOfTheCopy()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        await using var database = ApplicationDatabase.Create(options);
        var store = new SessionViewJournalStore(options, database).CreateSessionStore();
        var session = CreateSummary("session-damaged", updatedAt: "2026-06-18T10:50:00+00:00");
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
        await database.MigrateAsync("plugin:test", "test_", 1, async (connection, from, to, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE test_facts (id INTEGER); INSERT INTO test_facts VALUES (42);";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Assert.IsNotNull(await database.BackupAsync().ConfigureAwait(false));

        // Newer sessions than the copy has: the restored list must not hide them.
        var newer = CreateSummary("session-newer-than-the-copy", updatedAt: "2026-06-18T10:55:00+00:00");
        await store.UpsertSessionAsync(newer).ConfigureAwait(false);
        await File.WriteAllBytesAsync(options.ApplicationDatabasePath, new byte[8192].Select(static (_, index) => (byte)(index % 241)).ToArray()).ConfigureAwait(false);

        var sessions = await new SessionViewJournalStore(options, database).CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        CollectionAssert.AreEquivalent(new[] { "session-damaged", "session-newer-than-the-copy" }, sessions.Select(static item => item.SessionId).ToArray());
        Assert.IsNotNull(database.LastRecovery?.RestoredFromCopy);
        Assert.AreEqual(42L, await database.ReadAsync("plugin:test", async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM test_facts;";
            return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        }).ConfigureAwait(false));
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(options.ApplicationDatabasePath)!, "*.corrupt-*").Length, "The damaged file is kept aside.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ListSessionsAsync_AfterAWriteMetTheDamage_StillListsEverySession(bool withCopy)
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        await using var database = ApplicationDatabase.Create(options);
        var store = new SessionViewJournalStore(options, database).CreateSessionStore();
        await store.UpsertSessionAsync(CreateSummary("session-before-the-copy", updatedAt: "2026-06-18T10:50:00+00:00")).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
        if (withCopy)
        {
            Assert.IsNotNull(await database.BackupAsync().ConfigureAwait(false));
        }

        await store.UpsertSessionAsync(CreateSummary("session-after-the-copy", updatedAt: "2026-06-18T10:55:00+00:00")).ConfigureAwait(false);
        await File.WriteAllBytesAsync(options.ApplicationDatabasePath, new byte[8192].Select(static (_, index) => (byte)(index % 241)).ToArray()).ConfigureAwait(false);

        // A write meets the damage first, not the list: the tables it gets (those of the copy, or new ones) hold
        // its row and say nothing about the other sessions.
        await store.UpsertSessionAsync(CreateSummary("session-after-the-damage", updatedAt: "2026-06-18T10:58:00+00:00")).ConfigureAwait(false);
        var sessions = await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        CollectionAssert.AreEquivalent(
            new[] { "session-before-the-copy", "session-after-the-copy", "session-after-the-damage" },
            sessions.Select(static item => item.SessionId).ToArray());
        Assert.AreEqual(withCopy, database.LastRecovery?.RestoredFromCopy is not null);
    }

    [TestMethod]
    public async Task ListSessionsAsync_AfterANewerBuildWroteTheSessionTables_MakesThemAgainFromTheJournals()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        await using (var newer = ApplicationDatabase.Create(options))
        {
            var store = new SessionViewJournalStore(options, newer).CreateSessionStore();
            await store.UpsertSessionAsync(CreateSummary("session-downgrade", updatedAt: "2026-06-18T13:00:00+00:00")).ConfigureAwait(false);
            Assert.AreEqual(1, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
            await newer.MigrateAsync("plugin:test", "test_", 1, async (connection, from, to, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE test_facts (id INTEGER); INSERT INTO test_facts VALUES (5);";
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        // What a newer build leaves behind: a higher version of the session tables, in a layout this build does not know.
        await using (var connection = new SqliteConnection($"Data Source={options.ApplicationDatabasePath};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE app_meta SET version = 2 WHERE owner = 'session_cache';";
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            command.CommandText = "ALTER TABLE session_projection_cache RENAME COLUMN title TO title_of_the_newer_build;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var database = ApplicationDatabase.Create(options);
        var downgraded = new SessionViewJournalStore(options, database).CreateSessionStore();
        var sessions = await downgraded.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(1, sessions.Length);
        Assert.AreEqual("session-downgrade", sessions[0].SessionId);
        Assert.AreEqual(1, await database.GetVersionAsync("session_cache").ConfigureAwait(false), "The tables are those of this build again.");
        await downgraded.UpsertSessionAsync(CreateSummary("session-after-the-downgrade", updatedAt: "2026-06-18T13:05:00+00:00")).ConfigureAwait(false);
        Assert.AreEqual(2, (await downgraded.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
        Assert.AreEqual(5L, await database.ReadAsync("plugin:test", async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM test_facts;";
            return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        }).ConfigureAwait(false), "The tables of a plugin are not those of the session list: they stay.");
        Assert.IsNull(database.LastRecovery, "The file is not damaged: it is not replaced.");
    }

    [TestMethod]
    public async Task ListSessionsAsync_WhenTheDamagedFileCannotBeMovedAside_ReportsItAsLockedAndListsOnceItCan()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("A file that another process holds open can be moved on this system.");
        }

        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = options.ApplicationDatabasePath,
            BackupDirectory = options.ApplicationDatabaseBackupRoot,
            BusyTimeout = TimeSpan.FromMilliseconds(300),
        });
        var store = new SessionViewJournalStore(options, database).CreateSessionStore();
        await store.UpsertSessionAsync(CreateSummary("session-held", updatedAt: "2026-06-18T16:00:00+00:00")).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
        await File.WriteAllBytesAsync(options.ApplicationDatabasePath, new byte[8192].Select(static (_, index) => (byte)(index % 241)).ToArray()).ConfigureAwait(false);

        // Another process (the other instance, a scanner) holds the damaged file: it can be read, not moved.
        await using (new FileStream(options.ApplicationDatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await Assert.ThrowsExactlyAsync<AgentSessionCacheLockedException>(async () =>
                await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }

        var sessions = await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(1, sessions.Length);
        Assert.AreEqual("session-held", sessions[0].SessionId);
    }

    [TestMethod]
    public async Task ListSessionsAsync_WhenTheFileCannotBeMade_ReportsWhatTheSystemSaidAndListsOnceItCan()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        await using var database = ApplicationDatabase.Create(options);
        var store = new SessionViewJournalStore(options, database).CreateSessionStore();
        await new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path))
            .UpsertSessionAsync(CreateSummary("session-no-folder", updatedAt: "2026-06-18T16:30:00+00:00")).ConfigureAwait(false);

        // Something is in the way of the folder of the database: no file is damaged, and no process holds one.
        var folder = Path.GetDirectoryName(options.ApplicationDatabasePath)!;
        await File.WriteAllTextAsync(folder, "in the way").ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<AgentSessionCacheLockedException>(async () =>
            await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsInstanceOfType<IOException>(failure.InnerException);
        Assert.IsFalse(failure.Message.Contains("damaged", StringComparison.OrdinalIgnoreCase), failure.Message);
        Assert.IsTrue(failure.Message.Contains(failure.InnerException.Message, StringComparison.Ordinal), failure.Message);

        File.Delete(folder);
        var sessions = await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(1, sessions.Length);
    }

    [TestMethod]
    public async Task ReadsAndWritesThatMeetTheSameDamageTogether_AllSucceed()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        await using var database = ApplicationDatabase.Create(options);
        var store = new SessionViewJournalStore(options, database).CreateSessionStore();
        for (var index = 0; index < 4; index++)
        {
            await store.UpsertSessionAsync(CreateSummary($"session-together-{index}", updatedAt: $"2026-06-18T14:0{index}:00+00:00")).ConfigureAwait(false);
        }

        Assert.AreEqual(4, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
        await File.WriteAllBytesAsync(options.ApplicationDatabasePath, new byte[8192].Select(static (_, index) => (byte)(index % 241)).ToArray()).ConfigureAwait(false);

        // Each of them finds the file damaged: the tables are made again once, not under an operation that already
        // found them again.
        var operations = new List<Task>();
        for (var index = 4; index < 12; index++)
        {
            var summary = CreateSummary($"session-together-{index}", updatedAt: $"2026-06-18T14:{index + 10}:00+00:00");
            operations.Add(Task.Run(() => store.UpsertSessionAsync(summary)));
            operations.Add(Task.Run(async () => await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)));
        }

        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        Assert.AreEqual(12, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
    }

    [TestMethod]
    public async Task UpsertSessionAsync_WhenTheFileIsReplacedUnderTheWrite_MakesItsTablesAgainAndListsEverySession()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var clock = new HookClock();
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = options.ApplicationDatabasePath,
            BackupDirectory = options.ApplicationDatabaseBackupRoot,
            TimeProvider = clock,
        });
        var store = new SessionViewJournalStore(options, database).CreateSessionStore();
        await store.UpsertSessionAsync(CreateSummary("session-before", updatedAt: "2026-06-18T17:00:00+00:00")).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);

        // The file goes after the list checked its tables, as the write starts: the database makes a new file, which
        // has none of them.
        clock.OnTimestamp = () => File.Delete(options.ApplicationDatabasePath);
        await store.UpsertSessionAsync(CreateSummary("session-under-the-write", updatedAt: "2026-06-18T17:05:00+00:00")).ConfigureAwait(false);
        var sessions = await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(1, database.Generation);
        CollectionAssert.AreEquivalent(new[] { "session-before", "session-under-the-write" }, sessions.Select(static item => item.SessionId).ToArray());
    }

    [TestMethod]
    public async Task ListSessionsAsync_WhenTheFileIsReplacedWhileItsTablesAreMade_MakesThemInTheNewFile()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var clock = new HookClock();
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = options.ApplicationDatabasePath,
            BackupDirectory = options.ApplicationDatabaseBackupRoot,
            TimeProvider = clock,
        });
        var store = new SessionViewJournalStore(options, database).CreateSessionStore();
        await store.UpsertSessionAsync(CreateSummary("session-kept-in-its-journal", updatedAt: "2026-06-18T18:00:00+00:00")).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);

        // The file goes: the next operation makes the tables in a new file, then marks the list incomplete. The
        // file goes once more between the two, when the tables are there and the mark is not written yet.
        File.Delete(options.ApplicationDatabasePath);
        void DeleteOnceTheTablesAreMade()
        {
            if (database.Generation == 1 && HasSessionTable(options.ApplicationDatabasePath))
            {
                File.Delete(options.ApplicationDatabasePath);
            }
            else if (database.Generation < 2)
            {
                clock.OnTimestamp = DeleteOnceTheTablesAreMade;
            }
        }

        clock.OnTimestamp = DeleteOnceTheTablesAreMade;
        var sessions = await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(2, database.Generation);
        Assert.AreEqual(1, sessions.Length);
        Assert.AreEqual("session-kept-in-its-journal", sessions[0].SessionId);
    }

    [TestMethod]
    public async Task ListSessionsAsync_AfterOnlyThePagesOfTheSessionTableAreDamaged_RebuildsTheListAndKeepsThePluginData()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        await using var database = ApplicationDatabase.Create(options);
        var store = new SessionViewJournalStore(options, database).CreateSessionStore();
        for (var index = 0; index < 5; index++)
        {
            await store.UpsertSessionAsync(CreateSummary($"session-pages-{index}", updatedAt: $"2026-06-18T12:0{index}:00+00:00")).ConfigureAwait(false);
        }

        Assert.AreEqual(5, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
        await database.MigrateAsync("plugin:test", "test_", 1, async (connection, from, to, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE test_facts (id INTEGER); INSERT INTO test_facts VALUES (11);";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Assert.IsNotNull(await database.BackupAsync().ConfigureAwait(false));

        // The root page of the session table is overwritten; the pages of the plugin table are not touched.
        long rootPage;
        long pageSize;
        await using (var connection = new SqliteConnection($"Data Source={options.ApplicationDatabasePath};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = 'session_projection_cache';";
            rootPage = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
            command.CommandText = "PRAGMA page_size;";
            pageSize = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        }

        await using (var stream = new FileStream(options.ApplicationDatabasePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = (rootPage - 1) * pageSize;
            await stream.WriteAsync(Enumerable.Repeat((byte)0xFF, (int)pageSize).ToArray()).ConfigureAwait(false);
        }

        var sessions = await new SessionViewJournalStore(options, database).CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(5, sessions.Length, "The list is read again from the journals.");
        Assert.AreEqual(11L, await database.ReadAsync("plugin:test", async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM test_facts;";
            return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        }).ConfigureAwait(false), "The data of the plugin survives, by dropping the session tables or by the copy.");
        Assert.AreEqual(5, (await new SessionViewJournalStore(options, database).CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
    }

    [TestMethod]
    public async Task ListSessionsAsync_NeverDeletesThePluginTablesOfAValidFile()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        await using var database = ApplicationDatabase.Create(options);
        await database.MigrateAsync("plugin:test", "test_", 1, async (connection, from, to, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE test_facts (id INTEGER); INSERT INTO test_facts VALUES (7);";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        var store = new SessionViewJournalStore(options, database).CreateSessionStore();
        await store.UpsertSessionAsync(CreateSummary("session-plugin-neighbour", updatedAt: "2026-06-18T11:10:00+00:00")).ConfigureAwait(false);

        // A rebuild of the session list (the cache is marked incomplete) leaves the other tables of the file alone.
        await using (var connection = new SqliteConnection($"Data Source={options.ApplicationDatabasePath};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE session_projection_cache_metadata SET value = '0';";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var sessions = await new SessionViewJournalStore(options, database).CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(1, sessions.Length);
        Assert.AreEqual(7L, await database.ReadAsync("plugin:test", async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM test_facts;";
            return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        }).ConfigureAwait(false));
        Assert.IsNull(database.LastRecovery);
    }

    [TestMethod]
    public async Task ListSessionsAsync_PrunesRowsWhoseJournalWasDeleted()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var catalog = new SessionViewCatalog(options);
        var store = catalog.JournalStore.CreateSessionStore();
        var session = CreateSummary("session-stale", updatedAt: "2026-06-18T12:00:00+00:00");
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);

        var journalPath = new AgentRuntimePathLayout(temp.Path).GetSessionFilePath(session.SessionId, session.CreatedAt);
        File.Delete(journalPath);

        var afterDelete = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        var secondRead = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(0, afterDelete.Length);
        Assert.AreEqual(0, secondRead.Length, "The stale cache row should be deleted, not just filtered once.");
    }

    [TestMethod]
    public async Task ReconcileCacheAsync_ImportsExternalJournalAdditionsAndChanges()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var layout = new AgentRuntimePathLayout(temp.Path);
        var cachedCatalog = new SessionViewCatalog(options);
        var cachedStore = cachedCatalog.JournalStore.CreateSessionStore();
        var original = CreateSummary("session-original", updatedAt: "2026-06-18T13:00:00+00:00");
        await cachedStore.UpsertSessionAsync(original).ConfigureAwait(false);
        Assert.AreEqual(1, (await cachedStore.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);

        var externalStore = new FileSystemAgentSessionStore(layout);
        var external = CreateSummary("session-external", updatedAt: "2026-06-18T13:05:00+00:00") with
        {
            Summary = "external first summary",
        };
        await externalStore.UpsertSessionAsync(external).ConfigureAwait(false);

        var beforeReconcile = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(1, beforeReconcile.Length);

        var reconcileStore = new SessionViewCatalog(options).JournalStore.CreateSessionStore();
        var result = await reconcileStore.ReconcileCacheAsync().ConfigureAwait(false);
        Assert.IsTrue(result.Changed);

        var afterAdd = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);
        CollectionAssert.AreEquivalent(new[] { "session-original", "session-external" }, afterAdd.Select(static item => item.SessionId).ToArray());

        var changed = external with
        {
            UpdatedAt = DateTimeOffset.Parse("2026-06-18T13:10:00+00:00"),
            Summary = "external changed summary",
        };
        await externalStore.UpsertSessionAsync(changed).ConfigureAwait(false);
        var changeResult = await reconcileStore.ReconcileCacheAsync().ConfigureAwait(false);
        var afterChange = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.IsTrue(changeResult.Changed);
        Assert.AreEqual("external changed summary", afterChange.Single(static item => item.SessionId == "session-external").Summary);
    }

    [TestMethod]
    public async Task ReconcileCacheAsync_WhenTheFileIsReplacedWhileItRuns_DoesNotCallTheListComplete()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var clock = new HookClock();
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
        {
            DatabasePath = options.ApplicationDatabasePath,
            BackupDirectory = options.ApplicationDatabaseBackupRoot,
            TimeProvider = clock,
        });
        var store = new SessionViewJournalStore(options, database).CreateSessionStore();
        await store.UpsertSessionAsync(CreateSummary("session-listed-1", updatedAt: "2026-06-18T13:00:00+00:00")).ConfigureAwait(false);
        await store.UpsertSessionAsync(CreateSummary("session-listed-2", updatedAt: "2026-06-18T13:01:00+00:00")).ConfigureAwait(false);
        Assert.AreEqual(2, (await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
        var externalStore = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await externalStore.UpsertSessionAsync(CreateSummary("session-external-1", updatedAt: "2026-06-18T13:05:00+00:00")).ConfigureAwait(false);
        await externalStore.UpsertSessionAsync(CreateSummary("session-external-2", updatedAt: "2026-06-18T13:06:00+00:00")).ConfigureAwait(false);

        // The file goes once the first of the two new sessions is written, before the second one is: the rows that
        // were there, and that the reconciliation found up to date, are gone with it.
        var asked = 0;
        void DeleteOnceARowIsAdded()
        {
            // The clock is asked twice for a write: as it starts, before its connection opens, and as it ends.
            var starting = ++asked % 2 == 1;
            if (starting && CountSessionRows(options.ApplicationDatabasePath) == 3)
            {
                File.Delete(options.ApplicationDatabasePath);
                return;
            }

            clock.OnTimestamp = DeleteOnceARowIsAdded;
        }

        clock.OnTimestamp = DeleteOnceARowIsAdded;
        await store.ReconcileCacheAsync().ConfigureAwait(false);
        clock.OnTimestamp = null;
        var sessions = await store.ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(1, database.Generation, "The file was replaced under the reconciliation.");
        CollectionAssert.AreEquivalent(
            new[] { "session-listed-1", "session-listed-2", "session-external-1", "session-external-2" },
            sessions.Select(static item => item.SessionId).ToArray(),
            "A list that lost rows on the way is not complete: it is made again from the journals.");
    }

    [TestMethod]
    public async Task WriteThrough_ProjectsLocalStateLineageModelReasoningAndDelete()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var catalog = new SessionViewCatalog(options);
        var store = catalog.JournalStore.CreateSessionStore();
        var session = CreateSummary("session-write-through", updatedAt: "2026-06-18T14:00:00+00:00") with
        {
            ModelId = "gpt-5.4",
            ReasoningEffort = AgentReasoningEffort.High,
            AgentPromptId = "default",
            ParentSessionId = "agent-parent",
            CreatedBySessionId = "agent-creator",
            CreatedByRunId = new AgentRunId("run-create"),
        };
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        await store.UpsertStateAsync(CreateState(session, "resp_write")).ConfigureAwait(false);
        var descriptor = CreateDescriptor(session);
        var createdBy = new AltaActorProvenance
        {
            Kind = "agent",
            SourceSessionId = "local-creator",
            CreatedAt = DateTimeOffset.Parse("2026-06-18T14:01:00+00:00"),
        };
        await catalog.JournalStore.AppendStateAsync(
                descriptor,
                new SessionViewLocalState
                {
                    ProviderKey = "local-provider",
                    ModelId = "local-model",
                    ReasoningEffort = AgentReasoningEffort.Low,
                    AgentPromptId = "local-prompt",
                    Archived = true,
                    MessageCount = 42,
                    ParentSessionId = "local-parent",
                    CreatedBy = createdBy,
                })
            .ConfigureAwait(false);

        var metadata = (await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Single();
        Assert.AreEqual(AgentReasoningEffort.High, metadata.ReasoningEffort);
        Assert.AreEqual("gpt-5.4", metadata.ModelId);
        Assert.AreEqual("agent-parent", metadata.ParentSessionId);
        Assert.AreEqual("agent-creator", metadata.CreatedBySessionId);
        Assert.AreEqual(new AgentRunId("run-create"), metadata.CreatedByRunId);
        Assert.AreEqual("resp_write", ((RawApiSessionMetadataDetails?)metadata.Details)?.ProviderSessionId);
        Assert.IsNotNull(metadata.ViewState);
        Assert.AreEqual("local-provider", metadata.ViewState.ProviderKey);
        Assert.AreEqual("local-model", metadata.ViewState.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Low, metadata.ViewState.ReasoningEffort);
        Assert.AreEqual("local-prompt", metadata.ViewState.AgentPromptId);
        Assert.IsTrue(metadata.ViewState.Archived);
        Assert.AreEqual(42, metadata.ViewState.MessageCount);
        Assert.AreEqual("local-parent", metadata.ViewState.ParentSessionId);
        StringAssert.Contains(metadata.ViewState.CreatedByJson, "local-creator");

        Assert.IsTrue(await store.DeleteSessionAsync(session.SessionId).ConfigureAwait(false));
        Assert.AreEqual(0, (await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);
    }

    [TestMethod]
    public async Task PermissionModeOfASession_IsKeptInTheJournalAndTheCache_AndAnOlderStateHasNone()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var catalog = new SessionViewCatalog(options);
        var session = CreateSummary("session-permission-mode", updatedAt: "2026-06-18T16:00:00+00:00");
        await catalog.JournalStore.CreateSessionStore().UpsertSessionAsync(session).ConfigureAwait(false);
        var descriptor = CreateDescriptor(session);
        await catalog.JournalStore.AppendStateAsync(descriptor, new SessionViewLocalState { ProviderKey = "openai", PermissionMode = "acceptEdits" })
            .ConfigureAwait(false);

        // Read twice: the second read is the copy the store keeps of the last state.
        Assert.AreEqual("acceptEdits", (await catalog.JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt).ConfigureAwait(false))!.PermissionMode);
        Assert.AreEqual("acceptEdits", (await catalog.JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt).ConfigureAwait(false))!.PermissionMode);
        Assert.AreEqual("acceptEdits", (await ListSingleAsync()).ViewState!.PermissionMode);

        // A cache rebuilt from the journals reads it there.
        File.Delete(options.ApplicationDatabasePath);
        Assert.AreEqual("acceptEdits", (await ListSingleAsync()).ViewState!.PermissionMode);

        // A state written before the choice existed names no mode: the session runs in the one of its provider.
        await catalog.JournalStore.AppendStateAsync(descriptor, new SessionViewLocalState { ProviderKey = "openai" }).ConfigureAwait(false);
        var journalPath = new AgentRuntimePathLayout(temp.Path).GetSessionFilePath(session.SessionId, session.CreatedAt);
        var older = System.Text.RegularExpressions.Regex.Replace(File.ReadAllLines(journalPath)[^1], ",?\"permission_mode\":null", string.Empty);
        Assert.IsFalse(older.Contains("permission_mode", StringComparison.Ordinal));
        await File.AppendAllTextAsync(journalPath, older + Environment.NewLine).ConfigureAwait(false);
        Assert.IsNull((await new SessionViewCatalog(options).JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt).ConfigureAwait(false))!.PermissionMode);

        async Task<AgentSessionMetadata> ListSingleAsync()
            => (await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Single();
    }

    [TestMethod]
    public async Task CacheWrittenBeforeThePermissionMode_IsGivenItsColumnAndReadAgainFromTheJournals()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var catalog = new SessionViewCatalog(options);
        var session = CreateSummary("session-cache-migration", updatedAt: "2026-06-18T17:00:00+00:00");
        await catalog.JournalStore.CreateSessionStore().UpsertSessionAsync(session).ConfigureAwait(false);
        await catalog.JournalStore.AppendStateAsync(CreateDescriptor(session), new SessionViewLocalState { ProviderKey = "openai", PermissionMode = "dontAsk" })
            .ConfigureAwait(false);
        Assert.AreEqual(1, (await catalog.JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Length);

        // The cache of a version that did not keep the mode: no column, a row without it, and no version recorded for the owner.
        await using (var connection = new SqliteConnection($"Data Source={options.ApplicationDatabasePath};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE session_projection_cache DROP COLUMN local_permission_mode; DELETE FROM app_meta WHERE owner = 'session_cache';";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var sessions = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual("dontAsk", sessions.Single().ViewState!.PermissionMode);
    }

    [TestMethod]
    public async Task ListSessionsAsync_ToleratesCorruptJournalsDuringRebuild()
    {
        using var temp = TestTempDirectory.Create();
        var options = CreateOptions(temp.Path);
        var layout = new AgentRuntimePathLayout(temp.Path);
        var corruptPath = layout.GetSessionFilePath("session-corrupt-journal", DateTimeOffset.Parse("2026-06-18T15:00:00+00:00"));
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        await File.WriteAllTextAsync(corruptPath, "not-json" + Environment.NewLine + "also-not-json").ConfigureAwait(false);

        var sessions = await new SessionViewCatalog(options).JournalStore.CreateSessionStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(0, sessions.Length);
        Assert.IsTrue(File.Exists(options.ApplicationDatabasePath));
    }

    private static CatalogOptions CreateOptions(string globalRoot)
        => new() { GlobalRoot = globalRoot };

    private static bool HasSessionTable(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return false;
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'session_projection_cache';";
        return (long)command.ExecuteScalar()! > 0;
    }

    private static long CountSessionRows(string databasePath)
    {
        if (!HasSessionTable(databasePath))
        {
            return 0;
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_projection_cache;";
        return (long)command.ExecuteScalar()!;
    }

    // A clock that runs an action once, the next time the database starts the time of a write: the place of a test
    // between the moment the database saw its file and the moment the connection of the write opens it.
    private sealed class HookClock : TimeProvider
    {
        public Action? OnTimestamp { get; set; }

        public override long GetTimestamp()
        {
            var action = OnTimestamp;
            OnTimestamp = null;
            action?.Invoke();
            return base.GetTimestamp();
        }
    }

    private static AgentSessionSummary CreateSummary(string sessionId, string updatedAt)
        => new()
        {
            SessionId = sessionId,
            ProviderId = ModelProviderIds.OpenAIResponses,
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ModelId = "gpt-5",
            ReasoningEffort = AgentReasoningEffort.Medium,
            AgentPromptId = "default",
            WorkingDirectory = @"C:\repo\sqlite-cache-tests",
            Title = "SQLite cache test",
            Summary = "cached summary",
            CreatedAt = DateTimeOffset.Parse("2026-06-18T09:00:00+00:00"),
            UpdatedAt = DateTimeOffset.Parse(updatedAt),
        };

    private static AgentSessionState CreateState(AgentSessionSummary summary, string providerSessionId)
        => new()
        {
            SessionId = summary.SessionId,
            ProtocolFamily = summary.ProtocolFamily,
            ProviderKey = summary.ProviderKey,
            ProviderSessionId = providerSessionId,
            UpdatedAt = summary.UpdatedAt.AddMinutes(1),
        };

    private static SessionViewDescriptor CreateDescriptor(AgentSessionSummary summary)
        => new()
        {
            SessionId = summary.SessionId,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = summary.ProviderId.Value,
            ProviderKey = summary.ProviderKey,
            ProjectRef = "project-cache-test",
            ParentSessionId = summary.ParentSessionId,
            WorkingDirectory = summary.WorkingDirectory!,
            Title = summary.Title!,
            Status = SessionViewStatus.Active,
            CreatedAt = summary.CreatedAt,
            UpdatedAt = summary.UpdatedAt,
            LastActiveAt = summary.UpdatedAt,
            StartedAt = summary.CreatedAt,
            LatestSummary = summary.Summary,
            ModelId = summary.ModelId,
            ReasoningEffort = summary.ReasoningEffort,
            AgentPromptId = summary.AgentPromptId,
        };
}
