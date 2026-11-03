using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Server.Api;

/// <summary>The metadata of an endpoint that a device calls before the server knows its key (setup, enrollment) or that needs no device (health).</summary>
internal sealed class AllowUnknownDeviceAttribute : Attribute;

/// <summary>
/// Reads the body of every call to the REST API, keeps its hash for the signature, and authenticates the calling
/// device, unless the endpoint is for a device the server does not know yet.
/// </summary>
internal sealed class DeviceAuthenticationMiddleware(RequestDelegate next, RequestAuthenticator authenticator)
{
    /// <summary>The largest body of a request to the REST API.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    private const string DeviceKey = "codealta.device";
    private const string SignedKey = "codealta.signed";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(context).ConfigureAwait(false);
        var signed = SignedRequestData.From(context.Request, FleetSignedRequest.HashBody(body));
        context.Items[SignedKey] = signed;
        if (context.GetEndpoint()?.Metadata.GetMetadata<AllowUnknownDeviceAttribute>() is null)
        {
            var device = await authenticator.AuthenticateAsync(signed, context.RequestAborted).ConfigureAwait(false);
            context.Items[DeviceKey] = device;
            System.Diagnostics.Activity.Current?.SetTag("codealta.device.short_id", Telemetry.ServerMetrics.ShortId(device.Id));
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>Gets the device a request was authenticated as.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The device.</returns>
    /// <exception cref="InvalidOperationException">The endpoint does not authenticate its devices.</exception>
    public static DeviceRecord GetDevice(HttpContext context)
        => context.Items[DeviceKey] as DeviceRecord ?? throw new InvalidOperationException("The request was not authenticated.");

    /// <summary>Gets what a request carries for its signature.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The data.</returns>
    /// <exception cref="InvalidOperationException">The request did not go through the middleware.</exception>
    public static SignedRequestData GetSigned(HttpContext context)
        => context.Items[SignedKey] as SignedRequestData ?? throw new InvalidOperationException("The request was not read by the middleware.");

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        var request = context.Request;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBodyBytes;
        // Whether a body comes is what the server's body feature says, not the headers: an HTTP/2 or HTTP/3 request
        // sends its body in DATA frames without Content-Length or Transfer-Encoding, and that body is signed too.
        if (request.ContentLength is 0 || context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false }) return [];
        if (request.ContentLength > MaxBodyBytes) throw TooLarge();

        request.EnableBuffering(MaxBodyBytes);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) throw TooLarge();
            buffer.Write(chunk, 0, read);
        }

        request.Body.Position = 0;
        return buffer.ToArray();
    }

    private static CoordinatorException TooLarge()
        => new(StatusCodes.Status413PayloadTooLarge, CoordinatorErrorCodes.InvalidBody, $"A request body is at most {MaxBodyBytes} bytes.");
}
