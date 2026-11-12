using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Adds to a request of a turn the headers of the provider whose values name the session, filled for that turn
/// (<see cref="AgentRequestHeaderPlaceholders"/>): <c>x-opencode-session = "${session.id}"</c> keeps a conversation of
/// OpenCode on the same backend and its prompt cache.
/// </summary>
/// <param name="context">The headers of the turn that sends the request.</param>
internal sealed class AnthropicTurnHeadersHandler(AgentRequestHeaderContext context) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (context.Current is { Count: > 0 } headers)
        {
            foreach (var header in headers)
            {
                request.Headers.Remove(header.Key);
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
