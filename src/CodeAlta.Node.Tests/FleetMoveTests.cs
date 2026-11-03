using System.ComponentModel;
using System.Diagnostics;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Moves;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Node.Tests;

/// <summary>
/// Sessions moved between two machines over the network (fleet M2): a node (<see cref="NodeHost"/> with its
/// <see cref="NodeFleet"/>) and a viewer with a runtime of its own, as CodeAlta Desktop is, over real TCP on loopback,
/// each with a clone of one origin as its project. The viewer gives a session to the node (it drives the node's target
/// methods), and the node gives one to the viewer (moves.start: the viewer serves the target's methods back on its
/// channel).
/// </summary>
[TestClass]
public sealed class FleetMoveTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ViewerToNode_MovesTheSessionWithItsWork_AndTheNodeRunsIt()
    {
        await using var fixture = await MoveFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        await using var client = await fixture.ConnectAsync(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Move, token);
        var sessionId = await fixture.CreateViewerSessionAsync("Parser work");
        fixture.Write(fixture.ViewerProject, "readme.md", "work in progress\n");
        fixture.Write(fixture.ViewerProject, ".env", "TOKEN=secret\n");

        var preview = await fixture.ViewerSender.PreviewAsync(sessionId, token);
        Assert.AreEqual("ok", preview.Status, preview.Message);
        Assert.IsNull(preview.Busy);
        CollectionAssert.Contains(preview.Secrets.ToList(), ".env");

        var moved = await fixture.ViewerSender.MoveAsync(sessionId, "move-push", client.AsMoveTarget(), "test-node", false, token);

        Assert.AreEqual(SessionMoveState.Finished, moved.Status, $"{moved.Code}: {moved.Message} {string.Join(" ", moved.Warnings)}");
        Assert.AreEqual(1, moved.Epoch);
        Assert.IsTrue(fixture.ViewerHost.RuntimeService.Relinquishments.Read(sessionId)!.IsRelinquished, "The viewer keeps a read-only copy.");
        var folder = await fixture.Node.Host.RuntimeService.GetSessionWorktreeAsync(sessionId);
        Assert.IsNotNull(folder);
        Assert.AreEqual("work in progress\n", File.ReadAllText(Path.Combine(folder, "readme.md")));
        Assert.IsFalse(File.Exists(Path.Combine(folder, ".env")), "A secret stays on the source.");

        // The node runs it now, driven through its API, in its worktree.
        var sent = await client.SendAsync(sessionId, "Go on.", cancellationToken: token);
        Assert.AreEqual("accepted", sent.Status);
        await fixture.WaitIdleOnNodeAsync(client, sessionId, token);
        Assert.AreEqual(folder, fixture.NodeProvider.LastWorkingDirectory);
        StringAssert.Contains(fixture.NodeProvider.LastUserText, "this session moved from viewer");
        var moves = await client.ListMovesAsync(token);
        Assert.AreEqual(SessionMoveState.Finished, moves.Moves.Single(record => record.MoveId == "move-push").State);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Node.Host.CatalogOptions.FleetRoot, "moves", "bundles", "move-push")), "The copies of the bundle go.");
    }

    [TestMethod]
    public async Task NodeToViewer_MovesStart_GivesTheSessionToTheCaller()
    {
        await using var fixture = await MoveFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var created = await fixture.CreateNodeSessionAsync(token);
        fixture.Write(fixture.NodeProject, "src/node.txt", "from the node\n");
        await using var client = await fixture.ConnectAsync(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start | DevicePermissions.Move, token,
            serve: connection => FleetMoveRoutes.Register(connection, (_, via) => via is null
                ? fixture.ViewerReceiver.For(fixture.Fleet.DeviceId)
                : throw new FleetException(FleetErrorCodes.Forbidden, null, "No relay here.")));

        var probe = await client.StartMoveAsync(new FleetMoveStartParams(created, "move-pull", Probe: true), cancellationToken: token);
        Assert.AreEqual("preview", probe.Status);
        Assert.AreEqual("ok", probe.Preview!.Status);
        Assert.IsTrue(probe.Preview.Dirty);

        var moved = await client.StartMoveAsync(new FleetMoveStartParams(created, "move-pull", "viewer"), cancellationToken: token);

        Assert.AreEqual(SessionMoveState.Finished, moved.Status, $"{moved.Code}: {moved.Message} {string.Join(" ", moved.Warnings)}");
        var folder = await fixture.ViewerHost.RuntimeService.GetSessionWorktreeAsync(created);
        Assert.IsNotNull(folder);
        Assert.AreEqual("from the node\n", File.ReadAllText(Path.Combine(folder, "src", "node.txt")));
        Assert.IsTrue(fixture.Node.Host.RuntimeService.Relinquishments.Read(created)!.IsRelinquished);
        var receipt = fixture.Node.Host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), created, "Still there?")).Receipt!;
        Assert.AreEqual(SessionRuntimeService.SessionInUseCode, (await receipt.Completion.WaitAsync(Patience, token)).Code,
            "The node no longer runs a session it gave away.");

        await fixture.SendOnViewerAsync(created, "Here now.");
        Assert.AreEqual(folder, fixture.ViewerProvider.LastWorkingDirectory);
        StringAssert.Contains(fixture.ViewerProvider.LastUserText, "this session moved from test-node");
    }

    [TestMethod]
    public async Task LostConnectionAfterTheCommit_IsRecovered_ByAskingTheArbiter()
    {
        await using var fixture = await MoveFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var sessionId = await fixture.CreateViewerSessionAsync(null);
        fixture.Write(fixture.ViewerProject, "readme.md", "dirty\n");
        FleetMoveResult first;
        await using (var client = await fixture.ConnectAsync(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Move, token))
        {
            // The node commits and installs, then the channel fails before its answer arrives.
            first = await fixture.ViewerSender.MoveAsync(sessionId, "move-lost", new LoseAnswerOfCommit(client.AsMoveTarget()), "test-node", false, token);
        }

        Assert.AreEqual(SessionMoveState.Offered, first.Status, first.Message);
        Assert.AreEqual(SessionRelinquishment.Frozen, fixture.ViewerHost.RuntimeService.Relinquishments.Read(sessionId)!.State,
            "A lost answer never makes the source take the session back.");
        Assert.IsNotNull(await fixture.Node.Host.RuntimeService.GetSessionWorktreeAsync(sessionId), "The node installed the session it committed.");
        var sent = await TrySendOnViewerAsync(fixture, sessionId);
        Assert.AreEqual(SessionRuntimeService.SessionInUseCode, sent.Code, "The frozen copy is not written.");

        await using (var client = await fixture.ConnectAsync(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Move, token))
        {
            var resumed = await fixture.ViewerSender.ResumeAsync(client.AsMoveTarget(), "test-node", token);
            Assert.AreEqual(SessionMoveState.Finished, resumed.Single().Status, resumed.Single().Message);
        }

        Assert.IsTrue(fixture.ViewerHost.RuntimeService.Relinquishments.Read(sessionId)!.IsRelinquished);
        Assert.AreEqual(SessionMoveState.Finished, fixture.ViewerSender.Moves.Store.Read("move-lost", SessionMoveRole.Source)!.State);
    }

    [TestMethod]
    public async Task WithoutTheMovePermission_TheMoveIsRefused_AndNothingIsFrozen()
    {
        await using var fixture = await MoveFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        await using var client = await fixture.ConnectAsync(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start, token,
            serve: connection => FleetMoveRoutes.Register(connection, (_, _) => fixture.ViewerReceiver.For(fixture.Fleet.DeviceId)));
        var sessionId = await fixture.CreateViewerSessionAsync(null);

        var pushed = await fixture.ViewerSender.MoveAsync(sessionId, "move-forbidden", client.AsMoveTarget(), "test-node", false, token);

        Assert.AreEqual(("refused", FleetErrorCodes.Forbidden), (pushed.Status, pushed.Code));
        Assert.IsNull(fixture.ViewerHost.RuntimeService.Relinquishments.Read(sessionId), "Nothing was frozen.");
        Assert.IsNull(fixture.ViewerSender.Moves.Store.Read("move-forbidden", SessionMoveRole.Source));

        var nodeSession = await fixture.CreateNodeSessionAsync(token);
        var pulled = await Assert.ThrowsExactlyAsync<FleetException>(() => client.StartMoveAsync(new FleetMoveStartParams(nodeSession, "move-forbidden-2"),
            cancellationToken: token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, pulled.Error.Code);
        Assert.IsNull(fixture.Node.Host.RuntimeService.Relinquishments.Read(nodeSession));
    }

    [TestMethod]
    public async Task ABusySession_IsRefused_AndKeepsWorking()
    {
        await using var fixture = await MoveFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        await using var client = await fixture.ConnectAsync(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start | DevicePermissions.Move, token,
            serve: connection => FleetMoveRoutes.Register(connection, (_, _) => fixture.ViewerReceiver.For(fixture.Fleet.DeviceId)));
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        fixture.NodeProvider.Turns.Enqueue(ScriptedNodeProvider.WaitThenReply(gate.Task, "Finally.", started));
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(fixture.NodeProjectId, Prompt: "A long turn."), cancellationToken: token);
        await started.Task.WaitAsync(Patience, token);

        var refused = await client.StartMoveAsync(new FleetMoveStartParams(created.SessionId, "move-busy", "viewer"), cancellationToken: token);

        Assert.AreEqual(("refused", "working"), (refused.Status, refused.Code), refused.Message);
        Assert.IsNull(fixture.Node.Host.RuntimeService.Relinquishments.Read(created.SessionId));
        gate.SetResult();
        await fixture.WaitIdleOnNodeAsync(client, created.SessionId, token);
        Assert.IsNull(await fixture.ViewerHost.RuntimeService.TryGetRecoverableSessionAsync(created.SessionId, token), "Nothing reached the viewer.");
    }

    private static async Task<OwnedSessionCommandResult> TrySendOnViewerAsync(MoveFixture fixture, string sessionId)
    {
        var receipt = fixture.ViewerHost.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "Back here?")).Receipt!;
        return await receipt.Completion.WaitAsync(Patience);
    }

    // A target whose commit happens, but whose answer is lost with the channel.
    private sealed class LoseAnswerOfCommit(IFleetMoveTarget inner) : IFleetMoveTarget
    {
        public Task<FleetMoveOfferResult> OfferAsync(FleetMoveOffer offer, CancellationToken cancellationToken) => inner.OfferAsync(offer, cancellationToken);

        public Task<FleetMoveChunkResult> TransferAsync(FleetMoveChunk chunk, CancellationToken cancellationToken) => inner.TransferAsync(chunk, cancellationToken);

        public Task<FleetMoveStepResult> PrepareAsync(FleetMoveRef move, CancellationToken cancellationToken) => inner.PrepareAsync(move, cancellationToken);

        public async Task<FleetMoveStepResult> CommitAsync(FleetMoveRef move, CancellationToken cancellationToken)
        {
            await inner.CommitAsync(move, cancellationToken);
            throw new FleetConnectionClosedException(new FleetConnectionCloseInfo(FleetErrorCodes.ConnectionClosed, "lost", ClosedLocally: false));
        }

        public Task<FleetMoveStatus> StatusAsync(FleetMoveStatusParams parameters, CancellationToken cancellationToken)
            => throw new FleetConnectionClosedException(new FleetConnectionCloseInfo(FleetErrorCodes.ConnectionClosed, "lost", ClosedLocally: false));

        public Task<FleetMoveStepResult> CompleteAsync(FleetMoveRef move, CancellationToken cancellationToken)
            => throw new FleetConnectionClosedException(new FleetConnectionCloseInfo(FleetErrorCodes.ConnectionClosed, "lost", ClosedLocally: false));
    }
}

/// <summary>
/// A node and a viewer with runtimes of their own, each with a clone of one origin as its project, and the viewer's
/// device key; the viewer is paired with the node by the test with the permissions it asks for.
/// </summary>
internal sealed class MoveFixture : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private readonly NodeTestRoot _root;

    private MoveFixture(NodeTestRoot root, NodeHost node, NodeFleet fleet, ScriptedNodeProvider nodeProvider, CodeAltaHost viewer,
        ScriptedNodeProvider viewerProvider, string nodeProject, string viewerProject, string nodeProjectId)
    {
        _root = root;
        Node = node;
        Fleet = fleet;
        NodeProvider = nodeProvider;
        ViewerHost = viewer;
        ViewerProvider = viewerProvider;
        NodeProject = nodeProject;
        ViewerProject = viewerProject;
        NodeProjectId = nodeProjectId;
        var viewerFleet = Path.Combine(root.Root, "viewer", "fleet");
        ViewerIdentity = DeviceIdentity.LoadOrCreate(viewerFleet);
        ViewerMachines = new PairedMachineStore(viewerFleet);
        var moves = new SessionMoveService(viewer, "viewer");
        ViewerReceiver = new FleetMoveReceiver(viewer, moves, null);
        ViewerSender = new FleetMoveSender(viewer, moves);
    }

    public NodeHost Node { get; }

    public NodeFleet Fleet { get; }

    public ScriptedNodeProvider NodeProvider { get; }

    public CodeAltaHost ViewerHost { get; }

    public ScriptedNodeProvider ViewerProvider { get; }

    public string NodeProject { get; }

    public string ViewerProject { get; }

    public string NodeProjectId { get; }

    public DeviceIdentity ViewerIdentity { get; }

    public PairedMachineStore ViewerMachines { get; }

    public FleetMoveReceiver ViewerReceiver { get; }

    public FleetMoveSender ViewerSender { get; }

    public static async Task<MoveFixture> StartAsync(CancellationToken cancellationToken)
    {
        var root = new NodeTestRoot();
        NodeHost? node = null;
        NodeFleet? fleet = null;
        try
        {
            var origin = Path.Combine(root.Root, "origin.git");
            try
            {
                Git(root.Root, "init", "-q", "--bare", "-b", "main", origin);
            }
            catch (Win32Exception)
            {
                Assert.Inconclusive("git is not installed.");
            }

            var viewerProject = Path.Combine(root.Root, "viewer", "project");
            var nodeProject = Path.Combine(root.Root, "node-project");
            Git(root.Root, "clone", "-q", origin, viewerProject);
            Configure(viewerProject);
            File.WriteAllText(Path.Combine(viewerProject, "readme.md"), "one\n");
            Git(viewerProject, "add", "-A");
            Git(viewerProject, "commit", "-q", "-m", "One");
            Git(viewerProject, "push", "-q", "-u", "origin", "main");
            Git(root.Root, "clone", "-q", origin, nodeProject);
            Configure(nodeProject);

            var nodeProvider = new ScriptedNodeProvider();
            node = await NodeHost.StartAsync(root.Options() with { CurrentProjectPath = nodeProject, ConfigureModelProviders = nodeProvider.Register }, cancellationToken);
            var project = await node.Host.ProjectCatalog.EnsurePersistedAsync(node.Host.CurrentProject, cancellationToken);
            fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "test-node", "1.0.0");

            var viewerProvider = new ScriptedNodeProvider();
            var viewer = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = Directory.CreateDirectory(Path.Combine(root.Root, "viewer", "global")).FullName,
                CurrentProjectPath = viewerProject,
                IsHeadless = true,
                AutoApproveOwnedPermissions = true,
                OwnedCommandReceiptCapacity = 32,
                ConfigureModelProviders = viewerProvider.Register,
            }, cancellationToken);
            await viewer.ProjectCatalog.EnsurePersistedAsync(viewer.CurrentProject, cancellationToken);
            return new MoveFixture(root, node, fleet, nodeProvider, viewer, viewerProvider, nodeProject, viewerProject, project.Id);
        }
        catch
        {
            if (fleet is not null) await fleet.DisposeAsync();
            if (node is not null) await node.DisposeAsync();
            root.Dispose();
            throw;
        }
    }

    /// <summary>Pairs the viewer with the node once, gives it the permissions, and connects.</summary>
    public async Task<FleetNodeClient> ConnectAsync(DevicePermissions permissions, CancellationToken cancellationToken, Action<FleetConnection>? serve = null)
    {
        var machine = ViewerMachines.Find("test-node");
        if (machine is null)
        {
            var (offer, _) = Fleet.Service.Pairing.CreateOffer("test-node", Fleet.Addresses, TimeSpan.FromMinutes(1));
            machine = await FleetNodeClient.PairAsync(offer, ViewerIdentity, "viewer", ViewerMachines, cancellationToken);
        }

        await Fleet.Service.SetPermissionsAsync(ViewerIdentity.DeviceId, permissions);
        return await FleetNodeClient.ConnectAsync(machine, ViewerIdentity, serve, cancellationToken);
    }

    public async Task<string> CreateViewerSessionAsync(string? title)
    {
        var session = await ViewerHost.Commands.CreateDraftSessionAsync(ViewerHost.CurrentProject, ViewerProvider.Descriptor, title);
        await SendOnViewerAsync(session.SessionId, "Let us plan.");
        return session.SessionId;
    }

    public async Task SendOnViewerAsync(string sessionId, string prompt)
    {
        var receipt = ViewerHost.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, prompt)).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(Patience);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
    }

    /// <summary>A session of the node's project with one turn done, made through the node's own runtime.</summary>
    public async Task<string> CreateNodeSessionAsync(CancellationToken cancellationToken)
    {
        var project = await Node.Host.ProjectCatalog.GetByIdAsync(NodeProjectId, cancellationToken);
        var session = await Node.Host.Commands.CreateDraftSessionAsync(project, NodeProvider.Descriptor, "Node work");
        var receipt = Node.Host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), session.SessionId, "Start.")).Receipt!;
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await receipt.Completion.WaitAsync(Patience, cancellationToken)).Outcome);
        return session.SessionId;
    }

    public async Task WaitIdleOnNodeAsync(FleetNodeClient client, string sessionId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var listed = await client.ListSessionsAsync(cancellationToken: cancellationToken);
            if (listed.Sessions.FirstOrDefault(session => session.Id == sessionId) is { State: "idle" }
                && (await client.GetSessionStateAsync(sessionId, cancellationToken)).ActiveRunId is null) return;
            if (DateTime.UtcNow > deadline) Assert.Fail("The session never became idle on the node.");
            await Task.Delay(100, cancellationToken);
        }
    }

    public void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public async ValueTask DisposeAsync()
    {
        await ViewerHost.DisposeAsync();
        await Fleet.DisposeAsync();
        await Node.DisposeAsync();
        ViewerIdentity.Dispose();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root.Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        _root.Dispose();
    }

    private static void Configure(string root)
    {
        foreach (var (name, value) in new[] { ("user.name", "Test"), ("user.email", "test@example.invalid"), ("core.autocrlf", "false"), ("commit.gpgsign", "false") })
            Git(root, "config", name, value);
    }

    private static string Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error.Result}");
        return output;
    }
}
