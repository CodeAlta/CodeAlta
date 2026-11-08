using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.ModelServers;

namespace CodeAlta.Fleet.Client;

public sealed partial class FleetNodeClient
{
    private readonly ConcurrentDictionary<string, Channel<FleetLlamaHttpChunk?>> _llamaAnswers = new(StringComparer.Ordinal);
    private event Action<long>? LlamaChanged;
    private int _llamaWatching;

    /// <summary>Reads the model host of the machine.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Its status.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<ModelHostStatus> GetModelServersAsync(CancellationToken cancellationToken = default)
        => CallAsync(FleetModelServerMethods.Status, new FleetEmpty(), FleetModelServersJsonContext.Default.FleetEmpty, ModelHostWireJson.Default.ModelHostStatus, null, cancellationToken);

    /// <summary>Asks the model host of the machine for an action.</summary>
    /// <param name="action">The action.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What it gave.</returns>
    /// <exception cref="FleetException">The node refused the action.</exception>
    public Task<ModelHostActionResult> DoModelServersAsync(ModelHostAction action, CancellationToken cancellationToken = default)
        => CallAsync(FleetModelServerMethods.Do, action, ModelHostWireJson.Default.ModelHostAction, ModelHostWireJson.Default.ModelHostActionResult, null, cancellationToken);

    /// <summary>Lists the servers of the machine this device may use.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The servers.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public Task<FleetLlamaServerList> ListModelServersAsync(CancellationToken cancellationToken = default)
        => CallAsync(FleetModelServerMethods.Servers, new FleetEmpty(), FleetModelServersJsonContext.Default.FleetEmpty, FleetModelServersJsonContext.Default.FleetLlamaServerList,
            null, cancellationToken);

    /// <summary>Follows the changes of the model host of the machine while this channel lasts.</summary>
    /// <param name="changed">Called with the revision of each change, at most twice a second.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Something that stops calling <paramref name="changed"/>.</returns>
    /// <exception cref="FleetException">The node refused the call.</exception>
    public async Task<IDisposable> WatchModelServersAsync(Action<long> changed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changed);
        LlamaChanged += changed;
        if (Interlocked.Exchange(ref _llamaWatching, 1) == 0)
        {
            try
            {
                await CallAsync(FleetModelServerMethods.Watch, new FleetEmpty(), FleetModelServersJsonContext.Default.FleetEmpty, FleetModelServersJsonContext.Default.FleetEmpty,
                    null, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Exchange(ref _llamaWatching, 0);
                LlamaChanged -= changed;
                throw;
            }
        }

        return new Unsubscribe(() => LlamaChanged -= changed);
    }

    /// <summary>Sends an inference request to a server of the machine; the body of its answer streams as it comes.</summary>
    /// <param name="server">The server.</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="FleetException">The node refused the request.</exception>
    /// <exception cref="HttpRequestException">The body of the request is too large for the channel.</exception>
    public async Task<HttpResponseMessage> SendModelServerRequestAsync(string server, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body is { Length: > FleetNodeService.MaxLlamaBodyBytes }) throw new HttpRequestException("The request is too large to reach a model server of another machine.");
        var id = Guid.NewGuid().ToString("N");
        // The parts may come before the answer of the call: they wait here.
        var parts = Channel.CreateUnbounded<FleetLlamaHttpChunk?>(new UnboundedChannelOptions { SingleReader = true });
        _llamaAnswers[id] = parts;
        var path = request.RequestUri is { } uri ? uri.PathAndQuery : "/";
        FleetLlamaHttpResponse answer;
        try
        {
            answer = await CallAsync(FleetModelServerMethods.Http,
                new FleetLlamaHttpRequest(id, server, request.Method.Method, path, request.Content?.Headers.ContentType?.ToString(), body is null ? null : Convert.ToBase64String(body)),
                FleetModelServersJsonContext.Default.FleetLlamaHttpRequest, FleetModelServersJsonContext.Default.FleetLlamaHttpResponse, null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _llamaAnswers.TryRemove(id, out _);
            throw;
        }

        var response = new HttpResponseMessage((HttpStatusCode)answer.Status) { RequestMessage = request };
        response.Content = new StreamContent(new AnswerStream(this, id, parts.Reader, cancellationToken));
        if (answer.ContentType is { } type && MediaTypeHeaderValue.TryParse(type, out var contentType)) response.Content.Headers.ContentType = contentType;
        return response;
    }

    private void OnModelServersEvent(FleetEvent fleetEvent)
    {
        switch (fleetEvent.Event)
        {
            case FleetModelServerMethods.HttpChunkEvent when fleetEvent.Payload?.Deserialize(FleetModelServersJsonContext.Default.FleetLlamaHttpChunk) is { } chunk:
                if (_llamaAnswers.TryGetValue(chunk.RequestId, out var parts)) parts.Writer.TryWrite(chunk);
                break;
            case FleetModelServerMethods.HttpEndEvent when fleetEvent.Payload?.Deserialize(FleetModelServersJsonContext.Default.FleetLlamaHttpEnd) is { } end:
                if (_llamaAnswers.TryGetValue(end.RequestId, out var ended))
                {
                    if (end.Error is { } error and not "cancelled") ended.Writer.TryComplete(new IOException("The model server stopped answering: " + error));
                    else ended.Writer.TryComplete();
                }

                break;
            case FleetModelServerMethods.ChangedEvent when fleetEvent.Payload?.Deserialize(FleetModelServersJsonContext.Default.FleetLlamaChanged) is { } changed:
                LlamaChanged?.Invoke(changed.Revision);
                break;
        }
    }

    private async Task CancelModelServerRequestAsync(string id)
    {
        if (!_llamaAnswers.TryRemove(id, out _)) return;
        if (_channel.Connection.State != FleetConnectionState.Open) return;
        try
        {
            await CallAsync(FleetModelServerMethods.HttpCancel, new FleetLlamaHttpCancel(id), FleetModelServersJsonContext.Default.FleetLlamaHttpCancel,
                FleetModelServersJsonContext.Default.FleetEmpty, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetException or TimeoutException or IOException or ObjectDisposedException or FleetConnectionClosedException)
        {
        }
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    // The body of an answer, read from the parts the node sends in order; disposing it before the end stops the request.
    private sealed class AnswerStream(FleetNodeClient client, string id, ChannelReader<FleetLlamaHttpChunk?> parts, CancellationToken requestToken) : Stream
    {
        private byte[] _current = [];
        private int _offset;
        private bool _ended;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                if (_ended) return 0;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestToken);
                if (!await parts.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                {
                    _ended = true;
                    client._llamaAnswers.TryRemove(id, out _);
                    return 0;
                }

                if (parts.TryRead(out var chunk) && chunk is not null)
                {
                    _current = Convert.FromBase64String(chunk.Data);
                    _offset = 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_ended) _ = client.CancelModelServerRequestAsync(id);
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// The transport of a provider to a model server of another machine: each HTTP request goes over the fleet channel as
/// <c>llama.http</c>, and the node calls its server on loopback with its key. The address the provider names (its host) is
/// not used: only the path is.
/// </summary>
public sealed class FleetModelServerHandler : HttpMessageHandler
{
    private readonly Func<CancellationToken, Task<FleetNodeClient>> _client;
    private readonly string _server;

    /// <summary>Creates the transport.</summary>
    /// <param name="client">Gives the client of the machine now (a channel that reconnects gives a new one).</param>
    /// <param name="server">The server on that machine.</param>
    public FleetModelServerHandler(Func<CancellationToken, Task<FleetNodeClient>> client, string server)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        _client = client;
        _server = server;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        FleetNodeClient client;
        try
        {
            client = await _client(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException)
        {
            throw new HttpRequestException($"The machine of the model server cannot be reached: {exception.Message}", exception, HttpStatusCode.ServiceUnavailable);
        }

        try
        {
            return await client.SendModelServerRequestAsync(_server, request, cancellationToken).ConfigureAwait(false);
        }
        catch (FleetException exception)
        {
            var status = exception.Error.Code is FleetErrorCodes.Forbidden ? HttpStatusCode.Forbidden : exception.Error.Code is FleetModelServerMethods.NotFound ? HttpStatusCode.NotFound : HttpStatusCode.ServiceUnavailable;
            return new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent("{\"error\":{\"code\":" + ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"message\":"
                    + JsonSerializer.Serialize(exception.Error.Message ?? "The machine refused the request.", ModelServersWireStrings.Default.String) + "}}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
        catch (Exception exception) when (exception is FleetConnectionClosedException or ObjectDisposedException or TimeoutException)
        {
            throw new HttpRequestException($"The channel to the model server closed: {exception.Message}", exception, HttpStatusCode.ServiceUnavailable);
        }
    }
}

/// <summary>The JSON of plain strings.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class ModelServersWireStrings : System.Text.Json.Serialization.JsonSerializerContext;
