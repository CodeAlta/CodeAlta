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

    /// <summary>GitHub's OAuth: no ID token, the account is read from its API with the access token.</summary>
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
public sealed record ExternalIdentity(string Issuer, string Subject, string? Email, string? Name, IReadOnlyList<string> Groups);

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
/// signature and the lifetime of the ID token, or GitHub's account read with its access token. Discovery documents and
/// keys are cached per issuer and refreshed when a key is unknown.
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
    /// <exception cref="ExternalSignInException">The provider's discovery document cannot be read.</exception>
    public async Task<Uri> BuildAuthorizationUrlAsync(ExternalProvider provider, string redirectUri, string state, string nonce, string codeChallenge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var endpoint = provider.Kind == ExternalProviderKind.GitHub
            ? Combine(provider.Authority, "login/oauth/authorize")
            : (await GetConfigurationAsync(provider, cancellationToken).ConfigureAwait(false)).AuthorizationEndpoint;
        if (string.IsNullOrEmpty(endpoint)) throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, "The provider names no authorization endpoint.");
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"), new("client_id", provider.Client.ClientId), new("redirect_uri", redirectUri), new("scope", provider.Scopes), new("state", state),
            new("code_challenge", codeChallenge), new("code_challenge_method", "S256"),
        };
        if (provider.Kind == ExternalProviderKind.OpenIdConnect) query.Add(new("nonce", nonce));
        if (provider.Method is "microsoft" or "google") query.Add(new("prompt", "select_account"));
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
    /// <exception cref="ExternalSignInException">The provider refused the code, or its answer does not verify.</exception>
    public async Task<ExternalIdentity> RedeemAsync(ExternalProvider provider, string code, string redirectUri, string codeVerifier, string nonce,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var tokenEndpoint = provider.Kind == ExternalProviderKind.GitHub
            ? Combine(provider.Authority, "login/oauth/access_token")
            : (await GetConfigurationAsync(provider, cancellationToken).ConfigureAwait(false)).TokenEndpoint;
        if (string.IsNullOrEmpty(tokenEndpoint)) throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, "The provider names no token endpoint.");
        RequireTransport(new Uri(tokenEndpoint));

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirectUri), new("client_id", provider.Client.ClientId),
            new("code_verifier", codeVerifier),
        };
        if (!string.IsNullOrEmpty(provider.Client.ClientSecret)) form.Add(new("client_secret", provider.Client.ClientSecret));
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var token = await ReadJsonAsync(request, "token", cancellationToken).ConfigureAwait(false);
        var root = token.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new ExternalSignInException(SignInErrorCodes.CodeRefused, $"The provider refused the code: {error.GetString()}.");
        }

        return provider.Kind == ExternalProviderKind.GitHub
            ? await ReadGitHubAccountAsync(provider, root, cancellationToken).ConfigureAwait(false)
            : await ValidateIdTokenAsync(provider, root, nonce, cancellationToken).ConfigureAwait(false);
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
        if (!string.Equals(configuration.Issuer?.TrimEnd('/'), provider.Authority.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal))
            throw new ExternalSignInException(SignInErrorCodes.ProviderUnavailable, $"The discovery document of {provider.Authority} names the issuer '{configuration.Issuer}'.");
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<ExternalIdentity> ValidateIdTokenAsync(ExternalProvider provider, JsonElement answer, string nonce, CancellationToken cancellationToken)
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
        var issuer = (string)claims["iss"];
        var subject = Text(claims, "sub") ?? throw new ExternalSignInException(SignInErrorCodes.TokenInvalid, "The ID token has no subject.");
        // A Microsoft account is keyed on its tenant and object id, which stay when its sub (per application) would not.
        if (provider.Method == "microsoft" && Text(claims, "tid") is { } tenant && Text(claims, "oid") is { } objectId) subject = tenant + "/" + objectId;
        var email = Text(claims, "email") ?? Text(claims, "preferred_username");
        return new ExternalIdentity(issuer, subject, email, Text(claims, "name") ?? email, Groups(claims, provider.GroupsClaim));
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
                if (expected.Contains(TenantPlaceholder, StringComparison.Ordinal) && securityToken is JsonWebToken jwt && jwt.TryGetPayloadValue<string>("tid", out var tid)
                    && !string.IsNullOrEmpty(tid))
                {
                    expected = expected.Replace(TenantPlaceholder, tid, StringComparison.Ordinal);
                }

                if (!string.Equals(issuer, expected, StringComparison.Ordinal)) throw new SecurityTokenInvalidIssuerException($"The issuer '{issuer}' is not '{expected}'.");
                return issuer;
            },
        };
        return _tokens.ValidateTokenAsync(token, parameters);
    }

    private async Task<ExternalIdentity> ReadGitHubAccountAsync(ExternalProvider provider, JsonElement answer, CancellationToken cancellationToken)
    {
        if (!answer.TryGetProperty("access_token", out var accessToken) || accessToken.ValueKind != JsonValueKind.String)
            throw new ExternalSignInException(SignInErrorCodes.TokenInvalid, "GitHub answered no access token.");
        var api = provider.ApiAddress ?? throw new InvalidOperationException("A GitHub provider needs the address of its API.");
        using var request = new HttpRequestMessage(HttpMethod.Get, Combine(api, "user"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.GetString());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var user = await ReadJsonAsync(request, "user", cancellationToken).ConfigureAwait(false);
        var root = user.RootElement;
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number)
            throw new ExternalSignInException(SignInErrorCodes.TokenInvalid, "GitHub did not give the id of the account.");
        var login = root.TryGetProperty("login", out var value) ? value.GetString() : null;
        var name = root.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : login;
        var email = root.TryGetProperty("email", out var mail) && mail.ValueKind == JsonValueKind.String ? mail.GetString() : null;
        return new ExternalIdentity(provider.Authority.GetLeftPart(UriPartial.Authority), id.GetInt64().ToString(CultureInfo.InvariantCulture), email,
            name ?? login, []);
    }

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
