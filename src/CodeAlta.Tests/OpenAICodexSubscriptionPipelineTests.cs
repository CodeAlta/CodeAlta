#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.OpenAI.Codex;
using OpenAI;

namespace CodeAlta.Tests;

[TestClass]
public sealed class OpenAICodexSubscriptionPipelineTests
{
    [TestMethod]
    public async Task Pipeline_AddsOAuthAndCodexHeadersWithoutApiKeyAuth()
    {
        using var temp = TempDirectory.Create();
        var credential = new OpenAICodexSubscriptionCredential
        {
            AccessToken = "access-secret",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await store.SaveAsync("codex", credential).ConfigureAwait(false);
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        using var httpClient = new HttpClient(handler);
        var authManager = new OpenAICodexSubscriptionAuthManager(
            store,
            new OpenAICodexSubscriptionOAuthClient(new HttpClient(new RecordingHttpMessageHandler())),
            "codex");
        var turnState = new CodexTurnState();
        var pipeline = CreatePipeline(
            authManager,
            new CodexSubscriptionHeaderContext(
                AccountId: "acct_123",
                SessionId: "session_456",
                IsFedRamp: true,
                SendResponsesBetaHeader: true,
                turnState),
            httpClient);

        using var message = pipeline.CreateMessage(
            new Uri("https://chatgpt.com/backend-api/codex/responses"),
            "POST");
        await pipeline.SendAsync(message).ConfigureAwait(false);

        Assert.AreEqual("Bearer access-secret", handler.Requests[0]["Authorization"]);
        Assert.AreEqual("https://chatgpt.com/backend-api/codex/responses", handler.RequestUris[0].ToString());
        Assert.AreEqual("acct_123", handler.Requests[0]["ChatGPT-Account-Id"]);
        Assert.AreEqual("codealta", handler.Requests[0]["originator"]);
        Assert.AreEqual("responses=experimental", handler.Requests[0]["OpenAI-Beta"]);
        Assert.AreEqual("session_456", handler.Requests[0]["session_id"]);
        Assert.AreEqual("session_456", handler.Requests[0]["x-client-request-id"]);
        Assert.AreEqual("true", handler.Requests[0]["X-OpenAI-Fedramp"]);
        Assert.IsFalse(handler.Requests[0].ContainsKey("api-key"));
        Assert.IsFalse(handler.Requests[0].ContainsKey("x-codex-beta-features"));
        Assert.IsFalse(handler.Requests[0].ContainsKey("x-codex-turn-metadata"));
        Assert.IsFalse(handler.Requests[0].ContainsKey("x-responsesapi-include-timing-metrics"));
        Assert.IsFalse(handler.Requests[0].ContainsKey("x-codex-turn-state"));

        var redacted = OpenAICodexSubscriptionSecretRedactor.Redact(handler.Requests[0]["Authorization"], credential);
        Assert.IsFalse(redacted.Contains("access-secret", StringComparison.Ordinal));
        StringAssert.Contains(redacted, OpenAICodexSubscriptionSecretRedactor.Redacted);
    }

    [TestMethod]
    public async Task Pipeline_CapturesAndReplaysTurnStateOnlyWithinCurrentTurn()
    {
        using var temp = TempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await store.SaveAsync(
                "codex",
                new OpenAICodexSubscriptionCredential
                {
                    AccessToken = "access-secret",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                })
            .ConfigureAwait(false);
        var handler = new RecordingHttpMessageHandler(
            CreateResponse(turnState: "sticky-state"),
            CreateResponse(),
            CreateResponse());
        using var httpClient = new HttpClient(handler);
        var authManager = new OpenAICodexSubscriptionAuthManager(
            store,
            new OpenAICodexSubscriptionOAuthClient(new HttpClient(new RecordingHttpMessageHandler())),
            "codex");
        var turnState = new CodexTurnState();
        var pipeline = CreatePipeline(
            authManager,
            new CodexSubscriptionHeaderContext(
                AccountId: null,
                SessionId: "session_456",
                IsFedRamp: false,
                SendResponsesBetaHeader: true,
                turnState),
            httpClient);

        await SendAsync(pipeline).ConfigureAwait(false);
        await SendAsync(pipeline).ConfigureAwait(false);
        turnState.Clear();
        await SendAsync(pipeline).ConfigureAwait(false);

        Assert.IsFalse(handler.Requests[0].ContainsKey("x-codex-turn-state"));
        Assert.AreEqual("sticky-state", handler.Requests[1]["x-codex-turn-state"]);
        Assert.IsFalse(handler.Requests[2].ContainsKey("x-codex-turn-state"));
    }

    [TestMethod]
    public async Task Pipeline_ResolvesAccountHeadersFromAuthManager()
    {
        using var temp = TempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await store.SaveAsync(
                "codex",
                new OpenAICodexSubscriptionCredential
                {
                    AccessToken = "access-secret",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    AccountId = "acct_from_store",
                    IsFedRamp = true,
                })
            .ConfigureAwait(false);
        var handler = new RecordingHttpMessageHandler(CreateResponse());
        using var httpClient = new HttpClient(handler);
        var authManager = new OpenAICodexSubscriptionAuthManager(
            store,
            new OpenAICodexSubscriptionOAuthClient(new HttpClient(new RecordingHttpMessageHandler())),
            "codex");
        var pipeline = CreatePipeline(
            authManager,
            new CodexSubscriptionHeaderContext(
                AccountId: null,
                SessionId: "session_456",
                IsFedRamp: false,
                SendResponsesBetaHeader: true,
                TurnState: new CodexTurnState(),
                AuthManager: authManager),
            httpClient);

        await SendAsync(pipeline).ConfigureAwait(false);

        Assert.AreEqual("acct_from_store", handler.Requests[0]["ChatGPT-Account-Id"]);
        Assert.AreEqual("true", handler.Requests[0]["X-OpenAI-Fedramp"]);
    }

    [TestMethod]
    public void SdkFactory_CreatesCodexSubscriptionClientWithConfiguredEndpoint()
    {
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = AppContext.BaseDirectory,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        };
        var client = OpenAIProviderSdkFactory.CreateResponsesClient(
            provider,
            new OpenAIResponsesClientFactoryContext(
                "gpt-5.3-codex",
                "session_456",
                new AgentRunId("run_789"),
                new ModelProviderRuntimeDescriptor
                {
                    ProtocolFamily = "codex",
                    ProviderKey = "codex",
                    DisplayName = "Codex",
                    TransportKind = LocalAgentTransportKind.OpenAIResponses,
                }));

        Assert.AreEqual("https://chatgpt.com/backend-api/codex", client.Endpoint.ToString());
    }

    [TestMethod]
    public void SdkFactory_ConfiguresLongerNetworkTimeoutForCodexSubscription()
    {
        var nonCodexOptions = CreateClientOptions(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
        });
        var codexOptions = CreateClientOptions(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });

        Assert.IsNull(nonCodexOptions.NetworkTimeout);
        Assert.AreEqual(TimeSpan.FromMinutes(10), codexOptions.NetworkTimeout);
    }

    [TestMethod]
    public void WebSocketSession_MapsWrappedErrorFramesToHttpRequestException()
    {
        var mapped = TryCreateWebSocketResponseUpdateMessage(
            BinaryData.FromString(
                """
                {
                  "type": "error",
                  "status_code": 429,
                  "error": {
                    "type": "usage_limit_reached",
                    "message": "The usage limit has been reached."
                  },
                  "headers": {
                    "Retry-After": "0",
                    "x-codex-primary-used-percent": 100
                  }
                }
                """),
            out _,
            out var eventType,
            out var exception);

        Assert.IsFalse(mapped);
        Assert.AreEqual("error", eventType);
        var httpException = Assert.IsInstanceOfType<HttpRequestException>(exception);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, httpException.StatusCode);
        StringAssert.Contains(httpException.Message, "usage_limit_reached");
        StringAssert.Contains(httpException.Message, "usage limit");
        Assert.AreEqual(true, httpException.Data["OpenAI.WebSocketWrappedError"]);
        Assert.AreEqual("0", httpException.Data["Retry-After"]);
        Assert.AreEqual("100", httpException.Data["x-codex-primary-used-percent"]);
    }

    [TestMethod]
    public void WebSocketSession_MapsConnectionLimitErrorAsRetryableWithoutHttpStatus()
    {
        var mapped = TryCreateWebSocketResponseUpdateMessage(
            BinaryData.FromString(
                """
                {
                  "type": "error",
                  "status": 400,
                  "error": {
                    "type": "invalid_request_error",
                    "code": "websocket_connection_limit_reached",
                    "message": "Responses websocket connection limit reached (60 minutes). Create a new websocket connection to continue."
                  }
                }
                """),
            out _,
            out var eventType,
            out var exception);

        Assert.IsFalse(mapped);
        Assert.AreEqual("error", eventType);
        var httpException = Assert.IsInstanceOfType<HttpRequestException>(exception);
        Assert.IsNull(httpException.StatusCode);
        Assert.AreEqual("websocket_connection_limit_reached", httpException.Data["OpenAI.WebSocketErrorCode"]);
        StringAssert.Contains(httpException.Message, "connection limit");
    }

    [TestMethod]
    public void WebSocketSession_CapturesSideChannelEventsAndNormalizesDone()
    {
        var rateLimitsHandled = TryCreateWebSocketResponseUpdateMessage(
            BinaryData.FromString(
                """
                {
                  "type": "codex.rate_limits",
                  "primary": { "used_percent": 12.5 }
                }
                """),
            out _,
            out var rateLimitsType,
            out var rateLimitsException,
            out var rateLimitsSideChannel);
        var modelHandled = TryCreateWebSocketResponseUpdateMessage(
            BinaryData.FromString(
                """
                {
                  "type": "server_model",
                  "model": "gpt-5.3-codex"
                }
                """),
            out _,
            out var modelType,
            out var modelException,
            out var modelSideChannel);
        var metadataHandled = TryCreateWebSocketResponseUpdateMessage(
            BinaryData.FromString(
                """
                {
                  "type": "response.metadata",
                  "metadata": { "openai_verification_recommendation": ["approved"] }
                }
                """),
            out _,
            out var metadataType,
            out var metadataException,
            out var metadataSideChannel);
        var createdHandled = TryCreateWebSocketResponseUpdateMessage(
            BinaryData.FromString(
                """
                {
                  "type": "response.created",
                  "response": {
                    "id": "resp_1",
                    "headers": { "OpenAI-Model": "gpt-5.3-codex" }
                  }
                }
                """),
            out _,
            out var createdType,
            out var createdException,
            out var createdSideChannel);
        var doneHandled = TryCreateWebSocketResponseUpdateMessage(
            BinaryData.FromString(
                """
                {
                  "type": "response.done",
                  "sequence_number": 3,
                  "response": { "id": "resp_1", "status": "completed" }
                }
                """),
            out var normalizedDone,
            out var doneType,
            out var doneException);

        Assert.IsFalse(rateLimitsHandled);
        Assert.AreEqual("codex.rate_limits", rateLimitsType);
        Assert.IsNull(rateLimitsException);
        AssertSideChannelType("codex.rate_limits", rateLimitsSideChannel);
        Assert.IsFalse(modelHandled);
        Assert.AreEqual("server_model", modelType);
        Assert.IsNull(modelException);
        AssertSideChannelType("server_model", modelSideChannel);
        Assert.IsFalse(metadataHandled);
        Assert.AreEqual("response.metadata", metadataType);
        Assert.IsNull(metadataException);
        AssertSideChannelType("response.metadata", metadataSideChannel);
        Assert.IsTrue(createdHandled);
        Assert.AreEqual("response.created", createdType);
        Assert.IsNull(createdException);
        AssertSideChannelType("response.created", createdSideChannel);
        Assert.IsTrue(doneHandled);
        Assert.AreEqual("response.done", doneType);
        Assert.IsNull(doneException);
        StringAssert.Contains(normalizedDone.ToString(), "response.completed");
    }

    [TestMethod]
    public void WebSocketSession_ReadsTurnStateFromHandshakeHeadersCaseInsensitively()
    {
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.Ordinal)
        {
            ["X-Codex-Turn-State"] = ["ws-sticky-state"],
        };

        var found = OpenAICodexSubscriptionWebSocketSession.TryGetHeaderValue(
            headers,
            "x-codex-turn-state",
            out var turnState);

        Assert.IsTrue(found);
        Assert.AreEqual("ws-sticky-state", turnState);
    }

    [TestMethod]
    public async Task WebSocketSession_ReceiveFrameTimesOutWhenSocketIsSilent()
    {
        using var webSocket = new SilentWebSocket();

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(
                () => OpenAICodexSubscriptionWebSocketSession.ReceiveFrameWithIdleTimeoutAsync(
                    webSocket,
                    new ArraySegment<byte>(new byte[16]),
                    TimeSpan.FromMilliseconds(10),
                    CancellationToken.None))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Message, "did not receive");
    }

    [TestMethod]
    public void StaticModelCatalog_ReturnsVisiblePickerModels()
    {
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "codex",
            ProviderKey = "codex",
            DisplayName = "Codex",
            TransportKind = LocalAgentTransportKind.OpenAIResponses,
        };

        var visibleModels = CodexSubscriptionStaticModelCatalog.List(provider);
        Assert.AreEqual(
            "gpt-5.5|gpt-5.4|gpt-5.4-mini|gpt-5.3-codex|gpt-5.2",
            string.Join('|', visibleModels.Select(static model => model.Id)));
        Assert.IsFalse(visibleModels.Any(static model => model.Id == "codex-auto-review"));
        Assert.IsTrue(visibleModels.All(static model => Equals(400000L, model.Capabilities?["contextWindow"])));
        Assert.IsTrue(visibleModels.All(static model => Equals(400000L, model.Capabilities?["contextWindowTokens"])));
        Assert.IsTrue(visibleModels.All(static model => Equals(272000L, model.Capabilities?["inputTokenLimit"])));
        Assert.IsTrue(visibleModels.All(static model => Equals(272000L, model.Capabilities?["maxInputTokens"])));
        Assert.IsTrue(visibleModels.All(static model => Equals(128000L, model.Capabilities?["outputTokenLimit"])));
        Assert.IsTrue(visibleModels.All(static model => Equals(128000L, model.Capabilities?["maxTokens"])));
        Assert.IsTrue(visibleModels.All(static model => model.Capabilities?.ContainsKey("maxOutputTokens") == false));
        Assert.IsTrue(visibleModels.All(static model => Equals(true, model.Capabilities?["listable"])));
        Assert.IsTrue(visibleModels.All(static model => Equals(false, model.Capabilities?["hidden"])));
    }

    [TestMethod]
    public async Task ModelDiscovery_UsesCodexEndpointAndFiltersUnsupportedModels()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var handler = new RecordingHttpMessageHandler(CreateModelsResponse());
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            CodexSubscriptionHttpClient = new HttpClient(handler),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                AccountId = "acct_configured",
                ModelDiscovery = "codex_endpoint_with_static_fallback",
            },
        };

        var models = await OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            CreateProviderDescriptor(),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, models.Count);
        Assert.AreEqual("gpt-5.3-codex", models[0].Id);
        Assert.AreEqual("Codex model", models[0].DisplayName);
        Assert.AreEqual("codex-endpoint", models[0].Capabilities?["source"]);
        Assert.AreEqual(200000L, models[0].Capabilities?["contextWindow"]);
        Assert.AreEqual("medium", models[0].Capabilities?["defaultTextVerbosity"]);
        Assert.AreEqual(AgentReasoningEffort.High, models[0].DefaultReasoningEffort);
        Assert.AreEqual(true, models[0].Capabilities?["supportsImageInput"]);
        Assert.AreEqual(true, models[0].Capabilities?["supportsTextVerbosity"]);
        Assert.AreEqual("websocket-only-codex", models[1].Id);
        Assert.AreEqual(true, models[1].Capabilities?["requiresWebSocket"]);
        var version = typeof(OpenAIProviderSdkFactory).Assembly.GetName().Version!;
        Assert.AreEqual(
            $"https://chatgpt.com/backend-api/codex/models?client_version={version.Major}.{version.Minor}.{version.Build}",
            handler.RequestUris[0].ToString());
        Assert.AreEqual("Bearer access-token", handler.Requests[0]["Authorization"]);
        Assert.AreEqual("acct_configured", handler.Requests[0]["ChatGPT-Account-Id"]);
        Assert.AreEqual("codealta", handler.Requests[0]["originator"]);
        Assert.AreEqual("responses=experimental", handler.Requests[0]["OpenAI-Beta"]);
    }

    [TestMethod]
    public async Task ModelDiscovery_FiltersWebSocketOnlyModelsWhenTransportIsHttpOnly()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            CodexSubscriptionHttpClient = new HttpClient(new RecordingHttpMessageHandler(CreateModelsResponse())),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
                ModelDiscovery = "codex_endpoint_with_static_fallback",
            },
        };

        var models = await OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            CreateProviderDescriptor(),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("gpt-5.3-codex", models[0].Id);
    }

    [TestMethod]
    public async Task ModelDiscovery_IgnoresSingleModelIdForPickerCatalog()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            SingleModelId = "hidden-codex",
            CodexSubscriptionHttpClient = new HttpClient(new RecordingHttpMessageHandler(CreateModelsResponse())),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ModelDiscovery = "codex_endpoint_with_static_fallback",
            },
        };

        var models = await OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            CreateProviderDescriptor(),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(
            "gpt-5.3-codex|websocket-only-codex",
            string.Join('|', models.Select(static model => model.Id)));
        Assert.IsFalse(models.Any(static model => model.Id == "hidden-codex"));
    }

    [TestMethod]
    public async Task ModelDiscovery_FallsBackToStaticCatalogOnlyWhenConfigured()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            CodexSubscriptionHttpClient = new HttpClient(new RecordingHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}"),
                })),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ModelDiscovery = "codex_endpoint_with_static_fallback",
            },
        };

        var models = await OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            CreateProviderDescriptor(),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(
            "gpt-5.5|gpt-5.4|gpt-5.4-mini|gpt-5.3-codex|gpt-5.2",
            string.Join('|', models.Select(static model => model.Id)));

        provider.CodexSubscriptionHttpClient = new HttpClient(new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}"),
            }));
        provider.CodexSubscription.ModelDiscovery = "codex_endpoint";

        await Assert.ThrowsExactlyAsync<CodexSubscriptionModelDiscoveryException>(
            () => OpenAIProviderSdkFactory.ListModelsAsync(
                provider,
                CreateProviderDescriptor(),
                CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ModelDiscovery_DoesNotFallbackOnAuthErrors()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            CodexSubscriptionHttpClient = new HttpClient(new RecordingHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{}"),
                })),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ModelDiscovery = "codex_endpoint_with_static_fallback",
            },
        };

        var exception = await Assert.ThrowsExactlyAsync<CodexSubscriptionModelDiscoveryException>(
            () => OpenAIProviderSdkFactory.ListModelsAsync(
                provider,
                CreateProviderDescriptor(),
                CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [TestMethod]
    public async Task ConcurrencyLimiter_AllowsOnlyOneTurnPerSessionAndHonorsConfiguredAccountLimit()
    {
        var limiter = new CodexSubscriptionConcurrencyLimiter();
        var accountId = "acct_" + Guid.NewGuid().ToString("N");
        var first = await limiter.AcquireAsync(
            "codex",
            "session-one",
            accountId,
            maxConcurrentRequests: 16,
            CancellationToken.None).ConfigureAwait(false);

        var sameSession = limiter.AcquireAsync(
            "codex",
            "session-one",
            accountId,
            maxConcurrentRequests: 16,
            CancellationToken.None).AsTask();
        await Task.Delay(25).ConfigureAwait(false);
        Assert.IsFalse(sameSession.IsCompleted);

        await first.DisposeAsync().ConfigureAwait(false);
        var second = await sameSession.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await second.DisposeAsync().ConfigureAwait(false);

        var accountLeases = new List<IAsyncDisposable>();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                accountLeases.Add(await limiter.AcquireAsync(
                    "codex",
                    "account-session-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    accountId,
                    maxConcurrentRequests: 16,
                    CancellationToken.None).ConfigureAwait(false));
            }

            var sameAccount = limiter.AcquireAsync(
                "codex",
                "blocked-account-session",
                accountId,
                maxConcurrentRequests: 16,
                CancellationToken.None).AsTask();
            await Task.Delay(25).ConfigureAwait(false);
            Assert.IsFalse(sameAccount.IsCompleted);

            await accountLeases[0].DisposeAsync().ConfigureAwait(false);
            accountLeases.RemoveAt(0);
            var third = await sameAccount.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            await third.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            foreach (var lease in accountLeases)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [TestMethod]
    public async Task ConcurrencyLimiter_NotifiesWhenWaitingForAccountLimit()
    {
        var limiter = new CodexSubscriptionConcurrencyLimiter();
        var accountId = "acct_" + Guid.NewGuid().ToString("N");
        var first = await limiter.AcquireAsync(
            "codex",
            "session-one",
            accountId,
            maxConcurrentRequests: 1,
            CancellationToken.None).ConfigureAwait(false);
        var waitNotifications = new List<CodexSubscriptionConcurrencyWaitInfo>();

        var waiting = limiter.AcquireAsync(
            "codex",
            "session-two",
            accountId,
            maxConcurrentRequests: 1,
            (waitInfo, _) =>
            {
                waitNotifications.Add(waitInfo);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).AsTask();
        await Task.Delay(25).ConfigureAwait(false);

        Assert.IsFalse(waiting.IsCompleted);
        Assert.AreEqual(1, waitNotifications.Count);
        Assert.AreEqual(accountId, waitNotifications[0].AccountKey);
        Assert.AreEqual(1, waitNotifications[0].MaxConcurrentRequests);

        await first.DisposeAsync().ConfigureAwait(false);
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await second.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ConcurrencyLimiter_SharesAccountLimitAcrossProviderDefinitionsInSameLimiter()
    {
        var limiter = new CodexSubscriptionConcurrencyLimiter();
        var accountId = "acct_" + Guid.NewGuid().ToString("N");
        var first = await limiter.AcquireAsync(
            "codex_primary",
            "session-one",
            accountId,
            maxConcurrentRequests: 1,
            CancellationToken.None).ConfigureAwait(false);

        var sameAccountDifferentProvider = limiter.AcquireAsync(
            "codex_secondary",
            "session-two",
            accountId,
            maxConcurrentRequests: 1,
            CancellationToken.None).AsTask();
        await Task.Delay(25).ConfigureAwait(false);
        Assert.IsFalse(sameAccountDifferentProvider.IsCompleted);

        await first.DisposeAsync().ConfigureAwait(false);
        var second = await sameAccountDifferentProvider.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await second.DisposeAsync().ConfigureAwait(false);
    }

    private static OpenAIClientOptions CreateClientOptions(OpenAIProviderOptions provider)
    {
        var method = typeof(OpenAIProviderSdkFactory).GetMethod(
            "CreateClientOptions",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("OpenAI client option factory was not found.");
        return (OpenAIClientOptions)method.Invoke(null, [provider])!;
    }

    private static bool TryCreateWebSocketResponseUpdateMessage(
        BinaryData message,
        out BinaryData normalizedMessage,
        out string? eventType,
        out Exception? exception)
        => TryCreateWebSocketResponseUpdateMessage(
            message,
            out normalizedMessage,
            out eventType,
            out exception,
            out _);

    private static bool TryCreateWebSocketResponseUpdateMessage(
        BinaryData message,
        out BinaryData normalizedMessage,
        out string? eventType,
        out Exception? exception,
        out object? sideChannelEvent)
    {
        var sessionType = GetCodexSubscriptionWebSocketSessionType();
        var method = sessionType?.GetMethod(
            "TryCreateResponseUpdateMessage",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (method is null)
        {
            Assert.Fail("Codex subscription WebSocket parser was not found.");
        }

        object?[] parameters = [message, null, null, null, null];
        var result = (bool)method.Invoke(null, parameters)!;
        normalizedMessage = (BinaryData)parameters[1]!;
        eventType = (string?)parameters[2];
        exception = (Exception?)parameters[3];
        sideChannelEvent = parameters[4];
        return result;
    }

    private static void AssertSideChannelType(string expectedType, object? sideChannelEvent)
    {
        Assert.IsNotNull(sideChannelEvent);
        var actualType = (string?)sideChannelEvent.GetType().GetProperty("Type")?.GetValue(sideChannelEvent);
        Assert.AreEqual(expectedType, actualType);
    }

    private static Type? GetCodexSubscriptionWebSocketSessionType()
        => typeof(OpenAIProviderSdkFactory).Assembly.GetType(
            "CodeAlta.Agent.OpenAI.OpenAICodexSubscriptionWebSocketSession");

    private static ClientPipeline CreatePipeline(
        OpenAICodexSubscriptionAuthManager authManager,
        CodexSubscriptionHeaderContext headerContext,
        HttpClient httpClient)
    {
        var options = new ClientPipelineOptions
        {
            Transport = new HttpClientPipelineTransport(httpClient, enableLogging: false, loggerFactory: null),
        };
        PipelinePolicy[] perTryPolicies = [new ChatGptOAuthAuthenticationPolicy(authManager)];
        PipelinePolicy[] beforeTransportPolicies = [new CodexSubscriptionHeadersPolicy(headerContext)];
        return ClientPipeline.Create(
            options,
            perCallPolicies: [],
            perTryPolicies,
            beforeTransportPolicies);
    }

    private static async Task SendAsync(ClientPipeline pipeline)
    {
        using var message = pipeline.CreateMessage(
            new Uri("https://chatgpt.com/backend-api/codex/responses"),
            "POST");
        await pipeline.SendAsync(message).ConfigureAwait(false);
    }

    private static async Task SaveCredentialAsync(string stateRootPath)
    {
        var store = new FileOpenAICodexSubscriptionCredentialStore(stateRootPath);
        await store.SaveAsync(
            "codex",
            new OpenAICodexSubscriptionCredential
            {
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                AccountId = "acct_from_token",
            }).ConfigureAwait(false);
    }

    private static ModelProviderRuntimeDescriptor CreateProviderDescriptor()
        => new()
        {
            ProtocolFamily = "codex",
            ProviderKey = "codex",
            DisplayName = "Codex",
            TransportKind = LocalAgentTransportKind.OpenAIResponses,
        };

    private static HttpResponseMessage CreateResponse(string? turnState = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        };
        if (!string.IsNullOrWhiteSpace(turnState))
        {
            response.Headers.TryAddWithoutValidation("x-codex-turn-state", turnState);
        }

        return response;
    }

    private static HttpResponseMessage CreateModelsResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "models": [
                    {
                      "slug": "gpt-5.3-codex",
                      "display_name": "Codex model",
                      "supported_in_api": true,
                      "visibility": "list",
                      "input_modalities": ["text", "image"],
                      "support_verbosity": true,
                      "default_reasoning_level": "high",
                      "default_verbosity": "medium",
                      "context_window": 200000
                    },
                    {
                      "id": "unsupported-codex",
                      "supported_in_api": false,
                      "listable": true
                    },
                    {
                      "id": "hidden-codex",
                      "display_name": "Hidden Codex",
                      "supported_in_api": true,
                      "listable": false,
                      "hidden": true
                    },
                    {
                      "id": "websocket-only-codex",
                      "supported_in_api": true,
                      "listable": true,
                      "requires_websocket": true
                    }
                  ]
                }
                """),
        };

    private sealed class RecordingHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<IReadOnlyDictionary<string, string>> Requests { get; } = [];

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri ?? throw new InvalidOperationException("Request URI was not set."));
            Requests.Add(CaptureHeaders(request));
            if (_responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}"),
                });
            }

            return Task.FromResult(_responses.Dequeue());
        }

        private static IReadOnlyDictionary<string, string> CaptureHeaders(HttpRequestMessage request)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value);
                }
            }

            return headers;
        }
    }

    private sealed class SilentWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override void Dispose()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            _ = buffer;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Silent WebSocket receive unexpectedly completed.");
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "codex-pipeline-tests",
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
}
