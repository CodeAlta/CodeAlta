using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.OpenAI.Codex;

internal sealed class OpenAICodexSubscriptionOAuthClient
{
    private readonly HttpClient _httpClient;

    public OpenAICodexSubscriptionOAuthClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public static OpenAICodexSubscriptionPkce CreatePkce()
    {
        var verifier = CreateState();
        return new OpenAICodexSubscriptionPkce(verifier, Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static string CreateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static Uri BuildAuthorizeUri(
        OpenAICodexSubscriptionPkce pkce,
        string state,
        string nonce,
        string hostId,
        string redirectUri,
        OpenAICodexSubscriptionCredential? registration = null)
    {
        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = registration?.ClientId ?? OpenAICodexSubscriptionOAuthDefaults.ClientId,
            ["agent_name_hint"] = registration is null ? "CodeAlta" : null,
            ["ext_agent_host_id"] = hostId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = OpenAICodexSubscriptionOAuthDefaults.Scope,
            ["resource"] = OpenAICodexSubscriptionOAuthDefaults.Resource,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["nonce"] = nonce,
            // Deliberately omit the optional id_token_hint: the UI's copyable login URL
            // must not contain a retained credential. Returning users see account selection.
            ["login_hint"] = registration?.Email,
            // This button is an explicit request to enable plan usage after a decline.
            // Do not force consent on routine reauthorization of an existing grant.
            ["prompt"] = OpenAICodexSubscriptionLoginManager.IsRegistration(registration) && !registration.HasPlanUsagePermission ? "consent" : null,
        };
        return new Uri(OpenAICodexSubscriptionOAuthDefaults.AuthorizeEndpoint + "?" + string.Join(
            "&", query.Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
                .Select(static pair => WebUtility.UrlEncode(pair.Key) + "=" + WebUtility.UrlEncode(pair.Value))));
    }

    public static void ValidateState(string expected, string actual)
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual)))
        {
            throw new InvalidOperationException("OAuth state mismatch.");
        }
    }

    public async Task<OpenAICodexSubscriptionCredential> ExchangeAuthorizationCodeAsync(
        string code, string codeVerifier, string redirectUri, string clientId, string nonce,
        CancellationToken cancellationToken = default)
    {
        using var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
            ["resource"] = OpenAICodexSubscriptionOAuthDefaults.Resource,
        }, cancellationToken).ConfigureAwait(false);
        var credential = CredentialFromTokenJson(response.RootElement, clientId, DateTimeOffset.UtcNow);
        if (credential.HasPlanUsagePermission && string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            throw new InvalidOperationException("ChatGPT sign-in did not return a refresh token.");
        }

        await ValidateIdentityAsync(credential, nonce, expectedSubject: null, cancellationToken).ConfigureAwait(false);
        return credential;
    }

    public async Task<OpenAICodexSubscriptionCredential> RefreshAsync(
        OpenAICodexSubscriptionCredential previous,
        CancellationToken cancellationToken = default)
    {
        // Fetch signing keys before consuming a rotating refresh token. A transient
        // JWKS failure must leave the saved token usable for the next attempt.
        using var jwks = await GetJsonAsync(OpenAICodexSubscriptionOAuthDefaults.JwksEndpoint, cancellationToken).ConfigureAwait(false);
        using var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = previous.ClientId,
            ["refresh_token"] = previous.RefreshToken!,
            ["resource"] = OpenAICodexSubscriptionOAuthDefaults.Resource,
        }, cancellationToken).ConfigureAwait(false);
        var credential = CredentialFromTokenJson(response.RootElement, previous.ClientId, DateTimeOffset.UtcNow, previous.Scopes);
        credential.RefreshToken ??= previous.RefreshToken;
        credential.Subject = previous.Subject;
        credential.Email = previous.Email;
        credential.AccountId = previous.AccountId;
        credential.AccountLabel = previous.AccountLabel;
        credential.AgentHostId = previous.AgentHostId;
        if (credential.IdToken is not null)
        {
            ValidateIdentity(credential, jwks.RootElement, nonce: null, previous.Subject);
        }
        else
        {
            credential.IdToken = previous.IdToken;
        }

        return credential;
    }

    public async Task RevokeAsync(OpenAICodexSubscriptionCredential credential, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            return;
        }

        using var discovery = await GetJsonAsync(OpenAICodexSubscriptionOAuthDefaults.DiscoveryEndpoint, cancellationToken).ConfigureAwait(false);
        var endpoint = RequiredString(discovery.RootElement, "revocation_endpoint");
        // Never send a refresh token to an endpoint outside the pinned OpenAI origin.
        if (RequiredString(discovery.RootElement, "issuer") != OpenAICodexSubscriptionOAuthDefaults.Issuer ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "auth.openai.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Invalid ChatGPT revocation endpoint.");
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = credential.RefreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = credential.ClientId,
        });
        using var response = await _httpClient.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    internal static OpenAICodexSubscriptionCredential CredentialFromTokenJson(
        JsonElement root, string clientId, DateTimeOffset now, List<string>? previousScopes = null)
    {
        var scopes = GetString(root, "scope") is { } scope
            ? scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList()
            : previousScopes is null ? [] : new List<string>(previousScopes);
        if (!root.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt32(out var seconds) || seconds <= 0 ||
            !string.Equals(RequiredString(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ChatGPT token response contained an invalid expiry or token type.");
        }

        return new OpenAICodexSubscriptionCredential
        {
            ClientId = clientId,
            AccessToken = RequiredString(root, "access_token"),
            RefreshToken = GetString(root, "refresh_token"),
            IdToken = GetString(root, "id_token"),
            ExpiresAt = now.AddSeconds(seconds),
            Scopes = scopes,
        };
    }

    private async Task ValidateIdentityAsync(
        OpenAICodexSubscriptionCredential credential, string? nonce, string? expectedSubject, CancellationToken cancellationToken)
    {
        if (credential.IdToken is null)
        {
            throw new InvalidOperationException("ChatGPT sign-in did not return an ID token.");
        }

        using var jwks = await GetJsonAsync(OpenAICodexSubscriptionOAuthDefaults.JwksEndpoint, cancellationToken).ConfigureAwait(false);
        ValidateIdentity(credential, jwks.RootElement, nonce, expectedSubject);
    }

    private static void ValidateIdentity(
        OpenAICodexSubscriptionCredential credential, JsonElement jwks, string? nonce, string? expectedSubject)
    {
        var idToken = credential.IdToken ?? throw new InvalidOperationException("ChatGPT sign-in did not return an ID token.");
        var identity = OpenAICodexSubscriptionIdTokenValidator.Validate(idToken, jwks, credential.ClientId, nonce);
        if (expectedSubject is not null && identity.Subject != expectedSubject)
        {
            throw new InvalidOperationException("ChatGPT sign-in returned a different account. The saved registration was not changed.");
        }

        credential.Subject = identity.Subject;
        credential.Email = identity.Email;
        credential.AccountId = identity.AccountId;
        credential.AccountLabel = identity.Email ?? identity.Subject;
    }

    private async Task<JsonDocument> PostTokenAsync(Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await _httpClient.PostAsync(OpenAICodexSubscriptionOAuthDefaults.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        // Do not include raw response bodies: they may contain credentials.
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                await using var errorStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var error = await JsonDocument.ParseAsync(errorStream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (error.RootElement.ValueKind == JsonValueKind.Object && GetString(error.RootElement, "error") is { } errorCode)
                {
                    throw new OpenAICodexSubscriptionTokenException(errorCode, response.StatusCode);
                }
            }
            catch (JsonException)
            {
                // Non-OAuth infrastructure errors still retain their HTTP status.
            }
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetJsonAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal static string Base64UrlEncode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string RequiredString(JsonElement element, string name)
        => GetString(element, name) ?? throw new InvalidOperationException($"OAuth response did not include '{name}'.");

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString() : null;
}

internal sealed record OpenAICodexSubscriptionPkce(string Verifier, string Challenge);

internal sealed class OpenAICodexSubscriptionTokenException(string errorCode, HttpStatusCode statusCode)
    : HttpRequestException(errorCode == "invalid_client"
        ? "ChatGPT OAuth rejected the client registration (invalid_client). Check the saved issued client ID."
        : $"ChatGPT OAuth token request failed with HTTP {(int)statusCode}.", null, statusCode)
{
    public string ErrorCode { get; } = errorCode;

    public bool HasUnusableTokens => ErrorCode is "invalid_grant" or "invalid_refresh_token" or "token_expired" or
        "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused";
}
