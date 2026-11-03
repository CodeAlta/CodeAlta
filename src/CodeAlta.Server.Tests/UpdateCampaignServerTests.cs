using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Update;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using CodeAlta.Server.Storage.Sqlite;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The server's side of the coordinated update of the fleet (<c>doc/fleet/deployment.md</c>, updates), with nodes that
/// are bare control connections: the signed policy goes to them, a policy revokes the campaigns it no longer allows, a
/// cancellation waits for the node that was offline, the server takes an automatic version only while it is the latest,
/// resumes its own step after a restart, holds one active campaign, keeps working on old campaigns, and tries a failed
/// automatic version again a bounded number of times.
/// </summary>
[TestClass]
public sealed class UpdateCampaignServerTests
{
    private const string Runtime = "win-x64";

    [TestMethod]
    public async Task ThePolicyGoesToTheNodesAsSignedOnEachChangeAndEachConnection()
    {
        await using var fleet = await TestFleet.StartAsync(nodes: 1);
        var node = await fleet.ConnectAsync(0);
        var manual = await fleet.SetPolicyAsync(1, FleetUpdateModes.Manual);
        Assert.AreEqual(manual, await node.NextAsync(FleetServerMethods.UpdatePolicy, FleetUpdateJsonContext.Default.FleetSignedUpdateDocument), "every mode goes to the nodes");
        var pinned = await fleet.SetPolicyAsync(2, FleetUpdateModes.Pinned, pinned: "4.0.1");
        Assert.AreEqual(pinned, await node.NextAsync(FleetServerMethods.UpdatePolicy, FleetUpdateJsonContext.Default.FleetSignedUpdateDocument));

        await fleet.DisconnectAsync(node);
        var again = await fleet.ConnectAsync(0);
        Assert.AreEqual(pinned, await again.NextAsync(FleetServerMethods.UpdatePolicy, FleetUpdateJsonContext.Default.FleetSignedUpdateDocument), "and again on each connection");
    }

    [TestMethod]
    public async Task APolicyRevokesTheCampaignsItNoLongerAllows()
    {
        await using var fleet = await TestFleet.StartAsync(nodes: 1, latest: "4.0.2");
        // An automatic policy whose window is later today: its campaign waits for it.
        await fleet.SetPolicyAsync(1, FleetUpdateModes.Automatic, window: fleet.Clock.Now.AddHours(2).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        await fleet.TickAsync();
        var automatic = (await fleet.UpdatesAsync()).Campaigns.Single();
        Assert.AreEqual((true, FleetUpdateCampaignStates.Scheduled), (automatic.Automatic, automatic.State));

        await fleet.SetPolicyAsync(2, FleetUpdateModes.Manual);
        Assert.AreEqual(FleetUpdateCampaignStates.Cancelled, (await fleet.CampaignAsync(automatic.Id)).State, "a manual policy revokes the automatic campaign");
        await fleet.TickAsync();
        Assert.AreEqual(1, (await fleet.UpdatesAsync()).Campaigns.Count, "and starts none");

        // A campaign a person started to another version than the one the fleet is now pinned to is revoked too; the node
        // that was asked is told.
        var node = await fleet.ConnectAsync(0);
        var manual = await fleet.CreateCampaignAsync("4.0.2");
        await fleet.TickAsync();
        Assert.AreEqual(manual.Id, (await node.NextAsync(FleetServerMethods.UpdateInstruction, FleetServerJsonContext.Default.FleetUpdateInstruction)).CampaignId);
        await fleet.SetPolicyAsync(3, FleetUpdateModes.Pinned, pinned: "4.0.1");
        Assert.AreEqual(manual.Id, (await node.NextAsync(FleetServerMethods.UpdateCancel, FleetServerJsonContext.Default.FleetUpdateCancel)).CampaignId);
        Assert.AreEqual(FleetUpdateCampaignStates.Cancelled, (await fleet.CampaignAsync(manual.Id)).State);
    }

    [TestMethod]
    public async Task TheServerTakesAnAutomaticVersionOnlyWhileItIsTheLatestStable()
    {
        var reads = 0;
        // The feed lists 4.0.2 when the campaign starts, and withdraws it before the server's step.
        await using var fleet = await TestFleet.StartAsync(readLatest: () => Interlocked.Increment(ref reads) == 1 ? "4.0.2" : "4.0.1");
        await fleet.SetPolicyAsync(1, FleetUpdateModes.Automatic);
        await fleet.TickAsync();

        var campaign = (await fleet.UpdatesAsync()).Campaigns.Single();
        Assert.AreEqual(0, fleet.Server.Applied.Count, "the server reads the feed again before its own step");
        Assert.AreEqual((FleetUpdateCampaignStates.Failed, FleetUpdateMachineStates.Failed), (campaign.State, campaign.ServerState));
        StringAssert.Contains(campaign.ServerMessage, "4.0.2");
    }

    [TestMethod]
    public async Task ACancellationReachesANodeThatWasOfflineWhenItReconnects()
    {
        await using var fleet = await TestFleet.StartAsync(nodes: 1);
        var node = await fleet.ConnectAsync(0);
        var campaign = await fleet.CreateCampaignAsync("4.0.2");
        await fleet.TickAsync();
        Assert.AreEqual(campaign.Id, (await node.NextAsync(FleetServerMethods.UpdateInstruction, FleetServerJsonContext.Default.FleetUpdateInstruction)).CampaignId);
        await node.ReportAsync(campaign.Id, FleetUpdateMachineStates.Downloading);
        await fleet.WaitForAsync(async () => (await fleet.CampaignAsync(campaign.Id)).Machines.Single().State == FleetUpdateMachineStates.Downloading);

        // Offline while it downloads: the cancellation cannot reach it now.
        await fleet.DisconnectAsync(node);
        Assert.AreEqual(FleetUpdateCampaignStates.Cancelled, (await fleet.CancelAsync(campaign.Id)).State);

        var back = await fleet.ConnectAsync(0);
        Assert.AreEqual(campaign.Id, (await back.NextAsync(FleetServerMethods.UpdateCancel, FleetServerJsonContext.Default.FleetUpdateCancel)).CampaignId,
            "the cancellation waits for the node");
        await back.ReportAsync(campaign.Id, FleetUpdateMachineStates.Cancelled);
        await fleet.WaitForAsync(async () => !(await fleet.MachinesAsync(campaign.Id)).Single().CancelPending);

        await fleet.DisconnectAsync(back);
        var later = await fleet.ConnectAsync(0);
        await fleet.TickAsync();
        Assert.IsFalse(later.Received(FleetServerMethods.UpdateCancel), "once acknowledged, it is not sent again");
    }

    [TestMethod]
    public async Task TheServersStepARestartInterruptedBeforeItsSwitchWasRecordedIsTakenAgain()
    {
        using var root = new TempDirectory();
        using var owner = new TestDevice("Desktop");
        string id;
        await using (var first = await TestFleet.StartAsync(root: root, owner: owner))
            id = (await first.CreateCampaignAsync("4.0.2", notBefore: first.Clock.Now.AddHours(1))).Id;

        // The server stopped once it recorded its step as asked, before its self-update recorded the switch.
        await using (var store = new SqliteCoordinatorStore(new SqliteCoordinatorStoreOptions { DatabasePath = new ServerPaths(root.Path).DatabasePath }))
        {
            await store.InitializeAsync();
            var campaign = await store.GetUpdateCampaignAsync(id) ?? throw new InvalidOperationException("No campaign.");
            await store.SaveUpdateCampaignAsync(campaign with
            {
                State = FleetUpdateCampaignStates.UpdatingServer, ServerState = FleetUpdateMachineStates.Instructed,
            }, [], null);
        }

        await using var second = await TestFleet.StartAsync(root: root, owner: owner, setUp: false);
        Assert.AreEqual("4.0.2", (await second.Server.WaitAppliedAsync()).Version, "the step is taken again");
        await second.TickAsync();
        Assert.AreEqual(FleetUpdateCampaignStates.Done, (await second.CampaignAsync(id)).State);
        Assert.AreEqual(1, second.Server.Applied.Count, "once");
    }

    [TestMethod]
    public void AServerStepUnderWayIsNotTakenAgain()
    {
        var at = new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
        var campaign = new UpdateCampaignRecord("c1", "org", 1, false, "4.0.2", UpdateFeeds.NuGetOrg, false, null, null, "{}", "sig", "desktop",
            FleetUpdateCampaignStates.UpdatingServer, FleetUpdateMachineStates.Instructed, null, at, at);
        FleetUpdateStatus Status(FleetUpdatePending? pending = null, FleetUpdateTrial? trial = null, FleetUpdateOutcome? outcome = null, string running = "4.0.1")
            => new(running, null, null, null, trial, pending, [], outcome);
        var later = at.AddMinutes(5);

        Assert.AreEqual(FleetUpdateMachineStates.Pending, UpdateCampaignEngine.ResumeServerStep(campaign, Status(), later)?.ServerState, "nothing recorded: taken again");
        Assert.IsNull(UpdateCampaignEngine.ResumeServerStep(campaign, Status(pending: new("4.0.2", "requested", at, 0, false)), later), "its switch waits");
        Assert.IsNull(UpdateCampaignEngine.ResumeServerStep(campaign, Status(trial: new("4.0.2", at, 1)), later), "on trial");
        Assert.IsNull(UpdateCampaignEngine.ResumeServerStep(campaign, Status(running: "4.0.2"), later), "it runs");
        Assert.IsNull(UpdateCampaignEngine.ResumeServerStep(campaign, Status(outcome: new("4.0.2", "rolled_back", at.AddMinutes(1), null)), later), "it ended");
        Assert.AreEqual(FleetUpdateMachineStates.Pending,
            UpdateCampaignEngine.ResumeServerStep(campaign, Status(outcome: new("4.0.2", "failed", at.AddDays(-1), null)), later)?.ServerState, "an outcome of before");
        Assert.IsNull(UpdateCampaignEngine.ResumeServerStep(campaign with { ServerState = FleetUpdateMachineStates.Pending }, Status(), later), "not asked yet");
    }

    [TestMethod]
    public async Task TheStoreHoldsOneActiveCampaignPerOrganization()
    {
        await using var fleet = await TestFleet.StartAsync();
        var manual = await fleet.CreateCampaignAsync("4.0.2", notBefore: fleet.Clock.Now.AddHours(1));
        var record = await fleet.Store.GetUpdateCampaignAsync(manual.Id) ?? throw new InvalidOperationException("No campaign.");

        var automatic = record with { Id = "auto-race", Automatic = true, CreatedByDeviceId = null };
        Assert.IsFalse(await fleet.Store.CreateUpdateCampaignAsync(automatic, [], null), "another campaign is active");
        Assert.AreEqual(1, (await fleet.Store.ListUpdateCampaignsAsync(fleet.Organization, 100)).Count(static campaign => FleetUpdateCampaignStates.IsActive(campaign.State)));
    }

    [TestMethod]
    public async Task ANodeOfflineIsAskedWhenItReconnectsAfterManyNewerCampaigns()
    {
        await using var fleet = await TestFleet.StartAsync(nodes: 1);
        var campaign = await fleet.CreateCampaignAsync("4.0.2");
        await fleet.TickAsync();
        await fleet.TickAsync();
        Assert.AreEqual(FleetUpdateCampaignStates.PartlyDone, (await fleet.CampaignAsync(campaign.Id)).State, "the server updated; the node offline is pending");

        // More newer campaigns than the history the desktop reads, none for this node.
        var record = await fleet.Store.GetUpdateCampaignAsync(campaign.Id) ?? throw new InvalidOperationException("No campaign.");
        for (var i = 1; i <= 25; i++)
        {
            fleet.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.IsTrue(await fleet.Store.CreateUpdateCampaignAsync(record with
            {
                Id = $"later-{i}", Sequence = 100 + i, State = FleetUpdateCampaignStates.Cancelled, ServerState = FleetUpdateMachineStates.Cancelled,
                CreatedAt = fleet.Clock.Now, UpdatedAt = fleet.Clock.Now,
            }, [], null));
        }

        var node = await fleet.ConnectAsync(0);
        await fleet.TickAsync();
        Assert.AreEqual(campaign.Id, (await node.NextAsync(FleetServerMethods.UpdateInstruction, FleetServerJsonContext.Default.FleetUpdateInstruction)).CampaignId);
    }

    [TestMethod]
    public async Task AFailedAutomaticCampaignIsTriedAgainTheNextDaysABoundedNumberOfTimes()
    {
        await using var fleet = await TestFleet.StartAsync(latest: "4.0.2");
        fleet.Server.Failure = "The feed did not answer.";
        await fleet.SetPolicyAsync(1, FleetUpdateModes.Automatic);
        await fleet.TickAsync();
        Assert.AreEqual(FleetUpdateCampaignStates.Failed, (await fleet.UpdatesAsync()).Campaigns.Single().State);

        for (var day = 2; day <= 4; day++)
        {
            fleet.Clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)));
            await fleet.TickAsync();
        }

        var campaigns = (await fleet.UpdatesAsync()).Campaigns;
        Assert.AreEqual(3, campaigns.Count, "tried again each day, three times at most");
        Assert.IsTrue(campaigns.All(static campaign => campaign.Automatic && campaign.TargetVersion == "4.0.2" && campaign.State == FleetUpdateCampaignStates.Failed));
        Assert.AreEqual(3, fleet.Server.Applied.Count);
        StringAssert.Contains((await fleet.UpdatesAsync()).Policy.LastCheckMessage, "failed 3 times");
    }

    [TestMethod]
    public async Task AnAutomaticVersionThatRolledBackIsNotTriedAgain()
    {
        await using var fleet = await TestFleet.StartAsync(latest: "4.0.2");
        fleet.Server.RollBack = true;
        await fleet.SetPolicyAsync(1, FleetUpdateModes.Automatic);
        await fleet.TickAsync();
        await fleet.TickAsync();
        var first = (await fleet.UpdatesAsync()).Campaigns.Single();
        Assert.AreEqual((FleetUpdateCampaignStates.Failed, FleetUpdateMachineStates.RolledBack), (first.State, first.ServerState));

        fleet.Clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)));
        await fleet.TickAsync();
        Assert.AreEqual(1, (await fleet.UpdatesAsync()).Campaigns.Count, "a version that rolled back is not tried again");
    }

    /// <summary>The self-update of the server, faked: it records what it was asked, and runs the version, fails, or rolls back.</summary>
    private sealed class FakeServer(TestClock clock)
    {
        private readonly TaskCompletionSource<UpdateRequest> _applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile string _running = "4.0.1";
        private volatile FleetUpdateOutcome? _outcome;

        public List<UpdateRequest> Applied { get; } = [];

        public string? Failure { get; set; }

        public bool RollBack { get; set; }

        public FleetUpdateStatus? Status() => new(_running, null, null, null, null, null, [], _outcome);

        public Task<StagedUpdate?> ApplyAsync(UpdateRequest request, string requestedBy, CancellationToken cancellationToken)
        {
            lock (Applied) Applied.Add(request);
            _applied.TrySetResult(request);
            if (Failure is { } failure) throw new UpdateException(UpdateErrorCodes.NotFound, failure);
            if (RollBack) _outcome = new FleetUpdateOutcome(request.Version!, "rolled_back", clock.Now, "It did not confirm.");
            else _running = request.Version!;
            return Task.FromResult<StagedUpdate?>(null);
        }

        public Task<UpdateRequest> WaitAppliedAsync() => _applied.Task.WaitAsync(Waits.Hang);
    }

    /// <summary>A node as a bare control connection: it keeps the events the server sends and reports what it is told to.</summary>
    private sealed class TestNode(FleetConnection connection, TestDevice device) : IAsyncDisposable
    {
        private readonly List<FleetEvent> _seen = [];

        public Channel<FleetEvent> Events { get; } = Channel.CreateUnbounded<FleetEvent>();

        public FleetConnection Connection => connection;

        public TestDevice Device => device;

        public ValueTask OnEventAsync(FleetEvent fleetEvent)
        {
            lock (_seen) _seen.Add(fleetEvent);
            Events.Writer.TryWrite(fleetEvent);
            return ValueTask.CompletedTask;
        }

        public bool Received(string name)
        {
            lock (_seen) return _seen.Any(item => item.Event == name);
        }

        public async Task<T> NextAsync<T>(string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        {
            using var timeout = new CancellationTokenSource(Waits.Hang);
            while (true)
            {
                var next = await Events.Reader.ReadAsync(timeout.Token);
                if (next.Event == name) return next.Payload!.Value.Deserialize(type)!;
            }
        }

        public Task ReportAsync(string campaignId, string state)
            => connection.SendRequestAsync(FleetServerMethods.UpdateReport, JsonSerializer.SerializeToElement(
                new FleetUpdateReport(campaignId, state, "4.0.2", "4.0.1", null, DateTimeOffset.UtcNow), FleetServerJsonContext.Default.FleetUpdateReport));

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    /// <summary>A server set up by an owner's desktop, with nodes approved, whose own update is faked.</summary>
    private sealed class TestFleet : IAsyncDisposable
    {
        private readonly TempDirectory? _ownedRoot;
        private readonly TestDevice? _ownedOwner;
        private readonly List<TestNode> _connections = [];

        private TestFleet(ServerFixture fixture, FakeServer server, TestDevice owner, string organization, List<TestDevice> nodes, TempDirectory? ownedRoot, TestDevice? ownedOwner)
        {
            Fixture = fixture;
            Server = server;
            Owner = owner;
            Organization = organization;
            Nodes = nodes;
            _ownedRoot = ownedRoot;
            _ownedOwner = ownedOwner;
        }

        public ServerFixture Fixture { get; }

        public FakeServer Server { get; }

        public TestDevice Owner { get; }

        public string Organization { get; }

        public List<TestDevice> Nodes { get; }

        public TestClock Clock => Fixture.Clock;

        public ICoordinatorStore Store => Fixture.Server.Store;

        private UpdateCampaignService Service => Fixture.GetService<UpdateCampaignService>();

        public static async Task<TestFleet> StartAsync(int nodes = 0, string? latest = null, Func<string?>? readLatest = null, TempDirectory? root = null, TestDevice? owner = null,
            bool setUp = true)
        {
            var ownedRoot = root is null ? new TempDirectory() : null;
            var ownedOwner = owner is null ? new TestDevice("Desktop") : null;
            owner ??= ownedOwner!;
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var server = new FakeServer(clock);
            readLatest ??= () => latest;
            var fixture = await ServerFixture.StartAsync(root ?? ownedRoot, clock, updateStatus: server.Status, configure: options => new CoordinatorServerOptions
            {
                DataRoot = options.DataRoot, Urls = options.Urls, TimeProvider = options.TimeProvider, Relay = options.Relay, ConfigureLogging = options.ConfigureLogging,
                UpdateStatus = options.UpdateStatus, EnvironmentVariable = options.EnvironmentVariable, TelemetryExportInterval = options.TelemetryExportInterval,
                ApplyUpdate = server.ApplyAsync, UpdateRuntime = Runtime, UpdateInterval = TimeSpan.FromHours(1), UpdateOfflineGrace = TimeSpan.FromSeconds(30),
                ReadLatestUpdate = (_, _) => Task.FromResult(readLatest()),
            });
            string organization;
            var devices = new List<TestDevice>();
            if (setUp)
            {
                var setup = await fixture.SetUpAsync(owner);
                organization = setup.Organization.Id;
                for (var i = 0; i < nodes; i++)
                {
                    var node = new TestDevice($"Node {i}");
                    var pending = await fixture.EnrollAsync(owner, node);
                    var entry = await fixture.NextEntryAsync(owner, FleetDeviceListOperations.Add, pending.Id, node.PublicKey, FleetDeviceKinds.Node, setup.Owner.Id, organization);
                    var approved = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/devices/{pending.Id}/approve", new DeviceListChangeRequest(entry));
                    Assert.AreEqual(HttpStatusCode.OK, approved.Status, approved.Body.ToString());
                    devices.Add(node);
                }
            }
            else
            {
                organization = (await fixture.Server.Store.GetOrganizationAsync())?.Id ?? throw new InvalidOperationException("No organization.");
            }

            return new TestFleet(fixture, server, owner, organization, devices, ownedRoot, ownedOwner);
        }

        public Task TickAsync() => Service.TickAsync(CancellationToken.None);

        public async Task<TestNode> ConnectAsync(int index)
        {
            TestNode? node = null;
            var (connection, _) = await Fixture.ConnectAsync(Nodes[index], connection => connection.SetEventHandler((fleetEvent, _) => node!.OnEventAsync(fleetEvent)));
            node = new TestNode(connection, Nodes[index]);
            _connections.Add(node);
            var presence = Fixture.GetService<Control.PresenceRegistry>();
            await WaitForAsync(() => Task.FromResult(presence.IsOnline(Nodes[index].DeviceId)));
            return node;
        }

        public async Task DisconnectAsync(TestNode node)
        {
            await node.DisposeAsync();
            var presence = Fixture.GetService<Control.PresenceRegistry>();
            await WaitForAsync(() => Task.FromResult(!presence.IsOnline(node.Device.DeviceId)));
        }

        public async Task<FleetSignedUpdateDocument> SetPolicyAsync(long sequence, string mode, string? pinned = null, string? window = null)
        {
            var payload = FleetUpdateDocuments.Serialize(new FleetUpdatePolicy(FleetUpdatePolicy.CurrentVersion, Organization, sequence, mode, pinned, null, window, null,
                Owner.DeviceId, Clock.Now));
            var signed = new FleetSignedUpdateDocument(payload, Owner.Sign(FleetUpdateDocuments.GetPolicySignedBytes(payload)));
            var answer = await Fixture.SendAsync(Owner, HttpMethod.Put, "/api/v1/updates/policy", new SetUpdatePolicyRequest(signed));
            Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
            return signed;
        }

        public async Task<UpdateCampaignDto> CreateCampaignAsync(string version, DateTimeOffset? notBefore = null)
        {
            var sequence = (await UpdatesAsync()).NextSequence;
            var packages = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["CodeAlta.Node"] = new Dictionary<string, string> { [Runtime] = Convert.ToBase64String(SHA512.HashData("node"u8)) },
                ["CodeAlta.Server"] = new Dictionary<string, string> { [Runtime] = Convert.ToBase64String(SHA512.HashData("server"u8)) },
            };
            var manifest = new FleetUpdateManifest(FleetUpdateManifest.CurrentVersion, Organization, "c" + sequence, sequence, version, UpdateFeeds.NuGetOrg, false, notBefore,
                null, null, packages, Owner.DeviceId, Clock.Now);
            var answer = await Fixture.SendAsync(Owner, HttpMethod.Post, "/api/v1/updates/campaigns", new CreateUpdateCampaignRequest(Owner.SignManifest(manifest)));
            Assert.AreEqual(HttpStatusCode.Created, answer.Status, answer.Body.ToString());
            return answer.Read<UpdateCampaignDto>();
        }

        public async Task<UpdateCampaignDto> CancelAsync(string id)
        {
            var answer = await Fixture.SendAsync(Owner, HttpMethod.Post, $"/api/v1/updates/campaigns/{id}/cancel");
            Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
            return answer.Read<UpdateCampaignDto>();
        }

        public async Task<UpdatesDto> UpdatesAsync()
        {
            var answer = await Fixture.SendAsync(Owner, HttpMethod.Get, "/api/v1/updates");
            Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
            return answer.Read<UpdatesDto>();
        }

        public async Task<UpdateCampaignDto> CampaignAsync(string id)
        {
            var answer = await Fixture.SendAsync(Owner, HttpMethod.Get, $"/api/v1/updates/campaigns/{id}");
            Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
            return answer.Read<UpdateCampaignDto>();
        }

        public async Task<IReadOnlyList<UpdateCampaignMachineRecord>> MachinesAsync(string id) => await Store.ListUpdateCampaignMachinesAsync(id);

        public async Task WaitForAsync(Func<Task<bool>> condition)
        {
            var deadline = DateTime.UtcNow + Waits.Hang;
            while (!await condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("The condition did not hold in time.");
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _connections) await connection.DisposeAsync();
            await Fixture.DisposeAsync();
            foreach (var node in Nodes) node.Dispose();
            _ownedOwner?.Dispose();
            _ownedRoot?.Dispose();
        }
    }
}
