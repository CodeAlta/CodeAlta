using System.Security.Cryptography;
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
    public async Task FullSourceReceipt_IsBoundToEveryByte_AndDoesNotSaveTextOrCheckpoint()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        var prepared = await engine.PrepareAsync(SessionId);
        Assert.IsNotNull(prepared.Receipt, prepared.FailureReason);
        Assert.IsTrue(await engine.ValidateAsync(prepared.Receipt));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), prepared.Receipt.Source.Sha256);
        var archive = (await new StatisticsArchiveStore(harness.Root).ReadAsync()).Single();
        var text = System.Text.Encoding.UTF8.GetString(archive.Payload);
        Assert.DoesNotContain("PRIVATE PROMPT", text);
        Assert.DoesNotContain("PRIVATE ANSWER", text);
        Assert.DoesNotContain("OpenRuns", text);
        Assert.Contains("parent", text);
        var again = await engine.PrepareAsync(SessionId);
        Assert.AreEqual(prepared.Receipt, again.Receipt, again.FailureReason);

        // Same length and mtime, unchanged header: the incremental prefix fingerprint would miss this edit.
        var stamp = File.GetLastWriteTimeUtc(path);
        var mutated = (await File.ReadAllTextAsync(path)).Replace("1234", "4321", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, mutated);
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.IsFalse(await engine.ValidateAsync(prepared.Receipt));
    }

    [TestMethod]
    public async Task DeletionReconciliation_RejectsPresentSource_AndCanRepeatAfterPartialCleanup()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
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
    public async Task CancellationAndWriteFailure_NeverIssueAReceipt()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, path) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await engine.PrepareAsync(SessionId, cancellation.Token));
        var archive = new StatisticsArchiveStore(harness.Root);
        await using (var locked = new FileStream(archive.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await engine.PrepareAsync(SessionId);
            Assert.IsNull(result.Receipt);
            Assert.IsNotNull(result.FailureReason);
        }

        Assert.IsTrue(File.Exists(path));
        Assert.IsEmpty(await archive.ReadAsync());
    }

    [TestMethod]
    [DataRow(3, false)]
    [DataRow(4, true)]
    public async Task SourceMutationAroundCommit_InvalidatesReceiptWithoutDiscardingCommittedFacts(int lookup, bool committed)
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var layout = new AgentRuntimePathLayout(harness.Root);
        var path = layout.GetSessionFilePath(SessionId, JournalBuilder.Time(0));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Journal().ToBytes());
        var catalog = new ChangingCatalog(new FileSystemSessionJournalCatalog(layout));
        await using var engine = new StatisticsEngine(harness.Store, catalog);
        await engine.InitializeAsync();
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        var reads = 0;
        catalog.BeforeLookup = () =>
        {
            if (++reads != lookup) return;
            var stamp = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, Journal(4321).ToBytes());
            File.SetLastWriteTimeUtc(path, stamp);
        };
        var result = await engine.PrepareAsync(SessionId);
        Assert.IsNull(result.Receipt);
        Assert.IsNotNull(result.FailureReason);
        Assert.AreEqual(committed ? 1 : 0, (await new StatisticsArchiveStore(harness.Root).ReadAsync()).Count);
        Assert.IsTrue(File.Exists(path));
        catalog.BeforeLookup = null;
        Assert.IsNotNull((await engine.PrepareAsync(SessionId)).Receipt, "A later stable attempt can commit without duplicate effective facts.");
        Assert.AreEqual(4321L, await harness.Store.ReadAsync(sql => sql.ScalarLong($"SELECT SUM(input_tokens) FROM {harness.Store.Table("usage_q")}")));
    }

    private sealed class ChangingCatalog(ISessionJournalCatalog inner) : ISessionJournalCatalog
    {
        public Action? BeforeLookup { get; set; }
        public IAsyncEnumerable<SessionJournalFile> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public ValueTask<Stream?> OpenAsync(string sessionId, long offset = 0, CancellationToken cancellationToken = default) => inner.OpenAsync(sessionId, offset, cancellationToken);
        public ValueTask<SessionJournalFile?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            BeforeLookup?.Invoke();
            return inner.GetAsync(sessionId, cancellationToken);
        }
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

    private static async Task<string> QuerySnapshotAsync(StatisticsStore store)
    {
        var queries = new StatisticsQueries(store);
        var results = new List<string>();
        foreach (var frequency in new[] { StatisticsFrequency.Day, StatisticsFrequency.Month, StatisticsFrequency.Year })
        {
            var request = new StatisticsRequest { Period = "2026-01-01..2026-12-31", Frequency = frequency, Limit = 100 };
            foreach (var metric in new[] { "tokens", "runs", "active-time", "cost", "tool-calls", "sessions-at-once" })
                results.Add(StatisticsJson.Serialize(await queries.SeriesAsync(request, metric)));
            results.Add(StatisticsJson.Serialize(await queries.SummaryAsync(request)));
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
    [DataRow("pending")]
    [DataRow("malformed")]
    [DataRow("semantic")]
    [DataRow("late-payload")]
    [DataRow("unknown")]
    [DataRow("foreign-session")]
    [DataRow("open-run")]
    [DataRow("oversized")]
    public async Task UncertainSource_NeverIssuesAReceipt(string problem)
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var journal = Journal();
        var text = System.Text.Encoding.UTF8.GetString(journal.ToBytes());
        text = problem switch
        {
            "pending" => text + "{\"$type\":",
            "malformed" => text + "not-json\n",
            "semantic" => text.Replace("\"inputTokens\":1234", "\"inputTokens\":\"1234\"", StringComparison.Ordinal),
            "late-payload" => text.Replace("\"backendId\":\"codex\"", "\"backendId\":\"codex\",\"usage\":{}", StringComparison.Ordinal),
            "unknown" => text + "{\"$type\":\"future\"," + journal.Envelope(JournalBuilder.Time(5), null) + "\n",
            "foreign-session" => text.Replace(SessionId, "22222222-2222-2222-2222-222222222222", StringComparison.Ordinal),
            "open-run" => string.Join('\n', journal.Lines.SkipLast(1)) + "\n",
            "oversized" => text + new JournalBuilder(SessionId).LocalSnapshot(JournalBuilder.Time(5), 4 * 1024 * 1024 + 1).Lines.Single() + "\n",
            _ => throw new InvalidOperationException(),
        };
        var (engine, _) = await EngineAsync(harness, System.Text.Encoding.UTF8.GetBytes(text));
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        var result = await engine.PrepareAsync(SessionId);
        Assert.IsNull(result.Receipt);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsEmpty(await new StatisticsArchiveStore(harness.Root).ReadAsync());
    }

    [TestMethod]
    public async Task DisabledLimitedAndPausedHistory_FailClosedUnlessCandidateWasAlreadyComplete()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, _) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        Assert.IsNull((await engine.PrepareAsync(SessionId)).Receipt, "No history choice.");
        await engine.ChooseHistoryAsync(HistoryChoice.FromToday);
        Assert.IsNull((await engine.PrepareAsync(SessionId)).Receipt, "Limited history cannot be silently extended.");
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.PauseAsync();
        Assert.IsNull((await engine.PrepareAsync(SessionId)).Receipt, "A paused unfinished history must stay paused.");
        await engine.ResumeAsync();
        await engine.DrainAsync();
        await engine.PauseAsync();
        var ready = await engine.PrepareAsync(SessionId);
        Assert.IsNotNull(ready.Receipt, ready.FailureReason);
        await engine.DisposeAsync();
        Assert.IsNull((await engine.PrepareAsync(SessionId)).Receipt);
        Assert.IsFalse(await engine.ValidateAsync(ready.Receipt));
        Assert.IsNull(StatisticsPlugin.CreateForDesktop(new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(harness.Root))).RetirementArchive);
    }

    [TestMethod]
    public async Task CorruptArchive_PreventsReset_WithoutChangingReadableFactsOrChoice()
    {
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        var (engine, _) = await EngineAsync(harness, Journal().ToBytes());
        await using var lifetime = engine;
        await engine.ChooseHistoryAsync(HistoryChoice.All);
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
        Assert.IsFalse(await engine.ValidateAsync(result.Receipt));
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
        var prepared = await engine.PrepareAsync(SessionId);
        Assert.IsNotNull(prepared.Receipt, prepared.FailureReason);
        var before = await harness.DumpAsync("archive");
        var directory = Path.GetDirectoryName(new StatisticsArchiveStore(harness.Root).DatabasePath)!;
        Directory.Delete(directory, recursive: true);

        var result = await engine.PrepareAsync(SessionId);
        Assert.IsNull(result.Receipt);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsFalse(await engine.ValidateAsync(prepared.Receipt));
        // Preparation can record a verified reread of the surviving source, but must neither lose facts nor issue a receipt.
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
        Assert.IsNotNull((await engine.PrepareAsync(SessionId)).Receipt);
        using var oldReader = new SessionCatchUp();
        using var oldBytes = new MemoryStream(Journal(1000).ToBytes());
        var old = oldReader.CatchUp(SessionId, oldBytes, null, long.MaxValue, CancellationToken.None);
        await File.WriteAllBytesAsync(path, Journal(9000).ToBytes());
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
        var first = (await engine.PrepareAsync(SessionId)).Receipt!;
        using var oldReader = new SessionCatchUp();
        using var oldBytes = new MemoryStream(Journal(1000).ToBytes());
        var old = oldReader.CatchUp(SessionId, oldBytes, null, long.MaxValue, CancellationToken.None);
        await File.WriteAllBytesAsync(path, Journal(9000).ToBytes());
        Assert.IsNotNull((await engine.PrepareAsync(SessionId)).Receipt);
        await harness.Store.ApplyAsync(new ApplyRequest { SessionId = SessionId, Batch = old.Batch, Cursor = old.Cursor, Replace = true });
        await harness.Store.WriteAsync(sql => sql.Execute(oldMarker
            ? $"UPDATE {harness.Store.Table("archive")} SET generation=@p0"
            : $"DELETE FROM {harness.Store.Table("archive")} WHERE @p0 IS NOT NULL", first.Generation));
        await File.WriteAllBytesAsync(path, Journal(12000).ToBytes(trailingNewline: completeSource));
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
