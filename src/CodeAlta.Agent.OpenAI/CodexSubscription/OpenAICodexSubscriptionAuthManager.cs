using System.Text.Json;

namespace CodeAlta.Agent.OpenAI.CodexSubscription;

internal sealed class OpenAICodexSubscriptionAuthManager
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);
    private readonly IOpenAICodexSubscriptionCredentialStore _credentialStore;
    private readonly OpenAICodexSubscriptionOAuthClient _oauthClient;
    private readonly string _providerKey;
    private readonly string _authSource;
    private readonly string? _configuredAccountId;
    private readonly string? _codexHome;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private OpenAICodexSubscriptionCredential? _cachedCredential;

    public OpenAICodexSubscriptionAuthManager(
        IOpenAICodexSubscriptionCredentialStore credentialStore,
        OpenAICodexSubscriptionOAuthClient oauthClient,
        string providerKey,
        string authSource = "codealta_oauth",
        string? configuredAccountId = null,
        string? codexHome = null)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(oauthClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        _credentialStore = credentialStore;
        _oauthClient = oauthClient;
        _providerKey = providerKey;
        _authSource = authSource;
        _configuredAccountId = configuredAccountId;
        _codexHome = codexHome;
    }

    public async ValueTask<OpenAICodexSubscriptionCredential> GetCredentialAsync(
        CancellationToken cancellationToken = default)
    {
        var credential = await LoadCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (credential.ExpiresAt > DateTimeOffset.UtcNow + RefreshSkew)
        {
            return credential;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            credential = await LoadCredentialAsync(cancellationToken, forceReload: true).ConfigureAwait(false);
            if (credential.ExpiresAt > DateTimeOffset.UtcNow + RefreshSkew)
            {
                return credential;
            }

            var refreshed = await RefreshAsync(credential, cancellationToken).ConfigureAwait(false);
            _cachedCredential = refreshed;
            await _credentialStore.SaveAsync(_providerKey, refreshed, cancellationToken).ConfigureAwait(false);
            return refreshed;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        => (await GetCredentialAsync(cancellationToken).ConfigureAwait(false)).AccessToken;

    public async ValueTask ForceRefreshCredentialAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var credential = await LoadCredentialAsync(cancellationToken, forceReload: true).ConfigureAwait(false);
            var refreshed = await RefreshAsync(credential, cancellationToken).ConfigureAwait(false);
            _cachedCredential = refreshed;
            await _credentialStore.SaveAsync(_providerKey, refreshed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async ValueTask<OpenAICodexSubscriptionAccountContext> GetAccountContextAsync(
        CancellationToken cancellationToken = default)
    {
        var credential = await GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        var accountId = ResolveAccountId(_configuredAccountId, credential);
        return new OpenAICodexSubscriptionAccountContext(
            accountId,
            credential.AccountLabel,
            credential.IsFedRamp);
    }

    internal static string? ResolveAccountId(
        string? configuredAccountId,
        OpenAICodexSubscriptionCredential credential)
    {
        if (!string.IsNullOrWhiteSpace(configuredAccountId))
        {
            return configuredAccountId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(credential.AccountId))
        {
            return credential.AccountId.Trim();
        }

        return TryExtractAccountIdFromJwt(credential.AccessToken)
            ?? TryExtractAccountIdFromJwt(credential.IdToken);
    }

    internal static string? TryExtractAccountIdFromJwt(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadBytes = Base64UrlDecode(parts[1]);
            using var document = JsonDocument.Parse(payloadBytes);
            if (!document.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth) ||
                auth.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var propertyName in new[] { "chatgpt_account_id", "account_id", "workspace_id", "organization_id" })
            {
                if (auth.TryGetProperty(propertyName, out var property) &&
                    property.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.GetString()))
                {
                    return property.GetString()!.Trim();
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (FormatException)
        {
        }

        return null;
    }

    private async ValueTask<OpenAICodexSubscriptionCredential> LoadCredentialAsync(
        CancellationToken cancellationToken,
        bool forceReload = false)
    {
        if (!forceReload && _cachedCredential is not null)
        {
            return _cachedCredential;
        }

        var credential = _authSource switch
        {
            "codex_auth_file_readonly" => string.IsNullOrWhiteSpace(_codexHome)
                ? null
                : await CodexAuthFileReader.ReadAuthJsonAsync(_codexHome, cancellationToken).ConfigureAwait(false),
            "codex_auth_import" => string.IsNullOrWhiteSpace(_codexHome)
                ? await _credentialStore.LoadAsync(_providerKey, cancellationToken).ConfigureAwait(false)
                : await CodexAuthFileReader.ImportAuthJsonAsync(_codexHome, _credentialStore, _providerKey, cancellationToken).ConfigureAwait(false),
            _ => await _credentialStore.LoadAsync(_providerKey, cancellationToken).ConfigureAwait(false),
        };

        _cachedCredential = credential ?? throw new InvalidOperationException("ChatGPT login is required for the Codex subscription provider.");
        return _cachedCredential;
    }

    private async Task<OpenAICodexSubscriptionCredential> RefreshAsync(
        OpenAICodexSubscriptionCredential credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            throw new InvalidOperationException("ChatGPT refresh token is missing; re-authentication is required.");
        }

        try
        {
            var refreshed = await _oauthClient.RefreshAsync(credential.RefreshToken, cancellationToken).ConfigureAwait(false);
            refreshed.AccountId ??= credential.AccountId;
            refreshed.AccountLabel ??= credential.AccountLabel;
            refreshed.IsFedRamp = credential.IsFedRamp;
            return refreshed;
        }
        catch (HttpRequestException ex)
        {
            await _credentialStore.DeleteAsync(_providerKey, cancellationToken).ConfigureAwait(false);
            _cachedCredential = null;
            throw new InvalidOperationException(
                OpenAICodexSubscriptionSecretRedactor.Redact("ChatGPT token refresh failed; re-authentication is required. " + ex.Message, credential),
                ex);
        }
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}

internal sealed record OpenAICodexSubscriptionAccountContext(
    string? AccountId,
    string? AccountLabel,
    bool IsFedRamp);
