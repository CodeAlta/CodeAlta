#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenAI;
using OpenAI.Responses;

namespace CodeAlta.Agent.OpenAI.Codex;

internal static class CodexProtocolEventParser
{
    public static CodexProtocolEvent CreateInitialHttpEvent(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new CodexProtocolEvent(
            CodexProtocolTransport.Http,
            Type: null,
            Update: null,
            ParseHeaders(response.Headers, response.Content.Headers));
    }

    public static CodexProtocolEvent Parse(CodexProtocolTransport transport, BinaryData payload, string? sseEventType = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Empty(transport, sseEventType);
            }

            var root = document.RootElement;
            var type = !string.IsNullOrWhiteSpace(sseEventType) &&
                       !string.Equals(sseEventType, "message", StringComparison.Ordinal)
                ? sseEventType
                : GetString(root, "type");
            var normalizedPayload = string.Equals(type, "response.done", StringComparison.Ordinal)
                ? NormalizeLegacyDone(root)
                : payload;
            var metadata = ParseMetadata(root);
            var terminal = ParseTerminal(root, type);
            StreamingResponseUpdate? update = null;

            if (!IsMetadataOnly(type) &&
                (string.Equals(type, "error", StringComparison.Ordinal) ||
                 type?.StartsWith("response.", StringComparison.Ordinal) == true))
            {
                try
                {
                    update = ModelReaderWriter.Read<StreamingResponseUpdate>(
                        normalizedPayload,
                        new ModelReaderWriterOptions("J"),
                        OpenAIContext.Default);
                }
                catch (JsonException ex)
                {
                    // Codex intentionally tolerates malformed and newly introduced non-terminal events.
                    if (IsTerminal(type))
                    {
                        throw new OpenAIResponsesProtocolException(
                            OpenAIResponsesProtocolErrorCode.UnsupportedTerminalResponseUpdate,
                            "Codex returned a malformed terminal response update.",
                            ex);
                    }
                }
            }

            return new CodexProtocolEvent(transport, type, update, metadata, terminal);
        }
        catch (JsonException)
        {
            return Empty(transport, sseEventType);
        }
    }

    private static CodexProtocolEvent Empty(CodexProtocolTransport transport, string? type)
        => new(transport, type, null, new CodexResponseMetadata());

    private static CodexResponseMetadata ParseHeaders(
        HttpResponseHeaders headers,
        HttpContentHeaders contentHeaders)
    {
        string? Header(string name)
            => TryGetHeader(headers, name) ?? TryGetHeader(contentHeaders, name);

        return new CodexResponseMetadata(
            RequestId: Header("x-request-id"),
            EffectiveModel: Header("OpenAI-Model"),
            ModelsETag: Header("x-models-etag"),
            ReasoningIncluded: ParseBoolean(Header("x-reasoning-included")),
            SafetyBuffering: CreateHeaderSafetyBuffering(
                Header("x-codex-safety-buffering-enabled"),
                Header("x-codex-safety-buffering-faster-model")),
            RateLimits: ParseRateLimitHeaders(headers, contentHeaders));
    }

    private static CodexResponseMetadata ParseMetadata(JsonElement root)
    {
        var headers = GetNestedObject(root, "response", "headers") ?? GetObject(root, "headers");
        var metadata = GetObject(root, "metadata") ?? GetNestedObject(root, "response", "metadata");
        var safety = GetObject(root, "safety_buffering") ??
                     GetNestedObject(root, "response", "safety_buffering") ??
                     (metadata is { } metadataObject ? GetObject(metadataObject, "safety_buffering") : null);

        var retryModelElement = default(JsonElement);
        var retryModelPresent = safety is { } safetyObject && safetyObject.TryGetProperty("retry_model"u8, out retryModelElement);
        var retryModel = retryModelPresent && retryModelElement.ValueKind == JsonValueKind.String
            ? retryModelElement.GetString()
            : null;
        var parsedSafety = safety is null
            ? null
            : new CodexSafetyBuffering(
                retryModelPresent,
                retryModel,
                 GetString(safety.Value, "treatment"),
                 Message: GetString(safety.Value, "message"));

        return new CodexResponseMetadata(
            RequestId: GetString(headers, "x-request-id"),
            EffectiveModel: GetString(headers, "OpenAI-Model") ?? GetString(headers, "x-openai-model") ?? GetString(root, "model"),
            ModelsETag: GetString(headers, "x-models-etag"),
            ReasoningIncluded: GetBoolean(headers, "x-reasoning-included"),
            SafetyBuffering: parsedSafety,
            RateLimits: ParseRateLimits(root, headers),
            VerificationRecommendation: GetVerificationRecommendation(metadata),
            TurnModeration: GetBoundedJson(metadata, "openai_chatgpt_moderation_metadata"),
            TurnState: GetString(headers, "x-codex-turn-state"));
    }

    private static CodexTerminalMetadata? ParseTerminal(JsonElement root, string? type)
    {
        if (!IsTerminal(type))
        {
            return null;
        }

        var response = GetObject(root, "response");
        return new CodexTerminalMetadata(GetBoolean(response, "end_turn"));
    }

    private static IReadOnlyList<CodexNamedRateLimitSnapshot>? ParseRateLimits(JsonElement root, JsonElement? headers)
    {
        var limits = new List<CodexNamedRateLimitSnapshot>();
        if (string.Equals(GetString(root, "type"), "codex.rate_limits", StringComparison.Ordinal))
        {
            var details = GetObject(root, "rate_limits");
            limits.Add(new CodexNamedRateLimitSnapshot(
                NormalizeLimitName(GetString(root, "metered_limit_name") ?? GetString(root, "limit_name") ?? "codex"),
                details is { } rateLimits ? ParseRateLimitWindow(rateLimits, "primary") : null,
                details is { } rateLimitDetails ? ParseRateLimitWindow(rateLimitDetails, "secondary") : null,
                GetString(root, "limit_name"),
                GetString(root, "plan_type"),
                ParseCredits(root)));
        }

        if (headers is { } headerObject)
        {
            AddHeaderRateLimits(headerObject, limits);
        }

        return limits.Count == 0 ? null : limits.AsReadOnly();
    }

    private static IReadOnlyList<CodexNamedRateLimitSnapshot>? ParseRateLimitHeaders(
        HttpResponseHeaders headers,
        HttpContentHeaders contentHeaders)
    {
        var values = headers.Concat(contentHeaders)
            .Where(static header => header.Key.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(static header => header.Key, static header => header.Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);
        var limits = new List<CodexNamedRateLimitSnapshot>();
        var names = values.Keys
            .Select(TryGetRateLimitName)
            .Where(static name => name is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var name in names)
        {
            limits.Add(new CodexNamedRateLimitSnapshot(
                name,
                ParseHeaderRateLimitWindow(values, name, "primary"),
                ParseHeaderRateLimitWindow(values, name, "secondary"),
                TryGetValue(values, $"x-{name.Replace('_', '-')}-limit-name"),
                Credits: string.Equals(name, "codex", StringComparison.Ordinal) ? ParseHeaderCredits(values) : null));
        }

        return limits.Count == 0 ? null : limits.AsReadOnly();
    }

    private static void AddHeaderRateLimits(JsonElement headers, List<CodexNamedRateLimitSnapshot> limits)
    {
        foreach (var property in headers.EnumerateObject())
        {
            var name = TryGetRateLimitName(property.Name);
            if (name is null || limits.Any(limit => string.Equals(limit.Name, name, StringComparison.Ordinal)))
            {
                continue;
            }

            limits.Add(new CodexNamedRateLimitSnapshot(
                name,
                ParseJsonHeaderRateLimitWindow(headers, name, "primary"),
                ParseJsonHeaderRateLimitWindow(headers, name, "secondary"),
                GetString(headers, $"x-{name.Replace('_', '-')}-limit-name"),
                Credits: string.Equals(name, "codex", StringComparison.Ordinal) ? ParseJsonHeaderCredits(headers) : null));
        }
    }

    private static CodexSafetyBuffering? CreateHeaderSafetyBuffering(string? enabled, string? fallbackRetryModel)
        => enabled is null && fallbackRetryModel is null
            ? null
            : new CodexSafetyBuffering(false, null, enabled, fallbackRetryModel);

    private static CodexNamedRateLimitWindow? ParseRateLimitWindow(JsonElement details, string name)
        => GetObject(details, name) is { } window
            ? new CodexNamedRateLimitWindow(
                GetDouble(window, "used_percent"),
                GetInt64(window, "window_minutes"),
                GetUnixTime(window, "reset_at"))
            : null;

    private static CodexNamedCreditsSnapshot? ParseCredits(JsonElement root)
        => GetObject(root, "credits") is { } credits &&
           GetBoolean(credits, "has_credits") is { } hasCredits &&
           GetBoolean(credits, "unlimited") is { } unlimited
            ? new CodexNamedCreditsSnapshot(hasCredits, unlimited, GetString(credits, "balance"))
            : null;

    private static CodexNamedRateLimitWindow? ParseHeaderRateLimitWindow(
        IReadOnlyDictionary<string, string?> values,
        string name,
        string window)
    {
        var prefix = $"x-{name.Replace('_', '-')}-{window}";
        var used = TryGetDouble(values, $"{prefix}-used-percent");
        if (used is null)
        {
            return null;
        }

        return new CodexNamedRateLimitWindow(
            used,
            TryGetLong(values, $"{prefix}-window-minutes"),
            TryGetUnixTime(values, $"{prefix}-reset-at"));
    }

    private static CodexNamedRateLimitWindow? ParseJsonHeaderRateLimitWindow(JsonElement headers, string name, string window)
    {
        var prefix = $"x-{name.Replace('_', '-')}-{window}";
        var used = GetDoubleProperty(headers, $"{prefix}-used-percent");
        return used is null
            ? null
            : new CodexNamedRateLimitWindow(
                used,
                GetInt64Property(headers, $"{prefix}-window-minutes"),
                GetUnixTimeProperty(headers, $"{prefix}-reset-at"));
    }

    private static CodexNamedCreditsSnapshot? ParseHeaderCredits(IReadOnlyDictionary<string, string?> values)
        => TryGetBoolean(values, "x-codex-credits-has-credits") is { } hasCredits &&
           TryGetBoolean(values, "x-codex-credits-unlimited") is { } unlimited
            ? new CodexNamedCreditsSnapshot(hasCredits, unlimited, TryGetValue(values, "x-codex-credits-balance"))
            : null;

    private static CodexNamedCreditsSnapshot? ParseJsonHeaderCredits(JsonElement headers)
        => GetBoolean(headers, "x-codex-credits-has-credits") is { } hasCredits &&
           GetBoolean(headers, "x-codex-credits-unlimited") is { } unlimited
            ? new CodexNamedCreditsSnapshot(hasCredits, unlimited, GetString(headers, "x-codex-credits-balance"))
            : null;

    private static string? TryGetRateLimitName(string headerName)
    {
        const string primarySuffix = "-primary-used-percent";
        const string secondarySuffix = "-secondary-used-percent";
        var normalized = headerName.ToLowerInvariant();
        var suffix = normalized.EndsWith(primarySuffix, StringComparison.Ordinal) ? primarySuffix :
            normalized.EndsWith(secondarySuffix, StringComparison.Ordinal) ? secondarySuffix : null;
        return suffix is not null && normalized.StartsWith("x-", StringComparison.Ordinal)
            ? NormalizeLimitName(normalized[2..^suffix.Length])
            : null;
    }

    private static string NormalizeLimitName(string name) => name.Trim().ToLowerInvariant().Replace('-', '_');

    private static BinaryData NormalizeLegacyDone(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
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

        return BinaryData.FromBytes(stream.ToArray());
    }

    private static bool IsMetadataOnly(string? type)
        => string.IsNullOrWhiteSpace(type) ||
           type is "response.metadata" or "response.server_model" or "response.models_etag" or "safety_buffering" ||
           type.StartsWith("response.rate_limits", StringComparison.Ordinal) ||
           type.StartsWith("response.model_verification", StringComparison.Ordinal) ||
           type.StartsWith("codex.", StringComparison.Ordinal);

    internal static bool IsTerminal(string? type)
        => type is "response.completed" or "response.incomplete" or "response.failed" or "response.done";

    private static JsonElement? GetObject(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static JsonElement? GetNestedObject(JsonElement element, string objectName, string name)
        => GetObject(element, objectName) is { } nested ? GetObject(nested, name) : null;

    private static string? GetString(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } value &&
           TryGetProperty(value, name, out var property) &&
           (property.ValueKind == JsonValueKind.String ||
            property.ValueKind == JsonValueKind.Array && property.GetArrayLength() > 0 && property[0].ValueKind == JsonValueKind.String)
            ? property.ValueKind == JsonValueKind.String ? property.GetString() : property[0].GetString()
            : null;

    private static bool? GetBoolean(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value || !TryGetProperty(value, name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => ParseBoolean(property.GetString()),
            _ => null,
        };
    }

    private static bool? ParseBoolean(string? value)
        => bool.TryParse(value, out var parsed) ? parsed : null;

    private static double? GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? GetDouble(value) : null;

    private static double? GetDouble(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed)
            ? parsed
            : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : null;

    private static long? GetInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;

    private static DateTimeOffset? GetUnixTime(JsonElement element, string name)
        => GetInt64(element, name) is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static string? GetBoundedJson(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property))
        {
            return null;
        }

        var json = property.GetRawText();
        return json.Length <= 4096 ? json : json[..4096];
    }

    private static string? GetVerificationRecommendation(JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("openai_verification_recommendation"u8, out var recommendations) ||
            recommendations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return recommendations.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .FirstOrDefault(static item => string.Equals(item, "trusted_access_for_cyber", StringComparison.Ordinal));
    }

    private static double? GetDoubleProperty(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) ? GetDouble(value) : null;

    private static long? GetInt64Property(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;

    private static DateTimeOffset? GetUnixTimeProperty(JsonElement element, string name)
        => GetInt64Property(element, name) is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static string? TryGetHeader(HttpHeaders headers, string name)
        => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static long? TryGetLong(IReadOnlyDictionary<string, string?> values, string name)
        => values.TryGetValue(name, out var value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static double? TryGetDouble(IReadOnlyDictionary<string, string?> values, string name)
        => values.TryGetValue(name, out var value) &&
           double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
           double.IsFinite(parsed)
            ? parsed
            : null;

    private static bool? TryGetBoolean(IReadOnlyDictionary<string, string?> values, string name)
        => values.TryGetValue(name, out var value) ? ParseBoolean(value) : null;

    private static string? TryGetValue(IReadOnlyDictionary<string, string?> values, string name)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static DateTimeOffset? TryGetUnixTime(IReadOnlyDictionary<string, string?> values, string name)
        => TryGetLong(values, name) is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
