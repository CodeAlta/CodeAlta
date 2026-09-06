using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting;

internal delegate ValueTask CodexSubscriptionDeleteCredentialOperation(CancellationToken cancellationToken);

internal delegate ValueTask CodexAccountLookupOperation(
    Action<CodexAccountMetadata?> onMetadata,
    CancellationToken cancellationToken);

/// <summary>
/// Non-secret metadata from one locally stored Codex credential, not remote account enumeration or authentication validation.
/// </summary>
/// <param name="AccountId">The existing provider resolver's account identifier, or null when unavailable.</param>
/// <param name="AccountLabel">The raw nullable label captured once after account resolution, without trimming or localization.</param>
/// <remarks>
/// A null metadata result denotes a missing credential; a non-null record with a missing identifier still
/// denotes a loaded credential. Only these two strings escape, never a credential or a credential-capturing
/// lazy result. Metadata may be personal data and must not acquire additional logging. The once-only label
/// snapshot is intentional, not mechanical equivalence to the former nonblank branch's two property reads.
/// </remarks>
public sealed record CodexAccountMetadata(string? AccountId, string? AccountLabel);

/// <summary>
/// Composes configured Codex credential deletion and local non-secret account metadata lookup.
/// </summary>
/// <remarks>
/// The caller owns localization and lazy root selection. The root is trusted backend input, not a
/// renderer filesystem grant or sandbox. For deletion, required inputs and the exact ordinal <c>codex</c> type are checked before
/// the deferred production factory: root callback and credential-store construction/validation first,
/// then HTTP/OAuth-client construction, then provider-key read and login-manager construction/validation.
/// Root and key values are forwarded unchanged; the provider rejects null/blank values without a new
/// composition-layer normalization or fallback. The existing absence of manager/HTTP-client disposal
/// is preserved, not a lifetime guarantee. Deletion accesses the provider-owned credential file and
/// does not remotely revoke authorization or perform an OAuth network exchange merely by constructing
/// the OAuth client. No secret-bearing result or result DTO escapes deletion. Account lookup constructs
/// only the owned credential store and reports two-string metadata synchronously after loading; it has no
/// type, auth-source, enabled, expiry or access-token eligibility guard. TUI retains localization, root policy
/// and the dialog's unchanged cancellation behavior. Other Codex login/authentication orchestration remains
/// TUI-owned; broader provider behavior, application lifetime and native parity remain unqualified.
/// </remarks>
public static class ConfiguredCodexAuthentication
{
    /// <summary>
    /// Attempts deletion of the configured provider's CodeAlta-owned ChatGPT/Codex credential file.
    /// </summary>
    /// <param name="definition">Required original provider definition; its type must be exactly <c>codex</c>.</param>
    /// <param name="getStateRootPath">Required lazy root callback, invoked before credential-store validation and HTTP/OAuth construction. Its result is forwarded unchanged; the provider rejects null/blank roots without trimming or fallback.</param>
    /// <param name="formatInvalidProvider">Required message callback, invoked only on a type mismatch, before construction or root selection.</param>
    /// <param name="cancellationToken">Original token forwarded to deletion without an early cancellation check.</param>
    /// <returns>A task completing after the provider's credential-file deletion attempt, with no credential metadata.</returns>
    /// <exception cref="ArgumentNullException">The definition, root callback or message callback is null, checked in that order before type validation. Provider constructors also reject null root/key values at their respective construction points.</exception>
    /// <exception cref="ArgumentException">The provider rejects a blank root during store construction or a blank key during subsequent manager construction.</exception>
    /// <exception cref="InvalidOperationException">The provider type is not exactly <c>codex</c>.</exception>
    /// <exception cref="OperationCanceledException">A callback or the provider observes cancellation.</exception>
    /// <remarks>
    /// Constructor, callback and exceptions escaping provider credential-file handling propagate unchanged.
    /// No early cancellation, context suppression, additional scheduling, retry, wrapping or disposal is added.
    /// This is local credential-file deletion, not remote revocation or a qualification of provider storage behavior.
    /// </remarks>
    public static Task DeleteCredentialAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        CancellationToken cancellationToken)
        => DeleteCredentialAsync(
            definition, getStateRootPath, formatInvalidProvider,
            static (providerDefinition, getStateRootPath) => new OpenAICodexSubscriptionLoginManager(
                new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath()),
                new OpenAICodexSubscriptionOAuthClient(new HttpClient()),
                providerDefinition.ProviderKey).DeleteCredentialAsync,
            cancellationToken);

    internal static async Task DeleteCredentialAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Func<CodeAltaProviderDocument, Func<string>, CodexSubscriptionDeleteCredentialOperation> createOperation,
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

        var operation = createOperation(definition, getStateRootPath);
        await operation(cancellationToken);
    }

    /// <summary>
    /// Reads account metadata from the configured provider's CodeAlta-owned credential store.
    /// </summary>
    /// <param name="definition">Required original mutable definition. There is no provider-type or eligibility check; its configured account ID is read after the load await and only for a non-null credential.</param>
    /// <param name="getStateRootPath">Required lazy trusted-backend root callback. Store construction and provider-owned root validation precede the key read and load; values are not normalized or given a fallback here.</param>
    /// <param name="onMetadata">Required synchronous callback, invoked before operation completion with null for a missing credential or a two-string record otherwise. A missing ID does not mean a missing credential. Presentation can run in the same post-load continuation.</param>
    /// <param name="cancellationToken">Original token forwarded to the store without an early cancellation check.</param>
    /// <returns>A task completing after loading, existing provider account resolution and the synchronous metadata callback.</returns>
    /// <exception cref="ArgumentNullException">Definition, root callback or metadata callback is null, checked in that order before construction. The provider also rejects null root/key values.</exception>
    /// <exception cref="ArgumentException">Provider storage rejects a blank root or key at its existing validation point.</exception>
    /// <exception cref="OperationCanceledException">Storage or the callback observes cancellation. A missing file need not observe a precanceled token.</exception>
    /// <remarks>
    /// Root/store construction precedes key read/load. Null credentials short-circuit before configured-ID
    /// resolution; otherwise the existing resolver runs before the raw label is captured once and reported.
    /// This is not remote enumeration, auth-source import, token refresh or authentication validation.
    /// Constructor, storage, resolver and callback exceptions propagate without wrapping or settlement rules.
    /// No disposal, additional scheduling, context suppression, retry or new logging is introduced.
    /// </remarks>
    public static Task ReadAccountMetadataAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Action<CodexAccountMetadata?> onMetadata,
        CancellationToken cancellationToken)
        => ReadAccountMetadataAsync(
            definition, getStateRootPath, onMetadata,
            static (providerDefinition, getStateRootPath) =>
            {
                var store = new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath());
                return async (onMetadata, token) =>
                {
                    var credential = await store.LoadAsync(providerDefinition.ProviderKey, token);
                    if (credential is null)
                    {
                        onMetadata(null);
                        return;
                    }

                    var accountId = OpenAICodexSubscriptionAuthManager.ResolveAccountId(providerDefinition.AccountId, credential);
                    var accountLabel = credential.AccountLabel;
                    onMetadata(new CodexAccountMetadata(accountId, accountLabel));
                };
            },
            cancellationToken);

    internal static async Task ReadAccountMetadataAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Action<CodexAccountMetadata?> onMetadata,
        Func<CodeAltaProviderDocument, Func<string>, CodexAccountLookupOperation> createOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(onMetadata);
        ArgumentNullException.ThrowIfNull(createOperation);

        var operation = createOperation(definition, getStateRootPath);
        await operation(onMetadata, cancellationToken);
    }
}
