using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The state machine of an update campaign (<c>doc/fleet/deployment.md</c>, updates): it starts at its time, asks nodes a
/// bounded number at a time, counts a node offline as pending (asked when it reconnects), updates the server last, and
/// can be cancelled before the server's step.
/// </summary>
[TestClass]
public sealed class UpdateCampaignEngineTests
{
    private const string Target = "4.0.2";
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AScheduledCampaignWaitsForItsTime()
    {
        var clock = new TestClock(Start);
        var fleet = new Fleet(clock, "a", "b");
        fleet.Campaign = fleet.Campaign with { NotBefore = Start.AddHours(2) };

        var step = fleet.Step();
        Assert.AreEqual(FleetUpdateCampaignStates.Scheduled, fleet.Campaign.State);
        Assert.IsFalse(step.Changed(step.Campaign), "nothing happens before its time");
        Assert.AreEqual(0, step.Instruct.Count);

        clock.Advance(TimeSpan.FromHours(2));
        step = fleet.Step();
        Assert.AreEqual(FleetUpdateCampaignStates.UpdatingNodes, fleet.Campaign.State);
        CollectionAssert.AreEqual(new[] { "a", "b" }, step.Instruct.ToArray());
    }

    [TestMethod]
    public void NodesUpdateABoundedNumberAtATimeAndTheServerLast()
    {
        var clock = new TestClock(Start);
        var fleet = new Fleet(clock, "a", "b", "c", "d", "e");

        CollectionAssert.AreEqual(new[] { "a", "b" }, fleet.Step().Instruct.ToArray(), "two at a time");
        Assert.AreEqual(0, fleet.Step().Instruct.Count, "the others wait for a slot");

        fleet.Report("a", FleetUpdateMachineStates.Downloading);
        Assert.AreEqual(0, fleet.Step().Instruct.Count, "a node that works holds its slot");
        fleet.Switched("a");
        var step = fleet.Step();
        CollectionAssert.AreEqual(new[] { "c" }, step.Instruct.ToArray(), "a node done frees its slot");
        Assert.AreEqual(FleetUpdateMachineStates.Updated, fleet.Machine("a").State, "a node that runs the version is updated");

        fleet.Report("b", FleetUpdateMachineStates.RolledBack, "it did not confirm");
        CollectionAssert.AreEqual(new[] { "d" }, fleet.Step().Instruct.ToArray());
        fleet.Switched("c");
        fleet.Report("d", FleetUpdateMachineStates.Failed, "hash mismatch");
        step = fleet.Step();
        CollectionAssert.AreEqual(new[] { "e" }, step.Instruct.ToArray());
        Assert.IsFalse(step.UpdateServer, "the server waits for the last node");
        fleet.Switched("e");

        step = fleet.Step();
        Assert.IsTrue(step.UpdateServer, "the server updates itself once every node online is done");
        Assert.AreEqual((FleetUpdateCampaignStates.UpdatingServer, FleetUpdateMachineStates.Instructed), (fleet.Campaign.State, fleet.Campaign.ServerState));
        Assert.IsFalse(fleet.Step().UpdateServer, "once");

        fleet.ServerRunning = Target;
        fleet.Step();
        Assert.AreEqual((FleetUpdateCampaignStates.PartlyDone, FleetUpdateMachineStates.Updated), (fleet.Campaign.State, fleet.Campaign.ServerState),
            "two nodes failed or rolled back");
    }

    [TestMethod]
    public void ANodeOfflineIsPendingAndUpdatesWhenItReconnects()
    {
        var clock = new TestClock(Start);
        var fleet = new Fleet(clock, "a", "b");
        fleet.Online["b"] = false;

        CollectionAssert.AreEqual(new[] { "a" }, fleet.Step().Instruct.ToArray(), "an offline node is not asked");
        fleet.Switched("a");
        var step = fleet.Step();
        Assert.IsTrue(step.UpdateServer, "an offline node does not hold the campaign");
        Assert.AreEqual(FleetUpdateMachineStates.Pending, fleet.Machine("b").State, "counted pending, not failed");
        fleet.ServerRunning = Target;
        fleet.Step();
        Assert.AreEqual(FleetUpdateCampaignStates.PartlyDone, fleet.Campaign.State);

        clock.Advance(TimeSpan.FromDays(2));
        fleet.Online["b"] = true;
        CollectionAssert.AreEqual(new[] { "b" }, fleet.Step().Instruct.ToArray(), "asked once it reconnects");
        fleet.Switched("b");
        fleet.Step();
        Assert.AreEqual(FleetUpdateCampaignStates.Done, fleet.Campaign.State, "every machine runs the version now");
    }

    [TestMethod]
    public void ANodeThatGoesOfflineWhileItSwitchesHoldsTheServerWithinAGrace()
    {
        var clock = new TestClock(Start);
        var fleet = new Fleet(clock, "a");
        fleet.Step();
        fleet.Report("a", FleetUpdateMachineStates.Switching);
        fleet.Online["a"] = false;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsFalse(fleet.Step().UpdateServer, "it restarts into the new version");

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.IsTrue(fleet.Step().UpdateServer, "past the grace, it does not hold the server");
        fleet.ServerRunning = Target;
        fleet.Step();
        Assert.AreEqual(FleetUpdateCampaignStates.PartlyDone, fleet.Campaign.State);

        fleet.Online["a"] = true;
        fleet.Running["a"] = Target;
        fleet.Step();
        Assert.AreEqual((FleetUpdateCampaignStates.Done, FleetUpdateMachineStates.Updated), (fleet.Campaign.State, fleet.Machine("a").State));
    }

    [TestMethod]
    public void AnInstructionWithoutReportIsSentAgain()
    {
        var clock = new TestClock(Start);
        var fleet = new Fleet(clock, "a");
        CollectionAssert.AreEqual(new[] { "a" }, fleet.Step().Instruct.ToArray());
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, fleet.Step().Instruct.Count);
        clock.Advance(TimeSpan.FromSeconds(31));
        CollectionAssert.AreEqual(new[] { "a" }, fleet.Step().Instruct.ToArray(), "the instruction may have been lost with a connection");
        fleet.Report("a", FleetUpdateMachineStates.Waiting);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(0, fleet.Step().Instruct.Count, "a node that reported is not asked again");
    }

    [TestMethod]
    public void AVersionThatFailsOnEveryNodeDoesNotGoToTheServer()
    {
        var clock = new TestClock(Start);
        var fleet = new Fleet(clock, "a", "b");
        fleet.Step();
        fleet.Report("a", FleetUpdateMachineStates.RolledBack);
        fleet.Report("b", FleetUpdateMachineStates.Failed);
        var step = fleet.Step();
        Assert.IsFalse(step.UpdateServer);
        Assert.AreEqual(FleetUpdateCampaignStates.Failed, fleet.Campaign.State);
        StringAssert.Contains(fleet.Campaign.ServerMessage, "did not update");
    }

    [TestMethod]
    public void AServerThatRollsBackFailsTheCampaign()
    {
        var clock = new TestClock(Start);
        var fleet = new Fleet(clock);
        Assert.IsTrue(fleet.Step().UpdateServer, "a campaign without nodes goes to the server at once");
        clock.Advance(TimeSpan.FromMinutes(3));
        fleet.ServerOutcome = new FleetUpdateOutcome(Target, "rolled_back", clock.Now, "It did not answer its health request.");
        fleet.Step();
        Assert.AreEqual((FleetUpdateCampaignStates.Failed, FleetUpdateMachineStates.RolledBack), (fleet.Campaign.State, fleet.Campaign.ServerState));
        Assert.AreEqual("It did not answer its health request.", fleet.Campaign.ServerMessage);
    }

    [TestMethod]
    public void ACampaignIsCancelledOnlyBeforeTheServersStep()
    {
        var clock = new TestClock(Start);
        var fleet = new Fleet(clock, "a", "b", "c");
        fleet.Step();
        fleet.Switched("a");
        fleet.Step();
        fleet.Report("b", FleetUpdateMachineStates.Downloading);

        var cancelled = UpdateCampaignEngine.Cancel(fleet.Campaign, fleet.Machines, clock.Now);
        Assert.IsNotNull(cancelled);
        Assert.AreEqual((FleetUpdateCampaignStates.Cancelled, FleetUpdateMachineStates.Cancelled), (cancelled.Value.Campaign.State, cancelled.Value.Campaign.ServerState));
        CollectionAssert.AreEquivalent(new[] { "b", "c" }, cancelled.Value.Machines.Select(static machine => machine.DeviceId).ToArray(), "the updated node stays updated");
        CollectionAssert.AreEquivalent(new[] { "b", "c" }, cancelled.Value.Asked.ToArray(), "the nodes asked are told");

        // A node that switched before the cancellation reached it is updated still.
        var late = UpdateCampaignEngine.Apply(cancelled.Value.Machines.Single(static machine => machine.DeviceId == "b"),
            new FleetUpdateReport(fleet.Campaign.Id, FleetUpdateMachineStates.Updated, Target, Target, null, clock.Now), clock.Now);
        Assert.AreEqual(FleetUpdateMachineStates.Updated, late.State);

        var other = new Fleet(clock);
        other.Step();
        Assert.AreEqual(FleetUpdateCampaignStates.UpdatingServer, other.Campaign.State);
        Assert.IsNull(UpdateCampaignEngine.Cancel(other.Campaign, other.Machines, clock.Now), "too late once the server updates");
    }

    /// <summary>A campaign, its machines and what the server knows of them, moved by hand.</summary>
    private sealed class Fleet
    {
        private readonly TestClock _clock;
        private readonly UpdateCampaignEngine _engine = new(new UpdateCampaignLimits { MaxConcurrent = 2, OfflineGrace = TimeSpan.FromMinutes(10), ResendAfter = TimeSpan.FromMinutes(1) });

        public Fleet(TestClock clock, params string[] nodes)
        {
            _clock = clock;
            Campaign = new UpdateCampaignRecord("c1", "org", 1, false, Target, "https://feed.test/v3/index.json", false, null, null, "{}", "sig", "desktop",
                FleetUpdateCampaignStates.Scheduled, FleetUpdateMachineStates.Pending, null, clock.Now, clock.Now);
            Machines = [.. nodes.Select(node => new UpdateCampaignMachineRecord("c1", node, FleetUpdateMachineStates.Pending, null, null, null, clock.Now))];
            foreach (var node in nodes)
            {
                Online[node] = true;
                Running[node] = "4.0.1";
            }
        }

        public UpdateCampaignRecord Campaign { get; set; }

        public List<UpdateCampaignMachineRecord> Machines { get; }

        public Dictionary<string, bool> Online { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Running { get; } = new(StringComparer.Ordinal);

        public string ServerRunning { get; set; } = "4.0.1";

        public FleetUpdateOutcome? ServerOutcome { get; set; }

        public UpdateCampaignMachineRecord Machine(string id) => Machines.Single(machine => machine.DeviceId == id);

        public UpdateCampaignStep Step()
        {
            var step = _engine.Step(Campaign, Machines, id => new UpdateCampaignNode(Online[id], Running[id]), new UpdateCampaignServer(ServerRunning, ServerOutcome), _clock.Now);
            Campaign = step.Campaign;
            foreach (var changed in step.Machines) Machines[Machines.FindIndex(machine => machine.DeviceId == changed.DeviceId)] = changed;
            return step;
        }

        public void Report(string id, string state, string? message = null)
        {
            var index = Machines.FindIndex(machine => machine.DeviceId == id);
            Machines[index] = UpdateCampaignEngine.Apply(Machines[index], new FleetUpdateReport("c1", state, Target, Running[id], message, _clock.Now), _clock.Now);
        }

        public void Switched(string id) => Running[id] = Target;
    }
}
