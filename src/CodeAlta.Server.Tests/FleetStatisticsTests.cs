using System.Collections.Concurrent;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// Fleet statistics (<c>doc/fleet/statistics.md</c>) between the connector of a machine and a real server of this process:
/// sets of rows per session published with their versions, summed by the REST API for the person's own machines; a replay,
/// a lost outbox and a restarted connector change nothing; Private and unlinked sessions publish nothing and are withdrawn.
/// </summary>
[TestClass]
public sealed class FleetStatisticsTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly long Morning = FleetStatisticsFamilies.QuarterOf(Day.AddHours(10));
    // 23:30 UTC on the 2nd is 00:30 on the 3rd in Paris.
    private static readonly long LateEvening = FleetStatisticsFamilies.QuarterOf(Day.AddHours(23.5));

    [TestMethod]
    public async Task AMachinePublishesItsStatistics_AndTheServerSumsThemByMachineModelAndDay()
    {
        await using var fleet = await StatisticsFixture.StartAsync();
        var sessions = new FakeSessions();
        var statistics = new FakeStatistics();
        statistics.Set("s1", Usage(Morning, "claude-opus-5-5", 1000, 200), Activity(Morning, 60_000, 1), Cost(Morning, 1_500_000), Tool(Morning, 3));
        statistics.Set("s2", Usage(LateEvening, "gpt-6", 500, 100));
        statistics.Set("s-private", Usage(Morning, "claude-opus-5-5", 9999, 9999));
        statistics.Set("s-home", Usage(Morning, "claude-opus-5-5", 7777, 7777));
        sessions.Set(Session("s1", "work"), Session("s2", "work"), Session("s-private", "work", isPrivate: true), Session("s-home", "home"));
        await using var connector = fleet.NodeConnector(sessions, statistics, space => space == "work");
        connector.Start();

        var totals = await WaitForTotalsAsync(fleet, static measures => measures.InputTokens == 1500);
        Assert.AreEqual((1500L, 300L, 60_000L, 1L, 3L), (totals.InputTokens, totals.OutputTokens, totals.ActiveMs, totals.Runs, totals.ToolCalls),
            "the sessions of the linked space, never the Private one nor those of another space");
        Assert.AreEqual(1_500_000L, totals.CostMicro["usd"]);

        var byModel = await fleet.Desktop.QueryStatisticsAsync(new StatisticsQueryRequest("2026-11-01", "2026-11-04", null, [StatisticsGroupBy.Machine, StatisticsGroupBy.Model]));
        Assert.AreEqual(3, byModel.Groups.Count);
        Assert.AreEqual((fleet.NodeKey.DeviceId, "claude-opus-5-5", 1000L), (byModel.Groups[0].Machine, byModel.Groups[0].Model, byModel.Groups[0].Measures.InputTokens),
            "the largest first");
        Assert.AreEqual(("gpt-6", 500L), (byModel.Groups[1].Model, byModel.Groups[1].Measures.InputTokens));
        Assert.AreEqual(("", 3L), (byModel.Groups[2].Model, byModel.Groups[2].Measures.ToolCalls), "tool calls have no model of their own");
        var machine = byModel.Machines.Single(static machine => machine.Name == "Mac mini");
        Assert.AreEqual(2L, machine.Sessions, "the console shows each machine's publication, so that a gap shows");
        Assert.IsNotNull(machine.LastPublishedAt);

        // Days are the days of the time zone asked: a quarter hour at 23:30 UTC counts on the next day in Paris.
        var utc = await fleet.Desktop.QueryStatisticsAsync(new StatisticsQueryRequest("2026-11-02", "2026-11-03", null, [StatisticsGroupBy.Day]));
        CollectionAssert.AreEqual(new[] { "2026-11-02" }, utc.Groups.Select(static group => group.Period).ToArray());
        var paris = await fleet.Desktop.QueryStatisticsAsync(new StatisticsQueryRequest("2026-11-02", "2026-11-03", "Europe/Paris", [StatisticsGroupBy.Day]));
        Assert.AreEqual((1000L, 500L), (paris.Groups.Single(static group => group.Period == "2026-11-02").Measures.InputTokens,
            paris.Groups.Single(static group => group.Period == "2026-11-03").Measures.InputTokens));
        var november = await fleet.Desktop.QueryStatisticsAsync(new StatisticsQueryRequest("2026-11-01", "2026-11-30", "Europe/Paris", [StatisticsGroupBy.Month]));
        Assert.AreEqual(("2026-11", 1500L), (november.Groups.Single().Period, november.Groups.Single().Measures.InputTokens));

        // The current value of the rows, never an increment: a session that went on replaces its set.
        statistics.Set("s1", Usage(Morning, "claude-opus-5-5", 1200, 250), Activity(Morning, 60_000, 1), Cost(Morning, 1_500_000), Tool(Morning, 3));
        totals = await WaitForTotalsAsync(fleet, static measures => measures.InputTokens == 1700);
        Assert.AreEqual(350L, totals.OutputTokens);
    }

    [TestMethod]
    public async Task AReplay_ALostOutbox_AndARestartedConnector_ChangeNothing()
    {
        await using var fleet = await StatisticsFixture.StartAsync();
        var sessions = new FakeSessions();
        var statistics = new FakeStatistics();
        statistics.Set("s1", Usage(Morning, "claude-opus-5-5", 1000, 200));
        sessions.Set(Session("s1", "work"));
        var outbox = Path.Combine(fleet.Root.Path, "outbox.json");
        var results = new ConcurrentQueue<FleetStatisticsPublishResult>();
        await using (var connector = fleet.NodeConnector(sessions, statistics, static _ => true, outbox))
        {
            connector.StatisticsPublished += (_, result) => results.Enqueue(result);
            connector.Start();
            await WaitForTotalsAsync(fleet, static measures => measures.InputTokens == 1000);
        }

        // Started again on its outbox: nothing is sent again, the server holds it.
        results.Clear();
        await using (var again = fleet.NodeConnector(sessions, statistics, static _ => true, outbox))
        {
            again.StatisticsPublished += (_, result) => results.Enqueue(result);
            again.Start();
            await WaitForAsync(() => Task.FromResult(again.State == FleetConnectorState.Online));
            statistics.Touch("s1");
            again.Nudge();
            await Task.Delay(500);
            Assert.AreEqual(0, results.Sum(static result => result.Applied), "an unchanged set is not published again");
        }

        // The batch whose answer was lost, sent again: the server keeps the highest version, the sums stay.
        var service = fleet.Server.GetService<StatisticsService>();
        var device = (await fleet.Server.GetService<ICoordinatorStore>().GetDeviceAsync(fleet.NodeKey.DeviceId))!;
        var generation = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(fleet.Root.Path, "statistics.json"))).RootElement.GetProperty("generation").GetString()!;
        var replay = await service.PublishAsync(device, new FleetStatisticsPublish(1, generation, [new FleetStatisticsSet("s1", 0, 1, null, [Usage(Morning, "claude-opus-5-5", 1000, 200)])]),
            CancellationToken.None);
        Assert.AreEqual((0, 1), (replay.Applied, replay.Unchanged));
        Assert.AreEqual(1000L, (await TotalsAsync(fleet)).InputTokens);

        // A machine that lost its outbox starts a new generation: what it published before is replaced, never added to.
        File.Delete(outbox);
        File.Delete(Path.Combine(fleet.Root.Path, "statistics.json"));
        results.Clear();
        await using (var fresh = fleet.NodeConnector(sessions, statistics, static _ => true, outbox))
        {
            fresh.StatisticsPublished += (_, result) => results.Enqueue(result);
            fresh.Start();
            await WaitForAsync(() => Task.FromResult(results.Sum(static result => result.Applied) >= 1));
        }

        Assert.AreEqual(1000L, (await TotalsAsync(fleet)).InputTokens, "the same numbers, not twice");
    }

    [TestMethod]
    public async Task ASessionMadePrivateOrWhoseSpaceIsUnlinked_HasItsStatisticsWithdrawn_AndAStaleEpochIsRefused()
    {
        await using var fleet = await StatisticsFixture.StartAsync();
        var sessions = new FakeSessions();
        var statistics = new FakeStatistics();
        statistics.Set("s-a", Usage(Morning, "claude-opus-5-5", 100, 0));
        statistics.Set("s-b", Usage(Morning, "claude-opus-5-5", 10, 0));
        var linked = System.Collections.Immutable.ImmutableHashSet.Create("work", "home");
        var withdrawn = new ConcurrentQueue<string>();
        await using var connector = fleet.NodeConnector(sessions, statistics, space => Volatile.Read(ref linked).Contains(space ?? ""));
        connector.StatisticsWithdrawn += (_, result) => { foreach (var id in result.SessionIds) withdrawn.Enqueue(id); };
        sessions.Set(Session("s-a", "work"), Session("s-b", "home"));
        connector.Start();
        await WaitForTotalsAsync(fleet, static measures => measures.InputTokens == 110);

        sessions.Set(Session("s-a", "work", isPrivate: true), Session("s-b", "home"));
        await WaitForTotalsAsync(fleet, static measures => measures.InputTokens == 10);
        Volatile.Write(ref linked, ["work"]);
        sessions.Set(Session("s-a", "work", isPrivate: true), Session("s-b", "home"));
        await WaitForTotalsAsync(fleet, static measures => measures.InputTokens == 0);
        // The server shows the withdrawal before its answer reaches the connector, which then tells it.
        await WaitForAsync(() => Task.FromResult(withdrawn.Count >= 2));
        CollectionAssert.AreEqual(new[] { "s-a", "s-b" }, withdrawn.ToArray());
        var audit = await fleet.Server.GetService<ICoordinatorStore>().ListAuditAsync(0, 500);
        Assert.AreEqual(2, audit.Count(static record => record.Entry.Action == "statistics.withdraw"), "every withdrawal is audited");

        // Made public again, published again.
        sessions.Set(Session("s-a", "work"), Session("s-b", "home"));
        await WaitForTotalsAsync(fleet, static measures => measures.InputTokens == 100);

        // A set of an older epoch than the one held is refused: the machine is the single writer of its facts, an epoch fences it.
        var service = fleet.Server.GetService<StatisticsService>();
        var device = (await fleet.Server.GetService<ICoordinatorStore>().GetDeviceAsync(fleet.NodeKey.DeviceId))!;
        var generation = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(fleet.Root.Path, "statistics.json"))).RootElement.GetProperty("generation").GetString()!;
        var moved = await service.PublishAsync(device, new FleetStatisticsPublish(100, generation, [new FleetStatisticsSet("s-a", 2, 1, null, [Usage(Morning, "m", 5, 0)])]), CancellationToken.None);
        Assert.AreEqual(1, moved.Applied, "a newer epoch replaces the set whatever its version");
        var stale = await service.PublishAsync(device, new FleetStatisticsPublish(101, generation, [new FleetStatisticsSet("s-a", 1, 9, null, [Usage(Morning, "m", 50, 0)])]), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "s-a" }, stale.Stale.ToArray());
        Assert.AreEqual(5L, (await TotalsAsync(fleet)).InputTokens);
    }

    [TestMethod]
    public async Task ASetTheServerRefuses_StopsTheStatisticsOfTheConnection_NeverTheConnection()
    {
        await using var fleet = await StatisticsFixture.StartAsync();
        var sessions = new FakeSessions();
        var statistics = new FakeStatistics();
        statistics.Set("s1", new FleetStatisticsRow("histogram", Morning, "anthropic", "m", null, new Dictionary<string, long> { ["n"] = 1 }));
        sessions.Set(Session("s1", "work"));
        await using var connector = fleet.NodeConnector(sessions, statistics, static _ => true);
        var states = new ConcurrentQueue<FleetConnectorState>();
        connector.StateChanged += value => states.Enqueue(value.State);
        connector.Start();

        // The facts of the session are published; the statistics the server refuses do not drop the connection.
        await WaitForAsync(async () => (await fleet.Desktop.ListSessionsAsync()).Any(static session => session.Id == "s1"));
        await Task.Delay(1000);
        Assert.AreEqual(FleetConnectorState.Online, connector.State);
        Assert.AreEqual(1, states.Count(static state => state == FleetConnectorState.Online), "connected once, never again");
        Assert.AreEqual(0L, (await TotalsAsync(fleet)).InputTokens);
    }

    [TestMethod]
    public async Task AQuery_IsSignedAndChecked()
    {
        await using var fleet = await StatisticsFixture.StartAsync();
        async Task RefusedAsync(StatisticsQueryRequest request)
        {
            var refused = await Assert.ThrowsExactlyAsync<FleetServerException>(() => fleet.Desktop.QueryStatisticsAsync(request));
            Assert.AreEqual("statistics.invalid", refused.Code);
        }

        await RefusedAsync(new StatisticsQueryRequest("2026-11-03", "2026-11-01"));
        await RefusedAsync(new StatisticsQueryRequest("2025-01-01", "2026-11-01"));
        await RefusedAsync(new StatisticsQueryRequest("2026-11-01", "2026-11-03", "Mars/Olympus"));
        await RefusedAsync(new StatisticsQueryRequest("2026-11-01", "2026-11-03", null, ["session"]));
        await RefusedAsync(new StatisticsQueryRequest("2026-11-01", "2026-11-03", null, [StatisticsGroupBy.Day, StatisticsGroupBy.Month]));
        await RefusedAsync(new StatisticsQueryRequest("2026-11-01", "2026-11-03", null, null, "organization"));
        var empty = await fleet.Desktop.QueryStatisticsAsync(new StatisticsQueryRequest("2026-11-01", "2026-11-03"));
        Assert.AreEqual(("mine", 0L, 0), (empty.Scope, empty.Totals.InputTokens, empty.Groups.Count));

        // Without a device signature, nothing.
        using var content = new StringContent("""{"from":"2026-11-01","to":"2026-11-03"}""", System.Text.Encoding.UTF8, "application/json");
        using var anonymous = await fleet.Server.Http.PostAsync("/api/v1/statistics/query", content);
        Assert.AreEqual(401, (int)anonymous.StatusCode);
    }

    private static async Task<StatisticsMeasuresDto> TotalsAsync(StatisticsFixture fleet)
        => (await fleet.Desktop.QueryStatisticsAsync(new StatisticsQueryRequest("2026-11-01", "2026-11-04"))).Totals;

    private static async Task<StatisticsMeasuresDto> WaitForTotalsAsync(StatisticsFixture fleet, Func<StatisticsMeasuresDto, bool> condition)
    {
        StatisticsMeasuresDto totals = null!;
        await WaitForAsync(async () => condition(totals = await TotalsAsync(fleet)));
        return totals;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Waits.Hang;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The condition did not hold in time.");
            await Task.Delay(50);
        }
    }

    private static FleetLocalSession Session(string id, string? space, bool isPrivate = false)
        => new(id, space, isPrivate, 0, FleetSessionStates.Idle, Day, Day.AddHours(1), "anthropic", "claude-opus-5-5", new FleetSessionCounts(1, 2, 300, 40));

    private static FleetStatisticsRow Usage(long quarter, string model, long input, long output)
        => new(FleetStatisticsFamilies.Usage, quarter, "anthropic", model, ["high", "default", "0"], new Dictionary<string, long> { ["requests"] = 1, ["input_tokens"] = input, ["output_tokens"] = output });

    private static FleetStatisticsRow Activity(long quarter, long activeMs, long runs)
        => new(FleetStatisticsFamilies.Activity, quarter, "anthropic", "claude-opus-5-5", ["high"], new Dictionary<string, long> { ["active_ms"] = activeMs, ["runs_started"] = runs });

    private static FleetStatisticsRow Cost(long quarter, long micro)
        => new(FleetStatisticsFamilies.Cost, quarter, "anthropic", "claude-opus-5-5", ["usd"], new Dictionary<string, long> { ["total_micro"] = micro, ["records"] = 1 });

    private static FleetStatisticsRow Tool(long quarter, long calls)
        => new(FleetStatisticsFamilies.Tools, quarter, "anthropic", null, ["1", "read_file"], new Dictionary<string, long> { ["calls"] = calls, ["duration_ms_max"] = 40 });

    /// <summary>A server set up by a desktop key, with a node key enrolled and approved.</summary>
    private sealed class StatisticsFixture : IAsyncDisposable
    {
        private StatisticsFixture(ServerFixture server, TempDirectory root, DeviceIdentity desktopKey, DeviceIdentity nodeKey, FleetServerClient desktop)
        {
            Server = server;
            Root = root;
            DesktopKey = desktopKey;
            NodeKey = nodeKey;
            Desktop = desktop;
        }

        public ServerFixture Server { get; }

        public TempDirectory Root { get; }

        public DeviceIdentity DesktopKey { get; }

        public DeviceIdentity NodeKey { get; }

        public FleetServerClient Desktop { get; }

        public static async Task<StatisticsFixture> StartAsync()
        {
            var server = await ServerFixture.StartAsync();
            var root = new TempDirectory();
            var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
            var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
            var desktop = new FleetServerClient(server.BaseAddress, desktopKey);
            await desktop.SetUpAsync(server.SetupCode, "Alex", "Desktop");
            using var node = new FleetServerClient(server.BaseAddress, nodeKey);
            await node.EnrollAsync((await desktop.CreateEnrollmentTokenAsync()).Token, "Mac mini");
            await desktop.ApproveAsync(nodeKey.DeviceId);
            return new StatisticsFixture(server, root, desktopKey, nodeKey, desktop);
        }

        public FleetConnector NodeConnector(IFleetPublicationSource source, IFleetStatisticsSource statistics, Func<string?, bool> publishes, string? outbox = null)
            => new(new FleetConnectorOptions
            {
                ServerAddress = Server.BaseAddress, Identity = NodeKey, OutboxPath = outbox ?? Path.Combine(Root.Path, "outbox.json"),
                Client = new FleetClientInfo("node", "4.0.0", "macos"), Source = source, Statistics = statistics, PublishesSpace = publishes,
                MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromMilliseconds(500), PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
                PublishDelay = TimeSpan.FromMilliseconds(20), PollInterval = TimeSpan.FromMilliseconds(200),
            });

        public async ValueTask DisposeAsync()
        {
            Desktop.Dispose();
            DesktopKey.Dispose();
            NodeKey.Dispose();
            await Server.DisposeAsync();
            Root.Dispose();
        }
    }

    /// <summary>Sessions a test sets; each change says so.</summary>
    private sealed class FakeSessions : IFleetPublicationSource
    {
        private IReadOnlyList<FleetLocalSession> _sessions = [];

        public event Action? Changed;

        public void Set(params FleetLocalSession[] sessions)
        {
            Volatile.Write(ref _sessions, sessions);
            Changed?.Invoke();
        }

        public ValueTask<FleetMachineInventory> ReadInventoryAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new FleetMachineInventory("Mac mini", "macos", "arm64", "4.0.0", ["moves"], null, ["anthropic"]));

        public ValueTask<IReadOnlyList<FleetLocalSession>> ReadSessionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Volatile.Read(ref _sessions));
    }

    /// <summary>The statistics of sessions a test sets: each set is a new revision.</summary>
    private sealed class FakeStatistics : IFleetStatisticsSource
    {
        private readonly ConcurrentDictionary<string, (string Revision, FleetStatisticsRow[] Rows)> _sessions = new(StringComparer.Ordinal);
        private int _revision;

        public event Action? Changed;

        public void Set(string sessionId, params FleetStatisticsRow[] rows)
        {
            _sessions[sessionId] = (Interlocked.Increment(ref _revision).ToString(System.Globalization.CultureInfo.InvariantCulture), rows);
            Changed?.Invoke();
        }

        // A catch-up that saved no new fact: a new revision, the same rows.
        public void Touch(string sessionId) => Set(sessionId, _sessions[sessionId].Rows);

        public ValueTask<IReadOnlyDictionary<string, string>> ReadRevisionsAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(_sessions.ToDictionary(static pair => pair.Key, static pair => pair.Value.Revision, StringComparer.Ordinal));

        public ValueTask<IReadOnlyList<FleetStatisticsRow>> ReadRowsAsync(string sessionId, long fromQuarter, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<FleetStatisticsRow>>(_sessions.TryGetValue(sessionId, out var kept) ? [.. kept.Rows.Where(row => row.Quarter >= fromQuarter)] : []);
    }
}
