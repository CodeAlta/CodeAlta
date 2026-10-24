using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Journal;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugin.Statistics.Store;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Plugin.Statistics.Tests;

[TestClass]
public sealed class StatisticsArchiveTests
{
    private const string SessionId = "11111111-1111-1111-1111-111111111111";

    private static JournalBuilder Journal(long input = 1234) => new JournalBuilder(SessionId)
        .Header(JournalBuilder.Time(0), parent: "parent", createdByKind: "agent", createdBySession: "parent")
        .User(JournalBuilder.Time(1), "r1", "PRIVATE PROMPT")
        .Usage(JournalBuilder.Time(2), "r1", input: input, cost: 1.25, costUnit: "usd")
        .Assistant(JournalBuilder.Time(3), "r1", "PRIVATE ANSWER")
        .Idle(JournalBuilder.Time(4), "r1");

    // The same journal with more runs appended, the way a journal grows.
    private static JournalBuilder Journal(long input, params long[] laterInputs)
    {
        var journal = Journal(input);
        for (var index = 0; index < laterInputs.Length; index++)
        {
            var run = "r" + (index + 2);
            var minute = 5 + (4 * index);
            journal.User(JournalBuilder.Time(minute), run, "PRIVATE PROMPT")
                .Usage(JournalBuilder.Time(minute + 1), run, input: laterInputs[index], cost: 1.25, costUnit: "usd")
                .Assistant(JournalBuilder.Time(minute + 2), run, "PRIVATE ANSWER")
                .Idle(JournalBuilder.Time(minute + 3), run);
        }

        return journal;
    }

    private static async Task<(StatisticsEngine Engine, string Path)> EngineAsync(StoreHarness harness, byte[] bytes)
    {
        var layout = new AgentRuntimePathLayout(harness.Root);
        var path = layout.GetSessionFilePath(SessionId, JournalBuilder.Time(0));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        var engine = new StatisticsEngine(harness.Store, new FileSystemSessionJournalCatalog(layout), new StatisticsEngineOptions { StartDelay = TimeSpan.Zero, FlowDebounce = TimeSpan.Zero });
        await engine.InitializeAsync();
        return (engine, path);
    }

    [TestMethod]
    public async Task RetentionSave_KeepsWhatStatisticsRead_WithoutReadingTheJournal_AndDoesNotSaveTextOrCheckpoint()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();

        // Same length and write time, other bytes: a save that read the journal would fail on them or save other numbers.
        var stamp = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)'x', checked((int)length)).ToArray());
        File.SetLastWriteTimeUtc(path, stamp);
        var prepared = await engine.PrepareAsync(SessionId);
        Assert.IsNotNull(prepared.Receipt, prepared.FailureReason);
        Assert.IsNull(prepared.Receipt.Source.Sha256, "The journal is described, not read.");
        Assert.AreEqual(length, prepared.Receipt.Source.Length);
        Assert.AreEqual(stamp, prepared.Receipt.Source.LastWriteUtc.UtcDateTime);
        var archive = (await new StatisticsArchiveStore(harness.Root).ReadAsync()).Single();
        var text = System.Text.Encoding.UTF8.GetString(archive.Payload);
        Assert.DoesNotContain("PRIVATE PROMPT", text);
        Assert.DoesNotContain("PRIVATE ANSWER", text);
        Assert.DoesNotContain("OpenRuns", text);
        Assert.Contains("parent", text);
        var again = await engine.PrepareAsync(SessionId);
        Assert.AreEqual(prepared.Receipt, again.Receipt, again.FailureReason);

        File.Delete(path);
        await engine.ResetAsync();
        Assert.AreEqual(1234L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(input_tokens) FROM {harness.Store.Table("usage_q")}")));
        var coverage = await engine.Queries.CoverageAsync(new StatisticsRequest { Period = "all" });
        Assert.AreEqual(1, coverage.SavedSessions);
        Assert.AreEqual(0, coverage.UnverifiedSavedSessions, "A session Statistics had read to the end is saved in full.");
    }

    [TestMethod]
    public async Task DeletionReconciliation_RejectsPresentSource_AndCanRepeatAfterPartialCleanup()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        var prepared = await engine.PrepareAsync(SessionId);
        Assert.IsNotNull(prepared.Receipt, prepared.FailureReason);
        Assert.IsFalse(await engine.ReconcileDeletedAsync(prepared.Receipt, artifactsRemaining: false));
        File.Delete(path);
        Assert.IsTrue(await engine.ReconcileDeletedAsync(prepared.Receipt, artifactsRemaining: true));
        Assert.IsTrue((await harness.Store.GetJournalAsync(SessionId))!.Deleted);
        var saved = await harness.DumpAsync("archive");
        Assert.IsTrue(await engine.ReconcileDeletedAsync(prepared.Receipt, artifactsRemaining: false));
        Assert.AreEqual(saved, await harness.DumpAsync("archive"));
        Assert.AreEqual(1, engine.Status.SavedSessions);
        await File.WriteAllBytesAsync(path, Journal().ToBytes());
        Assert.IsFalse(await engine.ReconcileDeletedAsync(prepared.Receipt, artifactsRemaining: false));
    }

    [TestMethod]
    public async Task DeletionReconciliation_ImportsOnlyTheSessionItReconciles()
    {
        // Retention reconciles one deleted session after the other: importing every saved session again each time made
        // a cleanup of hundreds of sessions slower with every session it removed.
        const string otherId = "22222222-2222-2222-2222-222222222222";
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        var otherPath = new AgentRuntimePathLayout(harness.Root).GetSessionFilePath(otherId, JournalBuilder.Time(0));
        await File.WriteAllBytesAsync(otherPath, new JournalBuilder(otherId).Header(JournalBuilder.Time(0))
            .User(JournalBuilder.Time(1), "r1", "PROMPT").Usage(JournalBuilder.Time(2), "r1", input: 50, cost: 1, costUnit: "usd")
            .Assistant(JournalBuilder.Time(3), "r1", "ANSWER").Idle(JournalBuilder.Time(4), "r1").ToBytes());
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();

        var first = (await engine.PrepareAsync(SessionId)).Receipt;
        Assert.IsNotNull(first);
        File.Delete(path);
        Assert.IsTrue(await engine.ReconcileDeletedAsync(first, artifactsRemaining: false));
        // A mark on the facts of the first session: importing its saved copy again would erase it.
        await harness.Store.WriteAsync(sql => sql.Execute($"UPDATE {harness.Store.Table("usage_q")} SET input_tokens=999 WHERE session_id=@p0", SessionId));

        var second = (await engine.PrepareAsync(otherId)).Receipt;
        Assert.IsNotNull(second);
        File.Delete(otherPath);
        Assert.IsTrue(await engine.ReconcileDeletedAsync(second, artifactsRemaining: false));

        Assert.AreEqual(999L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(input_tokens) FROM {harness.Store.Table("usage_q")} WHERE session_id=@p0", SessionId)));
        Assert.IsTrue((await harness.Store.GetJournalAsync(otherId))!.Deleted);
        Assert.AreEqual(2, engine.Status.SavedSessions);
    }

    [TestMethod]
    public async Task CancellationAndWriteFailure_NeverIssueAReceipt()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await engine.PrepareAsync(SessionId, cancellation.Token));
        var archive = new StatisticsArchiveStore(harness.Root);
        // Native SQLite does not honor FileShare.None on Unix; use its writer lock on all platforms.
        using (var locked = new SqliteConnection($"Data Source={archive.DatabasePath};Pooling=False"))
        {
            locked.Open();
            using var transaction = locked.BeginTransaction(deferred: false);
            var result = await engine.PrepareAsync(SessionId);
            Assert.IsNull(result.Receipt);
            Assert.IsNotNull(result.FailureReason);
        }

        Assert.IsTrue(File.Exists(path));
        Assert.IsEmpty(await archive.ReadAsync());
    }

    [TestMethod]
    [DataRow("longer")]
    [DataRow("touched")]
    public async Task JournalChangedSinceStatisticsRead_IsNotSaved_UntilStatisticsReadsItAgain(string change)
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        if (change == "longer")
        {
            await File.WriteAllBytesAsync(path, Journal(1234, 3000).ToBytes());
        }
        else
        {
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        }

        var result = await engine.PrepareAsync(SessionId);
        Assert.IsNull(result.Receipt);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsEmpty(await new StatisticsArchiveStore(harness.Root).ReadAsync());
        Assert.IsTrue(File.Exists(path));
        engine.Signal(SessionId);
        await engine.DrainAsync();
        var saved = await engine.PrepareAsync(SessionId);
        Assert.IsNotNull(saved.Receipt, saved.FailureReason);
        Assert.AreEqual(change == "longer" ? 4234L : 1234L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(input_tokens) FROM {harness.Store.Table("usage_q")}")));
    }

    [TestMethod]
    public async Task ResetRecoveryForgetAndTimezone_PreserveAllBaseFamiliesRunsAndLineageExactlyOnce()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var batch = SyntheticFacts.Batch(new Random(212), SessionId, SyntheticFacts.SpringStart, 200 * 96, 100);
        batch.Runs["gap"] = new RunRow { SessionId = SessionId, RunId = "gap", Start = JournalBuilder.Time(0), End = JournalBuilder.Time(30), SkippedMs = 12345, Outcome = RunOutcome.Completed };
        await harness.Store.ApplyAsync(new ApplyRequest { SessionId = SessionId, Batch = batch, Cursor = new JournalCursor(100, null, new SessionFactsState()), FileLength = 100 });
        await harness.Store.MarkDeletedAsync([SessionId]);
        var before = await harness.DumpAsync("archive");
        var queries = await QuerySnapshotAsync(harness.Store);
        await harness.Store.ResetAsync();
        Assert.AreEqual(before, await harness.DumpAsync("archive"));
        Assert.AreEqual(queries, await QuerySnapshotAsync(harness.Store));
        Assert.AreEqual(0, (await harness.Store.ForgetDeletedAsync()).Sessions);
        await harness.Store.ResetAsync();
        await harness.ReopenAsync();
        Assert.AreEqual(before, await harness.DumpAsync("archive"));
        Assert.AreEqual(queries, await QuerySnapshotAsync(harness.Store));
        await harness.VerifyRollupsAsync();

        // Simulate generic application projection-table removal/recovery, leaving the separate archive alone.
        await harness.Store.WriteAsync(sql =>
        {
            foreach (var table in sql.Query("SELECT name FROM sqlite_master WHERE type='table' AND substr(name,1,11)='statistics_'", reader => reader.GetString(0)))
                sql.Execute($"DELETE FROM {table}");
        });
        await harness.ReopenAsync();
        Assert.AreEqual(before, await harness.DumpAsync("archive"));
        await harness.ReopenAsync(StoreHarness.Zone("Pacific/Chatham"));
        await harness.VerifyRollupsAsync();
        Assert.AreEqual(12345L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT skipped_ms FROM {harness.Store.Table("run")} WHERE run_id='gap'")));
    }

    [TestMethod]
    public async Task Startup_LeavesSavedSessionsThatAreAlreadyEffectiveAsTheyAre()
    {
        // Every start imported each saved session again, twice: hundreds of saved sessions held the database for seconds and
        // wrote hundreds of megabytes of temporary files, to put back the rows that were already there.
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var batch = SyntheticFacts.Batch(new Random(212), SessionId, SyntheticFacts.SpringStart, 96, 10);
        await harness.Store.ApplyAsync(new ApplyRequest { SessionId = SessionId, Batch = batch, Cursor = JournalCursor.Start });
        await harness.Store.MarkDeletedAsync([SessionId]);
        await harness.Store.ResetAsync();
        Assert.IsTrue(await harness.Store.IsArchivedProjectionAsync(SessionId, CancellationToken.None));
        // A mark on the saved facts and not on their roll-ups: an import would delete it and add the roll-ups up again.
        await harness.Store.WriteAsync(sql => sql.Execute($"UPDATE {harness.Store.Table("usage_q")} SET input_tokens=input_tokens+999 WHERE session_id=@p0", SessionId));
        var before = await harness.DumpAsync();
        var updated = await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT updated_ms FROM {harness.Store.Table("journal")} WHERE session_id=@p0", SessionId));

        await harness.ReopenAsync();
        await using var engine = new StatisticsEngine(harness.Store, new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(harness.Root)));
        await engine.InitializeAsync();

        Assert.AreEqual(before, await harness.DumpAsync());
        Assert.AreEqual(updated, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT updated_ms FROM {harness.Store.Table("journal")} WHERE session_id=@p0", SessionId)));
        Assert.AreEqual(1, engine.Status.SavedSessions);
    }

    [TestMethod]
    [DataRow("run")]
    [DataRow("usage_q")]
    [DataRow("session")]
    [DataRow("journal")]
    public async Task Startup_RestoresAnEffectiveSavedSessionWhoseRowsAreMissing(string table)
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var batch = SyntheticFacts.Batch(new Random(212), SessionId, SyntheticFacts.SpringStart, 96, 10);
        await harness.Store.ApplyAsync(new ApplyRequest { SessionId = SessionId, Batch = batch, Cursor = JournalCursor.Start });
        await harness.Store.MarkDeletedAsync([SessionId]);
        await harness.Store.ResetAsync();
        var before = await harness.DumpAsync();
        await harness.Store.WriteAsync(sql => sql.Execute($"DELETE FROM {harness.Store.Table(table)} WHERE session_id=@p0", SessionId));
        Assert.IsTrue(await harness.Store.IsArchivedProjectionAsync(SessionId, CancellationToken.None));

        await harness.ReopenAsync();

        Assert.AreEqual(before, await harness.DumpAsync());
        Assert.IsTrue((await harness.Store.GetJournalAsync(SessionId))!.Deleted);
        await harness.VerifyRollupsAsync();
    }

    private static async Task<string> QuerySnapshotAsync(StatisticsStore store)
    {
        var queries = new StatisticsQueries(store);
        var results = new List<string>();
        foreach (var frequency in new[] { StatisticsFrequency.Day, StatisticsFrequency.Month, StatisticsFrequency.Year })
        {
            var request = new StatisticsRequest { Period = "2026-01-01..2026-12-31", Frequency = frequency, Limit = 100 };
            foreach (var metric in new[] { "tokens", "runs", "active-time", "cost", "tool-calls", "sessions-at-once" })
                results.Add(StatisticsJson.Serialize(await queries.SeriesAsync(request, metric)));
            // Compare the recovered facts, not the time of the recovery write.
            results.Add(StatisticsJson.Serialize((await queries.SummaryAsync(request)) with { DataUpdatedAt = null }));
            results.Add(StatisticsJson.Serialize(await queries.ToolsAsync(request)));
            results.Add(StatisticsJson.Serialize(await queries.ModelsAsync(request)));
            results.Add(StatisticsJson.Serialize(await queries.ProjectsAsync(request)));
            results.Add(StatisticsJson.Serialize(await queries.SessionsAsync(request)));
            results.Add(StatisticsJson.Serialize(await queries.RecordsAsync(request)));
            results.Add(StatisticsJson.Serialize(await queries.RunsAsync(request)));
            results.Add(StatisticsJson.Serialize(await queries.DistributionAsync(request, "run-duration")));
            results.Add(StatisticsJson.Serialize(await queries.HealthAsync(request)));
        }

        results.Add(StatisticsJson.Serialize((await queries.SessionAsync(SessionId, withChildren: true))!));
        return string.Join('\n', results.Select(json =>
        {
            var value = JsonNode.Parse(json)!.AsObject();
            value.Remove("query"); // Coverage intentionally changes when the source is represented by a saved generation.
            return value.ToJsonString();
        }));
    }

    [TestMethod]
    public async Task MissingSourceMigration_DoesNotInventVerification_AndResetChartsNeedNoHistoryChoice()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        File.Delete(path);
        var state = await engine.ResetAsync();
        Assert.AreEqual(HistoryState.NeedsChoice, state.State);
        Assert.AreEqual(1, state.SavedSessions);
        var coverage = await engine.Queries.CoverageAsync(new StatisticsRequest { Period = "all" });
        Assert.AreEqual(1, coverage.SavedSessions);
        Assert.AreEqual(1, coverage.UnverifiedSavedSessions);
        var entry = (await new StatisticsArchiveStore(harness.Root).ReadAsync()).Single();
        Assert.IsNull(entry.Source);
        Assert.IsNull((await engine.PrepareAsync(SessionId)).Receipt);
    }

    [TestMethod]
    public async Task HistoricalFactsVersion_RecordsAvailabilityWithoutRewritingValues()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var batch = SyntheticFacts.Batch(new Random(12), SessionId, SyntheticFacts.SpringStart, 96, 10);
        await harness.Store.ApplyAsync(new ApplyRequest { SessionId = SessionId, Batch = batch, Cursor = JournalCursor.Start, FactsVersion = 4 });
        await harness.Store.MarkDeletedAsync([SessionId]);
        var before = await harness.DumpAsync("archive");
        await harness.Store.ResetAsync();
        Assert.AreEqual(before, await harness.DumpAsync("archive"));
        await harness.Store.SetMetaAsync(new Dictionary<string, string?> { ["history.choice"] = "all", ["history.done"] = "1" });
        var coverage = await new StatisticsQueries(harness.Store).CoverageAsync(new StatisticsRequest { Period = "all" });
        CollectionAssert.AreEqual(new[] { 4 }, coverage.SavedFactsVersions.ToArray());
        Assert.Contains("run.skipped_ms", coverage.UnavailableSavedMetrics);
        Assert.IsFalse(coverage.Complete);
    }

    [TestMethod]
    public async Task DeletedPartialHistory_PreservesFactsWithoutInventingTheUnknownSourceLength()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var batch = SyntheticFacts.Batch(new Random(44), SessionId, SyntheticFacts.SpringStart, 96, 10);
        await harness.Store.ApplyAsync(new ApplyRequest { SessionId = SessionId, Batch = batch, Cursor = new JournalCursor(1234, null, new SessionFactsState()), FileLength = 0 });
        await harness.Store.MarkDeletedAsync([SessionId]);
        var before = await harness.DumpAsync("archive");
        await harness.Store.ResetAsync();
        Assert.AreEqual(before, await harness.DumpAsync("archive"));
        var saved = StatisticsArchivePayload.Deserialize((await new StatisticsArchiveStore(harness.Root).ReadAsync()).Single().Payload);
        Assert.AreEqual(0L, saved.SourceLength, "Zero preserves the incremental reader's unknown/incomplete length sentinel.");
        Assert.AreEqual(1234L, saved.ReadOffset);
        Assert.IsFalse(saved.FullSourceVerified);
        Assert.IsNull(saved.Source);
    }

    [TestMethod]
    [DataRow("unread")]
    [DataRow("pending")]
    public async Task SessionNotReadToTheEnd_IsNotSaved(string problem)
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var text = System.Text.Encoding.UTF8.GetString(Journal().ToBytes());
        // A line still being written is not read: Statistics stops before it.
        if (problem == "pending") text += "{\"$type\":";
        var (engine, _) = await EngineAsync(harness, System.Text.Encoding.UTF8.GetBytes(text));
        await using var lifetime = engine;
        if (problem != "unread")
        {
            await engine.ChooseHistoryAsync(HistoryChoice.All);
            await engine.DrainAsync();
        }

        var result = await engine.PrepareAsync(SessionId);
        Assert.IsNull(result.Receipt);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsEmpty(await new StatisticsArchiveStore(harness.Root).ReadAsync());
    }

    [TestMethod]
    public async Task UnreadOrLimitedHistory_IsNotSaved_AndAPausedHistoryKeepsWhatItRead()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, _) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        Assert.IsNull((await engine.PrepareAsync(SessionId)).Receipt, "No history choice.");
        await engine.ChooseHistoryAsync(HistoryChoice.FromToday);
        await engine.DrainAsync();
        Assert.IsNull((await engine.PrepareAsync(SessionId)).Receipt, "A limited history did not read the old session.");
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        await engine.PauseAsync();
        var ready = await engine.PrepareAsync(SessionId);
        Assert.IsNotNull(ready.Receipt, ready.FailureReason);
        await engine.DisposeAsync();
        Assert.IsNull((await engine.PrepareAsync(SessionId)).Receipt);
        Assert.IsNull(StatisticsPlugin.CreateForDesktop(new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(harness.Root))).RetirementArchive);
    }

    [TestMethod]
    public async Task CorruptArchive_PreventsReset_WithoutChangingReadableFactsOrChoice()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, _) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        var result = await engine.PrepareAsync(SessionId);
        Assert.IsNotNull(result.Receipt, result.FailureReason);
        var before = await harness.DumpAsync();
        using (var connection = new SqliteConnection($"Data Source={new StatisticsArchiveStore(harness.Root).DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE archive SET payload=x'010203'";
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () => await engine.ResetAsync());
        Assert.AreEqual(before, await harness.DumpAsync());
        Assert.AreEqual("all", engine.Status.Choice);
        Assert.IsNull((await engine.PrepareAsync(SessionId)).Receipt);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingArchiveDirectory_PreventsResetOfImportedDeletedFacts(bool reopen)
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var batch = SyntheticFacts.Batch(new Random(212), SessionId, SyntheticFacts.SpringStart, 96, 10);
        await harness.Store.ApplyAsync(new ApplyRequest { SessionId = SessionId, Batch = batch, Cursor = JournalCursor.Start });
        await harness.Store.MarkDeletedAsync([SessionId]);
        await harness.Store.ResetAsync();
        Assert.IsTrue(await harness.Store.IsArchivedProjectionAsync(SessionId, CancellationToken.None));
        var before = await harness.DumpAsync();
        var directory = Path.GetDirectoryName(new StatisticsArchiveStore(harness.Root).DatabasePath)!;
        Directory.Delete(directory, recursive: true);

        if (reopen)
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () => await harness.ReopenAsync());
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () => await harness.Store.ResetAsync());
        Assert.AreEqual(before, await harness.DumpAsync(), "The remaining projection is irreplaceable while the archive is missing.");
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public async Task MissingArchiveDirectory_PreventsNewRetirementReceipts()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        var prepared = await engine.PrepareAsync(SessionId);
        Assert.IsNotNull(prepared.Receipt, prepared.FailureReason);
        var before = await harness.DumpAsync("archive");
        var directory = Path.GetDirectoryName(new StatisticsArchiveStore(harness.Root).DatabasePath)!;
        Directory.Delete(directory, recursive: true);

        var result = await engine.PrepareAsync(SessionId);
        Assert.IsNull(result.Receipt);
        Assert.IsNotNull(result.FailureReason);
        // Saving fails without losing facts or issuing a receipt.
        Assert.AreEqual(before, await harness.DumpAsync("archive"));
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public async Task ReturningJournal_MustBeVerifiedBeforeReplacingArchive_AndNewerDeletedFactsArePreserved()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        Assert.IsNotNull((await engine.PrepareAsync(SessionId)).Receipt);
        File.Delete(path);
        await engine.ResetAsync();
        var old = await harness.DumpAsync("archive");
        await File.WriteAllBytesAsync(path, Journal(9000).ToBytes(trailingNewline: false));
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        Assert.AreEqual(old, await harness.DumpAsync("archive"), "Incomplete returned source must not replace saved facts.");

        await File.WriteAllBytesAsync(path, Journal(9000).ToBytes());
        engine.Signal(SessionId);
        await engine.DrainAsync();
        Assert.AreEqual(9000L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(input_tokens) FROM {harness.Store.Table("usage_q")}")));
        File.Delete(path);
        await engine.ResetAsync();
        Assert.AreEqual(9000L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(input_tokens) FROM {harness.Store.Table("usage_q")}")));
        Assert.AreEqual(1, (await new StatisticsArchiveStore(harness.Root).ReadAsync()).Count);
    }

    [TestMethod]
    public async Task RecoveryOfOlderApplicationProjection_DoesNotRollBackTheDurableGeneration()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal(1000).ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        Assert.IsNotNull((await engine.PrepareAsync(SessionId)).Receipt);
        using var oldReader = new SessionCatchUp();
        using var oldBytes = new MemoryStream(Journal(1000).ToBytes());
        var old = oldReader.CatchUp(SessionId, oldBytes, null, long.MaxValue, CancellationToken.None);
        await File.WriteAllBytesAsync(path, Journal(1000, 8000).ToBytes());
        engine.Signal(SessionId);
        await engine.DrainAsync();
        Assert.IsNotNull((await engine.PrepareAsync(SessionId)).Receipt);
        File.Delete(path);
        // The recovered database predates the durable generation and its provenance table.
        await harness.Store.ApplyAsync(new ApplyRequest { SessionId = SessionId, Batch = old.Batch, Cursor = old.Cursor, Replace = true });
        await harness.Store.WriteAsync(sql => sql.Execute($"DELETE FROM {harness.Store.Table("archive")}"));
        await harness.ReopenAsync();
        await using var recovered = new StatisticsEngine(harness.Store, new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(harness.Root)));
        await recovered.InitializeAsync();
        Assert.AreEqual(9000L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(input_tokens) FROM {harness.Store.Table("usage_q")}")));
        await recovered.ResetAsync();
        Assert.AreEqual(9000L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(input_tokens) FROM {harness.Store.Table("usage_q")}")));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RecoveryWithPresentSource_RequiresVerifiedRereadBoundToCurrentGeneration(bool oldMarker, bool completeSource)
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal(1000).ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        var first = (await engine.PrepareAsync(SessionId)).Receipt!;
        using var oldReader = new SessionCatchUp();
        using var oldBytes = new MemoryStream(Journal(1000).ToBytes());
        var old = oldReader.CatchUp(SessionId, oldBytes, null, long.MaxValue, CancellationToken.None);
        await File.WriteAllBytesAsync(path, Journal(1000, 8000).ToBytes());
        engine.Signal(SessionId);
        await engine.DrainAsync();
        Assert.IsNotNull((await engine.PrepareAsync(SessionId)).Receipt);
        await harness.Store.ApplyAsync(new ApplyRequest { SessionId = SessionId, Batch = old.Batch, Cursor = old.Cursor, Replace = true });
        await harness.Store.WriteAsync(sql => sql.Execute(oldMarker
            ? $"UPDATE {harness.Store.Table("archive")} SET generation=@p0"
            : $"DELETE FROM {harness.Store.Table("archive")} WHERE @p0 IS NOT NULL", first.Generation));
        await File.WriteAllBytesAsync(path, Journal(1000, 8000, 3000).ToBytes(trailingNewline: completeSource));
        await harness.ReopenAsync();
        await using var recovered = new StatisticsEngine(harness.Store, new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(harness.Root)));
        await recovered.InitializeAsync();
        await recovered.ChooseHistoryAsync(HistoryChoice.All);
        await recovered.DrainAsync();
        Assert.AreEqual(completeSource ? 12000L : 9000L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(input_tokens) FROM {harness.Store.Table("usage_q")}")));
        File.Delete(path);
        await recovered.ResetAsync();
        Assert.AreEqual(completeSource ? 12000L : 9000L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(input_tokens) FROM {harness.Store.Table("usage_q")}")));
    }
}
