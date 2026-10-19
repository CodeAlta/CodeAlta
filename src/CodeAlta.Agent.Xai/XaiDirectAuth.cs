using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent.Runtime;
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
    private bool _forceRefresh;

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

    private async ValueTask<XaiDirectCredential> ResolveCredentialCoreAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var auth = _provider.Auth;
        return auth.AuthSource switch
        {
            XaiAuthSources.XaiBrowserOAuth or XaiAuthSources.XaiDeviceFlow
                => await LoadOrRefreshCachedCredentialAsync(forceRefresh, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported xAI direct auth source '{auth.AuthSource}'."),
        };
    }

    private async ValueTask<XaiDirectCredential> LoadOrRefreshCachedCredentialAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var cache = await _credentialStore.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"xAI login is required before this provider can be used (provider={_provider.ProviderKey}).");
        var baseUri = _provider.BaseUri ?? XaiDefaults.DefaultApiBaseUri;
        var cached = cache.ToCredential(baseUri, _provider.Auth.TokenRefreshSkew);
        if (!forceRefresh && cached is not null)
        {
            return cached;
        }

        await using var refreshLock = await _credentialStore.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        // Another process, or the login, may have refreshed the credential meanwhile: it is read again under the lock,
        // and a token other than the one this refresh was for is used as it is.
        var current = await _credentialStore.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"xAI login is required before this provider can be used (provider={_provider.ProviderKey}).");
        if (current.ToCredential(baseUri, _provider.Auth.TokenRefreshSkew) is { } currentCredential &&
            (!forceRefresh || !string.Equals(current.AccessToken, cache.AccessToken, StringComparison.Ordinal)))
        {
            return currentCredential;
        }

        cache = current;
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
        _path = Path.Combine(root, "auth", XaiDirectModelProviderRuntime.ProtocolFamily, SanitizeFileName(providerKey) + ".json");
    }

    public async ValueTask<XaiDirectCredentialCache?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            // A writer may replace the file while it is read: the reader keeps the content it opened.
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, useAsync: true);
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
        // A temporary file of this write alone, moved onto the cache in one step: a reader finds the previous cache or
        // the new one, never none, and writers that run together do not write the same temporary file.
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(stream, cache, XaiDirectJsonContext.Default.XaiDirectCredentialCache, cancellationToken).ConfigureAwait(false);
            }

            var cleared = ClearRestriction(_path);
            try
            {
                await StagedFileReplacement.MoveOverAsync(temporaryPath, _path).ConfigureAwait(false);
            }
            catch
            {
                // The cache that stays is the previous one: it keeps the attributes it had.
                RestoreRestriction(_path, cleared);
                throw;
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        TryRestrictFile(_path);
    }

    // Serializes the refreshes of the credential across the processes and the registrations that share it: a refresh
    // token is used once, and a refresh with one another used already would fail and sign the user out.
    public async ValueTask<IAsyncDisposable> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var path = _path + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            // Held by another owner: a sharing (32) or lock (33) violation on Windows, EWOULDBLOCK on Linux (11) and macOS (35).
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33 or 35)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
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

    // The cache is hidden once written: a replacement of a hidden or read-only file may be refused, the deletion that
    // came before it was not. Returns the attributes it cleared.
    private static FileAttributes ClearRestriction(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var restriction = attributes & (FileAttributes.Hidden | FileAttributes.ReadOnly);
            if (restriction != 0)
            {
                File.SetAttributes(path, attributes & ~restriction);
            }

            return restriction;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void RestoreRestriction(string path, FileAttributes restriction)
    {
        if (restriction == 0)
        {
            return;
        }

        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | restriction);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
