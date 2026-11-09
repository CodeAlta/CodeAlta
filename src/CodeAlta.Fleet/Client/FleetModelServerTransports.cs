using System.Net;
using System.Net.Http.Headers;
using CodeAlta.ModelServers;

namespace CodeAlta.Fleet.Client;

/// <summary>
/// Where the providers of an instance reach the model servers they name (<c>server = "&lt;machine&gt;/&lt;server&gt;"</c>): those of this
/// machine (<c>local</c>) on loopback with their key, those of a paired machine through the fleet channel. The parts are given
/// once the instance has them: a provider registered before asks again at each request.
/// </summary>
public sealed class FleetModelServerTransports
{
    /// <summary>The machine of the servers of this instance.</summary>
    public const string LocalMachine = "local";

    /// <summary>Gets or sets the model host of this machine; null while there is none.</summary>
    public ModelHost? Local { get; set; }

    /// <summary>Gets or sets what gives the client of a paired machine, by its id; null for an instance without a fleet.</summary>
    public Func<string, Func<CancellationToken, Task<FleetNodeClient>>?>? Remote { get; set; }

    /// <summary>The transport of a provider that names a model server.</summary>
    /// <param name="server">The server, <c>&lt;machine&gt;/&lt;server&gt;</c>.</param>
    /// <returns>The transport, or null for a text that names none.</returns>
    public HttpMessageHandler? Resolve(string server)
    {
        if (server?.Split('/') is not [{ Length: > 0 } machine, { Length: > 0 } id]) return null;
        if (machine == LocalMachine) return new LocalModelServerHandler(() => Local, id) { Name = server };
        return new FleetModelServerHandler(cancellationToken => Remote?.Invoke(machine) is { } client
            ? client(cancellationToken)
            : throw new InvalidOperationException($"The machine {machine} is not paired with this computer."), id) { Name = server };
    }
}

/// <summary>
/// The transport of a provider to a model server of this machine: the request goes to its loopback port with its key, read
/// at each request, and the list of models carries the context each one last loaded with. A request of inference first takes
/// a slot of its model (<see cref="ModelHost.SlotsOf"/>), in turn with the requests other machines relay here, and holds it
/// until its answer is read to its end.
/// </summary>
public sealed class LocalModelServerHandler : DelegatingHandler
{
    private readonly Func<ModelHost?> _host;
    private readonly string _server;

    /// <summary>Gets the server as the provider names it (<c>local/coder</c>), which a session that waits for a slot reads.</summary>
    public string? Name { get; init; }

    /// <summary>Creates the transport.</summary>
    /// <param name="host">Gives the model host of this machine.</param>
    /// <param name="server">The server.</param>
    public LocalModelServerHandler(Func<ModelHost?> host, string server)
        : base(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _server = server;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = _host();
        if (host?.Endpoint(_server) is not { } endpoint)
            return Unavailable(request, $"The model server {_server} is not on this machine.");
        var path = request.RequestUri is { } uri ? uri.PathAndQuery : "/";
        request.RequestUri = new Uri(endpoint.Address + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Key);
        IDisposable? slot = null;
        if (ModelServerSlotWait.TakesSlot(request.Method.Method, path) && request.Content is { } content)
        {
            // The model it names decides its slots: the body is read once, and sent as it was.
            var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var copy = new ByteArrayContent(body);
            foreach (var header in content.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Content = copy;
            if (host.SlotsOf(_server, ModelServerSlotWait.ModelOf(body)) is { } slots)
                slot = await ModelServerSlotWait.AcquireAsync(slots.Gate, Name ?? _server, ModelServerSlotWait.UpdatesOf(request), cancellationToken).ConfigureAwait(false);
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            slot?.Dispose();
            return Unavailable(request, $"The model server {_server} does not answer: is it started? ({exception.Message})");
        }
        catch
        {
            slot?.Dispose();
            throw;
        }

        if (slot is not null) return await ModelServerSlotWait.HoldUntilReadAsync(response, slot, cancellationToken).ConfigureAwait(false);

        if (request.Method == HttpMethod.Get && path.StartsWith("/v1/models", StringComparison.Ordinal) && response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType;
            response.Content = new ByteArrayContent(host.AdjustModelList(_server, json));
            response.Content.Headers.ContentType = type;
        }

        return response;
    }

    private static HttpResponseMessage Unavailable(HttpRequestMessage request, string message)
        => new(HttpStatusCode.ServiceUnavailable)
        {
            RequestMessage = request,
            Content = new StringContent("{\"error\":{\"code\":503,\"message\":\"" + message.Replace("\"", "'", StringComparison.Ordinal) + "\"}}", System.Text.Encoding.UTF8, "application/json"),
        };
}
