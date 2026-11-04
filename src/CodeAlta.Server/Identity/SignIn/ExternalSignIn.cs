using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CodeAlta.Server.Identity.SignIn;

/// <summary>How a provider proves its accounts.</summary>
public enum ExternalProviderKind
{
    /// <summary>OpenID Connect: discovery, an ID token signed by the provider's keys, a nonce.</summary>
    OpenIdConnect,

    /// <summary>
    /// GitHub's device flow, with the client id of a GitHub App: no redirect, no client secret, no ID token; the account
    /// is read from its API with the user token, which is then dropped.
    /// </summary>
    GitHub,
}

/// <summary>An identity provider as the server uses it for one sign-in.</summary>
/// <param name="Method">The sign-in method (<c>FleetSignInMethods</c>).</param>
/// <param name="Kind">How it proves its accounts.</param>
/// <param name="Authority">The issuer whose discovery document is read (OpenID Connect), or the address of GitHub.</param>
/// <param name="Client">The client of the server at the provider.</param>
/// <param name="Scopes">The scopes asked.</param>
/// <param name="GroupsClaim">The claim of the groups in the ID token.</param>
/// <param name="ApiAddress">The address of GitHub's API, for <see cref="ExternalProviderKind.GitHub"/>.</param>
public sealed record ExternalProvider(string Method, ExternalProviderKind Kind, Uri Authority, SignInClient Client, string Scopes, string GroupsClaim = "groups",
    Uri? ApiAddress = null);

/// <summary>An account an identity provider proved.</summary>
/// <param name="Issuer">Its issuer.</param>
/// <param name="Subject">Its stable id at the issuer.</param>
/// <param name="Email">Its email, or null.</param>
/// <param name="Name">Its name for people, or null.</param>
/// <param name="Groups">Its groups (ids), empty when the provider gives none.</param>
/// <param name="TenantId">The tenant of the account (the <c>tid</c> claim), or null.</param>
/// <param name="EmailVerified">Whether the provider says <paramref name="Email"/> is verified: its <c>email</c> claim with <c>email_verified</c> true.</param>
public sealed record ExternalIdentity(string Issuer, string Subject, string? Email, string? Name, IReadOnlyList<string> Groups, string? TenantId = null,
    bool EmailVerified = false);

/// <summary>The device code a provider gave for a sign-in (RFC 8628).</summary>
/// <param name="DeviceCode">The device code, which only the server holds and polls with.</param>
/// <param name="UserCode">The code the person enters at <paramref name="VerificationUri"/>.</param>
/// <param name="VerificationUri">The page where the person enters it.</param>
/// <param name="ExpiresIn">How long the codes work.</param>
/// <param name="Interval">How many seconds pass at least between two polls.</param>
public sealed record DeviceAuthorization(string DeviceCode, string UserCode, Uri VerificationUri, TimeSpan ExpiresIn, int Interval);

/// <summary>What a poll of a device code answered.</summary>
/// <param name="Pending">Whether the person has not entered the code yet.</param>
/// <param name="Interval">How many seconds pass at least before the next poll (more after a <c>slow_down</c>).</param>
/// <param name="Identity">The account, once the person entered the code; null while pending.</param>
public sealed record DevicePollResult(bool Pending, int Interval, ExternalIdentity? Identity);

/// <summary>A refusal of a provider's answer: the stable code and what went wrong, for the audit log and people.</summary>
public sealed class ExternalSignInException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ExternalSignInException"/> class.</summary>
    /// <param name="code">The stable code (<c>SignInErrorCodes</c>).</param>
    /// <param name="message">What went wrong.</param>
    /// <param name="inner">The cause, or null.</param>
    public ExternalSignInException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;

    /// <summary>Gets the stable code.</summary>
    public string Code { get; }
}

/// <summary>
/// The relying party of the server: it builds the authorization request of a sign-in (authorization code with PKCE, a
/// nonce, the loopback address of the device as redirect), redeems the code the device brings back with its verifier and
/// the client secret it holds, and checks what the provider answered — the issuer, the audience, the nonce, the
/// signature and the lifetime of the ID token. For GitHub it runs the device flow of a GitHub App instead: it asks for a
/// device code, polls GitHub with it, and reads the account with the user token, which it keeps nowhere. Discovery
/// documents and keys are cached per issuer and refreshed when a key is unknown.
/// </summary>
public sealed class ExternalSignInClient : IDisposable
{
    private const string TenantPlaceholder = "{tenantid}";

    private readonly HttpClient _http;
    private readonly SignInOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _configurations = new(StringComparer.Ordinal);
    private readonly JsonWebTokenHandler _tokens = new() { MapInboundClaims = false };

    /// <summary>Initializes a new instance of the <see cref="ExternalSignInClient"/> class.</summary>
    /// <param name="options">The options.</param>
    /// <param name="time">The clock of the lifetimes.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ExternalSignInClient(SignInOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _time = time;
        _http = options.ProviderHandler is null ? new HttpClient() : new HttpClient(options.ProviderHandler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CodeAlta-Server");
    }

    /// <summary>Builds the address of the authorization request.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="redirectUri">The loopback address of the device.</param>
    /// <param name="state">The state.</param>
    /// <param name="nonce">The nonce (OpenID Connect).</param>
    /// <param name="codeChallenge">The S256 challenge of the PKCE verifier.</param>
    /// <param name="cancellationToken">Cancels the read of the discovery document.</param>
    /// <returns>The address to open in the browser.</returns>
    /// <exception cref="ArgumentException">The provider is GitHub, which signs in with its device flow.</exception>
    /// <exception cref="ExternalSignInException">The provider's discovery document cannot be read.</exception>
    public Task<Uri> BuildAuthorizationUrlAsync(ExternalProvider provider, string redirectUri, string state, string nonce, string codeChallenge,
        CancellationToken cancellationToken)
        => BuildAuthorizationUrlAsync(provider, redirectUri, state, nonce, codeChallenge, false, cancellationToken);

    /// <summary>
    /// Builds the address of the authorization request, asking the provider to authenticate the person again when the
    /// sign-in confirms a dangerous action: a session the browser keeps at the provider does not confirm anything.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="redirectUri">The loopback address of the device.</param>
    /// <param name="state">The state.</param>
    /// <param name="nonce">The nonce (OpenID Connect).</param>
    /// <param name="codeChallenge">The S256 challenge of the PKCE verifier.</param>
    /// <param name="requireFreshAuthentication">
    /// Whether the person authenticates again at the provider: <c>prompt=login</c> (Microsoft's, which ignores
    /// <c>max_age=0</c>), <c>max_age=0</c> (Google's, which has no <c>prompt=login</c>), or both (OpenID Connect).
    /// <see cref="RedeemAsync(ExternalProvider, string, string, string, string, DateTimeOffset?, CancellationToken)"/> then checks <c>auth_time</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the read of the discovery document.</param>
    /// <returns>The address to open in the browser.</returns>
    /// <exception cref="ArgumentException">The provider is GitHub, which signs in with its device flow.</exception>
    /// <exception cref="ExternalSignInException">The provider's discovery document cannot be read.</exception>
    public async Task<Uri> BuildAuthorizationUrlAsync(ExternalProvider provider, string redirectUri, string state, string nonce, string codeChallenge,
        bool requireFreshAuthentication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        RequireOpenIdConnect(provider);
        var endpoint = (await GetConfigurationAsync(provider, cancellationToken).ConfigureAwait(false)).AuthorizationEndpoint;
        if (string.IsNullOrEmpty(endpoint)) throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, "The provider names no authorization endpoint.");
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"), new("client_id", provider.Client.ClientId), new("redirect_uri", redirectUri), new("scope", provider.Scopes), new("state", state),
            new("code_challenge", codeChallenge), new("code_challenge_method", "S256"),
        };
        query.Add(new("nonce", nonce));
        var fresh = requireFreshAuthentication ? FreshAuthenticationOf(provider) : FreshAuthentication.None;
        if ((fresh & FreshAuthentication.Prompt) != 0) query.Add(new("prompt", "login"));
        // The browser may hold several accounts of the provider: the person chooses, rather than the one signed in last.
        else if (provider.Method is "microsoft" or "google" or "company") query.Add(new("prompt", "select_account"));
        if ((fresh & FreshAuthentication.MaxAge) != 0) query.Add(new("max_age", "0"));
        var builder = new UriBuilder(endpoint);
        var existing = builder.Query.TrimStart('?');
        builder.Query = (existing.Length > 0 ? existing + "&" : string.Empty) + string.Join('&', query.Select(static pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
        return builder.Uri;
    }

    /// <summary>Redeems a code and checks what the provider answers.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="code">The code the browser brought back.</param>
    /// <param name="redirectUri">The redirect of the authorization request.</param>
    /// <param name="codeVerifier">The PKCE verifier.</param>
    /// <param name="nonce">The nonce the ID token must carry.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The account.</returns>
    /// <exception cref="ArgumentException">The provider is GitHub, which signs in with its device flow.</exception>
    /// <exception cref="ExternalSignInException">The provider refused the code, or its answer does not verify.</exception>
    public Task<ExternalIdentity> RedeemAsync(ExternalProvider provider, string code, string redirectUri, string codeVerifier, string nonce,
        CancellationToken cancellationToken)
        => RedeemAsync(provider, code, redirectUri, codeVerifier, nonce, null, cancellationToken);

    /// <summary>Redeems a code and checks what the provider answers, and that the person authenticated again since a time.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="code">The code the browser brought back.</param>
    /// <param name="redirectUri">The redirect of the authorization request.</param>
    /// <param name="codeVerifier">The PKCE verifier.</param>
    /// <param name="nonce">The nonce the ID token must carry.</param>
    /// <param name="authenticatedSince">
    /// For a sign-in that asked for a fresh authentication, the start of the sign-in: the ID token's <c>auth_time</c> must
    /// not be older (a provider that answered <c>max_age</c> always sends it; Microsoft's sends it only as an optional
    /// claim, and its <c>prompt=login</c> is trusted without it); null for an ordinary sign-in.
    /// </param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The account.</returns>
    /// <exception cref="ArgumentException">The provider is GitHub, which signs in with its device flow.</exception>
    /// <exception cref="ExternalSignInException">The provider refused the code, its answer does not verify, or the person did not authenticate again.</exception>
    public async Task<ExternalIdentity> RedeemAsync(ExternalProvider provider, string code, string redirectUri, string codeVerifier, string nonce,
        DateTimeOffset? authenticatedSince, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        RequireOpenIdConnect(provider);
        var configuration = await GetConfigurationAsync(provider, cancellationToken).ConfigureAwait(false);
        var tokenEndpoint = configuration.TokenEndpoint;
        if (string.IsNullOrEmpty(tokenEndpoint)) throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, "The provider names no token endpoint.");
        RequireTransport(new Uri(tokenEndpoint));

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirectUri), new("client_id", provider.Client.ClientId),
            new("code_verifier", codeVerifier),
        };
        // The secret goes in the form unless the provider takes it in the Authorization header only (Okta's default).
        var methods = configuration.TokenEndpointAuthMethodsSupported;
        var basic = !string.IsNullOrEmpty(provider.Client.ClientSecret) && methods is { Count: > 0 } && !methods.Contains("client_secret_post") && methods.Contains("client_secret_basic");
        if (!string.IsNullOrEmpty(provider.Client.ClientSecret) && !basic) form.Add(new("client_secret", provider.Client.ClientSecret));
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (basic)
        {
            // RFC 6749, section 2.3.1: each part form-encoded, then joined by a colon.
            var credentials = Uri.EscapeDataString(provider.Client.ClientId) + ":" + Uri.EscapeDataString(provider.Client.ClientSecret!);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credentials)));
        }
        using var token = await ReadJsonAsync(request, "token", cancellationToken).ConfigureAwait(false);
        var root = token.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new ExternalSignInException(SignInErrorCodes.CodeRefused, $"The provider refused the code: {error.GetString()}.");
        }

        return await ValidateIdTokenAsync(provider, root, nonce, authenticatedSince, cancellationToken).ConfigureAwait(false);
    }

    // How a provider is asked to authenticate the person again. Entra ID and Microsoft accounts take prompt=login and
    // answer max_age=0 with an auth_time older than the token; Google has no prompt=login and takes max_age; any other
    // OpenID Connect provider takes both, and must then send auth_time (OpenID Connect Core 3.1.2.1, 2).
    private static FreshAuthentication FreshAuthenticationOf(ExternalProvider provider)
        => IsMicrosoftHost(provider.Authority) ? FreshAuthentication.Prompt
            : provider.Method == "google" || provider.Authority.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) ? FreshAuthentication.MaxAge
            : FreshAuthentication.Prompt | FreshAuthentication.MaxAge;

    /// <summary>
    /// Gets whether an issuer is the template of Entra ID's multi-tenant authorities (<c>organizations</c>, <c>common</c>):
    /// <c>https://login.microsoftonline.com/{tenantid}/v2.0</c>, which each token fills with its own tenant (<c>tid</c>).
    /// Only Microsoft's own sign-in names its issuer so; a provider elsewhere that copies the placeholder is not believed.
    /// </summary>
    /// <param name="authority">The authority configured.</param>
    /// <param name="issuer">The issuer its discovery document names.</param>
    /// <returns>Whether the issuer is that template, of the host of the authority.</returns>
    internal static bool IsTenantTemplate(Uri authority, string? issuer)
    {
        if (issuer is null || !IsMicrosoftHost(authority) || authority.Scheme != Uri.UriSchemeHttps) return false;
        var segments = authority.AbsolutePath.Trim('/').Split('/');
        if (segments is not [var tenant, "v2.0"] || !(tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase) || tenant.Equals("common", StringComparison.OrdinalIgnoreCase)))
            return false;
        return string.Equals(issuer, $"https://{authority.Host}/{TenantPlaceholder}/v2.0", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gets whether an address is of Microsoft's sign-in (Entra ID, Microsoft accounts).</summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether its host is <c>login.microsoftonline.com</c>, <c>login.microsoft.com</c> or <c>sts.windows.net</c>.</returns>
    internal static bool IsMicrosoftHost(Uri address)
        => address.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) || address.Host.Equals("login.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || address.Host.Equals("sts.windows.net", StringComparison.OrdinalIgnoreCase);

    [Flags]
    private enum FreshAuthentication
    {
        None = 0,
        Prompt = 1,
        MaxAge = 2,
    }

    /// <summary>Asks GitHub for the device code of a sign-in: the code the person enters, and the device code the server polls with.</summary>
    /// <param name="provider">The provider, of kind <see cref="ExternalProviderKind.GitHub"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The codes.</returns>
    /// <exception cref="ArgumentException">The provider is not GitHub.</exception>
    /// <exception cref="ExternalSignInException">GitHub does not answer, refuses the client (the device flow is off), or answers something unusable.</exception>
    public async Task<DeviceAuthorization> StartDeviceAuthorizationAsync(ExternalProvider provider, CancellationToken cancellationToken)
    {
        RequireGitHub(provider);
        var endpoint = new Uri(Combine(provider.Authority, "login/device/code"));
        RequireTransport(endpoint);
        var form = new List<KeyValuePair<string, string>> { new("client_id", provider.Client.ClientId) };
        if (!string.IsNullOrWhiteSpace(provider.Scopes)) form.Add(new("scope", provider.Scopes));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var answer = await ReadJsonAsync(request, "device code", cancellationToken).ConfigureAwait(false);
        var root = answer.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            var text = error.ValueKind == JsonValueKind.String ? error.GetString() : null;
            throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, text == "device_flow_disabled"
                ? "The GitHub App of this server does not have the device flow on."
                : $"GitHub refused the client of this server: {text}.");
        }

        var deviceCode = TextOf(root, "device_code");
        var userCode = TextOf(root, "user_code");
        if (deviceCode is not { Length: > 0 and <= 512 } || userCode is not { Length: > 0 and <= 64 } || userCode.Any(static c => c is < '!' or > '~')
            || !Uri.TryCreate(TextOf(root, "verification_uri"), UriKind.Absolute, out var page))
            throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, "GitHub answered no usable device code.");
        // The person is sent to GitHub's own page, never to an address elsewhere that an answer would name.
        if (!string.Equals(page.GetLeftPart(UriPartial.Authority), provider.Authority.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, $"GitHub named a page at {page.GetLeftPart(UriPartial.Authority)} to enter the code.");
        RequireTransport(page);
        var expiresIn = Number(root, "expires_in") is { } seconds and > 0 ? TimeSpan.FromSeconds(Math.Min(seconds, 3600)) : TimeSpan.FromMinutes(15);
        return new DeviceAuthorization(deviceCode, userCode, page, expiresIn, Interval(Number(root, "interval"), 5));
    }

    /// <summary>Asks GitHub once whether the person entered the code; once they did, reads their account and drops the user token.</summary>
    /// <param name="provider">The provider, of kind <see cref="ExternalProviderKind.GitHub"/>.</param>
    /// <param name="deviceCode">The device code.</param>
    /// <param name="interval">The interval in force, in seconds.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>Pending with the interval to keep, or the account.</returns>
    /// <exception cref="ArgumentException">The provider is not GitHub.</exception>
    /// <exception cref="ExternalSignInException">
    /// The person refused (<see cref="SignInErrorCodes.Denied"/>), the code expired (<see cref="SignInErrorCodes.Expired"/>),
    /// GitHub refused the device code or the client, or its answer does not read.
    /// </exception>
    public async Task<DevicePollResult> PollDeviceAuthorizationAsync(ExternalProvider provider, string deviceCode, int interval, CancellationToken cancellationToken)
    {
        RequireGitHub(provider);
        ArgumentException.ThrowIfNullOrEmpty(deviceCode);
        var endpoint = new Uri(Combine(provider.Authority, "login/oauth/access_token"));
        RequireTransport(endpoint);
        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", provider.Client.ClientId), new("device_code", deviceCode), new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var answer = await ReadJsonAsync(request, "token", cancellationToken).ConfigureAwait(false);
        var root = answer.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            return (error.ValueKind == JsonValueKind.String ? error.GetString() : null) switch
            {
                "authorization_pending" => new DevicePollResult(true, interval, null),
                // GitHub adds five seconds to the interval and names the new one.
                "slow_down" => new DevicePollResult(true, Interval(Number(root, "interval"), interval + 5), null),
                "expired_token" or "token_expired" => throw new ExternalSignInException(SignInErrorCodes.Expired, "The code expired before it was entered at GitHub."),
                "access_denied" => throw new ExternalSignInException(SignInErrorCodes.Denied, "The sign-in was refused at GitHub."),
                "device_flow_disabled" => throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, "The GitHub App of this server does not have the device flow on."),
                "incorrect_client_credentials" or "unsupported_grant_type" => throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable,
                    $"GitHub refused the client of this server: {error.GetString()}."),
                var other => throw new ExternalSignInException(SignInErrorCodes.CodeRefused, $"GitHub refused the device code: {other}."),
            };
        }

        return new DevicePollResult(false, interval, await ReadGitHubAccountAsync(provider, root, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Checks that the discovery document of an issuer reads, for an owner who configures it.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once it was read.</returns>
    /// <exception cref="ExternalSignInException">It does not read, or names another issuer.</exception>
    public async Task ProbeAsync(ExternalProvider provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var configuration = await GetConfigurationAsync(provider, cancellationToken).ConfigureAwait(false);
        // The issuer is the authority's, or, for the multi-tenant authorities of Entra ID, the template its tokens fill with their tenant.
        if (!string.Equals(configuration.Issuer?.TrimEnd('/'), provider.Authority.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal)
            && !IsTenantTemplate(provider.Authority, configuration.Issuer))
            throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, $"The discovery document of {provider.Authority} names the issuer '{configuration.Issuer}'.");
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<ExternalIdentity> ValidateIdTokenAsync(ExternalProvider provider, JsonElement answer, string nonce, DateTimeOffset? authenticatedSince,
        CancellationToken cancellationToken)
    {
        if (!answer.TryGetProperty("id_token", out var idToken) || idToken.ValueKind != JsonValueKind.String)
            throw new ExternalSignInException(SignInErrorCodes.TokenInvalid, "The provider answered no ID token.");
        var manager = Manager(provider);
        var configuration = await GetConfigurationAsync(provider, cancellationToken).ConfigureAwait(false);
        var result = await ValidateAsync(idToken.GetString()!, provider, configuration).ConfigureAwait(false);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // A key the provider rotated in: read its keys again, once.
            manager.RequestRefresh();
            configuration = await GetConfigurationAsync(provider, cancellationToken).ConfigureAwait(false);
            result = await ValidateAsync(idToken.GetString()!, provider, configuration).ConfigureAwait(false);
        }

        if (!result.IsValid)
        {
            var code = result.Exception switch
            {
                SecurityTokenInvalidAudienceException => SignInErrorCodes.WrongAudience,
                SecurityTokenInvalidIssuerException => SignInErrorCodes.WrongIssuer,
                SecurityTokenExpiredException or SecurityTokenNotYetValidException or SecurityTokenInvalidLifetimeException => SignInErrorCodes.TokenExpired,
                _ => SignInErrorCodes.TokenInvalid,
            };
            throw new ExternalSignInException(code, $"The ID token does not verify: {result.Exception?.GetType().Name ?? "invalid"}.", result.Exception);
        }

        var claims = result.Claims;
        if (!claims.TryGetValue("nonce", out var claimed) || !string.Equals(claimed as string, nonce, StringComparison.Ordinal))
            throw new ExternalSignInException(SignInErrorCodes.WrongNonce, "The ID token does not carry the nonce of this sign-in.");
        if (authenticatedSince is { } since) RequireFreshAuthentication(provider, claims, since);
        var issuer = (string)claims["iss"];
        var subject = Text(claims, "sub") ?? throw new ExternalSignInException(SignInErrorCodes.TokenInvalid, "The ID token has no subject.");
        // A Microsoft account is keyed on its tenant and object id, which stay when its sub (per application) would not.
        if (provider.Method == "microsoft" && Text(claims, "tid") is { } tenant && Text(claims, "oid") is { } objectId) subject = tenant + "/" + objectId;
        var emailClaim = Text(claims, "email");
        var email = emailClaim ?? Text(claims, "preferred_username");
        // Only the email claim, with email_verified true, proves a domain; a preferred_username never does.
        var verified = emailClaim is not null && claims.TryGetValue("email_verified", out var flag)
            && (flag is true || flag is string text && string.Equals(text, "true", StringComparison.OrdinalIgnoreCase));
        // The name, else its parts (the optional given_name and family_name claims, which a Microsoft personal account without
        // a display name may still send), else the email.
        var parts = string.Join(' ', new[] { Text(claims, "given_name"), Text(claims, "family_name") }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        return new ExternalIdentity(issuer, subject, email, Text(claims, "name") ?? (parts.Length > 0 ? parts : null) ?? email, Groups(claims, provider.GroupsClaim),
            Text(claims, "tid"), verified);
    }

    // A token issued now proves a session, not a person who just authenticated: the provider says when they did.
    private static void RequireFreshAuthentication(ExternalProvider provider, IDictionary<string, object> claims, DateTimeOffset since)
    {
        if (!claims.TryGetValue("auth_time", out var value) || !TryReadSeconds(value, out var seconds))
        {
            if ((FreshAuthenticationOf(provider) & FreshAuthentication.MaxAge) == 0) return;
            throw new ExternalSignInException(SignInErrorCodes.NotFresh, "The provider did not say when you signed in: sign in again.");
        }

        if (DateTimeOffset.FromUnixTimeSeconds(seconds) < since - FreshnessSkew)
            throw new ExternalSignInException(SignInErrorCodes.NotFresh, "The provider used a sign-in of before: sign in again there.");
    }

    // The clocks of the provider and of the server differ by a little.
    private static readonly TimeSpan FreshnessSkew = TimeSpan.FromMinutes(2);

    private static bool TryReadSeconds(object value, out long seconds)
    {
        switch (value)
        {
            case long number: seconds = number; return true;
            case int number: seconds = number; return true;
            case double number when number is > 0 and < long.MaxValue: seconds = (long)number; return true;
            case string text when long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed): seconds = parsed; return true;
            default: seconds = 0; return false;
        }
    }

    private Task<TokenValidationResult> ValidateAsync(string token, ExternalProvider provider, OpenIdConnectConfiguration configuration)
    {
        var parameters = new TokenValidationParameters
        {
            ValidAudience = provider.Client.ClientId,
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSsaPssSha256],
            ClockSkew = TimeSpan.FromMinutes(2),
            // The clock of the server, not the one of the library: the same one decides every lifetime of the server.
            LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = _time.GetUtcNow().UtcDateTime;
                return expires is { } end && end.ToUniversalTime() + parameters.ClockSkew >= now && (notBefore is not { } start || start.ToUniversalTime() - parameters.ClockSkew <= now);
            },
            // The common endpoint of Microsoft names its issuer with a placeholder for the tenant of each account.
            IssuerValidator = (issuer, securityToken, _) =>
            {
                var expected = configuration.Issuer ?? provider.Authority.AbsoluteUri;
                // The same rule as when the authority was set (ProbeAsync): Microsoft's template only, filled with the token's own tenant.
                if (IsTenantTemplate(provider.Authority, configuration.Issuer) && securityToken is JsonWebToken jwt && jwt.TryGetPayloadValue<string>("tid", out var tid)
                    && !string.IsNullOrEmpty(tid) && tid.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    expected = expected.Replace(TenantPlaceholder, tid, StringComparison.OrdinalIgnoreCase);
                }

                if (!string.Equals(issuer, expected, StringComparison.Ordinal)) throw new SecurityTokenInvalidIssuerException($"The issuer '{issuer}' is not '{expected}'.");
                return issuer;
            },
        };
        return _tokens.ValidateTokenAsync(token, parameters);
    }

    // The user token is used for this one read and kept nowhere: the account is its numeric id and its display name only,
    // never its login (which its person can change, and give to someone else).
    private async Task<ExternalIdentity> ReadGitHubAccountAsync(ExternalProvider provider, JsonElement answer, CancellationToken cancellationToken)
    {
        if (!answer.TryGetProperty("access_token", out var accessToken) || accessToken.ValueKind != JsonValueKind.String || accessToken.GetString() is not { Length: > 0 } token)
            throw new ExternalSignInException(SignInErrorCodes.TokenInvalid, "GitHub answered no access token.");
        var api = provider.ApiAddress ?? throw new InvalidOperationException("A GitHub provider needs the address of its API.");
        var address = new Uri(Combine(api, "user"));
        RequireTransport(address);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var user = await ReadJsonAsync(request, "user", cancellationToken).ConfigureAwait(false);
        var root = user.RootElement;
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number <= 0)
            throw new ExternalSignInException(SignInErrorCodes.TokenInvalid, "GitHub did not give the id of the account.");
        var name = TextOf(root, "name") is { Length: > 0 } named ? named.Trim() : null;
        return new ExternalIdentity(provider.Authority.GetLeftPart(UriPartial.Authority), number.ToString(CultureInfo.InvariantCulture), null, name, []);
    }

    private static void RequireOpenIdConnect(ExternalProvider provider)
    {
        if (provider.Kind != ExternalProviderKind.OpenIdConnect) throw new ArgumentException("GitHub signs in with its device flow, not with a redirect.", nameof(provider));
    }

    private static void RequireGitHub(ExternalProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Kind != ExternalProviderKind.GitHub) throw new ArgumentException("Only GitHub signs in with a device code.", nameof(provider));
    }

    private static string? TextOf(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
        ? number : null;

    // At least a second, at most a minute: an answer never makes the device poll in a loop, nor wait for ever.
    private static int Interval(int? given, int fallback) => Math.Clamp(given ?? fallback, 1, 60);

    private async Task<JsonDocument> ReadJsonAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, $"The provider does not answer at its {what} endpoint.", exception);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }
            catch (JsonException exception)
            {
                throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, $"The provider's {what} endpoint answered something else than JSON.", exception);
            }

            if (!response.IsSuccessStatusCode && !document.RootElement.TryGetProperty("error", out _))
            {
                document.Dispose();
                throw new ExternalSignInException(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized ? SignInErrorCodes.CodeRefused
                    : SignInErrorCodes.ProviderUnavailable, $"The provider's {what} endpoint answered {(int)response.StatusCode}.");
            }

            return document;
        }
    }

    private async Task<OpenIdConnectConfiguration> GetConfigurationAsync(ExternalProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            return await Manager(provider).GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or HttpRequestException or ArgumentException)
        {
            throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, $"The discovery document of {provider.Authority} cannot be read.", exception);
        }
    }

    private ConfigurationManager<OpenIdConnectConfiguration> Manager(ExternalProvider provider)
    {
        RequireTransport(provider.Authority);
        var metadata = provider.Authority.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration";
        return _configurations.GetOrAdd(metadata, address => new ConfigurationManager<OpenIdConnectConfiguration>(address, new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(_http) { RequireHttps = !IsLoopbackHttp(new Uri(address)) })
        {
            AutomaticRefreshInterval = TimeSpan.FromHours(12),
            RefreshInterval = TimeSpan.FromMinutes(1),
        });
    }

    private void RequireTransport(Uri address)
    {
        if (address.Scheme == Uri.UriSchemeHttps || IsLoopbackHttp(address)) return;
        throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, $"The provider at {address.GetLeftPart(UriPartial.Authority)} is not reached over https://.");
    }

    private bool IsLoopbackHttp(Uri address) => _options.AllowLoopbackHttpProviders && address.Scheme == Uri.UriSchemeHttp && address.IsLoopback;

    private static string Combine(Uri address, string path) => address.AbsoluteUri.TrimEnd('/') + "/" + path;

    private static string? Text(IDictionary<string, object> claims, string name) => claims.TryGetValue(name, out var value) && value is string text && text.Length > 0 ? text : null;

    private static IReadOnlyList<string> Groups(IDictionary<string, object> claims, string name)
    {
        if (!claims.TryGetValue(name, out var value)) return [];
        return value switch
        {
            string single => [single],
            IEnumerable<object> many => [.. many.Select(static item => item?.ToString()).Where(static item => !string.IsNullOrEmpty(item)).Take(500)!],
            _ => [],
        };
    }
}
