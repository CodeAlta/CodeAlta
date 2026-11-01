using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace CodeAlta.Browser.Cdp;

/// <summary>
/// The commands and events of one target of a <see cref="CdpConnection"/>, or of the browser itself. The typed
/// domains (<c>session.Page</c>, <c>session.Runtime</c>…) are generated from the protocol.
/// </summary>
internal sealed partial class CdpSession
{
    // Events wait here for their handlers. A queue this deep is never full unless a handler blocks, which is a bug.
    private const int QueueCapacity = 16384;

    private readonly CdpConnection _connection;
    // An event, or a barrier of FlushAsync, which goes through the same queue so that it completes after the events before it.
    private readonly Channel<(string Method, JsonElement Parameters, TaskCompletionSource? Barrier)> _events = Channel.CreateBounded<(string, JsonElement, TaskCompletionSource?)>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TaskCompletionSource, byte> _pending = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Action<JsonElement>>> _handlers = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal CdpSession(CdpConnection connection, string? id)
    {
        _connection = connection;
        Id = id;
        _ = Task.Run(DeliverAsync, CancellationToken.None);
    }

    /// <summary>Gets the id of the session, or null for the session of the browser.</summary>
    public string? Id { get; }

    /// <summary>Gets the connection the session runs on.</summary>
    public CdpConnection Connection => _connection;

    /// <summary>Gets a task that completes when the session detaches or its connection ends.</summary>
    public Task Completed => _completed.Task;

    /// <summary>Sends a command and reads its result.</summary>
    /// <exception cref="CdpException">The browser refused the command, or the session ended first.</exception>
    public async Task<TResult> SendAsync<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> parametersInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        var result = await _connection.SendAsync(Id, method, parameters, parametersInfo, cancellationToken).ConfigureAwait(false);
        return result.Deserialize(resultInfo) ?? throw new CdpException(method, 0, "The browser sent no result.");
    }

    /// <summary>Sends a command whose result says nothing.</summary>
    /// <exception cref="CdpException">The browser refused the command, or the session ended first.</exception>
    public Task SendAsync<TParams>(string method, TParams parameters, JsonTypeInfo<TParams> parametersInfo, CancellationToken cancellationToken)
        => _connection.SendAsync(Id, method, parameters, parametersInfo, cancellationToken);

    /// <summary>Calls a handler for each event of a name, in the order the browser sent them, until the result is disposed.</summary>
    /// <remarks>A handler runs on the delivery loop of the session: it must not wait for a command of the same connection.</remarks>
    public IDisposable On<TEvent>(string method, JsonTypeInfo<TEvent> info, Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Action<JsonElement> raw = parameters =>
        {
            if (parameters.ValueKind != JsonValueKind.Object) return;
            if (parameters.Deserialize(info) is { } value) handler(value);
        };
        lock (_gate)
        {
            if (!_handlers.TryGetValue(method, out var list)) _handlers[method] = list = [];
            list.Add(raw);
        }

        return new Subscription(this, method, raw);
    }

    internal void Post(string method, JsonElement parameters)
    {
        // The read loop of the connection writes: waiting here holds back that loop while a handler is slow.
        if (!_events.Writer.TryWrite((method, parameters, null))) _events.Writer.WriteAsync((method, parameters, null)).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Waits until the handlers ran for every event that came before now. The answer of a command can arrive while the
    /// events sent before it still wait for their handlers: a heap snapshot is complete once this ends.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[barrier] = 0;
        try
        {
            await _events.Writer.WriteAsync((string.Empty, default, barrier), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // The session ended: every event before now was handled or will never be.
            barrier.TrySetResult();
        }

        try
        {
            await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(barrier, out _);
        }
    }

    internal void Complete() => _events.Writer.TryComplete();

    private async Task DeliverAsync()
    {
        try
        {
            await foreach (var (method, parameters, barrier) in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (barrier is not null)
                {
                    barrier.TrySetResult();
                    continue;
                }

                Action<JsonElement>[] handlers;
                lock (_gate)
                {
                    if (!_handlers.TryGetValue(method, out var list) || list.Count == 0) continue;
                    handlers = [.. list];
                }

                foreach (var handler in handlers)
                {
                    try
                    {
                        handler(parameters);
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
                    {
                        // An event this version of the bindings cannot read is skipped, never the ones after it.
                    }
                }
            }
        }
        finally
        {
            // The barriers of an ended session complete: no event will come before them any more.
            foreach (var barrier in _pending.Keys) barrier.TrySetResult();
            _completed.TrySetResult();
        }
    }

    private sealed class Subscription(CdpSession session, string method, Action<JsonElement> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (session._gate)
            {
                if (session._handlers.TryGetValue(method, out var list)) list.Remove(handler);
            }
        }
    }
}
