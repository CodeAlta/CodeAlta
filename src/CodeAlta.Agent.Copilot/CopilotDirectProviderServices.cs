namespace CodeAlta.Agent.Copilot;

/// <summary>
/// What the sessions of one provider registration share: the HTTP client, with its pool of connections, and the
/// credential manager, so that a token is exchanged or refreshed once for all of them.
/// </summary>
internal sealed class CopilotDirectProviderServices
{
    public CopilotDirectProviderServices(CopilotDirectProviderOptions provider, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(httpClient);
        HttpClient = httpClient;
        AuthManager = new CopilotDirectAuthManager(provider, httpClient);
    }

    public HttpClient HttpClient { get; }

    public CopilotDirectAuthManager AuthManager { get; }
}
