using System.Net.Http.Headers;
using System.Text.Json;

namespace CodeAlta.Agent.Copilot;

/// <summary>
/// Provides GitHub OAuth device-code login helpers for the GitHub Copilot direct provider.
/// </summary>
public sealed class CopilotDirectLoginManager
{
    private const string DefaultDeviceFlowClientId = "Iv1.b507a08c87ecfe98";
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="CopilotDirectLoginManager"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use. When <see langword="null"/>, a new client is created.</param>
    public CopilotDirectLoginManager(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Completes a browser-assisted GitHub device-code login and stores the exchanged Copilot credential.
    /// </summary>
    /// <param name="options">Login options.</param>
    /// <param name="onDeviceCode">Callback invoked when the verification URL and user code are available.</param>
    /// <param name="cancellationToken">A token to cancel the login.</param>
    /// <returns>The non-secret login result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> or <paramref name="onDeviceCode"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when GitHub authorization or Copilot token exchange fails.</exception>
    /// <exception cref="TimeoutException">Thrown when the device authorization expires before completion.</exception>
    public async ValueTask<CopilotDirectLoginResult> LoginWithDeviceCodeAsync(
        CopilotDirectLoginOptions options,
        Func<CopilotDirectDeviceCode, CancellationToken, ValueTask> onDeviceCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onDeviceCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderKey);

        var domain = CopilotDirectAuthUtilities.NormalizeGitHubHost(options.EnterpriseDomain);
        var clientId = string.IsNullOrWhiteSpace(options.DeviceFlowClientId)
            ? DefaultDeviceFlowClientId
            : options.DeviceFlowClientId.Trim();
        var device = await StartDeviceFlowAsync(domain, clientId, cancellationToken).ConfigureAwait(false);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, device.ExpiresIn));
        await onDeviceCode(
                new CopilotDirectDeviceCode(
                    new Uri(device.VerificationUri!),
                    device.UserCode!,
                    expiresAt),
                cancellationToken)
            .ConfigureAwait(false);

        var githubToken = await PollForGitHubTokenAsync(domain, clientId, device, options.PollingIntervalOverride, cancellationToken).ConfigureAwait(false);
        var exchange = await ExchangeGitHubTokenAsync(domain, githubToken, options, cancellationToken).ConfigureAwait(false);
        var store = new CopilotDirectCredentialStore(options.StateRootPath, options.ProviderKey);
        await store.WriteAsync(new CopilotDirectCredentialCache
        {
            GitHubToken = githubToken,
            CopilotToken = exchange.Token,
            CopilotTokenExpiresAtUnixSeconds = exchange.ExpiresAtUnixSeconds,
            EnterpriseDomain = options.EnterpriseDomain,
            CopilotApiBaseUri = exchange.BaseUri.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        return new CopilotDirectLoginResult(exchange.BaseUri, exchange.ExpiresAt, options.EnterpriseDomain);
    }

    /// <summary>
    /// Deletes CodeAlta-owned cached GitHub Copilot direct credentials for a provider.
    /// </summary>
    /// <param name="options">Login options identifying the provider and state root.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when deletion is attempted.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public ValueTask DeleteCredentialAsync(CopilotDirectLoginOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderKey);
        return new CopilotDirectCredentialStore(options.StateRootPath, options.ProviderKey).DeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Reads cached GitHub Copilot direct credential status without returning secret tokens.
    /// </summary>
    /// <param name="options">Login options identifying the provider and state root.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The cached credential status, or <see langword="null"/> when no usable credential is cached.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public async ValueTask<CopilotDirectLoginResult?> GetCredentialStatusAsync(
        CopilotDirectLoginOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderKey);
        var cache = await new CopilotDirectCredentialStore(options.StateRootPath, options.ProviderKey).ReadAsync(cancellationToken).ConfigureAwait(false);
        var credential = cache?.ToCredential(TimeSpan.FromMinutes(5));
        return credential is null
            ? null
            : new CopilotDirectLoginResult(credential.BaseUri, credential.ExpiresAt, cache?.EnterpriseDomain);
    }

    private async ValueTask<CopilotDeviceCodeResponse> StartDeviceFlowAsync(string domain, string clientId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{domain}/login/device/code"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = "read:user",
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        CopilotDirectHeaders.ApplyStaticHeaders(request.Headers);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub device authorization failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var device = JsonSerializer.Deserialize(json, CopilotDirectJsonContext.Default.CopilotDeviceCodeResponse)
            ?? throw new InvalidOperationException("GitHub device authorization response was empty.");
        if (string.IsNullOrWhiteSpace(device.DeviceCode) || string.IsNullOrWhiteSpace(device.UserCode) || string.IsNullOrWhiteSpace(device.VerificationUri))
        {
            throw new InvalidOperationException("GitHub device authorization response was missing required fields.");
        }

        return device;
    }

    private async ValueTask<string> PollForGitHubTokenAsync(
        string domain,
        string clientId,
        CopilotDeviceCodeResponse device,
        TimeSpan? pollingIntervalOverride,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, device.ExpiresIn)).Subtract(TimeSpan.FromSeconds(3));
        var interval = pollingIntervalOverride ?? TimeSpan.FromSeconds(Math.Max(5, device.Interval ?? 5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{domain}/login/oauth/access_token"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = device.DeviceCode!,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            CopilotDirectHeaders.ApplyStaticHeaders(request.Headers);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"GitHub device token polling failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var token = JsonSerializer.Deserialize(json, CopilotDirectJsonContext.Default.GitHubDeviceTokenResponse)
                ?? throw new InvalidOperationException("GitHub device token response was empty.");
            if (!string.IsNullOrWhiteSpace(token.AccessToken))
            {
                return token.AccessToken.Trim();
            }

            if (string.Equals(token.Error, "authorization_pending", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(token.Error, "slow_down", StringComparison.OrdinalIgnoreCase))
            {
                interval = pollingIntervalOverride ?? (token.Interval is > 0 ? TimeSpan.FromSeconds(token.Interval.Value) : interval.Add(TimeSpan.FromSeconds(5)));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(token.Error))
            {
                throw new InvalidOperationException($"GitHub device authorization failed: {token.ErrorDescription ?? token.Error}.");
            }
        }

        throw new TimeoutException("GitHub device authorization expired before login completed.");
    }

    private async ValueTask<CopilotDirectTokenExchangeResult> ExchangeGitHubTokenAsync(
        string domain,
        string githubToken,
        CopilotDirectLoginOptions options,
        CancellationToken cancellationToken)
    {
        var apiDomain = string.Equals(domain, "github.com", StringComparison.OrdinalIgnoreCase)
            ? "api.github.com"
            : $"api.{domain}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{apiDomain}/copilot_internal/v2/token"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        CopilotDirectHeaders.ApplyStaticHeaders(request.Headers);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub Copilot token exchange failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var token = JsonSerializer.Deserialize(content, CopilotDirectJsonContext.Default.CopilotTokenResponse)
            ?? throw new InvalidOperationException("GitHub Copilot token response was empty.");
        if (string.IsNullOrWhiteSpace(token.Token))
        {
            throw new InvalidOperationException("GitHub Copilot token response did not contain a token.");
        }

        var expiresAt = token.ExpiresAt is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(token.ExpiresAt.Value)
            : (DateTimeOffset?)null;
        var baseUri = CopilotDirectBaseUriResolver.Resolve(options.BaseUri, token.Token, options.EnterpriseDomain, token.Endpoints?.Api);
        return new CopilotDirectTokenExchangeResult(token.Token, token.ExpiresAt, expiresAt, baseUri);
    }

    private sealed record CopilotDirectTokenExchangeResult(string Token, long? ExpiresAtUnixSeconds, DateTimeOffset? ExpiresAt, Uri BaseUri);
}

/// <summary>
/// Options for a GitHub Copilot direct login operation.
/// </summary>
/// <param name="ProviderKey">The provider key whose credential cache should be updated.</param>
/// <param name="StateRootPath">The CodeAlta state root path.</param>
/// <param name="EnterpriseDomain">An optional GitHub Enterprise URL or domain.</param>
/// <param name="BaseUri">An optional explicit Copilot API base URI override.</param>
/// <param name="DeviceFlowClientId">An optional GitHub OAuth device-flow client id override.</param>
public sealed record CopilotDirectLoginOptions(
    string ProviderKey,
    string? StateRootPath = null,
    string? EnterpriseDomain = null,
    Uri? BaseUri = null,
    string? DeviceFlowClientId = null)
{
    /// <summary>
    /// Gets an optional polling interval override for tests and controlled hosts.
    /// </summary>
    public TimeSpan? PollingIntervalOverride { get; init; }
}

/// <summary>
/// Non-secret GitHub device authorization data surfaced to the UI.
/// </summary>
/// <param name="VerificationUri">The GitHub URL the user should open.</param>
/// <param name="UserCode">The code the user should enter in the browser.</param>
/// <param name="ExpiresAt">The authorization expiry time.</param>
public sealed record CopilotDirectDeviceCode(Uri VerificationUri, string UserCode, DateTimeOffset ExpiresAt);

/// <summary>
/// Non-secret GitHub Copilot direct login result metadata.
/// </summary>
/// <param name="BaseUri">The resolved Copilot API base URI.</param>
/// <param name="ExpiresAt">The Copilot token expiry time when provided by GitHub.</param>
/// <param name="EnterpriseDomain">The GitHub Enterprise domain when configured.</param>
public sealed record CopilotDirectLoginResult(Uri BaseUri, DateTimeOffset? ExpiresAt, string? EnterpriseDomain);
