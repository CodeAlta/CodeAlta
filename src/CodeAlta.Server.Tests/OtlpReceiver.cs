using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Fleet.Tests.Otlp;

/// <summary>An OTLP request a <see cref="OtlpReceiver"/> received.</summary>
/// <param name="Signal">The signal of its path: <c>traces</c>, <c>metrics</c> or <c>logs</c>.</param>
/// <param name="Path">Its path.</param>
/// <param name="Headers">Its headers, names in lower case.</param>
/// <param name="Body">Its body, as received.</param>
internal sealed record OtlpRequest(string Signal, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    /// <summary>Gets the attributes of the first resource of the request, whose values are strings.</summary>
    public IReadOnlyDictionary<string, string> Resource => OtlpProtobuf.ResourceAttributes(Body);

    /// <summary>Gets the <c>service.name</c> of its resource.</summary>
    public string? ServiceName => Resource.GetValueOrDefault("service.name");

    /// <summary>Gets whether the body holds a text, such as the name of a metric or a span.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether its UTF-8 bytes are in the body.</returns>
    public bool Contains(string text) => Body.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;
}

/// <summary>
/// A collector of a test: an OTLP/HTTP endpoint on the loopback that records each request per signal and answers 200
/// (or what the test says), so that a test sees what a host exports without a real collector.
/// </summary>
internal sealed class OtlpReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<OtlpRequest> _requests = new();
    private readonly SemaphoreSlim _arrived = new(0);

    private OtlpReceiver(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    /// <summary>Gets the base address of the collector: <c>http://127.0.0.1:port</c>.</summary>
    public Uri Endpoint { get; private set; }

    /// <summary>Gets or sets the status the receiver answers.</summary>
    public int Status { get; set; } = StatusCodes.Status200OK;

    /// <summary>Gets the requests received so far.</summary>
    public IReadOnlyList<OtlpRequest> Requests => [.. _requests];

    /// <summary>Starts a receiver on a free port of the loopback.</summary>
    /// <returns>The receiver.</returns>
    public static async Task<OtlpReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 64 * 1024 * 1024);
        var app = builder.Build();
        OtlpReceiver? receiver = null;
        app.MapPost("/{**path}", async (HttpContext context) =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            var path = context.Request.Path.Value ?? "";
            var signal = path.TrimEnd('/').Split('/')[^1];
            var headers = context.Request.Headers.ToDictionary(static pair => pair.Key.ToLowerInvariant(), static pair => pair.Value.ToString(), StringComparer.Ordinal);
            receiver!.Add(new OtlpRequest(signal, path, headers, buffer.ToArray()));
            context.Response.StatusCode = receiver.Status;
            context.Response.ContentType = "application/x-protobuf";
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        receiver = new OtlpReceiver(app, new Uri(address));
        return receiver;
    }

    /// <summary>Waits for a request that matches, among those received and those to come.</summary>
    /// <param name="match">What the request must be.</param>
    /// <param name="timeout">How long to wait; 30 seconds when null.</param>
    /// <returns>The first request that matches.</returns>
    /// <exception cref="TimeoutException">None came in time.</exception>
    public async Task<OtlpRequest> WaitAsync(Func<OtlpRequest, bool> match, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TestTime.Seconds(30));
        while (true)
        {
            if (_requests.FirstOrDefault(match) is { } found) return found;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                var seen = string.Join(", ", _requests.Select(static request => $"{request.Path} ({request.ServiceName})"));
                throw new TimeoutException($"No OTLP request matched in time; received: {(seen.Length == 0 ? "none" : seen)}.");
            }

            await _arrived.WaitAsync(left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250)); // not a Task: a slice of the deadline above, which names what it waited for
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _arrived.Dispose();
    }

    private void Add(OtlpRequest request)
    {
        _requests.Enqueue(request);
        _arrived.Release();
    }
}

/// <summary>Reads the little of OTLP protobuf a test needs: the string attributes of the first resource of an export request.</summary>
internal static class OtlpProtobuf
{
    /// <summary>
    /// Reads the attributes of the first resource of an <c>Export{Trace,Metrics,Logs}ServiceRequest</c>: field 1 is a
    /// <c>Resource{Spans,Metrics,Logs}</c>, whose field 1 is the <c>Resource</c>, whose field 1 repeats a <c>KeyValue</c>
    /// (key: field 1, value: an <c>AnyValue</c> in field 2, whose field 1 is a string).
    /// </summary>
    /// <param name="body">The body.</param>
    /// <returns>The attributes whose value is a string.</returns>
    public static IReadOnlyDictionary<string, string> ResourceAttributes(byte[] body)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var resourceScope = Field(body, 1);
        if (resourceScope is null) return attributes;
        var resource = Field(resourceScope, 1);
        if (resource is null) return attributes;
        foreach (var pair in Fields(resource, 1))
        {
            var key = Field(pair, 1);
            var value = Field(pair, 2);
            var text = value is null ? null : Field(value, 1);
            if (key is not null && text is not null) attributes[Encoding.UTF8.GetString(key)] = Encoding.UTF8.GetString(text);
        }

        return attributes;
    }

    private static byte[]? Field(byte[] message, int number) => Fields(message, number).FirstOrDefault();

    // The length-delimited fields of a number; the others are skipped.
    private static IEnumerable<byte[]> Fields(byte[] message, int number)
    {
        var position = 0;
        while (position < message.Length)
        {
            var tag = ReadVarint(message, ref position);
            var wire = (int)(tag & 7);
            var field = (int)(tag >> 3);
            switch (wire)
            {
                case 0:
                    ReadVarint(message, ref position);
                    break;
                case 1:
                    position += 8;
                    break;
                case 5:
                    position += 4;
                    break;
                case 2:
                    var length = (int)ReadVarint(message, ref position);
                    if (field == number) yield return message[position..(position + length)];
                    position += length;
                    break;
                default:
                    yield break;
            }
        }
    }

    private static ulong ReadVarint(byte[] message, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; position < message.Length && shift < 64; shift += 7)
        {
            var b = message[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
        }

        return value;
    }
}
