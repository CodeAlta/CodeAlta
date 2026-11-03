using System.Collections.Concurrent;
using CodeAlta.Automations;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// <b>Runs on</b> a paired machine: the desktop copies an automation of the user to the node through its node API (a real
/// channel to a node in this process, whose automations are recorded), runs it there by hand, shows the runs the node
/// reports, and removes it from the node when it comes back here.
/// </summary>
[TestClass]
public sealed class DesktopAutomationPlacementTests
{
    private const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1f11";
    private const string Epoch = "epoch";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly RemoteSessionHostOptions Quick = new()
    {
        MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromMilliseconds(200), ListInterval = TimeSpan.FromMilliseconds(200),
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RunsOn_CopiesTheAutomationToTheMachine_RunsItThere_ShowsItsRuns_AndRemovesItWhenItComesBack()
    {
        var token = TestContext.CancellationToken;
        var node = new RecordingNodeAutomations();
        await using var fixture = await FleetNodeFixture.StartAsync(token, node);
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        var desktopFleetRoot = Path.Combine(fixture.Root, "desktop-fleet");
        await using var fleet = new DesktopFleet(desktopFleetRoot, registry.TakeMembership(), "desktop",
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick,
                localDeviceId: identity.DeviceId));
        var machine = await fleet.PairAsync(fixture.CreatePairingLink(), token);
        var host = fleet.Find(machine.DeviceId)!;
        await WaitForAsync(() => host.State == RemoteMachineState.Online, "the machine to be online", token);

        var global = Path.Combine(fixture.Root, "desktop-config.toml");
        File.WriteAllText(global, $$"""
            [automations.{{Id}}]
            name = "Nightly on the mini"
            runs_on = "{{machine.DeviceId}}"
            prompt = "Summarize the day."
            triggers = [{ type = "daily", at = ["23:00"] }]
            """);
        var runner = new FakeAutomationRunner();
        await using var automations = new AutomationService(global, _ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]),
            new AutomationStateStore(Path.Combine(fixture.Root, "desktop-state", "automations.json"), false), runner, TimeProvider.System, TimeZoneInfo.Utc);
        await automations.RefreshAsync();
        var placement = new DesktopAutomationPlacement(automations, fleet.Find, desktopFleetRoot, () => fleet.DeviceId, fleet.SignAutomation);
        automations.Placement = placement;
        await using (placement)
        {
            // A pairing does not let a computer give automations: the node says so, and the desktop says what to do.
            await placement.SyncAsync(token);
            var refused = placement.Status(automations.Snapshot.Find(Id)!)!;
            Assert.AreEqual(("failed", "test-node"), (refused.State, refused.MachineName));
            StringAssert.Contains(refused.Message, $"altanode permissions {fleet.DeviceId} --allow automate");
            Assert.AreEqual(0, node.Puts.Count);

            fixture.Service.TrustedDevices.SetPermissions(fleet.DeviceId, DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start | DevicePermissions.Automate);
            await placement.SyncAsync(token);
            Assert.AreEqual("copied", placement.Status(automations.Snapshot.Find(Id)!)!.State);
            var put = node.Puts.Single();
            StringAssert.Contains(put.Definition, "Nightly on the mini");
            Assert.IsFalse(put.Definition.Contains("runs_on", StringComparison.Ordinal), "The machine runs it there: where it runs is this computer's.");
            Assert.IsNull(put.Repository, "A chat names no repository.");
            Assert.AreEqual(fleet.DeviceId, put.Caller);
            Assert.AreEqual(0, runner.Starts.Count, "Nothing of it runs here.");
            Assert.IsNull(automations.NextDue(Id), "Its schedule fires on its machine.");

            // Unchanged, it is not copied again, even by a placement that starts anew; changed, it is.
            await placement.SyncAsync(token);
            await using (var again = new DesktopAutomationPlacement(automations, fleet.Find, desktopFleetRoot, () => fleet.DeviceId, fleet.SignAutomation)) await again.SyncAsync(token);
            Assert.AreEqual(1, node.Puts.Count);
            var definition = automations.Snapshot.Find(Id)!.Definition;
            Assert.IsNull(await automations.SaveAsync(definition with { Prompt = "Summarize the week." }, null, token));
            await placement.SyncAsync(token);
            Assert.AreEqual(2, node.Puts.Count);
            StringAssert.Contains(node.Puts[1].Definition, "Summarize the week.");

            // Run now: the machine runs it, and its runs are what the page lists, with the machine they ran on.
            var run = await automations.RunAsync(Id, token);
            Assert.AreEqual(("node-run-1", "node-session-1", AutomationRun.Running), (run!.Id, run.SessionId, run.Status));
            Assert.AreEqual(0, runner.Starts.Count);
            await placement.SyncAsync(token);
            Assert.AreEqual("node-run-1", placement.Runs(Id).Single().Id);
            var rpc = new AutomationsService(automations, fixture.Host.ProjectCatalog, Epoch, placement: placement);
            var item = (await rpc.ListAsync(new(Epoch), token)).Items.Single();
            Assert.AreEqual((machine.DeviceId, "copied", "test-node"), (item.RunsOn, item.Placement!.State, item.Placement.MachineName));
            Assert.AreEqual(("node-run-1", machine.DeviceId), (item.LastRun!.Id, item.LastRun.MachineId));
            Assert.IsTrue(item.Running);
            Assert.AreEqual(machine.DeviceId, rpc.Runs(new(Epoch, Id, null)).Runs.Single().MachineId);
            var ranByRpc = await rpc.RunAsync(new(Epoch, Id), token);
            Assert.AreEqual(machine.DeviceId, ranByRpc.Run!.MachineId);

            // Back on this computer: the machine no longer has it, and its schedule fires here.
            Assert.IsNull(await automations.SaveAsync(automations.Snapshot.Find(Id)!.Definition with { RunsOn = null }, null, token));
            await placement.SyncAsync(token);
            CollectionAssert.AreEqual(new[] { Id }, node.Removed.ToArray());
            Assert.IsNull(placement.Status(automations.Snapshot.Find(Id)!));
            Assert.IsNotNull(automations.NextDue(Id));
        }
    }

    [TestMethod]
    public async Task VersionNumbers_NeverGoBelowTheLastSigned_EvenWhenTheClockGoesBack_OrAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-placement-numbers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "config.toml");
            File.WriteAllText(global, string.Empty);
            await using var automations = new AutomationService(global, _ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]),
                new AutomationStateStore(Path.Combine(root, "state", "automations.json"), false), new FakeAutomationRunner(), TimeProvider.System, TimeZoneInfo.Utc);
            var fleetRoot = Path.Combine(root, "fleet");
            var clock = new SetClock { Now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero) };
            DesktopAutomationPlacement Placement() => new(automations, static _ => null, fleetRoot, static () => "desktop",
                static _ => throw new InvalidOperationException("Nothing is signed here.")) { Time = clock };

            long last;
            await using (var placement = Placement())
            {
                var first = placement.NextNumber(Id);
                Assert.AreEqual(clock.Now.ToUnixTimeMilliseconds(), first, "a number starts from the time");
                Assert.AreEqual(first + 1, placement.NextNumber(Id), "the same millisecond still numbers after the last one");
                clock.Now = clock.Now.AddYears(-1);
                last = placement.NextNumber(Id);
                Assert.AreEqual(first + 2, last, "a clock set back numbers after the last one signed");
                Assert.AreEqual(clock.Now.ToUnixTimeMilliseconds(), placement.NextNumber("another"), "each automation has its own numbers");
            }

            // Started again with the clock still behind, and no record of a placement: it numbers after the last one signed.
            await using (var restarted = Placement()) Assert.AreEqual(last + 1, restarted.NextNumber(Id));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A clock the test sets.</summary>
    private sealed class SetClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(25, cancellationToken);
        }
    }

    /// <summary>The automations of the node, recorded: what was put, removed and run.</summary>
    private sealed class RecordingNodeAutomations : IFleetNodeAutomations
    {
        private int _runs;

        internal List<(string Definition, string? Repository, string Caller)> Puts { get; } = [];

        internal ConcurrentQueue<string> Removed { get; } = new();

        internal ConcurrentQueue<FleetAutomationRunInfo> RunList { get; } = new();

        public Task<FleetAutomationInfo> PutAsync(FleetAutomationCaller caller, FleetAutomationPutParams parameters, CancellationToken cancellationToken)
        {
            lock (Puts) Puts.Add((parameters.Definition, parameters.Repository, caller.DeviceId));
            return Task.FromResult(new FleetAutomationInfo(Id, "Nightly on the mini", true, "fingerprint") { AssignedBy = caller.DeviceId });
        }

        public Task<bool> RemoveAsync(FleetAutomationCaller caller, string id, CancellationToken cancellationToken)
        {
            Removed.Enqueue(id);
            return Task.FromResult(true);
        }

        public IReadOnlyList<FleetAutomationInfo> List(FleetAutomationCaller caller) => [];

        public Task<FleetAutomationRunInfo> RunAsync(FleetAutomationCaller caller, string id, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _runs);
            var run = new FleetAutomationRunInfo($"node-run-{count}", id, DateTimeOffset.UtcNow.AddSeconds(count), "manual", AutomationRun.Running)
                { Name = "Nightly on the mini", SessionId = $"node-session-{count}" };
            RunList.Enqueue(run);
            return Task.FromResult(run);
        }

        public IReadOnlyList<FleetAutomationRunInfo> Runs(FleetAutomationCaller caller, string? id, int limit)
            => [.. RunList.Where(run => id is null || run.AutomationId == id).OrderByDescending(static run => run.StartedAt).Take(limit)];
    }
}
