using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Channels;

namespace CodeNoesis.CodexSdk;

/// <summary>
/// Low-level JSON-RPC 2.0 transport over newline-delimited JSON (JSONL) on stdio streams.
/// Handles request/response correlation, server-initiated requests (approvals), and
/// notification dispatching.
/// </summary>
/// <remarks>
/// The codex app-server protocol omits the <c>"jsonrpc":"2.0"</c> field on the wire.
/// Messages are delimited by newlines. Each line is a complete JSON object.
/// </remarks>
internal sealed class JsonRpcTransport : IAsyncDisposable
{
    private readonly Stream _inputStream;
    private readonly Stream _outputStream;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly Channel<ServerMessage> _incomingMessages;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private long _nextId;

    /// <summary>
    /// Initializes a new <see cref="JsonRpcTransport"/> that reads from <paramref name="inputStream"/>
    /// and writes to <paramref name="outputStream"/>.
    /// </summary>
    /// <param name="inputStream">The stream to read server messages from (typically stdout of the codex process).</param>
    /// <param name="outputStream">The stream to write client messages to (typically stdin of the codex process).</param>
    /// <param name="jsonOptions">JSON serializer options to use.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="inputStream"/>, <paramref name="outputStream"/>, or
    /// <paramref name="jsonOptions"/> is <see langword="null"/>.
    /// </exception>
    internal JsonRpcTransport(Stream inputStream, Stream outputStream, JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        ArgumentNullException.ThrowIfNull(outputStream);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        _inputStream = inputStream;
        _outputStream = outputStream;
        _jsonOptions = jsonOptions;
        _incomingMessages = Channel.CreateUnbounded<ServerMessage>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Gets the channel reader for server-initiated notifications and requests.
    /// Consumers should read from this channel to process server events.
    /// </summary>
    internal ChannelReader<ServerMessage> Messages => _incomingMessages.Reader;

    /// <summary>
    /// Sends a JSON-RPC request and waits for the correlated response.
    /// </summary>
    /// <typeparam name="TParams">The type of the request parameters.</typeparam>
    /// <typeparam name="TResult">The expected type of the response result.</typeparam>
    /// <param name="method">The JSON-RPC method name.</param>
    /// <param name="parameters">The request parameters to serialize.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized response result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="method"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns a JSON-RPC error response.</exception>
    internal async Task<TResult> SendRequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        try
        {
            await using var registration = cancellationToken.Register(() =>
                tcs.TrySetCanceled(cancellationToken));

            await WriteEnvelopeAsync(method, id, writer =>
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters, _jsonOptions);
            }, cancellationToken).ConfigureAwait(false);

            var resultElement = await tcs.Task.ConfigureAwait(false);

            //Console.WriteLine(resultElement.ToString()); // Ensure the JsonElement is fully parsed before we leave the async method, to avoid deferred parsing issues.

            return resultElement.Deserialize<TResult>(_jsonOptions)
                ?? throw new JsonRpcException(-1, $"Failed to deserialize response for '{method}'.");
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Sends a JSON-RPC request with no parameters and waits for the response.
    /// </summary>
    /// <typeparam name="TResult">The expected type of the response result.</typeparam>
    /// <param name="method">The JSON-RPC method name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized response result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="method"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns a JSON-RPC error response.</exception>
    internal async Task<TResult> SendRequestAsync<TResult>(
        string method,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        try
        {
            await using var registration = cancellationToken.Register(() =>
                tcs.TrySetCanceled(cancellationToken));

            await WriteEnvelopeAsync(method, id, writeParams: null, cancellationToken).ConfigureAwait(false);

            var resultElement = await tcs.Task.ConfigureAwait(false);
            return resultElement.Deserialize<TResult>(_jsonOptions)
                ?? throw new JsonRpcException(-1, $"Failed to deserialize response for '{method}'.");
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Sends a JSON-RPC notification (no response expected).
    /// </summary>
    /// <typeparam name="TParams">The type of the notification parameters.</typeparam>
    /// <param name="method">The JSON-RPC method name.</param>
    /// <param name="parameters">The notification parameters to serialize.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="method"/> is <see langword="null"/>.</exception>
    internal async Task SendNotificationAsync<TParams>(
        string method,
        TParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);

        await WriteEnvelopeAsync(method, id: null, writer =>
        {
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters, _jsonOptions);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a JSON-RPC notification with no parameters.
    /// </summary>
    /// <param name="method">The JSON-RPC method name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="method"/> is <see langword="null"/>.</exception>
    internal async Task SendNotificationAsync(
        string method,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);

        await WriteEnvelopeAsync(method, id: null, writeParams: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a JSON-RPC response to a server-initiated request (e.g., approval requests).
    /// </summary>
    /// <typeparam name="TResult">The type of the response result.</typeparam>
    /// <param name="id">The request id to respond to.</param>
    /// <param name="result">The result to send back.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    internal async Task SendResponseAsync<TResult>(
        long id,
        TResult result,
        CancellationToken cancellationToken = default)
    {
        await WriteResponseEnvelopeAsync(id, result, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        // Complete pending requests so callers don't hang.
        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }

        _pendingRequests.Clear();
        _incomingMessages.Writer.TryComplete();

        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during teardown.
        }

        _writeLock.Dispose();
        _cts.Dispose();
    }

    private async Task WriteEnvelopeAsync(
        string method,
        long? id,
        Action<Utf8JsonWriter>? writeParams,
        CancellationToken cancellationToken)
    {
        var bufferWriter = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writer.WriteString("method", method);
            if (id.HasValue)
                writer.WriteNumber("id", id.Value);
            writeParams?.Invoke(writer);
            writer.WriteEndObject();
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _outputStream.WriteAsync(bufferWriter.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _outputStream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            await _outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteResponseEnvelopeAsync<TResult>(
        long id,
        TResult result,
        CancellationToken cancellationToken)
    {
        var bufferWriter = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, result, _jsonOptions);
            writer.WriteEndObject();
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _outputStream.WriteAsync(bufferWriter.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _outputStream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            await _outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static readonly byte[] NewLine = [(byte)'\n'];

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var pipe = PipeReader.Create(_inputStream);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var readResult = await pipe.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = readResult.Buffer;

                while (TryReadLine(ref buffer, out var line))
                {
                    ProcessLine(line);
                }

                pipe.AdvanceTo(buffer.Start, buffer.End);

                if (readResult.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            await pipe.CompleteAsync().ConfigureAwait(false);
            _incomingMessages.Writer.TryComplete();
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (reader.TryReadTo(out line, (byte)'\n'))
        {
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        line = default;
        return false;
    }

    private void ProcessLine(ReadOnlySequence<byte> lineBytes)
    {
        if (lineBytes.Length == 0)
            return;

        var reader = new Utf8JsonReader(lineBytes);
        JsonElement element;
        try
        {
            element = JsonElement.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            // Skip malformed lines.
            return;
        }

        // Determine message type from JSON shape:
        // - Has "id" + "result" or "error" => response to our request
        // - Has "id" + "method" => server-initiated request (approvals, tool calls)
        // - Has "method" (no "id") => server notification
        var hasId = element.TryGetProperty("id", out var idProp);
        var hasMethod = element.TryGetProperty("method", out var methodProp);
        var hasResult = element.TryGetProperty("result", out var resultProp);
        var hasError = element.TryGetProperty("error", out var errorProp);

        if (hasId && (hasResult || hasError))
        {
            // Response to a pending request.
            var id = idProp.GetInt64();
            if (_pendingRequests.TryRemove(id, out var tcs))
            {
                if (hasError)
                {
                    var code = errorProp.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : -1;
                    var msg = errorProp.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "Unknown error" : "Unknown error";
                    tcs.TrySetException(new JsonRpcException(code, msg, errorProp.Clone()));
                }
                else
                {
                    tcs.TrySetResult(resultProp.Clone());
                }
            }
        }
        else if (hasId && hasMethod)
        {
            // Server-initiated request (e.g., approval requests).
            var id = idProp.GetInt64();
            var method = methodProp.GetString() ?? "";
            var paramsElement = element.TryGetProperty("params", out var p) ? p.Clone() : default;
            _incomingMessages.Writer.TryWrite(new ServerMessage(method, paramsElement, id));
        }
        else if (hasMethod)
        {
            // Server notification.
            var method = methodProp.GetString() ?? "";
            var paramsElement = element.TryGetProperty("params", out var p) ? p.Clone() : default;
            _incomingMessages.Writer.TryWrite(new ServerMessage(method, paramsElement, RequestId: null));
        }
    }

}

/// <summary>
/// Represents a server-initiated message (notification or request) received from the transport.
/// </summary>
/// <param name="Method">The JSON-RPC method name.</param>
/// <param name="Params">The raw JSON parameters payload.</param>
/// <param name="RequestId">
/// Non-null when this is a server-initiated request that requires a response (e.g., approval requests).
/// Null for notifications.
/// </param>
internal sealed record ServerMessage(string Method, JsonElement Params, long? RequestId);
