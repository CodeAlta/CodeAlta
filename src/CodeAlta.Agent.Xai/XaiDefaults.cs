namespace CodeAlta.Agent.Xai;

internal static class XaiDefaults
{
    // Public Grok-CLI OAuth client. xAI's auth server only allowlists redirect URIs and
    // ports registered for specific clients, so we reuse the Grok-CLI client id that xAI
    // ships for desktop OAuth flows. The matching loopback port (56121) and redirect path
    // are part of that registration and cannot be changed.
    public const string OAuthClientId = "b1a00492-073a-47ea-816f-4c329264a828";

    public const string AuthorizeEndpoint = "https://auth.x.ai/oauth2/authorize";
    public const string TokenEndpoint = "https://auth.x.ai/oauth2/token";
    public const string DeviceAuthorizationEndpoint = "https://auth.x.ai/oauth2/device/code";

    public const string Scope = "openid profile email offline_access grok-cli:access api:access";

    public const string LoopbackHost = "127.0.0.1";
    public const int LoopbackPort = 56121;
    public const string LoopbackRedirectPath = "/callback";
    public const string LoopbackRedirectUri = "http://127.0.0.1:56121/callback";

    // `plan=generic` opts the consent screen into xAI's generic OAuth plan tier; without it
    // accounts.x.ai rejects loopback OAuth from non-allowlisted clients.
    public const string PlanParameter = "generic";

    public const string Referrer = "codealta";

    public const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    // Trailing slash is required so `new Uri(baseUri, "models")` resolves to
    // `/v1/models`. Without it, the `/v1` segment is treated as the resource
    // name and dropped during the relative-URI join.
    public static readonly Uri DefaultApiBaseUri = new("https://api.x.ai/v1/");
}
