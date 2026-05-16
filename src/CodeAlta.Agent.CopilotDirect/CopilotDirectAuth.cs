using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using XenoAtom.Logging;

namespace CodeAlta.Agent.CopilotDirect;

internal sealed class CopilotDirectAuthManager
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.CopilotDirect");
    private readonly CopilotDirectProviderOptions _provider;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CopilotDirectCredentialStore _credentialStore;
    private CopilotDirectCredential? _credential;
    private bool _forceRefresh;

    public CopilotDirectAuthManager(CopilotDirectProviderOptions provider, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(httpClient);
        _provider = provider;
        _httpClient = httpClient;
        _credentialStore = new CopilotDirectCredentialStore(provider.StateRootPath, provider.ProviderKey);
    }

    public async ValueTask<CopilotDirectCredential> GetCredentialAsync(CancellationToken cancellationToken)
    {
        if (!_forceRefresh && _credential is { } current && !current.ShouldRefresh(_provider.Auth.TokenRefreshSkew))
        {
            return current;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_forceRefresh && _credential is { } refreshed && !refreshed.ShouldRefresh(_provider.Auth.TokenRefreshSkew))
            {
                return refreshed;
            }

            _credential = await ResolveCredentialCoreAsync(_forceRefresh, cancellationToken).ConfigureAwait(false);
            _forceRefresh = false;
            return _credential;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async ValueTask ForceRefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _credential = null;
            _forceRefresh = true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async ValueTask<CopilotDirectCredential> ResolveCredentialCoreAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var auth = _provider.Auth;
        switch (auth.AuthSource)
        {
            case CopilotDirectAuthSources.CopilotTokenEnvironment:
            {
                var token = ResolveEnvironmentSecret(auth.CopilotTokenEnvironmentVariable, "Copilot token environment variable");
                var baseUri = CopilotDirectBaseUriResolver.Resolve(_provider.BaseUri, token, auth.EnterpriseDomain, endpointsApi: null);
                return new CopilotDirectCredential(token, ExpiresAt: null, baseUri);
            }
            case CopilotDirectAuthSources.GitHubTokenEnvironment:
            {
                var githubToken = ResolveEnvironmentSecret(auth.GitHubTokenEnvironmentVariable, "GitHub token environment variable");
                return await ExchangeGitHubTokenAsync(githubToken, persistCache: false, cancellationToken).ConfigureAwait(false);
            }
            case CopilotDirectAuthSources.GitHubDeviceFlow:
                return await ResolveDeviceFlowCredentialAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"Unsupported Copilot direct auth source '{auth.AuthSource}'.");
        }
    }

    private async ValueTask<CopilotDirectCredential> ResolveDeviceFlowCredentialAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var cache = await _credentialStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!forceRefresh && cache?.ToCredential(_provider.Auth.TokenRefreshSkew) is { } cachedCredential)
        {
            return cachedCredential;
        }

        if (!string.IsNullOrWhiteSpace(cache?.GitHubToken))
        {
            try
            {
                return await ExchangeGitHubTokenAsync(cache.GitHubToken, persistCache: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Warn))
                {
                    Logger.Warn(ex, $"Cached GitHub Copilot token refresh failed; starting device flow provider={_provider.ProviderKey}");
                }
            }
        }

        var githubToken = await RunDeviceFlowAsync(cancellationToken).ConfigureAwait(false);
        await _credentialStore.WriteAsync(new CopilotDirectCredentialCache
        {
            GitHubToken = githubToken,
            EnterpriseDomain = _provider.Auth.EnterpriseDomain,
        }, cancellationToken).ConfigureAwait(false);
        return await ExchangeGitHubTokenAsync(githubToken, persistCache: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string> RunDeviceFlowAsync(CancellationToken cancellationToken)
    {
        var domain = NormalizeGitHubHost(_provider.Auth.EnterpriseDomain);
        var clientId = string.IsNullOrWhiteSpace(_provider.Auth.DeviceFlowClientId)
            ? "Iv1.b507a08c87ecfe98" // GitHub Copilot extension OAuth app id. Ideally we should use our own, but for now we do what others do.
            : _provider.Auth.DeviceFlowClientId.Trim();
        using var deviceRequest = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{domain}/login/device/code"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = "read:user",
            }),
        };
        deviceRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        CopilotDirectHeaders.ApplyStaticHeaders(deviceRequest.Headers);
        using var deviceResponse = await _httpClient.SendAsync(deviceRequest, cancellationToken).ConfigureAwait(false);
        var deviceJson = await deviceResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!deviceResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub device authorization failed with HTTP {(int)deviceResponse.StatusCode} {deviceResponse.ReasonPhrase}.");
        }

        var device = JsonSerializer.Deserialize(deviceJson, CopilotDirectJsonContext.Default.CopilotDeviceCodeResponse)
            ?? throw new InvalidOperationException("GitHub device authorization response was empty.");
        if (string.IsNullOrWhiteSpace(device.DeviceCode) || string.IsNullOrWhiteSpace(device.UserCode) || string.IsNullOrWhiteSpace(device.VerificationUri))
        {
            throw new InvalidOperationException("GitHub device authorization response was missing required fields.");
        }

        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Info))
        {
            Logger.Info($"Copilot login required. Open {device.VerificationUri} and enter code {device.UserCode}. Waiting for authorization; cancel the operation to stop polling.");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, device.ExpiresIn)).Subtract(TimeSpan.FromSeconds(3));
        var interval = TimeSpan.FromSeconds(Math.Max(5, device.Interval ?? 5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{domain}/login/oauth/access_token"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = device.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }),
            };
            tokenRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            CopilotDirectHeaders.ApplyStaticHeaders(tokenRequest.Headers);
            using var tokenResponse = await _httpClient.SendAsync(tokenRequest, cancellationToken).ConfigureAwait(false);
            var tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"GitHub device token polling failed with HTTP {(int)tokenResponse.StatusCode} {tokenResponse.ReasonPhrase}.");
            }

            var token = JsonSerializer.Deserialize(tokenJson, CopilotDirectJsonContext.Default.GitHubDeviceTokenResponse)
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
                interval = interval.Add(TimeSpan.FromSeconds(5));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(token.Error))
            {
                throw new InvalidOperationException($"GitHub device authorization failed: {token.ErrorDescription ?? token.Error}.");
            }
        }

        throw new TimeoutException("GitHub device authorization expired before login completed.");
    }

    private async ValueTask<CopilotDirectCredential> ExchangeGitHubTokenAsync(string githubToken, bool persistCache, CancellationToken cancellationToken)
    {
        var domain = NormalizeGitHubHost(_provider.Auth.EnterpriseDomain);
        var apiDomain = string.Equals(domain, "github.com", StringComparison.OrdinalIgnoreCase)
            ? "api.github.com"
            : $"api.{domain}";
        var tokenUri = new Uri($"https://{apiDomain}/copilot_internal/v2/token");
        using var request = new HttpRequestMessage(HttpMethod.Get, tokenUri);
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
        var baseUri = CopilotDirectBaseUriResolver.Resolve(_provider.BaseUri, token.Token, _provider.Auth.EnterpriseDomain, token.Endpoints?.Api);
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Info))
        {
            Logger.Info($"Resolved GitHub Copilot direct credential provider={_provider.ProviderKey} baseUri={baseUri.ToString()}");
        }

        if (persistCache)
        {
            await _credentialStore.WriteAsync(new CopilotDirectCredentialCache
            {
                GitHubToken = githubToken,
                CopilotToken = token.Token,
                CopilotTokenExpiresAtUnixSeconds = token.ExpiresAt,
                EnterpriseDomain = _provider.Auth.EnterpriseDomain,
                CopilotApiBaseUri = baseUri.ToString(),
            }, cancellationToken).ConfigureAwait(false);
        }

        return new CopilotDirectCredential(token.Token, expiresAt, baseUri);
    }

    private static string ResolveEnvironmentSecret(string? variableName, string label)
    {
        if (string.IsNullOrWhiteSpace(variableName))
        {
            throw new InvalidOperationException($"{label} is required.");
        }

        var value = Environment.GetEnvironmentVariable(variableName.Trim());
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{label} '{variableName}' is not set.");
        }

        return value.Trim();
    }

    private static string NormalizeGitHubHost(string? enterpriseDomain)
    {
        if (string.IsNullOrWhiteSpace(enterpriseDomain))
        {
            return "github.com";
        }

        var value = enterpriseDomain.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return value.Trim('/');
    }
}

internal sealed record CopilotDirectCredential(string Token, DateTimeOffset? ExpiresAt, Uri BaseUri)
{
    public bool ShouldRefresh(TimeSpan skew)
        => ExpiresAt is { } expiresAt && DateTimeOffset.UtcNow >= expiresAt.Subtract(skew);
}

internal sealed class CopilotDirectCredentialStore
{
    private readonly string _path;

    public CopilotDirectCredentialStore(string? stateRootPath, string providerKey)
    {
        var root = string.IsNullOrWhiteSpace(stateRootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeAlta")
            : stateRootPath.Trim();
        _path = Path.Combine(root, "auth", CopilotDirectAgentBackend.ProtocolFamily, SanitizeFileName(providerKey) + ".json");
    }

    public async ValueTask<CopilotDirectCredentialCache?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync(stream, CopilotDirectJsonContext.Default.CopilotDirectCredentialCache, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (LogManager.IsInitialized && LogManager.GetLogger("CodeAlta.Agent.CopilotDirect").IsEnabled(LogLevel.Warn))
            {
                LogManager.GetLogger("CodeAlta.Agent.CopilotDirect").Warn(ex, "Unable to read GitHub Copilot direct credential cache.");
            }

            return null;
        }
    }

    public async ValueTask WriteAsync(CopilotDirectCredentialCache cache, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, cache, CopilotDirectJsonContext.Default.CopilotDirectCredentialCache, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        File.Move(tempPath, _path);
        TryRestrictFile(_path);
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static void TryRestrictFile(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }
}

internal sealed class CopilotDirectCredentialCache
{
    [JsonPropertyName("github_token")]
    public string? GitHubToken { get; set; }

    [JsonPropertyName("copilot_token")]
    public string? CopilotToken { get; set; }

    [JsonPropertyName("copilot_token_expires_at")]
    public long? CopilotTokenExpiresAtUnixSeconds { get; set; }

    [JsonPropertyName("enterprise_domain")]
    public string? EnterpriseDomain { get; set; }

    [JsonPropertyName("copilot_api_base_uri")]
    public string? CopilotApiBaseUri { get; set; }

    public CopilotDirectCredential? ToCredential(TimeSpan skew)
    {
        if (string.IsNullOrWhiteSpace(CopilotToken) || !Uri.TryCreate(CopilotApiBaseUri, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var expiresAt = CopilotTokenExpiresAtUnixSeconds is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(CopilotTokenExpiresAtUnixSeconds.Value)
            : (DateTimeOffset?)null;
        var credential = new CopilotDirectCredential(CopilotToken, expiresAt, baseUri);
        return credential.ShouldRefresh(skew) ? null : credential;
    }
}

internal static partial class CopilotDirectBaseUriResolver
{
    public static Uri Resolve(Uri? configuredBaseUri, string? token, string? enterpriseDomain, string? endpointsApi)
    {
        if (configuredBaseUri is not null)
        {
            return configuredBaseUri;
        }

        if (Uri.TryCreate(endpointsApi, UriKind.Absolute, out var endpointsApiUri))
        {
            return endpointsApiUri;
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            var fromToken = TryResolveFromProxyEndpoint(token);
            if (fromToken is not null)
            {
                return fromToken;
            }
        }

        if (!string.IsNullOrWhiteSpace(enterpriseDomain))
        {
            var domain = enterpriseDomain.Trim();
            if (Uri.TryCreate(domain, UriKind.Absolute, out var enterpriseUri))
            {
                domain = enterpriseUri.Host;
            }

            return new Uri($"https://copilot-api.{domain.Trim('/')}");
        }

        return new Uri("https://api.individual.githubcopilot.com");
    }

    private static Uri? TryResolveFromProxyEndpoint(string token)
    {
        const string marker = "proxy-ep=";
        var index = token.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var end = token.IndexOf(';', start);
        var proxyHost = (end < 0 ? token[start..] : token[start..end]).Trim();
        if (string.IsNullOrWhiteSpace(proxyHost))
        {
            return null;
        }

        var apiHost = proxyHost.StartsWith("proxy.", StringComparison.OrdinalIgnoreCase)
            ? "api." + proxyHost["proxy.".Length..]
            : proxyHost;
        return Uri.TryCreate($"https://{apiHost}", UriKind.Absolute, out var uri) ? uri : null;
    }
}

internal static class CopilotDirectAuthUtilities
{
    public static string NormalizeGitHubHost(string? enterpriseDomain)
    {
        if (string.IsNullOrWhiteSpace(enterpriseDomain))
        {
            return "github.com";
        }

        var value = enterpriseDomain.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return value.Trim('/');
    }
}

internal sealed class CopilotDeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string? DeviceCode { get; set; }

    [JsonPropertyName("user_code")]
    public string? UserCode { get; set; }

    [JsonPropertyName("verification_uri")]
    public string? VerificationUri { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int? Interval { get; set; }
}

internal sealed class GitHubDeviceTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }

    [JsonPropertyName("interval")]
    public int? Interval { get; set; }
}

internal sealed class CopilotTokenResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("expires_at")]
    public long? ExpiresAt { get; set; }

    [JsonPropertyName("refresh_in")]
    public long? RefreshIn { get; set; }

    [JsonPropertyName("endpoints")]
    public CopilotTokenEndpoints? Endpoints { get; set; }
}

internal sealed class CopilotTokenEndpoints
{
    [JsonPropertyName("api")]
    public string? Api { get; set; }
}
