using System.Net;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.ModelServers;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The model servers of a node over its API (<c>doc/fleet/model-servers.md</c>, Permissions): View reads them, Manage changes
/// them, Models uses the servers its computers may reach; the tunnel carries only the requests of inference.
/// </summary>
[TestClass]
public sealed class FleetNodeModelServersTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ADeviceThatViews_ReadsTheModelServers_ButNeitherChangesNorUsesThem()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await NodeFixture.StartAsync(token);
        var paired = await fixture.PairViewerAsync(token);
        await using var node = await ServedModelNode.StartAsync(fixture, DevicePermissions.View, token);

        var status = await node.Client.GetModelServersAsync(token);
        CollectionAssert.AreEquivalent(new[] { "open", "private" }, status.Servers.Select(static server => server.Definition.Id).ToArray());

        var change = await Assert.ThrowsExactlyAsync<FleetException>(() => node.Client.DoModelServersAsync(new ModelHostAction { Action = "removeServer", Id = "open" }, token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, change.Error.Code);
        var use = await Assert.ThrowsExactlyAsync<FleetException>(() => node.Client.ListModelServersAsync(token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, use.Error.Code, "using the servers needs Models");
        Assert.HasCount(2, node.Host.ListServers(), "nothing was removed");
        Assert.IsNotNull(paired);
    }

    [TestMethod]
    public async Task ADeviceWithModels_UsesTheServersItsComputersMayReach_ForInferenceOnly()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await NodeFixture.StartAsync(token);
        _ = await fixture.PairViewerAsync(token);
        await using var node = await ServedModelNode.StartAsync(fixture, DevicePermissions.Models, token);

        var listed = await node.Client.ListModelServersAsync(token);
        CollectionAssert.AreEqual(new[] { "open" }, listed.Servers.Select(static server => server.Id).ToArray(), "a server of this machine only is not offered");

        async Task<FleetException> RefusedAsync(string server, HttpMethod method, string path)
        {
            using var request = new HttpRequestMessage(method, "http://model-server.invalid" + path);
            return await Assert.ThrowsExactlyAsync<FleetException>(() => node.Client.SendModelServerRequestAsync(server, request, token));
        }

        Assert.AreEqual(FleetErrorCodes.Forbidden, (await RefusedAsync("private", HttpMethod.Post, "/v1/chat/completions")).Error.Code, "a server of this machine only");
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await RefusedAsync("open", HttpMethod.Post, "/models/load")).Error.Code, "the router's management");
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await RefusedAsync("open", HttpMethod.Get, "/v1/../models")).Error.Code, "a path that climbs");
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await RefusedAsync("open", HttpMethod.Delete, "/v1/models")).Error.Code, "another method");
        Assert.AreEqual(FleetModelServerMethods.NotFound, (await RefusedAsync("nowhere", HttpMethod.Get, "/v1/models")).Error.Code);
        var stopped = await RefusedAsync("open", HttpMethod.Get, "/v1/models");
        Assert.AreEqual(FleetNodeErrorCodes.ProvidersUnavailable, stopped.Error.Code, "a server that does not run says so");
        StringAssert.Contains(stopped.Error.Message, "does not run");

        var change = await Assert.ThrowsExactlyAsync<FleetException>(() => node.Client.DoModelServersAsync(new ModelHostAction { Action = "controlServer", Id = "open", Verb = "start" }, token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, change.Error.Code, "Models uses, Manage changes");
    }

    [TestMethod]
    public async Task ADeviceThatManages_ChangesTheModelServers()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await NodeFixture.StartAsync(token);
        _ = await fixture.PairViewerAsync(token);
        await using var node = await ServedModelNode.StartAsync(fixture, DevicePermissions.View | DevicePermissions.Manage, token);

        _ = await node.Client.DoModelServersAsync(new ModelHostAction { Action = "removeServer", Id = "private" }, token);

        CollectionAssert.AreEqual(new[] { "open" }, node.Host.ListServers().Select(static server => server.Definition.Id).ToArray());
        var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => node.Client.DoModelServersAsync(new ModelHostAction
        {
            Action = "setServer",
            Server = new ServerDefinition { Id = "open", Name = "Open", Backend = "cpu", Enabled = false, ExtraArguments = ["--tools", "all"], Models = [new ServerModel { Model = "a/b:Q4_K_M" }] },
        }, token));
        Assert.AreEqual(FleetErrorCodes.InvalidParams, refused.Error.Code, "an option that runs commands is refused");
    }

    [TestMethod]
    public void TheStatusOfTheNode_SaysItsBuildsAndEachServer()
    {
        var hardware = new HardwareInventory("linux", "x64", null, null, [], new MachineLibraries(null, null, null, false, false, null), []);
        var build = new InstalledBuild("b12110", 12110, "rocm", null, "f", 1, DateTimeOffset.UnixEpoch, [], null, true, false, false, "ggml-org");
        var server = new ServerView(new ServerDefinition { Id = "coder", Name = "Coder", Backend = "rocm", Models = [new ServerModel { Model = "a/b:Q4_K_M" }] },
            new ServerStatus("ready", 1, "http://127.0.0.1:9931", "b12110", null, null, 0, [new ServerModelStatus("a/b:Q4_K_M", "loaded", null, 32768, null, 0, 4, null, false)]));
        var status = new ModelHostStatus(hardware, [build], [], new LlamaRelease("b12193", 12193, DateTimeOffset.UnixEpoch, null, []), new ModelHostSettings(), null, [], "m", 0, 0, [], [],
            [server], [], [], [], 1);
        var lines = CodeAlta.Node.NodeServiceCommands.ModelServersLines(status).ToArray();
        CollectionAssert.AreEqual(new[]
        {
            "Model servers: 1; llama.cpp b12110 (rocm), b12193 available.",
            "  Coder (coder): ready at http://127.0.0.1:9931, 1 model, loaded: a/b:Q4_K_M.",
        }, lines);
    }

    /// <summary>A node API over a model host of its own, two servers in it that do not run, and the viewer's channel with the permissions named.</summary>
    private sealed class ServedModelNode : IAsyncDisposable
    {
        private readonly FleetNodeService _service;
        private readonly CancellationTokenSource _stop;
        private readonly Task _serving;
        private readonly string _root;

        private ServedModelNode(FleetNodeService service, CancellationTokenSource stop, Task serving, FleetNodeClient client, ModelHost host, string root)
        {
            _service = service;
            _stop = stop;
            _serving = serving;
            Client = client;
            Host = host;
            _root = root;
        }

        public FleetNodeClient Client { get; }

        public ModelHost Host { get; }

        public static async Task<ServedModelNode> StartAsync(NodeFixture fixture, DevicePermissions permissions, CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "codealta-model-node-" + Guid.NewGuid().ToString("N"));
            // Nothing reaches GitHub or Hugging Face: every request is answered 404.
            var host = new ModelHost(new ModelHostOptions
            {
                SharedRoot = Path.Combine(root, "shared"), InstanceRoot = Path.Combine(root, "instance"), FirstPort = 19931, NetworkHandler = new NotFoundHandler(),
            });
            foreach (var (id, access) in new[] { ("open", "my-computers"), ("private", "this-machine") })
            {
                await host.SetServerAsync(new ServerDefinition
                {
                    Id = id, Name = id, Backend = "cpu", Enabled = false, Access = access, Models = [new ServerModel { Model = "a/b:Q4_K_M" }],
                }, cancellationToken);
            }

            fixture.Fleet.Service.TrustedDevices.SetPermissions(fixture.ViewerIdentity.DeviceId, permissions);
            var service = new FleetNodeService(new FleetNodeServiceOptions
            {
                Host = fixture.Node.Host, Identity = fixture.NodeIdentity, TrustedDevices = fixture.Fleet.Service.TrustedDevices, Name = "test-node", ModelServers = host,
            });
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var (serverStream, clientStream) = FleetStreams.CreatePair();
            var serving = Task.Run(async () =>
            {
                using var certificate = fixture.NodeIdentity.CreateTlsCertificate();
                var channel = await FleetChannelStack.AcceptAsync(serverStream, new FleetChannelServerOptions
                {
                    Certificate = certificate,
                    TrustPolicy = service.TrustedDevices,
                }, stop.Token);
                await service.ServeAsync(channel, fromThisMachine: false, stop.Token);
            }, stop.Token);
            var client = await FleetNodeClient.ConnectAsync(clientStream, fixture.NodeIdentity.Fingerprint, fixture.ViewerIdentity, cancellationToken);
            return new ServedModelNode(service, stop, serving, client, host, root);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();
            try { await _serving.WaitAsync(Patience); }
            catch (OperationCanceledException) { }
            await _service.DisposeAsync();
            await Host.DisposeAsync();
            _stop.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request, Content = new StringContent("{}") });
    }
}
