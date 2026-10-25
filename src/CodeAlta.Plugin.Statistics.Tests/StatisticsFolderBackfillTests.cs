using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// The sessions read before the store kept the key of their folder get it from the header of their journal, once, in the background,
/// without their facts being read again.
/// </summary>
[TestClass]
public sealed class StatisticsFolderBackfillTests
{
    private static readonly DateTimeOffset Start = EngineHarness.Now.AddDays(-3);

    [TestMethod]
    public async Task ASessionWithoutAFolderKey_GetsItFromItsHeader_AndTheKeyNeverHoldsThePath()
    {
        await using var harness = await EngineHarness.CreateAsync();
        Assert.IsNull(await harness.Store.Store.GetMetaAsync(StatisticsStore.FolderBackfillKey), "A new store has no session to fill in.");
        harness.Journals.Set(EngineHarness.Session("a", Start), Start.AddHours(1));
        harness.Journals.Set(EngineHarness.Session("gone", Start), Start.AddHours(1));
        // A journal whose first line is no header: its runs are counted, its folder is not known.
        harness.Journals.Set(EngineHarness.AddRuns(new JournalBuilder("headless", "codex").State(Start), Start, 1), Start.AddHours(1));
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        var key = SessionFolders.Key(@"C:\placeholder");
        Assert.AreEqual(key, await FolderKeyAsync(harness, "a"), "A session read now gets the key from its header as it is read.");

        // The store as an older version left it: sessions without a key, and the backfill to do. One journal is gone since.
        await harness.Store.Store.WriteAsync(sql => sql.Execute($"UPDATE {harness.Store.Store.Prefix}session SET folder_key = NULL"));
        await harness.Store.Store.SetMetaAsync(new Dictionary<string, string?> { [StatisticsStore.FolderBackfillKey] = string.Empty });
        harness.Journals.Remove("gone");
        var tokens = await harness.SumAsync("usage_q", "input_tokens");
        var opened = new List<(string SessionId, long Offset)>();
        harness.Journals.Opening = (sessionId, offset) => opened.Add((sessionId, offset));
        await harness.RestartAsync();

        await harness.Engine.DrainAsync();

        Assert.AreEqual(key, await FolderKeyAsync(harness, "a"), "The key comes from the header.");
        Assert.IsFalse(key!.Contains("placeholder", StringComparison.OrdinalIgnoreCase), "The key is a hash, never the path.");
        Assert.IsNull(await FolderKeyAsync(harness, "headless"), "A journal that does not start with a header gives no key.");
        Assert.IsNull(await FolderKeyAsync(harness, "gone"), "A journal that is gone gives no key.");
        Assert.IsNull(await harness.Store.Store.GetMetaAsync(StatisticsStore.FolderBackfillKey), "The backfill is done once.");
        CollectionAssert.AreEquivalent(new[] { ("a", 0L), ("gone", 0L), ("headless", 0L) }, opened, "One opening of each session, for its first line.");
        Assert.AreEqual(tokens, await harness.SumAsync("usage_q", "input_tokens"), "No facts are read again.");

        // A later reading from the saved state, which has no key, keeps the key the backfill gave.
        harness.Journals.Append("a", EngineHarness.More("a", Start.AddHours(2)), Start.AddHours(3));
        harness.Engine.Signal("a");
        await harness.Engine.DrainAsync();
        Assert.AreEqual(key, await FolderKeyAsync(harness, "a"));

        opened.Clear();
        await harness.RestartAsync();
        await harness.Engine.DrainAsync();
        Assert.IsEmpty(opened, "Nothing is read again at the next start.");
    }

    [TestMethod]
    public async Task APause_StopsTheBackfill_AndResumeFinishesIt()
    {
        await using var harness = await EngineHarness.CreateAsync();
        harness.Journals.Set(EngineHarness.Session("a", Start), Start.AddHours(1));
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        await harness.Store.Store.WriteAsync(sql => sql.Execute($"UPDATE {harness.Store.Store.Prefix}session SET folder_key = NULL"));
        // The history was paused when the application closed.
        await harness.Store.Store.SetMetaAsync(new Dictionary<string, string?> { [StatisticsStore.FolderBackfillKey] = string.Empty, ["history.paused"] = "1" });
        await harness.RestartAsync();

        await harness.Engine.DrainAsync();
        Assert.AreEqual(HistoryState.Paused, harness.Engine.Status.State);
        Assert.IsNull(await FolderKeyAsync(harness, "a"), "A paused history reads nothing.");

        await harness.Engine.ResumeAsync();
        await harness.Engine.DrainAsync();
        Assert.AreEqual(SessionFolders.Key(@"C:\placeholder"), await FolderKeyAsync(harness, "a"));
    }

    private static async Task<string?> FolderKeyAsync(EngineHarness harness, string sessionId)
        => await harness.Store.Store.ReadAsync(sql => sql.Scalar($"SELECT folder_key FROM {harness.Store.Store.Prefix}session WHERE session_id = @p0", sessionId) as string);
}
