using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node.Tests;

/// <summary>
/// A retried side effect never runs twice (<c>doc/fleet/protocol.md</c>, Durability): a retry gets the first receipt, and a
/// key whose receipt was evicted answers <c>expired</c>, never running again; a restart of the node forgets neither.
/// </summary>
[TestClass]
public sealed class FleetNodeRetryTests
{
    private static readonly TimeSpan Patience = TestTime.Seconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ARetriedCreation_WhoseReceiptWasEvicted_AnswersExpired_AndCreatesNoSecondSession()
    {
        await using var fixture = await NodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        _ = await fixture.PairViewerAsync(token);
        await using var node = await ServedNode.StartAsync(fixture, capacity: 2, token);

        var first = await node.Client.CreateSessionAsync(new FleetCreateSessionParams(Title: "First"), "create-first", token);
        _ = await node.Client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Second"), "create-second", token);
        _ = await node.Client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Third"), "create-third", token);
        var before = await CountSessionsAsync(node.Client, token);

        var retry = await Assert.ThrowsExactlyAsync<FleetException>(() => node.Client.CreateSessionAsync(new FleetCreateSessionParams(Title: "First"), "create-first", token));

        Assert.AreEqual(FleetErrorCodes.Expired, retry.Error.Code, "A key whose receipt was evicted answers expired.");
        Assert.AreEqual(before, await CountSessionsAsync(node.Client, token), "The evicted creation does not run again.");
        Assert.IsTrue((await node.Client.ListSessionsAsync(cancellationToken: token)).Sessions.Any(session => session.Id == first.SessionId));
    }

    [TestMethod]
    public async Task ARetriedCreation_AfterTheNodeRestarted_GetsTheFirstSession()
    {
        await using var fixture = await NodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        _ = await fixture.PairViewerAsync(token);
        FleetCreateSessionResult created;
        await using (var node = await ServedNode.StartAsync(fixture, capacity: 256, token))
            created = await node.Client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Before the restart"), "create-restart", token);

        // The node API starts again on the same profile, as after a restart of the node.
        await using var restarted = await ServedNode.StartAsync(fixture, capacity: 256, token);
        var before = await CountSessionsAsync(restarted.Client, token);
        var again = await restarted.Client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Before the restart"), "create-restart", token);

        Assert.AreEqual(created.SessionId, again.SessionId, "A retry after a restart gets the first session.");
        Assert.AreEqual(before, await CountSessionsAsync(restarted.Client, token), "The creation does not run again after a restart.");
    }

    private static async Task<int> CountSessionsAsync(FleetNodeClient client, CancellationToken token)
        => (await client.ListSessionsAsync(new FleetSessionListParams(Limit: 500, IncludeArchived: true), token)).Sessions.Count;

    /// <summary>A node API of its own over the runtime of the fixture, and the viewer's channel to it.</summary>
    private sealed class ServedNode : IAsyncDisposable
    {
        private readonly FleetNodeService _service;
        private readonly CancellationTokenSource _stop;
        private readonly Task _serving;

        private ServedNode(FleetNodeService service, CancellationTokenSource stop, Task serving, FleetNodeClient client)
        {
            _service = service;
            _stop = stop;
            _serving = serving;
            Client = client;
        }

        public FleetNodeClient Client { get; }

        public static async Task<ServedNode> StartAsync(NodeFixture fixture, int capacity, CancellationToken cancellationToken)
        {
            var service = new FleetNodeService(new FleetNodeServiceOptions
            {
                Host = fixture.Node.Host, Identity = fixture.NodeIdentity, TrustedDevices = fixture.Fleet.Service.TrustedDevices, Name = "test-node",
                RetryReceiptCapacity = capacity,
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
            return new ServedNode(service, stop, serving, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();
            try { await _serving.WithinAsync(Patience); }
            catch (OperationCanceledException) { }
            await _service.DisposeAsync();
            _stop.Dispose();
        }
    }
}
