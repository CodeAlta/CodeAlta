using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting;

internal delegate ValueTask CodexSubscriptionDeleteCredentialOperation(CancellationToken cancellationToken);

internal delegate ValueTask CodexAccountLookupOperation(
    Action<CodexAccountMetadata?> onMetadata,
    CancellationToken cancellationToken);

internal delegate ValueTask CodexDeviceLoginOperation(
    Action<string, string> reportDeviceCode,
    Func<string> formatCompletionPrefix,
    Action<string, string?> onCompleted,
    CancellationToken cancellationToken);

internal delegate ValueTask CodexBrowserLoginOperation(
    Action<Uri> reportAuthorization,
    Action<Uri> openBrowser,
    Func<string> formatCompletionPrefix,
    Action<string, string?> onCompleted,
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
/// Composes configured Codex credential deletion, local non-secret account metadata lookup, device and browser login.
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
/// and the dialog's unchanged cancellation behavior. Device login projects display strings; browser login
/// projects the existing authorization URI and raw nullable account ID through synchronous callbacks.
/// Browser launch and presentation remain caller-owned; authentication-test orchestration remains TUI-owned.
/// Broader provider behavior, application lifetime and native parity remain unqualified.
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

    /// <summary>
    /// Completes configured ChatGPT/Codex device login through the existing provider and reports display values.
    /// </summary>
    /// <param name="definition">Required original definition; its type must be exactly ordinal <c>codex</c>. No configured-account, auth-source or enabled eligibility check is added.</param>
    /// <param name="getStateRootPath">Required lazy trusted-backend root callback, not a renderer grant or sandbox. Root/store construction and validation precede HTTP/OAuth construction, then the original key read and manager validation. Root/key values are not normalized or given a fallback here.</param>
    /// <param name="formatInvalidProvider">Required mismatch-only localization callback, invoked before the factory or root selection.</param>
    /// <param name="reportDeviceCode">Required synchronous callback receiving raw VerificationUri STRING first and UserCode second, without URI parsing. The provider callback token is ignored; returning does not imply a dispatcher has rendered the prompt.</param>
    /// <param name="formatCompletionPrefix">Required callback invoked first after the manager completes, before reading the credential's raw nullable account ID.</param>
    /// <param name="onCompleted">Required synchronous callback receiving the localized prefix first and the once-captured raw nullable account ID second. No trimming, resolver, label or account-metadata record is used; presentation runs without an intervening await.</param>
    /// <param name="cancellationToken">Original token forwarded to device completion without an early cancellation check; TimeProvider remains unspecified.</param>
    /// <returns>A task completing after provider request, reporting, polling, persistence and synchronous completion presentation.</returns>
    /// <exception cref="ArgumentNullException">Definition, root, invalid-provider, report, prefix or completion callback is null, checked in that order before the factory. Provider constructors also reject null root/key values.</exception>
    /// <exception cref="ArgumentException">The provider rejects a blank root at store construction or a blank key at later manager construction.</exception>
    /// <exception cref="InvalidOperationException">The type is not exactly <c>codex</c>, or the provider rejects authorization or a response.</exception>
    /// <exception cref="TimeoutException">The provider reports expired device authorization.</exception>
    /// <exception cref="OperationCanceledException">The provider or a callback observes cancellation.</exception>
    /// <remarks>
    /// Required guards and exact type selection precede the deferred factory. The existing provider requests,
    /// awaits synchronous reporting, polls, populates local metadata and persists before returning. Request,
    /// callback, protocol and storage exceptions propagate unchanged; completion presentation can fail AFTER
    /// persistence. The approved post-prefix ID snapshot intentionally replaces the former nonblank branch's
    /// two plain-property reads with one; it is not arbitrary-property or task identity/stack/settlement equivalence.
    /// No credential/protocol record or credential-capturing lazy result escapes. Transient authorization display
    /// values and possibly personal account metadata must not acquire extra logging or persistence.
    /// No disposal, ownership, context suppression, cancellation checks, scheduling, retry, wrapping or settlement
    /// framework is added. Existing manager/HTTP-client non-disposal, TUI root policy and dialog cancellation remain.
    /// Authentication testing remains TUI-owned. Inert forwarding/source checks do not qualify
    /// real protocol/storage behavior, native parity or application lifetime.
    /// </remarks>
    public static Task LoginWithDeviceCodeAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Action<string, string> reportDeviceCode,
        Func<string> formatCompletionPrefix,
        Action<string, string?> onCompleted,
        CancellationToken cancellationToken)
        => LoginWithDeviceCodeAsync(
            definition, getStateRootPath, formatInvalidProvider, reportDeviceCode, formatCompletionPrefix, onCompleted,
            static (providerDefinition, getStateRootPath) =>
            {
                var manager = new OpenAICodexSubscriptionLoginManager(
                    new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath()),
                    new OpenAICodexSubscriptionOAuthClient(new HttpClient()),
                    providerDefinition.ProviderKey);
                return async (reportDeviceCode, formatCompletionPrefix, onCompleted, token) =>
                {
                    var credential = await manager.CompleteDeviceLoginAsync(
                        (deviceCode, _) =>
                        {
                            reportDeviceCode(deviceCode.VerificationUri, deviceCode.UserCode);
                            return ValueTask.CompletedTask;
                        },
                        cancellationToken: token);
                    var prefix = formatCompletionPrefix();
                    // Approved once-only raw ID capture AFTER prefix localization, not resolver output.
                    // The provider returns a fresh, unexposed credential with a plain auto-property.
                    var rawAccountId = credential.AccountId;
                    onCompleted(prefix, rawAccountId);
                };
            },
            cancellationToken);

    internal static async Task LoginWithDeviceCodeAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Action<string, string> reportDeviceCode,
        Func<string> formatCompletionPrefix,
        Action<string, string?> onCompleted,
        Func<CodeAltaProviderDocument, Func<string>, CodexDeviceLoginOperation> createOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(reportDeviceCode);
        ArgumentNullException.ThrowIfNull(formatCompletionPrefix);
        ArgumentNullException.ThrowIfNull(onCompleted);
        ArgumentNullException.ThrowIfNull(createOperation);

        if (!string.Equals(definition.ProviderType, "codex", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        var operation = createOperation(definition, getStateRootPath);
        await operation(reportDeviceCode, formatCompletionPrefix, onCompleted, cancellationToken);
    }

    /// <summary>
    /// Completes configured ChatGPT/Codex browser login through the existing provider and caller-owned presentation.
    /// </summary>
    /// <param name="definition">Required original definition; its type must be exactly ordinal <c>codex</c>. Its account ID is read after manager construction, at Begin, without new eligibility or normalization.</param>
    /// <param name="getStateRootPath">Required lazy trusted-backend root callback, not a renderer grant or sandbox. Root/store validation precedes HTTP/OAuth construction, then the original key read and manager validation; values are forwarded unchanged.</param>
    /// <param name="formatInvalidProvider">Required mismatch-only formatter, invoked before the factory or root selection.</param>
    /// <param name="reportAuthorization">Required synchronous callback receiving the existing authorization <see cref="Uri"/> after the callback wait starts. Returning does not imply a dispatcher rendered the prompt.</param>
    /// <param name="openBrowser">Required synchronous caller-owned opener, invoked after reporting with a separate read of the authorization URI. The TUI opener still suppresses ordinary launch exceptions.</param>
    /// <param name="formatCompletionPrefix">Required callback invoked after awaiting the stored callback task, before reading the raw nullable credential account ID.</param>
    /// <param name="onCompleted">Required synchronous callback receiving prefix then once-captured raw nullable ID, without an intervening await, trimming, resolver, label, metadata record or lazy credential getter.</param>
    /// <param name="cancellationToken">Original token forwarded to the provider wait without an early cancellation check.</param>
    /// <returns>A task completing after provider callback handling and synchronous completion presentation; presentation may fail after persistence.</returns>
    /// <exception cref="ArgumentNullException">Definition, root, mismatch, report, opener, prefix or completion callback is null, checked in that order before ordinal type selection and construction. Provider constructors also reject null root/key values.</exception>
    /// <exception cref="ArgumentException">The provider rejects a blank root during store construction or a blank key during later manager construction.</exception>
    /// <exception cref="InvalidOperationException">The type is not exactly <c>codex</c>, or the provider rejects authorization or a response.</exception>
    /// <exception cref="OperationCanceledException">The provider or a callback observes cancellation.</exception>
    /// <remarks>
    /// The internal mandatory-factory guard follows all seven required-object guards. The deferred factory
    /// constructs root/store, HTTP/OAuth, then key/manager. Its operation reads the original configured ID at
    /// Begin, stores WaitForBrowserCallbackAsync(...).AsTask(), reports, opens, then awaits that stored task.
    /// An already-faulted/canceled returned wait still permits reporting/opening before await; a genuinely
    /// synchronous fake start-wait throw differs from ordinary async provider failures. Report/custom-opener
    /// failure intentionally leaves the wait unjoined; it may later persist. No joining or lifetime remedy is added.
    /// Prefix localization precedes the once-only raw ID read and synchronous completion. This accepted
    /// browser-specific nonblank two-reads-to-one deviation uses a fresh sealed credential's plain property,
    /// saved before return; it is not arbitrary-property or task identity/stack/settlement equivalence.
    /// URI query/correlation and potentially personal display data must not acquire extra logging/persistence,
    /// parsing, normalization or snapshots. No credential, PKCE, state or browser-context object crosses callbacks.
    /// The provider saves before its status-200 text/plain; charset=utf-8 success response, not HTML. Response,
    /// cleanup or presentation failures may follow persistence; provider response/cleanup exceptions may supersede
    /// earlier failures. Exceptions escaping constructors, callbacks and provider work propagate without wrapping.
    /// No early cancellation, context suppression, scheduling, retry, ownership/disposal or settlement framework
    /// is added; existing manager/HTTP-client non-disposal and TUI root/dialog cancellation policies remain.
    /// Inert recording order and named-source checks do not qualify concrete protocol/listener/storage behavior,
    /// application lifetime or native parity. Authentication-test orchestration remains TUI-owned and deferred.
    /// </remarks>
    public static Task LoginWithBrowserAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Action<Uri> reportAuthorization,
        Action<Uri> openBrowser,
        Func<string> formatCompletionPrefix,
        Action<string, string?> onCompleted,
        CancellationToken cancellationToken)
        => LoginWithBrowserAsync(
            definition, getStateRootPath, formatInvalidProvider, reportAuthorization, openBrowser, formatCompletionPrefix, onCompleted,
            static (providerDefinition, getStateRootPath) =>
            {
                var manager = new OpenAICodexSubscriptionLoginManager(
                    new FileOpenAICodexSubscriptionCredentialStore(getStateRootPath()),
                    new OpenAICodexSubscriptionOAuthClient(new HttpClient()),
                    providerDefinition.ProviderKey);
                return async (reportAuthorization, openBrowser, formatCompletionPrefix, onCompleted, token) =>
                {
                    var login = manager.BeginBrowserLogin(providerDefinition.AccountId);
                    var waitForCallbackTask = manager.WaitForBrowserCallbackAsync(login, token).AsTask();
                    reportAuthorization(login.AuthorizeUri);
                    openBrowser(login.AuthorizeUri);
                    var credential = await waitForCallbackTask;
                    var prefix = formatCompletionPrefix();
                    // Approved browser-specific post-prefix snapshot: the nonblank branch now reads once,
                    // not twice. The fresh sealed credential has a plain property and is saved before return.
                    // This is not arbitrary-property or task identity/stack/settlement equivalence.
                    var rawAccountId = credential.AccountId;
                    onCompleted(prefix, rawAccountId);
                };
            },
            cancellationToken);

    internal static async Task LoginWithBrowserAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Action<Uri> reportAuthorization,
        Action<Uri> openBrowser,
        Func<string> formatCompletionPrefix,
        Action<string, string?> onCompleted,
        Func<CodeAltaProviderDocument, Func<string>, CodexBrowserLoginOperation> createOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(reportAuthorization);
        ArgumentNullException.ThrowIfNull(openBrowser);
        ArgumentNullException.ThrowIfNull(formatCompletionPrefix);
        ArgumentNullException.ThrowIfNull(onCompleted);
        ArgumentNullException.ThrowIfNull(createOperation);

        if (!string.Equals(definition.ProviderType, "codex", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        var operation = createOperation(definition, getStateRootPath);
        await operation(reportAuthorization, openBrowser, formatCompletionPrefix, onCompleted, cancellationToken);
    }
}
