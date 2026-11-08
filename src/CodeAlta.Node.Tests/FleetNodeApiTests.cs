using System.Text;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node.Tests;

/// <summary>
/// Two hosts in one process over real TCP on loopback: a node (<see cref="NodeHost"/> with its <see cref="NodeFleet"/>)
/// and a viewer of another profile (its own device key and paired machines), through the whole channel stack.
/// </summary>
[TestClass]
public sealed class FleetNodeApiTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheToolsThePolicyTurnsOffForWorkOfTheFleet_ReachEveryDescendant_AndALineageThatCannotBeFollowedIsRefused()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await NodeFixture.StartAsync(token);
        var service = fixture.Fleet.Service;
        var policy = FleetPolicyState.Empty with { Number = 1, LastHash = "h1", Rules = new FleetPolicyRules(ToolsOff: ["shell_command"]) };
        // The server is linked to the default space only.
        Func<IEnumerable<Fleet.Server.FleetPolicySource>> sources = () => [new Fleet.Server.FleetPolicySource("https://fleet.example", policy, null, static space => space is null)];
        service.Policy = new Fleet.Server.FleetPolicyGuard(fixture.NodeIdentity.DeviceId, sources);
        var commands = fixture.Node.Host.Commands;
        var root = await commands.CreateDraftSessionAsync(null, fixture.Provider.Descriptor, "Remote work", null, null, null, null, null, null, null);
        service.Work.Record(root.SessionId, null, "remote", DateTimeOffset.UtcNow);

        // A session the work of the fleet started, many levels down: the tools stay off for it, as for the work itself.
        var deep = root;
        for (var level = 0; level < 40; level++)
        {
            deep = await commands.CreateDraftSessionAsync(null, fixture.Provider.Descriptor, $"Level {level}", new CodeAlta.Catalog.AltaActorProvenance
            {
                Kind = CodeAlta.Catalog.AltaActorProvenance.WorkflowKind, SourceSessionId = deep.SessionId, CreatedAt = DateTimeOffset.UtcNow,
            }, null, null, null, null, null, null);
        }

        CollectionAssert.AreEqual(new[] { "shell_command" }, (await service.PolicyToolsOffAsync(deep, token))?.ToArray(), "a deep descendant of the work of the fleet");
        // The work started in the default space; a session of it now in a space no server is linked to keeps the rules of the work.
        var moved = new CodeAlta.Catalog.SessionViewDescriptor { SessionId = Guid.NewGuid().ToString(), ParentSessionId = deep.SessionId, SpaceId = "unlinked" };
        CollectionAssert.AreEqual(new[] { "shell_command" }, (await service.PolicyToolsOffAsync(moved, token))?.ToArray(), "the space the work started in decides");
        var plain = await commands.CreateDraftSessionAsync(null, fixture.Provider.Descriptor, "Plain", null, null, null, null, null, null, null);
        Assert.IsNull(await service.PolicyToolsOffAsync(plain, token), "a session of the person at the machine keeps its tools");

        // A lineage too deep to follow to its start, or whose links make a cycle: whether the session is work of the fleet cannot be
        // told, and it is not given the tools the policy may turn off.
        var sessions = new Dictionary<string, CodeAlta.Catalog.SessionViewDescriptor>(StringComparer.Ordinal);
        CodeAlta.Catalog.SessionViewDescriptor Linked(string id, string? parent) => sessions[id] = new CodeAlta.Catalog.SessionViewDescriptor { SessionId = id, ParentSessionId = parent };
        Task<CodeAlta.Catalog.SessionViewDescriptor?> ParentOf(string id) => Task.FromResult(sessions.GetValueOrDefault(id));
        var endless = Linked("s0", null);
        for (var level = 1; level <= CodeAlta.Orchestration.Runtime.SessionToolGuards.MaximumLineageDepth; level++) endless = Linked("s" + level, endless.SessionId);
        await Assert.ThrowsExactlyAsync<CodeAlta.Orchestration.Runtime.SessionLineageException>(async () => await FleetNodeService.WorkOfLineageAsync(endless, [], ParentOf, token));
        Linked("a", "b");
        var looped = Linked("b", "a");
        await Assert.ThrowsExactlyAsync<CodeAlta.Orchestration.Runtime.SessionLineageException>(async () => await FleetNodeService.WorkOfLineageAsync(looped, [], ParentOf, token));
        Assert.IsNull(await FleetNodeService.WorkOfLineageAsync(Linked("lone", "gone"), [], ParentOf, token), "a parent this machine does not have is the start");
    }

    [TestMethod]
    public async Task Viewer_PairsWithTheNode_ThenReadsAndDrivesItsSessions()
    {
        await using var fixture = await NodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;

        // The owner's local control makes the code; the viewer redeems it over the channel.
        await using (var owner = await FleetNodeClient.ConnectAsOwnerAsync(fixture.Fleet.Addresses, fixture.NodeIdentity, token))
        {
            var offer = await owner.CreatePairingOfferAsync(TimeSpan.FromMinutes(1), token);
            var waiting = owner.WaitPairingAsync(Patience, token);
            var machine = await FleetNodeClient.PairAsync(PairingPayload.Parse(offer.Link, DateTimeOffset.UtcNow), fixture.ViewerIdentity, "viewer",
                fixture.ViewerMachines, token);

            Assert.AreEqual("test-node", machine.Name);
            Assert.AreEqual(fixture.Fleet.DeviceId, machine.DeviceId);
            var outcome = await waiting;
            Assert.AreEqual("redeemed", outcome.Status);
            Assert.AreEqual("viewer", outcome.DeviceName);
        }

        var trusted = fixture.Fleet.Service.TrustedDevices.FindTrusted(fixture.ViewerIdentity.Fingerprint);
        Assert.IsNotNull(trusted, "The node trusts the viewer once it redeemed the code.");
        Assert.AreEqual(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start | DevicePermissions.Models, trusted.Permissions,
            "Moving sessions here stays off; managing the node is given by a link made for it, never by being the first; its model servers are used.");
        var recorded = fixture.ViewerMachines.Find("test-node");
        Assert.IsNotNull(recorded, "The viewer records the machine.");
        CollectionAssert.AreEquivalent(new[] { "view", "drive", "start", "models" }, recorded.Permissions.ToArray());

        await using var client = await FleetNodeClient.ConnectAsync(recorded, fixture.ViewerIdentity, token);
        var info = await client.GetInfoAsync(token);
        Assert.AreEqual("test-node", info.Name);
        Assert.AreEqual(fixture.Fleet.DeviceId, info.DeviceId);
        Assert.AreEqual(FleetChannelProtocol.OperatingSystemName, info.Os);
        var ready = info.Providers.Single(provider => provider.Key == "scripted");
        Assert.IsTrue(ready.IsDefault, "The node says which provider a session that names none runs on.");
        Assert.IsTrue(ready.Models.Any(model => model.Id == ready.DefaultModelId), "A ready provider lists its models, its default one among them.");
        // The node says which of its spaces it publishes to a server: a controller device of its person keeps their keys.
        Assert.IsFalse(info.Spaces.Single(space => space.Id == CodeAlta.Catalog.SpaceDescriptor.DefaultId).Published);
        await fixture.Node.Host.SpaceCatalog.UpdateSettingsAsync(CodeAlta.Catalog.SpaceDescriptor.DefaultId,
            settings => settings with { Fleet = new CodeAlta.Catalog.SpaceFleet("https://fleet.example") }, token);
        Assert.IsTrue((await client.GetInfoAsync(token)).Spaces.Single(space => space.Id == CodeAlta.Catalog.SpaceDescriptor.DefaultId).Published);

        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Remote session"), "create-1", token);
        Assert.AreEqual("scripted", created.ProviderKey, "A session that names no provider runs on the default one of the node.");
        Assert.IsNull(created.Send);
        var again = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Remote session"), "create-1", token);
        Assert.AreEqual(created.SessionId, again.SessionId, "A retry of a creation is the same session.");

        var sessions = await client.ListSessionsAsync(cancellationToken: token);
        Assert.IsTrue(sessions.Sessions.Any(session => session.Id == created.SessionId), "The new session is listed.");

        // The turn waits on a gate, so that the watch sees it run, then answer.
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        fixture.Provider.Turns.Enqueue(ScriptedNodeProvider.WaitThenReply(gate.Task, "Hello from the node.", started));
        await using var watch = await client.WatchAsync(created.SessionId, token);
        var sent = await client.SendAsync(created.SessionId, "Hello node", retryKey: "send-1", cancellationToken: token);
        Assert.AreEqual("accepted", sent.Status);
        Assert.IsNotNull(sent.OperationId);
        var replayed = await client.SendAsync(created.SessionId, "Hello node", retryKey: "send-1", cancellationToken: token);
        Assert.AreEqual("replay", replayed.Status, "A retry of a send gets the first receipt and sends nothing.");
        Assert.AreEqual(sent.OperationId, replayed.OperationId);

        await started.Task.WaitAsync(Patience, token);
        var state = await client.GetSessionStateAsync(created.SessionId, token);
        Assert.IsNotNull(state.ActiveRunId, "The node says which run is at work.");
        gate.SetResult();

        var answered = await ReadUntilAsync(watch, display => display.Text.Any(text => text.Kind == "Assistant" && text.IsComplete && text.Text.Contains("Hello from the node.", StringComparison.Ordinal)), token);
        Assert.AreEqual(created.SessionId, answered.SessionId);

        var history = await WaitForAsync(async () =>
        {
            var window = await client.ReadHistoryAsync(new FleetHistoryParams(created.SessionId), token);
            return window.Entries.Any(entry => entry.Type == "content" && entry.Kind == "Assistant" && entry.Text == "Hello from the node.") ? window : null;
        }, token);
        Assert.IsTrue(history.Entries.Any(entry => entry.Type == "content" && entry.Kind == "User" && entry.Text?.Contains("Hello node", StringComparison.Ordinal) == true),
            "The window holds the prompt the viewer sent.");
        var forward = await client.ReadHistoryAsync(new FleetHistoryParams(created.SessionId, Direction: "forward"), token);
        Assert.IsTrue(forward.Entries.Count > 0);

        var queued = await client.QueueAsync(created.SessionId, "Next prompt", "queue-1", token);
        Assert.AreEqual("queued", queued.Status);
        Assert.AreEqual(queued.QueueItemId, (await client.QueueAsync(created.SessionId, "Next prompt", "queue-1", token)).QueueItemId,
            "A retry of a queued prompt is the same item.");

        Assert.AreEqual(0, (await client.ListPermissionsAsync(created.SessionId, token)).Requests.Count);
        Assert.IsNull((await client.ListAsksAsync(created.SessionId, token)).Ask);

        var missing = await Assert.ThrowsExactlyAsync<FleetException>(() => client.ReadHistoryAsync(new FleetHistoryParams("no-such-session"), token));
        Assert.AreEqual(FleetNodeErrorCodes.SessionNotFound, missing.Error.Code);
        var control = await Assert.ThrowsExactlyAsync<FleetException>(() => client.CreatePairingOfferAsync(null, token));
        Assert.AreEqual(FleetErrorCodes.MethodNotFound, control.Error.Code, "A paired device cannot make pairing codes.");
    }

    [TestMethod]
    public async Task NodeInfoAndSessionsList_LeaveOutAProjectFileTheNodeCannotRead_AndSayWhich()
    {
        await using var fixture = await NodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var catalog = fixture.Node.Host.ProjectCatalog;
        var good = await catalog.UpsertFromPathAsync(Path.Combine(Path.GetTempPath(), "fleet-good-" + Guid.NewGuid().ToString("N")), token);
        var machine = await fixture.PairViewerAsync(token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Listed session"), "create-unreadable", token);
        var broken = Path.Combine(catalog.Options.ProjectsRoot, "broken.md");
        await File.WriteAllTextAsync(broken, "---\nid: [unclosed\npath: {\n---\n", token);
        // A Windows path written by hand between double quotes: its single backslashes are invalid YAML escapes.
        var handWritten = Path.Combine(catalog.Options.ProjectsRoot, "hand-written.md");
        await File.WriteAllTextAsync(handWritten, "---\nid: 019a0000-0000-7000-8000-000000000001\nname: hand\npath: \"C:\\Users\\someone\\code\"\n---\n", token);

        var info = await client.GetInfoAsync(token);

        Assert.IsTrue(info.Projects.Any(project => project.Id == good.Id), "The projects the node can read are listed.");
        Assert.IsTrue(info.Diagnostics.All(static diagnostic => diagnostic.Code == FleetNodeDiagnostic.UnreadableProject));
        CollectionAssert.AreEquivalent(new[] { broken, handWritten }, info.Diagnostics.Select(static diagnostic => diagnostic.Path).ToArray());
        Assert.IsTrue(info.Diagnostics.All(static diagnostic => diagnostic.Message.Contains(diagnostic.Path!, StringComparison.Ordinal)));
        var sessions = await client.ListSessionsAsync(cancellationToken: token);
        Assert.IsTrue(sessions.Sessions.Any(session => session.Id == created.SessionId), "The sessions are listed beside a project file the node cannot read.");

        File.Delete(broken);
        File.Delete(handWritten);
        Assert.AreEqual(0, (await client.GetInfoAsync(token)).Diagnostics.Count, "A fixed catalog has nothing to say.");
    }

    [TestMethod]
    public async Task UnpairedDevice_IsRefused_AndMayOnlyRedeemAnOpenOffer()
    {
        await using var fixture = await NodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var machine = new PairedMachine("test-node", fixture.Fleet.DeviceId, fixture.NodeIdentity.Fingerprint, fixture.Fleet.Addresses, DateTimeOffset.UtcNow, []);

        // Without an open offer, TLS refuses a device the node does not know.
        var refused = await Assert.ThrowsExactlyAsync<DeviceAuthenticationException>(() => FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token));
        Assert.AreEqual(DeviceAuthenticationFailure.ClientNotTrusted, refused.Failure);

        // With one, it comes in, may call nothing but pair.redeem, and a wrong secret pairs nothing.
        fixture.Fleet.Service.Pairing.CreateOffer("test-node", fixture.Fleet.Addresses, TimeSpan.FromMinutes(1));
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        var unauthenticated = await Assert.ThrowsExactlyAsync<FleetException>(() => client.ListSessionsAsync(cancellationToken: token));
        Assert.AreEqual(FleetErrorCodes.Unauthenticated, unauthenticated.Error.Code);
        var wrong = await Assert.ThrowsExactlyAsync<FleetException>(() => client.RedeemAsync(Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_'), "viewer", token));
        Assert.AreEqual(FleetNodeErrorCodes.PairingRefused, wrong.Error.Code);
        Assert.IsNull(fixture.Fleet.Service.TrustedDevices.FindTrusted(fixture.ViewerIdentity.Fingerprint));
    }

    [TestMethod]
    public async Task RevokedDevice_IsDisconnected_AndRefusedAfterwards()
    {
        await using var fixture = await NodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var machine = await fixture.PairViewerAsync(token);
        var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        await using (client)
        {
            await client.ListSessionsAsync(cancellationToken: token);
            Assert.IsTrue(await fixture.Fleet.Service.RevokeAsync(fixture.ViewerIdentity.DeviceId));
            var closed = await client.Completion.WaitAsync(Patience, token);
            Assert.IsFalse(closed.ClosedLocally, "The node closed the channel of the revoked device.");
            await Assert.ThrowsAsync<FleetException>(() => client.ListSessionsAsync(cancellationToken: token));
        }

        var refused = await Assert.ThrowsExactlyAsync<DeviceAuthenticationException>(() => FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token));
        Assert.AreEqual(DeviceAuthenticationFailure.ClientRevoked, refused.Failure);
    }

    [TestMethod]
    public async Task CallsOutsideTheDevicePermissions_AreForbidden_AtOnce()
    {
        await using var fixture = await NodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var machine = await fixture.PairViewerAsync(token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Watched only"), cancellationToken: token);

        // The owner narrows the viewer to viewing, while its channel is open: the next call is checked against it.
        fixture.Fleet.Service.TrustedDevices.Add("viewer", fixture.ViewerIdentity.Fingerprint, DevicePermissions.View);
        var send = await Assert.ThrowsExactlyAsync<FleetException>(() => client.SendAsync(created.SessionId, "Hello", cancellationToken: token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, send.Error.Code);
        var create = await Assert.ThrowsExactlyAsync<FleetException>(() => client.CreateSessionAsync(new FleetCreateSessionParams(Prompt: "Hi"), cancellationToken: token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, create.Error.Code);
        var abort = await Assert.ThrowsExactlyAsync<FleetException>(() => client.AbortAsync(new FleetAbortParams(created.SessionId, Guid.NewGuid()), cancellationToken: token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, abort.Error.Code);
        Assert.IsTrue((await client.ListSessionsAsync(cancellationToken: token)).Sessions.Any(session => session.Id == created.SessionId), "Viewing is still allowed.");
    }

    [TestMethod]
    public async Task OwnerKey_FromAnotherMachine_IsRefused()
    {
        await using var fixture = await NodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var (serverStream, clientStream) = FleetStreams.CreatePair();
        var serve = Task.Run(async () =>
        {
            var channel = await FleetChannelStack.AcceptAsync(serverStream, new FleetChannelServerOptions
            {
                Certificate = fixture.NodeIdentity.CreateTlsCertificate(),
                TrustPolicy = fixture.Fleet.Service.TrustedDevices,
                OwnFingerprint = fixture.NodeIdentity.Fingerprint,
            }, token);
            await fixture.Fleet.Service.ServeAsync(channel, fromThisMachine: false, token);
        }, token);

        await using var client = await FleetNodeClient.ConnectAsync(clientStream, fixture.NodeIdentity.Fingerprint, fixture.NodeIdentity, token);
        await serve.WaitAsync(Patience, token);
        var closed = await client.Completion.WaitAsync(Patience, token);
        Assert.IsFalse(closed.ClosedLocally, "The node closed the channel of its own key from another machine.");
    }

    private static async Task<FleetSessionDisplay> ReadUntilAsync(FleetSessionWatch watch, Func<FleetSessionDisplay, bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Patience);
        var seen = new StringBuilder();
        try
        {
            await foreach (var display in watch.Updates.ReadAllAsync(timeout.Token))
            {
                seen.Append(display.Revision).Append(' ').Append(display.Lifecycle?.Kind).Append(';');
                if (condition(display)) return display;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        Assert.Fail($"The watch never showed what was expected. Seen: {seen}; ended: {watch.EndReason}");
        throw new InvalidOperationException();
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("The node never gave what was expected.");
            await Task.Delay(100, cancellationToken);
        }
    }
}

/// <summary>A node with its fleet part on a free loopback port, and the device key and paired machines of a viewer.</summary>
internal sealed class NodeFixture : IAsyncDisposable
{
    private readonly NodeTestRoot _root;

    private NodeFixture(NodeTestRoot root, NodeHost node, NodeFleet fleet, ScriptedNodeProvider provider)
    {
        _root = root;
        Node = node;
        Fleet = fleet;
        Provider = provider;
        NodeIdentity = DeviceIdentity.LoadOrCreate(node.Host.CatalogOptions);
        var viewerFleet = Path.Combine(root.Root, "viewer", "fleet");
        ViewerIdentity = DeviceIdentity.LoadOrCreate(viewerFleet);
        ViewerMachines = new PairedMachineStore(viewerFleet);
    }

    public NodeHost Node { get; }

    public NodeFleet Fleet { get; }

    public ScriptedNodeProvider Provider { get; }

    /// <summary>The node's own key, as <c>altanode pair</c> reads it from the profile.</summary>
    public DeviceIdentity NodeIdentity { get; }

    public DeviceIdentity ViewerIdentity { get; }

    public PairedMachineStore ViewerMachines { get; }

    public static async Task<NodeFixture> StartAsync(CancellationToken cancellationToken)
    {
        var root = new NodeTestRoot();
        var provider = new ScriptedNodeProvider();
        try
        {
            var node = await NodeHost.StartAsync(root.Options() with { ConfigureModelProviders = provider.Register }, cancellationToken);
            var fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "test-node", "1.0.0");
            return new NodeFixture(root, node, fleet, provider);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public async Task<PairedMachine> PairViewerAsync(CancellationToken cancellationToken)
    {
        var (offer, _) = Fleet.Service.Pairing.CreateOffer("test-node", Fleet.Addresses, TimeSpan.FromMinutes(1));
        return await FleetNodeClient.PairAsync(offer, ViewerIdentity, "viewer", ViewerMachines, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Fleet.DisposeAsync();
        await Node.DisposeAsync();
        NodeIdentity.Dispose();
        ViewerIdentity.Dispose();
        _root.Dispose();
    }
}

/// <summary>Two connected streams over loopback TCP, for a channel served without the listener.</summary>
internal static class FleetStreams
{
    public static (Stream Server, Stream Client) CreatePair()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var client = new System.Net.Sockets.TcpClient();
        client.Connect((System.Net.IPEndPoint)listener.LocalEndpoint);
        var server = listener.AcceptTcpClient();
        return (server.GetStream(), client.GetStream());
    }
}
