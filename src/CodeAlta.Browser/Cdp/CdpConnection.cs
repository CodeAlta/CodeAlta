using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CodeAlta.Browser.Cdp;

/// <summary>A failure the browser answered a command with, or a command that could not complete.</summary>
internal sealed class CdpException : Exception
{
    public CdpException(string method, int code, string message)
        : base($"{method}: {message}")
    {
        Method = method;
        Code = code;
    }

    /// <summary>Gets the command.</summary>
    public string Method { get; }

    /// <summary>Gets the code of the error, or 0 when the connection or the session ended first.</summary>
    public int Code { get; }
}

/// <summary>
/// One WebSocket connection to the DevTools endpoint of a Chromium browser, with the sessions of the targets it is
/// attached to in flat mode: commands carry the id of their session, and events come back with it.
/// </summary>
/// <remarks>
/// One loop reads the socket. It completes the answers of commands at once and hands each event to the queue of its
/// session, which delivers them in order to its handlers, so that a handler never sees an event before one the browser
/// sent earlier. A handler must not wait for a command: the reply would wait behind it.
/// </remarks>
internal sealed class CdpConnection : IAsyncDisposable
{
    private const int MaximumMessageBytes = 256 * 1024 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, CdpSession> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private Task? _reader;

    private CdpConnection(ClientWebSocket socket)
    {
        _socket = socket;
        Browser = new CdpSession(this, null);
    }

    /// <summary>Gets the session of the browser itself, for the commands of the Browser and Target domains.</summary>
    public CdpSession Browser { get; }

    /// <summary>Gets a task that completes when the connection ends, with what ended it or null for a close asked here.</summary>
    public Task<Exception?> Closed => _closed.Task;

    /// <summary>Gets whether the connection ended.</summary>
    public bool IsClosed => _closed.Task.IsCompleted;

    /// <summary>Connects to the WebSocket address of a browser, such as <c>ws://127.0.0.1:9222/devtools/browser/&lt;id&gt;</c>.</summary>
    /// <exception cref="WebSocketException">Nothing answers there.</exception>
    public static async Task<CdpConnection> ConnectAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        var socket = new ClientWebSocket();
        // A browser that died leaves a socket nothing reads: pings tell it apart from a quiet one.
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await socket.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var connection = new CdpConnection(socket);
        connection._reader = Task.Run(connection.ReadLoopAsync, CancellationToken.None);
        return connection;
    }

    /// <summary>Gets the session of an attached target, created on first use.</summary>
    public CdpSession Session(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        return _sessions.GetOrAdd(sessionId, id => new CdpSession(this, id));
    }

    /// <summary>Forgets a session whose target detached: its pending commands fail, and its handlers get nothing more.</summary>
    public void Detach(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        session.Complete();
        foreach (var (id, pending) in _pending)
        {
            if (pending.SessionId == sessionId && _pending.TryRemove(id, out var removed))
                removed.Completion.TrySetException(new CdpException(removed.Method, 0, "The target was closed."));
        }
    }

    internal async Task<JsonElement> SendAsync<TParams>(string? sessionId, string method, TParams parameters, JsonTypeInfo<TParams> parametersInfo, CancellationToken cancellationToken)
    {
        if (IsClosed) throw new CdpException(method, 0, "The connection to the browser is closed.");
        var id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(method, sessionId);
        _pending[id] = pending;
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters, parametersInfo);
            if (sessionId is not null) writer.WriteString("sessionId", sessionId);
            writer.WriteEndObject();
        }

        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }

            using var registration = cancellationToken.Register(static state => ((Pending)state!).Completion.TrySetCanceled(), pending);
            return await pending.Completion.Task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
        {
            throw new CdpException(method, 0, "The connection to the browser is closed.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        Exception? failure = null;
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                buffer.ResetWrittenCount();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.GetMemory(64 * 1024), _closing.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    buffer.Advance(result.Count);
                    if (buffer.WrittenCount > MaximumMessageBytes) throw new InvalidDataException("The browser sent a message larger than 256 MB.");
                }
                while (!result.EndOfMessage);

                Dispatch(buffer.WrittenMemory);
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is WebSocketException or InvalidDataException or JsonException or ObjectDisposedException)
        {
            failure = exception;
        }
        finally
        {
            Shutdown(failure);
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var idValue) && idValue.TryGetInt64(out var id))
        {
            if (!_pending.TryRemove(id, out var pending)) return;
            if (root.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var number) ? number : -1;
                var text = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() ?? "error" : "error";
                pending.Completion.TrySetException(new CdpException(pending.Method, code, text));
            }
            else pending.Completion.TrySetResult(root.TryGetProperty("result", out var resultValue) ? resultValue.Clone() : default);
            return;
        }

        if (!root.TryGetProperty("method", out var methodValue) || methodValue.GetString() is not { } method) return;
        var session = root.TryGetProperty("sessionId", out var sessionValue) && sessionValue.GetString() is { } sessionId
            ? (_sessions.TryGetValue(sessionId, out var found) ? found : null)
            : Browser;
        var parameters = root.TryGetProperty("params", out var paramsValue) ? paramsValue.Clone() : default;
        session?.Post(method, parameters);
        if (method == "Target.detachedFromTarget" && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("sessionId", out var detached) && detached.GetString() is { } detachedId)
        {
            Detach(detachedId);
        }
    }

    private void Shutdown(Exception? failure)
    {
        if (!_closed.TrySetResult(failure)) return;
        foreach (var (id, pending) in _pending)
        {
            if (_pending.TryRemove(id, out _)) pending.Completion.TrySetException(new CdpException(pending.Method, 0, "The connection to the browser is closed."));
        }

        Browser.Complete();
        foreach (var session in _sessions.Values) session.Complete();
        _sessions.Clear();
    }

    /// <summary>Closes the connection; the browser stays as it is.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_closing.IsCancellationRequested)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (_socket.State == WebSocketState.Open) await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The browser went first.
            }

            await _closing.CancelAsync().ConfigureAwait(false);
        }

        if (_reader is not null) await _reader.ConfigureAwait(false);
        Shutdown(null);
        _socket.Dispose();
        _sendGate.Dispose();
        _closing.Dispose();
    }

    private sealed class Pending(string method, string? sessionId)
    {
        public string Method { get; } = method;

        public string? SessionId { get; } = sessionId;

        public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
