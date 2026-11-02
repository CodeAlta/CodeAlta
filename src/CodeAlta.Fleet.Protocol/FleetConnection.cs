using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace CodeAlta.Fleet.Protocol;

/// <summary>Handles an incoming request.</summary>
/// <param name="context">The request and its connection.</param>
/// <param name="cancellationToken">Cancelled when the connection closes.</param>
/// <returns>The result, or <see langword="null"/> for none. Throw a <see cref="FleetException"/> to answer with its error.</returns>
public delegate ValueTask<JsonElement?> FleetRequestHandler(FleetRequestContext context, CancellationToken cancellationToken);

/// <summary>Handles an incoming event, in the order the events arrive.</summary>
/// <param name="fleetEvent">The event.</param>
/// <param name="cancellationToken">Cancelled when the connection closes.</param>
/// <returns>A task that completes when the event is handled; the next frame is read after it.</returns>
public delegate ValueTask FleetEventHandler(FleetEvent fleetEvent, CancellationToken cancellationToken);

/// <summary>An incoming request and the connection it came on.</summary>
public sealed class FleetRequestContext
{
    internal FleetRequestContext(FleetConnection connection, FleetRequest request)
    {
        Connection = connection;
        Request = request;
    }

    /// <summary>Gets the connection the request came on, to send events back.</summary>
    public FleetConnection Connection { get; }

    /// <summary>Gets the request.</summary>
    public FleetRequest Request { get; }

    /// <summary>Gets the method of the request.</summary>
    public string Method => Request.Method;

    /// <summary>Gets the parameters of the request, or <see langword="null"/>.</summary>
    public JsonElement? Params => Request.Params;

    /// <summary>Gets the idempotency key of the request, or <see langword="null"/>.</summary>
    public string? IdempotencyKey => Request.IdempotencyKey;

    /// <summary>Gets the W3C <c>traceparent</c> of the caller, or <see langword="null"/>.</summary>
    public string? TraceParent => Request.TraceParent;
}

/// <summary>The state of a <see cref="FleetConnection"/>.</summary>
public enum FleetConnectionState
{
    /// <summary>Created: handlers can be registered; nothing is read or sent.</summary>
    Created,

    /// <summary>Started: requests and events flow both ways.</summary>
    Open,

    /// <summary>Closing cleanly: frames already queued are sent, then the close; nothing new is accepted.</summary>
    Closing,

    /// <summary>Closed: <see cref="FleetConnection.Completion"/> says how.</summary>
    Closed,
}

/// <summary>
/// One bidirectional message connection of the fleet protocol over a transport, after its handshake: requests
/// correlated with their responses, request handlers by method, events both ways, the application heartbeat and a
/// dead-peer timeout, and a clean close.
/// </summary>
/// <remarks>
/// <para>
/// One reader loop reads every frame; one writer loop sends every frame from a bounded queue, so a sender waits when
/// the peer reads slowly. Incoming requests run on the thread pool, at most
/// <see cref="FleetConnectionOptions.MaxConcurrentIncomingRequests"/> at once (the reader waits for a slot); events
/// are handled in order by the reader. No lock is held across an await.
/// </para>
/// <para>
/// Register handlers, then <see cref="Start"/>. A pending request fails with <see cref="FleetConnectionClosedException"/>
/// when the connection closes, with <see cref="TimeoutException"/> when its response does not come in time, and with
/// <see cref="FleetException"/> when the peer answers an error.
/// </para>
/// </remarks>
public sealed class FleetConnection : IAsyncDisposable
{
    private const int StateCreated = (int)FleetConnectionState.Created;
    private const int StateOpen = (int)FleetConnectionState.Open;
    private const int StateClosing = (int)FleetConnectionState.Closing;
    private const int StateClosed = (int)FleetConnectionState.Closed;

    private readonly IFleetTransport _transport;
    private readonly FleetConnectionOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, FleetRequestHandler> _registrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<FleetResponse>> _pending = new(StringComparer.Ordinal);
    private readonly Channel<byte[]> _sendQueue;
    private readonly SemaphoreSlim _handlerSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<FleetConnectionCloseInfo> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FrozenDictionary<string, FleetRequestHandler> _handlers = FrozenDictionary<string, FleetRequestHandler>.Empty;
    private FleetEventHandler? _eventHandler;
    private FleetConnectionCloseInfo? _closeInfo;
    private string? _closeDescription;
    private int _state;
    private long _lastReceivedTimestamp;
    private long _pingSequence;

    /// <summary>Initializes a new instance of the <see cref="FleetConnection"/> class.</summary>
    /// <param name="transport">The transport, after its handshake. The connection owns it from now on.</param>
    /// <param name="options">The limits and timings, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit or a time of <paramref name="options"/> is not valid.</exception>
    public FleetConnection(IFleetTransport transport, FleetConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        options ??= new FleetConnectionOptions();
        options.Validate();
        _transport = transport;
        _options = options;
        _time = options.TimeProvider;
        _handlerSlots = new SemaphoreSlim(options.MaxConcurrentIncomingRequests, options.MaxConcurrentIncomingRequests);
        _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.SendQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>Gets the state of the connection.</summary>
    public FleetConnectionState State => (FleetConnectionState)Volatile.Read(ref _state);

    /// <summary>Gets a task that completes, never faulted, with how the connection closed.</summary>
    public Task<FleetConnectionCloseInfo> Completion => _completion.Task;

    /// <summary>Registers the handler of a method. Handlers are registered before <see cref="Start"/>, from one thread.</summary>
    /// <param name="method">The method, such as <c>sessions.send</c>.</param>
    /// <param name="handler">The handler.</param>
    /// <exception cref="ArgumentException"><paramref name="method"/> is null or empty, or already has a handler.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The connection was started.</exception>
    public void RegisterRequestHandler(string method, FleetRequestHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfStarted();
        if (!_registrations.TryAdd(method, handler)) throw new ArgumentException($"The method '{method}' already has a handler.", nameof(method));
    }

    /// <summary>Sets the handler of incoming events. It is set before <see cref="Start"/>; without one, events are dropped.</summary>
    /// <param name="handler">The handler. An exception it throws closes the connection with <see cref="FleetErrorCodes.InternalError"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The connection was started.</exception>
    public void SetEventHandler(FleetEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfStarted();
        _eventHandler = handler;
    }

    /// <summary>Starts reading and sending.</summary>
    /// <exception cref="InvalidOperationException">The connection was already started or closed.</exception>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, StateOpen, StateCreated) != StateCreated)
        {
            throw new InvalidOperationException("The fleet connection was already started or closed.");
        }

        // Registrations stop with the state change above; the loops start after the handlers are frozen.
        _handlers = _registrations.ToFrozenDictionary(StringComparer.Ordinal);
        Volatile.Write(ref _lastReceivedTimestamp, _time.GetTimestamp());

        var reader = Task.Run(ReadLoopAsync);
        var writer = Task.Run(WriteLoopAsync);
        var heartbeat = Task.Run(HeartbeatLoopAsync);
        _ = MonitorAsync(reader, writer, heartbeat);
    }

    /// <summary>Sends a request and waits for its response.</summary>
    /// <param name="method">The method.</param>
    /// <param name="parameters">The parameters, or <see langword="null"/>.</param>
    /// <param name="options">The idempotency key, trace and timeout of the request, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops waiting; the peer may still handle the request.</param>
    /// <returns>The result of the request, or <see langword="null"/> when it has none.</returns>
    /// <exception cref="ArgumentException"><paramref name="method"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout of <paramref name="options"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The connection was not started.</exception>
    /// <exception cref="FleetConnectionClosedException">The connection is closed or closed before the response.</exception>
    /// <exception cref="FleetException">The peer answered an error, or the request exceeds the frame limit (<see cref="FleetErrorCodes.FrameTooLarge"/>).</exception>
    /// <exception cref="TimeoutException">The response did not come in time.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<JsonElement?> SendRequestAsync(string method, JsonElement? parameters = null, FleetRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        var timeout = options?.Timeout ?? _options.RequestTimeout;
        FleetConnectionOptions.RequirePositiveOrInfinite(timeout, nameof(options));
        ThrowIfNotOpen();

        var id = Guid.CreateVersion7().ToString("N");
        var frame = SerializeFrame(new FleetRequest(id, method, parameters, options?.IdempotencyKey, options?.TraceParent ?? CurrentTraceParent()));
        var pending = new TaskCompletionSource<FleetResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        using var timeoutSource = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout, _time);
        using var linked = timeoutSource is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var token = linked?.Token ?? cancellationToken;
        try
        {
            // Registered first, checked second: a close either sees the pending request or is seen here.
            if (Volatile.Read(ref _state) != StateOpen) throw CreateClosedException();
            try
            {
                await _sendQueue.Writer.WriteAsync(frame, token).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw CreateClosedException();
            }

            var response = await pending.Task.WaitAsync(token).ConfigureAwait(false);
            if (response.Ok) return response.Result;
            throw new FleetException(response.Error ?? new FleetError(FleetErrorCodes.InternalError));
        }
        catch (OperationCanceledException exception) when (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The fleet request '{method}' got no response within {timeout}.", exception);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a request with typed parameters and result, and waits for its response.</summary>
    /// <typeparam name="TParams">The type of the parameters.</typeparam>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="method">The method.</param>
    /// <param name="parameters">The parameters.</param>
    /// <param name="parametersType">The source-generated contract of the parameters.</param>
    /// <param name="resultType">The source-generated contract of the result.</param>
    /// <param name="options">The idempotency key, trace and timeout of the request, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops waiting; the peer may still handle the request.</param>
    /// <returns>The result, or the default of <typeparamref name="TResult"/> when the response has none.</returns>
    /// <exception cref="ArgumentNullException">A contract is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The result does not read as <typeparamref name="TResult"/>.</exception>
    /// <inheritdoc cref="SendRequestAsync(string, JsonElement?, FleetRequestOptions?, CancellationToken)" path="/exception"/>
    public async Task<TResult?> SendRequestAsync<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> parametersType, JsonTypeInfo<TResult> resultType,
        FleetRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parametersType);
        ArgumentNullException.ThrowIfNull(resultType);
        var result = await SendRequestAsync(method, JsonSerializer.SerializeToElement(parameters, parametersType), options, cancellationToken).ConfigureAwait(false);
        return result is { } element ? element.Deserialize(resultType) : default;
    }

    /// <summary>Sends an event.</summary>
    /// <param name="fleetEvent">The event.</param>
    /// <param name="cancellationToken">Stops waiting for room in the send queue.</param>
    /// <returns>A task that completes when the event is queued.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fleetEvent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The event has no name.</exception>
    /// <exception cref="InvalidOperationException">The connection was not started.</exception>
    /// <exception cref="FleetConnectionClosedException">The connection is closed.</exception>
    /// <exception cref="FleetException">The event exceeds the frame limit (<see cref="FleetErrorCodes.FrameTooLarge"/>).</exception>
    public async ValueTask SendEventAsync(FleetEvent fleetEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fleetEvent);
        if (string.IsNullOrEmpty(fleetEvent.Event)) throw new ArgumentException("An event must have a name.", nameof(fleetEvent));
        ThrowIfNotOpen();
        var frame = SerializeFrame(fleetEvent);
        try
        {
            await _sendQueue.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw CreateClosedException();
        }
    }

    /// <summary>
    /// Closes the connection cleanly: pending requests fail at once, frames already queued are sent, then the close;
    /// the transport is aborted when the peer does not close in <see cref="FleetConnectionOptions.CloseTimeout"/>.
    /// </summary>
    /// <param name="reason">Why the connection closes, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops waiting for the close; the close goes on.</param>
    /// <returns>A task that completes when the connection is closed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task CloseAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        var info = new FleetConnectionCloseInfo(FleetErrorCodes.ConnectionClosed, reason, ClosedLocally: true);
        if (Interlocked.CompareExchange(ref _state, StateClosed, StateCreated) == StateCreated)
        {
            Interlocked.CompareExchange(ref _closeInfo, info, null);
            _shutdown.Cancel();
            _completion.TrySetResult(_closeInfo!);
            return;
        }

        BeginClose(info, reason);
        await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the connection cleanly if it is open, then disposes the transport.</summary>
    /// <returns>A task that completes when the transport is disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        var token = _shutdown.Token;
        var buffer = new ArrayBufferWriter<byte>(4096);
        try
        {
            while (true)
            {
                buffer.ResetWrittenCount();
                var type = await _transport.ReceiveAsync(buffer, _options.MaxFrameBytes, token).ConfigureAwait(false);
                if (type == FleetFrameType.Closed)
                {
                    BeginClose(new FleetConnectionCloseInfo(FleetErrorCodes.ConnectionClosed, "The peer closed the connection.", ClosedLocally: false), null);
                    return;
                }

                Volatile.Write(ref _lastReceivedTimestamp, _time.GetTimestamp());
                if (type != FleetFrameType.Text)
                {
                    Terminate(new FleetConnectionCloseInfo(FleetErrorCodes.ProtocolError, "This connection carries no binary frames.", ClosedLocally: true), abort: true);
                    return;
                }

                FleetMessage? message;
                try
                {
                    message = FleetMessageSerializer.Deserialize(buffer.WrittenSpan);
                }
                catch (JsonException exception)
                {
                    Terminate(new FleetConnectionCloseInfo(FleetErrorCodes.ProtocolError, "A frame is not a message of the fleet protocol.", ClosedLocally: true, exception), abort: true);
                    return;
                }

                if (!await DispatchAsync(message, token).ConfigureAwait(false)) return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (FleetException exception)
        {
            Terminate(new FleetConnectionCloseInfo(exception.Error.Code, exception.Error.Message, ClosedLocally: true, exception), abort: true);
        }
        catch (Exception exception)
        {
            // Whatever the transport throws (a reset socket, an aborted peer) ends the connection, never the process.
            Terminate(new FleetConnectionCloseInfo(FleetErrorCodes.TransportError, exception.Message, ClosedLocally: false, exception), abort: true);
        }
    }

    private async ValueTask<bool> DispatchAsync(FleetMessage? message, CancellationToken token)
    {
        switch (message)
        {
            case null:
                // A kind of frame from a newer version: ignored, as unknown fields are.
                return true;
            case FleetRequest request:
                return await OnRequestAsync(request, token).ConfigureAwait(false);
            case FleetResponse response:
                if (response.Id is not null && _pending.TryRemove(response.Id, out var pending)) pending.TrySetResult(response);
                return true;
            case FleetEvent fleetEvent:
                if (_eventHandler is null || Volatile.Read(ref _state) != StateOpen || string.IsNullOrEmpty(fleetEvent.Event)) return true;
                try
                {
                    await _eventHandler(fleetEvent, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Terminate(new FleetConnectionCloseInfo(FleetErrorCodes.InternalError, "The event handler failed.", ClosedLocally: true, exception), abort: true);
                    return false;
                }

                return true;
            case FleetPing ping:
                TryEnqueue(new FleetPong(ping.Sequence));
                return true;
            case FleetPong:
                return true;
            default:
                Terminate(new FleetConnectionCloseInfo(FleetErrorCodes.ProtocolError, $"A '{message.Type}' frame is out of place after the handshake.", ClosedLocally: true), abort: true);
                return false;
        }
    }

    private async ValueTask<bool> OnRequestAsync(FleetRequest request, CancellationToken token)
    {
        if (string.IsNullOrEmpty(request.Id))
        {
            Terminate(new FleetConnectionCloseInfo(FleetErrorCodes.ProtocolError, "A request has no id.", ClosedLocally: true), abort: true);
            return false;
        }

        // A closing connection sends nothing new: the peer's requests fail with its close.
        if (Volatile.Read(ref _state) != StateOpen) return true;
        if (string.IsNullOrEmpty(request.Method))
        {
            await EnqueueResponseAsync(FleetResponse.Failure(request.Id, new FleetError(FleetErrorCodes.InvalidRequest, null, "A request has no method.")), token).ConfigureAwait(false);
            return true;
        }

        if (!_handlers.TryGetValue(request.Method, out var handler))
        {
            await EnqueueResponseAsync(FleetResponse.Failure(request.Id, new FleetError(FleetErrorCodes.MethodNotFound, null, "No handler for this method.")), token).ConfigureAwait(false);
            return true;
        }

        await _handlerSlots.WaitAsync(token).ConfigureAwait(false);
        _ = RunHandlerAsync(request, handler, token);
        return true;
    }

    private async Task RunHandlerAsync(FleetRequest request, FleetRequestHandler handler, CancellationToken token)
    {
        try
        {
            // Off the reader loop, which goes on reading while the handler runs.
            await Task.Yield();
            FleetResponse response;
            try
            {
                var result = await handler(new FleetRequestContext(this, request), token).ConfigureAwait(false);
                response = FleetResponse.Success(request.Id, result);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                response = FleetResponse.Failure(request.Id, FleetErrors.FromException(exception));
            }

            await EnqueueResponseAsync(response, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _handlerSlots.Release();
        }
    }

    private async ValueTask EnqueueResponseAsync(FleetResponse response, CancellationToken token)
    {
        var frame = FleetMessageSerializer.Serialize(response);
        if (frame.Length > _options.MaxFrameBytes)
        {
            frame = FleetMessageSerializer.Serialize(FleetResponse.Failure(response.Id, new FleetError(FleetErrorCodes.FrameTooLarge, null, "The result exceeds the frame limit.")));
        }

        try
        {
            await _sendQueue.Writer.WriteAsync(frame, token).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Closing: the peer's request fails with the close.
        }
    }

    private async Task WriteLoopAsync()
    {
        var token = _shutdown.Token;
        var queue = _sendQueue.Reader;
        try
        {
            while (await queue.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (queue.TryRead(out var frame)) await _transport.SendAsync(frame, FleetFrameType.Text, token).ConfigureAwait(false);
            }

            // The queue completes on a clean close: the close follows the frames queued before it. A failure completes
            // it too, after the state is closed: the transport is aborted then, never closed as if all were well.
            if (Volatile.Read(ref _state) == StateClosing) await _transport.CloseAsync(Volatile.Read(ref _closeDescription), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminate(new FleetConnectionCloseInfo(FleetErrorCodes.TransportError, exception.Message, ClosedLocally: false, exception), abort: true);
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        var heartbeat = _options.HeartbeatInterval;
        var deadPeer = _options.DeadPeerTimeout;
        var infinite = Timeout.InfiniteTimeSpan;
        if (heartbeat == infinite && deadPeer == infinite) return;

        // Ticks often enough that a dead peer is seen within a quarter of its timeout after it.
        var tick = heartbeat == infinite ? deadPeer / 4 : deadPeer == infinite ? heartbeat : TimeSpan.FromTicks(Math.Min(heartbeat.Ticks, deadPeer.Ticks / 4));
        if (tick < TimeSpan.FromMilliseconds(1)) tick = TimeSpan.FromMilliseconds(1);
        var token = _shutdown.Token;
        var lastPing = _time.GetTimestamp();
        try
        {
            using var timer = new PeriodicTimer(tick, _time);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var now = _time.GetTimestamp();
                if (deadPeer != infinite && _time.GetElapsedTime(Volatile.Read(ref _lastReceivedTimestamp), now) >= deadPeer)
                {
                    Terminate(new FleetConnectionCloseInfo(FleetErrorCodes.PeerTimeout, $"Nothing came from the peer for {deadPeer}.", ClosedLocally: true), abort: true);
                    return;
                }

                if (heartbeat != infinite && Volatile.Read(ref _state) == StateOpen && _time.GetElapsedTime(lastPing, now) >= heartbeat - tick / 2)
                {
                    lastPing = now;
                    TryEnqueue(new FleetPing(Interlocked.Increment(ref _pingSequence)));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task MonitorAsync(Task reader, Task writer, Task heartbeat)
    {
        // The loops catch their own failures: they only end.
        await Task.WhenAll(reader, writer).ConfigureAwait(false);
        Terminate(new FleetConnectionCloseInfo(FleetErrorCodes.ConnectionClosed, null, ClosedLocally: false), abort: false);
        await heartbeat.ConfigureAwait(false);
        _completion.TrySetResult(Volatile.Read(ref _closeInfo)!);
    }

    private void BeginClose(FleetConnectionCloseInfo info, string? description)
    {
        if (Interlocked.CompareExchange(ref _state, StateClosing, StateOpen) != StateOpen) return;
        Volatile.Write(ref _closeDescription, description);
        Interlocked.CompareExchange(ref _closeInfo, info, null);
        FailPending();
        _sendQueue.Writer.TryComplete();
        _ = AbortAfterCloseTimeoutAsync();
    }

    private async Task AbortAfterCloseTimeoutAsync()
    {
        if (_options.CloseTimeout == Timeout.InfiniteTimeSpan) return;
        try
        {
            await Task.Delay(_options.CloseTimeout, _time, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The peer did not close in time: the close stands, the transport is cut.
        Terminate(Volatile.Read(ref _closeInfo)!, abort: true);
    }

    private void Terminate(FleetConnectionCloseInfo info, bool abort)
    {
        Interlocked.CompareExchange(ref _closeInfo, info, null);
        if (Interlocked.Exchange(ref _state, StateClosed) == StateClosed) return;
        if (abort) _transport.Abort();
        _shutdown.Cancel();
        _sendQueue.Writer.TryComplete();
        FailPending();
    }

    private void FailPending()
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending)) pending.TrySetException(CreateClosedException());
        }
    }

    private void TryEnqueue(FleetMessage message)
    {
        // A heartbeat behind a full queue is useless: it is dropped, not waited for.
        _sendQueue.Writer.TryWrite(FleetMessageSerializer.Serialize(message));
    }

    private byte[] SerializeFrame(FleetMessage message)
    {
        var frame = FleetMessageSerializer.Serialize(message);
        if (frame.Length > _options.MaxFrameBytes)
        {
            throw new FleetException(FleetErrorCodes.FrameTooLarge, null, $"A frame of {frame.Length} bytes exceeds the limit of {_options.MaxFrameBytes} bytes.");
        }

        return frame;
    }

    private FleetConnectionClosedException CreateClosedException()
        => new(Volatile.Read(ref _closeInfo) ?? new FleetConnectionCloseInfo(FleetErrorCodes.ConnectionClosed, "The connection is closing.", ClosedLocally: true));

    private void ThrowIfStarted()
    {
        if (Volatile.Read(ref _state) != StateCreated) throw new InvalidOperationException("Handlers are registered before the fleet connection starts.");
    }

    private void ThrowIfNotOpen()
    {
        var state = Volatile.Read(ref _state);
        if (state == StateCreated) throw new InvalidOperationException("The fleet connection is not started.");
        if (state != StateOpen) throw CreateClosedException();
    }

    private static string? CurrentTraceParent()
        => Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null;
}
