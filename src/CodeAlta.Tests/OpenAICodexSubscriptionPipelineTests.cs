#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using Azure.AI.OpenAI;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.OpenAI.Codex;
using OpenAI;

namespace CodeAlta.Tests;

[TestClass]
public sealed class OpenAICodexSubscriptionPipelineTests
{
    [TestMethod]
    public void RequestContext_ProjectsReservedMetadataFromImmutableTurnIdentity()
    {
        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_752_124_567_890);
        var context = new CodexSubscriptionRequestContext(
            "session-context",
            new AgentRunId("run-context"),
            "turn",
            startedAt,
            "installation-context",
            new CodexTurnState());

        var metadata = context.ClientMetadata;
        Assert.AreEqual("session-context", metadata["session_id"]);
        Assert.AreEqual("session-context", metadata["thread_id"]);
        Assert.AreEqual("run-context", metadata["turn_id"]);
        Assert.AreEqual("installation-context", metadata["x-codex-installation-id"]);
        using var turnMetadata = JsonDocument.Parse(metadata["x-codex-turn-metadata"]);
        Assert.AreEqual("turn", turnMetadata.RootElement.GetProperty("request_kind").GetString());
        Assert.AreEqual(1_752_124_567_890, turnMetadata.RootElement.GetProperty("turn_started_at_unix_ms").GetInt64());
    }

    [TestMethod]
    public void ProtocolParser_ReadsWebSocketTurnStateOnlyFromResponseMetadataHeaders()
    {
        var parsed = CodexProtocolEventParser.Parse(
            CodexProtocolTransport.WebSocket,
            BinaryData.FromString(
                """
                {
                  "type": "response.metadata",
                  "headers": {
                    "x-codex-turn-state": "metadata-state"
                  },
                  "metadata": {
                    "x-codex-turn-state": "ignored-metadata-state"
                  }
                }
                """));

        Assert.AreEqual("metadata-state", parsed.Metadata.TurnState);
    }

    [TestMethod]
    public void WebSocketRequest_ProjectsCurrentTurnStatePerSendAndKeepsFirstValidValue()
    {
        var turnState = new CodexTurnState();
        var context = new CodexSubscriptionRequestContext(
            "session-websocket",
            new AgentRunId("run-websocket"),
            "turn",
            DateTimeOffset.UtcNow,
            installationId: null,
            turnState);
        var options = new OpenAI.Responses.CreateResponseOptions
        {
            Model = "gpt-5.3-codex",
            StreamingEnabled = true,
        };

        using var first = JsonDocument.Parse(OpenAICodexSubscriptionWebSocketSession.CreateWebSocketRequest(options, context));
        Assert.IsFalse(first.RootElement.GetProperty("client_metadata").TryGetProperty("x-codex-turn-state", out _));

        turnState.Capture("first-state");
        turnState.Capture("ignored-state");
        using var second = JsonDocument.Parse(OpenAICodexSubscriptionWebSocketSession.CreateWebSocketRequest(options, context));
        Assert.AreEqual(
            "first-state",
            second.RootElement.GetProperty("client_metadata").GetProperty("x-codex-turn-state").GetString());
    }

    [TestMethod]
    public void ResponsesLiteRequest_HasMatchingHttpAndWebSocketPayloadProperties()
    {
        var options = new OpenAI.Responses.CreateResponseOptions
        {
            Model = "gpt-5.6-sol",
            Instructions = "Developer instructions",
            StreamingEnabled = true,
        };
        options.InputItems.Add(OpenAI.Responses.ResponseItem.CreateUserMessageItem("Hello"));
        options.Tools.Add(OpenAI.Responses.ResponseTool.CreateFunctionTool(
            "inspect_file",
            BinaryData.FromString("""{"type":"object","properties":{}}"""),
            strictModeEnabled: true));
        CodexResponsesLiteRequestBuilder.Apply(options, options.Instructions);
        var context = new CodexSubscriptionRequestContext(
            "session-lite",
            new AgentRunId("run-lite"),
            "turn",
            DateTimeOffset.FromUnixTimeSeconds(1_752_124_567),
            installationId: null,
            new CodexTurnState());
        context.ApplyClientMetadata(options, includeTurnState: false);

        using var http = JsonDocument.Parse(ModelReaderWriter.Write(
            options,
            new ModelReaderWriterOptions("J"),
            OpenAIContext.Default));
        using var webSocket = JsonDocument.Parse(
            OpenAICodexSubscriptionWebSocketSession.CreateWebSocketRequest(options, context));

        Assert.AreEqual(http.RootElement.EnumerateObject().Count() + 1, webSocket.RootElement.EnumerateObject().Count());
        Assert.AreEqual("response.create", webSocket.RootElement.GetProperty("type").GetString());
        foreach (var property in http.RootElement.EnumerateObject())
        {
            Assert.IsTrue(webSocket.RootElement.TryGetProperty(property.Name, out var webSocketProperty));
            Assert.IsTrue(JsonElement.DeepEquals(property.Value, webSocketProperty), property.Name);
        }
    }

    [TestMethod]
    public void ResponsesLiteRequest_PreservesNonemptyWhitespaceInstructions()
    {
        var options = new OpenAI.Responses.CreateResponseOptions
        {
            Model = "gpt-5.6-sol",
            Instructions = " ",
        };

        CodexResponsesLiteRequestBuilder.Apply(options, options.Instructions);

        using var document = JsonDocument.Parse(ModelReaderWriter.Write(
            options,
            new ModelReaderWriterOptions("J"),
            OpenAIContext.Default));
        var input = document.RootElement.GetProperty("input");
        Assert.AreEqual("additional_tools", input[0].GetProperty("type").GetString());
        Assert.AreEqual(" ", input[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

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
        Assert.AreEqual("session_456", handler.Requests[0]["session-id"]);
        Assert.AreEqual("session_456", handler.Requests[0]["thread-id"]);
        Assert.IsFalse(handler.Requests[0].ContainsKey("session_id"));
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
                    TransportKind = AgentTransportKind.OpenAIResponses,
                }));

        Assert.AreEqual("https://chatgpt.com/backend-api/codex", client.Endpoint.ToString());
    }

    [TestMethod]
    public void SdkFactory_LeavesDefaultNetworkTimeoutForCodexSubscription()
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
        Assert.IsNull(codexOptions.NetworkTimeout);
    }

    [TestMethod]
    public void SdkFactory_UsesConfiguredNetworkTimeout()
    {
        var openAiOptions = CreateClientOptions(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            NetworkTimeout = TimeSpan.FromMinutes(5),
        });
        var azureOptions = CreateAzureClientOptions(new OpenAIProviderOptions
        {
            ProviderKey = "azure-openai",
            NetworkTimeout = TimeSpan.FromMinutes(7),
        });
        var codexOptions = CreateClientOptions(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            NetworkTimeout = TimeSpan.FromMinutes(15),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });

        Assert.AreEqual(TimeSpan.FromMinutes(5), openAiOptions.NetworkTimeout);
        Assert.AreEqual(TimeSpan.FromMinutes(7), azureOptions.NetworkTimeout);
        Assert.AreEqual(TimeSpan.FromMinutes(15), codexOptions.NetworkTimeout);
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
    public void ProtocolParser_ProjectsAllowlistedMetadataBeforeSdkUpdateAndDiscardsUnknownFields()
    {
        var protocolEvent = CodexProtocolEventParser.Parse(
            CodexProtocolTransport.WebSocket,
            BinaryData.FromString(
                """
                {
                  "type": "response.created",
                  "authorization": "Bearer secret",
                  "cookie": "session=secret",
                  "arbitrary": { "secret": "must-not-survive" },
                  "headers": {
                    "x-request-id": "request-123",
                    "OpenAI-Model": "gpt-5.3-codex",
                    "x-models-etag": "models-v1",
                    "x-reasoning-included": "true",
                    "authorization": "Bearer nested-secret"
                  },
                  "response": {
                    "id": "resp_1",
                    "object": "response",
                    "created_at": 1,
                    "model": "gpt-5.3-codex",
                    "status": "in_progress",
                    "output": []
                  }
                }
                """));

        Assert.AreEqual("response.created", protocolEvent.Type);
        Assert.AreEqual("request-123", protocolEvent.Metadata.RequestId);
        Assert.AreEqual("gpt-5.3-codex", protocolEvent.Metadata.EffectiveModel);
        Assert.AreEqual("models-v1", protocolEvent.Metadata.ModelsETag);
        Assert.AreEqual(true, protocolEvent.Metadata.ReasoningIncluded);
        Assert.IsNotNull(protocolEvent.Update, "Metadata must be available on the envelope that carries the SDK update.");
        var serialized = JsonSerializer.Serialize(protocolEvent.Metadata);
        Assert.IsFalse(serialized.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("authorization", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains("arbitrary", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ProtocolParser_DistinguishesMissingAndExplicitNullSafetyRetryModel()
    {
        var omitted = CodexProtocolEventParser.Parse(
            CodexProtocolTransport.Http,
            BinaryData.FromString("""{"type":"safety_buffering","safety_buffering":{"treatment":"buffered"}}"""));
        var explicitNull = CodexProtocolEventParser.Parse(
            CodexProtocolTransport.Http,
            BinaryData.FromString("""{"type":"safety_buffering","safety_buffering":{"retry_model":null,"treatment":"buffered"}}"""));

        Assert.IsNotNull(omitted.Metadata.SafetyBuffering);
        Assert.IsFalse(omitted.Metadata.SafetyBuffering.RetryModelPresent);
        Assert.IsNotNull(explicitNull.Metadata.SafetyBuffering);
        Assert.IsTrue(explicitNull.Metadata.SafetyBuffering.RetryModelPresent);
        Assert.IsNull(explicitNull.Metadata.SafetyBuffering.RetryModel);
    }

    [TestMethod]
    public void ProtocolParser_NormalizesNamedRateLimitsCreditsModerationAndSafetyHeaders()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("x-request-id", "request-private-id");
        response.Headers.Add("OpenAI-Model", "gpt-5.3-codex-reroute");
        response.Headers.Add("x-models-etag", "models-v2");
        response.Headers.Add("x-reasoning-included", "true");
        response.Headers.Add("x-codex-primary-used-percent", "12.5");
        response.Headers.Add("x-codex-primary-window-minutes", "300");
        response.Headers.Add("x-codex-primary-reset-at", "1770000000");
        response.Headers.Add("x-codex-other-secondary-used-percent", "8");
        response.Headers.Add("x-codex-other-secondary-window-minutes", "60");
        response.Headers.Add("x-codex-credits-has-credits", "true");
        response.Headers.Add("x-codex-credits-unlimited", "false");
        response.Headers.Add("x-codex-credits-balance", "42.5");
        response.Headers.Add("x-codex-safety-buffering-enabled", "true");
        response.Headers.Add("x-codex-safety-buffering-faster-model", "gpt-5.3-codex-fast");

        var initial = CodexProtocolEventParser.CreateInitialHttpEvent(response);
        var metadata = CodexProtocolEventParser.Parse(
            CodexProtocolTransport.WebSocket,
            BinaryData.FromString(
                """
                {
                  "type":"response.metadata",
                  "metadata": {
                    "openai_verification_recommendation":["trusted_access_for_cyber","unknown"],
                    "openai_chatgpt_moderation_metadata":{"flagged":true,"category":"cyber"},
                    "raw_secret":"must-not-survive"
                  },
                  "safety_buffering":{"message":"Checking safety","retry_model":null}
                }
                """));

        Assert.AreEqual(2, initial.Metadata.RateLimits?.Count);
        Assert.AreEqual("codex", initial.Metadata.RateLimits?[0].Name);
        Assert.AreEqual(300, initial.Metadata.RateLimits?[0].Primary?.WindowDurationMinutes);
        Assert.AreEqual("codex_other", initial.Metadata.RateLimits?[1].Name);
        Assert.AreEqual(8, initial.Metadata.RateLimits?[1].Secondary?.UsedPercent);
        Assert.AreEqual("42.5", initial.Metadata.RateLimits?[0].Credits?.Balance);
        Assert.AreEqual("gpt-5.3-codex-fast", initial.Metadata.SafetyBuffering?.FallbackRetryModel);
        Assert.AreEqual("trusted_access_for_cyber", metadata.Metadata.VerificationRecommendation);
        StringAssert.Contains(metadata.Metadata.TurnModeration, "flagged");
        Assert.IsFalse(JsonSerializer.Serialize(metadata.Metadata).Contains("raw_secret", StringComparison.Ordinal));
        Assert.IsTrue(metadata.Metadata.SafetyBuffering?.RetryModelPresent);
        Assert.IsNull(metadata.Metadata.SafetyBuffering?.RetryModel);
    }

    [TestMethod]
    public void ProtocolParser_PreservesCreditsOnlyAndDiscardsInvalidRateLimitValues()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("x-reasoning-included", "false");
        response.Headers.Add("x-codex-credits-has-credits", "true");
        response.Headers.Add("x-codex-credits-unlimited", "false");
        response.Headers.Add("x-codex-credits-balance", "7");
        response.Headers.Add("x-codex-primary-used-percent", "NaN");
        response.Headers.Add("x-codex-primary-reset-at", long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var initial = CodexProtocolEventParser.CreateInitialHttpEvent(response);
        var eventMetadata = CodexProtocolEventParser.Parse(
            CodexProtocolTransport.WebSocket,
            BinaryData.FromString(
                """
                {
                  "type":"codex.rate_limits",
                  "rate_limits":{"primary":{"used_percent":"NaN","reset_at":9223372036854775807}},
                  "credits":{"has_credits":true,"unlimited":false,"balance":"7"}
                }
                """));

        Assert.AreEqual(true, initial.Metadata.ReasoningIncluded, "Codex treats response-header presence as authoritative.");
        Assert.AreEqual("7", initial.Metadata.RateLimits?.Single().Credits?.Balance);
        Assert.IsNull(initial.Metadata.RateLimits?.Single().Primary);
        Assert.AreEqual("7", eventMetadata.Metadata.RateLimits?.Single().Credits?.Balance);
        Assert.IsNull(eventMetadata.Metadata.RateLimits?.Single().Primary?.UsedPercent);
        Assert.IsNull(eventMetadata.Metadata.RateLimits?.Single().Primary?.ResetAt);
    }

    [TestMethod]
    public void ProtocolParser_TracksEndTurnFalseTrueAndAbsentOnlyOnTerminalEvents()
    {
        static CodexProtocolEvent Parse(string endTurn)
            => CodexProtocolEventParser.Parse(
                CodexProtocolTransport.Http,
                BinaryData.FromString("{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\"" + endTurn + "}}"));

        Assert.AreEqual(false, Parse(",\"end_turn\":false").Terminal?.EndTurn);
        Assert.AreEqual(true, Parse(",\"end_turn\":true").Terminal?.EndTurn);
        Assert.IsNull(Parse(string.Empty).Terminal?.EndTurn);
        var nonterminal = CodexProtocolEventParser.Parse(
            CodexProtocolTransport.Http,
            BinaryData.FromString("""{"type":"response.created","response":{"end_turn":false}}"""));
        Assert.IsNull(nonterminal.Terminal);
    }

    [TestMethod]
    public void ProtocolParser_ToleratesUnknownAndMalformedNonterminalEvents()
    {
        var unknown = CodexProtocolEventParser.Parse(
            CodexProtocolTransport.Http,
            BinaryData.FromString("""{"type":"response.future_extension","future":true}"""));
        var malformed = CodexProtocolEventParser.Parse(
            CodexProtocolTransport.Http,
            BinaryData.FromString("not-json"),
            "response.future_extension");

        Assert.AreEqual("response.future_extension", unknown.Type);
        Assert.IsNotNull(unknown.Update);
        Assert.AreEqual("response.future_extension", malformed.Type);
        Assert.IsNull(malformed.Update);
    }

    [TestMethod]
    public async Task HttpStreamSession_EmitsInitialMetadataBeforeSseUpdatesAndUsesConfiguredClient()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp_http\",\"object\":\"response\",\"created_at\":1,\"model\":\"gpt-5.3-codex\",\"status\":\"in_progress\",\"output\":[]}}\n\n" +
                "data: {\"type\":\"response.completed\",\"sequence_number\":1,\"response\":{\"id\":\"resp_http\",\"object\":\"response\",\"created_at\":1,\"model\":\"gpt-5.3-codex\",\"status\":\"completed\",\"output\":[]}}\n\n"),
        };
        response.Headers.Add("x-request-id", "request-http");
        response.Headers.Add("OpenAI-Model", "gpt-5.3-codex");
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        var handler = new RecordingHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            HttpClient = httpClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        };
        var authManager = OpenAIProviderSdkFactory.CreateCodexSubscriptionAuthManager(
            provider,
            provider.CodexSubscription,
            temp.Path);
        var session = new CodexSubscriptionHttpStreamSession(
            provider,
            authManager,
            new CodexSubscriptionRequestContext(
                "session-http",
                new AgentRunId("run-http"),
                "turn",
                DateTimeOffset.UtcNow,
                installationId: null,
                new CodexTurnState()));
        var options = new OpenAI.Responses.CreateResponseOptions
        {
            Model = "gpt-5.3-codex",
            StreamingEnabled = true,
        };

        var events = await session.CreateResponseStreamingAsync(options, CancellationToken.None)
            .ToListAsync()
            .ConfigureAwait(false);

        Assert.AreEqual(3, events.Count);
        Assert.IsNull(events[0].Type);
        Assert.AreEqual("request-http", events[0].Metadata.RequestId);
        Assert.IsNull(events[0].Update);
        Assert.IsInstanceOfType<OpenAI.Responses.StreamingResponseCreatedUpdate>(events[1].Update);
        Assert.IsInstanceOfType<OpenAI.Responses.StreamingResponseCompletedUpdate>(events[2].Update);
        Assert.AreEqual("Bearer access-token", handler.Requests[0]["Authorization"]);
        Assert.AreEqual("text/event-stream", handler.Requests[0]["Accept"]);
        Assert.AreEqual("https://chatgpt.com/backend-api/codex/responses", handler.RequestUris[0].ToString());
        Assert.AreEqual("session-http", handler.Requests[0]["session-id"]);
        Assert.AreEqual("session-http", handler.Requests[0]["thread-id"]);
        Assert.AreEqual("session-http", handler.Requests[0]["x-client-request-id"]);
        Assert.IsFalse(handler.Requests[0].ContainsKey("session_id"));
        Assert.IsFalse(handler.Requests[0].ContainsKey("OpenAI-Beta"));
        Assert.IsTrue(handler.Requests[0].ContainsKey("x-codex-turn-metadata"));
    }

    [TestMethod]
    public async Task WebSocketSession_ReceiveFrameTimesOutWhenSocketIsSilent()
    {
        using var webSocket = new SilentWebSocket();

        var exception = await Assert.ThrowsExactlyAsync<OpenAIResponsesTransportException>(
                () => OpenAICodexSubscriptionWebSocketSession.ReceiveFrameWithIdleTimeoutAsync(
                    webSocket,
                    new ArraySegment<byte>(new byte[16]),
                    TimeSpan.FromMilliseconds(10),
                    CancellationToken.None))
            .ConfigureAwait(false);

        Assert.AreEqual(OpenAIResponsesTransportErrorCode.WebSocketReceiveIdleTimeout, exception.ErrorCode);
    }

    [TestMethod]
    public void StaticModelCatalog_ReturnsVisiblePickerModels()
    {
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "codex",
            ProviderKey = "codex",
            DisplayName = "Codex",
            TransportKind = AgentTransportKind.OpenAIResponses,
        };

        var visibleModels = CodexSubscriptionStaticModelCatalog.List(provider);
        Assert.AreEqual(
            "gpt-5.6-sol|gpt-5.6-terra|gpt-5.6-luna|gpt-5.5|gpt-5.4|gpt-5.4-mini|gpt-5.3-codex|gpt-5.2",
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
        Assert.IsTrue(visibleModels.Where(static model => model.Id.StartsWith("gpt-5.6-", StringComparison.Ordinal)).All(
            static model => Equals(true, model.Capabilities?["useResponsesLite"])));
        Assert.AreEqual(
            false,
            visibleModels.Single(static model => model.Id == "gpt-5.2").Capabilities?["supportsImageDetailOriginal"]);
        CollectionAssert.AreEqual(
            new[]
            {
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
                AgentReasoningEffort.XHigh,
                AgentReasoningEffort.Max,
            },
            visibleModels.Single(static model => model.Id == "gpt-5.6-sol").SupportedReasoningEfforts!.ToArray());
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
        Assert.AreEqual(true, models[0].Capabilities?["supportsReasoningSummaries"]);
        Assert.AreEqual(true, models[0].Capabilities?["supportVerbosity"]);
        Assert.AreEqual(false, models[0].Capabilities?["supportsParallelToolCalls"]);
        Assert.AreEqual(true, models[0].Capabilities?["supportsImageDetailOriginal"]);
        Assert.AreEqual(false, models[0].Capabilities?["useResponsesLite"]);
        Assert.AreEqual("\"models-fixture-etag\"", models[0].Capabilities?["etag"]);
        Assert.AreEqual("websocket-only-codex", models[1].Id);
        Assert.AreEqual(true, models[1].Capabilities?["requiresWebSocket"]);
        var version = typeof(OpenAIProviderSdkFactory).Assembly.GetName().Version!;
        Assert.AreEqual(
            $"https://chatgpt.com/backend-api/codex/models?client_version={version.Major}.{version.Minor}.{version.Build}",
            handler.RequestUris[0].ToString());
        Assert.AreEqual("Bearer access-token", handler.Requests[0]["Authorization"]);
        Assert.AreEqual("acct_configured", handler.Requests[0]["ChatGPT-Account-Id"]);
        Assert.AreEqual("codealta", handler.Requests[0]["originator"]);
        Assert.IsFalse(handler.Requests[0].ContainsKey("OpenAI-Beta"));
        Assert.AreEqual(OpenAIProviderSdkFactory.CreateCodeAltaUserAgentApplicationId(), handler.Requests[0]["User-Agent"]);
    }

    [TestMethod]
    public void SubscriptionEndpoints_PreserveConfiguredQueryParameters()
    {
        var baseUri = new Uri("https://example.test/backend-api/codex?tenant=alpha%20one");

        var models = CodexSubscriptionModelDiscoveryClient.CreateModelsUri(baseUri, "CodeAlta/1.2.3.4");
        var responses = CodexSubscriptionHttpRequestFactory.ResolveEndpoint(baseUri, "responses");
        var webSocket = OpenAICodexSubscriptionWebSocketSession.ResolveWebSocketUri(baseUri);

        Assert.AreEqual("https://example.test/backend-api/codex/models?tenant=alpha%20one&client_version=1.2.3", models.AbsoluteUri);
        Assert.AreEqual("https://example.test/backend-api/codex/responses?tenant=alpha%20one", responses.AbsoluteUri);
        Assert.AreEqual("wss://example.test/backend-api/codex/responses?tenant=alpha%20one", webSocket.AbsoluteUri);
    }

    [TestMethod]
    public async Task ModelDiscovery_RetriesOnlyTransientFailuresAndUsesSuccessEtagHeader()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var success = CreateModelsResponse();
        success.Headers.Remove("ETag");
        success.Headers.TryAddWithoutValidation("X-Models-Etag", "success-etag");
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("temporary") },
            success);
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            HttpClient = new HttpClient(handler),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ModelDiscovery = "codex_endpoint",
                SendResponsesBetaHeader = true,
            },
        };

        var models = await OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            CreateProviderDescriptor(),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("success-etag", models[0].Capabilities?["etag"]);
        Assert.IsFalse(handler.Requests[0].ContainsKey("OpenAI-Beta"), "The legacy Responses beta header is turn-only.");
    }

    [TestMethod]
    public async Task ModelDiscovery_DoesNotRetryClientErrorsAndHonorsOuterTimeout()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var authManager = OpenAIProviderSdkFactory.CreateCodexSubscriptionAuthManager(
            new OpenAIProviderOptions { ProviderKey = "codex", StateRootPath = temp.Path },
            new OpenAICodexSubscriptionOptions(),
            temp.Path);
        var badRequestHandler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad") },
            CreateModelsResponse());
        var badRequestClient = new CodexSubscriptionModelDiscoveryClient(
            new HttpClient(badRequestHandler),
            authManager,
            new OpenAICodexSubscriptionOptions(),
            "CodeAlta/1.2.3",
            timeout: TimeSpan.FromSeconds(1),
            retryDelay: TimeSpan.Zero);

        _ = await Assert.ThrowsExactlyAsync<CodexSubscriptionModelDiscoveryException>(
            () => badRequestClient.GetModelsAsync(new Uri("https://example.test/codex"), CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.AreEqual(1, badRequestHandler.Requests.Count);

        var timeoutClient = new CodexSubscriptionModelDiscoveryClient(
            new HttpClient(new WaitingHttpMessageHandler()),
            authManager,
            new OpenAICodexSubscriptionOptions(),
            "CodeAlta/1.2.3",
            timeout: TimeSpan.FromMilliseconds(25),
            retryDelay: TimeSpan.Zero);
        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => timeoutClient.GetModelsAsync(new Uri("https://example.test/codex"), CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ModelDiscovery_UsesAdvertisedMaxAndIgnoresUnsupportedUltraReasoningEffort()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "models": [
                    {
                      "slug": "gpt-5.6-sol",
                      "display_name": "GPT-5.6 Sol",
                      "supported_in_api": true,
                      "visibility": "list",
                      "default_reasoning_level": "low",
                      "supported_reasoning_levels": [
                        { "effort": "low", "description": "Fast" },
                        { "effort": "xhigh", "description": "Extra high" },
                        { "effort": "max", "description": "Maximum" },
                        { "effort": "ultra", "description": "Maximum with delegation" }
                      ]
                    }
                  ]
                }
                """),
        };
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            CodexSubscriptionHttpClient = new HttpClient(new RecordingHttpMessageHandler(response)),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ModelDiscovery = "codex_endpoint",
            },
        };

        var models = await OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            CreateProviderDescriptor(),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, models.Count);
        Assert.AreEqual(AgentReasoningEffort.Low, models[0].DefaultReasoningEffort);
        CollectionAssert.AreEqual(
            new[]
            {
                AgentReasoningEffort.Low,
                AgentReasoningEffort.XHigh,
                AgentReasoningEffort.Max,
            },
            models[0].SupportedReasoningEfforts!.ToArray());
    }

    [TestMethod]
    public async Task ModelDiscovery_UsesCurrentSummaryCapabilityAndBackwardCompatibleDefault()
    {
        using var temp = TempDirectory.Create();
        await SaveCredentialAsync(temp.Path).ConfigureAwait(false);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "models": [
                    {
                      "slug": "gpt-5.6-sol",
                      "display_name": "GPT-5.6 Sol",
                      "supported_in_api": true,
                      "visibility": "list",
                      "supports_reasoning_summary_parameter": true,
                      "support_verbosity": false,
                      "supports_parallel_tool_calls": false,
                      "supports_image_detail_original": false,
                      "use_responses_lite": true,
                      "supported_reasoning_levels": [{ "effort": "low" }]
                    },
                    {
                      "slug": "gpt-5.5",
                      "display_name": "GPT-5.5",
                      "supported_in_api": true,
                      "visibility": "list",
                      "supports_reasoning_summaries": "true",
                      "support_verbosity": 1,
                      "supports_parallel_tool_calls": null,
                      "use_responses_lite": []
                    },
                    {
                      "slug": "gpt-5.4",
                      "display_name": "GPT-5.4",
                      "supported_in_api": true,
                      "visibility": "list"
                    }
                  ]
                }
                """),
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"exact-etag\"");
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
            StateRootPath = temp.Path,
            CodexSubscriptionHttpClient = new HttpClient(new RecordingHttpMessageHandler(response)),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ModelDiscovery = "codex_endpoint",
            },
        };

        var models = await OpenAIProviderSdkFactory.ListModelsAsync(
            provider,
            CreateProviderDescriptor(),
            CancellationToken.None).ConfigureAwait(false);

        var lite = models.Single(static model => model.Id == "gpt-5.6-sol");
        Assert.AreEqual(true, lite.Capabilities?["supportsReasoningSummaries"]);
        Assert.AreEqual(false, lite.Capabilities?["supportVerbosity"]);
        Assert.AreEqual(false, lite.Capabilities?["supportsParallelToolCalls"]);
        Assert.AreEqual(false, lite.Capabilities?["supportsImageDetailOriginal"]);
        Assert.AreEqual(true, lite.Capabilities?["useResponsesLite"]);
        Assert.AreEqual("\"exact-etag\"", lite.Capabilities?["etag"]);

        var malformed = models.Single(static model => model.Id == "gpt-5.5");
        Assert.AreEqual(false, malformed.Capabilities?["supportsReasoningSummaries"]);
        Assert.AreEqual(false, malformed.Capabilities?["supportVerbosity"]);
        Assert.AreEqual(false, malformed.Capabilities?["supportsParallelToolCalls"]);
        Assert.AreEqual(false, malformed.Capabilities?["supportsImageDetailOriginal"]);
        Assert.AreEqual(false, malformed.Capabilities?["useResponsesLite"]);

        var omitted = models.Single(static model => model.Id == "gpt-5.4");
        Assert.AreEqual(true, omitted.Capabilities?["supportsReasoningSummaries"]);
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
            "gpt-5.6-sol|gpt-5.6-terra|gpt-5.6-luna|gpt-5.5|gpt-5.4|gpt-5.4-mini|gpt-5.3-codex|gpt-5.2",
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

    private static AzureOpenAIClientOptions CreateAzureClientOptions(OpenAIProviderOptions provider)
    {
        var method = typeof(OpenAIProviderSdkFactory).GetMethod(
            "CreateAzureOpenAIClientOptions",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Azure OpenAI client option factory was not found.");
        return (AzureOpenAIClientOptions)method.Invoke(null, [provider, null])!;
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
            TransportKind = AgentTransportKind.OpenAIResponses,
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
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
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
                      "supports_reasoning_summaries": true,
                      "support_verbosity": true,
                      "supports_parallel_tool_calls": false,
                      "supports_image_detail_original": true,
                      "use_responses_lite": false,
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
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"models-fixture-etag\"");
        return response;
    }

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

    private sealed class WaitingHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable.");
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
