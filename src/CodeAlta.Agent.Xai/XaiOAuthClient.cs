using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Xai;

internal sealed class XaiOAuthClient
{
    private readonly HttpClient _httpClient;

    public XaiOAuthClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public static XaiPkce CreatePkce()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var verifier = Base64UrlEncode(bytes);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new XaiPkce(verifier, challenge);
    }

    public static string CreateState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public static Uri BuildAuthorizeUri(XaiPkce pkce, string state, string nonce)
    {
        ArgumentNullException.ThrowIfNull(pkce);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = XaiDefaults.OAuthClientId,
            ["redirect_uri"] = XaiDefaults.LoopbackRedirectUri,
            ["scope"] = XaiDefaults.Scope,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["nonce"] = nonce,
            ["plan"] = XaiDefaults.PlanParameter,
            ["referrer"] = XaiDefaults.Referrer,
        };
        return new Uri(XaiDefaults.AuthorizeEndpoint + "?" + BuildFormUrlEncoded(query));
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

    public async Task<XaiTokenResponse> ExchangeAuthorizationCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = XaiDefaults.OAuthClientId,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["redirect_uri"] = XaiDefaults.LoopbackRedirectUri,
            });
        return await PostTokenAsync(content, "authorization code exchange", cancellationToken).ConfigureAwait(false);
    }

    public async Task<XaiTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = XaiDefaults.OAuthClientId,
                ["refresh_token"] = refreshToken,
            });
        return await PostTokenAsync(content, "token refresh", cancellationToken).ConfigureAwait(false);
    }

    public async Task<XaiDeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, XaiDefaults.DeviceAuthorizationEndpoint)
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["client_id"] = XaiDefaults.OAuthClientId,
                    ["scope"] = XaiDefaults.Scope,
                }),
        };
        XaiDirectHeaders.ApplyStaticHeaders(request.Headers);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"xAI device code request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var deviceCode = RequiredString(root, "device_code");
        var userCode = RequiredString(root, "user_code");
        var verificationUri = RequiredString(root, "verification_uri");
        var verificationUriComplete = GetString(root, "verification_uri_complete");
        var expiresIn = TimeSpan.FromSeconds(GetInt32(root, "expires_in") ?? 300);
        var interval = TimeSpan.FromSeconds(Math.Max(1, GetInt32(root, "interval") ?? 5));
        return new XaiDeviceCodeResponse(deviceCode, userCode, verificationUri, verificationUriComplete, expiresIn, interval);
    }

    public async Task<XaiTokenResponse> PollDeviceTokenAsync(
        XaiDeviceCodeResponse deviceCode,
        TimeSpan? pollingIntervalOverride = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceCode);
        timeProvider ??= TimeProvider.System;
        var deadline = timeProvider.GetUtcNow() + deviceCode.ExpiresIn;
        var interval = pollingIntervalOverride ?? deviceCode.Interval;
        while (timeProvider.GetUtcNow() < deadline)
        {
            await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
            using var content = new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = XaiDefaults.DeviceCodeGrantType,
                    ["client_id"] = XaiDefaults.OAuthClientId,
                    ["device_code"] = deviceCode.DeviceCode,
                });
            using var request = new HttpRequestMessage(HttpMethod.Post, XaiDefaults.TokenEndpoint)
            {
                Content = content,
            };
            XaiDirectHeaders.ApplyStaticHeaders(request.Headers);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ParseTokenResponse(body);
            }

            var error = TryReadError(body);
            if (string.Equals(error, "authorization_pending", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(error, "slow_down", StringComparison.Ordinal))
            {
                interval = interval.Add(TimeSpan.FromSeconds(5));
                continue;
            }

            throw new InvalidOperationException($"xAI device authorization failed: {error ?? response.ReasonPhrase ?? response.StatusCode.ToString()}.");
        }

        throw new TimeoutException("xAI device authorization expired before login completed.");
    }

    private async Task<XaiTokenResponse> PostTokenAsync(
        FormUrlEncodedContent content,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, XaiDefaults.TokenEndpoint)
        {
            Content = content,
        };
        XaiDirectHeaders.ApplyStaticHeaders(request.Headers);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"xAI {operation} failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return ParseTokenResponse(body);
    }

    private static XaiTokenResponse ParseTokenResponse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accessToken = RequiredString(root, "access_token");
        var refreshToken = GetString(root, "refresh_token");
        var idToken = GetString(root, "id_token");
        var scope = GetString(root, "scope");
        var expiresIn = GetInt32(root, "expires_in");
        var expiresAtUnixSeconds = expiresIn is > 0
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value).ToUnixTimeSeconds()
            : (long?)null;
        var expiresAt = expiresAtUnixSeconds is { } unix
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : (DateTimeOffset?)null;
        return new XaiTokenResponse(accessToken, refreshToken, idToken, scope, expiresAtUnixSeconds, expiresAt);
    }

    private static string? TryReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return GetString(document.RootElement, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(
                static parts => Uri.UnescapeDataString(parts[0]),
                static parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')),
                StringComparer.Ordinal);

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
            ?? throw new InvalidOperationException($"xAI OAuth response did not include '{propertyName}'.");

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
}

internal sealed record XaiPkce(string Verifier, string Challenge);

internal sealed record XaiDeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    TimeSpan ExpiresIn,
    TimeSpan Interval);

internal sealed record XaiTokenResponse(
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    string? Scope,
    long? ExpiresAtUnixSeconds,
    DateTimeOffset? ExpiresAt);
