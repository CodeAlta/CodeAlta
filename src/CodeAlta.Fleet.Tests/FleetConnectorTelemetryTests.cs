using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The telemetry settings a server pushes to a connector: taken one at a time, in the order they came, so that the
/// check of a setting against the one held is never raced by another push.
/// </summary>
[TestClass]
public sealed class FleetConnectorTelemetryTests
{
    private static readonly TimeSpan Hang = TestTime.Seconds(30);

    [TestMethod]
    public async Task PushedSettingsAreTakenOneAtATimeInTheOrderTheyCame()
    {
        using var folder = new TestFolder();
        using var identity = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "node"));
        var (clientSide, serverSide) = FleetInMemoryTransport.CreatePair();
        FleetConnection? server = null;
        var serving = Task.Run(async () =>
        {
            var accepted = await FleetHandshake.AcceptAsync(serverSide, new FleetHandshakeServerOptions { Protocol = new FleetProtocolRange(1, 1) }, new AcceptAll(),
                CancellationToken.None);
            server = new FleetConnection(serverSide, FleetConnectionOptions.FromPolicy(accepted.Welcome.Policy));
            server.RegisterRequestHandler(FleetServerMethods.PresenceUpdate, static (_, _) => ValueTask.FromResult<JsonElement?>(null));
            server.Start();
            for (var sequence = 1; sequence <= 3; sequence++)
            {
                await server.SendEventAsync(new FleetEvent(FleetServerMethods.TelemetrySettings,
                    JsonSerializer.SerializeToElement(new FleetSignedTelemetrySettings("setting-" + sequence, "signature"), FleetServerJsonContext.Default.FleetSignedTelemetrySettings)),
                    CancellationToken.None);
            }
        });

        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var running = 0;
        var overlapped = 0;
        await using var connector = new FleetConnector(new FleetConnectorOptions
        {
            ServerAddress = new Uri("http://server.test/"), Identity = identity, OutboxPath = Path.Combine(folder.Path, "outbox.json"),
            Client = new FleetClientInfo("node", "4.0.0", "windows"),
            ConnectTransport = (_, _) => Task.FromResult<IFleetTransport>(clientSide),
            TelemetrySettings = async (settings, _) =>
            {
                if (Interlocked.Increment(ref running) > 1) Interlocked.Increment(ref overlapped);
                int count;
                lock (order)
                {
                    order.Add(settings.Payload);
                    count = order.Count;
                }

                if (count == 1)
                {
                    // The first holds while the others arrive: a second one taken now would race its check.
                    first.TrySetResult();
                    await release.Task;
                }

                Interlocked.Decrement(ref running);
                if (count == 3) allTaken.TrySetResult();
            },
        });
        connector.Start();
        await serving.WithinAsync(Hang);
        await first.Task.WithinAsync(Hang);
        // Long enough for the other pushes to reach the connector.
        await Task.Delay(300);
        release.SetResult();
        await allTaken.Task.WithinAsync(Hang);

        Assert.AreEqual(0, Volatile.Read(ref overlapped), "A pushed setting is never taken while another one is.");
        CollectionAssert.AreEqual(new[] { "setting-1", "setting-2", "setting-3" }, order, "The settings are taken in the order the server sent them.");
        await server!.DisposeAsync();
    }

    private sealed class AcceptAll : IFleetHandshakeVerifier
    {
        public ValueTask<FleetProofVerification> VerifyAsync(FleetHello hello, FleetProofContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(FleetProofVerification.Accept());
    }
}
