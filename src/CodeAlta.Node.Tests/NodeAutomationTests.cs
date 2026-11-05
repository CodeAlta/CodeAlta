using CodeAlta.Automations;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Node.Tests;

/// <summary>
/// Automations on a node (<c>doc/fleet/node.md</c>, "Automations and statistics"): a paired device assigns one through the
/// node API, the node fires its schedule, claims each due time once, checks its policy and the credential kind before a
/// run starts, and keeps what it was assigned across a restart, with nobody connected.
/// </summary>
[TestClass]
public sealed class NodeAutomationTests
{
    private const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1f01";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Noon = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PairedDevice_AssignsAnAutomation_TheNodeFiresItsScheduleOnce_AndKeepsItAcrossARestart()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var clock = new NodeClock { Now = Noon };
        var provider = new ScriptedNodeProvider();
        string deviceId;
        string runId;
        await using (var fixture = await AutomationFixture.StartAsync(root, provider, clock, token))
        {
            var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
            deviceId = fixture.ViewerIdentity.DeviceId;
            await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
            var put = await client.PutAutomationAsync(fixture.Signed(Definition("Half past", "Say hello."), 1), "put-1", token);
            Assert.AreEqual((Id, "Half past", true, deviceId, "viewer"), (put.Id, put.Name, put.Enabled, put.AssignedBy, put.AssignedByName));
            Assert.AreEqual(Noon.AddMinutes(30), put.NextDue, "The node fires its schedule.");
            Assert.AreEqual(Id, (await client.ListAutomationsAsync(token)).Automations.Single().Id);

            // The due time comes: the node claims it, runs a session there, and the device reads the run.
            var engine = fixture.Node.Automations!.Engine;
            clock.Now = Noon.AddMinutes(30).AddSeconds(5);
            engine.Tick(clock.Now);
            var run = await WaitForAsync(async () => (await client.ListAutomationRunsAsync(Id, cancellationToken: token)).Runs.FirstOrDefault(static candidate => candidate.Status == AutomationRun.Completed), token);
            Assert.AreEqual(("hourly", "Half past"), (run.Trigger, run.Name));
            Assert.IsNotNull(run.SessionId, "The run's session is on the node.");
            StringAssert.Contains(provider.LastUserText, "Say hello.");
            runId = run.Id;

            // The same due time fired again (a second engine, a catch-up) is taken already.
            var entry = engine.Snapshot.Find(Id)!;
            var occurrence = AutomationService.Occurrence(entry.Definition.Triggers[0], Noon.AddMinutes(30));
            Assert.IsFalse(engine.Trigger(entry, "hourly", null, entry.Definition.Prompt, occurrence: occurrence));
            Assert.AreEqual(AutomationRun.Skipped, (await client.ListAutomationRunsAsync(Id, cancellationToken: token)).Runs[0].Status);

            // A retry of a run by hand is the same run.
            var manual = await client.RunAutomationAsync(Id, "run-1", token);
            Assert.AreEqual(manual.Id, (await client.RunAutomationAsync(Id, "run-1", token)).Id);
            await WaitForAsync(async () => (await client.ListAutomationRunsAsync(Id, cancellationToken: token)).Runs.FirstOrDefault(candidate => candidate.Id == manual.Id && candidate.Status == AutomationRun.Completed), token);
        }

        // The node starts again with nobody connected: what it was assigned still fires, and the claimed time stays claimed.
        clock.Now = Noon.AddHours(1).AddMinutes(29);
        await using (var again = await AutomationFixture.StartAsync(root, provider, clock, token))
        {
            var engine = again.Node.Automations!.Engine;
            await engine.RefreshAsync();
            Assert.IsTrue(engine.IsAssigned(Id));
            Assert.AreEqual(Noon.AddHours(1).AddMinutes(30), engine.NextDue(Id));
            Assert.AreEqual(deviceId, again.Node.Automations.AssignmentOf(Id)?.DeviceId);
            Assert.IsTrue(engine.Runs(Id, 10).Any(run => run.Id == runId), "The runs are kept by the node.");
            clock.Now = Noon.AddHours(1).AddMinutes(30).AddSeconds(5);
            engine.Tick(clock.Now);
            await WaitForAsync(() => Task.FromResult(engine.Runs(Id, 10).FirstOrDefault(static run => run is { Status: AutomationRun.Completed, StartedAt.Hour: 13 })), token);
        }
    }

    [TestMethod]
    public async Task RunByHand_WhileABudgetHoldsIt_IsAnsweredAtOnce_AsWaitingForContinueAnyway()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var provider = new ScriptedNodeProvider();
        await using var fixture = await AutomationFixture.StartAsync(root, provider, new NodeClock { Now = Noon }, token);
        // A budget of the machine is reached and holds new runs: the run waits until its owner continues it anyway.
        var decision = new TaskCompletionSource<CodeAlta.Fleet.Server.FleetBudgetAdmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? held = null;
        fixture.Node.Automations!.AdmitBudget = (run, waits, cancellation) =>
        {
            held = run.RunId;
            waits?.Invoke();
            return decision.Task.WaitAsync(cancellation);
        };
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        await client.PutAutomationAsync(fixture.Signed(Definition("Held", "Say hello."), 1), cancellationToken: token);

        // Run now answers at once, not when the person decides: the run waits, bound to the hold that Continue anyway names.
        var run = await client.RunAutomationAsync(Id, cancellationToken: token).WaitAsync(TimeSpan.FromSeconds(20), token);
        Assert.AreEqual(("held", held), (run.Status, run.Id), run.Message);
        StringAssert.Contains(run.Message, "Continue anyway");
        Assert.AreEqual("held", (await client.ListAutomationRunsAsync(Id, cancellationToken: token)).Runs.Single(candidate => candidate.Id == run.Id).Status);

        // Continued anyway: the run starts its session, as any run by hand.
        decision.SetResult(CodeAlta.Fleet.Server.FleetBudgetAdmission.Admitted);
        var completed = await WaitForAsync(async () => (await client.ListAutomationRunsAsync(Id, cancellationToken: token)).Runs
            .FirstOrDefault(candidate => candidate.Id == run.Id && candidate.Status == AutomationRun.Completed), token);
        Assert.IsNotNull(completed.SessionId);
        StringAssert.Contains(provider.LastUserText, "Say hello.");
    }

    [TestMethod]
    public async Task Node_RefusesARun_ItsPolicy_TheCredentialKind_OrTheAssigningDevice_DoNotAllow()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var kind = AutomationCredentialKind.Unknown;
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token, _ => kind);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        await client.PutAutomationAsync(fixture.Signed(Definition("Guarded", "Hello."), 1), cancellationToken: token);

        async Task<FleetAutomationRunInfo> RunAsync() => await client.RunAutomationAsync(Id, cancellationToken: token);

        var unknown = await RunAsync();
        Assert.AreEqual(AutomationRun.Failed, unknown.Status);
        StringAssert.Contains(unknown.Message, "cannot be told");
        Assert.IsNull(unknown.SessionId, "Nothing is created for a run the node refuses.");

        kind = AutomationCredentialKind.Personal;
        NodeAutomationPolicy.Save(fixture.Node.Host.CatalogOptions.FleetRoot, new NodeAutomationPolicy { Credentials = NodeAutomationPolicy.AccountCredentials });
        StringAssert.Contains((await RunAsync()).Message, "account credentials only");
        NodeAutomationPolicy.Save(fixture.Node.Host.CatalogOptions.FleetRoot, new NodeAutomationPolicy { Enabled = false });
        StringAssert.Contains((await RunAsync()).Message, "turned them off");
        // The policy that turned them off breaks: what it said cannot be told, and nothing runs, with account credentials neither.
        kind = AutomationCredentialKind.Account;
        File.WriteAllText(Path.Combine(fixture.Node.Host.CatalogOptions.FleetRoot, NodeAutomationPolicy.FileName), "{ \"enabled\": fal");
        var unreadable = await RunAsync();
        Assert.AreEqual(AutomationRun.Failed, unreadable.Status);
        StringAssert.Contains(unreadable.Message, "policy cannot be read");
        kind = AutomationCredentialKind.Personal;
        NodeAutomationPolicy.Save(fixture.Node.Host.CatalogOptions.FleetRoot, new NodeAutomationPolicy());

        // Changed on the node by something else than the device: it does not run as it is now.
        var assigned = Path.Combine(fixture.Node.Host.CatalogOptions.FleetRoot, NodeAutomations.AssignedFileName);
        File.WriteAllText(assigned, File.ReadAllText(assigned).Replace("Hello.", "Do something else.", StringComparison.Ordinal));
        await fixture.Node.Automations!.Engine.RefreshAsync();
        StringAssert.Contains((await RunAsync()).Message, "changed on this machine");
        await client.PutAutomationAsync(fixture.Signed(Definition("Guarded", "Hello."), 2), cancellationToken: token);
        var allowed = await RunAsync();
        Assert.AreNotEqual(AutomationRun.Failed, allowed.Status, allowed.Message);

        // The device loses the permission to give automations: what it gave stops running, and it cannot give more.
        fixture.Fleet.Service.TrustedDevices.SetPermissions(fixture.ViewerIdentity.DeviceId, DevicePermissions.View | DevicePermissions.Start);
        StringAssert.Contains(fixture.Node.Automations.Check(fixture.Node.Automations.Engine.Snapshot.Find(Id)!, new CodeAlta.Agent.ModelProviderDescriptor(new("scripted"), "Scripted")),
            "may no longer give automations");
        var forbidden = await Assert.ThrowsExactlyAsync<FleetException>(() => client.PutAutomationAsync(fixture.Signed(Definition("Again", "x"), 3), cancellationToken: token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, forbidden.Error.Code);
        StringAssert.Contains(forbidden.Message, "automate");
    }

    // A machine of a team or of the organization runs automations with account credentials only, whatever gave them: one a
    // device paired directly gave it too, not only one a server assigned.
    [TestMethod]
    public async Task OnAMachineOfATeam_AnAutomationNoServerGaveRunsWithAccountCredentialsOnly()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token, _ => AutomationCredentialKind.Personal);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        await client.PutAutomationAsync(fixture.Signed(Definition("Local", "Hello."), 1), cancellationToken: token);
        var automations = fixture.Node.Automations!;
        automations.DeviceId ??= fixture.Fleet.DeviceId;
        Assert.AreNotEqual(AutomationRun.Failed, (await client.RunAutomationAsync(Id, cancellationToken: token)).Status, "a personal machine may use a personal plan");

        var team = new FleetAccessTeam("infra", ["p2"], [], false);
        var access = FleetAccessState.Empty with
        {
            OrganizationId = "org", Teams = FleetAccessState.Empty.Teams.Add("infra", team), Machines = FleetAccessState.Empty.Machines.Add(automations.DeviceId!, "infra"),
        };
        automations.ServerAccesses = () => [access];
        var refused = await client.RunAutomationAsync(Id, cancellationToken: token);
        Assert.AreEqual(AutomationRun.Failed, refused.Status);
        StringAssert.Contains(refused.Message, "account credentials only");
    }

    // A try is a method of its own (doc/fleet/protocol.md, "Versions"): a node that does not know it answers method_not_found,
    // and the device says so rather than run the automation for real.
    [TestMethod]
    public async Task ATry_IsAMethodOfItsOwn_ThatTheNodeRunsAsATry()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        await client.PutAutomationAsync(fixture.Signed(Definition("Tried", "Hello."), 1), cancellationToken: token);

        var tried = await client.TryAutomationAsync(Id, token);
        Assert.AreEqual("try", tried.Trigger, tried.Message);
        Assert.AreEqual("try", (await client.ListAutomationRunsAsync(Id, cancellationToken: token)).Runs.Single(run => run.Id == tried.Id).Trigger);
    }

    // Account credentials hold for the whole run (doc/fleet/orchestration.md, "Credentials of unattended runs"): its session that
    // changes provider, a model tier or a fallback, and the sessions it starts resolve only to account providers.
    [TestMethod]
    public async Task AccountCredentials_HoldForTheSessionsOfARun_ItsChildren_AndAChangeOfProvider()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var provider = new ScriptedNodeProvider();
        await using var fixture = await AutomationFixture.StartAsync(root, provider, new NodeClock { Now = Noon }, token,
            descriptor => descriptor.ProviderId.Value == "personal" ? AutomationCredentialKind.Personal : AutomationCredentialKind.Account);
        NodeAutomationPolicy.Save(fixture.Node.Host.CatalogOptions.FleetRoot, new NodeAutomationPolicy { Credentials = NodeAutomationPolicy.AccountCredentials });
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        await client.PutAutomationAsync(fixture.Signed(Definition("Unattended", "Hello."), 1), cancellationToken: token);
        var run = await client.RunAutomationAsync(Id, cancellationToken: token);
        Assert.AreNotEqual(AutomationRun.Failed, run.Status, run.Message);
        var host = fixture.Node.Host;
        var sessionId = run.SessionId ?? (await WaitForAsync(async () => (await client.ListAutomationRunsAsync(Id, cancellationToken: token)).Runs
            .FirstOrDefault(candidate => candidate.Id == run.Id && candidate.SessionId is not null), token)).SessionId!;
        var session = (await host.RuntimeService.TryGetRecoverableSessionAsync(sessionId, token))!;
        var child = await host.Commands.CreateDraftSessionAsync(null, provider.Descriptor, "Child", new CodeAlta.Catalog.AltaActorProvenance
        {
            Kind = CodeAlta.Catalog.AltaActorProvenance.WorkflowKind, SourceSessionId = sessionId, CreatedAt = DateTimeOffset.UtcNow,
        }, null, null, null, null, null, null);
        var plain = await host.Commands.CreateDraftSessionAsync(null, provider.Descriptor, "Plain", null, null, null, null, null, null, null);
        var gate = host.RuntimeService.ProviderGate;
        Assert.IsNotNull(gate, "the node checks the provider of every attachment of a session");
        // A provider of the machine that runs with a personal plan (only its descriptor is read here).
        host.ModelProviderRegistry.RegisterOrReplace(new CodeAlta.Agent.ModelProviderDescriptor(new("personal"), "Personal"), static () => throw new NotSupportedException());

        // The run's session that changes provider, and its child, are refused a personal plan; an account provider is theirs.
        StringAssert.Contains(await gate(session, "personal", token), "account credentials");
        StringAssert.Contains(await gate(child, "personal", token), "account credentials");
        Assert.IsNull(await gate(child, provider.Descriptor.ProviderId.Value, token));
        // Many levels below the run, the rules of the run still hold.
        var deep = child;
        for (var level = 0; level < 40; level++)
        {
            deep = await host.Commands.CreateDraftSessionAsync(null, provider.Descriptor, $"Level {level}", new CodeAlta.Catalog.AltaActorProvenance
            {
                Kind = CodeAlta.Catalog.AltaActorProvenance.WorkflowKind, SourceSessionId = deep.SessionId, CreatedAt = DateTimeOffset.UtcNow,
            }, null, null, null, null, null, null);
        }

        StringAssert.Contains(await gate(deep, "personal", token), "account credentials", "a deep descendant of the run");
        // A session that no automation started keeps the providers of the machine.
        Assert.IsNull(await gate(plain, "personal", token));
    }

    // The signed policy of the organization of the space a run goes to (doc/fleet/security-and-privacy.md, policies and the kill
    // switch) applies to the node's own automations too, not only to those a server delivered: its providers, its models, its
    // account credentials.
    [TestMethod]
    public async Task TheRulesOfTheSpace_ApplyToTheNodesOwnAutomations()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var kind = AutomationCredentialKind.Account;
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token, _ => kind);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        await client.PutAutomationAsync(fixture.Signed(Definition("Own", "Hello."), 1), cancellationToken: token);
        var policy = FleetPolicyState.Empty with { Number = 1, LastHash = "h1", Rules = new FleetPolicyRules(Providers: ["approved-only"]) };
        var linked = true;
        fixture.Node.Automations!.Policy = new FleetPolicyGuard(fixture.Fleet.DeviceId, () => [new FleetPolicySource("https://fleet.example", policy, null, _ => linked)]);

        var provider = await client.RunAutomationAsync(Id, cancellationToken: token);
        Assert.AreEqual(AutomationRun.Failed, provider.Status);
        StringAssert.Contains(provider.Message, "approved-only");
        Assert.IsNull(provider.SessionId, "Nothing is created for a run the policy refuses.");

        policy = policy with { Number = 2, Rules = new FleetPolicyRules(Models: ["claude-*"]) };
        StringAssert.Contains((await client.RunAutomationAsync(Id, cancellationToken: token)).Message, "claude-*");

        kind = AutomationCredentialKind.Personal;
        policy = policy with { Number = 3, Rules = new FleetPolicyRules(AccountCredentials: true) };
        StringAssert.Contains((await client.RunAutomationAsync(Id, cancellationToken: token)).Message, "account credentials");

        // A space not linked to the server of the policy: its rules say nothing there.
        linked = false;
        var allowed = await client.RunAutomationAsync(Id, cancellationToken: token);
        Assert.AreNotEqual(AutomationRun.Failed, allowed.Status, allowed.Message);
    }

    // The limits of the signed policy on what a session of the fleet may do reach the runs of automations too, whatever their
    // definition asks: a permission mode above its ceiling, a sandbox below its floor, the tools it turns off.
    [TestMethod]
    public async Task TheRulesOfTheSpace_LimitThePermissionModeTheSandboxAndTheToolsOfARun()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        var policy = FleetPolicyState.Empty with { Number = 1, LastHash = "h1", Rules = new FleetPolicyRules(PermissionMode: "default") };
        var automations = fixture.Node.Automations!;
        automations.Policy = new FleetPolicyGuard(fixture.Fleet.DeviceId, () => [new FleetPolicySource("https://fleet.example", policy, null, static _ => true)]);

        await client.PutAutomationAsync(fixture.Signed(Definition("Wide", "Hello.") + "\npermission_mode = \"acceptEdits\"\n", 1), cancellationToken: token);
        var wide = await client.RunAutomationAsync(Id, cancellationToken: token);
        Assert.AreEqual(AutomationRun.Failed, wide.Status, "a mode above the ceiling of the policy");
        StringAssert.Contains(wide.Message, "policy of the organization");
        Assert.IsNull(wide.SessionId);

        policy = policy with { Number = 2, Rules = new FleetPolicyRules(Network: "off") };
        await client.PutAutomationAsync(fixture.Signed(Definition("Open", "Hello.") + "\nsandbox = \"off\"\n", 2), cancellationToken: token);
        var open = await client.RunAutomationAsync(Id, cancellationToken: token);
        Assert.AreEqual(AutomationRun.Failed, open.Status, "no sandbox where the policy limits the network of the sandbox");
        StringAssert.Contains(open.Message, "sandbox");

        policy = policy with { Number = 3, Rules = new FleetPolicyRules(ToolsOff: ["shell_command"]) };
        await client.PutAutomationAsync(fixture.Signed(Definition("Tools", "Hello."), 3), cancellationToken: token);
        var run = await client.RunAutomationAsync(Id, cancellationToken: token);
        var sessionId = (await WaitForAsync(() => Task.FromResult(automations.Engine.Runs(Id, 10).FirstOrDefault(candidate => candidate.Id == run.Id && candidate.SessionId is not null)),
            token)).SessionId!;
        CollectionAssert.Contains(fixture.Node.Host.RuntimeService.GetDisabledToolPatterns(sessionId).ToList(), "shell_command", "the tools the policy turns off are off");
    }

    // The tools the policy turns off are held by the runtime in memory only: a session of a run continued after the node started
    // again, and a session it starts, do without them again.
    [TestMethod]
    public async Task TheToolsThePolicyTurnsOff_StayOff_WhenARunSessionIsContinuedAfterARestart()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        var policy = FleetPolicyState.Empty with { Number = 1, LastHash = "h1", Rules = new FleetPolicyRules(ToolsOff: ["shell_command"]) };
        var automations = fixture.Node.Automations!;
        automations.Policy = new FleetPolicyGuard(fixture.Fleet.DeviceId, () => [new FleetPolicySource("https://fleet.example", policy, null, static _ => true)]);
        await client.PutAutomationAsync(fixture.Signed(Definition("Tools", "Hello."), 1), cancellationToken: token);
        var run = await client.RunAutomationAsync(Id, cancellationToken: token);
        var ended = await WaitForAsync(() => Task.FromResult(automations.Engine.Runs(Id, 10).FirstOrDefault(candidate => candidate.Id == run.Id && candidate.SessionId is not null
            && candidate.Status is not (AutomationRun.Running or AutomationRun.Waiting or AutomationRun.Held))), token);
        var runtime = fixture.Node.Host.RuntimeService;
        var session = (await runtime.TryGetRecoverableSessionAsync(ended.SessionId!, token))!;

        // What a restart loses: the patterns set on the session.
        runtime.SetDisabledToolPatterns(session.SessionId, null);
        Assert.AreEqual(0, runtime.GetDisabledToolPatterns(session.SessionId).Count);
        CollectionAssert.AreEqual(new[] { "shell_command" }, (await runtime.ToolPatternGate!(session, token))?.ToArray(),
            "the node gives them again at the next attachment of the session");
        // A session the run started: the same.
        var child = new CodeAlta.Catalog.SessionViewDescriptor { SessionId = Guid.NewGuid().ToString(), ParentSessionId = session.SessionId, SpaceId = session.SpaceId };
        CollectionAssert.AreEqual(new[] { "shell_command" }, (await runtime.ToolPatternGate!(child, token))?.ToArray());
        // A session of the person's own, not the fleet's: nothing.
        var own = new CodeAlta.Catalog.SessionViewDescriptor { SessionId = Guid.NewGuid().ToString(), SpaceId = session.SpaceId };
        Assert.IsNull(await runtime.ToolPatternGate!(own, token));
    }

    // The longest time of a session in the policy reaches the runs of automations.
    [TestMethod]
    public async Task TheLongestTime_EndsTheRunsUnderWay()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var clock = new NodeClock { Now = Noon };
        var provider = new ScriptedNodeProvider();
        await using var fixture = await AutomationFixture.StartAsync(root, provider, clock, token);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        await client.PutAutomationAsync(fixture.Signed(Definition("Long", "Work a while."), 1), cancellationToken: token);
        var policy = FleetPolicyState.Empty with { Number = 1, LastHash = "h1", Rules = new FleetPolicyRules(MaxSessionMinutes: 1) };
        var automations = fixture.Node.Automations!;
        automations.Policy = new FleetPolicyGuard(fixture.Fleet.DeviceId, () => [new FleetPolicySource("https://fleet.example", policy, null, static _ => true)]);

        async Task<string> RunUnderWayAsync()
        {
            var started = new TaskCompletionSource();
            provider.Turns.Enqueue(ScriptedNodeProvider.WaitThenReply(new TaskCompletionSource().Task, "Never.", started));
            var run = await client.RunAutomationAsync(Id, cancellationToken: token);
            await started.Task.WaitAsync(Patience, token);
            return (await WaitForAsync(() => Task.FromResult(automations.Engine.Runs(Id, 10).FirstOrDefault(candidate => candidate.Id == run.Id && candidate.SessionId is not null)),
                token)).SessionId!;
        }

        var first = await RunUnderWayAsync();
        Assert.AreEqual(0, (await automations.EnforcePolicyAsync(token)).Count, "within its time it goes on");
        clock.Now = Noon.AddMinutes(2);
        CollectionAssert.AreEqual(new[] { first }, (await automations.EnforcePolicyAsync(token)).ToArray(), "past its longest time it is stopped");
    }

    // A stop the node holds (kept from before, read at its start) ends a run under way at the next check, though no newer version
    // of the policy came after the run started.
    [TestMethod]
    public async Task AStopTheNodeHolds_EndsARunUnderWay_WithoutANewerVersion()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var provider = new ScriptedNodeProvider();
        await using var fixture = await AutomationFixture.StartAsync(root, provider, new NodeClock { Now = Noon }, token);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        await client.PutAutomationAsync(fixture.Signed(Definition("Long", "Work a while."), 1), cancellationToken: token);
        FleetPolicyState? policy = null;
        var automations = fixture.Node.Automations!;
        automations.Policy = new FleetPolicyGuard(fixture.Fleet.DeviceId, () => [new FleetPolicySource("https://fleet.example", policy, null, static _ => true)]);

        var started = new TaskCompletionSource();
        provider.Turns.Enqueue(ScriptedNodeProvider.WaitThenReply(new TaskCompletionSource().Task, "Never.", started));
        var run = await client.RunAutomationAsync(Id, cancellationToken: token);
        await started.Task.WaitAsync(Patience, token);
        var sessionId = (await WaitForAsync(() => Task.FromResult(automations.Engine.Runs(Id, 10).FirstOrDefault(candidate => candidate.Id == run.Id && candidate.SessionId is not null)),
            token)).SessionId!;

        // The guard now reads a stop, as a node does that kept one and read it while the run was starting: no event is raised.
        policy = FleetPolicyState.Empty with { Number = 1, LastHash = "h1", Stop = new FleetPolicyStop(FleetPolicyStopScopes.Fleet, [], [], "A leaked key") };
        CollectionAssert.AreEqual(new[] { sessionId }, (await automations.EnforcePolicyAsync(token)).ToArray());
    }

    [TestMethod]
    public async Task Put_RefusesCommandTriggers_UnknownRepositories_AndInvalidDefinitions()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);

        var command = await Assert.ThrowsExactlyAsync<FleetException>(() => client.PutAutomationAsync(fixture.Signed($$"""
            [automations.{{Id}}]
            name = "Watcher"
            prompt = "x"
            triggers = [{ type = "command", command = "echo hi" }]
            """, 1), cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.AutomationInvalid, command.Error.Code);
        StringAssert.Contains(command.Message, "command");

        var project = await Assert.ThrowsExactlyAsync<FleetException>(() => client.PutAutomationAsync(
            fixture.Signed(Definition("Somewhere", "x"), 2, "github.com/nobody/nothing"), cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.ProjectNotFound, project.Error.Code);

        var invalid = await Assert.ThrowsExactlyAsync<FleetException>(() => client.PutAutomationAsync(fixture.Signed("name = 1", 3), cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.AutomationInvalid, invalid.Error.Code);

        var missing = await Assert.ThrowsExactlyAsync<FleetException>(() => client.RunAutomationAsync(Id, cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.AutomationNotFound, missing.Error.Code);
        Assert.AreEqual(0, (await client.ListAutomationsAsync(token)).Automations.Count);
        Assert.IsFalse((await client.RemoveAutomationAsync(Id, cancellationToken: token)).Removed);
    }

    [TestMethod]
    public async Task Put_TakesOnlyANewerVersion_SignedForThisNode_ByTheDeviceOnTheChannel()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token);
        var machine = await fixture.PairAsync(DevicePermissions.View | DevicePermissions.Start | DevicePermissions.Automate, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        using var outsider = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "outsider", "fleet"));

        async Task<string> RefusedAsync(FleetAutomationPutParams parameters)
        {
            var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => client.PutAutomationAsync(parameters, cancellationToken: token));
            Assert.AreEqual(FleetNodeErrorCodes.AutomationInvalid, refused.Error.Code);
            return refused.Message;
        }

        StringAssert.Contains(await RefusedAsync(new FleetAutomationPutParams(Definition("Unsigned", "x"), null)), "signed automations only");
        StringAssert.Contains(await RefusedAsync(fixture.Signed(Definition("Theirs", "x"), 1, signer: outsider)), "another device than the one that gives it");
        StringAssert.Contains(await RefusedAsync(fixture.Signed(Definition("Elsewhere", "x"), 1, machine: outsider.DeviceId)), "another machine");
        var genuine = fixture.Signed(Definition("Mine", "Hello."), 5);
        var altered = genuine with { Signed = genuine.Signed! with { Payload = genuine.Signed.Payload.Replace("Hello.", "Delete it all.", StringComparison.Ordinal) } };
        StringAssert.Contains(await RefusedAsync(altered), "signature does not verify");
        Assert.AreEqual(0, (await client.ListAutomationsAsync(token)).Automations.Count, "Nothing refused was written.");

        Assert.AreEqual("Mine", (await client.PutAutomationAsync(genuine, cancellationToken: token)).Name);
        Assert.AreEqual("Mine", (await client.PutAutomationAsync(genuine, cancellationToken: token)).Name, "The same version given again is taken as it is.");
        StringAssert.Contains(await RefusedAsync(fixture.Signed(Definition("Older", "x"), 4)), "never replaced by one that is not newer");
        Assert.AreEqual("Newer", (await client.PutAutomationAsync(fixture.Signed(Definition("Newer", "Hello."), 6), cancellationToken: token)).Name);
        Assert.AreEqual(6, fixture.Node.Automations!.AssignmentOf(Id)!.Number);

        // Taken away: a version older than the one removed stays refused.
        Assert.IsTrue((await client.RemoveAutomationAsync(Id, cancellationToken: token)).Removed);
        StringAssert.Contains(await RefusedAsync(genuine), "never replaced");
        Assert.AreEqual("Back", (await client.PutAutomationAsync(fixture.Signed(Definition("Back", "Hello."), 7), cancellationToken: token)).Name);

        // The run checks the signature again: a version stored without one, or whose signature was changed, does not run.
        var entry = fixture.Node.Automations.Engine.Snapshot.Find(Id)!;
        var provider = new CodeAlta.Agent.ModelProviderDescriptor(new("scripted"), "Scripted");
        Assert.IsNull(fixture.Node.Automations.Check(entry, provider));
        var file = Path.Combine(fixture.Node.Host.CatalogOptions.FleetRoot, NodeAutomations.AssignmentsFileName);
        var kept = File.ReadAllText(file);
        File.WriteAllText(file, kept.Replace("\"sig\": \"", "\"sig\": \"AAAA", StringComparison.Ordinal));
        await using (var restarted = new NodeAutomations(fixture.Node.Host, new NodeAutomationOptions { WatchRepositories = false }) { DeviceId = fixture.Fleet.DeviceId })
            StringAssert.Contains(restarted.Check(entry, provider), "signature of this automation no longer holds");
    }

    [TestMethod]
    public async Task PairedDevice_WithoutTheAutomatePermission_CannotListNorAssign()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var fixture = await AutomationFixture.StartAsync(root, new ScriptedNodeProvider(), new NodeClock { Now = Noon }, token);
        // The permissions a pairing gives: viewing, driving and starting sessions; not giving automations.
        var machine = await fixture.PairAsync(null, token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => client.ListAutomationsAsync(token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, refused.Error.Code);
    }

    private static string Definition(string name, string prompt) => $$"""
        [automations.{{Id}}]
        name = "{{name}}"
        project = "C:/somewhere/on/the/laptop"
        runs_on = "this-node"
        triggers = [{ type = "hourly", minute = 30 }]
        prompt = '''
        {{prompt}}
        '''
        """;

    private async Task<T> WaitForAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("The node did not get there in time.");
            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>A clock a test sets: the node's schedules see only the times the test gives.</summary>
    private sealed class NodeClock : TimeProvider
    {
        private long _ticks;

        public DateTimeOffset Now
        {
            get => new(Volatile.Read(ref _ticks), TimeSpan.Zero);
            set => Volatile.Write(ref _ticks, value.UtcTicks);
        }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A node with its fleet part and a viewer of another profile, on a root the test owns.</summary>
    private sealed class AutomationFixture : IAsyncDisposable
    {
        private AutomationFixture(NodeHost node, NodeFleet fleet, string viewerFleet)
        {
            Node = node;
            Fleet = fleet;
            ViewerIdentity = DeviceIdentity.LoadOrCreate(viewerFleet);
            ViewerMachines = new PairedMachineStore(viewerFleet);
        }

        public NodeHost Node { get; }

        public NodeFleet Fleet { get; }

        public DeviceIdentity ViewerIdentity { get; }

        public PairedMachineStore ViewerMachines { get; }

        public static async Task<AutomationFixture> StartAsync(NodeTestRoot root, ScriptedNodeProvider provider, TimeProvider clock, CancellationToken cancellationToken,
            Func<CodeAlta.Agent.ModelProviderDescriptor, AutomationCredentialKind>? kind = null)
        {
            var node = await NodeHost.StartAsync(root.Options() with
            {
                ConfigureModelProviders = provider.Register,
                Automations = new NodeAutomationOptions
                {
                    TimeProvider = clock, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false,
                    CredentialKind = kind ?? (static _ => AutomationCredentialKind.Account),
                },
            }, cancellationToken);
            var fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "test-node", "1.0.0");
            return new AutomationFixture(node, fleet, Path.Combine(root.Root, "viewer", "fleet"));
        }

        /// <summary>The parameters of automations.put with a version of the automation the viewer signed for the node.</summary>
        public FleetAutomationPutParams Signed(string definition, long number, string? repository = null, DeviceIdentity? signer = null, string? machine = null)
        {
            var version = new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, null, Id, number, machine ?? Fleet.DeviceId, false, true, ["hourly"], definition,
                repository, null, string.Empty, string.Empty, DateTimeOffset.UtcNow);
            return new FleetAutomationPutParams(definition, repository) { Signed = CodeAlta.Fleet.Server.FleetAutomationSigning.Sign(signer ?? ViewerIdentity, version) };
        }

        /// <summary>Pairs the viewer, then gives it the permissions asked; null keeps those of a pairing.</summary>
        public async Task<PairedMachine> PairAsync(DevicePermissions? permissions, CancellationToken cancellationToken)
        {
            var (offer, _) = Fleet.Service.Pairing.CreateOffer("test-node", Fleet.Addresses, TimeSpan.FromMinutes(1));
            var machine = await FleetNodeClient.PairAsync(offer, ViewerIdentity, "viewer", ViewerMachines, cancellationToken);
            if (permissions is { } granted) Fleet.Service.TrustedDevices.SetPermissions(ViewerIdentity.DeviceId, granted);
            return machine;
        }

        public async ValueTask DisposeAsync()
        {
            await Fleet.DisposeAsync();
            await Node.DisposeAsync();
            ViewerIdentity.Dispose();
        }
    }
}
