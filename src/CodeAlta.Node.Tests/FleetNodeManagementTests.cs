using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The devices that manage a node (<see cref="DevicePermissions.Manage"/>): the first one paired, or one a pairing link
/// for a manager adds, calls the owner's <c>control.*</c> methods from elsewhere, and only it.
/// </summary>
[TestClass]
public sealed class FleetNodeManagementTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FirstDevice_ManagesTheNode_TheNextOnesDoNot_UnlessTheirLinkSaysSo()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;

        await fixture.PairAsync(fixture.Owner, "laptop", manages: false, token);
        await fixture.PairAsync(fixture.Other, "phone", manages: false, token);
        await fixture.PairAsync(fixture.Third, "tablet", manages: true, token);

        var trusted = fixture.Fleet.Service.TrustedDevices;
        Assert.IsTrue(trusted.FindTrusted(fixture.Owner.Fingerprint)!.Permissions.HasFlag(DevicePermissions.Manage), "The first device of a node is its owner's.");
        Assert.IsFalse(trusted.FindTrusted(fixture.Other.Fingerprint)!.Permissions.HasFlag(DevicePermissions.Manage), "A later device does not manage it unasked.");
        Assert.IsTrue(trusted.FindTrusted(fixture.Third.Fingerprint)!.Permissions.HasFlag(DevicePermissions.Manage), "A link made with --manage gives it.");

        // Paired again, the first device keeps managing it.
        await fixture.PairAsync(fixture.Owner, "laptop", manages: false, token);
        Assert.IsTrue(trusted.FindTrusted(fixture.Owner.Fingerprint)!.Permissions.HasFlag(DevicePermissions.Manage));
    }

    [TestMethod]
    public async Task Manager_ChangesDevicesAndProjects_ReadsLogAndUpdates_WhileOthersAreForbidden()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var ownerMachine = await fixture.PairAsync(fixture.Owner, "laptop", manages: false, token);
        var otherMachine = await fixture.PairAsync(fixture.Other, "phone", manages: false, token);

        await using var manager = await FleetNodeClient.ConnectAsync(ownerMachine, fixture.Owner, token);
        await using var other = await FleetNodeClient.ConnectAsync(otherMachine, fixture.Other, token);

        var devices = await manager.ListDevicesAsync(token);
        CollectionAssert.AreEquivalent(new[] { "laptop", "phone" }, devices.Devices.Select(static device => device.Name).ToArray());
        var status = await manager.GetStatusAsync(token);
        Assert.AreEqual("managed-node", status.Name);

        // What each device may do changes from the manager's desktop, as altanode permissions does on the node.
        var changed = await manager.SetDevicePermissionsAsync(new FleetDevicePermissionsParams(fixture.Other.DeviceId, ["move", "automate"], []), token);
        Assert.IsTrue(changed.Changed);
        CollectionAssert.IsSubsetOf(new[] { "move", "automate" }, changed.Device.Permissions.ToArray());

        // A device that does not manage the node may not, however its channel was opened.
        var forbidden = await Assert.ThrowsExactlyAsync<FleetException>(() => other.ListDevicesAsync(token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, forbidden.Error.Code);
        var grant = await Assert.ThrowsExactlyAsync<FleetException>(() =>
            other.SetDevicePermissionsAsync(new FleetDevicePermissionsParams(fixture.Other.DeviceId, ["manage"], []), token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, grant.Error.Code, "A device never gives itself the manage permission.");
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(() => other.ReadLogAsync(10, token))).Error.Code);

        // Pairing and stopping stay the owner's on the machine.
        Assert.AreEqual(FleetErrorCodes.MethodNotFound, (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.CreatePairingOfferAsync(null, token))).Error.Code);
        Assert.AreEqual(FleetErrorCodes.MethodNotFound, (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.StopNodeAsync(token))).Error.Code);

        // Projects.
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root.Root, "work", "app")).FullName;
        var added = await manager.AddProjectAsync(new FleetProjectAddParams(folder), token);
        Assert.IsTrue(added.Changed);
        Assert.IsTrue((await manager.ListProjectsAsync(token)).Projects.Any(project => project.Id == added.Project.Id));
        var removed = await manager.RemoveProjectAsync(added.Project.Id, token);
        Assert.AreEqual(added.Project.Id, removed.Project.Id);
        Assert.IsFalse((await manager.ListProjectsAsync(token)).Projects.Any(project => project.Id == added.Project.Id && !project.Archived));

        // The log and the updates go to the management of the node, with who asked.
        var log = await manager.ReadLogAsync(12, token);
        Assert.AreEqual("12 lines", log.Text);
        var invalid = await Assert.ThrowsExactlyAsync<FleetException>(() => manager.ReadLogAsync(FleetNodeLogParams.MaxLines + 1, token));
        Assert.AreEqual(FleetErrorCodes.InvalidParams, invalid.Error.Code);
        var update = await manager.UpdateNodeAsync(new FleetNodeUpdateParams(Check: false, Now: true), token);
        Assert.AreEqual(FleetNodeUpdateResult.Staged, update.Status);
        Assert.AreEqual("manage:" + fixture.Owner.DeviceId, fixture.Management.LastRequestedBy, "A switch records the device that asked for it.");

        // Revoking a device closes its channel at once.
        var revoked = await manager.RevokeDeviceAsync(fixture.Other.DeviceId, token);
        Assert.IsTrue(revoked.Changed);
        Assert.IsNotNull(revoked.Device.RevokedAt);
        var closed = await other.Completion.WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.IsFalse(closed.ClosedLocally);
    }

    [TestMethod]
    public async Task ManagerWhoLosesManage_IsForbiddenAtTheNextCall()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var machine = await fixture.PairAsync(fixture.Owner, "laptop", manages: false, token);
        await using (var manager = await FleetNodeClient.ConnectAsync(machine, fixture.Owner, token))
        {
            await manager.SetDevicePermissionsAsync(new FleetDevicePermissionsParams(fixture.Owner.DeviceId, [], ["manage"]), token);
        }

        // The channel of a device that lost a permission closes; the next one is checked against what it may do now.
        await using var again = await FleetNodeClient.ConnectAsync(machine, fixture.Owner, token);
        var forbidden = await Assert.ThrowsExactlyAsync<FleetException>(() => again.ListDevicesAsync(token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, forbidden.Error.Code);
        Assert.IsTrue((await again.GetInfoAsync(token)).Permissions.Contains("view"), "It still views the sessions.");
    }

    [TestMethod]
    public async Task Manager_HasTheNodeJoinAServer_WhichItThenApprovesByTheDeviceTheNodeNamed()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        await using var server = await CodeAlta.Server.Hosting.CoordinatorServer.StartAsync(new CodeAlta.Server.Hosting.CoordinatorServerOptions
        {
            DataRoot = Path.Combine(fixture.Root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { },
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        using var desktop = new CodeAlta.Fleet.Server.FleetServerClient(address, fixture.Owner);
        await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "managed-node", token);
        var ownerMachine = await fixture.PairAsync(fixture.Owner, "laptop", manages: false, token);
        var otherMachine = await fixture.PairAsync(fixture.Other, "phone", manages: false, token);
        await using var manager = await FleetNodeClient.ConnectAsync(ownerMachine, fixture.Owner, token);
        await using var other = await FleetNodeClient.ConnectAsync(otherMachine, fixture.Other, token);

        var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => other.JoinServerAsync(new FleetServerJoinParams(address.ToString(), enrollment.Token), token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, refused.Error.Code, "only a device that manages the node has it join a server");

        var joined = await manager.JoinServerAsync(new FleetServerJoinParams(address.ToString(), enrollment.Token, "build-box"), token);
        Assert.AreEqual(fixture.Fleet.DeviceId, joined.DeviceId, "the node enrolls with its own key: the device to approve is the node");
        Assert.AreEqual("build-box", joined.Name);
        Assert.IsFalse(joined.AlreadyLinked);
        var link = new CodeAlta.Fleet.Server.FleetServerLinkStore(fixture.Node.Host.CatalogOptions.FleetRoot).List().Single();
        Assert.AreEqual(CodeAlta.Fleet.Server.FleetServerLinkStates.Pending, link.State);
        var pending = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending");
        Assert.AreEqual(joined.DeviceId, pending.Id);
        await desktop.ApproveAsync(pending.Id, token);
        Assert.AreEqual("approved", (await desktop.ListDevicesAsync(token)).Single(device => device.Id == joined.DeviceId).Status);

        Assert.IsTrue((await manager.JoinServerAsync(new FleetServerJoinParams(address.ToString(), "used-token"), token)).AlreadyLinked,
            "asked again, the node is linked already and enrolls nothing");
        var bad = await Assert.ThrowsExactlyAsync<FleetException>(() => manager.JoinServerAsync(new FleetServerJoinParams("ftp://nowhere", "x"), token));
        Assert.AreEqual(FleetErrorCodes.InvalidParams, bad.Error.Code);
    }

    [TestMethod]
    public async Task NodeManagement_ReadsTheEndOfTheLog_AndAnEmptyOneBeforeTheFirstRun()
    {
        using var root = new NodeTestRoot();
        var profile = root.Profile();
        var management = new NodeManagement(profile, "1.0.0", static () => null);
        var none = await management.ReadLogAsync(5, TestContext.CancellationToken);
        Assert.AreEqual("", none.Text);
        Assert.AreEqual(NodeLogging.GetLogFilePath(profile.StateRoot), none.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(none.Path)!);
        await File.WriteAllTextAsync(none.Path, "one\ntwo\nthree\n", TestContext.CancellationToken);
        var tail = await management.ReadLogAsync(2, TestContext.CancellationToken);
        Assert.AreEqual("two\nthree\n", tail.Text);
        Assert.IsFalse(tail.Truncated);

        var update = await management.UpdateAsync(new FleetNodeUpdateParams(), "local", TestContext.CancellationToken);
        Assert.AreEqual(FleetNodeUpdateResult.Unavailable, update.Status, "A node whose self-update does not run says so.");
    }
}

/// <summary>What a managed node answers for its log and its updates, in a test.</summary>
internal sealed class RecordingManagement : IFleetNodeManagement
{
    public string? LastRequestedBy { get; private set; }

    public Task<FleetNodeLog> ReadLogAsync(int lines, CancellationToken cancellationToken)
        => Task.FromResult(new FleetNodeLog($"{lines} lines", "node.log", false));

    public Task<FleetNodeUpdateResult> UpdateAsync(FleetNodeUpdateParams parameters, string requestedBy, CancellationToken cancellationToken)
    {
        LastRequestedBy = requestedBy;
        return Task.FromResult(parameters.Check
            ? new FleetNodeUpdateResult(FleetNodeUpdateResult.Available, "1.0.0", "1.1.0", "1.1.0")
            : new FleetNodeUpdateResult(FleetNodeUpdateResult.Staged, "1.0.0", "1.1.0", "1.1.0"));
    }
}

/// <summary>A node whose management records what it is asked, and three devices of other profiles.</summary>
internal sealed class ManagedNodeFixture : IAsyncDisposable
{
    private ManagedNodeFixture(NodeTestRoot root, NodeHost node, NodeFleet fleet, RecordingManagement management)
    {
        Root = root;
        Node = node;
        Fleet = fleet;
        Management = management;
        Owner = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop", "fleet"));
        Other = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "phone", "fleet"));
        Third = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet", "fleet"));
    }

    public NodeTestRoot Root { get; }

    public NodeHost Node { get; }

    public NodeFleet Fleet { get; }

    public RecordingManagement Management { get; }

    public DeviceIdentity Owner { get; }

    public DeviceIdentity Other { get; }

    public DeviceIdentity Third { get; }

    public static async Task<ManagedNodeFixture> StartAsync(CancellationToken cancellationToken)
    {
        var root = new NodeTestRoot();
        try
        {
            var node = await NodeHost.StartAsync(root.Options(), cancellationToken);
            var management = new RecordingManagement();
            var fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "managed-node", "1.0.0", null, null, null, management);
            return new ManagedNodeFixture(root, node, fleet, management);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public async Task<PairedMachine> PairAsync(DeviceIdentity device, string name, bool manages, CancellationToken cancellationToken)
    {
        var (offer, _) = Fleet.Service.Pairing.CreateOffer("managed-node", Fleet.Addresses, TimeSpan.FromMinutes(1), manages);
        return await FleetNodeClient.PairAsync(offer, device, name, new PairedMachineStore(Path.Combine(Root.Root, name, "fleet")), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Fleet.DisposeAsync();
        await Node.DisposeAsync();
        Owner.Dispose();
        Other.Dispose();
        Third.Dispose();
        Root.Dispose();
    }
}
