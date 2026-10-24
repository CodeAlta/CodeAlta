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
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="HarnessToolEndpoint" /> class.
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
/// answers a client that posts JSON-RPC messages: each response is one JSON body. One listener serves every endpoint;
/// each has a path and a bearer secret of its own, random, so that another local process does not reach it.
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
                    removed.Calls.Cancel();
                    removed.Calls.Dispose();
                }

                return ValueTask.CompletedTask;
            });
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

                var (status, body) = await HandleAsync(request, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(stream, status, body, request.KeepAlive, cancellationToken).ConfigureAwait(false);
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

    private async Task<(int Status, byte[]? Body)> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
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
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(authorization), registration.Authorization))
        {
            return (401, null);
        }

        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
        {
            // No stream of server messages: a client that asks for one goes on with posts only.
            return (405, null);
        }

        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (400, Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}"""));
        }

        CancellationToken calls;
        try
        {
            calls = registration.Calls.Token;
        }
        catch (ObjectDisposedException)
        {
            return (404, null);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, calls);
        if (message.ValueKind == JsonValueKind.Array)
        {
            var responses = new List<JsonElement>();
            foreach (var item in message.EnumerateArray())
            {
                if (await registration.Server.HandleAsync(item, linked.Token).ConfigureAwait(false) is { } response)
                {
                    responses.Add(response);
                }
            }

            return responses.Count == 0 ? (202, null) : (200, JsonSerializer.SerializeToUtf8Bytes(responses, HarnessJsonContext.Default.ListJsonElement));
        }

        var single = await registration.Server.HandleAsync(message, linked.Token).ConfigureAwait(false);
        return single is { } answer ? (200, JsonSerializer.SerializeToUtf8Bytes(answer, HarnessJsonContext.Default.JsonElement)) : (202, null);
    }

    private static async Task WriteResponseAsync(Stream stream, int status, byte[]? body, bool keepAlive, CancellationToken cancellationToken)
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
            header.Append("Content-Type: application/json\r\n");
        }

        if (status == 405)
        {
            header.Append("Allow: POST\r\n");
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

    private sealed record Registration(HarnessMcpToolServer Server, byte[] Authorization, CancellationTokenSource Calls);

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
