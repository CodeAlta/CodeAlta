using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Fleet.Update;

namespace CodeAlta.Node.Tests;

/// <summary>The coordinated update of the fleet on a node, with the node's own self-update: what it takes as a version confirmed.</summary>
[TestClass]
public sealed class NodeFleetUpdateTests
{
    private const string Organization = "org-a";
    private const string Runtime = "win-x64";

    [TestMethod]
    public async Task AnUnreadableSelfUpdateStateConfirmsNoUpdateOfTheFleet()
    {
        using var root = new NodeTestRoot();
        var store = NodeUpdatePaths.Store(root.Profile());
        var running = UpdateVersion.Parse("99.0.0");
        // 99.0.0 runs on trial, then its state.json goes bad: read as empty, it would have no trial.
        store.Select(running);
        var statePath = Path.Combine(store.Root, "state.json");
        File.WriteAllText(statePath, "{ not json");
        var updates = new NodeUpdates(new NodeUpdatesOptions
        {
            Store = store, Running = running, SessionsAtWork = static () => 0, PauseAdmission = static _ => { }, Healthy = static () => true, RequestRestart = static () => { },
        });
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.IsTrue(FleetDevicePublicKey.TryParse(Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo()), out var key));
        var devices = new FleetDeviceListState(Organization, 1, "hash",
            ImmutableDictionary.Create<string, FleetDeviceListMember>(StringComparer.Ordinal).Add(key.DeviceId, new FleetDeviceListMember(key.DeviceId, FleetDeviceKinds.Controller, key, "person-1")),
            ImmutableHashSet.Create<string>(StringComparer.Ordinal));
        await using var agent = new FleetUpdateAgent(new FleetUpdateAgentOptions
        {
            StatePath = NodeUpdatePaths.CampaignPath(root.Profile()), DeviceId = "node-1", Running = running, Runtime = Runtime,
            Policy = static () => UpdateSourcePolicy.From(new UpdateSourceSettings()),
            // It runs the version already: nothing to install.
            Apply = static (_, _, _) => Task.FromResult<StagedUpdate?>(null),
            Check = static (_, _) => throw new InvalidOperationException("No automatic campaign here."),
            Status = updates.Status,
        });

        await agent.AcceptAsync("link-1", Organization, new FleetUpdateInstruction("c1", Sign(owner, Manifest(key.DeviceId, "99.0.0"))),
            _ => Task.FromResult<FleetDeviceListState?>(devices), CancellationToken.None);
        await (agent.Tick() ?? Task.CompletedTask);
        Assert.AreEqual(FleetUpdateMachineStates.Switching, agent.Current?.State, "a state that does not read is not a confirmed one");

        // Readable again, the trial still waits: not yet updated; once it confirmed, the node is.
        File.Delete(statePath);
        store.Select(running);
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(FleetUpdateMachineStates.Switching, agent.Current?.State);
        Assert.IsTrue(store.Confirm(running));
        Assert.IsNull(agent.Tick());
        Assert.AreEqual(FleetUpdateMachineStates.Updated, agent.Current?.State);
    }

    private static FleetUpdateManifest Manifest(string signer, string version)
        => new(FleetUpdateManifest.CurrentVersion, Organization, "c1", 1, version, UpdateFeeds.NuGetOrg, false, null, null, null,
            new Dictionary<string, IReadOnlyDictionary<string, string>> { ["CodeAlta.Node"] = new Dictionary<string, string> { [Runtime] = Convert.ToBase64String(SHA512.HashData("n"u8)) } },
            signer, DateTimeOffset.UnixEpoch);

    private static FleetSignedUpdateDocument Sign(ECDsa key, FleetUpdateManifest manifest)
    {
        var text = FleetUpdateDocuments.Serialize(manifest);
        return new FleetSignedUpdateDocument(text, Base64Url.EncodeToString(key.SignData(FleetUpdateDocuments.GetManifestSignedBytes(text), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }
}
