using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent.OpenAI.Codex;

namespace CodeAlta.Tests;

[TestClass]
public sealed class OpenAICodexSubscriptionAuthTests
{
    private const string IssuedClientId = "oaiapp_codealta_test";
    private const string HostId = "urn:uuid:17d095df-b7fd-43cf-8c6a-d1ad3539a509";

    [TestMethod]
    public async Task FileCredentialStore_RoundTripsCredentialAndDeletes()
    {
        using var temp = TempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var credential = CreateCredential();
        await store.SaveAsync("codex/subscription", credential);
        var path = Directory.GetFiles(temp.Path, "*.credential", SearchOption.AllDirectories).Single();
        var raw = await File.ReadAllTextAsync(path);
        Assert.IsFalse(raw.Contains("access-secret", StringComparison.Ordinal));
        Assert.IsFalse(raw.Contains("refresh-secret", StringComparison.Ordinal));
        StringAssert.StartsWith(raw, OperatingSystem.IsWindows() ? "dpapi:" : "plain64:");
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        var loaded = await store.LoadAsync("codex/subscription");
        Assert.IsNotNull(loaded);
        Assert.AreEqual(IssuedClientId, loaded.ClientId);
        Assert.AreEqual("subject", loaded.Subject);
        Assert.AreEqual(HostId, loaded.AgentHostId);
        Assert.AreEqual("user@example.test", loaded.Email);
        Assert.AreEqual("refresh-secret", loaded.RefreshToken);
        CollectionAssert.AreEqual(credential.Scopes, loaded.Scopes);
        await store.DeleteAsync("codex/subscription");
        Assert.IsNull(await store.LoadAsync("codex/subscription"));
    }

    [TestMethod]
    public void SecretRedactor_RedactsCredentialSecretsAndBearerTokens()
    {
        var credential = CreateCredential();
        credential.IdToken = "id-secret";
        var redacted = OpenAICodexSubscriptionSecretRedactor.Redact("Authorization: Bearer access-secret refresh-secret id-secret", credential);
        Assert.IsFalse(redacted.Contains("access-secret", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("refresh-secret", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("id-secret", StringComparison.Ordinal));
        StringAssert.Contains(redacted, OpenAICodexSubscriptionSecretRedactor.Redacted);
    }

    [TestMethod]
    public void SecretRedactor_RedactsOAuthCodesPkceVerifiersAndJwtPayloads()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJlbWFpbCI6InVzZXJAZXhhbXBsZS5jb20iLCJhY2NvdW50IjoiYWNjdF8xMjMifQ.signature123";
        var redacted = OpenAICodexSubscriptionSecretRedactor.Redact("callback?code=oauth-code-secret&state=ok code_verifier=pkce-secret " + jwt);
        Assert.IsFalse(redacted.Contains("oauth-code-secret", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("pkce-secret", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains(jwt, StringComparison.Ordinal));
    }

    [TestMethod]
    public void OAuthClient_BuildAuthorizeUriIncludesRequiredParameters()
    {
        var uri = OpenAICodexSubscriptionOAuthClient.BuildAuthorizeUri(new("verifier", "challenge"), "state", "nonce", HostId, OpenAICodexSubscriptionOAuthDefaults.RedirectUri);
        var query = ParseQuery(uri.Query);
        Assert.AreEqual("https://auth.openai.com/api/accounts/authorize", uri.GetLeftPart(UriPartial.Path));
        Assert.AreEqual("dynamic_agent_client", query["client_id"]);
        Assert.AreEqual("CodeAlta", query["agent_name_hint"]);
        Assert.AreEqual(HostId, query["ext_agent_host_id"]);
        Assert.AreEqual("http://127.0.0.1:1455/auth/callback", query["redirect_uri"]);
        Assert.AreEqual("https://api.openai.com/v1", query["resource"]);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.Scope, query["scope"]);
        Assert.AreEqual("S256", query["code_challenge_method"]);
        Assert.AreEqual("challenge", query["code_challenge"]);
        Assert.AreEqual("nonce", query["nonce"]);
        Assert.AreEqual("state", query["state"]);
        Assert.IsFalse(query.ContainsKey("codex_cli_simplified_flow"));
    }

    [TestMethod]
    public async Task LoginManager_StartsListenerAndReusesRegistrationWithoutExposingIdToken()
    {
        using var temp = TempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var saved = CreateCredential();
        saved.IdToken = "retained-secret";
        await store.SaveAsync("codex", saved);
        using var http = new HttpClient(new QueueHttpMessageHandler());
        var manager = new OpenAICodexSubscriptionLoginManager(store, new(http), "codex");
        using var first = await manager.BeginBrowserLoginAsync(HostId);
        using var second = await manager.BeginBrowserLoginAsync(HostId);
        Assert.IsTrue(first.Listener.IsListening);
        var query = ParseQuery(first.AuthorizeUri.Query);
        Assert.AreEqual(IssuedClientId, query["client_id"]);
        Assert.IsFalse(query.ContainsKey("agent_name_hint"));
        Assert.IsFalse(query.ContainsKey("id_token_hint"));
        Assert.AreEqual(saved.Email, query["login_hint"]);
        Assert.AreNotEqual(first.State, second.State);
        Assert.AreNotEqual(first.Nonce, second.Nonce);
        Assert.AreNotEqual(first.Pkce.Verifier, second.Pkce.Verifier);
    }

    [TestMethod]
    public async Task LoginManager_ExchangesIssuedClientAndValidatesIdentityBeforeSaving()
    {
        using var temp = TempDirectory.Create();
        using var signer = new IdTokenSigner();
        using var handler = new QueueHttpMessageHandler();
        using var http = new HttpClient(handler);
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var manager = new OpenAICodexSubscriptionLoginManager(store, new(http), "codex");
        using var login = await manager.BeginBrowserLoginAsync(HostId);
        handler.Enqueue(TokenResponse(signer.Sign(login.Nonce)), JsonResponse(signer.Jwks));
        using var browser = new HttpClient();
        var wait = manager.WaitForBrowserCallbackAsync(login).AsTask();
        var browserResponse = await browser.GetAsync(login.RedirectUri + $"?code=auth-code&state={login.State}&client_id={IssuedClientId}");
        Assert.AreEqual(HttpStatusCode.OK, browserResponse.StatusCode);
        var credential = await wait;
        Assert.AreEqual("subject", credential.Subject);
        Assert.AreEqual("user@example.test", credential.AccountLabel);
        Assert.AreEqual(HostId, credential.AgentHostId);
        Assert.AreEqual(IssuedClientId, (await store.LoadAsync("codex"))?.ClientId);
        var form = ParseQuery(handler.Requests[0].Body);
        Assert.AreEqual(IssuedClientId, form["client_id"]);
        Assert.AreEqual(login.RedirectUri, form["redirect_uri"]);
        Assert.AreEqual(login.Pkce.Verifier, form["code_verifier"]);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.Resource, form["resource"]);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.TokenEndpoint, handler.Requests[0].Uri?.AbsoluteUri);
        Assert.IsFalse(form.ContainsKey("client_secret"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.CompleteBrowserLoginAsync(login, new(login.RedirectUri + "?state=" + login.State)).AsTask());
    }

    [TestMethod]
    [DataRow("code=x&state=wrong&client_id=oaiapp_test")]
    [DataRow("code=x")]
    [DataRow("code=x&state={state}")]
    [DataRow("code=x&state={state}&client_id=dynamic_agent_client")]
    [DataRow("error=access_denied&state={state}")]
    [DataRow("code=x&state={state}&state={state}&client_id=oaiapp_test")]
    public async Task LoginManager_RejectsUnverifiedCallbacksWithoutExchange(string query)
    {
        using var temp = TempDirectory.Create();
        using var handler = new QueueHttpMessageHandler();
        using var http = new HttpClient(handler);
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var manager = new OpenAICodexSubscriptionLoginManager(store, new(http), "codex");
        using var login = await manager.BeginBrowserLoginAsync(HostId);
        await Assert.ThrowsAsync<Exception>(() => manager.CompleteBrowserLoginAsync(login, new(login.RedirectUri + "?" + query.Replace("{state}", login.State))).AsTask());
        Assert.AreEqual(0, handler.Requests.Count);
        Assert.IsNull(await store.LoadAsync("codex"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoginManager_ReauthorizationRejectsDifferentClientOrSubject(bool differentSubject)
    {
        using var temp = TempDirectory.Create();
        using var signer = new IdTokenSigner();
        using var handler = new QueueHttpMessageHandler();
        using var http = new HttpClient(handler);
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await store.SaveAsync("codex", CreateCredential());
        var manager = new OpenAICodexSubscriptionLoginManager(store, new(http), "codex");
        using var login = await manager.BeginBrowserLoginAsync(HostId);
        handler.Enqueue(TokenResponse(signer.Sign(login.Nonce, "sub", "other-subject")), JsonResponse(signer.Jwks));
        var query = $"?code=x&state={login.State}" + (differentSubject ? "" : "&client_id=oaiapp_other");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.CompleteBrowserLoginAsync(login, new(login.RedirectUri + query)).AsTask());
        Assert.AreEqual("access-secret", (await store.LoadAsync("codex"))?.AccessToken);
        Assert.AreEqual(differentSubject ? 2 : 0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoginManager_ReturningCallbackMayOmitClientId()
    {
        using var temp = TempDirectory.Create();
        using var signer = new IdTokenSigner();
        using var handler = new QueueHttpMessageHandler();
        using var http = new HttpClient(handler);
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await store.SaveAsync("codex", CreateCredential());
        var manager = new OpenAICodexSubscriptionLoginManager(store, new(http), "codex");
        using var login = await manager.BeginBrowserLoginAsync(HostId);
        handler.Enqueue(TokenResponse(signer.Sign(login.Nonce)), JsonResponse(signer.Jwks));
        var credential = await manager.CompleteBrowserLoginAsync(login, new(login.RedirectUri + $"?code=x&state={login.State}"));
        Assert.AreEqual(IssuedClientId, credential.ClientId);
    }

    [TestMethod]
    public async Task LoginManager_InvalidGrantRetainsPendingClientForFreshSignIn()
    {
        using var temp = TempDirectory.Create();
        using var signer = new IdTokenSigner();
        using var handler = new QueueHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_grant"}"""),
        });
        using var http = new HttpClient(handler);
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var manager = new OpenAICodexSubscriptionLoginManager(store, new(http), "codex");
        using var first = await manager.BeginBrowserLoginAsync(HostId);
        await Assert.ThrowsExactlyAsync<OpenAICodexSubscriptionTokenException>(() => manager.CompleteBrowserLoginAsync(
            first, new(first.RedirectUri + $"?code=expired-code&state={first.State}&client_id={IssuedClientId}")).AsTask());

        var pending = await store.LoadAsync("codex");
        Assert.IsNotNull(pending);
        Assert.AreEqual(IssuedClientId, pending.ClientId);
        Assert.AreEqual(HostId, pending.AgentHostId);
        Assert.IsFalse(OpenAICodexSubscriptionLoginManager.IsRegistration(pending));
        Assert.IsNull(pending.Subject);
        Assert.AreEqual(string.Empty, pending.AccessToken);
        Assert.IsNull(pending.RefreshToken);
        Assert.IsNull(pending.IdToken);
        Assert.IsFalse(pending.HasPlanUsagePermission);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new OpenAICodexSubscriptionAuthManager(store, new(http), "codex").GetAccessTokenAsync().AsTask());

        // The UI creates a new manager for each action; recovery must survive that and a restart.
        var retryManager = new OpenAICodexSubscriptionLoginManager(new FileOpenAICodexSubscriptionCredentialStore(temp.Path), new(http), "codex");
        using var retry = await retryManager.BeginBrowserLoginAsync(HostId);
        var query = ParseQuery(retry.AuthorizeUri.Query);
        Assert.AreEqual(IssuedClientId, query["client_id"]);
        Assert.IsFalse(query.ContainsKey("agent_name_hint"));
        Assert.IsFalse(query.ContainsKey("prompt"));
        Assert.AreNotEqual(first.State, retry.State);
        Assert.AreNotEqual(first.Nonce, retry.Nonce);
        Assert.AreNotEqual(first.Pkce.Verifier, retry.Pkce.Verifier);
        handler.Enqueue(TokenResponse(signer.Sign(retry.Nonce)), JsonResponse(signer.Jwks));

        var credential = await retryManager.CompleteBrowserLoginAsync(retry, new(retry.RedirectUri + $"?code=fresh-code&state={retry.State}"));
        Assert.AreEqual("subject", credential.Subject);
        Assert.IsTrue(OpenAICodexSubscriptionLoginManager.IsRegistration(await store.LoadAsync("codex")));
        var exchange = ParseQuery(handler.Requests[1].Body);
        Assert.AreEqual(IssuedClientId, exchange["client_id"]);
        Assert.AreEqual("fresh-code", exchange["code"]);
        Assert.AreEqual(retry.Pkce.Verifier, exchange["code_verifier"]);
        Assert.AreEqual(retry.RedirectUri, exchange["redirect_uri"]);
    }

    [TestMethod]
    public async Task LoginManager_InvalidGrantDoesNotOverwriteAnotherCompletedSignIn()
    {
        using var temp = TempDirectory.Create();
        using var handler = new QueueHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_grant"}"""),
        });
        using var http = new HttpClient(handler);
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var manager = new OpenAICodexSubscriptionLoginManager(store, new(http), "codex");
        using var login = await manager.BeginBrowserLoginAsync(HostId);
        var saved = CreateCredential();
        saved.ClientId = "oaiapp_other_completed_registration";
        await store.SaveAsync("codex", saved);

        await Assert.ThrowsExactlyAsync<OpenAICodexSubscriptionTokenException>(() => manager.CompleteBrowserLoginAsync(
            login, new(login.RedirectUri + $"?code=expired-code&state={login.State}&client_id={IssuedClientId}")).AsTask());

        var retained = await store.LoadAsync("codex");
        Assert.AreEqual(saved.ClientId, retained?.ClientId);
        Assert.AreEqual(saved.Subject, retained?.Subject);
        Assert.AreEqual(saved.AccessToken, retained?.AccessToken);
        Assert.AreEqual(saved.RefreshToken, retained?.RefreshToken);
    }

    [TestMethod]
    public async Task LoginManager_PendingRegistrationRejectsChangedClientAndCanBeSignedOut()
    {
        using var temp = TempDirectory.Create();
        using var handler = new QueueHttpMessageHandler();
        using var http = new HttpClient(handler);
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await store.SaveAsync("codex", new() { ClientId = IssuedClientId, AgentHostId = HostId });
        var manager = new OpenAICodexSubscriptionLoginManager(store, new(http), "codex");
        using var login = await manager.BeginBrowserLoginAsync(HostId);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.CompleteBrowserLoginAsync(
            login, new(login.RedirectUri + $"?code=x&state={login.State}&client_id=oaiapp_other")).AsTask());
        Assert.AreEqual(0, handler.Requests.Count);
        Assert.AreEqual(IssuedClientId, (await store.LoadAsync("codex"))?.ClientId);
        Assert.IsTrue(await manager.SignOutAsync());
        Assert.IsNull(await store.LoadAsync("codex"));
        using var fresh = await manager.BeginBrowserLoginAsync(HostId);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.ClientId, ParseQuery(fresh.AuthorizeUri.Query)["client_id"]);
    }

    [TestMethod]
    [DataRow("iss", "https://attacker.test")]
    [DataRow("aud", "oaiapp_other")]
    [DataRow("nonce", "wrong")]
    [DataRow("sub", "")]
    [DataRow("exp", "expired")]
    [DataRow("iat", "future")]
    [DataRow("nbf", "future")]
    [DataRow("azp", "oaiapp_other")]
    public void IdTokenValidator_RejectsInvalidClaims(string claim, string value)
    {
        using var signer = new IdTokenSigner();
        var token = signer.Sign("nonce", claim, value);
        using var jwks = JsonDocument.Parse(signer.Jwks);
        Assert.ThrowsExactly<InvalidOperationException>(() => OpenAICodexSubscriptionIdTokenValidator.Validate(token, jwks.RootElement, IssuedClientId, "nonce"));
    }

    [TestMethod]
    public void IdTokenValidator_ValidatesMultipleAudiencesAndAuthorizedParty()
    {
        using var signer = new IdTokenSigner();
        using var jwks = JsonDocument.Parse(signer.Jwks);
        var claims = new Dictionary<string, object?>
        {
            ["aud"] = new[] { IssuedClientId, "another-audience" },
            ["azp"] = IssuedClientId,
        };
        var valid = signer.Sign("nonce", overrides: claims);
        Assert.AreEqual("subject", OpenAICodexSubscriptionIdTokenValidator.Validate(valid, jwks.RootElement, IssuedClientId, "nonce").Subject);

        claims.Remove("azp");
        var missingAuthorizedParty = signer.Sign("nonce", overrides: claims);
        Assert.ThrowsExactly<InvalidOperationException>(() => OpenAICodexSubscriptionIdTokenValidator.Validate(missingAuthorizedParty, jwks.RootElement, IssuedClientId, "nonce"));
    }

    [TestMethod]
    public void IdTokenValidator_RejectsUnsignedWrongKeyAndTamperedTokens()
    {
        using var signer = new IdTokenSigner();
        using var other = new IdTokenSigner();
        using var jwks = JsonDocument.Parse(signer.Jwks);
        var signed = signer.Sign("nonce");
        var parts = signed.Split('.');
        var tampered = parts[0] + "." + Encode("{\"sub\":\"attacker\"}") + "." + parts[2];
        foreach (var token in new[] { tampered, other.Sign("nonce"), Encode("{\"alg\":\"none\"}") + "." + parts[1] + "." })
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => OpenAICodexSubscriptionIdTokenValidator.Validate(token, jwks.RootElement, IssuedClientId, "nonce"));
        }
    }

    [TestMethod]
    public async Task LoginManager_RetainsValidatedIdentityWithoutPlanPermissionButPreventsInference()
    {
        using var temp = TempDirectory.Create();
        using var signer = new IdTokenSigner();
        using var handler = new QueueHttpMessageHandler();
        using var http = new HttpClient(handler);
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var manager = new OpenAICodexSubscriptionLoginManager(store, new(http), "codex");
        using var login = await manager.BeginBrowserLoginAsync(HostId);
        handler.Enqueue(JsonResponse($$"""{"access_token":"identity-token","id_token":"{{signer.Sign(login.Nonce)}}","token_type":"Bearer","expires_in":3600,"scope":"openid profile email"}"""), JsonResponse(signer.Jwks));

        var credential = await manager.CompleteBrowserLoginAsync(login, new(login.RedirectUri + $"?code=x&state={login.State}&client_id={IssuedClientId}"));

        Assert.AreEqual("subject", credential.Subject);
        Assert.IsNull(credential.AccountId, "An OIDC subject is not a workspace ID.");
        Assert.IsFalse(credential.HasPlanUsagePermission);
        Assert.AreEqual(IssuedClientId, (await store.LoadAsync("codex"))?.ClientId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new OpenAICodexSubscriptionAuthManager(store, new(http), "codex").GetAccessTokenAsync().AsTask());
        using var reauthorization = await manager.BeginBrowserLoginAsync(HostId);
        var query = ParseQuery(reauthorization.AuthorizeUri.Query);
        Assert.AreEqual(IssuedClientId, query["client_id"]);
        Assert.AreEqual("consent", query["prompt"]);
    }

    [TestMethod]
    public async Task AuthManager_SerializesRefreshAcrossManagersAndPreservesGrant()
    {
        using var temp = TempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var credential = CreateCredential();
        credential.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        credential.IdToken = "retained-id-token";
        await store.SaveAsync("codex", credential);
        using var handler = new QueueHttpMessageHandler(JsonResponse("""{"keys":[]}"""), JsonResponse("""{"access_token":"new-token","refresh_token":"rotated-refresh","token_type":"Bearer","expires_in":3600}"""));
        using var http = new HttpClient(handler);
        var first = new OpenAICodexSubscriptionAuthManager(store, new(http), "codex");
        var second = new OpenAICodexSubscriptionAuthManager(new FileOpenAICodexSubscriptionCredentialStore(temp.Path), new(http), "codex");
        var tokens = await Task.WhenAll(first.GetAccessTokenAsync().AsTask(), second.GetAccessTokenAsync().AsTask());
        CollectionAssert.AreEqual(new[] { "new-token", "new-token" }, tokens);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.JwksEndpoint, handler.Requests[0].Uri?.AbsoluteUri);
        var form = ParseQuery(handler.Requests[1].Body);
        Assert.AreEqual(IssuedClientId, form["client_id"]);
        Assert.AreEqual("refresh-secret", form["refresh_token"]);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.Resource, form["resource"]);
        Assert.IsFalse(form.ContainsKey("scope"));
        var stored = await store.LoadAsync("codex");
        Assert.AreEqual("rotated-refresh", stored?.RefreshToken);
        Assert.AreEqual("retained-id-token", stored?.IdToken);
        Assert.AreEqual(HostId, stored?.AgentHostId);
        CollectionAssert.AreEqual(credential.Scopes, stored!.Scopes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AuthManager_JwksOutagePrecedesRotationAndAllowsRetry(bool forceRefresh)
    {
        using var temp = TempDirectory.Create();
        using var signer = new IdTokenSigner();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var credential = CreateCredential();
        credential.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.SaveAsync("codex", credential);
        using var handler = new QueueHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var manager = new OpenAICodexSubscriptionAuthManager(store, new(http), "codex");
        var failure = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => forceRefresh
            ? manager.ForceRefreshCredentialAsync().AsTask()
            : manager.GetAccessTokenAsync().AsTask());

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.JwksEndpoint, handler.Requests[0].Uri?.AbsoluteUri);
        Assert.AreEqual(credential.RefreshToken, (await store.LoadAsync("codex"))?.RefreshToken);

        handler.Enqueue(JsonResponse(signer.Jwks), TokenResponse(signer.Sign("refresh-nonce")));
        if (forceRefresh)
        {
            await manager.ForceRefreshCredentialAsync();
        }
        else
        {
            Assert.AreEqual("new-access", await manager.GetAccessTokenAsync());
        }

        Assert.AreEqual(3, handler.Requests.Count, "No signing-key fetch may follow a rotating refresh.");
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.JwksEndpoint, handler.Requests[1].Uri?.AbsoluteUri);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.TokenEndpoint, handler.Requests[2].Uri?.AbsoluteUri);
        var saved = await store.LoadAsync("codex");
        Assert.AreEqual("new-refresh", saved?.RefreshToken);
        Assert.AreEqual("subject", saved?.Subject);
        Assert.AreEqual(credential.RefreshToken, ParseQuery(handler.Requests[2].Body)["refresh_token"]);
    }

    [TestMethod]
    [DataRow("sub", "other-subject")]
    [DataRow("aud", "oaiapp_other")]
    [DataRow("iss", "https://attacker.test")]
    public async Task AuthManager_RejectsInvalidRefreshedIdentityWithoutReplacingCredentials(string claim, string value)
    {
        using var temp = TempDirectory.Create();
        using var signer = new IdTokenSigner();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var credential = CreateCredential();
        credential.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.SaveAsync("codex", credential);
        using var handler = new QueueHttpMessageHandler(JsonResponse(signer.Jwks), TokenResponse(signer.Sign("refresh-nonce", claim, value)));
        using var http = new HttpClient(handler);
        var manager = new OpenAICodexSubscriptionAuthManager(store, new(http), "codex");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.GetAccessTokenAsync().AsTask());

        Assert.AreEqual(2, handler.Requests.Count);
        var saved = await store.LoadAsync("codex");
        Assert.AreEqual(credential.Subject, saved?.Subject);
        Assert.AreEqual(credential.ClientId, saved?.ClientId);
        Assert.AreEqual(credential.AccessToken, saved?.AccessToken);
        Assert.AreEqual(credential.RefreshToken, saved?.RefreshToken);
    }

    [TestMethod]
    [DataRow(400, "invalid_grant", true)]
    [DataRow(401, "invalid_refresh_token", true)]
    [DataRow(400, "token_expired", true)]
    [DataRow(400, "refresh_token_expired", true)]
    [DataRow(400, "refresh_token_invalidated", true)]
    [DataRow(400, "refresh_token_reused", true)]
    [DataRow(400, "invalid_client", false)]
    [DataRow(400, "invalid_scope", false)]
    [DataRow(400, "", false)]
    [DataRow(503, "", false)]
    public async Task AuthManager_RefreshFailureRetainsRegistrationAndPreservesTransientTokens(int status, string error, bool unusableTokens)
    {
        using var temp = TempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var credential = CreateCredential();
        credential.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);
        await store.SaveAsync("codex", credential);
        using var http = new HttpClient(new QueueHttpMessageHandler(JsonResponse("""{"keys":[]}"""), new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent($$"""{"error":"{{error}}"}"""),
        }));
        var manager = new OpenAICodexSubscriptionAuthManager(store, new(http), "codex");
        await Assert.ThrowsAsync<Exception>(() => manager.GetAccessTokenAsync().AsTask());
        var saved = await store.LoadAsync("codex");
        Assert.AreEqual(IssuedClientId, saved?.ClientId);
        Assert.AreEqual("subject", saved?.Subject);
        Assert.AreEqual(unusableTokens ? null : "refresh-secret", saved?.RefreshToken);
    }

    [TestMethod]
    public async Task AuthManager_RejectsLegacyCredentialsWithoutRefreshing()
    {
        using var temp = TempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await store.SaveAsync("codex", new() { AccessToken = "old-token", RefreshToken = "old-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        using var handler = new QueueHttpMessageHandler();
        using var http = new HttpClient(handler);
        var manager = new OpenAICodexSubscriptionAuthManager(store, new(http), "codex");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.GetAccessTokenAsync().AsTask());
        Assert.AreEqual(0, handler.Requests.Count);
        Assert.AreEqual("old-token", (await store.LoadAsync("codex"))?.AccessToken);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LoginManager_SignOutClearsTokensRetainsMappingAndReportsRevocation(bool success)
    {
        using var temp = TempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await store.SaveAsync("codex", CreateCredential());
        using var handler = new QueueHttpMessageHandler(JsonResponse("""{"issuer":"https://auth.openai.com","revocation_endpoint":"https://auth.openai.com/api/accounts/oauth/revoke"}"""), new(success ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var auth = new OpenAICodexSubscriptionAuthManager(store, new(http), "codex");
        Assert.AreEqual("access-secret", await auth.GetAccessTokenAsync());
        var manager = new OpenAICodexSubscriptionLoginManager(store, new(http), "codex");
        Assert.AreEqual(success, await manager.SignOutAsync());
        var saved = await store.LoadAsync("codex");
        Assert.AreEqual(IssuedClientId, saved?.ClientId);
        Assert.AreEqual("subject", saved?.Subject);
        Assert.AreEqual(HostId, saved?.AgentHostId);
        Assert.AreEqual(string.Empty, saved?.AccessToken);
        Assert.IsNull(saved?.RefreshToken);
        Assert.IsNull(saved?.IdToken);
        Assert.AreEqual(IssuedClientId, ParseQuery(handler.Requests[1].Body)["client_id"]);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => auth.GetAccessTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task HostIdProvider_PersistsOneIdAcrossInstancesAndDoesNotImportCodexTelemetry()
    {
        using var temp = TempDirectory.Create();
        var ids = await Task.WhenAll(new OpenAICodexSubscriptionHostIdProvider(temp.Path).GetOrCreateAsync().AsTask(), new OpenAICodexSubscriptionHostIdProvider(temp.Path).GetOrCreateAsync().AsTask());
        Assert.AreEqual(ids[0], ids[1]);
        StringAssert.StartsWith(ids[0], "urn:uuid:");
        Assert.IsTrue(Guid.TryParse(ids[0][9..], out _));
        using var other = TempDirectory.Create();
        Assert.AreNotEqual(ids[0], await new OpenAICodexSubscriptionHostIdProvider(other.Path).GetOrCreateAsync());
    }

    [TestMethod]
    public void AuthManager_DoesNotTreatUnvalidatedTokenClaimsAsWorkspaceIdentity()
    {
        var jwt = Encode("{\"alg\":\"none\"}") + "." + Encode("""{"https://api.openai.com/auth":{"chatgpt_account_id":"acct_from_jwt"}}""") + ".";
        var credential = CreateCredential();
        credential.AccessToken = jwt;
        Assert.IsNull(OpenAICodexSubscriptionAuthManager.ResolveAccountId(null, credential));
    }

    [TestMethod]
    public void CodexHomeResolver_ResolvesCodexHomeFromEnvironment()
    {
        Assert.AreEqual(@"C:\tmp\codex-home", CodexHomeResolver.ResolveCodexHome(new Dictionary<string, string?> { ["CODEX_HOME"] = @"C:\tmp\codex-home" }));
    }

    [TestMethod]
    public async Task InstallationIdProvider_OmitsIdWhenDisabled()
    {
        using var temp = TempDirectory.Create();
        Assert.IsNull(await new CodexSubscriptionInstallationIdProvider(temp.Path).ResolveAsync(false, "codealta_state"));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "installation")));
    }

    [TestMethod]
    public async Task InstallationIdProvider_GeneratesStableCodeAltaId()
    {
        using var temp = TempDirectory.Create();
        var provider = new CodexSubscriptionInstallationIdProvider(temp.Path);
        var first = await provider.ResolveAsync(true, "codealta_state");
        Assert.IsTrue(Guid.TryParse(first, out _));
        Assert.AreEqual(first, await provider.ResolveAsync(true, "codealta_state"));
    }

    [TestMethod]
    public async Task InstallationIdProvider_ImportsCodexHomeIdWithoutRewritingCodexFile()
    {
        using var state = TempDirectory.Create();
        using var codexHome = TempDirectory.Create();
        var id = Guid.NewGuid().ToString();
        var path = Path.Combine(codexHome.Path, "installation_id");
        await File.WriteAllTextAsync(path, id);
        var before = File.GetLastWriteTimeUtc(path);
        var provider = new CodexSubscriptionInstallationIdProvider(state.Path, codexHome.Path);
        Assert.AreEqual(id, await provider.ResolveAsync(true, "codex_home_import"));
        Assert.AreEqual(id, await provider.ResolveAsync(true, "codealta_state"));
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(path));
    }

    [TestMethod]
    public async Task InstallationIdProvider_ReadonlyCodexHomeFallsBackForInvalidUuid()
    {
        using var state = TempDirectory.Create();
        using var codexHome = TempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(codexHome.Path, "installation_id"), "not-a-uuid");
        Assert.IsTrue(Guid.TryParse(await new CodexSubscriptionInstallationIdProvider(state.Path, codexHome.Path).ResolveAsync(true, "codex_home_readonly"), out _));
    }

    private static OpenAICodexSubscriptionCredential CreateCredential() => new()
    {
        ClientId = IssuedClientId, Subject = "subject", Email = "user@example.test", AccountLabel = "user@example.test",
        AgentHostId = HostId, AccessToken = "access-secret", RefreshToken = "refresh-secret",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Scopes = OpenAICodexSubscriptionOAuthDefaults.Scope.Split(' ').ToList(),
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage TokenResponse(string idToken) => JsonResponse($$"""{"access_token":"new-access","refresh_token":"new-refresh","id_token":"{{idToken}}","token_type":"Bearer","expires_in":3600,"scope":"{{OpenAICodexSubscriptionOAuthDefaults.Scope}}"}""");
    private static string Encode(string value) => OpenAICodexSubscriptionOAuthClient.Base64UrlEncode(Encoding.UTF8.GetBytes(value));
    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2)).ToDictionary(parts => WebUtility.UrlDecode(parts[0]), parts => WebUtility.UrlDecode(parts[1]), StringComparer.Ordinal);

    private sealed class IdTokenSigner : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        public string Jwks
        {
            get
            {
                var key = _rsa.ExportParameters(false);
                return $$"""{"keys":[{"kid":"test-key","kty":"RSA","alg":"RS256","use":"sig","n":"{{OpenAICodexSubscriptionOAuthClient.Base64UrlEncode(key.Modulus!)}}","e":"{{OpenAICodexSubscriptionOAuthClient.Base64UrlEncode(key.Exponent!)}}"}]}""";
            }
        }

        public string Sign(string nonce, string? claim = null, string? value = null, Dictionary<string, object?>? overrides = null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = new Dictionary<string, object?> { ["iss"] = OpenAICodexSubscriptionOAuthDefaults.Issuer, ["aud"] = IssuedClientId, ["sub"] = "subject", ["email"] = "user@example.test", ["nonce"] = nonce, ["iat"] = now, ["exp"] = now + 3600 };
            if (claim is not null)
            {
                claims[claim] = value == "expired" ? now - 60 : value == "future" ? now + 60 : value;
            }

            if (overrides is not null)
            {
                foreach (var entry in overrides)
                {
                    claims[entry.Key] = entry.Value;
                }
            }

            var unsigned = Encode("{\"alg\":\"RS256\",\"kid\":\"test-key\"}") + "." + Encode(JsonSerializer.Serialize(claims));
            return unsigned + "." + OpenAICodexSubscriptionOAuthClient.Base64UrlEncode(_rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        public void Dispose() => _rsa.Dispose();
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;
        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "codex-auth-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }

        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<(Uri? Uri, string Body)> Requests { get; } = [];
        public void Enqueue(params HttpResponseMessage[] responses)
        {
            foreach (var response in responses) _responses.Enqueue(response);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Dequeue();
        }
    }
}
