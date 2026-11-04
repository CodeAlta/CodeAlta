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
