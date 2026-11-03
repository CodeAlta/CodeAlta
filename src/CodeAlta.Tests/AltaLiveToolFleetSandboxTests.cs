using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Sandbox;

namespace CodeAlta.Tests;

// The fleet is never a way out of a sandbox (doc/fleet/security-and-privacy.md, "Remote execution", rule 8): what a
// sandboxed session starts or drives on another machine is at least as confined as itself, and a profile that does not
// allow the fleet refuses it here.
public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public async Task ASandboxedSession_WhoseProfileForbidsTheFleet_StartsAndDrivesNothingElsewhere()
    {
        await using var fleet = await FleetSandboxFixture.CreateAsync("[sandbox]\nall_sessions = \"strict\"\n");

        var created = await fleet.Dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--message", "Run the tests."], caller: fleet.Agent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, created.ExitCode, created.Transcript);
        StringAssert.Contains(created.Transcript, "sandbox.fleetDenied");
        var sent = await fleet.Dispatcher.InvokeAsync(["session", "send", "child-1", "--message", "Go on."], caller: fleet.Agent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, sent.ExitCode, sent.Transcript);
        StringAssert.Contains(sent.Transcript, "sandbox.fleetDenied");
        var machines = await fleet.Dispatcher.InvokeAsync(["session", "machines"], caller: fleet.Agent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, machines.ExitCode, machines.Transcript);
        Assert.AreEqual(0, fleet.Remote.Created.Count + fleet.Remote.Sent.Count, "Nothing reached the machine.");
    }

    [TestMethod]
    public async Task ASandboxedSession_ReachesOnAnotherMachineOnlyTheSessionsItStarted_InTheProjectOfItsRepository()
    {
        await using var fleet = await FleetSandboxFixture.CreateAsync("[sandbox]\nall_sessions = \"workspace\"\n");

        // Its machines are listed, and its child there is reached.
        var machines = await fleet.Dispatcher.InvokeAsync(["session", "machines"], caller: fleet.Agent);
        Assert.AreEqual(AltaExitCodes.Success, machines.ExitCode, machines.Transcript);
        var created = await fleet.Dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--message", "Run the tests."], caller: fleet.Agent);
        Assert.AreEqual(AltaExitCodes.Success, created.ExitCode, created.Transcript);
        // The child goes with what the sandbox confines here, which the machine holds it to: never the name alone.
        var request = fleet.Remote.Created.Single();
        Assert.AreEqual("workspace", request.Sandbox);
        CollectionAssert.AreEqual(SandboxConfinement.Of(SandboxProfiles.Resolve(SandboxProfiles.Workspace, [])).Hosts.ToArray(), request.Confinement!.Hosts.ToArray());
        Assert.IsNull(request.Confinement.WeakerThan(SandboxConfinement.Of(SandboxProfiles.Resolve(SandboxProfiles.Workspace, []))));
        var own = await fleet.Dispatcher.InvokeAsync(["session", "queue", "child-1", "--message", "Then the docs."], caller: fleet.Agent);
        Assert.AreEqual(AltaExitCodes.Success, own.ExitCode, own.Transcript);
        // A prompt goes with the floor of its sender: the machine refuses it for a session less confined.
        Assert.AreEqual(("workspace", request.PermissionMode), (fleet.Remote.Sent.Single().Floor.Sandbox, fleet.Remote.Sent.Single().Floor.PermissionMode));
        // A report reads what other sessions did: a sandboxed session reads none, here or elsewhere.
        var report = await fleet.Dispatcher.InvokeAsync(["session", "report", "child-1"], caller: fleet.Agent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, report.ExitCode, report.Transcript);
        Assert.AreEqual(0, fleet.Remote.Reported.Count);

        // Another session of that machine, which it did not start, is out of its reach, as one of this computer is.
        foreach (var arguments in new[] { new[] { "session", "send", "stranger-1", "--message", "x" }, ["session", "queue", "stranger-1", "--message", "x"], ["session", "steer", "stranger-1", "--message", "x"] })
        {
            var refused = await fleet.Dispatcher.InvokeAsync(arguments, caller: fleet.Agent);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, refused.ExitCode, string.Join(' ', arguments) + ": " + refused.Transcript);
            StringAssert.Contains(refused.Transcript, "sandbox.notOwnSession");
        }

        Assert.AreEqual(1, fleet.Remote.Sent.Count, "Only the prompt to its own child went out.");

        // It works in the folder of its project here: elsewhere, in the project of the same repository only.
        foreach (var where in new[] { new[] { "--project", "other" }, ["--global"] })
        {
            var refused = await fleet.Dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--message", "x", .. where], caller: fleet.Agent);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, refused.ExitCode, refused.Transcript);
            StringAssert.Contains(refused.Transcript, "sandbox.projectDenied");
        }

        Assert.AreEqual(1, fleet.Remote.Created.Count);
    }

    [TestMethod]
    public async Task ASessionWithoutSandbox_ReachesTheSessionsOfItsSpaceElsewhere_AsBefore()
    {
        await using var fleet = await FleetSandboxFixture.CreateAsync(null);

        var sent = await fleet.Dispatcher.InvokeAsync(["session", "send", "stranger-1", "--message", "Hello."], caller: fleet.Agent);
        Assert.AreEqual(AltaExitCodes.Success, sent.ExitCode, sent.Transcript);
        var created = await fleet.Dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--message", "x", "--project", "other"], caller: fleet.Agent);
        Assert.AreEqual(AltaExitCodes.Success, created.ExitCode, created.Transcript);
        Assert.IsNull(fleet.Remote.Created.Single().Sandbox);
        Assert.IsNull(fleet.Remote.Sent.Single().Floor.Sandbox);

        // It turns one on for its child, which goes with what that profile confines here.
        var sandboxed = await fleet.Dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--message", "x", "--sandbox", "strict"], caller: fleet.Agent);
        Assert.AreEqual(AltaExitCodes.Success, sandboxed.ExitCode, sandboxed.Transcript);
        Assert.AreEqual(("strict", SandboxNetwork.Off), (fleet.Remote.Created[^1].Sandbox, fleet.Remote.Created[^1].Confinement!.Network));
    }

    [TestMethod]
    public void ASandboxedSession_NeverMovesASession_NorPlacesOrRunsAnAutomation()
    {
        // These run here, outside its sandbox, and start work on a machine that its sandbox does not follow there.
        foreach (var command in new[] { "alta session move", "alta automation create", "alta automation run", "alta automation enable", "alta automation update", "alta fleet" })
            Assert.IsFalse(AltaSandboxGate.Allows(command), command);
    }

    // A parent session, the dispatcher of its agent, and a machine that lists its child and a session it did not start.
    private sealed class FleetSandboxFixture : IAsyncDisposable
    {
        private readonly TempDirectory _root;

        private FleetSandboxFixture(TempDirectory root, SessionRuntimeService runtime, AltaCommandDispatcher dispatcher, LineageRemoteChildren remote, AltaCallerIdentity agent)
        {
            _root = root;
            Runtime = runtime;
            Dispatcher = dispatcher;
            Remote = remote;
            Agent = agent;
        }

        public SessionRuntimeService Runtime { get; }

        public AltaCommandDispatcher Dispatcher { get; }

        public LineageRemoteChildren Remote { get; }

        public AltaCallerIdentity Agent { get; }

        public static async Task<FleetSandboxFixture> CreateAsync(string? config)
        {
            var root = TempDirectory.Create();
            var options = new CatalogOptions { GlobalRoot = root.Path };
            if (config is not null) File.WriteAllText(Path.Combine(root.Path, "config.toml"), config);
            var provider = new StatefulProviderRuntime(new ModelProviderId("fleet-sandbox-provider")) { Models = [new AgentModelInfo("synthetic-model")] };
            var registry = new ModelProviderRegistry();
            registry.RegisterOrReplaceSessionRuntime(new ModelProviderDescriptor(provider.ProviderId, "Synthetic"), () => provider);
            var runtime = CreateRuntime(options, registry);
            var parent = await runtime.CreateGlobalSessionAsync(new SessionExecutionOptions
            {
                ProviderId = provider.ProviderId, ProviderKey = provider.ProviderId.Value, WorkingDirectory = root.Path, ProjectRoots = [],
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }, "Parent");
            var remote = new LineageRemoteChildren(parent.SessionId);
            runtime.RemoteChildren = remote;
            var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(runtime).Add(new SessionViewCatalog(options)).Add(new ProjectCatalog(options)));
            return new FleetSandboxFixture(root, runtime, dispatcher, remote, new AltaCallerIdentity { Kind = "agent", SourceSessionId = parent.SessionId });
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            _root.Dispose();
        }
    }

    // A machine that lists `child-1`, started by the parent, and `stranger-1`, started by a session of another machine.
    private sealed class LineageRemoteChildren(string parentSessionId) : IRemoteChildSessions
    {
        public List<RemoteChildRequest> Created { get; } = [];

        public List<RemoteMessageRequest> Sent { get; } = [];

        public List<string> Reported { get; } = [];

        public IReadOnlyList<RemoteMachine> ListMachines() => [new("m1", "mac-mini", "online")];

        public RemoteMachine? FindMachineOf(string sessionId) => sessionId is "child-1" or "stranger-1" ? new("m1", "mac-mini", "online") : null;

        public string? FindParentOf(string childSessionId) => childSessionId == "child-1" ? parentSessionId : null;

        public Task<RemoteChildResult> CreateAsync(RemoteChildRequest request, CancellationToken cancellationToken)
        {
            Created.Add(request);
            return Task.FromResult(new RemoteChildResult("created", null, null)
            {
                SessionId = "child-1", MachineId = "m1", MachineName = "mac-mini", ProviderKey = "codex", Sandbox = request.Sandbox, PromptSent = request.Prompt is not null,
            });
        }

        public Task<RemoteChildAnswer> WaitForAnswerAsync(string childSessionId, TimeSpan? timeout, CancellationToken cancellationToken)
            => Task.FromResult(new RemoteChildAnswer("completed", "Done.", null));

        public Task<RemoteMessageResult> SendAsync(RemoteMessageRequest request, CancellationToken cancellationToken)
        {
            Sent.Add(request);
            return Task.FromResult(new RemoteMessageResult(request.Kind == "queue" ? "queued" : "accepted", null, null, "mac-mini"));
        }

        public Task<RemoteSessionReport?> ReportAsync(string sessionId, CancellationToken cancellationToken)
        {
            Reported.Add(sessionId);
            return Task.FromResult<RemoteSessionReport?>(new RemoteSessionReport(sessionId, "m1", "mac-mini", "Work", "idle",
                sessionId == "child-1" ? parentSessionId : "elsewhere", "Done.", DateTimeOffset.UtcNow));
        }
    }
}
