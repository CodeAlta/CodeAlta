using CodeAlta.Catalog;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Node.Tests;

public sealed partial class NodeClientCommandTests
{
    [TestMethod]
    public async Task Devices_Permissions_Revoke_And_Status_GoThroughTheRunningNode()
    {
        using var root = new NodeTestRoot();
        var token = TestContext.CancellationToken;
        var viewerRoot = Path.Combine(root.Root, "viewer-alta");
        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runError = new CapturingWriter();
        var node = new NodeApplication(new CapturingWriter(), runError)
        {
            InitializeLogging = false,
            ConfigureHost = root.Isolate,
            Started = (_, stop) => started.TrySetResult(stop),
        };
        var running = node.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0", "--name", "test-node"]);
        var stop = await started.Task.WaitAsync(Patience, token);
        try
        {
            var pairOutput = new CapturingWriter();
            var pairError = new CapturingWriter();
            var pairing = Client(pairOutput, pairError).RunAsync(["pair", "--data-root", root.DataRoot, "--minutes", "1", "--manage"]);
            var link = await WaitForAsync(() => LinkPattern().Match(pairOutput.Text) is { Success: true } match ? match.Value : null, pairing, pairError, token);
            var (code, output, error) = await RunAsync(["connect", link, "--data-root", viewerRoot, "--name", "viewer"]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            Assert.AreEqual(NodeExitCodes.Success, await pairing.WaitAsync(Patience, token), pairError.Text);

            // The viewer keeps a channel open: the node shows it connected, and closes it when it is revoked.
            var viewerCatalog = new CatalogOptions { GlobalRoot = viewerRoot, StateRoot = viewerRoot };
            using var viewerIdentity = DeviceIdentity.LoadOrCreate(viewerCatalog);
            var machine = new PairedMachineStore(viewerCatalog).Find("test-node")!;
            await using var viewer = await FleetNodeClient.ConnectAsync(machine, viewerIdentity, token);
            await viewer.GetInfoAsync(token);

            (code, output, error) = await RunAsync(["devices", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "viewer");
            StringAssert.Contains(output, viewerIdentity.DeviceId);
            StringAssert.Contains(output, viewerIdentity.Fingerprint.ToDisplayString()[..9]);
            StringAssert.Contains(output, "view,drive,start");
            StringAssert.Contains(output, "connected (1)");

            (code, output, error) = await RunAsync(["status", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "Node: test-node (");
            StringAssert.Contains(output, "Listening on: 127.0.0.1:");
            StringAssert.Contains(output, "Paired devices: 1; connected now: 1.");
            StringAssert.Contains(output, "Sessions at work: 0.");

            (code, output, error) = await RunAsync(["permissions", "viewer", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "may view, drive, start, manage.", "A link made with --manage gives it.");

            (code, output, error) = await RunAsync(["permissions", viewerIdentity.DeviceId[..6], "--allow", "move", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "may view, drive, start, move, manage.");
            Assert.IsFalse(viewer.Completion.IsCompleted, "A permission given keeps the channel.");

            (code, output, error) = await RunAsync(["permissions", "viewer", "--deny", "drive,move", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "may view, start, manage.");
            StringAssert.Contains(output, "1 open channel(s) closed");
            await viewer.Completion.WaitAsync(Patience, token);
            (code, output, error) = await RunAsync(["remote", "test-node", "info", "--data-root", viewerRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "this device may view, start, manage.");

            await using var second = await FleetNodeClient.ConnectAsync(machine, viewerIdentity, token);
            await second.GetInfoAsync(token);
            (code, output, error) = await RunAsync(["revoke", "viewer", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, $"Revoked viewer ({viewerIdentity.DeviceId})");
            StringAssert.Contains(output, "1 open channel(s) closed");
            var closed = await second.Completion.WaitAsync(Patience, token);
            Assert.IsFalse(closed.ClosedLocally, "The node closed the channel of the revoked device.");
            (code, _, _) = await RunAsync(["remote", "test-node", "info", "--data-root", viewerRoot]);
            Assert.AreEqual(NodeExitCodes.Failure, code, "A revoked device is refused.");

            (code, output, _) = await RunAsync(["revoke", "viewer", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code);
            StringAssert.Contains(output, "was already revoked");
            (code, _, error) = await RunAsync(["permissions", "viewer", "--allow", "view", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Failure, code);
            StringAssert.Contains(error, "is revoked");
            (code, _, error) = await RunAsync(["revoke", "nobody", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Failure, code);
            StringAssert.Contains(error, "No paired device is named 'nobody'");
            (_, output, _) = await RunAsync(["devices", "--data-root", root.DataRoot]);
            StringAssert.Contains(output, "revoked ");
        }
        finally
        {
            stop();
        }

        Assert.AreEqual(NodeExitCodes.Success, await running.WaitAsync(Patience, token), runError.Text);

        // Without a running node, the commands read and change the profile; the next node reads it.
        var (offlineCode, offline, offlineError) = await RunAsync(["devices", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, offlineCode, offlineError);
        StringAssert.Contains(offline, "viewer");
        StringAssert.Contains(offline, "no node that listens runs on this profile");
        (offlineCode, offline, offlineError) = await RunAsync(["status", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Failure, offlineCode);
        StringAssert.Contains(offline, "Paired devices: 0 (1 revoked).");
    }

    [TestMethod]
    public async Task DeviceCommands_WithoutANode_ChangeTheProfile()
    {
        using var root = new NodeTestRoot();
        var store = new TrustedDeviceStore(new CatalogOptions { GlobalRoot = root.DataRoot, StateRoot = root.DataRoot });
        var laptop = store.Add("laptop", DeviceFingerprint.FromBytes(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), DevicePermissions.View);

        var (code, output, error) = await RunAsync(["permissions", "laptop", "--allow", "drive", "--allow", "start", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, code, error);
        StringAssert.Contains(output, "may view, drive, start.");
        (code, output, error) = await RunAsync(["revoke", laptop.DeviceId, "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, code, error);
        StringAssert.Contains(output, "the node refuses its key when it starts");

        var reopened = new TrustedDeviceStore(new CatalogOptions { GlobalRoot = root.DataRoot, StateRoot = root.DataRoot }).List(includeRevoked: true).Single();
        Assert.IsTrue(reopened.IsRevoked);
        Assert.AreEqual(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start, reopened.Permissions);
    }
}
