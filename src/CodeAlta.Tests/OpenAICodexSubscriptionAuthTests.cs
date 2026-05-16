using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent.OpenAI.CodexSubscription;

namespace CodeAlta.Tests;

[TestClass]
public sealed class OpenAICodexSubscriptionAuthTests
{
    [TestMethod]
    public async Task FileCredentialStore_RoundTripsCredentialAndDeletes()
    {
        using var temp = TempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var credential = new OpenAICodexSubscriptionCredential
        {
            AccessToken = "access-secret",
            RefreshToken = "refresh-secret",
            IdToken = "id-secret",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            AccountId = "acct_123",
            AccountLabel = "Workspace",
            IsFedRamp = true,
            Scopes = ["openid", "offline_access"],
        };

        await store.SaveAsync("codex/subscription", credential).ConfigureAwait(false);
        var credentialPath = Directory.GetFiles(temp.Path, "*.credential", SearchOption.AllDirectories).Single();
        var rawCredential = await File.ReadAllTextAsync(credentialPath).ConfigureAwait(false);
        Assert.IsFalse(rawCredential.Contains("access-secret", StringComparison.Ordinal));
        Assert.IsFalse(rawCredential.Contains("refresh-secret", StringComparison.Ordinal));
        StringAssert.StartsWith(
            rawCredential,
            OperatingSystem.IsWindows() ? "dpapi:" : "plain64:");

        var loaded = await store.LoadAsync("codex/subscription").ConfigureAwait(false);

        Assert.IsNotNull(loaded);
        Assert.AreEqual("access-secret", loaded!.AccessToken);
        Assert.AreEqual("refresh-secret", loaded.RefreshToken);
        Assert.AreEqual("id-secret", loaded.IdToken);
        Assert.AreEqual("acct_123", loaded.AccountId);
        Assert.AreEqual("Workspace", loaded.AccountLabel);
        Assert.IsTrue(loaded.IsFedRamp);
        CollectionAssert.AreEqual(new[] { "openid", "offline_access" }, loaded.Scopes);

        await store.DeleteAsync("codex/subscription").ConfigureAwait(false);
        Assert.IsNull(await store.LoadAsync("codex/subscription").ConfigureAwait(false));
    }

    [TestMethod]
    public void SecretRedactor_RedactsCredentialSecretsAndBearerTokens()
    {
        var credential = new OpenAICodexSubscriptionCredential
        {
            AccessToken = "access-secret",
            RefreshToken = "refresh-secret",
            IdToken = "id-secret",
        };

        var redacted = OpenAICodexSubscriptionSecretRedactor.Redact(
            "Authorization: Bearer access-secret refresh-secret id-secret",
            credential);

        Assert.IsFalse(redacted.Contains("access-secret", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("refresh-secret", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("id-secret", StringComparison.Ordinal));
        StringAssert.Contains(redacted, OpenAICodexSubscriptionSecretRedactor.Redacted);
    }

    [TestMethod]
    public void SecretRedactor_RedactsOAuthCodesPkceVerifiersAndJwtPayloads()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJlbWFpbCI6InVzZXJAZXhhbXBsZS5jb20iLCJhY2NvdW50IjoiYWNjdF8xMjMifQ.signature123";

        var redacted = OpenAICodexSubscriptionSecretRedactor.Redact(
            "callback?code=oauth-code-secret&state=ok code_verifier=pkce-secret PKCE verifier:pkce-secret-2 " + jwt);

        Assert.IsFalse(redacted.Contains("oauth-code-secret", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("pkce-secret", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("user@example.com", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains(jwt, StringComparison.Ordinal));
        StringAssert.Contains(redacted, OpenAICodexSubscriptionSecretRedactor.Redacted);
    }

    [TestMethod]
    public void CodexAuthFileReader_ResolvesCodexHomeFromEnvironment()
    {
        var home = CodexAuthFileReader.ResolveCodexHome(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["CODEX_HOME"] = @"C:\tmp\codex-home",
            });

        Assert.AreEqual(@"C:\tmp\codex-home", home);
    }

    [TestMethod]
    public async Task CodexAuthFileReader_ReadsTokenAuthWithoutWritingFile()
    {
        using var temp = TempDirectory.Create();
        var authPath = Path.Combine(temp.Path, "auth.json");
        await File.WriteAllTextAsync(
            authPath,
            """
            {
              "auth_mode": "chatgpt",
              "OPENAI_API_KEY": "ignored-api-key",
              "tokens": {
                "access_token": "access-secret",
                "refresh_token": "refresh-secret",
                "id_token": "id-secret",
                "expires_at": "2026-04-24T12:34:56Z",
                "account_id": "acct_123",
                "account_label": "Workspace",
                "is_fedramp": true,
                "scopes": ["openid", "offline_access"]
              },
              "last_refresh": "2026-04-24T11:34:56Z"
            }
            """).ConfigureAwait(false);
        var before = File.GetLastWriteTimeUtc(authPath);

        var credential = await CodexAuthFileReader.ReadAuthJsonAsync(temp.Path).ConfigureAwait(false);

        Assert.IsNotNull(credential);
        Assert.AreEqual("access-secret", credential!.AccessToken);
        Assert.AreEqual("refresh-secret", credential.RefreshToken);
        Assert.AreEqual("id-secret", credential.IdToken);
        Assert.AreEqual(DateTimeOffset.Parse("2026-04-24T12:34:56Z"), credential.ExpiresAt);
        Assert.AreEqual("acct_123", credential.AccountId);
        Assert.AreEqual("Workspace", credential.AccountLabel);
        Assert.IsTrue(credential.IsFedRamp);
        CollectionAssert.AreEqual(new[] { "openid", "offline_access" }, credential.Scopes);
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(authPath));
    }

    [TestMethod]
    public async Task CodexAuthFileReader_IgnoresApiKeyOnlyAuth()
    {
        using var temp = TempDirectory.Create();
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "auth.json"),
            """
            {
              "auth_mode": "apikey",
              "OPENAI_API_KEY": "ignored-api-key"
            }
            """).ConfigureAwait(false);

        Assert.IsNull(await CodexAuthFileReader.ReadAuthJsonAsync(temp.Path).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CodexAuthFileReader_ReturnsNullForMissingFileAndRejectsInvalidShape()
    {
        using var missing = TempDirectory.Create();
        Assert.IsNull(await CodexAuthFileReader.ReadAuthJsonAsync(missing.Path).ConfigureAwait(false));

        using var invalid = TempDirectory.Create();
        await File.WriteAllTextAsync(
            Path.Combine(invalid.Path, "auth.json"),
            """{"tokens":""").ConfigureAwait(false);

        try
        {
            _ = await CodexAuthFileReader.ReadAuthJsonAsync(invalid.Path).ConfigureAwait(false);
            Assert.Fail("Expected malformed Codex auth JSON to throw.");
        }
        catch (JsonException)
        {
        }
    }

    [TestMethod]
    public async Task CodexAuthFileReader_ImportCopiesIntoCodeAltaStore()
    {
        using var codexHome = TempDirectory.Create();
        using var codeAltaState = TempDirectory.Create();
        await File.WriteAllTextAsync(
            Path.Combine(codexHome.Path, "auth.json"),
            """
            {
              "tokens": {
                "access_token": "access-secret",
                "expires_at": 1777034096,
                "scope": "openid offline_access"
              }
            }
            """).ConfigureAwait(false);
        var store = new FileOpenAICodexSubscriptionCredentialStore(codeAltaState.Path);

        var imported = await CodexAuthFileReader.ImportAuthJsonAsync(
                codexHome.Path,
                store,
                "codex")
            .ConfigureAwait(false);
        var stored = await store.LoadAsync("codex").ConfigureAwait(false);

        Assert.IsNotNull(imported);
        Assert.IsNotNull(stored);
        Assert.AreEqual("access-secret", stored!.AccessToken);
        CollectionAssert.AreEqual(new[] { "openid", "offline_access" }, stored.Scopes);
    }

    [TestMethod]
    public void OAuthClient_BuildAuthorizeUriIncludesRequiredParameters()
    {
        var pkce = new OpenAICodexSubscriptionPkce("verifier", "challenge");
        var uri = OpenAICodexSubscriptionOAuthClient.BuildAuthorizeUri(pkce, "state", "workspace_123");
        var query = ParseQuery(uri.Query);

        Assert.AreEqual("https://auth.openai.com/oauth/authorize", uri.GetLeftPart(UriPartial.Path));
        Assert.AreEqual("code", query["response_type"]);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.ClientId, query["client_id"]);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.RedirectUri, query["redirect_uri"]);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.Scope, query["scope"]);
        Assert.AreEqual("challenge", query["code_challenge"]);
        Assert.AreEqual("S256", query["code_challenge_method"]);
        Assert.AreEqual("state", query["state"]);
        Assert.AreEqual("true", query["id_token_add_organizations"]);
        Assert.AreEqual("true", query["codex_cli_simplified_flow"]);
        Assert.AreEqual("codealta", query["originator"]);
        Assert.AreEqual("workspace_123", query["allowed_workspace_id"]);
    }

    [TestMethod]
    public void OAuthClient_StateMismatchThrows()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => OpenAICodexSubscriptionOAuthClient.ValidateState("expected", "actual"));
    }

    [TestMethod]
    public async Task OAuthClient_ExchangeAuthorizationCodeStoresExpiryAndScopes()
    {
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "access_token": "access-secret",
                      "refresh_token": "refresh-secret",
                      "id_token": "id-secret",
                      "expires_in": 3600,
                      "scope": "openid offline_access"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }));
        var client = new OpenAICodexSubscriptionOAuthClient(httpClient);
        var before = DateTimeOffset.UtcNow;

        var credential = await client.ExchangeAuthorizationCodeAsync(
                "code",
                "verifier",
                OpenAICodexSubscriptionOAuthDefaults.RedirectUri)
            .ConfigureAwait(false);

        Assert.AreEqual("access-secret", credential.AccessToken);
        Assert.AreEqual("refresh-secret", credential.RefreshToken);
        Assert.AreEqual("id-secret", credential.IdToken);
        Assert.IsTrue(credential.ExpiresAt >= before.AddMinutes(59));
        CollectionAssert.AreEqual(new[] { "openid", "offline_access" }, credential.Scopes);
    }

    [TestMethod]
    public async Task LoginManager_CompleteBrowserLoginStoresCredentialAndExtractsAccountId()
    {
        using var temp = TempDirectory.Create();
        var jwt = CreateUnsignedJwt(
            """
            {
              "https://api.openai.com/auth": {
                "chatgpt_account_id": "acct_from_login"
              }
            }
            """);
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "access_token": "{{jwt}}",
                      "refresh_token": "refresh-secret",
                      "expires_in": 3600
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }));
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var manager = new OpenAICodexSubscriptionLoginManager(
            store,
            new OpenAICodexSubscriptionOAuthClient(httpClient),
            "codex");
        var login = new OpenAICodexSubscriptionBrowserLogin(
            new Uri("https://auth.openai.com/oauth/authorize"),
            new OpenAICodexSubscriptionPkce("verifier", "challenge"),
            "expected-state");

        var credential = await manager.CompleteBrowserLoginAsync(
                login,
                new Uri("http://localhost:1455/auth/callback?code=auth-code&state=expected-state"))
            .ConfigureAwait(false);
        var stored = await store.LoadAsync("codex").ConfigureAwait(false);

        Assert.AreEqual("acct_from_login", credential.AccountId);
        Assert.AreEqual("acct_from_login", stored?.AccountId);
        Assert.AreEqual("refresh-secret", stored?.RefreshToken);
    }

    [TestMethod]
    public async Task OAuthClient_RequestDeviceCodeParsesVerificationDetails()
    {
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "device_code": "device",
                      "user_code": "ABCD-EFGH",
                      "verification_uri": "https://auth.openai.com/codex/device",
                      "expires_in": 900,
                      "interval": 3
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }));
        var client = new OpenAICodexSubscriptionOAuthClient(httpClient);

        var deviceCode = await client.RequestDeviceCodeAsync().ConfigureAwait(false);

        Assert.AreEqual("device", deviceCode.DeviceCode);
        Assert.AreEqual("ABCD-EFGH", deviceCode.UserCode);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.DeviceVerificationUri, deviceCode.VerificationUri);
        Assert.AreEqual(TimeSpan.FromSeconds(900), deviceCode.ExpiresIn);
        Assert.AreEqual(TimeSpan.FromSeconds(3), deviceCode.Interval);
    }

    [TestMethod]
    public async Task LoginManager_CompleteDeviceLoginDisplaysCodeAndStoresCredential()
    {
        using var temp = TempDirectory.Create();
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "device_code": "device",
                      "user_code": "ABCD-EFGH",
                      "verification_uri": "https://auth.openai.com/codex/device",
                      "expires_in": 900,
                      "interval": 0
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "access_token": "access-secret",
                      "refresh_token": "refresh-secret",
                      "expires_in": 3600
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }));
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        var manager = new OpenAICodexSubscriptionLoginManager(
            store,
            new OpenAICodexSubscriptionOAuthClient(httpClient),
            "codex");
        OpenAICodexSubscriptionDeviceCode? displayed = null;

        _ = await manager.CompleteDeviceLoginAsync(
                (deviceCode, _) =>
                {
                    displayed = deviceCode;
                    return ValueTask.CompletedTask;
                })
            .ConfigureAwait(false);
        var stored = await store.LoadAsync("codex").ConfigureAwait(false);

        Assert.IsNotNull(displayed);
        Assert.AreEqual("ABCD-EFGH", displayed!.UserCode);
        Assert.AreEqual(OpenAICodexSubscriptionOAuthDefaults.DeviceVerificationUri, displayed.VerificationUri);
        Assert.AreEqual("access-secret", stored?.AccessToken);
        Assert.AreEqual("refresh-secret", stored?.RefreshToken);
    }

    [TestMethod]
    public async Task OAuthClient_PollDeviceTokenHonorsPollingCadenceAndSlowDown()
    {
        using var handler = new QueueHttpMessageHandler(
            CreateDeviceErrorResponse("authorization_pending"),
            CreateDeviceErrorResponse("slow_down"),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "access_token": "access-secret",
                      "refresh_token": "refresh-secret",
                      "expires_in": 3600
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var timeProvider = new AutoAdvanceTimeProvider(DateTimeOffset.Parse("2026-04-24T12:00:00Z"));
        var client = new OpenAICodexSubscriptionOAuthClient(httpClient);
        var deviceCode = new OpenAICodexSubscriptionDeviceCode(
            "device",
            "ABCD-EFGH",
            OpenAICodexSubscriptionOAuthDefaults.DeviceVerificationUri,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromSeconds(3));

        var credential = await client.PollDeviceTokenAsync(deviceCode, timeProvider).ConfigureAwait(false);

        Assert.AreEqual("access-secret", credential.AccessToken);
        Assert.AreEqual(3, handler.RequestCount);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8) },
            timeProvider.Delays);
    }

    [TestMethod]
    public async Task OAuthClient_PollDeviceTokenStopsOnExpiry()
    {
        using var handler = new QueueHttpMessageHandler(CreateDeviceErrorResponse("authorization_pending"));
        using var httpClient = new HttpClient(handler);
        var timeProvider = new AutoAdvanceTimeProvider(DateTimeOffset.Parse("2026-04-24T12:00:00Z"));
        var client = new OpenAICodexSubscriptionOAuthClient(httpClient);
        var deviceCode = new OpenAICodexSubscriptionDeviceCode(
            "device",
            "ABCD-EFGH",
            OpenAICodexSubscriptionOAuthDefaults.DeviceVerificationUri,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));

        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => client.PollDeviceTokenAsync(deviceCode, timeProvider)).ConfigureAwait(false);

        Assert.AreEqual(1, handler.RequestCount);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(2) }, timeProvider.Delays);
    }

    [TestMethod]
    public async Task OAuthClient_PollDeviceTokenStopsOnCancellation()
    {
        using var handler = new QueueHttpMessageHandler(CreateDeviceErrorResponse("authorization_pending"));
        using var httpClient = new HttpClient(handler);
        var client = new OpenAICodexSubscriptionOAuthClient(httpClient);
        var deviceCode = new OpenAICodexSubscriptionDeviceCode(
            "device",
            "ABCD-EFGH",
            OpenAICodexSubscriptionOAuthDefaults.DeviceVerificationUri,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => client.PollDeviceTokenAsync(deviceCode, cancellationToken: cancellation.Token)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OAuthClient_PollDeviceTokenThrowsForTerminalErrors()
    {
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(CreateDeviceErrorResponse("access_denied")));
        var client = new OpenAICodexSubscriptionOAuthClient(httpClient);
        var deviceCode = new OpenAICodexSubscriptionDeviceCode(
            "device",
            "ABCD-EFGH",
            OpenAICodexSubscriptionOAuthDefaults.DeviceVerificationUri,
            TimeSpan.FromMinutes(10),
            TimeSpan.Zero);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.PollDeviceTokenAsync(deviceCode)).ConfigureAwait(false);

        StringAssert.Contains(exception.Message, "access_denied");
    }

    [TestMethod]
    public void AuthManager_ExtractsAccountIdFromJwtClaimMetadata()
    {
        var jwt = CreateUnsignedJwt(
            """
            {
              "https://api.openai.com/auth": {
                "chatgpt_account_id": "acct_from_jwt"
              }
            }
            """);

        Assert.AreEqual("acct_from_jwt", OpenAICodexSubscriptionAuthManager.TryExtractAccountIdFromJwt(jwt));
    }

    [TestMethod]
    public async Task AuthManager_RefreshesExpiredTokenOnceForConcurrentCallers()
    {
        using var temp = TempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await store.SaveAsync(
                "codex",
                new OpenAICodexSubscriptionCredential
                {
                    AccessToken = "old-token",
                    RefreshToken = "refresh-secret",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    AccountId = "acct_123",
                    AccountLabel = "Workspace",
                    IsFedRamp = true,
                })
            .ConfigureAwait(false);
        using var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "access_token": "new-token",
                      "refresh_token": "new-refresh",
                      "expires_in": 3600
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var manager = new OpenAICodexSubscriptionAuthManager(
            store,
            new OpenAICodexSubscriptionOAuthClient(httpClient),
            "codex");

        var tokens = await Task.WhenAll(
                manager.GetAccessTokenAsync().AsTask(),
                manager.GetAccessTokenAsync().AsTask())
            .ConfigureAwait(false);
        var account = await manager.GetAccountContextAsync().ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { "new-token", "new-token" }, tokens);
        Assert.AreEqual(1, handler.RequestCount);
        Assert.AreEqual("acct_123", account.AccountId);
        Assert.AreEqual("Workspace", account.AccountLabel);
        Assert.IsTrue(account.IsFedRamp);
    }

    [TestMethod]
    public async Task AuthManager_RefreshFailureDeletesOnlyCodeAltaCredentials()
    {
        using var state = TempDirectory.Create();
        using var codexHome = TempDirectory.Create();
        var codexAuthPath = Path.Combine(codexHome.Path, "auth.json");
        await File.WriteAllTextAsync(codexAuthPath, """{"auth_mode":"chatgpt"}""").ConfigureAwait(false);
        var before = File.GetLastWriteTimeUtc(codexAuthPath);
        var store = new FileOpenAICodexSubscriptionCredentialStore(state.Path);
        await store.SaveAsync(
                "codex",
                new OpenAICodexSubscriptionCredential
                {
                    AccessToken = "old-token",
                    RefreshToken = "refresh-secret",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                })
            .ConfigureAwait(false);
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
            }));
        var manager = new OpenAICodexSubscriptionAuthManager(
            store,
            new OpenAICodexSubscriptionOAuthClient(httpClient),
            "codex",
            codexHome: codexHome.Path);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => manager.GetAccessTokenAsync().AsTask()).ConfigureAwait(false);

        Assert.IsNull(await store.LoadAsync("codex").ConfigureAwait(false));
        Assert.IsTrue(File.Exists(codexAuthPath));
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(codexAuthPath));
    }

    [TestMethod]
    public async Task InstallationIdProvider_OmitsIdWhenDisabled()
    {
        using var temp = TempDirectory.Create();
        var provider = new CodexSubscriptionInstallationIdProvider(temp.Path);

        Assert.IsNull(await provider.ResolveAsync(sendInstallationId: false, "codealta_state").ConfigureAwait(false));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "installation")));
    }

    [TestMethod]
    public async Task InstallationIdProvider_GeneratesStableCodeAltaId()
    {
        using var temp = TempDirectory.Create();
        var provider = new CodexSubscriptionInstallationIdProvider(temp.Path);

        var first = await provider.ResolveAsync(sendInstallationId: true, "codealta_state").ConfigureAwait(false);
        var second = await provider.ResolveAsync(sendInstallationId: true, "codealta_state").ConfigureAwait(false);

        Assert.IsTrue(Guid.TryParse(first, out _));
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public async Task InstallationIdProvider_ImportsCodexHomeIdWithoutRewritingCodexFile()
    {
        using var state = TempDirectory.Create();
        using var codexHome = TempDirectory.Create();
        var codexId = Guid.NewGuid().ToString();
        var codexPath = Path.Combine(codexHome.Path, "installation_id");
        await File.WriteAllTextAsync(codexPath, codexId).ConfigureAwait(false);
        var before = File.GetLastWriteTimeUtc(codexPath);
        var provider = new CodexSubscriptionInstallationIdProvider(state.Path, codexHome.Path);

        var resolved = await provider.ResolveAsync(sendInstallationId: true, "codex_home_import").ConfigureAwait(false);
        var imported = await provider.ResolveAsync(sendInstallationId: true, "codealta_state").ConfigureAwait(false);

        Assert.AreEqual(codexId, resolved);
        Assert.AreEqual(codexId, imported);
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(codexPath));
    }

    [TestMethod]
    public async Task InstallationIdProvider_ReadonlyCodexHomeFallsBackForInvalidUuid()
    {
        using var state = TempDirectory.Create();
        using var codexHome = TempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(codexHome.Path, "installation_id"), "not-a-uuid").ConfigureAwait(false);
        var provider = new CodexSubscriptionInstallationIdProvider(state.Path, codexHome.Path);

        var resolved = await provider.ResolveAsync(sendInstallationId: true, "codex_home_readonly").ConfigureAwait(false);

        Assert.IsTrue(Guid.TryParse(resolved, out _));
    }

    private static string CreateUnsignedJwt(string payloadJson)
    {
        static string Encode(string text)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        return Encode("""{"alg":"none"}""") + "." + Encode(payloadJson) + ".";
    }

    private static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(
                static parts => Uri.UnescapeDataString(parts[0]),
                static parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')),
                StringComparer.Ordinal);

    private static HttpResponseMessage CreateDeviceErrorResponse(string error)
        => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                $$"""{"error":"{{error}}"}""",
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "codex-auth-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No HTTP response was queued.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class AutoAdvanceTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public List<TimeSpan> Delays { get; } = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            Delays.Add(dueTime);
            _now += dueTime;
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new CompletedTimer();
        }
    }

    private sealed class CompletedTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Dispose()
        {
        }
    }
}
