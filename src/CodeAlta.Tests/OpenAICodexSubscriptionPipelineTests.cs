#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Net;
using System.Reflection;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.OpenAI.CodexSubscription;
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
        await store.SaveAsync("codex_subscription", credential).ConfigureAwait(false);
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        using var httpClient = new HttpClient(handler);
        var authManager = new OpenAICodexSubscriptionAuthManager(
            store,
            new OpenAICodexSubscriptionOAuthClient(new HttpClient(new RecordingHttpMessageHandler())),
            "codex_subscription");
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
                "codex_subscription",
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
            "codex_subscription");
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
                "codex_subscription",
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
            "codex_subscription");
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
            ProviderKey = "codex_subscription",
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
                new LocalAgentProviderDescriptor
                {
                    ProtocolFamily = "openai-codex-subscription",
                    ProviderKey = "codex_subscription",
                    DisplayName = "Codex (ChatGPT subscription)",
                    BackendId = new AgentBackendId("codex_subscription"),
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
            ProviderKey = "codex_subscription",
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
    public void WebSocketSession_IgnoresSideChannelEventsAndNormalizesDone()
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
            out var rateLimitsException);
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
            out var modelException);
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
        Assert.IsFalse(modelHandled);
        Assert.AreEqual("server_model", modelType);
        Assert.IsNull(modelException);
        Assert.IsTrue(doneHandled);
        Assert.AreEqual("response.done", doneType);
        Assert.IsNull(doneException);
        StringAssert.Contains(normalizedDone.ToString(), "response.completed");
    }

    [TestMethod]
    public void StaticModelCatalog_ReturnsConfiguredHiddenModelAndRejectsUnknownModels()
    {
        var provider = new LocalAgentProviderDescriptor
        {
            ProtocolFamily = "openai-codex-subscription",
            ProviderKey = "codex_subscription",
            DisplayName = "Codex (ChatGPT subscription)",
            BackendId = new AgentBackendId("codex_subscription"),
            TransportKind = LocalAgentTransportKind.OpenAIResponses,
        };

        var visibleModels = CodexSubscriptionStaticModelCatalog.List(provider);
        Assert.IsTrue(visibleModels.Any(static model => model.Id == "gpt-5.3-codex"));
        Assert.IsFalse(visibleModels.Any(static model => model.Id == "codex-auto-review"));
        Assert.IsTrue(visibleModels.All(static model => Equals(272000L, model.Capabilities?["contextWindow"])));

        var hiddenConfiguredModel = CodexSubscriptionStaticModelCatalog.List(provider, "codex-auto-review");
        Assert.AreEqual(1, hiddenConfiguredModel.Count);
        Assert.AreEqual("codex-auto-review", hiddenConfiguredModel[0].Id);
        Assert.AreEqual(true, hiddenConfiguredModel[0].Capabilities?["hidden"]);
        Assert.AreEqual(true, hiddenConfiguredModel[0].Capabilities?["supportedInApi"]);
        Assert.AreEqual(true, hiddenConfiguredModel[0].Capabilities?["supportsReasoningSummary"]);
        Assert.AreEqual(true, hiddenConfiguredModel[0].Capabilities?["supportsEncryptedReasoning"]);
        Assert.AreEqual(true, hiddenConfiguredModel[0].Capabilities?["supportsTextVerbosity"]);
        Assert.AreEqual(true, hiddenConfiguredModel[0].Capabilities?["supportsTools"]);
        Assert.AreEqual(false, hiddenConfiguredModel[0].Capabilities?["requiresWebSocket"]);
        Assert.AreEqual(272000L, hiddenConfiguredModel[0].Capabilities?["contextWindow"]);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => CodexSubscriptionStaticModelCatalog.List(provider, "unknown-codex-model"));
    }

    [TestMethod]
    public async Task ModelDiscovery_UsesCodexEndpointAndFiltersUnsupportedModels()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var handler = new RecordingHttpMessageHandler(CreateModelsResponse());
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex_subscription",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            CodexSubscriptionHttpClient = new HttpClient(handler),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                AccountId = "acct_configured",
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
            ProviderKey = "codex_subscription",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            CodexSubscriptionHttpClient = new HttpClient(new RecordingHttpMessageHandler(CreateModelsResponse())),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
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
    public async Task ModelDiscovery_AllowsConfiguredHiddenModelFromAuthenticatedResponse()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex_subscription",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            SingleModelId = "hidden-codex",
            CodexSubscriptionHttpClient = new HttpClient(new RecordingHttpMessageHandler(CreateModelsResponse())),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        };

        var models = await OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            CreateProviderDescriptor(),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("hidden-codex", models[0].Id);
        Assert.AreEqual(true, models[0].Capabilities?["hidden"]);
    }

    [TestMethod]
    public async Task ModelDiscovery_FallsBackToStaticCatalogOnlyWhenConfigured()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex_subscription",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            SingleModelId = "gpt-5.3-codex",
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

        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("gpt-5.3-codex", models[0].Id);

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
            ProviderKey = "codex_subscription",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            SingleModelId = "gpt-5.3-codex",
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
    public async Task ConcurrencyLimiter_AllowsOnlyOneTurnPerSessionAndAccountByDefault()
    {
        var first = await CodexSubscriptionConcurrencyLimiter.AcquireAsync(
            "codex_subscription",
            "session-one",
            "acct_123",
            maxConcurrentRequests: 1,
            CancellationToken.None).ConfigureAwait(false);

        var sameSession = CodexSubscriptionConcurrencyLimiter.AcquireAsync(
            "codex_subscription",
            "session-one",
            "acct_123",
            maxConcurrentRequests: 1,
            CancellationToken.None).AsTask();
        await Task.Delay(25).ConfigureAwait(false);
        Assert.IsFalse(sameSession.IsCompleted);

        await first.DisposeAsync().ConfigureAwait(false);
        var second = await sameSession.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await second.DisposeAsync().ConfigureAwait(false);

        var accountFirst = await CodexSubscriptionConcurrencyLimiter.AcquireAsync(
            "codex_subscription",
            "session-two",
            "acct_123",
            maxConcurrentRequests: 1,
            CancellationToken.None).ConfigureAwait(false);
        var sameAccount = CodexSubscriptionConcurrencyLimiter.AcquireAsync(
            "codex_subscription",
            "session-three",
            "acct_123",
            maxConcurrentRequests: 1,
            CancellationToken.None).AsTask();
        await Task.Delay(25).ConfigureAwait(false);
        Assert.IsFalse(sameAccount.IsCompleted);

        await accountFirst.DisposeAsync().ConfigureAwait(false);
        var third = await sameAccount.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await third.DisposeAsync().ConfigureAwait(false);
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
    {
        var sessionType = GetCodexSubscriptionWebSocketSessionType();
        var method = sessionType?.GetMethod(
            "TryCreateResponseUpdateMessage",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (method is null)
        {
            Assert.Fail("Codex subscription WebSocket parser was not found.");
        }

        object?[] parameters = [message, null, null, null];
        var result = (bool)method.Invoke(null, parameters)!;
        normalizedMessage = (BinaryData)parameters[1]!;
        eventType = (string?)parameters[2];
        exception = (Exception?)parameters[3];
        return result;
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
            "codex_subscription",
            new OpenAICodexSubscriptionCredential
            {
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                AccountId = "acct_from_token",
            }).ConfigureAwait(false);
    }

    private static LocalAgentProviderDescriptor CreateProviderDescriptor()
        => new()
        {
            ProtocolFamily = "openai-codex-subscription",
            ProviderKey = "codex_subscription",
            DisplayName = "Codex (ChatGPT subscription)",
            BackendId = new AgentBackendId("codex_subscription"),
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

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "openai-codex-subscription-pipeline-tests",
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
