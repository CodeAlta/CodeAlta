using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Harness;

/// <summary>
/// Serves the MCP server of the tools of a session to an agent harness that connects to it by itself, such as an ACP
/// agent given the server in <c>session/new</c>.
/// </summary>
public interface IHarnessToolEndpoints
{
    /// <summary>
    /// Opens an endpoint for a session: a URL and the headers that reach <paramref name="server" />, until the endpoint is disposed.
    /// </summary>
    /// <param name="server">The MCP server of the session.</param>
    /// <param name="cancellationToken">A token to cancel the opening.</param>
    /// <returns>The endpoint.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="server" /> is <see langword="null" />.</exception>
    ValueTask<HarnessToolEndpoint> OpenAsync(HarnessMcpToolServer server, CancellationToken cancellationToken = default);
}

/// <summary>
/// An endpoint of the MCP server of a session: the URL and the headers a harness calls it with.
/// </summary>
public sealed class HarnessToolEndpoint : IAsyncDisposable
{
    private readonly Func<ValueTask> _close;
    private readonly Func<JsonElement, CancellationToken, ValueTask<bool>>? _notify;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="HarnessToolEndpoint" /> class, for a transport that sends the
    /// harness no message of the server.
    /// </summary>
    /// <param name="url">The URL of the endpoint.</param>
    /// <param name="headers">The headers each request carries.</param>
    /// <param name="close">Closes the endpoint.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null" />.</exception>
    public HarnessToolEndpoint(Uri url, IReadOnlyList<KeyValuePair<string, string>> headers, Func<ValueTask> close)
    {
        Url = url ?? throw new ArgumentNullException(nameof(url));
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
        _close = close ?? throw new ArgumentNullException(nameof(close));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HarnessToolEndpoint" /> class, for a transport that sends the
    /// harness the messages of the server, such as <c>notifications/tools/list_changed</c>.
    /// </summary>
    /// <param name="url">The URL of the endpoint.</param>
    /// <param name="headers">The headers each request carries.</param>
    /// <param name="close">Closes the endpoint.</param>
    /// <param name="notify">Sends a message to the harness; returns whether a harness received it.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null" />.</exception>
    public HarnessToolEndpoint(
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        Func<ValueTask> close,
        Func<JsonElement, CancellationToken, ValueTask<bool>> notify)
        : this(url, headers, close)
    {
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
    }

    /// <summary>
    /// Sends a message of the server to the harness, such as <see cref="HarnessMcpToolServer.CreateToolsChangedNotification" />.
    /// </summary>
    /// <param name="message">The JSON-RPC message, a notification.</param>
    /// <param name="cancellationToken">A token to cancel the sending.</param>
    /// <returns>
    /// Whether a harness received it, or receives it with the response of a request it waits for; false when it opened
    /// no stream for the messages of the server and waits for no response, or when the endpoint is closed.
    /// </returns>
    public ValueTask<bool> NotifyAsync(JsonElement message, CancellationToken cancellationToken = default)
        => _notify is null || Volatile.Read(ref _disposed) != 0 ? ValueTask.FromResult(false) : _notify(message, cancellationToken);

    /// <summary>Gets the URL of the endpoint.</summary>
    public Uri Url { get; }

    /// <summary>Gets the headers each request carries: the secret of the endpoint.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    /// <summary>Closes the endpoint: a request to it is refused from then on.</summary>
    /// <returns>A task that ends once it is closed.</returns>
    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _disposed, 1) == 0 ? _close() : ValueTask.CompletedTask;
}

/// <summary>
/// Serves the MCP servers of sessions over HTTP on the loopback interface, as the Streamable HTTP transport of MCP
/// answers a client that posts JSON-RPC messages: each response is one JSON body. A client that opens a stream of
/// events (<c>GET</c> with <c>Accept: text/event-stream</c>) is sent the messages of the server on it
/// (<see cref="HarnessToolEndpoint.NotifyAsync" />). One listener serves every endpoint; each has a path and a bearer
/// secret of its own, random, so that another local process does not reach it.
/// </summary>
public sealed class LoopbackHarnessToolEndpoints : IHarnessToolEndpoints, IAsyncDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Registration> _endpoints = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Logger? _logger;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private long _nextStreamId;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoopbackHarnessToolEndpoints" /> class. Nothing listens until the first endpoint opens.
    /// </summary>
    /// <param name="logger">The logger of the requests that fail, or <see langword="null" />.</param>
    public LoopbackHarnessToolEndpoints(Logger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Gets the port the listener listens on, or zero before the first endpoint opens.</summary>
    public int Port => _listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0;

    /// <inheritdoc />
    public async ValueTask<HarnessToolEndpoint> OpenAsync(HarnessMcpToolServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await EnsureListeningAsync(cancellationToken).ConfigureAwait(false);

        var path = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var registration = new Registration(server, Encoding.ASCII.GetBytes("Bearer " + secret), new CancellationTokenSource());
        _endpoints[path] = registration;
        var url = new Uri($"http://127.0.0.1:{Port}/mcp/{path}");
        return new HarnessToolEndpoint(
            url,
            [new KeyValuePair<string, string>("Authorization", "Bearer " + secret)],
            () =>
            {
                if (_endpoints.TryRemove(path, out var removed))
                {
                    // The calls and the streams of events end with it.
                    removed.Calls.Cancel();
                    removed.Calls.Dispose();
                }

                return ValueTask.CompletedTask;
            },
            (message, cancellationToken) => SendEventAsync(registration, message, cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        if (_acceptLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        foreach (var registration in _endpoints.Values)
        {
            registration.Calls.Cancel();
            registration.Calls.Dispose();
        }

        _endpoints.Clear();
        _lifetime.Dispose();
        _startGate.Dispose();
    }

    private async Task EnsureListeningAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null)
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_listener is not null)
            {
                return;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _listener = listener;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _lifetime.Token));
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = ServeConnectionAsync(client, cancellationToken);
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        client.NoDelay = true;
        try
        {
            var stream = client.GetStream();
            var reader = new HttpRequestReader(stream);
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpRequest? request;
                using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    headerTimeout.CancelAfter(HeaderTimeout);
                    request = await reader.ReadAsync(headerTimeout.Token).ConfigureAwait(false);
                }

                if (request is null)
                {
                    return;
                }

                var (status, registration) = Authorize(request);
                byte[]? body = null;
                var contentType = "application/json";
                if (registration is not null)
                {
                    if (string.Equals(request.Method, "POST", StringComparison.Ordinal))
                    {
                        (status, body, contentType) = await HandleAsync(registration, request, cancellationToken).ConfigureAwait(false);
                    }
                    else if (string.Equals(request.Method, "GET", StringComparison.Ordinal) && AcceptsEventStream(request))
                    {
                        // The connection is the stream of the messages of the server from then on.
                        await ServeEventStreamAsync(stream, registration, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        status = 405;
                    }
                }

                await WriteResponseAsync(stream, status, body, contentType, request.KeepAlive, cancellationToken).ConfigureAwait(false);
                if (!request.KeepAlive)
                {
                    return;
                }
            }
        }
        catch (HttpFormatException ex)
        {
            _logger?.Debug($"A request to the MCP endpoint of a session was refused: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client closed the connection, or the listener stops.
        }
    }

    // The endpoint a request is for, when it carries its secret; else the status that refuses it.
    private (int Status, Registration? Registration) Authorize(HttpRequest request)
    {
        const string Prefix = "/mcp/";
        var path = request.Path;
        var query = path.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            path = path[..query];
        }

        if (!path.StartsWith(Prefix, StringComparison.Ordinal) ||
            !_endpoints.TryGetValue(path[Prefix.Length..].TrimEnd('/'), out var registration))
        {
            return (404, null);
        }

        var authorization = request.Headers.GetValueOrDefault("authorization") ?? string.Empty;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(authorization), registration.Authorization)
            ? (200, registration)
            : (401, null);
    }

    private static bool AcceptsEventStream(HttpRequest request)
        => request.Headers.GetValueOrDefault("accept")?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<(int Status, byte[]? Body, string ContentType)> HandleAsync(Registration registration, HttpRequest request, CancellationToken cancellationToken)
    {
        const string Json = "application/json";
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (400, Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}"""), Json);
        }

        CancellationToken calls;
        try
        {
            calls = registration.Calls.Token;
        }
        catch (ObjectDisposedException)
        {
            return (404, null, Json);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, calls);
        var responses = new List<JsonElement>();
        Interlocked.Increment(ref registration.InFlight);
        try
        {
            foreach (var item in message.ValueKind == JsonValueKind.Array ? message.EnumerateArray().ToArray() : [message])
            {
                if (await registration.Server.HandleAsync(item, linked.Token).ConfigureAwait(false) is { } response)
                {
                    responses.Add(response);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref registration.InFlight);
        }

        if (responses.Count == 0)
        {
            return (202, null, Json);
        }

        // The messages the server had for a harness that opened no stream of events go before the response, in a
        // stream of events, for a harness that takes one: what a tool call changed reaches it with its result.
        if (AcceptsEventStream(request) && registration.TakePending() is { Count: > 0 } pending)
        {
            var events = new StringBuilder();
            foreach (var json in pending)
            {
                events.Append("event: message\ndata: ").Append(json).Append("\n\n");
            }

            foreach (var response in responses)
            {
                events.Append("event: message\ndata: ").Append(JsonSerializer.Serialize(response, HarnessJsonContext.Default.JsonElement)).Append("\n\n");
            }

            return (200, Encoding.UTF8.GetBytes(events.ToString()), "text/event-stream");
        }

        return message.ValueKind == JsonValueKind.Array
            ? (200, JsonSerializer.SerializeToUtf8Bytes(responses, HarnessJsonContext.Default.ListJsonElement), Json)
            : (200, JsonSerializer.SerializeToUtf8Bytes(responses[0], HarnessJsonContext.Default.JsonElement), Json);
    }

    private static async Task WriteResponseAsync(Stream stream, int status, byte[]? body, bool keepAlive, CancellationToken cancellationToken)
        => await WriteResponseAsync(stream, status, body, "application/json", keepAlive, cancellationToken).ConfigureAwait(false);

    private static async Task WriteResponseAsync(Stream stream, int status, byte[]? body, string contentType, bool keepAlive, CancellationToken cancellationToken)
    {
        var reason = status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error",
        };
        var header = new StringBuilder();
        header.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        if (body is not null)
        {
            header.Append("Content-Type: ").Append(contentType).Append("\r\n");
        }

        if (status == 405)
        {
            header.Append("Allow: GET, POST\r\n");
        }

        header.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n");
        header.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
        header.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), cancellationToken).ConfigureAwait(false);
        if (body is not null)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // Keeps the connection of a harness as a stream of events, chunked, until the harness or the endpoint closes it.
    private async Task ServeEventStreamAsync(Stream stream, Registration registration, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, registration.Calls.Token);
        }
        catch (ObjectDisposedException)
        {
            // Closed meanwhile.
            await WriteResponseAsync(stream, 404, null, keepAlive: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        using (linked)
        {
            var events = new EventStream(stream);
            await stream.WriteAsync(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nTransfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n"u8.ToArray(),
                    linked.Token)
                .ConfigureAwait(false);
            await stream.FlushAsync(linked.Token).ConfigureAwait(false);
            var id = Interlocked.Increment(ref _nextStreamId);
            registration.Streams[id] = events;
            try
            {
                // The harness sends nothing more on it: a read ends when it closes the connection.
                var buffer = new byte[256];
                while (await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false) > 0)
                {
                }
            }
            catch (OperationCanceledException) when (registration.Calls.IsCancellationRequested)
            {
                // The endpoint closed: the stream ends with its last chunk.
                await events.EndAsync().ConfigureAwait(false);
            }
            finally
            {
                registration.Streams.TryRemove(id, out _);
            }
        }
    }

    // Sends a message on the streams the harness opened; without one, keeps it for the response of a request the
    // harness is waiting for.
    private static async ValueTask<bool> SendEventAsync(Registration registration, JsonElement message, CancellationToken cancellationToken)
    {
        if (registration.Calls.IsCancellationRequested)
        {
            return false;
        }

        var json = JsonSerializer.Serialize(message, HarnessJsonContext.Default.JsonElement);
        if (registration.Streams.IsEmpty)
        {
            return Volatile.Read(ref registration.InFlight) > 0 && registration.AddPending(json);
        }

        var payload = Encoding.UTF8.GetBytes($"event: message\ndata: {json}\n\n");
        var sent = false;
        foreach (var (id, events) in registration.Streams)
        {
            try
            {
                await events.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                sent = true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                registration.Streams.TryRemove(id, out _);
            }
        }

        return sent;
    }

    private sealed record Registration(HarnessMcpToolServer Server, byte[] Authorization, CancellationTokenSource Calls)
    {
        private const int MaxPending = 16;
        private readonly Lock _gate = new();
        private List<string>? _pending;

        // The requests of the harness being answered.
        public int InFlight;

        public ConcurrentDictionary<long, EventStream> Streams { get; } = new();

        // Keeps a message for the next response, once: the same notification twice says nothing more.
        public bool AddPending(string json)
        {
            lock (_gate)
            {
                _pending ??= [];
                if (!_pending.Contains(json, StringComparer.Ordinal) && _pending.Count < MaxPending)
                {
                    _pending.Add(json);
                }

                return true;
            }
        }

        public List<string>? TakePending()
        {
            lock (_gate)
            {
                var pending = _pending;
                _pending = null;
                return pending;
            }
        }
    }

    // A stream of events of one harness: its chunks are written one at a time.
    private sealed class EventStream(Stream stream)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _ended;

        public async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_ended, this);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{payload.Length:x}\r\n"), cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task EndAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_ended)
                {
                    _ended = true;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                    await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body, bool KeepAlive);

    private sealed class HttpFormatException(string message) : Exception(message);

    // Reads the HTTP/1.1 requests of one connection, one after the other.
    private sealed class HttpRequestReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        public async Task<HttpRequest?> ReadAsync(CancellationToken cancellationToken)
        {
            var headerBytes = await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            if (headerBytes is null)
            {
                return null;
            }

            var lines = Encoding.ASCII.GetString(headerBytes).Split("\r\n");
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                throw new HttpFormatException("The request line is not one of HTTP/1.1.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.Length == 0)
                {
                    continue;
                }

                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    throw new HttpFormatException("A header has no name.");
                }

                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            byte[] body;
            if (headers.TryGetValue("transfer-encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                body = await ReadChunkedAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (headers.TryGetValue("content-length", out var lengthText))
            {
                if (!int.TryParse(lengthText, out var length) || length < 0 || length > MaxBodyBytes)
                {
                    throw new HttpFormatException("The body is too large or its length is invalid.");
                }

                body = await ReadExactAsync(length, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                body = [];
            }

            var keepAlive = !requestLine[2].Equals("HTTP/1.0", StringComparison.Ordinal) &&
                            !(headers.TryGetValue("connection", out var connection) && connection.Contains("close", StringComparison.OrdinalIgnoreCase));
            return new HttpRequest(requestLine[0], requestLine[1], headers, body, keepAlive);
        }

        private async Task<byte[]?> ReadHeaderAsync(CancellationToken cancellationToken)
        {
            var header = new ArrayBufferWriter<byte>();
            while (true)
            {
                var available = _buffer.AsSpan(_start, _end - _start);
                var end = available.IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    header.Write(available[..end]);
                    _start += end + 4;
                    return header.WrittenSpan.ToArray();
                }

                // Keep the last three bytes: the end of the header can straddle two reads.
                var keep = Math.Min(3, available.Length);
                header.Write(available[..^keep]);
                _start = _end - keep;
                if (header.WrittenCount > MaxHeaderBytes)
                {
                    throw new HttpFormatException("The header is too large.");
                }

                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (header.WrittenCount == 0 && _start == _end)
                    {
                        return null;
                    }

                    throw new HttpFormatException("The connection closed in the middle of a header.");
                }
            }
        }

        private async Task<byte[]> ReadExactAsync(int length, CancellationToken cancellationToken)
        {
            var body = new byte[length];
            var written = 0;
            while (written < length)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new HttpFormatException("The connection closed in the middle of a body.");
                }

                var count = Math.Min(length - written, _end - _start);
                _buffer.AsSpan(_start, count).CopyTo(body.AsSpan(written));
                _start += count;
                written += count;
            }

            return body;
        }

        private async Task<byte[]> ReadChunkedAsync(CancellationToken cancellationToken)
        {
            var body = new ArrayBufferWriter<byte>();
            while (true)
            {
                var sizeLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                var extension = sizeLine.IndexOf(';');
                var sizeText = extension >= 0 ? sizeLine[..extension] : sizeLine;
                if (!int.TryParse(sizeText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0)
                {
                    throw new HttpFormatException("A chunk has an invalid size.");
                }

                if (size == 0)
                {
                    // Trailers, then the empty line.
                    while ((await ReadLineAsync(cancellationToken).ConfigureAwait(false)).Length > 0)
                    {
                    }

                    return body.WrittenSpan.ToArray();
                }

                if (body.WrittenCount + size > MaxBodyBytes)
                {
                    throw new HttpFormatException("The body is too large.");
                }

                body.Write(await ReadExactAsync(size, cancellationToken).ConfigureAwait(false));
                if ((await ReadLineAsync(cancellationToken).ConfigureAwait(false)).Length != 0)
                {
                    throw new HttpFormatException("A chunk does not end where its size says.");
                }
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new ArrayBufferWriter<byte>();
            while (true)
            {
                var available = _buffer.AsSpan(_start, _end - _start);
                var end = available.IndexOf("\r\n"u8);
                if (end >= 0)
                {
                    line.Write(available[..end]);
                    _start += end + 2;
                    return Encoding.ASCII.GetString(line.WrittenSpan);
                }

                var keep = Math.Min(1, available.Length);
                line.Write(available[..^keep]);
                _start = _end - keep;
                if (line.WrittenCount > MaxHeaderBytes)
                {
                    throw new HttpFormatException("A line is too long.");
                }

                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new HttpFormatException("The connection closed in the middle of a line.");
                }
            }
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            if (_start > 0)
            {
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                throw new HttpFormatException("A line is too long.");
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            _end += read;
            return true;
        }
    }
}
