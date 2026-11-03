using System.Collections.Concurrent;
using CodeAlta.Automations;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// <b>Runs on</b> a machine this computer reaches only through a server: the desktop signs each version, seals what it
/// does for the machine and the person's controllers, and gives it to a real server, which delivers it to the machine's
/// control connection (a node connector in this process that opens it with its own key and acknowledges it); the desktop
/// shows where it stands and the runs the machine reports, and takes it away with a signed removal.
/// </summary>
[TestClass]
public sealed class DesktopServerAutomationPlacementTests
{
    private const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1f21";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RunsOn_AMachineOfTheServer_GivesItSealedThroughTheServer_ShowsItsRuns_AndTakesItAway()
    {
        var token = TestContext.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-server-automations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = Path.Combine(root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");
            var desktopFleetRoot = Path.Combine(root, "desktop-fleet");
            using var desktopKey = DeviceIdentity.LoadOrCreate(desktopFleetRoot);
            using var desktop = new FleetServerClient(address, desktopKey);
            var setup = await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
            using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root, "node-fleet"));
            using (var enrolling = new FleetServerClient(address, nodeKey))
                await enrolling.EnrollAsync((await desktop.CreateEnrollmentTokenAsync(null, "mini", token)).Token, "mini", FleetDeviceKinds.Node, token);
            await desktop.ApproveAsync(nodeKey.DeviceId, token);

            // The machine: its control connection opens what it is given with its own key, and says it took it.
            var given = new ConcurrentQueue<(FleetAutomationVersion Version, FleetAutomationContent? Content)>();
            var asked = new ConcurrentQueue<FleetAutomationRunRequest>();
            // The machine says it took it only once the test has seen it held for the machine: a fast acknowledgment
            // would otherwise turn "pending" into "copied" before it is read.
            var acknowledge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            FleetConnector? nodeConnector = null;
            await using var connector = nodeConnector = new FleetConnector(new FleetConnectorOptions
            {
                ServerAddress = address, Identity = nodeKey, OutboxPath = Path.Combine(root, "node-outbox.json"), Client = new FleetClientInfo("node", "4.0.0", "windows"),
                MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(500),
                AutomationsAssigned = async (delivery, cancellationToken) =>
                {
                    var list = await nodeConnector!.Devices.RefreshAsync(cancellationToken);
                    var acks = new List<FleetAutomationAck>();
                    foreach (var signed in delivery.Versions)
                    {
                        Assert.IsTrue(FleetAutomationVersionDocuments.TryVerify(signed, list, setup.Organization.Id, nodeKey.DeviceId, out var version, out var reason), reason);
                        FleetAutomationContent? content = null;
                        if (!version.Removed)
                            Assert.IsTrue(FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, nodeKey, list, out content, out reason), reason);
                        given.Enqueue((version, content));
                        acks.Add(new FleetAutomationAck(version.AutomationId, version.Number, true, null));
                    }

                    await acknowledge.Task.WaitAsync(cancellationToken);
                    await nodeConnector.AcknowledgeAutomationsAsync(new FleetAutomationAcks(acks), cancellationToken);
                },
                AutomationsRun = async (signed, cancellationToken) =>
                {
                    var list = await nodeConnector!.Devices.RefreshAsync(cancellationToken);
                    Assert.IsTrue(FleetAutomationRunRequestDocuments.TryVerify(signed, list, setup.Organization.Id, nodeKey.DeviceId, DateTimeOffset.UtcNow, out var request, out var reason),
                        reason);
                    asked.Enqueue(request);
                },
            });
            connector.Start();
            await WaitForAsync(() => connector.State == FleetConnectorState.Online, "the machine to connect", token);

            var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
            await using var fleet = new DesktopFleet(desktopFleetRoot, registry.TakeMembership(), "desktop");
            var global = Path.Combine(root, "desktop-config.toml");
            File.WriteAllText(global, $$"""
                [automations.{{Id}}]
                name = "Nightly on the mini"
                runs_on = "{{nodeKey.DeviceId}}"
                prompt = "Summarize the day."
                triggers = [{ type = "daily", at = ["23:00"] }]
                """);
            var runner = new FakeAutomationRunner();
            await using var automations = new AutomationService(global, _ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]),
                new AutomationStateStore(Path.Combine(root, "desktop-state", "automations.json"), false), runner, TimeProvider.System, TimeZoneInfo.Utc);
            await automations.RefreshAsync();
            var placement = new DesktopAutomationPlacement(automations, fleet.Find, desktopFleetRoot, () => fleet.DeviceId, fleet.SignAutomation)
            {
                SignRun = fleet.SignAutomationRun,
                ServerRoute = machineId => machineId == nodeKey.DeviceId
                    ? new DesktopServerAutomationRoute("link-1", setup.Organization.Id, "mini", new FleetOrganizationDevices(address, desktopKey), desktopKey,
                        new FleetServerClient(address, desktopKey))
                    : null,
                RunsReadInterval = TimeSpan.Zero,
            };
            automations.Placement = placement;
            await using (placement)
            {
                // Given to the server, which delivers it to the machine: the server reads none of it.
                await placement.SyncAsync(token);
                Assert.AreEqual(("pending", "mini"), (placement.Status(automations.Snapshot.Find(Id)!)!.State, placement.Status(automations.Snapshot.Find(Id)!)!.MachineName));
                acknowledge.SetResult();
                await WaitForAsync(() => !given.IsEmpty, "the machine to be given it", token);
                Assert.IsTrue(given.TryDequeue(out var first));
                StringAssert.Contains(first.Content!.Definition, "Nightly on the mini");
                Assert.IsFalse(first.Content.Definition.Contains("runs_on", StringComparison.Ordinal), "where it runs is this computer's");
                Assert.AreEqual((setup.Organization.Id, false, true), (first.Version.OrganizationId, first.Version.Removed, first.Version.Enabled));
                CollectionAssert.AreEqual(new[] { "daily" }, first.Version.Triggers.ToArray());
                var stored = (await desktop.ListAutomationsAsync(token)).Automations.Single();
                Assert.IsFalse(stored.Version.Payload.Contains("Summarize", StringComparison.Ordinal), "the server holds it sealed");
                Assert.AreEqual(0, runner.Starts.Count, "nothing of it runs here");
                Assert.IsNull(automations.NextDue(Id), "its schedule fires on its machine");

                await WaitForAsync(async () =>
                {
                    await placement.SyncAsync(token);
                    return placement.Status(automations.Snapshot.Find(Id)!)!.State == "copied";
                }, "the machine to take it", token);

                // The machine reports a run: the desktop lists it.
                var now = DateTimeOffset.UtcNow;
                var due = new DateTimeOffset(2026, 11, 3, 23, 0, 0, TimeSpan.Zero);
                Assert.IsTrue(await connector.ReportAutomationRunsAsync(new FleetAutomationRunFacts([
                    new FleetAutomationRunFact("node-run-1", Id, first.Version.Number, "daily", AutomationRun.Completed, now, now.AddMinutes(1)) { SessionId = "node-session-1" }])
                    { Schedules = [new FleetAutomationSchedule(Id, due)] }, token));
                await WaitForAsync(async () =>
                {
                    await placement.SyncAsync(token);
                    return placement.Runs(Id).Any(static run => run is { Id: "node-run-1", Status: AutomationRun.Completed, SessionId: "node-session-1" });
                }, "the run to be listed", token);
                Assert.AreEqual(due, placement.NextDue(Id), "when the machine says it is next due, through the server");
                // Run now: a request this computer signs goes through the server to the machine, which verifies it.
                var ran = (await automations.RunAsync(Id, token))!;
                Assert.AreEqual(AutomationRun.Running, ran.Status, ran.Message);
                await WaitForAsync(() => !asked.IsEmpty, "the machine to be asked the run", token);
                Assert.IsTrue(asked.TryDequeue(out var runRequest));
                Assert.AreEqual((Id, ran.Id, fleet.DeviceId), (runRequest.AutomationId, runRequest.RunId, runRequest.SignerDeviceId));

                // Changed, it is given again, with a newer number; unchanged, it is not.
                await placement.SyncAsync(token);
                Assert.IsTrue(given.IsEmpty);
                Assert.IsNull(await automations.SaveAsync(automations.Snapshot.Find(Id)!.Definition with { Prompt = "Summarize the week." }, null, token));
                await placement.SyncAsync(token);
                await WaitForAsync(() => !given.IsEmpty, "the new version", token);
                Assert.IsTrue(given.TryDequeue(out var second));
                StringAssert.Contains(second.Content!.Definition, "Summarize the week.");
                Assert.IsTrue(second.Version.Number > first.Version.Number);

                // Back on this computer: a signed removal takes it away from the machine.
                Assert.IsNull(await automations.SaveAsync(automations.Snapshot.Find(Id)!.Definition with { RunsOn = null }, null, token));
                await placement.SyncAsync(token);
                await WaitForAsync(() => !given.IsEmpty, "the removal", token);
                Assert.IsTrue(given.TryDequeue(out var removal));
                Assert.IsTrue(removal.Version.Removed);
                Assert.IsTrue(removal.Version.Number > second.Version.Number);
                Assert.IsNull(placement.Status(automations.Snapshot.Find(Id)!));
                Assert.IsNotNull(automations.NextDue(Id));

                // Once the machine took it away, the server is asked to forget it.
                await WaitForAsync(async () =>
                {
                    await placement.SyncAsync(token);
                    return (await desktop.ListAutomationsAsync(token)).Automations.Count == 0;
                }, "the server to forget the removal", token);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task AltaFleetPlace_OnAMachineOfTheServer_IsTakenForAMachineOfThePerson_AndRefusedForAnother()
    {
        var token = TestContext.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-server-place-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = Path.Combine(root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");
            var desktopFleetRoot = Path.Combine(root, "desktop-fleet");
            using var desktopKey = DeviceIdentity.LoadOrCreate(desktopFleetRoot);
            using var desktop = new FleetServerClient(address, desktopKey);
            var setup = await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
            using var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root, "node-fleet"));
            using (var enrolling = new FleetServerClient(address, nodeKey))
                await enrolling.EnrollAsync((await desktop.CreateEnrollmentTokenAsync(null, "mini", token)).Token, "mini", FleetDeviceKinds.Node, token);
            await desktop.ApproveAsync(nodeKey.DeviceId, token);

            var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
            await using var fleet = new DesktopFleet(desktopFleetRoot, registry.TakeMembership(), "desktop");
            var global = Path.Combine(root, "desktop-config.toml");
            File.WriteAllText(global, $$"""
                [automations.{{Id}}]
                name = "Nightly"
                enabled = false
                prompt = "Summarize the day."
                triggers = [{ type = "daily", at = ["23:00"] }]
                """);
            await using var automations = new AutomationService(global, _ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]),
                new AutomationStateStore(Path.Combine(root, "desktop-state", "automations.json"), false), new FakeAutomationRunner(), TimeProvider.System, TimeZoneInfo.Utc);
            await automations.RefreshAsync();
            DesktopServerAutomationRoute? Route(string machineId) => machineId is "elsewhere" || machineId == nodeKey.DeviceId
                ? new DesktopServerAutomationRoute("link-1", setup.Organization.Id, "mini", new FleetOrganizationDevices(address, desktopKey), desktopKey,
                    new FleetServerClient(address, desktopKey))
                : null;
            await using var placement = new DesktopAutomationPlacement(automations, fleet.Find, desktopFleetRoot, () => fleet.DeviceId, fleet.SignAutomation) { ServerRoute = Route };
            var alta = new DesktopAltaFleet(fleet, "desktop-pc", "4.0.0")
            {
                Automations = automations, Placement = placement, ServerRoute = Route,
                // "elsewhere" is listed by the server but is no node of this person's in its device list.
                ServerMachines = () => [(nodeKey.DeviceId, "mini"), ("elsewhere", "elsewhere")],
            };

            // A machine this computer is not paired with, reached through the server, signed with the person's authority.
            var placed = await alta.PlaceAutomationAsync(Id, "MINI", token);
            Assert.AreEqual("ok", placed.Status, placed.Message);
            Assert.AreEqual(nodeKey.DeviceId, automations.Snapshot.Find(Id)!.Definition.RunsOn);
            Assert.IsFalse(automations.Snapshot.Find(Id)!.Definition.Enabled, "placed, it stays disabled until a person enables it");

            var refused = await alta.PlaceAutomationAsync(Id, "elsewhere", token);
            Assert.AreEqual("not_allowed", refused.Status);
            StringAssert.Contains(refused.Message, "not one of your machines");
            Assert.AreEqual(nodeKey.DeviceId, automations.Snapshot.Find(Id)!.Definition.RunsOn, "nothing changed");
            Assert.AreEqual("machine_not_found", (await alta.PlaceAutomationAsync(Id, "nowhere", token)).Status);
            Assert.AreEqual("ok", (await alta.PlaceAutomationAsync(Id, null, token)).Status);
            Assert.IsNull(automations.Snapshot.Find(Id)!.Definition.RunsOn);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Task WaitForAsync(Func<bool> condition, string what, CancellationToken cancellationToken)
        => WaitForAsync(() => Task.FromResult(condition()), what, cancellationToken);

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(50, cancellationToken);
        }
    }
}
