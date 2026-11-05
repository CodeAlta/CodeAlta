using System.Net;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A desktop that the person's other computers pair with (<see cref="DesktopNode"/>): off and silent until turned on, then a
/// machine another desktop pairs with from a link, as with <c>altanode</c>, whose permissions and revocation apply at once.
/// </summary>
[TestClass]
public sealed class DesktopNodeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ADesktopNeverMadeVisible_MakesNoKeyAndListensNowhere()
    {
        var token = TestContext.CancellationToken;
        var root = NewRoot();
        try
        {
            await using var host = await FleetNodeFixture.CreateHostAsync(root, new ScriptedFleetProvider(), false, token);
            var fleetRoot = Path.Combine(root, "desktop-fleet");
            await using var node = new DesktopNode(host, "desk", "1.0.0", fleetRoot) { ListenAddress = IPAddress.Loopback, PortOverride = 0 };

            await node.StartAsync(token);

            Assert.IsFalse(node.Visible);
            Assert.IsFalse(node.Listening);
            Assert.AreEqual(0, node.Addresses.Count);
            Assert.IsNull(node.Words, "no key is made");
            Assert.IsFalse(Directory.Exists(fleetRoot) && Directory.EnumerateFileSystemEntries(fleetRoot).Any(), "nothing is written before the person turns it on");
            Assert.AreEqual(0, node.Devices().Count);
            Assert.ThrowsExactly<InvalidOperationException>(() => node.CreatePairing());
        }
        finally
        {
            Delete(root);
        }
    }

    [TestMethod]
    public async Task AnotherDesktopPairsFromItsLink_ThenItsPermissionsAndItsRevocationApply()
    {
        var token = TestContext.CancellationToken;
        var root = NewRoot();
        try
        {
            await using var host = await FleetNodeFixture.CreateHostAsync(root, new ScriptedFleetProvider(), false, token);
            await using var node = new DesktopNode(host, "desk", "1.0.0", Path.Combine(root, "desktop-fleet")) { ListenAddress = IPAddress.Loopback, PortOverride = 0 };
            await node.SetVisibleAsync(true, token);
            Assert.IsTrue(node.Listening);
            CollectionAssert.AreEqual(new[] { $"127.0.0.1:{node.Port}" }, node.Addresses.ToArray());

            var pairing = node.CreatePairing();
            var waiting = node.CurrentPairing();
            Assert.IsNotNull(waiting);
            Assert.IsTrue(PairingPayload.TryParse(pairing.Link, DateTimeOffset.UtcNow, out var offer, out _), pairing.Link);
            Assert.AreEqual("desk", offer.DeviceName);
            Assert.AreEqual(node.Words, pairing.Words, "the other computer shows the words of this one");

            // The other desktop pairs exactly as with a node: the link names this computer, pinned to its key.
            var viewerRoot = Path.Combine(root, "laptop-fleet");
            using var laptop = DeviceIdentity.LoadOrCreate(viewerRoot);
            var machine = await FleetNodeClient.PairAsync(offer, laptop, "laptop", new PairedMachineStore(viewerRoot), token);
            Assert.AreEqual("desk", machine.Name);
            var outcome = await waiting.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.AreEqual(("redeemed", "laptop"), (outcome.Status, outcome.DeviceName));

            var device = node.Devices().Single();
            Assert.AreEqual("laptop", device.Name);
            Assert.AreEqual(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start, device.Permissions, "a pairing never lets it move sessions here, nor manage this computer");
            await using (var client = await FleetNodeClient.ConnectAsync(machine, laptop, token))
            {
                var info = await client.GetInfoAsync(token);
                Assert.AreEqual("desk", info.Name);
                CollectionAssert.AreEquivalent(new[] { "view", "drive", "start" }, info.Permissions.ToArray());
                Assert.IsNotNull(await client.ListSessionsAsync(cancellationToken: token), "it lists the sessions of this computer");
            }

            Assert.IsTrue(node.SetPermission(device.DeviceId, DevicePermissions.Move, true));
            Assert.IsTrue(node.Devices().Single().Permissions.HasFlag(DevicePermissions.Move), "the person lets it move sessions here");

            Assert.IsTrue(await node.RevokeAsync(device.DeviceId));
            Assert.AreEqual(0, node.Devices().Count);
            var refused = false;
            try
            {
                await using var again = await FleetNodeClient.ConnectAsync(machine, laptop, token);
                await again.GetInfoAsync(token);
            }
            catch (Exception exception) when (exception is not AssertFailedException)
            {
                refused = true;
            }

            Assert.IsTrue(refused, "a revoked computer is refused at once");
        }
        finally
        {
            Delete(root);
        }
    }

    [TestMethod]
    public async Task TheChoiceIsKeptForTheNextStart_AndTurningItOffStopsListening()
    {
        var token = TestContext.CancellationToken;
        var root = NewRoot();
        try
        {
            await using var host = await FleetNodeFixture.CreateHostAsync(root, new ScriptedFleetProvider(), false, token);
            var fleetRoot = Path.Combine(root, "desktop-fleet");
            await using (var first = new DesktopNode(host, "desk", "1.0.0", fleetRoot) { ListenAddress = IPAddress.Loopback, PortOverride = 0 })
            {
                await first.SetVisibleAsync(true, token);
                Assert.IsTrue(first.Listening);
            }

            await using var next = new DesktopNode(host, "desk", "1.0.0", fleetRoot) { ListenAddress = IPAddress.Loopback, PortOverride = 0 };
            Assert.IsTrue(next.Visible, "the next start finds it turned on");
            await next.StartAsync(token);
            Assert.IsTrue(next.Listening);

            await next.SetVisibleAsync(false, token);
            Assert.IsFalse(next.Listening);
            Assert.AreEqual(0, next.Addresses.Count);
            Assert.ThrowsExactly<InvalidOperationException>(() => next.CreatePairing());
            await using var last = new DesktopNode(host, "desk", "1.0.0", fleetRoot);
            Assert.IsFalse(last.Visible);
        }
        finally
        {
            Delete(root);
        }
    }

    [TestMethod]
    public async Task OnATailnet_ItsNameThereComesFirstInTheLinks()
    {
        var token = TestContext.CancellationToken;
        var root = NewRoot();
        try
        {
            await using var host = await FleetNodeFixture.CreateHostAsync(root, new ScriptedFleetProvider(), false, token);
            var tailnet = new CodeAlta.Desktop.Mcp.DesktopTailnetNode("desk.tail1234.ts.net", [IPAddress.Parse("100.64.0.7"), IPAddress.Parse("fd7a:115c:a1e0::7")]);
            await using var node = new DesktopNode(host, "desk", "1.0.0", Path.Combine(root, "desktop-fleet"))
            {
                ListenAddress = IPAddress.Loopback, PortOverride = 0, FindTailnet = _ => Task.FromResult<CodeAlta.Desktop.Mcp.DesktopTailnetNode?>(tailnet),
            };
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node.Changed += () => { if (node.TailnetName is not null) changed.TrySetResult(); };
            await node.SetVisibleAsync(true, token);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

            var port = node.Port;
            Assert.AreEqual("desk.tail1234.ts.net", node.TailnetName);
            CollectionAssert.AreEqual(new[] { $"desk.tail1234.ts.net:{port}", $"100.64.0.7:{port}" }, node.Addresses.Take(2).ToArray(),
                "the tailnet answers at home and away; its IPv6 address is left out of an IPv4 listener");
            Assert.IsTrue(PairingPayload.TryParse(node.CreatePairing().Link, DateTimeOffset.UtcNow, out var offer, out _));
            Assert.AreEqual($"desk.tail1234.ts.net:{port}", offer.Addresses[0]);
        }
        finally
        {
            Delete(root);
        }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "codealta-desktop-node-tests", Guid.NewGuid().ToString("N"));

    private static void Delete(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
