using System.Security.Cryptography;
using CodeAlta.Agent;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class StatisticsArchiveStoreTests
{
    [TestMethod]
    public async Task ImmutableGenerations_ReopenExactlyOnce_AndRemainSeparateFromApplicationDatabase()
    {
        using var fixture = new Fixture();
        var source = new SessionStatisticsSource("s", Path.Combine(fixture.Root, "sessions", "s.jsonl"), 42, DateTimeOffset.UnixEpoch, Convert.ToHexString(SHA256.HashData("source"u8)));
        var first = await fixture.Store.SaveAsync("s", [1, 2, 3], source);
        var repeat = await fixture.Store.SaveAsync("s", [1, 2, 3], source);
        Assert.AreEqual(first.Generation, repeat.Generation);
        var second = await fixture.Store.SaveAsync("s", [4, 5, 6], source with { Sha256 = Convert.ToHexString(SHA256.HashData("next"u8)) });
        Assert.AreNotEqual(first.Generation, second.Generation);
        var reopened = await new StatisticsArchiveStore(fixture.Root).ReadAsync();
        Assert.AreEqual(second.Generation, reopened.Single().Generation);
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, reopened.Single().Payload);
        Assert.AreEqual(2L, fixture.Scalar("SELECT COUNT(*) FROM archive"));
        Assert.AreEqual("delete", fixture.Scalar("PRAGMA journal_mode"));
        Assert.AreEqual(Path.Combine(fixture.Root, "saved_statistics_after_deleted_sessions", "archives.sqlite3"), fixture.Store.DatabasePath);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "data")));
    }

    [TestMethod]
    [DataRow("UPDATE archive SET payload=x'0000'")]
    [DataRow("PRAGMA user_version=999")]
    [DataRow("DELETE FROM effective")]
    [DataRow("UPDATE effective SET session_id='another'")]
    public async Task DamagedOrUnsupportedArchive_IsNeverRecreatedOrSilentlyOmitted(string damage)
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync("s", [1, 2, 3], null);
        fixture.Scalar(damage);
        var before = await File.ReadAllBytesAsync(fixture.Store.DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new StatisticsArchiveStore(fixture.Root).ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Store.SaveAsync("s", [4, 5, 6], null));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Store.DatabasePath));
    }

    [TestMethod]
    public async Task MissingOrEmptyExistingArchive_FailsClosed()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync("s", [1], null);
        File.Delete(fixture.Store.DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Store.ReadAsync());
        await File.WriteAllBytesAsync(fixture.Store.DatabasePath, []);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Store.ReadAsync());
        Assert.AreEqual(0, new FileInfo(fixture.Store.DatabasePath).Length);
    }

    [TestMethod]
    public async Task MissingEntireArchiveDirectory_FailsClosedAcrossReopen()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync("s", [1, 2, 3], null);
        var directory = Path.GetDirectoryName(fixture.Store.DatabasePath)!;
        Directory.Delete(directory, recursive: true);

        var reopened = new StatisticsArchiveStore(fixture.Root);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reopened.ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reopened.SaveAsync("another", [4, 5, 6], null));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Store.ReadAsync());
        Assert.IsFalse(Directory.Exists(directory), "Missing initialized storage must not be recreated.");
    }

    [TestMethod]
    public async Task MissingEmptyArchiveDirectory_IsNotFirstUseAfterReopen()
    {
        using var fixture = new Fixture();
        Assert.IsEmpty(await fixture.Store.ReadAsync());
        Directory.Delete(Path.GetDirectoryName(fixture.Store.DatabasePath)!, recursive: true);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new StatisticsArchiveStore(fixture.Root).ReadAsync());
    }

    [TestMethod]
    public async Task ExistingArchiveWithoutMarker_IsAdoptedBeforeAcknowledgingItsFacts()
    {
        using var fixture = new Fixture();
        var entry = await fixture.Store.SaveAsync("s", [1, 2, 3], null);
        var markerPath = Path.Combine(fixture.Root, "saved_statistics_after_deleted_sessions.initialized");
        Assert.IsTrue(File.Exists(markerPath));
        File.Delete(markerPath); // An archive created before the initialization marker was introduced.
        var before = await File.ReadAllBytesAsync(fixture.Store.DatabasePath);

        var reopened = new StatisticsArchiveStore(fixture.Root);
        Assert.AreEqual(entry.Generation, (await reopened.ReadAsync()).Single().Generation);
        Assert.IsTrue(File.Exists(markerPath));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Store.DatabasePath));
        Directory.Delete(Path.GetDirectoryName(fixture.Store.DatabasePath)!, recursive: true);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new StatisticsArchiveStore(fixture.Root).ReadAsync());
    }

    [TestMethod]
    public async Task InterruptedInitialization_WithoutArchiveDirectory_FailsClosed()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        // Even an interruption before the marker bytes were written is initialization evidence.
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "saved_statistics_after_deleted_sessions.initialized"), []);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new StatisticsArchiveStore(fixture.Root).ReadAsync());
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(fixture.Store.DatabasePath)));
    }

    [TestMethod]
    public async Task LockedArchive_CannotIssueSuccessOrReplaceEarlierBytes()
    {
        using var fixture = new Fixture();
        var entry = await fixture.Store.SaveAsync("s", [1], null);
        var before = await File.ReadAllBytesAsync(fixture.Store.DatabasePath);
        // FileShare.None does not exclude native SQLite on Unix. Hold its actual writer lock
        // so this tests an unavailable archive on every platform, not Windows sharing semantics.
        using (var locked = new SqliteConnection($"Data Source={fixture.Store.DatabasePath};Pooling=False"))
        {
            locked.Open();
            using var transaction = locked.BeginTransaction(deferred: false);
            var error = await Assert.ThrowsAsync<SqliteException>(async () => await fixture.Store.SaveAsync("s", [2], null));
            Assert.AreEqual(5, error.SqliteErrorCode); // SQLITE_BUSY
        }

        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Store.DatabasePath));
        Assert.AreEqual(entry.Generation, (await fixture.Store.ReadAsync()).Single().Generation);
    }

    [TestMethod]
    public async Task NormalAndDeveloperRoots_DoNotShareArchiveState()
    {
        using var fixture = new Fixture();
        var developer = new StatisticsArchiveStore(Path.Combine(fixture.Root, "dev"));
        await fixture.Store.SaveAsync("normal", [1], null);
        await developer.SaveAsync("developer", [2], null);
        Assert.AreEqual("normal", (await fixture.Store.ReadAsync()).Single().SessionId);
        Assert.AreEqual("developer", (await developer.ReadAsync()).Single().SessionId);
    }

    [TestMethod]
    public async Task DeletionMetadata_IsIdempotentAndBoundToEffectiveGeneration()
    {
        using var fixture = new Fixture();
        var source = new SessionStatisticsSource("s", Path.Combine(fixture.Root, "s.jsonl"), 42, DateTimeOffset.UnixEpoch, Convert.ToHexString(SHA256.HashData("source"u8)));
        var entry = await fixture.Store.SaveAsync("s", [1], source);
        var receipt = new SessionStatisticsRetirementReceipt(source, entry.Generation, entry.PayloadSha256);
        Assert.IsTrue(await fixture.Store.RecordDeletionAsync(receipt, artifactsRemaining: true));
        Assert.AreEqual(1L, fixture.Scalar("SELECT source_deleted FROM effective"));
        Assert.AreEqual(1L, fixture.Scalar("SELECT artifacts_remaining FROM effective"));
        Assert.IsTrue(await fixture.Store.RecordDeletionAsync(receipt, artifactsRemaining: false));
        Assert.AreEqual(0L, fixture.Scalar("SELECT artifacts_remaining FROM effective"));
        await fixture.Store.SaveAsync("s", [2], source);
        Assert.IsFalse(await fixture.Store.RecordDeletionAsync(receipt, artifactsRemaining: false));
        Assert.AreEqual(0L, fixture.Scalar("SELECT source_deleted FROM effective"));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CodeAlta.Catalog.Tests", Guid.NewGuid().ToString("N"));
        public StatisticsArchiveStore Store { get; }
        public Fixture() => Store = new StatisticsArchiveStore(Root);

        public object? Scalar(string text)
        {
            using var connection = new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = text;
            return command.ExecuteScalar();
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
