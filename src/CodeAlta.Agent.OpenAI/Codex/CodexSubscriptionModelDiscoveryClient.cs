using System.Net;
using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.OpenAI.Codex;

internal sealed class CodexSubscriptionModelDiscoveryClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAICodexSubscriptionAuthManager _authManager;
    private readonly OpenAICodexSubscriptionOptions _options;
    private readonly string _userAgentApplicationId;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _retryDelay;

    public CodexSubscriptionModelDiscoveryClient(
        HttpClient httpClient,
        OpenAICodexSubscriptionAuthManager authManager,
        OpenAICodexSubscriptionOptions options,
        string userAgentApplicationId,
        TimeSpan? timeout = null,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(authManager);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgentApplicationId);

        _httpClient = httpClient;
        _authManager = authManager;
        _options = options;
        _userAgentApplicationId = userAgentApplicationId;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);
    }

    public async ValueTask<IReadOnlyList<CodexSubscriptionDiscoveredModel>> GetModelsAsync(
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);
            var effectiveCancellation = timeoutSource.Token;
            var identity = await CodexSubscriptionHttpRequestFactory.CreateIdentityAsync(
                _authManager,
                _options,
                effectiveCancellation).ConfigureAwait(false);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        CreateModelsUri(baseUri, _userAgentApplicationId));
                    CodexSubscriptionHttpRequestFactory.ApplyIdentity(request, identity);
                    using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        effectiveCancellation).ConfigureAwait(false);
                    var content = await response.Content.ReadAsStringAsync(effectiveCancellation).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new CodexSubscriptionModelDiscoveryException(
                            CreateFailureMessage(response.StatusCode, content),
                            response.StatusCode);
                    }

                    var etag = response.Headers.ETag?.Tag ??
                               (response.Headers.TryGetValues("X-Models-Etag", out var values) ? values.FirstOrDefault() : null);
                    return ParseModels(content, etag);
                }
                catch (Exception ex) when (attempt < 3 && IsRetryable(ex, cancellationToken))
                {
                    await Task.Delay(_retryDelay, effectiveCancellation).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Codex model discovery exceeded the five-second timeout.", ex);
        }
        catch (JsonException ex)
        {
            throw new CodexSubscriptionModelDiscoveryException(
                "Codex model discovery returned an unexpected response shape.",
                statusCode: null,
                ex);
        }
    }

    public static Uri CreateModelsUri(Uri baseUri, string clientVersion)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);

        var modelsUri = CodexSubscriptionHttpRequestFactory.ResolveEndpoint(baseUri, "models");
        return CodexSubscriptionHttpRequestFactory.AppendQueryParameter(
            modelsUri,
            "client_version",
            NormalizeClientVersion(clientVersion));
    }

    private static bool IsRetryable(
        Exception exception,
        CancellationToken callerCancellation)
        => exception switch
        {
            CodexSubscriptionModelDiscoveryException { StatusCode: >= HttpStatusCode.InternalServerError } => true,
            HttpRequestException => true,
            OperationCanceledException => !callerCancellation.IsCancellationRequested,
            _ => false,
        };

    private static string NormalizeClientVersion(string clientVersion)
    {
        var trimmed = clientVersion.Trim();
        var slashIndex = trimmed.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex + 1 < trimmed.Length)
        {
            trimmed = trimmed[(slashIndex + 1)..];
        }

        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 3
            ? string.Join('.', parts.Take(3))
            : trimmed;
    }

    private static string CreateFailureMessage(HttpStatusCode statusCode, string content)
    {
        var message = $"Codex model discovery failed with HTTP {(int)statusCode}.";
        var detail = TryReadErrorDetail(content);
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = content.Trim();
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            return message;
        }

        const int maxDetailLength = 500;
        if (detail.Length > maxDetailLength)
        {
            detail = detail[..maxDetailLength] + "…";
        }

        return message + " " + detail;
    }

    private static string? TryReadErrorDetail(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("detail", out var detailElement) &&
                detailElement.ValueKind is JsonValueKind.String)
            {
                return detailElement.GetString();
            }

            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind is JsonValueKind.String)
                {
                    return errorElement.GetString();
                }

                if (errorElement.ValueKind is JsonValueKind.Object &&
                    errorElement.TryGetProperty("message", out var messageElement) &&
                    messageElement.ValueKind is JsonValueKind.String)
                {
                    return messageElement.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static IReadOnlyList<CodexSubscriptionDiscoveredModel> ParseModels(string content, string? etag)
    {
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("models", out var modelsElement) ||
            modelsElement.ValueKind is not JsonValueKind.Array)
        {
            throw new JsonException("Codex model response must contain a models array.");
        }

        var models = new List<CodexSubscriptionDiscoveredModel>();
        foreach (var modelElement in modelsElement.EnumerateArray())
        {
            var id = GetString(modelElement, "id") ??
                GetString(modelElement, "slug") ??
                GetString(modelElement, "name");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var visibility = GetString(modelElement, "visibility");
            models.Add(new CodexSubscriptionDiscoveredModel(
                id.Trim(),
                GetString(modelElement, "display_name") ??
                    GetString(modelElement, "displayName") ??
                    GetString(modelElement, "name") ??
                    id.Trim(),
                GetBoolean(modelElement, "supported_in_api") ?? GetBoolean(modelElement, "supportedInApi") ?? false,
                GetBoolean(modelElement, "listable") ?? GetBoolean(modelElement, "is_listable") ?? IsListVisibility(visibility),
                GetBoolean(modelElement, "hidden") ?? IsHiddenVisibility(visibility),
                GetBoolean(modelElement, "requires_websocket") ?? GetBoolean(modelElement, "requiresWebSocket") ?? false,
                GetBoolean(modelElement, "supports_reasoning_effort") ?? GetBoolean(modelElement, "supportsReasoningEffort") ?? true,
                GetReasoningSummaryParameterSupport(modelElement),
                GetBoolean(modelElement, "supports_encrypted_reasoning") ?? GetBoolean(modelElement, "supportsEncryptedReasoning") ?? true,
                GetBoolean(modelElement, "supports_text_verbosity") ??
                    GetBoolean(modelElement, "supportsTextVerbosity") ??
                    GetBoolean(modelElement, "support_verbosity") ??
                    false,
                GetBoolean(modelElement, "supports_image_input") ??
                    GetBoolean(modelElement, "supportsImageInput") ??
                    ContainsString(modelElement, "input_modalities", "image"),
                GetBoolean(modelElement, "supports_tools") ?? GetBoolean(modelElement, "supportsTools") ?? true,
                GetBoolean(modelElement, "supports_parallel_tool_calls") ?? false,
                GetBoolean(modelElement, "supports_image_detail_original") ?? false,
                GetBoolean(modelElement, "use_responses_lite") ?? false,
                GetReasoningEfforts(modelElement),
                GetString(modelElement, "default_reasoning_effort") ??
                    GetString(modelElement, "defaultReasoningEffort") ??
                    GetString(modelElement, "default_reasoning_level"),
                GetString(modelElement, "default_text_verbosity") ??
                    GetString(modelElement, "defaultTextVerbosity") ??
                    GetString(modelElement, "default_verbosity"),
                GetInt64(modelElement, "context_window") ?? GetInt64(modelElement, "contextWindow"),
                etag));
        }

        return models;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? GetBoolean(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static bool GetReasoningSummaryParameterSupport(JsonElement element)
    {
        if (element.TryGetProperty("supports_reasoning_summary_parameter", out var currentValue) ||
            element.TryGetProperty("supportsReasoningSummaryParameter", out currentValue) ||
            element.TryGetProperty("supports_reasoning_summaries", out currentValue) ||
            element.TryGetProperty("supports_reasoning_summary", out currentValue) ||
            element.TryGetProperty("supportsReasoningSummary", out currentValue))
        {
            return currentValue.ValueKind is JsonValueKind.True or JsonValueKind.False && currentValue.GetBoolean();
        }

        // Current Codex model metadata defaults this backward-compatible capability to true when omitted.
        return true;
    }

    private static bool IsListVisibility(string? visibility)
        => string.IsNullOrWhiteSpace(visibility) || string.Equals(visibility, "list", StringComparison.OrdinalIgnoreCase);

    private static bool IsHiddenVisibility(string? visibility)
        => string.Equals(visibility, "hidden", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsString(JsonElement element, string name, string expected)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.String &&
                string.Equals(item.GetString(), expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string>? GetReasoningEfforts(JsonElement element)
    {
        if (!element.TryGetProperty("supported_reasoning_levels", out var values) &&
            !element.TryGetProperty("supportedReasoningEfforts", out values))
        {
            return null;
        }

        if (values.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        var efforts = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            var effort = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Object => GetString(value, "effort"),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(effort))
            {
                efforts.Add(effort.Trim());
            }
        }

        return efforts;
    }

    private static long? GetInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number &&
            value.TryGetInt64(out var number)
            ? number
            : null;
}

internal sealed record CodexSubscriptionDiscoveredModel(
    string Id,
    string DisplayName,
    bool SupportedInApi,
    bool Listable,
    bool Hidden,
    bool RequiresWebSocket,
    bool SupportsReasoningEffort,
    bool SupportsReasoningSummary,
    bool SupportsEncryptedReasoning,
    bool SupportsTextVerbosity,
    bool SupportsImageInput,
    bool SupportsTools,
    bool SupportsParallelToolCalls,
    bool SupportsImageDetailOriginal,
    bool UseResponsesLite,
    IReadOnlyList<string>? SupportedReasoningEfforts,
    string? DefaultReasoningEffort,
    string? DefaultTextVerbosity,
    long? ContextWindow,
    string? ETag);

internal sealed class CodexSubscriptionModelDiscoveryException : Exception
{
    public CodexSubscriptionModelDiscoveryException(
        string message,
        HttpStatusCode? statusCode,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}
