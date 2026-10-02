namespace CodeAlta.Agent.OpenAI.Codex;

internal sealed class OpenAICodexSubscriptionAuthManager
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);
    private readonly IOpenAICodexSubscriptionCredentialStore _credentialStore;
    private readonly OpenAICodexSubscriptionOAuthClient _oauthClient;
    private readonly string _providerKey;
    private readonly string? _configuredAccountId;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public OpenAICodexSubscriptionAuthManager(
        IOpenAICodexSubscriptionCredentialStore credentialStore,
        OpenAICodexSubscriptionOAuthClient oauthClient,
        string providerKey,
        string authSource = "codealta_oauth",
        string? configuredAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(oauthClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        _credentialStore = credentialStore;
        _oauthClient = oauthClient;
        _providerKey = providerKey;
        _configuredAccountId = configuredAccountId;
        if (authSource != "codealta_oauth")
        {
            throw new InvalidOperationException("Codex subscription credentials cannot be imported. Use Continue with ChatGPT in CodeAlta.");
        }
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
            await using var lease = await _credentialStore.AcquireLockAsync(_providerKey, cancellationToken).ConfigureAwait(false);
            credential = await LoadCredentialAsync(cancellationToken).ConfigureAwait(false);
            if (credential.ExpiresAt > DateTimeOffset.UtcNow + RefreshSkew)
            {
                return credential;
            }

            var refreshed = await RefreshAsync(credential, cancellationToken).ConfigureAwait(false);
            await _credentialStore.SaveAsync(_providerKey, refreshed, cancellationToken).ConfigureAwait(false);
            EnsurePlanUsagePermission(refreshed);
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
            await using var lease = await _credentialStore.AcquireLockAsync(_providerKey, cancellationToken).ConfigureAwait(false);
            var credential = await LoadCredentialAsync(cancellationToken).ConfigureAwait(false);
            var refreshed = await RefreshAsync(credential, cancellationToken).ConfigureAwait(false);
            await _credentialStore.SaveAsync(_providerKey, refreshed, cancellationToken).ConfigureAwait(false);
            EnsurePlanUsagePermission(refreshed);
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

        return null;
    }

    private async ValueTask<OpenAICodexSubscriptionCredential> LoadCredentialAsync(
        CancellationToken cancellationToken)
    {
        // Always reload so sign-out, account changes, and another process's rotating
        // refresh are observed before sending another authenticated request.
        var credential = await _credentialStore.LoadAsync(_providerKey, cancellationToken).ConfigureAwait(false);
        if (!OpenAICodexSubscriptionLoginManager.IsRegistration(credential) || string.IsNullOrWhiteSpace(credential!.AccessToken))
        {
            throw new InvalidOperationException("ChatGPT login is required for the Codex subscription provider. Use Continue with ChatGPT; legacy Codex credentials are not supported.");
        }

        EnsurePlanUsagePermission(credential);
        return credential;
    }

    private static void EnsurePlanUsagePermission(OpenAICodexSubscriptionCredential credential)
    {
        if (!credential.HasPlanUsagePermission)
        {
            throw new InvalidOperationException("ChatGPT plan usage was not authorized. Continue with ChatGPT and allow access to your plan, or configure an API-key provider.");
        }
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
            var refreshed = await _oauthClient.RefreshAsync(credential, cancellationToken).ConfigureAwait(false);
            refreshed.AccountId ??= credential.AccountId;
            refreshed.AccountLabel ??= credential.AccountLabel;
            refreshed.IsFedRamp = credential.IsFedRamp;
            return refreshed;
        }
        catch (OpenAICodexSubscriptionTokenException ex) when (ex.HasUnusableTokens)
        {
            credential.AccessToken = string.Empty;
            credential.RefreshToken = null;
            credential.IdToken = null;
            credential.ExpiresAt = DateTimeOffset.MinValue;
            await _credentialStore.SaveAsync(_providerKey, credential, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                OpenAICodexSubscriptionSecretRedactor.Redact("ChatGPT token refresh failed; re-authentication is required. " + ex.Message, credential),
                ex);
        }
    }
}

internal sealed record OpenAICodexSubscriptionAccountContext(
    string? AccountId,
    string? AccountLabel,
    bool IsFedRamp);
