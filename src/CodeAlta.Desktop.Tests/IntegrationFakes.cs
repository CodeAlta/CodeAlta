using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace CodeAlta.Desktop.Tests;

/// <summary>A request a fake tool received.</summary>
internal sealed record FakeToolRequest(HttpMethod Method, Uri Uri, string? Authorization, string? ContentType, string Body);

/// <summary>
/// An in-process fake of the APIs of Jira, Slack and incident.io: each route answers JSON, every request is recorded.
/// Nothing leaves the process.
/// </summary>
internal sealed class FakeToolServer : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path, Func<FakeToolRequest, (HttpStatusCode Status, string Json)> Answer)> _routes = [];

    /// <summary>The requests, in order.</summary>
    internal ConcurrentQueue<FakeToolRequest> Requests { get; } = new();

    /// <summary>Answers a method and a path (without its query).</summary>
    internal FakeToolServer On(HttpMethod method, string path, Func<FakeToolRequest, (HttpStatusCode, string)> answer)
    {
        lock (_routes) _routes.Add((method, path, answer));
        return this;
    }

    /// <summary>Answers a method and a path with the same JSON each time.</summary>
    internal FakeToolServer On(HttpMethod method, string path, string json, HttpStatusCode status = HttpStatusCode.OK) => On(method, path, _ => (status, json));

    /// <summary>The requests to a path.</summary>
    internal FakeToolRequest[] To(string path) => [.. Requests.Where(request => request.Uri.AbsolutePath == path)];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var received = new FakeToolRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType, body);
        Requests.Enqueue(received);
        Func<FakeToolRequest, (HttpStatusCode, string)>? answer;
        lock (_routes) answer = _routes.LastOrDefault(route => route.Method == request.Method && route.Path == request.RequestUri!.AbsolutePath).Answer;
        var (status, json) = answer?.Invoke(received) ?? (HttpStatusCode.NotFound, """{"message":"no route"}""");
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// The Socket Mode end of a fake Slack: a WebSocket over a loopback TCP connection, with the framing of the protocol and
/// no HTTP handshake. Each connection the listener opens is given to <see cref="Accepted"/>.
/// </summary>
internal sealed class FakeSlackSockets : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<IDisposable> _owned = [];

    internal FakeSlackSockets() => _listener.Start();

    /// <summary>The server ends of the connections, in order.</summary>
    internal System.Threading.Channels.Channel<WebSocket> Accepted { get; } = System.Threading.Channels.Channel.CreateUnbounded<WebSocket>();

    /// <summary>The addresses the listener connected to.</summary>
    internal ConcurrentQueue<Uri> Urls { get; } = new();

    /// <summary>Connects as the listener does: the client end, once the server end is accepted.</summary>
    internal async Task<WebSocket> ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        Urls.Enqueue(url);
        var client = new TcpClient();
        var accept = _listener.AcceptTcpClientAsync(cancellationToken).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port, cancellationToken);
        var server = await accept;
        lock (_owned) _owned.AddRange([client, server]);
        await Accepted.Writer.WriteAsync(WebSocket.CreateFromStream(server.GetStream(), isServer: true, null, Timeout.InfiniteTimeSpan), cancellationToken);
        return WebSocket.CreateFromStream(client.GetStream(), isServer: false, null, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Sends a frame of text from the server end.</summary>
    internal static Task SendAsync(WebSocket socket, string json) => socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Reads a frame of text at the server end.</summary>
    internal static async Task<string> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        lock (_owned) foreach (var owned in _owned) owned.Dispose();
        return ValueTask.CompletedTask;
    }
}
