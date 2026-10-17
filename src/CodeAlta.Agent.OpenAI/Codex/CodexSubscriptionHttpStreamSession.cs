#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using OpenAI;
using OpenAI.Responses;

namespace CodeAlta.Agent.OpenAI.Codex;

internal sealed class CodexSubscriptionHttpStreamSession
{
    // Bodies from this size on are sent compressed to OpenAI's own endpoint: the ChatGPT plan route reads gzip, and a
    // long session sends its whole history on each request over HTTP (OpenAI's clients compress it too). A smaller
    // body, and any body for another endpoint (a proxy may not read it), is sent as it is.
    internal const int CompressionThreshold = 32 * 1024;

    private readonly OpenAIProviderOptions _provider;
    private readonly OpenAICodexSubscriptionOptions _options;
    private readonly OpenAICodexSubscriptionAuthManager _authManager;
    private readonly CodexSubscriptionRequestContext _requestContext;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly OpenAIProtocolTraceLogger? _trace;

    public CodexSubscriptionHttpStreamSession(
        OpenAIProviderOptions provider,
        OpenAICodexSubscriptionAuthManager authManager,
        CodexSubscriptionRequestContext requestContext,
        OpenAIProtocolTraceLogger? trace = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(authManager);
        ArgumentNullException.ThrowIfNull(requestContext);

        _provider = provider;
        _options = provider.CodexSubscription ?? throw new ArgumentException("Codex subscription options are required.", nameof(provider));
        _authManager = authManager;
        _requestContext = requestContext;
        _httpClient = CodexSubscriptionHttpRequestFactory.ResolveHttpClient(provider, out _ownsHttpClient);
        _trace = trace;
    }

    public async IAsyncEnumerable<CodexProtocolEvent> CreateResponseStreamingAsync(
        CreateResponseOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var timeoutSource = CreateTimeoutSource(cancellationToken);
        var effectiveCancellation = timeoutSource?.Token ?? cancellationToken;
        var request = await CreateRequestAsync(options, compress: IsOpenAIEndpoint(_provider.BaseUri), effectiveCancellation).ConfigureAwait(false);
        HttpResponseMessage? response = null;
        try
        {
            TraceRequest(request);
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, effectiveCancellation).ConfigureAwait(false);
            TraceResponse(response);
            if (response.StatusCode == HttpStatusCode.UnsupportedMediaType && request.Content?.Headers.ContentEncoding.Count > 0)
            {
                // A route that does not read a compressed body is asked again with the body as it is.
                _trace?.WriteLine("<<< compressed body refused: sending it again uncompressed");
                response.Dispose();
                request.Dispose();
                request = await CreateRequestAsync(options, compress: false, effectiveCancellation).ConfigureAwait(false);
                TraceRequest(request);
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, effectiveCancellation).ConfigureAwait(false);
                TraceResponse(response);
            }

            CaptureTurnState(response);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateHttpExceptionAsync(response, effectiveCancellation).ConfigureAwait(false);
            }

            yield return CodexProtocolEventParser.CreateInitialHttpEvent(response);
            await using var stream = await response.Content.ReadAsStreamAsync(effectiveCancellation).ConfigureAwait(false);
            await foreach (var sseEvent in SseParser.Create(stream).EnumerateAsync(effectiveCancellation).ConfigureAwait(false))
            {
                if (string.Equals(sseEvent.Data, "[DONE]", StringComparison.Ordinal))
                {
                    yield break;
                }

                _trace?.WriteLine($"<<< SSE event={sseEvent.EventType ?? "message"} bytes={System.Text.Encoding.UTF8.GetByteCount(sseEvent.Data)}");
                yield return CodexProtocolEventParser.Parse(
                    CodexProtocolTransport.Http,
                    BinaryData.FromString(sseEvent.Data),
                    sseEvent.EventType);
            }
        }
        finally
        {
            response?.Dispose();
            request.Dispose();
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        CreateResponseOptions options,
        bool compress,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            CodexSubscriptionHttpRequestFactory.ResolveEndpoint(_provider.BaseUri, "responses"));
        var payload = ChatGptPlanRequestNormalizer.Normalize(
            ModelReaderWriter.Write(options, new ModelReaderWriterOptions("J"), OpenAIContext.Default), isHttp: true);
        var body = payload.ToMemory();
        if (compress && body.Length >= CompressionThreshold)
        {
            request.Content = new ByteArrayContent(Compress(body.Span));
            request.Content.Headers.ContentEncoding.Add("gzip");
            _trace?.WriteLine($">>> body bytes={body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} gzip={request.Content.Headers.ContentLength?.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        else
        {
            request.Content = new ByteArrayContent(body.ToArray());
        }

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        _trace?.WriteLine($">>> body: {payload}");

        var identity = await CodexSubscriptionHttpRequestFactory.CreateIdentityAsync(
            _authManager,
            _options,
            cancellationToken).ConfigureAwait(false);
        CodexSubscriptionHttpRequestFactory.ApplyIdentity(request, identity);
        if (_options.SendResponsesBetaHeader)
        {
            SetHeader(request, "OpenAI-Beta", "responses=experimental");
        }

        var extraHeaders = OpenAIModelRequestOverrides.MergeHeaders(
            _provider.ExtraHeaders,
            OpenAIModelRequestOverrides.Find(_provider.ModelRequestOverrides, options.Model));
        if (extraHeaders is not null)
        {
            foreach (var header in extraHeaders)
            {
                SetHeader(request, header.Key, header.Value);
            }
        }

        foreach (var header in _requestContext.CompatibilityHeaders)
        {
            SetHeader(request, header.Key, header.Value);
        }

        if (_requestContext.TurnState.TryGetCapturedState(out var turnState))
        {
            SetHeader(request, "x-codex-turn-state", turnState);
        }

        return request;
    }

    private CancellationTokenSource? CreateTimeoutSource(CancellationToken cancellationToken)
    {
        if (_provider.NetworkTimeout is not { } timeout)
        {
            return null;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private void CaptureTurnState(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-codex-turn-state", out var values) &&
            values.FirstOrDefault() is { } state &&
            !string.IsNullOrWhiteSpace(state))
        {
            _requestContext.TurnState.Capture(state);
        }
    }

    internal static bool IsOpenAIEndpoint(Uri? baseUri)
        => baseUri is null || string.Equals(baseUri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);

    internal static byte[] Compress(ReadOnlySpan<byte> body)
    {
        using var output = new MemoryStream(body.Length / 3 + 64);
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(body);
        }

        return output.ToArray();
    }

    internal static async Task<HttpRequestException> CreateHttpExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body;
        var exception = new HttpRequestException(
            $"ChatGPT/Codex HTTP request failed with status {(int)response.StatusCode} ({response.StatusCode}): {detail}",
            inner: null,
            response.StatusCode);
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            exception.Data["Retry-After"] = retryAfter.ToString();
        }

        AgentErrorData.Set(exception, AgentErrorData.Transport, "http");
        AgentErrorData.Set(exception, AgentErrorData.RequestId, GetRequestId(response));
        if (TryReadErrorCodes(body, out var code, out var type))
        {
            AgentErrorData.Set(exception, AgentErrorData.Code, code);
            AgentErrorData.Set(exception, AgentErrorData.Type, type);
        }

        AgentErrorData.Set(exception, AgentErrorData.Payload, body);
        return exception;
    }

    // The id OpenAI gives the request, by the first header that has one.
    internal static string? GetRequestId(HttpResponseMessage response)
    {
        foreach (var name in (ReadOnlySpan<string>)["x-request-id", "x-oai-request-id", "cf-ray"])
        {
            if (response.Headers.TryGetValues(name, out var values) &&
                values.FirstOrDefault() is { } value &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    // The `code` and `type` of an error body: `{"error":{...}}` or the error object itself.
    private static bool TryReadErrorCodes(string? body, out string? code, out string? type)
    {
        code = null;
        type = null;
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.TryGetProperty("error"u8, out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : document.RootElement;
            code = ReadScalar(error, "code"u8);
            type = ReadScalar(error, "type"u8);
            return code is not null || type is not null;
        }
        catch (JsonException)
        {
            return false;
        }

        static string? ReadScalar(JsonElement element, ReadOnlySpan<byte> name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null,
                }
                : null;
    }

    private void TraceRequest(HttpRequestMessage request)
    {
        if (_trace is null)
        {
            return;
        }

        _trace.WriteLine($">>> {request.Method} {request.RequestUri}");
        foreach (var header in request.Headers)
        {
            var value = IsSensitiveHeader(header.Key) ? "<redacted>" : string.Join(",", header.Value);
            _trace.WriteLine($">>> {header.Key}: {value}");
        }
    }

    private void TraceResponse(HttpResponseMessage response)
    {
        if (_trace is null)
        {
            return;
        }

        _trace.WriteLine($"<<< HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        foreach (var header in response.Headers)
        {
            var value = IsSensitiveHeader(header.Key) ? "<redacted>" : string.Join(",", header.Value);
            _trace.WriteLine($"<<< {header.Key}: {value}");
        }
    }

    private static bool IsSensitiveHeader(string name)
        => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("api-key", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase);

    private static void SetHeader(HttpRequestMessage request, string name, string value)
        => CodexSubscriptionHttpRequestFactory.SetHeader(request, name, value);
}
