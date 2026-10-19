namespace CodeAlta.Agent.Xai;

/// <summary>
/// What the sessions of one provider registration share: the HTTP client, with its pool of connections, and the
/// credential manager, so that a token is refreshed once for all of them.
/// </summary>
internal sealed class XaiDirectProviderServices
{
    public XaiDirectProviderServices(XaiProviderOptions provider, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(httpClient);
        HttpClient = httpClient;
        AuthManager = new XaiDirectAuthManager(provider, httpClient);
    }

    public HttpClient HttpClient { get; }

    public XaiDirectAuthManager AuthManager { get; }
}
