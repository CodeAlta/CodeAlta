using CodeAlta.Automations;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The budgets a node enforces before a run starts (<c>doc/fleet/observability.md</c>, budgets and alerts): an automation run of a
/// node whose enforced budget has nothing left waits, creates no session, and starts once the owner's desktop signed Continue
/// anyway; a budget that stops refuses the run.
/// </summary>
[TestClass]
public sealed class NodeBudgetTests
{
    private const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1f17";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ARunOverItsBudget_WaitsOnTheNode_UntilTheOwnerContinuesItAnyway()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop-fleet"));
        using var otherKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "other-fleet"));
        using var desktop = new FleetServerClient(address, desktopKey);
        var setup = await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);
        Assert.AreEqual(NodeExitCodes.Success, await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]));
        var nodeId = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending").Id;
        await desktop.ApproveAsync(nodeId, FleetDeviceKinds.Node, token);
        // Another machine of the person, which holds the whole budget.
        using (var other = new FleetServerClient(address, otherKey))
        {
            await other.EnrollAsync((await desktop.CreateEnrollmentTokenAsync(null, "other", token)).Token, "Build box", FleetDeviceKinds.Node, token);
            await desktop.ApproveAsync(otherKey.DeviceId, FleetDeviceKinds.Node, token);
        }

        var provider = new ScriptedNodeProvider();
        var nodeOptions = root.Options() with
        {
            ConfigureModelProviders = provider.Register,
            Automations = new NodeAutomationOptions { TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account },
        };
        var node = await NodeHost.StartAsync(nodeOptions, token);
        var automations = node.Automations!;
        var servers = NodeServers.Start(node, null, "test-node", "1.0.0", Fast);
        try
        {
            await WaitForAsync(async () => (await desktop.ListMachinesAsync(token)).Any(static machine => machine.Online) ? "online" : null, token);
            var manager = new FleetBudgetsManager(new FleetOrganizationDevices(address, desktopKey, Path.Combine(root.Root, "desktop-link", FleetOrganizationDevices.PinFileName)));
            var budget = await manager.PutAsync(null, new FleetBudgetSettings("Home tokens", FleetBudgetScopeKinds.Person, null, FleetBudgetMeasures.Tokens, 100,
                FleetBudgetPeriods.Month, "UTC", FleetBudgetModes.Enforced, FleetBudgetReachedActions.Hold), token);
            using (var other = new FleetServerClient(address, otherKey))
                Assert.IsTrue((await other.AskAllowanceAsync(budget.Id, new FleetAllowanceRequest(1, 0, 100), token)).Exhausted is false);

            var list = await desktop.ReadVerifiedDeviceListAsync(null, token);
            await desktop.PutAutomationAsync(Sign(desktopKey, list, setup.Organization.Id, nodeId, Id), token);
            await WaitForAsync(() => Task.FromResult(automations.AssignmentOf(Id) is { Number: 1 } ? "held" : null), token);
            var runId = Guid.CreateVersion7().ToString("N");
            await desktop.RunAutomationAsync(FleetAutomationSigning.SignRun(desktopKey, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion,
                setup.Organization.Id, Id, nodeId, runId, string.Empty, string.Empty, DateTimeOffset.UtcNow)), token);

            // The node holds the run: the budget has nothing left for it.
            var hold = await WaitForAsync(async () => (await manager.ListHoldsAsync(token)).Holds.FirstOrDefault(), token);
            Assert.AreEqual((FleetBudgetHoldStates.Waiting, nodeId, (string?)null), (hold.Status, hold.MachineId, hold.Title), "the server never reads what the run is about");
            Assert.IsNull((await desktop.ListAutomationRunsAsync(Id, null, token)).Runs.FirstOrDefault(run => run.RunId == runId && run.SessionId is not null),
                "no session starts while the run waits");
            Assert.AreEqual(string.Empty, provider.LastUserText, "no model is called");

            // The owner continues it anyway: the node verifies the signature and the run starts.
            await manager.ContinueAsync(hold.Id, token);
            var completed = await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs
                .FirstOrDefault(run => run.RunId == runId && run.Status == AutomationRun.Completed && run.SessionId is not null), token);
            Assert.IsNotNull(completed.SessionId);

            // A budget that stops: the next run is refused at once, without a hold.
            await manager.PutAsync(budget.Id, new FleetBudgetSettings("Home tokens", FleetBudgetScopeKinds.Person, null, FleetBudgetMeasures.Tokens, 100,
                FleetBudgetPeriods.Month, "UTC", FleetBudgetModes.Enforced, FleetBudgetReachedActions.Stop), token);
            var stoppedRun = Guid.CreateVersion7().ToString("N");
            await desktop.RunAutomationAsync(FleetAutomationSigning.SignRun(desktopKey, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion,
                setup.Organization.Id, Id, nodeId, stoppedRun, string.Empty, string.Empty, DateTimeOffset.UtcNow)), token);
            var refused = await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs
                .FirstOrDefault(run => run.RunId == stoppedRun && run.Message is not null), token);
            StringAssert.Contains(refused.Message, "'Home tokens' is reached");
            Assert.IsNull(refused.SessionId);
            Assert.AreEqual(1, (await manager.ListHoldsAsync(token)).Holds.Count, "a budget that stops holds nothing");
            Assert.IsTrue(File.Exists(Path.Combine(servers.Connections.Store.LinkFolder(servers.Connections.Connections.Single().Link.Id), NodeBudgets.FileName)),
                "the node keeps its budgets, for its runs offline");
        }
        finally
        {
            await servers.DisposeAsync();
            await node.DisposeAsync();
        }
    }

    // A version signed by a controller for a machine.
    private static FleetSignedAutomationVersion Sign(DeviceIdentity signer, FleetDeviceListState list, string organization, string machine, string id)
    {
        var definition = $$"""
            [automations.{{id}}]
            name = "Report"
            prompt = "Write the report."
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
        SecretsChanged = options.SecretsChanged, BudgetsChanged = options.BudgetsChanged,
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
