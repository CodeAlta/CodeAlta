using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public async Task SessionCreate_OnAnotherMachine_AsksForWhatTheCallerHasAtMost_AndRefusesMore()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var provider = new StatefulProviderRuntime(new ModelProviderId("remote-test-provider")) { Models = [new AgentModelInfo("synthetic-model")] };
        var providerId = provider.ProviderId;
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplaceSessionRuntime(new ModelProviderDescriptor(provider.ProviderId, "Synthetic"), () => provider);
        await using var runtime = CreateRuntime(options, registry);
        var parent = await runtime.CreateGlobalSessionAsync(new SessionExecutionOptions
        {
            ProviderId = providerId, ProviderKey = providerId.Value, WorkingDirectory = root.Path, ProjectRoots = [],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }, "Parent");
        runtime.SetDisabledToolPatterns(parent.SessionId, ["web_fetch"]);
        var remote = new RecordingRemoteChildren();
        runtime.RemoteChildren = remote;
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(runtime).Add(new SessionViewCatalog(options)).Add(new ProjectCatalog(options)));
        var agent = new AltaCallerIdentity { Kind = "agent", SourceSessionId = parent.SessionId };

        var machines = await dispatcher.InvokeAsync(["session", "machines"], caller: agent);
        Assert.AreEqual(AltaExitCodes.Success, machines.ExitCode, machines.Stderr);
        Assert.AreEqual("mac-mini", ReadJsonLines(machines.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.machine").GetProperty("name").GetString());

        var created = await dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--message", "Run the iOS tests."], caller: agent);
        Assert.AreEqual(AltaExitCodes.Success, created.ExitCode, created.Stderr);
        var record = ReadJsonLines(created.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.created");
        Assert.AreEqual(("child-1", "mac-mini", parent.SessionId), (record.GetProperty("sessionId").GetString(), record.GetProperty("machine").GetString(),
            record.GetProperty("parentSessionId").GetString()));
        var request = remote.Created.Single();
        Assert.AreEqual((parent.SessionId, "mac-mini", "Run the iOS tests."), (request.ParentSessionId, request.Machine, request.Prompt));
        CollectionAssert.Contains(request.DisabledTools.ToArray(), "web_fetch", "The child does without what its parent does without.");
        Assert.AreEqual("default", request.SpaceId);
        Assert.IsTrue(request.SubSessions);
        Assert.AreEqual(runtime.GetEffectivePermissionMode(parent.SessionId), request.PermissionMode, "The widest mode the child may run in is the parent's.");
        Assert.IsNull(request.Project, "Without --project, the machine takes the project of the same repository as the parent's.");

        // More than the caller has: a mode that asks less, a sandbox turned off.
        var wider = await dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--message", "x", "--permission-mode", "bypassPermissions"],
            caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = parent.SessionId });
        if (request.PermissionMode != "bypassPermissions")
        {
            Assert.AreEqual(AltaExitCodes.PolicyDenied, wider.ExitCode, wider.Transcript);
            StringAssert.Contains(wider.Transcript, "session.permissionModeDenied");
        }

        var unsandboxed = await dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--message", "x", "--sandbox", "off"], caller: agent);
        Assert.AreNotEqual(AltaExitCodes.Success, unsandboxed.ExitCode, "An agent turns a sandbox on, never off, on another machine either.");
        var noSubSessions = await dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--message", "x", "--no-sub-sessions"], caller: agent);
        Assert.AreEqual(AltaExitCodes.Success, noSubSessions.ExitCode, noSubSessions.Stderr);
        Assert.IsFalse(remote.Created[^1].SubSessions);

        // The machine decides: its refusal is the agent's error.
        remote.Refusal = "agent_sessions_off";
        var off = await dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--message", "x"], caller: agent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, off.ExitCode);
        StringAssert.Contains(off.Transcript, "machine.policyDenied");
        remote.Refusal = null;

        // Only an agent, as the child of its own session, starts one; and nothing of this computer's worktrees or tiers goes there.
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini"], caller: AltaCallerIdentity.Cli)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--worktree"], caller: agent)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--tier", "quick"], caller: agent)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Usage, (await dispatcher.InvokeAsync(["session", "create", "--machine", "mac-mini", "--parent", "someone-else"], caller: agent)).ExitCode);

        // A prompt to a session another machine lists goes there, signed for the caller; it changes nothing of that session.
        var sent = await dispatcher.InvokeAsync(["session", "send", "child-1", "--message", "Now the Android tests."], caller: agent);
        Assert.AreEqual(AltaExitCodes.Success, sent.ExitCode, sent.Stderr);
        Assert.AreEqual(("child-1", "send", "Now the Android tests.", parent.SessionId), (remote.Sent.Single().SessionId, remote.Sent.Single().Kind, remote.Sent.Single().Text,
            remote.Sent.Single().SenderSessionId));
        var queued = await dispatcher.InvokeAsync(["session", "queue", "child-1", "--message", "Then the docs."], caller: agent);
        Assert.AreEqual(AltaExitCodes.Success, queued.ExitCode, queued.Stderr);
        Assert.AreEqual("queue", remote.Sent[^1].Kind);
        var changing = await dispatcher.InvokeAsync(["session", "send", "child-1", "--message", "x", "--model", "other"], caller: agent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, changing.ExitCode, changing.Transcript);
        Assert.AreEqual(2, remote.Sent.Count);

        // Its report, as its machine gives it.
        var report = await dispatcher.InvokeAsync(["session", "report", "child-1"], caller: agent);
        Assert.AreEqual(AltaExitCodes.Success, report.ExitCode, report.Transcript);
        var item = ReadJsonLines(report.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.report.item");
        Assert.AreEqual(("mac-mini", "idle", "The iOS tests pass."), (item.GetProperty("machine").GetString(), item.GetProperty("state").GetString(),
            item.GetProperty("finalAnswer").GetString()));
    }

    private sealed class RecordingRemoteChildren : IRemoteChildSessions
    {
        public List<RemoteChildRequest> Created { get; } = [];

        public List<RemoteMessageRequest> Sent { get; } = [];

        public string? Refusal { get; set; }

        public IReadOnlyList<RemoteMachine> ListMachines() => [new("m1", "mac-mini", "online")];

        public RemoteMachine? FindMachineOf(string sessionId) => sessionId == "child-1" ? new("m1", "mac-mini", "online") : null;

        public Task<RemoteChildResult> CreateAsync(RemoteChildRequest request, CancellationToken cancellationToken)
        {
            if (Refusal is { } code) return Task.FromResult(new RemoteChildResult("refused", code, "The owner did not allow it.") { MachineName = "mac-mini" });
            Created.Add(request);
            return Task.FromResult(new RemoteChildResult("created", null, null)
            {
                SessionId = "child-1", MachineId = "m1", MachineName = "mac-mini", ProviderKey = "codex", PermissionMode = "acceptEdits", PromptSent = request.Prompt is not null,
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
            => Task.FromResult(sessionId == "child-1"
                ? new RemoteSessionReport("child-1", "m1", "mac-mini", "Run the iOS tests", "idle", Created.FirstOrDefault()?.ParentSessionId, "The iOS tests pass.", DateTimeOffset.UtcNow)
                : null);
    }
}
