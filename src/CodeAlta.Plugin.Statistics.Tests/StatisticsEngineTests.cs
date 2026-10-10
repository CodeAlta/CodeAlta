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
    public async Task ASignalOfTheSessionTheHistoryReads_DoesNotCountItsBytesTwice()
    {
        await using var harness = await EngineHarness.CreateAsync(configure: static options => options.ChunkBytes = 1500);
        AddSession(harness, "big", Today9, runs: 12);
        AddSession(harness, "small", Today9.AddDays(-3));
        var signaled = false;
        harness.Engine.DataChanged += _ =>
        {
            if (!signaled)
            {
                // The session writes while the history is in the middle of its journal: the first chunk is saved, the others are not.
                signaled = true;
                harness.Engine.Signal("big");
            }
        };

        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.AreEqual(14L, await harness.SumAsync("usage_q", "requests"), "The twelve requests of 'big' and the two of 'small', each once.");
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "big", "small"), await harness.Store.DumpAsync());
        await harness.Store.VerifyRollupsAsync();
    }

    [TestMethod]
    public async Task ASessionThatWritesBeforeMoreHistoryReachesIt_IsStillReadAgainFromItsStart()
    {
        await using var harness = await EngineHarness.CreateAsync();
        // One session that began 40 days ago and was used 3 days ago, read with a floor of 30 days.
        var journal = EngineHarness.Session("long", Today9.AddDays(-40), 1);
        EngineHarness.AddRuns(journal, Today9.AddDays(-3), 1, tag: "late");
        harness.Journals.Set(journal, Today9.AddDays(-3).AddSeconds(30));
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.LastDays(30));
        await harness.Engine.DrainAsync();
        Assert.AreEqual(1L, await harness.SumAsync("usage_q", "requests"));

        // The user asks for everything, and the session writes before the history looked at it: the flow goes first.
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        harness.Journals.Append("long", EngineHarness.More("long", Today9), Today9.AddSeconds(30));
        harness.Engine.Signal("long");
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.AreEqual(3L, await harness.SumAsync("usage_q", "requests"), "The request of 40 days ago is read too.");
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "long"), await harness.Store.DumpAsync());
        await harness.Store.VerifyRollupsAsync();
    }

    [TestMethod]
    public async Task AStoppedHistory_StillCatchesUpWhatChangedWithoutASignal()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "recent", Today9, runs: 1);
        AddSession(harness, "middle", Today9.AddDays(-10), runs: 1);
        AddSession(harness, "old", Today9.AddDays(-40), runs: 1);
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
        Assert.AreEqual(HistoryState.StoppedHere, harness.Engine.Status.State);
        var floor = harness.Engine.Status.FloorDay;

        // Another application wrote to a session and made one of its own; this one was closed meanwhile.
        harness.Journals.Append("recent", EngineHarness.More("recent", Today9.AddMinutes(30)), Today9.AddMinutes(31));
        AddSession(harness, "tui", Today9.AddMinutes(45), runs: 1);
        await harness.RestartAsync();
        var states = new List<StatisticsStatus>();
        harness.Engine.StatusChanged += states.Add;
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.StoppedHere, harness.Engine.Status.State, "The history stays stopped where the user stopped it.");
        Assert.AreEqual(floor, harness.Engine.Status.FloorDay);
        Assert.IsTrue(states.Any(static status => status.State == HistoryState.Reading && status.Reason == "catch-up"));
        Assert.IsNotNull(await harness.Store.Store.GetJournalAsync("tui"));
        Assert.IsNull(await harness.Store.Store.GetJournalAsync("old"), "What is older than the stop is not read.");
        Assert.AreEqual(4L, await harness.SumAsync("usage_q", "requests"), "recent (2), middle and tui.");

        // A look later finds what changed again, without a restart.
        harness.Journals.Append("tui", EngineHarness.More("tui", Today9.AddMinutes(50)), Today9.AddMinutes(51));
        harness.Time.Advance(TimeSpan.FromMinutes(6));
        await harness.Engine.DrainAsync();
        Assert.AreEqual(5L, await harness.SumAsync("usage_q", "requests"));
        Assert.AreEqual(HistoryState.StoppedHere, harness.Engine.Status.State);

        // And the stop is kept across a restart that follows a catch-up.
        await harness.RestartAsync();
        await harness.Engine.DrainAsync();
        Assert.AreEqual(HistoryState.StoppedHere, harness.Engine.Status.State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StoppingACatchUp_LeavesTheHistoryAsItWas_AndTheRestIsReadAtTheNextLook(bool pauseFirst)
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9, runs: 1);
        AddSession(harness, "b", Today9.AddHours(-1), runs: 1);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);

        // Two sessions changed without a signal. The user stops the catch-up after the first one: it is not the reading the user
        // chose, and stopping it says nothing about where the statistics start.
        harness.Journals.Append("a", EngineHarness.More("a", Today9.AddMinutes(30)), Today9.AddMinutes(31));
        harness.Journals.Append("b", EngineHarness.More("b", Today9.AddMinutes(40)), Today9.AddMinutes(41));
        var stopped = false;
        harness.Engine.DataChanged += _ =>
        {
            if (!stopped)
            {
                stopped = true;
                if (pauseFirst)
                {
                    harness.Engine.PauseAsync().AsTask().GetAwaiter().GetResult();
                }

                harness.Engine.StopHereAsync().AsTask().GetAwaiter().GetResult();
            }
        };
        harness.Time.Advance(TimeSpan.FromMinutes(6));
        await harness.Engine.DrainAsync();

        var status = harness.Engine.Status;
        Assert.IsTrue(stopped);
        Assert.AreEqual(HistoryState.Done, status.State, "The history was read to its end before the catch-up, and still is.");
        Assert.IsNull(status.FloorDay);
        Assert.AreEqual(3L, await harness.SumAsync("usage_q", "requests"), "One of the two sessions was caught up.");

        // The state is kept, and the next look at the journals reads what is left.
        await harness.RestartAsync();
        await harness.Engine.InitializeAsync();
        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "a", "b"), await harness.Store.DumpAsync());
    }

    [TestMethod]
    public async Task StoppingTheCatchUpOfAStoppedHistory_KeepsItsFloor()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "recent", Today9, runs: 1);
        AddSession(harness, "middle", Today9.AddDays(-10), runs: 1);
        AddSession(harness, "old", Today9.AddDays(-40), runs: 1);
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
        Assert.AreEqual(HistoryState.StoppedHere, harness.Engine.Status.State);
        var floor = harness.Engine.Status.FloorDay;

        harness.Journals.Append("recent", EngineHarness.More("recent", Today9.AddMinutes(30)), Today9.AddMinutes(31));
        harness.Journals.Append("middle", EngineHarness.More("middle", Today9.AddMinutes(40)), Today9.AddMinutes(41));
        changes = 1;
        harness.Time.Advance(TimeSpan.FromMinutes(6));
        await harness.Engine.DrainAsync();

        Assert.AreEqual(2, changes, "The catch-up was stopped after its first session.");
        Assert.AreEqual(HistoryState.StoppedHere, harness.Engine.Status.State);
        Assert.AreEqual(floor, harness.Engine.Status.FloorDay);
        Assert.AreEqual(floor, harness.Engine.Status.CompleteFromDay);
    }

    [TestMethod]
    public async Task ASessionReadAgainThatIsPausedOnTheWay_KeepsItsOldNumbersUntilItIsReadToItsEnd()
    {
        await using var harness = await EngineHarness.CreateAsync(configure: static options => options.ChunkBytes = 1500);
        AddSession(harness, "a", Today9, runs: 10);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        var dump = await harness.Store.DumpAsync();

        // The rows were computed by an earlier version of the facts: the session is read again from its start, in several chunks,
        // and the reading is paused between two of them.
        await harness.Store.Store.WriteAsync(sql =>
            sql.Execute($"UPDATE {harness.Store.Store.Prefix}journal SET facts_version = 0, state = @p0", System.Text.Encoding.UTF8.GetBytes("{\"Version\":0}")));
        await harness.RestartAsync(static options => options.ChunkBytes = 1500);
        var paused = false;
        harness.Journals.Opening = (_, offset) =>
        {
            if (offset > 0 && !paused)
            {
                paused = true;
                harness.Engine.PauseAsync().AsTask().GetAwaiter().GetResult();
            }
        };
        await harness.Engine.DrainAsync();

        Assert.IsTrue(paused);
        Assert.AreEqual(HistoryState.Paused, harness.Engine.Status.State);
        Assert.AreEqual(dump, await harness.Store.DumpAsync(), "A part of the session does not take the place of all of it.");

        await harness.Engine.ResumeAsync();
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.AreEqual(dump, await harness.Store.DumpAsync());
        Assert.AreEqual(1L, await harness.Store.Store.ReadAsync(sql => sql.ScalarLong($"SELECT COUNT(*) FROM {harness.Store.Store.Prefix}journal WHERE facts_version = {SessionFactsState.CurrentVersion}")));
        await harness.Store.VerifyRollupsAsync();
    }

    [TestMethod]
    public async Task AStartThatFailed_IsTriedAgainWhenTheUserAsks_AndTheLoopReadsAgain()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9);
        var database = new FailingDatabase(harness.Store.Database) { Failing = true };
        var store = new StatisticsStore(database, new LocalDays(TimeZoneInfo.Utc));
        await using var engine = new StatisticsEngine(store, harness.Journals, new StatisticsEngineOptions { Time = harness.Time, StartDelay = TimeSpan.Zero, FlowDebounce = TimeSpan.Zero });
        using var lifetime = new CancellationTokenSource();
        var loop = engine.RunAsync(lifetime.Token);
        await WaitForAsync(() => engine.Status.State == HistoryState.Failed);
        Assert.AreEqual("The disk is not there.", engine.Status.Error);

        // Trying again while it still fails says so, and the engine waits for the next try.
        var again = await engine.ResumeAsync();
        Assert.AreEqual(HistoryState.Failed, again.State);
        Assert.IsFalse(loop.IsCompleted, "The loop waits for a start that works.");

        database.Failing = false;
        var status = await engine.ResumeAsync();
        Assert.AreEqual(HistoryState.NeedsChoice, status.State);
        Assert.IsNull(status.Error);

        // The loop is the one that reads: nothing else is driven here.
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await WaitForAsync(() => engine.Status.State == HistoryState.Done);
        Assert.IsNotNull(await store.GetJournalAsync("a"));

        await lifetime.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task AStartThatFailedAtItsLastStep_IsStillAFailedStart_AndCanBeTriedAgain()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9);
        var database = new FailingDatabase(harness.Store.Database);
        var options = new StatisticsEngineOptions { Time = harness.Time, StartDelay = TimeSpan.Zero, FlowDebounce = TimeSpan.Zero };

        // How many times a start reads the database: the last of them is the reading of the names of the projects.
        await using (var counting = new StatisticsEngine(new StatisticsStore(database, new LocalDays(TimeZoneInfo.Utc)), harness.Journals, options))
        {
            await counting.InitializeAsync();
        }

        var readsOfAStart = database.Reads;
        database.Reads = 0;
        database.FailingRead = readsOfAStart;
        var store = new StatisticsStore(database, new LocalDays(TimeZoneInfo.Utc));
        await using var engine = new StatisticsEngine(store, harness.Journals, options);
        using var lifetime = new CancellationTokenSource();
        var loop = engine.RunAsync(lifetime.Token);
        await WaitForAsync(() => engine.Status.State == HistoryState.Failed);

        // The disk is back: "Try again" starts the engine, whose state is known again.
        database.FailingRead = 0;
        var status = await engine.ResumeAsync();

        Assert.AreEqual(HistoryState.NeedsChoice, status.State);
        Assert.IsNull(status.Error);
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await WaitForAsync(() => engine.Status.State == HistoryState.Done);
        Assert.IsNotNull(await store.GetJournalAsync("a"));

        await lifetime.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < limit, "The engine did not get there in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>The database of the plugin, which can be made to fail: a disk that is not there when the application starts.</summary>
    private sealed class FailingDatabase(CodeAlta.Plugins.Abstractions.IPluginDatabase inner) : CodeAlta.Plugins.Abstractions.IPluginDatabase
    {
        public bool Failing { get; set; }

        /// <summary>Gets or sets the number of reads asked so far.</summary>
        public int Reads { get; set; }

        /// <summary>Gets or sets the read that fails, counted from one; zero for none.</summary>
        public int FailingRead { get; set; }

        public bool HasDatabase => inner.HasDatabase;

        public string TablePrefix => inner.TablePrefix;

        public ValueTask MigrateAsync(int version, CodeAlta.Plugins.Abstractions.PluginDatabaseMigration migrate, CancellationToken cancellationToken = default)
            => Failing ? throw new IOException("The disk is not there.") : inner.MigrateAsync(version, migrate, cancellationToken);

        public ValueTask<T> ReadAsync<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken = default)
            => Failing || ++Reads == FailingRead ? throw new IOException("The disk is not there.") : inner.ReadAsync(read, cancellationToken);

        public ValueTask WriteAsync(Func<Microsoft.Data.Sqlite.SqliteConnection, CancellationToken, ValueTask> write, CancellationToken cancellationToken = default)
            => Failing ? throw new IOException("The disk is not there.") : inner.WriteAsync(write, cancellationToken);
    }

    [TestMethod]
    public async Task ARunWithATimeFarFromTheOthers_IsListedWithTheTimeItLasted()
    {
        await using var harness = await EngineHarness.CreateAsync();
        // A run of fifteen seconds, with a record ten days after the others (a clock that was wrong), and a run of twenty seconds.
        var start = EngineHarness.Now.AddDays(-20);
        var odd = new JournalBuilder("odd", "codex");
        odd.Header(start).State(start)
            .User(start.AddSeconds(1), "odd-run", "a prompt")
            .Usage(start.AddSeconds(11), "odd-run")
            .Usage(start.AddDays(10), "odd-run")
            .Idle(start.AddDays(10).AddSeconds(5), "odd-run");
        harness.Journals.Set(odd, start.AddDays(10).AddSeconds(5));
        AddSession(harness, "plain", Today9, runs: 1);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        var request = new CodeAlta.Plugin.Statistics.Query.StatisticsRequest { Period = "all", Limit = 10 };
        var runs = await harness.Engine.Queries.RunsAsync(request, "longest");
        var session = await harness.Engine.Queries.SessionAsync("odd");

        CollectionAssert.AreEqual(new[] { "plain-run0", "odd-run" }, runs.Runs.Select(static run => run.RunId).ToArray(), "The ten days are not time of the run.");
        Assert.AreEqual(15_000d, (double)runs.Runs[1].DurationMs);
        Assert.AreEqual(15_000d, (double)session!.Runs.Single().DurationMs);
    }

    [TestMethod]
    public async Task AStoreOfTheFirstLayout_IsGivenTheNewColumnOfTheRuns_AndKeepsItsRows()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9, runs: 1);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        // The layout of a build before the column: the table of the runs without it, and the version that says so.
        var prefix = harness.Store.Store.Prefix;
        await harness.Store.Application.WriteAsync("test", async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE {prefix}run DROP COLUMN skipped_ms; UPDATE app_meta SET version = 1 WHERE owner = 'plugin:statistics';";
            await command.ExecuteNonQueryAsync(token);
        });
        await harness.RestartAsync();
        await harness.Engine.DrainAsync();

        var runs = await harness.Engine.Queries.RunsAsync(new CodeAlta.Plugin.Statistics.Query.StatisticsRequest { Period = "all", Limit = 10 }, "longest");
        Assert.AreEqual("a-run0", runs.Runs.Single().RunId);
        Assert.AreEqual(19_000d, (double)runs.Runs.Single().DurationMs);
    }

    [TestMethod]
    public async Task StoppingBeforeAnythingIsRead_StartsTheStatisticsToday_AndMoreCanBeReadLater()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "today", Today9, runs: 1);
        AddSession(harness, "old", Today9.AddDays(-40), runs: 1);

        // The user chooses everything and stops at once, before the engine listed a journal.
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        var stopped = await harness.Engine.StopHereAsync();
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.StoppedHere, stopped.State);
        Assert.AreEqual(20261009, stopped.FloorDay, "Nothing was reached: the statistics start today.");
        Assert.IsNull(await harness.Store.Store.GetJournalAsync("old"));

        // "Read more history" is not stuck: any choice that goes back before today reads.
        var status = await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        Assert.AreEqual(HistoryState.Reading, status.State);
        await harness.Engine.DrainAsync();
        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.IsNotNull(await harness.Store.Store.GetJournalAsync("old"));
    }

    [TestMethod]
    public async Task ASkippedSession_StaysCountedAtTheNextLooks_AndTryAgainReadsIt()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "broken", Today9);
        AddSession(harness, "fine", Today9.AddHours(-1));
        harness.Journals.FailAfter("broken", 10);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        Assert.AreEqual(1, harness.Engine.Status.SkippedCount);

        // The next look at the journals finds nothing new: the session is still one that could not be read.
        harness.Time.Advance(TimeSpan.FromMinutes(6));
        await harness.Engine.DrainAsync();
        var status = harness.Engine.Status;
        Assert.AreEqual(HistoryState.Done, status.State);
        Assert.AreEqual(1, status.SkippedCount);
        Assert.AreEqual("broken", status.Skipped.Single().SessionId);
        Assert.IsFalse(string.IsNullOrEmpty(status.Skipped.Single().Reason));

        // The disk is back and the user asks to try again: the journal did not change, and it is read.
        harness.Journals.StopFailing();
        await harness.Engine.ResumeAsync();
        await harness.Engine.DrainAsync();

        Assert.AreEqual(HistoryState.Done, harness.Engine.Status.State);
        Assert.AreEqual(0, harness.Engine.Status.SkippedCount);
        Assert.IsNotNull(await harness.Store.Store.GetJournalAsync("broken"));
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "broken", "fine"), await harness.Store.DumpAsync());
    }

    [TestMethod]
    public async Task AJournalReplacedWhileItIsReadAgain_GivesTheFactsOfTheNewFileOnly()
    {
        await using var harness = await EngineHarness.CreateAsync(configure: static options => options.ChunkBytes = 1500);
        AddSession(harness, "a", Today9, runs: 10);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        // The rows were computed by an earlier version of the facts: the session is read again from its start, in several chunks.
        await harness.Store.Store.WriteAsync(sql =>
            sql.Execute($"UPDATE {harness.Store.Store.Prefix}journal SET facts_version = 0, state = @p0", System.Text.Encoding.UTF8.GetBytes("{\"Version\":0}")));
        await harness.RestartAsync(static options => options.ChunkBytes = 1500);
        var replaced = false;
        harness.Journals.Opening = (_, offset) =>
        {
            if (offset > 0 && !replaced)
            {
                // Between two chunks the file becomes another one.
                replaced = true;
                var other = EngineHarness.Session("a", Today9.AddHours(-2), runs: 9, input: 7000);
                harness.Journals.Set(other, Today9.AddHours(-1));
            }
        };
        await harness.Engine.DrainAsync();

        Assert.IsTrue(replaced);
        Assert.AreEqual(9L, await harness.SumAsync("usage_q", "requests"), "The requests of the first file are not added to those of the second.");
        Assert.AreEqual(await ReferenceDumpAsync(harness.Journals, "a"), await harness.Store.DumpAsync());
        await harness.Store.VerifyRollupsAsync();
    }

    [TestMethod]
    public async Task Reset_AsksTheNamesOfTheProjectsAgain()
    {
        var names = new Dictionary<string, string> { ["22222222-2222-2222-2222-222222222222"] = "Alpha" };
        await using var harness = await EngineHarness.CreateAsync(configure: options => options.ResolveProjectNames = _ => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(names));
        AddSession(harness, "a", Today9);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        Assert.AreEqual("Alpha", (await harness.Store.Store.GetProjectNamesAsync()).Single().Value);

        await harness.Engine.ResetAsync();
        Assert.AreEqual(0, (await harness.Store.Store.GetProjectNamesAsync()).Count);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        Assert.AreEqual("Alpha", (await harness.Store.Store.GetProjectNamesAsync()).Single().Value, "The names are kept again after a reset.");
    }

    [TestMethod]
    public async Task ADayOfZeroInTheStore_IsNoDay()
    {
        await using var harness = await EngineHarness.CreateAsync();
        AddSession(harness, "a", Today9);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();

        // A catch-up of a history without a floor writes 0 for "complete from everything"; the application is closed before it ends.
        await harness.Store.Store.SetMetaAsync(new Dictionary<string, string?> { ["history.complete_from_day"] = "0" });
        await harness.RestartAsync();
        await harness.Engine.InitializeAsync();

        Assert.IsNull(harness.Engine.Status.CompleteFromDay);
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
    public async Task ARunThatWaitedForLong_IsNotLeftInterruptedWhenItGoesOn()
    {
        await using var harness = await EngineHarness.CreateAsync();
        // A run that has written nothing for three hours: it waits for an answer of the user.
        var start = EngineHarness.Now.AddHours(-3);
        AddSession(harness, "waiting", start, runs: 1, endLastRun: false);
        await harness.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await harness.Engine.DrainAsync();
        Assert.AreEqual(1L, await harness.SumAsync("activity_year", "runs_interrupted"), "Quiet for long: taken for interrupted.");

        // The answer comes, and the same run goes on and ends.
        var more = new JournalBuilder("waiting");
        var resumed = EngineHarness.Now.AddMinutes(-2);
        more.Usage(resumed, "waiting-run0", input: 500, output: 50).Assistant(resumed.AddSeconds(5), "waiting-run0").Idle(resumed.AddSeconds(10), "waiting-run0");
        harness.Journals.Append("waiting", more.ToBytes(), resumed.AddSeconds(10));
        harness.Engine.Signal("waiting");
        await harness.Engine.DrainAsync();

        var runs = await harness.Store.Store.ReadAsync(sql => sql.Query(
            $"SELECT run_id, outcome, requests FROM {harness.Store.Store.Prefix}run",
            static reader => (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2))));
        CollectionAssert.AreEqual(new[] { ("waiting-run0", (long)RunOutcome.Completed, 2L) }, runs);
        Assert.AreEqual(0L, await harness.SumAsync("activity_year", "runs_interrupted"));
        Assert.AreEqual(1L, await harness.SumAsync("activity_year", "runs_completed"));
        Assert.AreEqual(1L, await harness.SumAsync("activity_year", "runs_started"));
        Assert.AreEqual(1L, await harness.Store.Store.ReadAsync(sql => sql.ScalarLong(
            $"SELECT COALESCE(SUM(n), 0) FROM {harness.Store.Store.Prefix}histogram_year WHERE measure = {(int)HistogramMeasure.RunDurationMs}")), "One run, one duration.");
        await harness.Store.VerifyRollupsAsync();
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
