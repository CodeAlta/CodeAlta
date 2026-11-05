using System.Collections.Immutable;
using System.Security.Cryptography;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Fleet.Update;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The acknowledgement of a cancellation, between the real update agent of a node and the real campaign engine of the
/// server: whatever final state the node's campaign reached, the server hears it, and stops sending the cancellation.
/// </summary>
[TestClass]
public sealed class UpdateAcknowledgementTests
{
    private const string Organization = "org-a";
    private const string Runtime = "win-x64";
    private const string LinkId = "link-1";

    [TestMethod]
    public async Task EveryFinalStateOfANodeAcknowledgesACancellationOnTheServer()
    {
        var states = new[]
        {
            FleetUpdateMachineStates.Updated, FleetUpdateMachineStates.Failed, FleetUpdateMachineStates.RolledBack, FleetUpdateMachineStates.Refused,
            FleetUpdateMachineStates.Cancelled, FleetUpdateMachineStates.Superseded,
        };
        Assert.IsTrue(states.All(FleetUpdateMachineStates.IsTerminal));
        foreach (var state in states)
        {
            await using var node = new Node();
            var report = await node.EndAsync(state);
            Assert.AreEqual(("c1", state), (report.CampaignId, report.State), $"the node reports how campaign c1 ended ({state})");

            // The server cancelled the campaign once it had asked the node: the cancellation is due until acknowledged.
            var at = node.Clock.GetUtcNow();
            var machine = new UpdateCampaignMachineRecord("c1", "node-1", FleetUpdateMachineStates.Cancelled, "4.0.1", null, at, at, CancelPending: true);
            var applied = UpdateCampaignEngine.Apply(machine, report, at);
            Assert.IsFalse(applied.CancelPending, $"a node whose campaign is {state} acknowledged its cancellation");
        }
    }

    /// <summary>A node's update agent on a folder of its own, with a controller that signs and a faked self-update.</summary>
    private sealed class Node : IAsyncDisposable
    {
        private readonly TempDirectory _folder = new();
        private readonly TestDevice _owner = new("Desktop");
        private readonly FleetDeviceListState _devices;
        private string _running = "4.0.1";
        private FleetUpdateStatus? _status;
        private bool _fails;
        private FleetUpdateAgent _agent;

        public Node()
        {
            FleetDevicePublicKey.TryParse(_owner.PublicKey, out var key);
            _devices = new FleetDeviceListState(Organization, 1, "hash",
                ImmutableDictionary.Create<string, FleetDeviceListMember>(StringComparer.Ordinal).Add(_owner.DeviceId,
                    new FleetDeviceListMember(_owner.DeviceId, FleetDeviceKinds.Controller, key!, "person-1")),
                ImmutableHashSet.Create<string>(StringComparer.Ordinal));
            _agent = Start();
        }

        public TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

        /// <summary>Takes campaign c1 to a final state, then gives what the node answers to its cancellation.</summary>
        public async Task<FleetUpdateReport> EndAsync(string state)
        {
            var c1 = Instruction("c1", 1, "4.0.2");
            switch (state)
            {
                case FleetUpdateMachineStates.Refused:
                    await AcceptAsync(c1 with { Manifest = c1.Manifest! with { Signature = "" } });
                    break;
                case FleetUpdateMachineStates.Superseded:
                    await AcceptAsync(c1);
                    await AcceptAsync(Instruction("c2", 2, "4.0.3"));
                    break;
                case FleetUpdateMachineStates.Cancelled:
                    await AcceptAsync(c1);
                    _agent.CancelAndReport(LinkId, new FleetUpdateCancel("c1"));
                    break;
                case FleetUpdateMachineStates.Failed:
                    _fails = true;
                    await AcceptAsync(c1);
                    await (_agent.Tick() ?? Task.CompletedTask);
                    break;
                case FleetUpdateMachineStates.Updated:
                    // It switched and its trial confirmed.
                    await AcceptAsync(c1);
                    await (_agent.Tick() ?? Task.CompletedTask);
                    Restart("4.0.2", null);
                    break;
                case FleetUpdateMachineStates.RolledBack:
                    await AcceptAsync(c1);
                    await (_agent.Tick() ?? Task.CompletedTask);
                    Clock.Advance(TimeSpan.FromMinutes(2));
                    Restart("4.0.1", new FleetUpdateOutcome("4.0.2", UpdateOutcomes.RolledBack, Clock.GetUtcNow(), "It did not confirm in time."));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }

            return _agent.CancelAndReport(LinkId, new FleetUpdateCancel("c1")) ?? throw new AssertFailedException("The node did not acknowledge the cancellation.");
        }

        private void Restart(string running, FleetUpdateOutcome? outcome)
        {
            _agent.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _running = running;
            _status = new FleetUpdateStatus(running, null, running, null, null, null, [], outcome);
            _agent = Start();
        }

        private FleetUpdateAgent Start() => new(new FleetUpdateAgentOptions
        {
            StatePath = Path.Combine(_folder.Path, "fleet", "update-campaign.json"), DeviceId = "node-1", Running = UpdateVersion.Parse(_running), Runtime = Runtime,
            Policy = static () => UpdateSourcePolicy.From(new UpdateSourceSettings()),
            Apply = (request, _, _) => _fails
                ? throw new IOException("The disk is full.")
                : Task.FromResult<StagedUpdate?>(new StagedUpdate(UpdateVersion.Parse(request.Version!), _folder.Path, PackageHash.Parse(request.ExpectedSha512!), "expected hash", false)),
            Check = static (_, _) => throw new InvalidOperationException("No automatic campaign here."),
            Status = () => _status ?? new FleetUpdateStatus(_running, null, null, null, null, null, [], null),
            Time = Clock,
        });

        private Task AcceptAsync(FleetUpdateInstruction instruction)
            => _agent.AcceptAsync(LinkId, Organization, instruction, _ => Task.FromResult<FleetDeviceListState?>(_devices), CancellationToken.None);

        private FleetUpdateInstruction Instruction(string campaign, long sequence, string version)
            => new(campaign, _owner.SignManifest(new FleetUpdateManifest(FleetUpdateManifest.CurrentVersion, Organization, campaign, sequence, version, UpdateFeeds.NuGetOrg,
                false, null, null, null,
                new Dictionary<string, IReadOnlyDictionary<string, string>> { ["CodeAlta.Node"] = new Dictionary<string, string> { [Runtime] = Convert.ToBase64String(SHA512.HashData("n"u8)) } },
                _owner.DeviceId, DateTimeOffset.UnixEpoch)));

        public async ValueTask DisposeAsync()
        {
            await _agent.DisposeAsync();
            _owner.Dispose();
            _folder.Dispose();
        }
    }
}
