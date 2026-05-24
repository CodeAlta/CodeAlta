using System.Text.Json;
using System.Text.Json.Serialization;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Xai;

internal sealed class XaiDirectAuthManager
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.Xai");
    private readonly XaiProviderOptions _provider;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly XaiDirectCredentialStore _credentialStore;
    private readonly XaiOAuthClient _oauthClient;
    private XaiDirectCredential? _credential;

    public XaiDirectAuthManager(XaiProviderOptions provider, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(httpClient);
        _provider = provider;
        _httpClient = httpClient;
        _credentialStore = new XaiDirectCredentialStore(provider.StateRootPath, provider.ProviderKey);
        _oauthClient = new XaiOAuthClient(httpClient);
    }

    public async ValueTask<XaiDirectCredential> GetCredentialAsync(CancellationToken cancellationToken)
    {
        if (_credential is { } current && !current.ShouldRefresh(_provider.Auth.TokenRefreshSkew))
        {
            return current;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_credential is { } refreshed && !refreshed.ShouldRefresh(_provider.Auth.TokenRefreshSkew))
            {
                return refreshed;
            }

            _credential = await ResolveCredentialCoreAsync(cancellationToken).ConfigureAwait(false);
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
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async ValueTask<XaiDirectCredential> ResolveCredentialCoreAsync(CancellationToken cancellationToken)
    {
        var auth = _provider.Auth;
        return auth.AuthSource switch
        {
            XaiAuthSources.XaiBrowserOAuth or XaiAuthSources.XaiDeviceFlow
                => await LoadOrRefreshCachedCredentialAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported xAI direct auth source '{auth.AuthSource}'."),
        };
    }

    private async ValueTask<XaiDirectCredential> LoadOrRefreshCachedCredentialAsync(CancellationToken cancellationToken)
    {
        var cache = await _credentialStore.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"xAI login is required before this provider can be used (provider={_provider.ProviderKey}).");
        var baseUri = _provider.BaseUri ?? XaiDefaults.DefaultApiBaseUri;
        var cached = cache.ToCredential(baseUri, _provider.Auth.TokenRefreshSkew);
        if (cached is not null)
        {
            return cached;
        }

        if (string.IsNullOrWhiteSpace(cache.RefreshToken))
        {
            throw new InvalidOperationException(
                $"xAI cached access token expired and no refresh token is available (provider={_provider.ProviderKey}). Re-run login.");
        }

        try
        {
            var refreshed = await _oauthClient.RefreshAsync(cache.RefreshToken, cancellationToken).ConfigureAwait(false);
            var newCache = new XaiDirectCredentialCache
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = refreshed.RefreshToken ?? cache.RefreshToken,
                ExpiresAtUnixSeconds = refreshed.ExpiresAtUnixSeconds,
                Scope = refreshed.Scope ?? cache.Scope,
            };
            await _credentialStore.WriteAsync(newCache, cancellationToken).ConfigureAwait(false);
            Logger.Info($"Refreshed xAI direct credential provider={_provider.ProviderKey}");
            return newCache.ToCredential(baseUri, _provider.Auth.TokenRefreshSkew)
                ?? new XaiDirectCredential(refreshed.AccessToken, null, baseUri, refreshed.RefreshToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            await _credentialStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"xAI token refresh failed (provider={_provider.ProviderKey}); re-authentication is required.",
                ex);
        }
    }

}

internal sealed record XaiDirectCredential(string Token, DateTimeOffset? ExpiresAt, Uri BaseUri, string? RefreshToken)
{
    public bool ShouldRefresh(TimeSpan skew)
        => ExpiresAt is { } expiresAt && DateTimeOffset.UtcNow >= expiresAt.Subtract(skew);
}

internal sealed class XaiDirectCredentialStore
{
    private readonly string _path;

    public XaiDirectCredentialStore(string? stateRootPath, string providerKey)
    {
        var root = string.IsNullOrWhiteSpace(stateRootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeAlta")
            : stateRootPath.Trim();
        _path = Path.Combine(root, "auth", XaiDirectAgentBackend.ProtocolFamily, SanitizeFileName(providerKey) + ".json");
    }

    public async ValueTask<XaiDirectCredentialCache?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync(stream, XaiDirectJsonContext.Default.XaiDirectCredentialCache, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogManager.GetLogger("CodeAlta.Agent.Xai").Warn(ex, "Unable to read xAI direct credential cache.");
            return null;
        }
    }

    public async ValueTask WriteAsync(XaiDirectCredentialCache cache, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, cache, XaiDirectJsonContext.Default.XaiDirectCredentialCache, cancellationToken).ConfigureAwait(false);
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

internal sealed class XaiDirectCredentialCache
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_at")]
    public long? ExpiresAtUnixSeconds { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    public XaiDirectCredential? ToCredential(Uri baseUri, TimeSpan skew)
    {
        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            return null;
        }

        var expiresAt = ExpiresAtUnixSeconds is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnixSeconds.Value)
            : (DateTimeOffset?)null;
        var credential = new XaiDirectCredential(AccessToken, expiresAt, baseUri, RefreshToken);
        return credential.ShouldRefresh(skew) ? null : credential;
    }
}
