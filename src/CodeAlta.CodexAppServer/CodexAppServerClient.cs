using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.CodexAppServer.Protocol;
using XenoAtom.Logging;

namespace CodeAlta.CodexAppServer;

/// <summary>A notification or a request the Codex app-server sends.</summary>
/// <param name="Method">The method.</param>
/// <param name="Params">The parameters, or an undefined element when there are none.</param>
/// <param name="RequestId">The identifier of a request, which waits for an answer; <see langword="null"/> for a notification.</param>
public sealed record CodexServerMessage(string Method, JsonElement Params, JsonElement? RequestId);

/// <summary>
/// A client of the Codex app-server: JSON-RPC over the standard streams of its process, one message a line, without
/// the <c>jsonrpc</c> member the protocol omits. The typed requests are generated (<c>CodexAppServerClient.Methods.gen.cs</c>).
/// </summary>
/// <remarks>
/// The messages of the server are given to the handler on the reading loop, in order: the handler must not wait. A
/// request of the server waits for <see cref="RespondAsync{T}"/> or <see cref="RespondErrorAsync"/>.
/// </remarks>
public sealed partial class CodexAppServerClient : IAsyncDisposable
{
    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly CodexAppServerProcess _process;
    private readonly Action<CodexServerMessage>? _messageHandler;
    private readonly Logger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, (string Method, TaskCompletionSource<JsonElement> Response)> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _readLoop;
    private long _nextId;
    private Exception? _closed;
    private int _disposed;

    private CodexAppServerClient(CodexAppServerProcess process, Action<CodexServerMessage>? messageHandler, Logger? logger)
    {
        _process = process;
        _messageHandler = messageHandler;
        _logger = logger;
        _readLoop = Task.Run(() => ReadLoopAsync(_lifetime.Token));
    }

    /// <summary>Gets how long a request waits for its answer when its token does not end it before.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets the answer of the server to <c>initialize</c>.</summary>
    public InitializeResponse InitializeResponse { get; private set; } = new();

    /// <summary>Gets a task that ends when the server closed its output or the client was disposed.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Gets whether the connection is closed.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) is not null;

    /// <summary>Gets the process of the server.</summary>
    public CodexAppServerProcess Process => _process;

    /// <summary>
    /// Connects to a started server and initializes the protocol with it: <c>initialize</c>, then <c>initialized</c>.
    /// </summary>
    /// <param name="process">The process of the server; the client ends it when it is disposed.</param>
    /// <param name="initialize">What the client tells of itself.</param>
    /// <param name="messageHandler">Receives the notifications and requests of the server, or <see langword="null"/>.</param>
    /// <param name="initializeTimeout">How long the server has to answer <c>initialize</c>.</param>
    /// <param name="logger">The log, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to cancel the connection.</param>
    /// <returns>The client.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="CodexConnectionClosedException">The server stopped before it answered.</exception>
    /// <exception cref="CodexJsonRpcException">The server refused <c>initialize</c>.</exception>
    /// <exception cref="TimeoutException">The server did not answer in time.</exception>
    public static async Task<CodexAppServerClient> ConnectAsync(
        CodexAppServerProcess process,
        InitializeParams initialize,
        Action<CodexServerMessage>? messageHandler,
        TimeSpan initializeTimeout,
        Logger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(initialize);
        var client = new CodexAppServerClient(process, messageHandler, logger) { RequestTimeout = initializeTimeout };
        try
        {
            client.InitializeResponse = await client.InitializeAsync(initialize, cancellationToken).ConfigureAwait(false);
            await client.SendNotificationAsync("initialized", cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a request without parameters and reads its answer.</summary>
    /// <exception cref="CodexJsonRpcException">The server answered with an error.</exception>
    /// <exception cref="CodexConnectionClosedException">The server stopped before it answered.</exception>
    /// <exception cref="TimeoutException">The server did not answer within <see cref="RequestTimeout"/>.</exception>
    public Task<TResult> SendRequestAsync<TResult>(string method, JsonTypeInfo<TResult> resultType, CancellationToken cancellationToken)
        => SendCoreAsync(method, static _ => { }, resultType, cancellationToken);

    /// <summary>Sends a request and reads its answer.</summary>
    /// <exception cref="CodexJsonRpcException">The server answered with an error.</exception>
    /// <exception cref="CodexConnectionClosedException">The server stopped before it answered.</exception>
    /// <exception cref="TimeoutException">The server did not answer within <see cref="RequestTimeout"/>.</exception>
    public Task<TResult> SendRequestAsync<TParams, TResult>(string method, TParams? parameters, JsonTypeInfo<TParams> paramsType, JsonTypeInfo<TResult> resultType, CancellationToken cancellationToken)
        => SendCoreAsync(method, writer =>
        {
            if (parameters is not null)
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters, paramsType);
            }
        }, resultType, cancellationToken);

    /// <summary>Sends a notification without parameters.</summary>
    /// <exception cref="CodexConnectionClosedException">The connection is closed.</exception>
    public Task SendNotificationAsync(string method, CancellationToken cancellationToken = default)
        => WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("method", method);
            writer.WriteEndObject();
        }, cancellationToken);

    /// <summary>Answers a request of the server.</summary>
    /// <param name="requestId">The identifier of the request.</param>
    /// <param name="result">The answer.</param>
    /// <param name="resultType">The type of the answer.</param>
    /// <param name="cancellationToken">A token to cancel the writing.</param>
    /// <exception cref="CodexConnectionClosedException">The connection is closed.</exception>
    public Task RespondAsync<T>(JsonElement requestId, T result, JsonTypeInfo<T> resultType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resultType);
        return WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            requestId.WriteTo(writer);
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, result, resultType);
            writer.WriteEndObject();
        }, cancellationToken);
    }

    /// <summary>Answers a request of the server with an error.</summary>
    /// <param name="requestId">The identifier of the request.</param>
    /// <param name="code">The code of the error, such as <see cref="CodexErrorCodes.MethodNotFound"/>.</param>
    /// <param name="message">The message of the error.</param>
    /// <param name="cancellationToken">A token to cancel the writing.</param>
    /// <exception cref="CodexConnectionClosedException">The connection is closed.</exception>
    public Task RespondErrorAsync(JsonElement requestId, int code, string message, CancellationToken cancellationToken = default)
        => WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            requestId.WriteTo(writer);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, cancellationToken);

    /// <summary>Says why the connection closed: the exit of the server and the last lines of its errors.</summary>
    public string DescribeExit() => _process.DescribeExit();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close(new CodexConnectionClosedException("The connection to the Codex app-server was closed."));
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }

        _lifetime.Dispose();
    }

    private async Task<TResult> SendCoreAsync<TResult>(string method, Action<Utf8JsonWriter> writeParams, JsonTypeInfo<TResult> resultType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(resultType);
        if (Volatile.Read(ref _closed) is { } closed)
        {
            throw new CodexConnectionClosedException(closed.Message, closed);
        }

        var id = Interlocked.Increment(ref _nextId);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (method, response);
        try
        {
            await WriteAsync(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("method", method);
                writer.WriteNumber("id", id);
                writeParams(writer);
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);

            JsonElement result;
            try
            {
                result = await response.Task.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"The Codex app-server did not answer {method} within {RequestTimeout.TotalSeconds:0} seconds.");
            }

            return result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? JsonSerializer.Deserialize("{}", resultType)!
                : result.Deserialize(resultType) ?? throw new JsonException($"The answer of the Codex app-server to {method} is empty.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WriteAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) is { } closed)
        {
            throw new CodexConnectionClosedException(closed.Message, closed);
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new CodexConnectionClosedException(DescribeExit(), ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _process.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0 || line[0] != '{')
                {
                    continue;
                }

                ProcessLine(line);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or System.Threading.Channels.ChannelClosedException)
        {
        }
        finally
        {
            Close(new CodexConnectionClosedException(Volatile.Read(ref _disposed) != 0 ? "The connection to the Codex app-server was closed." : DescribeExit()));
        }
    }

    private void ProcessLine(string line)
    {
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(line);
            message = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger?.Debug($"A line of the Codex app-server is not JSON: {ex.Message}");
            return;
        }

        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        if (message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
        {
            var parameters = message.TryGetProperty("params", out var p) ? p : default;
            try
            {
                _messageHandler?.Invoke(new CodexServerMessage(methodElement.GetString()!, parameters, hasId ? id : null));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger?.Warn($"A message of the Codex app-server ({methodElement.GetString()!}) could not be handled: {ex.Message}");
            }

            return;
        }

        if (!hasId || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var key) || !_pending.TryRemove(key, out var pending))
        {
            return;
        }

        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var value) ? value : CodexErrorCodes.InternalError;
            var text = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : string.Empty;
            JsonElement? data = error.TryGetProperty("data", out var d) ? d : null;
            pending.Response.TrySetException(new CodexJsonRpcException(pending.Method, code, text, data));
            return;
        }

        pending.Response.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
    }

    private void Close(Exception reason)
    {
        if (Interlocked.CompareExchange(ref _closed, reason, null) is not null)
        {
            return;
        }

        foreach (var (key, pending) in _pending)
        {
            if (_pending.TryRemove(key, out _))
            {
                pending.Response.TrySetException(new CodexConnectionClosedException(reason.Message, reason));
            }
        }

        _completion.TrySetResult();
    }
}
