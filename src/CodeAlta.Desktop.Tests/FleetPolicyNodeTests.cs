using CodeAlta.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The signed policy on a node (<c>doc/fleet/security-and-privacy.md</c>, policies and the kill switch): a stop that reaches the
/// machine ends the turn of the fleet under way and refuses new work until it is lifted; a stop of other machines does not
/// reach it; the rules of the organization of a space refuse who starts, the providers and the models they do not allow, and
/// say nothing of a space not linked to its server.
/// </summary>
[TestClass]
public sealed class FleetPolicyNodeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AStopEndsTheWorkOfTheFleetAndRefusesMore_RulesRefuseWhatTheyDoNotAllow_AndALiftLetsWorkGoOn()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        FleetPolicyState? policy = null;
        var linked = true;
        fixture.Service.Policy = new FleetPolicyGuard(fixture.NodeIdentity.DeviceId,
            () => [new FleetPolicySource("https://fleet.example", policy, null, _ => linked)]);
        await using var client = await FleetNodeClient.ConnectAsync(await fixture.PairViewerAsync(token), fixture.ViewerIdentity, token);
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Work"), "create-1", token);

        // A turn of the fleet under way when the owner stops every machine.
        var running = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        fixture.Provider.Turns.Enqueue(ScriptedFleetProvider.WaitThenReply(running.Task, "Never.", started));
        Assert.AreEqual("accepted", (await client.SendAsync(created.SessionId, "work", retryKey: "send-1", cancellationToken: token)).Status);
        await started.Task.WaitAsync(Patience, token);
        policy = FleetPolicyState.Empty with { Number = 1, LastHash = "h1", Stop = new FleetPolicyStop(FleetPolicyStopScopes.Fleet, [], [], "A leaked key") };
        var stopped = await fixture.Service.ApplyPolicyAsync(token);
        CollectionAssert.AreEqual(new[] { created.SessionId }, stopped.ToArray(), "the session of the fleet at work is stopped");
        Assert.IsFalse(running.Task.IsCompleted, "it was stopped, not answered");

        var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => client.CreateSessionAsync(new FleetCreateSessionParams(Title: "More"), "create-2", token));
        Assert.AreEqual(FleetNodeErrorCodes.FleetStopped, refused.Error.Code);
        StringAssert.Contains(refused.Message, "A leaked key");
        var send = await Assert.ThrowsExactlyAsync<FleetException>(() => client.SendAsync(created.SessionId, "again", retryKey: "send-2", cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.FleetStopped, send.Error.Code, "no prompt either");

        // A stop of other machines does not reach this one.
        policy = policy with { Number = 2, Stop = new FleetPolicyStop(FleetPolicyStopScopes.Machines, ["another-machine"], []) };
        await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Allowed"), "create-3", token);

        // The rules of the organization of the space.
        policy = policy with { Number = 3, Stop = null, Rules = new FleetPolicyRules(RemoteStarts: false) };
        var nobody = await Assert.ThrowsExactlyAsync<FleetException>(() => client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Remote"), "create-4", token));
        Assert.AreEqual(FleetNodeErrorCodes.PolicyRefused, nobody.Error.Code);
        policy = policy with { Number = 4, Rules = new FleetPolicyRules(Providers: ["approved-only"]) };
        var provider = await Assert.ThrowsExactlyAsync<FleetException>(() => client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Provider"), "create-5", token));
        Assert.AreEqual(FleetNodeErrorCodes.PolicyRefused, provider.Error.Code);
        StringAssert.Contains(provider.Message, "approved-only");
        policy = policy with { Number = 5, Rules = new FleetPolicyRules(Models: ["claude-*"]) };
        var model = await Assert.ThrowsExactlyAsync<FleetException>(() => client.SendAsync(created.SessionId, "with another model", retryKey: "send-3",
            selection: new FleetModelSelection(ModelId: "gpt-6.1"), cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.PolicyRefused, model.Error.Code);

        // A space not linked to the server of the policy: its rules say nothing there.
        linked = false;
        policy = policy with { Number = 6, Rules = new FleetPolicyRules(RemoteStarts: false) };
        await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Personal space"), "create-6", token);

        // Lifted: the work goes on.
        linked = true;
        policy = policy with { Number = 7, Rules = FleetPolicyRules.None };
        await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Again"), "create-7", token);
    }

    [TestMethod]
    public async Task APromptThatSwitchesTheSessionToAProviderTheRulesDoNotAllow_IsRefused()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        new ScriptedFleetProvider { Descriptor = new(new CodeAlta.Agent.ModelProviderId("team-gateway"), "Team gateway", "anthropic") { DefaultModelId = "claude-opus-5-5" } }
            .Register(fixture.Host.ModelProviderRegistry);
        new ScriptedFleetProvider { Descriptor = new(new CodeAlta.Agent.ModelProviderId("personal"), "Personal plan", "codex") { DefaultModelId = "gpt-6.1" } }
            .Register(fixture.Host.ModelProviderRegistry);
        var policy = FleetPolicyState.Empty with { Number = 1, LastHash = "h1", Rules = new FleetPolicyRules(Providers: ["anthropic"]) };
        fixture.Service.Policy = new FleetPolicyGuard(fixture.NodeIdentity.DeviceId, () => [new FleetPolicySource("https://fleet.example", policy, null, static _ => true)]);
        await using var client = await FleetNodeClient.ConnectAsync(await fixture.PairViewerAsync(token), fixture.ViewerIdentity, token);
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Work", Selection: new FleetModelSelection("team-gateway")), "create-1", token);

        // The next prompt names the provider of a personal plan and one of its models: the rules allow neither.
        var switched = await Assert.ThrowsExactlyAsync<FleetException>(() => client.SendAsync(created.SessionId, "go on", retryKey: "send-1",
            selection: new FleetModelSelection("personal", "gpt-6.1"), cancellationToken: token));
        Assert.AreEqual(FleetNodeErrorCodes.PolicyRefused, switched.Error.Code);
        StringAssert.Contains(switched.Message, "anthropic");
        Assert.AreEqual("accepted", (await client.SendAsync(created.SessionId, "go on", retryKey: "send-2", cancellationToken: token)).Status, "its own provider goes on");
    }

    // A stop the node holds (kept from before, read at its start) ends work under way at the next check of the node, though no
    // newer version came after the work started.
    [TestMethod]
    public async Task AStopTheNodeHolds_EndsWorkUnderWay_WithoutANewerVersion()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        FleetPolicyState? policy = null;
        fixture.Service.Policy = new FleetPolicyGuard(fixture.NodeIdentity.DeviceId, () => [new FleetPolicySource("https://fleet.example", policy, null, static _ => true)]);
        await using var client = await FleetNodeClient.ConnectAsync(await fixture.PairViewerAsync(token), fixture.ViewerIdentity, token);
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Work"), "create-1", token);
        var running = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        fixture.Provider.Turns.Enqueue(ScriptedFleetProvider.WaitThenReply(running.Task, "Never.", started));
        await client.SendAsync(created.SessionId, "work", retryKey: "send-1", cancellationToken: token);
        await started.Task.WaitAsync(Patience, token);

        // The guard now reads a stop, as a node does that kept one and read it while this turn was starting: no event is raised.
        policy = FleetPolicyState.Empty with { Number = 1, LastHash = "h1", Stop = new FleetPolicyStop(FleetPolicyStopScopes.Fleet, [], [], "A leaked key") };
        CollectionAssert.AreEqual(new[] { created.SessionId }, (await fixture.Service.EnforcePolicyAsync(token)).ToArray());
        Assert.IsFalse(running.Task.IsCompleted, "it was stopped, not answered");
    }

    [TestMethod]
    public async Task ASessionOfTheFleetPastTheLongestTimeOfItsPolicyIsStopped()
    {
        var token = TestContext.CancellationToken;
        var clock = new ManualClock();
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, null, clock);
        var policy = FleetPolicyState.Empty with { Number = 1, LastHash = "h1", Rules = new FleetPolicyRules(MaxSessionMinutes: 1) };
        fixture.Service.Policy = new FleetPolicyGuard(fixture.NodeIdentity.DeviceId, () => [new FleetPolicySource("https://fleet.example", policy, null, static _ => true)]);
        await using var client = await FleetNodeClient.ConnectAsync(await fixture.PairViewerAsync(token), fixture.ViewerIdentity, token);
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Long"), "create-1", token);
        var running = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        fixture.Provider.Turns.Enqueue(ScriptedFleetProvider.WaitThenReply(running.Task, "Never.", started));
        await client.SendAsync(created.SessionId, "work", retryKey: "send-1", cancellationToken: token);
        await started.Task.WaitAsync(Patience, token);

        Assert.AreEqual(0, (await fixture.Service.EnforceSessionLengthAsync(token)).Count, "within its time it goes on");
        clock.Advance(TimeSpan.FromMinutes(2));
        CollectionAssert.AreEqual(new[] { created.SessionId }, (await fixture.Service.EnforceSessionLengthAsync(token)).ToArray());
        Assert.IsFalse(running.Task.IsCompleted, "it was stopped, not answered");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
