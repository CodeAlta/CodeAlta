using System.Collections.Frozen;
using System.Diagnostics;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Server;
using CodeAlta.Hosting;
using CodeAlta.Node;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Fleet statistics end to end (<c>doc/fleet/statistics.md</c>): a CodeAlta Node of this process with a session counts its
/// statistics and publishes them to a CodeAlta Server of this process; the server sums them; the <c>fleetServer</c> service of
/// the window returns the "My fleet" view, with the node named and its project named by this computer, which has the same
/// repository and computes the same blind index with the person's key. No model is called: the session is a journal.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FleetStatisticsEndToEndTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);
    private static readonly ModelProviderId Provider = new("fleet-fixture");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ANodePublishes_TheServerSums_AndTheDesktopShowsMyFleet()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-statistics-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        // The node logs as the executable does; the log of this test is its own, shut down at its end.
        var ownsLog = !XenoAtom.Logging.LogManager.IsInitialized;
        if (ownsLog) XenoAtom.Logging.LogManager.InitializeForAsync(new XenoAtom.Logging.LogManagerConfig());
        try
        {
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { },
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");

            // One repository, checked out on the node and on this computer: one project of the person, on two machines.
            var nodeCheckout = Repository(At("node-checkout"));
            var desktopCheckout = Repository(At("desktop-checkout"));

            // The desktop: the owner's controller device, which made the server and approves the node.
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = At("global"), CurrentProjectPath = desktopCheckout, DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            var desktopProject = await host.ProjectCatalog.UpsertFromPathAsync(desktopCheckout, token);
            await using var servers = new DesktopFleetServers(host, "Desktop", "4.0.0", Fast);
            var epoch = Guid.NewGuid().ToString("D");
            var service = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog, IsPaired = static _ => false };
            Assert.AreEqual("no_server", (await service.StatisticsAsync(Request(epoch), token)).Status);
            var created = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;
            var enrollment = await service.CreateTokenAsync(new FleetServerTokenRequest(epoch, linkId, 2, "test-node"), token);

            // The node, with a session of its project in its journals: one turn, a tool call, 1200 tokens in and 300 out.
            var dataRoot = At("node");
            var nodeOptions = NodeOptions(root, dataRoot, nodeCheckout);
            await using (var writer = await NodeHost.StartAsync(nodeOptions, token))
            {
                var project = await writer.Host.ProjectCatalog.UpsertFromPathAsync(nodeCheckout, token);
                await AddProjectSessionAsync(writer.Host, "node-session", project, token);
            }

            var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
            var node = new NodeApplication(TextWriter.Null, TextWriter.Null)
            {
                InitializeLogging = false,
                ConfigureHost = _ => nodeOptions with { StartPlugins = true },
                Started = (_, stop) => started.TrySetResult(stop),
                ConfigureConnector = Fast,
                ServiceEnvironment = static () => null,
            };
            var running = node.RunAsync(["run", "--data-root", dataRoot, "--no-listen", "--name", "test-node"]);
            var stop = await started.Task.WaitAsync(Patience, token);
            try
            {
                var joined = await new NodeApplication(TextWriter.Null, TextWriter.Null) { InitializeLogging = false, ServiceEnvironment = static () => null }
                    .RunAsync(["join", address.ToString(), "--token", enrollment.Token!, "--data-root", dataRoot, "--name", "test-node", "--no-wait"]);
                Assert.AreEqual(NodeExitCodes.Success, joined);
                var pending = (await service.DevicesAsync(new FleetServerLinkRequest(epoch, linkId), token)).Devices.Single(static device => device.Status == "pending");
                Assert.AreEqual("ok", (await service.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, pending.Id), token)).Status);

                // The node counts its session, publishes its set once it holds the person's key, and the server sums it.
                var deadline = DateTime.UtcNow + Patience;
                FleetStatisticsResult result;
                while (true)
                {
                    result = await service.StatisticsAsync(Request(epoch, "machine", "project"), token);
                    if (result.View?.Groups.Any(static group => group.ProjectName is not null) == true) break;
                    if (DateTime.UtcNow > deadline) Assert.Fail($"The desktop did not get the statistics of the node: {result.Status} {result.Message} {result.View?.Totals.InputTokens}.");
                    await Task.Delay(250, token);
                }

                var view = result.View!;
                Assert.AreEqual(("ok", linkId), (result.Status, view.LinkId));
                Assert.AreEqual((1200m, 300m, 1m, 1m), (view.Totals.InputTokens, view.Totals.OutputTokens, view.Totals.Runs, view.Totals.ToolCalls));
                var group = view.Groups.Single(static group => group.Measures.InputTokens > 0);
                Assert.AreEqual(("test-node", desktopProject.DisplayName), (group.MachineName, group.ProjectName),
                    "the node is named by the server, its project by this computer, which has the same repository");
                Assert.AreEqual(22, group.Project!.Length, "the server holds a blind index, never the project");
                var machine = view.Machines.Single(static machine => machine.Name == "test-node");
                Assert.AreEqual((1, false), (machine.Sessions, machine.IsThisComputer));
                Assert.IsNotNull(machine.LastPublishedAt);
                Assert.IsTrue(view.Machines.Single(static machine => machine.Name == "Desktop").IsThisComputer);

                // By model and by day, from the same facts.
                var byModel = (await service.StatisticsAsync(Request(epoch, "provider", "model"), token)).View!;
                Assert.IsTrue(byModel.Groups.Any(static group => group.Provider == Provider.Value && group.Measures.InputTokens == 1200m), string.Join(", ", byModel.Groups.Select(static group => $"{group.Provider}/{group.Model}")));
                var byDay = (await service.StatisticsAsync(Request(epoch, "day"), token)).View!;
                Assert.AreEqual(1200m, byDay.Groups.Sum(static group => group.Measures.InputTokens));
                Assert.AreEqual("invalid_request", (await service.StatisticsAsync(Request(epoch, "session"), token)).Status);
            }
            finally
            {
                stop();
                Assert.AreEqual(NodeExitCodes.Success, await running.WaitAsync(Patience, token));
            }
        }
        finally
        {
            if (ownsLog) XenoAtom.Logging.LogManager.Shutdown();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static FleetStatisticsRequest Request(string epoch, params string[] groupBy)
        => new(epoch, null, DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"), DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd"), null, groupBy);

    // Connectors that poll often, with what the test needs of each: the statistics, the keys and the device list of the link.
    private static FleetConnectorOptions Fast(FleetConnectorOptions options) => new()
    {
        ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
        Statistics = options.Statistics, StatisticsFromQuarter = options.StatisticsFromQuarter, PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory,
        MetadataKeys = options.MetadataKeys, Devices = options.Devices, DeviceListPinPath = options.DeviceListPinPath, KeyRefreshInterval = TimeSpan.FromSeconds(1),
        MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1), PublishDelay = TimeSpan.FromMilliseconds(20),
        PollInterval = TimeSpan.FromMilliseconds(250), PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
    };

    // A node on a profile of its own, off the network and without providers, as the tests of the node isolate it.
    private static NodeHostOptions NodeOptions(string root, string dataRoot, string project) => new()
    {
        Profile = CodeAltaInstanceProfile.Create(dataRoot, false),
        CurrentProjectPath = project,
        StartPlugins = false,
        Offline = true,
        ConfigureModelProviders = static _ => { },
        DiscoveryScope = new SessionDiscoveryScope(Directory.CreateDirectory(Path.Combine(root, "node-home")).FullName, root),
        BuiltInSkillRoot = Directory.CreateDirectory(Path.Combine(root, "node-builtin")).FullName,
        DrainLimit = TimeSpan.FromSeconds(5),
        CloneRoot = Path.Combine(root, "node-code"),
    };

    private static async Task AddProjectSessionAsync(CodeAltaHost host, string id, ProjectDescriptor project, CancellationToken token)
    {
        var at = DateTimeOffset.UtcNow;
        var descriptor = new SessionViewDescriptor
        {
            SessionId = id, Kind = SessionViewKind.ProjectSession, ProjectRef = project.Id, ProviderId = Provider.Value, ProviderKey = Provider.Value,
            WorkingDirectory = project.ProjectPath, Title = "Fix the parser", CreatedAt = at, UpdatedAt = at, SpaceId = SpaceDescriptor.DefaultId,
        };
        await host.SessionViewCatalog.JournalStore.EnsureHeaderAsync(descriptor, token);
        var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
        await store.UpsertSessionAsync(new AgentSessionSummary
        {
            SessionId = id, ProviderId = Provider, ProtocolFamily = Provider.Value, ProviderKey = Provider.Value, WorkingDirectory = project.ProjectPath, Title = "Fix the parser",
            CreatedAt = at, UpdatedAt = at,
        }, token);
        var run = new AgentRunId("run-1");
        await store.AppendEventsAsync(Provider.Value, Provider.Value, id, [
            new AgentContentCompletedEvent(Provider, id, at, run, AgentContentKind.User, "prompt:1", null, "Hello"),
            new AgentActivityEvent(Provider, id, at, run, AgentActivityKind.ToolCall, AgentActivityPhase.Started, "tool:1", null, "read_file", null),
            new AgentActivityEvent(Provider, id, at.AddSeconds(1), run, AgentActivityKind.ToolCall, AgentActivityPhase.Completed, "tool:1", null, "read_file", null),
            new AgentSessionUpdateEvent(Provider, id, at.AddSeconds(2), run, AgentSessionUpdateKind.UsageUpdated, null,
                Usage: new(LastOperation: new AgentOperationUsageSnapshot(InputTokens: 1200, OutputTokens: 300))),
            new AgentSessionUpdateEvent(Provider, id, at.AddSeconds(3), run, AgentSessionUpdateKind.Idle, null),
        ], token);
        await host.AgentSessionCatalog.InvalidateAsync(token);
    }

    // A checkout whose origin is one remote: its repository key is the same on every machine.
    private static string Repository(string folder)
    {
        Git(folder, "init", "--quiet");
        Git(folder, "remote", "add", "origin", "https://github.com/example/fleet-fixture.git");
        return folder;
    }

    private static void Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error.Result}");
    }
}
