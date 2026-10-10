using System.Diagnostics;
using CodeAlta.Agent.Runtime;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// Read-only harness over a real session store: reads the whole history through the engine into a database in a temporary folder,
/// and prints counts, sizes and timings, never a text of a session. Skipped unless <c>CODEALTA_STATS_SESSIONS</c> names a sessions
/// folder (for example <c>~/.alta/sessions</c>).
/// </summary>
[TestClass]
public sealed class RealProfileHistoryHarness
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ReadTheWholeHistory_EndToEnd()
    {
        var sessions = Environment.GetEnvironmentVariable("CODEALTA_STATS_SESSIONS");
        if (string.IsNullOrWhiteSpace(sessions) || !Directory.Exists(sessions))
        {
            Assert.Inconclusive("Set CODEALTA_STATS_SESSIONS to a sessions folder to run the real-profile harness.");
        }

        // The layout wants the folder that holds "sessions".
        var layout = new AgentRuntimePathLayout(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessions!)))!);
        var catalog = new FileSystemSessionJournalCatalog(layout);
        await using var harness = await StoreHarness.CreateAsync(TimeZoneInfo.Local);
        var engine = new StatisticsEngine(harness.Store, catalog, new StatisticsEngineOptions { StartDelay = TimeSpan.Zero });
        var process = Process.GetCurrentProcess();
        var before = GC.GetTotalAllocatedBytes();
        var timer = Stopwatch.StartNew();
        var status = new List<StatisticsStatus>();
        engine.StatusChanged += value => status.Add(value);

        await engine.ChooseHistoryAsync(HistoryChoice.All);
        await engine.DrainAsync();
        timer.Stop();

        process.Refresh();
        var final = engine.Status;
        TestContext.WriteLine($"state={final.State} sessions={final.SessionsDone}/{final.SessionsTotal} skipped={final.SkippedCount} bytes={final.BytesDone / 1048576.0:F0} MiB");
        TestContext.WriteLine($"history: {timer.Elapsed.TotalSeconds:F1} s end to end, {final.BytesDone / 1048576.0 / timer.Elapsed.TotalSeconds:F0} MiB/s, peak working set {process.PeakWorkingSet64 / 1048576.0:F0} MiB, allocated {(GC.GetTotalAllocatedBytes() - before) / 1048576.0:F0} MiB");
        foreach (var skipped in final.Skipped.Take(5))
        {
            TestContext.WriteLine($"skipped {skipped.SessionId}: {skipped.Reason}");
        }

        var databaseFile = Path.Combine(harness.Root, "data", "alta.sqlite3");
        var walFile = databaseFile + "-wal";
        TestContext.WriteLine($"database: {new FileInfo(databaseFile).Length / 1048576.0:F1} MiB, write-ahead log {(File.Exists(walFile) ? new FileInfo(walFile).Length : 0) / 1048576.0:F1} MiB");
        var rows = new List<string>();
        foreach (var table in await harness.Store.ReadAsync(sql => sql.Query($"SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '{harness.Store.Prefix}%' ORDER BY name", static reader => reader.GetString(0))))
        {
            var count = await harness.CountAsync(table[harness.Store.Prefix.Length..]);
            rows.Add($"{table[harness.Store.Prefix.Length..]}={count}");
        }

        TestContext.WriteLine("rows: " + string.Join(", ", rows));

        // The numbers against a second reading of the same journals, without the store.
        long requests = 0, runs = 0, activeMs = 0, calls = 0;
        using (var catchUp = new SessionCatchUp())
        {
            // The profile is live: each journal is read up to where the engine stopped, not to its end now.
            var stored = await harness.Store.ListJournalsAsync();
            foreach (var row in stored.Values)
            {
                await using var stream = (await catalog.OpenAsync(row.SessionId))!;
                var result = catchUp.CatchUp(row.SessionId, stream, null, Math.Max(1, row.Offset - 1), CancellationToken.None);
                Assert.AreEqual(row.Offset, result.Cursor.Offset, row.SessionId);
                requests += result.Batch.Usage.Values.Sum(static value => value.Requests);
                runs += result.Batch.Activity.Values.Sum(static value => value.RunsStarted);
                activeMs += result.Batch.Activity.Values.Sum(static value => value.ActiveMs);
                calls += result.Batch.Tools.Values.Sum(static value => value.Calls);
            }
        }

        var storedRequests = await harness.SumAsync("usage_year", "requests");
        var storedRuns = await harness.SumAsync("activity_year", "runs_started");
        var storedActive = await harness.SumAsync("activity_year", "active_ms");
        var storedCalls = await harness.SumAsync("tool_year", "calls");
        TestContext.WriteLine($"cross-check: requests {storedRequests}/{requests}, runs {storedRuns}/{runs}, active ms {storedActive}/{activeMs}, tool calls {storedCalls}/{calls}");
        Assert.AreEqual(requests, storedRequests);
        Assert.AreEqual(runs, storedRuns);
        Assert.AreEqual(activeMs, storedActive);
        Assert.AreEqual(calls, storedCalls);
        await harness.VerifyRollupsAsync();

        // A second start finds nothing to read: the check of the journals against the saved ones is all it does.
        var restart = Stopwatch.StartNew();
        var again = new StatisticsEngine(harness.Store, catalog, new StatisticsEngineOptions { StartDelay = TimeSpan.Zero });
        await again.DrainAsync();
        restart.Stop();
        TestContext.WriteLine($"second start: {restart.Elapsed.TotalSeconds:F2} s, {again.Status.SessionsTotal} sessions to read, state {again.Status.State}");

        var queries = engine.Queries;
        var stopwatch = Stopwatch.StartNew();
        var period = new StatisticsRequest { Period = "all", Limit = 20 };
        async Task<string> Time(string name, Func<Task> work)
        {
            stopwatch.Restart();
            await work();
            return $"{name} {stopwatch.Elapsed.TotalMilliseconds:F0} ms";
        }

        var timings = new List<string>
        {
            await Time("summary(all)", async () => await queries.SummaryAsync(period)),
            await Time("summary(30d, compare)", async () => await queries.SummaryAsync(new StatisticsRequest { Period = "30d", Comparison = StatisticsComparison.PreviousPeriod })),
            await Time("series tokens by model", async () => await queries.SeriesAsync(period with { Frequency = StatisticsFrequency.Week }, "tokens", "model")),
            await Time("series tokens by project (quarters)", async () => await queries.SeriesAsync(period with { Frequency = StatisticsFrequency.Month }, "tokens", "project")),
            await Time("top tools", async () => await queries.TopAsync(period, "tools", "calls")),
            await Time("top sessions", async () => await queries.TopAsync(period, "sessions", "time")),
            await Time("tools", async () => await queries.ToolsAsync(period)),
            await Time("models", async () => await queries.ModelsAsync(period)),
            await Time("projects", async () => await queries.ProjectsAsync(period)),
            await Time("sessions", async () => await queries.SessionsAsync(period, "recent")),
            await Time("calendar", async () => await queries.CalendarAsync(new StatisticsRequest { Period = "365d" })),
            await Time("week by hour", async () => await queries.WeekHourAsync(period)),
            await Time("records", async () => await queries.RecordsAsync(period)),
            await Time("health", async () => await queries.HealthAsync(period)),
            await Time("distribution run-duration", async () => await queries.DistributionAsync(period, "run-duration")),
        };
        TestContext.WriteLine("queries: " + string.Join("; ", timings));

        var rebuild = Stopwatch.StartNew();
        await harness.Store.RebuildRollupsAsync();
        TestContext.WriteLine($"rebuild of the roll-ups from the facts: {rebuild.Elapsed.TotalSeconds:F2} s");
        var summary = await queries.SummaryAsync(period);
        TestContext.WriteLine("summary tiles: " + string.Join(", ", summary.Tiles.Select(static tile => $"{tile.Id}={tile.Value:0.##}")) + "; costs: " + string.Join(", ", summary.Costs.Select(static tile => $"{tile.Id}={tile.Value:0.##}")));
        await engine.DisposeAsync();
        await again.DisposeAsync();
    }
}
