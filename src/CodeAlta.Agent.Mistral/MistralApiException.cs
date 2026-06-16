using System.Net;
using System.Text.Json;

namespace CodeAlta.Agent.Mistral;

/// <summary>
/// Represents a structured Mistral API error response.
/// </summary>
internal sealed class MistralApiException : HttpRequestException
{
    public MistralApiException(
        string message,
        HttpStatusCode? statusCode,
        string? errorCode,
        string? errorType,
        string? param,
        string? requestId,
        TimeSpan? retryAfter,
        string? responseBody)
        : base(message, inner: null, statusCode)
    {
        ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode;
        ErrorType = string.IsNullOrWhiteSpace(errorType) ? null : errorType;
        Param = string.IsNullOrWhiteSpace(param) ? null : param;
        RequestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId;
        RetryAfter = retryAfter;
        ResponseBody = string.IsNullOrWhiteSpace(responseBody) ? null : responseBody;
    }

    public string? ErrorCode { get; }

    public string? ErrorType { get; }

    public string? Param { get; }

    public string? RequestId { get; }

    public TimeSpan? RetryAfter { get; }

    public string? ResponseBody { get; }
}

internal readonly record struct MistralProviderError(
    string? Message,
    string? Type,
    string? Code,
    string? Param)
{
    public static MistralProviderError Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var source = root;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                source = error;
            }

            var message = GetString(source, "message") ?? GetString(root, "message") ?? GetString(root, "detail");
            var type = GetString(source, "type") ?? GetString(root, "type");
            var code = GetString(source, "code") ?? GetString(root, "code");
            var param = GetString(source, "param") ?? GetString(root, "param");
            return new MistralProviderError(message, type, code, param);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public string? FormatDetail()
    {
        if (string.IsNullOrWhiteSpace(Message) &&
            string.IsNullOrWhiteSpace(Type) &&
            string.IsNullOrWhiteSpace(Code) &&
            string.IsNullOrWhiteSpace(Param))
        {
            return null;
        }

        var detail = string.IsNullOrWhiteSpace(Message) ? "Provider error" : Message!.Trim();
        var qualifiers = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(Type))
        {
            qualifiers.Add($"type: {Type}");
        }

        if (!string.IsNullOrWhiteSpace(Code))
        {
            qualifiers.Add($"code: {Code}");
        }

        if (!string.IsNullOrWhiteSpace(Param))
        {
            qualifiers.Add($"param: {Param}");
        }

        return qualifiers.Count == 0 ? detail : $"{detail} ({string.Join(", ", qualifiers)})";
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }
}
