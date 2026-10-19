using System.Net;

namespace CodeAlta.Hosting;

/// <summary>
/// Creates the HTTP clients a provider registration gives all the runtimes, sessions and requests it creates, so that
/// they share one pool of connections (and their TLS sessions) where a client was made for each request or session.
/// </summary>
/// <remarks>
/// A client is owned by the registration that holds it: it is not disposed, since the sessions of a replaced
/// registration keep running on it; it is collected with the last runtime that uses it, and the pool closes its idle
/// connections after a minute. Each client keeps the settings of the client it replaces (decompression, timeout,
/// cookies), with a connection lifetime added so that a change of DNS reaches a long-lived pool. No request header is
/// set on a client: the providers set theirs on each request.
/// </remarks>
internal static class ProviderHttpClients
{
    /// <summary>The time after which a pooled connection is replaced by a new one.</summary>
    internal static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Replaces a <c>new HttpClient()</c> made for each request: no decompression, the default timeout of 100 seconds,
    /// and no cookie kept from one request to the next.
    /// </summary>
    public static HttpClient CreatePerRequestReplacement()
        => new(CreateHandler(DecompressionMethods.None, useCookies: false));

    /// <summary>
    /// Replaces a <c>new HttpClient()</c> each session kept: no decompression, the default timeout of 100 seconds, and the
    /// cookies of the responses kept for the next requests.
    /// </summary>
    public static HttpClient CreatePerSessionReplacement()
        => new(CreateHandler(DecompressionMethods.None, useCookies: true));

    /// <summary>
    /// Replaces the client the Anthropic SDK makes for each of its clients: gzip responses, no timeout of its own (the SDK
    /// bounds each attempt), and no cookie kept from one request to the next.
    /// </summary>
    public static HttpClient CreateForAnthropicSdk()
        => new(CreateHandler(DecompressionMethods.GZip, useCookies: false)) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Creates the pool behind the clients of the Google GenAI SDK, which disposes each client it is given: no
    /// decompression and no cookie kept from one request to the next, as its own clients.
    /// </summary>
    public static HttpMessageHandler CreateHandlerForGoogleSdk()
        => CreateHandler(DecompressionMethods.None, useCookies: false);

    private static SocketsHttpHandler CreateHandler(DecompressionMethods decompression, bool useCookies)
        => new()
        {
            AutomaticDecompression = decompression,
            UseCookies = useCookies,
            PooledConnectionLifetime = PooledConnectionLifetime,
        };
}
