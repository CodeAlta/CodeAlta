using System.Collections.Frozen;
using System.Net;
using System.Text;

namespace CodeAlta.Agent.Xai;

/// <summary>
/// Provides xAI OAuth login helpers (browser PKCE + device code) for the xAI direct provider.
/// </summary>
public sealed class XaiDirectLoginManager
{
    private readonly HttpClient _httpClient;
    private readonly XaiOAuthClient _oauthClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="XaiDirectLoginManager"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use. When <see langword="null"/>, a new client is created.</param>
    public XaiDirectLoginManager(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _oauthClient = new XaiOAuthClient(_httpClient);
    }

    /// <summary>
    /// Completes a browser-based PKCE login by binding the registered loopback redirect URI,
    /// surfacing the authorization URL via <paramref name="onAuthorize"/>, and persisting the exchanged credential.
    /// </summary>
    /// <param name="options">Login options identifying the provider and state root.</param>
    /// <param name="onAuthorize">Callback invoked once the loopback listener is bound; receives the URL the user must open.</param>
    /// <param name="cancellationToken">A token to cancel the login.</param>
    /// <returns>The non-secret login result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> or <paramref name="onAuthorize"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the OAuth callback reports an error or fails the state check.</exception>
    public async ValueTask<XaiDirectLoginResult> LoginWithBrowserAsync(
        XaiDirectLoginOptions options,
        Func<XaiDirectBrowserAuthorization, CancellationToken, ValueTask> onAuthorize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onAuthorize);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderKey);

        var pkce = XaiOAuthClient.CreatePkce();
        var state = XaiOAuthClient.CreateState();
        var nonce = XaiOAuthClient.CreateState();
        var authorizeUri = XaiOAuthClient.BuildAuthorizeUri(pkce, state, nonce);

        using var listener = new HttpListener();
        // Bind to the loopback root so the consent page's CORS / Private-Network
        // preflight probe to `/callback` lands even though the spec-mandated
        // HttpListener prefix syntax requires a trailing slash. Without this,
        // xAI's pre-redirect reachability probe fails and the consent screen
        // shows a "couldn't reach your app" fallback before the real redirect.
        listener.Prefixes.Add($"http://{XaiDefaults.LoopbackHost}:{XaiDefaults.LoopbackPort}/");
        listener.Start();
        using var registration = cancellationToken.Register(static state => ((HttpListener)state!).Stop(), listener);
        try
        {
            await onAuthorize(new XaiDirectBrowserAuthorization(authorizeUri), cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    WritePreflightResponse(context);
                    continue;
                }

                var path = context.Request.Url?.AbsolutePath;
                if (!string.Equals(path, XaiDefaults.LoopbackRedirectPath, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                try
                {
                    var callbackUri = context.Request.Url
                        ?? throw new InvalidOperationException("xAI OAuth callback URL was unavailable.");
                    var token = await CompleteBrowserCallbackAsync(callbackUri, pkce, state, cancellationToken).ConfigureAwait(false);
                    await WriteBrowserCallbackResponseAsync(
                        context,
                        HtmlSuccess,
                        cancellationToken).ConfigureAwait(false);
                    return await PersistTokenAsync(options, token, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await WriteBrowserCallbackResponseAsync(
                        context,
                        HtmlFailure,
                        cancellationToken).ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    // CORS allowlist for the loopback callback. The redirect_uri is already
    // bound to 127.0.0.1 and gated by PKCE+state, so we only accept xAI's own
    // auth origins for defense-in-depth on the OPTIONS preflight. Frozen so the
    // codebase-wide ban on static mutable collections is satisfied.
    private static readonly FrozenSet<string> CorsAllowedOrigins = FrozenSet.ToFrozenSet(
        new[] { "https://accounts.x.ai", "https://auth.x.ai" },
        StringComparer.Ordinal);

    private static void WritePreflightResponse(HttpListenerContext context)
    {
        ApplyCorsHeaders(context);
        context.Response.StatusCode = 204;
        context.Response.Close();
    }

    private static void ApplyCorsHeaders(HttpListenerContext context)
    {
        var origin = context.Request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin) || !CorsAllowedOrigins.Contains(origin))
        {
            return;
        }

        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
        context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        // Chromium gates fetches from public origins to private network (RFC 1918 /
        // loopback) destinations on this header. Without it the consent page's
        // reachability probe is blocked and xAI surfaces a "couldn't reach your app"
        // fallback even though our loopback server is up.
        context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
        context.Response.Headers["Vary"] = "Origin";
    }

    /// <summary>
    /// Completes the device-code flow by polling the xAI token endpoint until the user authorizes.
    /// </summary>
    /// <param name="options">Login options identifying the provider and state root.</param>
    /// <param name="onDeviceCode">Callback invoked when the verification URL and user code are available.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The non-secret login result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> or <paramref name="onDeviceCode"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when xAI authorization fails.</exception>
    /// <exception cref="TimeoutException">Thrown when the device authorization expires before completion.</exception>
    public async ValueTask<XaiDirectLoginResult> LoginWithDeviceCodeAsync(
        XaiDirectLoginOptions options,
        Func<XaiDirectDeviceCode, CancellationToken, ValueTask> onDeviceCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onDeviceCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderKey);

        var deviceCode = await _oauthClient.RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
        var displayUri = string.IsNullOrWhiteSpace(deviceCode.VerificationUriComplete)
            ? deviceCode.VerificationUri
            : deviceCode.VerificationUriComplete!;
        var expiresAt = DateTimeOffset.UtcNow.Add(deviceCode.ExpiresIn);
        await onDeviceCode(
            new XaiDirectDeviceCode(new Uri(displayUri), deviceCode.UserCode, expiresAt),
            cancellationToken).ConfigureAwait(false);

        var token = await _oauthClient.PollDeviceTokenAsync(
            deviceCode,
            options.PollingIntervalOverride,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await PersistTokenAsync(options, token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes CodeAlta-owned cached xAI credentials for a provider.
    /// </summary>
    /// <param name="options">Login options identifying the provider and state root.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when deletion is attempted.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public ValueTask DeleteCredentialAsync(XaiDirectLoginOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderKey);
        return new XaiDirectCredentialStore(options.StateRootPath, options.ProviderKey).DeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Reads cached xAI credential status without returning secret tokens.
    /// </summary>
    /// <param name="options">Login options identifying the provider and state root.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The cached credential status, or <see langword="null"/> when no usable credential is cached.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public async ValueTask<XaiDirectLoginResult?> GetCredentialStatusAsync(
        XaiDirectLoginOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderKey);
        var cache = await new XaiDirectCredentialStore(options.StateRootPath, options.ProviderKey).ReadAsync(cancellationToken).ConfigureAwait(false);
        if (cache is null || string.IsNullOrWhiteSpace(cache.AccessToken))
        {
            return null;
        }

        var expiresAt = cache.ExpiresAtUnixSeconds is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(cache.ExpiresAtUnixSeconds.Value)
            : (DateTimeOffset?)null;
        var baseUri = options.BaseUri ?? XaiDefaults.DefaultApiBaseUri;
        return new XaiDirectLoginResult(baseUri, expiresAt, cache.Scope);
    }

    private async ValueTask<XaiTokenResponse> CompleteBrowserCallbackAsync(
        Uri callbackUri,
        XaiPkce pkce,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var query = XaiOAuthClient.ParseQuery(callbackUri.Query);
        if (query.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
        {
            var description = query.TryGetValue("error_description", out var details) ? details : null;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(description)
                    ? $"xAI OAuth login failed: {error}"
                    : $"xAI OAuth login failed: {error} - {description}");
        }

        if (!query.TryGetValue("state", out var actualState))
        {
            throw new InvalidOperationException("xAI OAuth callback did not include state.");
        }

        XaiOAuthClient.ValidateState(expectedState, actualState);
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("xAI OAuth callback did not include an authorization code.");
        }

        return await _oauthClient.ExchangeAuthorizationCodeAsync(code, pkce.Verifier, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<XaiDirectLoginResult> PersistTokenAsync(
        XaiDirectLoginOptions options,
        XaiTokenResponse token,
        CancellationToken cancellationToken)
    {
        var store = new XaiDirectCredentialStore(options.StateRootPath, options.ProviderKey);
        var cache = new XaiDirectCredentialCache
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAtUnixSeconds = token.ExpiresAtUnixSeconds,
            Scope = token.Scope,
        };
        await store.WriteAsync(cache, cancellationToken).ConfigureAwait(false);
        var baseUri = options.BaseUri ?? XaiDefaults.DefaultApiBaseUri;
        return new XaiDirectLoginResult(baseUri, token.ExpiresAt, token.Scope);
    }

    private static async ValueTask WriteBrowserCallbackResponseAsync(
        HttpListenerContext context,
        string html,
        CancellationToken cancellationToken)
    {
        ApplyCorsHeaders(context);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(html), cancellationToken).ConfigureAwait(false);
        context.Response.Close();
    }

    private const string HtmlSuccess = """
        <!doctype html>
        <html>
          <head>
            <title>CodeAlta - xAI Authorization Successful</title>
            <style>
              body { font-family: system-ui, -apple-system, sans-serif; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0; background: #131010; color: #f1ecec; }
              .container { text-align: center; padding: 2rem; }
              h1 { color: #f1ecec; margin-bottom: 1rem; }
              p { color: #b7b1b1; }
            </style>
          </head>
          <body>
            <div class="container">
              <h1>Authorization Successful</h1>
              <p>You can close this window and return to CodeAlta.</p>
            </div>
            <script>setTimeout(() => window.close(), 2000)</script>
          </body>
        </html>
        """;

    private const string HtmlFailure = """
        <!doctype html>
        <html>
          <head>
            <title>CodeAlta - xAI Authorization Failed</title>
            <style>
              body { font-family: system-ui, -apple-system, sans-serif; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0; background: #131010; color: #f1ecec; }
              .container { text-align: center; padding: 2rem; }
              h1 { color: #fc533a; margin-bottom: 1rem; }
              p { color: #b7b1b1; }
            </style>
          </head>
          <body>
            <div class="container">
              <h1>Authorization Failed</h1>
              <p>Return to CodeAlta and try again.</p>
            </div>
          </body>
        </html>
        """;
}

/// <summary>
/// Options for an xAI direct login operation.
/// </summary>
/// <param name="ProviderKey">The provider key whose credential cache should be updated.</param>
/// <param name="StateRootPath">The CodeAlta state root path.</param>
/// <param name="BaseUri">An optional explicit xAI API base URI override.</param>
public sealed record XaiDirectLoginOptions(
    string ProviderKey,
    string? StateRootPath = null,
    Uri? BaseUri = null)
{
    /// <summary>
    /// Gets an optional polling interval override for tests and controlled hosts.
    /// </summary>
    public TimeSpan? PollingIntervalOverride { get; init; }
}

/// <summary>
/// Non-secret xAI authorization information surfaced to the UI while the browser flow is in progress.
/// </summary>
/// <param name="AuthorizeUri">The URL the user should open in a browser.</param>
public sealed record XaiDirectBrowserAuthorization(Uri AuthorizeUri);

/// <summary>
/// Non-secret xAI device authorization data surfaced to the UI.
/// </summary>
/// <param name="VerificationUri">The xAI URL the user should open.</param>
/// <param name="UserCode">The code the user should enter in the browser.</param>
/// <param name="ExpiresAt">The authorization expiry time.</param>
public sealed record XaiDirectDeviceCode(Uri VerificationUri, string UserCode, DateTimeOffset ExpiresAt);

/// <summary>
/// Non-secret xAI login result metadata.
/// </summary>
/// <param name="BaseUri">The resolved xAI API base URI.</param>
/// <param name="ExpiresAt">The xAI access-token expiry time when provided.</param>
/// <param name="Scope">The granted OAuth scope.</param>
public sealed record XaiDirectLoginResult(Uri BaseUri, DateTimeOffset? ExpiresAt, string? Scope);
