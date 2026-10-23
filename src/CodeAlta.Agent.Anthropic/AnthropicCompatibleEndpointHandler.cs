namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Keeps the headers of a request to an endpoint other than the Claude API (an Anthropic-compatible gateway such as Kimi or
/// MiniMax, the Messages route of Copilot) as the SDK sent them before its version 13: the API key in <c>X-Api-Key</c>
/// rather than <c>Authorization: Bearer</c>, and <c>anthropic-version: 2023-06-01</c> rather than <c>2026-10-21</c>.
/// </summary>
/// <remarks>
/// The JSON of a request is the same under both API versions; the Claude API takes either header and the new version. Another
/// vendor's endpoint may read only <c>X-Api-Key</c> or refuse a version it does not know. A bearer token that is not the API
/// key of the provider (the token of Copilot) is left as it is.
/// </remarks>
internal sealed class AnthropicCompatibleEndpointHandler(string? apiKey) : DelegatingHandler
{
    internal const string ApiVersion = "2023-06-01";

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!AnthropicModelProviderRuntime.IsClaudeApi(request.RequestUri))
        {
            if (request.Headers.Remove("anthropic-version"))
            {
                request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
            }

            if (!string.IsNullOrEmpty(apiKey)
                && request.Headers.Authorization is { Scheme: var scheme, Parameter: var parameter }
                && string.Equals(scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                && string.Equals(parameter, apiKey, StringComparison.Ordinal))
            {
                request.Headers.Authorization = null;
                request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
