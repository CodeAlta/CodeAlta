using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting;

internal delegate ValueTask CodexSubscriptionDeleteCredentialOperation(CancellationToken cancellationToken);

/// <summary>
/// Composes configured Codex credential deletion only, without exposing credential or account metadata.
/// </summary>
/// <remarks>
/// The caller owns localization and lazy root selection. The root is trusted backend input, not a
/// renderer filesystem grant. Required inputs and the exact ordinal <c>codex</c> type are checked before
/// the deferred production factory: root callback and credential-store construction/validation first,
/// then HTTP/OAuth-client construction, then provider-key read and login-manager construction/validation.
/// Root and key values are forwarded unchanged; the provider rejects null/blank values without a new
/// composition-layer normalization or fallback. The existing absence of manager/HTTP-client disposal
/// is preserved, not a lifetime guarantee. Deletion accesses the provider-owned credential file and
/// does not remotely revoke authorization or perform an OAuth network exchange merely by constructing
/// the OAuth client. No secret-bearing result or result DTO escapes this operation. Other Codex login,
/// authentication and account orchestration remains TUI-owned; broader application lifetime is unqualified.
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
}
