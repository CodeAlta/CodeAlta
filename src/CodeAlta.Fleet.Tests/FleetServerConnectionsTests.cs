using CodeAlta.Catalog;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

[TestClass]
public sealed class FleetServerConnectionsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ALinkWhoseSpacesDidNotRead_IsLoadedOnlyOnceTheyRead_WithTheSpacesLinkedToIt()
    {
        var token = TestContext.CancellationToken;
        using var folder = new TestFolder();
        var fleetRoot = Path.Combine(folder.Path, "fleet");
        using var identity = DeviceIdentity.LoadOrCreate(fleetRoot);
        // A server that is away (nothing listens on that loopback port): the spaces linked to it are known all the same.
        var url = FleetServerAddress.Format(new Uri("http://127.0.0.1:9"));
        var now = DateTimeOffset.UtcNow;
        new FleetServerLinkStore(fleetRoot).Save(new FleetServerLink(FleetServerLinkStore.NewId(), url, null, null, identity.DeviceId, FleetDeviceKinds.Node,
            FleetServerLinkStates.Approved, now, now));
        var spaces = new SpaceCatalog(new ProjectCatalog(new CatalogOptions { GlobalRoot = folder.Path }));
        Assert.AreEqual(SpaceChangeStatus.Ok, (await spaces.UpdateSettingsAsync(SpaceDescriptor.DefaultId, settings => settings with { Fleet = new SpaceFleet(url) }, token)).Status);

        // The first reads of the spaces fail, then they read.
        var failing = 1;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connections = new FleetServerConnections(new FleetServerConnectionsOptions
        {
            FleetRoot = fleetRoot,
            Identity = identity,
            Client = new FleetClientInfo("node", "4.0.0", "windows"),
            Spaces = spaces,
            ReloadInterval = TimeSpan.FromMilliseconds(100),
            ReadSpaces = cancellationToken =>
            {
                if (Volatile.Read(ref failing) == 1)
                {
                    failed.TrySetResult();
                    throw new IOException("The spaces do not read.");
                }

                return spaces.LoadAsync(cancellationToken);
            },
        });
        connections.Start();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        // Several reloads while the spaces do not read: the links are not loaded, and nothing counts them as such.
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        Assert.IsFalse(connections.Loaded.IsCompleted, "the links are not loaded while the spaces linked to them do not read");
        Assert.IsNotNull(connections.LoadProblem);

        Volatile.Write(ref failing, 0);
        await connections.Loaded.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.IsNull(connections.LoadProblem);
        Assert.IsTrue(connections.Connections.Single().Connector!.LinksSpace(null), "the spaces linked to the server are read once loaded");
    }
}
