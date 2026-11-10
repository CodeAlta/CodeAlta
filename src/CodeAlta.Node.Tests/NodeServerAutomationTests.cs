using System.Collections.Immutable;
using System.Buffers.Text;
using CodeAlta.Automations;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Node.Tests;

/// <summary>
/// Automations a person gives a machine through CodeAlta Server: the desktop signs and seals each version, the server
/// stores and delivers it, the node verifies it against the device list it anchored, opens it with its own key and fires
/// its schedule; it claims each due time with the server, runs it alone while the server is away, refuses what a server
/// altered, and stops running what a device removed from the list gave it.
/// </summary>
[TestClass]
public sealed class NodeServerAutomationTests
{
    private const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1f02";
    private static readonly TimeSpan Patience = TestTime.Seconds(30);
    private static readonly DateTimeOffset Noon = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Definition = $$"""
        [automations.{{Id}}]
        name = "Half past"
        triggers = [{ type = "hourly", minute = 30 }]
        prompt = "Say hello."
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheServerDeliversASignedVersion_TheNodeRunsIt_ClaimsWithTheServer_AndRunsAloneWhileItIsAway()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var clock = new NodeClock { Now = Noon };
        var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop-fleet"));
        using var desktop = new FleetServerClient(address, desktopKey);
        var setup = await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);
        Assert.AreEqual(NodeExitCodes.Success, await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]));
        var nodeId = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending").Id;
        await desktop.ApproveAsync(nodeId, FleetDeviceKinds.Node, token);

        var provider = new ScriptedNodeProvider();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            ConfigureModelProviders = provider.Register,
            Automations = new NodeAutomationOptions
            {
                TimeProvider = clock, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account,
            },
        }, token);
        var automations = node.Automations!;
        var servers = NodeServers.Start(node, null, "test-node", "1.0.0", Fast);
        try
        {
            await WaitForAsync(async () => (await desktop.ListMachinesAsync(token)).Any(static machine => machine.Online) ? "online" : null, token);
            Assert.AreEqual(nodeId, automations.DeviceId);

            // The desktop signs a version for the node, sealed for it and the person's controllers.
            var list = await desktop.ReadVerifiedDeviceListAsync(null, token);
            var signed = Sign(desktopKey, list, setup.Organization.Id, nodeId, 1);
            Assert.IsFalse(signed.Payload.Contains("Say hello", StringComparison.Ordinal), "the server never reads what it does");
            Assert.AreEqual("pending", (await desktop.PutAutomationAsync(signed, token)).State);

            await WaitForAsync(() => Task.FromResult(automations.AssignmentOf(Id) is { Via: not null, Number: 1 } held ? held : null), token);
            Assert.AreEqual(("Desktop", desktopKey.DeviceId), (automations.AssignmentOf(Id)!.DeviceName, automations.AssignmentOf(Id)!.DeviceId),
                "the name the server gives the controller that signed it, beside its verified id");
            Assert.AreEqual(Noon.AddMinutes(30), automations.Engine.NextDue(Id), "the node fires its schedule");
            await WaitForAsync(async () => (await desktop.ListAutomationsAsync(token)).Automations.SingleOrDefault(static item => item.State == "applied"), token);
            Assert.AreEqual(Noon.AddMinutes(30), (await WaitForAsync(async () => (await desktop.ListAutomationsAsync(token)).Automations
                .SingleOrDefault(static item => item.NextDue is not null), token)).NextDue, "the node reports when it is next due");
            var entry = automations.Engine.Snapshot.Find(Id)!;
            Assert.IsNull(automations.Check(entry, new CodeAlta.Agent.ModelProviderDescriptor(new("scripted"), "Scripted")));

            // A server that alters the version it delivers: the node refuses it, and keeps what it held.
            var held = automations.AssignmentOf(Id)!;
            var altered = Sign(desktopKey, list, setup.Organization.Id, nodeId, 2) is var next
                ? next with { Payload = next.Payload.Replace("\"enabled\":true", "\"enabled\":false", StringComparison.Ordinal) }
                : null!;
            var refused = await automations.AcceptFromServerAsync(held.Via!, list, setup.Organization.Id, altered, _ => (null, "not opened"), token);
            Assert.IsFalse(refused.Applied);
            StringAssert.Contains(refused.Message, "signature");
            Assert.AreEqual(1, automations.AssignmentOf(Id)!.Number);

            // The due time comes: the node claims it with the server, runs it, and reports the run.
            clock.Now = Noon.AddMinutes(30).AddSeconds(5);
            automations.Engine.Tick(clock.Now);
            var reported = await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs
                .FirstOrDefault(static run => run.Status == AutomationRun.Completed), token);
            Assert.AreEqual(("hourly", nodeId, 1L), (reported.Trigger, reported.MachineId, reported.Number));
            Assert.AreEqual((120L, 30L, 0.002, "usd"), (reported.InputTokens, reported.OutputTokens, reported.Cost, reported.CostUnit), "what the session used, as facts");
            Assert.AreEqual(AutomationService.Occurrence(entry.Definition.Triggers[0], Noon.AddMinutes(30)), reported.Occurrence, "the claim of the server keyed the run");
            StringAssert.Contains(provider.LastUserText, "Say hello.");

            // Run now, asked through the server with a request the desktop signs: it runs with the id it names, once.
            var runId = Guid.CreateVersion7().ToString("N");
            var request = FleetAutomationSigning.SignRun(desktopKey, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, setup.Organization.Id, Id, nodeId,
                runId, string.Empty, string.Empty, clock.Now));
            Assert.AreEqual(("requested", runId), (await desktop.RunAutomationAsync(request, token)) is var asked ? (asked.Status, asked.RunId) : default);
            var manual = await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs
                .FirstOrDefault(run => run.RunId == runId && run.Status == AutomationRun.Completed), token);
            Assert.AreEqual(("manual", nodeId), (manual.Trigger, manual.MachineId));
            var link = automations.AssignmentOf(Id)!.Via!;
            var again = await automations.RunFromServerAsync(link, list, setup.Organization.Id, request, token);
            Assert.AreEqual(runId, again.Run?.Id, "the same request is answered by the run it started");
            Assert.AreEqual(1, automations.Engine.Runs(Id, 50).Count(run => run.Id == runId));
            var otherRun = request with { Payload = request.Payload.Replace(runId, Guid.CreateVersion7().ToString("N"), StringComparison.Ordinal) };
            StringAssert.Contains((await automations.RunFromServerAsync(link, list, setup.Organization.Id, otherRun, token)).Refused, "signature");

            // A take-over is not a run, nor a run a take-over; a take-over names a run of autonomy in progress.
            var takeOver = FleetAutomationSigning.SignRun(desktopKey, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, setup.Organization.Id, Id, nodeId,
                runId, string.Empty, string.Empty, clock.Now) { Action = FleetAutomationRunRequest.TakeOverAction });
            StringAssert.Contains((await automations.RunFromServerAsync(link, list, setup.Organization.Id, takeOver, token)).Refused, "takes a run over");
            Assert.AreEqual("the request does not take a run over", automations.TakeOverFromServer(link, list, setup.Organization.Id, request));
            Assert.AreEqual("no run of this automation with autonomy is in progress with this id", automations.TakeOverFromServer(link, list, setup.Organization.Id, takeOver));
            StringAssert.Contains(automations.TakeOverFromServer(link, list, setup.Organization.Id, takeOver with { Signature = request.Signature }), "signature");

            // A try asked through the server is a dry run of the automation on this machine.
            var tryId = Guid.CreateVersion7().ToString("N");
            var tryRequest = FleetAutomationSigning.SignRun(desktopKey, new FleetAutomationRunRequest(FleetAutomationRunRequest.TryVersion, setup.Organization.Id, Id, nodeId,
                tryId, string.Empty, string.Empty, clock.Now) { Action = FleetAutomationRunRequest.TryAction });
            var tried = await automations.RunFromServerAsync(link, list, setup.Organization.Id, tryRequest, token);
            Assert.AreEqual((tryId, AutomationService.TryTrigger, (string?)null), (tried.Run?.Id, tried.Run?.Trigger, tried.Refused));
            await WaitForAsync(() => Task.FromResult(automations.Engine.Runs(Id, 50).FirstOrDefault(run => run.Id == tryId && !run.InProgress)), token);

            // Run now while a budget of the machine holds new runs: the machine answers at once that the run waits for Continue
            // anyway, and reports it so to the server, which the desktop reads; continued, the run goes on.
            var decision = new TaskCompletionSource<CodeAlta.Fleet.Server.FleetBudgetAdmission>(TaskCreationOptions.RunContinuationsAsynchronously);
            automations.AdmitBudget = (_, waits, cancellation) => { waits?.Invoke(); return decision.Task.WaitAsync(cancellation); };
            var heldRunId = Guid.CreateVersion7().ToString("N");
            var heldRequest = FleetAutomationSigning.SignRun(desktopKey, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, setup.Organization.Id, Id, nodeId,
                heldRunId, string.Empty, string.Empty, clock.Now));
            var heldRun = await automations.RunFromServerAsync(link, list, setup.Organization.Id, heldRequest, token).WithinAsync(TestTime.Seconds(20), token);
            Assert.AreEqual((heldRunId, "held"), (heldRun.Run?.Id, heldRun.Run?.Status));
            await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs.FirstOrDefault(run => run.RunId == heldRunId && run.Status == "held"), token);
            decision.SetResult(CodeAlta.Fleet.Server.FleetBudgetAdmission.Admitted);
            await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs
                .FirstOrDefault(run => run.RunId == heldRunId && run.Status == AutomationRun.Completed), token);
            automations.AdmitBudget = null;

            // A long run: while it works, the node renews the lease of its due time with the server.
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Turns.Enqueue(ScriptedNodeProvider.WaitThenReply(gate.Task, "Done.", working));
            var longOccurrence = AutomationService.Occurrence(entry.Definition.Triggers[0], Noon.AddHours(1).AddMinutes(30));
            clock.Now = Noon.AddHours(1).AddMinutes(30).AddSeconds(5);
            automations.Engine.Tick(clock.Now);
            await working.Task.WithinAsync(Patience, token);
            var claimed = await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs.FirstOrDefault(run => run.Occurrence == longOccurrence), token);
            await Task.Delay(50, token);
            clock.Now = clock.Now.AddMinutes(2);
            automations.Engine.Tick(clock.Now);
            await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs
                .FirstOrDefault(run => run.Occurrence == longOccurrence && run.LeaseUntil > claimed.LeaseUntil), token);
            gate.SetResult();
            await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs
                .FirstOrDefault(run => run.Occurrence == longOccurrence && run.Status == AutomationRun.Completed), token);

            // The server is away when the next time comes: the node runs it alone.
            await server.DisposeAsync();
            server = null;
            clock.Now = Noon.AddHours(2).AddMinutes(30).AddSeconds(5);
            automations.Engine.Tick(clock.Now);
            await WaitForAsync(() => Task.FromResult(automations.Engine.Runs(Id, 10).FirstOrDefault(static run => run is { Status: AutomationRun.Completed, StartedAt.Hour: 14 })), token);
        }
        finally
        {
            await servers.DisposeAsync();
            if (server is not null) await server.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AnEventOfAPool_WhoseGrantWasLost_IsClaimedAgainByTheSameRun_AClaimLostToAnotherMachineEnds_AndALostTagStopsARunByHand()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var clock = new NodeClock { Now = Noon };
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions
            {
                TimeProvider = clock, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account, PoolWait = TimeSpan.Zero,
            },
        }, token);
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var tablet = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet"));
        using var self = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var list = DeviceList(laptop, tablet, self, removeTablet: false);
        var automations = node.Automations!;
        automations.DeviceId = self.DeviceId;
        automations.ServerDevices = _ => list;
        IReadOnlyList<string> tags = ["gpu"];
        automations.OwnTags = () => tags;
        var outcome = await automations.AcceptFromServerAsync("link-1", list, "org", SignPooled(laptop, list, "org", self.DeviceId, 1, ["gpu"]),
            version => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, out var content, out var reason)
                ? (content, null) : (null, reason), token);
        Assert.IsTrue(outcome.Applied, outcome.Message);

        // The server keeps one run per event; its answer to the first claim is lost on the way back.
        var held = new System.Collections.Concurrent.ConcurrentDictionary<string, (string Machine, string Run)>(StringComparer.Ordinal);
        var lose = 1;
        automations.ServerClaim = (_, request, _) =>
        {
            var holder = held.GetOrAdd(request.Occurrence, (self.DeviceId, request.RunId));
            if (Interlocked.Exchange(ref lose, 0) == 1) return Task.FromResult<FleetAutomationClaimResult?>(null);
            return Task.FromResult<FleetAutomationClaimResult?>(holder == (self.DeviceId, request.RunId) ? new(true, null)
                : new(false, "taken") { TakenBy = holder.Machine });
        };
        var claims = automations.Claims!;
        const string Event = "event:issue:0123456789abcdef#42";
        Assert.AreEqual(AutomationPoolClaim.Later, (await claims.ClaimEventAsync(Id, Event, "run-1", clock.Now, token)).Outcome, "no answer: looked at again later");
        var again = await claims.ClaimEventAsync(Id, Event, "run-2", clock.Now, token);
        Assert.AreEqual((AutomationPoolClaim.Granted, "run-1"), (again.Outcome, again.RunId), "the grant whose answer was lost is asked again by the same run, and runs");

        // Another machine runs an event: the claim of this one ends, and still says it was seen.
        held["event:issue:0123456789abcdef#43"] = ("other-node", "run-x");
        Assert.AreEqual(AutomationPoolClaim.Taken, (await claims.ClaimEventAsync(Id, "event:issue:0123456789abcdef#43", "run-3", clock.Now, token)).Outcome);
        var file = new AutomationClaimStore(Path.Combine(node.Host.CatalogOptions.FleetRoot, NodeAutomations.ClaimsFileName), "test");
        Assert.IsNotNull(file.Find(Id, "event:issue:0123456789abcdef#43")?.EndedAt, "a claim lost to another machine is not left unfinished");
        Assert.AreEqual(AutomationPoolClaim.Taken, (await claims.ClaimEventAsync(Id, "event:issue:0123456789abcdef#43", "run-4", clock.Now, token)).Outcome);

        // The machine loses its tag: Run now, through a stale list of machines, is refused at the last check before a run.
        var entry = automations.Engine.Snapshot.Find(Id)!;
        var provider = new CodeAlta.Agent.ModelProviderDescriptor(new("scripted"), "Scripted");
        Assert.IsNull(automations.Check(entry, provider));
        tags = [];
        StringAssert.Contains(automations.Check(entry, provider), "no longer has the tag gpu");
        Assert.AreEqual(AutomationPoolClaim.Later, (await claims.ClaimEventAsync(Id, "event:issue:0123456789abcdef#44", "run-5", clock.Now, token)).Outcome);
    }

    [TestMethod]
    public async Task APooledVersion_RunsWhileTheMachineHasItsTags_OnlyWithTheClaimOfItsServer_AndTwoMembersRunADueTimeOnce()
    {
        var token = TestContext.CancellationToken;
        using var rootA = new NodeTestRoot();
        using var rootB = new NodeTestRoot();
        var clock = new NodeClock { Now = Noon };
        var providerA = new ScriptedNodeProvider();
        var providerB = new ScriptedNodeProvider();
        NodeAutomationOptions Options() => new()
        {
            TimeProvider = clock, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account, PoolWait = TimeSpan.Zero,
        };
        await using var nodeA = await NodeHost.StartAsync(rootA.Options() with { ConfigureModelProviders = providerA.Register, Automations = Options() }, token);
        await using var nodeB = await NodeHost.StartAsync(rootB.Options() with { ConfigureModelProviders = providerB.Register, Automations = Options() }, token);
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(rootA.Root, "laptop"));
        using var tablet = DeviceIdentity.LoadOrCreate(Path.Combine(rootA.Root, "tablet"));
        using var selfA = DeviceIdentity.LoadOrCreate(Path.Combine(rootA.Root, "self"));
        using var selfB = DeviceIdentity.LoadOrCreate(Path.Combine(rootB.Root, "self"));
        var (listA, listB) = (DeviceList(laptop, tablet, selfA, removeTablet: false), DeviceList(laptop, tablet, selfB, removeTablet: false));
        var (a, b) = (nodeA.Automations!, nodeB.Automations!);
        (a.DeviceId, b.DeviceId) = (selfA.DeviceId, selfB.DeviceId);
        a.ServerDevices = _ => listA;
        b.ServerDevices = _ => listB;
        IReadOnlyList<string> tagsA = ["gpu", "linux"];
        IReadOnlyList<string> tagsB = [];
        a.OwnTags = () => tagsA;
        b.OwnTags = () => tagsB;

        // The server: one claim per due time, whichever machine asks first; away while it is away.
        var claims = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var asked = new System.Collections.Concurrent.ConcurrentQueue<(string Machine, string Occurrence)>();
        var away = false;
        Func<string, FleetAutomationClaimRequest, CancellationToken, Task<FleetAutomationClaimResult?>> Server(string machine) => (_, request, _) =>
        {
            asked.Enqueue((machine, request.Occurrence));
            if (Volatile.Read(ref away)) return Task.FromResult<FleetAutomationClaimResult?>(null);
            var holder = claims.GetOrAdd(request.Occurrence, machine);
            return Task.FromResult<FleetAutomationClaimResult?>(holder == machine ? new(true, null) : new(false, "taken") { TakenBy = holder });
        };
        a.ServerClaim = Server(selfA.DeviceId);
        b.ServerClaim = Server(selfB.DeviceId);

        // Both are given the version of the pool, which signs its tags.
        foreach (var (node, self, list) in new[] { (a, selfA, listA), (b, selfB, listB) })
        {
            var outcome = await node.AcceptFromServerAsync("link-1", list, "org", SignPooled(laptop, list, "org", self.DeviceId, 1, ["gpu", "linux"]),
                version => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, out var content, out var reason)
                    ? (content, null) : (null, reason), token);
            Assert.IsTrue(outcome.Applied, outcome.Message);
            CollectionAssert.AreEqual(new[] { "gpu", "linux" }, node.AssignmentOf(Id)!.Tags!.ToArray());
        }

        // The machine without the tags arms nothing and claims nothing; the other claims the due time and runs it.
        Assert.IsNull(a.PoolProblem(Id));
        StringAssert.Contains(b.PoolProblem(Id), "gpu, linux");
        var first = AutomationService.Occurrence(a.Engine.Snapshot.Find(Id)!.Definition.Triggers[0], Noon.AddMinutes(30));
        clock.Now = Noon.AddMinutes(30).AddSeconds(5);
        a.Engine.Tick(clock.Now);
        b.Engine.Tick(clock.Now);
        await WaitForAsync(() => Task.FromResult(a.Engine.Runs(Id, 10).FirstOrDefault(static run => run.Status == AutomationRun.Completed)), token);
        Assert.IsFalse(asked.Any(item => item.Machine == selfB.DeviceId), "a machine without the tags of the pool does not claim");
        Assert.AreEqual(0, b.Engine.Runs(Id, 10).Count);
        Assert.AreEqual(selfA.DeviceId, claims[first]);

        // It gains them: both claim the next due time, one runs it, the other records nothing of it.
        tagsB = ["GPU", "linux", "prod"];
        Assert.IsNull(b.PoolProblem(Id), "tags compare without case, and more tags are no matter");
        var second = AutomationService.Occurrence(a.Engine.Snapshot.Find(Id)!.Definition.Triggers[0], Noon.AddHours(1).AddMinutes(30));
        clock.Now = Noon.AddHours(1).AddMinutes(30).AddSeconds(5);
        a.Engine.Tick(clock.Now);
        b.Engine.Tick(clock.Now);
        await WaitForAsync(() => Task.FromResult(asked.Count(item => item.Occurrence == second) >= 2 ? "both claimed" : null), token);
        var winner = claims[second] == selfA.DeviceId ? a : b;
        var loser = winner == a ? b : a;
        await WaitForAsync(() => Task.FromResult(winner.Engine.Runs(Id, 10).FirstOrDefault(static run => run is { Status: AutomationRun.Completed, StartedAt.Hour: 13 })), token);
        await Task.Delay(200, token);
        Assert.AreEqual(1, a.Engine.Runs(Id, 10).Concat(b.Engine.Runs(Id, 10)).Count(static run => run.StartedAt.Hour == 13), "two members, one due time: one run");
        Assert.AreEqual(0, loser.Engine.Runs(Id, 10).Count(static run => run.StartedAt.Hour == 13), "the machine that did not get it records nothing");
        var loserRoot = (loser == a ? nodeA : nodeB).Host.CatalogOptions.FleetRoot;
        Assert.IsNotNull(new AutomationClaimStore(Path.Combine(loserRoot, NodeAutomations.ClaimsFileName), "test").Find(Id, second)?.EndedAt,
            "its own claim of the time ends: it is neither renewed nor kept unfinished");

        // The server is away: no member runs the due time, and each records why it skipped it.
        Volatile.Write(ref away, true);
        clock.Now = Noon.AddHours(2).AddMinutes(30).AddSeconds(5);
        a.Engine.Tick(clock.Now);
        b.Engine.Tick(clock.Now);
        foreach (var node in new[] { a, b })
        {
            var skipped = await WaitForAsync(() => Task.FromResult(node.Engine.Runs(Id, 10).FirstOrDefault(static run => run.StartedAt.Hour == 14 && !run.InProgress)), token);
            Assert.AreEqual(AutomationRun.Skipped, skipped.Status);
            StringAssert.Contains(skipped.Message, "did not answer");
        }

        Assert.AreEqual(2, a.Engine.Runs(Id, 10).Concat(b.Engine.Runs(Id, 10)).Count(static run => run.Status == AutomationRun.Completed), "two due times ran, once each");
    }

    // A version given to the machines of a pool: of the format of a pool, with the tags it signs.
    private static FleetSignedAutomationVersion SignPooled(DeviceIdentity signer, FleetDeviceListState list, string organization, string machine, long number, IReadOnlyList<string> tags)
    {
        var sealedText = FleetAutomationSigning.Seal(new FleetAutomationContent(Definition, null), Id, number, machine, list, signer);
        return FleetAutomationSigning.Sign(signer, new FleetAutomationVersion(FleetAutomationVersion.PoolVersion, organization, Id, number, machine, false, true, ["hourly"],
            null, null, sealedText, string.Empty, string.Empty, DateTimeOffset.UtcNow) { Tags = tags });
    }

    [TestMethod]
    public async Task APersonalMachineGivenToAnotherPerson_TakesNoRunRequestFromItsFormerOwner()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions { TimeProvider = new NodeClock { Now = Noon }, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false },
        }, token);
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var tablet = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet"));
        using var self = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var automations = node.Automations!;
        automations.DeviceId = self.DeviceId;
        var list = DeviceList(laptop, tablet, self, removeTablet: false);
        var access = FleetAccessState.Empty with { OrganizationId = "org", Number = 1, Owners = ["p1"] };
        automations.ServerAccess = _ => access;
        var now = Noon;
        var run = FleetAutomationSigning.SignRun(laptop, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, "org", Id, self.DeviceId, "run-1",
            string.Empty, string.Empty, now));
        var takeOver = FleetAutomationSigning.SignRun(laptop, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, "org", Id, self.DeviceId, "run-1",
            string.Empty, string.Empty, now) { Action = FleetAutomationRunRequest.TakeOverAction });

        // The person the device list names owns the machine: the requests of their device verify (the machine holds no such automation).
        StringAssert.Contains((await automations.RunFromServerAsync("link-1", list, "org", run, token)).Refused, "holds no such automation");
        StringAssert.Contains(automations.TakeOverFromServer("link-1", list, "org", takeOver), "holds no such automation");

        // Given to Mia (p2): the signed document names her, and the requests of the former owner are refused.
        access = access with { Number = 2, PersonalMachines = access.PersonalMachines.Add(self.DeviceId, "p2") };
        StringAssert.Contains((await automations.RunFromServerAsync("link-1", list, "org", run, token)).Refused, "another person than the owner");
        StringAssert.Contains(automations.TakeOverFromServer("link-1", list, "org", takeOver), "another person than the owner");
    }

    [TestMethod]
    public void AnotherPersonTakesARunOver_WhenTheSignedAccessDocumentSaysSo()
    {
        var access = FleetAccessState.Empty with
        {
            Teams = new Dictionary<string, FleetAccessTeam> { ["infra"] = new("infra", ["mia"], ["max"], false) }.ToImmutableDictionary(StringComparer.Ordinal),
            Machines = new Dictionary<string, string> { ["team-node"] = "infra" }.ToImmutableDictionary(StringComparer.Ordinal),
            Suspended = ["old"],
            Approvers = FleetAccessState.ApproversOf([new FleetAccessApprovers("a1", ["oncall", "old", "temp"])]),
            ApproversUntil = FleetAccessState.UntilOf([new FleetAccessApprovers("a1", ["temp"]) { Until = new Dictionary<string, DateTimeOffset> { ["temp"] = Noon.AddHours(1) } }]),
        };
        var takeOver = new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, "org", "a1", "node", "run-1", "d", "k", Noon) { Action = FleetAutomationRunRequest.TakeOverAction };

        Assert.IsTrue(NodeAutomations.MayTakeOver(access, "node", takeOver, "oncall", Noon), "an approver the document names");
        Assert.IsFalse(NodeAutomations.MayTakeOver(access, "node", takeOver with { AutomationId = "a2" }, "oncall", Noon), "for that automation only");
        Assert.IsFalse(NodeAutomations.MayTakeOver(access, "node", takeOver, "old", Noon), "a suspended person");
        Assert.IsTrue(NodeAutomations.MayTakeOver(access, "team-node", takeOver with { AutomationId = "a2" }, "max", Noon), "a member of the team of a team machine");
        Assert.IsFalse(NodeAutomations.MayTakeOver(access, "node", takeOver with { AutomationId = "a2" }, "max", Noon), "not on a machine of another");
        Assert.IsFalse(NodeAutomations.MayTakeOver(access, "node", takeOver with { Action = null }, "oncall", Noon), "a run by hand stays the owner's");
        Assert.IsFalse(NodeAutomations.MayTakeOver(null, "node", takeOver, "oncall", Noon), "no document: nobody else");
        Assert.IsTrue(NodeAutomations.MayTakeOver(access, "node", takeOver, "temp", Noon.AddMinutes(59)), "granted until a time, before it");
        Assert.IsFalse(NodeAutomations.MayTakeOver(access, "node", takeOver, "temp", Noon.AddHours(1)), "and not from it, by the clock of the machine");
    }

    [TestMethod]
    public async Task AVersionFromAControllerTheListRemovedDoesNotRun_NorOneOfAnotherPerson()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions
            {
                TimeProvider = new NodeClock { Now = Noon }, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account,
            },
        }, token);
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var tablet = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet"));
        using var self = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var automations = node.Automations!;
        automations.DeviceId = self.DeviceId;
        var list = DeviceList(laptop, tablet, self, removeTablet: false);

        // The tablet, a controller of the person, gives the automation: it runs.
        var outcome = await automations.AcceptFromServerAsync("link-1", list, "org", Sign(tablet, list, "org", self.DeviceId, 1),
            version => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, out var content, out var reason)
                ? (content, null) : (null, reason), token);
        Assert.IsTrue(outcome.Applied, outcome.Message);
        var entry = automations.Engine.Snapshot.Find(Id)!;
        var provider = new CodeAlta.Agent.ModelProviderDescriptor(new("scripted"), "Scripted");
        Assert.IsNull(automations.Check(entry, provider));

        // The tablet is removed from the device list: what it gave stops running.
        var removed = DeviceList(laptop, tablet, self, removeTablet: true);
        automations.ServerDevices = _ => removed;
        StringAssert.Contains(automations.Check(entry, provider), "no longer a controller device");
        // Nor does it give anything more.
        var again = await automations.AcceptFromServerAsync("link-1", removed, "org", Sign(tablet, list, "org", self.DeviceId, 2), _ => (null, "unused"), token);
        Assert.IsFalse(again.Applied);
        StringAssert.Contains(again.Message, "not signed by a controller device");

        // A version sealed for another machine does not open here.
        automations.ServerDevices = _ => list;
        var elsewhere = await automations.AcceptFromServerAsync("link-1", list, "org", Sign(laptop, list, "org", self.DeviceId, 3),
            _ => (null, "the definition was not sealed for this machine"), token);
        Assert.IsFalse(elsewhere.Applied);
        StringAssert.Contains(elsewhere.Message, "does not open");
        Assert.AreEqual(1, automations.AssignmentOf(Id)!.Number, "nothing refused replaced what the node holds");
    }

    [TestMethod]
    public async Task OnATeamMachine_RunsUseAccountCredentialsOnly_AndAnAutomationOfTheTeamRunsAsOneOfItsServiceIdentities()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions
            {
                TimeProvider = new NodeClock { Now = Noon }, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false,
                CredentialKind = static provider => provider.ProviderId.Value == "plan" ? AutomationCredentialKind.Personal : AutomationCredentialKind.Account,
            },
        }, token);
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var tablet = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet"));
        using var self = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var automations = node.Automations!;
        automations.DeviceId = self.DeviceId;
        var list = DeviceList(laptop, tablet, self, removeTablet: false);
        automations.ServerDevices = _ => list;
        var team = new FleetAccessTeam("infra", ["p1"], [], false);
        var access = FleetAccessState.Empty with
        {
            OrganizationId = "org", Number = 1, Teams = FleetAccessState.Empty.Teams.Add("infra", team), Machines = FleetAccessState.Empty.Machines.Add(self.DeviceId, "infra"),
        };
        automations.ServerAccess = _ => access;
        var sealedText = FleetAutomationSigning.Seal(new FleetAutomationContent(Definition, null), Id, 1, self.DeviceId, list, tablet);
        var signed = FleetAutomationSigning.Sign(tablet, new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, "org", Id, 1, self.DeviceId, false, true, ["hourly"],
            null, null, sealedText, string.Empty, string.Empty, DateTimeOffset.UtcNow) { Scope = "team:infra", Provider = "gateway" });
        var outcome = await automations.AcceptFromServerAsync("link-1", list, "org", signed,
            version => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, out var content, out var reason)
                ? (content, null) : (null, reason), token);
        Assert.IsTrue(outcome.Applied, outcome.Message);
        var entry = automations.Engine.Snapshot.Find(Id)!;
        var account = new CodeAlta.Agent.ModelProviderDescriptor(new("gateway"), "Gateway");
        var plan = new CodeAlta.Agent.ModelProviderDescriptor(new("plan"), "Plan");

        // Without a service identity of the team on, an automation of the team does not run.
        StringAssert.Contains(automations.Check(entry, account), "service identit");
        access = access with { Teams = access.Teams.SetItem("infra", team with { Services = ["svc-infra"] }) };
        Assert.IsNull(automations.Check(entry, account), "it runs as the service identity of the team");
        // A personal plan never runs on a machine of a team, whatever the machine's own policy says.
        StringAssert.Contains(automations.Check(entry, plan), "account credentials only");
        // The machine leaves the team: an automation of the team no longer runs there.
        access = access with { Machines = access.Machines.Remove(self.DeviceId) };
        Assert.IsNotNull(automations.Check(entry, account));
    }

    [TestMethod]
    public async Task AMachineOfATeamOpensWhatAMaintainerOfAnotherPersonSealed_AndAnyMaintainerRunsItByHand_NoMember()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions
            {
                TimeProvider = new NodeClock { Now = Noon }, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account,
            },
        }, token);
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var mia = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "mia"));
        using var ada = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "ada"));
        using var max = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "max"));
        using var self = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var time = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
        var entries = new List<FleetSignedDeviceListEntry>();
        void Add(DeviceIdentity device, string kind, string? owner)
        {
            var payload = FleetDeviceListEntries.SerializePayload(new FleetDeviceListEntryPayload(1, entries.Count == 0 ? null : "org", entries.Count + 1,
                entries.Count == 0 ? null : FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add, device.DeviceId,
                Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()), kind, owner, laptop.DeviceId, time));
            entries.Add(new FleetSignedDeviceListEntry(payload, Base64Url.EncodeToString(laptop.SignData(FleetDeviceListEntries.GetSignedBytes(payload)))));
        }

        Add(laptop, FleetDeviceKinds.Controller, null);
        Add(self, FleetDeviceKinds.Node, "p1");
        Add(mia, FleetDeviceKinds.Controller, "p2");
        Add(ada, FleetDeviceKinds.Controller, "p4");
        Add(max, FleetDeviceKinds.Controller, "p3");
        var list = FleetDeviceListVerifier.VerifyOrThrow(entries, "org");
        var automations = node.Automations!;
        automations.DeviceId = self.DeviceId;
        automations.ServerDevices = _ => list;
        // The machine p1 enrolled is the team's: Mia and Ada maintain it, Max is a member.
        var access = FleetAccessState.Empty with
        {
            OrganizationId = "org", Number = 1, Teams = FleetAccessState.Empty.Teams.Add("infra", new FleetAccessTeam("infra", ["p2", "p4"], ["p3"], false, ["svc"])),
            Machines = FleetAccessState.Empty.Machines.Add(self.DeviceId, "infra"),
        };
        automations.ServerAccess = _ => access;
        var sealedText = FleetAutomationSigning.SealForTeam(new FleetAutomationContent(Definition, null), Id, 1, self.DeviceId, "infra", list, access, mia);
        var signed = FleetAutomationSigning.Sign(mia, new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, "org", Id, 1, self.DeviceId, false, true, ["hourly"],
            null, null, sealedText, string.Empty, string.Empty, DateTimeOffset.UtcNow) { Scope = "team:infra", Provider = "gateway" });
        var outcome = await automations.AcceptFromServerAsync("link-1", list, "org", signed,
            version => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, access, out var content, out var reason)
                ? (content, null) : (null, reason), token);
        Assert.IsTrue(outcome.Applied, outcome.Message);
        Assert.IsNull(automations.Check(automations.Engine.Snapshot.Find(Id)!, new CodeAlta.Agent.ModelProviderDescriptor(new("gateway"), "Gateway")));

        FleetSignedAutomationRunRequest Ask(DeviceIdentity device) => FleetAutomationSigning.SignRun(device, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion,
            "org", Id, self.DeviceId, Guid.NewGuid().ToString("N"), string.Empty, string.Empty, Noon));
        var (_, byAda) = await automations.RunFromServerAsync("link-1", list, "org", Ask(ada), token);
        Assert.IsFalse(byAda?.Contains("another person", StringComparison.Ordinal) == true, byAda);
        var (_, byMax) = await automations.RunFromServerAsync("link-1", list, "org", Ask(max), token);
        StringAssert.Contains(byMax, "another person", "a member does not run what the team runs");

        // Granted automation.run, as the signed access document names him: the machine takes his request; once the grant ends, no more.
        var granted = access with
        {
            Runners = FleetAccessState.ApproversOf([new FleetAccessApprovers(Id, ["p3"])]),
            RunnersUntil = FleetAccessState.UntilOf([new FleetAccessApprovers(Id, ["p3"]) { Until = new Dictionary<string, DateTimeOffset> { ["p3"] = Noon.AddHours(1) } }]),
        };
        automations.ServerAccess = _ => granted;
        var (_, byGrant) = await automations.RunFromServerAsync("link-1", list, "org", Ask(max), token);
        Assert.IsFalse(byGrant?.Contains("another person", StringComparison.Ordinal) == true, byGrant);
        automations.ServerAccess = _ => granted with { RunnersUntil = FleetAccessState.UntilOf([new FleetAccessApprovers(Id, ["p3"]) { Until = new Dictionary<string, DateTimeOffset> { ["p3"] = Noon.AddHours(-1) } }]) };
        StringAssert.Contains((await automations.RunFromServerAsync("link-1", list, "org", Ask(max), token)).Refused, "another person", "a grant that ended runs nothing");
        automations.ServerAccess = _ => access;

        // Ada opens it in her editor and saves it: her newer version, sealed for the team, is taken; a member's is not.
        FleetSignedAutomationVersion Edited(DeviceIdentity device, long number, string name) => FleetAutomationSigning.Sign(device, new FleetAutomationVersion(
            FleetAutomationVersion.CurrentVersion, "org", Id, number, self.DeviceId, false, true, ["hourly"], null, null,
            FleetAutomationSigning.SealForTeam(new FleetAutomationContent(Definition.Replace("Half past", name, StringComparison.Ordinal), null), Id, number, self.DeviceId,
                "infra", list, access, device), string.Empty, string.Empty, DateTimeOffset.UtcNow) { Scope = "team:infra", Provider = "gateway" });
        (FleetAutomationContent?, string?) Open(FleetAutomationVersion version)
            => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, access, out var content, out var reason)
                ? (content, null) : (null, reason);
        var adaVersion = Edited(ada, 2, "Half past, by Ada");
        var byAdaEdit = await automations.AcceptFromServerAsync("link-1", list, "org", adaVersion, Open, token);
        Assert.IsTrue(byAdaEdit.Applied, byAdaEdit.Message);
        Assert.AreEqual("Half past, by Ada", automations.Engine.Snapshot.Find(Id)!.Definition.Name);
        Assert.IsTrue(FleetAutomationVersionDocuments.TryParse(adaVersion.Payload, out var adas));
        Assert.IsTrue(FleetAutomationSigning.TryOpen(adas.Sealed, Id, 2, self.DeviceId, mia, list, access, out var forMia, out var notForMia), notForMia);
        StringAssert.Contains(forMia.Definition, "by Ada", "the other maintainers open what she saved");
        Assert.ThrowsExactly<ArgumentException>(() => Edited(max, 3, "Half past, by Max"), "a member does not seal for the team");
        var byMaxEdit = await automations.AcceptFromServerAsync("link-1", list, "org", FleetAutomationSigning.Sign(max, new FleetAutomationVersion(
            FleetAutomationVersion.CurrentVersion, "org", Id, 3, self.DeviceId, false, true, ["hourly"], null, null,
            FleetAutomationSigning.Seal(new FleetAutomationContent(Definition, null), Id, 3, self.DeviceId, list, max), string.Empty, string.Empty, DateTimeOffset.UtcNow)
        {
            Scope = "team:infra", Provider = "gateway",
        }), Open, token);
        Assert.IsFalse(byMaxEdit.Applied, "a member gives the team's automation no version");
        Assert.AreEqual("Half past, by Ada", automations.Engine.Snapshot.Find(Id)!.Definition.Name);
    }

    [TestMethod]
    public async Task AMachineOfTheOrganization_RunsItsAutomationsAsItsServiceIdentity_WithAccountCredentialsOnly()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions
            {
                TimeProvider = new NodeClock { Now = Noon }, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false,
                CredentialKind = static provider => provider.ProviderId.Value == "plan" ? AutomationCredentialKind.Personal : AutomationCredentialKind.Account,
            },
        }, token);
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var tablet = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet"));
        using var self = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var automations = node.Automations!;
        automations.DeviceId = self.DeviceId;
        var list = DeviceList(laptop, tablet, self, removeTablet: false);
        automations.ServerDevices = _ => list;
        var access = FleetAccessState.Empty with { OrganizationId = "org", Number = 1, Owners = ["p1"], OrganizationMachines = [self.DeviceId] };
        automations.ServerAccess = _ => access;
        var sealedText = FleetAutomationSigning.SealForOrganization(new FleetAutomationContent(Definition, null), Id, 1, self.DeviceId, list, access, tablet);
        var signed = FleetAutomationSigning.Sign(tablet, new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, "org", Id, 1, self.DeviceId, false, true, ["hourly"],
            null, null, sealedText, string.Empty, string.Empty, DateTimeOffset.UtcNow) { Scope = FleetAutomationVersion.OrganizationScope, Provider = "gateway" });
        var outcome = await automations.AcceptFromServerAsync("link-1", list, "org", signed,
            version => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, access, out var content, out var reason)
                ? (content, null) : (null, reason), token);
        Assert.IsTrue(outcome.Applied, outcome.Message);
        var entry = automations.Engine.Snapshot.Find(Id)!;
        var account = new CodeAlta.Agent.ModelProviderDescriptor(new("gateway"), "Gateway");
        StringAssert.Contains(automations.Check(entry, account), "service identit");
        Assert.IsNull(automations.ActingAs("link-1", Id), "without one, it acts as nobody");
        access = access with { OrganizationServices = ["svc-org"] };
        Assert.IsNull(automations.Check(entry, account));
        Assert.AreEqual("svc-org", automations.ActingAs("link-1", Id), "its runs say they acted as the service identity of the organization");
        StringAssert.Contains(automations.Check(entry, new CodeAlta.Agent.ModelProviderDescriptor(new("plan"), "Plan")), "account credentials only");
    }

    [TestMethod]
    public async Task ARunOfAnAutomationOfTheOrganization_IsNotTheWorkOfTheOwnerOfTheMachine_AndResolvesNoneOfTheOwnersValues()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var provider = new ScriptedNodeProvider();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            ConfigureModelProviders = provider.Register,
            Automations = new NodeAutomationOptions
            {
                TimeProvider = new NodeClock { Now = Noon }, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account,
            },
        }, token);
        await using var fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "test-node", "1.0.0");
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var tablet = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet"));
        using var self = DeviceIdentity.LoadOrCreate(node.Host.CatalogOptions);
        var automations = node.Automations!;
        var list = DeviceList(laptop, tablet, self, removeTablet: false);
        automations.ServerDevices = _ => list;
        var access = FleetAccessState.Empty with { OrganizationId = "org", Number = 1, Owners = ["p1"], OrganizationMachines = [self.DeviceId], OrganizationServices = ["svc-org"] };
        automations.ServerAccess = _ => access;
        var sealedText = FleetAutomationSigning.SealForOrganization(new FleetAutomationContent(Definition, null), Id, 1, self.DeviceId, list, access, tablet);
        var signed = FleetAutomationSigning.Sign(tablet, new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, "org", Id, 1, self.DeviceId, false, true, ["hourly"],
            null, null, sealedText, string.Empty, string.Empty, DateTimeOffset.UtcNow) { Scope = FleetAutomationVersion.OrganizationScope, Provider = "scripted" });
        var outcome = await automations.AcceptFromServerAsync("link-1", list, "org", signed,
            version => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, access, out var content, out var reason)
                ? (content, null) : (null, reason), token);
        Assert.IsTrue(outcome.Applied, outcome.Message);

        // A run of it starts a session that nobody attends and that is not the owner's: it resolves none of the owner's own values.
        var run = await automations.RunAsync(new CodeAlta.Fleet.NodeApi.FleetAutomationCaller(laptop.DeviceId, "laptop"), Id, token);
        var deadline = DateTime.UtcNow + Patience;
        string? sessionId = null;
        while (DateTime.UtcNow < deadline && (sessionId = automations.Engine.Runs(Id, 10).FirstOrDefault(candidate => candidate.Id == run.Id)?.SessionId) is null)
            await Task.Delay(50, token);
        Assert.IsNotNull(sessionId, "the run started a session");
        Assert.IsFalse(node.Host.Identities.IsOwnerWork?.Invoke(sessionId) ?? true, "a run of the organization is not the owner's work");
    }

    [TestMethod]
    public async Task AnAutomationTheDocumentGivesToAnotherOwner_StopsOnTheMachineOfItsFormerOwner_WhoStillTakesItAway()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions
            {
                TimeProvider = new NodeClock { Now = Noon }, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account,
            },
        }, token);
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var tablet = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet"));
        using var self = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var automations = node.Automations!;
        automations.DeviceId = self.DeviceId;
        var list = DeviceList(laptop, tablet, self, removeTablet: false);
        automations.ServerDevices = _ => list;
        var access = FleetAccessState.Empty with { OrganizationId = "org", Number = 1, Owners = ["p1"] };
        automations.ServerAccess = _ => access;
        (FleetAutomationContent?, string?) Open(FleetAutomationVersion version)
            => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, out var content, out var reason) ? (content, null) : (null, reason);
        Assert.IsTrue((await automations.AcceptFromServerAsync("link-1", list, "org", Sign(tablet, list, "org", self.DeviceId, 1), Open, token)).Applied);
        var entry = automations.Engine.Snapshot.Find(Id)!;
        var provider = new CodeAlta.Agent.ModelProviderDescriptor(new("gateway"), "Gateway");
        Assert.IsNull(automations.Check(entry, provider));

        // The signed document says p1 gave it to p2: it stops here, and p1 gives it no new version, only its removal.
        access = access with { Automations = access.Automations.Add(Id, new FleetAccessAutomation(Id, FleetPrincipalKinds.Person, "p2", true)) };
        StringAssert.Contains(automations.Check(entry, provider), "another owner");
        var again = await automations.AcceptFromServerAsync("link-1", list, "org", Sign(tablet, list, "org", self.DeviceId, 2), Open, token);
        Assert.IsFalse(again.Applied);
        StringAssert.Contains(again.Message, "another owner");
        Assert.IsTrue((await automations.AcceptFromServerAsync("link-1", list, "org", SignRemoval(tablet, "org", self.DeviceId, 3, Id), _ => (null, "unused"), token)).Applied);
        Assert.IsNull(automations.Engine.Snapshot.Find(Id));
    }

    [TestMethod]
    public async Task TheAutomationsAServerGaveStopOnceTheNodeLeftItAndAReplayedVersionIsRefused()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions
            {
                TimeProvider = new NodeClock { Now = Noon }, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account,
            },
        }, token);
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var tablet = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet"));
        using var self = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var automations = node.Automations!;
        automations.DeviceId = self.DeviceId;
        var list = DeviceList(laptop, tablet, self, removeTablet: false);
        (FleetAutomationContent?, string?) Open(FleetAutomationVersion version)
            => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, out var content, out var reason) ? (content, null) : (null, reason);
        Assert.IsTrue((await automations.AcceptFromServerAsync("link-1", list, "org", Sign(tablet, list, "org", self.DeviceId, 1), Open, token)).Applied);
        Assert.IsNotNull(automations.Engine.Snapshot.Find(Id));

        // Another server's link going leaves it; the link it came through going takes it away.
        Assert.AreEqual(0, await automations.ForgetServerAsync("link-2", token));
        Assert.AreEqual(1, await automations.ForgetServerAsync("link-1", token));
        Assert.IsNull(automations.Engine.Snapshot.Find(Id));
        Assert.IsNull(automations.AssignmentOf(Id));
        Assert.AreEqual(1, automations.NumberOf(Id), "its number stays");
        Assert.IsFalse((await automations.AcceptFromServerAsync("link-1", list, "org", Sign(tablet, list, "org", self.DeviceId, 1), Open, token)).Applied,
            "the version replayed later is refused");
    }

    [TestMethod]
    public async Task TheSchedulesReportedLeaveOutWhatWasRemoved_AndGoInBatchesTheServerTakes()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions
            {
                TimeProvider = new NodeClock { Now = Noon }, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account,
            },
        }, token);
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop"));
        using var tablet = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet"));
        using var self = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "self"));
        var automations = node.Automations!;
        automations.DeviceId = self.DeviceId;
        var list = DeviceList(laptop, tablet, self, removeTablet: false);
        (FleetAutomationContent?, string?) Open(FleetAutomationVersion version)
            => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, self, list, out var content, out var reason) ? (content, null) : (null, reason);

        // More automations given and taken away again than one report holds: their tombstones stay on the node.
        var ids = Enumerable.Range(0, 110).Select(static _ => Guid.CreateVersion7().ToString()).ToArray();
        var removed = ids.Take(104).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var given = await automations.AcceptFromServerAsync("link-1", list, "org", Sign(tablet, list, "org", self.DeviceId, 1, id), Open, token);
            Assert.IsTrue(given.Applied, given.Message);
            if (!removed.Contains(id)) continue;
            var taken = await automations.AcceptFromServerAsync("link-1", list, "org", SignRemoval(tablet, "org", self.DeviceId, 2, id), Open, token);
            Assert.IsTrue(taken.Applied, taken.Message);
        }

        var reports = new List<FleetAutomationRunFacts>();
        automations.ServerReport = (_, facts, _) =>
        {
            lock (reports) reports.Add(facts);
            return Task.FromResult(true);
        };
        await automations.ReportRunsAsync("link-1", all: true, token);

        Assert.IsTrue(reports.Count > 0);
        foreach (var report in reports) Assert.IsTrue(report.Schedules is { Count: > 0 and <= 100 }, "a report holds what the server takes");
        var reported = reports.SelectMany(static report => report.Schedules!).Select(static schedule => schedule.AutomationId).ToList();
        CollectionAssert.AreEquivalent(ids.Where(id => !removed.Contains(id)).ToArray(), reported, "every automation held, once; none taken away");
    }

    // A version signed by a controller for a machine, its definition sealed for the machine and the person's controllers.
    [TestMethod]
    public async Task AnEventPushedToAHook_RunsTheAutomationThatNamesIt_OnceItsSignatureVerifiesOnTheNode()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var clock = new NodeClock { Now = Noon };
        var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop-fleet"));
        using var desktop = new FleetServerClient(address, desktopKey);
        var setup = await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);
        Assert.AreEqual(NodeExitCodes.Success, await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]));
        var nodeId = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending").Id;
        await desktop.ApproveAsync(nodeId, FleetDeviceKinds.Node, token);

        // The signing secret is the node's: the server never holds it.
        var key = System.Text.Encoding.UTF8.GetBytes("the-sender-signing-key-0123456789");
        using var http = new HttpClient();
        var provider = new ScriptedNodeProvider();
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            ConfigureModelProviders = provider.Register,
            Automations = new NodeAutomationOptions
            {
                TimeProvider = clock, TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account,
                Integrations = new AutomationIntegrationOptions
                {
                    Http = http,
                    Resolve = (_, text) => text == "${secret:ALERTS}" ? ("whsec_" + Convert.ToBase64String(key), null) : (null, "not set"),
                },
            },
        }, token);
        var automations = node.Automations!;
        var servers = NodeServers.Start(node, null, "test-node", "1.0.0", Fast);
        try
        {
            await WaitForAsync(async () => (await desktop.ListMachinesAsync(token)).Any(static machine => machine.Online) ? "online" : null, token);
            var hook = await desktop.CreateHookAsync(nodeId, CodeAlta.Fleet.Protocol.Api.HookSources.Webhook, "Alerts", token);
            var definition = $$"""
                [automations.{{Id}}]
                name = "Alerts"
                triggers = [{ type = "webhook", hook = "{{hook.Id}}", secret = "${secret:ALERTS}", fields = ["/alert"] }]
                prompt = "Look at the alert."
                """;
            var list = await desktop.ReadVerifiedDeviceListAsync(null, token);
            var sealedText = FleetAutomationSigning.Seal(new FleetAutomationContent(definition, null), Id, 1, nodeId, list, desktopKey);
            await desktop.PutAutomationAsync(FleetAutomationSigning.Sign(desktopKey, new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, setup.Organization.Id, Id, 1,
                nodeId, false, true, ["webhook"], null, null, sealedText, string.Empty, string.Empty, DateTimeOffset.UtcNow)), token);
            await WaitForAsync(() => Task.FromResult(automations.AssignmentOf(Id) is { Number: 1 } held ? held : null), token);

            async Task<System.Net.HttpStatusCode> PushAsync(string body, string id, byte[] signingKey)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                var stamp = Noon.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                byte[] signed = [.. System.Text.Encoding.UTF8.GetBytes(id + "." + stamp + "."), .. bytes];
                var signature = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(signingKey, signed));
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, hook.Path)) { Content = new ByteArrayContent(bytes) };
                request.Headers.Add("webhook-id", id);
                request.Headers.Add("webhook-timestamp", stamp);
                request.Headers.Add("webhook-signature", "v1," + signature);
                using var response = await http.SendAsync(request, token);
                return response.StatusCode;
            }

            // An event someone made up without the secret reaches the node and runs nothing.
            Assert.AreEqual(System.Net.HttpStatusCode.Accepted, await PushAsync("""{"alert":"forged"}""", "msg_0", System.Text.Encoding.UTF8.GetBytes("not the key")));
            await WaitForAsync(async () => (await desktop.ListHookEventsAsync(hook.Id, null, token)).Events.FirstOrDefault(static item => item.State == "refused"), token);
            Assert.AreEqual(0, automations.Engine.Runs(Id, 10).Count);

            Assert.AreEqual(System.Net.HttpStatusCode.Accepted, await PushAsync("""{"alert":"disk full on db-1","other":"not bound"}""", "msg_1", key));
            var run = await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs.FirstOrDefault(static item => item.Status == AutomationRun.Completed), token);
            Assert.AreEqual(("webhook", nodeId), (run.Trigger, run.MachineId));
            StringAssert.StartsWith(run.Occurrence, "event:webhook:", "the event was claimed with the server: it runs once whatever the machine");
            StringAssert.Contains(provider.LastUserText, "/alert: disk full on db-1");
            Assert.IsFalse(provider.LastUserText.Contains("not bound", StringComparison.Ordinal));
            await WaitForAsync(async () => (await desktop.ListHookEventsAsync(hook.Id, null, token)).Events.FirstOrDefault(static item => item.State == "accepted"), token);
        }
        finally
        {
            await servers.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static FleetSignedAutomationVersion Sign(DeviceIdentity signer, FleetDeviceListState list, string organization, string machine, long number, string id = Id)
    {
        var definition = Definition.Replace(Id, id, StringComparison.Ordinal);
        var sealedText = FleetAutomationSigning.Seal(new FleetAutomationContent(definition, null), id, number, machine, list, signer);
        return FleetAutomationSigning.Sign(signer, new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, organization, id, number, machine, false, true, ["hourly"],
            null, null, sealedText, string.Empty, string.Empty, DateTimeOffset.UtcNow));
    }

    // A version that takes an automation away from a machine.
    private static FleetSignedAutomationVersion SignRemoval(DeviceIdentity signer, string organization, string machine, long number, string id)
        => FleetAutomationSigning.Sign(signer, new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, organization, id, number, machine, true, false, [],
            null, null, null, string.Empty, string.Empty, DateTimeOffset.UtcNow));

    // The laptop (the first controller, of person p1), the tablet (another controller of p1) and the node of p1.
    private static FleetDeviceListState DeviceList(DeviceIdentity laptop, DeviceIdentity tablet, DeviceIdentity node, bool removeTablet)
    {
        var time = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
        FleetSignedDeviceListEntry Entry(long sequence, string? previous, string operation, DeviceIdentity device, string? kind, string? organization, string? owner)
        {
            var add = operation == FleetDeviceListOperations.Add;
            var payload = FleetDeviceListEntries.SerializePayload(new FleetDeviceListEntryPayload(1, organization, sequence, previous, operation, device.DeviceId,
                add ? Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()) : null, kind, owner, laptop.DeviceId, time));
            return new FleetSignedDeviceListEntry(payload, Base64Url.EncodeToString(laptop.SignData(FleetDeviceListEntries.GetSignedBytes(payload))));
        }

        var entries = new List<FleetSignedDeviceListEntry> { Entry(1, null, FleetDeviceListOperations.Add, laptop, FleetDeviceKinds.Controller, null, null) };
        entries.Add(Entry(2, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add, node, FleetDeviceKinds.Node, "org", "p1"));
        entries.Add(Entry(3, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Add, tablet, FleetDeviceKinds.Controller, "org", "p1"));
        if (removeTablet) entries.Add(Entry(4, FleetDeviceListEntries.Hash(entries[^1]), FleetDeviceListOperations.Remove, tablet, null, "org", null));
        return FleetDeviceListVerifier.VerifyOrThrow(entries, "org");
    }

    // A connector that tries again within a second, and keeps the automation callbacks of the node.
    private static FleetConnectorOptions Fast(FleetConnectorOptions options) => new()
    {
        ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
        PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory, PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
        MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(500),
        DeviceListPinPath = options.DeviceListPinPath, Devices = options.Devices, MetadataKeys = options.MetadataKeys, AcceptRelay = options.AcceptRelay,
        AutomationsAssigned = options.AutomationsAssigned, AutomationsOnline = options.AutomationsOnline, AutomationsRun = options.AutomationsRun,
        HookEvent = options.HookEvent,
    };

    private async Task<T> WaitForAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("It did not get there in time.");
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
}
