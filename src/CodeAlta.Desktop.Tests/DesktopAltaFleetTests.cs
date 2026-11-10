using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;

namespace CodeAlta.Desktop.Tests;

/// <summary><c>alta fleet</c> in CodeAlta Desktop (<see cref="DesktopAltaFleet"/>) against a real node in this process.</summary>
[TestClass]
public sealed class DesktopAltaFleetTests
{
    private static readonly TimeSpan Patience = TestTime.Seconds(30);

    private static readonly RemoteSessionHostOptions Quick = new()
    {
        MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromMilliseconds(200), ListInterval = TimeSpan.FromMilliseconds(200),
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Fleet_ListsThisComputerAndItsMachine_ShowsWhatASessionThereNames_AndTheDevicesOfTheMachineItManages()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        var project = await fixture.Host.ProjectCatalog.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(fixture.Root, "app")).FullName, token);
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        await using var fleet = new DesktopFleet(Path.Combine(fixture.Root, "desktop-fleet"), registry.TakeMembership(), "desktop",
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick,
                localDeviceId: identity.DeviceId));
        var paired = await fleet.PairAsync(fixture.CreatePairingLink(), token);
        var host = fleet.Find(paired.DeviceId)!;
        var deadline = DateTime.UtcNow + Patience;
        while (host.State != RemoteMachineState.Online || host.Listing.Info is null)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The machine never came online.");
            await Task.Delay(50, token);
        }

        var alta = new DesktopAltaFleet(fleet, "desktop-pc", "4.0.0");
        var machines = alta.ListMachines();
        Assert.AreEqual(("desktop-pc", true, "ready"), (machines[0].Name, machines[0].IsThisComputer, machines[0].Readiness));
        var node = machines.Single(static machine => !machine.IsThisComputer);
        Assert.AreEqual(("test-node", "online", "ready", true), (node.Name, node.State, node.Readiness, node.Paired));
        CollectionAssert.Contains(node.Permissions.ToArray(), "start");
        Assert.HasCount(1, node.Providers, "its ready provider");

        var shown = await alta.ShowMachineAsync("TEST-NODE", token);
        Assert.AreEqual("ok", shown.Status, shown.Message);
        CollectionAssert.Contains(shown.Projects.Select(static value => value.Id).ToArray(), project.Id, "The id a session there names as its project.");
        Assert.AreEqual("scripted", shown.Providers.Single().Key);
        Assert.AreEqual("not_found", (await alta.ShowMachineAsync("nowhere", token)).Status);

        var devices = await alta.ListDevicesAsync(paired.DeviceId, token);
        Assert.AreEqual("ok", devices.Status, devices.Message);
        CollectionAssert.Contains(devices.Items.Single(static device => device.IsThisComputer).Permissions.ToArray(), "manage", "A link made to manage the node gives it.");
        Assert.AreEqual("this_computer", (await alta.ListSessionsAsync("desktop-pc", 10, token)).Status);
        Assert.AreEqual("ok", (await alta.ListSessionsAsync("test-node", 10, token)).Status);

        // Without a server there are no statistics, updates nor servers; without automations nothing is placed.
        Assert.AreEqual("no_server", (await alta.QueryStatisticsAsync(new("2026-11-01", "2026-11-03", null, [], null), token)).Status);
        Assert.AreEqual("no_server", (await alta.ReadUpdatesAsync(null, token)).Status);
        Assert.IsEmpty(alta.ListServers());
        Assert.AreEqual("unavailable", (await alta.PlaceAutomationAsync("a", "test-node", token)).Status);
        Assert.AreEqual("session_missing", (await alta.GetMoveStatusAsync("no-such-session", "test-node", token)).Status);

        // The secrets of the machine it manages, by name; this computer without its runtime keeps none here.
        var secrets = await alta.ListSecretsAsync("test-node", token);
        Assert.AreEqual(("ok", "test-node"), (secrets.Status, secrets.Machine), secrets.Message);
        Assert.AreEqual("unavailable", (await alta.ListSecretsAsync(null, token)).Status);
        Assert.AreEqual("not_found", (await alta.ListSecretsAsync("nowhere", token)).Status);
    }

    [TestMethod]
    public void Readiness_IsReadyOnlyForAnOnlineMachineThatLetsThisComputerStartASessionWithAProvider()
    {
        Assert.AreEqual("ready", DesktopAltaFleet.Readiness("online", ["start"], 1, updating: false));
        Assert.AreEqual("ready", DesktopAltaFleet.Readiness("online", ["start"], null, updating: false), "A machine that did not say its providers yet is not refused for it.");
        Assert.AreEqual("offline", DesktopAltaFleet.Readiness("offline", ["start"], 1, updating: false));
        Assert.AreEqual("unauthorized", DesktopAltaFleet.Readiness("unauthorized", ["start"], 1, updating: false));
        Assert.AreEqual("updating", DesktopAltaFleet.Readiness("online", ["start"], 1, updating: true));
        Assert.AreEqual("not_allowed", DesktopAltaFleet.Readiness("online", ["view", "drive"], 1, updating: false));
        Assert.AreEqual("no_provider", DesktopAltaFleet.Readiness("online", ["start"], 0, updating: false));
    }
}
