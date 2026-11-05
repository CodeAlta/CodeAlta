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
        var placement = new DesktopAutomationPlacement(automations, fleet.Find, desktopFleetRoot, () => fleet.DeviceId, fleet.SignAutomation) { PausesPlaced = true };
        automations.Placement = placement;
        await using (placement)
        {
            // A pairing does not let a computer give automations: the node says so, and the desktop says what to do. The
            // computer that manages the machine (the first one paired) is sent to the switch of its page, not to a terminal.
            await placement.SyncAsync(token);
            var manager = placement.Status(automations.Snapshot.Find(Id)!)!;
            Assert.AreEqual(("failed", "test-node"), (manager.State, manager.MachineName));
            StringAssert.Contains(manager.Message, "turn on Automate for This computer on its page");
            Assert.IsFalse(manager.Message!.Contains("altanode", StringComparison.Ordinal), manager.Message);
            Assert.AreEqual(0, node.Puts.Count);

            // Another computer is told what the owner runs on the machine.
            fixture.Service.TrustedDevices.SetPermissions(fleet.DeviceId, DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start);
            await placement.SyncAsync(token);
            var refused = placement.Status(automations.Snapshot.Find(Id)!)!;
            Assert.AreEqual("failed", refused.State);
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

            // Paused here, it is paused on its machine too: given disabled, then enabled again once resumed.
            automations.Paused = true;
            await placement.SyncAsync(token);
            Assert.AreEqual(3, node.Puts.Count);
            StringAssert.Contains(node.Puts[2].Definition, "enabled = false");
            automations.Paused = false;
            await placement.SyncAsync(token);
            Assert.AreEqual(4, node.Puts.Count);
            StringAssert.Contains(node.Puts[3].Definition, "enabled = true");

            // Run now: the machine runs it, and its runs are what the page lists, with the machine they ran on.
            var run = await automations.RunAsync(Id, token);
            Assert.AreEqual(("node-run-1", "node-session-1", AutomationRun.Running), (run!.Id, run.SessionId, run.Status));
            Assert.AreEqual(0, runner.Starts.Count);
            await placement.SyncAsync(token);
            Assert.AreEqual("node-run-1", placement.Runs(Id).Single().Id);
            Assert.AreEqual(new DateTimeOffset(2026, 11, 3, 23, 0, 0, TimeSpan.Zero), placement.NextDue(Id), "when its machine says it is next due");
            var rpc = new AutomationsService(automations, fixture.Host.ProjectCatalog, Epoch, placement: placement);
            var item = (await rpc.ListAsync(new(Epoch), token)).Items.Single();
            Assert.AreEqual((machine.DeviceId, "copied", "test-node"), (item.RunsOn, item.Placement!.State, item.Placement.MachineName));
            Assert.AreEqual(("node-run-1", machine.DeviceId), (item.LastRun!.Id, item.LastRun.MachineId));
            Assert.AreEqual(new DateTimeOffset(2026, 11, 3, 23, 0, 0, TimeSpan.Zero), item.NextRunAt);
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
    public async Task PlacedElsewhere_WhileItsMachineIsAway_WaitsForItsRemovalThere_KeptAcrossARestart()
    {
        var token = TestContext.CancellationToken;
        await using var handoff = await HandoffFixture.StartAsync(enabled: true, token);
        var (automations, a, b) = (handoff.Automations, handoff.A, handoff.B);
        await using (var placement = handoff.Placement())
        {
            await placement.SyncAsync(token);
            Assert.AreEqual(1, a.Node.Puts.Count);

            // The first machine is away when the automation is placed on the second one: the second waits, so that both never run it.
            handoff.Reach(a, false);
            Assert.IsNull(await automations.SaveAsync(automations.Snapshot.Find(Id)!.Definition with { RunsOn = b.DeviceId }, null, token));
            await placement.SyncAsync(token);
            Assert.AreEqual(0, b.Node.Puts.Count, "not copied while the copy it replaces may still run");
            var waiting = placement.Status(automations.Snapshot.Find(Id)!)!;
            Assert.AreEqual(("waiting", b.DeviceId), (waiting.State, waiting.MachineId));
            StringAssert.Contains(waiting.Message, "takes away its copy");
        }

        // The removal still waits after a restart: it is kept in its own record.
        await using (var restarted = handoff.Placement())
        {
            await restarted.SyncAsync(token);
            Assert.AreEqual(0, b.Node.Puts.Count);
            Assert.IsEmpty(a.Node.Removed);

            // The first machine answers: it takes its copy away, then the second gets its own.
            handoff.Reach(a, true);
            await restarted.SyncAsync(token);
            CollectionAssert.AreEqual(new[] { Id }, a.Node.Removed.ToArray());
            Assert.AreEqual(1, b.Node.Puts.Count);
            Assert.AreEqual("copied", restarted.Status(automations.Snapshot.Find(Id)!)!.State);
            await restarted.SyncAsync(token);
            Assert.AreEqual(1, a.Node.Removed.Count, "removed once");
        }
    }

    [TestMethod]
    public async Task PlacedElsewhere_ADisabledCopyNeverWaits_APersonStartsAnEnabledOne_AndTheOldCopyIsStillTakenAway()
    {
        var token = TestContext.CancellationToken;
        await using var handoff = await HandoffFixture.StartAsync(enabled: false, token);
        var (automations, a, b) = (handoff.Automations, handoff.A, handoff.B);
        await using var placement = handoff.Placement();
        await placement.SyncAsync(token);
        Assert.AreEqual(1, a.Node.Puts.Count);

        // Disabled, it runs nowhere by itself: it is copied at once.
        handoff.Reach(a, false);
        Assert.IsNull(await automations.SaveAsync(automations.Snapshot.Find(Id)!.Definition with { RunsOn = b.DeviceId }, null, token));
        await placement.SyncAsync(token);
        Assert.AreEqual(1, b.Node.Puts.Count);
        StringAssert.Contains(b.Node.Puts[0].Definition, "enabled = false");

        // Enabled, it waits for the machine it left; the person starts it anyway from the page.
        Assert.IsNull(await automations.SetEnabledAsync(Id, true, token));
        await placement.SyncAsync(token);
        Assert.AreEqual(1, b.Node.Puts.Count);
        var rpc = new AutomationsService(automations, handoff.First.Host.ProjectCatalog, Epoch, placement: placement);
        Assert.AreEqual("waiting", (await rpc.ListAsync(new(Epoch), token)).Items.Single().Placement!.State);
        Assert.AreEqual("ok", rpc.StartPlaced(new(Epoch, Id)).Status);
        await placement.SyncAsync(token);
        Assert.AreEqual(2, b.Node.Puts.Count);
        StringAssert.Contains(b.Node.Puts[1].Definition, "enabled = true");
        Assert.AreEqual("copied", placement.Status(automations.Snapshot.Find(Id)!)!.State);
        Assert.AreEqual("refused", rpc.StartPlaced(new(Epoch, Id)).Status, "nothing waits any more");

        // The copy it left is still taken away once its machine answers.
        Assert.IsEmpty(a.Node.Removed);
        handoff.Reach(a, true);
        await placement.SyncAsync(token);
        CollectionAssert.AreEqual(new[] { Id }, a.Node.Removed.ToArray());
    }

    [TestMethod]
    public async Task BackHere_WhileItsMachineIsAway_ItsTriggersWaitForItsRemovalThere_OrAPersonStartsItAnyway()
    {
        var token = TestContext.CancellationToken;
        await using var handoff = await HandoffFixture.StartAsync(enabled: true, token);
        var (automations, a) = (handoff.Automations, handoff.A);
        await using var placement = handoff.Placement();
        var rpc = new AutomationsService(automations, handoff.First.Host.ProjectCatalog, Epoch, placement: placement);
        await placement.SyncAsync(token);
        Assert.AreEqual(1, a.Node.Puts.Count);

        // Its machine is away when it comes back here: its triggers here wait, from the moment it is saved, so that both never run it.
        handoff.Reach(a, false);
        Assert.IsNull(await automations.SaveAsync(automations.Snapshot.Find(Id)!.Definition with { RunsOn = null }, null, token));
        Assert.IsNull(automations.NextDue(Id), "not armed here while its machine may still run its copy");
        await placement.SyncAsync(token);
        Assert.IsNull(automations.NextDue(Id));
        var waiting = (await rpc.ListAsync(new(Epoch), token)).Items.Single().Placement!;
        Assert.AreEqual("waiting", waiting.State);
        StringAssert.Contains(waiting.Message, "takes away its copy");

        // The machine answers: it takes its copy away, and the triggers start it here.
        handoff.Reach(a, true);
        await placement.SyncAsync(token);
        CollectionAssert.AreEqual(new[] { Id }, a.Node.Removed.ToArray());
        Assert.IsNotNull(automations.NextDue(Id));
        Assert.IsNull((await rpc.ListAsync(new(Epoch), token)).Items.Single().Placement);

        // Again, and the person starts it here anyway: its removal there is still made once the machine answers.
        Assert.IsNull(await automations.SaveAsync(automations.Snapshot.Find(Id)!.Definition with { RunsOn = a.DeviceId }, null, token));
        await placement.SyncAsync(token);
        handoff.Reach(a, false);
        Assert.IsNull(await automations.SaveAsync(automations.Snapshot.Find(Id)!.Definition with { RunsOn = null }, null, token));
        await placement.SyncAsync(token);
        Assert.IsNull(automations.NextDue(Id));
        Assert.AreEqual("ok", rpc.StartPlaced(new(Epoch, Id)).Status);
        Assert.IsNotNull(automations.NextDue(Id), "started here anyway, as the person asked");
        handoff.Reach(a, true);
        await placement.SyncAsync(token);
        Assert.AreEqual(2, a.Node.Removed.Count);
    }

    [TestMethod]
    public async Task PlacedElsewhere_WhenTheRecordOfItsRemovalCannotBeWritten_KeepsThePlacement_AndStillWaitsAfterARestart()
    {
        var token = TestContext.CancellationToken;
        await using var handoff = await HandoffFixture.StartAsync(enabled: true, token);
        var (automations, a, b) = (handoff.Automations, handoff.A, handoff.B);
        var removals = Path.Combine(handoff.FleetRoot, DesktopAutomationPlacement.RemovalsFileName);
        await using (var placement = handoff.Placement())
        {
            await placement.SyncAsync(token);
            Assert.AreEqual(1, a.Node.Puts.Count);

            // The record of the removals cannot be written: a folder stands where its staged copy goes. A file held open
            // would do on Windows only, since Linux and macOS replace a file another process reads.
            handoff.Reach(a, false);
            File.WriteAllText(removals, "[]");
            var staged = Directory.CreateDirectory(removals + ".tmp");
            try
            {
                Assert.IsNull(await automations.SaveAsync(automations.Snapshot.Find(Id)!.Definition with { RunsOn = b.DeviceId }, null, token));
                await placement.SyncAsync(token);
                Assert.AreEqual(0, b.Node.Puts.Count, "not copied while its removal is not recorded");
                var waiting = placement.Status(automations.Snapshot.Find(Id)!)!;
                Assert.AreEqual("waiting", waiting.State);
                StringAssert.Contains(waiting.Message, "could not be written");
            }
            finally
            {
                staged.Delete();
            }
        }

        // Started again once the file can be written: the removal is recorded, and the second machine still waits for the first.
        await using (var restarted = handoff.Placement())
        {
            await restarted.SyncAsync(token);
            Assert.AreEqual(0, b.Node.Puts.Count);
            Assert.AreEqual("waiting", restarted.Status(automations.Snapshot.Find(Id)!)!.State);
            handoff.Reach(a, true);
            await restarted.SyncAsync(token);
            CollectionAssert.AreEqual(new[] { Id }, a.Node.Removed.ToArray());
            Assert.AreEqual(1, b.Node.Puts.Count);
        }
    }

    [TestMethod]
    public async Task PlacedElsewhere_WhenTheRecordOfItsRemovalCannotBeRead_GivesNothingEnabled_UntilItReads()
    {
        var token = TestContext.CancellationToken;
        await using var handoff = await HandoffFixture.StartAsync(enabled: true, token);
        var (automations, a, b) = (handoff.Automations, handoff.A, handoff.B);
        var removals = Path.Combine(handoff.FleetRoot, DesktopAutomationPlacement.RemovalsFileName);
        await using (var placement = handoff.Placement())
        {
            await placement.SyncAsync(token);
            handoff.Reach(a, false);
            Assert.IsNull(await automations.SaveAsync(automations.Snapshot.Find(Id)!.Definition with { RunsOn = b.DeviceId }, null, token));
            await placement.SyncAsync(token);
            Assert.AreEqual("waiting", placement.Status(automations.Snapshot.Find(Id)!)!.State);
        }

        // The record is broken while this computer is closed: what it waited for cannot be told, so nothing enabled is given.
        var recorded = File.ReadAllText(removals);
        File.WriteAllText(removals, "{ broken");
        await using (var restarted = handoff.Placement())
        {
            await restarted.SyncAsync(token);
            Assert.AreEqual(0, b.Node.Puts.Count, "not copied while the record of the removals cannot be read");
            var waiting = restarted.Status(automations.Snapshot.Find(Id)!)!;
            Assert.AreEqual("waiting", waiting.State);
            StringAssert.Contains(waiting.Message, "cannot be read");

            // It reads again: the removal it held still comes first.
            File.WriteAllText(removals, recorded);
            await restarted.SyncAsync(token);
            Assert.AreEqual(0, b.Node.Puts.Count);
            handoff.Reach(a, true);
            await restarted.SyncAsync(token);
            CollectionAssert.AreEqual(new[] { Id }, a.Node.Removed.ToArray());
            Assert.AreEqual(1, b.Node.Puts.Count);
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

    /// <summary>Two paired machines whose automations are recorded, one automation placed on the first, and machines the test makes reachable or not.</summary>
    private sealed class HandoffFixture : IAsyncDisposable
    {
        private readonly HashSet<string> _away = [];

        private HandoffFixture(FleetNodeFixture first, FleetNodeFixture second, DesktopFleet fleet, AutomationService automations, string fleetRoot, Machine a, Machine b)
            => (First, Second, Fleet, Automations, FleetRoot, A, B) = (first, second, fleet, automations, fleetRoot, a, b);

        public FleetNodeFixture First { get; }

        public FleetNodeFixture Second { get; }

        public DesktopFleet Fleet { get; }

        public AutomationService Automations { get; }

        public string FleetRoot { get; }

        public Machine A { get; }

        public Machine B { get; }

        public static async Task<HandoffFixture> StartAsync(bool enabled, CancellationToken token)
        {
            var nodeA = new RecordingNodeAutomations();
            var nodeB = new RecordingNodeAutomations();
            var first = await FleetNodeFixture.StartAsync(token, nodeA);
            var second = await FleetNodeFixture.StartAsync(token, nodeB);
            var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
            var fleetRoot = Path.Combine(first.Root, "desktop-fleet");
            var fleet = new DesktopFleet(fleetRoot, registry.TakeMembership(), "desktop",
                static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick,
                    localDeviceId: identity.DeviceId));
            var a = new Machine((await fleet.PairAsync(first.CreatePairingLink(), token)).DeviceId, nodeA);
            var b = new Machine((await fleet.PairAsync(second.CreatePairingLink(), token)).DeviceId, nodeB);
            foreach (var (fixture, machine) in new[] { (first, a), (second, b) })
            {
                await WaitForAsync(() => fleet.Find(machine.DeviceId)!.State == RemoteMachineState.Online, "the machine to be online", token);
                fixture.Service.TrustedDevices.SetPermissions(fleet.DeviceId, DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate);
            }

            var global = Path.Combine(first.Root, "desktop-config.toml");
            File.WriteAllText(global, $$"""
                [automations.{{Id}}]
                name = "Nightly"
                enabled = {{(enabled ? "true" : "false")}}
                runs_on = "{{a.DeviceId}}"
                prompt = "Summarize the day."
                triggers = [{ type = "daily", at = ["23:00"] }]
                """);
            var automations = new AutomationService(global, _ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]),
                new AutomationStateStore(Path.Combine(first.Root, "desktop-state", "automations.json"), false), new FakeAutomationRunner(), TimeProvider.System, TimeZoneInfo.Utc);
            await automations.RefreshAsync();
            return new HandoffFixture(first, second, fleet, automations, fleetRoot, a, b);
        }

        // A machine the test made away is not reached: as a machine offline, its removal waits.
        public void Reach(Machine machine, bool reachable)
        {
            lock (_away)
            {
                if (reachable) _away.Remove(machine.DeviceId);
                else _away.Add(machine.DeviceId);
            }
        }

        public DesktopAutomationPlacement Placement()
        {
            var placement = new DesktopAutomationPlacement(Automations, id =>
            {
                lock (_away) return _away.Contains(id) ? null : Fleet.Find(id);
            }, FleetRoot, () => Fleet.DeviceId, Fleet.SignAutomation);
            Automations.Placement = placement;
            return placement;
        }

        public async ValueTask DisposeAsync()
        {
            await Automations.DisposeAsync();
            await Fleet.DisposeAsync();
            await Second.DisposeAsync();
            await First.DisposeAsync();
        }
    }

    /// <summary>A paired machine and what it was given.</summary>
    private sealed record Machine(string DeviceId, RecordingNodeAutomations Node);

    /// <summary>A clock the test sets.</summary>
    private sealed class SetClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public async Task RunsOn_IsCopiedAtOnce_WhenThisComputerTurnsOnAutomate_OnThePageOfTheMachine()
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
        await using var automations = new AutomationService(global, _ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]),
            new AutomationStateStore(Path.Combine(fixture.Root, "desktop-state", "automations.json"), false), new FakeAutomationRunner(), TimeProvider.System, TimeZoneInfo.Utc);
        await automations.RefreshAsync();
        var placement = new DesktopAutomationPlacement(automations, fleet.Find, desktopFleetRoot, () => fleet.DeviceId, fleet.SignAutomation);
        automations.Placement = placement;
        // As the application wires it: a change of a machine wakes the placement.
        fleet.Changed += placement.Poke;
        await using (placement)
        {
            placement.Start();
            await WaitForAsync(() => placement.Status(automations.Snapshot.Find(Id)!)!.State == "failed", "the machine to refuse it", token);

            // Automate turned on for this computer, on the page of the machine: the card is not left with the refusal until the
            // next look of the loop, half a minute later, or until Run now.
            await host.ManageAsync((client, cancellation) => client.SetDevicePermissionsAsync(new FleetDevicePermissionsParams(fleet.DeviceId, ["automate"], []), cancellation),
                changes: true, token);
            await WaitForAsync(() => placement.Status(automations.Snapshot.Find(Id)!)!.State == "copied", "the copy once allowed", token, TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, node.Puts.Count);
            CollectionAssert.Contains(host.Listing.Info!.Permissions.ToArray(), "automate", "The listing says what this computer may do now.");
        }
    }

    private static Task WaitForAsync(Func<bool> condition, string what, CancellationToken cancellationToken) => WaitForAsync(condition, what, cancellationToken, Patience);

    private static async Task WaitForAsync(Func<bool> condition, string what, CancellationToken cancellationToken, TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;
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

        public IReadOnlyList<FleetAutomationInfo> List(FleetAutomationCaller caller)
            => [new FleetAutomationInfo(Id, "Nightly on the mini", true, "fingerprint") { NextDue = new DateTimeOffset(2026, 11, 3, 23, 0, 0, TimeSpan.Zero) }];

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
