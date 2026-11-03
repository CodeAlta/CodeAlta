using System.Net;
using System.Security.Cryptography;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Fleet.Update;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The coordinated update of the fleet with a real server and nodes in this process, whose self-update is faked (nothing
/// is installed): the desktop signs a campaign with the hashes of the feed, the nodes update first, the server last, a
/// node offline updates when it reconnects; a node waits for the time of an instruction, across a restart.
/// </summary>
[TestClass]
public sealed class UpdateCampaignTests
{
    private const string Runtime = "win-x64";
    private const string LinkId = "link-1";

    [TestMethod]
    public async Task ACampaignUpdatesTheNodesFirstThenTheServerAndANodeOfflineWhenItReconnects()
    {
        using var root = new TempDirectory();
        var feed = WriteFeed(root, "4.0.1", "4.0.2");
        var server = new FakeServerUpdate("4.0.1");
        await using var fixture = await ServerFixture.StartAsync(updateStatus: server.Status, configure: options => WithUpdates(options, server));
        await using var fleet = await TestFleet.StartAsync(fixture, root, "Mac mini", "Linux box");
        var (mac, linux) = (fleet.Nodes[0], fleet.Nodes[1]);
        await mac.StartAsync();

        var campaign = await fleet.Desktop.StartUpdateAsync(new FleetUpdateCampaignChange(Feed: feed));
        Assert.AreEqual(("4.0.2", 1L, FleetUpdateMachineStates.Pending), (campaign.TargetVersion, campaign.Sequence, campaign.ServerState), "the latest version both products list");
        Assert.AreEqual(2, campaign.Machines.Count, "every node of the organization by default");

        // The node online updates first, with the hash the desktop read from the feed and signed for its runtime.
        var request = await mac.WaitAppliedAsync();
        Assert.AreEqual(("4.0.2", feed, UpdateOrigin.Remote), (request.Version, request.Source, request.Origin));
        var signedHash = request.ExpectedSha512;
        Assert.AreEqual(Sha512(Path.Combine(feed, "CodeAlta.Node.win-x64.4.0.2.nupkg")), signedHash);
        await WaitForAsync(async () => (await fleet.CampaignAsync(campaign.Id)).Machines.Single(m => m.DeviceId == mac.DeviceId).State == FleetUpdateMachineStates.Switching);
        Assert.AreEqual(0, server.Applied.Count, "the server waits for its nodes");

        // The node switches to the new version: it says so once it connects again.
        await mac.RestartAsync("4.0.2");
        var serverRequest = await server.WaitAppliedAsync();
        var serverHash = serverRequest.ExpectedSha512;
        Assert.AreEqual(Sha512(Path.Combine(feed, "CodeAlta.Server.win-x64.4.0.2.nupkg")), serverHash, "the server checks its own package too");
        var afterServer = await WaitForCampaignAsync(fleet, campaign.Id, FleetUpdateCampaignStates.PartlyDone);
        Assert.AreEqual(FleetUpdateMachineStates.Updated, afterServer.ServerState);
        Assert.AreEqual(FleetUpdateMachineStates.Updated, afterServer.Machines.Single(m => m.DeviceId == mac.DeviceId).State);
        Assert.AreEqual(FleetUpdateMachineStates.Pending, afterServer.Machines.Single(m => m.DeviceId == linux.DeviceId).State, "offline: pending, not failed");

        // The node offline updates when it reconnects, and the campaign is done.
        await linux.StartAsync();
        Assert.AreEqual("4.0.2", (await linux.WaitAppliedAsync()).Version);
        await linux.RestartAsync("4.0.2");
        var done = await WaitForCampaignAsync(fleet, campaign.Id, FleetUpdateCampaignStates.Done);
        Assert.IsTrue(done.Machines.All(static m => m.State == FleetUpdateMachineStates.Updated && m.Running == "4.0.2"));
        Assert.AreEqual(1, server.Applied.Count, "the server updated once");

        var audit = await fixture.SendAsync(fleet.Owner, HttpMethod.Get, "/api/v1/audit");
        Assert.IsTrue(audit.Read<ListResponse<AuditRecordDto>>().Items.Any(static item => item.Action == "update.campaign.create" && item.Result == "success"));
    }

    [TestMethod]
    public async Task AFleetOnAPrereleaseUpdatesToTheLatestPrereleaseByDefault()
    {
        using var root = new TempDirectory();
        var feed = WriteFeed(root, "4.0.0-alpha.901", "4.0.0-alpha.902");
        var server = new FakeServerUpdate("4.0.0-alpha.901");
        await using var fixture = await ServerFixture.StartAsync(updateStatus: server.Status, configure: options => WithUpdates(options, server));
        await using var fleet = await TestFleet.StartAsync(fixture, root);

        // As each machine's own update: one that runs a prerelease follows the prereleases.
        var campaign = await fleet.Desktop.StartUpdateAsync(new FleetUpdateCampaignChange(Feed: feed));
        Assert.AreEqual("4.0.0-alpha.902", campaign.TargetVersion);
    }

    [TestMethod]
    public async Task ACampaignIsCancelledBeforeTheServersStepAndANewOneFollowsIt()
    {
        using var root = new TempDirectory();
        var feed = WriteFeed(root, "4.0.1", "4.0.2");
        var server = new FakeServerUpdate("4.0.1");
        await using var fixture = await ServerFixture.StartAsync(updateStatus: server.Status, configure: options => WithUpdates(options, server));
        await using var fleet = await TestFleet.StartAsync(fixture, root, "Mac mini");
        var mac = fleet.Nodes[0];

        // Scheduled in an hour: nothing is asked meanwhile.
        var first = await fleet.Desktop.StartUpdateAsync(new FleetUpdateCampaignChange(Feed: feed, NotBefore: DateTimeOffset.UtcNow.AddHours(1), TimeZone: "Europe/Paris"));
        Assert.AreEqual((FleetUpdateCampaignStates.Scheduled, "Europe/Paris"), (first.State, first.TimeZone));
        await mac.StartAsync();
        var conflict = await Assert.ThrowsExactlyAsync<FleetServerException>(() => fleet.Desktop.StartUpdateAsync(new FleetUpdateCampaignChange(Feed: feed)));
        Assert.AreEqual("update.conflict", conflict.Code, "one campaign at a time");

        var cancelled = await fleet.Desktop.CancelUpdateAsync(first.Id);
        Assert.AreEqual(FleetUpdateCampaignStates.Cancelled, cancelled.State);
        Assert.IsTrue(cancelled.Machines.All(static m => m.State == FleetUpdateMachineStates.Cancelled));
        Assert.AreEqual(0, mac.Applied.Count);

        var second = await fleet.Desktop.StartUpdateAsync(new FleetUpdateCampaignChange(Version: "4.0.2", Feed: feed));
        Assert.AreEqual(2L, second.Sequence, "numbered after the first");
        await mac.WaitAppliedAsync();
        await mac.RestartAsync("4.0.2");
        await server.WaitAppliedAsync();
        await WaitForCampaignAsync(fleet, second.Id, FleetUpdateCampaignStates.Done);
        var late = await Assert.ThrowsExactlyAsync<FleetServerException>(() => fleet.Desktop.CancelUpdateAsync(second.Id));
        Assert.AreEqual("update.conflict", late.Code, "too late once the server updated");
    }

    [TestMethod]
    public async Task OnlyAnOwnersControllerStartsACampaignWithAManifestItSigned()
    {
        using var root = new TempDirectory();
        var feed = WriteFeed(root, "4.0.1", "4.0.2");
        var server = new FakeServerUpdate("4.0.1");
        await using var fixture = await ServerFixture.StartAsync(updateStatus: server.Status, configure: options => WithUpdates(options, server));
        await using var fleet = await TestFleet.StartAsync(fixture, root, "Mac mini");
        using var nodeClient = new FleetServerClient(fixture.BaseAddress, fleet.Nodes[0].Key);

        var byNode = await Assert.ThrowsExactlyAsync<FleetServerException>(() => nodeClient.GetUpdatesAsync());
        Assert.AreEqual((HttpStatusCode.Forbidden, FleetErrorCodes.Forbidden), (byNode.Status, byNode.Code), "a node reads nothing of the updates");

        var updates = await fleet.Desktop.GetUpdatesAsync();
        var organization = (await fleet.Desktop.GetOrganizationAsync()).Id;
        using var outsider = new TestDevice("Outsider");
        var forged = outsider.SignManifest(Manifest(organization, fleet.OwnerKey.DeviceId, updates.NextSequence, "4.0.2"));
        var refused = await fixture.SendAsync(fleet.Owner, HttpMethod.Post, "/api/v1/updates/campaigns", new CreateUpdateCampaignRequest(forged));
        Assert.AreEqual((HttpStatusCode.BadRequest, "update.invalid"), (refused.Status, refused.Code), "a manifest the caller did not sign");

        var noServerHash = fleet.Owner.SignManifest(Manifest(organization, fleet.OwnerKey.DeviceId, updates.NextSequence, "4.0.2", server: false));
        refused = await fixture.SendAsync(fleet.Owner, HttpMethod.Post, "/api/v1/updates/campaigns", new CreateUpdateCampaignRequest(noServerHash));
        Assert.AreEqual((HttpStatusCode.BadRequest, "update.invalid"), (refused.Status, refused.Code), "the server could not check its own package");

        await fleet.Desktop.SetUpdatePolicyAsync(new FleetUpdatePolicyChange(FleetUpdateModes.Pinned, PinnedVersion: "4.0.1"));
        var pinned = await Assert.ThrowsExactlyAsync<FleetServerException>(() => fleet.Desktop.StartUpdateAsync(new FleetUpdateCampaignChange(Version: "4.0.2", Feed: feed)));
        Assert.AreEqual("update.conflict", pinned.Code, "a pinned fleet stays on its version");
        Assert.AreEqual(FleetUpdateModes.Pinned, (await fleet.Desktop.GetUpdatesAsync()).Policy.Mode);
    }

    [TestMethod]
    public async Task ANodeWaitsForTheTimeOfAnInstructionAcrossARestart()
    {
        using var root = new TempDirectory();
        var feed = WriteFeed(root, "4.0.1", "4.0.2");
        var server = new FakeServerUpdate("4.0.1");
        await using var fixture = await ServerFixture.StartAsync(updateStatus: server.Status, configure: options => WithUpdates(options, server));
        await using var fleet = await TestFleet.StartAsync(fixture, root, "Mac mini");
        var mac = fleet.Nodes[0];
        await mac.StartAsync();
        var organization = (await fleet.Desktop.GetOrganizationAsync()).Id;
        var hashes = await UpdateReleases.ReadHashesAsync(UpdateFeeds.Open(feed, new HttpClient()), UpdateReleases.FleetProducts, UpdateVersion.Parse("4.0.2"), CancellationToken.None);
        var start = fixture.Clock.Now.AddHours(3);
        var manifest = Manifest(organization, fleet.OwnerKey.DeviceId, 1, "4.0.2", feed: feed) with { Packages = hashes, NotBefore = start };

        // An instruction received early (here straight from the test) waits for its time on the node itself.
        var instruction = new FleetUpdateInstruction("c1", fleet.SignManifest(manifest));
        await mac.Agent!.AcceptAsync(mac.Connector!, LinkId, organization, instruction, CancellationToken.None);
        Assert.AreEqual(FleetUpdateMachineStates.Waiting, mac.Agent.Current?.State);
        Assert.IsNull(mac.Agent.Tick());
        await mac.RestartAsync("4.0.1");
        Assert.AreEqual(FleetUpdateMachineStates.Waiting, mac.Agent!.Current?.State, "kept across a restart");
        // What the node kept runs once its server sent the campaign again, after its policy, as at each connection.
        await mac.Agent.AcceptAsync(mac.Connector!, LinkId, organization, instruction, CancellationToken.None);
        Assert.IsNull(mac.Agent.Tick());
        Assert.AreEqual(0, mac.Applied.Count);

        fixture.Clock.Advance(TimeSpan.FromHours(3));
        await (mac.Agent.Tick() ?? Task.CompletedTask);
        Assert.AreEqual(1, mac.Applied.Count, "it updates once its time came");
        Assert.AreEqual(FleetUpdateMachineStates.Switching, mac.Agent.Current?.State);

        // The same manifest replayed after it is not taken again, nor an older one.
        await mac.Agent.AcceptAsync(mac.Connector!, LinkId, organization, new FleetUpdateInstruction("c0", fleet.SignManifest(manifest with { CampaignId = "c0", Sequence = 1 })),
            CancellationToken.None);
        Assert.AreEqual(("c1", FleetUpdateMachineStates.Switching), (mac.Agent.Current?.CampaignId, mac.Agent.Current?.State));
    }

    private static CoordinatorServerOptions WithUpdates(CoordinatorServerOptions options, FakeServerUpdate server) => new()
    {
        DataRoot = options.DataRoot, Urls = options.Urls, TimeProvider = options.TimeProvider, Relay = options.Relay, ConfigureLogging = options.ConfigureLogging,
        UpdateStatus = options.UpdateStatus, EnvironmentVariable = options.EnvironmentVariable, TelemetryExportInterval = options.TelemetryExportInterval,
        ApplyUpdate = server.ApplyAsync, UpdateRuntime = Runtime, UpdateInterval = TimeSpan.FromMilliseconds(100), UpdateOfflineGrace = TimeSpan.FromSeconds(30),
    };

    private static FleetUpdateManifest Manifest(string organization, string signer, long sequence, string version, bool server = true, string feed = UpdateFeeds.NuGetOrg)
    {
        var packages = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["CodeAlta.Node"] = new Dictionary<string, string> { [Runtime] = Convert.ToBase64String(SHA512.HashData("node"u8)) },
        };
        if (server) packages["CodeAlta.Server"] = new Dictionary<string, string> { [Runtime] = Convert.ToBase64String(SHA512.HashData("server"u8)) };
        return new FleetUpdateManifest(FleetUpdateManifest.CurrentVersion, organization, "c" + sequence, sequence, version, feed, false, null, null, null, packages, signer,
            DateTimeOffset.UtcNow);
    }

    // A folder feed with the tool and win-x64 runtime packages of both products at each version: content enough to hash.
    private static string WriteFeed(TempDirectory root, params string[] versions)
    {
        var feed = Directory.CreateDirectory(Path.Combine(root.Path, "feed")).FullName;
        foreach (var version in versions)
        {
            foreach (var product in new[] { "CodeAlta.Node", "CodeAlta.Server" })
            {
                File.WriteAllText(Path.Combine(feed, $"{product}.{version}.nupkg"), $"tool {product} {version}");
                File.WriteAllText(Path.Combine(feed, $"{product}.{Runtime}.{version}.nupkg"), $"runtime {product} {version}");
            }
        }

        return feed;
    }

    private static string Sha512(string file) => Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(file)));

    private static async Task<UpdateCampaignDto> WaitForCampaignAsync(TestFleet fleet, string id, string state)
    {
        UpdateCampaignDto? campaign = null;
        await WaitForAsync(async () => (campaign = await fleet.CampaignAsync(id)).State == state, () => $"the campaign is {campaign?.State}");
        return campaign!;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow + Waits.Hang;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The condition did not hold in time: " + describe?.Invoke());
            await Task.Delay(50);
        }
    }

    /// <summary>The self-update of the server, faked: it records what it was asked and runs the version at once.</summary>
    private sealed class FakeServerUpdate(string running)
    {
        private readonly TaskCompletionSource<UpdateRequest> _applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile string _running = running;

        public List<UpdateRequest> Applied { get; } = [];

        public FleetUpdateStatus? Status() => new(_running, null, null, null, null, null, [], null);

        public Task<StagedUpdate?> ApplyAsync(UpdateRequest request, string requestedBy, CancellationToken cancellationToken)
        {
            lock (Applied) Applied.Add(request);
            _running = request.Version!;
            _applied.TrySetResult(request);
            return Task.FromResult<StagedUpdate?>(null);
        }

        public Task<UpdateRequest> WaitAppliedAsync() => _applied.Task.WaitAsync(Waits.Hang);
    }

    /// <summary>A server set up by a desktop, with nodes enrolled and approved.</summary>
    private sealed class TestFleet : IAsyncDisposable
    {
        private TestFleet(ServerFixture server, DeviceIdentity ownerKey, FleetServerClient desktop, TestDevice owner, List<TestNode> nodes)
        {
            Server = server;
            OwnerKey = ownerKey;
            Desktop = desktop;
            Owner = owner;
            Nodes = nodes;
        }

        public ServerFixture Server { get; }

        public DeviceIdentity OwnerKey { get; }

        public FleetServerClient Desktop { get; }

        /// <summary>The owner's device as the signed requests of <see cref="ServerFixture.SendAsync"/> need it (the same key, exported).</summary>
        public TestDevice Owner { get; }

        public List<TestNode> Nodes { get; }

        public static async Task<TestFleet> StartAsync(ServerFixture server, TempDirectory root, params string[] names)
        {
            var ownerKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
            var desktop = new FleetServerClient(server.BaseAddress, ownerKey);
            await desktop.SetUpAsync(server.SetupCode, "Alex", "Desktop");
            var organization = (await desktop.GetOrganizationAsync()).Id;
            var nodes = new List<TestNode>();
            foreach (var name in names)
            {
                var key = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, name));
                using (var client = new FleetServerClient(server.BaseAddress, key))
                    await client.EnrollAsync((await desktop.CreateEnrollmentTokenAsync()).Token, name);
                await desktop.ApproveAsync(key.DeviceId, FleetDeviceKinds.Node);
                nodes.Add(new TestNode(server, key, Path.Combine(root.Path, name + "-state"), organization));
            }

            return new TestFleet(server, ownerKey, desktop, TestDevice.From(ownerKey), nodes);
        }

        public Task<UpdateCampaignDto> CampaignAsync(string id) => Server.Server.Services.GetService(typeof(Services.UpdateCampaignService)) is Services.UpdateCampaignService updates
            ? ReadAsync(updates, id)
            : throw new InvalidOperationException("No update service.");

        public FleetSignedUpdateDocument SignManifest(FleetUpdateManifest manifest) => Owner.SignManifest(manifest);

        private async Task<UpdateCampaignDto> ReadAsync(Services.UpdateCampaignService updates, string id)
        {
            var actor = await Server.Server.Store.GetDeviceAsync(OwnerKey.DeviceId) ?? throw new InvalidOperationException("No owner device.");
            return await updates.GetCampaignAsync(actor, id, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in Nodes) await node.DisposeAsync();
            Desktop.Dispose();
            OwnerKey.Dispose();
            Owner.Dispose();
        }
    }

    /// <summary>A node in this process: its connector and its update agent, whose self-update is faked; a restart makes both again.</summary>
    private sealed class TestNode(ServerFixture server, DeviceIdentity key, string folder, string organization) : IAsyncDisposable
    {
        private TaskCompletionSource<UpdateRequest> _applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _running = "4.0.1";

        public DeviceIdentity Key => key;

        public string DeviceId => key.DeviceId;

        public FleetUpdateAgent? Agent { get; private set; }

        public FleetConnector? Connector { get; private set; }

        public List<UpdateRequest> Applied { get; } = [];

        public async Task StartAsync()
        {
            Directory.CreateDirectory(folder);
            FleetConnector? connector = null;
            var agent = new FleetUpdateAgent(new FleetUpdateAgentOptions
            {
                StatePath = Path.Combine(folder, "update-campaign.json"), DeviceId = key.DeviceId, Running = UpdateVersion.Parse(_running), Runtime = Runtime,
                Policy = () => UpdateSourcePolicy.From(new UpdateSourceSettings { AllowedSources = [Path.Combine(Path.GetDirectoryName(folder)!, "feed")] }),
                Apply = (request, _, _) =>
                {
                    lock (Applied) Applied.Add(request);
                    _applied.TrySetResult(request);
                    return Task.FromResult<StagedUpdate?>(new StagedUpdate(UpdateVersion.Parse(request.Version!), folder, PackageHash.Parse(request.ExpectedSha512!), "expected hash", false));
                },
                Check = (_, _) => throw new InvalidOperationException("No automatic campaign here."),
                Status = () => new FleetUpdateStatus(_running, null, null, null, null,
                    Applied.Count > 0 && _running != Applied[^1].Version ? new FleetUpdatePending(Applied[^1].Version!, "waiting_idle", DateTimeOffset.UtcNow, 0, false) : null, [], null),
                Connector = _ => connector,
                Time = server.Clock,
                Interval = TimeSpan.FromMilliseconds(50),
            });
            connector = new FleetConnector(new FleetConnectorOptions
            {
                ServerAddress = server.BaseAddress, Identity = key, OutboxPath = Path.Combine(folder, "outbox.json"), Client = new FleetClientInfo("node", _running, "windows"),
                MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromMilliseconds(500), PollInterval = TimeSpan.FromMilliseconds(200),
                PublishDelay = TimeSpan.FromMilliseconds(20),
                UpdateInstruction = (instruction, token) => agent.AcceptAsync(connector!, LinkId, organization, instruction, token),
                UpdateCancel = (cancel, _) =>
                {
                    agent.Cancel(LinkId, cancel);
                    return Task.CompletedTask;
                },
                UpdateReport = () => agent.Report(LinkId),
                UpdatePolicy = (policy, token) => agent.AcceptPolicyAsync(LinkId, organization, policy, async read => await connector!.Devices.RefreshAsync(read), token),
            });
            Agent = agent;
            Connector = connector;
            agent.Start();
            connector.Start();
            await WaitForAsync(() => Task.FromResult(connector.State == FleetConnectorState.Online));
        }

        public Task<UpdateRequest> WaitAppliedAsync() => _applied.Task.WaitAsync(Waits.Hang);

        public async Task RestartAsync(string running)
        {
            await StopAsync();
            _running = running;
            _applied = new TaskCompletionSource<UpdateRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            await StartAsync();
        }

        private async Task StopAsync()
        {
            if (Connector is { } connector) await connector.DisposeAsync();
            if (Agent is { } agent) await agent.DisposeAsync();
            Connector = null;
            Agent = null;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            key.Dispose();
        }
    }
}
