using CodeAlta.Automations;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node.Tests;

/// <summary>
/// Automations on a node (<c>doc/fleet/node.md</c>, "Automations and statistics"): a paired device assigns one through the
/// node API, the node fires its schedule, claims each due time once, checks its policy and the credential kind before a
/// run starts, and keeps what it was assigned across a restart, with nobody connected.
/// </summary>
[TestClass]
public sealed class NodeAutomationTests
{
    private const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1f01";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Noon = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PairedDevice_AssignsAnAutomation_TheNodeFiresItsScheduleOnce_AndKeepsItAcrossARestart()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var clock = new NodeClock { Now = Noon };
        var provider = new ScriptedNodeProvider();
        string deviceId;
        string runId;
        await using (var fixture = await AutomationFixture.StartAsync(root, provider, clock, token))
        {
            var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
            deviceId = fixture.ViewerIdentity.DeviceId;
            await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
            var put = await client.PutAutomationAsync(new FleetAutomationPutParams(Definition("Half past", "Say hello."), null), "put-1", token);
            Assert.AreEqual((Id, "Half past", true, deviceId, "viewer"), (put.Id, put.Name, put.Enabled, put.AssignedBy, put.AssignedByName));
            Assert.AreEqual(Noon.AddMinutes(30), put.NextDue, "The node fires its schedule.");
            Assert.AreEqual(Id, (await client.ListAutomationsAsync(token)).Automations.Single().Id);

            // The due time comes: the node claims it, runs a session there, and the device reads the run.
            var engine = fixture.Node.Automations!.Engine;
            clock.Now = Noon.AddMinutes(30).AddSeconds(5);
            engine.Tick(clock.Now);
            var run = await WaitForAsync(async () => (await client.ListAutomationRunsAsync(Id, cancellationToken: token)).Runs.FirstOrDefault(static candidate => candidate.Status == AutomationRun.Completed), token);
            Assert.AreEqual(("hourly", "Half past"), (run.Trigger, run.Name));
            Assert.IsNotNull(run.SessionId, "The run's session is on the node.");
            StringAssert.Contains(provider.LastUserText, "Say hello.");
            runId = run.Id;

            // The same due time fired again (a second engine, a catch-up) is taken already.
            var entry = engine.Snapshot.Find(Id)!;
            var occurrence = AutomationService.Occurrence(entry.Definition.Triggers[0], Noon.AddMinutes(30));
            Assert.IsFalse(engine.Trigger(entry, "hourly", null, entry.Definition.Prompt, occurrence: occurrence));
            Assert.AreEqual(AutomationRun.Skipped, (await client.ListAutomationRunsAsync(Id, cancellationToken: token)).Runs[0].Status);

            // A retry of a run by hand is the same run.
            var manual = await client.RunAutomationAsync(Id, "run-1", token);
            Assert.AreEqual(manual.Id, (await client.RunAutomationAsync(Id, "run-1", token)).Id);
            await WaitForAsync(async () => (await client.ListAutomationRunsAsync(Id, cancellationToken: token)).Runs.FirstOrDefault(candidate => candidate.Id == manual.Id && candidate.Status == AutomationRun.Completed), token);
        }

        // The node starts again with nobody connected: what it was assigned still fires, and the claimed time stays claimed.
        clock.Now = Noon.AddHours(1).AddMinutes(29);
        await using (var again = await AutomationFixture.StartAsync(root, provider, clock, token))
        {
            var engine = again.Node.Automations!.Engine;
            await engine.RefreshAsync();
            Assert.IsTrue(engine.IsAssigned(Id));
            Assert.AreEqual(Noon.AddHours(1).AddMinutes(30), engine.NextDue(Id));
            Assert.AreEqual(deviceId, again.Node.Automations.AssignmentOf(Id)?.DeviceId);
            Assert.IsTrue(engine.Runs(Id, 10).Any(run => run.Id == runId), "The runs are kept by the node.");
            clock.Now = Noon.AddHours(1).AddMinutes(30).AddSeconds(5);
            engine.Tick(clock.Now);
            await WaitForAsync(() => Task.FromResult(engine.Runs(Id, 10).FirstOrDefault(static run => run is { Status: AutomationRun.Completed, StartedAt.Hour: 13 })), token);
        }
    }

    [TestMethod]
    public async Task Node_RefusesARun_ItsPolicy_TheCredentialKind_OrTheAssigningDevice_DoNotAllow()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var kind = AutomationCredentialKind.Unknown;
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token, _ => kind);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        await client.PutAutomationAsync(new FleetAutomationPutParams(Definition("Guarded", "Hello."), null), cancellationToken: token);

        async Task<FleetAutomationRunInfo> RunAsync() => await client.RunAutomationAsync(Id, cancellationToken: token);

        var unknown = await RunAsync();
        Assert.AreEqual(AutomationRun.Failed, unknown.Status);
        StringAssert.Contains(unknown.Message, "cannot be told");
        Assert.IsNull(unknown.SessionId, "Nothing is created for a run the node refuses.");

        kind = AutomationCredentialKind.Personal;
        NodeAutomationPolicy.Save(fixture.Node.Host.CatalogOptions.FleetRoot, new NodeAutomationPolicy { Credentials = NodeAutomationPolicy.AccountCredentials });
        StringAssert.Contains((await RunAsync()).Message, "account credentials only");
        NodeAutomationPolicy.Save(fixture.Node.Host.CatalogOptions.FleetRoot, new NodeAutomationPolicy { Enabled = false });
        StringAssert.Contains((await RunAsync()).Message, "turned them off");
        NodeAutomationPolicy.Save(fixture.Node.Host.CatalogOptions.FleetRoot, new NodeAutomationPolicy());

        // Changed on the node by something else than the device: it does not run as it is now.
        var assigned = Path.Combine(fixture.Node.Host.CatalogOptions.FleetRoot, NodeAutomations.AssignedFileName);
        File.WriteAllText(assigned, File.ReadAllText(assigned).Replace("Hello.", "Do something else.", StringComparison.Ordinal));
        await fixture.Node.Automations!.Engine.RefreshAsync();
        StringAssert.Contains((await RunAsync()).Message, "changed on this machine");
        await client.PutAutomationAsync(new FleetAutomationPutParams(Definition("Guarded", "Hello."), null), cancellationToken: token);
        var allowed = await RunAsync();
        Assert.AreNotEqual(AutomationRun.Failed, allowed.Status, allowed.Message);

        // The device loses the permission to give automations: what it gave stops running, and it cannot give more.
        fixture.Fleet.Service.TrustedDevices.SetPermissions(fixture.ViewerIdentity.DeviceId, DevicePermissions.View | DevicePermissions.Start);
        StringAssert.Contains(fixture.Node.Automations.Check(fixture.Node.Automations.Engine.Snapshot.Find(Id)!, new CodeAlta.Agent.ModelProviderDescriptor(new("scripted"), "Scripted")),
            "may no longer give automations");
        var forbidden = await Assert.ThrowsExactlyAsync<FleetException>(() => client.PutAutomationAsync(new FleetAutomationPutParams(Definition("Again", "x"), null), cancellationToken: token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, forbidden.Error.Code);
        StringAssert.Contains(forbidden.Message, "automate");
    }

    [TestMethod]
    public async Task Put_RefusesCommandTriggers_UnknownRepositories_AndInvalidDefinitions()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);

        var command = await Assert.ThrowsExactlyAsync<FleetException>(() => client.PutAutomationAsync(new FleetAutomationPutParams($$"""
            [automations.{{Id}}]
            name = "Watcher"
            prompt = "x"
            triggers = [{ type = "command", command = "echo hi" }]
            """, null), cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.AutomationInvalid, command.Error.Code);
        StringAssert.Contains(command.Message, "command");

        var project = await Assert.ThrowsExactlyAsync<FleetException>(() => client.PutAutomationAsync(
            new FleetAutomationPutParams(Definition("Somewhere", "x"), "github.com/nobody/nothing"), cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.ProjectNotFound, project.Error.Code);

        var invalid = await Assert.ThrowsExactlyAsync<FleetException>(() => client.PutAutomationAsync(new FleetAutomationPutParams("name = 1", null), cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.AutomationInvalid, invalid.Error.Code);

        var missing = await Assert.ThrowsExactlyAsync<FleetException>(() => client.RunAutomationAsync(Id, cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.AutomationNotFound, missing.Error.Code);
        Assert.AreEqual(0, (await client.ListAutomationsAsync(token)).Automations.Count);
        Assert.IsFalse((await client.RemoveAutomationAsync(Id, cancellationToken: token)).Removed);
    }

    [TestMethod]
    public async Task PairedDevice_WithoutTheAutomatePermission_CannotListNorAssign()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token);
        // The permissions a pairing gives: viewing, driving and starting sessions; not giving automations.
        var machine = await fixture.PairAsync(null, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => client.ListAutomationsAsync(token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, refused.Error.Code);
    }

    private static string Definition(string name, string prompt) => $$"""
        [automations.{{Id}}]
        name = "{{name}}"
        project = "C:/somewhere/on/the/laptop"
        runs_on = "this-node"
        triggers = [{ type = "hourly", minute = 30 }]
        prompt = '''
        {{prompt}}
        '''
        """;

    private async Task<T> WaitForAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("The node did not get there in time.");
            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>A clock a test sets: the node's schedules see only the times the test gives.</summary>
    private sealed class NodeClock : TimeProvider
    {
        private long _ticks;

        public DateTimeOffset Now
        {
            get => new(Volatile.Read(ref _ticks), TimeSpan.Zero);
            set => Volatile.Write(ref _ticks, value.UtcTicks);
        }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A node with its fleet part and a viewer of another profile, on a root the test owns.</summary>
    private sealed class AutomationFixture : IAsyncDisposable
    {
        private AutomationFixture(NodeHost node, NodeFleet fleet, string viewerFleet)
        {
            Node = node;
            Fleet = fleet;
            ViewerIdentity = DeviceIdentity.LoadOrCreate(viewerFleet);
            ViewerMachines = new PairedMachineStore(viewerFleet);
        }

        public NodeHost Node { get; }

        public NodeFleet Fleet { get; }

        public DeviceIdentity ViewerIdentity { get; }

        public PairedMachineStore ViewerMachines { get; }

        public static async Task<AutomationFixture> StartAsync(NodeTestRoot root, ScriptedNodeProvider provider, TimeProvider clock, CancellationToken cancellationToken,
            Func<CodeAlta.Agent.ModelProviderDescriptor, AutomationCredentialKind>? kind = null)
        {
            var node = await NodeHost.StartAsync(root.Options() with
            {
                ConfigureModelProviders = provider.Register,
                Automations = new NodeAutomationOptions
                {
                    TimeProvider = clock, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false,
                    CredentialKind = kind ?? (static _ => AutomationCredentialKind.Account),
                },
            }, cancellationToken);
            var fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "test-node", "1.0.0");
            return new AutomationFixture(node, fleet, Path.Combine(root.Root, "viewer", "fleet"));
        }

        /// <summary>Pairs the viewer, then gives it the permissions asked; null keeps those of a pairing.</summary>
        public async Task<PairedMachine> PairAsync(DevicePermissions? permissions, CancellationToken cancellationToken)
        {
            var (offer, _) = Fleet.Service.Pairing.CreateOffer("test-node", Fleet.Addresses, TimeSpan.FromMinutes(1));
            var machine = await FleetNodeClient.PairAsync(offer, ViewerIdentity, "viewer", ViewerMachines, cancellationToken);
            if (permissions is { } granted) Fleet.Service.TrustedDevices.SetPermissions(ViewerIdentity.DeviceId, granted);
            return machine;
        }

        public async ValueTask DisposeAsync()
        {
            await Fleet.DisposeAsync();
            await Node.DisposeAsync();
            ViewerIdentity.Dispose();
        }
    }
}
