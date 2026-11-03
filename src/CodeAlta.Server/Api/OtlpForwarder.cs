using System.Diagnostics;
using System.Net.Http.Headers;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Api;

/// <summary>
/// The OTLP/HTTP endpoint of the server (<c>/otlp/v1/{traces,metrics,logs}</c>) for the fleet's server route: a request
/// signed by an approved device of the organization, as the REST API's are, is forwarded byte for byte to the collector
/// of the fleet's setting, with the headers only the server holds. The server forwards and never stores; it answers what
/// the collector answered.
/// </summary>
internal sealed class OtlpForwarder : IDisposable
{
    /// <summary>The largest OTLP request forwarded.</summary>
    public const int MaxBodyBytes = 8 * 1024 * 1024;

    private const int MaxAnswerBytes = 64 * 1024;

    private readonly ServerTelemetry _telemetry;
    private readonly RequestAuthenticator _authenticator;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="OtlpForwarder"/> class.</summary>
    /// <param name="telemetry">The telemetry of the server: where to forward, and its instruments.</param>
    /// <param name="authenticator">Checks the signature of each request.</param>
    /// <param name="logger">The log.</param>
    /// <param name="handler">The HTTP handler toward the collector; null for the usual one.</param>
    public OtlpForwarder(ServerTelemetry telemetry, RequestAuthenticator authenticator, ILogger logger, HttpMessageHandler? handler = null)
    {
        _telemetry = telemetry;
        _authenticator = authenticator;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Serves a request of a machine.</summary>
    /// <param name="context">The request.</param>
    /// <param name="signal">The signal of its path.</param>
    /// <returns>A task that completes once it is answered.</returns>
    /// <exception cref="CoordinatorException">The request is not signed by an approved device, too large, or the fleet does not forward this signal.</exception>
    public async Task HandleAsync(HttpContext context, string signal)
    {
        // A forwarded batch is not traced: its span would only describe the forwarding of other spans.
        if (Activity.Current is { } activity)
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            activity.IsAllDataRequested = false;
        }

        if (!FleetTelemetrySignals.IsKnown(signal)) throw CoordinatorException.NotFound(CoordinatorErrorCodes.TelemetryNotForwarded, "The signals are traces, metrics and logs.");
        var body = await ReadBodyAsync(context).ConfigureAwait(false);
        // The key first: who is not a device of the organization learns nothing of the setting.
        await _authenticator.AuthenticateAsync(SignedRequestData.From(context.Request, FleetSignedRequest.HashBody(body)), context.RequestAborted).ConfigureAwait(false);
        if (_telemetry.Forward is not { } target || !target.Signals.Contains(signal))
        {
            _telemetry.Metrics.Forwarded(signal, "not_forwarded", 0);
            throw CoordinatorException.NotFound(CoordinatorErrorCodes.TelemetryNotForwarded, $"The fleet does not send its {signal} through this server.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, FleetTelemetryPaths.SignalAddress(target.Endpoint, signal)) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type) ? type : new MediaTypeHeaderValue("application/x-protobuf");
        if (context.Request.Headers.ContentEncoding.ToString() is { Length: > 0 } encoding) request.Content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);
        foreach (var (name, value) in target.Headers) request.Headers.TryAddWithoutValidation(name, value);
        try
        {
            using var answer = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
            _telemetry.Metrics.Forwarded(signal, answer.IsSuccessStatusCode ? "ok" : ((int)answer.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                answer.IsSuccessStatusCode ? body.Length : 0);
            context.Response.StatusCode = (int)answer.StatusCode;
            if (answer.Content.Headers.ContentType is { } answerType) context.Response.ContentType = answerType.ToString();
            var bytes = await answer.Content.ReadAsByteArrayAsync(context.RequestAborted).ConfigureAwait(false);
            if (bytes.Length <= MaxAnswerBytes) await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            _telemetry.Metrics.Forwarded(signal, "unreachable", 0);
            _logger.LogWarning("The collector {Endpoint} did not take the {Signal} of a machine: {Reason}", target.Endpoint, signal, exception.Message);
            // The exporter of the machine retries a 502 and a 503 later.
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        var request = context.Request;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBodyBytes;
        if (request.ContentLength > MaxBodyBytes)
            throw new CoordinatorException(StatusCodes.Status413PayloadTooLarge, CoordinatorErrorCodes.InvalidBody, $"An OTLP request is at most {MaxBodyBytes} bytes.");
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                throw new CoordinatorException(StatusCodes.Status413PayloadTooLarge, CoordinatorErrorCodes.InvalidBody, $"An OTLP request is at most {MaxBodyBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
