using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodeAlta.Browser.Cdp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Browser.Tests;

/// <summary>The CDP connection against a fake endpoint that answers as a browser would.</summary>
[TestClass]
public sealed class CdpConnectionTests
{
    /// <summary>A WebSocket endpoint whose behavior the test writes: it gets each message and the socket.</summary>
    private sealed class FakeEndpoint : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeEndpoint(WebApplication app, Uri address)
        {
            _app = app;
            Address = address;
        }

        public Uri Address { get; }

        public static async Task<FakeEndpoint> StartAsync(Func<JsonElement, Func<string, Task>, Task> onMessage)
        {
            var port = Launch.LaunchHelpers.FreePort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            var app = builder.Build();
            app.UseWebSockets();
            app.Map("/devtools/browser/test", async (HttpContext context) =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var send = new SemaphoreSlim(1, 1);
                async Task Send(string text)
                {
                    await send.WaitAsync();
                    try { await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
                    finally { send.Release(); }
                }

                var buffer = new byte[1 << 16];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                    if (document.RootElement.GetProperty("method").GetString() == "Test.close")
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        break;
                    }

                    await onMessage(document.RootElement.Clone(), Send);
                }
            });
            await app.StartAsync();
            return new FakeEndpoint(app, new Uri($"ws://127.0.0.1:{port}/devtools/browser/test"));
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    [TestMethod]
    public async Task EventsReachTheirSessionInOrderBeforeTheAnswerThatFollowsThem()
    {
        await using var endpoint = await FakeEndpoint.StartAsync(async (message, send) =>
        {
            var id = message.GetProperty("id").GetInt64();
            // Many events, then the answer: a handler must see each of them, in order.
            for (var index = 0; index < 200; index++) await send($$"""{"method":"Page.frameStartedLoading","params":{"frameId":"{{index}}"},"sessionId":"s1"}""");
            await send($$"""{"id":{{id}},"result":{},"sessionId":"s1"}""");
        });
        await using var connection = await CdpConnection.ConnectAsync(endpoint.Address, CancellationToken.None);
        var session = connection.Session("s1");
        var seen = new List<string>();
        using var subscription = session.Page.OnFrameStartedLoading(item => { lock (seen) seen.Add(item.FrameId); });
        await session.Page.EnableAsync(cancellationToken: CancellationToken.None);
        await session.FlushAsync(CancellationToken.None);
        lock (seen) CollectionAssert.AreEqual(Enumerable.Range(0, 200).Select(static index => index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(), seen);
    }

    [TestMethod]
    public async Task AnErrorOfTheBrowserFailsItsCommand()
    {
        await using var endpoint = await FakeEndpoint.StartAsync((message, send)
            => send($$$"""{"id":{{{message.GetProperty("id").GetInt64()}}},"error":{"code":-32000,"message":"Cannot navigate to invalid URL"}}"""));
        await using var connection = await CdpConnection.ConnectAsync(endpoint.Address, CancellationToken.None);
        var failure = await Assert.ThrowsExactlyAsync<CdpException>(() => connection.Browser.Page.NavigateAsync(new PageNavigateParams { Url = "x" }));
        Assert.AreEqual(-32000, failure.Code);
        StringAssert.Contains(failure.Message, "Cannot navigate to invalid URL");
    }

    [TestMethod]
    public async Task ADetachedSessionFailsItsPendingCommands()
    {
        var detach = new TaskCompletionSource();
        await using var endpoint = await FakeEndpoint.StartAsync(async (message, send) =>
        {
            // The command of the session is never answered: the target detaches instead.
            if (message.TryGetProperty("sessionId", out _)) await send("""{"method":"Target.detachedFromTarget","params":{"sessionId":"s2"}}""");
        });
        await using var connection = await CdpConnection.ConnectAsync(endpoint.Address, CancellationToken.None);
        var session = connection.Session("s2");
        var failure = await Assert.ThrowsExactlyAsync<CdpException>(() => session.Page.EnableAsync(cancellationToken: CancellationToken.None));
        StringAssert.Contains(failure.Message, "target was closed");
        await session.Completed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ASocketThatClosesFailsWhatWaits()
    {
        await using var endpoint = await FakeEndpoint.StartAsync((message, send) => Task.CompletedTask);
        await using var connection = await CdpConnection.ConnectAsync(endpoint.Address, CancellationToken.None);
        var pending = connection.Browser.Page.EnableAsync(cancellationToken: CancellationToken.None);
        // The endpoint closes on this command, with the other one still waiting.
        _ = connection.SendAsync(null, "Test.close", new PageEnableParams(), CdpJsonContext.Default.PageEnableParams, CancellationToken.None);
        var failure = await Assert.ThrowsExactlyAsync<CdpException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        StringAssert.Contains(failure.Message, "closed");
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(connection.IsClosed);
    }
}
