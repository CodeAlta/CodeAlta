using CodeAlta.Agent.Copilot;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting;

/// <summary>
/// Composes configured Copilot device login, credential deletion and cached non-secret status operations.
/// </summary>
/// <remarks>
/// The caller owns lazy root selection, localization and presentation, including any browser launch.
/// Production operations can perform network and credential-file I/O in the provider package; this is
/// not a sandbox or a live readiness check. After required-argument validation and the exact ordinal
/// <c>copilot</c> type check, the manager and its HTTP client are constructed before the root callback.
/// Option arguments are evaluated in order: provider key, root callback, enterprise string, API URI.
/// Blank values are not rejected or normalized here; key validation and blank-root fallback remain
/// provider-owned and may resolve a user credential path. This class does not discover a root itself.
/// The existing absence of manager/HTTP-client disposal is preserved, not a new lifetime guarantee.
/// Callbacks and cancellation tokens pass through without an early cancellation check, additional
/// scheduling or synchronization-context suppression. Constructor, callback, transport and storage
/// exceptions propagate unchanged. No registry, runtime or application lifetime is owned here.
/// </remarks>
public static class ConfiguredCopilotAuthentication
{
    /// <summary>
    /// Completes configured Copilot device login, optionally browser-assisted by the caller's callback.
    /// </summary>
    /// <param name="definition">The provider definition; its type must be exactly <c>copilot</c>.</param>
    /// <param name="getStateRootPath">Required caller-owned root callback, invoked once after manager construction. Its result is forwarded unchanged.</param>
    /// <param name="formatInvalidProvider">Required message callback, invoked only on a provider-type mismatch before construction or root selection.</param>
    /// <param name="onDeviceCode">Required provider callback, forwarded unchanged with no frontend dispatch or browser launch here.</param>
    /// <param name="cancellationToken">Token forwarded to the provider without an early cancellation check.</param>
    /// <returns>The provider's non-secret login result after login and credential persistence complete.</returns>
    /// <exception cref="ArgumentNullException">The definition, device callback, root callback or message callback is null, checked in that order before type validation. The provider also rejects a null key after options construction.</exception>
    /// <exception cref="InvalidOperationException">The type is not exactly <c>copilot</c>, or provider authorization/token exchange fails.</exception>
    /// <exception cref="ArgumentException">The provider rejects a blank key after options have been constructed.</exception>
    /// <exception cref="TimeoutException">The provider's device authorization expires before completion.</exception>
    /// <exception cref="OperationCanceledException">The provider or a callback observes cancellation.</exception>
    /// <remarks>Device callbacks are awaited by the provider. Other constructor, callback, network and credential-storage exceptions propagate unchanged.</remarks>
    public static Task<CopilotDirectLoginResult> LoginWithDeviceCodeAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Func<CopilotDirectDeviceCode, CancellationToken, ValueTask> onDeviceCode,
        CancellationToken cancellationToken)
        => LoginWithDeviceCodeAsync(
            definition, getStateRootPath, formatInvalidProvider,
            static () => new CopilotDirectLoginManager(new HttpClient()).LoginWithDeviceCodeAsync,
            onDeviceCode, cancellationToken);

    /// <summary>
    /// Attempts deletion of the configured provider's CodeAlta-owned Copilot credentials.
    /// </summary>
    /// <param name="definition">The provider definition; its type must be exactly <c>copilot</c>.</param>
    /// <param name="getStateRootPath">Required caller-owned root callback, invoked once after manager construction. Its result is forwarded unchanged.</param>
    /// <param name="formatInvalidProvider">Required message callback, invoked only on a provider-type mismatch before construction or root selection.</param>
    /// <param name="cancellationToken">Token forwarded to the provider without an early cancellation check.</param>
    /// <returns>A task completing after the provider's deletion attempt.</returns>
    /// <exception cref="ArgumentNullException">The definition, root callback or message callback is null, checked in that order before type validation. The provider also rejects a null key after options construction.</exception>
    /// <exception cref="InvalidOperationException">The type is not exactly <c>copilot</c>.</exception>
    /// <exception cref="ArgumentException">The provider rejects a blank key after options have been constructed.</exception>
    /// <exception cref="OperationCanceledException">The provider or a callback observes cancellation.</exception>
    /// <remarks>Constructor, callback and credential-storage exceptions propagate unchanged. This does not revoke credentials at GitHub.</remarks>
    public static Task DeleteCredentialAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        CancellationToken cancellationToken)
        => DeleteCredentialAsync(
            definition, getStateRootPath, formatInvalidProvider,
            static () => new CopilotDirectLoginManager(new HttpClient()).DeleteCredentialAsync,
            cancellationToken);

    /// <summary>
    /// Reads configured Copilot cached credential status without returning secrets or performing live authentication.
    /// </summary>
    /// <param name="definition">The provider definition; its type must be exactly <c>copilot</c>.</param>
    /// <param name="getStateRootPath">Required caller-owned root callback, invoked once after manager construction. Its result is forwarded unchanged.</param>
    /// <param name="formatInvalidProvider">Required message callback, invoked only on a provider-type mismatch before construction or root selection.</param>
    /// <param name="cancellationToken">Token forwarded to the provider without an early cancellation check.</param>
    /// <returns>The provider's non-secret cached result, or null when its cache policy finds no usable credential.</returns>
    /// <exception cref="ArgumentNullException">The definition, root callback or message callback is null, checked in that order before type validation. The provider also rejects a null key after options construction.</exception>
    /// <exception cref="InvalidOperationException">The type is not exactly <c>copilot</c>.</exception>
    /// <exception cref="ArgumentException">The provider rejects a blank key after options have been constructed.</exception>
    /// <exception cref="OperationCanceledException">The provider or a callback observes cancellation.</exception>
    /// <remarks>There is no refresh, environment-token verification or model turn. Constructor, callback and any storage exceptions escaping provider cache handling propagate unchanged.</remarks>
    public static Task<CopilotDirectLoginResult?> GetCredentialStatusAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        CancellationToken cancellationToken)
        => GetCredentialStatusAsync(
            definition, getStateRootPath, formatInvalidProvider,
            static () => new CopilotDirectLoginManager(new HttpClient()).GetCredentialStatusAsync,
            cancellationToken);

    // Mandatory call-scoped seams: no injected factory can fall back to a concrete manager.
    internal delegate ValueTask<CopilotDirectLoginResult> CopilotDirectLoginOperation(
        CopilotDirectLoginOptions options,
        Func<CopilotDirectDeviceCode, CancellationToken, ValueTask> onDeviceCode,
        CancellationToken cancellationToken);

    internal delegate ValueTask CopilotDirectDeleteCredentialOperation(
        CopilotDirectLoginOptions options,
        CancellationToken cancellationToken);

    internal delegate ValueTask<CopilotDirectLoginResult?> CopilotDirectCredentialStatusOperation(
        CopilotDirectLoginOptions options,
        CancellationToken cancellationToken);

    internal static async Task<CopilotDirectLoginResult> LoginWithDeviceCodeAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Func<CopilotDirectLoginOperation> createOperation,
        Func<CopilotDirectDeviceCode, CancellationToken, ValueTask> onDeviceCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(onDeviceCode);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(createOperation);

        if (!string.Equals(definition.ProviderType, "copilot", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        // Preserve construction before root/options and the existing absence of manager/HttpClient disposal.
        var operation = createOperation();
        return await operation(CreateCopilotDirectLoginOptions(definition, getStateRootPath), onDeviceCode, cancellationToken);
    }

    internal static async Task DeleteCredentialAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Func<CopilotDirectDeleteCredentialOperation> createOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(createOperation);

        if (!string.Equals(definition.ProviderType, "copilot", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        var operation = createOperation();
        await operation(CreateCopilotDirectLoginOptions(definition, getStateRootPath), cancellationToken);
    }

    internal static async Task<CopilotDirectLoginResult?> GetCredentialStatusAsync(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath,
        Func<string> formatInvalidProvider,
        Func<CopilotDirectCredentialStatusOperation> createOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(getStateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidProvider);
        ArgumentNullException.ThrowIfNull(createOperation);

        if (!string.Equals(definition.ProviderType, "copilot", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(formatInvalidProvider());
        }

        var operation = createOperation();
        return await operation(CreateCopilotDirectLoginOptions(definition, getStateRootPath), cancellationToken);
    }

    private static CopilotDirectLoginOptions CreateCopilotDirectLoginOptions(
        CodeAltaProviderDocument definition,
        Func<string> getStateRootPath)
        => new(
            definition.ProviderKey,
            getStateRootPath(),
            definition.GitHubEnterpriseUrl,
            TryCreateUri(definition.ApiUrl));

    private static Uri? TryCreateUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
}
