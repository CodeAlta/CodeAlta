using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CodeAlta.Server.Tests.SignIn;

/// <summary>An account of the fake provider.</summary>
/// <param name="Subject">Its subject.</param>
/// <param name="Email">Its email.</param>
/// <param name="Name">Its name.</param>
/// <param name="Groups">Its groups.</param>
/// <param name="GitHubId">Its numeric id at <see cref="FakeGitHubDeviceFlow"/>.</param>
/// <param name="EmailVerified">Whether its ID token says the email is verified.</param>
/// <param name="TenantId">Its tenant (the <c>tid</c> claim), or null for none.</param>
internal sealed record FakeAccount(string Subject, string Email, string Name, IReadOnlyList<string> Groups, long GitHubId = 0, bool EmailVerified = true, string? TenantId = null);

/// <summary>
/// An OpenID Connect provider in the process of a test, on the loopback: discovery, an authorization endpoint that
/// signs the current account in at once and redirects with a code, a token endpoint that checks the client, the
/// redirect, the PKCE verifier and that a code is used once, and ID tokens signed with a test RSA key and carrying
/// groups. Switches make it answer wrong tokens (audience, issuer, nonce, expiry, key) for the refusals.
/// </summary>
internal sealed class FakeOidcProvider : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Grant> _codes = new(StringComparer.Ordinal);

    private FakeOidcProvider(WebApplication app, string clientId, string? clientSecret, Func<DateTimeOffset> now)
    {
        _app = app;
        ClientId = clientId;
        ClientSecret = clientSecret;
        Now = now;
    }

    /// <summary>Gets the issuer: the address of the provider.</summary>
    public Uri Issuer { get; private set; } = null!;

    /// <summary>Gets the client id it accepts.</summary>
    public string ClientId { get; }

    /// <summary>Gets the client secret it requires, or null for a public client.</summary>
    public string? ClientSecret { get; }

    /// <summary>Gets or sets the account the next authorization signs in.</summary>
    public FakeAccount Account { get; set; } = new("user-1", "alex@contoso.example", "Alex Martin", ["g-engineering", "g-admins"], 4242);

    /// <summary>Gets or sets the audience of the next ID tokens; null for the client id.</summary>
    public string? Audience { get; set; }

    /// <summary>Gets or sets the issuer of the next ID tokens; null for the provider's.</summary>
    public string? TokenIssuer { get; set; }

    /// <summary>Gets or sets the nonce of the next ID tokens; null for the one of the request.</summary>
    public string? Nonce { get; set; }

    /// <summary>Gets or sets how long the next ID tokens live; negative for an expired one.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets whether the next ID tokens are signed with a key the provider does not publish.</summary>
    public bool SignWithUnknownKey { get; set; }

    /// <summary>Gets or sets whether the token endpoint takes the client secret in the Authorization header only, as Okta does by default.</summary>
    public bool BasicAuthenticationOnly { get; set; }

    /// <summary>Gets the authorization requests it received.</summary>
    public ConcurrentQueue<IReadOnlyDictionary<string, string>> Authorizations { get; } = new();

    /// <summary>Gets how many token requests it refused.</summary>
    public int RefusedTokens => _refused;

    private int _refused;

    private Func<DateTimeOffset> Now { get; }

    /// <summary>Starts a provider on the loopback.</summary>
    /// <param name="clientId">The client id it accepts.</param>
    /// <param name="clientSecret">The secret it requires, or null.</param>
    /// <param name="now">The clock of its tokens; null for the system clock.</param>
    /// <returns>The provider.</returns>
    public static async Task<FakeOidcProvider> StartAsync(string clientId = "codealta-test-client", string? clientSecret = null, Func<DateTimeOffset>? now = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var provider = new FakeOidcProvider(app, clientId, clientSecret, now ?? (static () => DateTimeOffset.UtcNow));
        provider.Map();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        provider.Issuer = new Uri(address.TrimEnd('/'));
        return provider;
    }

    /// <summary>Follows an authorization address like a browser whose person signs in at once: the redirect it answers, with the code and the state.</summary>
    /// <param name="authorizationUrl">The address.</param>
    /// <returns>The address of the redirect.</returns>
    public static async Task<Uri> AuthorizeAsync(string authorizationUrl)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await http.GetAsync(authorizationUrl);
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, response.StatusCode, await response.Content.ReadAsStringAsync());
        return response.Headers.Location!;
    }

    /// <summary>Reads a parameter of the query of an address.</summary>
    /// <param name="address">The address.</param>
    /// <param name="name">The parameter.</param>
    /// <returns>Its value, or null.</returns>
    public static string? Query(Uri address, string name)
        => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(address.Query).TryGetValue(name, out var value) ? value.ToString() : null;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _key.Dispose();
        _otherKey.Dispose();
    }

    private void Map()
    {
        _app.MapGet("/.well-known/openid-configuration", () => Results.Text(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["issuer"] = Issuer.AbsoluteUri.TrimEnd('/'),
            ["authorization_endpoint"] = Issuer.AbsoluteUri.TrimEnd('/') + "/authorize",
            ["token_endpoint"] = Issuer.AbsoluteUri.TrimEnd('/') + "/token",
            ["jwks_uri"] = Issuer.AbsoluteUri.TrimEnd('/') + "/jwks",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = BasicAuthenticationOnly ? new[] { "client_secret_basic" } : new[] { "client_secret_post", "client_secret_basic" },
        }), "application/json"));
        _app.MapGet("/jwks", () =>
        {
            var parameters = _key.ExportParameters(false);
            return Results.Text(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["keys"] = new[]
                {
                    new Dictionary<string, string> { ["kty"] = "RSA", ["use"] = "sig", ["alg"] = "RS256", ["kid"] = "test-key", ["n"] = Base64Url.EncodeToString(parameters.Modulus),
                        ["e"] = Base64Url.EncodeToString(parameters.Exponent) },
                },
            }), "application/json");
        });
        _app.MapGet("/authorize", (Func<HttpContext, IResult>)Authorize);
        _app.MapPost("/token", (Func<HttpContext, Task<IResult>>)Token);
    }

    private IResult Authorize(HttpContext context)
    {
        var query = context.Request.Query.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToString(), StringComparer.Ordinal);
        Authorizations.Enqueue(query);
        if (query.GetValueOrDefault("client_id") != ClientId) return Results.BadRequest("unknown client");
        if (query.GetValueOrDefault("code_challenge_method") != "S256" || string.IsNullOrEmpty(query.GetValueOrDefault("code_challenge"))) return Results.BadRequest("PKCE needed");
        var code = "code-" + Guid.NewGuid().ToString("N");
        _codes[code] = new Grant(Account, query["redirect_uri"], query["code_challenge"], query.GetValueOrDefault("nonce"));
        var target = query["redirect_uri"] + (query["redirect_uri"].Contains('?') ? "&" : "?") + "code=" + Uri.EscapeDataString(code) + "&state="
            + Uri.EscapeDataString(query.GetValueOrDefault("state") ?? string.Empty);
        return Results.Redirect(target);
    }

    private async Task<IResult> Token(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        IResult Refuse(string error)
        {
            Interlocked.Increment(ref _refused);
            return Results.Text(JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = error }), "application/json", statusCode: 400);
        }

        if (form["grant_type"] != "authorization_code" || form["client_id"] != ClientId) return Refuse("invalid_client");
        if (BasicAuthenticationOnly)
        {
            var header = context.Request.Headers.Authorization.ToString();
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(ClientId) + ":" + Uri.EscapeDataString(ClientSecret ?? string.Empty)));
            if (header != expected || form.ContainsKey("client_secret")) return Refuse("invalid_client");
        }
        else if (ClientSecret is not null && form["client_secret"] != ClientSecret)
        {
            return Refuse("invalid_client");
        }
        // A code works once.
        if (!_codes.TryRemove(form["code"].ToString(), out var grant)) return Refuse("invalid_grant");
        if (form["redirect_uri"] != grant.RedirectUri) return Refuse("invalid_grant");
        var verifier = form["code_verifier"].ToString();
        if (Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != grant.Challenge) return Refuse("invalid_grant");

        var answer = new Dictionary<string, object>
        {
            ["access_token"] = "at-" + Guid.NewGuid().ToString("N"), ["token_type"] = "Bearer", ["expires_in"] = 300, ["id_token"] = IdToken(grant),
        };
        return Results.Text(JsonSerializer.Serialize(answer), "application/json");
    }

    private string IdToken(Grant grant)
    {
        var now = Now();
        var claims = new Dictionary<string, object>
        {
            ["sub"] = grant.Account.Subject, ["email"] = grant.Account.Email, ["name"] = grant.Account.Name, ["groups"] = grant.Account.Groups.ToArray(),
            ["email_verified"] = grant.Account.EmailVerified,
        };
        if (grant.Account.TenantId is { } tenant) claims["tid"] = tenant;
        if ((Nonce ?? grant.Nonce) is { } nonce) claims["nonce"] = nonce;
        var expires = now + Lifetime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = TokenIssuer ?? Issuer.AbsoluteUri.TrimEnd('/'),
            Audience = Audience ?? ClientId,
            Claims = claims,
            IssuedAt = (Lifetime < TimeSpan.Zero ? expires - TimeSpan.FromMinutes(5) : now).UtcDateTime,
            NotBefore = (Lifetime < TimeSpan.Zero ? expires - TimeSpan.FromMinutes(5) : now).UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(SignWithUnknownKey ? _otherKey : _key) { KeyId = SignWithUnknownKey ? "other-key" : "test-key" },
                SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private sealed record Grant(FakeAccount Account, string RedirectUri, string Challenge, string? Nonce);
}
