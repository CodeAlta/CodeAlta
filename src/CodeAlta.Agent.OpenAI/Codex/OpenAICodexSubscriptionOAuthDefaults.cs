namespace CodeAlta.Agent.OpenAI.Codex;

internal static class OpenAICodexSubscriptionOAuthDefaults
{
    public const string Issuer = "https://auth.openai.com";
    public const string AuthorizeEndpoint = "https://auth.openai.com/api/accounts/authorize";
    public const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    public const string DiscoveryEndpoint = "https://auth.openai.com/.well-known/openid-configuration";
    public const string JwksEndpoint = "https://auth.openai.com/.well-known/jwks.json";
    public const string Resource = "https://api.openai.com/v1";
    public const int LocalPort = 1455;
    public const string RedirectUri = "http://127.0.0.1:1455/auth/callback";
    public const string ClientId = "dynamic_agent_client";
    public const string DirectTokenScope = "chatgpt.tokens.use.direct";
    public const string Scope = "openid profile email offline_access resource.invoke " + DirectTokenScope;
}
