using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeAlta.Agent.LocalRuntime;

namespace CodeAlta.Agent.OpenAI.CodexSubscription;

internal sealed class CodexSubscriptionModelDiscoveryClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAICodexSubscriptionAuthManager _authManager;
    private readonly OpenAICodexSubscriptionOptions _options;
    private readonly string _userAgentApplicationId;

    public CodexSubscriptionModelDiscoveryClient(
        HttpClient httpClient,
        OpenAICodexSubscriptionAuthManager authManager,
        OpenAICodexSubscriptionOptions options,
        string userAgentApplicationId)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(authManager);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgentApplicationId);

        _httpClient = httpClient;
        _authManager = authManager;
        _options = options;
        _userAgentApplicationId = userAgentApplicationId;
    }

    public async ValueTask<IReadOnlyList<CodexSubscriptionDiscoveredModel>> GetModelsAsync(
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        var credential = await _authManager.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        var accountContext = await _authManager.GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, CreateModelsUri(baseUri, _userAgentApplicationId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.TryAddWithoutValidation("originator", "codealta");
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgentApplicationId);
        if (!string.IsNullOrWhiteSpace(accountContext.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountContext.AccountId);
        }

        if (_options.SendResponsesBetaHeader)
        {
            request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        }

        if (accountContext.IsFedRamp)
        {
            request.Headers.TryAddWithoutValidation("X-OpenAI-Fedramp", "true");
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CodexSubscriptionModelDiscoveryException(
                $"Codex model discovery failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ParseModels(content, response.Headers.ETag?.Tag);
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

        var baseText = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri.AbsoluteUri
            : baseUri.AbsoluteUri + "/";
        var builder = new UriBuilder(new Uri(new Uri(baseText), "models"));
        builder.Query = "client_version=" + Uri.EscapeDataString(clientVersion);
        return builder.Uri;
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
            var id = GetString(modelElement, "id") ?? GetString(modelElement, "name");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            models.Add(new CodexSubscriptionDiscoveredModel(
                id.Trim(),
                GetString(modelElement, "display_name") ??
                    GetString(modelElement, "displayName") ??
                    GetString(modelElement, "name") ??
                    id.Trim(),
                GetBoolean(modelElement, "supported_in_api") ?? GetBoolean(modelElement, "supportedInApi") ?? false,
                GetBoolean(modelElement, "listable") ?? GetBoolean(modelElement, "is_listable") ?? true,
                GetBoolean(modelElement, "hidden") ?? false,
                GetBoolean(modelElement, "requires_websocket") ?? GetBoolean(modelElement, "requiresWebSocket") ?? false,
                GetBoolean(modelElement, "supports_reasoning_effort") ?? GetBoolean(modelElement, "supportsReasoningEffort") ?? true,
                GetBoolean(modelElement, "supports_reasoning_summary") ?? GetBoolean(modelElement, "supportsReasoningSummary") ?? true,
                GetBoolean(modelElement, "supports_encrypted_reasoning") ?? GetBoolean(modelElement, "supportsEncryptedReasoning") ?? true,
                GetBoolean(modelElement, "supports_text_verbosity") ?? GetBoolean(modelElement, "supportsTextVerbosity") ?? true,
                GetBoolean(modelElement, "supports_image_input") ?? GetBoolean(modelElement, "supportsImageInput") ?? false,
                GetBoolean(modelElement, "supports_tools") ?? GetBoolean(modelElement, "supportsTools") ?? true,
                GetString(modelElement, "default_reasoning_effort") ?? GetString(modelElement, "defaultReasoningEffort"),
                GetString(modelElement, "default_text_verbosity") ?? GetString(modelElement, "defaultTextVerbosity"),
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
