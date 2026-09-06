using CodeAlta.Agent.Xai;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting;

/// <summary>
/// Composes configured xAI browser login, device login, credential deletion and cached non-secret status operations.
/// </summary>
/// <remarks>
/// The caller owns lazy root selection, localization and presentation, including any browser launch.
/// Production login performs network and credential-file I/O; browser login also binds the provider's
/// loopback listener for PKCE authorization. Deletion and cached status access provider-owned credential files.
/// This is not a sandbox, a live readiness check or an application-lifetime owner. After required-argument
/// validation and the exact ordinal <c>xai</c> type check, the manager and its HTTP client are constructed
/// before the root callback. Option arguments are evaluated in order: provider key, root callback, API URI.
/// Blank keys/roots are forwarded unchanged; provider validation and blank-root fallback may resolve a user
/// credential path. Invalid or relative API URIs become null; other absolute schemes are not restricted here.
/// No polling override is set. The existing absence of manager/HTTP-client disposal is preserved, not a
/// lifetime guarantee. Callbacks and cancellation tokens pass through without early cancellation, additional
/// scheduling, synchronization-context suppression, retry or exception wrapping. Exceptions escaping provider
/// operations and caller callbacks propagate unchanged; provider-internal exception transformations, loopback
/// teardown, CORS and the browser response/persistence sequence remain provider-owned.
/// </remarks>
public static class ConfiguredXaiAuthentication
{
    /// <summary>
    /// Completes configured xAI browser PKCE login through the provider's loopback authorization flow.
    /// </summary>
    /// <param name="definition">The provider definition; its type must be exactly <c>xai</c>.</param>
    /// <param name="getStateRootPath">Required caller-owned root callback, invoked once after manager construction. Its result is forwarded unchanged.</param>
    /// <param name="formatInvalidProvider">Required message callback, invoked only on a provider-type mismatch before construction or root selection.</param>
    /// <param name="onAuthorize">Required authorization callback, forwarded unchanged and awaited by the provider after its listener binds. The caller supplies any browser launch.</param>
    /// <param name="cancellationToken">Token forwarded to the provider without an early cancellation check.</param>
    /// <returns>The provider's non-secret result after login and credential persistence complete.</returns>
    /// <exception cref="ArgumentNullException">The definition, authorization callback, root callback or message callback is null, checked in that order before type validation. The provider also rejects a null key after options construction.</exception>
    /// <exception cref="InvalidOperationException">The type is not exactly <c>xai</c>, or provider authorization, state validation or token exchange fails.</exception>
    /// <exception cref="ArgumentException">The provider rejects a blank key after options have been constructed.</exception>
    /// <exception cref="OperationCanceledException">The provider or a callback observes cancellation.</exception>
    /// <remarks>Constructor, callback and exceptions escaping provider network, listener and credential-storage handling propagate unchanged. Provider loopback teardown and exception transformations are not altered here.</remarks>
    public static Task<XaiDirectLoginResult> LoginWithBrowserAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Func<XaiDirectBrowserAuthorization, CancellationToken, ValueTask> onAuthorize,
        CancellationToken cancellationToken)
        => LoginWithBrowserAsync(
            definition, getStateRootPath, formatInvalidProvider,
            static () => new XaiDirectLoginManager(new HttpClient()).LoginWithBrowserAsync,
            onAuthorize, cancellationToken);

    /// <summary>
    /// Completes configured xAI device-code login through the provider's authorization polling flow.
    /// </summary>
    /// <param name="definition">The provider definition; its type must be exactly <c>xai</c>.</param>
    /// <param name="getStateRootPath">Required caller-owned root callback, invoked once after manager construction. Its result is forwarded unchanged.</param>
    /// <param name="formatInvalidProvider">Required message callback, invoked only on a provider-type mismatch before construction or root selection.</param>
    /// <param name="onDeviceCode">Required device callback, forwarded unchanged and awaited by the provider before polling. No browser is launched here.</param>
    /// <param name="cancellationToken">Token forwarded to the provider without an early cancellation check.</param>
    /// <returns>The provider's non-secret result after login and credential persistence complete.</returns>
    /// <exception cref="ArgumentNullException">The definition, device callback, root callback or message callback is null, checked in that order before type validation. The provider also rejects a null key after options construction.</exception>
    /// <exception cref="InvalidOperationException">The type is not exactly <c>xai</c>, or provider device authorization/token exchange fails.</exception>
    /// <exception cref="ArgumentException">The provider rejects a blank key after options have been constructed.</exception>
    /// <exception cref="TimeoutException">The provider's device authorization expires before completion.</exception>
    /// <exception cref="OperationCanceledException">The provider or a callback observes cancellation.</exception>
    /// <remarks>Constructor, callback and exceptions escaping provider network and credential-storage handling propagate unchanged. Polling and persistence remain provider-owned.</remarks>
    public static Task<XaiDirectLoginResult> LoginWithDeviceCodeAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Func<XaiDirectDeviceCode, CancellationToken, ValueTask> onDeviceCode,
        CancellationToken cancellationToken)
        => LoginWithDeviceCodeAsync(
            definition, getStateRootPath, formatInvalidProvider,
            static () => new XaiDirectLoginManager(new HttpClient()).LoginWithDeviceCodeAsync,
            onDeviceCode, cancellationToken);

    /// <summary>
    /// Attempts deletion of the configured provider's CodeAlta-owned xAI credentials.
    /// </summary>
    /// <param name="definition">The provider definition; its type must be exactly <c>xai</c>.</param>
    /// <param name="getStateRootPath">Required caller-owned root callback, invoked once after manager construction. Its result is forwarded unchanged.</param>
    /// <param name="formatInvalidProvider">Required message callback, invoked only on a provider-type mismatch before construction or root selection.</param>
    /// <param name="cancellationToken">Token forwarded to the provider without an early cancellation check.</param>
    /// <returns>A task completing after the provider's deletion attempt.</returns>
    /// <exception cref="ArgumentNullException">The definition, root callback or message callback is null, checked in that order before type validation. The provider also rejects a null key after options construction.</exception>
    /// <exception cref="InvalidOperationException">The type is not exactly <c>xai</c>.</exception>
    /// <exception cref="ArgumentException">The provider rejects a blank key after options have been constructed.</exception>
    /// <exception cref="OperationCanceledException">The provider or a callback observes cancellation.</exception>
    /// <remarks>Constructor, callback and exceptions escaping provider credential-storage handling propagate unchanged. This does not revoke credentials at xAI.</remarks>
    public static Task DeleteCredentialAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        CancellationToken cancellationToken)
        => DeleteCredentialAsync(
            definition, getStateRootPath, formatInvalidProvider,
            static () => new XaiDirectLoginManager(new HttpClient()).DeleteCredentialAsync,
            cancellationToken);

    /// <summary>
    /// Reads configured xAI cached credential status without returning secrets or performing live authentication.
    /// </summary>
    /// <param name="definition">The provider definition; its type must be exactly <c>xai</c>.</param>
    /// <param name="getStateRootPath">Required caller-owned root callback, invoked once after manager construction. Its result is forwarded unchanged.</param>
    /// <param name="formatInvalidProvider">Required message callback, invoked only on a provider-type mismatch before construction or root selection.</param>
    /// <param name="cancellationToken">Token forwarded to the provider without an early cancellation check.</param>
    /// <returns>The provider's non-secret cached result, including past or unknown expiry, or null when no nonempty cached access token is available.</returns>
    /// <exception cref="ArgumentNullException">The definition, root callback or message callback is null, checked in that order before type validation. The provider also rejects a null key after options construction.</exception>
    /// <exception cref="InvalidOperationException">The type is not exactly <c>xai</c>.</exception>
    /// <exception cref="ArgumentException">The provider rejects a blank key after options have been constructed.</exception>
    /// <exception cref="OperationCanceledException">The provider or a callback observes cancellation.</exception>
    /// <remarks>Cached status does not reject expired credentials or apply refresh skew. There is no refresh, environment-token verification or model turn. Constructor, callback and exceptions escaping provider cache handling propagate unchanged.</remarks>
    public static Task<XaiDirectLoginResult?> GetCredentialStatusAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        CancellationToken cancellationToken)
        => GetCredentialStatusAsync(
            definition, getStateRootPath, formatInvalidProvider,
            static () => new XaiDirectLoginManager(new HttpClient()).GetCredentialStatusAsync,
            cancellationToken);

    // Mandatory call-scoped seams: no injected factory can fall back to a concrete manager.
    internal delegate ValueTask<XaiDirectLoginResult> XaiDirectBrowserLoginOperation(
        XaiDirectLoginOptions options,
        Func<XaiDirectBrowserAuthorization, CancellationToken, ValueTask> onAuthorize,
        CancellationToken cancellationToken);

    internal delegate ValueTask<XaiDirectLoginResult> XaiDirectDeviceLoginOperation(
        XaiDirectLoginOptions options,
        Func<XaiDirectDeviceCode, CancellationToken, ValueTask> onDeviceCode,
        CancellationToken cancellationToken);

    internal delegate ValueTask XaiDirectDeleteCredentialOperation(
        XaiDirectLoginOptions options,
        CancellationToken cancellationToken);

    internal delegate ValueTask<XaiDirectLoginResult?> XaiDirectCredentialStatusOperation(
        XaiDirectLoginOptions options,
        CancellationToken cancellationToken);

    internal static async Task<XaiDirectLoginResult> LoginWithBrowserAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Func<XaiDirectBrowserLoginOperation> createOperation,
        Func<XaiDirectBrowserAuthorization, CancellationToken, ValueTask> onAuthorize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(onAuthorize);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(createOperation);

        if (!string.Equals(definition.ProviderType, "xai", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        // Preserve construction before root/options and the existing absence of manager/HttpClient disposal.
        var operation = createOperation();
        return await operation(CreateXaiDirectLoginOptions(definition, getStateRootPath), onAuthorize, cancellationToken);
    }

    internal static async Task<XaiDirectLoginResult> LoginWithDeviceCodeAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Func<XaiDirectDeviceLoginOperation> createOperation,
        Func<XaiDirectDeviceCode, CancellationToken, ValueTask> onDeviceCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(onDeviceCode);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(createOperation);

        if (!string.Equals(definition.ProviderType, "xai", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        var operation = createOperation();
        return await operation(CreateXaiDirectLoginOptions(definition, getStateRootPath), onDeviceCode, cancellationToken);
    }

    internal static async Task DeleteCredentialAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Func<XaiDirectDeleteCredentialOperation> createOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(createOperation);

        if (!string.Equals(definition.ProviderType, "xai", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        var operation = createOperation();
        await operation(CreateXaiDirectLoginOptions(definition, getStateRootPath), cancellationToken);
    }

    internal static async Task<XaiDirectLoginResult?> GetCredentialStatusAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Func<XaiDirectCredentialStatusOperation> createOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(createOperation);

        if (!string.Equals(definition.ProviderType, "xai", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        var operation = createOperation();
        return await operation(CreateXaiDirectLoginOptions(definition, getStateRootPath), cancellationToken);
    }

    private static XaiDirectLoginOptions CreateXaiDirectLoginOptions(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath)
        => new(
            definition.ProviderKey,
            getStateRootPath(),
            TryCreateUri(definition.ApiUrl));

    private static Uri? TryCreateUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
}
