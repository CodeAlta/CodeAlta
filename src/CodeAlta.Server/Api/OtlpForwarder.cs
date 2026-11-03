using System.Buffers;
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

    // The most of a collector's answer the server reads: past it, the machine gets the status without the body.
    private const int MaxAnswerBytes = 64 * 1024;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ServerTelemetry _telemetry;
    private readonly RequestAuthenticator _authenticator;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="OtlpForwarder"/> class.</summary>
    /// <param name="telemetry">The telemetry of the server: where to forward, and its instruments.</param>
    /// <param name="authenticator">Checks the signature of each request.</param>
    /// <param name="logger">The log.</param>
    /// <param name="handler">The HTTP handler toward the collector; null for the usual one.</param>
    /// <param name="timeout">How long a forwarding may take, the collector's answer read to its end; null for 30 seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive.</exception>
    public OtlpForwarder(ServerTelemetry telemetry, RequestAuthenticator authenticator, ILogger logger, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        if (timeout is { } given) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(given, TimeSpan.Zero, nameof(timeout));
        _telemetry = telemetry;
        _authenticator = authenticator;
        _logger = logger;
        _timeout = timeout ?? DefaultTimeout;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        // The timeout of HttpClient ends with the headers of the answer: each forwarding has its own deadline, body included.
        _http.Timeout = Timeout.InfiniteTimeSpan;
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
        byte[]? bytes;
        int status;
        string? answerType;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
        {
            deadline.CancelAfter(_timeout);
            try
            {
                using var answer = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                status = (int)answer.StatusCode;
                answerType = answer.Content.Headers.ContentType?.ToString();
                // Read before anything is answered: a collector that stalls in its body still gets the machine a 502.
                bytes = await ReadAnswerAsync(answer.Content, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
            {
                _telemetry.Metrics.Forwarded(signal, "unreachable", 0);
                _logger.LogWarning("The collector {Endpoint} did not take the {Signal} of a machine: {Reason}", target.Endpoint, signal,
                    deadline.IsCancellationRequested ? $"no complete answer within {_timeout.TotalSeconds:0.#} s" : exception.Message);
                // The exporter of the machine retries a 502 and a 503 later.
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }
        }

        var success = status is >= 200 and <= 299;
        _telemetry.Metrics.Forwarded(signal, success ? "ok" : status.ToString(System.Globalization.CultureInfo.InvariantCulture), success ? body.Length : 0);
        context.Response.StatusCode = status;
        if (answerType is not null) context.Response.ContentType = answerType;
        if (bytes is not null) await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    // The answer of the collector, or null when it is larger than the server relays: never more than the limit is read.
    private static async Task<byte[]?> ReadAnswerAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxAnswerBytes) return null;
        var buffer = ArrayPool<byte>.Shared.Rent(MaxAnswerBytes + 1);
        try
        {
            var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var length = 0;
                int read;
                while (length <= MaxAnswerBytes && (read = await stream.ReadAsync(buffer.AsMemory(length, MaxAnswerBytes + 1 - length), cancellationToken).ConfigureAwait(false)) > 0)
                    length += read;
                return length > MaxAnswerBytes ? null : buffer.AsSpan(0, length).ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

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
