using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Fleet.Protocol.Tests;

/// <summary>The handshake and the connection over real WebSockets, against a Kestrel endpoint on the loopback.</summary>
[TestClass]
public sealed class FleetWebSocketTransportTests
{
    private static readonly FleetProtocolRange Versions = new(1, 1);

    /// <summary>A fleet endpoint at <c>/fleet/v1</c>: it challenges each client, then serves one connection.</summary>
    private sealed class FleetEndpoint : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly TaskCompletionSource<FleetConnection> _served = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FleetEndpoint(WebApplication app, Uri address)
        {
            _app = app;
            Address = address;
        }

        public Uri Address { get; }

        public Task<FleetConnection> Served => _served.Task;

        public static async Task<FleetEndpoint> StartAsync(FleetConnectionPolicy policy, Action<FleetConnection> configure)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.UseWebSockets();
            FleetEndpoint? endpoint = null;
            app.Map("/fleet/v1", async (HttpContext context) =>
            {
                var transport = new FleetWebSocketTransport(await context.WebSockets.AcceptWebSocketAsync());
                var options = new FleetHandshakeServerOptions { Protocol = Versions, Policy = policy, Features = ["relay"] };
                var accepted = await FleetHandshake.AcceptAsync(transport, options, new TestDeviceKeys("laptop"), context.RequestAborted);
                await using var connection = new FleetConnection(transport, FleetConnectionOptions.FromPolicy(accepted.Welcome.Policy));
                configure(connection);
                connection.Start();
                endpoint!._served.TrySetResult(connection);
                // The socket lives as long as the request: the request ends when the connection does.
                await connection.Completion;
            });
            await app.StartAsync();
            var address = new Uri(app.Urls.First().Replace("http://", "ws://", StringComparison.Ordinal) + "/fleet/v1");
            endpoint = new FleetEndpoint(app, address);
            return endpoint;
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private static async Task<FleetConnection> ConnectAsync(Uri address, Action<FleetConnection>? configure = null)
    {
        var transport = await FleetWebSocketTransport.ConnectAsync(address, null, CancellationToken.None).WithinAsync(Json.Hang);
        var welcome = await FleetHandshake.ConnectAsync(transport,
            new FleetHandshakeClientOptions { Protocol = Versions, Client = new FleetClientInfo("desktop", "4.0.0", "windows") },
            new TestDeviceKeys("laptop"), CancellationToken.None).WithinAsync(Json.Hang);
        CollectionAssert.AreEqual(new[] { "relay" }, welcome.Features.ToArray());
        var connection = new FleetConnection(transport, FleetConnectionOptions.FromPolicy(welcome.Policy));
        configure?.Invoke(connection);
        connection.Start();
        return connection;
    }

    [TestMethod]
    public async Task RequestsEventsAndACleanCloseCrossAWebSocket()
    {
        await using var endpoint = await FleetEndpoint.StartAsync(new FleetConnectionPolicy(1 << 20, 25_000), server =>
            server.RegisterRequestHandler("echo", async (context, token) =>
            {
                await context.Connection.SendEventAsync(new FleetEvent("echo.seen", null, 1, 1), token);
                return context.Params;
            }));
        var seen = new TaskCompletionSource<FleetEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = await ConnectAsync(endpoint.Address, connection => connection.SetEventHandler((fleetEvent, _) =>
        {
            seen.TrySetResult(fleetEvent);
            return ValueTask.CompletedTask;
        }));

        // Larger than one receive chunk, so that a frame arrives in several pieces.
        var text = new string('é', 50_000);
        var calls = Enumerable.Range(0, 20).Select(n => client.SendRequestAsync("echo", JsonSerializer.SerializeToElement($"{n}:{text}"))).ToArray();
        var results = await Task.WhenAll(calls).WithinAsync(Json.Hang);
        for (var n = 0; n < results.Length; n++) Assert.AreEqual($"{n}:{text}", results[n]!.Value.GetString());
        Assert.AreEqual("echo.seen", (await seen.Task.WithinAsync(Json.Hang)).Event);

        var unknown = await Assert.ThrowsExactlyAsync<FleetException>(() => client.SendRequestAsync("sessions.fly").WithinAsync(Json.Hang));
        Assert.AreEqual(FleetErrorCodes.MethodNotFound, unknown.Error.Code);

        var server = await endpoint.Served.WithinAsync(Json.Hang);
        await client.CloseAsync("done").WithinAsync(Json.Hang);
        Assert.IsTrue((await client.Completion.WithinAsync(Json.Hang)).IsClean);
        var serverClose = await server.Completion.WithinAsync(Json.Hang);
        Assert.IsTrue(serverClose.IsClean);
        Assert.IsFalse(serverClose.ClosedLocally);
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task AFrameAboveThePolicyClosesTheReceivingSide()
    {
        await using var endpoint = await FleetEndpoint.StartAsync(new FleetConnectionPolicy(64 * 1024, 25_000), _ => { });
        var transport = await FleetWebSocketTransport.ConnectAsync(endpoint.Address, null, CancellationToken.None).WithinAsync(Json.Hang);
        var welcome = await FleetHandshake.ConnectAsync(transport,
            new FleetHandshakeClientOptions { Protocol = Versions, Client = new FleetClientInfo("node", "4.0.0", "linux") },
            new TestDeviceKeys("laptop"), CancellationToken.None).WithinAsync(Json.Hang);
        Assert.AreEqual(64 * 1024, welcome.Policy.MaxFrameBytes);

        // A client that ignores the policy: the server reads no more than the limit and closes.
        await using var client = new FleetConnection(transport, new FleetConnectionOptions { MaxFrameBytes = 1 << 20 });
        client.Start();
        await client.SendEventAsync(new FleetEvent("big", JsonSerializer.SerializeToElement(new string('x', 200_000))));

        var server = await endpoint.Served.WithinAsync(Json.Hang);
        Assert.AreEqual(FleetErrorCodes.FrameTooLarge, (await server.Completion.WithinAsync(Json.Hang)).Code);
        var clientClose = await client.Completion.WithinAsync(Json.Hang);
        Assert.IsFalse(clientClose.IsClean, $"{clientClose}");
    }
}
