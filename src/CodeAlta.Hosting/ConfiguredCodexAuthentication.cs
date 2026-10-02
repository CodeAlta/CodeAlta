using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting;

/// <summary>Non-secret account and registration metadata from a validated ChatGPT registration.</summary>
/// <param name="AccountLabel">The provider's raw account label, or null.</param>
/// <param name="Subject">The validated identity subject, or null.</param>
/// <param name="ClientId">The issued account/workspace registration ID.</param>
/// <param name="HasPlanUsagePermission">Whether the registration permits ChatGPT plan inference.</param>
/// <remarks>Contains no tokens. Account metadata may be personal data and must not be logged indiscriminately.</remarks>
public sealed record CodexAccountMetadata(string? AccountLabel, string? Subject, string ClientId, bool HasPlanUsagePermission);

/// <summary>Composes the provider's ChatGPT token-sharing sign-in, sign-out and registration inspection.</summary>
/// <remarks>
/// Frontends own root selection, localization and browser launch. OAuth, identity validation, credential
/// persistence and refresh remain in the provider. These operations use the same flow as the TUI on main;
/// legacy device login and credential imports are not supported. Roots are trusted backend inputs,
/// not renderer filesystem grants. Callback and provider exceptions propagate unchanged.
/// </remarks>
public static class ConfiguredCodexAuthentication
{
    /// <summary>Signs in through the system browser and returns only non-secret registration metadata.</summary>
    /// <param name="definition">The configured Codex provider.</param>
    /// <param name="getStateRootPath">Caller-owned lazy global state root selection.</param>
    /// <param name="formatInvalidProvider">Formats an error only for a provider-type mismatch.</param>
    /// <param name="reportAuthorization">Reports the authorization URI after starting the callback wait.</param>
    /// <param name="openBrowser">Launches the system browser after reporting the URI.</param>
    /// <param name="cancellationToken">Forwarded to host-ID creation and provider login.</param>
    /// <returns>Validated registration metadata, including plan permission even when access was declined.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Provider storage rejects a blank root or provider key.</exception>
    /// <exception cref="IOException">Provider state cannot be read or written.</exception>
    /// <exception cref="InvalidOperationException">The provider type is not Codex or authorization fails.</exception>
    /// <exception cref="TimeoutException">The browser callback expires.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static Task<CodexAccountMetadata> LoginWithBrowserAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Action<Uri> reportAuthorization,
        Action<Uri> openBrowser,
        CancellationToken cancellationToken)
        => LoginWithBrowserAsync(definition, getStateRootPath, formatInvalidProvider, reportAuthorization, openBrowser,
            static (provider, getRoot) =>
            {
                var manager = CreateLoginManager(provider, getRoot);
                return async (report, open, token) =>
                {
                    var hostId = await new OpenAICodexSubscriptionHostIdProvider(getRoot()).GetOrCreateAsync(token);
                    using var login = await manager.BeginBrowserLoginAsync(hostId, token);
                    var waitForCallbackTask = manager.WaitForBrowserCallbackAsync(login, token).AsTask();
                    report(login.AuthorizeUri);
                    open(login.AuthorizeUri);
                    return Project(await waitForCallbackTask);
                };
            }, cancellationToken);

    internal static async Task<CodexAccountMetadata> LoginWithBrowserAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Action<Uri> reportAuthorization,
        Action<Uri> openBrowser,
        Func<CodeAltaProviderDocument, Func<string>, Func<Action<Uri>, Action<Uri>, CancellationToken, Task<CodexAccountMetadata>>> createOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(reportAuthorization);
        ArgumentNullException.ThrowIfNull(openBrowser);
        ArgumentNullException.ThrowIfNull(createOperation);
        if (!string.Equals(definition.ProviderType, "codex", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        return await createOperation(definition, getStateRootPath)(reportAuthorization, openBrowser, cancellationToken);
    }

    /// <summary>Attempts remote revocation and clears local tokens while retaining the registration.</summary>
    /// <param name="definition">The configured Codex provider.</param>
    /// <param name="getStateRootPath">Caller-owned lazy global state root selection.</param>
    /// <param name="formatInvalidProvider">Formats an error only for a provider-type mismatch.</param>
    /// <param name="cancellationToken">Forwarded to provider sign-out.</param>
    /// <returns>Whether remote revocation was confirmed.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Provider storage rejects a blank root or provider key.</exception>
    /// <exception cref="IOException">Provider state cannot be read or written.</exception>
    /// <exception cref="InvalidOperationException">The provider type is not Codex.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static Task<bool> SignOutAsync(
        CodeAltaProviderDocument definition, Func<string> getStateRootPath,
        Func<string> formatInvalidProvider, CancellationToken cancellationToken)
        => SignOutAsync(definition, getStateRootPath, formatInvalidProvider,
            static (provider, getRoot) => CreateLoginManager(provider, getRoot).SignOutAsync, cancellationToken);

    internal static async Task<bool> SignOutAsync(
        CodeAltaProviderDocument definition, Func<string> getStateRootPath, Func<string> formatInvalidProvider,
        Func<CodeAltaProviderDocument, Func<string>, Func<CancellationToken, ValueTask<bool>>> createOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(createOperation);
        if (!string.Equals(definition.ProviderType, "codex", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        return await createOperation(definition, getStateRootPath)(cancellationToken);
    }

    /// <summary>Gets or refreshes the authorized credential without sending a model turn.</summary>
    /// <param name="definition">The configured Codex provider.</param>
    /// <param name="getStateRootPath">Caller-owned lazy global state root selection.</param>
    /// <param name="formatInvalidProvider">Formats an error only for a provider-type mismatch.</param>
    /// <param name="cancellationToken">Forwarded to provider authentication.</param>
    /// <returns>Non-secret account and registration metadata.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Provider storage or authentication rejects a blank root or key.</exception>
    /// <exception cref="IOException">Provider state cannot be read or written.</exception>
    /// <exception cref="InvalidOperationException">The provider type, registration or plan permission is invalid.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <remarks>A fresh cached credential need not contact the server. Refresh may update provider-owned storage.</remarks>
    public static Task<CodexAccountMetadata> TestAuthenticationAsync(
        CodeAltaProviderDocument definition, Func<string> getStateRootPath,
        Func<string> formatInvalidProvider, CancellationToken cancellationToken)
        => TestAuthenticationAsync(definition, getStateRootPath, formatInvalidProvider,
            static (provider, getRoot) =>
            {
                var manager = new OpenAICodexSubscriptionAuthManager(
                    new FileOpenAICodexSubscriptionCredentialStore(getRoot()),
                    new OpenAICodexSubscriptionOAuthClient(new HttpClient()),
                    provider.ProviderKey, provider.AuthSource ?? "codealta_oauth", provider.AccountId);
                return async token => Project(await manager.GetCredentialAsync(token));
            }, cancellationToken);

    internal static async Task<CodexAccountMetadata> TestAuthenticationAsync(
        CodeAltaProviderDocument definition, Func<string> getStateRootPath, Func<string> formatInvalidProvider,
        Func<CodeAltaProviderDocument, Func<string>, Func<CancellationToken, Task<CodexAccountMetadata>>> createOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(createOperation);
        if (!string.Equals(definition.ProviderType, "codex", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        return await createOperation(definition, getStateRootPath)(cancellationToken);
    }

    /// <summary>Reads validated registration metadata from the local store, without network authentication.</summary>
    /// <param name="definition">The configured provider whose registration is read.</param>
    /// <param name="getStateRootPath">Caller-owned lazy global state root selection.</param>
    /// <param name="cancellationToken">Forwarded to credential storage.</param>
    /// <returns>Non-secret metadata, or null for a missing, pending or legacy registration.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Provider storage rejects a blank root or provider key.</exception>
    /// <exception cref="IOException">Provider state cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static Task<CodexAccountMetadata?> ReadAccountMetadataAsync(
        CodeAltaProviderDocument definition, Func<string> getStateRootPath, CancellationToken cancellationToken)
        => ReadAccountMetadataAsync(definition, getStateRootPath,
            static (provider, getRoot) =>
            {
                var store = new FileOpenAICodexSubscriptionCredentialStore(getRoot());
                return async token =>
                {
                    var credential = await store.LoadAsync(provider.ProviderKey, token);
                    return OpenAICodexSubscriptionLoginManager.IsRegistration(credential) ? Project(credential) : null;
                };
            }, cancellationToken);

    internal static async Task<CodexAccountMetadata?> ReadAccountMetadataAsync(
        CodeAltaProviderDocument definition, Func<string> getStateRootPath,
        Func<CodeAltaProviderDocument, Func<string>, Func<CancellationToken, Task<CodexAccountMetadata?>>> createOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(createOperation);
        return await createOperation(definition, getStateRootPath)(cancellationToken);
    }

    private static OpenAICodexSubscriptionLoginManager CreateLoginManager(CodeAltaProviderDocument definition, Func<string> getRoot)
        => new(new FileOpenAICodexSubscriptionCredentialStore(getRoot()),
            new OpenAICodexSubscriptionOAuthClient(new HttpClient()), definition.ProviderKey);

    internal static CodexAccountMetadata Project(OpenAICodexSubscriptionCredential credential)
        => new(credential.AccountLabel, credential.Subject, credential.ClientId, credential.HasPlanUsagePermission);
}
