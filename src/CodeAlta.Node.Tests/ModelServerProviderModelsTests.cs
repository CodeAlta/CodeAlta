using System.Net;
using System.Runtime.InteropServices;
using CodeAlta.Agent;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.ModelServers;
using CodeAlta.ModelServers.Hardware;

namespace CodeAlta.Node.Tests;

[TestClass]
public sealed class ModelServerProviderModelsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AProviderAddedForAServer_ListsItsModelsWithoutATest_AlsoWhenItWasWaiting(bool waiting)
    {
        using var root = new NodeTestRoot();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var token = stop.Token;
        using var network = new NotFoundHandler();
        await using var models = new ModelHost(new ModelHostOptions
        {
            SharedRoot = Path.Combine(root.Root, "shared"),
            InstanceRoot = Path.Combine(root.Root, "models"),
            NetworkHandler = network,
            Probe = new HardwareProbe(static (_, _, _) => Task.FromResult<string?>(null), root.Root),
        });
        var hub = Path.Combine(root.Root, "hub");
        WriteModel(hub, "Alpha");
        WriteModel(hub, "Beta");
        models.SetSettings(models.Settings with { ModelsFolder = hub });
        await models.StartAsync(token);
        var transports = new FleetModelServerTransports { Local = models };
        await using var node = await NodeHost.StartAsync(root.Options() with { ModelServerTransports = transports.Resolve }, token);
        await using var fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "models", "1.0.0", null, null, null, null, models);
        var viewerRoot = Path.Combine(root.Root, "viewer");
        using var viewer = DeviceIdentity.LoadOrCreate(viewerRoot);
        var (offer, _) = fleet.Service.Pairing.CreateOffer("models", fleet.Addresses, TestTime.Minutes(1), manages: true);
        var machine = await FleetNodeClient.PairAsync(offer, viewer, "viewer", new PairedMachineStore(viewerRoot), token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, viewer, token);
        var initialization = node.Host.ModelProviderInitializationService;
        var id = new ModelProviderId("llama-coder");
        var definition = new ServerDefinition
        {
            Id = "coder", Name = "Coder", Backend = "cpu", Port = FreePort(),
            Models = [new ServerModel { Model = "fake/Alpha-GGUF:Q4_K_M" }],
        };
        try
        {
            if (!waiting) await AddBuildAsync(models, root.Root, token);
            await models.SetServerAsync(definition, token);
            if (waiting) Assert.AreEqual("waiting", models.ListServers().Single().Status.State);
            else await WaitUntilAsync(() => models.ListServers().Single().Status.State == "ready", token).WithinAsync(TestTime.Seconds(30));

            await client.SetProviderAsync(new FleetProviderSetParams(id.Value, "llama-cpp", "Coder", null, null, null) { Server = "local/coder" }, token);
            var first = await initialization.GetModelsAsync(id, token).WithinAsync(TestTime.Seconds(30));
            if (waiting)
            {
                Assert.AreEqual(0, first.Count, "the provider was added before its build was installed");
                await AddBuildAsync(models, root.Root, token);
                await models.ControlServerAsync("coder", "start", null, token);
            }

            await WaitUntilAsync(() => models.ListServers().Single().Status.State == "ready", token).WithinAsync(TestTime.Seconds(30));
            await WaitUntilAsync(() => initialization.CurrentStates.Any(state => state.ProviderId == id && state.Models.Count == 1), token)
                .WithinAsync(TestTime.Seconds(30));
            var listed = await initialization.GetModelsAsync(id, token);
            StringAssert.Contains(listed.Single().Id, "Alpha");

            // A changed server keeps the same provider: its next list must not keep the previous model.
            await models.SetServerAsync(definition with { Models = [new ServerModel { Model = "fake/Beta-GGUF:Q4_K_M" }] }, token);
            await WaitUntilAsync(() => initialization.CurrentStates.Any(state => state.ProviderId == id && state.Models.Any(model => model.Id.Contains("Beta", StringComparison.Ordinal))), token)
                .WithinAsync(TestTime.Seconds(30));
            listed = await initialization.GetModelsAsync(id, token);
            Assert.AreEqual(1, listed.Count);
            StringAssert.Contains(listed.Single().Id, "Beta");
        }
        catch
        {
            foreach (var server in models.ListServers())
                TestContext.WriteLine($"{server.Status}\n{string.Join('\n', models.ReadServerLog(server.Definition.Id, 20))}");
            throw;
        }
        finally
        {
            await stop.CancelAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(TestTime.Milliseconds(50), cancellationToken);
    }

    private static async Task AddBuildAsync(ModelHost models, string root, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
            Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")));
        var folder = Directory.CreateDirectory(Path.Combine(root, "fake-build")).FullName;
        const string assembly = "CodeAlta.Tests.FakeLlamaServer";
        var program = OperatingSystem.IsWindows() ? assembly + ".exe" : assembly;
        var target = Path.Combine(folder, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
        File.Copy(Path.Combine(AppContext.BaseDirectory, program), target);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(Path.Combine(AppContext.BaseDirectory, program)));
        foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, assembly + extension), Path.Combine(folder, assembly + extension));
        var build = await models.Builds.AddOwnAsync(folder, "cpu", cancellationToken);
        models.Builds.MakeCurrent("cpu", build.Tag);
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void WriteModel(string hub, string name)
    {
        var folder = Path.Combine(hub, "models--fake--" + name + "-GGUF");
        Directory.CreateDirectory(Path.Combine(folder, "refs"));
        File.WriteAllText(Path.Combine(folder, "refs", "main"), "fake-commit");
        var snapshot = Directory.CreateDirectory(Path.Combine(folder, "snapshots", "fake-commit")).FullName;
        File.WriteAllBytes(Path.Combine(snapshot, name + "-Q4_K_M.gguf"), [.. "GGUF"u8, .. new byte[60]]);
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }
}
