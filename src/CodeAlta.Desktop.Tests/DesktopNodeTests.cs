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
    public void ItsPort_IsNeitherTheOneOfANodeNorTheOneOfAServer_OfTheSameComputer()
    {
        // A desktop that runs CodeAlta Server or CodeAlta Node beside it (Create my server… > On this computer) still listens.
        int[] taken = [CodeAlta.Fleet.Channels.FleetChannelProtocol.DefaultPort, CodeAlta.Server.Hosting.ServerPaths.DefaultPort];
        CollectionAssert.DoesNotContain(taken, DesktopNode.DefaultPort);
    }

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
            var outcome = await waiting.WithinAsync(TestTime.Seconds(10), token);
            Assert.AreEqual(("redeemed", "laptop"), (outcome.Status, outcome.DeviceName));

            var device = node.Devices().Single();
            Assert.AreEqual("laptop", device.Name);
            Assert.AreEqual(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start | DevicePermissions.Models, device.Permissions,
                "a pairing lets it use the model servers here, never move sessions here nor manage this computer");
            await using (var client = await FleetNodeClient.ConnectAsync(machine, laptop, token))
            {
                var info = await client.GetInfoAsync(token);
                Assert.AreEqual("desk", info.Name);
                CollectionAssert.AreEquivalent(new[] { "view", "drive", "start", "models" }, info.Permissions.ToArray());
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
            await changed.Task.WithinAsync(TestTime.Seconds(10), token);

            var port = node.Port;
            Assert.AreEqual("desk.tail1234.ts.net", node.TailnetName);
            Assert.AreEqual("Tailscale", node.NetworkName, "the network it is found on is named, for the section This computer");
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

    [TestMethod]
    public async Task OnePairingFromALinkOfADesktop_GoesBothWays_WithTheSameTrust()
    {
        var token = TestContext.CancellationToken;
        var root = NewRoot();
        try
        {
            // Two desktops of the person, each with its machines and its node on one fleet folder, as the start wires them.
            await using var desk = await Desktop.StartAsync(root, "desk", token);
            await using var laptop = await Desktop.StartAsync(root, "laptop", token);
            await desk.Node.SetVisibleAsync(true, token);
            Assert.IsTrue(desk.Node.PairBothWays, "on until the person turns it off");
            var link = desk.Node.CreatePairing();
            var waiting = desk.Node.CurrentPairing()!;
            Assert.IsTrue(desk.Fleet.TryPreview(link.Link, out var preview, out _));
            Assert.IsTrue(preview.BothWays, "the link of a desktop offers both ways");

            // The laptop does not listen yet: asking for both ways turns it on, and it trusts the desk before the desk records it.
            Assert.IsFalse(laptop.Node.Visible);
            var (offer, turnedOn) = await laptop.Node.PrepareBothWaysAsync(token);
            Assert.IsTrue(turnedOn);
            Assert.IsTrue(laptop.Node.Listening);
            var paired = await laptop.Fleet.PairAsync(link.Link, offer, token);

            Assert.IsTrue(paired.BothWays);
            var outcome = await waiting.WithinAsync(TestTime.Seconds(10), token);
            Assert.AreEqual(("redeemed", "laptop", true), (outcome.Status, outcome.DeviceName, outcome.BothWays));
            // Each trusts the key of the other, with what a pairing gives, and lists the other with its machines.
            var deskOnLaptop = laptop.Node.Devices().Single();
            Assert.AreEqual(("desk", FleetPairingCoordinatorDefaults), (deskOnLaptop.Name, deskOnLaptop.Permissions));
            Assert.AreEqual(("laptop", FleetPairingCoordinatorDefaults), (desk.Node.Devices().Single().Name, desk.Node.Devices().Single().Permissions));
            var laptopOnDesk = desk.Fleet.Hosts.Single();
            Assert.AreEqual(("laptop", laptop.Fleet.DeviceId), (laptopOnDesk.Machine.Name, laptopOnDesk.MachineId));
            CollectionAssert.AreEqual(laptop.Node.Addresses.ToArray(), laptopOnDesk.Machine.Addresses.ToArray(), "the desk reaches the laptop where it listens");
            CollectionAssert.AreEquivalent(new[] { "view", "drive", "start", "models" }, laptopOnDesk.Machine.Permissions.ToArray());
            Assert.AreEqual("desk", laptop.Fleet.Hosts.Single().Machine.Name);
            // The first connection of the desk is accepted: the laptop trusted it before.
            await WaitAsync(() => laptopOnDesk.State == RemoteMachineState.Online, token);

            // Each direction is revoked on its own side.
            Assert.IsTrue(await laptop.Node.RevokeAsync(deskOnLaptop.DeviceId));
            Assert.AreEqual(1, desk.Node.Devices().Count, "the laptop still reaches the desk");
        }
        finally
        {
            Delete(root);
        }
    }

    [TestMethod]
    public async Task ADesktopThatPairsOneWayOnly_LeavesTheOtherAsItWas()
    {
        var token = TestContext.CancellationToken;
        var root = NewRoot();
        try
        {
            await using var desk = await Desktop.StartAsync(root, "desk", token);
            await using var laptop = await Desktop.StartAsync(root, "laptop", token);
            await desk.Node.SetVisibleAsync(true, token);
            await laptop.Node.SetVisibleAsync(true, token);

            // Turned off: its links say nothing of both ways, and the laptop trusts nothing.
            await desk.Node.SetPairBothWaysAsync(false, token);
            var link = desk.Node.CreatePairing().Link;
            Assert.IsTrue(desk.Fleet.TryPreview(link, out var preview, out _));
            Assert.IsFalse(preview.BothWays);
            var (offer, turnedOn) = await laptop.Node.PrepareBothWaysAsync(token);
            Assert.IsFalse(turnedOn, "it listened already");
            var paired = await laptop.Fleet.PairAsync(link, offer, token);
            Assert.IsFalse(paired.BothWays);
            Assert.AreEqual(0, laptop.Node.Devices().Count);
            Assert.AreEqual(0, desk.Fleet.Hosts.Count);
            Assert.AreEqual(1, desk.Node.Devices().Count, "the laptop reaches the desk");

            // Turned off after the link was made: the desk declines at the pairing, and the laptop takes its trust back.
            await desk.Node.SetPairBothWaysAsync(true, token);
            link = desk.Node.CreatePairing().Link;
            await desk.Node.SetPairBothWaysAsync(false, token);
            (offer, _) = await laptop.Node.PrepareBothWaysAsync(token);
            paired = await laptop.Fleet.PairAsync(link, offer, token);
            Assert.IsFalse(paired.BothWays);
            Assert.AreEqual(0, laptop.Node.Devices().Count, "the trust given for the pairing is taken back");
            Assert.AreEqual(0, desk.Fleet.Hosts.Count);

            // A link refused leaves no trust either.
            await desk.Node.SetPairBothWaysAsync(true, token);
            link = desk.Node.CreatePairing().Link;
            desk.Node.CreatePairing();
            (offer, _) = await laptop.Node.PrepareBothWaysAsync(token);
            await Assert.ThrowsExactlyAsync<CodeAlta.Fleet.Protocol.FleetException>(() => laptop.Fleet.PairAsync(link, offer, token));
            Assert.AreEqual(0, laptop.Node.Devices().Count);
        }
        finally
        {
            Delete(root);
        }
    }

    [TestMethod]
    public async Task ThePage_TurnsListeningOnForAPairingBothWays_AndOffAgainWhenItWentOneWay()
    {
        var token = TestContext.CancellationToken;
        var root = NewRoot();
        try
        {
            await using var desk = await Desktop.StartAsync(root, "desk", token);
            await using var laptop = await Desktop.StartAsync(root, "laptop", token);
            var page = new CodeAlta.Desktop.Rpc.MachinesService(laptop.Fleet, "e") { Node = laptop.Node };
            await desk.Node.SetVisibleAsync(true, token);

            // Declined at the pairing: the laptop pairs one way, and stops listening again.
            var link = desk.Node.CreatePairing().Link;
            var preview = page.PreviewPairing(new("e", link));
            Assert.AreEqual(("ok", true, false), (preview.Status, preview.BothWays, preview.Listening));
            await desk.Node.SetPairBothWaysAsync(false, token);
            var oneWay = await page.PairAsync(new("e", link) { BothWays = true }, token);
            Assert.AreEqual(("ok", false), (oneWay.Status, oneWay.BothWays));
            Assert.IsFalse(laptop.Node.Visible, "listening was turned on for that pairing alone");
            Assert.AreEqual(0, laptop.Node.Devices().Count);

            // Both ways: it listens from now on, for the desk.
            await desk.Node.SetPairBothWaysAsync(true, token);
            var both = await page.PairAsync(new("e", desk.Node.CreatePairing().Link) { BothWays = true }, token);
            Assert.AreEqual(("ok", true), (both.Status, both.BothWays));
            Assert.IsTrue(laptop.Node.Visible);
            Assert.AreEqual("desk", laptop.Node.Devices().Single().Name);
        }
        finally
        {
            Delete(root);
        }
    }

    private const DevicePermissions FleetPairingCoordinatorDefaults = CodeAlta.Fleet.NodeApi.FleetPairingCoordinator.DefaultPermissions;

    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TestTime.Seconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The condition was not met in time.");
            await Task.Delay(50, cancellationToken);
        }
    }

    // A desktop as DesktopFleetStartup wires its machines and its node: one fleet folder, the node records who pairs back.
    private sealed class Desktop : IAsyncDisposable
    {
        private readonly CodeAlta.Orchestration.Hosting.CodeAltaHost _host;

        private Desktop(CodeAlta.Orchestration.Hosting.CodeAltaHost host, DesktopFleet fleet, DesktopNode node) => (_host, Fleet, Node) = (host, fleet, node);

        public DesktopFleet Fleet { get; }

        public DesktopNode Node { get; }

        public static async Task<Desktop> StartAsync(string root, string name, CancellationToken cancellationToken)
        {
            var host = await FleetNodeFixture.CreateHostAsync(Path.Combine(root, name), new ScriptedFleetProvider(), false, cancellationToken);
            var fleetRoot = Path.Combine(root, name + "-fleet");
            var registry = new CodeAlta.Desktop.SessionHosts.SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
            var fleet = new DesktopFleet(fleetRoot, registry.TakeMembership(), name, null,
                new RemoteSessionHostOptions { MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromSeconds(30), ListInterval = TimeSpan.FromMilliseconds(100) });
            var node = new DesktopNode(host, name, "1.0.0", fleetRoot) { ListenAddress = IPAddress.Loopback, PortOverride = 0, PairedBack = fleet.AddPairedBackAsync };
            return new Desktop(host, fleet, node);
        }

        public async ValueTask DisposeAsync()
        {
            await Fleet.DisposeAsync();
            await Node.DisposeAsync();
            await _host.DisposeAsync();
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
