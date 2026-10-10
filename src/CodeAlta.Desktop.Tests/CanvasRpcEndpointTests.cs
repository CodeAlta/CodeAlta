using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Desktop;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The host of the calls of one canvas: a real NeoAstra host, built at run time from what a plugin registers, driven through a fake
/// carrier that plays the page. These tests are the spike of the design: a host without a view, handlers added by name, a session whose
/// frames are carried by something else.
/// </summary>
[TestClass]
public sealed class CanvasRpcEndpointTests
{
    private sealed record Board(string Title, int Columns);

    private sealed record GetBoard(string Project, int Limit = 3);

    [TestMethod]
    public async Task Call_ReadsTheRequestAsAnOrdinaryTypeAndWritesTheResultInCamelCase()
    {
        await using var harness = new Harness(rpc => rpc.Handle<GetBoard, Board>("board.get", (request, _) => new(new Board($"{request.Project}:{request.Limit}", 2))));
        var connection = harness.Connect();

        harness.Send(connection, Invoke("r1", "board.get", """{"project":"p1"}"""));

        var result = await harness.NextAsync(frame => frame.Kind == "result" && frame.Id == "r1");
        Assert.IsTrue(result.Root.GetProperty("ok").GetBoolean());
        Assert.AreEqual("p1:3", result.Root.GetProperty("value").GetProperty("title").GetString());
        Assert.AreEqual(2, result.Root.GetProperty("value").GetProperty("columns").GetInt32());
    }

    [TestMethod]
    public async Task Call_WithoutInputReadsAnEmptyObject_AndAVoidCallHasNoResultValue()
    {
        var seen = new TaskCompletionSource<string>();
        await using var harness = new Harness(rpc =>
        {
            rpc.Handle<GetBoard, Board>("board.default", (request, _) => new(new Board(request.Project ?? "none", request.Limit)));
            rpc.Handle<GetBoard>("board.touch", (request, _) =>
            {
                seen.TrySetResult(request.Project);
                return ValueTask.CompletedTask;
            });
        });
        var connection = harness.Connect();

        harness.Send(connection, """{"neoastra":1,"kind":"invoke","id":"r1","command":"board.default","args":null}""");
        var result = await harness.NextAsync(frame => frame.Id == "r1");
        Assert.AreEqual("none", result.Root.GetProperty("value").GetProperty("title").GetString(), "a call without input reads as {}: the record's defaults apply");
        harness.Send(connection, Invoke("r2", "board.touch", """{"project":"p9"}"""));
        var done = await harness.NextAsync(frame => frame.Id == "r2");
        Assert.IsTrue(done.Root.GetProperty("ok").GetBoolean());
        Assert.AreEqual("p9", await seen.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public async Task Errors_CarryStableCodes_AndNeverTheTextOfAnUnexpectedFailure()
    {
        await using var harness = new Harness(rpc =>
        {
            rpc.Handle<GetBoard, Board>("explicit", (_, _) => throw new PluginRpcException("not_found", "No such board.\nSecond line", retryable: true));
            rpc.Handle<GetBoard, Board>("crash", (_, _) => throw new InvalidOperationException("secret path C:\\Users\\someone\\token.txt"));
            rpc.Handle<GetBoard, Board>("strict", (_, _) => new(new Board("x", 1)));
        });
        var connection = harness.Connect();

        harness.Send(connection, Invoke("e1", "explicit", "{}"));
        var explicitError = (await harness.NextAsync(frame => frame.Id == "e1")).Root.GetProperty("error");
        Assert.AreEqual(("not_found", "No such board. Second line", true),
            (explicitError.GetProperty("code").GetString(), explicitError.GetProperty("message").GetString(), explicitError.GetProperty("retryable").GetBoolean()));

        harness.Send(connection, Invoke("e2", "crash", "{}"));
        var crash = await harness.NextAsync(frame => frame.Id == "e2");
        Assert.AreEqual("internal_error", crash.Root.GetProperty("error").GetProperty("code").GetString());
        Assert.IsFalse(crash.Json.Contains("secret", StringComparison.Ordinal), "the text of the failure stays with the plugin");

        harness.Send(connection, Invoke("e3", "strict", """{"project":42}"""));
        Assert.AreEqual("invalid_request", (await harness.NextAsync(frame => frame.Id == "e3")).Root.GetProperty("error").GetProperty("code").GetString());

        harness.Send(connection, Invoke("e4", "missing", "{}"));
        Assert.AreEqual("command_not_found", (await harness.NextAsync(frame => frame.Id == "e4")).Root.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Cancel_CancelsTheTokenOfTheHandler_AndAnswersWithTheCancellation()
    {
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        await using var harness = new Harness(rpc => rpc.Handle<GetBoard, Board>("slow", async (_, cancellationToken) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            return null!;
        }));
        var connection = harness.Connect();

        harness.Send(connection, Invoke("c1", "slow", "{}"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Send(connection, """{"neoastra":1,"kind":"cancel","id":"c1"}""");

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await harness.NextAsync(frame => frame.Id == "c1");
        Assert.AreEqual("operation_canceled", result.Root.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Stream_IsPulledOnlyAsFastAsTheScriptAcknowledges()
    {
        var produced = 0;
        await using var harness = new Harness(rpc => rpc.Stream<GetBoard, int>("count", Count));
        async IAsyncEnumerable<int> Count(GetBoard request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var index = 1; index <= 100; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref produced);
                yield return index;
                await Task.Yield();
            }
        }

        var connection = harness.Connect();
        harness.Send(connection, Invoke("s1", "count", "{}"));
        var channel = (await harness.NextAsync(frame => frame.Id == "s1")).Root.GetProperty("value").GetProperty("channel").GetString()!;

        // Without acknowledgements the host sends its credits (8) and stops: the iterator is not pulled further than one item ahead.
        var first = new List<int>();
        for (var index = 0; index < 8; index++) first.Add((await harness.NextAsync(frame => frame.Kind == "channel_item")).Root.GetProperty("value").GetInt32());
        CollectionAssert.AreEqual(Enumerable.Range(1, 8).ToArray(), first);
        await Task.Delay(300);
        Assert.IsFalse(harness.Has(frame => frame.Kind == "channel_item"), "no more than the credits without an acknowledgement");
        Assert.IsTrue(Volatile.Read(ref produced) <= 10, $"the iterator ran {produced} items ahead of a script that took 8");

        // Each acknowledgement frees credits, and the stream goes on in order until it completes.
        var rest = new List<int>();
        harness.Send(connection, $$"""{"neoastra":1,"kind":"channel_ack","channel":"{{channel}}","sequence":8}""");
        var sequence = 8;
        while (rest.Count < 92)
        {
            var item = await harness.NextAsync(frame => frame.Kind == "channel_item");
            rest.Add(item.Root.GetProperty("value").GetInt32());
            sequence = item.Root.GetProperty("sequence").GetInt32();
            harness.Send(connection, $$"""{"neoastra":1,"kind":"channel_ack","channel":"{{channel}}","sequence":{{sequence}}}""");
        }

        CollectionAssert.AreEqual(Enumerable.Range(9, 92).Take(rest.Count).ToArray(), rest);
        await harness.NextAsync(frame => frame.Kind == "channel_complete");
    }

    [TestMethod]
    public async Task Stream_StopsWhenTheScriptClosesTheChannel_AndWhenTheEndpointCloses()
    {
        var disposed = new TaskCompletionSource();
        await using var harness = new Harness(rpc => rpc.Stream<GetBoard, int>("forever", Forever));
        async IAsyncEnumerable<int> Forever(GetBoard request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                for (var index = 0; ; index++)
                {
                    yield return index;
                    await Task.Delay(5, cancellationToken);
                }
            }
            finally
            {
                disposed.TrySetResult();
            }
        }

        var connection = harness.Connect();
        harness.Send(connection, Invoke("s1", "forever", "{}"));
        var channel = (await harness.NextAsync(frame => frame.Id == "s1")).Root.GetProperty("value").GetProperty("channel").GetString()!;
        await harness.NextAsync(frame => frame.Kind == "channel_item");

        harness.Send(connection, $$"""{"neoastra":1,"kind":"channel_close","channel":"{{channel}}"}""");

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task Closing_MidStream_EndsTheSession_TellsThePage_AndRefusesLaterFrames()
    {
        var disposed = new TaskCompletionSource();
        var harness = new Harness(rpc => rpc.Stream<GetBoard, int>("forever", Forever));
        async IAsyncEnumerable<int> Forever(GetBoard request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                for (var index = 0; ; index++)
                {
                    yield return index;
                    await Task.Delay(5, cancellationToken);
                }
            }
            finally
            {
                disposed.TrySetResult();
            }
        }

        var connection = harness.Connect();
        harness.Send(connection, Invoke("s1", "forever", "{}"));
        await harness.NextAsync(frame => frame.Kind == "channel_item");

        await harness.Endpoint.DisposeAsync();

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(harness.Carrier.Closed.Contains((connection, "closed")));
        Assert.AreEqual("closed", harness.Endpoint.Receive(connection, [Invoke("late", "forever", "{}")]));
        Assert.IsNull(harness.Endpoint.Connect(1), "an endpoint that closed opens no session");
        await harness.DisposeAsync();
    }

    [TestMethod]
    public async Task Connecting_AgainEndsTheOldConnection_AndOnlyTheNewOneIsServed()
    {
        await using var harness = new Harness(rpc => rpc.Handle<GetBoard, Board>("board.get", (_, _) => new(new Board("t", 1))));
        var first = harness.Connect();
        var second = harness.Connect();

        Assert.AreNotEqual(first, second);
        Assert.AreEqual("closed", harness.Endpoint.Receive(first, [Invoke("r1", "board.get", "{}")]), "the old connection is refused");
        Assert.AreEqual("ok", harness.Endpoint.Receive(second, [Invoke("r2", "board.get", "{}")]));
        await harness.NextAsync(frame => frame.Id == "r2");
        SpinWait.SpinUntil(() => harness.Carrier.Closed.Contains((first, "replaced")), TimeSpan.FromSeconds(10));
        Assert.IsTrue(harness.Carrier.Closed.Contains((first, "replaced")));
        Assert.AreEqual("closed", harness.Endpoint.Receive("not-a-connection", [Invoke("r3", "board.get", "{}")]));
        Assert.IsTrue(harness.Endpoint.Close(second));
        Assert.IsFalse(harness.Endpoint.Close(second));
    }

    [TestMethod]
    public async Task DroppingSessions_EndsOnlyTheOnesOfWatchesThatAreGone()
    {
        await using var harness = new Harness(rpc => rpc.Handle<GetBoard, Board>("board.get", (_, _) => new(new Board("t", 1))));
        var old = harness.Endpoint.Connect(generation: 1)!;

        harness.Endpoint.DropSession(upToGeneration: 0);
        Assert.AreEqual(old, harness.Endpoint.CurrentConnection, "a session of a newer watch stays");
        harness.Endpoint.DropSession(upToGeneration: 1);
        Assert.IsNull(harness.Endpoint.CurrentConnection);
        SpinWait.SpinUntil(() => harness.Carrier.Closed.Contains((old, "page_gone")), TimeSpan.FromSeconds(10));
        Assert.IsTrue(harness.Carrier.Closed.Contains((old, "page_gone")));
    }

    [TestMethod]
    public async Task Event_IsSentToTheSubscription_AsANameAndAValue()
    {
        await using var harness = new Harness(rpc => rpc.Handle<GetBoard>("noop", (_, _) => ValueTask.CompletedTask));
        var connection = harness.Connect();

        // Before the script subscribed: nothing is kept.
        await harness.Registry.PublishAsync("board.changed", new Board("lost", 0));
        harness.Send(connection, $$"""{"neoastra":1,"kind":"subscribe","id":"sub1","event":"{{CanvasRpcEndpoint.EventsName}}"}""");
        await harness.NextAsync(frame => frame.Kind == "subscribed" && frame.Id == "sub1");

        await harness.Registry.PublishAsync("board.changed", new Board("kept", 3));

        var received = await harness.NextAsync(frame => frame.Kind == "event");
        var value = received.Root.GetProperty("value");
        Assert.AreEqual("board.changed", value.GetProperty("name").GetString());
        Assert.AreEqual("kept", value.GetProperty("value").GetProperty("title").GetString());
        Assert.IsFalse(harness.Has(frame => frame.Kind == "event"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await harness.Registry.PublishAsync("Bad Name", 1));
    }

    [TestMethod]
    public async Task Limits_AFrameAboveTheLimitIsRefused_AndAResultAboveTheLimitBecomesAnError()
    {
        await using var harness = new Harness(rpc => rpc.Handle<GetBoard, string>("big", (_, _) => new(new string('x', CanvasRpcEndpoint.MaximumOutboundFrameChars))));
        var connection = harness.Connect();

        var tooLarge = Invoke("r1", "big", "{\"project\":\"" + new string('a', CanvasRpcEndpoint.MaximumFrameBytes) + "\"}");
        Assert.AreEqual("payload_too_large", harness.Endpoint.Receive(connection, [tooLarge, Invoke("r2", "big", "{}")]), "the other frames of the batch are still given");

        var result = await harness.NextAsync(frame => frame.Id == "r2");
        Assert.IsFalse(result.Root.GetProperty("ok").GetBoolean());
        Assert.AreEqual("payload_too_large", result.Root.GetProperty("error").GetProperty("code").GetString());
        Assert.IsFalse(harness.Has(frame => frame.Id == "r1"), "an oversized request gets no answer from the host: the page answers it");
    }

    [TestMethod]
    public async Task Limits_AFloodOfCallsIsRefusedWithTooManyRequests()
    {
        await using var harness = new Harness(rpc => rpc.Handle<GetBoard, Board>("board.get", (_, _) => new(new Board("t", 1))));
        var connection = harness.Connect();

        var frames = Enumerable.Range(0, 120).Select(index => Invoke("f" + index, "board.get", "{}")).ToArray();
        for (var round = 0; round < 5; round++) harness.Endpoint.Receive(connection, [.. frames.Select(frame => frame.Replace("\"id\":\"f", "\"id\":\"" + round + "f", StringComparison.Ordinal))]);

        var codes = new List<string?>();
        while (codes.Count < 600)
        {
            var result = await harness.NextAsync(frame => frame.Kind == "result");
            codes.Add(result.Root.GetProperty("ok").GetBoolean() ? null : result.Root.GetProperty("error").GetProperty("code").GetString());
        }

        Assert.IsTrue(codes.Count(static code => code == "too_many_requests") > 100, "the burst of 400 is spent");
        Assert.IsTrue(codes.Count(static code => code is null) > 0, "the calls that fit were served");
    }

    [TestMethod]
    public async Task Limits_AScriptThatKeepsBeingRefusedLosesItsSession_AndThePageIsTold()
    {
        await using var harness = new Harness(rpc => rpc.Handle<GetBoard, Board>("board.get", (_, _) => new(new Board("t", 1))));
        var connection = harness.Connect();

        for (var round = 0; round < 30 && harness.Endpoint.CurrentConnection is not null; round++)
            harness.Endpoint.Receive(connection, [.. Enumerable.Range(0, 100).Select(index => Invoke($"{round}-{index}", "board.get", "{}"))]);

        SpinWait.SpinUntil(() => harness.Carrier.Closed.Contains((connection, "session_closed")), TimeSpan.FromSeconds(30));
        Assert.IsTrue(harness.Carrier.Closed.Contains((connection, "session_closed")), "NeoAstra closed the session by itself, and the page must hear it");
        Assert.IsNull(harness.Endpoint.CurrentConnection);
        Assert.AreEqual("closed", harness.Endpoint.Receive(connection, [Invoke("late", "board.get", "{}")]));
        var next = harness.Connect();
        harness.Send(next, Invoke("again", "board.get", "{}"));
        Assert.IsTrue((await harness.NextAsync(frame => frame.Id == "again")).Root.GetProperty("ok").GetBoolean(), "the next connection is served");
    }

    [TestMethod]
    public async Task APageThatIsGone_EndsTheSession()
    {
        await using var harness = new Harness(rpc => rpc.Handle<GetBoard, Board>("board.get", (_, _) => new(new Board("t", 1))));
        var connection = harness.Connect();
        harness.Carrier.Gone = true;

        harness.Send(connection, Invoke("r1", "board.get", "{}"));

        SpinWait.SpinUntil(() => harness.Carrier.Failures > 0, TimeSpan.FromSeconds(10));
        Assert.IsTrue(harness.Carrier.Failures > 0, "the frame could not be delivered");
        SpinWait.SpinUntil(() => harness.Carrier.Closed.Contains((connection, "session_closed")), TimeSpan.FromSeconds(10));
        Assert.IsTrue(harness.Carrier.Closed.Contains((connection, "session_closed")));
    }

    [TestMethod]
    public async Task TheCanvasClosing_CancelsTheTokensOfTheHandlers()
    {
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        var harness = new Harness(rpc => rpc.Handle<GetBoard, Board>("slow", async (_, cancellationToken) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            return null!;
        }));
        var connection = harness.Connect();
        harness.Send(connection, Invoke("c1", "slow", "{}"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        harness.CloseCanvas();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.DisposeAsync();
    }

    [TestMethod]
    public void Registry_ValidatesNamesRejectsDuplicates_AndSealsAtTheEndOfOpen()
    {
        using var closed = new CancellationTokenSource();
        var registry = new PluginRpcRegistry("plugin", "board", null, closed.Token);
        registry.Handle<GetBoard, Board>("board.get", (_, _) => new(new Board("t", 1)));

        Assert.ThrowsExactly<ArgumentException>(() => registry.Handle<GetBoard>("board.get", (_, _) => ValueTask.CompletedTask));
        Assert.ThrowsExactly<ArgumentException>(() => registry.Stream<GetBoard, int>("board.get", static (_, _) => AsyncEnumerable.Empty<int>()));
        foreach (var bad in new[] { "", "Board", "a b", "-a", ".a", "a:b", "a/b", new string('a', 65), "é" })
            Assert.ThrowsExactly<ArgumentException>(() => registry.Handle<GetBoard>(bad, (_, _) => ValueTask.CompletedTask), bad);
        Assert.ThrowsExactly<ArgumentNullException>(() => registry.Handle<GetBoard>(null!, (_, _) => ValueTask.CompletedTask));
        Assert.ThrowsExactly<ArgumentNullException>(() => registry.Handle<GetBoard, Board>("other", null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => registry.JsonOptions = null!);
        Assert.AreEqual(1, registry.Count);

        registry.Seal();

        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Handle<GetBoard>("late", (_, _) => ValueTask.CompletedTask));
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.JsonOptions = new JsonSerializerOptions());
        Assert.IsTrue(registry.IsAvailable);
    }

    [TestMethod]
    public void Registry_AcceptsAtMost256Registrations()
    {
        using var closed = new CancellationTokenSource();
        var registry = new PluginRpcRegistry("plugin", "board", null, closed.Token);
        for (var index = 0; index < PluginRpcRegistry.MaximumRegistrations; index++) registry.Handle<GetBoard>("call" + index, (_, _) => ValueTask.CompletedTask);

        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Handle<GetBoard>("one-more", (_, _) => ValueTask.CompletedTask));
    }

    [TestMethod]
    public async Task Registry_UsesTheOptionsOfThePlugin()
    {
        await using var harness = new Harness(rpc =>
        {
            rpc.JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            rpc.Handle<JsonElement, Board>("snake", (request, _) => new(new Board(request.GetProperty("board_name").GetString()!, 1)));
        });
        var connection = harness.Connect();

        harness.Send(connection, Invoke("r1", "snake", """{"board_name":"b"}"""));

        var result = await harness.NextAsync(frame => frame.Id == "r1");
        Assert.AreEqual("b", result.Root.GetProperty("value").GetProperty("title").GetString());
    }

    [TestMethod]
    public void RpcException_ChecksItsCode_AndTheNoopRegistryAcceptsNothing()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PluginRpcException("Not Found", "m"));
        Assert.ThrowsExactly<ArgumentException>(() => new PluginRpcException("not_found", " "));
        Assert.ThrowsExactly<ArgumentException>(() => new PluginRpcException("1a", "m"));
        var error = new PluginRpcException("busy", "Try again.", retryable: true);
        Assert.AreEqual(("busy", true), (error.Code, error.Retryable));

        var noop = new NoopPluginCanvasRpc();
        Assert.IsFalse(noop.IsAvailable);
        noop.Handle<GetBoard>("a", (_, _) => ValueTask.CompletedTask);
        Assert.ThrowsExactly<ArgumentException>(() => noop.Handle<GetBoard>("A", (_, _) => ValueTask.CompletedTask));
        Assert.IsTrue(noop.PublishAsync("a", 1).IsCompletedSuccessfully);
        Assert.IsTrue(PluginRpc.IsValidName("a.b-c_d9"));
        Assert.IsFalse(PluginRpc.IsValidName("_a"));
        Assert.AreEqual("Short.", PluginRpcRegistry.Safe("Short.\r\n"));
        Assert.AreEqual(400, PluginRpcRegistry.Safe(new string('x', 900)).Length);
        Assert.AreEqual("The call failed.", PluginRpcRegistry.Safe("\n\n"));
    }

    private static string Invoke(string id, string command, string args)
        => $$"""{"neoastra":1,"kind":"invoke","id":"{{id}}","command":"{{command}}","args":{{args}}}""";

    private sealed class Frame(string json)
    {
        public string Json { get; } = json;

        public JsonDocument Document { get; } = JsonDocument.Parse(json);

        public JsonElement Root => Document.RootElement;

        public string Kind => Root.GetProperty("kind").GetString()!;

        public string? Id => Root.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    private sealed class FakeCarrier : ICanvasRpcCarrier
    {
        private readonly Channel<Frame> _frames = Channel.CreateUnbounded<Frame>();

        public bool Gone { get; set; }

        public int Failures;

        public System.Collections.Concurrent.ConcurrentBag<(string Connection, string Reason)> Closed { get; } = [];

        public Channel<Frame> Frames => _frames;

        ValueTask ICanvasRpcCarrier.SendAsync(string instanceId, string connection, string frame, CancellationToken cancellationToken)
        {
            if (Gone)
            {
                Interlocked.Increment(ref Failures);
                return ValueTask.FromException(new IOException("gone"));
            }

            _frames.Writer.TryWrite(new Frame(frame));
            return ValueTask.CompletedTask;
        }

        void ICanvasRpcCarrier.Closed(string instanceId, string connection, string reason) => Closed.Add((connection, reason));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _canvasClosed = new();
        private readonly List<Frame> _kept = [];

        public Harness(Action<IPluginCanvasRpc> register)
        {
            Registry = new PluginRpcRegistry("plugin", "board", null, _canvasClosed.Token);
            register(Registry);
            Carrier = new FakeCarrier();
            Endpoint = new CanvasRpcEndpoint("instance1", Registry, Carrier);
        }

        public PluginRpcRegistry Registry { get; }

        public FakeCarrier Carrier { get; }

        public CanvasRpcEndpoint Endpoint { get; }

        public string Connect() => Endpoint.Connect(1) ?? throw new InvalidOperationException("no connection");

        public void Send(string connection, string frame) => Assert.AreEqual("ok", Endpoint.Receive(connection, [frame]));

        public void CloseCanvas() => _canvasClosed.Cancel();

        public async Task<Frame> NextAsync(Func<Frame, bool> wanted)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            for (var index = 0; index < _kept.Count; index++)
            {
                if (!wanted(_kept[index])) continue;
                var found = _kept[index];
                _kept.RemoveAt(index);
                return found;
            }

            while (true)
            {
                var frame = await Carrier.Frames.Reader.ReadAsync(timeout.Token);
                if (wanted(frame)) return frame;
                _kept.Add(frame);
            }
        }

        public bool Has(Func<Frame, bool> wanted)
        {
            Thread.Sleep(100);
            while (Carrier.Frames.Reader.TryRead(out var frame)) _kept.Add(frame);
            return _kept.Any(wanted);
        }

        public async ValueTask DisposeAsync()
        {
            await Endpoint.DisposeAsync();
            _canvasClosed.Dispose();
        }
    }
}
