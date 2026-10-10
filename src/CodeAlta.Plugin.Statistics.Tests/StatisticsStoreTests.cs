using System.Diagnostics;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Tests;

[TestClass]
public sealed class StatisticsStoreTests
{
    // Zones with whole-hour, half-hour and quarter-hour offsets, daylight saving and none, north and south.
    public static IEnumerable<string> ZoneIds { get; } =
    [
        "UTC", "Europe/Paris", "America/New_York", "Asia/Kolkata", "America/St_Johns", "Asia/Kathmandu", "Australia/Lord_Howe", "Pacific/Chatham", "Pacific/Kiritimati",
    ];

    public static IEnumerable<object[]> Zones => ZoneIds.Select(static id => new object[] { id });

    private static ApplyRequest Request(FactBatch batch, string session, long offset = 100, bool replace = false, int floor = int.MinValue)
        => new()
        {
            SessionId = session,
            Batch = batch,
            Cursor = new JournalCursor(offset, null, new SessionFactsState()),
            Replace = replace,
            FileLength = offset,
            FileStampTicks = 1,
            FloorQuarter = floor,
        };

    [TestMethod]
    [DynamicData(nameof(Zones))]
    public async Task Rollups_AreTheSumsOfTheQuarters_InEveryZone(string zoneId)
    {
        await using var harness = await StoreHarness.CreateAsync(StoreHarness.Zone(zoneId));
        var random = new Random(42);
        for (var session = 0; session < 6; session++)
        {
            // Several catch-ups of the same session, each adding to the rows of the one before.
            for (var step = 0; step < 4; step++)
            {
                var id = "session-" + session;
                var result = await harness.Store.ApplyAsync(Request(SyntheticFacts.Batch(random, id, SyntheticFacts.SpringStart + (session * 500), 235 * 96, 120), id, 100 + step));
                Assert.IsGreaterThan(0, result.Rows);
                Assert.IsNotEmpty(result.ChangedDays);
            }
        }

        await harness.VerifyRollupsAsync();
    }

    [TestMethod]
    public async Task ReadingInHalves_GivesTheSameRowsAsReadingAtOnce()
    {
        var random = new Random(7);
        var parts = Enumerable.Range(0, 5).Select(_ => SyntheticFacts.Batch(random, "s", SyntheticFacts.SpringStart, 60 * 96, 80)).ToArray();
        var whole = new FactBatch("s");
        foreach (var part in parts)
        {
            whole.Merge(part);
        }

        await using var split = await StoreHarness.CreateAsync(StoreHarness.Zone("Europe/Paris"));
        await using var once = await StoreHarness.CreateAsync(StoreHarness.Zone("Europe/Paris"));
        foreach (var part in parts)
        {
            await split.Store.ApplyAsync(Request(part, "s"));
        }

        await once.Store.ApplyAsync(Request(whole, "s"));

        Assert.AreEqual(await once.DumpAsync("run", "session"), await split.DumpAsync("run", "session"));
        await split.VerifyRollupsAsync();
    }

    [TestMethod]
    public async Task ASessionThatIsReplaced_LeavesNothingOfItsOldFacts_AndTheRollupsFollow()
    {
        var random = new Random(11);
        var other = SyntheticFacts.Batch(random, "other", SyntheticFacts.SpringStart, 40 * 96, 60);
        var first = SyntheticFacts.Batch(random, "mine", SyntheticFacts.SpringStart + 500, 40 * 96, 80);
        var second = SyntheticFacts.Batch(random, "mine", SyntheticFacts.SpringStart + 3000, 20 * 96, 40);

        await using var replaced = await StoreHarness.CreateAsync(StoreHarness.Zone("Asia/Kolkata"));
        await using var fresh = await StoreHarness.CreateAsync(StoreHarness.Zone("Asia/Kolkata"));
        await replaced.Store.ApplyAsync(Request(other, "other"));
        await replaced.Store.ApplyAsync(Request(first, "mine"));
        var result = await replaced.Store.ApplyAsync(Request(second, "mine", replace: true));
        await fresh.Store.ApplyAsync(Request(other, "other"));
        await fresh.Store.ApplyAsync(Request(second, "mine"));

        Assert.AreEqual(await fresh.DumpAsync("run", "session"), await replaced.DumpAsync("run", "session"));
        await replaced.VerifyRollupsAsync();
        Assert.IsNotEmpty(result.ChangedDays, "The days of the old facts and of the new ones changed.");
    }

    [TestMethod]
    public async Task AWriteThatFails_LeavesNoFactAndNoCursor()
    {
        await using var harness = await StoreHarness.CreateAsync();
        var random = new Random(3);
        var batch = SyntheticFacts.Batch(random, "s", SyntheticFacts.SpringStart, 10 * 96, 50);
        // A null text violates the schema after other rows of the transaction were written.
        batch.UsageFor(new UsageKey(new QuarterHour(SyntheticFacts.SpringStart + 5), "codex", null!, "", "", UsagePurpose.Turn)).Requests = 1;

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () => await harness.Store.ApplyAsync(Request(batch, "s")));

        Assert.AreEqual(0L, await harness.CountAsync("activity_q"));
        Assert.AreEqual(0L, await harness.CountAsync("activity_day"));
        Assert.AreEqual(0L, await harness.CountAsync("journal"));
        Assert.AreEqual(0L, await harness.CountAsync("session"));
    }

    [TestMethod]
    public async Task TheCursorIsSavedWithTheFacts_AndComesBackAfterARestart()
    {
        await using var harness = await StoreHarness.CreateAsync();
        var batch = SyntheticFacts.Batch(new Random(5), "s", SyntheticFacts.SpringStart, 96, 10);
        var state = new SessionFactsState { Model = "gpt-6.1-sol", Provider = "codex", OpenRuns = [new OpenRunState { RunId = "run-1", Start = DateTimeOffset.UnixEpoch, AccountedTo = DateTimeOffset.UnixEpoch }] };
        var cursor = new JournalCursor(1234, new Journal.JournalFingerprint(77, ulong.MaxValue - 5), state);
        await harness.Store.ApplyAsync(new ApplyRequest { SessionId = "s", Batch = batch, Cursor = cursor, FileLength = 2000, FileStampTicks = 99, FloorQuarter = 17 });

        await harness.ReopenAsync();
        var row = await harness.Store.GetJournalAsync("s");

        Assert.IsNotNull(row);
        Assert.AreEqual(1234, row.Offset);
        Assert.AreEqual(2000, row.FileLength);
        Assert.AreEqual(99, row.FileStampTicks);
        Assert.AreEqual(17, row.FloorQuarter);
        Assert.AreEqual(new Journal.JournalFingerprint(77, ulong.MaxValue - 5), row.FirstLine);
        var back = row.ToCursor();
        Assert.AreEqual("run-1", back.State.OpenRuns.Single().RunId);
        Assert.AreEqual("gpt-6.1-sol", back.State.Model);
    }

    [TestMethod]
    public async Task TheFloor_LeavesOutFactsBeforeIt()
    {
        await using var harness = await StoreHarness.CreateAsync();
        var batch = new FactBatch("s");
        var early = new QuarterHour(1000);
        var late = new QuarterHour(2000);
        batch.ActivityFor(new ActivityKey(early, "codex", "m", "")).ActiveMs = 5;
        batch.ActivityFor(new ActivityKey(late, "codex", "m", "")).ActiveMs = 7;

        await harness.Store.ApplyAsync(Request(batch, "s", floor: 1500));

        Assert.AreEqual(1L, await harness.CountAsync("activity_q"));
        Assert.AreEqual(7L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(active_ms) FROM {harness.Store.Prefix}activity_year")));
    }

    [TestMethod]
    public async Task ADeletedSession_KeepsItsFacts_UntilTheyAreForgotten()
    {
        await using var harness = await StoreHarness.CreateAsync(StoreHarness.Zone("Europe/Paris"));
        var random = new Random(13);
        await harness.Store.ApplyAsync(Request(SyntheticFacts.Batch(random, "gone", SyntheticFacts.SpringStart, 30 * 96, 50), "gone"));
        await harness.Store.ApplyAsync(Request(SyntheticFacts.Batch(random, "kept", SyntheticFacts.SpringStart, 30 * 96, 50), "kept"));
        var before = await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(active_ms) FROM {harness.Store.Prefix}activity_year"));

        await harness.Store.MarkDeletedAsync(["gone"]);

        Assert.AreEqual(before, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(active_ms) FROM {harness.Store.Prefix}activity_year")));
        Assert.AreEqual(1L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT deleted FROM {harness.Store.Prefix}session WHERE session_id = 'gone'")));

        var (sessions, days) = await harness.Store.ForgetDeletedAsync();

        Assert.AreEqual(1, sessions);
        Assert.IsNotEmpty(days);
        Assert.AreEqual(0L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT COUNT(*) FROM {harness.Store.Prefix}activity_q WHERE session_id = 'gone'")));
        Assert.AreEqual(0L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT COUNT(*) FROM {harness.Store.Prefix}session WHERE session_id = 'gone'")));
        await harness.VerifyRollupsAsync();
    }

    [TestMethod]
    public async Task ChangingTheTimeZone_AddsTheRollupsUpAgainFromTheFacts()
    {
        await using var harness = await StoreHarness.CreateAsync(StoreHarness.Zone("America/New_York"));
        var random = new Random(21);
        for (var session = 0; session < 4; session++)
        {
            await harness.Store.ApplyAsync(Request(SyntheticFacts.Batch(random, "s" + session, SyntheticFacts.SpringStart, 200 * 96, 100), "s" + session));
        }

        await harness.VerifyRollupsAsync();
        await harness.ReopenAsync(StoreHarness.Zone("Pacific/Chatham"));

        Assert.AreEqual("Pacific/Chatham", await harness.Store.GetMetaAsync(StatisticsStore.RollupTimeZoneKey));
        await harness.VerifyRollupsAsync();
    }

    [TestMethod]
    public async Task ADayThatWasWrittenInAnotherZone_DoesNotLingerAfterTheRebuild()
    {
        await using var harness = await StoreHarness.CreateAsync(StoreHarness.Zone("Pacific/Kiritimati"));
        var batch = new FactBatch("s");
        // 23:00 UTC is already the next day at +14: the day of the roll-up depends on the zone.
        batch.ActivityFor(new ActivityKey(QuarterHour.Of(new DateTimeOffset(2026, 6, 10, 23, 0, 0, TimeSpan.Zero)), "codex", "m", "")).ActiveMs = 1000;
        await harness.Store.ApplyAsync(Request(batch, "s"));
        Assert.AreEqual(20260611L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT p FROM {harness.Store.Prefix}activity_day")));

        await harness.ReopenAsync(TimeZoneInfo.Utc);

        Assert.AreEqual(20260610L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT p FROM {harness.Store.Prefix}activity_day")));
        Assert.AreEqual(1L, await harness.CountAsync("activity_day"));
        Assert.AreEqual(1L, await harness.CountAsync("activity_month"));
        Assert.AreEqual(1L, await harness.CountAsync("activity_year"));
    }

    [TestMethod]
    public async Task TheLargestValues_MergeWithTheLarger_AndCarryTheirSession()
    {
        await using var harness = await StoreHarness.CreateAsync();
        var quarter = new QuarterHour(SyntheticFacts.SpringStart);
        var a = new FactBatch("a");
        a.Offer(quarter, ExtremeMeasure.LongestRunMs, "", 500, "run-a", quarter.Start);
        var b = new FactBatch("b");
        b.Offer(new QuarterHour(quarter.Index + 1), ExtremeMeasure.LongestRunMs, "", 900, "run-b", quarter.Start.AddMinutes(20));
        var c = new FactBatch("c");
        c.Offer(new QuarterHour(quarter.Index + 2), ExtremeMeasure.LongestRunMs, "", 100, "run-c", quarter.Start.AddMinutes(40));

        await harness.Store.ApplyAsync(Request(a, "a"));
        await harness.Store.ApplyAsync(Request(b, "b"));
        await harness.Store.ApplyAsync(Request(c, "c"));

        var year = await harness.Store.ReadAsync(sql => sql.Query($"SELECT value, session_id, run_id FROM {harness.Store.Prefix}extreme_year", static reader => (reader.GetInt64(0), reader.GetString(1), reader.GetString(2))));
        CollectionAssert.AreEqual(new[] { (900L, "b", "run-b") }, year);
        await harness.VerifyRollupsAsync();

        // Replacing the session that holds the record gives the record back to the next one.
        await harness.Store.ApplyAsync(Request(new FactBatch("b"), "b", replace: true));
        year = await harness.Store.ReadAsync(sql => sql.Query($"SELECT value, session_id, run_id FROM {harness.Store.Prefix}extreme_year", static reader => (reader.GetInt64(0), reader.GetString(1), reader.GetString(2))));
        CollectionAssert.AreEqual(new[] { (500L, "a", "run-a") }, year);
    }

    [TestMethod]
    public async Task ARunIsReplacedEachTimeItChanges()
    {
        await using var harness = await StoreHarness.CreateAsync();
        var first = new FactBatch("s");
        first.Runs["r1"] = new RunRow { SessionId = "s", RunId = "r1", Start = DateTimeOffset.UnixEpoch.AddHours(1), End = DateTimeOffset.UnixEpoch.AddHours(1), Outcome = RunOutcome.Running, Requests = 1 };
        var second = new FactBatch("s");
        second.Runs["r1"] = new RunRow { SessionId = "s", RunId = "r1", Start = DateTimeOffset.UnixEpoch.AddHours(1), End = DateTimeOffset.UnixEpoch.AddHours(2), Outcome = RunOutcome.Completed, Requests = 5 };

        await harness.Store.ApplyAsync(Request(first, "s"));
        await harness.Store.ApplyAsync(Request(second, "s"));

        Assert.AreEqual(1L, await harness.CountAsync("run"));
        Assert.AreEqual(5L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT requests FROM {harness.Store.Prefix}run")));
        Assert.AreEqual((long)RunOutcome.Completed, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT outcome FROM {harness.Store.Prefix}run")));
    }

    [TestMethod]
    public async Task NoRowHoldsAText_OfASession_ButItsTitle()
    {
        await using var harness = await StoreHarness.CreateAsync();
        var batch = SyntheticFacts.Batch(new Random(1), "s", SyntheticFacts.SpringStart, 96, 20);
        batch.Session!.WorkingDirectory = @"C:\secret\folder";
        await harness.Store.ApplyAsync(Request(batch, "s"));

        var dump = await harness.DumpAsync();

        Assert.DoesNotContain("secret", dump);
        Assert.Contains("A title", dump);
    }

    [TestMethod]
    public async Task RebuildingTheRollups_OfAStoreOfAFewHundredThousandRows_IsFast()
    {
        await using var harness = await StoreHarness.CreateAsync(StoreHarness.Zone("Europe/Paris"));
        var random = new Random(99);
        var applied = Stopwatch.StartNew();
        var total = 0;
        for (var session = 0; session < 60; session++)
        {
            var batch = SyntheticFacts.Batch(random, "s" + session, SyntheticFacts.SpringStart, 220 * 96, 500);
            total += (await harness.Store.ApplyAsync(Request(batch, "s" + session))).Rows;
        }

        applied.Stop();
        var rows = await harness.Store.ReadAsync(sql => FactTables.All.Sum(table => sql.ScalarLong($"SELECT COUNT(*) FROM {table.TableName(harness.Store.Prefix, RollupLevel.Quarter)}")));

        var rebuild = Stopwatch.StartNew();
        await harness.Store.RebuildRollupsAsync();
        rebuild.Stop();

        Console.WriteLine($"applied {total} rows ({rows} fact rows in the quarter tables) in {applied.Elapsed.TotalSeconds:0.00} s; rebuilt the roll-ups of {rows} rows in {rebuild.Elapsed.TotalSeconds:0.00} s");
        await harness.VerifyRollupsAsync();
        Assert.IsLessThan(30, rebuild.Elapsed.TotalSeconds);
    }
}
