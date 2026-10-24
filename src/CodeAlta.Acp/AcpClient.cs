using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using XenoAtom.Logging;

namespace CodeAlta.Acp;

/// <summary>
/// High-level ACP JSON-RPC client.
/// </summary>
public sealed partial class AcpClient : IAsyncDisposable
{
    private readonly AcpProcess? _process;
    private readonly AcpJsonRpcTransport _transport;
    private readonly JsonSerializerOptions _jsonOptions;
    private InitializeResponse? _initializeResponse;
    private bool _disposed;

    private AcpClient(
        AcpProcess? process,
        AcpJsonRpcTransport transport,
        JsonSerializerOptions jsonOptions)
    {
        _process = process;
        _transport = transport;
        _jsonOptions = jsonOptions;
    }

    /// <summary>
    /// Gets the initialize response returned by the agent.
    /// </summary>
    public InitializeResponse InitializeResponse => _initializeResponse
        ?? throw new InvalidOperationException("The ACP client has not been initialized.");

    /// <summary>
    /// Starts an ACP process, performs initialization, and returns a connected client.
    /// </summary>
    public static async Task<AcpClient> StartAsync(
        AcpClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var logger = LogManager.GetLogger("acp.client");
        var process = AcpProcess.Start(options.ProcessOptions, logger);
        var jsonOptions = CreateJsonSerializerOptions();
        var transport = new AcpJsonRpcTransport(
            process.ReadLineAsync,
            process.StandardInput,
            jsonOptions,
            logger,
            () => DescribeExit(options.ProcessOptions.FileName, process));

        var client = new AcpClient(process, transport, jsonOptions);
        try
        {
            await client.InitializeFromOptionsAsync(options, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Connects an ACP client to existing streams and performs initialization.
    /// </summary>
    public static async Task<AcpClient> ConnectAsync(
        Stream serverOutput,
        Stream clientInput,
        InitializeRequest initializeRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverOutput);
        ArgumentNullException.ThrowIfNull(clientInput);
        ArgumentNullException.ThrowIfNull(initializeRequest);

        var logger = LogManager.GetLogger("acp.client");
        var jsonOptions = CreateJsonSerializerOptions();
        var transport = new AcpJsonRpcTransport(serverOutput, clientInput, jsonOptions, logger);
        var client = new AcpClient(process: null, transport, jsonOptions);
        try
        {
            client._initializeResponse = await client.InitializeCoreAsync(initializeRequest, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Gets a task that ends when the agent's output ends (it exited) or the client is disposed.
    /// </summary>
    public Task Completion => _transport.Completion;

    /// <summary>
    /// Gets the process id of the agent, or <see langword="null" /> for a client connected to streams.
    /// </summary>
    public int? ProcessId => _process?.Id;

    /// <summary>
    /// Gets whether the agent process exited; always <see langword="false" /> for a client connected to streams.
    /// </summary>
    public bool HasExited => _process?.HasExited ?? false;

    /// <summary>
    /// Gets the last lines the agent wrote to its standard error, or an empty string.
    /// </summary>
    public string ErrorTail => _process?.ErrorTail ?? string.Empty;

    /// <summary>
    /// Streams raw server-initiated ACP messages.
    /// </summary>
    public async IAsyncEnumerable<AcpServerMessage> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _transport.Messages.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    /// <summary>
    /// Sends a raw typed JSON-RPC response to a server-initiated request.
    /// </summary>
    public Task RespondToRequestAsync<TResult>(
        RequestId requestId,
        TResult result,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _transport.SendResponseAsync(requestId, result, cancellationToken);
    }

    /// <summary>
    /// Answers a server-initiated request with a JSON-RPC error.
    /// </summary>
    /// <param name="requestId">The id of the request.</param>
    /// <param name="code">The JSON-RPC error code, such as <c>-32601</c> for a method the client does not have.</param>
    /// <param name="message">The error message.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="message" /> is <see langword="null" />.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the client is disposed.</exception>
    public Task RespondWithErrorAsync(
        RequestId requestId,
        int code,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _transport.SendErrorResponseAsync(requestId, code, message, cancellationToken);
    }

    /// <summary>
    /// Sends a raw typed ACP JSON-RPC request.
    /// </summary>
    public Task<TResult> SendRequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _transport.SendRequestAsync<TParams, TResult>(method, parameters, cancellationToken);
    }

    /// <summary>
    /// Sends a raw typed ACP JSON-RPC request that fails with a <see cref="TimeoutException" /> when the agent does not
    /// answer in time. The agent is sent <c>$/cancel_request</c> for a request that times out or is cancelled.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="parameters">The parameters.</param>
    /// <param name="timeout">How long the agent has to answer; <see cref="Timeout.InfiniteTimeSpan" /> for no limit.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The result of the request.</returns>
    /// <exception cref="TimeoutException">Thrown when the agent does not answer in time.</exception>
    /// <exception cref="AcpJsonRpcException">Thrown when the agent answers with an error.</exception>
    /// <exception cref="AcpConnectionClosedException">Thrown when the agent exited.</exception>
    public Task<TResult> SendRequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _transport.SendRequestAsync<TParams, TResult>(method, parameters, timeout, cancellationToken);
    }

    /// <summary>
    /// Asks the agent to stop the prompt turn of a session (<c>session/cancel</c>). The pending
    /// <c>session/prompt</c> then ends with the stop reason <c>cancelled</c>.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId" /> is empty.</exception>
    public Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SessionCancelAsync(new CancelNotification { SessionId = new SessionId { Value = sessionId } }, cancellationToken);
    }

    /// <summary>
    /// Sends a raw ACP JSON-RPC notification.
    /// </summary>
    public Task SendNotificationAsync<TParams>(
        string method,
        TParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _transport.SendNotificationAsync(method, parameters, cancellationToken);
    }

    /// <summary>
    /// Creates serializer options for ACP DTOs.
    /// </summary>
    public static JsonSerializerOptions CreateJsonSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = AcpJsonSerializerContext.Default
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_process is not null)
        {
            await _process.DisposeAsync().ConfigureAwait(false);
        }

        GC.KeepAlive(_jsonOptions);
    }

    private async Task InitializeFromOptionsAsync(
        AcpClientOptions options,
        CancellationToken cancellationToken)
    {
        var initializeRequest = new InitializeRequest
        {
            ClientInfo = options.ClientInfo,
            ClientCapabilities = options.ClientCapabilities,
            ProtocolVersion = new ProtocolVersion
            {
                Value = CreateNumberElement(options.ProtocolVersion)
            },
        };
        _initializeResponse = await _transport.SendRequestAsync<InitializeRequest, InitializeResponse>(
                "initialize",
                initializeRequest,
                options.InitializeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string DescribeExit(string fileName, AcpProcess process)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var (exitCode, errorTail) = process.DescribeExit(TimeSpan.FromSeconds(2));
        var tail = errorTail.Trim();
        var message = exitCode is { } code
            ? $"The ACP agent '{name}' exited with code {code}."
            : $"The ACP agent '{name}' closed its output.";
        return tail.Length == 0 ? message : $"{message}{Environment.NewLine}{tail}";
    }

    private static JsonElement CreateNumberElement(ushort value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteNumberValue(value);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private Task<InitializeResponse> InitializeCoreAsync(
        InitializeRequest request,
        CancellationToken cancellationToken)
    {
        return _transport.SendRequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            request,
            cancellationToken);
    }
}
