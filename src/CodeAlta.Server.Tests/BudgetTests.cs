using System.Net;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The budgets of a person at the server (<c>doc/fleet/observability.md</c>, budgets and alerts): signed by a controller device of
/// their owner; consumption from the published facts, with 80% and 100% told once per period; allowances that never pass the
/// limit; a reached budget that holds a run until a Continue anyway its owner signed, which the machine verifies; offline
/// spending within what a machine holds.
/// </summary>
[TestClass]
public sealed class BudgetTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Allowances_NeverPassTheLimit_WhateverTheMachinesAskOrSpend()
    {
        var random = new Random(4242);
        for (var round = 0; round < 50; round++)
        {
            var limit = random.NextInt64(1, 10_000);
            var granted = new Dictionary<string, long>(StringComparer.Ordinal);
            var used = new Dictionary<string, long>(StringComparer.Ordinal);
            string[] machines = ["a", "b", "c", "d"];
            for (var step = 0; step < 400; step++)
            {
                var machine = machines[random.Next(machines.Length)];
                if (random.Next(3) == 0)
                {
                    // A machine spends offline what it holds, a run sometimes a little more (a run admitted with the reserve may pass it).
                    var holds = granted.GetValueOrDefault(machine) - used.GetValueOrDefault(machine);
                    used[machine] = used.GetValueOrDefault(machine) + Math.Max(0, random.NextInt64(0, Math.Max(1, holds + 3)));
                    continue;
                }

                var needed = random.NextInt64(0, limit / 10 + 2);
                var spent = used.GetValueOrDefault(machine) + (random.Next(4) == 0 ? random.NextInt64(0, 5) : 0);
                var (holdsNow, exhausted) = BudgetAllowances.Grant(limit, granted, used, machine, spent, needed, BudgetAllowances.Share(limit));
                var given = holdsNow - granted.GetValueOrDefault(machine);
                Assert.IsGreaterThanOrEqualTo(0, given, "an allowance is never taken back by a grant");
                granted[machine] = holdsNow;
                Assert.IsLessThanOrEqualTo(limit, granted.Values.Sum(), $"the allowances never pass the limit (round {round}, step {step})");
                if (given > 0) Assert.IsLessThanOrEqualTo(limit, BudgetAllowances.Committed(granted, used), "a grant gives only what no machine held or used");
                if (!exhausted) Assert.IsGreaterThanOrEqualTo(needed, holdsNow - Math.Max(spent, used.GetValueOrDefault(machine)), "what is not exhausted holds what is needed");
            }
        }
    }

    [TestMethod]
    public void AMachine_GetsAShare_SpendsIt_AndTopsUpUntilTheBudgetIsReached()
    {
        var granted = new Dictionary<string, long>(StringComparer.Ordinal);
        var used = new Dictionary<string, long>(StringComparer.Ordinal);
        var share = BudgetAllowances.Share(1000);
        Assert.AreEqual(250, share);

        // The first ask gets a share, more than a run needs: the machine spends it without asking, also offline.
        Assert.AreEqual((250L, false), BudgetAllowances.Grant(1000, granted, used, "node", 0, 10, share));
        granted["node"] = 250;
        Assert.AreEqual((250L, false), BudgetAllowances.Grant(1000, granted, used, "node", 200, 10, share), "it holds enough: nothing changes");

        // Spent nearly all of it: it tops up by another share, and keeps what it did not spend.
        Assert.AreEqual((500L, false), BudgetAllowances.Grant(1000, granted, used, "node", 245, 10, share));
        granted["node"] = 500;

        // A desktop that spends without allowances still counts: its facts take their part of the limit.
        used["desktop"] = 400;
        Assert.AreEqual((600L, false), BudgetAllowances.Grant(1000, granted, used, "node", 495, 10, share), "only what nobody committed is given");
        granted["node"] = 600;
        Assert.AreEqual((600L, true), BudgetAllowances.Grant(1000, granted, used, "node", 595, 10, share), "the budget is reached");
        Assert.AreEqual((0L, true), BudgetAllowances.Grant(1000, granted, used, "laptop", 0, 10, share), "another machine gets nothing either");

        // A machine that spent more than it held (a run passed it) commits what it spent, never less.
        used["desktop"] = 0;
        Assert.AreEqual((900L, false), BudgetAllowances.Grant(1000, granted, used, "node", 650, 10, share), "its overspend (650 of 600) is covered first, then a share");
    }

    [TestMethod]
    public async Task ABudget_IsSignedByItsOwner_AndTheServerTakesNoOtherSignature()
    {
        await using var fleet = await BudgetFixture.StartAsync();
        var budget = await fleet.Manager.PutAsync(null, Settings(1000), CancellationToken.None);
        Assert.AreEqual((1L, FleetBudgetStates.Ok, 1000L), (budget.Number, budget.Usage!.State, budget.Usage.Limit));
        var changed = await fleet.Manager.PutAsync(budget.Id, Settings(2000), CancellationToken.None);
        Assert.AreEqual((2L, 2000L), (changed.Number, changed.Usage!.Limit));

        // A machine reads the budgets of its owner, never sets one.
        FleetBudgetDocuments.TryRead(changed.Signed.Payload, out var definition, out _);
        var bySelf = FleetBudgetsManager.SignBudget(fleet.NodeKey, definition! with { Number = 3 });
        var refused = await fleet.Server.SendAsync(TestDevice.From(fleet.NodeKey), HttpMethod.Put, $"/api/v1/budgets/{budget.Id}", new PutFleetBudgetRequest(bySelf));
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.Status);

        // Another signature, an older version, a team: refused.
        var forged = new FleetSignedBudget(FleetBudgetDocuments.Serialize(definition with { Number = 3, Limit = 10_000_000 }), changed.Signed.Signature);
        var tampered = await fleet.Server.SendAsync(TestDevice.From(fleet.DesktopKey), HttpMethod.Put, $"/api/v1/budgets/{budget.Id}", new PutFleetBudgetRequest(forged));
        Assert.AreEqual((HttpStatusCode.BadRequest, CoordinatorErrorCodes.BudgetInvalid), (tampered.Status, tampered.Code));
        var stale = FleetBudgetsManager.SignBudget(fleet.DesktopKey, definition with { Number = 2 });
        var older = await fleet.Server.SendAsync(TestDevice.From(fleet.DesktopKey), HttpMethod.Put, $"/api/v1/budgets/{budget.Id}", new PutFleetBudgetRequest(stale));
        Assert.AreEqual((HttpStatusCode.Conflict, CoordinatorErrorCodes.BudgetConflict), (older.Status, older.Code));
        await Assert.ThrowsExactlyAsync<FleetServerException>(() => fleet.Manager.PutAsync(null, Settings(1000) with { ScopeKind = FleetBudgetScopeKinds.Team, ScopeId = "t" },
            CancellationToken.None));

        // The machine verifies what it reads against the device list it anchored.
        using var node = new FleetServerClient(fleet.Server.BaseAddress, fleet.NodeKey);
        var listed = (await node.ListMachineBudgetsAsync()).Budgets.Single();
        var list = await new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey).RefreshAsync(CancellationToken.None);
        Assert.IsTrue(FleetBudgetDocuments.TryVerify(listed.Signed, list, fleet.NodeKey.DeviceId, out var verified, out var reason), reason);
        Assert.AreEqual(2000, verified.Limit);
        Assert.IsFalse(FleetBudgetDocuments.TryVerify(forged, list, fleet.NodeKey.DeviceId, out _, out _), "a budget the server made up does not verify");

        // Removed, it limits nothing and is no longer listed for its owner; a machine reads its signed removal, which ends what it kept.
        await fleet.Manager.RemoveAsync(budget.Id, CancellationToken.None);
        Assert.AreEqual(0, (await fleet.Manager.ListAsync(CancellationToken.None)).Budgets.Count);
        var removal = (await node.ListMachineBudgetsAsync()).Budgets.Single();
        Assert.IsTrue(FleetBudgetDocuments.TryVerify(removal.Signed, list, fleet.NodeKey.DeviceId, out var removed, out reason), reason);
        Assert.IsTrue(removed.Removed);
        CollectionAssert.IsSubsetOf(new[] { "budget.set", "budget.remove" }, (await fleet.AuditAsync("budget.")).Select(static record => record.Action).ToArray());
    }

    [TestMethod]
    public async Task TheFactsOfAPeriod_Warn_At80And100_OncePerPeriod()
    {
        await using var fleet = await BudgetFixture.StartAsync();
        var budget = await fleet.Manager.PutAsync(null, Settings(1000) with { Mode = FleetBudgetModes.Advisory }, CancellationToken.None);
        await fleet.PublishAsync(fleet.NodeKey, 1, 700, 150);
        var usage = (await fleet.Manager.ListAsync(CancellationToken.None)).Budgets.Single().Usage!;
        Assert.AreEqual((850L, FleetBudgetStates.Warning), (usage.Used, usage.State));
        var notifications = (await fleet.Manager.ListNotificationsAsync(CancellationToken.None)).Notifications;
        Assert.AreEqual(FleetNotificationKinds.BudgetWarning, notifications.Single().Kind);
        Assert.AreEqual((budget.Id, "Tokens of the person", "850/1000"), (notifications[0].BudgetId, notifications[0].Subject, notifications[0].Detail));

        // The same facts again, then more of them on another machine: one warning, one reached.
        await fleet.PublishAsync(fleet.NodeKey, 2, 700, 150);
        await fleet.PublishAsync(fleet.OtherKey, 1, 300, 0);
        await fleet.PublishAsync(fleet.OtherKey, 2, 400, 0);
        notifications = (await fleet.Manager.ListNotificationsAsync(CancellationToken.None)).Notifications;
        CollectionAssert.AreEquivalent(new[] { FleetNotificationKinds.BudgetWarning, FleetNotificationKinds.BudgetReached }, notifications.Select(static n => n.Kind).ToArray());
        Assert.AreEqual(FleetBudgetStates.Reached, (await fleet.Manager.ListAsync(CancellationToken.None)).Budgets.Single().Usage!.State);

        // Dismissed on one device, it is read on all; it is not made again.
        var read = await fleet.Manager.ReadNotificationAsync(notifications[0].Id, CancellationToken.None);
        Assert.IsNotNull(read.Notifications.Single(n => n.Id == notifications[0].Id).ReadAt);
        await fleet.PublishAsync(fleet.OtherKey, 3, 500, 0);
        Assert.AreEqual(2, (await fleet.Manager.ListNotificationsAsync(CancellationToken.None)).Notifications.Count);

        // A machine sees no notification, and dismisses none.
        using var node = new FleetServerClient(fleet.Server.BaseAddress, fleet.NodeKey);
        Assert.AreEqual(0, (await node.ListNotificationsAsync()).Notifications.Count);
        var other = await Assert.ThrowsExactlyAsync<FleetServerException>(() => node.ReadNotificationAsync(notifications[0].Id));
        Assert.AreEqual(CoordinatorErrorCodes.NotificationNotFound, other.Code);
    }

    [TestMethod]
    public async Task AnEnforcedBudget_GivesAllowances_NeverMoreThanItsLimit()
    {
        await using var fleet = await BudgetFixture.StartAsync();
        var budget = await fleet.Manager.PutAsync(null, Settings(1000), CancellationToken.None);
        using var node = new FleetServerClient(fleet.Server.BaseAddress, fleet.NodeKey);
        using var other = new FleetServerClient(fleet.Server.BaseAddress, fleet.OtherKey);
        var first = await node.AskAllowanceAsync(budget.Id, new FleetAllowanceRequest(1, 0, 10));
        Assert.AreEqual((250L, false), (first.Granted, first.Exhausted));
        var big = await other.AskAllowanceAsync(budget.Id, new FleetAllowanceRequest(1, 0, 700));
        Assert.AreEqual((700L, false), (big.Granted, big.Exhausted));
        var last = await node.AskAllowanceAsync(budget.Id, new FleetAllowanceRequest(1, 245, 100));
        Assert.AreEqual((300L, true), (last.Granted, last.Exhausted), "the rest of the limit, then reached");
        var listed = (await fleet.Manager.ListAsync(CancellationToken.None)).Budgets.Single().Usage!;
        Assert.AreEqual((1000L, 0L), (listed.Granted, listed.Used), "what the machines hold is the limit, nothing spent yet");
        Assert.AreEqual(FleetNotificationKinds.BudgetReached, (await fleet.Manager.ListNotificationsAsync(CancellationToken.None)).Notifications
            .Single(static n => n.Kind == FleetNotificationKinds.BudgetReached).Kind, "an exhausted budget tells its owner once");

        // An old version of the budget, a budget that is advisory: refused.
        var stale = await Assert.ThrowsExactlyAsync<FleetServerException>(() => node.AskAllowanceAsync(budget.Id, new FleetAllowanceRequest(7, 0, 10)));
        Assert.AreEqual(CoordinatorErrorCodes.BudgetConflict, stale.Code);
        var advisory = await fleet.Manager.PutAsync(null, Settings(1000) with { Name = "Advisory", Mode = FleetBudgetModes.Advisory }, CancellationToken.None);
        Assert.AreEqual(CoordinatorErrorCodes.BudgetInvalid,
            (await Assert.ThrowsExactlyAsync<FleetServerException>(() => node.AskAllowanceAsync(advisory.Id, new FleetAllowanceRequest(1, 0, 10)))).Code);
    }

    [TestMethod]
    public async Task AReachedBudget_HoldsARun_UntilItsOwnerSignsContinueAnyway()
    {
        await using var fleet = await BudgetFixture.StartAsync();
        var budget = await fleet.Manager.PutAsync(null, Settings(100), CancellationToken.None);
        // Another machine holds all of it.
        using (var other = new FleetServerClient(fleet.Server.BaseAddress, fleet.OtherKey))
            Assert.IsTrue((await other.AskAllowanceAsync(budget.Id, new FleetAllowanceRequest(1, 0, 100))).Granted == 100);

        var gate = fleet.Gate(new FakeSpend());
        var held = new TaskCompletionSource();
        var admission = gate.AdmitAsync(new FleetBudgetRun("run-1", "Deploy", null, null), () => held.TrySetResult(), TestContext.CancellationToken);
        await held.Task.WaitAsync(Patience, TestContext.CancellationToken);
        var hold = await WaitForAsync(async () => (await fleet.Manager.ListHoldsAsync(CancellationToken.None)).Holds.FirstOrDefault(), TestContext.CancellationToken);
        // The hold names the run, never what it is about: the server does not read the titles of sessions.
        Assert.AreEqual((FleetBudgetHoldStates.Waiting, "run-1", (string?)null, fleet.NodeKey.DeviceId), (hold.Status, hold.RunId, hold.Title, hold.MachineId));
        Assert.IsFalse(admission.IsCompleted, "the run waits");

        // A Continue anyway the server makes up, or one the machine signs, is refused: only the owner's controller device signs it.
        FleetBudgetDocuments.TryRead(budget.Signed.Payload, out var definition, out _);
        var time = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var bySelf = FleetBudgetsManager.SignContinue(fleet.NodeKey, new FleetBudgetContinue(1, definition!.OrganizationId, hold.Id, budget.Id, 1, hold.PeriodStart,
            hold.MachineId, hold.RunId, hold.Nonce, definition.PersonId, fleet.NodeKey.DeviceId, time, time.AddHours(1)));
        var forged = await fleet.Server.SendAsync(TestDevice.From(fleet.NodeKey), HttpMethod.Post, $"/api/v1/budget-holds/{hold.Id}/continue", new ContinueFleetBudgetHoldRequest(bySelf));
        Assert.AreEqual(HttpStatusCode.Forbidden, forged.Status);

        var continued = await fleet.Manager.ContinueAsync(hold.Id, CancellationToken.None);
        Assert.AreEqual(FleetBudgetHoldStates.Continued, continued.Status);
        gate.Changed();
        var result = await admission.WaitAsync(Patience, TestContext.CancellationToken);
        Assert.IsNull(result.Problem, result.Problem);
        CollectionAssert.AreEqual(new[] { budget.Id }, result.Continued.ToArray());
        await Assert.ThrowsExactlyAsync<FleetServerException>(() => fleet.Manager.ContinueAsync(hold.Id, CancellationToken.None), "a hold is decided once");

        // The next run waits again: Continue anyway is for one run.
        var stopped = gate.AdmitAsync(new FleetBudgetRun("run-2", null, null, null), TestContext.CancellationToken);
        var second = await WaitForAsync(async () => (await fleet.Manager.ListHoldsAsync(CancellationToken.None)).Holds.FirstOrDefault(h => h.RunId == "run-2"), TestContext.CancellationToken);
        await fleet.Manager.StopAsync(second.Id, CancellationToken.None);
        gate.Changed();
        StringAssert.Contains((await stopped.WaitAsync(Patience, TestContext.CancellationToken)).Problem, "stopped");
        CollectionAssert.IsSubsetOf(new[] { "budget.hold", "budget.continue", "budget.stop" }, (await fleet.AuditAsync("budget.")).Select(static record => record.Action).ToArray());
    }

    [TestMethod]
    public async Task AStopBudget_RefusesAtOnce_AndAnAdvisoryOne_HoldsOnceTheFactsReachIt()
    {
        await using var fleet = await BudgetFixture.StartAsync();
        var spend = new FakeSpend();
        var gate = fleet.Gate(spend);
        var advisory = await fleet.Manager.PutAsync(null, Settings(1000) with { Mode = FleetBudgetModes.Advisory }, CancellationToken.None);
        Assert.IsNull((await gate.AdmitAsync(new FleetBudgetRun("run-1", null, null, null), TestContext.CancellationToken)).Problem, "under its limit, a run starts");

        // The facts of another machine reach the limit: the next run of this machine waits.
        await fleet.PublishAsync(fleet.OtherKey, 1, 900, 200);
        var waiting = gate.AdmitAsync(new FleetBudgetRun("run-2", null, null, null), TestContext.CancellationToken);
        var hold = await WaitForAsync(async () => (await fleet.Manager.ListHoldsAsync(CancellationToken.None)).Holds.FirstOrDefault(), TestContext.CancellationToken);
        Assert.AreEqual(advisory.Id, hold.BudgetId);

        // A higher limit releases it: the budget is no longer reached.
        await fleet.Manager.PutAsync(advisory.Id, Settings(5000) with { Mode = FleetBudgetModes.Advisory }, CancellationToken.None);
        gate.Changed();
        Assert.IsNull((await waiting.WaitAsync(Patience, TestContext.CancellationToken)).Problem);

        // A budget that stops: this machine's own count reaches it, without the server.
        await fleet.Manager.PutAsync(null, Settings(100) with { Name = "Machine", ScopeKind = FleetBudgetScopeKinds.Machine, ScopeId = fleet.NodeKey.DeviceId,
            Mode = FleetBudgetModes.Advisory, Reached = FleetBudgetReachedActions.Stop }, CancellationToken.None);
        spend.Spent = 100;
        var refused = await gate.AdmitAsync(new FleetBudgetRun("run-3", null, null, null), TestContext.CancellationToken);
        StringAssert.Contains(refused.Problem, "'Machine' is reached");
    }

    [TestMethod]
    public async Task OfflineAMachine_SpendsWhatItHolds_AndNoMore()
    {
        var root = new TempDirectory();
        var fleet = await BudgetFixture.StartAsync(root);
        try
        {
            var spend = new FakeSpend();
            var gate = fleet.Gate(spend, Path.Combine(root.Path, "node-budgets.json"));
            gate.HoldWait = TimeSpan.FromSeconds(1);
            gate.HoldPoll = TimeSpan.FromMilliseconds(100);
            gate.ReadTimeout = TimeSpan.FromSeconds(2);
            await fleet.Manager.PutAsync(null, Settings(1000), CancellationToken.None);
            Assert.IsNull((await gate.AdmitAsync(new FleetBudgetRun("run-1", null, null, null), TestContext.CancellationToken)).Problem);

            // The server goes away: the machine runs on what it holds (a share, 250), counted by itself.
            await fleet.DisposeServerAsync();
            spend.Spent = 200;
            Assert.IsNull((await gate.AdmitAsync(new FleetBudgetRun("run-2", null, null, null), TestContext.CancellationToken)).Problem, "within its allowance");
            spend.Spent = 245;
            var over = await gate.AdmitAsync(new FleetBudgetRun("run-3", null, null, null), TestContext.CancellationToken);
            StringAssert.Contains(over.Problem, "nobody continued the run in time", "it holds less than a run needs, and cannot ask: the run waits, then gives up");
        }
        finally
        {
            await fleet.DisposeAsync();
            root.Dispose();
        }
    }

    // A server that leaves a budget out, replaces it with one whose signature does not hold, or gives an older version back,
    // takes no restriction away: the machine keeps the newest version it verified, and only a signed removal ends a budget.
    [TestMethod]
    public async Task AServer_CannotEraseNorRollBackABudgetTheMachineKnows()
    {
        var root = new TempDirectory();
        var fleet = await BudgetFixture.StartAsync(root);
        try
        {
            var spend = new FakeSpend { Spent = 100 };
            var tamper = new Tamper();
            var statePath = Path.Combine(root.Path, "node-budgets.json");
            var gate = fleet.Gate(spend, statePath, tamper);
            FleetBudgetSettings Machine(long limit) => Settings(limit) with { Name = "Machine", ScopeKind = FleetBudgetScopeKinds.Machine, ScopeId = fleet.NodeKey.DeviceId,
                Mode = FleetBudgetModes.Advisory, Reached = FleetBudgetReachedActions.Stop };
            async Task<string?> AdmitAsync(FleetBudgetGate with, string run) => (await with.AdmitAsync(new FleetBudgetRun(run, null, null, null), TestContext.CancellationToken)).Problem;

            var loose = await fleet.Manager.PutAsync(null, Machine(1000), CancellationToken.None);
            Assert.IsNull(await AdmitAsync(gate, "run-1"), "under its limit");
            var strict = await fleet.Manager.PutAsync(loose.Id, Machine(100), CancellationToken.None);
            StringAssert.Contains(await AdmitAsync(gate, "run-2"), "is reached");

            // Left out, replaced by a version whose signature does not hold, or by the older version with the higher limit: still reached.
            tamper.Budgets = static _ => [];
            StringAssert.Contains(await AdmitAsync(gate, "run-3"), "is reached", "a budget left out is kept");
            tamper.Budgets = budgets => [.. budgets.Select(budget => budget with { Signed = budget.Signed with { Payload = budget.Signed.Payload.Replace("\"limit\":100", "\"limit\":100000", StringComparison.Ordinal) } })];
            StringAssert.Contains(await AdmitAsync(gate, "run-4"), "is reached", "a version whose signature does not hold is not taken");
            tamper.Budgets = budgets => [.. budgets.Select(budget => budget with { Signed = loose.Signed })];
            StringAssert.Contains(await AdmitAsync(gate, "run-5"), "is reached", "an older version is not taken back");
            tamper.Budgets = budgets => [.. budgets.Select(budget => budget with { Id = "another-budget" })];
            StringAssert.Contains(await AdmitAsync(gate, "run-6"), "is reached", "a version under the id of another budget is not taken");

            // Restarted, with a server that leaves it out: what it verified is kept in its file.
            tamper.Budgets = static _ => [];
            StringAssert.Contains(await AdmitAsync(fleet.Gate(spend, statePath, tamper), "run-7"), "is reached");

            // A signed removal ends it, and an older version given back after it does not bring it back.
            tamper.Budgets = null;
            await fleet.Manager.RemoveAsync(strict.Id, CancellationToken.None);
            Assert.IsNull(await AdmitAsync(gate, "run-8"), "a signed removal ends the budget");
            tamper.Budgets = budgets => [.. budgets.Select(budget => budget with { Signed = strict.Signed })];
            Assert.IsNull(await AdmitAsync(gate, "run-9"), "the removal is kept");
        }
        finally
        {
            await fleet.DisposeAsync();
            root.Dispose();
        }
    }

    // A controller device removed after it signed a budget takes no restriction away: the machine keeps enforcing the version
    // it verified, after a restart too, until a newer signed version (a removal included) of another controller ends it.
    [TestMethod]
    public async Task ARunOfALinkedSpace_IsNotAdmittedAsOfNoSpace_BeforeTheKeyOfItsSpaceIsRead()
    {
        await using var fleet = await BudgetFixture.StartAsync();
        // The key of the space, made by the desktop and given to the node; the server knows the space by an opaque id only.
        var spaceKey = (await new FleetMetadataKeyring(new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey,
            Path.Combine(fleet.Root.Path, "desktop-link", FleetOrganizationDevices.PinFileName))).RefreshAsync("default", CancellationToken.None))!;
        await fleet.Manager.PutAsync(null, Settings(100) with { Name = "Default space", ScopeKind = FleetBudgetScopeKinds.Space, ScopeId = spaceKey.Grant!.SpaceId,
            Mode = FleetBudgetModes.Advisory, Reached = FleetBudgetReachedActions.Stop }, CancellationToken.None);

        // The node starts: its keys are not read yet (its connector reads them later) when a run of that space asks to start.
        var devices = new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.NodeKey, null);
        var link = new FleetBudgetLink("link", devices, new FleetMetadataKeyring(devices), null) { LinksSpace = static space => space == "default" };
        var gate = new FleetBudgetGate(() => [link], new FakeSpend { Spent = 100 }) { HoldPoll = TimeSpan.FromMilliseconds(100) };
        var admission = await gate.AdmitAsync(new FleetBudgetRun("run-1", null, "default", null), TestContext.CancellationToken);
        StringAssert.Contains(admission.Problem, "is reached", "the run is the space's: its budget holds it, not the absence of a key read");
    }

    [TestMethod]
    public async Task ABudget_StaysEnforced_WhenTheDeviceThatSignedItIsRemoved()
    {
        var root = new TempDirectory();
        var fleet = await BudgetFixture.StartAsync(root);
        try
        {
            using var laptopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "laptop"));
            using (var desktop = new FleetServerClient(fleet.Server.BaseAddress, fleet.DesktopKey)
                   { DeviceList = new FleetOrganizationDevices(fleet.Server.BaseAddress, fleet.DesktopKey, Path.Combine(root.Path, "desktop-link", FleetOrganizationDevices.PinFileName)) })
            {
                using (var laptop = new FleetServerClient(fleet.Server.BaseAddress, laptopKey)) await laptop.EnrollAsync((await desktop.CreateEnrollmentTokenAsync()).Token, "Laptop", FleetDeviceKinds.Controller);
                await desktop.ApproveAsync(laptopKey.DeviceId, FleetDeviceKinds.Controller);
            }

            var spend = new FakeSpend { Spent = 100 };
            var statePath = Path.Combine(root.Path, "node-budgets.json");
            var gate = fleet.Gate(spend, statePath);
            var budget = await fleet.Manager.PutAsync(null, Settings(100) with { Name = "Machine", ScopeKind = FleetBudgetScopeKinds.Machine, ScopeId = fleet.NodeKey.DeviceId,
                Mode = FleetBudgetModes.Advisory, Reached = FleetBudgetReachedActions.Stop }, CancellationToken.None);
            StringAssert.Contains((await gate.AdmitAsync(new FleetBudgetRun("run-1", null, null, null), TestContext.CancellationToken)).Problem, "is reached");

            // The laptop removes the desktop, which signed the budget; the machine reads the new list, as after a restart.
            var laptopDevices = new FleetOrganizationDevices(fleet.Server.BaseAddress, laptopKey, Path.Combine(root.Path, "laptop-link", FleetOrganizationDevices.PinFileName));
            using (var laptop = new FleetServerClient(fleet.Server.BaseAddress, laptopKey) { DeviceList = laptopDevices }) await laptop.RevokeAsync(fleet.DesktopKey.DeviceId);
            var restarted = fleet.Gate(spend, statePath);
            StringAssert.Contains((await restarted.AdmitAsync(new FleetBudgetRun("run-2", null, null, null), TestContext.CancellationToken)).Problem, "is reached",
                "a budget verified before its signer was removed still holds");

            // A removal the laptop signs ends it.
            await new FleetBudgetsManager(laptopDevices).RemoveAsync(budget.Id, CancellationToken.None);
            Assert.IsNull((await restarted.AdmitAsync(new FleetBudgetRun("run-3", null, null, null), TestContext.CancellationToken)).Problem);
        }
        finally
        {
            await fleet.DisposeAsync();
            root.Dispose();
        }
    }

    // A held run is released only by a signed decision or by its budget not being reached: a server that says the budget is
    // not found releases nothing.
    [TestMethod]
    public async Task AHeldRun_IsNotReleasedByAServerThatSaysItsBudgetIsNotFound()
    {
        await using var fleet = await BudgetFixture.StartAsync();
        var budget = await fleet.Manager.PutAsync(null, Settings(100), CancellationToken.None);
        using (var other = new FleetServerClient(fleet.Server.BaseAddress, fleet.OtherKey))
            Assert.AreEqual(100, (await other.AskAllowanceAsync(budget.Id, new FleetAllowanceRequest(1, 0, 100))).Granted);
        var tamper = new Tamper { HoldNotFound = true };
        var gate = fleet.Gate(new FakeSpend(), null, tamper);
        gate.HoldWait = TimeSpan.FromSeconds(1);
        var admission = await gate.AdmitAsync(new FleetBudgetRun("run-1", null, null, null), TestContext.CancellationToken).WaitAsync(Patience, TestContext.CancellationToken);
        StringAssert.Contains(admission.Problem, "nobody continued the run in time");
    }

    // A new version of the budget of a held run decides again, as for a new run: a rename or a lower limit keeps it waiting,
    // a limit it is no longer reached under starts it, and a version that stops refuses it. A version change alone authorizes nothing.
    [TestMethod]
    public async Task ANewVersionOfTheBudgetOfAHeldRun_DecidesAgain_ItReleasesNothingByItself()
    {
        await using var fleet = await BudgetFixture.StartAsync();
        var budget = await fleet.Manager.PutAsync(null, Settings(100), CancellationToken.None);
        using (var other = new FleetServerClient(fleet.Server.BaseAddress, fleet.OtherKey))
            Assert.AreEqual(100, (await other.AskAllowanceAsync(budget.Id, new FleetAllowanceRequest(1, 0, 100))).Granted);
        var gate = fleet.Gate(new FakeSpend());
        var held = new TaskCompletionSource();
        var admission = gate.AdmitAsync(new FleetBudgetRun("run-1", null, null, null), () => held.TrySetResult(), TestContext.CancellationToken);
        await held.Task.WaitAsync(Patience, TestContext.CancellationToken);

        // Renamed, then given a lower limit: still reached, the run waits.
        await fleet.Manager.PutAsync(budget.Id, Settings(100) with { Name = "Renamed" }, CancellationToken.None);
        gate.Changed();
        await fleet.Manager.PutAsync(budget.Id, Settings(50) with { Name = "Renamed" }, CancellationToken.None);
        gate.Changed();
        await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.CancellationToken);
        Assert.IsFalse(admission.IsCompleted, "a new version that is still reached releases nothing");

        // A limit it is no longer reached under: the run starts.
        await fleet.Manager.PutAsync(budget.Id, Settings(10_000) with { Name = "Renamed" }, CancellationToken.None);
        gate.Changed();
        Assert.IsNull((await admission.WaitAsync(Patience, TestContext.CancellationToken)).Problem);

        // A version that stops runs refuses a held one.
        var second = await fleet.Manager.PutAsync(null, Settings(100) with { Name = "Second" }, CancellationToken.None);
        using (var other = new FleetServerClient(fleet.Server.BaseAddress, fleet.OtherKey))
            await other.AskAllowanceAsync(second.Id, new FleetAllowanceRequest(1, 0, 100));
        var waiting = new TaskCompletionSource();
        var stopped = gate.AdmitAsync(new FleetBudgetRun("run-2", null, null, null), () => waiting.TrySetResult(), TestContext.CancellationToken);
        await waiting.Task.WaitAsync(Patience, TestContext.CancellationToken);
        await fleet.Manager.PutAsync(second.Id, Settings(100) with { Name = "Second", Reached = FleetBudgetReachedActions.Stop }, CancellationToken.None);
        gate.Changed();
        StringAssert.Contains((await stopped.WaitAsync(Patience, TestContext.CancellationToken)).Problem, "is reached");
    }

    // A run held by one budget is checked against every budget again before it starts: one that stops runs, which the machine
    // learned while the run waited, refuses it after the first is continued anyway.
    [TestMethod]
    public async Task AHeldRun_IsCheckedAgainstABudgetLearnedWhileItWaited()
    {
        await using var fleet = await BudgetFixture.StartAsync();
        var spend = new FakeSpend { Spent = 100 };
        await fleet.Manager.PutAsync(null, Settings(100), CancellationToken.None);
        var gate = fleet.Gate(spend);
        var held = new TaskCompletionSource();
        var admission = gate.AdmitAsync(new FleetBudgetRun("run-1", null, null, null), () => held.TrySetResult(), TestContext.CancellationToken);
        await held.Task.WaitAsync(Patience, TestContext.CancellationToken);
        var hold = await WaitForAsync(async () => (await fleet.Manager.ListHoldsAsync(CancellationToken.None)).Holds.FirstOrDefault(), TestContext.CancellationToken);

        // A budget of the machine that stops its runs, reached too; then the first is continued anyway.
        await fleet.Manager.PutAsync(null, Settings(100) with { Name = "Machine", ScopeKind = FleetBudgetScopeKinds.Machine, ScopeId = fleet.NodeKey.DeviceId,
            Mode = FleetBudgetModes.Advisory, Reached = FleetBudgetReachedActions.Stop }, CancellationToken.None);
        gate.Changed();
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.CancellationToken);
        await fleet.Manager.ContinueAsync(hold.Id, CancellationToken.None);
        gate.Changed();
        StringAssert.Contains((await admission.WaitAsync(Patience, TestContext.CancellationToken)).Problem, "'Machine' is reached");
    }

    // A Continue anyway is for the version of the budget the run started to wait under: once the machine verified a newer version
    // that still holds the run (a lower limit), a Continue anyway signed for the earlier one does not start it.
    [TestMethod]
    public async Task AContinueAnywayForAnEarlierVersionOfTheBudget_DoesNotStartTheRun()
    {
        await using var fleet = await BudgetFixture.StartAsync();
        var budget = await fleet.Manager.PutAsync(null, Settings(100), CancellationToken.None);
        using (var other = new FleetServerClient(fleet.Server.BaseAddress, fleet.OtherKey))
            Assert.AreEqual(100, (await other.AskAllowanceAsync(budget.Id, new FleetAllowanceRequest(1, 0, 100))).Granted);
        var gate = fleet.Gate(new FakeSpend());
        var held = new TaskCompletionSource();
        var admission = gate.AdmitAsync(new FleetBudgetRun("run-1", null, null, null), () => held.TrySetResult(), TestContext.CancellationToken);
        await held.Task.WaitAsync(Patience, TestContext.CancellationToken);
        var hold = await WaitForAsync(async () => (await fleet.Manager.ListHoldsAsync(CancellationToken.None)).Holds.FirstOrDefault(), TestContext.CancellationToken);

        // A lower limit, then the hold of the first version is continued anyway.
        await fleet.Manager.PutAsync(budget.Id, Settings(50), CancellationToken.None);
        Assert.AreEqual(FleetBudgetHoldStates.Continued, (await fleet.Manager.ContinueAsync(hold.Id, CancellationToken.None)).Status);
        gate.Changed();
        var result = await admission.WaitAsync(Patience, TestContext.CancellationToken);
        StringAssert.Contains(result.Problem, "earlier version or period", "the run does not start on a decision about another version");
        Assert.IsEmpty(result.Continued);
    }

    // What the server gives of an enforced budget is not signed: the machine never holds more than its signed limit, and what it
    // spent itself reaching the limit holds the run, whatever allowance the server gives.
    [TestMethod]
    public async Task AnAllowance_IsBoundedByTheSignedLimit_WhateverTheServerGives()
    {
        await using var fleet = await BudgetFixture.StartAsync();
        await fleet.Manager.PutAsync(null, Settings(100), CancellationToken.None);
        var spend = new FakeSpend { Spent = 100 };
        var tamper = new Tamper { Allowance = 1_000_000 };
        var gate = fleet.Gate(spend, null, tamper);
        gate.HoldWait = TimeSpan.FromSeconds(1);
        var reached = await gate.AdmitAsync(new FleetBudgetRun("run-1", null, null, null), TestContext.CancellationToken).WaitAsync(Patience, TestContext.CancellationToken);
        StringAssert.Contains(reached.Problem, "nobody continued the run in time", "its own spending reached the signed limit");
        // Below the limit, the run starts on what is left of it, not on the allowance the server made up.
        spend.Spent = 60;
        Assert.IsNull((await gate.AdmitAsync(new FleetBudgetRun("run-2", null, null, null), TestContext.CancellationToken)).Problem);
    }

    /// <summary>A server in the path of a machine that changes what it answers, as a compromised one would.</summary>
    private sealed class Tamper() : DelegatingHandler(new SocketsHttpHandler())
    {
        /// <summary>Gets or sets what the server answers for the budgets of the machine; null leaves its answer.</summary>
        public Func<IReadOnlyList<FleetMachineBudgetDto>, IReadOnlyList<FleetMachineBudgetDto>>? Budgets { get; set; }

        /// <summary>Gets or sets an allowance the server gives, whatever was asked; null leaves its answer.</summary>
        public long? Allowance { get; set; }

        /// <summary>Gets or sets whether a hold is answered with budget.not_found.</summary>
        public bool HoldNotFound { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (HoldNotFound && request.Method == HttpMethod.Post && path.EndsWith("/holds", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"status":404,"title":"budget.not_found","detail":"No such budget.","code":"budget.not_found"}""", System.Text.Encoding.UTF8,
                        "application/problem+json"),
                };
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (Budgets is { } budgets && path.EndsWith("/machine-budgets", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                var listed = System.Text.Json.JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(cancellationToken), FleetBudgetsApiJsonContext.Default.FleetMachineBudgetListDto)!;
                response.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new FleetMachineBudgetListDto(budgets(listed.Budgets)),
                    FleetBudgetsApiJsonContext.Default.FleetMachineBudgetListDto), System.Text.Encoding.UTF8, "application/json");
            }

            if (Allowance is { } given && path.EndsWith("/allowance", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                var allowance = System.Text.Json.JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(cancellationToken), FleetBudgetsApiJsonContext.Default.FleetAllowanceDto)!;
                response.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(allowance with { Granted = given, Exhausted = false },
                    FleetBudgetsApiJsonContext.Default.FleetAllowanceDto), System.Text.Encoding.UTF8, "application/json");
            }

            return response;
        }
    }

    private static FleetBudgetSettings Settings(long limit) => new("Tokens of the person", FleetBudgetScopeKinds.Person, null, FleetBudgetMeasures.Tokens, limit,
        FleetBudgetPeriods.Month, "UTC", FleetBudgetModes.Enforced, FleetBudgetReachedActions.Hold);

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("It did not get there in time.");
            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>What a machine spent, as a test says it.</summary>
    private sealed class FakeSpend : IFleetBudgetSpend
    {
        public long Spent { get; set; }

        public ValueTask<long> SpentAsync(FleetBudgetLink link, FleetBudgetDefinition budget, FleetBudgetPeriod period, CancellationToken cancellationToken) => ValueTask.FromResult(Spent);
    }

    private sealed class BudgetFixture : IAsyncDisposable
    {
        private bool _serverDisposed;

        private BudgetFixture(ServerFixture server, TempDirectory root, bool ownsRoot, DeviceIdentity desktopKey, DeviceIdentity nodeKey, DeviceIdentity otherKey)
        {
            Server = server;
            Root = root;
            OwnsRoot = ownsRoot;
            DesktopKey = desktopKey;
            NodeKey = nodeKey;
            OtherKey = otherKey;
            Manager = new FleetBudgetsManager(new FleetOrganizationDevices(server.BaseAddress, desktopKey, Path.Combine(root.Path, "desktop-link", FleetOrganizationDevices.PinFileName)));
        }

        public ServerFixture Server { get; }

        public TempDirectory Root { get; }

        private bool OwnsRoot { get; }

        public DeviceIdentity DesktopKey { get; }

        public DeviceIdentity NodeKey { get; }

        public DeviceIdentity OtherKey { get; }

        public FleetBudgetsManager Manager { get; }

        public static async Task<BudgetFixture> StartAsync(TempDirectory? root = null)
        {
            var server = await ServerFixture.StartAsync();
            var owns = root is null;
            root ??= new TempDirectory();
            var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "desktop"));
            var nodeKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "node"));
            var otherKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Path, "other"));
            using var desktop = new FleetServerClient(server.BaseAddress, desktopKey);
            await desktop.SetUpAsync(server.SetupCode, "Alex", "Desktop");
            foreach (var (key, name) in new[] { (nodeKey, "Mac mini"), (otherKey, "Build box") })
            {
                using var machine = new FleetServerClient(server.BaseAddress, key);
                await machine.EnrollAsync((await desktop.CreateEnrollmentTokenAsync()).Token, name);
                await desktop.ApproveAsync(key.DeviceId, FleetDeviceKinds.Node);
            }

            return new BudgetFixture(server, root, owns, desktopKey, nodeKey, otherKey);
        }

        /// <summary>The gate of the node machine, with what it spent as the test says it.</summary>
        public FleetBudgetGate Gate(IFleetBudgetSpend spend, string? statePath = null, HttpMessageHandler? handler = null)
        {
            var devices = new FleetOrganizationDevices(Server.BaseAddress, NodeKey, null, handler);
            var link = new FleetBudgetLink("link", devices, null, statePath);
            return new FleetBudgetGate(() => [link], spend) { HoldPoll = TimeSpan.FromMilliseconds(100) };
        }

        /// <summary>Publishes the statistics of one session of a machine, as its control connection does: tokens in and out this quarter hour.</summary>
        public async Task PublishAsync(DeviceIdentity machine, long version, long input, long output)
        {
            var device = await Server.GetService<ICoordinatorStore>().GetDeviceAsync(machine.DeviceId, CancellationToken.None) ?? throw new InvalidOperationException();
            var row = new FleetStatisticsRow(FleetStatisticsFamilies.Usage, FleetStatisticsFamilies.QuarterOf(DateTimeOffset.UtcNow), "anthropic", "claude", [""],
                new Dictionary<string, long> { ["input_tokens"] = input, ["output_tokens"] = output, ["requests"] = 1 });
            await Server.GetService<StatisticsService>().PublishAsync(device, new FleetStatisticsPublish(version, "generation-" + machine.DeviceId,
                [new FleetStatisticsSet("session-of-" + machine.DeviceId, 0, version, null, [row])]), CancellationToken.None);
        }

        public async Task<IReadOnlyList<AuditRecordDto>> AuditAsync(string prefix)
            => [.. (await Server.SendAsync(TestDevice.From(DesktopKey), HttpMethod.Get, "/api/v1/audit?limit=500")).Read<ListResponse<AuditRecordDto>>().Items
                .Where(record => record.Action.StartsWith(prefix, StringComparison.Ordinal))];

        public async Task DisposeServerAsync()
        {
            if (_serverDisposed) return;
            _serverDisposed = true;
            await Server.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeServerAsync();
            DesktopKey.Dispose();
            NodeKey.Dispose();
            OtherKey.Dispose();
            if (OwnsRoot) Root.Dispose();
        }
    }
}
