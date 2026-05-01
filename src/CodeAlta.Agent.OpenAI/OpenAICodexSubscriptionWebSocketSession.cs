#pragma warning disable OPENAI001

using System.Buffers;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent.OpenAI.CodexSubscription;
using OpenAI.Responses;

namespace CodeAlta.Agent.OpenAI;

internal sealed class OpenAICodexSubscriptionWebSocketSession : IOpenAIResponsesWebSocketSession
{
    private const int ReceiveBufferSize = 1024 * 16;
    private const string ResponsesWebSocketsBetaHeader = "responses_websockets=2026-02-06";
    private const string WebSocketErrorCodeDataKey = "OpenAI.WebSocketErrorCode";
    private const string WebSocketErrorPayloadDataKey = "OpenAI.WebSocketErrorPayload";
    private const string WebSocketWrappedErrorDataKey = "OpenAI.WebSocketWrappedError";
    private const string WebSocketConnectionLimitReachedCode = "websocket_connection_limit_reached";
    private const string WebSocketConnectionLimitReachedMessage = "Responses websocket connection limit reached (60 minutes). Create a new websocket connection to continue.";

    private readonly Uri _baseUri;
    private readonly OpenAICodexSubscriptionOptions _options;
    private readonly OpenAICodexSubscriptionAuthManager _authManager;
    private readonly string _sessionId;
    private readonly string _userAgentApplicationId;
    private readonly SemaphoreSlim _streamSemaphore = new(initialCount: 1, maxCount: 1);

    private ClientWebSocket? _webSocket;
    private bool _disposed;

    public OpenAICodexSubscriptionWebSocketSession(
        Uri? baseUri,
        OpenAICodexSubscriptionOptions options,
        OpenAICodexSubscriptionAuthManager authManager,
        string sessionId,
        string userAgentApplicationId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgentApplicationId);

        _baseUri = baseUri ?? new Uri("https://chatgpt.com/backend-api/codex");
        _options = options;
        _authManager = authManager;
        _sessionId = sessionId.Trim();
        _userAgentApplicationId = userAgentApplicationId.Trim();
    }

    public AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
        CreateResponseOptions options,
        CreateResponseOptions? reconnectOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StreamingEnabled != true)
        {
            throw new InvalidOperationException(
                $"{nameof(CreateResponseOptions.StreamingEnabled)} must be set to true for Codex subscription WebSocket streaming.");
        }

        reconnectOptions ??= options;
        if (reconnectOptions.StreamingEnabled != true)
        {
            throw new InvalidOperationException(
                $"{nameof(CreateResponseOptions.StreamingEnabled)} must be set to true for Codex subscription WebSocket reconnect streaming.");
        }

        return new OpenAIResponsesWebSocketUpdateCollection(
            CreateResponseStreamingCoreAsync(options, reconnectOptions, cancellationToken));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _webSocket?.Dispose();
        _webSocket = null;
        // Keep the semaphore alive because Dispose can race an active stream whose finally block still releases it.
    }

    internal static Uri ResolveWebSocketUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        var responsesUri = ResolveResponsesUri(baseUri);
        var builder = new UriBuilder(responsesUri)
        {
            Scheme = responsesUri.Scheme.ToLowerInvariant() switch
            {
                "http" => "ws",
                "https" => "wss",
                _ => responsesUri.Scheme,
            },
            Query = string.Empty,
        };
        return builder.Uri;
    }

    private async IAsyncEnumerable<StreamingResponseUpdate> CreateResponseStreamingCoreAsync(
        CreateResponseOptions options,
        CreateResponseOptions reconnectOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _streamSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        var sawTerminalEvent = false;

        try
        {
            var reusedOpenConnection = _webSocket?.State == WebSocketState.Open;
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestWithStaleReconnectAsync(
                    options,
                    reconnectOptions,
                    reusedOpenConnection,
                    cancellationToken)
                .ConfigureAwait(false);

            await foreach (var message in ReceiveMessagesAsync(_webSocket!, cancellationToken).ConfigureAwait(false))
            {
                if (!TryCreateResponseUpdateMessage(message, out var normalizedMessage, out var eventType, out var messageException))
                {
                    if (messageException is not null)
                    {
                        throw messageException;
                    }

                    continue;
                }

                StreamingResponseUpdate? update;
                try
                {
                    update = ModelReaderWriter.Read<StreamingResponseUpdate>(
                        normalizedMessage,
                        new ModelReaderWriterOptions("J"),
                        OpenAIResponsesContext.Default);
                }
                catch (JsonException) when (!IsTerminalEvent(eventType))
                {
                    continue;
                }

                if (update is null)
                {
                    if (IsTerminalEvent(eventType))
                    {
                        throw new InvalidOperationException("Codex subscription WebSocket returned an unsupported terminal response update.");
                    }

                    continue;
                }

                yield return update;

                if (IsTerminalEvent(eventType))
                {
                    sawTerminalEvent = true;
                    yield break;
                }
            }

            if (!sawTerminalEvent)
            {
                throw new InvalidOperationException("Codex subscription WebSocket stream closed before a terminal response event was received.");
            }
        }
        finally
        {
            if (!sawTerminalEvent)
            {
                await CloseWebSocketSilentlyAsync().ConfigureAwait(false);
            }

            _streamSemaphore.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_webSocket?.State == WebSocketState.Open)
        {
            return;
        }

        _webSocket?.Dispose();
        _webSocket = await CreateAndConnectWebSocketAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ClientWebSocket> CreateAndConnectWebSocketAsync(CancellationToken cancellationToken)
    {
        var credential = await _authManager.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        var accountContext = await _authManager.GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
        var webSocket = new ClientWebSocket();
        ApplyHeaders(webSocket.Options, credential.AccessToken, accountContext);

        try
        {
            await webSocket.ConnectAsync(ResolveWebSocketUri(_baseUri), cancellationToken).ConfigureAwait(false);
            return webSocket;
        }
        catch
        {
            webSocket.Dispose();
            throw;
        }
    }

    private void ApplyHeaders(
        ClientWebSocketOptions options,
        string accessToken,
        OpenAICodexSubscriptionAccountContext accountContext)
    {
        options.DangerousDeflateOptions = new WebSocketDeflateOptions
        {
            ClientContextTakeover = false,
            ServerContextTakeover = false,
        };
        options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        options.SetRequestHeader("OpenAI-Beta", ResponsesWebSocketsBetaHeader);
        options.SetRequestHeader("originator", "codealta");
        options.SetRequestHeader("session_id", _sessionId);
        options.SetRequestHeader("x-client-request-id", _sessionId);
        options.SetRequestHeader("User-Agent", _userAgentApplicationId);

        var accountId = !string.IsNullOrWhiteSpace(_options.AccountId)
            ? _options.AccountId
            : accountContext.AccountId;
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            options.SetRequestHeader("ChatGPT-Account-Id", accountId);
        }

        if (accountContext.IsFedRamp)
        {
            options.SetRequestHeader("X-OpenAI-Fedramp", "true");
        }
    }

    private async Task SendRequestWithStaleReconnectAsync(
        CreateResponseOptions options,
        CreateResponseOptions reconnectOptions,
        bool reusedOpenConnection,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendRequestAsync(CreateWebSocketRequest(options), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (reusedOpenConnection && ex is not OperationCanceledException)
        {
            await CloseWebSocketSilentlyAsync().ConfigureAwait(false);
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestAsync(CreateWebSocketRequest(reconnectOptions), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendRequestAsync(BinaryData request, CancellationToken cancellationToken)
    {
        var bytes = request.ToArray();
        await _webSocket!.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async IAsyncEnumerable<BinaryData> ReceiveMessagesAsync(
        ClientWebSocket webSocket,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                stream.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        yield break;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        throw new InvalidOperationException("Codex subscription WebSocket returned a non-text frame.");
                    }

                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                yield return BinaryData.FromBytes(stream.ToArray());
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task CloseWebSocketSilentlyAsync()
    {
        var webSocket = _webSocket;
        _webSocket = null;
        if (webSocket is null)
        {
            return;
        }

        try
        {
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "done",
                        cancellationTokenSource.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort shutdown only.
        }
        finally
        {
            webSocket.Dispose();
        }
    }

    private static BinaryData CreateWebSocketRequest(CreateResponseOptions options)
    {
        using var optionsDocument = JsonDocument.Parse(SerializeModel(options));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "response.create");
            foreach (var property in optionsDocument.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return BinaryData.FromBytes(stream.ToArray());
    }

    internal static bool TryCreateResponseUpdateMessage(
        BinaryData message,
        out BinaryData normalizedMessage,
        out string? eventType,
        out Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(message);

        normalizedMessage = message;
        eventType = null;
        exception = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException ex)
        {
            exception = new InvalidOperationException("Codex subscription WebSocket returned an invalid JSON text frame.", ex);
            return false;
        }

        using (document)
        {
            eventType = document.RootElement.TryGetProperty("type"u8, out var typeElement)
                ? typeElement.GetString()
                : null;

            if (string.Equals(eventType, "error", StringComparison.Ordinal))
            {
                exception = CreateWrappedWebSocketErrorException(document.RootElement, message.ToString());
                return false;
            }

            if (IsIgnorableSideChannelEvent(eventType))
            {
                return false;
            }

            if (!string.Equals(eventType, "response.done", StringComparison.Ordinal))
            {
                return true;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("type"u8))
                    {
                        writer.WriteString("type"u8, "response.completed");
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            normalizedMessage = BinaryData.FromBytes(stream.ToArray());
            return true;
        }
    }

    internal static bool IsIgnorableSideChannelEvent(string? eventType)
        => string.IsNullOrWhiteSpace(eventType) ||
           !eventType.StartsWith("response.", StringComparison.Ordinal);

    internal static Exception CreateWrappedWebSocketErrorException(JsonElement errorEvent, string payload)
    {
        var code = GetNestedString(errorEvent, "error", "code");
        var errorType = GetNestedString(errorEvent, "error", "type");
        var message = GetNestedString(errorEvent, "error", "message") ??
                      GetString(errorEvent, "message");

        if (string.Equals(code, WebSocketConnectionLimitReachedCode, StringComparison.Ordinal))
        {
            var retryable = new HttpRequestException(
                string.IsNullOrWhiteSpace(message) ? WebSocketConnectionLimitReachedMessage : message);
            PopulateWebSocketErrorData(retryable, errorEvent, payload, code);
            return retryable;
        }

        if (TryGetStatusCode(errorEvent, out var statusCode) && (int)statusCode >= 400)
        {
            var httpException = new HttpRequestException(
                CreateWrappedWebSocketErrorMessage(statusCode, code, errorType, message),
                inner: null,
                statusCode);
            PopulateWebSocketErrorData(httpException, errorEvent, payload, code);
            return httpException;
        }

        var protocolException = new InvalidOperationException(
            CreateWrappedWebSocketErrorMessage(null, code, errorType, message));
        PopulateWebSocketErrorData(protocolException, errorEvent, payload, code);
        return protocolException;
    }

    private static string CreateWrappedWebSocketErrorMessage(
        HttpStatusCode? statusCode,
        string? code,
        string? errorType,
        string? message)
    {
        var detail = string.IsNullOrWhiteSpace(message)
            ? "Codex subscription WebSocket returned an error event."
            : message.Trim();
        var identifier = !string.IsNullOrWhiteSpace(code) ? code : errorType;
        if (!string.IsNullOrWhiteSpace(identifier) &&
            !detail.Contains(identifier, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"{identifier}: {detail}";
        }

        return statusCode is { } status
            ? $"Codex subscription WebSocket failed with HTTP {(int)status}: {detail}"
            : detail;
    }

    private static void PopulateWebSocketErrorData(
        Exception exception,
        JsonElement errorEvent,
        string payload,
        string? code)
    {
        exception.Data[WebSocketWrappedErrorDataKey] = true;
        exception.Data[WebSocketErrorPayloadDataKey] = payload;
        if (!string.IsNullOrWhiteSpace(code))
        {
            exception.Data[WebSocketErrorCodeDataKey] = code;
        }

        if (!errorEvent.TryGetProperty("headers"u8, out var headersElement) ||
            headersElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var header in headersElement.EnumerateObject())
        {
            if (TryGetScalarString(header.Value, out var value))
            {
                exception.Data[header.Name] = value;
            }
        }
    }

    private static bool TryGetStatusCode(JsonElement element, out HttpStatusCode statusCode)
    {
        if ((TryGetInt32(element, "status", out var status) || TryGetInt32(element, "status_code", out status)) &&
            status is >= 100 and <= 599)
        {
            statusCode = (HttpStatusCode)status;
            return true;
        }

        statusCode = default;
        return false;
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        if (element.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String &&
                int.TryParse(property.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetNestedString(JsonElement element, string objectName, string propertyName)
        => element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, propertyName)
            : null;

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetScalarString(JsonElement element, out string value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = element.GetRawText();
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    private static Uri ResolveResponsesUri(Uri baseUri)
    {
        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        builder.Path = path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
            ? path
            : path + "/responses";
        return builder.Uri;
    }

    private static bool IsTerminalEvent(string? eventType)
        => eventType is "response.completed" or "response.incomplete" or "response.failed" or "response.done";

    private static string SerializeModel<T>(T model)
        where T : notnull
        => model is IPersistableModel<T> persistable
            ? persistable.Write(new ModelReaderWriterOptions("J")).ToString()
            : model.ToString() ?? string.Empty;

    private sealed class OpenAIResponsesWebSocketUpdateCollection(
        IAsyncEnumerable<StreamingResponseUpdate> updates) : AsyncCollectionResult<StreamingResponseUpdate>
    {
        public override async IAsyncEnumerable<ClientResult> GetRawPagesAsync()
        {
            yield return ClientResult.FromResponse(new OpenAIResponsesWebSocketPipelineResponse());
            await Task.CompletedTask.ConfigureAwait(false);
        }

        protected override async IAsyncEnumerable<StreamingResponseUpdate> GetValuesFromPageAsync(ClientResult page)
        {
            await foreach (var update in updates.ConfigureAwait(false))
            {
                yield return update;
            }
        }

        public override ContinuationToken GetContinuationToken(ClientResult page) => default!;
    }

    private sealed class OpenAIResponsesWebSocketPipelineResponse : PipelineResponse
    {
        private readonly PipelineResponseHeaders _headers = new EmptyPipelineResponseHeaders();
        private readonly BinaryData _content = BinaryData.FromString("{}");

        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        protected override PipelineResponseHeaders HeadersCore => _headers;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => _content;

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => _content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_content);

        public override void Dispose()
        {
        }
    }

    private sealed class EmptyPipelineResponseHeaders : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            yield break;
        }

        public override bool TryGetValue(string name, out string? value)
        {
            value = null;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }
}
