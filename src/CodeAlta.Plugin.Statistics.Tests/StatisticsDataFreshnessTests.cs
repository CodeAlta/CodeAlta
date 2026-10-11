using System.Globalization;
using System.Text.Json;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Journal;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugin.Statistics.Store;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Plugin.Statistics.Tests;

[TestClass]
public sealed class StatisticsDataFreshnessTests
{
    [TestMethod]
    public async Task EmptyStatisticsHaveNoInventedUpdateTime()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        Assert.IsNull(await UpdatedAtAsync(harness.Queries));
        harness.Time.Advance(TimeSpan.FromDays(1));
        Assert.IsNull(await UpdatedAtAsync(harness.Queries));
    }

    [TestMethod]
    public async Task UpdateTimeComesFromCommittedData_NotTheQueryClock_AndSurvivesRestart()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using var harness = await QueryHarness.CreateAsync(sessions: 1, entries: 1);
        var updated = await UpdatedAtAsync(harness.Queries);
        Assert.IsNotNull(updated, "The overview must receive the stored data-update time.");
        Assert.IsTrue(updated >= before && updated <= DateTimeOffset.UtcNow);
        harness.Time.Advance(TimeSpan.FromDays(30));
        Assert.AreEqual(updated, await UpdatedAtAsync(harness.Queries), "Asking again is not a data update.");
        await harness.Store.ReopenAsync();
        var reopened = new StatisticsQueries(harness.Store.Store, harness.Directory, harness.Time);
        Assert.AreEqual(updated, await UpdatedAtAsync(reopened), "Opening an existing database must retain its freshness.");
    }

    [TestMethod]
    public async Task OlderDatabasesUseTheirExistingJournalUpdateTime()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 1, entries: 1);
        var updated = new DateTimeOffset(2026, 9, 10, 12, 34, 56, TimeSpan.Zero);
        await harness.Store.Store.SetMetaAsync(new Dictionary<string, string?> { ["data.updated_ms"] = null });
        await harness.Store.Store.WriteAsync(sql => sql.Execute($"UPDATE {harness.Store.Store.Prefix}journal SET updated_ms=@p0", updated.ToUnixTimeMilliseconds()));
        Assert.AreEqual(updated, await UpdatedAtAsync(harness.Queries));
    }

    [TestMethod]
    public async Task ForgettingTheLastSessionRecordsTheUpdate_EvenWhenNoJournalsRemain()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 1, entries: 1);
        await harness.Store.Store.MarkDeletedAsync(["session-0"]);
        var earlier = new DateTimeOffset(2026, 1, 1, 1, 2, 3, TimeSpan.Zero);
        await harness.Store.Store.SetMetaAsync(new Dictionary<string, string?>
        {
            ["data.updated_ms"] = earlier.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        });
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var forgotten = await harness.Store.Store.ForgetDeletedAsync();
        Assert.AreEqual(1, forgotten.Sessions);
        var updated = await UpdatedAtAsync(harness.Queries);
        Assert.IsNotNull(updated);
        Assert.IsTrue(updated >= before && updated <= DateTimeOffset.UtcNow);
        await harness.Store.Store.ForgetDeletedAsync();
        Assert.AreEqual(updated, await UpdatedAtAsync(harness.Queries), "A no-op is not a data update.");
    }

    [TestMethod]
    public async Task RestoringTheSameArchiveDoesNotAdvanceFreshness_EvenInAnOlderDatabase()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var batch = SyntheticFacts.Batch(new Random(212), "saved-session", SyntheticFacts.SpringStart, 96, 3);
        await harness.Store.ApplyAsync(new ApplyRequest { SessionId = "saved-session", Batch = batch, Cursor = JournalCursor.Start });
        await harness.Store.MarkDeletedAsync(["saved-session"]);
        await harness.Store.ResetAsync(); // Preserve the deleted session and import its durable archive.
        var updated = await UpdatedAtAsync(new StatisticsQueries(harness.Store));
        Assert.IsNotNull(updated);
        await harness.ReopenAsync();
        Assert.AreEqual(updated, await UpdatedAtAsync(new StatisticsQueries(harness.Store)));

        await harness.Store.SetMetaAsync(new Dictionary<string, string?> { ["data.updated_ms"] = null });
        updated = await UpdatedAtAsync(new StatisticsQueries(harness.Store));
        await harness.ReopenAsync();
        Assert.AreEqual(updated, await UpdatedAtAsync(new StatisticsQueries(harness.Store)), "An old journal's saved update time also survives an unchanged archive replay.");
    }

    [TestMethod]
    public async Task DataAndTimestampCommitTogether_AndNoOpDeletionDoesNotAdvanceTime()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 1, entries: 1);
        var store = harness.Store.Store;
        var earlier = new DateTimeOffset(2026, 1, 1, 1, 2, 3, TimeSpan.Zero);
        await store.SetMetaAsync(new Dictionary<string, string?> { ["data.updated_ms"] = earlier.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) });
        await store.WriteAsync(sql => sql.Execute($"CREATE TRIGGER reject_freshness BEFORE UPDATE ON {store.Table("meta")} WHEN NEW.key='data.updated_ms' BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END"));
        await Assert.ThrowsAsync<SqliteException>(async () => await store.MarkDeletedAsync(["session-0"]));
        Assert.IsFalse((await store.GetJournalAsync("session-0"))!.Deleted, "A failed freshness write rolls back the data change too.");
        Assert.AreEqual(earlier, await UpdatedAtAsync(harness.Queries));
        await store.WriteAsync(sql => sql.Execute("DROP TRIGGER reject_freshness"));

        await store.MarkDeletedAsync(["session-0"]);
        var updated = await UpdatedAtAsync(harness.Queries);
        Assert.IsTrue(updated > earlier);
        await store.MarkDeletedAsync(["session-0", "unknown-session"]);
        Assert.AreEqual(updated, await UpdatedAtAsync(harness.Queries));
    }

    private static async Task<DateTimeOffset?> UpdatedAtAsync(StatisticsQueries queries)
    {
        var summary = await queries.SummaryAsync(new StatisticsRequest { Period = "all" });
        using var json = JsonDocument.Parse(StatisticsJson.Serialize(summary));
        return json.RootElement.TryGetProperty("dataUpdatedAt", out var value) ? value.GetDateTimeOffset() : null;
    }
}
