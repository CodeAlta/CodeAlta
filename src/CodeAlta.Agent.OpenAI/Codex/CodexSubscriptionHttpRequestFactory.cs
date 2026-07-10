using System.Net.Http.Headers;

namespace CodeAlta.Agent.OpenAI.Codex;

internal static class CodexSubscriptionHttpRequestFactory
{
    internal static readonly Uri DefaultBaseUri = new("https://chatgpt.com/backend-api/codex");

    public static Uri ResolveEndpoint(Uri? baseUri, string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        var builder = new UriBuilder(baseUri ?? DefaultBaseUri);
        var path = builder.Path.TrimEnd('/');
        var suffix = "/" + endpoint.Trim('/');
        if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = path + suffix;
        }

        return builder.Uri;
    }

    public static Uri AppendQueryParameter(Uri uri, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var builder = new UriBuilder(uri);
        var existing = builder.Query.TrimStart('?');
        var parameter = Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
        builder.Query = string.IsNullOrEmpty(existing) ? parameter : existing + "&" + parameter;
        return builder.Uri;
    }

    public static HttpClient ResolveHttpClient(OpenAIProviderOptions provider, out bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.HttpClient is { } configuredClient)
        {
            ownsClient = false;
            return configuredClient;
        }

        if (provider.CodexSubscriptionHttpClient is { } subscriptionClient)
        {
            ownsClient = false;
            return subscriptionClient;
        }

        ownsClient = true;
        return new HttpClient();
    }

    public static async ValueTask<CodexSubscriptionHttpIdentity> CreateIdentityAsync(
        OpenAICodexSubscriptionAuthManager authManager,
        OpenAICodexSubscriptionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authManager);
        ArgumentNullException.ThrowIfNull(options);
        var credential = await authManager.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        var account = await authManager.GetAccountContextAsync(cancellationToken).ConfigureAwait(false);
        return new CodexSubscriptionHttpIdentity(
            credential.AccessToken,
            string.IsNullOrWhiteSpace(options.AccountId) ? account.AccountId : options.AccountId,
            account.IsFedRamp,
            OpenAIProviderSdkFactory.CreateCodeAltaUserAgentApplicationId());
    }

    public static void ApplyIdentity(HttpRequestMessage request, CodexSubscriptionHttpIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.AccessToken);
        SetHeader(request, "originator", "codealta");
        SetHeader(request, "User-Agent", identity.UserAgentApplicationId);
        if (!string.IsNullOrWhiteSpace(identity.AccountId))
        {
            SetHeader(request, "ChatGPT-Account-Id", identity.AccountId);
        }

        if (identity.IsFedRamp)
        {
            SetHeader(request, "X-OpenAI-Fedramp", "true");
        }
    }

    public static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }
}

internal sealed record CodexSubscriptionHttpIdentity(
    string AccessToken,
    string? AccountId,
    bool IsFedRamp,
    string UserAgentApplicationId);
