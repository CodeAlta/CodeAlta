using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using XenoAtom.Logging;

namespace CodeAlta.Acp;

internal sealed class AcpJsonRpcTransport : IAsyncDisposable
{
    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly Func<CancellationToken, ValueTask<string?>> _readLine;
    private readonly Stream _outputStream;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Func<string?>? _describeClose;
    private readonly Action<AcpServerMessage>? _messageHandler;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly Channel<AcpServerMessage> _incomingMessages;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _readLoop;
    private readonly Logger? _logger;
    private long _nextId;
    private volatile Exception? _closedError;
    private bool _disposed;

    internal AcpJsonRpcTransport(Stream inputStream, Stream outputStream, JsonSerializerOptions jsonOptions, Logger? logger = null)
        : this(CreateLineReader(inputStream), outputStream, jsonOptions, logger, describeClose: null)
    {
    }

    internal AcpJsonRpcTransport(Stream inputStream, Stream outputStream, JsonSerializerOptions jsonOptions, Logger? logger, Action<AcpServerMessage>? messageHandler)
        : this(CreateLineReader(inputStream), outputStream, jsonOptions, logger, describeClose: null, messageHandler)
    {
    }

    internal AcpJsonRpcTransport(
        Func<CancellationToken, ValueTask<string?>> readLine,
        Stream outputStream,
        JsonSerializerOptions jsonOptions,
        Logger? logger,
        Func<string?>? describeClose)
        : this(readLine, outputStream, jsonOptions, logger, describeClose, messageHandler: null)
    {
    }

    // A message handler is called on the reading loop for each message of the agent, in the order of its output,
    // before the response that follows it completes its request: it must not wait. Without one, the messages go
    // to Messages.
    internal AcpJsonRpcTransport(
        Func<CancellationToken, ValueTask<string?>> readLine,
        Stream outputStream,
        JsonSerializerOptions jsonOptions,
        Logger? logger,
        Func<string?>? describeClose,
        Action<AcpServerMessage>? messageHandler)
    {
        ArgumentNullException.ThrowIfNull(readLine);
        ArgumentNullException.ThrowIfNull(outputStream);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        _readLine = readLine;
        _outputStream = outputStream;
        _jsonOptions = jsonOptions;
        _logger = logger;
        _describeClose = describeClose;
        _messageHandler = messageHandler;
        _incomingMessages = Channel.CreateUnbounded<AcpServerMessage>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    internal ChannelReader<AcpServerMessage> Messages => _incomingMessages.Reader;

    /// <summary>Gets a task that ends when the agent's output ends or the transport is disposed.</summary>
    internal Task Completion => _completion.Task;

    internal Task<TResult> SendRequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        CancellationToken cancellationToken = default)
        => SendRequestAsync<TParams, TResult>(method, parameters, timeout: null, cancellationToken);

    internal async Task<TResult> SendRequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ThrowIfClosed(method);

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        using var timeoutSource = timeout is { } limit && limit != Timeout.InfiniteTimeSpan ? new CancellationTokenSource(limit) : null;
        using var linked = timeoutSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var token = linked?.Token ?? cancellationToken;
        try
        {
            await using var registration = token.Register(() => tcs.TrySetCanceled(token));
            await WriteEnvelopeAsync(
                    method,
                    id,
                    writer =>
                    {
                        writer.WritePropertyName("params");
                        JsonSerializer.Serialize(writer, parameters, GetJsonTypeInfo<TParams>());
                    },
                    token)
                .ConfigureAwait(false);

            JsonElement resultElement;
            try
            {
                resultElement = await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_disposed && _closedError is null)
            {
                await TrySendCancelRequestAsync(id).ConfigureAwait(false);
                if (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"The ACP agent did not answer '{method}' within {timeout!.Value.TotalSeconds:0} seconds.");
                }

                throw;
            }

            return resultElement.Deserialize(GetJsonTypeInfo<TResult>())
                ?? throw new AcpJsonRpcException(-1, $"Failed to deserialize ACP response for '{method}'.");
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    internal async Task SendNotificationAsync<TParams>(
        string method,
        TParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ThrowIfClosed(method);

        await WriteEnvelopeAsync(
                method,
                id: null,
                writer =>
                {
                    writer.WritePropertyName("params");
                    JsonSerializer.Serialize(writer, parameters, GetJsonTypeInfo<TParams>());
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task SendResponseAsync<TResult>(
        RequestId id,
        TResult result,
        CancellationToken cancellationToken = default)
    {
        var bufferWriter = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            JsonSerializer.Serialize(writer, id, GetJsonTypeInfo<RequestId>());
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, result, GetJsonTypeInfo<TResult>());
            writer.WriteEndObject();
        }

        await WriteBufferAsync(bufferWriter.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    internal async Task SendErrorResponseAsync(
        RequestId id,
        int code,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var bufferWriter = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            JsonSerializer.Serialize(writer, id, GetJsonTypeInfo<RequestId>());
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await WriteBufferAsync(bufferWriter.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closedError ??= new AcpConnectionClosedException("The ACP connection was closed.");
        await _cts.CancelAsync().ConfigureAwait(false);
        FailPendingRequests();
        _incomingMessages.Writer.TryComplete();

        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _completion.TrySetResult();
        _writeLock.Dispose();
        _cts.Dispose();
    }

    private static Func<CancellationToken, ValueTask<string?>> CreateLineReader(Stream inputStream)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        var reader = new StreamReader(inputStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return reader.ReadLineAsync;
    }

    private void ThrowIfClosed(string method)
    {
        if (_closedError is { } error)
        {
            throw new AcpConnectionClosedException($"The ACP agent cannot take '{method}': {error.Message}", error);
        }
    }

    private async Task TrySendCancelRequestAsync(long id)
    {
        try
        {
            await WriteEnvelopeAsync(
                    "$/cancel_request",
                    id: null,
                    writer =>
                    {
                        writer.WriteStartObject("params");
                        writer.WriteNumber("requestId", id);
                        writer.WriteEndObject();
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The agent is gone: nothing to cancel.
        }
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
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            if (id.HasValue)
            {
                writer.WriteNumber("id", id.Value);
            }

            writeParams?.Invoke(writer);
            writer.WriteEndObject();
        }

        await WriteBufferAsync(bufferWriter.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteBufferAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        _logger?.Trace($"ACP Send: {Encoding.UTF8.GetString(buffer.Span)}");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _outputStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _outputStream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            await _outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? error = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _readLine(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                ProcessLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            error = ex;
        }
        finally
        {
            if (_closedError is null)
            {
                var description = _describeClose?.Invoke();
                _closedError = new AcpConnectionClosedException(
                    string.IsNullOrWhiteSpace(description) ? "The ACP agent closed its output." : description,
                    error);
            }

            FailPendingRequests();
            _incomingMessages.Writer.TryComplete();
            _completion.TrySetResult();
        }
    }

    private void FailPendingRequests()
    {
        var error = _closedError ?? new AcpConnectionClosedException("The ACP connection was closed.");
        foreach (var pending in _pendingRequests.Values)
        {
            pending.TrySetException(new AcpConnectionClosedException(error.Message, error.InnerException));
        }
    }

    private void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _logger?.Trace($"ACP Receive: {line}");

        JsonElement element;
        try
        {
            using var document = JsonDocument.Parse(line);
            element = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Agents can write logs to their standard output before the protocol starts.
            _logger?.Debug($"ACP agent wrote a line that is not JSON-RPC: {(line.Length > 500 ? line[..500] : line)}");
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var hasId = element.TryGetProperty("id", out var idProp);
        var hasMethod = element.TryGetProperty("method", out var methodProp);
        var hasResult = element.TryGetProperty("result", out var resultProp);
        var hasError = element.TryGetProperty("error", out var errorProp);

        if (hasId && !hasMethod && (hasResult || hasError))
        {
            if (TryGetResponseId(idProp, out var numericId) && _pendingRequests.TryRemove(numericId, out var pending))
            {
                if (hasError)
                {
                    var code = errorProp.TryGetProperty("code", out var codeProp) && codeProp.TryGetInt32(out var parsedCode) ? parsedCode : -1;
                    var message = errorProp.TryGetProperty("message", out var messageProp)
                        ? messageProp.GetString() ?? "Unknown error"
                        : "Unknown error";
                    pending.TrySetException(new AcpJsonRpcException(code, message, errorProp.Clone()));
                }
                else
                {
                    pending.TrySetResult(resultProp.Clone());
                }
            }

            return;
        }

        if (!hasMethod)
        {
            return;
        }

        RequestId? requestId = null;
        if (hasId)
        {
            requestId = idProp.Deserialize(GetJsonTypeInfo<RequestId>());
        }

        var method = methodProp.GetString() ?? string.Empty;
        var parameters = element.TryGetProperty("params", out var paramsProp) ? paramsProp.Clone() : default;
        var serverMessage = new AcpServerMessage(method, parameters, requestId);
        if (_messageHandler is { } handler)
        {
            try
            {
                handler(serverMessage);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger?.Error(ex, $"The handler of an ACP message '{method}' failed.");
            }

            return;
        }

        _incomingMessages.Writer.TryWrite(serverMessage);
    }

    private static bool TryGetResponseId(JsonElement id, out long value)
    {
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out value))
        {
            return true;
        }

        // Some agents echo a numeric id as a string.
        if (id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private JsonTypeInfo<T> GetJsonTypeInfo<T>()
    {
        var typeInfo = _jsonOptions.GetTypeInfo(typeof(T));
        if (typeInfo is JsonTypeInfo<T> typedTypeInfo)
        {
            return typedTypeInfo;
        }

        throw new InvalidOperationException($"ACP JSON metadata is not available for type '{typeof(T).FullName}'.");
    }
}
