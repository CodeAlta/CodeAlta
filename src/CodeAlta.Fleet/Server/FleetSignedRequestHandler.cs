using System.Buffers.Text;
using System.Globalization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// Signs every request it sends with a device key, as the server's REST API takes them (<see cref="FleetSignedRequest"/>):
/// the method, the path, a time, a nonce and the hash of the body. It lets a client that knows nothing of the fleet,
/// such as the OTLP exporter of a node on the fleet's server route, reach the server as its device.
/// </summary>
/// <remarks>The body is read once and sent as bytes, so that what is signed is what is sent.</remarks>
public sealed class FleetSignedRequestHandler : DelegatingHandler
{
    private readonly DeviceIdentity _identity;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="FleetSignedRequestHandler"/> class.</summary>
    /// <param name="identity">The key of the device; the caller keeps and disposes it.</param>
    /// <param name="innerHandler">The handler that sends the requests.</param>
    /// <param name="time">The clock of the signatures; null for the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> or <paramref name="innerHandler"/> is null.</exception>
    public FleetSignedRequestHandler(DeviceIdentity identity, HttpMessageHandler innerHandler, TimeProvider? time = null) : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] body = [];
        if (request.Content is { } content)
        {
            using var stream = content.ReadAsStream(cancellationToken);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            body = buffer.ToArray();
        }

        Sign(request, body);
        return base.Send(request, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is { } content ? await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false) : [];
        Sign(request, body);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private void Sign(HttpRequestMessage request, byte[] body)
    {
        if (request.Content is { } content)
        {
            // The bytes read are the bytes sent, with the headers of the content they came with.
            var bytes = new ByteArrayContent(body);
            foreach (var header in content.Headers)
            {
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) bytes.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            request.Content = bytes;
            content.Dispose();
        }

        var time = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var nonce = FleetSignedRequest.CreateNonce();
        var signed = FleetSignedRequest.GetSignedBytes(request.Method.Method, request.RequestUri!.PathAndQuery, time, nonce, FleetSignedRequest.HashBody(body),
            _identity.DeviceId);
        request.Headers.Remove(FleetSignedRequest.DeviceHeader);
        request.Headers.Remove(FleetSignedRequest.TimestampHeader);
        request.Headers.Remove(FleetSignedRequest.NonceHeader);
        request.Headers.Remove(FleetSignedRequest.SignatureHeader);
        request.Headers.Add(FleetSignedRequest.DeviceHeader, _identity.DeviceId);
        request.Headers.Add(FleetSignedRequest.TimestampHeader, time.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(FleetSignedRequest.NonceHeader, nonce);
        request.Headers.Add(FleetSignedRequest.SignatureHeader, Base64Url.EncodeToString(_identity.SignData(signed)));
    }
}
