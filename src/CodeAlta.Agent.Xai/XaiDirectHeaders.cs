using System.Net.Http.Headers;

namespace CodeAlta.Agent.Xai;

internal static class XaiDirectHeaders
{
    public static void ApplyStaticHeaders(HttpRequestHeaders headers)
    {
        headers.TryAddWithoutValidation("User-Agent", "CodeAlta/1.0");
    }
}
