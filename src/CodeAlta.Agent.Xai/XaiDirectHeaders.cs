using System.Net.Http.Headers;

namespace CodeAlta.Agent.Xai;

internal static class XaiDirectHeaders
{
    public static void ApplyStaticHeaders(HttpRequestHeaders headers)
    {
        headers.TryAddWithoutValidation("User-Agent", "CodeAlta/1.0");
    }

    public static void ApplyExtraHeaders(
        HttpRequestHeaders headers,
        IReadOnlyDictionary<string, string>? extraHeaders)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (extraHeaders is not { Count: > 0 })
        {
            return;
        }

        foreach (var header in extraHeaders)
        {
            if (!string.IsNullOrWhiteSpace(header.Key))
            {
                headers.Remove(header.Key);
                headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }
}
