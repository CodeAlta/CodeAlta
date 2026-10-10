using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Tests;

[TestClass]
public sealed class StatisticsEngineTests
{
    private static readonly DateTimeOffset Today9 = EngineHarness.Now.AddHours(-3);

    private static void AddSession(EngineHarness harness, string id, DateTimeOffset start, int runs = 2, bool endLastRun = true, long input = 1000)
    {
        var builder = EngineHarness.Session(id, start, runs, endLastRun: endLastRun, input: input);
        harness.Journals.Set(builder, EngineHarness.EndOf(builder, start, runs));
    }

    /// <summary>What the numbers must be: the sessions read in one go, without the engine, into a store of their own.</summary>
    private static async Task<string> ReferenceDumpAsync(FakeJournalCatalog journals, params string[] sessions)
    {
        await using var reference = await StoreHarness.CreateAsync();
        using var catchUp = new SessionCatchUp();
        foreach (var id in sessions)
        {
            await using var stream = (await journals.OpenAsync(id))!;
            var result = catchUp.CatchUp(id, stream, null, long.MaxValue, CancellationToken.None);
            var file = (await journals.GetAsync(id))!;
            var batch = result.Batch;
            var cursor = result.Cursor;
            if (cursor.State.OpenRuns.Count > 0 && EngineHarness.Now - file.LastWriteUtc > TimeSpan.FromHours(1))
            {
                var reducer = new SessionFactsReducer(id, cursor.State.Clone());
                reducer.InterruptOpenRuns();
                batch.Merge(reducer.TakeBatch());
                cursor = cursor with { State = reducer.State };
            }

            await reference.Store.ApplyAsync(new ApplyRequest { SessionId = id, Batch = batch, Cursor = cursor, FileLength = file.Length, FileStampTicks = file.LastWriteUtc.UtcTicks });
        }

        return await reference.DumpAsync();
    }

    [TestMethod]
    public async Task NothingIsRead_UntilTheUserChooses()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9);

        await harness.Engine.DrainAsync();
        harness.Engine.Signal("a");
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.NeedsChoice, harness.Engine.Status.State);
        Assert.AreEqual(0, harness.Journals.Opens);
        Assert.AreEqual(0L, await harness.Store.CountAsync("journal"));
    }

    [TestMethod]
    public async Task TheFirstTimeStatus_CountsTheSessionsTheirBytesAndTheEarliestDay_WithoutReadingAnyJournal()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "old", Today9.AddDays(-30), runs: 3);
        AddSession(harness, "recent", Today9.AddDays(-2), runs: 2);
        AddSession(harness, "today", Today9, runs: 1);

        await harness.Engine.DrainAsync();

        var status = harness.Engine.Status;
        Assert.AreEqual(HistoryState.NeedsChoice, status.State);
        Assert.AreEqual(3, status.SessionsTotal);
        Assert.AreEqual(0, status.SessionsDone);
        Assert.AreEqual(harness.Journals.TotalBytes, status.BytesTotal);
        Assert.IsTrue(status.BytesTotal > 0);
        // The earliest day is the one of the oldest journal: its last write, the end of its three runs.
        Assert.AreEqual(20260909, status.OldestDateReached);
        Assert.AreEqual(0, harness.Journals.Opens, "No journal is opened to count.");
    }

    [TestMethod]
    public async Task TheFirstTimeNumbers_AreCached_AndReadAgainWhenTheyAreOldOrAskedFor()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9.AddDays(-5));
        await harness.Engine.DrainAsync();
        var lists = harness.Journals.Lists;
        Assert.AreEqual(1, harness.Engine.Status.SessionsTotal);

        // A session that appears is not seen while the numbers are fresh: the listing is not repeated.
        AddSession(harness, "b", Today9.AddDays(-9));
        var cached = await harness.Engine.RefreshOverviewAsync(TimeSpan.FromMinutes(1));
        Assert.AreEqual(lists, harness.Journals.Lists);
        Assert.AreEqual(1, cached.SessionsTotal);

        // Past the age that is accepted, or asked for with no age, it is listed again and the status follows.
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        var changes = new List<StatisticsStatus>();
        harness.Engine.StatusChanged += changes.Add;
        var fresh = await harness.Engine.RefreshOverviewAsync(TimeSpan.FromMinutes(1));
        Assert.AreEqual(lists + 1, harness.Journals.Lists);
        Assert.AreEqual(2, fresh.SessionsTotal);
        Assert.AreEqual(20260930, fresh.OldestDateReached);
        Assert.AreEqual(1, changes.Count(change => change.SessionsTotal == 2), "The new numbers are published.");
        AddSession(harness, "c", Today9.AddDays(-1));
        Assert.AreEqual(3, (await harness.Engine.RefreshOverviewAsync(TimeSpan.Zero)).SessionsTotal);
    }

    [TestMethod]
    public async Task TheFirstTimeNumbers_AreLeftOnceTheUserChooses_AndCostNothingAfterwards()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9.AddDays(-5));
        AddSession(harness, "b", Today9.AddDays(-400));
        await harness.Engine.DrainAsync();
        Assert.AreEqual(2, harness.Engine.Status.SessionsTotal);

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.LastDays(30));
        await harness.Engine.DrainAsync();
        var done = harness.Engine.Status;
        Assert.AreEqual(HistoryState.Done, done.State);
        Assert.AreEqual(1, done.SessionsTotal, "Only the session inside the choice is counted now.");
        var lists = harness.Journals.Lists;
        var again = await harness.Engine.RefreshOverviewAsync(TimeSpan.Zero);
        Assert.AreEqual(lists, harness.Journals.Lists, "Once chosen, nothing is listed for the first-time card.");
        Assert.AreEqual(HistoryState.Done, again.State);
    }

    [TestMethod]
    public async Task TheFirstTimeNumbers_OfAThousandsOfFiles_AreOneListing()
    {
        await using var harness = await EngineHarness.CreateAsync();
        for (var index = 0; index < 3000; index++)
        {
            harness.Journals.Set($"s{index:D4}", [1, 2, 3, 4], Today9.AddMinutes(-index));
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await harness.Engine.DrainAsync();

        Assert.AreEqual(3000, harness.Engine.Status.SessionsTotal);
        Assert.AreEqual(12000L, harness.Engine.Status.BytesTotal);
        Assert.AreEqual(1, harness.Journals.Lists);
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
    }

    [TestMethod]
    public async Task Reset_EmptiesTheNumbers_AndComesBackToTheChoice()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9.AddDays(-3), runs: 3);
        AddSession(harness, "b", Today9, runs: 1);
        await harness.Engine.DrainAsync();
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.IsTrue(await harness.Store.CountAsync("journal") > 0);
        var changes = new List<StatisticsDataChange>();
        harness.Engine.DataChanged += changes.Add;

        var status = await harness.Engine.ResetAsync();

        Assert.AreEqual(HistoryState.NeedsChoice, status.State);
        Assert.IsNull(status.Choice);
        Assert.AreEqual(2, status.SessionsTotal, "The card has its numbers again.");
        Assert.AreEqual(0L, await harness.Store.CountAsync("journal"));
        Assert.AreEqual(0L, await harness.Store.CountAsync("session"));
        Assert.AreEqual(1, changes.Count, "The pages are told that every day changed.");
        Assert.IsTrue(changes[0].Revision > 0);

        // The choice can be made again, and the numbers come back as they were.
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.AreEqual(2L, await harness.Store.CountAsync("session"));

        // And it survives a restart as a first start: nothing chosen.
        await harness.Engine.ResetAsync();
        await harness.RestartAsync();
        await harness.Engine.DrainAsync();
        Assert.AreEqual(HistoryState.NeedsChoice, harness.Engine.Status.State);
    }

    [TestMethod]
    public async Task ReadingAllTheHistory_GivesTheNumbersOfReadingEachSessionAtOnce()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9.AddDays(-30), runs: 3);
        AddSession(harness, "b", Today9.AddDays(-2), runs: 2, endLastRun: false);
        AddSession(harness, "c", Today9, runs: 1);

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var status = harness.Engine.Status;
        Assert.AreEqual(HistoryState.Done, status.State);
        Assert.AreEqual(3, status.SessionsTotal);
        Assert.AreEqual(3, status.SessionsDone);
        Assert.IsNull(status.CompleteFromDay, "Everything is read.");
        Assert.IsTrue(status.IsComplete);
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "a", "b", "c"), await harness.Store.DumpAsync());
        await harness.Store.VerifyRollupsAsync();
    }

    [TestMethod]
    public async Task TheMostRecentSessionsAreReadFirst()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "old", Today9.AddDays(-50));
        AddSession(harness, "recent", Today9);
        AddSession(harness, "middle", Today9.AddDays(-9));
        var order = new List<string>();
        harness.Engine.DataChanged += change => order.AddRange(change.SessionIds);

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        CollectionAssert.AreEqual(new[] { "recent", "middle", "old" }, order);
    }

    [TestMethod]
    public async Task TheStatusSaysHowFarTheReadingIs()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "old", Today9.AddDays(-50));
        AddSession(harness, "recent", Today9);
        var seen = new List<StatisticsStatus>();
        harness.Engine.StatusChanged += seen.Add;

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var reading = seen.Where(static status => status.State == HistoryState.Reading).ToList();
        Assert.IsNotEmpty(reading);
        Assert.IsTrue(reading.Any(static status => status.CurrentSessionId == "recent"));
        Assert.IsTrue(reading.Any(static status => status.SessionsDone == 1 && status.OldestDateReached == 20261009 && status.CompleteFromDay is not null));
        Assert.IsTrue(reading.Where(static status => status.SessionsTotal > 0).All(static status => status.SessionsTotal == 2 && status.Reason == "first-read"));
        var done = harness.Engine.Status;
        Assert.AreEqual(done.BytesTotal, done.BytesDone);
        Assert.AreEqual(20260820, done.OldestDateReached);
    }

    [TestMethod]
    public async Task LastDays_ReadsTheSessionsWithActivityThere_AndStartsTheStatisticsAtThatDay()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "old", Today9.AddDays(-40));
        AddSession(harness, "recent", Today9.AddDays(-3));

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.LastDays(30));
        await harness.Engine.DrainAsync();

        Assert.AreEqual(1, harness.Engine.Status.SessionsTotal);
        Assert.AreEqual(1L, await harness.Store.CountAsync("journal"));
        Assert.AreEqual(20260910, harness.Engine.Status.FloorDay, "Today and the 29 days before it.");
        Assert.AreEqual(20260910, harness.Engine.Status.CompleteFromDay);
        Assert.IsFalse(harness.Engine.Status.IsComplete);
    }

    [TestMethod]
    public async Task FromToday_ReadsNothingOfThePast_AndKeepsTheFlow()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "yesterday", Today9.AddDays(-1));
        AddSession(harness, "today", Today9);

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.FromToday);
        await harness.Engine.DrainAsync();

        Assert.AreEqual(1L, await harness.Store.CountAsync("journal"), "Only the session of today is read.");
        var before = await harness.SumAsync("usage_year", "requests");

        harness.Journals.Append("today", EngineHarness.More("today", Today9.AddHours(1)), Today9.AddHours(1).AddSeconds(20));
        harness.Engine.Signal("today");
        await harness.Engine.DrainAsync();

        Assert.IsGreaterThan(before, await harness.SumAsync("usage_year", "requests"));
    }

    [TestMethod]
    public async Task TheFloor_LeavesOutWhatASessionOfTodayDidBeforeIt()
    {
        await using var harness = await EngineHarness.CreateAsync();
        // A session that began last week and was used today: only today counts when the statistics start from today.
        var builder = EngineHarness.Session("long", Today9.AddDays(-7), 1);
        EngineHarness.AddRuns(builder, Today9, 1, tag: "today");
        harness.Journals.Set(builder, Today9.AddSeconds(30));

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.FromToday);
        await harness.Engine.DrainAsync();

        var requests = await harness.SumAsync("usage_q", "requests");
        Assert.AreEqual(1L, requests, "The request of last week is left out.");
        Assert.AreEqual(1L, await harness.Store.CountAsync("run"), "So is its run.");
    }

    [TestMethod]
    public async Task APausedReading_GoesOnFromItsCursor_AlsoAfterARestart()
    {
        await using var harness = await EngineHarness.CreateAsync(configure: static options => options.ChunkBytes = 1500);
        AddSession(harness, "big", Today9, runs: 12);
        AddSession(harness, "small", Today9.AddDays(-3));
        var paused = false;
        harness.Engine.DataChanged += _ =>
        {
            if (!paused)
            {
                paused = true;
                harness.Engine.PauseAsync().AsTask().GetAwaiter().GetResult();
            }
        };

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.Paused, harness.Engine.Status.State);
        var row = await harness.Store.Store.GetJournalAsync("big");
        Assert.IsNotNull(row);
        var length = (await harness.Journals.GetAsync("big"))!.Length;
        Assert.IsTrue(row.Offset > 0 && row.Offset < length, "The cursor of the part that was read is saved.");
        Assert.AreEqual(0L, row.FileStampTicks, "A reading that did not reach the end is not taken for a complete one.");
        Assert.IsNull(await harness.Store.Store.GetJournalAsync("small"));

        // The pause is kept: another start of the application does not read.
        await harness.RestartAsync();
        await harness.Engine.DrainAsync();
        Assert.AreEqual(HistoryState.Paused, harness.Engine.Status.State);
        Assert.IsNull(await harness.Store.Store.GetJournalAsync("small"));

        await harness.Engine.ResumeAsync();
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "big", "small"), await harness.Store.DumpAsync());
    }

    [TestMethod]
    public async Task StoppingHere_StartsTheStatisticsAtTheDateReached()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "recent", Today9);
        AddSession(harness, "middle", Today9.AddDays(-10));
        AddSession(harness, "old", Today9.AddDays(-40));
        var changes = 0;
        harness.Engine.DataChanged += _ =>
        {
            if (++changes == 2)
            {
                harness.Engine.StopHereAsync().AsTask().GetAwaiter().GetResult();
            }
        };

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var status = harness.Engine.Status;
        Assert.AreEqual(HistoryState.StoppedHere, status.State);
        Assert.AreEqual(20260930, status.FloorDay, "The day after the one of the session that was being read.");
        Assert.IsNull(await harness.Store.Store.GetJournalAsync("old"));

        // Reading more goes back again, beyond the point that was reached.
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.IsNotNull(await harness.Store.Store.GetJournalAsync("old"));
    }

    [TestMethod]
    public async Task ReadingMoreHistory_ReadsAgainWhatWasLeftOutOfTheSessionsAlreadyRead()
    {
        await using var harness = await EngineHarness.CreateAsync();
        // One session that began 40 days ago and was used 3 days ago.
        var early = EngineHarness.Session("long", Today9.AddDays(-40), 1);
        EngineHarness.AddRuns(early, Today9.AddDays(-3), 1, tag: "late");
        harness.Journals.Set(early, Today9.AddDays(-3).AddSeconds(30));

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.LastDays(30));
        await harness.Engine.DrainAsync();
        Assert.AreEqual(1L, await harness.SumAsync("usage_q", "requests"));

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        Assert.AreEqual(2L, await harness.SumAsync("usage_q", "requests"));
        await harness.Store.VerifyRollupsAsync();
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "long"), await harness.Store.DumpAsync());
    }

    [TestMethod]
    public async Task AskingForLessThanWasChosen_DoesNothing()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9.AddDays(-50));
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var status = await harness.Engine.ChooseHistoryAsync(HistoryChoice.LastDays(5));

        Assert.AreEqual("all", status.Choice);
        Assert.AreEqual(HistoryState.Done, status.State);
    }

    [TestMethod]
    public async Task TheFlow_CatchesUpASessionThatWrote_AndANewOne()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9, runs: 1);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        var revision = harness.Engine.Status.Revision;
        var changes = new List<StatisticsDataChange>();
        harness.Engine.DataChanged += changes.Add;

        harness.Journals.Append("a", EngineHarness.More("a", Today9.AddMinutes(30)), Today9.AddMinutes(31));
        AddSession(harness, "new", Today9.AddMinutes(40), runs: 1);
        harness.Engine.Signal("a");
        harness.Engine.Signal("new");
        harness.Engine.Signal("a");
        await harness.Engine.DrainAsync();

        Assert.AreEqual(2, changes.Count, "One catch-up for each session, the second signal of 'a' is coalesced.");
        Assert.IsTrue(harness.Engine.Status.Revision > revision);
        Assert.IsTrue(changes.All(static change => change.FromDay == 20261009 && change.ToDay == 20261009));
        Assert.AreEqual(3L, await harness.SumAsync("usage_q", "requests"));
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "a", "new"), await harness.Store.DumpAsync());
    }

    [TestMethod]
    public async Task TheFlow_WaitsForTheDebounce_AndCatchesUpOnceForManySignals()
    {
        await using var harness = await EngineHarness.CreateAsync(configure: static options => options.FlowDebounce = TimeSpan.FromSeconds(1));
        AddSession(harness, "a", Today9, runs: 1);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.FromToday);
        await harness.Engine.DrainAsync();
        var changes = 0;
        harness.Engine.DataChanged += _ => changes++;

        harness.Journals.Append("a", EngineHarness.More("a", Today9.AddMinutes(30)), Today9.AddMinutes(31));
        for (var index = 0; index < 20; index++)
        {
            harness.Engine.Signal("a");
        }

        await harness.Engine.DrainAsync();
        Assert.AreEqual(0, changes, "Not due yet.");

        harness.Time.Advance(TimeSpan.FromSeconds(1.5));
        await harness.Engine.DrainAsync();

        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public async Task TheSameRangeIsNeverCountedTwice()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        var dump = await harness.Store.DumpAsync();

        for (var index = 0; index < 3; index++)
        {
            harness.Engine.Signal("a");
            await harness.Engine.DrainAsync();
        }

        await harness.RestartAsync();
        await harness.Engine.DrainAsync();

        Assert.AreEqual(dump, await harness.Store.DumpAsync());
    }

    [TestMethod]
    public async Task ACrashBetweenTwoCommits_ResumesFromTheLastOneAndGivesTheSameTotals()
    {
        await using var harness = await EngineHarness.CreateAsync(configure: static options => options.ChunkBytes = 1200);
        AddSession(harness, "big", Today9, runs: 10);
        var length = (await harness.Journals.GetAsync("big"))!.Length;
        // The disk fails once part of the file was committed.
        harness.Journals.FailAfter("big", length / 2);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var row = await harness.Store.Store.GetJournalAsync("big");
        Assert.IsNotNull(row);
        Assert.IsTrue(row.Offset > 0 && row.Offset < length);
        Assert.AreEqual(1, harness.Engine.Status.SkippedCount);
        Assert.AreEqual("big", harness.Engine.Status.Skipped.Single().SessionId);

        harness.Journals.StopFailing();
        await harness.RestartAsync(static options => options.ChunkBytes = 1200);
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.AreEqual(0, harness.Engine.Status.SkippedCount);
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "big"), await harness.Store.DumpAsync());
    }

    [TestMethod]
    public async Task ASessionThatCannotBeRead_IsSkippedAndCounted_AndTheOthersGoOn()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "broken", Today9);
        AddSession(harness, "fine", Today9.AddHours(-1));
        harness.Journals.FailAfter("broken", 10);

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var status = harness.Engine.Status;
        Assert.AreEqual(HistoryState.Done, status.State);
        Assert.AreEqual(2, status.SessionsDone);
        Assert.AreEqual(1, status.SkippedCount);
        Assert.IsNotNull(await harness.Store.Store.GetJournalAsync("fine"));
    }

    [TestMethod]
    public async Task TheNamesOfTheProjects_AreKept_AndNamedInTheResults()
    {
        var names = new Dictionary<string, string> { ["22222222-2222-2222-2222-222222222222"] = "Alpha" };
        await using var harness = await EngineHarness.CreateAsync(configure: options => options.ResolveProjectNames = _ => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(names));
        AddSession(harness, "a", Today9);

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        Assert.AreEqual("Alpha", (await harness.Store.Store.GetProjectNamesAsync()).Single().Value);
        var series = await harness.Engine.Queries.SeriesAsync(new Query.StatisticsRequest { Period = "2026-10-01..2026-10-31" }, "runs", "project");
        Assert.AreEqual("Alpha", series.Series.Single().Label);
        // A restart finds the names in the store, without asking the host again.
        await harness.RestartAsync();
        await harness.Engine.DrainAsync();
        var again = await harness.Engine.Queries.SeriesAsync(new Query.StatisticsRequest { Period = "2026-10-01..2026-10-31" }, "runs", "project");
        Assert.AreEqual("Alpha", again.Series.Single().Label);
    }

    [TestMethod]
    public async Task AJournalThatFailed_IsTriedAgainWhenItChanges_NotAtEveryLook()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "broken", Today9);
        harness.Journals.FailAfter("broken", 10);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        Assert.AreEqual(1, harness.Engine.Status.SkippedCount);
        var opens = harness.Journals.Opens;

        // The disk is back, but the journal did not change: the next looks leave it alone.
        harness.Journals.StopFailing();
        harness.Time.Advance(TimeSpan.FromMinutes(6));
        await harness.Engine.DrainAsync();
        Assert.AreEqual(opens, harness.Journals.Opens);
        Assert.IsNull(await harness.Store.Store.GetJournalAsync("broken"));

        // It changes: it is read.
        harness.Journals.Append("broken", EngineHarness.More("broken", Today9.AddMinutes(30)), Today9.AddMinutes(31));
        harness.Time.Advance(TimeSpan.FromMinutes(6));
        await harness.Engine.DrainAsync();
        Assert.IsNotNull(await harness.Store.Store.GetJournalAsync("broken"));
        Assert.AreEqual(3L, await harness.SumAsync("usage_q", "requests"));
    }

    [TestMethod]
    public async Task TheNumbersAreCompleteFromTheOldFloor_WhileMoreHistoryIsRead()
    {
        await using var harness = await EngineHarness.CreateAsync(configure: static options => options.ChunkBytes = 1500);
        AddSession(harness, "recent", Today9.AddDays(-2), runs: 8);
        AddSession(harness, "old", Today9.AddDays(-60), runs: 2);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.LastDays(30));
        await harness.Engine.DrainAsync();
        var floor = harness.Engine.Status.FloorDay;
        var seen = new List<int?>();
        harness.Engine.StatusChanged += status => seen.Add(status.CompleteFromDay);

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        Assert.IsNotNull(floor);
        Assert.IsTrue(seen.Where(static day => day is not null).All(day => day == floor), "The hatch stays where the old floor was until the older sessions are read.");
        Assert.IsNull(harness.Engine.Status.CompleteFromDay);
        Assert.IsTrue(harness.Engine.Status.IsComplete);
    }

    [TestMethod]
    public async Task ARewrittenJournal_ReplacesTheFactsOfItsSession()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9, runs: 2);
        AddSession(harness, "b", Today9.AddMinutes(-30), runs: 1);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        // The file is replaced by another one: a header added to the front, other runs.
        var rewritten = EngineHarness.Session("a", Today9.AddHours(-2), runs: 3, input: 5000);
        harness.Journals.Set(rewritten, Today9.AddHours(-1));
        await harness.RestartAsync();
        await harness.Engine.DrainAsync();

        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "a", "b"), await harness.Store.DumpAsync());
        await harness.Store.VerifyRollupsAsync();
    }

    [TestMethod]
    public async Task TheFlowGoesBeforeTheNextOldSession_WhileTheHistoryIsRead()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "recent", Today9, runs: 1);
        AddSession(harness, "old", Today9.AddDays(-20), runs: 1);
        AddSession(harness, "older", Today9.AddDays(-40), runs: 1);
        var order = new List<string>();
        var signaled = false;
        harness.Engine.DataChanged += change =>
        {
            order.AddRange(change.SessionIds);
            if (!signaled)
            {
                // The session that was just read writes again, while the history is still going back.
                signaled = true;
                harness.Journals.Append("recent", EngineHarness.More("recent", Today9.AddHours(1)), Today9.AddHours(1).AddSeconds(30));
                harness.Engine.Signal("recent");
            }
        };

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        CollectionAssert.AreEqual(new[] { "recent", "recent", "old", "older" }, order);
        Assert.AreEqual(4L, await harness.SumAsync("activity_year", "runs_started"), "Both runs of 'recent', and one run of each old session, are counted.");
    }

    [TestMethod]
    public async Task ADeletedJournal_KeepsItsFacts_AndTheyCanBeForgotten()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "gone", Today9);
        AddSession(harness, "kept", Today9.AddHours(-1));
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        var requests = await harness.SumAsync("usage_year", "requests");

        harness.Journals.Remove("gone");
        await harness.RestartAsync();
        await harness.Engine.DrainAsync();

        Assert.AreEqual(requests, await harness.SumAsync("usage_year", "requests"));
        Assert.AreEqual(1L, await harness.Store.Store.ReadAsync(sql => sql.ScalarLong($"SELECT deleted FROM {harness.Store.Store.Prefix}session WHERE session_id = 'gone'")));

        var forgotten = await harness.Engine.ForgetDeletedAsync();

        Assert.AreEqual(1, forgotten);
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "kept"), await harness.Store.DumpAsync());
    }

    [TestMethod]
    public async Task ANewVersionOfTheFacts_ReadsTheSessionsAgain_AndTheOldNumbersStayUntilThen()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9);
        AddSession(harness, "b", Today9.AddHours(-1));
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        var dump = await harness.Store.DumpAsync();

        // The rows were computed by an earlier version of the facts.
        await harness.Store.Store.WriteAsync(sql =>
            sql.Execute($"UPDATE {harness.Store.Store.Prefix}journal SET facts_version = 0, state = @p0", System.Text.Encoding.UTF8.GetBytes("{\"Version\":0}")));
        await harness.RestartAsync();
        var states = new List<StatisticsStatus>();
        harness.Engine.StatusChanged += states.Add;
        await harness.Engine.DrainAsync();

        Assert.IsTrue(states.Any(static status => status.State == HistoryState.Reading && status.Reason == "facts-improved"));
        Assert.AreEqual(dump, await harness.Store.DumpAsync(), "A session read again gives its numbers again, none twice.");
        Assert.AreEqual(2L, await harness.Store.Store.ReadAsync(sql => sql.ScalarLong($"SELECT COUNT(*) FROM {harness.Store.Store.Prefix}journal WHERE facts_version = {SessionFactsState.CurrentVersion}")));
    }

    [TestMethod]
    public async Task ARunThatNeverEnded_IsInterruptedOnlyWhenTheSessionHasBeenQuietForLong()
    {
        await using var harness = await EngineHarness.CreateAsync();
        // Two sessions with a run that has no end: one wrote a minute ago, the other three hours ago.
        AddSession(harness, "live", EngineHarness.Now.AddMinutes(-1), runs: 1, endLastRun: false);
        AddSession(harness, "dead", EngineHarness.Now.AddHours(-3), runs: 1, endLastRun: false);

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var interrupted = await harness.Store.Store.ReadAsync(sql => sql.Query(
            $"SELECT session_id, outcome FROM {harness.Store.Store.Prefix}run ORDER BY session_id",
            static reader => (reader.GetString(0), reader.GetInt64(1))));
        CollectionAssert.AreEqual(new[] { ("dead", (long)RunOutcome.Interrupted), ("live", (long)RunOutcome.Running) }, interrupted);
        Assert.AreEqual(1L, await harness.SumAsync("activity_year", "runs_interrupted"));

        // The live one stops writing: once it has been quiet long enough, the next look at the journals settles it.
        harness.Time.Advance(TimeSpan.FromHours(2));
        await harness.RestartAsync();
        await harness.Engine.DrainAsync();

        Assert.AreEqual(2L, await harness.SumAsync("activity_year", "runs_interrupted"));
    }

    [TestMethod]
    public async Task TheJournalsAreCheckedAtEachStart_ForWhatChangedWhileTheApplicationWasClosed()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9, runs: 1);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        // Another application wrote to a, and made a session of its own.
        harness.Journals.Append("a", EngineHarness.More("a", Today9.AddMinutes(30)), Today9.AddMinutes(31));
        AddSession(harness, "tui", Today9.AddMinutes(45), runs: 1);
        await harness.RestartAsync();
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "a", "tui"), await harness.Store.DumpAsync());
    }
}
