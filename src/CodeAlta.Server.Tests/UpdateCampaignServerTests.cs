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
