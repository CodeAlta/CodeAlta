using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Desktop.Tests;

/// <summary>The page of a machine (<see cref="MachinesService.ManagementAsync"/>) against a real node, for the computer that manages it and one that does not.</summary>
[TestClass]
public sealed class MachineManagementTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly RemoteSessionHostOptions Quick = new()
    {
        MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromMilliseconds(200), ListInterval = TimeSpan.FromMilliseconds(200),
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FirstComputer_ManagesTheMachineFromItsPage_TheNextOneIsToldHowToBeGivenIt()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        await using var firstComputer = await PairAsync(fixture, "first", token);
        await using var secondComputer = await PairAsync(fixture, "second", token);
        var (first, firstService, firstEpoch) = firstComputer;
        var (second, secondService, secondEpoch) = secondComputer;
        var firstMachine = first.Hosts.Single().MachineId!;
        var secondMachine = second.Hosts.Single().MachineId!;

        var managed = await firstService.ManagementAsync(new MachineRequest(firstEpoch, firstMachine), token);
        Assert.AreEqual("ok", managed.Status, managed.Message);
        Assert.AreEqual("1.0.0", managed.Version);
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, managed.Devices.Select(static device => device.Name).ToArray());
        var me = managed.Devices.Single(static device => device.IsThisComputer);
        Assert.AreEqual("first", me.Name);
        CollectionAssert.Contains(me.Permissions, "manage", "The first computer paired with a node manages it.");
        Assert.AreEqual("scripted", managed.Providers.Single().Key, "The page says which providers are ready there.");

        var other = await secondService.ManagementAsync(new MachineRequest(secondEpoch, secondMachine), token);
        Assert.AreEqual("not_manager", other.Status);
        Assert.AreEqual(second.DeviceId, other.ThisDevice, "The page names this computer, for the owner to give it the permission.");
        Assert.AreEqual("scripted", other.Providers.Single().Key, "Its providers' readiness is shown all the same.");
        Assert.AreEqual("not_manager", (await secondService.RevokeDeviceAsync(new MachineDeviceRequest(secondEpoch, secondMachine, first.DeviceId), token)).Status,
            "A computer that does not manage the machine changes nothing there.");

        // The manager gives the second computer the manage permission: its page then opens.
        var given = await firstService.SetDevicePermissionAsync(new MachineDevicePermissionRequest(firstEpoch, firstMachine, second.DeviceId, "manage", true), token);
        Assert.AreEqual("ok", given.Status, given.Message);
        Assert.IsTrue(fixture.Service.TrustedDevices.List().Single(device => device.DeviceId == second.DeviceId).Permissions.HasFlag(DevicePermissions.Manage));
        Assert.AreEqual("ok", (await secondService.ManagementAsync(new MachineRequest(secondEpoch, secondMachine), token)).Status);
        Assert.AreEqual("invalid_request", (await firstService.SetDevicePermissionAsync(new MachineDevicePermissionRequest(firstEpoch, firstMachine, second.DeviceId, "root", true),
            token)).Status, "Only the permissions of a node are named.");

        // A folder of the machine becomes one of its projects, and goes again.
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "work", "app")).FullName;
        Assert.AreEqual("ok", (await firstService.AddProjectAsync(new MachineProjectRequest(firstEpoch, firstMachine, folder), token)).Status);
        var project = (await firstService.ManagementAsync(new MachineRequest(firstEpoch, firstMachine), token)).Projects.Single(value => value.Path == folder);
        Assert.AreEqual("ok", (await firstService.RemoveProjectAsync(new MachineProjectRequest(firstEpoch, firstMachine, project.Id), token)).Status);
        Assert.IsFalse((await firstService.ManagementAsync(new MachineRequest(firstEpoch, firstMachine), token)).Projects.Any(value => value.Path == folder));

        // A node without a log nor a self-update says so.
        Assert.AreEqual(CodeAlta.Fleet.NodeApi.FleetNodeErrorCodes.Unavailable,
            (await firstService.ReadLogAsync(new MachineLogRequest(firstEpoch, firstMachine, 50), token)).Status);
        Assert.AreEqual("unavailable", (await firstService.UpdateMachineAsync(new MachineUpdateRequest(firstEpoch, firstMachine, true, false), token)).Status);

        // Revoking the second computer from the first one's page refuses it there at once.
        Assert.AreEqual("ok", (await firstService.RevokeDeviceAsync(new MachineDeviceRequest(firstEpoch, firstMachine, second.DeviceId), token)).Status);
        Assert.IsNull(fixture.Service.TrustedDevices.FindTrusted(DeviceFingerprintOf(fixture, second.DeviceId)));
    }

    private static DeviceFingerprint DeviceFingerprintOf(FleetNodeFixture fixture, string deviceId)
        => fixture.Service.TrustedDevices.List(includeRevoked: true).Single(device => device.DeviceId == deviceId).Fingerprint;

    // A computer of its own profile, paired with the node from a link, its machine online.
    private static async Task<PairedComputer> PairAsync(FleetNodeFixture fixture, string name, CancellationToken cancellationToken)
    {
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        var fleet = new DesktopFleet(Path.Combine(fixture.Root, name + "-fleet"), registry.TakeMembership(), name,
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick,
                localDeviceId: identity.DeviceId));
        var machine = await fleet.PairAsync(fixture.CreatePairingLink(), cancellationToken);
        var host = fleet.Find(machine.DeviceId)!;
        var deadline = DateTime.UtcNow + Patience;
        while (host.State != RemoteMachineState.Online)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"{name} never saw the machine online.");
            await Task.Delay(50, cancellationToken);
        }

        var epoch = Guid.NewGuid().ToString("D");
        return new PairedComputer(fleet, new MachinesService(fleet, epoch), epoch);
    }

    private sealed record PairedComputer(DesktopFleet Fleet, MachinesService Service, string Epoch) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Fleet.DisposeAsync();
    }
}
