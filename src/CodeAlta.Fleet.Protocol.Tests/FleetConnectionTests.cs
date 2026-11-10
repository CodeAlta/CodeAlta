using System.Buffers;
using System.Text.Json;

namespace CodeAlta.Fleet.Protocol.Tests;

[TestClass]
public sealed class FleetConnectionTests
{
    private static readonly FleetConnectionOptions Quiet = new()
    {
        HeartbeatInterval = Timeout.InfiniteTimeSpan,
        DeadPeerTimeout = Timeout.InfiniteTimeSpan,
        RequestTimeout = Json.Hang,
    };

    [TestMethod]
    public async Task ConcurrentRequestsGetTheirOwnResponses()
    {
        await using var pair = ConnectionPair.Start(server => server.RegisterRequestHandler("echo", async (context, token) =>
        {
            // Answers out of order: later requests often answer first.
            var value = context.Params!.Value.GetProperty("n").GetInt32();
            await Task.Delay(value % 7, token);
            return context.Params;
        }), clientOptions: Quiet, serverOptions: Quiet);

        var calls = Enumerable.Range(0, 300)
            .Select(async n => (n, result: await pair.Client.SendRequestAsync("echo", Json.Parse($$"""{"n":{{n}}}"""))))
            .ToArray();
        var results = await Task.WhenAll(calls).WithinAsync(Json.Hang);

        foreach (var (n, result) in results) Assert.AreEqual(n, result!.Value.GetProperty("n").GetInt32());
    }

    [TestMethod]
    public async Task RequestsFlowBothWaysOnOneConnection()
    {
        await using var pair = ConnectionPair.Start(
            server => server.RegisterRequestHandler("node.info", (_, _) => ValueTask.FromResult<JsonElement?>(Json.Parse("""{"side":"server"}"""))),
            client => client.RegisterRequestHandler("node.info", (_, _) => ValueTask.FromResult<JsonElement?>(Json.Parse("""{"side":"client"}"""))),
            Quiet, Quiet);

        var fromServer = await pair.Client.SendRequestAsync("node.info").WithinAsync(Json.Hang);
        var fromClient = await pair.Server.SendRequestAsync("node.info").WithinAsync(Json.Hang);

        Assert.AreEqual("server", fromServer!.Value.GetProperty("side").GetString());
        Assert.AreEqual("client", fromClient!.Value.GetProperty("side").GetString());
    }

    [TestMethod]
    public async Task HandlerErrorsAreAnsweredWithTheirCodes()
    {
        await using var pair = ConnectionPair.Start(server =>
        {
            server.RegisterRequestHandler("sessions.send", (_, _) => throw new FleetException("session.stale_run", FleetErrorHints.Refresh, "The run ended."));
            server.RegisterRequestHandler("sessions.crash", (_, _) => throw new InvalidOperationException("C:\\Users\\secret"));
            server.RegisterRequestHandler("sessions.parse", (context, _) =>
                ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(context.Params!.Value.GetProperty("missing").GetInt32())));
        }, clientOptions: Quiet, serverOptions: Quiet);

        var stale = await Assert.ThrowsExactlyAsync<FleetException>(() => pair.Client.SendRequestAsync("sessions.send").WithinAsync(Json.Hang));
        Assert.AreEqual(new FleetError("session.stale_run", FleetErrorHints.Refresh, "The run ended."), stale.Error);

        var crash = await Assert.ThrowsExactlyAsync<FleetException>(() => pair.Client.SendRequestAsync("sessions.crash").WithinAsync(Json.Hang));
        Assert.AreEqual(FleetErrorCodes.InternalError, crash.Error.Code);
        Assert.IsFalse(crash.Message.Contains("secret", StringComparison.Ordinal), "The message of a handler's exception stays on its side.");

        var unknown = await Assert.ThrowsExactlyAsync<FleetException>(() => pair.Client.SendRequestAsync("sessions.fly").WithinAsync(Json.Hang));
        Assert.AreEqual(FleetErrorCodes.MethodNotFound, unknown.Error.Code);

        // A failed request does not harm the connection.
        Assert.AreEqual(FleetConnectionState.Open, pair.Client.State);
        Assert.AreEqual(FleetConnectionState.Open, pair.Server.State);
    }

    [TestMethod]
    public async Task EventsArriveInOrderAndHandlersCanAnswerWithEvents()
    {
        var received = new List<long>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = ConnectionPair.Start(
            server => server.RegisterRequestHandler("sessions.watch", async (context, token) =>
            {
                for (var seq = 1; seq <= 100; seq++) await context.Connection.SendEventAsync(new FleetEvent("session.display", null, 3, seq), token);
                return null;
            }),
            client => client.SetEventHandler((fleetEvent, _) =>
            {
                received.Add(fleetEvent.Sequence!.Value);
                if (received.Count == 100) done.TrySetResult();
                return ValueTask.CompletedTask;
            }),
            Quiet, Quiet);

        Assert.IsNull(await pair.Client.SendRequestAsync("sessions.watch").WithinAsync(Json.Hang));
        await done.Task.WithinAsync(Json.Hang);

        CollectionAssert.AreEqual(Enumerable.Range(1, 100).Select(n => (long)n).ToArray(), received.ToArray());
    }

    [TestMethod]
    public async Task HandlersAllBusy_TheReaderStillReads_AndARequestBeyondWhatMayWaitIsRefusedAsBusy()
    {
        // One handler at a time: one request runs, four wait for its slot, and the sixth is refused.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        await using var pair = ConnectionPair.Start(
            server => server.RegisterRequestHandler("block", async (_, token) =>
            {
                Interlocked.Increment(ref started);
                await release.Task.WaitAsync(token);
                return null;
            }),
            client => client.RegisterRequestHandler("client.info", (_, _) => ValueTask.FromResult<JsonElement?>(Json.Parse("""{"side":"client"}"""))),
            clientOptions: Quiet, serverOptions: new FleetConnectionOptions
            {
                HeartbeatInterval = Timeout.InfiniteTimeSpan, DeadPeerTimeout = Timeout.InfiniteTimeSpan, RequestTimeout = Json.Hang, MaxConcurrentIncomingRequests = 1,
            });

        var waiting = Enumerable.Range(0, 5).Select(_ => pair.Client.SendRequestAsync("block", null)).ToArray();
        var deadline = DateTime.UtcNow + Json.Hang;
        while (Volatile.Read(ref started) == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.AreEqual(1, Volatile.Read(ref started));

        // The reader of the server is not held by the requests that wait: it reads the answer of its own request.
        var answer = await pair.Server.SendRequestAsync("client.info", null).WithinAsync(Json.Hang);
        Assert.AreEqual("client", answer!.Value.GetProperty("side").GetString());
        var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => pair.Client.SendRequestAsync("block", null).WithinAsync(Json.Hang));
        Assert.AreEqual((FleetErrorCodes.ServerBusy, FleetErrorHints.Retry), (refused.Error.Code, refused.Error.Hint));

        // Once the handler is free, those that waited run, each in turn.
        release.SetResult();
        await Task.WhenAll(waiting).WithinAsync(Json.Hang);
        Assert.AreEqual(5, Volatile.Read(ref started));
    }

    [TestMethod]
    public async Task ARequestWithoutAnswerTimesOutAndOneCancelledStopsWaiting()
    {
        await using var pair = ConnectionPair.Start(server => server.RegisterRequestHandler("slow", async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }), clientOptions: Quiet, serverOptions: Quiet);

        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            pair.Client.SendRequestAsync("slow", null, new FleetRequestOptions { Timeout = TimeSpan.FromMilliseconds(50) }).WithinAsync(Json.Hang));

        using var cancellation = new CancellationTokenSource();
        var call = pair.Client.SendRequestAsync("slow", null, null, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WithinAsync(Json.Hang));
        Assert.AreEqual(FleetConnectionState.Open, pair.Client.State);
    }

    [TestMethod]
    public async Task ClosingFailsPendingRequestsAndClosesBothSidesCleanly()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = ConnectionPair.Start(server => server.RegisterRequestHandler("slow", async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }), clientOptions: Quiet, serverOptions: Quiet);

        var pending = pair.Client.SendRequestAsync("slow");
        await started.Task.WithinAsync(Json.Hang);
        await pair.Client.CloseAsync("bye").WithinAsync(Json.Hang);

        var failure = await Assert.ThrowsExactlyAsync<FleetConnectionClosedException>(() => pending.WithinAsync(Json.Hang));
        Assert.AreEqual(FleetErrorCodes.ConnectionClosed, failure.Error.Code);
        Assert.AreEqual(FleetErrorHints.Reconnect, failure.Error.Hint);

        var clientClose = await pair.Client.Completion.WithinAsync(Json.Hang);
        var serverClose = await pair.Server.Completion.WithinAsync(Json.Hang);
        Assert.IsTrue(clientClose.IsClean);
        Assert.IsTrue(clientClose.ClosedLocally);
        Assert.IsTrue(serverClose.IsClean);
        Assert.IsFalse(serverClose.ClosedLocally);
        Assert.AreEqual(FleetConnectionState.Closed, pair.Server.State);

        await Assert.ThrowsExactlyAsync<FleetConnectionClosedException>(() => pair.Client.SendRequestAsync("slow"));
        await Assert.ThrowsExactlyAsync<FleetConnectionClosedException>(async () => await pair.Server.SendEventAsync(new FleetEvent("late")));
        await pair.DisposeAsync();
    }

    [TestMethod]
    public async Task APeerThatClosesFailsTheRequestsWaitingForIt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = ConnectionPair.Start(server => server.RegisterRequestHandler("slow", async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }), clientOptions: Quiet, serverOptions: Quiet);

        var pending = pair.Client.SendRequestAsync("slow");
        await started.Task.WithinAsync(Json.Hang);
        await pair.Server.CloseAsync().WithinAsync(Json.Hang);

        var failure = await Assert.ThrowsExactlyAsync<FleetConnectionClosedException>(() => pending.WithinAsync(Json.Hang));
        Assert.IsFalse(failure.CloseInfo.ClosedLocally);
        Assert.IsTrue((await pair.Client.Completion.WithinAsync(Json.Hang)).IsClean);
    }

    [TestMethod]
    public async Task ASilentPeerClosesTheConnectionAfterTheDeadPeerTimeout()
    {
        var (local, silent) = FleetInMemoryTransport.CreatePair(capacity: 1024);
        var connection = new FleetConnection(local, new FleetConnectionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(20),
            DeadPeerTimeout = TimeSpan.FromMilliseconds(200),
            RequestTimeout = Json.Hang,
        });
        connection.Start();
        var pending = connection.SendRequestAsync("node.info");

        var close = await connection.Completion.WithinAsync(Json.Hang);

        Assert.AreEqual(FleetErrorCodes.PeerTimeout, close.Code);
        Assert.IsTrue(close.ClosedLocally);
        var failure = await Assert.ThrowsExactlyAsync<FleetConnectionClosedException>(() => pending.WithinAsync(Json.Hang));
        Assert.AreEqual(FleetErrorCodes.PeerTimeout, failure.Error.Code);

        // The connection pinged the silent peer before giving up on it.
        var buffer = new ArrayBufferWriter<byte>();
        var sawPing = false;
        while (await silent.ReceiveAsync(buffer, 1 << 16, CancellationToken.None).AsTask().ContinueWith(t => t.IsCompletedSuccessfully && t.Result == FleetFrameType.Text))
        {
            sawPing |= FleetMessageSerializer.Deserialize(buffer.WrittenSpan) is FleetPing;
            buffer.ResetWrittenCount();
        }

        Assert.IsTrue(sawPing);
        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task PingsKeepAnIdleConnectionAlive()
    {
        var (first, second) = FleetInMemoryTransport.CreatePair();
        var clientTransport = new CountingTransport(first);
        var serverTransport = new CountingTransport(second);
        // A dead-peer timeout far above the heartbeat: the test counts pings, never waits on the clock.
        var options = new FleetConnectionOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(10), DeadPeerTimeout = Json.Hang };
        await using var client = new FleetConnection(clientTransport, options);
        await using var server = new FleetConnection(serverTransport, options);
        server.Start();
        client.Start();

        while (clientTransport.Received < 10 || serverTransport.Received < 10)
        {
            Assert.AreEqual(FleetConnectionState.Open, client.State);
            await Task.Delay(10);
        }

        Assert.AreEqual(FleetConnectionState.Open, client.State);
        Assert.AreEqual(FleetConnectionState.Open, server.State);
    }

    [TestMethod]
    public async Task AFrameAboveTheLimitIsRefusedBeforeItIsSentAndClosesTheReceiver()
    {
        var small = new FleetConnectionOptions { MaxFrameBytes = 1024, HeartbeatInterval = Timeout.InfiniteTimeSpan, DeadPeerTimeout = Timeout.InfiniteTimeSpan, RequestTimeout = Json.Hang };
        var large = new FleetConnectionOptions { MaxFrameBytes = 1 << 20, HeartbeatInterval = Timeout.InfiniteTimeSpan, DeadPeerTimeout = Timeout.InfiniteTimeSpan, RequestTimeout = Json.Hang };
        await using var pair = ConnectionPair.Start(clientOptions: small, serverOptions: large);
        var payload = JsonSerializer.SerializeToElement(new string('x', 4096));

        var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => pair.Client.SendRequestAsync("big", payload));
        Assert.AreEqual(FleetErrorCodes.FrameTooLarge, refused.Error.Code);
        Assert.AreEqual(FleetConnectionState.Open, pair.Client.State);

        // The server's limit is larger: what it sends is too large for the client, which closes.
        await pair.Server.SendEventAsync(new FleetEvent("big", payload));
        var close = await pair.Client.Completion.WithinAsync(Json.Hang);
        Assert.AreEqual(FleetErrorCodes.FrameTooLarge, close.Code);
    }

    [TestMethod]
    public async Task UnknownKindsAreIgnoredAndMalformedFramesCloseTheConnection()
    {
        var (local, raw) = FleetInMemoryTransport.CreatePair();
        await using var connection = new FleetConnection(local, Quiet);
        connection.RegisterRequestHandler("node.info", (_, _) => ValueTask.FromResult<JsonElement?>(Json.Parse("1")));
        connection.Start();

        await raw.SendAsync("""{"t":"stream.credit","ch":1,"n":4}""".Utf8(), FleetFrameType.Text, CancellationToken.None);
        await raw.SendAsync("""{"t":"req","id":"r1","m":"node.info"}""".Utf8(), FleetFrameType.Text, CancellationToken.None);
        var buffer = new ArrayBufferWriter<byte>();
        Assert.AreEqual(FleetFrameType.Text, await raw.ReceiveAsync(buffer, 1 << 16, CancellationToken.None).AsTask().WithinAsync(Json.Hang));
        var response = (FleetResponse)FleetMessageSerializer.Deserialize(buffer.WrittenSpan)!;
        Assert.AreEqual("r1", response.Id);
        Assert.IsTrue(response.Ok);

        await raw.SendAsync("""{"t":"req","id":"r2"}""".Utf8(), FleetFrameType.Text, CancellationToken.None);
        buffer.ResetWrittenCount();
        Assert.AreEqual(FleetFrameType.Text, await raw.ReceiveAsync(buffer, 1 << 16, CancellationToken.None).AsTask().WithinAsync(Json.Hang));
        Assert.AreEqual(FleetErrorCodes.InvalidRequest, ((FleetResponse)FleetMessageSerializer.Deserialize(buffer.WrittenSpan)!).Error!.Code);

        await raw.SendAsync("not json"u8.ToArray(), FleetFrameType.Text, CancellationToken.None);
        var close = await connection.Completion.WithinAsync(Json.Hang);
        Assert.AreEqual(FleetErrorCodes.ProtocolError, close.Code);
    }

    [TestMethod]
    public async Task TypedRequestsUseSourceGeneratedContracts()
    {
        await using var pair = ConnectionPair.Start(server => server.RegisterRequestHandler("math.add", (context, _) =>
        {
            var numbers = context.Params!.Value.Deserialize(TestJsonContext.Default.Int32Array)!;
            return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(numbers.Sum(), TestJsonContext.Default.Int32));
        }), clientOptions: Quiet, serverOptions: Quiet);

        var sum = await pair.Client.SendRequestAsync("math.add", new[] { 1, 2, 3 }, TestJsonContext.Default.Int32Array, TestJsonContext.Default.Int32).WithinAsync(Json.Hang);
        Assert.AreEqual(6, sum);
    }

    [TestMethod]
    public void HandlersAreRegisteredBeforeTheStart()
    {
        var (local, _) = FleetInMemoryTransport.CreatePair();
        var connection = new FleetConnection(local, Quiet);
        connection.RegisterRequestHandler("a", (_, _) => ValueTask.FromResult<JsonElement?>(null));
        Assert.ThrowsExactly<ArgumentException>(() => connection.RegisterRequestHandler("a", (_, _) => ValueTask.FromResult<JsonElement?>(null)));
        connection.Start();
        Assert.ThrowsExactly<InvalidOperationException>(() => connection.RegisterRequestHandler("b", (_, _) => ValueTask.FromResult<JsonElement?>(null)));
        Assert.ThrowsExactly<InvalidOperationException>(connection.Start);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(int[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
internal sealed partial class TestJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
