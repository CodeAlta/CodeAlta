using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Mistral;

/// <summary>
/// Mistral chat-completions <see cref="IChatClient"/> implementation.
/// </summary>
internal sealed class MistralChatClient : IChatClient
{
    private const string DefaultModelId = "mistral-small-latest";
    private const int DefaultMaxRetryAttempts = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly MistralProviderOptions _provider;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private ChatClientMetadata? _metadata;

    public MistralChatClient(MistralProviderOptions provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _provider = provider;
        _httpClient = provider.HttpClient ?? new HttpClient();
        _disposeHttpClient = provider.HttpClient is null;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        using var response = await SendChatRequestWithRetriesAsync(messageList, options, cancellationToken).ConfigureAwait(false);

        var toolCallBuilders = new SortedDictionary<int, StreamingToolCallBuilder>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var sseEvent in SseParser.Create(stream).EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            var data = sseEvent.Data;
            if (data == "[DONE]")
            {
                yield break;
            }

            var update = ParseStreamingUpdate(data, toolCallBuilders);
            if (update is not null)
            {
                yield return update;
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is not null
            ? null
            : serviceType == typeof(ChatClientMetadata)
                ? (_metadata ??= new ChatClientMetadata(nameof(MistralChatClient), GetBaseUri()))
                : serviceType.IsInstanceOfType(this)
                    ? this
                    : null;
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private HttpRequestMessage CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, CreateChatUri());
        if (!string.IsNullOrWhiteSpace(_provider.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _provider.ApiKey);
        }

        if (_provider.ExtraHeaders is { Count: > 0 } extraHeaders)
        {
            foreach (var pair in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        request.Content = new StringContent(CreateRequestBody(messages, options), Encoding.UTF8, "application/json");
        return request;
    }

    private Uri CreateChatUri()
    {
        var baseUri = GetBaseUri();
        return new Uri($"{baseUri.AbsoluteUri.TrimEnd('/')}{MistralDefaults.ChatCompletionsPath}", UriKind.Absolute);
    }

    private Uri GetBaseUri()
        => _provider.BaseUri ?? _httpClient.BaseAddress ?? new Uri(MistralDefaults.DefaultBaseUrl);

    private async Task<HttpResponseMessage> SendChatRequestWithRetriesAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var maxAttempts = GetMaxRetryAttempts(_provider);
        for (var attempt = 0; ; attempt++)
        {
            using var request = CreateRequest(messages, options);
            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var exception = await CreateApiExceptionAsync(response, "chat completion", cancellationToken).ConfigureAwait(false);
                response.Dispose();
                response = null;
                if (!ShouldRetry(exception, attempt, maxAttempts))
                {
                    throw exception;
                }

                await DelayBeforeRetryAsync(_provider, exception, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ShouldRetry(ex, attempt, maxAttempts))
            {
                response?.Dispose();
                await DelayBeforeRetryAsync(_provider, ex, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                response?.Dispose();
                throw;
            }
        }
    }

    internal static async Task<MistralApiException> CreateApiExceptionAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        var content = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var providerError = MistralProviderError.Parse(content);
        var retryAfter = GetRetryAfter(response.Headers.RetryAfter);
        var requestId = GetRequestId(response.Headers);
        return new MistralApiException(
            CreateApiExceptionMessage(response, operation, providerError, retryAfter, requestId, content),
            response.StatusCode,
            providerError.Code,
            providerError.Type,
            providerError.Param,
            requestId,
            retryAfter,
            string.IsNullOrWhiteSpace(content) ? null : content);
    }

    internal static int GetMaxRetryAttempts(MistralProviderOptions provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return Math.Max(1, provider.MaxRetryAttempts ?? DefaultMaxRetryAttempts);
    }

    internal static bool ShouldRetry(Exception exception, int attempt, int maxAttempts)
        => attempt < maxAttempts - 1 && IsRetryableException(exception);

    internal static bool IsRetryableException(Exception exception)
        => exception switch
        {
            MistralApiException apiException => IsRetryableStatusCode(apiException.StatusCode),
            HttpRequestException { StatusCode: { } statusCode } => IsRetryableStatusCode(statusCode),
            HttpRequestException => true,
            _ => false,
        };

    internal static bool IsRetryableStatusCode(HttpStatusCode? statusCode)
        => statusCode is not null && (int)statusCode.Value is 408 or 409 or 425 or 429 or 500 or 502 or 503 or 504 or 529;

    internal static Task DelayBeforeRetryAsync(
        MistralProviderOptions provider,
        Exception exception,
        int attempt,
        CancellationToken cancellationToken)
    {
        var delay = exception is MistralApiException { RetryAfter: { } retryAfter }
            ? retryAfter
            : GetExponentialBackoffDelay(attempt);
        return (provider.RetryDelayAsync ?? Task.Delay)(delay, cancellationToken);
    }

    private static TimeSpan GetExponentialBackoffDelay(int attempt)
    {
        var milliseconds = InitialRetryDelay.TotalMilliseconds * Math.Pow(1.5d, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxRetryDelay.TotalMilliseconds));
    }

    private static string CreateApiExceptionMessage(
        HttpResponseMessage response,
        string operation,
        MistralProviderError providerError,
        TimeSpan? retryAfter,
        string? requestId,
        string? content)
    {
        var status = FormatStatus(response.StatusCode, response.ReasonPhrase);
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => $"Mistral authentication failed ({status}). Check the configured API key.",
            HttpStatusCode.Forbidden => $"Mistral rejected the request because the account, workspace, plan, or policy does not allow it ({status}).",
            HttpStatusCode.TooManyRequests => $"Mistral rate limit or quota was reached ({status}). Retry later or after the service-provided Retry-After time.",
            HttpStatusCode.BadRequest => $"Mistral rejected the {operation} request shape ({status}).",
            HttpStatusCode.UnprocessableEntity => $"Mistral rejected the {operation} request ({status}).",
            >= HttpStatusCode.InternalServerError => $"Mistral service is temporarily unavailable during {operation} ({status}).",
            _ => $"Mistral {operation} failed ({status}).",
        };

        var detail = providerError.FormatDetail();
        if (!string.IsNullOrWhiteSpace(detail))
        {
            message = $"{message} {detail}.";
        }
        else if (!string.IsNullOrWhiteSpace(content))
        {
            message = $"{message} Response body: {CreateExcerpt(content!)}";
        }

        if (retryAfter is not null)
        {
            message = $"{message} Retry after {FormatRetryAfter(retryAfter.Value)}.";
        }

        if (!string.IsNullOrWhiteSpace(requestId))
        {
            message = $"{message} Request ID: {requestId}.";
        }

        return message;
    }

    private static string FormatStatus(HttpStatusCode statusCode, string? reasonPhrase)
    {
        var reason = string.IsNullOrWhiteSpace(reasonPhrase) ? statusCode.ToString() : reasonPhrase.Trim();
        return $"HTTP {(int)statusCode} {reason}";
    }

    private static string CreateExcerpt(string value, int maxLength = 400)
    {
        var normalized = value.Trim().Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return normalized.Length <= maxLength ? normalized : string.Concat(normalized.AsSpan(0, maxLength), "…");
    }

    private static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    private static string FormatRetryAfter(TimeSpan delay)
    {
        if (delay.TotalSeconds < 60)
        {
            var seconds = (int)Math.Ceiling(delay.TotalSeconds);
            return $"{seconds} {(seconds == 1 ? "second" : "seconds")}";
        }

        var minutes = (int)Math.Ceiling(delay.TotalMinutes);
        return $"{minutes} {(minutes == 1 ? "minute" : "minutes")}";
    }

    private static string? GetRequestId(HttpResponseHeaders headers)
    {
        foreach (var name in new[] { "x-request-id", "request-id", "mistral-correlation-id", "correlation-id" })
        {
            if (headers.TryGetValues(name, out var values))
            {
                var value = values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private static string CreateRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", string.IsNullOrWhiteSpace(options?.ModelId) ? DefaultModelId : options!.ModelId);
            writer.WriteBoolean("stream", true);
            WriteChatOptions(writer, options);
            writer.WriteStartArray("messages");
            if (!string.IsNullOrWhiteSpace(options?.Instructions))
            {
                WriteTextMessage(writer, "system", options!.Instructions!.Trim());
            }

            foreach (var message in messages)
            {
                WriteMessage(writer, message);
            }

            writer.WriteEndArray();
            WriteTools(writer, options);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteChatOptions(Utf8JsonWriter writer, ChatOptions? options)
    {
        if (options is null)
        {
            return;
        }

        if (options.Temperature is { } temperature)
        {
            writer.WriteNumber("temperature", temperature);
        }

        if (options.TopP is { } topP)
        {
            writer.WriteNumber("top_p", topP);
        }

        if (options.PresencePenalty is { } presencePenalty)
        {
            writer.WriteNumber("presence_penalty", presencePenalty);
        }

        if (options.FrequencyPenalty is { } frequencyPenalty)
        {
            writer.WriteNumber("frequency_penalty", frequencyPenalty);
        }

        if (options.MaxOutputTokens is { } maxOutputTokens)
        {
            writer.WriteNumber("max_tokens", maxOutputTokens);
        }

        if (options.Seed is { } seed)
        {
            writer.WriteNumber("random_seed", seed);
        }

        if (options.StopSequences is { Count: > 0 } stopSequences)
        {
            writer.WritePropertyName("stop");
            if (stopSequences.Count == 1)
            {
                writer.WriteStringValue(stopSequences[0]);
            }
            else
            {
                writer.WriteStartArray();
                foreach (var stopSequence in stopSequences)
                {
                    writer.WriteStringValue(stopSequence);
                }

                writer.WriteEndArray();
            }
        }

        if (options.ResponseFormat is ChatResponseFormatJson jsonFormat)
        {
            writer.WriteStartObject("response_format");
            if (jsonFormat.Schema is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } schema)
            {
                writer.WriteString("type", "json_schema");
                writer.WriteStartObject("json_schema");
                writer.WriteString("name", string.IsNullOrWhiteSpace(jsonFormat.SchemaName) ? "response" : jsonFormat.SchemaName);
                if (!string.IsNullOrWhiteSpace(jsonFormat.SchemaDescription))
                {
                    writer.WriteString("description", jsonFormat.SchemaDescription);
                }

                writer.WritePropertyName("schema");
                schema.WriteTo(writer);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteString("type", "json_object");
            }

            writer.WriteEndObject();
        }

        var wroteReasoningEffort = WriteReasoningOptions(writer, options.Reasoning);
        if (options.AllowMultipleToolCalls is { } allowMultipleToolCalls)
        {
            writer.WriteBoolean("parallel_tool_calls", allowMultipleToolCalls);
        }

        WriteAdditionalProperties(writer, options, wroteReasoningEffort);
    }

    private static bool WriteReasoningOptions(Utf8JsonWriter writer, ReasoningOptions? reasoning)
    {
        if (reasoning?.Effort is not { } effort)
        {
            return false;
        }

        writer.WriteString("reasoning_effort", ToMistralReasoningEffort(effort));
        return true;
    }

    private static string ToMistralReasoningEffort(ReasoningEffort effort)
        => effort == ReasoningEffort.None ? "none" :
            effort == ReasoningEffort.Low ? "low" :
            effort == ReasoningEffort.Medium ? "medium" :
            effort == ReasoningEffort.High ? "high" :
            effort == ReasoningEffort.ExtraHigh ? "xhigh" :
            effort.ToString().ToLowerInvariant();

    private static void WriteAdditionalProperties(Utf8JsonWriter writer, ChatOptions options, bool wroteReasoningEffort)
    {
        if (options.AdditionalProperties is not { Count: > 0 } additionalProperties)
        {
            return;
        }

        foreach (var pair in additionalProperties)
        {
            if (!IsSupportedMistralOption(pair.Key, wroteReasoningEffort))
            {
                continue;
            }

            writer.WritePropertyName(pair.Key);
            WriteJsonValue(writer, pair.Value);
        }
    }

    private static bool IsSupportedMistralOption(string key, bool wroteReasoningEffort)
        => key switch
        {
            "metadata" or "n" or "prediction" or "prompt_mode" or "guardrails" or "prompt_cache_key" or "safe_prompt" => true,
            "reasoning_effort" => !wroteReasoningEffort,
            _ => false,
        };

    private static void WriteMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        if (message.Role == ChatRole.System)
        {
            WriteTextMessage(writer, "system", ConcatText(message.Contents));
        }
        else if (message.Role == ChatRole.Assistant)
        {
            WriteAssistantMessage(writer, message);
        }
        else if (message.Role == ChatRole.Tool)
        {
            WriteToolMessage(writer, message);
        }
        else
        {
            WriteUserMessage(writer, message);
        }
    }

    private static void WriteTextMessage(Utf8JsonWriter writer, string role, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", text);
        writer.WriteEndObject();
    }

    private static void WriteUserMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        WriteUserContent(writer, message.Contents);
        writer.WriteEndObject();
    }

    private static void WriteAssistantMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "assistant");

        var hasReasoning = message.Contents.OfType<TextReasoningContent>().Any(static reasoning =>
            !string.IsNullOrEmpty(reasoning.Text) || !string.IsNullOrWhiteSpace(reasoning.ProtectedData));
        if (hasReasoning)
        {
            WriteAssistantContentChunks(writer, message.Contents);
        }
        else
        {
            var text = ConcatText(message.Contents);
            if (!string.IsNullOrEmpty(text))
            {
                writer.WriteString("content", text);
            }
        }

        var toolCalls = message.Contents.OfType<FunctionCallContent>().ToArray();
        if (toolCalls.Length > 0)
        {
            writer.WriteStartArray("tool_calls");
            foreach (var toolCall in toolCalls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", toolCall.CallId);
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", toolCall.Name);
                writer.WriteString("arguments", SerializeArguments(toolCall.Arguments));
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteAssistantContentChunks(Utf8JsonWriter writer, IList<AIContent> contents)
    {
        writer.WriteStartArray("content");
        foreach (var content in contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                    WriteTextChunk(writer, textContent.Text);
                    break;
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text) || !string.IsNullOrWhiteSpace(reasoning.ProtectedData):
                    WriteThinkingChunk(writer, reasoning);
                    break;
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteTextChunk(Utf8JsonWriter writer, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text);
        writer.WriteEndObject();
    }

    private static void WriteThinkingChunk(Utf8JsonWriter writer, TextReasoningContent reasoning)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "thinking");
        writer.WriteStartArray("thinking");
        if (!string.IsNullOrEmpty(reasoning.Text))
        {
            WriteTextChunk(writer, reasoning.Text);
        }

        writer.WriteEndArray();
        if (!string.IsNullOrWhiteSpace(reasoning.ProtectedData))
        {
            writer.WriteString("signature", reasoning.ProtectedData);
        }

        writer.WriteEndObject();
    }

    private static void WriteToolMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        var functionResult = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        writer.WriteStartObject();
        writer.WriteString("role", "tool");
        writer.WriteString("content", ToResultString(functionResult));
        if (!string.IsNullOrWhiteSpace(functionResult?.CallId))
        {
            writer.WriteString("tool_call_id", functionResult.CallId);
        }

        writer.WriteEndObject();
    }

    private static void WriteUserContent(Utf8JsonWriter writer, IList<AIContent> contents)
    {
        if (contents.Count == 1 && contents[0] is TextContent singleText)
        {
            writer.WriteString("content", singleText.Text);
            return;
        }

        var wroteAny = false;
        using var contentStream = new MemoryStream();
        using (var contentWriter = new Utf8JsonWriter(contentStream))
        {
            contentWriter.WriteStartArray();
            foreach (var content in contents)
            {
                wroteAny |= TryWriteContentChunk(contentWriter, content);
            }

            contentWriter.WriteEndArray();
        }

        if (!wroteAny)
        {
            writer.WriteString("content", ConcatText(contents));
            return;
        }

        writer.WritePropertyName("content");
        using var document = JsonDocument.Parse(contentStream.ToArray());
        document.RootElement.WriteTo(writer);
    }

    private static bool TryWriteContentChunk(Utf8JsonWriter writer, AIContent content)
    {
        switch (content)
        {
            case TextContent textContent:
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", textContent.Text);
                writer.WriteEndObject();
                return true;
            case UriContent uriContent when IsImageMediaType(uriContent.MediaType):
                WriteImageUrlChunk(writer, uriContent.Uri.ToString());
                return true;
            case DataContent dataContent when dataContent.HasTopLevelMediaType("image") && dataContent.Uri is { } imageUri:
                WriteImageUrlChunk(writer, imageUri.ToString());
                return true;
            case DataContent dataContent when dataContent.HasTopLevelMediaType("image") && dataContent.Data is { } imageData:
                WriteImageUrlChunk(writer, CreateDataUrl(dataContent.MediaType ?? "image/png", imageData.ToArray()));
                return true;
            case DataContent audioContent when audioContent.HasTopLevelMediaType("audio") && audioContent.Uri is { } audioUri:
                WriteAudioChunk(writer, audioUri.ToString());
                return true;
            case DataContent audioContent when audioContent.HasTopLevelMediaType("audio") && audioContent.Data is { } audioData:
                WriteAudioChunk(writer, CreateDataUrl(audioContent.MediaType ?? "audio/wav", audioData.ToArray()));
                return true;
            default:
                return false;
        }
    }

    private static void WriteImageUrlChunk(Utf8JsonWriter writer, string url)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "image_url");
        writer.WriteStartObject("image_url");
        writer.WriteString("url", url);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteAudioChunk(Utf8JsonWriter writer, string url)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "input_audio");
        writer.WriteString("input_audio", url);
        writer.WriteEndObject();
    }

    private static void WriteTools(Utf8JsonWriter writer, ChatOptions? options)
    {
        if (options?.ToolMode is NoneChatToolMode)
        {
            writer.WriteString("tool_choice", "none");
            return;
        }

        if (options?.Tools is not { Count: > 0 } tools)
        {
            return;
        }

        writer.WriteStartArray("tools");
        foreach (var tool in tools)
        {
            if (tool is not AIFunctionDeclaration function)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", function.Name);
            if (!string.IsNullOrWhiteSpace(function.Description))
            {
                writer.WriteString("description", function.Description);
            }

            writer.WritePropertyName("parameters");
            if (function.JsonSchema.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                function.JsonSchema.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        switch (options.ToolMode)
        {
            case RequiredChatToolMode { RequiredFunctionName: { Length: > 0 } functionName }:
                writer.WriteStartObject("tool_choice");
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", functionName);
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case RequiredChatToolMode:
                writer.WriteString("tool_choice", "any");
                break;
            default:
                writer.WriteString("tool_choice", "auto");
                break;
        }
    }

    private static ChatResponseUpdate? ParseStreamingUpdate(
        string data,
        SortedDictionary<int, StreamingToolCallBuilder> toolCallBuilders)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var responseId = GetString(root, "id");
        var modelId = GetString(root, "model");
        var createdAt = GetInt64(root, "created") is { } created
            ? DateTimeOffset.FromUnixTimeSeconds(created)
            : (DateTimeOffset?)null;
        var update = new ChatResponseUpdate
        {
            ResponseId = responseId,
            ModelId = modelId,
            CreatedAt = createdAt,
            Role = ChatRole.Assistant,
        };

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                AddDeltaContents(update, delta, toolCallBuilders);
            }

            var finishReason = GetString(choice, "finish_reason");
            if (!string.IsNullOrWhiteSpace(finishReason))
            {
                update.FinishReason = ToFinishReason(finishReason);
                AddAccumulatedToolCalls(update, toolCallBuilders);
            }
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            update.Contents.Add(new UsageContent(new UsageDetails
            {
                InputTokenCount = GetInt64(usage, "prompt_tokens"),
                OutputTokenCount = GetInt64(usage, "completion_tokens"),
                TotalTokenCount = GetInt64(usage, "total_tokens"),
                CachedInputTokenCount = GetNestedInt64(usage, "prompt_tokens_details", "cached_tokens") ?? GetInt64(usage, "num_cached_tokens"),
                ReasoningTokenCount = GetNestedInt64(usage, "completion_tokens_details", "reasoning_tokens"),
            }));
        }

        return update;
    }

    private static void AddDeltaContents(
        ChatResponseUpdate update,
        JsonElement delta,
        SortedDictionary<int, StreamingToolCallBuilder> toolCallBuilders)
    {
        AddContentDelta(update, delta);

        if (TryExtractText(delta, "reasoning_content", out var reasoningContent))
        {
            update.Contents.Add(new TextReasoningContent(reasoningContent));
        }

        if (TryExtractText(delta, "reasoning", out var reasoning))
        {
            update.Contents.Add(new TextReasoningContent(reasoning));
        }

        if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            if (toolCall.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var index = GetInt32(toolCall, "index") ?? toolCallBuilders.Count;
            if (!toolCallBuilders.TryGetValue(index, out var builder))
            {
                builder = new StreamingToolCallBuilder();
                toolCallBuilders.Add(index, builder);
            }

            builder.Id = GetString(toolCall, "id") ?? builder.Id;
            if (toolCall.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                builder.Name = GetString(function, "name") ?? builder.Name;
                if (TryExtractText(function, "arguments", out var arguments))
                {
                    builder.Arguments.Append(arguments);
                }
            }
        }
    }

    private static void AddContentDelta(ChatResponseUpdate update, JsonElement delta)
    {
        if (!delta.TryGetProperty("content", out var content))
        {
            return;
        }

        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    update.Contents.Add(new TextContent(text));
                }

                break;
            case JsonValueKind.Array:
                foreach (var chunk in content.EnumerateArray())
                {
                    AddContentChunk(update, chunk);
                }

                break;
        }
    }

    private static void AddContentChunk(ChatResponseUpdate update, JsonElement chunk)
    {
        if (chunk.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        var type = GetString(chunk, "type");
        if (string.Equals(type, "thinking", StringComparison.Ordinal))
        {
            var reasoning = ExtractThinkingText(chunk);
            var signature = GetString(chunk, "signature");
            if (!string.IsNullOrEmpty(reasoning) || !string.IsNullOrWhiteSpace(signature))
            {
                update.Contents.Add(new TextReasoningContent(reasoning)
                {
                    ProtectedData = signature,
                });
            }

            return;
        }

        if (TryExtractText(chunk, "text", out var text))
        {
            update.Contents.Add(new TextContent(text));
        }
    }

    private static string ExtractThinkingText(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("thinking", out var thinking) || thinking.ValueKind is not JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in thinking.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String)
            {
                builder.Append(textElement.GetString());
            }
        }

        return builder.ToString();
    }

    private static void AddAccumulatedToolCalls(
        ChatResponseUpdate update,
        SortedDictionary<int, StreamingToolCallBuilder> toolCallBuilders)
    {
        if (toolCallBuilders.Count == 0)
        {
            return;
        }

        foreach (var (_, builder) in toolCallBuilders)
        {
            update.Contents.Add(new FunctionCallContent(
                string.IsNullOrWhiteSpace(builder.Id) ? $"tool:{Guid.CreateVersion7()}" : builder.Id,
                builder.Name ?? string.Empty,
                ParseArguments(builder.Arguments.ToString())));
        }

        toolCallBuilders.Clear();
    }

    private static bool TryExtractText(JsonElement element, string propertyName, out string text)
    {
        text = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.String:
                text = property.GetString() ?? string.Empty;
                return text.Length > 0;
            case JsonValueKind.Array:
                var builder = new StringBuilder();
                foreach (var item in property.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("text", out var textElement) &&
                        textElement.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(textElement.GetString());
                    }
                }

                text = builder.ToString();
                return text.Length > 0;
            default:
                return false;
        }
    }

    private static ChatFinishReason? ToFinishReason(string finishReason)
        => finishReason switch
        {
            "stop" => ChatFinishReason.Stop,
            "length" or "model_length" => ChatFinishReason.Length,
            "tool_calls" => ChatFinishReason.ToolCalls,
            "content_filter" => ChatFinishReason.ContentFilter,
            "error" => new ChatFinishReason("error"),
            _ => new ChatFinishReason(finishReason),
        };

    private static Dictionary<string, object?>? ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) || arguments == "{}")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = ConvertJsonValue(property.Value);
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (arguments is not null)
            {
                foreach (var pair in arguments)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteJsonValue(writer, pair.Value);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ToResultString(FunctionResultContent? functionResult)
    {
        if (functionResult is null)
        {
            return string.Empty;
        }

        if (functionResult.Result is JsonElement jsonElement)
        {
            return jsonElement.ValueKind == JsonValueKind.String
                ? jsonElement.GetString() ?? string.Empty
                : jsonElement.GetRawText();
        }

        if (functionResult.Result is string text)
        {
            return text;
        }

        if (functionResult.Result is not null)
        {
            return SerializeValue(functionResult.Result);
        }

        return functionResult.Exception?.Message ?? string.Empty;
    }

    private static string SerializeValue(object value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteJsonValue(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static object? ConvertJsonValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => ConvertJsonValue(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToArray(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var int64Value) => int64Value,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                WriteJsonObject(writer, dictionary);
                break;
            case IDictionary<string, object?> dictionary:
                WriteJsonObject(writer, dictionary);
                break;
            case IEnumerable<object?> items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    WriteJsonValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteJsonObject(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> values)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in values)
        {
            writer.WritePropertyName(key);
            WriteJsonValue(writer, value);
        }

        writer.WriteEndObject();
    }

    private static string ConcatText(IEnumerable<AIContent> contents)
        => string.Concat(contents.OfType<TextContent>().Select(static content => content.Text));

    private static string CreateDataUrl(string mediaType, byte[] data)
        => $"data:{mediaType};base64,{Convert.ToBase64String(data)}";

    private static bool IsImageMediaType(string? mediaType)
        => mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;

    private static long? GetInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)
            ? value
            : null;

    private static long? GetNestedInt64(JsonElement element, string propertyName, string nestedPropertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Object
            ? GetInt64(property, nestedPropertyName)
            : null;

    private sealed class StreamingToolCallBuilder
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
