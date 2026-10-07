using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Receives what a CLI process writes. The methods are called from the reader of the connection, one at a
/// time and in the order of the lines: they must not wait.
/// </summary>
internal interface IClaudeCodeConnectionHandler
{
    /// <summary>A message of the conversation or of the session (<c>assistant</c>, <c>result</c>, <c>system</c>, ...).</summary>
    void OnMessage(string type, JsonElement message);

    /// <summary>A request of the CLI that this side answers (<c>can_use_tool</c>, <c>mcp_message</c>, ...).</summary>
    void OnControlRequest(string requestId, string subtype, JsonElement request);

    /// <summary>The CLI no longer waits for the answer to one of its requests.</summary>
    void OnControlCancel(string requestId);

    /// <summary>The process ended or its output could not be read any more.</summary>
    void OnClosed(Exception? failure);
}

/// <summary>
/// The control protocol of the CLI over one process: requests of this side and their responses, and the
/// dispatch of everything else the process writes.
/// </summary>
/// <remarks>
/// The protocol is the one the Claude Agent SDKs speak (<c>--input-format stream-json --output-format
/// stream-json</c>). A line that is not a JSON object and a message of a type this side does not know are
/// skipped, so that a newer CLI keeps working.
/// </remarks>
internal sealed class ClaudeCodeConnection : IAsyncDisposable
{
    private readonly IClaudeCodeTransport _transport;
    private readonly IClaudeCodeConnectionHandler _handler;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _closing = new();
    private Task? _reader;
    private int _nextRequestId;
    private volatile bool _closed;
    private int _disposed;

    public ClaudeCodeConnection(IClaudeCodeTransport transport, IClaudeCodeConnectionHandler handler)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>Gets a value indicating whether the process ended.</summary>
    public bool IsClosed => _closed;

    /// <summary>Starts reading the output of the process.</summary>
    public void Start() => _reader ??= Task.Run(ReadAsync);

    /// <summary>
    /// Sends a control request and waits for its response.
    /// </summary>
    /// <returns>The <c>response</c> object of a successful response, or an undefined element when it has none.</returns>
    /// <exception cref="ClaudeCodeControlException">The CLI answered with an error.</exception>
    /// <exception cref="TimeoutException">The CLI did not answer in time.</exception>
    /// <exception cref="IOException">The process ended.</exception>
    public async Task<JsonElement> RequestAsync(
        string subtype,
        Action<Utf8JsonWriter>? writeFields,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subtype);

        var requestId = $"codealta_{Interlocked.Increment(ref _nextRequestId)}";
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;
        try
        {
            if (_closed)
            {
                throw new IOException(DescribeExit());
            }

            await SendAsync(writer =>
            {
                writer.WriteString("type", "control_request");
                writer.WriteString("request_id", requestId);
                writer.WriteStartObject("request");
                writer.WriteString("subtype", subtype);
                writeFields?.Invoke(writer);
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);

            try
            {
                return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Claude Code did not answer the '{subtype}' request within {timeout.TotalSeconds:0} seconds.");
            }
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>Answers a request of the CLI.</summary>
    public ValueTask RespondAsync(string requestId, Action<Utf8JsonWriter>? writeResponse)
        => SendAsync(writer =>
        {
            writer.WriteString("type", "control_response");
            writer.WriteStartObject("response");
            writer.WriteString("subtype", "success");
            writer.WriteString("request_id", requestId);
            writer.WriteStartObject("response");
            writeResponse?.Invoke(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, CancellationToken.None);

    /// <summary>Answers a request of the CLI with an error.</summary>
    public ValueTask RespondErrorAsync(string requestId, string error)
        => SendAsync(writer =>
        {
            writer.WriteString("type", "control_response");
            writer.WriteStartObject("response");
            writer.WriteString("subtype", "error");
            writer.WriteString("request_id", requestId);
            writer.WriteString("error", error);
            writer.WriteEndObject();
        }, CancellationToken.None);

    /// <summary>Writes one JSON object; <paramref name="writeMessage"/> writes its properties.</summary>
    /// <exception cref="IOException">The process ended.</exception>
    public async ValueTask SendAsync(Action<Utf8JsonWriter> writeMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeMessage);

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeMessage(writer);
            writer.WriteEndObject();
        }

        if (_closed)
        {
            throw new IOException(DescribeExit());
        }

        await _transport.WriteLineAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Says how the process ended, with the end of its error output.</summary>
    public string DescribeExit()
    {
        var exitCode = _transport.ExitCode;
        var standardError = _transport.StandardErrorTail;
        var message = exitCode is null
            ? "Claude Code stopped unexpectedly."
            : $"Claude Code exited with code {exitCode}.";
        return string.IsNullOrWhiteSpace(standardError) ? message : $"{message} {standardError}";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The input is closed first: the CLI ends by itself and the reader sees the end of its output.
        await _transport.DisposeAsync().ConfigureAwait(false);
        _closing.Cancel();
        if (_reader is not null)
        {
            try
            {
                await _reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _closing.Dispose();
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            while (await _transport.ReadLineAsync(_closing.Token).ConfigureAwait(false) is { } line)
            {
                Dispatch(line);
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        _closed = true;
        var closed = new IOException(DescribeExit(), failure);
        foreach (var (requestId, completion) in _pending)
        {
            if (_pending.TryRemove(requestId, out _))
            {
                completion.TrySetException(closed);
            }
        }

        _handler.OnClosed(failure);
    }

    private void Dispatch(string line)
    {
        // Some builds write diagnostics to the standard output: only a JSON object is a message.
        var text = line.AsSpan().Trim();
        if (text.IsEmpty || text[0] != '{')
        {
            return;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        var type = ClaudeCodeJson.GetString(root, "type");
        if (type is null)
        {
            return;
        }

        switch (type)
        {
            case "control_response":
                CompleteRequest(root);
                break;
            case "control_request":
                if (ClaudeCodeJson.GetString(root, "request_id") is { } requestId &&
                    root.TryGetProperty("request", out var request) &&
                    request.ValueKind == JsonValueKind.Object)
                {
                    _handler.OnControlRequest(requestId, ClaudeCodeJson.GetString(request, "subtype") ?? string.Empty, request);
                }

                break;
            case "control_cancel_request":
                if (ClaudeCodeJson.GetString(root, "request_id") is { } cancelledRequestId)
                {
                    _handler.OnControlCancel(cancelledRequestId);
                }

                break;
            default:
                _handler.OnMessage(type, root);
                break;
        }
    }

    private void CompleteRequest(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) ||
            response.ValueKind != JsonValueKind.Object ||
            ClaudeCodeJson.GetString(response, "request_id") is not { } requestId ||
            !_pending.TryRemove(requestId, out var completion))
        {
            return;
        }

        if (string.Equals(ClaudeCodeJson.GetString(response, "subtype"), "error", StringComparison.Ordinal))
        {
            completion.TrySetException(new ClaudeCodeControlException(ClaudeCodeJson.GetString(response, "error") ?? "Claude Code rejected the request."));
            return;
        }

        completion.TrySetResult(response.TryGetProperty("response", out var payload) ? payload : default);
    }
}

/// <summary>
/// The CLI answered a control request with an error.
/// </summary>
internal sealed class ClaudeCodeControlException(string message) : Exception(message);
