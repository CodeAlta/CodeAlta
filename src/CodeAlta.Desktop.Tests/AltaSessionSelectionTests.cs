using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// What `alta session choices` lists and what `alta session send` and `alta session queue` choose with a prompt: the
/// model, the effort, the agent prompt and the permission mode the composer of the window chooses, from the same host code.
/// </summary>
[TestClass]
public sealed class AltaSessionSelectionTests
{
    [TestMethod]
    public async Task Choices_AreTheOnesOfTheComposer_AndASendChoosesModelEffortAndMode()
    {
        using var temp = new TempFolder();
        var provider = new GatedProvider();
        await using var host = await CreateHostAsync(temp, provider, inherit: () => false);
        var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()));
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Phone");

        var listed = await commands.InvokeAsync(["session", "choices", session.SessionId], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, listed.ExitCode, listed.Transcript);
        using (var choices = Record(listed, "alta.session.choices"))
        {
            var root = choices.RootElement;
            Assert.AreEqual(session.SessionId, root.GetProperty("sessionId").GetString());
            var current = root.GetProperty("current");
            Assert.AreEqual("gated", current.GetProperty("providerKey").GetString());
            Assert.AreEqual("small", current.GetProperty("modelId").GetString());
            Assert.AreEqual("high", current.GetProperty("reasoningEffort").GetString());
            Assert.AreEqual("default", current.GetProperty("agentPromptId").GetString());
            // No mode of its own: alta writes no null field.
            Assert.IsFalse(current.TryGetProperty("permissionMode", out _));
            Assert.AreEqual("default", root.GetProperty("defaultPermissionMode").GetString());
            var models = root.GetProperty("models").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new[] { "small", "large" }, models.Select(static model => model.GetProperty("id").GetString()).ToArray());
            Assert.AreEqual("Large Model", models[1].GetProperty("name").GetString());
            CollectionAssert.AreEqual(new[] { "medium", "xhigh" }, models[1].GetProperty("efforts").EnumerateArray().Select(static effort => effort.GetString()).ToArray());
            Assert.AreEqual("medium", models[1].GetProperty("startEffort").GetString());
            Assert.IsTrue(root.GetProperty("prompts").EnumerateArray().Any(static prompt => prompt.GetProperty("id").GetString() == "default"));
            var modes = root.GetProperty("permissionModes").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new[] { "default", "acceptEdits", "bypassPermissions" }, modes.Select(static mode => mode.GetProperty("id").GetString()).ToArray());
            Assert.AreEqual("Accept edits", modes[1].GetProperty("name").GetString());
            Assert.AreEqual("Accept file edits; ask before commands.", modes[1].GetProperty("description").GetString());
        }

        // A new session is given the same modes: `provider list --detailed` lists them for each provider.
        var providers = await commands.InvokeAsync(["provider", "list", "--detailed"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, providers.ExitCode, providers.Transcript);
        using (var gated = JsonDocument.Parse(providers.Transcript.Split('\n').First(line => line.Contains("\"alta.provider.item\"", StringComparison.Ordinal) && line.Contains("\"providerKey\":\"gated\"", StringComparison.Ordinal))))
        {
            var modes = gated.RootElement.GetProperty("permissionModes").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new[] { "default", "acceptEdits", "bypassPermissions" }, modes.Select(static mode => mode.GetProperty("id").GetString()).ToArray());
            Assert.AreEqual("Accept edits", modes[1].GetProperty("name").GetString());
        }

        // The turn runs with what the prompt chooses, and the session keeps it.
        var sent = await commands.InvokeAsync(["session", "send", session.SessionId, "--message", "hello", "--model", "large", "--reasoning", "xhigh", "--permission-mode", "acceptEdits"],
            caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, sent.ExitCode, sent.Transcript);
        await WaitIdleAsync(host, session, provider, turns: 1);
        Assert.AreEqual(("large", AgentReasoningEffort.XHigh), provider.Turns[0]);
        var kept = (await host.Commands.GetSelectionChoicesAsync(session.SessionId))!.Current;
        Assert.AreEqual("large", kept.ModelId);
        Assert.AreEqual(AgentReasoningEffort.XHigh, kept.ReasoningEffort);
        Assert.AreEqual(SessionPermissionModes.AcceptEdits, kept.PermissionMode);
        Assert.AreEqual(SessionPermissionPolicy.AcceptEdits, host.RuntimeService.GetPermissionPolicy(session.SessionId));

        // What the provider does not offer is refused, and nothing is sent.
        async Task RefusedAsync(string code, string expected, params string[] selection)
        {
            var refused = await commands.InvokeAsync(["session", "send", session.SessionId, "--message", "again", .. selection], caller: AltaCallerIdentity.Cli);
            Assert.AreNotEqual(AltaExitCodes.Success, refused.ExitCode, refused.Transcript);
            StringAssert.Contains(refused.Transcript, code);
            StringAssert.Contains(refused.Transcript, expected);
            Assert.AreEqual(1, provider.Turns.Count, refused.Transcript);
        }

        await RefusedAsync("usage.invalidModel", "small, large", "--model", "missing");
        await RefusedAsync("usage.invalidReasoning", "medium, xhigh", "--reasoning", "low");
        await RefusedAsync("usage.invalidPermissionMode", "default, acceptEdits, bypassPermissions", "--permission-mode", "plan");
        Assert.AreEqual(SessionPermissionModes.AcceptEdits, (await host.Commands.GetSelectionChoicesAsync(session.SessionId))!.Current.PermissionMode);

        // A model named alone keeps the effort of the session when it has it, else takes the one it starts with;
        // `provider` takes the session back to the mode of its provider.
        var back = await commands.InvokeAsync(["session", "send", session.SessionId, "--message", "small", "--model", "small", "--permission-mode", "provider"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, back.ExitCode, back.Transcript);
        await WaitIdleAsync(host, session, provider, turns: 2);
        Assert.AreEqual(("small", AgentReasoningEffort.High), provider.Turns[1]);
        Assert.IsNull((await host.Commands.GetSelectionChoicesAsync(session.SessionId))!.Current.PermissionMode);
    }

    [TestMethod]
    public async Task ASessionThatAsks_GivesNoSessionAModeThatAsksLess()
    {
        using var temp = new TempFolder();
        var provider = new GatedProvider();
        await using var host = await CreateHostAsync(temp, provider, inherit: () => true);
        var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()));
        var creator = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Creator");
        var target = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Target");
        await DesktopUiToolsTests.SendAsync(host, creator, "one");
        var agent = new AltaCallerIdentity { Kind = "agent", SourceSessionId = creator.SessionId, SourceProjectId = host.CurrentProject.Id };

        var denied = await commands.InvokeAsync(["session", "send", target.SessionId, "--message", "go", "--permission-mode", SessionPermissionModes.Bypass], caller: agent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, denied.ExitCode, denied.Transcript);
        StringAssert.Contains(denied.Transcript, "session.permissionModeDenied");
        // Nor itself: a session does not take a mode that asks less than the one it has.
        var self = await commands.InvokeAsync(["session", "queue", creator.SessionId, "--message", "later", "--permission-mode", SessionPermissionModes.AcceptEdits], caller: agent);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, self.ExitCode, self.Transcript);
        Assert.AreEqual(1, provider.Turns.Count);
        Assert.AreEqual(SessionPermissionPolicy.Review, host.RuntimeService.GetPermissionPolicy(target.SessionId));

        // A mode that asks as much is given, with the model the prompt chooses.
        var allowed = await commands.InvokeAsync(["session", "send", target.SessionId, "--message", "go", "--model", "large", "--permission-mode", SessionPermissionModes.Ask], caller: agent);
        Assert.AreEqual(AltaExitCodes.Success, allowed.ExitCode, allowed.Transcript);
        await WaitIdleAsync(host, target, provider, turns: 2);
        Assert.AreEqual(("large", AgentReasoningEffort.Medium), provider.Turns[1]);
        Assert.AreEqual(SessionPermissionModes.Ask, (await host.Commands.GetSelectionChoicesAsync(target.SessionId))!.Current.PermissionMode);
        // The user's own client gives any mode.
        var user = await commands.InvokeAsync(["session", "queue", target.SessionId, "--message", "later", "--permission-mode", SessionPermissionModes.Bypass], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, user.ExitCode, user.Transcript);
        Assert.AreEqual(SessionPermissionPolicy.Approve, host.RuntimeService.GetPermissionPolicy(target.SessionId));
    }

    [TestMethod]
    public async Task APromptQueuedWithAModel_RunsWithIt_AfterTheTurnInProgress()
    {
        using var temp = new TempFolder();
        var provider = new GatedProvider();
        await using var host = await CreateHostAsync(temp, provider, inherit: () => false);
        var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()));
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Busy");
        var gate = provider.Hold();
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), session.SessionId, "long work")).Receipt!;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            while (!await host.RuntimeService.HasActiveRunAsync(session, timeout.Token)) await Task.Delay(20, timeout.Token);
        }

        // A send that chooses a model starts a turn of its own: it is refused while the session runs.
        var busy = await commands.InvokeAsync(["session", "send", session.SessionId, "--message", "next", "--model", "large"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Unsupported, busy.ExitCode, busy.Transcript);
        StringAssert.Contains(busy.Transcript, "session.running");

        var queued = await commands.InvokeAsync(["session", "queue", session.SessionId, "--message", "next", "--model", "large", "--reasoning", "xhigh"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, queued.ExitCode, queued.Transcript);
        StringAssert.Contains(queued.Transcript, "alta.session.queued");
        // The session shows the model its next turn runs with.
        Assert.AreEqual("large", (await host.Commands.GetSelectionChoicesAsync(session.SessionId))!.Current.ModelId);

        gate.SetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(20))).Outcome);
        await WaitIdleAsync(host, session, provider, turns: 2);
        Assert.AreEqual("small", provider.Turns[0].Model);
        Assert.AreEqual(("large", AgentReasoningEffort.XHigh), provider.Turns[1]);
        var kept = (await host.Commands.GetSelectionChoicesAsync(session.SessionId))!.Current;
        Assert.AreEqual(("large", AgentReasoningEffort.XHigh), (kept.ModelId, kept.ReasoningEffort));
    }

    // A session whose tool calls a guard checks (the run of an automation with autonomy) hands work only to the sessions of its
    // own run: a prompt, a reminder or the end of a job handed to another session would be done there with its tools, unchecked.
    [TestMethod]
    public async Task AGuardedSession_HandsWorkOnlyToTheSessionsOfItsRun()
    {
        using var temp = new TempFolder();
        var provider = new GatedProvider();
        await using var host = await CreateHostAsync(temp, provider, inherit: () => true);
        _ = new CodeAlta.Automations.AutomationRunner(host);
        var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()));
        const string AutomationId = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d79";
        var run = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Run", new AltaActorProvenance
        {
            Kind = AltaActorProvenance.AutomationKind, AutomationId = AutomationId, CorrelationId = "run-1", CreatedAt = DateTimeOffset.UtcNow,
        }, null, null, null, null, null, null);
        var child = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Child", new AltaActorProvenance
        {
            Kind = AltaActorProvenance.WorkflowKind, SourceSessionId = run.SessionId, CreatedAt = DateTimeOffset.UtcNow,
        }, null, null, null, null, null, null);
        var other = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "The user's own");
        Assert.IsNotNull(await host.ToolGuards.FindAsync(run.SessionId, default), "the session of the run is guarded");
        var agent = new AltaCallerIdentity { Kind = "agent", SourceSessionId = run.SessionId, SourceProjectId = host.CurrentProject.Id };

        foreach (var args in new[]
                 {
                     new[] { "session", "send", other.SessionId, "--message", "deploy" }, ["session", "queue", other.SessionId, "--message", "deploy"],
                     ["session", "message", other.SessionId, "--message", "deploy"], ["reminder", "create", "--session", other.SessionId, "--duration", "60", "--content", "deploy"],
                     ["job", "start", "--session", other.SessionId, "--command", "echo deploy"],
                 })
        {
            var refused = await commands.InvokeAsync(args, caller: agent);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, refused.ExitCode, string.Join(' ', args) + ": " + refused.Transcript);
            StringAssert.Contains(refused.Transcript, "guard.otherSession", string.Join(' ', args));
        }

        Assert.AreEqual(0, provider.Turns.Count, "the user's session was handed nothing");
        // Nor to another machine: a session it starts there, or one it moves there, runs out of reach of the guard.
        var moves = new RecordingMoves();
        var fleetCommands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), machineMoves: moves);
        foreach (var args in new[]
                 {
                     new[] { "session", "move", "--machine", "mac-mini" }, ["session", "move", child.SessionId, "--machine", "mac-mini"],
                     ["session", "create", "--machine", "mac-mini", "--message", "deploy"],
                 })
        {
            var refused = await fleetCommands.InvokeAsync(args, caller: agent);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, refused.ExitCode, string.Join(' ', args) + ": " + refused.Transcript);
            StringAssert.Contains(refused.Transcript, "guard.otherSession", string.Join(' ', args));
        }

        Assert.AreEqual(0, moves.Calls, "nothing was moved");
        // A session of its own run takes it; a session no guard checks hands work as before.
        var toChild = await commands.InvokeAsync(["session", "queue", child.SessionId, "--message", "look"], caller: agent);
        Assert.AreEqual(AltaExitCodes.Success, toChild.ExitCode, toChild.Transcript);
        var plain = new AltaCallerIdentity { Kind = "agent", SourceSessionId = other.SessionId, SourceProjectId = host.CurrentProject.Id };
        var fromPlain = await commands.InvokeAsync(["reminder", "create", "--session", child.SessionId, "--duration", "60", "--content", "later"], caller: plain);
        Assert.AreEqual(AltaExitCodes.Success, fromPlain.ExitCode, fromPlain.Transcript);
        var plainMove = await fleetCommands.InvokeAsync(["session", "move", "--machine", "mac-mini"], caller: plain);
        Assert.AreEqual(AltaExitCodes.Success, plainMove.ExitCode, plainMove.Transcript);
        Assert.AreEqual(1, moves.Calls);
    }

    private sealed class RecordingMoves : IAltaSessionMachineMoves
    {
        public int Calls { get; private set; }

        public IReadOnlyList<AltaMoveMachine> ListMachines() => [new("", "pc", true, "online"), new("m1", "mac-mini", false, "online")];

        public Task<AltaSessionMoveResult> MoveAsync(string sessionId, string machine, bool cloneRepository, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AltaSessionMoveResult("finished", null, null, machine, "/work/x", []));
        }

        public AltaSessionMoveResult Schedule(string sessionId, string machine, bool cloneRepository)
        {
            Calls++;
            return new AltaSessionMoveResult("scheduled", null, null, machine, null, []);
        }
    }

    // A session of a run whose first turn ended (or after a restart) takes its guard from its automation: its later turns are
    // supervised as its first one was. An approval of a command that waits too long is denied and stops the turn that waits.
    [TestMethod]
    public async Task ALaterTurnOfARun_StopsWhenAnApprovalWaitsTooLong()
    {
        using var temp = new TempFolder();
        var provider = new GatedProvider();
        await using var host = await CreateHostAsync(temp, provider, inherit: () => true);
        const string AutomationId = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d79";
        var entry = new CodeAlta.Automations.AutomationEntry(new(AutomationId, "Diagnosis")
        {
            Prompt = "Diagnose the incident.", Autonomy = new CodeAlta.Automations.AutomationAutonomy { ApprovalTimeout = 30 },
        }, new CodeAlta.Automations.AutomationSource(Path.Combine(temp.Path, "config.toml"), null, null), null, null, null);
        var clock = new AutomationRunGuardTests.TimerClock(DateTimeOffset.UtcNow);
        var runner = new CodeAlta.Automations.AutomationRunner(host) { Time = clock, WatchInterval = TimeSpan.FromMilliseconds(50), Origin = (_, _) => (entry, false) };
        Assert.IsNotNull(runner);
        var run = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Run", new AltaActorProvenance
        {
            Kind = AltaActorProvenance.AutomationKind, AutomationId = AutomationId, CorrelationId = "run-1", CreatedAt = DateTimeOffset.UtcNow,
        }, null, null, null, null, null, null);

        // A later turn of its session (a queued prompt, a reminder), which works until it is stopped.
        async Task<OwnedSessionCommandReceipt> LaterTurnAsync()
        {
            provider.Hold();
            var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), run.SessionId, "Look again.")).Receipt!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!await host.RuntimeService.HasActiveRunAsync(run, timeout.Token)) await Task.Delay(20, timeout.Token);
            return receipt;
        }

        var turn = await LaterTurnAsync();
        var later = (CodeAlta.Automations.AutomationRunGuard)(await host.ToolGuards.FindAsync(run.SessionId, default))!;
        // It runs a command that waits for its approval.
        var approval = host.RuntimeService.Permissions.RequestForSessionAsync(run.SessionId, new AgentCommandPermissionRequest(new ModelProviderId("gated"), run.SessionId,
            DateTimeOffset.UtcNow, null, "approval-1", null, "deploy --all", temp.Path, null, null, null, null, null), CancellationToken.None);
        using (var visible = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            while ((await host.RuntimeService.Permissions.ListOwnedCommandsAsync(run.SessionId, visible.Token)).Entries.Count == 0)
            {
                Assert.IsFalse(approval.IsCompleted, "the approval waits for a person");
                await Task.Delay(20, visible.Token);
            }
        }

        Assert.IsNull((await later.BeforeToolCallAsync(Call(run.SessionId, "shell_command"), default)).BlockMessage, "a command keeps the guards of the host");
        using (var watched = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            while (!later.Waiting) await Task.Delay(20, watched.Token);
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await approval.WaitAsync(TimeSpan.FromSeconds(20))).Kind, "the approval that waited too long is denied");
        // Stopped (aborted), never completed: the provider holds the turn until then.
        Assert.AreNotEqual(OwnedSessionCommandOutcome.Completed, (await turn.Completion.WaitAsync(TimeSpan.FromSeconds(20))).Outcome, "the turn that waited is stopped");

        static CodeAlta.Agent.Runtime.Tools.AgentToolHookCall Call(string sessionId, string tool)
            => CodeAlta.Agent.Runtime.Tools.AgentToolHookCall.Create(new AgentToolInvocation(new ModelProviderId("gated"), sessionId, "call-" + tool, tool,
                JsonDocument.Parse("{}").RootElement.Clone()), null, null, CodeAlta.Agent.Runtime.Tools.AgentToolHookCapabilities.All);
    }

    [TestMethod]
    public async Task Choices_RefuseWithoutTheOwnerOfTheSessions()
    {
        var dispatcher = new AltaCommandDispatcher(new AltaCommandRegistry(), new AltaServiceCollection());
        var result = await dispatcher.InvokeAsync(["session", "choices", "missing"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.ServiceUnavailable, result.ExitCode, result.Transcript);
    }

    private static async Task<CodeAltaHost> CreateHostAsync(TempFolder temp, GatedProvider provider, Func<bool> inherit)
    {
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        return await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true, OwnedCommandReceiptCapacity = 32,
            SessionPermissionModes = true, ReviewOwnedPermissionsPolicy = () => true, InheritPermissionModePolicy = inherit,
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
    }

    // Until the provider ran that many turns and the session is idle again.
    private static async Task WaitIdleAsync(CodeAltaHost host, SessionViewDescriptor session, GatedProvider provider, int turns)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (provider.Turns.Count < turns || await host.RuntimeService.HasActiveRunAsync(session, timeout.Token)) await Task.Delay(20, timeout.Token);
    }

    private static JsonDocument Record(AltaCommandResult result, string type)
        => JsonDocument.Parse(result.Transcript.Split('\n').First(line => line.Contains($"\"{type}\"", StringComparison.Ordinal)));

    // A provider with two models that have their efforts; it records the model and the effort of each turn, and a turn
    // can wait for the test.
    private sealed class GatedProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        private TaskCompletionSource? _gate;

        public List<(string? Model, AgentReasoningEffort? Effort)> Turns { get; } = [];

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("gated"), "Gated") { DefaultModelId = "small" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "gated", DisplayName = "Gated", TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        // The next turn waits until the returned source is completed.
        public TaskCompletionSource Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };

        public IModelProviderTurnExecutor CreateTurnExecutor() => this;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models =
                [
                    new AgentModelInfo("small", DisplayName: "Small Model", DefaultReasoningEffort: AgentReasoningEffort.High,
                        SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High]),
                    new AgentModelInfo("large", DisplayName: "Large Model", DefaultReasoningEffort: AgentReasoningEffort.Medium,
                        SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.XHigh]),
                ],
                SelectedModelId = "small",
            });

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            lock (Turns) Turns.Add((request.ModelId, request.ReasoningEffort));
            if (Interlocked.Exchange(ref _gate, null) is { } gate) await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new AgentTurnResponse { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("done")]) };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-selection-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
