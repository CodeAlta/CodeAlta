using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var verifier = Base64UrlEncode(bytes);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new OpenAICodexSubscriptionPkce(verifier, challenge);
    }

    public static string CreateState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public static Uri BuildAuthorizeUri(
        OpenAICodexSubscriptionPkce pkce,
        string state,
        string? allowedWorkspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(pkce);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = OpenAICodexSubscriptionOAuthDefaults.ClientId,
            ["redirect_uri"] = OpenAICodexSubscriptionOAuthDefaults.RedirectUri,
            ["scope"] = OpenAICodexSubscriptionOAuthDefaults.Scope,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "codealta",
        };
        if (!string.IsNullOrWhiteSpace(allowedWorkspaceId))
        {
            query["allowed_workspace_id"] = allowedWorkspaceId.Trim();
        }

        return new Uri(OpenAICodexSubscriptionOAuthDefaults.AuthorizeEndpoint + "?" + BuildFormUrlEncoded(query));
    }

    public static void ValidateState(string expected, string actual)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(actual)))
        {
            throw new InvalidOperationException("OAuth state mismatch.");
        }
    }

    public async Task<OpenAICodexSubscriptionCredential> ExchangeAuthorizationCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = OpenAICodexSubscriptionOAuthDefaults.ClientId,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["redirect_uri"] = redirectUri,
            });
        using var response = await _httpClient.PostAsync(
                OpenAICodexSubscriptionOAuthDefaults.TokenEndpoint,
                content,
                cancellationToken)
            .ConfigureAwait(false);
        return await ReadTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OpenAICodexSubscriptionCredential> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = OpenAICodexSubscriptionOAuthDefaults.ClientId,
                ["refresh_token"] = refreshToken,
            });
        using var response = await _httpClient.PostAsync(
                OpenAICodexSubscriptionOAuthDefaults.TokenEndpoint,
                content,
                cancellationToken)
            .ConfigureAwait(false);
        return await ReadTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OpenAICodexSubscriptionDeviceCode> RequestDeviceCodeAsync(
        CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(
                new OpenAICodexSubscriptionDeviceCodeRequest(OpenAICodexSubscriptionOAuthDefaults.ClientId),
                OpenAICodexSubscriptionJsonSerializerContext.Default.OpenAICodexSubscriptionDeviceCodeRequest),
            Encoding.UTF8,
            "application/json");
        using var response = await _httpClient.PostAsync(
                OpenAICodexSubscriptionOAuthDefaults.DeviceUserCodeEndpoint,
                content,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new OpenAICodexSubscriptionDeviceCode(
            RequiredString(root, "device_auth_id"),
            RequiredString(root, "user_code"),
            OpenAICodexSubscriptionOAuthDefaults.DeviceVerificationUri,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromSeconds(GetInt32OrString(root, "interval") ?? 5));
    }

    public async Task<OpenAICodexSubscriptionCredential> PollDeviceTokenAsync(
        OpenAICodexSubscriptionDeviceCode deviceCode,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceCode);
        timeProvider ??= TimeProvider.System;
        var expiresAt = timeProvider.GetUtcNow() + deviceCode.ExpiresIn;
        while (timeProvider.GetUtcNow() < expiresAt)
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(
                    new OpenAICodexSubscriptionDeviceTokenRequest(deviceCode.DeviceAuthId, deviceCode.UserCode),
                    OpenAICodexSubscriptionJsonSerializerContext.Default.OpenAICodexSubscriptionDeviceTokenRequest),
                Encoding.UTF8,
                "application/json");
            using var response = await _httpClient.PostAsync(
                    OpenAICodexSubscriptionOAuthDefaults.DeviceTokenEndpoint,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                _ = RequiredString(root, "code_challenge");
                return await ExchangeAuthorizationCodeAsync(
                        RequiredString(root, "authorization_code"),
                        RequiredString(root, "code_verifier"),
                        OpenAICodexSubscriptionOAuthDefaults.DeviceRedirectUri,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (response.StatusCode is not HttpStatusCode.Forbidden and not HttpStatusCode.NotFound)
            {
                var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Device authorization failed: {error ?? response.StatusCode.ToString()}.");
            }

            await Task.Delay(deviceCode.Interval, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Device authorization expired before login completed.");
    }

    public static OpenAICodexSubscriptionCredential CredentialFromTokenJson(JsonElement root, DateTimeOffset now)
    {
        var expiresIn = GetInt32(root, "expires_in");
        return new OpenAICodexSubscriptionCredential
        {
            Issuer = OpenAICodexSubscriptionOAuthDefaults.Issuer,
            ClientId = OpenAICodexSubscriptionOAuthDefaults.ClientId,
            AccessToken = RequiredString(root, "access_token"),
            RefreshToken = GetString(root, "refresh_token"),
            IdToken = GetString(root, "id_token"),
            ExpiresAt = expiresIn is null ? now : now.AddSeconds(expiresIn.Value),
            Scopes = (GetString(root, "scope") ?? OpenAICodexSubscriptionOAuthDefaults.Scope)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        };
    }

    private async Task<OpenAICodexSubscriptionCredential> ReadTokenResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return CredentialFromTokenJson(document.RootElement, DateTimeOffset.UtcNow);
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return GetString(document.RootElement, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildFormUrlEncoded(IReadOnlyDictionary<string, string?> values)
        => string.Join(
            "&",
            values
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.Value))
                .Select(static entry => WebUtility.UrlEncode(entry.Key) + "=" + WebUtility.UrlEncode(entry.Value)));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string RequiredString(JsonElement element, string propertyName)
        => GetString(element, propertyName)
            ?? throw new InvalidOperationException($"OAuth response did not include '{propertyName}'.");

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;

    private static int? GetInt32OrString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null,
        };
    }
}

internal sealed record OpenAICodexSubscriptionPkce(string Verifier, string Challenge);

internal sealed record OpenAICodexSubscriptionDeviceCode(
    string DeviceAuthId,
    string UserCode,
    string VerificationUri,
    TimeSpan ExpiresIn,
    TimeSpan Interval);

internal sealed record OpenAICodexSubscriptionDeviceCodeRequest(
    [property: JsonPropertyName("client_id")] string ClientId);

internal sealed record OpenAICodexSubscriptionDeviceTokenRequest(
    [property: JsonPropertyName("device_auth_id")] string DeviceAuthId,
    [property: JsonPropertyName("user_code")] string UserCode);
