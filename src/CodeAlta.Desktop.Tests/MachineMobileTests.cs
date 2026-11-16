using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Connect a phone on the page of a machine (#456): the page of the phones of a node, asked through <c>control.mobile.*</c> by the computer
/// that manages it by pairing, against a real node whose Mobile plugin is a stand-in that records what it is asked.
/// </summary>
[TestClass]
public sealed class MachineMobileTests
{
    private static readonly TimeSpan Patience = TestTime.Seconds(30);

    private static readonly RemoteSessionHostOptions Quick = new()
    {
        MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromMilliseconds(200), ListInterval = TimeSpan.FromMilliseconds(200),
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheComputerThatManagesANode_TurnsItsPhonePageOn_AsksForACode_AndCallsNothingElse()
    {
        var token = TestContext.CancellationToken;
        var mobile = new RecordingMobile();
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, null, null, false, null, null, mobile: mobile);
        await using var manager = await PairAsync(fixture, "first", manages: true, token);
        var machine = manager.Fleet.Hosts.Single().MachineId!;

        var off = await manager.Service.MobileAsync(new MachineRequest(manager.Epoch, machine), token);
        Assert.AreEqual("ok", off.Status, off.Message);
        Assert.IsTrue(off.Available);
        Assert.IsFalse(off.Enabled);
        Assert.IsFalse(off.Running);
        Assert.AreEqual("unavailable", (await manager.Service.MobileInvokeAsync(new MachineMobileInvokeRequest(manager.Epoch, machine, "mobile.status", null), token)).Status,
            "A page that is off answers no call, and the page of the machine says so.");

        var on = await manager.Service.MobileEnableAsync(new MachineMobileEnableRequest(manager.Epoch, machine, true), token);
        Assert.AreEqual("ok", on.Status, on.Message);
        Assert.IsTrue(on.Enabled && on.Running);

        var paired = await manager.Service.MobileInvokeAsync(new MachineMobileInvokeRequest(manager.Epoch, machine, "mobile.pair", null), token);
        Assert.AreEqual("ok", paired.Status, paired.Message);
        Assert.AreEqual("{\"called\":\"mobile.pair\"}", paired.Json);
        var acted = await manager.Service.MobileInvokeAsync(new MachineMobileInvokeRequest(manager.Epoch, machine, "mobile.act", "{\"action\":\"signin-github\"}"), token);
        Assert.AreEqual("ok", acted.Status, acted.Message);

        foreach (var method in new[] { "mobile.settings.get", "mobile.settings.set", "mobile.reset" })
            Assert.AreEqual("ok", (await manager.Service.MobileInvokeAsync(new MachineMobileInvokeRequest(manager.Epoch, machine, method, null), token)).Status, method);
        // An unknown call, a payload that is too long and another epoch never reach the machine.
        var calls = mobile.Calls.Count;
        foreach (var refused in new[] { "mobile.nothing", "" })
            Assert.AreEqual("invalid_request", (await manager.Service.MobileInvokeAsync(new MachineMobileInvokeRequest(manager.Epoch, machine, refused, null), token)).Status, refused);
        Assert.AreEqual("invalid_request", (await manager.Service.MobileInvokeAsync(new MachineMobileInvokeRequest(manager.Epoch, machine, "mobile.act", new string('x', FleetMobileInvokeParams.MaxPayload + 1)), token)).Status);
        Assert.AreEqual("stale_epoch", (await manager.Service.MobileInvokeAsync(new MachineMobileInvokeRequest(Guid.NewGuid().ToString("D"), machine, "mobile.status", null), token)).Status);
        Assert.AreEqual("not_found", (await manager.Service.MobileInvokeAsync(new MachineMobileInvokeRequest(manager.Epoch, "unknown", "mobile.status", null), token)).Status);
        Assert.AreEqual(calls, mobile.Calls.Count, "none of them reached the machine");
        CollectionAssert.AreEqual(new[] { "mobile.pair", "mobile.act {\"action\":\"signin-github\"}", "mobile.settings.get", "mobile.settings.set", "mobile.reset" }, mobile.Calls.ToArray());

        var turnedOff = await manager.Service.MobileEnableAsync(new MachineMobileEnableRequest(manager.Epoch, machine, false), token);
        Assert.IsFalse(turnedOff.Enabled || turnedOff.Running);
    }

    [TestMethod]
    public async Task ANodeThatLetsOtherPeopleIn_RefusesToServeThePage_AndShowsWhy()
    {
        var token = TestContext.CancellationToken;
        var mobile = new RecordingMobile();
        // A machine an organization gave to a team: a phone would see the sessions of everyone who works there.
        var shared = false;
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, null, null, false, null, null, mobile: mobile, isSharedMachine: () => shared);
        await using var manager = await PairAsync(fixture, "first", manages: true, token);
        var machine = manager.Fleet.Hosts.Single().MachineId!;
        Assert.IsNull((await manager.Service.MobileAsync(new MachineRequest(manager.Epoch, machine), token)).Refusal, "Nothing keeps a node of its owner from it.");
        shared = true;

        var state = await manager.Service.MobileAsync(new MachineRequest(manager.Epoch, machine), token);
        Assert.AreEqual("ok", state.Status, state.Message);
        Assert.AreEqual(FleetNodeService.MobileSharedMachine, state.Refusal);
        var refused = await manager.Service.MobileEnableAsync(new MachineMobileEnableRequest(manager.Epoch, machine, true), token);
        Assert.AreEqual("policy.refused", refused.Status);
        Assert.IsFalse(mobile.Enabled);
    }

    [TestMethod]
    public async Task AComputerThatDoesNotManageTheNode_IsRefused_AndANodeWithoutThePage_Says_ItServesNone()
    {
        var token = TestContext.CancellationToken;
        await using (var fixture = await FleetNodeFixture.StartAsync(token, null, null, null, false, null, null, mobile: new RecordingMobile()))
        {
            await using var viewer = await PairAsync(fixture, "second", manages: false, token);
            var machine = viewer.Fleet.Hosts.Single().MachineId!;
            Assert.AreEqual("not_manager", (await viewer.Service.MobileAsync(new MachineRequest(viewer.Epoch, machine), token)).Status);
            Assert.AreEqual("not_manager", (await viewer.Service.MobileEnableAsync(new MachineMobileEnableRequest(viewer.Epoch, machine, true), token)).Status);
            Assert.AreEqual("not_manager", (await viewer.Service.MobileInvokeAsync(new MachineMobileInvokeRequest(viewer.Epoch, machine, "mobile.pair", null), token)).Status,
                "Showing a code is the work of a computer that manages the machine.");
        }

        await using (var plain = await FleetNodeFixture.StartAsync(token))
        {
            await using var manager = await PairAsync(plain, "first", manages: true, token);
            var machine = manager.Fleet.Hosts.Single().MachineId!;
            var none = await manager.Service.MobileAsync(new MachineRequest(manager.Epoch, machine), token);
            Assert.AreEqual(CodeAlta.Fleet.NodeApi.FleetNodeErrorCodes.Unavailable, none.Status, "A node without the plugin offers nothing.");
        }
    }

    private static async Task<PairedComputer> PairAsync(FleetNodeFixture fixture, string name, bool manages, CancellationToken cancellationToken)
    {
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        var fleet = new DesktopFleet(Path.Combine(fixture.Root, name + "-fleet"), registry.TakeMembership(), name,
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick,
                localDeviceId: identity.DeviceId));
        var machine = await fleet.PairAsync(fixture.CreatePairingLink(manages), cancellationToken);
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

    // The Mobile plugin of the node: it records the calls it is given, answers each with its own name, and is off until it is turned on.
    private sealed class RecordingMobile : IFleetNodeMobile
    {
        public bool Enabled { get; private set; }

        public List<string> Calls { get; } = [];

        public Task<FleetMobileState> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(new FleetMobileState(true, Enabled, Enabled));

        public Task<FleetMobileState> EnableAsync(bool enabled, CancellationToken cancellationToken)
        {
            Enabled = enabled;
            return ReadAsync(cancellationToken);
        }

        public Task<string?> InvokeAsync(string method, string? payload, CancellationToken cancellationToken)
        {
            if (!Enabled) return Task.FromResult<string?>(null);
            Calls.Add(payload is null ? method : $"{method} {payload}");
            return Task.FromResult<string?>($"{{\"called\":\"{method}\"}}");
        }
    }
}
