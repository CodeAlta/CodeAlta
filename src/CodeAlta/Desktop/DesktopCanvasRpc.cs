using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// What carries the frames of the RPC of a canvas between the host and the page: the page cannot be reached by a NeoAstra host of its
/// own, so the frames go through the one service of the page that the canvases share.
/// </summary>
internal interface ICanvasRpcCarrier
{
    /// <summary>Carries one frame to the page. It waits while the page has too much waiting for it, so a slow page slows a stream down.</summary>
    /// <param name="instanceId">The instance whose connection the frame belongs to.</param>
    /// <param name="connection">The connection.</param>
    /// <param name="frame">The frame, as JSON.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="IOException">No page watches: the frame cannot be delivered, and the connection ends.</exception>
    ValueTask SendAsync(string instanceId, string connection, string frame, CancellationToken cancellationToken);

    /// <summary>Tells the page that a connection ended, after the frames sent for it. It does nothing when no page watches.</summary>
    /// <param name="instanceId">The instance.</param>
    /// <param name="connection">The connection.</param>
    /// <param name="reason">A short token that says why: <c>closed</c>, <c>replaced</c>, <c>client_close</c>, <c>failed</c>.</param>
    void Closed(string instanceId, string connection, string reason);
}

/// <summary>
/// The calls of the script of one canvas instance, as its plugin registered them, until the instance is sealed. It is the
/// <see cref="IPluginCanvasRpc"/> the plugin sees; the <see cref="CanvasRpcEndpoint"/> turns it into a NeoAstra host.
/// </summary>
internal sealed class PluginRpcRegistry : IPluginCanvasRpc
{
    /// <summary>The most calls, streams and events that a plugin may register for one instance.</summary>
    internal const int MaximumRegistrations = 256;

    /// <summary>The type information of the JSON values that cross the NeoAstra host: the typing is the plugin's, in its handlers.</summary>
    private static readonly JsonTypeInfo<JsonElement> ElementInfo = (JsonTypeInfo<JsonElement>)JsonSerializerOptions.Default.GetTypeInfo(typeof(JsonElement));

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly Lock _gate = new();
    private readonly List<Action<NeoRpcBuilder>> _registrations = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly string _pluginKey;
    private readonly string _canvasId;
    private readonly Logger? _logger;
    private readonly CancellationToken _closed;
    private JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);
    private CanvasRpcEndpoint? _endpoint;
    private bool _sealed;

    /// <summary>Creates the registry of an instance.</summary>
    /// <param name="pluginKey">The runtime key of the plugin, for its log lines.</param>
    /// <param name="canvasId">The canvas, for its log lines.</param>
    /// <param name="logger">The logger of the plugin, or null.</param>
    /// <param name="closed">A token cancelled when the instance closes or the plugin stops: every handler's token follows it.</param>
    internal PluginRpcRegistry(string pluginKey, string canvasId, Logger? logger, CancellationToken closed)
    {
        _pluginKey = pluginKey;
        _canvasId = canvasId;
        _logger = logger;
        _closed = closed;
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public JsonSerializerOptions JsonOptions
    {
        get { lock (_gate) return _options; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                EnsureOpen();
                _options = value;
            }
        }
    }

    /// <summary>The number of calls and streams registered.</summary>
    internal int Count
    {
        get { lock (_gate) return _registrations.Count; }
    }

    /// <summary>Whether the plugin registered nothing more can be registered.</summary>
    internal bool IsSealed
    {
        get { lock (_gate) return _sealed; }
    }

    /// <summary>The logger of the plugin, or null.</summary>
    internal Logger? Logger => _logger;

    /// <summary>Ends the registration: later registrations throw. Returns what was registered.</summary>
    /// <returns>The registrations, to apply to a NeoAstra builder.</returns>
    internal IReadOnlyList<Action<NeoRpcBuilder>> Seal()
    {
        lock (_gate)
        {
            _sealed = true;
            return [.. _registrations];
        }
    }

    /// <summary>Gives the registry the endpoint that sends its events.</summary>
    internal void Attach(CanvasRpcEndpoint endpoint) => Volatile.Write(ref _endpoint, endpoint);

    /// <inheritdoc />
    public void Handle<TRequest, TResult>(string name, PluginRpcHandler<TRequest, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(name, builder => builder.AddCommand<JsonElement, JsonElement>(name, async (request, _, cancellationToken) =>
        {
            // The plugin's code runs on the thread pool, not on the thread of the page's call that carried the frame.
            await Task.Yield();
            var value = ReadRequest<TRequest>(request);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed);
            try
            {
                var result = await handler(value, linked.Token).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(result, JsonOptions);
            }
            catch (Exception exception)
            {
                throw Map(exception, "call", name);
            }
        }, ElementInfo, ElementInfo));
    }

    /// <inheritdoc />
    public void Handle<TRequest>(string name, PluginRpcVoidHandler<TRequest> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(name, builder => builder.AddCommand<JsonElement>(name, async (request, _, cancellationToken) =>
        {
            await Task.Yield();
            var value = ReadRequest<TRequest>(request);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed);
            try
            {
                await handler(value, linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw Map(exception, "call", name);
            }
        }, ElementInfo));
    }

    /// <inheritdoc />
    public void Stream<TRequest, TItem>(string name, PluginRpcStreamHandler<TRequest, TItem> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(name, builder => builder.AddChannelCommand<JsonElement, JsonElement>(name, (request, _, _) =>
        {
            // A request that does not match is an error of the call, not of the stream.
            var value = ReadRequest<TRequest>(request);
            return ValueTask.FromResult(new NeoRpcChannel<JsonElement>(Items(name, value, handler), ElementInfo));
        }, ElementInfo));
    }

    /// <inheritdoc />
    public ValueTask PublishAsync<T>(string name, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!PluginRpc.IsValidName(name)) throw new ArgumentException("The name is 1 to 64 of a-z, 0-9, '.', '-' and '_', starting with a letter or a digit.", nameof(name));
        if (Volatile.Read(ref _endpoint) is not { } endpoint) return ValueTask.CompletedTask;
        byte[] json;
        try
        {
            json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            Failure("event", name, exception);
            return ValueTask.CompletedTask;
        }

        return endpoint.PublishAsync(name, json, cancellationToken);
    }

    /// <summary>Logs a failure of the plugin's code, in the logger of the plugin. Nothing of it crosses to the page.</summary>
    internal void Failure(string what, string name, Exception exception)
        => _logger?.Error(exception, $"The {what} '{name}' of the canvas '{_canvasId}' of the plugin '{_pluginKey}' failed");

    /// <summary>Logs a line in the logger of the plugin.</summary>
    internal void Warn(string text) => _logger?.Warn($"Canvas '{_canvasId}' of the plugin '{_pluginKey}': {text}");

    private async IAsyncEnumerable<JsonElement> Items<TRequest, TItem>(string name, TRequest request, PluginRpcStreamHandler<TRequest, TItem> handler,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed);
        IAsyncEnumerator<TItem> items;
        try
        {
            items = handler(request, linked.Token).GetAsyncEnumerator(linked.Token);
        }
        catch (Exception exception)
        {
            throw Failed(exception, name);
        }

        await using (items.ConfigureAwait(false))
        {
            while (true)
            {
                JsonElement element;
                try
                {
                    if (!await items.MoveNextAsync().ConfigureAwait(false)) yield break;
                    element = JsonSerializer.SerializeToElement(items.Current, JsonOptions);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    yield break;
                }
                catch (Exception exception)
                {
                    throw Failed(exception, name);
                }

                yield return element;
            }
        }

        // What ends a stream with an error is logged here, and the pump tells the script only that the stream failed.
        Exception Failed(Exception exception, string streamName)
        {
            Failure("stream", streamName, exception);
            return exception;
        }
    }

    private TRequest ReadRequest<TRequest>(JsonElement request)
    {
        var options = JsonOptions;
        try
        {
            // A call without input reads as an empty object: a request type whose members are optional needs no argument.
            if (request.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) request = EmptyObject;
            return request.Deserialize<TRequest>(options) ?? throw new JsonException("The request is null.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new NeoRpcException(NeoRpcErrorCodes.InvalidRequest, "The request does not match what the plugin expects.");
        }
    }

    // What a handler throws, as what the script may see: the plugin's own errors as written, a cancellation as it is, the rest as a failure without detail.
    private Exception Map(Exception exception, string what, string name)
    {
        switch (exception)
        {
            case OperationCanceledException:
            case NeoRpcException:
                return exception;
            case PluginRpcException shown:
                return new NeoRpcException(shown.Code, Safe(shown.Message), shown.Retryable);
            default:
                Failure(what, name, exception);
                return new NeoRpcException(NeoRpcErrorCodes.InternalError, "The handler of the plugin failed.");
        }
    }

    // One line of at most 400 characters, as NeoAstra writes an error message.
    internal static string Safe(string message)
    {
        var builder = new StringBuilder(Math.Min(message.Length, 400));
        foreach (var value in message)
        {
            if (builder.Length == 400) break;
            builder.Append(char.IsControl(value) ? ' ' : value);
        }

        var text = builder.ToString().Trim();
        return text.Length == 0 ? "The call failed." : text;
    }

    private void Register(string name, Action<NeoRpcBuilder> register)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!PluginRpc.IsValidName(name)) throw new ArgumentException("The name is 1 to 64 of a-z, 0-9, '.', '-' and '_', starting with a letter or a digit.", nameof(name));
        lock (_gate)
        {
            EnsureOpen();
            if (_registrations.Count >= MaximumRegistrations) throw new InvalidOperationException($"A canvas registers at most {MaximumRegistrations} calls and streams.");
            if (!_names.Add(name)) throw new ArgumentException($"The name '{name}' is registered already.", nameof(name));
            _registrations.Add(register);
        }
    }

    private void EnsureOpen()
    {
        if (_sealed) throw new InvalidOperationException("The calls of a canvas are registered in the handler that opens it, before it returns the view.");
    }
}

/// <summary>
/// The NeoAstra host of one canvas instance and its session: it serves the calls of the script of the tab, over the frames that the page carries.
/// </summary>
/// <remarks>
/// <para>
/// The host is built when the instance opens, from what its plugin registered, and lives as long as the instance. The page connects
/// with <see cref="Connect"/>, which opens a session the page names with an identifier the host chose: a frame of another
/// identifier is refused, so a page that reloaded, or a second connection of the same tab, never talks to the session of the first.
/// There is one session at a time; connecting again ends the one before it.
/// </para>
/// <para>
/// The limits are those of the NeoAstra host: a frame of 1 MiB, 8 calls at once, 8 streams, 8 subscriptions, 8 items in flight on a
/// stream, 200 calls a second with a burst of 400, and a session that keeps being refused for being too fast is closed.
/// </para>
/// </remarks>
internal sealed class CanvasRpcEndpoint : IAsyncDisposable
{
    /// <summary>The largest frame the page may send, in bytes.</summary>
    internal const int MaximumFrameBytes = 1024 * 1024;

    /// <summary>The largest frame the host carries to the page, in characters: a result above it is replaced by an error.</summary>
    internal const int MaximumOutboundFrameChars = 4 * 1024 * 1024;

    /// <summary>The name of the NeoAstra event that carries the events of the plugin: each one is <c>{ name, value }</c>.</summary>
    internal const string EventsName = "alta.events";

    private readonly string _instanceId;
    private readonly ICanvasRpcCarrier _carrier;
    private readonly PluginRpcRegistry _registry;
    private readonly NeoRpcHost _host;
    private readonly NeoRpcEvent<JsonElement> _events;
    private readonly Lock _gate = new();
    private readonly List<Task> _ending = [];
    private Connection? _current;
    private bool _disposed;

    /// <summary>Builds the host of an instance from what its plugin registered. The registry is sealed.</summary>
    /// <param name="instanceId">The instance.</param>
    /// <param name="registry">What the plugin registered.</param>
    /// <param name="carrier">Carries the frames to the page.</param>
    /// <exception cref="ArgumentException">A registration is not valid for the host.</exception>
    internal CanvasRpcEndpoint(string instanceId, PluginRpcRegistry registry, ICanvasRpcCarrier carrier)
    {
        _instanceId = instanceId;
        _registry = registry;
        _carrier = carrier;
        var builder = new NeoRpcBuilder(new NeoRpcOptions
        {
            // The frames carry no generated contract: the plugin's names are the contract.
            ContractHash = string.Empty,
            Release = true,
            MaximumFrameBytes = MaximumFrameBytes,
            MaximumConcurrentInvocations = 16,
            MaximumConcurrentInvocationsPerSession = 8,
            MaximumChannelsPerSession = 8,
            MaximumSubscriptionsPerSession = 8,
            MaximumUnacknowledgedChannelItems = 8,
            MaximumQueuedEventsPerSubscription = 64,
            MaximumQueuedEventBytesPerSubscription = MaximumFrameBytes,
            MaximumRetainedRequestIds = 100_000,
            RequestRatePerSecond = 200,
            RequestRateBurst = 400,
            AbuseClosureThreshold = 500,
            InvocationTimeout = TimeSpan.FromSeconds(30),
            DiagnosticSink = new CanvasRpcDiagnostics(registry),
        });
        foreach (var register in registry.Seal()) register(builder);
        _events = builder.AddEvent(EventsName, (JsonTypeInfo<JsonElement>)JsonSerializerOptions.Default.GetTypeInfo(typeof(JsonElement)),
            new NeoRpcEventOptions { OverflowBehavior = NeoRpcOverflowBehavior.DropOldest });
        _host = builder.Build();
        registry.Attach(this);
    }

    /// <summary>The identifier of the connection that is open now, or null.</summary>
    internal string? CurrentConnection
    {
        get { lock (_gate) return _current?.Id; }
    }

    /// <summary>Opens a session for the page, and ends the one that was open.</summary>
    /// <param name="generation">The number of the watch of the page that opens it: a watch that ends drops the sessions of its generation and the ones before.</param>
    /// <returns>The identifier of the connection, which every frame after it names; null when the endpoint is closed.</returns>
    internal string? Connect(int generation)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            var id = Base64UrlId();
            var session = _host.OpenSession(new NeoRpcSessionIdentity("canvas:" + _instanceId, id), (json, cancellationToken) => SendAsync(id, json, cancellationToken));
            var previous = _current;
            var created = new Connection(id, session, generation);
            _current = created;
            if (previous is not null) _ending.Add(EndAsync(previous, "replaced"));
            // A session that NeoAstra closes by itself (a script that keeps calling too fast, a frame the page could not take) is ended here too, and the page is told.
            session.Closed.Register(() => OnSessionClosed(created));
            return id;
        }
    }

    /// <summary>Gives the session of a connection the frames the page sent. A call is not waited for: its answer is a frame sent later.</summary>
    /// <param name="connection">The connection the frames were sent on.</param>
    /// <param name="frames">The frames, as JSON, in the order they were written.</param>
    /// <returns><c>ok</c>; <c>closed</c> when the connection is not the open one; <c>payload_too_large</c> when a frame was above the limit (the others were given).</returns>
    internal string Receive(string connection, IReadOnlyList<string> frames)
    {
        NeoRpcSession session;
        lock (_gate)
        {
            if (_disposed || _current is not { } current || !string.Equals(current.Id, connection, StringComparison.Ordinal)) return "closed";
            session = current.Session;
        }

        var status = "ok";
        foreach (var frame in frames)
        {
            if (frame is null || frame.Length > MaximumFrameBytes || Encoding.UTF8.GetByteCount(frame) > MaximumFrameBytes)
            {
                status = "payload_too_large";
                continue;
            }

            // Not awaited: an invoke completes with its answer. The session starts the call before the first await, so a cancel that follows in the same batch finds it.
            _ = ObserveAsync(session.ReceiveAsync(frame, CancellationToken.None));
        }

        return status;
    }

    /// <summary>Ends a connection the page closed.</summary>
    /// <param name="connection">The connection.</param>
    /// <returns>False when it is not the open one.</returns>
    internal bool Close(string connection)
    {
        lock (_gate)
        {
            if (_disposed || _current is not { } current || !string.Equals(current.Id, connection, StringComparison.Ordinal)) return false;
            _current = null;
            _ending.Add(EndAsync(current, "client_close"));
            return true;
        }
    }

    /// <summary>Ends the session that is open, because the page that opened it is gone. The host stays: a page that comes back connects again.</summary>
    /// <param name="upToGeneration">The newest generation of a watch that is gone: a session of a later generation belongs to a page that watches.</param>
    internal void DropSession(int upToGeneration)
    {
        lock (_gate)
        {
            if (_disposed || _current is not { } current || current.Generation > upToGeneration) return;
            _current = null;
            _ending.Add(EndAsync(current, "page_gone"));
        }
    }

    /// <summary>Sends an event to the script that is connected.</summary>
    /// <param name="name">The name of the event.</param>
    /// <param name="valueJson">The value, as JSON.</param>
    /// <param name="cancellationToken">Cancels the publication.</param>
    internal async ValueTask PublishAsync(string name, byte[] valueJson, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed || _current is null) return;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WritePropertyName("value");
            writer.WriteRawValue(valueJson, skipInputValidation: true);
            writer.WriteEndObject();
        }

        try
        {
            await _events.PublishAsync(JsonSerializer.Deserialize<JsonElement>(buffer.WrittenSpan), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or JsonException)
        {
            // The instance closed meanwhile: an event is not kept.
        }
    }

    /// <summary>Ends the endpoint without waiting: the instance closed, or its plugin was replaced.</summary>
    internal void Retire()
        => _ = Task.Run(async () =>
        {
            try { await DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { _registry.Failure("session", "retire", exception); }
        });

    /// <summary>Ends the session and the host, and tells the page.</summary>
    public async ValueTask DisposeAsync()
    {
        Connection? current;
        Task[] ending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            current = _current;
            _current = null;
            ending = [.. _ending];
        }

        if (current is not null) await EndAsync(current, "closed").ConfigureAwait(false);
        try { await _host.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { _registry.Failure("host", "dispose", exception); }
        await Task.WhenAll(ending).ConfigureAwait(false);
    }

    private void OnSessionClosed(Connection connection)
    {
        lock (_gate)
        {
            if (_disposed || _current != connection) return;
            _current = null;
            _ending.Add(EndAsync(connection, "session_closed"));
        }
    }

    private ValueTask SendAsync(string connection, string json, CancellationToken cancellationToken)
    {
        if (json.Length > MaximumOutboundFrameChars)
        {
            // A result that is too large is an error for its call; any other frame that is too large ends the session.
            json = OversizedResult(json) ?? throw new InvalidOperationException("A frame is too large to carry to the page.");
            _registry.Warn("a result was too large to carry to the page and was replaced by an error.");
        }

        return _carrier.SendAsync(_instanceId, connection, json, cancellationToken);
    }

    private async Task EndAsync(Connection connection, string reason)
    {
        try { await connection.Session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { _registry.Failure("session", "end", exception); }
        _carrier.Closed(_instanceId, connection.Id, reason);
    }

    private async Task ObserveAsync(ValueTask task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException) { /* The session ended. */ }
        catch (Exception exception) { _registry.Failure("frame", "receive", exception); }
    }

    // The frame that answers the call of an oversized result, read from the start of the frame: {"neoastra":1,"kind":"result","id":"...",...}.
    private static string? OversizedResult(string json)
    {
        var head = Encoding.UTF8.GetBytes(json.AsSpan(0, Math.Min(json.Length, 600)).ToString());
        var reader = new Utf8JsonReader(head, isFinalBlock: false, default);
        string? kind = null, id = null;
        try
        {
            while ((kind is null || id is null) && reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                var isKind = reader.ValueTextEquals("kind"u8);
                var isId = reader.ValueTextEquals("id"u8);
                if (!isKind && !isId) continue;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) return null;
                if (isKind) kind = reader.GetString();
                else id = reader.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        if (kind != "result" || string.IsNullOrEmpty(id)) return null;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("neoastra", 1);
            writer.WriteString("kind", "result");
            writer.WriteString("id", id);
            writer.WriteBoolean("ok", false);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("code", NeoRpcErrorCodes.PayloadTooLarge);
            writer.WriteString("message", "The result is too large to carry to the page.");
            writer.WriteBoolean("retryable", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Base64UrlId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_');

    private sealed record Connection(string Id, NeoRpcSession Session, int Generation);
}

/// <summary>Writes what the NeoAstra host of a canvas says about its session in the logger of the plugin: each code at most once in ten seconds.</summary>
internal sealed class CanvasRpcDiagnostics(PluginRpcRegistry registry) : INeoRpcDiagnosticSink
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _last = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Write(NeoRpcDiagnostic diagnostic)
    {
        if (diagnostic.Level < NeoRpcDiagnosticLevel.Warning) return;
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_last.TryGetValue(diagnostic.Code, out var before) && Stopwatch.GetElapsedTime(before, now) < Quiet) return;
            _last[diagnostic.Code] = now;
        }

        registry.Warn($"the connection of its script: {diagnostic.Code} {diagnostic.Message}");
    }
}
