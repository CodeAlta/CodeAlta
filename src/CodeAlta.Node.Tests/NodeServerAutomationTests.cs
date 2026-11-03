using System.Buffers.Text;
using CodeAlta.Automations;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Node.Tests;

/// <summary>
/// Automations a person gives a machine through CodeAlta Server: the desktop signs and seals each version, the server
/// stores and delivers it, the node verifies it against the device list it anchored, opens it with its own key and fires
/// its schedule; it claims each due time with the server, runs it alone while the server is away, refuses what a server
/// altered, and stops running what a device removed from the list gave it.
/// </summary>
[TestClass]
public sealed class NodeServerAutomationTests
{
    private const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1f02";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Noon = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Definition = $$"""
        [automations.{{Id}}]
        name = "Half past"
        triggers = [{ type = "hourly", minute = 30 }]
        prompt = "Say hello."
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheServerDeliversASignedVersion_TheNodeRunsIt_ClaimsWithTheServer_AndRunsAloneWhileItIsAway()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var clock = new NodeClock { Now = Noon };
        var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop-fleet"));
        using var desktop = new FleetServerClient(address, desktopKey);
        var setup = await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);
        Assert.AreEqual(NodeExitCodes.Success, await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]));
        var nodeId = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending").Id;
        await desktop.ApproveAsync(nodeId, token);

        var provider = new ScriptedNodeProvider();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            ConfigureModelProviders = provider.Register,
            Automations = new NodeAutomationOptions
            {
                TimeProvider = clock, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account,
            },
        }, token);
        var automations = node.Automations!;
        var servers = NodeServers.Start(node, null, "test-node", "1.0.0", Fast);
        try
        {
            await WaitForAsync(async () => (await desktop.ListMachinesAsync(token)).Any(static machine => machine.Online) ? "online" : null, token);
            Assert.AreEqual(nodeId, automations.DeviceId);

            // The desktop signs a version for the node, sealed for it and the person's controllers.
            var list = await desktop.ReadVerifiedDeviceListAsync(null, token);
            var signed = Sign(desktopKey, list, setup.Organization.Id, nodeId, 1);
            Assert.IsFalse(signed.Payload.Contains("Say hello", StringComparison.Ordinal), "the server never reads what it does");
            Assert.AreEqual("pending", (await desktop.PutAutomationAsync(signed, token)).State);

            await WaitForAsync(() => Task.FromResult(automations.AssignmentOf(Id) is { Via: not null, Number: 1 } held ? held : null), token);
            Assert.AreEqual(Noon.AddMinutes(30), automations.Engine.NextDue(Id), "the node fires its schedule");
            await WaitForAsync(async () => (await desktop.ListAutomationsAsync(token)).Automations.SingleOrDefault(static item => item.State == "applied"), token);
            var entry = automations.Engine.Snapshot.Find(Id)!;
            Assert.IsNull(automations.Check(entry, new CodeAlta.Agent.ModelProviderDescriptor(new("scripted"), "Scripted")));

            // A server that alters the version it delivers: the node refuses it, and keeps what it held.
            var held = automations.AssignmentOf(Id)!;
            var altered = Sign(desktopKey, list, setup.Organization.Id, nodeId, 2) is var next
                ? next with { Payload = next.Payload.Replace("\"enabled\":true", "\"enabled\":false", StringComparison.Ordinal) }
                : null!;
            var refused = await automations.AcceptFromServerAsync(held.Via!, list, setup.Organization.Id, altered, _ => (null, "not opened"), token);
            Assert.IsFalse(refused.Applied);
            StringAssert.Contains(refused.Message, "signature");
            Assert.AreEqual(1, automations.AssignmentOf(Id)!.Number);

            // The due time comes: the node claims it with the server, runs it, and reports the run.
            clock.Now = Noon.AddMinutes(30).AddSeconds(5);
            automations.Engine.Tick(clock.Now);
            var reported = await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs
                .FirstOrDefault(static run => run.Status == AutomationRun.Completed), token);
            Assert.AreEqual(("hourly", nodeId, 1L), (reported.Trigger, reported.MachineId, reported.Number));
            Assert.AreEqual(AutomationService.Occurrence(entry.Definition.Triggers[0], Noon.AddMinutes(30)), reported.Occurrence, "the claim of the server keyed the run");
            StringAssert.Contains(provider.LastUserText, "Say hello.");

            // The server is away when the next time comes: the node runs it alone.
            await server.DisposeAsync();
            server = null;
            clock.Now = Noon.AddHours(1).AddMinutes(30).AddSeconds(5);
            automations.Engine.Tick(clock.Now);
            await WaitForAsync(() => Task.FromResult(automations.Engine.Runs(Id, 10).FirstOrDefault(static run => run is { Status: AutomationRun.Completed, StartedAt.Hour: 13 })), token);
        }
        finally
        {
            await servers.DisposeAsync();
            if (server is not null) await server.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AVersionFromAControllerTheListRemovedDoesNotRun_NorOneOfAnotherPerson()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions
            {
                TimeProvider = new NodeClock { Now = Noon }, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account,
            },
        }, token);
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var tablet = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet"));
        using var self = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var automations = node.Automations!;
        automations.DeviceId = self.DeviceId;
        var list = DeviceList(laptop, tablet, self, removeTablet: false);

        // The tablet, a controller of the person, gives the automation: it runs.
        var outcome = await automations.AcceptFromServerAsync("link-1", list, "org", Sign(tablet, list, "org", self.DeviceId, 1),
            version => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, out var content, out var reason)
                ? (content, null) : (null, reason), token);
        Assert.IsTrue(outcome.Applied, outcome.Message);
        var entry = automations.Engine.Snapshot.Find(Id)!;
        var provider = new CodeAlta.Agent.ModelProviderDescriptor(new("scripted"), "Scripted");
        Assert.IsNull(automations.Check(entry, provider));

        // The tablet is removed from the device list: what it gave stops running.
        var removed = DeviceList(laptop, tablet, self, removeTablet: true);
        automations.ServerDevices = _ => removed;
        StringAssert.Contains(automations.Check(entry, provider), "no longer a controller device");
        // Nor does it give anything more.
        var again = await automations.AcceptFromServerAsync("link-1", removed, "org", Sign(tablet, list, "org", self.DeviceId, 2), _ => (null, "unused"), token);
        Assert.IsFalse(again.Applied);
        StringAssert.Contains(again.Message, "not signed by a controller device");

        // A version sealed for another machine does not open here.
        automations.ServerDevices = _ => list;
        var elsewhere = await automations.AcceptFromServerAsync("link-1", list, "org", Sign(laptop, list, "org", self.DeviceId, 3),
            _ => (null, "the definition was not sealed for this machine"), token);
        Assert.IsFalse(elsewhere.Applied);
        StringAssert.Contains(elsewhere.Message, "does not open");
        Assert.AreEqual(1, automations.AssignmentOf(Id)!.Number, "nothing refused replaced what the node holds");
    }

    // A version signed by a controller for a machine, its definition sealed for the machine and the person's controllers.
    private static FleetSignedAutomationVersion Sign(DeviceIdentity signer, FleetDeviceListState list, string organization, string machine, long number)
    {
        var sealedText = FleetAutomationSigning.Seal(new FleetAutomationContent(Definition, null), Id, number, machine, list, signer);
        return FleetAutomationSigning.Sign(signer, new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, organization, Id, number, machine, false, true, ["hourly"],
            null, null, sealedText, string.Empty, string.Empty, DateTimeOffset.UtcNow));
    }

    // The laptop (the first controller, of person p1), the tablet (another controller of p1) and the node of p1.
    private static FleetDeviceListState DeviceList(DeviceIdentity laptop, DeviceIdentity tablet, DeviceIdentity node, bool removeTablet)
    {
        var time = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
        FleetSignedDeviceListEntry Entry(long sequence, string? previous, string operation, DeviceIdentity device, string? kind, string? organization, string? owner)
        {
            var add = operation == FleetDeviceListOperations.Add;
            var payload = FleetDeviceListEntries.SerializePayload(new FleetDeviceListEntryPayload(1, organization, sequence, previous, operation, device.DeviceId,
                add ? Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()) : null, kind, owner, laptop.DeviceId, time));
            return new FleetSignedDeviceListEntry(payload, Base64Url.EncodeToString(laptop.SignData(FleetDeviceListEntries.GetSignedBytes(payload))));
        }

        var entries = new List<FleetSignedDeviceListEntry> { Entry(1, null, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, null, null) };
        entries.Add(Entry(2, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, "org", "p1"));
        entries.Add(Entry(3, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add, tablet, FleetDeviceKinds.Controller, "org", "p1"));
        if (removeTablet) entries.Add(Entry(4, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Remove, tablet, null, "org", null));
        return FleetDeviceListVerifier.VerifyOrThrow(entries, "org");
    }

    // A connector that tries again within a second, and keeps the automation callbacks of the node.
    private static FleetConnectorOptions Fast(FleetConnectorOptions options) => new()
    {
        ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
        PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory, PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
        MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(500),
        DeviceListPinPath = options.DeviceListPinPath, Devices = options.Devices, MetadataKeys = options.MetadataKeys, AcceptRelay = options.AcceptRelay,
        AutomationsAssigned = options.AutomationsAssigned, AutomationsOnline = options.AutomationsOnline,
    };

    private async Task<T> WaitForAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("It did not get there in time.");
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
}
