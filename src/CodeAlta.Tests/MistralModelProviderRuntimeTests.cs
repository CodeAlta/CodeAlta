using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Mistral;
using CodeAlta.Agent.Runtime;
using Microsoft.Extensions.AI;

namespace CodeAlta.Tests;

[TestClass]
public sealed class MistralModelProviderRuntimeTests
{
    [TestMethod]
    public async Task MistralTurnExecutor_SerializesRolesAndToolDeclarations()
    {
        using var handler = new CapturingMistralHandler(CreateTextStream());
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
        });
        var argumentSchema = JsonDocument.Parse("""
            {"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}
            """).RootElement.Clone();
        var priorArguments = JsonDocument.Parse("""{"city":"Paris"}""").RootElement.Clone();
        var request = CreateTurnRequest(
            conversation:
            [
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Hello")]),
                new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.ToolCall("call_1", "lookup_weather", priorArguments)]),
                new AgentConversationMessage(
                    AgentConversationRole.Tool,
                    [new AgentMessagePart.ToolResult(
                        "call_1",
                        new AgentToolResult(true, [new AgentToolResultItem.Text("Sunny")]))]),
            ],
            tools:
            [
                new AgentToolDefinition(
                    new AgentToolSpec("lookup_weather", "Look up weather.", argumentSchema),
                    static (_, _) => Task.FromResult(new AgentToolResult(true, []))),
            ]) with
        {
            ReasoningEffort = AgentReasoningEffort.High,
            MaxOutputTokens = 123,
            ModelInfo = new AgentModelInfo(
                "mistral-small-latest",
                SupportedReasoningEfforts: [AgentReasoningEffort.High]),
        };

        var response = await executor.ExecuteTurnAsync(
            request,
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual("OK", response.Summary);
        Assert.IsNotNull(handler.LastRequestBody);
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var root = document.RootElement;
        Assert.AreEqual("mistral-small-latest", root.GetProperty("model").GetString());
        Assert.IsTrue(root.GetProperty("stream").GetBoolean());
        Assert.AreEqual(123, root.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual("high", root.GetProperty("reasoning_effort").GetString());
        var messages = root.GetProperty("messages");
        Assert.AreEqual("system", messages[0].GetProperty("role").GetString());
        Assert.AreEqual("System instructions", messages[0].GetProperty("content").GetString());
        Assert.AreEqual("user", messages[1].GetProperty("role").GetString());
        Assert.AreEqual("assistant", messages[2].GetProperty("role").GetString());
        Assert.AreEqual("call_1", messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.AreEqual("lookup_weather", messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.AreEqual("tool", messages[3].GetProperty("role").GetString());
        Assert.AreEqual("call_1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.AreEqual("Sunny", messages[3].GetProperty("content").GetString());

        var tool = root.GetProperty("tools")[0];
        Assert.AreEqual("function", tool.GetProperty("type").GetString());
        Assert.AreEqual("lookup_weather", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.AreEqual("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.AreEqual("auto", root.GetProperty("tool_choice").GetString());
        Assert.AreEqual("https://mistral.test/v1/chat/completions", handler.LastRequestUri?.AbsoluteUri);
        Assert.AreEqual("Bearer", handler.LastAuthorizationScheme);
        Assert.AreEqual("test-key", handler.LastAuthorizationParameter);
    }

    [TestMethod]
    public async Task MistralTurnExecutor_DoesNotSerializeUnsupportedReasoningEffortWhenModelInfoLimitsEfforts()
    {
        using var handler = new CapturingMistralHandler(CreateTextStream());
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
        });
        var request = CreateTurnRequest(
            conversation: [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Hello")])],
            tools: []) with
        {
            ReasoningEffort = AgentReasoningEffort.Low,
            ModelInfo = new AgentModelInfo(
                "mistral-small-latest",
                SupportedReasoningEfforts: [AgentReasoningEffort.None, AgentReasoningEffort.High]),
        };

        await executor.ExecuteTurnAsync(
            request,
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.IsNotNull(handler.LastRequestBody);
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.IsFalse(document.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [TestMethod]
    public async Task MistralTurnExecutor_DoesNotSerializeLowReasoningEffortWhenModelInfoIsUnknown()
    {
        using var handler = new CapturingMistralHandler(CreateTextStream());
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
        });
        var request = CreateTurnRequest(
            conversation: [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Hello")])],
            tools: []) with
        {
            ReasoningEffort = AgentReasoningEffort.Low,
            ModelInfo = null,
        };

        await executor.ExecuteTurnAsync(
            request,
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.IsNotNull(handler.LastRequestBody);
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.IsFalse(document.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [TestMethod]
    public async Task MistralTurnExecutor_SerializesMinimalReasoningEffortWhenModelInfoAllowsIt()
    {
        using var handler = new CapturingMistralHandler(CreateTextStream());
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
        });
        var request = CreateTurnRequest(
            conversation: [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Hello")])],
            tools: []) with
        {
            ReasoningEffort = AgentReasoningEffort.Minimal,
            ModelInfo = new AgentModelInfo(
                "mistral-reasoning-test",
                SupportedReasoningEfforts: [AgentReasoningEffort.Minimal]),
        };

        await executor.ExecuteTurnAsync(
            request,
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.IsNotNull(handler.LastRequestBody);
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.AreEqual("minimal", document.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [TestMethod]
    public async Task MistralTurnExecutor_ParsesStreamingToolCallFragments()
    {
        using var handler = new CapturingMistralHandler("""
            data: {"id":"cmpl-tool","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_2","type":"function","function":{"name":"lookup_weather","arguments":"{\"city\":\"Pa"}}]},"finish_reason":null}]}

            data: {"id":"cmpl-tool","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ris\"}"}}]},"finish_reason":null}]}

            data: {"id":"cmpl-tool","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
        });

        var response = await executor.ExecuteTurnAsync(
            CreateTurnRequest(
                conversation: [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Use the tool")])],
                tools: []),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var toolCall = response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("call_2", toolCall.CallId);
        Assert.AreEqual("lookup_weather", toolCall.Name);
        Assert.AreEqual("Paris", toolCall.Arguments.GetProperty("city").GetString());
    }

    [TestMethod]
    public async Task MistralTurnExecutor_ParsesThinkingChunksAndUsageDetails()
    {
        using var handler = new CapturingMistralHandler("""
            data: {"id":"cmpl-thinking","created":1,"model":"mistral-medium-latest","choices":[{"index":0,"delta":{"content":[{"type":"thinking","thinking":[{"type":"text","text":"Check constraints."}],"signature":"sig-1"},{"type":"text","text":"Done"}]},"finish_reason":null}]}

            data: {"id":"cmpl-thinking","created":1,"model":"mistral-medium-latest","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"prompt_tokens_details":{"cached_tokens":3,"audio_tokens":2},"completion_tokens_details":{"reasoning_tokens":4}}}

            data: [DONE]

            """);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
        });

        var response = await executor.ExecuteTurnAsync(
            CreateTurnRequest(
                conversation: [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Think")])],
                tools: []),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var reasoning = response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Single();
        Assert.AreEqual("Check constraints.", reasoning.Value);
        Assert.AreEqual("sig-1", reasoning.ProtectedData);
        Assert.AreEqual("Done", response.Summary);
        Assert.IsNotNull(response.Usage);
        Assert.AreEqual(10L, response.Usage!.LastOperation?.InputTokens);
        Assert.AreEqual(5L, response.Usage.LastOperation?.OutputTokens);
        Assert.AreEqual(15L, response.Usage.CurrentTokens);
        Assert.AreEqual(3L, response.Usage.LastOperation?.CachedInputTokens);
        Assert.AreEqual(4L, response.Usage.LastOperation?.ReasoningTokens);
    }

    [TestMethod]
    public async Task MistralTurnExecutor_FormatsRateLimitStatusWithProviderDetails()
    {
        using var handler = new CapturingMistralHandler(
            """{"object":"error","message":"Rate limit exceeded","type":"rate_limited","param":null,"code":"1300","raw_status_code":429}""",
            HttpStatusCode.TooManyRequests,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Retry-After"] = "17",
                ["x-request-id"] = "req-rate-limit",
            });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
            RetryDelayAsync = static (_, _) => Task.CompletedTask,
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(() => executor.ExecuteTurnAsync(
            CreateTurnRequest(
                conversation: [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Hello")])],
                tools: []),
            static (_, _) => ValueTask.CompletedTask)).ConfigureAwait(false);

        Assert.IsFalse(exception.Failure.IsContextOverflow);
        StringAssert.Contains(exception.Failure.Message, "Mistral rate limit");
        StringAssert.Contains(exception.Failure.Message, "HTTP 429");
        StringAssert.Contains(exception.Failure.Message, "Rate limit exceeded");
        StringAssert.Contains(exception.Failure.Message, "rate_limited");
        StringAssert.Contains(exception.Failure.Message, "1300");
        StringAssert.Contains(exception.Failure.Message, "Retry after 17 seconds");
        StringAssert.Contains(exception.Failure.Message, "req-rate-limit");
        var apiException = (MistralApiException)exception.InnerException!;
        Assert.AreEqual(HttpStatusCode.TooManyRequests, apiException.StatusCode);
        Assert.AreEqual("1300", apiException.ErrorCode);
        Assert.AreEqual("rate_limited", apiException.ErrorType);
        Assert.AreEqual(TimeSpan.FromSeconds(17), apiException.RetryAfter);
    }

    [TestMethod]
    public async Task MistralTurnExecutor_RetriesRateLimitBeforeStreamingResponse()
    {
        using var handler = new SequencedMistralHandler(
        [
            new MistralTestResponse(
                HttpStatusCode.TooManyRequests,
                """{"message":"Rate limit exceeded","type":"rate_limited","code":"1300"}""",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Retry-After"] = "0" }),
            new MistralTestResponse(HttpStatusCode.OK, CreateTextStream()),
        ]);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        var retryDelays = new List<TimeSpan>();
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
            RetryDelayAsync = (delay, _) =>
            {
                retryDelays.Add(delay);
                return Task.CompletedTask;
            },
        });

        var response = await executor.ExecuteTurnAsync(
            CreateTurnRequest(
                conversation: [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Hello")])],
                tools: []),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual("OK", response.Summary);
        Assert.AreEqual(2, handler.RequestCount);
        CollectionAssert.AreEqual(new[] { TimeSpan.Zero }, retryDelays);
    }

    [TestMethod]
    public async Task MistralTurnExecutor_PreservesContextOverflowDetectionForStatusErrors()
    {
        using var handler = new CapturingMistralHandler(
            """{"message":"prompt is too long","type":"invalid_request_error","code":"context_length_exceeded"}""",
            HttpStatusCode.UnprocessableEntity);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(() => executor.ExecuteTurnAsync(
            CreateTurnRequest(
                conversation: [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Hello")])],
                tools: []),
            static (_, _) => ValueTask.CompletedTask)).ConfigureAwait(false);

        Assert.IsTrue(exception.Failure.IsContextOverflow);
        StringAssert.Contains(exception.Failure.Message, "HTTP 422");
        StringAssert.Contains(exception.Failure.Message, "prompt is too long");
        StringAssert.Contains(exception.Failure.Message, "context_length_exceeded");
    }

    [TestMethod]
    public async Task MistralChatClient_SerializesOfficialOptionsAndReasoningReplay()
    {
        using var handler = new CapturingMistralHandler(CreateTextStream());
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        using var client = new MistralChatClient(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
        });
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""").RootElement.Clone();
        var options = new ChatOptions
        {
            ModelId = "mistral-medium-latest",
            Temperature = 0.2f,
            TopP = 0.9f,
            PresencePenalty = 0.1f,
            FrequencyPenalty = 0.3f,
            MaxOutputTokens = 321,
            Seed = 7,
            AllowMultipleToolCalls = false,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            ResponseFormat = new ChatResponseFormatJson(schema, "answer_schema", "Answer payload"),
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["metadata"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["trace"] = "abc" },
                ["n"] = 1,
                ["prompt_mode"] = "reasoning",
                ["safe_prompt"] = true,
            },
        };

        await client.GetResponseAsync(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new TextReasoningContent("Prior reasoning") { ProtectedData = "sig-prior" },
                        new TextContent("Prior answer"),
                    ]),
                new ChatMessage(ChatRole.User, [new TextContent("Continue")]),
            ],
            options).ConfigureAwait(false);

        Assert.IsNotNull(handler.LastRequestBody);
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var root = document.RootElement;
        Assert.AreEqual("mistral-medium-latest", root.GetProperty("model").GetString());
        Assert.AreEqual(0.2, root.GetProperty("temperature").GetDouble(), 0.0001);
        Assert.AreEqual(0.9, root.GetProperty("top_p").GetDouble(), 0.0001);
        Assert.AreEqual(0.1, root.GetProperty("presence_penalty").GetDouble(), 0.0001);
        Assert.AreEqual(0.3, root.GetProperty("frequency_penalty").GetDouble(), 0.0001);
        Assert.AreEqual(321, root.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual(7, root.GetProperty("random_seed").GetInt32());
        Assert.IsFalse(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.AreEqual("xhigh", root.GetProperty("reasoning_effort").GetString());
        Assert.AreEqual(1, root.GetProperty("n").GetInt32());
        Assert.AreEqual("reasoning", root.GetProperty("prompt_mode").GetString());
        Assert.IsTrue(root.GetProperty("safe_prompt").GetBoolean());
        Assert.AreEqual("abc", root.GetProperty("metadata").GetProperty("trace").GetString());
        var responseFormat = root.GetProperty("response_format");
        Assert.AreEqual("json_schema", responseFormat.GetProperty("type").GetString());
        Assert.AreEqual("answer_schema", responseFormat.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.AreEqual("Answer payload", responseFormat.GetProperty("json_schema").GetProperty("description").GetString());
        Assert.AreEqual("object", responseFormat.GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString());
        var assistantContent = root.GetProperty("messages")[0].GetProperty("content");
        Assert.AreEqual("thinking", assistantContent[0].GetProperty("type").GetString());
        Assert.AreEqual("Prior reasoning", assistantContent[0].GetProperty("thinking")[0].GetProperty("text").GetString());
        Assert.AreEqual("sig-prior", assistantContent[0].GetProperty("signature").GetString());
        Assert.AreEqual("text", assistantContent[1].GetProperty("type").GetString());
        Assert.AreEqual("Prior answer", assistantContent[1].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task MistralModelCatalog_ListsModelsOverHttpAndFiltersDuplicateModelIds()
    {
        using var handler = new CapturingMistralHandler("""
            {
              "data": [
                {
                  "id": "mistral-large-2512",
                  "name": "Mistral Large",
                  "description": "Frontier model",
                  "created": 1710000000,
                  "owned_by": "mistralai",
                  "max_context_length": 131072,
                  "aliases": ["mistral-large-latest"],
                  "capabilities": {
                    "completion_chat": true,
                    "function_calling": true,
                    "reasoning": true,
                    "vision": false,
                    "ocr": true,
                    "audio_transcription": true
                  }
                },
                {
                  "id": "mistral-large-2512",
                  "name": "Mistral Large duplicate",
                  "capabilities": { "completion_chat": true }
                },
                {
                  "id": "mistral-large-latest",
                  "name": "mistral-large-2512",
                  "capabilities": { "completion_chat": true }
                },
                {
                  "id": "voxtral-small-2507",
                  "capabilities": { "completion_chat": true }
                },
                {
                  "id": "voxtral-small-latest",
                  "name": "voxtral-small-2507",
                  "capabilities": { "completion_chat": true }
                }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
            ExtraHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Test"] = "catalog",
            },
        });
        var catalog = (IModelProviderModelCatalog)executor;

        var models = await catalog.ListModelsAsync(CreateTurnRequest([], []).Provider).ConfigureAwait(false);

        Assert.AreEqual(HttpMethod.Get, handler.LastRequestMethod);
        Assert.AreEqual("https://mistral.test/v1/models", handler.LastRequestUri?.AbsoluteUri);
        Assert.AreEqual("Bearer", handler.LastAuthorizationScheme);
        Assert.AreEqual("test-key", handler.LastAuthorizationParameter);
        Assert.AreEqual("catalog", handler.LastTestHeader);
        CollectionAssert.AreEqual(
            new[] { "mistral-large-2512", "mistral-large-latest", "voxtral-small-2507", "voxtral-small-latest" },
            models.Select(static model => model.Id).ToArray());
        var model = models.Single(static model => model.Id == "mistral-large-2512");
        Assert.AreEqual("mistral-large-2512", model.Id);
        Assert.AreEqual("Mistral Large", model.DisplayName);
        Assert.AreEqual("Frontier model", model.Description);
        Assert.AreEqual("mistral", model.Provider);
        Assert.IsNotNull(model.Capabilities);
        Assert.AreEqual(131072L, model.Capabilities!["maxContextLength"]);
        Assert.AreEqual("mistralai", model.Capabilities["ownedBy"]);
        Assert.AreEqual(1710000000L, model.Capabilities["created"]);
        var capabilities = (IReadOnlyDictionary<string, object?>)model.Capabilities["capabilities"]!;
        Assert.AreEqual(true, capabilities["completionChat"]);
        Assert.AreEqual(true, capabilities["functionCalling"]);
        Assert.AreEqual(true, capabilities["reasoning"]);
        Assert.AreEqual(false, capabilities["vision"]);
        Assert.AreEqual(true, capabilities["ocr"]);
        Assert.AreEqual(true, capabilities["audioTranscription"]);
        Assert.AreEqual(AgentReasoningEffort.High, model.DefaultReasoningEffort);
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.None, AgentReasoningEffort.High },
            model.SupportedReasoningEfforts!.ToArray());
        var nonReasoningModel = models.Single(static model => model.Id == "mistral-large-latest");
        CollectionAssert.AreEqual(
            Array.Empty<AgentReasoningEffort>(),
            nonReasoningModel.SupportedReasoningEfforts!.ToArray());
    }

    [TestMethod]
    public async Task MistralModelCatalog_MapsExplicitSupportedReasoningEffortsWhenPresent()
    {
        using var handler = new CapturingMistralHandler("""
            {
              "data": [
                {
                  "id": "mistral-reasoning-test",
                  "capabilities": {
                    "completion_chat": true,
                    "reasoning": true,
                    "reasoning_efforts": ["none", "minimal", "low", "medium", "high", "xhigh", "low"]
                  }
                }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://mistral.test/") };
        var executor = MistralModelProviderRuntime.CreateTurnExecutor(new MistralProviderOptions
        {
            ProviderKey = "mistral",
            ApiKey = "test-key",
            HttpClient = httpClient,
        });
        var catalog = (IModelProviderModelCatalog)executor;

        var models = await catalog.ListModelsAsync(CreateTurnRequest([], []).Provider).ConfigureAwait(false);

        var model = models.Single();
        CollectionAssert.AreEqual(
            new[]
            {
                AgentReasoningEffort.None,
                AgentReasoningEffort.Minimal,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
                AgentReasoningEffort.XHigh,
            },
            model.SupportedReasoningEfforts!.ToArray());
    }

    private static AgentTurnRequest CreateTurnRequest(
        IReadOnlyList<AgentConversationMessage> conversation,
        IReadOnlyList<AgentToolDefinition> tools)
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = "mistral-chat",
                ProviderKey = "mistral",
                DisplayName = "Mistral",
                TransportKind = AgentTransportKind.MistralChat,
                Profile = new AgentProviderProfile
                {
                    SupportsDeveloperRole = true,
                    SupportsStore = false,
                    StreamsUsage = true,
                    MaxTokensFieldName = "max_tokens",
                },
            },
            ProviderId = new ModelProviderId("mistral"),
            SessionId = "session-test",
            RunId = new AgentRunId("run-test"),
            ModelId = "mistral-small-latest",
            SystemMessage = "System instructions",
            Conversation = conversation,
            Tools = tools,
            State = new AgentSessionState { SessionId = "session-test", UpdatedAt = DateTimeOffset.UtcNow },
        };

    private static string CreateTextStream()
        => """
            data: {"id":"cmpl-text","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"content":"OK"},"finish_reason":null}]}

            data: {"id":"cmpl-text","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}

            data: [DONE]

            """;

    private sealed class CapturingMistralHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        IReadOnlyDictionary<string, string>? responseHeaders = null) : HttpMessageHandler, IDisposable
    {
        public string? LastRequestBody { get; private set; }

        public string? LastAuthorizationScheme { get; private set; }

        public string? LastAuthorizationParameter { get; private set; }

        public HttpMethod? LastRequestMethod { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public string? LastTestHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestMethod = request.Method;
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            LastTestHeader = request.Headers.TryGetValues("X-Test", out var values) ? values.SingleOrDefault() : null;

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream"),
            };
            if (responseHeaders is not null)
            {
                foreach (var (name, value) in responseHeaders)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return response;
        }
    }

    private sealed record MistralTestResponse(
        HttpStatusCode StatusCode,
        string Body,
        IReadOnlyDictionary<string, string>? Headers = null);

    private sealed class SequencedMistralHandler(IReadOnlyList<MistralTestResponse> responses) : HttpMessageHandler, IDisposable
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Math.Min(RequestCount, responses.Count - 1);
            RequestCount++;
            var current = responses[index];
            var response = new HttpResponseMessage(current.StatusCode)
            {
                Content = new StringContent(current.Body, Encoding.UTF8, current.StatusCode == HttpStatusCode.OK ? "text/event-stream" : "application/json"),
            };

            if (current.Headers is not null)
            {
                foreach (var (name, value) in current.Headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return Task.FromResult(response);
        }
    }
}
