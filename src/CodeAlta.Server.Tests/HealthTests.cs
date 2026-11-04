using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The health of the machines at the server (<c>doc/fleet/observability.md</c>, health checks): a problem a machine reports in its
/// heartbeat is told to its owner once it lasts, once, and resolved when it ends; one that comes back is another one; a machine
/// offline longer than usual, or behind the version of the server, is told by the server itself.
/// </summary>
[TestClass]
public sealed class HealthTests
{
    [TestMethod]
    public async Task AProblemThatLasts_IsToldOnce_AndResolvedWhenItEnds()
    {
        await using var fleet = await HealthFixture.StartAsync();
        var health = fleet.Server.GetService<HealthService>();
        var node = await fleet.DeviceAsync(fleet.NodeKey);
        FleetHealthProblem[] disk = [new(FleetHealthKinds.DiskLow, null, "900")];

        // A new problem is not told at once: a heartbeat may see a moment's trouble.
        await health.ReportAsync(node, disk, CancellationToken.None);
        Assert.AreEqual(0, (await fleet.NotificationsAsync()).Count);
        fleet.Clock.Advance(TimeSpan.FromMinutes(11));
        for (var heartbeat = 0; heartbeat < 5; heartbeat++)
        {
            await health.ReportAsync(node, [new(FleetHealthKinds.DiskLow, null, (800 - heartbeat).ToString(System.Globalization.CultureInfo.InvariantCulture))], CancellationToken.None);
            fleet.Clock.Advance(TimeSpan.FromSeconds(25));
        }

        var told = (await fleet.NotificationsAsync()).Single();
        Assert.AreEqual((FleetNotificationKinds.Health, FleetHealthKinds.DiskLow, node.Id, "error"), (told.Kind, told.Problem, told.MachineId, told.Severity));
        Assert.IsNull(told.ResolvedAt);

        // It ends: resolved. It comes back and lasts: another one.
        await health.ReportAsync(node, [], CancellationToken.None);
        Assert.IsNotNull((await fleet.NotificationsAsync()).Single().ResolvedAt);
        await health.ReportAsync(node, disk, CancellationToken.None);
        fleet.Clock.Advance(TimeSpan.FromMinutes(11));
        await health.ReportAsync(node, disk, CancellationToken.None);
        var all = await fleet.NotificationsAsync();
        Assert.AreEqual(2, all.Count);
        Assert.AreEqual(1, all.Count(static notification => notification.ResolvedAt is null));

        // Two problems at once, each of its own subject; the kinds the server sees are not the machine's to report.
        await health.ReportAsync(node, [.. disk, new(FleetHealthKinds.SignInFailed, "anthropic", "auth"), new(FleetHealthKinds.SignInFailed, "openai")], CancellationToken.None);
        fleet.Clock.Advance(TimeSpan.FromMinutes(11));
        await health.ReportAsync(node, [.. disk, new(FleetHealthKinds.SignInFailed, "anthropic", "auth"), new(FleetHealthKinds.SignInFailed, "openai")], CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "anthropic", "openai" }, (await fleet.NotificationsAsync())
            .Where(static n => n.Problem == FleetHealthKinds.SignInFailed).Select(static n => n.Subject).ToArray());
        var refused = await Assert.ThrowsExactlyAsync<CoordinatorException>(() => health.ReportAsync(node, [new(FleetHealthKinds.Offline)], CancellationToken.None));
        Assert.AreEqual(CoordinatorErrorCodes.PublicationInvalid, refused.Code);
    }

    [TestMethod]
    public async Task AMachineOfflineTooLong_OrBehindTheServer_IsToldByTheServer()
    {
        await using var fleet = await HealthFixture.StartAsync(running: "4.1.0");
        var health = fleet.Server.GetService<HealthService>();
        var store = fleet.Server.GetService<ICoordinatorStore>();
        var node = await fleet.DeviceAsync(fleet.NodeKey);
        await store.PublishInventoryAsync(new MachineRecord(node.Id, node.OrganizationId,
            System.Text.Json.JsonSerializer.Serialize(new FleetMachineInventory("Mac mini", "macos", AppVersion: "4.0.0"), FleetServerJsonContext.Default.FleetMachineInventory),
            1, fleet.Clock.GetUtcNow()));

        await health.SweepAsync(CancellationToken.None);
        var behind = (await fleet.NotificationsAsync()).Single();
        Assert.AreEqual((FleetHealthKinds.VersionBehind, "4.0.0", "info"), (behind.Problem, behind.Detail, behind.Severity));

        // Offline a while: nothing; longer than usual: told, once.
        fleet.Clock.Advance(TimeSpan.FromHours(1));
        await health.SweepAsync(CancellationToken.None);
        Assert.IsFalse((await fleet.NotificationsAsync()).Any(static n => n.Problem == FleetHealthKinds.Offline));
        fleet.Clock.Advance(TimeSpan.FromHours(6));
        await health.SweepAsync(CancellationToken.None);
        await health.SweepAsync(CancellationToken.None);
        var offline = (await fleet.NotificationsAsync()).Single(static n => n.Problem == FleetHealthKinds.Offline);
        Assert.AreEqual((node.Id, "7"), (offline.MachineId, offline.Detail));

        // Updated: no longer behind.
        await store.PublishInventoryAsync(new MachineRecord(node.Id, node.OrganizationId,
            System.Text.Json.JsonSerializer.Serialize(new FleetMachineInventory("Mac mini", "macos", AppVersion: "4.1.0"), FleetServerJsonContext.Default.FleetMachineInventory),
            2, fleet.Clock.GetUtcNow()));
        await health.SweepAsync(CancellationToken.None);
        Assert.IsNotNull((await fleet.NotificationsAsync()).Single(static n => n.Problem == FleetHealthKinds.VersionBehind).ResolvedAt);
    }

    private sealed class HealthFixture : IAsyncDisposable
    {
        private HealthFixture(ServerFixture server, TestClock clock, TempDirectory root, DeviceIdentity desktopKey, DeviceIdentity nodeKey)
        {
            (Server, Clock, Root, DesktopKey, NodeKey) = (server, clock, root, desktopKey, nodeKey);
        }

        public ServerFixture Server { get; }

        public TestClock Clock { get; }

        public TempDirectory Root { get; }

        public DeviceIdentity DesktopKey { get; }

        public DeviceIdentity NodeKey { get; }

        public static async Task<HealthFixture> StartAsync(string? running = null)
        {
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var server = await ServerFixture.StartAsync(clock: clock,
                updateStatus: running is null ? null : () => new FleetUpdateStatus(running, null, null, null, null, null, [], null),
                configure: options => new Hosting.CoordinatorServerOptions
                {
                    DataRoot = options.DataRoot, Urls = options.Urls, TimeProvider = options.TimeProvider, Relay = options.Relay, ConfigureLogging = options.ConfigureLogging,
                    UpdateStatus = options.UpdateStatus, EnvironmentVariable = options.EnvironmentVariable, Health = new HealthOptions { RunLoop = false },
                });
            var root = new TempDirectory();
            var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
            var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
            using var desktop = new FleetServerClient(server.BaseAddress, desktopKey);
            await desktop.SetUpAsync(server.SetupCode, "Alex", "Desktop");
            using var machine = new FleetServerClient(server.BaseAddress, nodeKey);
            await machine.EnrollAsync((await desktop.CreateEnrollmentTokenAsync()).Token, "Mac mini");
            await desktop.ApproveAsync(nodeKey.DeviceId, FleetDeviceKinds.Node);
            return new HealthFixture(server, clock, root, desktopKey, nodeKey);
        }

        public async Task<DeviceRecord> DeviceAsync(DeviceIdentity key)
            => await Server.GetService<ICoordinatorStore>().GetDeviceAsync(key.DeviceId, CancellationToken.None) ?? throw new InvalidOperationException();

        // Read in the store: the clock of the server moves on, the clock of a signed request would not.
        public async Task<IReadOnlyList<NotificationRecord>> NotificationsAsync()
        {
            var desktop = await DeviceAsync(DesktopKey);
            return await ((IBudgetStore)Server.GetService<ICoordinatorStore>()).ListNotificationsAsync(desktop.OrganizationId, desktop.OwnerPersonId!, 100, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            DesktopKey.Dispose();
            NodeKey.Dispose();
            Root.Dispose();
        }
    }
}
