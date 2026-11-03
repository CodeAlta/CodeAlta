using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Fleet.Update;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The node's side of the coordinated update of the fleet (<c>doc/fleet/deployment.md</c>, updates), with its
/// self-update faked: what a node keeps of the campaigns it accepted, what it refuses, and what it reports.
/// </summary>
[TestClass]
public sealed class FleetUpdateAgentTests
{
    private const string LinkA = "link-a";
    private const string LinkB = "link-b";

    [TestMethod]
    public async Task ACancelledManifestReplayedAfterARefusalIsNotTakenAgain()
    {
        using var harness = new AgentHarness();
        var agent = harness.Start();
        var c5 = harness.Instruction(harness.Manifest(5, "c5", "4.0.2"));
        await harness.AcceptAsync(agent, LinkA, c5);
        agent.Cancel(LinkA, new FleetUpdateCancel("c5"));
        Assert.AreEqual(FleetUpdateMachineStates.Cancelled, agent.Current?.State);

        // An instruction the node refuses does not replace what it keeps of the campaign it cancelled.
        var tampered = harness.Instruction(harness.Manifest(6, "c6", "4.0.3"));
        await harness.AcceptAsync(agent, LinkA, tampered with { Manifest = tampered.Manifest! with { Signature = "" } });
        Assert.AreEqual(FleetUpdateMachineStates.Refused, agent.Report(LinkA)?.State, "the refusal is reported to the server that asked");

        await harness.AcceptAsync(agent, LinkA, c5);
        Assert.AreEqual(("c5", FleetUpdateMachineStates.Cancelled), (agent.Current?.CampaignId, agent.Current?.State), "the replay only gets its receipt");
        Assert.IsNull(agent.Tick(), "nothing starts again");
        Assert.AreEqual(0, harness.Applied.Count);
    }

    [TestMethod]
    public async Task ACompletedManifestReplayedAfterARefusalIsNotInstalledAgain()
    {
        using var harness = new AgentHarness();
        var agent = harness.Start();
        var c5 = harness.Instruction(harness.Manifest(5, "c5", "4.0.2"));
        await harness.AcceptAsync(agent, LinkA, c5);
        await (agent.Tick() ?? Task.CompletedTask);
        harness.Running = "4.0.2";
        agent = harness.Restart(agent);
        Assert.AreEqual(FleetUpdateMachineStates.Updated, agent.Current?.State);

        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(6, "c6", "4.0.3")) with { Manifest = null });
        await harness.AcceptAsync(agent, LinkA, c5);
        Assert.AreEqual(("c5", FleetUpdateMachineStates.Updated), (agent.Current?.CampaignId, agent.Current?.State));
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(1, harness.Applied.Count, "installed once");
    }

    [TestMethod]
    public async Task AnInstructionOfAnotherFleetIsRefusedWhileACampaignRunsAndTheFirstFleetKeepsItsReport()
    {
        using var harness = new AgentHarness();
        using var otherOwner = new UpdateSigner();
        var agent = harness.Start();
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(1, "a1", "4.0.2")));
        Assert.AreEqual(FleetUpdateMachineStates.Waiting, agent.Report(LinkA)?.State);

        // Another server, its own organization and controller: its campaign waits for the first to end.
        var other = harness.Instruction(AgentHarness.Manifest(otherOwner, "org-b", 1, "b1", "4.0.3"), otherOwner);
        await harness.AcceptAsync(agent, LinkB, other, "org-b", AgentHarness.Devices(otherOwner, "org-b"));
        Assert.AreEqual(("a1", FleetUpdateMachineStates.Waiting), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State), "the first fleet is not abandoned");
        Assert.AreEqual(("b1", FleetUpdateMachineStates.Refused), (agent.Report(LinkB)?.CampaignId, agent.Report(LinkB)?.State));
        StringAssert.Contains(agent.Report(LinkB)?.Message, "another server");

        // Once the first ends, the other fleet's campaign is taken, and the first keeps its receipt.
        agent.Cancel(LinkA, new FleetUpdateCancel("a1"));
        await harness.AcceptAsync(agent, LinkB, other, "org-b", AgentHarness.Devices(otherOwner, "org-b"));
        Assert.AreEqual(("b1", FleetUpdateMachineStates.Waiting), (agent.Report(LinkB)?.CampaignId, agent.Report(LinkB)?.State));
        Assert.AreEqual(("a1", FleetUpdateMachineStates.Cancelled), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State));
        var restarted = harness.Restart(agent);
        Assert.AreEqual(("a1", FleetUpdateMachineStates.Cancelled), (restarted.Report(LinkA)?.CampaignId, restarted.Report(LinkA)?.State), "kept across a restart");
    }

    [TestMethod]
    public async Task ACancellationSentAgainIsAcknowledgedWithAFinalState()
    {
        using var harness = new AgentHarness();
        var agent = harness.Start();
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(1, "c1", "4.0.2")));
        Assert.AreEqual(("c1", FleetUpdateMachineStates.Cancelled), (agent.CancelAndReport(LinkA, new FleetUpdateCancel("c1"))?.CampaignId, agent.Current?.State));

        // The server tells the node again until it hears that the campaign ended: each time, the node says how it ended.
        Assert.AreEqual(("c1", FleetUpdateMachineStates.Cancelled), (agent.CancelAndReport(LinkA, new FleetUpdateCancel("c1")) is { } again ? (again.CampaignId, again.State) : default));
        var unknown = agent.CancelAndReport(LinkA, new FleetUpdateCancel("c9"));
        Assert.IsTrue(FleetUpdateMachineStates.IsTerminal(unknown?.State), "a campaign this node never took is done with too");
        Assert.AreEqual(("c1", FleetUpdateMachineStates.Cancelled), (agent.Current?.CampaignId, agent.Current?.State));
    }

    [TestMethod]
    public async Task ACancellationIsKeptBeforeItIsAcknowledgedEvenBeforeItsInstruction()
    {
        using var harness = new AgentHarness();
        var agent = harness.Start();

        // The cancellation comes first; the node restarts; then the instruction comes.
        Assert.AreEqual(("c1", FleetUpdateMachineStates.Cancelled), agent.CancelAndReport(LinkA, new FleetUpdateCancel("c1")) is { } ack ? (ack.CampaignId, ack.State) : default);
        agent = harness.Restart(agent);
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(1, "c1", "4.0.2")));
        Assert.IsNull(agent.Current, "a campaign cancelled before its instruction never starts");
        StringAssert.Contains(agent.Report(LinkA)?.Message, "cancelled");

        // The cancellation comes while the instruction is checked (its device list is read): the instruction loses.
        await agent.AcceptAsync(LinkA, AgentHarness.Organization, harness.Instruction(harness.Manifest(2, "c2", "4.0.2")), _ =>
        {
            Assert.AreEqual(FleetUpdateMachineStates.Cancelled, agent.CancelAndReport(LinkA, new FleetUpdateCancel("c2"))?.State);
            return Task.FromResult<FleetDeviceListState?>(harness.DeviceList);
        }, CancellationToken.None);
        Assert.IsNull(agent.Current);
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(0, harness.Applied.Count);
    }

    [TestMethod]
    public async Task ACancellationThatCannotBeKeptIsNotAcknowledged()
    {
        using var harness = new AgentHarness();
        var agent = harness.Start();
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(1, "c1", "4.0.2")));
        using (harness.FailWrites())
        {
            Assert.IsNull(agent.CancelAndReport(LinkA, new FleetUpdateCancel("c1")), "no acknowledgement: the file still holds the campaign waiting");
            Assert.IsNull(agent.Report(LinkA), "nor a report that would acknowledge it");
            Assert.AreEqual(FleetUpdateMachineStates.Cancelled, agent.Current?.State, "its work stops all the same");
            Assert.IsNull(agent.Tick());
        }

        // Sent again once it can be kept, the cancellation is acknowledged, and it holds across a restart.
        Assert.AreEqual(FleetUpdateMachineStates.Cancelled, agent.CancelAndReport(LinkA, new FleetUpdateCancel("c1"))?.State);
        agent = harness.Restart(agent);
        Assert.AreEqual(("c1", FleetUpdateMachineStates.Cancelled), (agent.Current?.CampaignId, agent.Current?.State));
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(0, harness.Applied.Count);
    }

    [TestMethod]
    public async Task ANewerPolicyThatCannotBeKeptStopsTheWorkOfTheFleetUntilItIsKept()
    {
        using var harness = new AgentHarness();
        harness.Latest = UpdateVersion.Parse("4.1.0");
        var agent = harness.Start();
        await harness.AcceptAsync(agent, LinkA, harness.Automatic("auto-1", harness.Policy(2, FleetUpdateModes.Automatic), "4.1.0"));
        using (harness.FailWrites())
        {
            await harness.AcceptPolicyAsync(agent, LinkA, harness.Owner.Sign(harness.Policy(3, FleetUpdateModes.Manual)));
            Assert.AreEqual(FleetUpdateMachineStates.Cancelled, agent.Current?.State, "it takes away at once, in memory");
            Assert.IsNull(agent.Report(LinkA), "the campaign it stopped is not acknowledged before it is kept");
            await harness.AcceptAsync(agent, LinkA, harness.Automatic("auto-2", harness.Policy(4, FleetUpdateModes.Automatic), "4.1.0"));
            Assert.AreEqual(("auto-2", FleetUpdateMachineStates.Refused), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State), "no instruction is taken");
            Assert.IsNull(agent.Tick(), "no work of the fleet runs");
        }

        // The write is tried again: once it is kept, a restart does not bring the older policy and its campaign back.
        Assert.IsNull(agent.Tick());
        agent = harness.Restart(agent);
        Assert.AreEqual(("auto-1", FleetUpdateMachineStates.Cancelled), (agent.Current?.CampaignId, agent.Current?.State));
        await harness.AcceptAsync(agent, LinkA, harness.Automatic("auto-3", harness.Policy(2, FleetUpdateModes.Automatic), "4.1.0"));
        StringAssert.Contains(agent.Report(LinkA)?.Message, "older than policy 3");
        Assert.AreEqual(0, harness.Applied.Count);
    }

    [TestMethod]
    public async Task ACheckpointThatDoesNotReadRefusesEveryUpdateAndIsLeftAsItIs()
    {
        using var harness = new AgentHarness();
        var agent = harness.Start();
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(6, "c6", "4.0.2")));
        agent.Cancel(LinkA, new FleetUpdateCancel("c6"));
        File.WriteAllText(harness.StatePath, "{ not json");

        // Read as empty, the checkpoint would let an older manifest through again.
        agent = harness.Restart(agent);
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(5, "c5", "4.0.2")));
        Assert.AreEqual(("c5", FleetUpdateMachineStates.Refused), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State));
        StringAssert.Contains(agent.Report(LinkA)?.Message, "update-campaign.json cannot be read");
        StringAssert.Contains(agent.Report(LinkA)?.Message, "delete it");
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(0, harness.Applied.Count);
        Assert.AreEqual("{ not json", File.ReadAllText(harness.StatePath), "the owner's file is not replaced");

        // Once the owner resets it, a campaign is taken again.
        File.Delete(harness.StatePath);
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(7, "c7", "4.0.2")));
        Assert.AreEqual(("c7", FleetUpdateMachineStates.Waiting), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State));
    }

    [TestMethod]
    public async Task AnInstructionWhoseCheckpointCannotBeWrittenIsNotTaken()
    {
        using var harness = new AgentHarness();
        // The folder of the checkpoint is a file: nothing can be written there.
        File.WriteAllText(Path.GetDirectoryName(harness.StatePath)!, "");
        var agent = harness.Start();

        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(1, "c1", "4.0.2")));
        Assert.AreEqual(("c1", FleetUpdateMachineStates.Refused), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State));
        StringAssert.Contains(agent.Report(LinkA)?.Message, "could not be kept");
        Assert.IsNull(agent.Current, "nothing accepted");
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(0, harness.Applied.Count);
    }

    [TestMethod]
    public async Task ANodeIsUpdatedOnlyOnceItsTrialConfirmedAndARollbackIsReported()
    {
        using var harness = new AgentHarness();
        var agent = harness.Start();
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(1, "c1", "4.0.2")));
        await (agent.Tick() ?? Task.CompletedTask);
        Assert.AreEqual(FleetUpdateMachineStates.Switching, agent.Current?.State);

        // It starts the new version on trial: running it is not enough.
        harness.Running = "4.0.2";
        harness.Status = harness.OnTrial("4.0.2");
        agent = harness.Restart(agent);
        Assert.AreEqual(FleetUpdateMachineStates.Switching, agent.Current?.State, "not updated before its trial confirmed");
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(FleetUpdateMachineStates.Switching, agent.Current?.State);

        // It does not confirm: its launcher rolls it back, and the server is told.
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        harness.Running = "4.0.1";
        harness.Status = harness.Outcome("4.0.2", UpdateOutcomes.RolledBack, "It did not confirm in time.");
        agent = harness.Restart(agent);
        Assert.AreEqual((FleetUpdateMachineStates.RolledBack, "It did not confirm in time."), (agent.Current?.State, agent.Current?.Message));
    }

    [TestMethod]
    public async Task ATrialThatConfirmsMakesTheNodeUpdated()
    {
        using var harness = new AgentHarness();
        harness.Staged = false;
        // It runs the version already, on trial (an update of its own): it is updated once that trial confirmed.
        harness.Running = "4.0.2";
        harness.Status = harness.OnTrial("4.0.2");
        var agent = harness.Start();
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(1, "c1", "4.0.2")));
        await (agent.Tick() ?? Task.CompletedTask);
        Assert.AreEqual(FleetUpdateMachineStates.Switching, agent.Current?.State, "running the version on trial is not updated");

        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        harness.Status = harness.Outcome("4.0.2", UpdateOutcomes.Confirmed, null);
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(FleetUpdateMachineStates.Updated, agent.Current?.State);
    }

    [TestMethod]
    public async Task ANodeThatRunsTheVersionAlreadyCompletesItsCampaignWithoutInstallingIt()
    {
        using var harness = new AgentHarness();
        harness.Latest = UpdateVersion.Parse("4.1.0");
        harness.Running = "4.1.0";
        harness.Staged = false;
        var agent = harness.Start();

        // An automatic campaign to the version it runs, confirmed: it is updated, with nothing installed.
        await harness.AcceptAsync(agent, LinkA, harness.Automatic("auto-1", harness.Policy(1, FleetUpdateModes.Automatic), "4.1.0"));
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(("auto-1", FleetUpdateMachineStates.Updated), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State));
        Assert.AreEqual(0, harness.Applied.Count);

        // The same for a campaign a person started.
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(1, "c1", "4.1.0")));
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(("c1", FleetUpdateMachineStates.Updated), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State));
        Assert.AreEqual(0, harness.Applied.Count);
    }

    [TestMethod]
    public async Task AManualPolicyRevokesTheAutomaticPolicyItReplaced()
    {
        using var harness = new AgentHarness();
        using var outsider = new UpdateSigner();
        harness.Latest = UpdateVersion.Parse("4.1.0");
        var agent = harness.Start();
        var automatic = harness.Policy(2, FleetUpdateModes.Automatic);
        await harness.AcceptAsync(agent, LinkA, harness.Automatic("auto-1", automatic, "4.1.0"));
        Assert.AreEqual(FleetUpdateMachineStates.Waiting, agent.Current?.State);

        // A policy that does not verify, or older than the one kept, changes nothing.
        var manual = harness.Policy(3, FleetUpdateModes.Manual);
        await harness.AcceptPolicyAsync(agent, LinkA, outsider.Sign(manual with { SignerDeviceId = outsider.DeviceId }));
        await harness.AcceptPolicyAsync(agent, LinkA, harness.Owner.Sign(manual) with { Signature = "" });
        await harness.AcceptPolicyAsync(agent, LinkA, harness.Owner.Sign(manual with { OrganizationId = "org-b" }));
        await harness.AcceptPolicyAsync(agent, LinkA, harness.Owner.Sign(harness.Policy(1, FleetUpdateModes.Manual)));
        Assert.AreEqual(FleetUpdateMachineStates.Waiting, agent.Current?.State);

        // The owner turns the automatic updates off: the update that waited is dropped, and the policy it came with no longer counts.
        await harness.AcceptPolicyAsync(agent, LinkA, harness.Owner.Sign(manual));
        Assert.AreEqual(FleetUpdateMachineStates.Cancelled, agent.Current?.State);
        StringAssert.Contains(agent.Current?.Message, "manual");
        Assert.IsNull(agent.Tick());
        await harness.AcceptAsync(agent, LinkA, harness.Automatic("auto-2", automatic, "4.1.0"));
        Assert.AreEqual(("auto-2", FleetUpdateMachineStates.Refused), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State));
        StringAssert.Contains(agent.Report(LinkA)?.Message, "older than policy 3");

        agent = harness.Restart(agent);
        await harness.AcceptAsync(agent, LinkA, harness.Automatic("auto-3", automatic, "4.1.0"));
        Assert.AreEqual(("auto-3", FleetUpdateMachineStates.Refused), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State), "kept across a restart");
        Assert.AreEqual(0, harness.Applied.Count);

        // A newer automatic policy, as an instruction carries it, counts again.
        await harness.AcceptAsync(agent, LinkA, harness.Automatic("auto-4", harness.Policy(4, FleetUpdateModes.Automatic), "4.1.0"));
        Assert.AreEqual(("auto-4", FleetUpdateMachineStates.Waiting), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State));
    }

    [TestMethod]
    public async Task APinnedPolicyRefusesAndDropsAManifestForAnotherVersion()
    {
        using var harness = new AgentHarness();
        var agent = harness.Start();
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(1, "c1", "4.0.2")));
        Assert.AreEqual(FleetUpdateMachineStates.Waiting, agent.Current?.State);

        await harness.AcceptPolicyAsync(agent, LinkA, harness.Owner.Sign(harness.Policy(1, FleetUpdateModes.Pinned, pinned: "4.0.1")));
        Assert.AreEqual(FleetUpdateMachineStates.Cancelled, agent.Current?.State, "the fleet stays on 4.0.1");
        StringAssert.Contains(agent.Current?.Message, "4.0.1");

        // A manifest signed before the pin, delivered after it.
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(2, "c2", "4.0.2")));
        Assert.AreEqual(("c2", FleetUpdateMachineStates.Refused), (agent.Report(LinkA)?.CampaignId, agent.Report(LinkA)?.State));
        StringAssert.Contains(agent.Report(LinkA)?.Message, "pinned");
        Assert.AreEqual(0, harness.Applied.Count);
    }

    [TestMethod]
    public async Task AnAutomaticUpdateWaitsForTheSignedMaintenanceWindow()
    {
        using var harness = new AgentHarness();
        harness.Latest = UpdateVersion.Parse("4.1.0");
        var agent = harness.Start();
        // Noon UTC; the window opens at 02:00 in Paris, 01:00 UTC in November.
        var policy = harness.Policy(1, FleetUpdateModes.Automatic, window: "02:00", zone: "Europe/Paris");
        await harness.AcceptAsync(agent, LinkA, harness.Automatic("auto-1", policy, "4.1.0"));
        Assert.AreEqual(FleetUpdateMachineStates.Waiting, agent.Current?.State);
        Assert.IsNull(agent.Tick(), "the server sent it at noon");

        harness.Clock.Now = new DateTimeOffset(2026, 11, 4, 0, 59, 0, TimeSpan.Zero);
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(0, harness.Applied.Count);

        harness.Clock.Now = new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.Zero);
        await (agent.Tick() ?? Task.CompletedTask);
        Assert.AreEqual(1, harness.Applied.Count, "it starts within its window");
    }

    [TestMethod]
    public async Task AnAutomaticUpdateThatMissedItsWindowWaitsForTheNextOne()
    {
        using var harness = new AgentHarness();
        harness.Latest = UpdateVersion.Parse("4.1.0");
        harness.Clock.Now = new DateTimeOffset(2026, 11, 4, 1, 30, 0, TimeSpan.Zero);
        var agent = harness.Start();
        await harness.AcceptAsync(agent, LinkA, harness.Automatic("auto-1", harness.Policy(1, FleetUpdateModes.Automatic, window: "02:00", zone: "Europe/Paris"), "4.1.0"));

        // The node was off from inside its window to past its end.
        harness.Clock.Now = new DateTimeOffset(2026, 11, 4, 6, 0, 0, TimeSpan.Zero);
        agent = harness.Restart(agent);
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(0, harness.Applied.Count);
        harness.Clock.Now = new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.Zero);
        await (agent.Tick() ?? Task.CompletedTask);
        Assert.AreEqual(1, harness.Applied.Count);
    }

    [TestMethod]
    public async Task CancellingACampaignStopsItsDownloadAndLeavesALocalSwitchAsItIs()
    {
        using var harness = new AgentHarness();
        var store = new UpdateVersionStore(Path.Combine(harness.Folder, "versions"), harness.Clock);
        var download = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The self-update as the node's: it publishes its switch once the download ends, unless it was cancelled.
        var agent = harness.Start(options => options with
        {
            Apply = async (request, requestedBy, token) =>
            {
                await download.Task;
                token.ThrowIfCancellationRequested();
                var version = UpdateVersion.Parse(request.Version!);
                store.RequestSwitch(version, false, requestedBy);
                return new StagedUpdate(version, harness.Folder, PackageHash.Parse(request.ExpectedSha512!), "expected hash", false);
            },
            CancelSwitch = (requestedBy, reason) => store.CancelPending(reason, requestedBy),
        });

        // Cancelled while it downloads; meanwhile the owner asks for another version on the machine.
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(1, "c1", "4.0.2")));
        var applying = agent.Tick();
        Assert.IsNotNull(applying);
        agent.Cancel(LinkA, new FleetUpdateCancel("c1"));
        store.RequestSwitch(UpdateVersion.Parse("4.0.3"), false, "local");
        download.SetResult();
        await applying;
        Assert.AreEqual(("4.0.3", "local"), (store.Read().Pending?.Version, store.Read().Pending?.RequestedBy), "the download publishes nothing once cancelled");

        // A campaign whose switch the owner replaced: its cancellation withdraws nothing of the owner's.
        store.CancelPending(null);
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(2, "c2", "4.0.2")));
        await (agent.Tick() ?? Task.CompletedTask);
        Assert.AreEqual((FleetUpdateMachineStates.Switching, FleetUpdateAgent.Requester("c2")), (agent.Current?.State, store.Read().Pending?.RequestedBy));
        store.RequestSwitch(UpdateVersion.Parse("4.0.3"), false, "local");
        agent.Cancel(LinkA, new FleetUpdateCancel("c2"));
        Assert.AreEqual(("4.0.3", "local"), (store.Read().Pending?.Version, store.Read().Pending?.RequestedBy));

        // Not cancelled: a campaign whose switch the owner replaced does not wait for it forever.
        store.CancelPending(null);
        await harness.AcceptAsync(agent, LinkA, harness.Instruction(harness.Manifest(3, "c3", "4.0.2")));
        await (agent.Tick() ?? Task.CompletedTask);
        store.RequestSwitch(UpdateVersion.Parse("4.0.3"), false, "local");
        harness.Status = UpdateStatusReport.Describe(store.Read(), harness.Running, null);
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(FleetUpdateMachineStates.Failed, agent.Current?.State);
        StringAssert.Contains(agent.Current?.Message, "replaced");
    }
}

/// <summary>An update agent on a folder of its own, with a controller that signs, a clock and a faked self-update.</summary>
internal sealed class AgentHarness : IDisposable
{
    public const string Organization = "org-a";
    public const string Runtime = "win-x64";
    private readonly TestFolder _folder = new();

    public AgentHarness()
    {
        Owner = new UpdateSigner();
        DeviceList = Devices(Owner, Organization);
    }

    public UpdateSigner Owner { get; }

    public FleetDeviceListState DeviceList { get; }

    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    public string StatePath => Path.Combine(_folder.Path, "fleet", "update-campaign.json");

    public string Folder => _folder.Path;

    /// <summary>Gets or sets the version that runs.</summary>
    public string Running { get; set; } = "4.0.1";

    /// <summary>Gets or sets whether the faked self-update stages the version (a switch waits) or finds it runs already.</summary>
    public bool Staged { get; set; } = true;

    /// <summary>Gets or sets where the self-update stands, as the agent reads it.</summary>
    public FleetUpdateStatus? Status { get; set; }

    /// <summary>Gets or sets the latest stable version the feed lists, for an automatic campaign; null for none.</summary>
    public UpdateVersion? Latest { get; set; }

    public List<UpdateRequest> Applied { get; } = [];

    public List<string> Logs { get; } = [];

    /// <summary>Makes every write of the checkpoint fail until disposed, the file on disk kept as it is (what a restart reads).</summary>
    public IDisposable FailWrites()
    {
        if (OperatingSystem.IsWindows())
        {
            // A file open without delete sharing cannot be replaced.
            return new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        return FailUnixWrites();
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private IDisposable FailUnixWrites()
    {
        var folder = Path.GetDirectoryName(StatePath)!;
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var restore = new Restore(() => File.SetUnixFileMode(folder, mode));
        try
        {
            File.WriteAllText(Path.Combine(folder, "probe"), "");
        }
        catch (UnauthorizedAccessException)
        {
            return restore;
        }

        File.Delete(Path.Combine(folder, "probe"));
        restore.Dispose();
        Assert.Inconclusive("This user writes in a read-only folder (root): a write cannot be made to fail here.");
        return restore;
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    public FleetUpdateAgent Start(Func<FleetUpdateAgentOptions, FleetUpdateAgentOptions>? configure = null)
    {
        var options = new FleetUpdateAgentOptions
        {
            StatePath = StatePath, DeviceId = "node-1", Running = UpdateVersion.Parse(Running), Runtime = Runtime,
            Policy = () => UpdateSourcePolicy.From(new UpdateSourceSettings()),
            Apply = (request, _, _) =>
            {
                lock (Applied) Applied.Add(request);
                return Task.FromResult(Staged
                    ? new StagedUpdate(UpdateVersion.Parse(request.Version!), Folder, PackageHash.Parse(request.ExpectedSha512 ?? Hash('n')), "expected hash", false)
                    : null);
            },
            Check = (request, _) => Latest is { } latest
                ? Task.FromResult(new UpdateCheck(request.Source ?? UpdateFeeds.NuGetOrg, "CodeAlta.Node", UpdateVersion.Parse(Running), latest, latest, null))
                : throw new InvalidOperationException("No automatic campaign here."),
            Status = () => Status ?? new FleetUpdateStatus(Running, null, null, null, null, null, [], null),
            Time = Clock,
            Log = message => { lock (Logs) Logs.Add(message); },
        };
        return new FleetUpdateAgent(configure?.Invoke(options) ?? options);
    }

    /// <summary>Makes the agent again on the same folder, as a restart of the node does.</summary>
    public FleetUpdateAgent Restart(FleetUpdateAgent agent, Func<FleetUpdateAgentOptions, FleetUpdateAgentOptions>? configure = null)
    {
        agent.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return Start(configure);
    }

    public Task AcceptAsync(FleetUpdateAgent agent, string linkId, FleetUpdateInstruction instruction, string organization = Organization, FleetDeviceListState? devices = null)
        => agent.AcceptAsync(linkId, organization, instruction, _ => Task.FromResult<FleetDeviceListState?>(devices ?? DeviceList), CancellationToken.None);

    public Task AcceptPolicyAsync(FleetUpdateAgent agent, string linkId, FleetSignedUpdateDocument policy)
        => agent.AcceptPolicyAsync(linkId, Organization, policy, _ => Task.FromResult<FleetDeviceListState?>(DeviceList), CancellationToken.None);

    public FleetUpdatePolicy Policy(long sequence, string mode, string? window = null, string? zone = null, string? pinned = null)
        => new(FleetUpdatePolicy.CurrentVersion, Organization, sequence, mode, pinned, null, window, zone, Owner.DeviceId, DateTimeOffset.UnixEpoch);

    public FleetUpdateInstruction Automatic(string campaign, FleetUpdatePolicy policy, string version) => new(campaign, null, Owner.Sign(policy), version);

    public FleetUpdateManifest Manifest(long sequence, string campaign, string version) => Manifest(Owner, Organization, sequence, campaign, version);

    public FleetUpdateInstruction Instruction(FleetUpdateManifest manifest, UpdateSigner? signer = null)
        => new(manifest.CampaignId, (signer ?? Owner).Sign(manifest));

    public static FleetUpdateManifest Manifest(UpdateSigner signer, string organization, long sequence, string campaign, string version)
        => new(FleetUpdateManifest.CurrentVersion, organization, campaign, sequence, version, UpdateFeeds.NuGetOrg, false, null, null, null,
            new Dictionary<string, IReadOnlyDictionary<string, string>> { ["CodeAlta.Node"] = new Dictionary<string, string> { [Runtime] = Hash('n') } },
            signer.DeviceId, DateTimeOffset.UnixEpoch);

    public static FleetDeviceListState Devices(UpdateSigner owner, string organization)
        => new(organization, 1, "hash", ImmutableDictionary.Create<string, FleetDeviceListMember>(StringComparer.Ordinal).Add(owner.DeviceId, owner.Member(FleetDeviceKinds.Controller)),
            ImmutableHashSet.Create<string>(StringComparer.Ordinal));

    public static string Hash(char seed) => Convert.ToBase64String(SHA512.HashData([(byte)seed]));

    /// <summary>Gets the status of a node that runs a version on trial.</summary>
    public FleetUpdateStatus OnTrial(string version) => new(Running, null, version, "4.0.1", new FleetUpdateTrial(version, Clock.GetUtcNow(), 1), null, [], null);

    /// <summary>Gets the status of a node whose last switch ended.</summary>
    public FleetUpdateStatus Outcome(string version, string outcome, string? message)
        => new(Running, null, Running, null, null, null, [], new FleetUpdateOutcome(version, outcome, Clock.GetUtcNow(), message));

    public void Dispose()
    {
        Owner.Dispose();
        _folder.Dispose();
    }
}

/// <summary>A controller device that signs the documents of the fleet's updates.</summary>
internal sealed class UpdateSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public UpdateSigner()
    {
        FleetDevicePublicKey.TryParse(Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()), out var key);
        Key = key!;
    }

    public FleetDevicePublicKey Key { get; }

    public string DeviceId => Key.DeviceId;

    public FleetDeviceListMember Member(string kind) => new(DeviceId, kind, Key, "person-1");

    public FleetSignedUpdateDocument Sign(FleetUpdateManifest manifest)
    {
        var text = FleetUpdateDocuments.Serialize(manifest);
        return new FleetSignedUpdateDocument(text, Base64Url.EncodeToString(_key.SignData(FleetUpdateDocuments.GetManifestSignedBytes(text), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    public FleetSignedUpdateDocument Sign(FleetUpdatePolicy policy)
    {
        var text = FleetUpdateDocuments.Serialize(policy);
        return new FleetSignedUpdateDocument(text, Base64Url.EncodeToString(_key.SignData(FleetUpdateDocuments.GetPolicySignedBytes(text), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    public void Dispose() => _key.Dispose();
}
