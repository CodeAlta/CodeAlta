using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Desktop.Tests;

/// <summary>The page of a machine (<see cref="MachinesService.ManagementAsync"/>) against a real node, for the computer that manages it and one that does not.</summary>
[TestClass]
public sealed class MachineManagementTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly RemoteSessionHostOptions Quick = new()
    {
        MinimumBackoff = TimeSpan.FromMilliseconds(50), MaximumBackoff = TimeSpan.FromMilliseconds(200), ListInterval = TimeSpan.FromMilliseconds(200),
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FirstComputer_ManagesTheMachineFromItsPage_TheNextOneIsToldHowToBeGivenIt()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        await using var firstComputer = await PairAsync(fixture, "first", token);
        await using var secondComputer = await PairAsync(fixture, "second", token);
        var (first, firstService, firstEpoch) = firstComputer;
        var (second, secondService, secondEpoch) = secondComputer;
        var firstMachine = first.Hosts.Single().MachineId!;
        var secondMachine = second.Hosts.Single().MachineId!;

        var managed = await firstService.ManagementAsync(new MachineRequest(firstEpoch, firstMachine), token);
        Assert.AreEqual("ok", managed.Status, managed.Message);
        Assert.AreEqual("1.0.0", managed.Version);
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, managed.Devices.Select(static device => device.Name).ToArray());
        var me = managed.Devices.Single(static device => device.IsThisComputer);
        Assert.AreEqual("first", me.Name);
        CollectionAssert.Contains(me.Permissions, "manage", "A link made to manage the node gives it.");
        Assert.AreEqual("scripted", managed.Providers.Single().Key, "The page says which providers are ready there.");

        var other = await secondService.ManagementAsync(new MachineRequest(secondEpoch, secondMachine), token);
        Assert.AreEqual("not_manager", other.Status);
        Assert.AreEqual(second.DeviceId, other.ThisDevice, "The page names this computer, for the owner to give it the permission.");
        Assert.AreEqual("scripted", other.Providers.Single().Key, "Its providers' readiness is shown all the same.");
        Assert.AreEqual("not_manager", (await secondService.RevokeDeviceAsync(new MachineDeviceRequest(secondEpoch, secondMachine, first.DeviceId), token)).Status,
            "A computer that does not manage the machine changes nothing there.");

        // The manager gives the second computer the manage permission: its page then opens.
        var given = await firstService.SetDevicePermissionAsync(new MachineDevicePermissionRequest(firstEpoch, firstMachine, second.DeviceId, "manage", true), token);
        Assert.AreEqual("ok", given.Status, given.Message);
        Assert.IsTrue(fixture.Service.TrustedDevices.List().Single(device => device.DeviceId == second.DeviceId).Permissions.HasFlag(DevicePermissions.Manage));
        Assert.AreEqual("ok", (await secondService.ManagementAsync(new MachineRequest(secondEpoch, secondMachine), token)).Status);
        Assert.AreEqual("invalid_request", (await firstService.SetDevicePermissionAsync(new MachineDevicePermissionRequest(firstEpoch, firstMachine, second.DeviceId, "root", true),
            token)).Status, "Only the permissions of a node are named.");

        // A folder of the machine becomes one of its projects, and goes again.
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "work", "app")).FullName;
        Assert.AreEqual("ok", (await firstService.AddProjectAsync(new MachineProjectRequest(firstEpoch, firstMachine, folder), token)).Status);
        var project = (await firstService.ManagementAsync(new MachineRequest(firstEpoch, firstMachine), token)).Projects.Single(value => value.Path == folder);
        Assert.AreEqual("ok", (await firstService.RemoveProjectAsync(new MachineProjectRequest(firstEpoch, firstMachine, project.Id), token)).Status);
        Assert.IsFalse((await firstService.ManagementAsync(new MachineRequest(firstEpoch, firstMachine), token)).Projects.Any(value => value.Path == folder));

        // A node without a log nor a self-update says so.
        Assert.AreEqual(CodeAlta.Fleet.NodeApi.FleetNodeErrorCodes.Unavailable,
            (await firstService.ReadLogAsync(new MachineLogRequest(firstEpoch, firstMachine, 50), token)).Status);
        Assert.AreEqual("unavailable", (await firstService.UpdateMachineAsync(new MachineUpdateRequest(firstEpoch, firstMachine, true, false), token)).Status);

        // Revoking the second computer from the first one's page refuses it there at once.
        Assert.AreEqual("ok", (await firstService.RevokeDeviceAsync(new MachineDeviceRequest(firstEpoch, firstMachine, second.DeviceId), token)).Status);
        Assert.IsNull(fixture.Service.TrustedDevices.FindTrusted(DeviceFingerprintOf(fixture, second.DeviceId)));
    }

    [TestMethod]
    public async Task TheComputerThatManagesAMachine_ChangesItsSecretsFromItsPage_AndOneThatDoesNotIsRefused()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        var store = new CodeAlta.Catalog.Secrets.LocalSecretStore(fixture.Host.CatalogOptions.GlobalRoot);
        try
        {
            await using var firstComputer = await PairAsync(fixture, "first", token);
            await using var secondComputer = await PairAsync(fixture, "second", token);
            var (first, firstService, firstEpoch) = firstComputer;
            var (second, secondService, secondEpoch) = secondComputer;
            var firstMachine = first.Hosts.Single().MachineId!;
            var secondMachine = second.Hosts.Single().MachineId!;

            Assert.AreEqual("ok", (await firstService.SetSecretAsync(new MachineSecretWriteRequest(firstEpoch, firstMachine, "GRAFANA_TOKEN", "glsa_first_1"), token)).Status);
            Assert.AreEqual("ok", (await firstService.RotateSecretAsync(new MachineSecretWriteRequest(firstEpoch, firstMachine, "GRAFANA_TOKEN", "glsa_first_2"), token)).Status);
            Assert.AreEqual("ok", (await firstService.SetVariableAsync(new MachineSecretWriteRequest(firstEpoch, firstMachine, "GRAFANA_URL", "https://grafana.example"), token)).Status);
            var listed = await firstService.SecretsAsync(new MachineRequest(firstEpoch, firstMachine), token);
            Assert.AreEqual("ok", listed.Status);
            Assert.AreEqual(("GRAFANA_TOKEN", 2, "first (manage)"), (listed.Secrets.Single().Name, listed.Secrets.Single().Version, listed.Secrets.Single().SetBy),
                "the secret by name and version, set by this computer as the machine knows it");
            Assert.AreEqual("https://grafana.example", listed.Variables.Single().Value);
            Assert.IsTrue(store.TryGetSecret("GRAFANA_TOKEN", out var value));
            Assert.AreEqual("glsa_first_2", value, "the value is on the machine");

            // What the page refuses before the machine does, and what the machine refuses.
            Assert.AreEqual("invalid_params", (await firstService.SetSecretAsync(new MachineSecretWriteRequest(firstEpoch, firstMachine, "not-a-name", "x"), token)).Status);
            Assert.AreEqual("invalid_params", (await firstService.SetSecretAsync(new MachineSecretWriteRequest(firstEpoch, firstMachine, "EMPTY", ""), token)).Status);
            Assert.AreEqual(CodeAlta.Fleet.NodeApi.FleetNodeErrorCodes.SecretNotFound,
                (await firstService.RotateSecretAsync(new MachineSecretWriteRequest(firstEpoch, firstMachine, "MISSING", "x"), token)).Status);
            Assert.AreEqual("stale_epoch", (await firstService.SecretsAsync(new MachineRequest("other", firstMachine), token)).Status);

            // A computer that does not manage the machine reads and changes nothing there.
            Assert.AreEqual("not_manager", (await secondService.SecretsAsync(new MachineRequest(secondEpoch, secondMachine), token)).Status);
            Assert.AreEqual("not_manager", (await secondService.SetSecretAsync(new MachineSecretWriteRequest(secondEpoch, secondMachine, "GRAFANA_TOKEN", "stolen"), token)).Status);
            Assert.AreEqual("not_manager", (await secondService.DeleteSecretAsync(new MachineSecretNameRequest(secondEpoch, secondMachine, "GRAFANA_TOKEN"), token)).Status);
            Assert.IsTrue(store.TryGetSecret("GRAFANA_TOKEN", out value));
            Assert.AreEqual("glsa_first_2", value);

            Assert.AreEqual("ok", (await firstService.DeleteSecretAsync(new MachineSecretNameRequest(firstEpoch, firstMachine, "GRAFANA_TOKEN"), token)).Status);
            Assert.AreEqual("ok", (await firstService.DeleteVariableAsync(new MachineSecretNameRequest(firstEpoch, firstMachine, "GRAFANA_URL"), token)).Status);
            var empty = await firstService.SecretsAsync(new MachineRequest(firstEpoch, firstMachine), token);
            Assert.AreEqual((0, 0), (empty.Secrets.Length, empty.Variables.Length));
        }
        finally
        {
            store.Delete();
        }
    }

    // What altanode agents allow|deny sets on the machine, set from its page: off until a space is turned on.
    [TestMethod]
    public async Task TheComputerThatManagesAMachine_LetsAgentsOfOtherMachinesInFromItsPage_AndOneThatDoesNotIsRefused()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        await using var firstComputer = await PairAsync(fixture, "first", token);
        await using var secondComputer = await PairAsync(fixture, "second", token);
        var (first, firstService, firstEpoch) = firstComputer;
        var (second, secondService, secondEpoch) = secondComputer;
        var firstMachine = first.Hosts.Single().MachineId!;
        var secondMachine = second.Hosts.Single().MachineId!;

        var off = await firstService.AgentsAsync(new MachineRequest(firstEpoch, firstMachine), token);
        Assert.AreEqual("ok", off.Status, off.Message);
        Assert.IsTrue(off.Spaces.Length > 0 && off.Spaces.All(static space => !space.Allowed), "agents are let in no space until the owner allows one");
        Assert.AreEqual((null, false), (off.Sandbox, off.AllowBypass));
        CollectionAssert.AreEqual(new[] { "workspace", "strict" }, off.Sandboxes);

        Assert.AreEqual("ok", (await firstService.SetAgentSpaceAsync(new MachineAgentSpaceRequest(firstEpoch, firstMachine, "default", true), token)).Status);
        Assert.AreEqual("ok", (await firstService.SetAgentSandboxAsync(new MachineAgentSandboxRequest(firstEpoch, firstMachine, "strict"), token)).Status);
        Assert.AreEqual("ok", (await firstService.SetAgentBypassAsync(new MachineAgentBypassRequest(firstEpoch, firstMachine, true), token)).Status);
        var policy = fixture.Service.AgentSessions.ReadPolicy();
        CollectionAssert.AreEqual(new[] { "default" }, policy.Spaces.ToArray(), "each change keeps the others");
        Assert.AreEqual(("strict", true), (policy.Sandbox, policy.AllowBypass));
        var on = await firstService.AgentsAsync(new MachineRequest(firstEpoch, firstMachine), token);
        Assert.IsTrue(on.Spaces.Single(static space => space.Id == "default").Allowed);
        Assert.AreEqual(("strict", true), (on.Sandbox, on.AllowBypass));

        // What the machine refuses: a space or a profile it does not have; the policy stays as it was.
        var noSpace = await firstService.SetAgentSpaceAsync(new MachineAgentSpaceRequest(firstEpoch, firstMachine, "nowhere", true), token);
        Assert.AreEqual(CodeAlta.Fleet.NodeApi.FleetNodeErrorCodes.Unavailable, noSpace.Status);
        StringAssert.Contains(noSpace.Message, "nowhere");
        Assert.AreEqual(CodeAlta.Fleet.NodeApi.FleetNodeErrorCodes.Unavailable,
            (await firstService.SetAgentSandboxAsync(new MachineAgentSandboxRequest(firstEpoch, firstMachine, "missing"), token)).Status);
        Assert.AreEqual("invalid_params", (await firstService.SetAgentSpaceAsync(new MachineAgentSpaceRequest(firstEpoch, firstMachine, " ", true), token)).Status);
        Assert.AreEqual("stale_epoch", (await firstService.AgentsAsync(new MachineRequest("other", firstMachine), token)).Status);
        Assert.AreEqual(("strict", true), (fixture.Service.AgentSessions.ReadPolicy().Sandbox, fixture.Service.AgentSessions.ReadPolicy().AllowBypass));

        // A computer that does not manage the machine reads and changes nothing there.
        Assert.AreEqual("not_manager", (await secondService.AgentsAsync(new MachineRequest(secondEpoch, secondMachine), token)).Status);
        Assert.AreEqual("not_manager", (await secondService.SetAgentSpaceAsync(new MachineAgentSpaceRequest(secondEpoch, secondMachine, "default", false), token)).Status);
        Assert.AreEqual("not_manager", (await secondService.SetAgentBypassAsync(new MachineAgentBypassRequest(secondEpoch, secondMachine, false), token)).Status);
        Assert.IsTrue(fixture.Service.AgentSessions.ReadPolicy().AllowBypass);

        // Back off: the sandbox of their parent, no bypass, no space.
        Assert.AreEqual("ok", (await firstService.SetAgentSandboxAsync(new MachineAgentSandboxRequest(firstEpoch, firstMachine, null), token)).Status);
        Assert.AreEqual("ok", (await firstService.SetAgentBypassAsync(new MachineAgentBypassRequest(firstEpoch, firstMachine, false), token)).Status);
        Assert.AreEqual("ok", (await firstService.SetAgentSpaceAsync(new MachineAgentSpaceRequest(firstEpoch, firstMachine, "DEFAULT", false), token)).Status);
        policy = fixture.Service.AgentSessions.ReadPolicy();
        Assert.AreEqual((0, null, false), (policy.Spaces.Count, policy.Sandbox, policy.AllowBypass));
    }

    // What altanode labels sets on the machine, set from its page.
    [TestMethod]
    public async Task TheComputerThatManagesAMachine_SetsItsLabelsFromItsPage_AndOneThatDoesNotIsRefused()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        await using var firstComputer = await PairAsync(fixture, "first", token);
        await using var secondComputer = await PairAsync(fixture, "second", token);
        var (first, firstService, firstEpoch) = firstComputer;
        var (second, secondService, secondEpoch) = secondComputer;
        var firstMachine = first.Hosts.Single().MachineId!;
        var secondMachine = second.Hosts.Single().MachineId!;

        var none = await firstService.LabelsAsync(new MachineRequest(firstEpoch, firstMachine), token);
        Assert.AreEqual(("ok", 0), (none.Status, none.Labels.Length), none.Message);
        Assert.AreEqual("ok", (await firstService.SetLabelsAsync(new MachineLabelsRequest(firstEpoch, firstMachine, [" gpu", "prod", "GPU"]), token)).Status);
        CollectionAssert.AreEqual(new[] { "gpu", "prod" }, (await firstService.LabelsAsync(new MachineRequest(firstEpoch, firstMachine), token)).Labels);
        Assert.AreEqual("invalid_params", (await firstService.SetLabelsAsync(new MachineLabelsRequest(firstEpoch, firstMachine, ["two words"]), token)).Status);
        Assert.AreEqual("stale_epoch", (await firstService.LabelsAsync(new MachineRequest("other", firstMachine), token)).Status);

        // A computer that does not manage the machine reads and changes nothing there.
        Assert.AreEqual("not_manager", (await secondService.LabelsAsync(new MachineRequest(secondEpoch, secondMachine), token)).Status);
        Assert.AreEqual("not_manager", (await secondService.SetLabelsAsync(new MachineLabelsRequest(secondEpoch, secondMachine, []), token)).Status);
        CollectionAssert.AreEqual(new[] { "gpu", "prod" }, (await firstService.LabelsAsync(new MachineRequest(firstEpoch, firstMachine), token)).Labels);
    }

    // What altanode permission-mode sets on the machine, set from its page.
    [TestMethod]
    public async Task TheComputerThatManagesAMachine_MakesItsSessionsAskFirstFromItsPage_AndOneThatDoesNotIsRefused()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        await using var firstComputer = await PairAsync(fixture, "first", token);
        await using var secondComputer = await PairAsync(fixture, "second", token);
        var (first, firstService, firstEpoch) = firstComputer;
        var (second, secondService, secondEpoch) = secondComputer;
        var firstMachine = first.Hosts.Single().MachineId!;
        var secondMachine = second.Hosts.Single().MachineId!;

        var initial = await firstService.SessionDefaultsAsync(new MachineRequest(firstEpoch, firstMachine), token);
        Assert.AreEqual(("ok", "bypassPermissions"), (initial.Status, initial.PermissionMode), initial.Message);
        Assert.AreEqual("ok", (await firstService.SetSessionPermissionModeAsync(new MachineSessionPermissionModeRequest(firstEpoch, firstMachine, "default"), token)).Status);
        Assert.AreEqual("default", (await firstService.SessionDefaultsAsync(new MachineRequest(firstEpoch, firstMachine), token)).PermissionMode);
        Assert.AreEqual("invalid_params", (await firstService.SetSessionPermissionModeAsync(new MachineSessionPermissionModeRequest(firstEpoch, firstMachine, "auto"), token)).Status);
        Assert.AreEqual("stale_epoch", (await firstService.SessionDefaultsAsync(new MachineRequest("other", firstMachine), token)).Status);

        // A computer that does not manage the machine reads and changes nothing there.
        Assert.AreEqual("not_manager", (await secondService.SessionDefaultsAsync(new MachineRequest(secondEpoch, secondMachine), token)).Status);
        Assert.AreEqual("not_manager", (await secondService.SetSessionPermissionModeAsync(new MachineSessionPermissionModeRequest(secondEpoch, secondMachine, "bypassPermissions"), token)).Status);
        Assert.AreEqual("default", (await firstService.SessionDefaultsAsync(new MachineRequest(firstEpoch, firstMachine), token)).PermissionMode);
    }

    // The providers of a machine, from its page: the calls reach the machine, which answers for itself; this node has none to change.
    [TestMethod]
    public async Task TheProvidersOfAMachine_AreAskedOfTheMachine_AndRefusedToAComputerThatDoesNotManageIt()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token);
        await using var firstComputer = await PairAsync(fixture, "first", token);
        await using var secondComputer = await PairAsync(fixture, "second", token);
        var (first, firstService, firstEpoch) = firstComputer;
        var (second, secondService, secondEpoch) = secondComputer;
        var firstMachine = first.Hosts.Single().MachineId!;
        var secondMachine = second.Hosts.Single().MachineId!;

        var listed = await firstService.ProvidersAsync(new MachineRequest(firstEpoch, firstMachine), token);
        Assert.AreEqual("provider.unavailable", listed.Status, "a node whose host has no providers to change says so");
        Assert.AreEqual("provider.unavailable", (await firstService.SetProviderAsync(new MachineProviderRequest(firstEpoch, firstMachine, "openai", "openai-responses", null, null, null,
            "sk-1", true, false), token)).Status);
        Assert.AreEqual("invalid_request", (await firstService.SetProviderAsync(new MachineProviderRequest(firstEpoch, firstMachine, "two words", null, null, null, null, null, true, false),
            token)).Status, "checked here before it goes to the machine");
        Assert.AreEqual("invalid_request", (await firstService.ProviderSignInAsync(new MachineProviderKeyRequest(firstEpoch, firstMachine, "../x"), token)).Status);
        Assert.AreEqual("not_found", (await firstService.ProvidersAsync(new MachineRequest(firstEpoch, "nothing"), token)).Status);
        Assert.AreEqual("stale_epoch", (await firstService.ProvidersAsync(new MachineRequest("other", firstMachine), token)).Status);

        // A computer that does not manage the machine reads and changes none of its providers.
        Assert.AreEqual("not_manager", (await secondService.ProvidersAsync(new MachineRequest(secondEpoch, secondMachine), token)).Status);
        Assert.AreEqual("not_manager", (await secondService.SetProviderAsync(new MachineProviderRequest(secondEpoch, secondMachine, "openai", null, null, null, null, "stolen", true, false),
            token)).Status);
        Assert.AreEqual("not_manager", (await secondService.ProviderSignInAsync(new MachineProviderKeyRequest(secondEpoch, secondMachine, "copilot"), token)).Status);
    }

    private static DeviceFingerprint DeviceFingerprintOf(FleetNodeFixture fixture, string deviceId)
        => fixture.Service.TrustedDevices.List(includeRevoked: true).Single(device => device.DeviceId == deviceId).Fingerprint;

    // A computer of its own profile, paired with the node from a link, its machine online.
    private static async Task<PairedComputer> PairAsync(FleetNodeFixture fixture, string name, CancellationToken cancellationToken)
    {
        var registry = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
        var fleet = new DesktopFleet(Path.Combine(fixture.Root, name + "-fleet"), registry.TakeMembership(), name,
            static (machine, identity) => new RemoteSessionHost(machine, cancellation => FleetNodeClient.ConnectAsync(machine, identity, cancellation), Quick,
                localDeviceId: identity.DeviceId));
        // The first computer is paired with a link made to manage the node (altanode pair --manage); the next ones with a plain one.
        var machine = await fleet.PairAsync(fixture.CreatePairingLink(manages: name == "first"), cancellationToken);
        var host = fleet.Find(machine.DeviceId)!;
        var deadline = DateTime.UtcNow + Patience;
        while (host.State != RemoteMachineState.Online)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"{name} never saw the machine online.");
            await Task.Delay(50, cancellationToken);
        }

        var epoch = Guid.NewGuid().ToString("D");
        return new PairedComputer(fleet, new MachinesService(fleet, epoch), epoch);
    }

    private sealed record PairedComputer(DesktopFleet Fleet, MachinesService Service, string Epoch) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Fleet.DisposeAsync();
    }
}
