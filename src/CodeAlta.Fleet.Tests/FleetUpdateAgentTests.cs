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
        harness.Staged = false;
        var agent = harness.Start();
        var c5 = harness.Instruction(harness.Manifest(5, "c5", "4.0.2"));
        await harness.AcceptAsync(agent, LinkA, c5);
        await (agent.Tick() ?? Task.CompletedTask);
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

    public List<UpdateRequest> Applied { get; } = [];

    public List<string> Logs { get; } = [];

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
            Check = (_, _) => throw new InvalidOperationException("No automatic campaign here."),
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
