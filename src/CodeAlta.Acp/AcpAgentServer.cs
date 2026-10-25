using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using XenoAtom.Logging;

namespace CodeAlta.Acp;

/// <summary>
/// Serves an ACP agent to a client over two streams, such as the standard input and output of a process: it reads the
/// requests and notifications of the client, calls the <see cref="IAcpAgent"/> for each, and writes its answers.
/// </summary>
/// <remarks>
/// <para>
/// Each request runs in a task of its own (<c>session/prompt</c> and <c>session/cancel</c> start on the reading loop, in
/// their order, see <see cref="IAcpAgent.PromptAsync"/>) and is always answered: with the result of the agent, with the error of an
/// <see cref="AcpJsonRpcException"/> it throws, with <see cref="AcpErrorCodes.InternalError"/> for another exception,
/// with <see cref="AcpErrorCodes.MethodNotFound"/> for a method the server does not know, and with
/// <see cref="AcpErrorCodes.RequestCancelled"/> when the client cancelled it with <c>$/cancel_request</c> and the agent
/// stopped. A notification the server does not know is ignored.
/// </para>
/// <para>
/// When the input ends, the client is gone: the requests still running are cancelled, and <see cref="Completion"/> ends
/// once they have returned. The server does not close the streams.
/// </para>
/// </remarks>
public sealed class AcpAgentServer : IAsyncDisposable
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ClientConnection _client;
    private readonly IAcpAgent _agent;
    private readonly AcpJsonRpcTransport _transport;
    private readonly Logger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly Task _completion;
    private int _disposed;

    /// <summary>
    /// Starts serving an agent: the server reads <paramref name="input"/> from now on.
    /// </summary>
    /// <param name="input">What the client writes.</param>
    /// <param name="output">What the client reads.</param>
    /// <param name="createAgent">
    /// Creates the agent, given the connection to the client it sends its updates and requests to. It is called once,
    /// before the first message is read.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="createAgent"/> returns <see langword="null"/>.</exception>
    public AcpAgentServer(Stream input, Stream output, Func<IAcpClientConnection, IAcpAgent> createAgent)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(createAgent);

        _logger = LogManager.GetLogger("acp.server");
        _jsonOptions = AcpClient.CreateJsonSerializerOptions();
        _client = new ClientConnection();
        _agent = createAgent(_client) ?? throw new InvalidOperationException("The factory of the ACP agent returned no agent.");

        // Without a handler, the transport keeps the messages it reads in its channel until the loop below reads
        // them: a message that arrives before the server is ready is not lost.
        _transport = new AcpJsonRpcTransport(input, output, _jsonOptions, _logger);
        _client.Transport = _transport;
        _completion = Task.Run(RunAsync);
    }

    /// <summary>Gets the connection to the client, which the agent was given.</summary>
    public IAcpClientConnection Client => _client;

    /// <summary>
    /// Gets a task that ends when the input ends or the server is disposed, once the requests that were running have
    /// returned.
    /// </summary>
    public Task Completion => _completion;

    /// <summary>Stops serving: the running requests are cancelled, and the server waits for them to return.</summary>
    /// <returns>A task that ends when the server has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        await _completion.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var message in _transport.Messages.ReadAllAsync().ConfigureAwait(false))
            {
                Dispatch(message);
            }
        }
        finally
        {
            try
            {
                await _shutdown.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                await Task.WhenAll(_running.Keys).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.Error(ex, "An ACP request of the client ended with an error that was not answered.");
            }
        }
    }

    // Runs on the loop that reads the messages, in their order: a request is registered before a later
    // $/cancel_request for it is read, and its handling runs in a task of its own.
    private void Dispatch(AcpServerMessage message)
    {
        if (message.Method == "$/cancel_request")
        {
            CancelRequest(message.Params);
            return;
        }

        // A session/cancel cancels the prompt the client sent before it: both start on this loop, in their order, so that
        // the agent has taken a prompt (up to its first await) before it handles a cancel read after it.
        if (message.RequestId is not { } id)
        {
            Track(message.Method == "session/cancel" ? HandleNotificationAsync(message) : Task.Run(() => HandleNotificationAsync(message)));
            return;
        }

        var key = RequestKey(id.Value);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (!_requests.TryAdd(key, cancellation))
        {
            cancellation.Dispose();
            Track(TryRespondErrorAsync(id, AcpErrorCodes.InvalidRequest, $"A request with the id {key} is already running.", data: null));
            return;
        }

        Track(message.Method == "session/prompt" ? HandleRequestAsync(message, id, key, cancellation) : Task.Run(() => HandleRequestAsync(message, id, key, cancellation)));
    }

    private void Track(Task task)
    {
        _running.TryAdd(task, 0);
        _ = task.ContinueWith(completed => _running.TryRemove(completed, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void CancelRequest(JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("requestId", out var requestId) &&
            _requests.TryGetValue(RequestKey(requestId), out var cancellation))
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The request has just ended.
            }
        }
    }

    private async Task HandleRequestAsync(AcpServerMessage message, RequestId id, string key, CancellationTokenSource cancellation)
    {
        try
        {
            await InvokeAsync(message.Method, id, message.Params, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // The connection has ended: there is nobody to answer.
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await TryRespondErrorAsync(id, AcpErrorCodes.RequestCancelled, "The request was cancelled.", data: null).ConfigureAwait(false);
        }
        catch (AcpJsonRpcException ex)
        {
            await TryRespondErrorAsync(id, ex.Code, ex.Message, ex.ErrorData).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or AcpConnectionClosedException)
        {
            // The client is gone before the answer was written.
            _logger.Debug($"The answer to '{message.Method}' was not written: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.Error(ex, $"The ACP agent failed to answer '{message.Method}'.");
            await TryRespondErrorAsync(id, AcpErrorCodes.InternalError, ex.Message, data: null).ConfigureAwait(false);
        }
        finally
        {
            _requests.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cancellation));
            cancellation.Dispose();
        }
    }

    private Task InvokeAsync(string method, RequestId id, JsonElement parameters, CancellationToken cancellationToken)
        => method switch
        {
            "initialize" => AnswerAsync<InitializeRequest, InitializeResponse>(id, method, parameters, (request, token) =>
            {
                _client.Initialize = request;
                return _agent.InitializeAsync(request, token);
            }, cancellationToken),
            "authenticate" => AnswerAsync<AuthenticateRequest, AuthenticateResponse>(id, method, parameters, _agent.AuthenticateAsync, cancellationToken),
            "session/new" => AnswerAsync<NewSessionRequest, NewSessionResponse>(id, method, parameters, _agent.NewSessionAsync, cancellationToken),
            "session/load" => AnswerAsync<LoadSessionRequest, LoadSessionResponse>(id, method, parameters, _agent.LoadSessionAsync, cancellationToken),
            "session/resume" => AnswerAsync<ResumeSessionRequest, ResumeSessionResponse>(id, method, parameters, _agent.ResumeSessionAsync, cancellationToken),
            "session/list" => AnswerAsync<ListSessionsRequest, ListSessionsResponse>(id, method, parameters, _agent.ListSessionsAsync, cancellationToken),
            "session/fork" => AnswerAsync<ForkSessionRequest, ForkSessionResponse>(id, method, parameters, _agent.ForkSessionAsync, cancellationToken),
            "session/close" => AnswerAsync<CloseSessionRequest, CloseSessionResponse>(id, method, parameters, _agent.CloseSessionAsync, cancellationToken),
            "session/set_mode" => AnswerAsync<SetSessionModeRequest, SetSessionModeResponse>(id, method, parameters, _agent.SetSessionModeAsync, cancellationToken),
            "session/set_config_option" => AnswerAsync<SetSessionConfigOptionRequest, SetSessionConfigOptionResponse>(id, method, parameters, _agent.SetSessionConfigOptionAsync, cancellationToken),
            "session/prompt" => AnswerAsync<PromptRequest, PromptResponse>(id, method, parameters, _agent.PromptAsync, cancellationToken),
            _ => throw new AcpJsonRpcException(AcpErrorCodes.MethodNotFound, $"Method '{method}' not found."),
        };

    private async Task AnswerAsync<TParams, TResult>(
        RequestId id,
        string method,
        JsonElement parameters,
        Func<TParams, CancellationToken, Task<TResult>> handler,
        CancellationToken cancellationToken)
    {
        var request = ReadParams<TParams>(method, parameters);
        var result = await handler(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The ACP agent answered '{method}' with no result.");
        await _transport.SendResponseAsync(id, result, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleNotificationAsync(AcpServerMessage message)
    {
        try
        {
            switch (message.Method)
            {
                case "session/cancel":
                    await _agent.CancelAsync(ReadParams<CancelNotification>(message.Method, message.Params), _shutdown.Token).ConfigureAwait(false);
                    break;
                default:
                    _logger.Debug($"The ACP client sent the notification '{message.Method}', which the server does not handle.");
                    break;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.Error(ex, $"The ACP agent failed to handle the notification '{message.Method}'.");
        }
    }

    private TParams ReadParams<TParams>(string method, JsonElement parameters)
    {
        var typeInfo = (JsonTypeInfo<TParams>)_jsonOptions.GetTypeInfo(typeof(TParams));
        try
        {
            // A request without parameters is read as one with an empty object.
            var element = parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? AcpJson.EmptyObject : parameters;
            return element.Deserialize(typeInfo)
                ?? throw new AcpJsonRpcException(AcpErrorCodes.InvalidParams, $"The parameters of '{method}' are missing.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new AcpJsonRpcException(AcpErrorCodes.InvalidParams, $"The parameters of '{method}' are not valid: {ex.Message}");
        }
    }

    private async Task TryRespondErrorAsync(RequestId id, int code, string message, JsonElement? data)
    {
        try
        {
            await _transport.SendErrorResponseAsync(id, code, message, data, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or AcpConnectionClosedException)
        {
            // The client is gone.
        }
    }

    // The raw JSON of an id: the id 1 and the id "1" are two requests.
    private static string RequestKey(JsonElement id) => id.ValueKind == JsonValueKind.Undefined ? string.Empty : id.GetRawText();

    private sealed class ClientConnection : IAcpClientConnection
    {
        private volatile InitializeRequest? _initialize;

        public AcpJsonRpcTransport? Transport { get; set; }

        public InitializeRequest? Initialize
        {
            get => _initialize;
            set => _initialize = value;
        }

        public ClientCapabilities? ClientCapabilities => _initialize?.ClientCapabilities;

        public Implementation? ClientInfo => _initialize?.ClientInfo;

        public Task SessionUpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            if (update.Value.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("The update is not defined: make it with AcpSessionUpdates.", nameof(update));
            }

            return GetTransport().SendNotificationAsync(
                "session/update",
                new SessionNotification { SessionId = sessionId, Update = update },
                cancellationToken);
        }

        public Task<TResult> SendRequestAsync<TParams, TResult>(string method, TParams parameters, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(method);
            ArgumentNullException.ThrowIfNull(parameters);
            return GetTransport().SendRequestAsync<TParams, TResult>(method, parameters, cancellationToken);
        }

        public Task SendNotificationAsync<TParams>(string method, TParams parameters, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(method);
            ArgumentNullException.ThrowIfNull(parameters);
            return GetTransport().SendNotificationAsync(method, parameters, cancellationToken);
        }

        private AcpJsonRpcTransport GetTransport()
            => Transport ?? throw new InvalidOperationException("The ACP server has not started: the agent cannot reach the client while it is created.");
    }
}
