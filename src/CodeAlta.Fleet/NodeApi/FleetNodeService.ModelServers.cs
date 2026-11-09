using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.ModelServers;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>The methods and events of the model servers of a machine (<c>doc/model-servers.md</c>).</summary>
public static class FleetModelServerMethods
{
    /// <summary>Everything the model host of the machine says: its GPUs, builds, models, downloads and servers. View.</summary>
    public const string Status = "control.llama.status";

    /// <summary>An action on the model host (<see cref="ModelHostAction"/>). Manage; a read action needs View.</summary>
    public const string Do = "control.llama.do";

    /// <summary>Asks for <see cref="ChangedEvent"/> while the channel lasts. View.</summary>
    public const string Watch = "control.llama.watch";

    /// <summary>The model host changed: read its status again.</summary>
    public const string ChangedEvent = "llama.changed";

    /// <summary>A download or an install or update of the model host ended (<see cref="FleetLlamaEnded"/>), sent to the channels that watch.</summary>
    public const string EndedEvent = "llama.ended";

    /// <summary>The servers this device may use, with their models. Models or Manage.</summary>
    public const string Servers = "llama.servers.list";

    /// <summary>An inference request to a server of the machine; its body comes back as <see cref="HttpChunkEvent"/> then <see cref="HttpEndEvent"/>. Models or Manage.</summary>
    public const string Http = "llama.http";

    /// <summary>Stops an inference request.</summary>
    public const string HttpCancel = "llama.http.cancel";

    /// <summary>A part of the body of the answer of an inference request.</summary>
    public const string HttpChunkEvent = "llama.http.chunk";

    /// <summary>The end of the answer of an inference request.</summary>
    public const string HttpEndEvent = "llama.http.end";

    /// <summary>An inference request waits for a slot of its model (<see cref="FleetLlamaHttpQueued"/>): its place, each time it changes.</summary>
    public const string HttpQueuedEvent = "llama.http.queued";

    /// <summary>The error of a server, a model or a download that is not on the machine.</summary>
    public const string NotFound = "llama.not_found";
}

/// <summary>A server another computer may use.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Purpose"><c>chat</c>, <c>embedding</c> or <c>rerank</c>.</param>
/// <param name="Models">Its models.</param>
public sealed record FleetLlamaServerInfo(string Id, string Name, string State, string Purpose, IReadOnlyList<ServerModelStatus> Models);

/// <summary>The servers another computer may use.</summary>
/// <param name="Servers">The servers.</param>
public sealed record FleetLlamaServerList(IReadOnlyList<FleetLlamaServerInfo> Servers);

/// <summary>An inference request.</summary>
/// <param name="RequestId">Its id, chosen by the caller.</param>
/// <param name="Server">The server.</param>
/// <param name="Method"><c>GET</c> or <c>POST</c>.</param>
/// <param name="Path">The path, such as <c>/v1/chat/completions</c>.</param>
/// <param name="ContentType">The type of the body, or null.</param>
/// <param name="Body">The body in base64, or null.</param>
public sealed record FleetLlamaHttpRequest(string RequestId, string Server, string Method, string Path, string? ContentType, string? Body);

/// <summary>The start of the answer of an inference request: its body follows as events.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="ContentType">The type of the body.</param>
public sealed record FleetLlamaHttpResponse(int Status, string? ContentType);

/// <summary>A part of the body of an answer.</summary>
/// <param name="RequestId">The request.</param>
/// <param name="Sequence">Its order, from 0.</param>
/// <param name="Data">The bytes in base64.</param>
public sealed record FleetLlamaHttpChunk(string RequestId, long Sequence, string Data);

/// <summary>The end of an answer.</summary>
/// <param name="RequestId">The request.</param>
/// <param name="Count">How many parts were sent.</param>
/// <param name="Error">Why it ended early, or null.</param>
public sealed record FleetLlamaHttpEnd(string RequestId, long Count, string? Error);

/// <summary>Stops an inference request.</summary>
/// <param name="RequestId">The request.</param>
public sealed record FleetLlamaHttpCancel(string RequestId);

/// <summary>An inference request that waits for a slot of its model on the machine of its server.</summary>
/// <param name="RequestId">The request.</param>
/// <param name="Before">How many requests are before it, from this machine and the others.</param>
public sealed record FleetLlamaHttpQueued(string RequestId, int Before);

/// <summary>The model host changed.</summary>
/// <param name="Revision">Its revision now.</param>
public sealed record FleetLlamaChanged(long Revision);

/// <summary>A download, or an install or update of llama.cpp, ended on the machine.</summary>
/// <param name="Download">The download that ended, or null.</param>
/// <param name="Task">The install or update that ended, or null.</param>
public sealed record FleetLlamaEnded(DownloadInfo? Download, HostTask? Task);

/// <summary>The JSON of the model servers on the node API.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetLlamaServerList))]
[JsonSerializable(typeof(FleetLlamaHttpRequest))]
[JsonSerializable(typeof(FleetLlamaHttpResponse))]
[JsonSerializable(typeof(FleetLlamaHttpChunk))]
[JsonSerializable(typeof(FleetLlamaHttpEnd))]
[JsonSerializable(typeof(FleetLlamaHttpCancel))]
[JsonSerializable(typeof(FleetLlamaHttpQueued))]
[JsonSerializable(typeof(FleetLlamaChanged))]
[JsonSerializable(typeof(FleetLlamaEnded))]
[JsonSerializable(typeof(FleetEmpty))]
public sealed partial class FleetModelServersJsonContext : JsonSerializerContext;

// The model servers of the machine (doc/model-servers.md): its owner and the devices that manage it read and change them,
// the devices with Models use them. An inference request goes only to a server whose access lets the person's computers in,
// only on the paths of inference (never the router's management, which downloads and starts processes), with the key the
// node holds; its answer streams back as events, in parts that fit a frame.
public sealed partial class FleetNodeService
{
    /// <summary>The largest body of an inference request: what fits in a frame of the channel with its envelope.</summary>
    public const int MaxLlamaBodyBytes = 3 * 1024 * 1024;

    private const int LlamaChunkBytes = 48 * 1024;
    private static readonly string[] LlamaPaths =
    [
        "/v1/chat/completions", "/v1/completions", "/v1/responses", "/v1/embeddings", "/v1/rerank", "/v1/messages", "/v1/messages/count_tokens", "/v1/models",
        "/props", "/health", "/infill", "/tokenize", "/detokenize", "/apply-template", "/completion", "/embedding", "/rerank", "/slots",
    ];

    private readonly ConcurrentDictionary<(FleetConnection, string), CancellationTokenSource> _llamaRequests = new();
    private readonly ConcurrentDictionary<FleetConnection, LlamaWatch> _llamaWatches = new();
    private HttpClient? _llamaHttp;

    private void RegisterModelServers(FleetNodeCaller caller, bool owner)
    {
        if (_options.ModelServers is null) return;
        var read = owner ? Access.Owner : Access.View;
        var any = owner ? Access.Owner : Access.Paired;
        On(caller, FleetModelServerMethods.Status, read, FleetModelServersJsonContext.Default.FleetEmpty, ModelHostWireJson.Default.ModelHostStatus,
            (_, _, _) => Task.FromResult(ModelServersHost.GetStatus()), optionalParams: true);
        On(caller, FleetModelServerMethods.Do, any, ModelHostWireJson.Default.ModelHostAction, ModelHostWireJson.Default.ModelHostActionResult, LlamaDoAsync);
        On(caller, FleetModelServerMethods.Watch, read, FleetModelServersJsonContext.Default.FleetEmpty, FleetModelServersJsonContext.Default.FleetEmpty, LlamaWatchAsync,
            optionalParams: true);
        if (owner) return;
        On(caller, FleetModelServerMethods.Servers, Access.Paired, FleetModelServersJsonContext.Default.FleetEmpty, FleetModelServersJsonContext.Default.FleetLlamaServerList,
            LlamaServersAsync, optionalParams: true);
        On(caller, FleetModelServerMethods.Http, Access.Paired, FleetModelServersJsonContext.Default.FleetLlamaHttpRequest,
            FleetModelServersJsonContext.Default.FleetLlamaHttpResponse, LlamaHttpAsync);
        On(caller, FleetModelServerMethods.HttpCancel, Access.Paired, FleetModelServersJsonContext.Default.FleetLlamaHttpCancel,
            FleetModelServersJsonContext.Default.FleetEmpty, LlamaHttpCancelAsync);
    }

    private ModelHost ModelServersHost => _options.ModelServers
        ?? throw new FleetException(FleetModelServerMethods.NotFound, null, "This machine runs no model server.");

    private async Task<ModelHostActionResult> LlamaDoAsync(FleetNodeCall call, ModelHostAction action, CancellationToken cancellationToken)
    {
        // Reading a log needs View; every change needs Manage (the local control of the owner may do all).
        if (call.Device is { } device)
        {
            var needed = ModelHostActions.IsRead(action.Action) ? DevicePermissions.View : DevicePermissions.Manage;
            if ((device.Permissions & (needed | DevicePermissions.Manage)) == 0)
                throw new FleetException(FleetErrorCodes.Forbidden, null, $"This device may not do this on the node (it needs {Describe(needed)[0]}).");
        }

        try
        {
            var result = await ModelHostActions.RunAsync(ModelServersHost, action, call.Device is null ? "owner" : "device " + call.Caller.DeviceId, cancellationToken).ConfigureAwait(false);
            if (!ModelHostActions.IsRead(action.Action)) LogInfo($"{(call.Device is null ? "The owner" : call.Caller.DeviceId)} asked the model host to {action.Action}.");
            return result;
        }
        catch (ModelHostException exception)
        {
            throw new FleetException(exception.Code == "not_found" ? FleetModelServerMethods.NotFound : FleetErrorCodes.InvalidParams, null, exception.Message);
        }
    }

    private Task<FleetEmpty> LlamaWatchAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
    {
        var connection = call.Caller.Connection;
        if (_llamaWatches.ContainsKey(connection)) return Task.FromResult(new FleetEmpty());
        var watch = new LlamaWatch(this, connection);
        if (_llamaWatches.TryAdd(connection, watch))
        {
            ModelServersHost.Changed += watch.OnChanged;
            ModelServersHost.DownloadEnded += watch.OnDownloadEnded;
            ModelServersHost.TaskEnded += watch.OnTaskEnded;
        }

        return Task.FromResult(new FleetEmpty());
    }

    private Task<FleetLlamaServerList> LlamaServersAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
    {
        RequireModels(call);
        var servers = ModelServersHost.ListServers().Where(static view => view.Definition.Access == "my-computers")
            .Select(static view => new FleetLlamaServerInfo(view.Definition.Id, view.Definition.Name, view.Status.State, view.Definition.Purpose, view.Status.Models)).ToList();
        return Task.FromResult(new FleetLlamaServerList(servers));
    }

    private async Task<FleetLlamaHttpResponse> LlamaHttpAsync(FleetNodeCall call, FleetLlamaHttpRequest parameters, CancellationToken cancellationToken)
    {
        RequireModels(call);
        if (parameters.RequestId is not { Length: > 0 and <= 64 }) throw new FleetException(FleetErrorCodes.InvalidParams, null, "A request needs an id of 1 to 64 characters.");
        var server = ModelServersHost.ListServers().FirstOrDefault(view => view.Definition.Id == parameters.Server)
            ?? throw new FleetException(FleetModelServerMethods.NotFound, null, $"There is no model server {parameters.Server} on this machine.");
        if (server.Definition.Access != "my-computers")
            throw new FleetException(FleetErrorCodes.Forbidden, null, $"{server.Definition.Name} serves this machine only.");
        var path = parameters.Path ?? "";
        var bare = path.Split('?', 2)[0];
        if (parameters.Method is not ("GET" or "POST") || !LlamaPaths.Contains(bare, StringComparer.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            throw new FleetException(FleetErrorCodes.Forbidden, null, $"{parameters.Method} {bare} is not a request of inference.");
        byte[]? body = null;
        if (parameters.Body is { Length: > 0 } encoded)
        {
            try { body = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new FleetException(FleetErrorCodes.InvalidParams, null, "The body is not base64."); }
            if (body.Length > MaxLlamaBodyBytes) throw new FleetException(FleetErrorCodes.InvalidParams, null, "The body of the request is too large for the channel.");
        }

        if (server.Status.State is "stopped" or "waiting" or "failed")
            throw new FleetException(FleetNodeErrorCodes.ProvidersUnavailable, FleetErrorHints.Retry, $"{server.Definition.Name} does not run: {server.Status.Reason ?? server.Status.State}.");
        var (address, key) = ModelServersHost.Endpoint(server.Definition.Id)
            ?? throw new FleetException(FleetNodeErrorCodes.ProvidersUnavailable, FleetErrorHints.Retry, $"{server.Definition.Name} does not run: {server.Status.Reason ?? server.Status.State}.");
        var http = _llamaHttp ??= new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var request = new HttpRequestMessage(new HttpMethod(parameters.Method), address + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(parameters.ContentType ?? "application/json", out var type) ? type : new MediaTypeHeaderValue("application/json");
        }

        var connection = call.Caller.Connection;
        var stop = new CancellationTokenSource();
        if (!_llamaRequests.TryAdd((connection, parameters.RequestId), stop))
        {
            stop.Dispose();
            request.Dispose();
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "A request with this id is under way.");
        }

        // The channel that asked closes: the request stops, and the server is not left generating for nobody.
        _ = connection.Completion.ContinueWith(_ =>
        {
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        HttpResponseMessage response;
        IDisposable? slot = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
            // A slot of its model first, in turn with the sessions of this machine and the requests of the others: the caller is
            // told its place, and its channel is seen alive meanwhile.
            if (body is not null && Client.ModelServerSlotWait.TakesSlot(parameters.Method, path)
                && ModelServersHost.SlotsOf(server.Definition.Id, Client.ModelServerSlotWait.ModelOf(body)) is { } slots)
            {
                var requestId = parameters.RequestId;
                slot = await slots.Gate.AcquireAsync((before, token) => SendLlamaQueuedAsync(connection, requestId, before, token), linked.Token).ConfigureAwait(false);
            }

            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            slot?.Dispose();
            _llamaRequests.TryRemove((connection, parameters.RequestId), out _);
            stop.Dispose();
            request.Dispose();
            throw new FleetException(FleetNodeErrorCodes.ProvidersUnavailable, FleetErrorHints.Retry, $"{server.Definition.Name} does not answer: {exception.Message}");
        }

        if (parameters.Method == "GET" && bare == "/v1/models" && response.IsSuccessStatusCode)
        {
            // The list carries the context each model last loaded with, which a provider sizes its compaction by.
            var listed = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType;
            response.Content = new ByteArrayContent(ModelServersHost.AdjustModelList(server.Definition.Id, listed));
            response.Content.Headers.ContentType = type;
        }

        _ = Task.Run(() => PumpLlamaAnswerAsync(connection, parameters.RequestId, request, response, stop, slot), CancellationToken.None);
        return new FleetLlamaHttpResponse((int)response.StatusCode, response.Content.Headers.ContentType?.ToString());
    }

    // The place of a request that waits for a slot, told to the channel that asked it.
    private static async ValueTask SendLlamaQueuedAsync(FleetConnection connection, string requestId, int before, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendEventAsync(new FleetEvent(FleetModelServerMethods.HttpQueuedEvent,
                JsonSerializer.SerializeToElement(new FleetLlamaHttpQueued(requestId, before), FleetModelServersJsonContext.Default.FleetLlamaHttpQueued)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetConnectionClosedException or ObjectDisposedException or IOException)
        {
            // The channel closed: its request stops waiting with it.
        }
    }

    private async Task PumpLlamaAnswerAsync(FleetConnection connection, string requestId, HttpRequestMessage request, HttpResponseMessage response, CancellationTokenSource stop,
        IDisposable? slot)
    {
        long sequence = 0;
        string? error = null;
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
            var buffer = new byte[LlamaChunkBytes];
            int read;
            while ((read = await body.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) > 0)
            {
                var chunk = new FleetLlamaHttpChunk(requestId, sequence++, Convert.ToBase64String(buffer, 0, read));
                await connection.SendEventAsync(new FleetEvent(FleetModelServerMethods.HttpChunkEvent,
                    JsonSerializer.SerializeToElement(chunk, FleetModelServersJsonContext.Default.FleetLlamaHttpChunk)), stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            error = "cancelled";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or FleetConnectionClosedException or ObjectDisposedException)
        {
            error = exception.Message;
        }
        finally
        {
            // The answer ended, or its channel closed: the slot goes to the next request.
            slot?.Dispose();
            _llamaRequests.TryRemove((connection, requestId), out _);
            response.Dispose();
            request.Dispose();
            stop.Dispose();
        }

        try
        {
            await connection.SendEventAsync(new FleetEvent(FleetModelServerMethods.HttpEndEvent,
                JsonSerializer.SerializeToElement(new FleetLlamaHttpEnd(requestId, sequence, error), FleetModelServersJsonContext.Default.FleetLlamaHttpEnd))).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetConnectionClosedException or ObjectDisposedException or IOException or OperationCanceledException)
        {
        }
    }

    private Task<FleetEmpty> LlamaHttpCancelAsync(FleetNodeCall call, FleetLlamaHttpCancel parameters, CancellationToken cancellationToken)
    {
        if (_llamaRequests.TryGetValue((call.Caller.Connection, parameters.RequestId ?? ""), out var stop))
        {
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        return Task.FromResult(new FleetEmpty());
    }

    private static void RequireModels(FleetNodeCall call)
    {
        if (call.Device is { } device && (device.Permissions & (DevicePermissions.Models | DevicePermissions.Manage)) == 0)
            throw new FleetException(FleetErrorCodes.Forbidden, null, "This device may not use the model servers of the node (it needs models).");
    }

    // Sends the change of the model host to one channel, at most twice a second, while the channel lasts.
    private sealed class LlamaWatch(FleetNodeService service, FleetConnection connection)
    {
        private int _pending;

        public void OnChanged()
        {
            if (Interlocked.Exchange(ref _pending, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(500).ConfigureAwait(false);
                Interlocked.Exchange(ref _pending, 0);
                if (connection.State != FleetConnectionState.Open)
                {
                    Stop();
                    return;
                }

                try
                {
                    var revision = service.ModelServersHost.GetStatus().Revision;
                    await connection.SendEventAsync(new FleetEvent(FleetModelServerMethods.ChangedEvent,
                        JsonSerializer.SerializeToElement(new FleetLlamaChanged(revision), FleetModelServersJsonContext.Default.FleetLlamaChanged))).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is FleetConnectionClosedException or ObjectDisposedException or IOException or OperationCanceledException)
                {
                    Stop();
                }
            });
        }

        // The end of a download or of an install is sent at once: the computer that asked tells its person and its session.
        public void OnDownloadEnded(DownloadInfo download) => SendEnded(new FleetLlamaEnded(download, null));

        public void OnTaskEnded(HostTask task) => SendEnded(new FleetLlamaEnded(null, task));

        private void SendEnded(FleetLlamaEnded ended)
        {
            if (connection.State != FleetConnectionState.Open)
            {
                Stop();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await connection.SendEventAsync(new FleetEvent(FleetModelServerMethods.EndedEvent,
                        JsonSerializer.SerializeToElement(ended, FleetModelServersJsonContext.Default.FleetLlamaEnded))).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is FleetConnectionClosedException or ObjectDisposedException or IOException or OperationCanceledException)
                {
                    Stop();
                }
            });
        }

        private void Stop()
        {
            if (!service._llamaWatches.TryRemove(connection, out _) || service._options.ModelServers is not { } host) return;
            host.Changed -= OnChanged;
            host.DownloadEnded -= OnDownloadEnded;
            host.TaskEnded -= OnTaskEnded;
        }
    }
}
