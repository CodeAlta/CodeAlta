using System.Net;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.ModelServers;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.ModelServers;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopModelServersTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RemoteServers_AreWatchedOnConnectionAndReconnection_WithoutOpeningSettings()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-model-server-watch-").FullName;
        try
        {
            using var network = new NotFoundHandler();
            await using var models = new ModelHost(new ModelHostOptions
            {
                SharedRoot = Path.Combine(root, "shared"), InstanceRoot = Path.Combine(root, "instance"), NetworkHandler = network,
            });
            models.SetSettings(models.Settings with { ModelsFolder = Path.Combine(root, "hub") });
            await using var fixture = await FleetNodeFixture.StartAsync(token, null, null, null, false, null, null, models);
            var machine = await fixture.PairViewerAsync(token);
            var hosts = new SessionHostRegistry(new SessionHostRegistryTests.RecordingSessionHost(null));
            await using var fleet = new DesktopFleet(fixture.ViewerFleetRoot, hosts.TakeMembership(), "viewer");
            var next = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var servers = new DesktopModelServers(null, fleet, new FleetModelServerTransports(), new ModelServerProviderRefresh(fixture.Host, null));
            servers.Changed += id => Volatile.Read(ref next).TrySetResult(id);
            await servers.StartAsync(token);
            await fleet.StartAsync(token);
            Assert.AreEqual(machine.DeviceId, await next.Task.WithinAsync(TestTime.Seconds(30), token), "connecting subscribes and reads once");

            Volatile.Write(ref next, new(TaskCreationOptions.RunContinuationsAsynchronously));
            await models.SetServerAsync(new ServerDefinition
            {
                Id = "coder", Name = "Coder", Backend = "cpu", Enabled = false, Models = [new ServerModel { Model = "fake/Model-GGUF:Q4_K_M" }],
            }, token);
            Assert.AreEqual(machine.DeviceId, await next.Task.WithinAsync(TestTime.Seconds(30), token), "the remote watch follows changes");

            Volatile.Write(ref next, new(TaskCreationOptions.RunContinuationsAsynchronously));
            await fleet.Hosts.Single().CurrentClient!.DisposeAsync();
            Assert.AreEqual(machine.DeviceId, await next.Task.WithinAsync(TestTime.Seconds(30), token), "a new channel subscribes and reads again");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }
}
