using CodeAlta.Automations;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The values a node takes from its server (<c>doc/fleet/secrets.md</c>): sealed by the person's desktop, taken by the node
/// as soon as the server says they changed, given to the processes of a run by its scope; a run in an environment that
/// requires an approval waits for the signature of an approver's device; each use is published, never the value.
/// </summary>
[TestClass]
public sealed class NodeFleetSecretTests
{
    private const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1f07";
    private const string OtherId = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1f08";
    private const string DeployToken = "deploy-token-0123456789";
    private static readonly TimeSpan Patience = TestTime.Seconds(45);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ARunInAnEnvironment_WaitsForItsApproval_ThenItsProcessesGetItsValues_AndTheUseIsPublished()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
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
        var nodeOptions = root.Options() with
        {
            ConfigureModelProviders = provider.Register,
            Automations = new NodeAutomationOptions { TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account },
        };
        var node = await NodeHost.StartAsync(nodeOptions, token);
        // The space gives its sessions a CLI profile that reads the secret by its name: the run's processes get it from the fleet.
        await node.Host.SpaceCatalog.UpdateSettingsAsync(SpaceDescriptor.DefaultId, settings => settings with
        {
            Identity = new SpaceIdentity(Cli: [new SpaceCliProfile("deploy", [new("DEPLOY_TOKEN", "${secret:DEPLOY_TOKEN}"), new("GRAFANA_TOKEN", "${secret:GRAFANA_TOKEN}")])]),
        }, token);
        var automations = node.Automations!;
        var servers = NodeServers.Start(node, null, "test-node", "1.0.0", Fast);
        try
        {
            await WaitForAsync(async () => (await desktop.ListMachinesAsync(token)).Any(static machine => machine.Online) ? "online" : null, token);
            var manager = new FleetSecretsManager(new FleetOrganizationDevices(address, desktopKey, Path.Combine(root.Root, "desktop-link", FleetOrganizationDevices.PinFileName)));
            var production = await manager.PutEnvironmentAsync("production", [setup.Owner.Id], [], [Id], false, token);
            var deploy = await manager.SetAsync(FleetSecretKinds.Secret, "DEPLOY_TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Environment, production.Id), DeployToken, false,
                [nodeId], token);
            await manager.SetAsync(FleetSecretKinds.Secret, "GRAFANA_TOKEN", new FleetSecretScopeDto(FleetSecretScopeKinds.Person), "grafana-0123456789", false, [nodeId], token);

            // The server says the values changed: the node takes them, protected, and masks them at once.
            var store = new LocalSecretStore(node.Host.CatalogOptions.GlobalRoot);
            await WaitForAsync(() => Task.FromResult(store.ListFleetCopies().Count == 2 ? "taken" : null), token);
            Assert.IsTrue(store.GetSecretValues().Contains(DeployToken));
            Assert.IsFalse(File.ReadAllText(Path.Combine(node.Host.CatalogOptions.GlobalRoot, "secrets", "store.json")).Contains(DeployToken, StringComparison.Ordinal));

            // The automation names its environment; run now, it waits for the approval of the owner, an approver.
            var list = await desktop.ReadVerifiedDeviceListAsync(null, token);
            await desktop.PutAutomationAsync(Sign(desktopKey, list, setup.Organization.Id, nodeId, Id, "production"), token);
            await desktop.PutAutomationAsync(Sign(desktopKey, list, setup.Organization.Id, nodeId, OtherId, "production"), token);
            await WaitForAsync(() => Task.FromResult(automations.AssignmentOf(Id) is { Number: 1 } && automations.AssignmentOf(OtherId) is { Number: 1 } ? "held" : null), token);
            var runId = Guid.CreateVersion7().ToString("N");
            await desktop.RunAutomationAsync(FleetAutomationSigning.SignRun(desktopKey, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion,
                setup.Organization.Id, Id, nodeId, runId, string.Empty, string.Empty, DateTimeOffset.UtcNow)), token);
            var waiting = await WaitForAsync(async () => (await manager.ListApprovalsAsync(token)).Approvals.FirstOrDefault(approval => approval.RunId == runId), token);
            Assert.AreEqual((FleetApprovalStates.Pending, "production", nodeId), (waiting.Status, waiting.EnvironmentName, waiting.MachineId));
            Assert.IsNull(waiting.Title, "the server never reads what the run is about");
            Assert.IsNull((await desktop.ListAutomationRunsAsync(Id, null, token)).Runs.FirstOrDefault(run => run.RunId == runId && run.SessionId is not null),
                "no session starts before the approval");

            await manager.ApproveAsync(waiting.Id, FleetSecretsManager.ReviewOf(waiting), token);
            var completed = await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs
                .FirstOrDefault(run => run.RunId == runId && run.Status == AutomationRun.Completed && run.SessionId is not null), token);
            var sessionId = completed.SessionId!;
            Assert.AreEqual("production", node.Host.Identities.Environments.Get(sessionId));
            var environment = (await node.Host.RuntimeService.GetSessionIdentityAsync(sessionId, token)).Environment;
            Assert.AreEqual(DeployToken, environment.Get("DEPLOY_TOKEN"), "the run's processes get the value of its environment");
            Assert.AreEqual("grafana-0123456789", environment.Get("GRAFANA_TOKEN"));

            // Each use is published to the server, never the value.
            var uses = await WaitForAsync(async () => (await manager.ListUsesAsync(deploy.Id, token)).Uses is { Count: > 0 } found ? found : null, token);
            Assert.AreEqual((sessionId, 1, nodeId), (uses[0].RunId, uses[0].Version, uses[0].MachineId));

            // The node restarts: the session keeps its admission, bound to the environment, its version and the approval
            // taken, which is neither asked nor taken again.
            await servers.DisposeAsync();
            await node.DisposeAsync();
            node = await NodeHost.StartAsync(nodeOptions, token);
            servers = NodeServers.Start(node, null, "test-node", "1.0.0", Fast);
            var restarted = servers;
            await WaitForAsync(() => Task.FromResult(restarted.Secrets.Environments().ContainsKey("production") ? "synced" : null), token);
            Assert.AreEqual(waiting.Id, node.Host.Identities.Environments.Find(sessionId)?.Admission.ApprovalId, "the approval that admitted it is kept with it");
            environment = (await node.Host.RuntimeService.GetSessionIdentityAsync(sessionId, token)).Environment;
            Assert.AreEqual(DeployToken, environment.Get("DEPLOY_TOKEN"), "a restart does not drop the values of its environment");
            Assert.AreEqual(1, (await manager.ListApprovalsAsync(token)).Approvals.Count(approval => approval.RunId == runId), "no approval is asked again");

            // New rules of the environment: the admission was to the version before, and gives nothing more until the run is admitted again.
            await manager.PutEnvironmentAsync("production", [setup.Owner.Id], [], [Id], false, token);
            await WaitForAsync(() => Task.FromResult(restarted.Secrets.Environments().TryGetValue("production", out var found) && found.Definition.Number == 2 ? "v2" : null), token);
            Assert.IsNull(node.Host.Identities.Environments.Get(sessionId));
            Assert.IsNull((await node.Host.RuntimeService.GetSessionIdentityAsync(sessionId, token)).Environment.Get("DEPLOY_TOKEN"));

            // An automation the environment does not name is refused, without asking any approval.
            var otherRun = Guid.CreateVersion7().ToString("N");
            await desktop.RunAutomationAsync(FleetAutomationSigning.SignRun(desktopKey, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion,
                setup.Organization.Id, OtherId, nodeId, otherRun, string.Empty, string.Empty, DateTimeOffset.UtcNow)), token);
            var refused = await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(OtherId, null, token)).Runs
                .FirstOrDefault(run => run.RunId == otherRun && run.Message is not null), token);
            StringAssert.Contains(refused.Message, "open to some automations only");
            Assert.IsNull(refused.SessionId);
            Assert.IsFalse((await manager.ListApprovalsAsync(token)).Approvals.Any(approval => approval.RunId == otherRun));

            // A machine the person revokes takes the values of that server away.
            await desktop.RevokeAsync(nodeId, token);
            await WaitForAsync(() => Task.FromResult(store.ListFleetCopies().Count == 0 ? "gone" : null), token);
            Assert.IsTrue(store.GetSecretValues().Contains(DeployToken), "taken away, the value is never given again but stays masked: a run may still hold it");
        }
        finally
        {
            await servers.DisposeAsync();
            await node.DisposeAsync();
        }
    }

    // A version signed by a controller for a machine, its definition naming an environment.
    private static FleetSignedAutomationVersion Sign(DeviceIdentity signer, FleetDeviceListState list, string organization, string machine, string id, string environment)
    {
        var definition = $$"""
            [automations.{{id}}]
            name = "Deploy"
            prompt = "Deploy it."
            environment = "{{environment}}"
            """;
        var sealedText = FleetAutomationSigning.Seal(new FleetAutomationContent(definition, null), id, 1, machine, list, signer);
        return FleetAutomationSigning.Sign(signer, new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, organization, id, 1, machine, false, true, [],
            null, null, sealedText, string.Empty, string.Empty, DateTimeOffset.UtcNow));
    }

    // A connector that tries again within a second, and keeps the callbacks of the node.
    private static FleetConnectorOptions Fast(FleetConnectorOptions options) => new()
    {
        ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
        PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory, PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
        MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(500),
        DeviceListPinPath = options.DeviceListPinPath, Devices = options.Devices, MetadataKeys = options.MetadataKeys, AcceptRelay = options.AcceptRelay,
        AutomationsAssigned = options.AutomationsAssigned, AutomationsOnline = options.AutomationsOnline, AutomationsRun = options.AutomationsRun,
        SecretsChanged = options.SecretsChanged,
    };

    private async Task<T> WaitForAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("It did not get there in time.");
            await Task.Delay(100, cancellationToken);
        }
    }
}
