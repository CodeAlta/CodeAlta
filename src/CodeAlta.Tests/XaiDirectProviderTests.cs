using System.Net;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Xai;
using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class XaiDirectProviderTests
{
    [TestMethod]
    [DataRow("grok-4", null)]
    [DataRow("grok-4-fast-reasoning", null)]
    [DataRow("grok-4.20-0309-reasoning", null)]
    [DataRow("grok-build-0.1", 0)]
    [DataRow("grok-code-fast-1", 0)]
    [DataRow("grok-4-fast-non-reasoning", 0)]
    [DataRow("grok-4.20-0309-non-reasoning", 0)]
    [DataRow("", null)]
    public void XaiReasoningCapability_GetSupportedEfforts_TagsNonReasoningIds(string modelId, int? expectedCount)
    {
        var result = XaiReasoningCapability.GetSupportedEfforts(modelId);
        if (expectedCount is null)
        {
            Assert.IsNull(result, $"Expected reasoning-capable for '{modelId}'.");
        }
        else
        {
            Assert.IsNotNull(result, $"Expected empty list for '{modelId}'.");
            Assert.AreEqual(expectedCount.Value, result!.Count);
        }
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_AcceptsXaiBrowserOAuthProvider()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.xai]
            type = "xai"
            auth_source = "xai_browser_oauth"
            model_discovery = "xai_endpoint_with_static_fallback"
            """);

        Assert.IsTrue(result.IsValid, result.Message);
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_AcceptsXaiDeviceFlowProvider()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.xai]
            type = "xai"
            auth_source = "xai_device_flow"
            model_discovery = "xai_endpoint_with_static_fallback"
            """);

        Assert.IsTrue(result.IsValid, result.Message);
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_RejectsApiKeyEnvAuthSourceOnXai()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.xai]
            type = "xai"
            auth_source = "api_key_env"
            """);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Message ?? string.Empty, "auth_source");
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_RejectsApiKeyFieldOnXai()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.xai]
            type = "xai"
            auth_source = "xai_browser_oauth"
            api_key = "secret"
            """);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Message ?? string.Empty, "api_key");
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_RejectsUnknownXaiAuthSource()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.xai]
            type = "xai"
            auth_source = "github_device_flow"
            """);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Message ?? string.Empty, "auth_source");
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_RejectsCopilotOnlyFieldsOnXai()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.xai]
            type = "xai"
            github_enterprise_url = "https://github.example"
            """);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Message ?? string.Empty, "github_enterprise_url");
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_RejectsInsecureApiUrlForXai()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.xai]
            type = "xai"
            auth_source = "xai_browser_oauth"
            api_url = "http://api.example.com"
            """);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Message ?? string.Empty, "HTTPS");
    }

    [TestMethod]
    public async Task XaiDirect_ListModels_FallsBackToStaticCatalogWhenEndpointUnavailable()
    {
        using var temp = TestTempDirectory.Create();
        WriteCachedCredential(temp.Path, "xai");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var backend = new XaiDirectAgentBackend(new XaiAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new XaiProviderOptions
                {
                    ProviderKey = "xai",
                    StateRootPath = temp.Path,
                    Auth = new XaiAuthOptions
                    {
                        AuthSource = XaiAuthSources.XaiBrowserOAuth,
                    },
                    HttpClient = new HttpClient(handler),
                },
            },
        });

        try
        {
            var models = await backend.ListModelsAsync().ConfigureAwait(false);
            Assert.IsTrue(models.Count > 0);
            Assert.IsTrue(models.All(static model => model.Id.StartsWith("grok-4", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            await backend.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task XaiDirect_ListModels_ParsesEndpointResponseAndSendsBearerToken()
    {
        using var temp = TestTempDirectory.Create();
        WriteCachedCredential(temp.Path, "xai", accessToken: "cached-token");
        var handler = new StubHandler(request =>
        {
            Assert.AreEqual("Bearer cached-token", request.Headers.Authorization?.ToString());
            Assert.AreEqual(new Uri("https://api.x.ai/v1/language-models"), request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "models": [
                        { "id": "grok-4", "object": "model", "created": 1, "owned_by": "xai" },
                        { "id": "grok-4-fast", "object": "model", "created": 1, "owned_by": "xai" }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var backend = new XaiDirectAgentBackend(new XaiAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new XaiProviderOptions
                {
                    ProviderKey = "xai",
                    StateRootPath = temp.Path,
                    Auth = new XaiAuthOptions
                    {
                        AuthSource = XaiAuthSources.XaiBrowserOAuth,
                    },
                    ModelDiscovery = XaiModelDiscoveryModes.Endpoint,
                    HttpClient = new HttpClient(handler),
                },
            },
        });

        try
        {
            var models = await backend.ListModelsAsync().ConfigureAwait(false);
            Assert.AreEqual(2, models.Count);
            Assert.IsNotNull(models.Single(static m => m.Id == "grok-4"));
            Assert.IsNotNull(models.Single(static m => m.Id == "grok-4-fast"));
        }
        finally
        {
            await backend.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task XaiDirectLoginManager_DeviceFlow_PersistsAccessAndRefreshTokens()
    {
        using var temp = TestTempDirectory.Create();
        var observedDeviceCode = default(XaiDirectDeviceCode);
        var tokenPolls = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri == new Uri("https://auth.x.ai/oauth2/device/code"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "device_code": "dev-1",
                          "user_code": "ABCD-1234",
                          "verification_uri": "https://auth.x.ai/oauth2/device",
                          "verification_uri_complete": "https://auth.x.ai/oauth2/device?user_code=ABCD-1234",
                          "expires_in": 60,
                          "interval": 1
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            Assert.AreEqual(new Uri("https://auth.x.ai/oauth2/token"), request.RequestUri);
            tokenPolls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "access_token": "access-1",
                      "refresh_token": "refresh-1",
                      "expires_in": 3600,
                      "scope": "openid offline_access api:access",
                      "token_type": "Bearer"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var manager = new XaiDirectLoginManager(new HttpClient(handler));

        var result = await manager.LoginWithDeviceCodeAsync(
            new XaiDirectLoginOptions("xai", temp.Path)
            {
                PollingIntervalOverride = TimeSpan.FromMilliseconds(1),
            },
            (deviceCode, _) =>
            {
                observedDeviceCode = deviceCode;
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        Assert.IsNotNull(observedDeviceCode);
        Assert.AreEqual("ABCD-1234", observedDeviceCode!.UserCode);
        Assert.AreEqual(1, tokenPolls);
        Assert.AreEqual(new Uri("https://api.x.ai/v1/"), result.BaseUri);
        Assert.IsTrue(result.ExpiresAt > DateTimeOffset.UtcNow);

        var status = await manager.GetCredentialStatusAsync(new XaiDirectLoginOptions("xai", temp.Path)).ConfigureAwait(false);
        Assert.IsNotNull(status);
        Assert.IsTrue(status!.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public async Task XaiDirectLoginManager_DeviceFlow_PropagatesAuthorizationError()
    {
        using var temp = TestTempDirectory.Create();
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri == new Uri("https://auth.x.ai/oauth2/device/code"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"device_code":"dev","user_code":"X","verification_uri":"https://auth.x.ai/oauth2/device","expires_in":60,"interval":1}
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"error\":\"access_denied\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var manager = new XaiDirectLoginManager(new HttpClient(handler));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await manager.LoginWithDeviceCodeAsync(
                new XaiDirectLoginOptions("xai", temp.Path)
                {
                    PollingIntervalOverride = TimeSpan.FromMilliseconds(1),
                },
                (_, _) => ValueTask.CompletedTask).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task XaiDirectLoginManager_GetCredentialStatus_ReturnsNullWhenNoCacheFile()
    {
        using var temp = TestTempDirectory.Create();
        var manager = new XaiDirectLoginManager(new HttpClient(new StubHandler(_ =>
        {
            Assert.Fail("No HTTP request expected when reading status from disk.");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));

        var status = await manager.GetCredentialStatusAsync(new XaiDirectLoginOptions("xai", temp.Path)).ConfigureAwait(false);
        Assert.IsNull(status);
    }

    [TestMethod]
    public async Task XaiDirectLoginManager_DeleteCredential_RemovesCachedFile()
    {
        using var temp = TestTempDirectory.Create();
        var loginHandler = new StubHandler(request =>
        {
            if (request.RequestUri == new Uri("https://auth.x.ai/oauth2/device/code"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"device_code":"dev","user_code":"X","verification_uri":"https://auth.x.ai/oauth2/device","expires_in":60,"interval":1}
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"access_token\":\"at\",\"refresh_token\":\"rt\",\"expires_in\":600}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var manager = new XaiDirectLoginManager(new HttpClient(loginHandler));
        _ = await manager.LoginWithDeviceCodeAsync(
            new XaiDirectLoginOptions("xai", temp.Path)
            {
                PollingIntervalOverride = TimeSpan.FromMilliseconds(1),
            },
            (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.IsNotNull(await manager.GetCredentialStatusAsync(new XaiDirectLoginOptions("xai", temp.Path)).ConfigureAwait(false));
        await manager.DeleteCredentialAsync(new XaiDirectLoginOptions("xai", temp.Path)).ConfigureAwait(false);
        Assert.IsNull(await manager.GetCredentialStatusAsync(new XaiDirectLoginOptions("xai", temp.Path)).ConfigureAwait(false));
    }

    private static void WriteCachedCredential(string stateRoot, string providerKey, string accessToken = "test-token")
    {
        var dir = Path.Combine(stateRoot, "auth", "xai");
        Directory.CreateDirectory(dir);
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var json = $$"""
            {
              "access_token": "{{accessToken}}",
              "refresh_token": "refresh-token",
              "expires_at": {{expires}},
              "scope": "openid offline_access api:access"
            }
            """;
        File.WriteAllText(Path.Combine(dir, providerKey + ".json"), json);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
