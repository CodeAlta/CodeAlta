using System.Text.Json;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The machines the built-in Mobile plugin reaches (<see cref="DesktopPluginMachines"/>), against a real node in this process through the
/// whole channel stack: named by their device id, confined to the plugin, fail closed, and followed without polling.
/// </summary>
[TestClass]
public sealed class DesktopPluginMachinesTests
{
    private static readonly TimeSpan Patience = TestTime.Seconds(30);

    private static readonly RemoteSessionHostOptions Quick = new()
    {
        MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromMilliseconds(200), ListInterval = TimeSpan.FromMilliseconds(200),
    };

    public TestContext TestContext { get; set; } = null!;

    private sealed class Rig(FleetNodeFixture fixture, DesktopFleet fleet, DesktopPluginMachines machines, PairedMachine machine) : IAsyncDisposable
    {
        internal FleetNodeFixture Fixture { get; } = fixture;
        internal DesktopFleet Fleet { get; } = fleet;
        internal DesktopPluginMachines Machines { get; } = machines;
        internal PairedMachine Machine { get; } = machine;
        internal RemoteSessionHost Host => Fleet.Find(Machine.DeviceId)!;

        public async ValueTask DisposeAsync()
        {
            await Fleet.DisposeAsync();
            await Fixture.DisposeAsync();
        }
    }

    private async Task<Rig> StartAsync()
    {
        var token = TestContext.CancellationToken;
        var fixture = await FleetNodeFixture.StartWithReadsAsync(token);
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        var fleet = new DesktopFleet(Path.Combine(fixture.Root, "desktop-fleet"), registry.TakeMembership(), "desktop",
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick, localDeviceId: identity.DeviceId));
        var machine = await fleet.PairAsync(fixture.CreatePairingLink(manages: false), token);
        var machines = new DesktopPluginMachines();
        machines.Attach(fleet);
        var rig = new Rig(fixture, fleet, machines, machine);
        await WaitForAsync(() => rig.Host.State == RemoteMachineState.Online, "the machine to be online");
        return rig;
    }

    [TestMethod]
    public async Task OnlyTheBuiltInMobilePlugin_IsGivenTheMachines()
    {
        await using var rig = await StartAsync();
        Assert.AreSame(rig.Machines, rig.Machines.ForPlugin("builtin:mobile", builtIn: true));
        Assert.AreSame(NoopPluginMachineService.Instance, rig.Machines.ForPlugin("builtin:mobile", builtIn: false), "A folder plugin may name itself like the built-in.");
        Assert.AreSame(NoopPluginMachineService.Instance, rig.Machines.ForPlugin("builtin:git", builtIn: true));
        Assert.AreSame(NoopPluginMachineService.Instance, rig.Machines.ForPlugin("folder:/plugins/mobile", builtIn: false));
        Assert.IsFalse(NoopPluginMachineService.Instance.HasMachines);
        Assert.IsFalse(new DesktopPluginMachines().HasMachines, "Before the fleet runs there is none.");
        Assert.IsNull(new DesktopPluginMachines().LocalDeviceId, "Asking creates no key.");
        Assert.AreEqual(rig.Fleet.DeviceId, rig.Machines.LocalDeviceId, "The device this computer is to its machines.");
        await rig.Fleet.ForgetAsync(rig.Machine.DeviceId, TestContext.CancellationToken);
        Assert.IsNull(rig.Machines.LocalDeviceId, "A computer that reaches no machine names none.");
        Assert.AreEqual(0, (await new DesktopPluginMachines().ListAsync(TestContext.CancellationToken)).Count);
    }

    [TestMethod]
    public async Task AWatcherStartedBeforeTheFleet_FollowsItOnceAttached()
    {
        await using var rig = await StartAsync();
        var machines = new DesktopPluginMachines();
        await using var changes = machines.WatchAsync(TestContext.CancellationToken).GetAsyncEnumerator();
        var first = changes.MoveNextAsync().AsTask();
        Assert.IsFalse(first.IsCompleted, "Plugins activate before the desktop fleet exists.");
        machines.Attach(rig.Fleet);
        Assert.IsTrue(await first.WithinAsync(Patience, TestContext.CancellationToken));
        Assert.AreEqual(rig.Machine.DeviceId, changes.Current.MachineId);
        Assert.IsTrue(changes.Current.Baseline);
    }

    [TestMethod]
    public async Task AMachine_IsListedByItsDeviceId_WithItsStateItsPathItsPermissionsAndItsFeatures()
    {
        var token = TestContext.CancellationToken;
        await using var rig = await StartAsync();

        Assert.IsTrue(rig.Machines.HasMachines);
        var machine = (await rig.Machines.ListAsync(token)).Single();
        Assert.AreEqual((rig.Machine.DeviceId, "test-node", "online", "direct"), (machine.Id, machine.Name, machine.State, machine.Path));
        Assert.IsNotNull(machine.LastSeen);
        CollectionAssert.IsSubsetOf(new[] { "view", "drive", "start" }, machine.Permissions.ToArray());
        CollectionAssert.Contains(machine.Features.ToArray(), FleetSessionReads.Feature, "The features it announces.");
        Assert.AreEqual(0, machine.Waiting);

        Assert.IsNull(await rig.Machines.GetAsync("device-that-is-not-paired", token), "An unknown machine has no services.");
        var services = await rig.Machines.GetAsync(rig.Machine.DeviceId, token);
        Assert.AreEqual(machine.Id, services!.Machine.Id);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await rig.Machines.GetAsync(null!, token));
    }

    [TestMethod]
    public async Task ASessionBeyondTheCachedList_IsReadByTheTypedReadsOfTheMachine()
    {
        var token = TestContext.CancellationToken;
        await using var rig = await StartAsync();
        var services = (await rig.Machines.GetAsync(rig.Machine.DeviceId, token))!;

        var created = await services.Alta.InvokeAsync(["session", "create", "--global", "--space", "default", "--provider", "scripted", "--title", "From the phone"], cancellationToken: token);
        Assert.AreEqual(0, created.ExitCode, created.Error);
        var sessionId = Records(created).Single(static record => Text(record, "type") == "alta.session.created").GetProperty("sessionId").GetString()!;

        // The list is asked of the machine with its own limit, not read from the 200 the host caches.
        var listed = await services.Alta.InvokeAsync(["session", "list", "--limit", "1000"], cancellationToken: token);
        Assert.AreEqual(0, listed.ExitCode, listed.Error);
        Assert.IsTrue(Records(listed).Any(record => Text(record, "sessionId") == sessionId));
        var status = await services.Alta.InvokeAsync(["session", "status", sessionId], cancellationToken: token);
        Assert.AreEqual(0, status.ExitCode, status.Error);

        // What is not a typed call of the node API is not run there.
        var arbitrary = await services.Alta.InvokeAsync(["session", "delete", sessionId], cancellationToken: token);
        Assert.AreEqual(1, arbitrary.ExitCode);
        StringAssert.Contains(arbitrary.TranscriptJsonl, FleetMachineAlta.Unsupported);
    }

    [TestMethod]
    public async Task ADeviceThatMayOnlyView_ReadsAndEveryWriteIsRefusedBeforeTheMachineIsAsked()
    {
        var token = TestContext.CancellationToken;
        await using var rig = await StartAsync();
        var services = (await rig.Machines.GetAsync(rig.Machine.DeviceId, token))!;
        var created = await services.Alta.InvokeAsync(["session", "create", "--global", "--space", "default", "--provider", "scripted"], cancellationToken: token);
        var sessionId = Records(created).Single(static record => Text(record, "type") == "alta.session.created").GetProperty("sessionId").GetString()!;

        rig.Fixture.Service.TrustedDevices.SetPermissions(rig.Fleet.DeviceId, DevicePermissions.View);
        await rig.Host.ReadInfoAsync(token);
        // The host takes what the machine says at its next read of the list.
        await WaitForAsync(() => rig.Machines.ListAsync(token).AsTask().Result.Single().Permissions.SequenceEqual(["view"]), "the permissions the machine now gives");

        Assert.AreEqual(0, (await services.Alta.InvokeAsync(["session", "status", sessionId], cancellationToken: token)).ExitCode, "A read is allowed.");
        string[][] writes = [["session", "send", sessionId, "--stdin"], ["session", "queue", sessionId, "--stdin"], ["session", "steer", sessionId, "--stdin"],
            ["session", "abort", sessionId], ["session", "archive", sessionId], ["session", "create", "--global"], ["job", "cancel", "job-1"]];
        foreach (var write in writes)
        {
            var refused = await services.Alta.InvokeAsync(write, "text", cancellationToken: token);
            Assert.AreEqual(1, refused.ExitCode, string.Join(' ', write));
            StringAssert.Contains(refused.TranscriptJsonl, "machine.forbidden", string.Join(' ', write));
        }

        var outcomes = new[]
        {
            (await services.Asks.AnswerAsync(new PluginAskReference(Guid.NewGuid(), sessionId, "ask", "0"), [], null, token)).Status,
            (await services.Asks.CancelAsync(new PluginAskReference(Guid.NewGuid(), sessionId, "ask", "0"), token)).Status,
            (await services.Permissions.ResolveAsync(sessionId, Guid.NewGuid().ToString("D"), PluginPermissionDecision.AllowOnce, token)).Status,
        };
        CollectionAssert.AreEqual(new[] { "rejected", "rejected", "rejected" }, outcomes, "No answer is granted to a device that may only view.");
        Assert.AreEqual(0, (await services.Asks.ListAskingAsync(token)).Count);
        Assert.AreEqual(0, (await services.Permissions.ListWaitingAsync(token)).Count);
    }

    [TestMethod]
    public async Task AForgottenMachine_FailsClosed_AndItsStreamsEnd()
    {
        var token = TestContext.CancellationToken;
        await using var rig = await StartAsync();
        var services = (await rig.Machines.GetAsync(rig.Machine.DeviceId, token))!;
        var created = await services.Alta.InvokeAsync(["session", "create", "--global", "--space", "default", "--provider", "scripted"], cancellationToken: token);
        var sessionId = Records(created).Single(static record => Text(record, "type") == "alta.session.created").GetProperty("sessionId").GetString()!;

        var display = rig.Machines.WatchSessionAsync(rig.Machine.DeviceId, sessionId, token).GetAsyncEnumerator(token);
        var ended = Task.Run(async () => { try { while (await display.MoveNextAsync()) { } } finally { await display.DisposeAsync(); } }, token);
        var changes = rig.Machines.WatchAsync(token).GetAsyncEnumerator(token);
        Assert.IsTrue(await changes.MoveNextAsync(), "The baseline of the machine comes first.");

        Assert.IsTrue(await rig.Fleet.ForgetAsync(rig.Machine.DeviceId, token));
        await ended.WithinAsync(Patience, token);
        Assert.IsNull(await rig.Machines.GetAsync(rig.Machine.DeviceId, token));
        Assert.AreEqual(0, (await rig.Machines.ListAsync(token)).Count);
        Assert.IsFalse(rig.Machines.HasMachines);
        var result = await services.Alta.InvokeAsync(["session", "status", sessionId], cancellationToken: token);
        Assert.AreEqual(1, result.ExitCode, "The services of a machine that is gone read nothing.");
        Assert.AreEqual(0, (await services.Asks.ListAskingAsync(token)).Count);

        var told = new List<PluginMachineChange>();
        while (await changes.MoveNextAsync().AsTask().WithinAsync(Patience, token)) { told.Add(changes.Current); if (changes.Current.State == "unauthorized") break; }
        Assert.IsTrue(told.Any(change => change.SessionId is null && change.State == "unauthorized"), "The machine is told as not reached any more.");
        await changes.DisposeAsync();
        Assert.IsEmpty((await rig.Machines.WatchSessionAsync("never-paired", sessionId, token).ToListAsync(token)), "An unknown machine is not watched.");
    }

    [TestMethod]
    public async Task TheStatesOfTheSessions_AreABaselineFirst_ThenChangesOnceEach_AndABaselineAgainAfterAReconnection()
    {
        var token = TestContext.CancellationToken;
        await using var rig = await StartAsync();
        var services = (await rig.Machines.GetAsync(rig.Machine.DeviceId, token))!;
        var first = await CreateAsync(services, "First");
        Assert.IsTrue(await rig.Host.WaitListedAsync(first, Patience, token), "The machine lists it before anyone follows.");

        await using var changes = rig.Machines.WatchAsync(token).GetAsyncEnumerator(token);
        var baseline = new List<PluginMachineChange>();
        while (!baseline.Any(change => change.SessionId == first) && await changes.MoveNextAsync().AsTask().WithinAsync(Patience, token)) baseline.Add(changes.Current);
        Assert.IsTrue(baseline.All(static change => change.Baseline), "What it finds when it starts is how things stand: nothing is notified for it.");
        Assert.IsTrue(baseline.Any(change => change is { SessionId: null, State: "online" } && change.MachineId == rig.Machine.DeviceId), "The state of the machine is told.");
        Assert.IsTrue(baseline.Any(change => change.SessionId == first && change.MachineId == rig.Machine.DeviceId));

        var second = await CreateAsync(services, "Second");
        var news = await NextAsync(changes, change => change.SessionId == second);
        Assert.IsFalse(news.Baseline, "A session that appears is news.");

        // The channel closes under the host, which connects again: whatever the machine says then is a baseline again, never news.
        var before = rig.Host.Connections;
        await rig.Host.CurrentClient!.DisposeAsync();
        await WaitForAsync(() => rig.Host.Connections > before && rig.Host.State == RemoteMachineState.Online, "the machine to be reached again");
        var again = await NextAsync(changes, change => change.SessionId == first && change.Baseline);
        Assert.IsTrue(again.Baseline);
        TestContext.WriteLine("Baseline again: " + again);
    }

    [TestMethod]
    public async Task TheLiveLineOfASession_IsFollowedAsReplacements_AndEndsWhenTheMachineMayNotBeSeenAnyMore()
    {
        var token = TestContext.CancellationToken;
        await using var rig = await StartAsync();
        var services = (await rig.Machines.GetAsync(rig.Machine.DeviceId, token))!;
        var sessionId = await CreateAsync(services, "Live");
        rig.Fixture.Provider.Turns.Enqueue((_, _) => Task.FromResult(ScriptedFleetProvider.Answer("Hello from the node.")));

        await using var display = rig.Machines.WatchSessionAsync(rig.Machine.DeviceId, sessionId, token).GetAsyncEnumerator(token);
        var sent = await services.Alta.InvokeAsync(["session", "send", sessionId, "--stdin", "--no-wait"], "Hello node", cancellationToken: token);
        Assert.AreEqual(0, sent.ExitCode, sent.Error);
        PluginMachineDisplay? answer = null;
        var deadline = DateTime.UtcNow + Patience;
        while (answer is null && DateTime.UtcNow < deadline && await display.MoveNextAsync().AsTask().WithinAsync(Patience, token))
            if (display.Current is { Kind: "assistant" } current && current.Line == "Hello from the node.") answer = current;
        Assert.IsNotNull(answer, "The line of the answer comes as a replacement.");
        Assert.AreEqual((rig.Machine.DeviceId, sessionId), (answer.MachineId, answer.SessionId));

        // The pairing narrowed to nothing the device may see: the stream ends.
        rig.Fixture.Service.TrustedDevices.SetPermissions(rig.Fleet.DeviceId, DevicePermissions.None);
        try { await rig.Host.ReadInfoAsync(token); }
        catch (CodeAlta.Fleet.Protocol.FleetException) { /* The machine no longer answers this device. */ }
        rig.Host.RequestRefresh();
        var rest = Task.Run(async () => { while (await display.MoveNextAsync()) { } }, token);
        await rest.WithinAsync(Patience, token);
    }

    [TestMethod]
    public void ACommandNeedsThePermissionTheNodeApiAsksForIt()
    {
        Assert.AreEqual("view", DesktopPluginMachines.Needs(["session", "events", "s", "--since", "0"]));
        Assert.AreEqual("view", DesktopPluginMachines.Needs(["session", "list", "--limit", "500"]));
        Assert.AreEqual("view", DesktopPluginMachines.Needs(["job", "output", "j", "--session", "s"]) ?? "view", "A job read is a read.");
        Assert.AreEqual("drive", DesktopPluginMachines.Needs(["session", "send", "s"]));
        Assert.AreEqual("drive", DesktopPluginMachines.Needs(["session", "abort", "s"]));
        Assert.AreEqual("drive", DesktopPluginMachines.Needs(["session", "archive", "s"]), "A small action changes the session.");
        Assert.AreEqual("start", DesktopPluginMachines.Needs(["session", "create", "--global"]));
        Assert.IsNull(DesktopPluginMachines.Needs(["session", "delete", "s"]), "Nothing else is carried by the node API, and the commands refuse it.");
        Assert.IsNull(DesktopPluginMachines.Needs(["plugin", "list"]));
    }

    [TestMethod]
    public void TheLine_IsOneBoundedLine()
    {
        Assert.AreEqual("", DesktopPluginMachines.Concise("  \n "));
        Assert.AreEqual("first", DesktopPluginMachines.Concise("\n  first  \nsecond"));
        var longLine = DesktopPluginMachines.Concise(new string('x', 500));
        Assert.AreEqual(DesktopPluginMachines.MaxLine, longLine.Length);
        Assert.IsTrue(longLine.EndsWith('…'));
    }

    private static async Task<string> CreateAsync(PluginMachineServices services, string title)
    {
        var created = await services.Alta.InvokeAsync(["session", "create", "--global", "--space", "default", "--provider", "scripted", "--title", title]);
        Assert.AreEqual(0, created.ExitCode, created.Error);
        return Records(created).Single(static record => Text(record, "type") == "alta.session.created").GetProperty("sessionId").GetString()!;
    }

    private async Task<PluginMachineChange> NextAsync(IAsyncEnumerator<PluginMachineChange> changes, Func<PluginMachineChange, bool> wanted)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline && await changes.MoveNextAsync().AsTask().WithinAsync(Patience, TestContext.CancellationToken))
            if (wanted(changes.Current)) return changes.Current;
        Assert.Fail("The change did not come.");
        return null!;
    }

    private static JsonElement[] Records(PluginAltaCommandResult result)
        => [.. result.TranscriptJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => JsonDocument.Parse(line).RootElement.Clone())];

    private static string? Text(JsonElement record, string name) => record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Waited too long for {what}.");
            await Task.Delay(50, TestContext.CancellationToken);
        }
    }
}
