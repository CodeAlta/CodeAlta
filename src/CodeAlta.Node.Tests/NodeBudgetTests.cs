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

    [TestMethod]
    public async Task ARunThatWaitedForItsBudget_DoesNotStart_WhenTheOwnerStoppedTheFleetMeanwhile()
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
        using (var other = new FleetServerClient(address, otherKey))
        {
            await other.EnrollAsync((await desktop.CreateEnrollmentTokenAsync(null, "other", token)).Token, "Build box", FleetDeviceKinds.Node, token);
            await desktop.ApproveAsync(otherKey.DeviceId, FleetDeviceKinds.Node, token);
        }

        var provider = new ScriptedNodeProvider();
        var node = await NodeHost.StartAsync(root.Options() with
        {
            ConfigureModelProviders = provider.Register,
            Automations = new NodeAutomationOptions { TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account },
        }, token);
        var automations = node.Automations!;
        var servers = NodeServers.Start(node, null, "test-node", "1.0.0", Fast);
        try
        {
            await WaitForAsync(async () => (await desktop.ListMachinesAsync(token)).Any(static machine => machine.Online) ? "online" : null, token);
            var desktopDevices = new FleetOrganizationDevices(address, desktopKey, Path.Combine(root.Root, "desktop-link", FleetOrganizationDevices.PinFileName));
            var manager = new FleetBudgetsManager(desktopDevices);
            var budget = await manager.PutAsync(null, new FleetBudgetSettings("Home tokens", FleetBudgetScopeKinds.Person, null, FleetBudgetMeasures.Tokens, 100,
                FleetBudgetPeriods.Month, "UTC", FleetBudgetModes.Enforced, FleetBudgetReachedActions.Hold), token);
            using (var other = new FleetServerClient(address, otherKey)) await other.AskAllowanceAsync(budget.Id, new FleetAllowanceRequest(1, 0, 100), token);
            var list = await desktop.ReadVerifiedDeviceListAsync(null, token);
            await desktop.PutAutomationAsync(Sign(desktopKey, list, setup.Organization.Id, nodeId, Id), token);
            await WaitForAsync(() => Task.FromResult(automations.AssignmentOf(Id) is { Number: 1 } ? "held" : null), token);
            var runId = Guid.CreateVersion7().ToString("N");
            await desktop.RunAutomationAsync(FleetAutomationSigning.SignRun(desktopKey, new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion,
                setup.Organization.Id, Id, nodeId, runId, string.Empty, string.Empty, DateTimeOffset.UtcNow)), token);
            var hold = await WaitForAsync(async () => (await manager.ListHoldsAsync(token)).Holds.FirstOrDefault(), token);

            // The owner stops the fleet while the run waits, then continues it anyway: the stop wins.
            var policy = new FleetOrganizationPolicy(new FleetOrganizationAccess(desktopDevices));
            await policy.SignAsync(await policy.PrepareAsync(FleetPolicyRules.None, new FleetPolicyStop(FleetPolicyStopScopes.Fleet, [], [], "Incident"), null, token), token);
            await WaitForAsync(() => Task.FromResult(automations.Policy?.Halt is not null ? "stopped" : null), token);
            await manager.ContinueAsync(hold.Id, token);
            var refused = await WaitForAsync(async () => (await desktop.ListAutomationRunsAsync(Id, null, token)).Runs
                .FirstOrDefault(run => run.RunId == runId && run.Message is not null), token);
            StringAssert.Contains(refused.Message, "Incident");
            Assert.IsNull(refused.SessionId, "no session starts");
            Assert.AreEqual(string.Empty, provider.LastUserText, "no model is called");
        }
        finally
        {
            await servers.DisposeAsync();
            await node.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ASendHeldWhenTheNodeStopped_IsNotSent_OnAStartThatCannotVerifyTheBudgetsItKept()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop-fleet"));
        using var otherKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "other-fleet"));
        using var desktop = new FleetServerClient(address, desktopKey);
        await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);
        Assert.AreEqual(NodeExitCodes.Success, await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]));
        var nodeId = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending").Id;
        await desktop.ApproveAsync(nodeId, FleetDeviceKinds.Node, token);
        using (var other = new FleetServerClient(address, otherKey))
        {
            await other.EnrollAsync((await desktop.CreateEnrollmentTokenAsync(null, "other", token)).Token, "Build box", FleetDeviceKinds.Node, token);
            await desktop.ApproveAsync(otherKey.DeviceId, FleetDeviceKinds.Node, token);
        }

        var provider = new ScriptedNodeProvider();
        var options = root.Options() with { ConfigureModelProviders = provider.Register };
        string sessionId;
        string globalRoot, fleetRoot;
        // The first start: a budget that stops new runs is reached (another machine holds all of it); the node keeps it.
        var node = await NodeHost.StartAsync(options, token);
        var fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "test-node", "1.0.0");
        var servers = NodeServers.Start(node, fleet.Service, "test-node", "1.0.0", Fast);
        try
        {
            await WaitForAsync(async () => (await desktop.ListMachinesAsync(token)).Any(static machine => machine.Online) ? "online" : null, token);
            var manager = new FleetBudgetsManager(new FleetOrganizationDevices(address, desktopKey, Path.Combine(root.Root, "desktop-link", FleetOrganizationDevices.PinFileName)));
            var budget = await manager.PutAsync(null, new FleetBudgetSettings("Home tokens", FleetBudgetScopeKinds.Person, null, FleetBudgetMeasures.Tokens, 100,
                FleetBudgetPeriods.Month, "UTC", FleetBudgetModes.Enforced, FleetBudgetReachedActions.Stop), token);
            using (var other = new FleetServerClient(address, otherKey)) await other.AskAllowanceAsync(budget.Id, new FleetAllowanceRequest(1, 0, 100), token);
            var session = await node.Host.Commands.CreateDraftSessionAsync(null, provider.Descriptor, "Report");
            sessionId = session.SessionId;
            (globalRoot, fleetRoot) = (node.Host.CatalogOptions.GlobalRoot, node.Host.CatalogOptions.FleetRoot);
            var first = await WaitForAsync(async () => await node.Budgets!.AdmitAsync(new CodeAlta.Fleet.NodeApi.FleetNodeBudgetRun("run-first", null, session.SpaceId, null), null, token)
                is { Problem: not null } refused ? refused : null, token);
            StringAssert.Contains(first.Problem, "'Home tokens' is reached");
        }
        finally
        {
            await servers.DisposeAsync();
            await fleet.DisposeAsync();
            await node.DisposeAsync();
        }

        // A send the budget held when the node stopped, kept in its fleet folder; the server is away at the next start.
        new CodeAlta.Fleet.NodeApi.FleetHeldSends(Path.Combine(fleetRoot, "held-sends.json"), new CodeAlta.Catalog.Secrets.LocalSecretStore(globalRoot))
            .Add(new CodeAlta.Fleet.NodeApi.FleetHeldSend("fleet:held:send-1", sessionId, "The held work.", null, "nonce-1", null, null, null, null, DateTimeOffset.UtcNow));
        await server.DisposeAsync();

        node = await NodeHost.StartAsync(options, token);
        fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "test-node", "1.0.0");
        servers = NodeServers.Start(node, fleet.Service, "test-node", "1.0.0", Fast);
        try
        {
            // It cannot verify the budget it kept (its device list is not read yet): the send stays held, never sent as if no budget held it.
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            Assert.AreEqual(string.Empty, provider.LastUserText, "no model is called");
            Assert.AreEqual(1, new CodeAlta.Fleet.NodeApi.FleetHeldSends(Path.Combine(fleetRoot, "held-sends.json"),
                new CodeAlta.Catalog.Secrets.LocalSecretStore(globalRoot)).List().Count, "the send stays held");
        }
        finally
        {
            await servers.DisposeAsync();
            await fleet.DisposeAsync();
            await node.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ANodeWhoseServerLinksDoNotRead_SendsNoHeldPrompt_AndAdmitsNoRun()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var provider = new ScriptedNodeProvider();
        var options = root.Options() with { ConfigureModelProviders = provider.Register };
        var node = await NodeHost.StartAsync(options, token);
        NodeFleet? fleet = null;
        NodeServers? servers = null;
        try
        {
            var session = await node.Host.Commands.CreateDraftSessionAsync(null, provider.Descriptor, "Report");
            var fleetRoot = node.Host.CatalogOptions.FleetRoot;
            new CodeAlta.Fleet.NodeApi.FleetHeldSends(Path.Combine(fleetRoot, "held-sends.json"), new CodeAlta.Catalog.Secrets.LocalSecretStore(node.Host.CatalogOptions.GlobalRoot))
                .Add(new CodeAlta.Fleet.NodeApi.FleetHeldSend("fleet:held:send-1", session.SessionId, "The held work.", null, "nonce-1", null, null, null, null, DateTimeOffset.UtcNow));
            // The file of the server links is damaged: the budgets and the stops of its servers are not known.
            var links = new FleetServerLinkStore(fleetRoot).FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(links)!);
            File.WriteAllText(links, "{ damaged");

            fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "test-node", "1.0.0");
            servers = NodeServers.Start(node, fleet.Service, "test-node", "1.0.0", Fast);
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            Assert.AreEqual(string.Empty, provider.LastUserText, "the held prompt is not sent as if no server held it");
            var admission = await node.Budgets!.AdmitAsync(new CodeAlta.Fleet.NodeApi.FleetNodeBudgetRun("run-new", null, session.SpaceId, null), null, token);
            Assert.IsNotNull(admission.Problem, "no run starts while the links of the node do not read");
        }
        finally
        {
            if (servers is not null) await servers.DisposeAsync();
            if (fleet is not null) await fleet.DisposeAsync();
            await node.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TheSpacesLinkedToAServer_AreKnownOnceTheLinksAreLoaded_AlsoWhileTheServerIsAway()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = Path.Combine(root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { }, EnvironmentVariable = static _ => null,
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        using var desktopKey = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "desktop-fleet"));
        using (var desktop = new FleetServerClient(address, desktopKey))
        {
            await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
            var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);
            // Joined, the default space of the node publishes to the server.
            Assert.AreEqual(NodeExitCodes.Success, await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
                .RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]));
        }

        // The server is away when the node starts: its connector does not connect, nor publish.
        await server.DisposeAsync();
        var node = await NodeHost.StartAsync(root.Options(), token);
        var servers = NodeServers.Start(node, null, "test-node", "1.0.0", Fast);
        try
        {
            await servers.Connections.Loaded.WaitAsync(Patience, token);
            // What the policies and budgets of spaces read is known as soon as the links are: the default space is linked to that server.
            Assert.IsTrue(servers.Connections.Connections.Single().Connector!.LinksSpace(null), "the spaces linked to the server are known before it answers");
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
