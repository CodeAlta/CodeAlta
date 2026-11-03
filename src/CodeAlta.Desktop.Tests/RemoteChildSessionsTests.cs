using System.Collections.Frozen;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Scripts;
using CodeAlta.Sandbox;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Sessions agents start on other machines (<c>doc/fleet/sessions-across-machines.md</c>), with two real hosts in this
/// process: the host of this computer, whose parent session runs on a scripted provider, and a node served on loopback,
/// paired with it. No paid model is called.
/// </summary>
[TestClass]
public sealed class RemoteChildSessionsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly RemoteSessionHostOptions Quick = new()
    {
        MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromMilliseconds(200), ListInterval = TimeSpan.FromMilliseconds(200),
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AParentStartsAChildOnAnotherMachine_WhenItAllowsIt_AndGetsItsFinalAnswerBack_ThenSendsItAnotherPrompt()
    {
        var token = TestContext.CancellationToken;
        await using var two = await TwoHosts.StartAsync(token);
        var parent = await two.CreateParentAsync(token);

        // Off until the owner of the machine allows it, in that space.
        var refused = await two.Children.CreateAsync(Request(parent.SessionId, "Run the iOS tests."), token);
        Assert.AreEqual(("refused", "agent_sessions_off"), (refused.Status, refused.Code), refused.Message);
        StringAssert.Contains(refused.Message, "altanode agents allow --space default");
        Assert.AreEqual(0, two.Node.Service.AgentSessions.Children.Count, "Nothing was started.");

        two.Node.Service.AgentSessions.WritePolicy(new FleetAgentPolicyInfo(["default"]));
        two.Node.Provider.Turns.Enqueue((_, _) => Task.FromResult(ScriptedFleetProvider.Answer("The iOS tests pass.")));
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        two.ParentProvider.Turns.Enqueue((request, _) =>
        {
            delivered.TrySetResult(LastUserText(request));
            return Task.FromResult(ScriptedFleetProvider.Answer("Thanks."));
        });

        var created = await two.Children.CreateAsync(Request(parent.SessionId, "Run the iOS tests.") with { DisabledTools = ["web_fetch"] }, token);
        Assert.AreEqual("created", created.Status, created.Message);
        Assert.AreEqual("test-node", created.MachineName);
        Assert.IsTrue(created.PromptSent);
        var childId = created.SessionId!;

        // The lineage, on both sides: the node knows the parent and its machine, this computer knows the child and its machine.
        var lineage = two.Node.Service.AgentSessions.Find(childId)!;
        Assert.AreEqual((parent.SessionId, two.Fleet.DeviceId, "desktop-pc"), (lineage.ParentSessionId, lineage.ParentDeviceId, lineage.ParentMachineName));
        CollectionAssert.AreEqual(new[] { "web_fetch" }, lineage.DisabledTools.ToArray());
        CollectionAssert.AreEqual(new[] { "web_fetch" }, two.Node.Host.RuntimeService.GetDisabledToolPatterns(childId).ToArray(), "The child does without what its parent does without.");
        var stored = await two.Node.Host.RuntimeService.TryGetRecoverableSessionAsync(childId, token);
        Assert.AreEqual(parent.SessionId, stored!.ParentSessionId);
        Assert.AreEqual("desktop-pc", stored.CreatedBy?.SourceMachineName);
        Assert.AreEqual(childId, two.Children.Children.Single().ChildSessionId);
        await WaitForAsync(() => two.Machine.Listing.Sessions.Any(session => session.Id == childId && session.ParentMachineId == two.Fleet.DeviceId
            && session.ParentSessionId == parent.SessionId), "the machine to list the child with its parent and the parent's machine", token);

        // Its final answer comes back as the answer of a child of this computer: a delegated-agent message to the parent.
        var message = await delivered.Task.WaitAsync(Patience, token);
        StringAssert.Contains(message, "[CodeAlta delegated-agent message]");
        StringAssert.Contains(message, $"Source session: {childId}");
        StringAssert.Contains(message, "Source machine: test-node");
        StringAssert.Contains(message, "The iOS tests pass.");
        await WaitForAsync(() => two.Node.Service.AgentSessions.Take(two.Fleet.DeviceId, 10).Count == 0, "the delivery to be acknowledged", token);

        // The window shows it as a child of its parent, with its machine: in the list of sessions and in the sub-agents of the parent.
        var epoch = Guid.NewGuid().ToString("D");
        var workspace = new CodeAlta.Desktop.Rpc.WorkspaceService(two.Registry, two.Desktop, epoch);
        var row = (await workspace.SnapshotAsync(new(), token)).Sessions.Single(session => session.Id == childId);
        Assert.AreEqual((parent.SessionId, two.Machine.MachineId, "test-node"), (row.ParentSessionId, row.MachineId, row.MachineName));
        var tree = await workspace.SessionChildrenAsync(new(epoch, parent.SessionId), token);
        Assert.AreEqual("ok", tree.Status);
        var child = tree.Children.Single();
        Assert.AreEqual((childId, parent.SessionId, 1, "test-node"), (child.Session.Id, child.ParentSessionId, child.Depth, child.Session.MachineName));
        CollectionAssert.DoesNotContain(tree.DescendantIds.ToArray(), childId, "A session of another machine is never deleted with the tree of this computer.");

        // A message across machines: the parent sends its child another prompt, and gets that answer too.
        await WaitForAsync(() => two.Machine.Listing.Sessions.Any(session => session.Id == childId && session.State == "idle"), "the child to be idle", token);
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        two.Node.Provider.Turns.Enqueue((_, _) => Task.FromResult(ScriptedFleetProvider.Answer("The Android tests pass too.")));
        two.ParentProvider.Turns.Enqueue((request, _) =>
        {
            second.TrySetResult(LastUserText(request));
            return Task.FromResult(ScriptedFleetProvider.Answer("Good."));
        });
        var sent = await two.Children.SendAsync(new RemoteMessageRequest(parent.SessionId, childId, "Now the Android tests.", "send")
            { SpaceId = "default", Floor = await two.Desktop.RuntimeService.GetFleetFloorAsync(parent.SessionId, token) }, token);
        Assert.AreEqual("accepted", sent.Status, sent.Message);
        StringAssert.Contains(await second.Task.WaitAsync(Patience, token), "The Android tests pass too.");
        var report = await two.Children.ReportAsync(childId, token);
        Assert.AreEqual(("test-node", parent.SessionId), (report!.MachineName, report.ParentSessionId));
    }

    [TestMethod]
    public async Task AScriptStartsAnAgentOnAnotherMachine_AndGetsItsAnswer()
    {
        var token = TestContext.CancellationToken;
        await using var two = await TwoHosts.StartAsync(token);
        var parent = await two.CreateParentAsync(token);
        two.Node.Service.AgentSessions.WritePolicy(new FleetAgentPolicyInfo(["default"]));
        two.Node.Provider.Turns.Enqueue((_, _) => Task.FromResult(ScriptedFleetProvider.Answer("42 tests, all green.")));

        var parentPrompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        two.ParentProvider.Turns.Enqueue((_, _) =>
        {
            parentPrompted.TrySetResult();
            return Task.FromResult(ScriptedFleetProvider.Answer("Unexpected."));
        });

        var agents = new CodeModeAgents(two.Desktop);
        await using var _ = agents;
        var result = await agents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Count the tests.") { RunId = "run-1", Machine = "test-node", Timeout = Patience }, token);
        Assert.IsTrue(result.Succeeded, result.Failure);
        Assert.AreEqual("42 tests, all green.", result.Answer);
        Assert.IsTrue(two.Children.Children.Single().ForScript);
        await Task.Delay(500, token);
        Assert.IsFalse(parentPrompted.Task.IsCompleted, "The answer goes to the script, never to the parent as a prompt.");

        var kept = await agents.OpenAsync(parent.SessionId, new CodeModeAgentRequest("") { RunId = "run-1", Machine = "test-node" }, token);
        Assert.AreEqual("refused", kept.Status, "session() keeps sessions of this machine only.");
    }

    [TestMethod]
    public async Task TheNodeRefusesUnsignedRequests_ReplaysAndWhatItCannotHold_AndNeverGivesMoreThanItsPolicy()
    {
        var token = TestContext.CancellationToken;
        await using var two = await TwoHosts.StartAsync(token);
        two.Node.Service.AgentSessions.WritePolicy(new FleetAgentPolicyInfo(["default"]));
        var client = await two.Machine.WaitOnlineAsync(token);
        var nodeId = two.Node.NodeIdentity.DeviceId;
        using var stranger = DeviceIdentity.LoadOrCreate(Path.Combine(two.Node.Root, "stranger"));

        FleetSignedAgentRequest Sign(DeviceIdentity identity, FleetAgentLimits limits, string? prompt = "Hello", string space = "default")
            => FleetAgentSigning.Sign(identity, new FleetAgentRequest(FleetAgentRequest.CurrentVersion, FleetAgentRequestKinds.Start, nodeId, "", "", DateTimeOffset.UtcNow,
                "", "parent-x", "desktop-pc", space, null, null, prompt is null ? null : FleetAgentRequestDocuments.HashText(prompt), limits));
        var limits = new FleetAgentLimits([], SessionPermissionModes.Bypass, null, true, 1);
        async Task<string> RefusedAsync(FleetCreateSessionParams parameters)
        {
            var exception = await Assert.ThrowsExactlyAsync<FleetException>(() => client.CreateSessionAsync(parameters, Guid.NewGuid().ToString("N"), token));
            return exception.Error.Code;
        }

        // Signed by another key than the one of the channel (a server, or anyone who is not the person's device).
        Assert.AreEqual(FleetNodeErrorCodes.AgentRequestInvalid, await RefusedAsync(new FleetCreateSessionParams(SpaceId: "default", Prompt: "Hello") { Agent = Sign(stranger, limits) }));
        // A prompt, a space or a project other than the ones signed.
        var signed = Sign(two.Fleet.DeviceKey, limits);
        Assert.AreEqual(FleetNodeErrorCodes.AgentRequestInvalid, await RefusedAsync(new FleetCreateSessionParams(SpaceId: "default", Prompt: "rm -rf /") { Agent = signed }));
        // A space the owner did not allow.
        Assert.AreEqual(FleetNodeErrorCodes.AgentSessionsOff,
            await RefusedAsync(new FleetCreateSessionParams(SpaceId: "work", Prompt: "Hello") { Agent = Sign(two.Fleet.DeviceKey, limits, space: "work") }));
        // A sandbox the node does not have: it does not run the session less sandboxed than its parent.
        Assert.AreEqual(FleetNodeErrorCodes.AgentRequestDenied, await RefusedAsync(new FleetCreateSessionParams(SpaceId: "default", Prompt: "Hello")
            { Agent = Sign(two.Fleet.DeviceKey, limits with { Sandbox = "no-such-profile", Confinement = Workspace with { Profile = "no-such-profile" } }) }));

        // Within the rules: a parent that bypasses permissions gives a child that does not, unless the owner allows it.
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(SpaceId: "default", Prompt: "Hello") { Agent = signed }, "start-1", token);
        Assert.AreNotEqual(SessionPermissionModes.Bypass, created.PermissionMode);
        Assert.AreEqual(created.SessionId, (await client.CreateSessionAsync(new FleetCreateSessionParams(SpaceId: "default", Prompt: "Hello") { Agent = signed }, "start-1", token)).SessionId,
            "A retry of the same key is the same session.");
        Assert.AreEqual(FleetNodeErrorCodes.AgentRequestInvalid, await RefusedAsync(new FleetCreateSessionParams(SpaceId: "default", Prompt: "Hello") { Agent = signed }),
            "The same signed request with another key is a replay.");

        // A prompt of an agent changes no model, and reaches only its own space.
        var send = FleetAgentSigning.Sign(two.Fleet.DeviceKey, new FleetAgentRequest(FleetAgentRequest.CurrentVersion, FleetAgentRequestKinds.Send, nodeId, "", "",
            DateTimeOffset.UtcNow, "", "parent-x", "desktop-pc", "default", created.SessionId, null, FleetAgentRequestDocuments.HashText("Again"), null,
            new FleetAgentFloor(SessionPermissionModes.Bypass, null, null)));
        var selection = await Assert.ThrowsExactlyAsync<FleetException>(() => client.SendAsync(new FleetSendParams(created.SessionId, "Again", new FleetModelSelection(ModelId: "x")) { Agent = send },
            Guid.NewGuid().ToString("N"), token));
        Assert.AreEqual(FleetNodeErrorCodes.AgentRequestDenied, selection.Error.Code);
    }

    // The fleet is never a way out of a sandbox (doc/fleet/security-and-privacy.md, "Remote execution", rule 8): the node
    // holds the child of a sandboxed parent to what the parent's profile confines on its own machine, not to a name.
    [TestMethod]
    public async Task TheNodeRunsTheChildOfASandboxedParent_AtLeastAsConfined_OrRefusesIt()
    {
        var token = TestContext.CancellationToken;
        await using var two = await TwoHosts.StartAsync(token);
        two.Node.Service.AgentSessions.WritePolicy(new FleetAgentPolicyInfo(["default"]));
        var client = await two.Machine.WaitOnlineAsync(token);
        var config = Path.Combine(two.Node.Root, "global", "config.toml");
        var available = two.Node.Host.RuntimeService.GetSandboxAvailability().IsAvailable;
        FleetCreateSessionParams Start(string sandbox, FleetSandboxConfinement confinement)
            => new(SpaceId: "default", Prompt: "Hello")
            {
                Agent = FleetAgentSigning.Sign(two.Fleet.DeviceKey, new FleetAgentRequest(FleetAgentRequest.CurrentVersion, FleetAgentRequestKinds.Start,
                    two.Node.NodeIdentity.DeviceId, "", "", DateTimeOffset.UtcNow, "", "parent-x", "desktop-pc", "default", null, null, FleetAgentRequestDocuments.HashText("Hello"),
                    new FleetAgentLimits([], SessionPermissionModes.Ask, sandbox, true, 1, confinement))),
            };
        async Task<string> RefusedAsync(FleetCreateSessionParams parameters)
        {
            var exception = await Assert.ThrowsExactlyAsync<FleetException>(() => client.CreateSessionAsync(parameters, Guid.NewGuid().ToString("N"), token));
            Assert.AreEqual(FleetNodeErrorCodes.AgentRequestDenied, exception.Error.Code, exception.Error.Message);
            return exception.Error.Message ?? "";
        }

        // The workspace of the node reaches the whole internet: the same name, but it would let the child do more.
        File.WriteAllText(config, "[sandbox.profiles.workspace]\nnetwork = \"on\"\n");
        StringAssert.Contains(await RefusedAsync(Start("workspace", Workspace)), "lets the session do more than its parent");
        Assert.AreEqual(0, two.Node.Service.AgentSessions.Children.Count, "Nothing was started.");

        // A parent that does not let its sessions reach other machines never asks; a floor that says so is held too.
        File.WriteAllText(config, "");
        StringAssert.Contains(await RefusedAsync(Start("workspace", Workspace with { Fleet = false })), "other machines");

        // As strict as the parent's, or the stricter one the owner requires: the child runs in it, or nowhere.
        if (!available)
        {
            StringAssert.Contains(await RefusedAsync(Start("workspace", Workspace)), "cannot start a sandbox");
            return;
        }

        var same = await client.CreateSessionAsync(Start("workspace", Workspace), Guid.NewGuid().ToString("N"), token);
        Assert.AreEqual("workspace", same.Sandbox);
        Assert.AreEqual("workspace", (await two.Node.Host.RuntimeService.GetSessionSandboxAsync(same.SessionId, token)).Choice.Profile);
        two.Node.Service.AgentSessions.WritePolicy(new FleetAgentPolicyInfo(["default"], SandboxProfiles.Strict));
        var stricter = await client.CreateSessionAsync(Start("workspace", Workspace), Guid.NewGuid().ToString("N"), token);
        Assert.AreEqual(SandboxProfiles.Strict, stricter.Sandbox, "the policy of the owner is stricter than the parent: it wins");
        Assert.AreEqual(SandboxProfiles.Strict, (await two.Node.Host.RuntimeService.GetSessionSandboxAsync(stricter.SessionId, token)).Choice.Profile);
    }

    [TestMethod]
    public async Task AnAgentDrivesOnAnotherMachine_NoSessionLessConfinedThanItself()
    {
        var token = TestContext.CancellationToken;
        await using var two = await TwoHosts.StartAsync(token);
        two.Node.Service.AgentSessions.WritePolicy(new FleetAgentPolicyInfo(["default"]));
        var client = await two.Machine.WaitOnlineAsync(token);
        var peer = await two.Node.Host.Commands.CreateDraftSessionAsync(null, two.Node.Host.ModelProviderRegistry.ListProviders().Single(static provider => provider.IsEnabled), "Peer");
        var peerMode = await two.Node.Host.RuntimeService.GetPermissionModeAsync(peer.SessionId, token);
        FleetSendParams Send(FleetAgentFloor floor)
            => new(peer.SessionId, "Hello")
            {
                Agent = FleetAgentSigning.Sign(two.Fleet.DeviceKey, new FleetAgentRequest(FleetAgentRequest.CurrentVersion, FleetAgentRequestKinds.Send,
                    two.Node.NodeIdentity.DeviceId, "", "", DateTimeOffset.UtcNow, "", "parent-x", "desktop-pc", "default", peer.SessionId, null,
                    FleetAgentRequestDocuments.HashText("Hello"), null, floor)),
            };
        async Task<string> RefusedAsync(FleetSendParams parameters)
        {
            var exception = await Assert.ThrowsExactlyAsync<FleetException>(() => client.SendAsync(parameters, Guid.NewGuid().ToString("N"), token));
            Assert.AreEqual(FleetNodeErrorCodes.AgentRequestDenied, exception.Error.Code, exception.Error.Message);
            return exception.Error.Message ?? "";
        }

        // A sandboxed agent does not drive a session without a sandbox, nor an agent that asks more a session that asks less.
        StringAssert.Contains(await RefusedAsync(Send(new FleetAgentFloor(SessionPermissionModes.Bypass, "workspace", Workspace))), "runs without a sandbox");
        StringAssert.Contains(await RefusedAsync(Send(new FleetAgentFloor("plan", null, null))), $"'{peerMode}'");

        // An agent that runs without a sandbox, and asks no more than the session, drives it as before (a session that
        // bypasses permissions holds the prompt of an agent for a person, whatever its floor).
        if (SessionPermissionModes.Rank(peerMode) >= SessionPermissionModes.Rank(SessionPermissionModes.Bypass)) return;
        var accepted = await client.SendAsync(Send(new FleetAgentFloor(SessionPermissionModes.Bypass, null, null)), Guid.NewGuid().ToString("N"), token);
        Assert.AreEqual("accepted", accepted.Status);
    }

    private static FleetSandboxConfinement Workspace => FleetSandboxFloors.ToWire(SandboxConfinement.Of(SandboxProfiles.Resolve(SandboxProfiles.Workspace, [])));

    private static RemoteChildRequest Request(string parentId, string prompt)
        => new(parentId, "test-node") { Prompt = prompt, SpaceId = "default", PermissionMode = SessionPermissionModes.Ask };

    private static string LastUserText(AgentTurnRequest request)
        => string.Join("\n", request.Conversation.Where(static message => message.Role == AgentConversationRole.User)
            .SelectMany(static message => message.Parts.OfType<AgentMessagePart.Text>()).Select(static part => part.Value));

    private static async Task WaitForAsync(Func<bool> condition, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(50, cancellationToken);
        }
    }

    // This computer (a host with a scripted provider, its fleet paired with the node, its remote children) and the node.
    private sealed class TwoHosts : IAsyncDisposable
    {
        private TwoHosts(FleetNodeFixture node, CodeAltaHost desktop, ScriptedFleetProvider parentProvider, DesktopFleet fleet, RemoteSessionHost machine,
            DesktopRemoteChildren children)
        {
            (Node, Desktop, ParentProvider, Fleet, Machine, Children) = (node, desktop, parentProvider, fleet, machine, children);
        }

        public FleetNodeFixture Node { get; }

        public CodeAltaHost Desktop { get; }

        public ScriptedFleetProvider ParentProvider { get; }

        public DesktopFleet Fleet { get; }

        public RemoteSessionHost Machine { get; }

        public DesktopRemoteChildren Children { get; }

        public required SessionHostRegistry Registry { get; init; }

        public static async Task<TwoHosts> StartAsync(CancellationToken cancellationToken)
        {
            var node = await FleetNodeFixture.StartAsync(cancellationToken);
            var root = Path.Combine(node.Root, "desktop");
            var global = Path.Combine(root, "global");
            var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, home, builtin }) Directory.CreateDirectory(path);
            var provider = new ScriptedFleetProvider();
            var desktop = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = root, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, OwnsLogging = false, IsHeadless = true,
                ConfigureModelProviders = provider.Register,
            }, cancellationToken);
            var registry = new SessionHostRegistry(new LocalSessionHost(desktop));
            var fleet = new DesktopFleet(Path.Combine(root, "fleet"), registry.TakeMembership(), "desktop-pc",
                static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick,
                    localDeviceId: identity.DeviceId));
            var paired = await fleet.PairAsync(node.CreatePairingLink(), cancellationToken);
            var machine = fleet.Find(paired.DeviceId)!;
            var children = new DesktopRemoteChildren(desktop, fleet, "desktop-pc", interval: TimeSpan.FromMilliseconds(100));
            desktop.RuntimeService.RemoteChildren = children;
            children.Start();
            var two = new TwoHosts(node, desktop, provider, fleet, machine, children) { Registry = registry };
            await WaitForAsync(() => machine.State == RemoteMachineState.Online, "the machine to be online", cancellationToken);
            return two;
        }

        public Task<SessionViewDescriptor> CreateParentAsync(CancellationToken cancellationToken)
            => Desktop.Commands.CreateDraftSessionAsync(null, Desktop.ModelProviderRegistry.ListProviders().Single(static provider => provider.IsEnabled), "Parent");

        public async ValueTask DisposeAsync()
        {
            await Children.DisposeAsync();
            await Fleet.DisposeAsync();
            await Desktop.DisposeAsync();
            await Node.DisposeAsync();
        }
    }
}
