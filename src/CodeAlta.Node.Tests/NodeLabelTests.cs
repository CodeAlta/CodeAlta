using CodeAlta.Automations;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The labels of a machine (<c>doc/fleet/node.md</c>): set by its owner with <c>altanode labels</c> or by a device that
/// manages it, kept in its profile, published in its inventory to its servers, and read by the rules of environments.
/// </summary>
[TestClass]
public sealed class NodeLabelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AManager_SetsTheLabels_OthersAreForbidden_AndTheInventoryCarriesThem()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await ManagedNodeFixture.StartAsync(token);
        var managerMachine = await fixture.PairAsync(fixture.Owner, "laptop", manages: true, token);
        var otherMachine = await fixture.PairAsync(fixture.Other, "phone", manages: false, token);
        await using var manager = await FleetNodeClient.ConnectAsync(managerMachine, fixture.Owner, token);
        await using var other = await FleetNodeClient.ConnectAsync(otherMachine, fixture.Other, token);

        Assert.AreEqual(0, (await manager.GetLabelsAsync(token)).Labels.Count);
        CollectionAssert.AreEqual(new[] { "gpu", "prod" }, (await manager.SetLabelsAsync(["gpu", "prod", "GPU"], token)).Labels.ToArray());
        CollectionAssert.AreEqual(new[] { "gpu", "prod" }, FleetMachineLabels.Read(fixture.Node.Host.CatalogOptions.FleetRoot).ToArray(), "kept in the profile of the machine");
        Assert.AreEqual(FleetErrorCodes.InvalidParams, (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.SetLabelsAsync(["not a label"], token))).Error.Code);
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(() => other.SetLabelsAsync(["stolen"], token))).Error.Code);
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(() => other.GetLabelsAsync(token))).Error.Code);

        // The inventory the machine publishes carries them.
        await using var source = new FleetHostPublicationSource(fixture.Node.Host, "build-box", "4.0.0");
        CollectionAssert.AreEqual(new[] { "gpu", "prod" }, (await source.ReadInventoryAsync(token)).Labels!.ToArray());
    }

    [TestMethod]
    public async Task TheOwner_ListsAddsRemovesAndSetsTheLabels_WithAltanode()
    {
        using var root = new NodeTestRoot();
        async Task<(int Code, string Output, string Error)> RunAsync(params string[] words)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await new NodeApplication(output, error) { InitializeLogging = false, ServiceEnvironment = static () => null }
                .RunAsync(["labels", .. words, "--data-root", root.DataRoot]);
            return (code, output.ToString().Trim(), error.ToString());
        }

        Assert.AreEqual((NodeExitCodes.Success, "This machine has no label."), (await RunAsync()) is var empty ? (empty.Code, empty.Output) : default);
        Assert.AreEqual((NodeExitCodes.Success, $"gpu{Environment.NewLine}prod"), (await RunAsync("add", "gpu", "prod")) is var added ? (added.Code, added.Output) : default);
        Assert.AreEqual($"prod", (await RunAsync("remove", "GPU")).Output);
        Assert.AreEqual($"eu{Environment.NewLine}always-on", (await RunAsync("set", "eu", "always-on")).Output);
        var refused = await RunAsync("add", "two words");
        Assert.AreEqual(NodeExitCodes.InvalidUsage, refused.Code);
        StringAssert.Contains(refused.Error, "is not a label");
        Assert.AreEqual(NodeExitCodes.InvalidUsage, (await RunAsync("rename", "x")).Code);
        Assert.AreEqual("This machine has no label.", (await RunAsync("set")).Output, "set alone removes them all");
    }

    [TestMethod]
    public async Task TheServerSeesTheLabels_AndAnEnvironmentRunsOnlyOnALabeledMachine()
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
        await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);
        Assert.AreEqual(NodeExitCodes.Success, await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]));
        var nodeId = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending").Id;
        await desktop.ApproveAsync(nodeId, FleetDeviceKinds.Node, token);
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions { TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account },
        }, token);
        var servers = NodeServers.Start(node, null, "test-node", "1.0.0", Fast);
        try
        {
            var manager = new FleetSecretsManager(new FleetOrganizationDevices(address, desktopKey, Path.Combine(root.Root, "desktop-link", FleetOrganizationDevices.PinFileName)));
            await WaitForAsync(async () => (await desktop.ListMachinesAsync(token)).Any(static machine => machine.Online) ? "online" : null, token);
            await manager.PutEnvironmentAsync("gpu-only", [], ["gpu"], [], false, token);
            var gate = node.Automations!.Environments!;
            StringAssert.Contains((await gate.AdmitAsync("gpu-only", Guid.CreateVersion7().ToString("N"), null, token)).Problem, "machines labeled gpu");

            // The owner labels the machine: the server lists the label at the next publication, and the environment admits the run.
            FleetMachineLabels.Write(node.Host.CatalogOptions.FleetRoot, ["gpu"]);
            var listed = await WaitForAsync(async () => (await desktop.ListMachinesAsync(token)).FirstOrDefault(machine => machine.Inventory.Labels?.Contains("gpu") == true), token);
            Assert.AreEqual(nodeId, listed.DeviceId);
            Assert.IsNull((await gate.AdmitAsync("gpu-only", Guid.CreateVersion7().ToString("N"), null, token)).Problem);
        }
        finally
        {
            await servers.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TheApprovalARunAsks_NamesNoTitleTheServerCouldRead()
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
        await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "test-node", token);
        Assert.AreEqual(NodeExitCodes.Success, await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync(["join", address.ToString(), "--token", enrollment.Token, "--data-root", root.DataRoot, "--name", "test-node", "--no-wait"]));
        var nodeId = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending").Id;
        await desktop.ApproveAsync(nodeId, FleetDeviceKinds.Node, token);
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            Automations = new NodeAutomationOptions { TimeZone = TimeZoneInfo.Utc, WatchRepositories = false, CredentialKind = static _ => AutomationCredentialKind.Account },
        }, token);
        var servers = NodeServers.Start(node, null, "test-node", "1.0.0", Fast);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var manager = new FleetSecretsManager(new FleetOrganizationDevices(address, desktopKey, Path.Combine(root.Root, "desktop-link", FleetOrganizationDevices.PinFileName)));
            await WaitForAsync(async () => (await desktop.ListMachinesAsync(token)).Any(static machine => machine.Online) ? "online" : null, token);
            var person = (await manager.Devices.RefreshAsync(token)).Members[desktopKey.DeviceId].OwnerId!;
            await manager.PutEnvironmentAsync("production", [person], [], [], false, token);

            // The run waits for its approval; what the server holds of it names the run, never what it is about.
            var admission = node.Automations!.Environments!.AdmitAsync("production", "run-private", null, waiting.Token);
            var asked = await WaitForAsync(async () => (await manager.ListApprovalsAsync(token)).Approvals.FirstOrDefault(static approval => approval.RunId == "run-private"), token);
            Assert.IsNull(asked.Title, "the server never reads the title of a run");
            await waiting.CancelAsync();
            try { await admission; }
            catch (OperationCanceledException) { }
        }
        finally
        {
            await servers.DisposeAsync();
        }
    }

    private static FleetConnectorOptions Fast(FleetConnectorOptions options) => new()
    {
        ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
        PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory, PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
        MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(300),
        DeviceListPinPath = options.DeviceListPinPath, Devices = options.Devices, MetadataKeys = options.MetadataKeys, AcceptRelay = options.AcceptRelay,
        AutomationsAssigned = options.AutomationsAssigned, AutomationsOnline = options.AutomationsOnline, AutomationsRun = options.AutomationsRun,
        SecretsChanged = options.SecretsChanged,
    };

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
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