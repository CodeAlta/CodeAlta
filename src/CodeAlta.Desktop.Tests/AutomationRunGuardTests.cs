using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Automations;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class AutomationRunGuardTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static AutomationEntry Entry(AutomationAutonomy autonomy)
        => new(new AutomationDefinition("0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77", "Diagnosis") { Prompt = "Diagnose.", Autonomy = autonomy }, new("config.toml", null, null), null, null, null);

    private static AgentToolHookCall Call(string tool, string arguments = """{"namespace":"shop","pod":"api-7"}""", bool runByAgent = false)
        => AgentToolHookCall.Create(new AgentToolInvocation(new ModelProviderId("scripted"), "s1", "call-1", tool, JsonDocument.Parse(arguments).RootElement.Clone()), null, "/work",
            AgentToolHookCapabilities.All, runByAgent);

    private sealed class Asker
    {
        internal List<AgentCommandPermissionRequest> Asked { get; } = [];

        internal AgentPermissionDecisionKind Answer { get; set; } = AgentPermissionDecisionKind.Deny;

        internal TaskCompletionSource<AgentPermissionDecision>? Held { get; set; }

        internal Task<AgentPermissionDecision> AskAsync(AgentPermissionRequest request, CancellationToken cancellationToken)
        {
            Asked.Add((AgentCommandPermissionRequest)request);
            if (Held is { } held)
            {
                cancellationToken.Register(() => held.TrySetCanceled(cancellationToken));
                return held.Task;
            }

            return Task.FromResult(new AgentPermissionDecision(Answer));
        }
    }

    [TestMethod]
    public async Task TheHostsToolsAndTheAgentsOwn_KeepTheirGuards_AnAllowedToolRuns_AnyOtherWaitsForAPerson()
    {
        var asker = new Asker();
        var guard = new AutomationRunGuard(Entry(new AutomationAutonomy { AllowedTools = ["k8s_restart_pod"] }), new AutomationCallBreaker(), asker.AskAsync,
            new AutomationClock { Now = Noon }, dryRun: false);

        Assert.IsNull((await guard.BeforeToolCallAsync(Call("shell_command", """{"command":"kubectl delete ns shop"}"""), default)).BlockMessage, "commands follow the permission mode");
        Assert.IsNull((await guard.BeforeToolCallAsync(Call("Bash", """{"command":"ls"}""", runByAgent: true), default)).BlockMessage, "the agent's own prompts");
        Assert.IsNull((await guard.BeforeToolCallAsync(Call("k8s_restart_pod"), default)).BlockMessage);
        Assert.AreEqual(0, asker.Asked.Count);

        var refused = await guard.BeforeToolCallAsync(Call("k8s_delete_namespace", """{"namespace":"shop"}"""), default);
        StringAssert.Contains(refused.BlockMessage, "did not allow the call of k8s_delete_namespace");
        var asked = asker.Asked.Single();
        Assert.AreEqual(("""k8s_delete_namespace {"namespace":"shop"}""", "commandExecution", "/work"), (asked.Command, asked.Kind, asked.WorkingDirectory),
            "the person reads the exact call they allow");
        StringAssert.Contains(asked.Reason, "may not call k8s_delete_namespace by itself");

        asker.Answer = AgentPermissionDecisionKind.AllowOnce;
        Assert.IsNull((await guard.BeforeToolCallAsync(Call("k8s_delete_namespace"), default)).BlockMessage, "allowed once by a person");
    }

    // A person approves the complete action: an approval names the digest of the tool and its complete arguments, and a call too
    // long to be shown whole says that it is shortened, with that digest and where the complete arguments are read.
    [TestMethod]
    public async Task AnApproval_IsBoundToTheDigestOfTheCompleteCall_AndALongOneSaysItIsShortened()
    {
        var asker = new Asker();
        var guard = new AutomationRunGuard(Entry(new AutomationAutonomy()), new AutomationCallBreaker(), asker.AskAsync, new AutomationClock { Now = Noon }, dryRun: false);
        static string Digest(string tool, string arguments)
            => "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tool + "\n" + arguments)));

        var arguments = $$"""{"padding":"{{new string('a', 3000)}}","target":"prod-db"}""";
        await guard.BeforeToolCallAsync(Call("db_drop", arguments), default);
        var asked = asker.Asked.Single();
        Assert.AreEqual(Digest("db_drop", arguments), asked.ApprovalId, "the approval is bound to the complete call");
        StringAssert.Contains(asked.Command, "shortened");
        StringAssert.Contains(asked.Command, Digest("db_drop", arguments));
        StringAssert.Contains(asked.Reason, "complete arguments");

        // A short call is shown whole, and bound to its digest as well.
        await guard.BeforeToolCallAsync(Call("db_drop", """{ "target" : "dev-db" }"""), default);
        Assert.AreEqual(Digest("db_drop", """{"target":"dev-db"}"""), asker.Asked[1].ApprovalId, "the digest is of the arguments without their spacing");
        Assert.AreEqual("""db_drop { "target" : "dev-db" }""", asker.Asked[1].Command);
    }

    [TestMethod]
    public async Task TheCircuitBreaker_AsksAPersonBeyondItsCallsOfTheHour_AllRunsTogether()
    {
        var asker = new Asker { Answer = AgentPermissionDecisionKind.Deny };
        var clock = new AutomationClock { Now = Noon };
        var breaker = new AutomationCallBreaker();
        var autonomy = new AutomationAutonomy { AllowedTools = ["k8s_restart_pod"], MaxCallsPerHour = 2 };
        var first = new AutomationRunGuard(Entry(autonomy), breaker, asker.AskAsync, clock, dryRun: false);
        var second = new AutomationRunGuard(Entry(autonomy), breaker, asker.AskAsync, clock, dryRun: false);

        Assert.IsNull((await first.BeforeToolCallAsync(Call("k8s_restart_pod"), default)).BlockMessage);
        Assert.IsNull((await second.BeforeToolCallAsync(Call("k8s_restart_pod"), default)).BlockMessage);
        Assert.IsNotNull((await first.BeforeToolCallAsync(Call("k8s_restart_pod"), default)).BlockMessage);
        StringAssert.Contains(asker.Asked.Single().Reason, "circuit breaker");

        clock.Now = Noon.AddMinutes(61);
        Assert.IsNull((await first.BeforeToolCallAsync(Call("k8s_restart_pod"), default)).BlockMessage, "an hour later");
    }

    // The calls of the hour are kept with the state of the automations: a restart does not give an automation its calls again.
    [TestMethod]
    public async Task TheCircuitBreaker_KeepsTheCallsOfTheHour_AcrossARestart()
    {
        var folder = Directory.CreateTempSubdirectory("codealta-breaker-").FullName;
        try
        {
            var path = Path.Combine(folder, "state.json");
            var asker = new Asker { Answer = AgentPermissionDecisionKind.Deny };
            var clock = new AutomationClock { Now = Noon };
            var autonomy = new AutomationAutonomy { AllowedTools = ["k8s_restart_pod"], MaxCallsPerHour = 2 };
            var before = new AutomationRunGuard(Entry(autonomy), new AutomationCallBreaker(new AutomationStateStore(path, false)), asker.AskAsync, clock, dryRun: false);
            Assert.IsNull((await before.BeforeToolCallAsync(Call("k8s_restart_pod"), default)).BlockMessage);
            Assert.IsNull((await before.BeforeToolCallAsync(Call("k8s_restart_pod"), default)).BlockMessage);

            // Restarted within the hour: the circuit breaker asks a person.
            clock.Now = Noon.AddMinutes(30);
            var after = new AutomationRunGuard(Entry(autonomy), new AutomationCallBreaker(new AutomationStateStore(path, false)), asker.AskAsync, clock, dryRun: false);
            Assert.IsNotNull((await after.BeforeToolCallAsync(Call("k8s_restart_pod"), default)).BlockMessage);
            StringAssert.Contains(asker.Asked.Single().Reason, "circuit breaker");

            // An hour after the first calls, the tool runs again by itself.
            clock.Now = Noon.AddMinutes(61);
            var later = new AutomationRunGuard(Entry(autonomy), new AutomationCallBreaker(new AutomationStateStore(path, false)), asker.AskAsync, clock, dryRun: false);
            Assert.IsNull((await later.BeforeToolCallAsync(Call("k8s_restart_pod"), default)).BlockMessage);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task APinnedBoundedTool_RunsAsItWasPinned_WithinItsBounds_OnceItsPreconditionHolds()
    {
        var spec = new AgentToolSpec("k8s_restart_pod", "Restarts a pod.", JsonDocument.Parse("""{ "type": "object" }""").RootElement.Clone()) { Version = "1.4.2" };
        var hash = AutomationAllowedTool.HashOf(spec);
        StringAssert.StartsWith(hash, "sha256:");
        Assert.AreEqual(hash, AutomationAllowedTool.HashOf(spec with { InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone() }), "the spaces of the schema do not count");
        var state = "available";
        var reads = new List<string>();
        var read = new AgentToolDefinition(new AgentToolSpec("k8s_get_deployment", "Reads a deployment.", JsonDocument.Parse("{}").RootElement.Clone()), (invocation, _) =>
        {
            reads.Add(invocation.Arguments.GetRawText());
            return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text($"deployment api: {state}")]));
        });
        var autonomy = new AutomationAutonomy
        {
            AllowedTools = ["k8s_restart_pod", "k8s_get_deployment"],
            Tools =
            [
                new AutomationAllowedTool("k8s_restart_pod")
                {
                    Hash = hash, Version = "1.4.2",
                    Bounds = [new AutomationToolBound("namespace") { Values = ["shop"] }, new AutomationToolBound("replicas") { Min = 1, Max = 5 }],
                    Preconditions = [new AutomationToolPrecondition("k8s_get_deployment") { Arguments = ["namespace"], Expect = "Available" }],
                },
            ],
        };
        Assert.IsNull(autonomy.Validate());
        var asker = new Asker { Answer = AgentPermissionDecisionKind.AllowOnce };
        var guard = new AutomationRunGuard(Entry(autonomy), new AutomationCallBreaker(), asker.AskAsync, new AutomationClock { Now = Noon }, dryRun: false);
        AgentToolHookCall Restart(string arguments, AgentToolSpec? as_ = null) => AgentToolHookCall.Create(
            new AgentToolInvocation(new ModelProviderId("scripted"), "s1", "call-1", "k8s_restart_pod", JsonDocument.Parse(arguments).RootElement.Clone()), as_ ?? spec, "/work",
            AgentToolHookCapabilities.All) with { FindTool = name => name == read.Spec.Name ? read : null };

        Assert.IsNull((await guard.BeforeToolCallAsync(Restart("""{"namespace":"shop","replicas":3,"pod":"api-7"}"""), default)).BlockMessage, "as pinned, within its bounds");
        CollectionAssert.AreEqual(new[] { """{"namespace":"shop"}""" }, reads, "its precondition read the target first, with the arguments it names");
        Assert.AreEqual(0, asker.Asked.Count);

        // Out of its bounds, or not as it was pinned: a person decides, then its precondition is read again before it acts.
        foreach (var (arguments, changed, why) in new[]
        {
            ("""{"namespace":"payments","replicas":3}""", (AgentToolSpec?)null, "namespace is payments, not one of shop"),
            ("""{"namespace":"shop","replicas":9}""", null, "replicas is 9, more than 5"),
            ("""{"namespace":"shop"}""", null, "replicas is not given"),
            ("""{"namespace":"shop","replicas":3}""", spec with { Description = "Restarts every pod." }, "definition of k8s_restart_pod changed since it was pinned"),
            ("""{"namespace":"shop","replicas":3}""", spec with { Version = "2.0.0" }, "version 2.0.0, not 1.4.2"),
        })
        {
            asker.Asked.Clear();
            Assert.IsNull((await guard.BeforeToolCallAsync(Restart(arguments, changed), default)).BlockMessage, why);
            StringAssert.Contains(asker.Asked.Single().Reason, why);
        }

        // A target that is not as it must be: the call is not made, and a person's approval does not make it either.
        state = "degraded";
        StringAssert.Contains((await guard.BeforeToolCallAsync(Restart("""{"namespace":"shop","replicas":3}"""), default)).BlockMessage, "was not made: k8s_get_deployment did not say");
        asker.Asked.Clear();
        StringAssert.Contains((await guard.BeforeToolCallAsync(Restart("""{"namespace":"shop","replicas":9}"""), default)).BlockMessage, "was not made");
        Assert.AreEqual(1, asker.Asked.Count, "asked, then read again after the approval");

        // A try makes no call, preconditions included, and says what its pin would be.
        var trying = new AutomationRunGuard(Entry(autonomy), new AutomationCallBreaker(), asker.AskAsync, new AutomationClock { Now = Noon }, dryRun: true);
        reads.Clear();
        StringAssert.Contains((await trying.BeforeToolCallAsync(Restart("""{"namespace":"payments"}"""), default)).BlockMessage, $"hash = \"{hash}\", version = \"1.4.2\"");
        Assert.AreEqual(0, reads.Count);
    }

    [TestMethod]
    public async Task ATry_RecordsTheCallsOfItsAllowedTools_AndMakesNone()
    {
        var asker = new Asker();
        var guard = new AutomationRunGuard(Entry(new AutomationAutonomy { AllowedTools = ["k8s_restart_pod"] }), new AutomationCallBreaker(), asker.AskAsync,
            new AutomationClock { Now = Noon }, dryRun: true);
        var recorded = new List<string>();
        guard.Recorded += recorded.Add;

        var faked = await guard.BeforeToolCallAsync(Call("k8s_restart_pod"), default);

        StringAssert.Contains(faked.BlockMessage, "recorded and not made");
        CollectionAssert.AreEqual(new[] { """k8s_restart_pod {"namespace":"shop","pod":"api-7"}""" }, recorded);
        Assert.AreEqual(0, asker.Asked.Count);
    }

    [TestMethod]
    public async Task AnApprovalThatWaitsTooLong_StopsTheRun_UnlessAPersonTookItOver()
    {
        var clock = new TimerClock(Noon);
        var asker = new Asker { Held = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var guard = new AutomationRunGuard(Entry(new AutomationAutonomy { ApprovalTimeout = 30 }), new AutomationCallBreaker(), asker.AskAsync, clock, dryRun: false);
        var stopped = new List<string>();
        var waiting = new List<bool>();
        guard.Stopped += stopped.Add;
        guard.WaitingChanged += waiting.Add;

        var held = guard.BeforeToolCallAsync(Call("k8s_scale"), default).AsTask();
        Assert.IsTrue(guard.Waiting);
        clock.Advance(TimeSpan.FromMinutes(31));
        var decision = await held.WaitAsync(TimeSpan.FromSeconds(10));
        StringAssert.Contains(decision.BlockMessage, "within 30 minutes: the run stopped");
        Assert.AreEqual(1, stopped.Count);
        CollectionAssert.AreEqual(new[] { true, false }, waiting);

        // Taken over, the next approval waits for that person as long as they take.
        asker.Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = guard.BeforeToolCallAsync(Call("k8s_scale"), default).AsTask();
        guard.TakeOver();
        clock.Advance(TimeSpan.FromHours(2));
        await Task.Delay(50);
        Assert.IsFalse(next.IsCompleted);
        asker.Held.SetResult(new(AgentPermissionDecisionKind.AllowOnce));
        Assert.IsNull((await next.WaitAsync(TimeSpan.FromSeconds(10))).BlockMessage);
        Assert.AreEqual(1, stopped.Count);
    }

    // A runner whose runs have a guard of their own, asking the given asker, and that complete when the test says.
    private sealed class GuardedRunner(Asker asker) : IAutomationRunner
    {
        internal TaskCompletionSource<AutomationOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal AutomationRunGuard? Guard { get; private set; }

        public Task<AutomationStart> StartAsync(AutomationEntry entry, string runId, string prompt, string? detail, CancellationToken cancellationToken)
            => StartAsync(entry, runId, prompt, detail, false, cancellationToken);

        public Task<AutomationStart> StartAsync(AutomationEntry entry, string runId, string prompt, string? detail, bool dryRun, CancellationToken cancellationToken)
        {
            Guard = new AutomationRunGuard(entry, new AutomationCallBreaker(), asker.AskAsync, new AutomationClock { Now = Noon }, dryRun);
            return Task.FromResult(new AutomationStart("session-1", null, Completion.Task) { Guard = Guard });
        }
    }

    // Each approval a run waits for is one of its own: its server tells a person of each one (a notification per approval, not
    // per run), and who is on call is read again when it starts to wait.
    [TestMethod]
    public async Task EachApprovalARunWaitsFor_HasAnIdOfItsOwn()
    {
        using var root = new AutomationTempRoot();
        File.WriteAllText(Path.Combine(root.Path, "config.toml"), """
            [automations.0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77]
            name = "Diagnosis"
            prompt = "Diagnose."
            autonomy = { allowed_tools = ["k8s_restart_pod"] }
            """);
        var asker = new Asker { Held = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var runner = new GuardedRunner(asker);
        await using var service = new AutomationService(Path.Combine(root.Path, "config.toml"), _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>([]),
            new AutomationStateStore(Path.Combine(root.Path, "state.json"), false), runner, new AutomationClock { Now = Noon }, TimeZoneInfo.Utc);
        await service.RefreshAsync();
        const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77";

        await service.RunAsync(Id, default);
        Assert.IsNull(service.Runs(Id, 1)[0].Approval, "a run that waits for nobody has no approval");
        var first = runner.Guard!.BeforeToolCallAsync(Call("k8s_delete_namespace"), default).AsTask();
        var approval = service.Runs(Id, 1)[0].Approval;
        Assert.IsNotNull(approval);
        asker.Held.SetResult(new(AgentPermissionDecisionKind.AllowOnce));
        await first;
        Assert.IsNull(service.Runs(Id, 1)[0].Approval, "answered, it waits for nothing");

        asker.Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = runner.Guard.BeforeToolCallAsync(Call("k8s_delete_namespace"), default).AsTask();
        var next = service.Runs(Id, 1)[0].Approval;
        Assert.IsNotNull(next);
        Assert.AreNotEqual(approval, next, "the next approval is another one");
        asker.Held.SetResult(new(AgentPermissionDecisionKind.Deny));
        await second;
        runner.Completion.SetResult(new(AutomationRun.Completed));
        for (var i = 0; i < 200 && service.IsRunning(Id); i++) await Task.Delay(10);
    }

    [TestMethod]
    public async Task ARunWaitsWhileACallWaitsForAPerson_ATryKeepsItsCalls_AndATakeOverIsSaid()
    {
        using var root = new AutomationTempRoot();
        File.WriteAllText(Path.Combine(root.Path, "config.toml"), """
            [automations.0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77]
            name = "Diagnosis"
            prompt = "Diagnose."
            autonomy = { allowed_tools = ["k8s_restart_pod"] }
            """);
        var asker = new Asker { Held = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var runner = new GuardedRunner(asker);
        await using var service = new AutomationService(Path.Combine(root.Path, "config.toml"), _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>([]),
            new AutomationStateStore(Path.Combine(root.Path, "state.json"), false), runner, new AutomationClock { Now = Noon }, TimeZoneInfo.Utc);
        await service.RefreshAsync();
        const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77";

        var run = (await service.RunAsync(Id, default))!;
        Task<(string Status, string? Message, string? SessionId)> TakeOver(string runId) => AutomationTakeOvers.TakeOverAsync(service, null, null, runId, default);
        var held = runner.Guard!.BeforeToolCallAsync(Call("k8s_delete_namespace"), default).AsTask();
        Assert.AreEqual(AutomationRun.Waiting, service.Runs(Id, 1)[0].Status, "the run waits for a person");
        // A person takes it over by the id of the run alone, as the phone does.
        Assert.AreEqual(("not_found", (string?)null), await TakeOver("run-unknown") is var unknown ? (unknown.Status, unknown.SessionId) : default);
        // Named for another automation, the run is not taken over: what authorized the request was about that other automation.
        Assert.IsNull(service.TakeOver(run.Id, "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d78"));
        Assert.IsFalse(service.IsTakenOver(run.Id));
        Assert.AreEqual(("ok", "session-1"), await TakeOver(run.Id) is var taken ? (taken.Status, taken.SessionId) : default);
        Assert.IsTrue(service.IsTakenOver(run.Id));
        // What waits for the person: this run, and the run of a team on a machine of a server, which a server told them of.
        var teamRun = new CodeAlta.LiveTool.AltaWaitingRun("run-team", Id, null, "node-1", "Build box", null, Noon.AddMinutes(-5));
        var alta = new DesktopAltaAutomations(service, null!, true, null, _ => Task.FromResult<IReadOnlyList<CodeAlta.LiveTool.AltaWaitingRun>>([teamRun]));
        var listed = await alta.ListWaitingAsync(default);
        Assert.AreEqual(("run-team", "node-1"), (listed.Single(other => other.MachineId is not null).RunId, listed.Single(other => other.MachineId is not null).MachineId));
        Assert.AreEqual(run.Id, listed.Single(other => other.MachineId is null).RunId);
        Assert.AreEqual("Taken over by a person.", service.Runs(Id, 1)[0].Message);
        asker.Held.SetResult(new(AgentPermissionDecisionKind.AllowOnce));
        await held;
        Assert.AreEqual(AutomationRun.Running, service.Runs(Id, 1)[0].Status);
        runner.Completion.SetResult(new(AutomationRun.Completed));
        for (var i = 0; i < 200 && service.IsRunning(Id); i++) await Task.Delay(10);
        Assert.IsNull(service.TakeOver(run.Id), "a run that ended is not taken over");

        // A try, by hand: the allowed tool is a fake, and its call stays on the run.
        var runner2 = new GuardedRunner(new Asker());
        await using var trying = new AutomationService(Path.Combine(root.Path, "config.toml"), _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>([]),
            new AutomationStateStore(Path.Combine(root.Path, "state2.json"), false), runner2, new AutomationClock { Now = Noon }, TimeZoneInfo.Utc);
        await trying.RefreshAsync();
        var (tried, problem) = await trying.TryAsync(Id, -1, null, default);
        Assert.IsNull(problem);
        Assert.AreEqual(AutomationService.TryTrigger, tried!.Trigger);
        Assert.IsTrue(runner2.Guard!.DryRun);
        await runner2.Guard.BeforeToolCallAsync(Call("k8s_restart_pod"), default);
        CollectionAssert.AreEqual(new[] { """k8s_restart_pod {"namespace":"shop","pod":"api-7"}""" }, trying.Runs(Id, 1)[0].Calls!.ToArray());
        runner2.Completion.SetResult(new(AutomationRun.Completed));

        // A script of the library is tried the same way: its session runs it, and the calls of the script go through the guard.
        File.WriteAllText(Path.Combine(root.Path, "script.toml"), """
            [automations.0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d78]
            name = "Scripted diagnosis"
            prompt = ""
            script = "diagnose"
            autonomy = { allowed_tools = ["k8s_restart_pod"] }
            """);
        var runner3 = new GuardedRunner(new Asker());
        await using var scripted = new AutomationService(Path.Combine(root.Path, "script.toml"), _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>([]),
            new AutomationStateStore(Path.Combine(root.Path, "state3.json"), false), runner3, new AutomationClock { Now = Noon }, TimeZoneInfo.Utc);
        await scripted.RefreshAsync();
        var (script, scriptProblem) = await scripted.TryAsync("0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d78", -1, null, default);
        Assert.IsNull(scriptProblem);
        Assert.AreEqual(AutomationService.TryTrigger, script!.Trigger);
        Assert.IsTrue(runner3.Guard!.DryRun, "the script's run is a dry run");
        runner3.Completion.SetResult(new(AutomationRun.Completed));
    }

    /// <summary>A clock whose timers fire when a test moves it.</summary>
    private sealed class TimerClock(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public void Advance(TimeSpan by)
        {
            Timer[] due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(timer => timer.Due is { } at && at <= _now)];
                foreach (var timer in due) timer.Due = null;
            }

            foreach (var timer in due) timer.Callback(timer.State);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate) _timers.Add(timer);
            return timer;
        }

        private sealed class Timer(TimerClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal TimerCallback Callback => callback;

            internal object? State => state;

            internal DateTimeOffset? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }

            public void Dispose()
            {
                lock (clock._gate) clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [TestMethod]
    public async Task TheGuardOfASession_IsLookedUpAtEachCall_AfterThePlugins()
    {
        var guards = new SessionToolGuards();
        var hooks = guards.Compose(new SessionToolHookScope("s1", null, "/work", null), null);
        var asker = new Asker();
        var guard = new AutomationRunGuard(Entry(new AutomationAutonomy()), new AutomationCallBreaker(), asker.AskAsync, new AutomationClock { Now = Noon }, dryRun: false);

        Assert.IsNull((await hooks.BeforeToolCallAsync(Call("k8s_scale"), default)).BlockMessage, "no guard yet: as before");
        guards.Set("s1", guard);
        Assert.IsNotNull((await hooks.BeforeToolCallAsync(Call("k8s_scale"), default)).BlockMessage, "given after the session was attached, it applies");
        Assert.IsTrue(guards.Remove("s1", guard));
        Assert.IsNull((await hooks.BeforeToolCallAsync(Call("k8s_scale"), default)).BlockMessage);
        Assert.AreEqual(1, asker.Asked.Count);
    }
}
