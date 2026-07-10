#pragma warning disable OPENAI001

using System.ClientModel;
#pragma warning disable SCME0001
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeAlta.App;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.OpenAI.Codex;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace CodeAlta.Tests;

[TestClass]
public sealed class OpenAIRawApiModelProviderRuntimeTests
{
    [TestMethod]
    public void OpenAIResponsesTurnExecutor_MapsMaxInferenceEffort()
    {
        Assert.AreEqual(
            new ResponseReasoningEffortLevel("max"),
            OpenAIResponsesTurnExecutor.MapReasoningEffortLevel(AgentReasoningEffort.Max));
    }

    [TestMethod]
    public async Task OpenAIResponsesModelProviderRuntime_UsesLocalReplayAndDoesNotSetPreviousResponseId()
    {
        using var temp = TestTempDirectory.Create();
        var responsesClient = new RecordingOpenAIResponseClient(
            [
                [
                    CreateToolCallResponseUpdate(
                        responseId: "response-1",
                        modelId: "gpt-test",
                        callId: "call-1",
                        toolName: "inspect_file",
                        arguments: """{"path":"README.md"}""",
                        summaryText: "Need to inspect a file."),
                ],
                [
                    CreateAssistantResponseUpdate(
                        responseId: "response-2",
                        modelId: "gpt-test",
                        text: "Inspection complete.",
                        reasoningText: "Looked at the file.",
                        encryptedReasoning: "sig-1",
                        inputTokens: 33,
                        outputTokens: 0),
                ],
            ]);

        await using var providerRuntime = new OpenAIResponsesModelProviderRuntime(new OpenAIResponsesModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "openai",
                    IsDefault = true,
                    ResponsesClientFactory = _ => responsesClient,
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo(
                            "gpt-test",
                            DisplayName: "GPT Test",
                            SupportedReasoningEfforts: [AgentReasoningEffort.Max],
                            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["inputTokenLimit"] = 200000L,
                            }),
                    ]),
                },
            },
        });

        var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);
        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("gpt-test", models[0].Id);

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-test",
            WorkingDirectory = temp.Path,
            SystemMessage = "System instructions",
            DeveloperInstructions = "Developer instructions",
            ReasoningEffort = AgentReasoningEffort.Max,
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec(
                        "inspect_file",
                        "Inspect a file.",
                        JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""").RootElement.Clone()),
                    static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("README contents")]))),
            ],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Inspect README")]),
        }).ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.Requests.Count);
        StringAssert.StartsWith(responsesClient.Requests[0].Options.Instructions, "System instructions");
        StringAssert.Contains(responsesClient.Requests[0].Options.Instructions, "<developer_instructions>");
        Assert.IsNull(responsesClient.Requests[0].Options.PreviousResponseId);
        Assert.IsNull(responsesClient.Requests[1].Options.PreviousResponseId);
        Assert.IsNotNull(responsesClient.Requests[0].Options.ReasoningOptions);
        Assert.AreEqual(new ResponseReasoningEffortLevel("max"), responsesClient.Requests[0].Options.ReasoningOptions!.ReasoningEffortLevel);
        Assert.AreEqual(ResponseReasoningSummaryVerbosity.Detailed, responsesClient.Requests[0].Options.ReasoningOptions.ReasoningSummaryVerbosity);
        Assert.IsTrue(
            responsesClient.Requests[1].InputItems.OfType<FunctionCallOutputResponseItem>()
                .Any(static item => item.CallId == "call-1" && item.FunctionOutput == "README contents"));

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.ToolOutput && e.Content == "README contents"));
        var completedReasoning = history.OfType<AgentContentCompletedEvent>().Single(static e =>
            e.Kind == AgentContentKind.Reasoning && e.Content == "Looked at the file.");
        Assert.AreEqual(
            "Looked at the file.",
            completedReasoning.Details!.Value.GetProperty("summaryParts")[0].GetString());
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Assistant && e.Content == "Inspection complete."));
        var usageEvent = history.OfType<AgentSessionUpdateEvent>().Last(static e => e.Kind == AgentSessionUpdateKind.UsageUpdated);
        Assert.IsNotNull(usageEvent.Usage);
        Assert.IsTrue(usageEvent.Usage.CurrentTokens > 33L);
        Assert.AreEqual(33L, usageEvent.Usage.LastOperation?.InputTokens + usageEvent.Usage.LastOperation?.OutputTokens);
        Assert.AreEqual(200000L, usageEvent.Usage.TokenLimit);
        Assert.AreEqual(4, usageEvent.Usage.MessageCount);

        var metadata = (await CreateSessionStore(temp.Path).ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Single();
        var details = Assert.IsInstanceOfType<RawApiSessionMetadataDetails>(metadata.Details);
        Assert.AreEqual("response-2", details.ProviderSessionId);
    }

    [TestMethod]
    public async Task OpenAIChatModelProviderRuntime_MergesDeveloperInstructionsWhenProfileDisablesDeveloperRole()
    {
        using var temp = TestTempDirectory.Create();
        var chatClient = new RecordingOpenAIChatClient(
            [
                OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                    completionId: "chatcmpl-1",
                    contentUpdate: [ChatMessageContentPart.CreateTextPart("OpenAI chat answer.")],
                    model: "gpt-chat-test",
                    usage: OpenAIChatModelFactory.ChatTokenUsage(
                        outputTokenCount: 7,
                        inputTokenCount: 11,
                        totalTokenCount: 18,
                        outputTokenDetails: OpenAIChatModelFactory.ChatOutputTokenUsageDetails(reasoningTokenCount: 2),
                        inputTokenDetails: OpenAIChatModelFactory.ChatInputTokenUsageDetails(cachedTokenCount: 3))),
            ]);

        await using var providerRuntime = new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "compat-provider",
                    IsDefault = true,
                    Profile = new AgentProviderProfile
                    {
                        SupportsDeveloperRole = false,
                        SupportsReasoningEffort = true,
                        SupportsStore = false,
                        StreamsUsage = true,
                    },
                    ChatClientFactory = _ => chatClient,
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo(
                            "gpt-chat-test",
                            DisplayName: "GPT Chat Test",
                            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["contextWindowTokens"] = 128000L,
                            }),
                    ]),
                },
            },
        });

        _ = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-chat-test",
            WorkingDirectory = temp.Path,
            SystemMessage = "System instructions",
            DeveloperInstructions = "Developer instructions",
            ReasoningEffort = AgentReasoningEffort.High,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        Assert.AreEqual(1, chatClient.Requests.Count);
        Assert.AreEqual(2, chatClient.Requests[0].Messages.Count);
        var systemMessage = Assert.IsInstanceOfType<SystemChatMessage>(chatClient.Requests[0].Messages[0]);
        var systemText = string.Concat(systemMessage.Content.Select(static part => part.Text));
        StringAssert.Contains(systemText, "System instructions");
        StringAssert.Contains(systemText, "Developer instructions");
        Assert.IsFalse(chatClient.Requests[0].Messages.Any(static message => message.GetType().Name.Contains("Developer", StringComparison.Ordinal)));
        Assert.IsNotNull(chatClient.Requests[0].Options);
        Assert.AreEqual(ChatReasoningEffortLevel.High, chatClient.Requests[0].Options!.ReasoningEffortLevel);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Assistant && e.Content == "OpenAI chat answer."));
        var usageEvent = history.OfType<AgentSessionUpdateEvent>().Single(static e => e.Kind == AgentSessionUpdateKind.UsageUpdated);
        Assert.IsNotNull(usageEvent.Usage);
        Assert.IsTrue(usageEvent.Usage.CurrentTokens > 18L);
        Assert.AreEqual(18L, usageEvent.Usage.LastOperation?.InputTokens + usageEvent.Usage.LastOperation?.OutputTokens);
        Assert.AreEqual(128000L, usageEvent.Usage.TokenLimit);
        Assert.AreEqual(2, usageEvent.Usage.MessageCount);

        var metadata = (await CreateSessionStore(temp.Path).ListSessionsAsync().ToArrayAsync().ConfigureAwait(false)).Single();
        var details = Assert.IsInstanceOfType<RawApiSessionMetadataDetails>(metadata.Details);
        Assert.AreEqual("chatcmpl-1", details.ProviderSessionId);
    }

    [TestMethod]
    public async Task OpenAIChatModelProviderRuntime_OmitsReasoningEffortWhenSetToNone()
    {
        using var temp = TestTempDirectory.Create();
        var chatClient = new RecordingOpenAIChatClient(
            [
                OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                    completionId: "chatcmpl-1",
                    contentUpdate: [ChatMessageContentPart.CreateTextPart("OpenAI chat answer.")],
                    model: "gpt-chat-test"),
            ]);

        await using var providerRuntime = new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "compat-provider",
                    IsDefault = true,
                    Profile = new AgentProviderProfile
                    {
                        SupportsDeveloperRole = true,
                        SupportsReasoningEffort = true,
                        SupportsStore = false,
                        StreamsUsage = true,
                    },
                    ChatClientFactory = _ => chatClient,
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo("gpt-chat-test", DisplayName: "GPT Chat Test"),
                    ]),
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-chat-test",
            WorkingDirectory = temp.Path,
            ReasoningEffort = AgentReasoningEffort.None,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        Assert.AreEqual(1, chatClient.Requests.Count);
        Assert.IsNotNull(chatClient.Requests[0].Options);
        Assert.IsNull(chatClient.Requests[0].Options!.ReasoningEffortLevel);
    }

    [TestMethod]
    public async Task OpenAIChatTurnExecutor_UsesConfiguredMaxTokenFieldAndOmitsUnsupportedParallelToolCalls()
    {
        var chatClient = new RecordingOpenAIChatClient(
        [
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                completionId: "chatcmpl-compat",
                contentUpdate: [ChatMessageContentPart.CreateTextPart("OK")],
                model: "qwen-test"),
        ]);
        var profile = new AgentProviderProfile
        {
            SupportsStore = false,
            SupportsParallelToolCalls = false,
            MaxTokensFieldName = "max_tokens",
        };
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "alibaba",
            Profile = profile,
            ChatClientFactory = _ => chatClient,
        };
        var executor = new OpenAIChatTurnExecutor(provider);

        _ = await executor.ExecuteTurnAsync(
                new AgentTurnRequest
                {
                    Provider = new ModelProviderRuntimeDescriptor
                    {
                        ProtocolFamily = "openai",
                        ProviderKey = "alibaba",
                        DisplayName = "Alibaba",
                        TransportKind = AgentTransportKind.OpenAIChatCompletions,
                        Profile = profile,
                    },
                    ProviderId = new ModelProviderId("alibaba"),
                    SessionId = "session-1",
                    RunId = new AgentRunId("run-1"),
                    ModelId = "qwen-test",
                    MaxOutputTokens = 1234,
                    Conversation =
                    [
                        new AgentConversationMessage(
                            AgentConversationRole.User,
                            [new AgentMessagePart.Text("Hello")]),
                    ],
                    Tools =
                    [
                        new AgentToolDefinition(
                            new AgentToolSpec(
                                "inspect_file",
                                "Inspect a file.",
                                JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""").RootElement.Clone()),
                            static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("ok")]))),
                    ],
                    State = new AgentSessionState
                    {
                        SessionId = "session-1",
                        ProtocolFamily = "openai",
                        ProviderKey = "alibaba",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                },
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(1, chatClient.Requests.Count);
        var options = chatClient.Requests[0].Options;
        Assert.IsNotNull(options);
        Assert.IsNull(options!.MaxOutputTokenCount);
        Assert.IsNull(options.AllowParallelToolCalls);
        Assert.IsTrue(options.Patch.TryGetValue("$.max_tokens"u8, out int maxTokens));
        Assert.AreEqual(1234, maxTokens);
    }

    [TestMethod]
    public async Task OpenAIChatTurnExecutor_EnablesParallelToolCallsByDefaultWhenToolsAreAvailable()
    {
        var chatClient = new RecordingOpenAIChatClient(
        [
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                completionId: "chatcmpl-tools",
                contentUpdate: [ChatMessageContentPart.CreateTextPart("OK")],
                model: "gpt-test"),
        ]);
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ChatClientFactory = _ => chatClient,
        };
        var executor = new OpenAIChatTurnExecutor(provider);

        _ = await executor.ExecuteTurnAsync(
                new AgentTurnRequest
                {
                    Provider = new ModelProviderRuntimeDescriptor
                    {
                        ProtocolFamily = "openai",
                        ProviderKey = "openai",
                        DisplayName = "OpenAI",
                        TransportKind = AgentTransportKind.OpenAIChatCompletions,
                    },
                    ProviderId = new ModelProviderId("openai"),
                    SessionId = "session-1",
                    RunId = new AgentRunId("run-1"),
                    ModelId = "gpt-test",
                    Conversation =
                    [
                        new AgentConversationMessage(
                            AgentConversationRole.User,
                            [new AgentMessagePart.Text("Inspect README")]),
                    ],
                    Tools =
                    [
                        new AgentToolDefinition(
                            new AgentToolSpec(
                                "inspect_file",
                                "Inspect a file.",
                                JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""").RootElement.Clone()),
                            static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("ok")]))),
                    ],
                    State = new AgentSessionState
                    {
                        SessionId = "session-1",
                        ProtocolFamily = "openai",
                        ProviderKey = "openai",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                },
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(1, chatClient.Requests.Count);
        Assert.AreEqual(true, chatClient.Requests[0].Options!.AllowParallelToolCalls);
    }

    [TestMethod]
    public async Task OpenAIChatTurnExecutor_OmitsStrictToolSchemaWhenProfileDisablesStrictTools()
    {
        using var temp = TestTempDirectory.Create();
        var handler = new StaticHttpMessageHandler(CreateChatStreamingResponse("chatcmpl-nonstrict", "OK"));
        var profile = new AgentProviderProfile
        {
            SupportsDeveloperRole = true,
            SupportsStore = false,
            SupportsParallelToolCalls = false,
            SupportsStrictTools = false,
            SupportsReasoningEffort = true,
            StreamsUsage = true,
            MaxTokensFieldName = "max_tokens",
            ReasoningFieldNames = ["reasoning_content"],
        };

        await using var providerRuntime = new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "xiaomi",
                    IsDefault = true,
                    ApiKey = "test-key",
                    BaseUri = new Uri("https://token-plan-ams.xiaomimimo.com/v1"),
                    HttpClient = new HttpClient(handler),
                    Profile = profile,
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo("mimo-v2.5-pro", DisplayName: "MiMo Test"),
                    ]),
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "mimo-v2.5-pro",
            WorkingDirectory = temp.Path,
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec(
                        "inspect_file",
                        "Inspect a file.",
                        JsonDocument.Parse(
                            """
                            {
                              "type": "object",
                              "properties": {
                                "path": { "type": "string", "minLength": 1 },
                                "recursive": { "type": "boolean" }
                              },
                              "required": ["path"]
                            }
                            """).RootElement.Clone()),
                    static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("ok")]))),
            ],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        var body = handler.RequestBodies.Single();
        using var requestDocument = JsonDocument.Parse(body);
        var inspectTool = requestDocument.RootElement.GetProperty("tools").EnumerateArray()
            .Select(static tool => tool.GetProperty("function"))
            .Single(static function => function.GetProperty("name").GetString() == "inspect_file");
        Assert.IsFalse(inspectTool.TryGetProperty("strict", out _), "Non-strict providers must omit the OpenAI strict tool flag.");
        var parameters = inspectTool.GetProperty("parameters");
        Assert.AreEqual(1, parameters.GetProperty("properties").GetProperty("path").GetProperty("minLength").GetInt32());
        CollectionAssert.AreEqual(
            new[] { "path" },
            parameters.GetProperty("required").EnumerateArray().Select(static item => item.GetString()).ToArray());
        Assert.IsFalse(parameters.TryGetProperty("additionalProperties", out _), "Non-strict providers must keep the original schema instead of strict-normalizing it.");
    }

    [TestMethod]
    public async Task OpenAIChatTurnExecutor_RejectsEmptyStreamWithTypedProtocolError()
    {
        var chatClient = new RecordingOpenAIChatClient([]);
        var executor = new OpenAIChatTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ChatClientFactory = _ => chatClient,
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    CreateChatTurnRequest(),
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        var protocolException = Assert.IsInstanceOfType<OpenAIChatProtocolException>(exception.InnerException);
        Assert.AreEqual(OpenAIChatProtocolErrorCode.StreamCompletedWithoutAssistantContent, protocolException.ErrorCode);
        Assert.AreEqual(1, chatClient.Requests.Count);
    }

    [TestMethod]
    public async Task OpenAIChatTurnExecutor_RejectsMalformedToolArgumentsAndTracesDiagnostic()
    {
        using var temp = TestTempDirectory.Create();
        var chatClient = new RecordingOpenAIChatClient(
        [
            DeserializeStreamingChatCompletionUpdate(
                """
                {
                  "id": "chatcmpl-bad-tool",
                  "object": "chat.completion.chunk",
                  "created": 1744060800,
                  "model": "gpt-test",
                  "choices": [
                    {
                      "index": 0,
                      "delta": {
                        "tool_calls": [
                          {
                            "index": 0,
                            "id": "call-bad",
                            "type": "function",
                            "function": {
                              "name": "inspect_file",
                              "arguments": "<tool name=\"inspect_file\"><path>README.md</path></tool>"
                            }
                          }
                        ]
                      }
                    }
                  ]
                }
                """),
        ]);
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ChatClientFactory = _ => chatClient,
            ProtocolTracing = new OpenAIProtocolTraceOptions
            {
                Enabled = true,
                StateRootPath = temp.Path,
            },
        };
        var executor = new OpenAIChatTurnExecutor(provider);
        var request = CreateChatTurnRequest() with
        {
            SessionId = "session-malformed-tool",
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec(
                        "inspect_file",
                        "Inspect a file.",
                        JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""").RootElement.Clone()),
                    static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("ok")]))),
            ],
        };

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    request,
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        var protocolException = Assert.IsInstanceOfType<OpenAIChatProtocolException>(exception.InnerException);
        Assert.AreEqual(OpenAIChatProtocolErrorCode.InvalidToolCallArguments, protocolException.ErrorCode);
        var tracePath = new AgentRuntimePathLayout(temp.Path).GetSessionTraceFilePath(request.SessionId);
        var trace = File.ReadAllText(tracePath);
        StringAssert.Contains(trace, "!!! Malformed OpenAI chat tool-call arguments");
        StringAssert.Contains(trace, "call=call-bad");
        StringAssert.Contains(trace, "\\u003Ctool");
    }

    [TestMethod]
    public async Task OpenAIChatModelProviderRuntime_ProtocolTracing_WritesSessionTrace()
    {
        using var temp = TestTempDirectory.Create();
        var handler = new StaticHttpMessageHandler(CreateChatStreamingResponse("chatcmpl-trace", "Trace answer."));

        await using var providerRuntime = new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "openai",
                    IsDefault = true,
                    ApiKey = "test-key",
                    BaseUri = new Uri("https://api.openai.test/v1"),
                    HttpClient = new HttpClient(handler),
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo("gpt-chat-test", DisplayName: "GPT Chat Test"),
                    ]),
                    ProtocolTracing = new OpenAIProtocolTraceOptions
                    {
                        Enabled = true,
                    },
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-chat-test",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Trace this")]),
        }).ConfigureAwait(false);

        var tracePath = new AgentRuntimePathLayout(temp.Path).GetSessionTraceFilePath(session.SessionId);
        Assert.IsTrue(File.Exists(tracePath));
        var trace = File.ReadAllText(tracePath);
        StringAssert.Contains(trace, "### turn start provider=openai");
        StringAssert.Contains(trace, "<<< body: <streaming raw SSE lines>");
        StringAssert.Contains(trace, "<<< sse: data: {");
        StringAssert.Contains(trace, "Trace answer.");
        StringAssert.Contains(trace, "<<< sse: data: [DONE]");
        Assert.IsFalse(trace.Contains("sdk model JSON", StringComparison.Ordinal));
        StringAssert.Contains(trace, "### turn end provider=openai");
        StringAssert.Contains(trace, "completion=chatcmpl-trace");
    }

    [TestMethod]
    public void RawApiProviderDefaultsCatalog_AppliesXiaomiOpenAIChatCompatibilityProfile()
    {
        var profile = RawApiProviderDefaultsCatalog.ApplyProfileDefaults(
            AgentTransportKind.OpenAIChatCompletions,
            "xiaomi",
            new Uri("https://token-plan-ams.xiaomimimo.com/v1"),
            new AgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                SupportsParallelToolCalls = true,
                SupportsStrictTools = true,
                MaxTokensFieldName = "max_completion_tokens",
            });

        Assert.IsFalse(profile.SupportsDeveloperRole);
        Assert.IsFalse(profile.SupportsStore);
        Assert.IsFalse(profile.SupportsReasoningEffort);
        Assert.IsFalse(profile.SupportsParallelToolCalls);
        Assert.IsFalse(profile.SupportsStrictTools);
        Assert.AreEqual("max_tokens", profile.MaxTokensFieldName);
    }

    [TestMethod]
    public void RawApiProviderDefaultsCatalog_AppliesZaiReasoningReplayField()
    {
        var profile = RawApiProviderDefaultsCatalog.ApplyProfileDefaults(
            AgentTransportKind.OpenAIChatCompletions,
            "custom-glm",
            new Uri("https://open.bigmodel.cn/api/paas/v4"),
            new AgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                ReasoningFieldNames = ["reasoning_content", "reasoning"],
            });

        Assert.AreEqual("reasoning_content", profile.ReasoningInputFieldName);
    }

    [TestMethod]
    public async Task OpenAIChatModelProviderRuntime_MapsRefusalUpdatesToAssistantContent()
    {
        using var temp = TestTempDirectory.Create();
        var chatClient = new RecordingOpenAIChatClient(
        [
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                completionId: "chatcmpl-refusal",
                refusalUpdate: "I can't help with that request.",
                model: "gpt-chat-test"),
        ]);

        await using var providerRuntime = new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "openai",
                    IsDefault = true,
                    ChatClientFactory = _ => chatClient,
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-chat-test",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Refuse this")]),
        }).ConfigureAwait(false);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e =>
            e.Kind == AgentContentKind.Assistant &&
            e.Content == "I can't help with that request."));
    }

    [TestMethod]
    public async Task OpenAIChatModelProviderRuntime_MapsReasoningDeltasFromPatch()
    {
        using var temp = TestTempDirectory.Create();
        var chatClient = new RecordingOpenAIChatClient(
        [
            DeserializeStreamingChatCompletionUpdate(
                """
                {
                  "id": "chatcmpl-reasoning",
                  "object": "chat.completion.chunk",
                  "created": 1744060800,
                  "model": "gpt-chat-test",
                  "choices": [
                    {
                      "index": 0,
                      "delta": {
                        "reasoning_content": "Thinking through the repository layout."
                      }
                    }
                  ]
                }
                """),
        ]);

        await using var providerRuntime = new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "openai",
                    IsDefault = true,
                    ChatClientFactory = _ => chatClient,
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-chat-test",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Think")]),
        }).ConfigureAwait(false);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e =>
            e.Kind == AgentContentKind.Reasoning &&
            e.Content == "Thinking through the repository layout."));
    }

    [TestMethod]
    public async Task OpenAIChatModelProviderRuntime_ReplaysConfiguredReasoningInputFieldForToolCalls()
    {
        using var temp = TestTempDirectory.Create();
        var chatClient = RecordingOpenAIChatClient.ForBatches(
        [
            [
                DeserializeStreamingChatCompletionUpdate(
                    """
                    {
                      "id": "chatcmpl-deepseek-1",
                      "object": "chat.completion.chunk",
                      "created": 1744060800,
                      "model": "deepseek-v4-pro",
                      "choices": [
                        {
                          "index": 0,
                          "delta": {
                            "reasoning_content": "I need to inspect the requested file before answering.",
                            "tool_calls": [
                              {
                                "index": 0,
                                "id": "call-inspect",
                                "type": "function",
                                "function": {
                                  "name": "inspect_file",
                                  "arguments": "{\"path\":\"README.md\"}"
                                }
                              }
                            ]
                          }
                        }
                      ]
                    }
                    """),
            ],
            [
                DeserializeStreamingChatCompletionUpdate(
                    """
                    {
                      "id": "chatcmpl-deepseek-2",
                      "object": "chat.completion.chunk",
                      "created": 1744060801,
                      "model": "deepseek-v4-pro",
                      "choices": [
                        {
                          "index": 0,
                          "delta": {
                            "tool_calls": [
                              {
                                "index": 0,
                                "id": "call-read",
                                "type": "function",
                                "function": {
                                  "name": "inspect_file",
                                  "arguments": "{\"path\":\"src/CodeAlta.csproj\"}"
                                }
                              }
                            ]
                          }
                        }
                      ]
                    }
                    """),
            ],
            [
                DeserializeStreamingChatCompletionUpdate(
                    """
                    {
                      "id": "chatcmpl-deepseek-3",
                      "object": "chat.completion.chunk",
                      "created": 1744060802,
                      "model": "deepseek-v4-pro",
                      "choices": [
                        {
                          "index": 0,
                          "delta": {
                            "content": "Done."
                          }
                        }
                      ]
                    }
                    """),
            ],
        ]);

        await using var providerRuntime = new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "deepseek",
                    IsDefault = true,
                    Profile = new AgentProviderProfile
                    {
                        SupportsDeveloperRole = true,
                        SupportsReasoningEffort = true,
                        SupportsStore = false,
                        StreamsUsage = true,
                        ReasoningFieldNames = ["reasoning_content"],
                        ReasoningInputFieldName = "reasoning_content",
                    },
                    ChatClientFactory = _ => chatClient,
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "deepseek-v4-pro",
            WorkingDirectory = temp.Path,
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec(
                        "inspect_file",
                        "Inspect a file.",
                        JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""").RootElement.Clone()),
                    static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("README contents")]))),
            ],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Inspect README")]),
        }).ConfigureAwait(false);

        Assert.AreEqual(3, chatClient.Requests.Count);
        var replayedAssistant = chatClient.Requests[1].Messages.OfType<AssistantChatMessage>().Single();
        Assert.IsTrue(replayedAssistant.Patch.TryGetValue("$.reasoning_content"u8, out string? replayedReasoning));
        Assert.AreEqual("I need to inspect the requested file before answering.", replayedReasoning);
        Assert.AreEqual(1, replayedAssistant.ToolCalls.Count);
        Assert.IsFalse(
            replayedAssistant.Content.Any(static part =>
                part.Text.Contains("<assistant_reasoning>", StringComparison.Ordinal)));

        var replayedAssistants = chatClient.Requests[2].Messages.OfType<AssistantChatMessage>().ToArray();
        Assert.AreEqual(2, replayedAssistants.Length);
        Assert.IsTrue(replayedAssistants[0].Patch.TryGetValue("$.reasoning_content"u8, out string? priorReasoning));
        Assert.AreEqual("I need to inspect the requested file before answering.", priorReasoning);
        Assert.IsTrue(replayedAssistants[1].Patch.TryGetValue("$.reasoning_content"u8, out string? emptyReasoning));
        Assert.AreEqual(string.Empty, emptyReasoning);
        Assert.AreEqual(1, replayedAssistants[1].ToolCalls.Count);

        foreach (var assistant in replayedAssistants)
        {
            _ = SerializeModel(assistant);
        }
    }

    [TestMethod]
    public async Task OpenAIChatModelProviderRuntime_ReplaysUnconfiguredReasoningAsPlainTextWithoutTags()
    {
        using var temp = TestTempDirectory.Create();
        var chatClient = RecordingOpenAIChatClient.ForBatches(
        [
            [
                DeserializeStreamingChatCompletionUpdate(
                    """
                    {
                      "id": "chatcmpl-glm-1",
                      "object": "chat.completion.chunk",
                      "created": 1744060800,
                      "model": "glm-4.7",
                      "choices": [
                        {
                          "index": 0,
                          "delta": {
                            "reasoning_content": "Prior thinking.",
                            "content": "First answer."
                          }
                        }
                      ]
                    }
                    """),
            ],
            [
                DeserializeStreamingChatCompletionUpdate(
                    """
                    {
                      "id": "chatcmpl-glm-2",
                      "object": "chat.completion.chunk",
                      "created": 1744060801,
                      "model": "glm-4.7",
                      "choices": [
                        {
                          "index": 0,
                          "delta": {
                            "content": "Second answer."
                          }
                        }
                      ]
                    }
                    """),
            ],
        ]);

        await using var providerRuntime = new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "glm",
                    IsDefault = true,
                    Profile = new AgentProviderProfile
                    {
                        SupportsDeveloperRole = true,
                        SupportsReasoningEffort = true,
                        SupportsStore = false,
                        StreamsUsage = true,
                        ReasoningFieldNames = ["reasoning_content"],
                    },
                    ChatClientFactory = _ => chatClient,
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "glm-4.7",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("First")]),
        }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Second")]),
        }).ConfigureAwait(false);

        Assert.AreEqual(2, chatClient.Requests.Count);
        var replayedAssistant = chatClient.Requests[1].Messages.OfType<AssistantChatMessage>().Single();
        Assert.IsFalse(replayedAssistant.Patch.TryGetValue("$.reasoning_content"u8, out string? replayedReasoning));
        Assert.IsNull(replayedReasoning);
        var replayedText = string.Concat(replayedAssistant.Content.Select(static part => part.Text));
        StringAssert.Contains(replayedText, "First answer.");
        StringAssert.Contains(replayedText, "Assistant reasoning:\nPrior thinking.");
        Assert.IsFalse(replayedText.Contains("<assistant_reasoning", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OpenAIChatModelProviderRuntime_AppliesExtraBodyAndParsesCumulativeReasoningDetails()
    {
        using var temp = TestTempDirectory.Create();
        var chatClient = new RecordingOpenAIChatClient(
        [
            DeserializeStreamingChatCompletionUpdate(
                """
                {
                  "id": "chatcmpl-minimax-1",
                  "object": "chat.completion.chunk",
                  "created": 1744060800,
                  "model": "MiniMax-M2.7",
                  "choices": [
                    {
                      "index": 0,
                      "delta": {
                        "reasoning_details": [
                          { "text": "Thinking" }
                        ]
                      }
                    }
                  ]
                }
                """),
            DeserializeStreamingChatCompletionUpdate(
                """
                {
                  "id": "chatcmpl-minimax-1",
                  "object": "chat.completion.chunk",
                  "created": 1744060801,
                  "model": "MiniMax-M2.7",
                  "choices": [
                    {
                      "index": 0,
                      "delta": {
                        "reasoning_details": [
                          { "text": "Thinking through the repository layout." }
                        ]
                      }
                    }
                  ]
                }
                """),
            DeserializeStreamingChatCompletionUpdate(
                """
                {
                  "id": "chatcmpl-minimax-1",
                  "object": "chat.completion.chunk",
                  "created": 1744060802,
                  "model": "MiniMax-M2.7",
                  "choices": [
                    {
                      "index": 0,
                      "delta": {
                        "content": "Done."
                      }
                    }
                  ]
                }
                """),
        ]);

        await using var providerRuntime = new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "minimax",
                    IsDefault = true,
                    Profile = new AgentProviderProfile
                    {
                        SupportsDeveloperRole = false,
                        SupportsReasoningEffort = true,
                        SupportsStore = false,
                        StreamsUsage = true,
                        ReasoningFieldNames = ["reasoning_details[0].text"],
                    },
                    ExtraBody = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["reasoning_split"] = true,
                    },
                    ChatClientFactory = _ => chatClient,
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "MiniMax-M2.7",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Think")]),
        }).ConfigureAwait(false);

        Assert.AreEqual(1, chatClient.Requests.Count);
        Assert.IsTrue(chatClient.Requests[0].Options!.Patch.TryGetValue("$.reasoning_split"u8, out bool reasoningSplit));
        Assert.IsTrue(reasoningSplit);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e =>
            e.Kind == AgentContentKind.Reasoning &&
            e.Content == "Thinking through the repository layout."));
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e =>
            e.Kind == AgentContentKind.Assistant &&
            e.Content == "Done."));
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_ReconstructsResponseWhenStreamEndsWithoutCompletedPayload()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateCreatedResponseUpdate(
                    responseId: "response-recovered",
                    modelId: "gpt-test"),
                CreateOutputItemDoneUpdate(
                    outputIndex: 0,
                    item: ResponseItem.CreateAssistantMessageItem("Recovered answer.", [])),
            ],
        ]);

        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ResponsesClientFactory = _ => responsesClient,
        });

        var response = await executor.ExecuteTurnAsync(
            CreateTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var text = response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value;
        Assert.AreEqual("Recovered answer.", text);
        Assert.AreEqual("response-recovered", response.ProviderSessionId);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RejectsGenericEofAfterToolCall()
    {
        var toolCall = ResponseItem.CreateFunctionCallItem(
            "call-unterminated",
            "inspect_file",
            BinaryData.FromString("""{"path":"README.md"}"""));
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateCreatedResponseUpdate("response-unterminated-tool", "gpt-test"),
                CreateOutputItemDoneUpdate(0, toolCall),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ResponsesClientFactory = _ => responsesClient,
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
            () => executor.ExecuteTurnAsync(
                CreateTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)).ConfigureAwait(false);

        var protocolException = Assert.IsInstanceOfType<OpenAIResponsesProtocolException>(exception.InnerException);
        Assert.AreEqual(OpenAIResponsesProtocolErrorCode.StreamClosedAfterToolCall, protocolException.ErrorCode);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexStopsAtFirstCompletedEventWithoutWaitingForTransportEof()
    {
        var terminal = new ResponseResult
        {
            Id = "response-first-terminal",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.3-codex",
            Status = ResponseStatus.Completed,
        };
        terminal.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Completed promptly.", []));
        var responsesClient = new PendingAfterFirstUpdateResponseClient(CreateCompletedUpdate(terminal));
        var executor = CreateCodexExecutor(responsesClient);

        var executeTask = executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            static (_, _) => ValueTask.CompletedTask);
        var response = await executeTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Assert.AreEqual("Completed promptly.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexFirstTerminalEventWins()
    {
        var completed = new ResponseResult
        {
            Id = "response-completed-first",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.3-codex",
            Status = ResponseStatus.Completed,
        };
        completed.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("First terminal wins.", []));
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateCompletedUpdate(completed),
                CreateFailedResponseUpdate("response-late-failure", "gpt-5.3-codex", "bio_policy", "Must not be observed."),
            ],
        ]);
        var response = await CreateCodexExecutor(responsesClient).ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual("First terminal wins.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_DoesNotRetryCodexBioPolicyFailure()
    {
        var responsesClient = new PendingAfterFirstUpdateResponseClient(
            CreateFailedResponseUpdate("response-bio-policy", "gpt-5.3-codex", "bio_policy", "Biological policy rejected the request."));

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
            () => CreateCodexExecutor(responsesClient).ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)).ConfigureAwait(false);

        Assert.AreEqual(1, responsesClient.RequestCount);
        StringAssert.Contains(exception.Failure.Message, "bio_policy");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexIncompleteFailsImmediatelyWithoutWaitingForTransportEof()
    {
        var incomplete = new ResponseResult
        {
            Id = "response-incomplete-pending",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.3-codex",
            Status = ResponseStatus.Incomplete,
        };
        var completed = new ResponseResult
        {
            Id = "response-after-incomplete",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.3-codex",
            Status = ResponseStatus.Completed,
        };
        completed.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Retried after incomplete.", []));
        var responsesClient = new SequentialPendingAfterFirstUpdateResponseClient(
            [CreateIncompleteUpdate(incomplete), CreateCompletedUpdate(completed)]);

        var response = await CreateCodexExecutor(responsesClient).ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)
            .WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.RequestCount);
        Assert.AreEqual("Retried after incomplete.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_UsesIndexedDoneItemsAsAuthoritativeTerminalOutput()
    {
        var terminal = new ResponseResult
        {
            Id = "response-merged",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.3-codex",
            Status = ResponseStatus.Completed,
        };
        terminal.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Stale terminal zero.", []));
        terminal.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Terminal-only one.", []));
        terminal.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Terminal-only two.", []));
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateOutputItemDoneUpdate(0, ResponseItem.CreateAssistantMessageItem("Older streamed zero.", [])),
                CreateOutputItemDoneUpdate(0, ResponseItem.CreateAssistantMessageItem("Authoritative streamed zero.", [])),
                CreateOutputItemDoneUpdate(1, ResponseItem.CreateAssistantMessageItem("Authoritative streamed one.", [])),
                CreateCompletedUpdate(terminal),
            ],
        ]);

        var response = await CreateCodexExecutor(responsesClient).ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[] { "Authoritative streamed zero.", "Authoritative streamed one.", "Terminal-only two." },
            response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value).ToArray());
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexRejectsStreamWithoutCompletedPayload()
    {
        var partialUpdates = new StreamingResponseUpdate[]
        {
            CreateCreatedResponseUpdate(
                responseId: "response-started-without-terminal",
                modelId: "gpt-5.3-codex"),
            CreateOutputItemDoneUpdate(
                outputIndex: 0,
                item: ResponseItem.CreateAssistantMessageItem("Partial answer.", [])),
        };
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            partialUpdates,
            partialUpdates,
            partialUpdates,
            partialUpdates,
            partialUpdates,
            partialUpdates,
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    CreateCodexTurnRequest(),
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        Assert.AreEqual(6, responsesClient.Requests.Count);
        var translatedException = Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
        var protocolException = Assert.IsInstanceOfType<OpenAIResponsesProtocolException>(translatedException.InnerException);
        Assert.AreEqual(OpenAIResponsesProtocolErrorCode.StreamClosedBeforeTerminalResponse, protocolException.ErrorCode);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexRejectsTerminalReasoningOnlyResponse()
    {
        var reasoningOnlyResponse = new ResponseResult
        {
            Id = "response-reasoning-only",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.3-codex",
            Status = ResponseStatus.Completed,
        };
        reasoningOnlyResponse.OutputItems.Add(new ReasoningResponseItem("Still thinking."));
        var reasoningOnlyUpdate = CreateCompletedUpdate(reasoningOnlyResponse);
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [reasoningOnlyUpdate],
            [reasoningOnlyUpdate],
            [reasoningOnlyUpdate],
            [reasoningOnlyUpdate],
            [reasoningOnlyUpdate],
            [reasoningOnlyUpdate],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    CreateCodexTurnRequest(),
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        Assert.AreEqual(6, responsesClient.Requests.Count);
        var translatedException = Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
        var protocolException = Assert.IsInstanceOfType<OpenAIResponsesProtocolException>(translatedException.InnerException);
        Assert.AreEqual(OpenAIResponsesProtocolErrorCode.TerminalResponseWithoutAssistantOutput, protocolException.ErrorCode);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_NormalizesRotatingCopilotDeltaItemIds()
    {
        var streamMessageItem = ResponseItem.CreateAssistantMessageItem(string.Empty, []);
        streamMessageItem.Id = "msg_stream";
        var streamReasoningItem = new ReasoningResponseItem(string.Empty)
        {
            Id = "rs_stream",
        };
        var messageItem = ResponseItem.CreateAssistantMessageItem("Hello world.", []);
        messageItem.Id = "msg_final";
        var reasoningItem = new ReasoningResponseItem("Thinking done.")
        {
            Id = "rs_final",
        };
        var completedResponse = new ResponseResult
        {
            Id = "response-copilot",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.5",
            Status = ResponseStatus.Completed,
        };
        completedResponse.OutputItems.Add(messageItem);
        completedResponse.OutputItems.Add(reasoningItem);
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateOutputItemAddedUpdate(0, streamMessageItem),
                CreateOutputTextDeltaUpdate("rotating-message-1", "Hello ", outputIndex: 0),
                CreateOutputTextDeltaUpdate("rotating-message-2", "world.", outputIndex: 0),
                CreateOutputItemAddedUpdate(1, streamReasoningItem),
                CreateReasoningSummaryTextDeltaUpdate("rotating-reasoning-1", "Thinking ", summaryIndex: 0),
                CreateReasoningSummaryTextDeltaUpdate("rotating-reasoning-2", "done.", summaryIndex: 1),
                CreateCompletedUpdate(completedResponse),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "copilot",
            ResponsesClientFactory = _ => responsesClient,
        });
        var deltas = new List<AgentTurnDelta>();
        var request = CreateTurnRequest();

        var response = await executor.ExecuteTurnAsync(
            request with
            {
                Provider = request.Provider with
                {
                    ProtocolFamily = "copilot",
                },
            },
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[] { "msg_stream", "msg_stream" },
            deltas.Where(static delta => delta.Kind == AgentContentKind.Assistant).Select(static delta => delta.ContentId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "rs_stream", "rs_stream" },
            deltas.Where(static delta => delta.Kind == AgentContentKind.Reasoning).Select(static delta => delta.ContentId).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            deltas
                .Where(static delta => delta.Kind == AgentContentKind.Reasoning)
                .Select(static delta => delta.Details!.Value.GetProperty("summaryIndex").GetInt32())
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "msg_stream", "rs_stream" },
            response.AssistantPartContentIds!.ToArray());
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_PreservesCompletedReasoningSummaryParts()
    {
        var completedResponse = new ResponseResult
        {
            Id = "response-summary-parts",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.6-sol",
            Status = ResponseStatus.Completed,
        };
        completedResponse.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Done.", []));
        completedResponse.OutputItems.Add(
            new ReasoningResponseItem(
            [
                ReasoningSummaryPart.CreateTextPart("**Planning**\n\nInspect the repository."),
                ReasoningSummaryPart.CreateTextPart("**Checking tests**\n\n<!-- -->"),
            ]));
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ResponsesClientFactory = _ => new RecordingOpenAIResponseClient([[CreateCompletedUpdate(completedResponse)]]),
        });

        var response = await executor.ExecuteTurnAsync(
            CreateTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var reasoning = response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Single();
        Assert.AreEqual(
            "**Planning**\n\nInspect the repository.**Checking tests**\n\n<!-- -->",
            reasoning.Value);
        CollectionAssert.AreEqual(
            new[]
            {
                "**Planning**\n\nInspect the repository.",
                "**Checking tests**\n\n<!-- -->",
            },
            reasoning.SummaryParts!.ToArray());
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_SendsInlineImagesWithMediaType()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-image",
                    modelId: "gpt-test",
                    text: "Image received.",
                    reasoningText: string.Empty,
                    encryptedReasoning: null),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ResponsesClientFactory = _ => responsesClient,
        });
        var imageBytes = new byte[] { 1, 2, 3 };
        var request = CreateTurnRequest() with
        {
            Conversation =
            [
                new AgentConversationMessage(
                    AgentConversationRole.User,
                    [
                        new AgentMessagePart.Text("Describe this image."),
                        new AgentMessagePart.Data(Convert.ToBase64String(imageBytes), "image/png", "image.png"),
                    ]),
            ],
        };

        var response = await executor.ExecuteTurnAsync(
            request,
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var text = response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value;
        Assert.AreEqual("Image received.", text);
        var message = Assert.IsInstanceOfType<MessageResponseItem>(responsesClient.Requests[0].InputItems.Single());
        var imagePart = message.Content.Single(static part => part.Kind == ResponseContentPartKind.InputImage);
        Assert.AreEqual("data:image/png;base64,AQID", imagePart.InputImageUri);
        Assert.IsNull(imagePart.InputImageDetailLevel);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_UsesStreamedOutputItemsWhenCompletedPayloadOmitsOutput()
    {
        var completedResponse = new ResponseResult
        {
            Id = "response-empty-terminal",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-test",
            Status = ResponseStatus.Completed,
        };
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateCreatedResponseUpdate(
                    responseId: "response-empty-terminal",
                    modelId: "gpt-test"),
                CreateOutputItemDoneUpdate(
                    outputIndex: 0,
                    item: ResponseItem.CreateFunctionCallItem(
                        "call-read",
                        "read_file",
                        BinaryData.FromString("""{"path":"readme.md","offset":1,"limit":120}"""))),
                CreateCompletedUpdate(completedResponse),
            ],
        ]);

        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ResponsesClientFactory = _ => responsesClient,
        });

        var response = await executor.ExecuteTurnAsync(
            CreateTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var toolCall = response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("call-read", toolCall.CallId);
        Assert.AreEqual("read_file", toolCall.Name);
        Assert.AreEqual("response-empty-terminal", response.ProviderSessionId);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_MapsErrorUpdateToContextOverflowFailure()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateErrorResponseUpdate(
                    code: "invalid_prompt",
                    message: "maximum context length exceeded"),
            ],
        ]);

        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ResponsesClientFactory = _ => responsesClient,
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
            () => executor.ExecuteTurnAsync(
                CreateTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)).ConfigureAwait(false);

        Assert.IsTrue(exception.Failure.IsContextOverflow);
        StringAssert.Contains(exception.Message, "maximum context length exceeded");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_MapsUsageIntoCurrentWindowAndLastOperation()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-usage",
                    modelId: "gpt-test",
                    text: "Answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: null,
                    inputTokens: 100,
                    outputTokens: 7,
                    cachedInputTokens: 40,
                    reasoningTokens: 3,
                    totalTokens: 107),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ResponsesClientFactory = _ => responsesClient,
        });

        var request = CreateTurnRequest() with
        {
            ModelInfo = new AgentModelInfo(
                "gpt-test",
                DisplayName: "GPT Test",
                Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["inputTokenLimit"] = 200000L,
                    ["outputTokenLimit"] = 128000L,
                }),
        };

        var response = await executor.ExecuteTurnAsync(
            request,
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.IsNotNull(response.Usage);
        Assert.AreEqual(100L, response.Usage.LastOperation?.InputTokens);
        Assert.AreEqual(7L, response.Usage.LastOperation?.OutputTokens);
        Assert.AreEqual(40L, response.Usage.LastOperation?.CachedInputTokens);
        Assert.AreEqual(3L, response.Usage.LastOperation?.ReasoningTokens);
        Assert.AreEqual(107L, response.Usage.CurrentTokens);
        Assert.AreEqual(200000L, response.Usage.TokenLimit);
        Assert.AreEqual(AgentUsageScope.CurrentWindow, response.Usage.Scope);
    }

    [TestMethod]
    public async Task OpenAIResponsesModelProviderRuntime_AppliesConfiguredExtraBody()
    {
        using var temp = TestTempDirectory.Create();
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-extra-body",
                    modelId: "gpt-test",
                    text: "Answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: null),
            ],
        ]);

        await using var providerRuntime = new OpenAIResponsesModelProviderRuntime(new OpenAIResponsesModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "compat-provider",
                    IsDefault = true,
                    ExtraBody = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["custom_flag"] = true,
                    },
                    ResponsesClientFactory = _ => responsesClient,
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-test",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        Assert.AreEqual(1, responsesClient.Requests.Count);
        Assert.IsTrue(responsesClient.Requests[0].Options.Patch.TryGetValue("$.custom_flag"u8, out bool customFlag));
        Assert.IsTrue(customFlag);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_PassesRequestContextToResponsesClientFactory()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-context",
                    modelId: "gpt-test",
                    text: "Answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: null),
            ],
        ]);
        OpenAIResponsesClientFactoryContext? capturedContext = null;
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ResponsesClientContextFactory = context =>
            {
                capturedContext = context;
                return responsesClient;
            },
        });

        _ = await executor.ExecuteTurnAsync(
            CreateTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.IsNotNull(capturedContext);
        Assert.AreEqual("gpt-test", capturedContext!.ModelId);
        Assert.AreEqual("session-1", capturedContext.SessionId);
        Assert.AreEqual(new AgentRunId("run-1"), capturedContext.RunId);
        Assert.AreEqual("openai", capturedContext.Provider.ProviderKey);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_ReusesCodexTurnStateOnlyWithinSameRun()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateTextOnlyAssistantResponseUpdate("response-turn-state-1", "gpt-5.3-codex", "First answer.")],
            [CreateTextOnlyAssistantResponseUpdate("response-turn-state-2", "gpt-5.3-codex", "Second answer.")],
            [CreateTextOnlyAssistantResponseUpdate("response-turn-state-3", "gpt-5.3-codex", "Third answer.")],
        ]);
        var contexts = new List<OpenAIResponsesClientFactoryContext>();
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientContextFactory = context =>
            {
                contexts.Add(context);
                return responsesClient;
            },
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });
        var sameRun = CreateCodexTurnRequest() with
        {
            RunId = new AgentRunId("run-same-turn"),
        };
        var nextRun = CreateCodexTurnRequest() with
        {
            RunId = new AgentRunId("run-next-turn"),
        };

        _ = await executor.ExecuteTurnAsync(sameRun, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);
        Assert.IsNotNull(contexts[0].TurnState);
        var firstTurnState = contexts[0].TurnState!;
        Assert.IsFalse(firstTurnState.TryGetCapturedState(out _));
        firstTurnState.Capture("sticky-state");

        _ = await executor.ExecuteTurnAsync(sameRun, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(2, contexts.Count);
        Assert.AreSame(firstTurnState, contexts[1].TurnState);
        Assert.IsTrue(contexts[1].TurnState!.TryGetCapturedState(out var replayedState));
        Assert.AreEqual("sticky-state", replayedState);

        _ = await executor.ExecuteTurnAsync(nextRun, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(3, contexts.Count);
        Assert.AreNotSame(firstTurnState, contexts[2].TurnState);
        Assert.IsFalse(contexts[2].TurnState!.TryGetCapturedState(out _));
        Assert.IsFalse(firstTurnState.TryGetCapturedState(out _));
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_ReusesSameImmutableCodexContextAcrossRetry()
    {
        var successClient = new RecordingOpenAIResponseClient(
        [
            [CreateTextOnlyAssistantResponseUpdate("response-context-retry", "gpt-5.3-codex", "Retried.")],
        ]);
        var contexts = new List<CodexSubscriptionRequestContext?>();
        var factoryCalls = 0;
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientContextFactory = context =>
            {
                contexts.Add(context.RequestContext);
                factoryCalls++;
                return factoryCalls == 1
                    ? new ThrowingOpenAIResponseClient(WithZeroRetryAfter(new HttpRequestException("retry")))
                    : successClient;
            },
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        _ = await executor.ExecuteTurnAsync(CreateCodexTurnRequest(), static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(2, contexts.Count);
        Assert.IsNotNull(contexts[0]);
        Assert.AreSame(contexts[0], contexts[1]);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_ReusesSocketButPassesPerRunRequestContext()
    {
        var webSocketSession = new ContextRecordingOpenAIResponsesWebSocketSession(
        [
            [CreateTextOnlyAssistantResponseUpdate("response-context-ws-1", "gpt-5.3-codex", "First.")],
            [CreateTextOnlyAssistantResponseUpdate("response-context-ws-2", "gpt-5.3-codex", "Second.")],
        ]);
        var factoryCalls = 0;
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => new RecordingOpenAIResponseClient([]),
            ResponsesWebSocketSessionFactory = _ =>
            {
                factoryCalls++;
                return ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession);
            },
            CodexSubscription = new OpenAICodexSubscriptionOptions { Experimental = true },
        });

        _ = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest() with { RunId = new AgentRunId("run-websocket-one") },
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);
        _ = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest() with { RunId = new AgentRunId("run-websocket-two") },
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(1, factoryCalls);
        Assert.AreEqual(2, webSocketSession.Contexts.Count);
        Assert.AreEqual("run-websocket-one", webSocketSession.Contexts[0].RunId.Value);
        Assert.AreEqual("run-websocket-two", webSocketSession.Contexts[1].RunId.Value);
        Assert.AreNotSame(webSocketSession.Contexts[0], webSocketSession.Contexts[1]);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_PreservesLegacyResponsesClientFactory()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-legacy",
                    modelId: "gpt-test",
                    text: "Answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: null),
            ],
        ]);
        string? capturedModel = null;
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ResponsesClientFactory = model =>
            {
                capturedModel = model;
                return responsesClient;
            },
        });

        _ = await executor.ExecuteTurnAsync(
            CreateTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual("gpt-test", capturedModel);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_InvokesRequestCustomizerAfterCommonMapping()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-customizer",
                    modelId: "gpt-test",
                    text: "Answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: null),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "openai",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesRequestCustomizer = context =>
            {
                Assert.AreEqual("session-1", context.Request.SessionId);
                context.Options.Patch.Set("$.customized"u8, true);
            },
        });

        _ = await executor.ExecuteTurnAsync(
            CreateTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.IsTrue(responsesClient.Requests[0].Options.Patch.TryGetValue("$.customized"u8, out bool customized));
        Assert.IsTrue(customized);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_AppliesCodexSubscriptionRequestDeltas()
    {
        using var temp = TestTempDirectory.Create();
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-codex",
                    modelId: "gpt-5.3-codex",
                    text: "Answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: "encrypted-reasoning"),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            StateRootPath = temp.Path,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
                IncludeEncryptedReasoning = true,
                SendInstallationId = true,
                TextVerbosity = "low",
            },
        });
        var request = CreateTurnRequest() with
        {
            Provider = CreateTurnRequest().Provider with
            {
                ProtocolFamily = "codex",
                ProviderKey = "codex",
                DisplayName = "Codex",
                Profile = new AgentProviderProfile
                {
                    SupportsStore = false,
                    SupportsReasoningEffort = true,
                },
            },
            ModelId = "gpt-5.3-codex",
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec(
                        "inspect_file",
                        "Inspect a file.",
                        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                    static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("ok")]))),
            ],
        };

        _ = await executor.ExecuteTurnAsync(
            request,
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var options = responsesClient.Requests[0].Options;
        Assert.IsFalse(options.StoredOutputEnabled);
        Assert.IsTrue(options.StreamingEnabled);
        Assert.IsTrue(options.ParallelToolCallsEnabled);
        Assert.IsNotNull(options.ToolChoice);
        Assert.IsNull(options.PreviousResponseId);
        Assert.IsTrue(options.IncludedProperties.Contains(IncludedResponseProperty.ReasoningEncryptedContent));

        using var document = JsonDocument.Parse(responsesClient.Requests[0].SerializedOptions);
        Assert.IsFalse(document.RootElement.GetProperty("store").GetBoolean());
        Assert.IsTrue(document.RootElement.GetProperty("stream").GetBoolean());
        Assert.AreEqual("session-1", document.RootElement.GetProperty("prompt_cache_key").GetString());
        Assert.AreEqual("low", document.RootElement.GetProperty("text").GetProperty("verbosity").GetString());
        var installationId = document.RootElement
            .GetProperty("client_metadata")
            .GetProperty("x-codex-installation-id")
            .GetString();
        Assert.IsTrue(Guid.TryParse(installationId, out _));
        Assert.AreEqual("session-1", document.RootElement.GetProperty("client_metadata").GetProperty("session_id").GetString());
        Assert.AreEqual("session-1", document.RootElement.GetProperty("client_metadata").GetProperty("thread_id").GetString());
        Assert.AreEqual("run-1", document.RootElement.GetProperty("client_metadata").GetProperty("turn_id").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("include").EnumerateArray().Any(
            static item => item.GetString() == "reasoning.encrypted_content"));
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_AppliesTrustedCodexModelCapabilitiesToRequest()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateTextOnlyAssistantResponseUpdate("response-capabilities", "gpt-5.6-sol", "Answer.")],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
                TextVerbosity = "high",
            },
        });
        var request = CreateCodexTurnRequest() with
        {
            ModelId = "gpt-5.6-sol",
            ReasoningEffort = AgentReasoningEffort.Low,
            ModelInfo = new AgentModelInfo(
                "gpt-5.6-sol",
                SupportedReasoningEfforts: [AgentReasoningEffort.Low],
                Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["source"] = "codex-endpoint",
                    ["supportsReasoningSummaries"] = false,
                    ["supportVerbosity"] = false,
                    ["supportsParallelToolCalls"] = false,
                }),
        };

        _ = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        using var document = JsonDocument.Parse(responsesClient.Requests[0].SerializedOptions);
        Assert.IsFalse(document.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.IsFalse(document.RootElement.TryGetProperty("text", out _));
        Assert.IsTrue(document.RootElement.TryGetProperty("reasoning", out var reasoning));
        Assert.IsFalse(reasoning.TryGetProperty("summary", out _));
    }

    [TestMethod]
    [DataRow("gpt-5.6-sol")]
    [DataRow("gpt-5.6-terra")]
    [DataRow("gpt-5.6-luna")]
    public async Task OpenAIResponsesTurnExecutor_UsesResponsesLiteRequestDialect(string modelId)
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateTextOnlyAssistantResponseUpdate("response-lite", modelId, "Answer.")],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesRequestCustomizer = context =>
            {
                var content = ((MessageResponseItem)context.Options.InputItems[0]).Content;
                var image = content
                    .Single(static part => part.Kind == ResponseContentPartKind.InputImage);
                content.Remove(image);
                content.Add(ResponseContentPart.CreateInputImagePart(
                    new Uri(image.InputImageUri!),
                    ResponseImageDetailLevel.High));
            },
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });
        var request = CreateCodexTurnRequest() with
        {
            ModelId = modelId,
            SystemMessage = "System instructions",
            DeveloperInstructions = "Developer instructions",
            ReasoningEffort = AgentReasoningEffort.Medium,
            ModelInfo = new AgentModelInfo(
                modelId,
                SupportedReasoningEfforts: [AgentReasoningEffort.Medium],
                Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["source"] = "codex-endpoint",
                    ["useResponsesLite"] = true,
                    ["supportsReasoningSummaries"] = true,
                    ["supportsParallelToolCalls"] = true,
                }),
            Conversation =
            [
                new AgentConversationMessage(
                    AgentConversationRole.User,
                    [
                        new AgentMessagePart.Text("Inspect this image."),
                        new AgentMessagePart.Data("AQID", "image/png", "image.png"),
                    ]),
                new AgentConversationMessage(
                    AgentConversationRole.Assistant,
                    [
                        new AgentMessagePart.Reasoning(
                            "Need a tool.",
                            "encrypted",
                            new AgentReasoningProvenance("codex", "codex", AgentTransportKind.OpenAIResponses, modelId)),
                        new AgentMessagePart.ToolCall("call-1", "inspect_file", JsonDocument.Parse("{}").RootElement.Clone()),
                    ]),
                new AgentConversationMessage(
                    AgentConversationRole.Tool,
                    [new AgentMessagePart.ToolResult("call-1", new AgentToolResult(true, [new AgentToolResultItem.Text("ok")]))]),
            ],
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec(
                        "inspect_file",
                        "Inspect a file.",
                        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                    static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("ok")]))),
            ],
        };

        _ = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        using var document = JsonDocument.Parse(responsesClient.Requests.Single().SerializedOptions);
        var root = document.RootElement;
        Assert.IsFalse(root.TryGetProperty("instructions", out _));
        Assert.IsFalse(root.TryGetProperty("tools", out _));
        Assert.IsFalse(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.AreEqual("all_turns", root.GetProperty("reasoning").GetProperty("context").GetString());
        Assert.IsFalse(root.TryGetProperty("stream_options", out _));
        var input = root.GetProperty("input").EnumerateArray().ToArray();
        Assert.AreEqual("additional_tools", input[0].GetProperty("type").GetString());
        Assert.AreEqual("developer", input[0].GetProperty("role").GetString());
        Assert.AreEqual("function", input[0].GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.AreEqual("message", input[1].GetProperty("type").GetString());
        Assert.AreEqual("developer", input[1].GetProperty("role").GetString());
        StringAssert.Contains(input[1].GetProperty("content")[0].GetProperty("text").GetString(), "Developer instructions");
        CollectionAssert.AreEqual(
            new[] { "message", "reasoning", "function_call", "function_call_output" },
            input.Skip(2).Select(static item => item.GetProperty("type").GetString()).ToArray());
        Assert.IsFalse(input[2].GetProperty("content")[1].TryGetProperty("detail", out _));
        Assert.IsTrue(root.TryGetProperty("client_metadata", out _));
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CanNegotiateSequentialCutoffThroughInternalSeam()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateTextOnlyAssistantResponseUpdate("response-cutoff", "gpt-5.6-sol", "Answer.")],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
                EnableSequentialCutoffReasoningSummaries = true,
            },
        });

        _ = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        using var document = JsonDocument.Parse(responsesClient.Requests.Single().SerializedOptions);
        Assert.AreEqual(
            "sequential_cutoff",
            document.RootElement.GetProperty("stream_options").GetProperty("reasoning_summary_delivery").GetString());
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_SequentialCutoffDoneEventsDoNotDuplicateLegacyDeltas()
    {
        var reasoningItem = new ReasoningResponseItem(string.Empty) { Id = "reasoning-1" };
        var response = new ResponseResult
        {
            Id = "response-reasoning-done",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.6-sol",
            Status = ResponseStatus.Completed,
        };
        response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Done.", []));
        response.OutputItems.Add(new ReasoningResponseItem([ReasoningSummaryPart.CreateTextPart("Final summary")])
        {
            Id = "reasoning-1",
            EncryptedContent = "encrypted-final",
        });
        var updates = new List<StreamingResponseUpdate>
        {
            CreateOutputItemAddedUpdate(1, reasoningItem),
            CreateReasoningSummaryPartUpdate("response.reasoning_summary_part.added", "reasoning-1", summaryIndex: 2),
            CreateReasoningSummaryTextDoneUpdate("other-item", "Ignored interleaved", summaryIndex: 0),
            CreateReasoningSummaryTextDoneUpdate("reasoning-1", "Done only", summaryIndex: 2),
            CreateReasoningSummaryPartUpdate("response.reasoning_summary_part.done", "reasoning-1", summaryIndex: 2),
            CreateReasoningSummaryTextDoneUpdate("reasoning-1", "Duplicate", summaryIndex: 2),
            CreateReasoningSummaryTextDeltaUpdate("reasoning-1", "Legacy", summaryIndex: 3),
            CreateReasoningSummaryTextDoneUpdate("reasoning-1", "Legacy", summaryIndex: 3),
            CreateReasoningTextDoneUpdate("reasoning-1", "Raw done", contentIndex: 0),
            CreateReasoningTextDoneUpdate("reasoning-1", "Raw duplicate", contentIndex: 0),
            CreateOutputItemDoneUpdate(1, response.OutputItems[1]),
            CreateCompletedUpdate(response),
        };
        var responsesClient = new RecordingOpenAIResponseClient([updates]);
        var executor = CreateCodexExecutor(responsesClient);
        var deltas = new List<AgentTurnDelta>();

        var result = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[] { "Done only", "Legacy", "Raw done" },
            deltas.Where(static delta => delta.Kind == AgentContentKind.Reasoning).Select(static delta => delta.Text).ToArray());
        var finalReasoning = result.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Single();
        Assert.AreEqual("encrypted-final", finalReasoning.ProtectedData);
        CollectionAssert.AreEqual(new[] { "Final summary" }, finalReasoning.SummaryParts!.ToArray());
    }

    [TestMethod]
    public async Task OpenAIResponsesModelProviderRuntime_CodexModelDiscoveryHttpFailureUsesStaticFallback()
    {
        using var temp = TestTempDirectory.Create();
        var credentialStore = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await credentialStore.SaveAsync(
            "codex",
            new OpenAICodexSubscriptionCredential
            {
                AccessToken = "access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }).ConfigureAwait(false);
        var handler = new StaticHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"detail":"model discovery is temporarily unavailable"}"""),
        });

        await using var providerRuntime = new OpenAIResponsesModelProviderRuntime(new OpenAIResponsesModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "codex",
                    IsDefault = true,
                    BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
                    CodexSubscriptionHttpClient = new HttpClient(handler),
                    CodexSubscription = new OpenAICodexSubscriptionOptions
                    {
                        Experimental = true,
                        ResponseTransport = "http",
                        ModelDiscovery = "codex_endpoint_with_static_fallback",
                    },
                },
            },
        });

        var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

        Assert.AreEqual(
            "gpt-5.6-sol|gpt-5.6-terra|gpt-5.6-luna|gpt-5.5|gpt-5.4|gpt-5.4-mini|gpt-5.3-codex|gpt-5.2",
            string.Join('|', models.Select(static model => model.Id)));
        Assert.IsTrue(models.All(static model => Equals("codex-static-fallback", model.Capabilities?["source"])));
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("/backend-api/codex/models", handler.Requests[0].AbsolutePath);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_SetsCodexToolControlFieldsWithoutTools()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-codex-compaction",
                    modelId: "gpt-5.3-codex",
                    text: "Summary.",
                    reasoningText: "Summarizing.",
                    encryptedReasoning: "encrypted-reasoning"),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
                IncludeEncryptedReasoning = true,
            },
        });
        var request = CreateCodexTurnRequest() with
        {
            MaxOutputTokens = 1024,
            ReasoningEffort = null,
            Tools = [],
        };

        _ = await executor.ExecuteTurnAsync(
            request,
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var options = responsesClient.Requests[0].Options;
        Assert.IsTrue(options.ParallelToolCallsEnabled);
        Assert.IsNotNull(options.ToolChoice);
        Assert.IsNull(options.MaxOutputTokenCount);
        Assert.AreEqual(0, options.Tools.Count);

        using var document = JsonDocument.Parse(responsesClient.Requests[0].SerializedOptions);
        Assert.IsTrue(document.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.IsTrue(document.RootElement.TryGetProperty("tool_choice", out _));
        Assert.IsFalse(document.RootElement.TryGetProperty("max_output_tokens", out _));
        Assert.IsFalse(document.RootElement.TryGetProperty("tools", out _));
        Assert.IsTrue(document.RootElement.GetProperty("include").EnumerateArray().Any(
            static item => item.GetString() == "reasoning.encrypted_content"));
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_OmitsCodexInstallationMetadataByDefault()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-codex-default",
                    modelId: "gpt-5.3-codex",
                    text: "Answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: "encrypted-reasoning"),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
                IncludeEncryptedReasoning = true,
            },
        });
        var request = CreateTurnRequest() with
        {
            Provider = CreateTurnRequest().Provider with
            {
                ProtocolFamily = "codex",
                ProviderKey = "codex",
                DisplayName = "Codex",
                Profile = new AgentProviderProfile
                {
                    SupportsStore = false,
                    SupportsReasoningEffort = true,
                },
            },
            ModelId = "gpt-5.3-codex",
        };

        _ = await executor.ExecuteTurnAsync(
            request,
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        using var document = JsonDocument.Parse(responsesClient.Requests[0].SerializedOptions);
        var metadata = document.RootElement.GetProperty("client_metadata");
        Assert.IsFalse(metadata.TryGetProperty("x-codex-installation-id", out _));
        Assert.AreEqual("session-1", metadata.GetProperty("session_id").GetString());
        Assert.AreEqual("run-1", metadata.GetProperty("turn_id").GetString());
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_ReservedCodexMetadataOverridesCustomizerConflicts()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateTextOnlyAssistantResponseUpdate("response-metadata", "gpt-5.3-codex", "Answer.")],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesRequestCustomizer = context =>
            {
                context.Options.Patch.Set("$.client_metadata.session_id"u8, BinaryData.FromString("\"conflicting-session\""));
                context.Options.Patch.Set("$.client_metadata.turn_id"u8, BinaryData.FromString("\"conflicting-turn\""));
                context.Options.Patch.Set("$.client_metadata.custom"u8, BinaryData.FromString("\"preserved\""));
            },
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        _ = await executor.ExecuteTurnAsync(CreateCodexTurnRequest(), static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        using var document = JsonDocument.Parse(responsesClient.Requests[0].SerializedOptions);
        var metadata = document.RootElement.GetProperty("client_metadata");
        Assert.AreEqual("session-1", metadata.GetProperty("session_id").GetString());
        Assert.AreEqual("run-1", metadata.GetProperty("turn_id").GetString());
        Assert.AreEqual("preserved", metadata.GetProperty("custom").GetString());
    }

    [TestMethod]
    public async Task OpenAIResponsesModelProviderRuntime_CodexUsesLocalToolBridgeSequentially()
    {
        using var temp = TestTempDirectory.Create();
        var callOneItem = ResponseItem.CreateFunctionCallItem(
            "call-one",
            "inspect_one",
            BinaryData.FromString("""{"path":"README.md"}"""));
        var callTwoItem = ResponseItem.CreateFunctionCallItem(
            "call-two",
            "inspect_two",
            BinaryData.FromString("""{"path":"src/CodeAlta.csproj"}"""));
        var toolResponse = new ResponseResult
        {
            Id = "response-tools",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.3-codex",
            Status = ResponseStatus.Completed,
        };
        toolResponse.OutputItems.Add(callOneItem);
        toolResponse.OutputItems.Add(callTwoItem);
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateCreatedResponseUpdate(
                    responseId: "response-tools",
                    modelId: "gpt-5.3-codex"),
                CreateOutputItemDoneUpdate(
                    outputIndex: 0,
                    item: callOneItem),
                CreateOutputItemDoneUpdate(
                    outputIndex: 1,
                    item: callTwoItem),
                CreateCompletedUpdate(toolResponse),
            ],
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-final",
                    modelId: "gpt-5.3-codex",
                    text: "Done.",
                    reasoningText: "Used local tools.",
                    encryptedReasoning: "encrypted-reasoning"),
            ],
        ]);
        var sequence = new List<string>();

        await using var providerRuntime = new OpenAIResponsesModelProviderRuntime(new OpenAIResponsesModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "codex",
                    IsDefault = true,
                    ResponsesClientFactory = _ => responsesClient,
                    CodexSubscription = new OpenAICodexSubscriptionOptions
                    {
                        Experimental = true,
                        ResponseTransport = "http",
                    },
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo(
                            "gpt-5.3-codex",
                            DisplayName: "GPT Codex",
                            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["inputTokenLimit"] = 200000L,
                            }),
                    ]),
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-5.3-codex",
            WorkingDirectory = temp.Path,
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec(
                        "inspect_one",
                        "Read a file.",
                        JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""").RootElement.Clone()),
                    (_, _) =>
                    {
                        sequence.Add("inspect_one");
                        return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("README contents")]));
                    }),
                new AgentToolDefinition(
                    new AgentToolSpec(
                        "inspect_two",
                        "Inspect a file.",
                        JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""").RootElement.Clone()),
                    (_, _) =>
                    {
                        sequence.Add("inspect_two");
                        return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("Project contents")]));
                    }),
            ],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Use tools")]),
        }).ConfigureAwait(false);

        Assert.AreEqual("inspect_one,inspect_two", string.Join(",", sequence));
        Assert.AreEqual(2, responsesClient.Requests.Count);
        using var firstRequest = JsonDocument.Parse(responsesClient.Requests[0].SerializedOptions);
        var toolNames = firstRequest.RootElement.GetProperty("tools")
            .EnumerateArray()
            .Select(static tool => tool.GetProperty("name").GetString())
            .ToArray();
        CollectionAssert.Contains(toolNames, "inspect_one");
        CollectionAssert.Contains(toolNames, "inspect_two");
        Assert.IsTrue(firstRequest.RootElement.GetProperty("parallel_tool_calls").GetBoolean());

        var secondInputs = responsesClient.Requests[1].InputItems.OfType<FunctionCallOutputResponseItem>().ToArray();
        Assert.AreEqual(2, secondInputs.Length);
        Assert.IsTrue(secondInputs.Any(static item => item.CallId == "call-one" && item.FunctionOutput == "README contents"));
        Assert.IsTrue(secondInputs.Any(static item => item.CallId == "call-two" && item.FunctionOutput == "Project contents"));

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.ToolOutput && e.Content == "README contents"));
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.ToolOutput && e.Content == "Project contents"));
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Assistant && e.Content == "Done."));
        var reasoningEvent = history.OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.Reasoning);
        Assert.AreEqual("Used local tools.", reasoningEvent.Content);
        Assert.IsNotNull(reasoningEvent.Details);
        Assert.IsTrue(reasoningEvent.Details!.Value.GetProperty("protectedDataRedacted").GetBoolean());
        Assert.IsFalse(reasoningEvent.Details.Value.GetRawText().Contains("encrypted-reasoning", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OpenAIResponsesModelProviderRuntime_CodexSessionResumeReplaysLocalHistory()
    {
        using var temp = TestTempDirectory.Create();
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-before-resume",
                    modelId: "gpt-5.3-codex",
                    text: "First answer."),
            ],
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-after-resume",
                    modelId: "gpt-5.3-codex",
                    text: "Second answer."),
            ],
        ]);

        await using var providerRuntime = new OpenAIResponsesModelProviderRuntime(new OpenAIResponsesModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "codex",
                    IsDefault = true,
                    ResponsesClientFactory = _ => responsesClient,
                    CodexSubscription = new OpenAICodexSubscriptionOptions
                    {
                        Experimental = true,
                        ResponseTransport = "http",
                    },
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo(
                            "gpt-5.3-codex",
                            DisplayName: "GPT Codex",
                            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["inputTokenLimit"] = 200000L,
                            }),
                    ]),
                },
            },
        });

        string sessionId;
        await using (var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-5.3-codex",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false))
        {
            sessionId = session.SessionId;
            _ = await session.SendAsync(new AgentSendOptions
            {
                Input = new AgentInput([new AgentInputItem.Text("First prompt")]),
            }).ConfigureAwait(false);
        }

        await using (var resumed = await providerRuntime.ResumeSessionAsync(
            sessionId,
            new AgentSessionResumeOptions
            {
                Model = "gpt-5.3-codex",
                WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }).ConfigureAwait(false))
        {
            _ = await resumed.SendAsync(new AgentSendOptions
            {
                Input = new AgentInput([new AgentInputItem.Text("Second prompt")]),
            }).ConfigureAwait(false);
        }

        Assert.AreEqual(2, responsesClient.Requests.Count);
        Assert.IsNull(responsesClient.Requests[0].Options.PreviousResponseId);
        Assert.IsNull(responsesClient.Requests[1].Options.PreviousResponseId);
        StringAssert.Contains(responsesClient.Requests[1].SerializedOptions, "First prompt");
        StringAssert.Contains(responsesClient.Requests[1].SerializedOptions, "First answer.");
        StringAssert.Contains(responsesClient.Requests[1].SerializedOptions, "Second prompt");
    }

    [TestMethod]
    public async Task OpenAIResponsesModelProviderRuntime_CodexHttpLiveSessionReplaysFullContextWithoutPreviousResponseId()
    {
        using var temp = TestTempDirectory.Create();
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-live-provider-1",
                    modelId: "gpt-5.3-codex",
                    text: "First live answer."),
            ],
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-live-provider-2",
                    modelId: "gpt-5.3-codex",
                    text: "Second live answer."),
            ],
        ]);

        await using var providerRuntime = new OpenAIResponsesModelProviderRuntime(new OpenAIResponsesModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "codex",
                    IsDefault = true,
                    ResponsesClientFactory = _ => responsesClient,
                    CodexSubscription = new OpenAICodexSubscriptionOptions
                    {
                        Experimental = true,
                        ResponseTransport = "http",
                    },
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo("gpt-5.3-codex", DisplayName: "GPT Codex"),
                    ]),
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-5.3-codex",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("First live prompt")]),
        }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Second live prompt")]),
        }).ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.Requests.Count);
        Assert.IsNull(responsesClient.Requests[0].Options.PreviousResponseId);
        Assert.IsNull(responsesClient.Requests[1].Options.PreviousResponseId);
        Assert.IsTrue(responsesClient.Requests[1].InputItems.Count > 1);
        StringAssert.Contains(responsesClient.Requests[1].SerializedOptions, "First live prompt");
        StringAssert.Contains(responsesClient.Requests[1].SerializedOptions, "First live answer.");
        StringAssert.Contains(responsesClient.Requests[1].SerializedOptions, "Second live prompt");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexDefaultsToWebSocketAndFallsBackToHttpAfterRetriesBeforeStreaming()
    {
        var webSocketSession = new ThrowingOpenAIResponsesWebSocketSession(
            WithZeroRetryAfter(new HttpRequestException("WebSocket unavailable.")));
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-fallback",
                    modelId: "gpt-5.3-codex",
                    text: "Fallback answer.",
                    reasoningText: "Recovered over HTTP.",
                    encryptedReasoning: null),
            ],
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-fallback-2",
                    modelId: "gpt-5.3-codex",
                    text: "Sticky fallback answer.",
                    reasoningText: "Stayed on HTTP.",
                    encryptedReasoning: null),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });

        var response = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);
        var secondResponse = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(6, webSocketSession.RequestCount);
        Assert.AreEqual(2, responsesClient.Requests.Count);
        Assert.AreEqual("Fallback answer.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
        Assert.AreEqual("Sticky fallback answer.", secondResponse.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexWebSocketAbruptCloseFallsBackAfterRetriesBeforeVisibleOutput()
    {
        var webSocketSession = new PartiallyFailingOpenAIResponsesWebSocketSession(
            CreateCreatedResponseUpdate(
                responseId: "response-started",
                modelId: "gpt-5.3-codex"),
            WithZeroRetryAfter(new WebSocketException("The remote party closed the WebSocket connection without completing the close handshake.")));
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-fallback-abrupt-close",
                    modelId: "gpt-5.3-codex",
                    text: "Fallback answer.",
                    reasoningText: "Recovered over HTTP.",
                    encryptedReasoning: null),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });
        var deltas = new List<AgentTurnDelta>();

        var response = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                (delta, _) =>
                {
                    deltas.Add(delta);
                    return ValueTask.CompletedTask;
                })
            .ConfigureAwait(false);

        Assert.AreEqual(6, webSocketSession.RequestCount);
        Assert.AreEqual(6, webSocketSession.DisposeCount);
        Assert.AreEqual(1, responsesClient.Requests.Count);
        Assert.AreEqual(0, deltas.Count);
        Assert.AreEqual("Fallback answer.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexWebSocketTransportAbortFallsBackAfterRetriesBeforeVisibleOutput()
    {
        var webSocketSession = new PartiallyFailingOpenAIResponsesWebSocketSession(
            CreateCreatedResponseUpdate(
                responseId: "response-started",
                modelId: "gpt-5.3-codex"),
            WithZeroRetryAfter(CreateTransportAbortedTaskCanceledException()));
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-fallback-transport-abort",
                    modelId: "gpt-5.3-codex",
                    text: "Fallback answer.",
                    reasoningText: "Recovered over HTTP.",
                    encryptedReasoning: null),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });

        var response = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(6, webSocketSession.RequestCount);
        Assert.AreEqual(6, webSocketSession.DisposeCount);
        Assert.AreEqual(1, responsesClient.Requests.Count);
        Assert.AreEqual("Fallback answer.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexWebSocketFallbackDiscardsAbandonedStreamState()
    {
        var webSocketSession = new PartiallyFailingOpenAIResponsesWebSocketSession(
            CreateOutputItemDoneUpdate(
                0,
                ResponseItem.CreateAssistantMessageItem("Abandoned WebSocket answer.", [])),
            WithZeroRetryAfter(new WebSocketException("The remote party closed the WebSocket connection without completing the close handshake.")));
        var fallbackResponse = new ResponseResult
        {
            Id = "response-http-empty",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.3-codex",
            Status = ResponseStatus.Completed,
        };
        fallbackResponse.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("HTTP fallback answer.", []));
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateCompletedUpdate(fallbackResponse)],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });

        var response = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(6, webSocketSession.RequestCount);
        Assert.AreEqual(1, responsesClient.Requests.Count);
        Assert.AreEqual("response-http-empty", response.ProviderSessionId);
        Assert.AreEqual("HTTP fallback answer.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexHttpOnlyTransportSkipsWebSocket()
    {
        var webSocketSession = new ThrowingOpenAIResponsesWebSocketSession(
            new InvalidOperationException("WebSocket should not be used."));
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-http-only",
                    modelId: "gpt-5.3-codex",
                    text: "HTTP answer.",
                    reasoningText: "Stayed on HTTP.",
                    encryptedReasoning: null),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        _ = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(0, webSocketSession.RequestCount);
        Assert.AreEqual(1, responsesClient.Requests.Count);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexSlowResponseDoesNotPublishPendingStatus()
    {
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionUpdates = new List<AgentTurnSessionUpdate>();
        var responsesClient = new DelayedOpenAIResponseClient(
            releaseResponse.Task,
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-slow",
                    modelId: "gpt-5.3-codex",
                    text: "Slow answer."),
            ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var turnTask = executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            static (_, _) => ValueTask.CompletedTask,
            (update, _) =>
            {
                sessionUpdates.Add(update);
                return ValueTask.CompletedTask;
            });

        releaseResponse.SetResult();

        var response = await turnTask.ConfigureAwait(false);
        Assert.AreEqual("Slow answer.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
        Assert.IsFalse(sessionUpdates.Any(static update =>
            update.Kind == AgentSessionUpdateKind.Info &&
            update.Message?.Contains("Waiting for ChatGPT/Codex response", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexWebSocketHttpErrorsDoNotFallbackToHttp()
    {
        var webSocketSession = new ThrowingOpenAIResponsesWebSocketSession(
            new HttpRequestException("Wrapped request error.", null, HttpStatusCode.BadRequest));
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "unexpected-http",
                    modelId: "gpt-5.3-codex",
                    text: "Should not be used.",
                    reasoningText: "Should not be used.",
                    encryptedReasoning: null),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    CreateCodexTurnRequest(),
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        Assert.AreEqual(1, webSocketSession.RequestCount);
        Assert.AreEqual(0, responsesClient.Requests.Count);
        StringAssert.Contains(exception.Failure.Message, "rejected the request shape");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RetriesCodexWebSocketConnectionLimitWithoutHttpFallback()
    {
        var connectionLimit = new HttpRequestException("Responses websocket connection limit reached.");
        connectionLimit.Data["OpenAI.WebSocketErrorCode"] = "websocket_connection_limit_reached";
        connectionLimit.Data["Retry-After"] = TimeSpan.Zero;
        var webSocketSession = new FlakyOpenAIResponsesWebSocketSession(
            [connectionLimit],
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-websocket-retried",
                    modelId: "gpt-5.3-codex",
                    text: "Retried over WebSocket.",
                    reasoningText: "Opened a fresh WebSocket.",
                    encryptedReasoning: null),
            ]);
        var responsesClient = new RecordingOpenAIResponseClient([]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });

        var response = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(2, webSocketSession.RequestCount);
        Assert.AreEqual(0, responsesClient.Requests.Count);
        Assert.AreEqual("Retried over WebSocket.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_DisposeProviderSessionClosesWebSocketSession()
    {
        var webSocketSession = new RecordingOpenAIResponsesWebSocketSession(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-dispose",
                    modelId: "gpt-5.3-codex",
                    text: "Disposable answer.",
                    reasoningText: "Disposable reasoning.",
                    encryptedReasoning: null),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => new RecordingOpenAIResponseClient([]),
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });

        _ = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);
        await ((IAgentProviderSessionCleanup)executor).DisposeProviderSessionAsync("session-1").ConfigureAwait(false);

        Assert.AreEqual(1, webSocketSession.RequestCount);
        Assert.AreEqual(1, webSocketSession.DisposeCount);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_IdleExpiryClosesWebSocketSession()
    {
        var firstWebSocketSession = new RecordingOpenAIResponsesWebSocketSession(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-idle-1",
                    modelId: "gpt-5.3-codex",
                    text: "First WebSocket answer.",
                    reasoningText: "First reasoning.",
                    encryptedReasoning: null),
            ],
        ]);
        var secondWebSocketSession = new RecordingOpenAIResponsesWebSocketSession(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-idle-2",
                    modelId: "gpt-5.3-codex",
                    text: "Second WebSocket answer.",
                    reasoningText: "Second reasoning.",
                    encryptedReasoning: null),
            ],
        ]);
        var sessions = new Queue<RecordingOpenAIResponsesWebSocketSession>([firstWebSocketSession, secondWebSocketSession]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => new RecordingOpenAIResponseClient([]),
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(sessions.Dequeue()),
            ResponsesWebSocketIdleTimeout = TimeSpan.Zero,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });

        _ = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);
        _ = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(1, firstWebSocketSession.RequestCount);
        Assert.AreEqual(1, firstWebSocketSession.DisposeCount);
        Assert.AreEqual(1, secondWebSocketSession.RequestCount);
        Assert.AreEqual(1, secondWebSocketSession.DisposeCount);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexWebSocketSideChannelsDoNotPersistRawPayloads()
    {
        var webSocketSession = new RecordingOpenAIResponsesWebSocketSession(
        [
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-side-channel",
                    modelId: "gpt-5.3-codex",
                    text: "Side channel answer."),
            ],
        ],
        [
            new OpenAIResponsesWebSocketSideChannelEvent(
                "codex.rate_limits",
                BinaryData.FromString(
                    """
                    {
                      "type": "codex.rate_limits",
                      "limit_name": "ChatGPT Plus",
                      "metered_limit_name": "gpt-5",
                      "plan_type": "plus",
                      "rate_limits": {
                        "primary": { "used_percent": 12.5, "reset_at": 1234567890, "window_minutes": 300 },
                        "secondary": { "used_percent": 3 }
                      },
                      "credits": { "has_credits": true, "unlimited": false, "balance": "42" }
                    }
                    """)),
            new OpenAIResponsesWebSocketSideChannelEvent(
                "websocket.handshake",
                BinaryData.FromString(
                    """
                    {
                      "type": "websocket.handshake",
                      "headers": {
                        "OpenAI-Model": "gpt-5.3-codex",
                        "x-models-etag": "models-etag-handshake",
                        "x-reasoning-included": "true"
                      }
                    }
                    """)),
            new OpenAIResponsesWebSocketSideChannelEvent(
                "server_model",
                BinaryData.FromString(
                    """
                    {
                      "type": "server_model",
                      "model": "gpt-5.3-codex",
                      "models_etag": "models-etag-123"
                    }
                    """)),
            new OpenAIResponsesWebSocketSideChannelEvent(
                "response.metadata",
                BinaryData.FromString(
                    """
                    {
                      "type": "response.metadata",
                      "metadata": { "openai_verification_recommendation": ["trustedAccessForCyber"] }
                    }
                    """)),
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => new RecordingOpenAIResponseClient([]),
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });

        var response = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.IsTrue(response.ProviderState.HasValue);
        var providerState = response.ProviderState.Value;
        Assert.AreEqual("response-side-channel", providerState.GetProperty("responseId").GetString());
        Assert.IsFalse(providerState.TryGetProperty("codexWebSocketSideChannels", out _));
        Assert.IsFalse(providerState.ToString().Contains("trustedAccessForCyber", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_ProjectsMetadataBeforeCompletionAndPersistsOnlyAllowlistedSummary()
    {
        var completed = CreateTextOnlyAssistantResponseUpdate(
            responseId: "response-metadata",
            modelId: "gpt-5.3-codex-reroute",
            text: "Metadata answer.");
        var session = new ProtocolEventOpenAIResponsesWebSocketSession(
        [
            new CodexProtocolEvent(
                CodexProtocolTransport.WebSocket,
                Type: null,
                Update: null,
                new CodexResponseMetadata(
                    RequestId: "request-allowlisted",
                    EffectiveModel: "gpt-5.3-codex-reroute",
                    ModelsETag: "models-v3",
                    ReasoningIncluded: true,
                    SafetyBuffering: new CodexSafetyBuffering(false, null, "true", "gpt-5.3-codex-fast"),
                    RateLimits:
                    [
                        new CodexNamedRateLimitSnapshot(
                            "codex",
                            Primary: new CodexNamedRateLimitWindow(12.5, 300, DateTimeOffset.FromUnixTimeSeconds(1770000000))),
                    ])),
            new CodexProtocolEvent(
                CodexProtocolTransport.WebSocket,
                "response.metadata",
                null,
                new CodexResponseMetadata(
                    SafetyBuffering: new CodexSafetyBuffering(false, null, null, Message: "Safety check"),
                    VerificationRecommendation: "trusted_access_for_cyber",
                    TurnModeration: "{\"flagged\":true,\"secret\":\"transient-only\"}",
                    TurnState: "never-persist-turn-state")),
            new CodexProtocolEvent(
                CodexProtocolTransport.WebSocket,
                "response.completed",
                completed,
                new CodexResponseMetadata(),
                new CodexTerminalMetadata(false)),
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => new RecordingOpenAIResponseClient([]),
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(session),
            CodexSubscription = new OpenAICodexSubscriptionOptions { Experimental = true },
        });
        var updates = new List<AgentTurnSessionUpdate>();

        var response = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                static (_, _) => ValueTask.CompletedTask,
                (update, _) =>
                {
                    updates.Add(update);
                    return ValueTask.CompletedTask;
                })
            .ConfigureAwait(false);

        Assert.IsTrue(response.RequiresProviderFollowUp);
        Assert.AreEqual(AgentSessionUpdateKind.ModelChanged, updates[0].Kind);
        Assert.IsTrue(updates.Any(static update => update.Kind == AgentSessionUpdateKind.UsageUpdated && update.Usage is not null));
        Assert.IsTrue(updates.Any(update => update.Details?.ToString().Contains("gpt-5.3-codex-fast", StringComparison.Ordinal) == true));
        var details = Assert.IsInstanceOfType<CodexSessionUsageDetails>(response.Usage?.Details);
        Assert.AreEqual("codex", details.NamedRateLimits?[0].LimitId);
        Assert.AreEqual(13, details.RateLimits?.Primary?.UsedPercent);
        var providerState = response.ProviderState?.ToString() ?? string.Empty;
        StringAssert.Contains(providerState, "request-allowlisted");
        StringAssert.Contains(providerState, "gpt-5.3-codex-reroute");
        Assert.IsFalse(providerState.Contains("transient-only", StringComparison.Ordinal));
        Assert.IsFalse(providerState.Contains("never-persist-turn-state", StringComparison.Ordinal));
        Assert.IsFalse(providerState.Contains("authorization", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexWebSocketReceivesFullReconnectPayloadForContinuation()
    {
        var webSocketSession = new RecordingOpenAIResponsesWebSocketSession(
        [
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-ws-live-1",
                    modelId: "gpt-5.3-codex",
                    text: "First answer."),
            ],
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-ws-live-2",
                    modelId: "gpt-5.3-codex",
                    text: "Second answer."),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => new RecordingOpenAIResponseClient([]),
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });
        var firstUserMessage = new AgentConversationMessage(
            AgentConversationRole.User,
            [new AgentMessagePart.Text("First prompt")]);
        var firstRequest = CreateCodexTurnRequest() with
        {
            Conversation = [firstUserMessage],
            CanUseProviderContinuation = true,
        };

        var firstResponse = await executor.ExecuteTurnAsync(
                firstRequest,
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);
        var secondRequest = CreateCodexTurnRequest() with
        {
            CanUseProviderContinuation = true,
            Conversation =
            [
                firstUserMessage,
                firstResponse.AssistantMessage,
                new AgentConversationMessage(
                    AgentConversationRole.User,
                    [new AgentMessagePart.Text("Second prompt")]),
            ],
        };

        _ = await executor.ExecuteTurnAsync(
                secondRequest,
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(2, webSocketSession.Requests.Count);
        Assert.IsNull(webSocketSession.Requests[0].PreviousResponseId);
        Assert.IsNull(webSocketSession.Requests[0].ReconnectPreviousResponseId);
        Assert.AreEqual("response-ws-live-1", webSocketSession.Requests[1].PreviousResponseId);
        Assert.IsNull(webSocketSession.Requests[1].ReconnectPreviousResponseId);
        Assert.IsTrue(webSocketSession.Requests[1].ReconnectInputItemCount > webSocketSession.Requests[1].InputItemCount);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexWebSocketFallbackUsesFullContextWithoutPreviousResponseId()
    {
        var webSocketSession = new SecondRequestThrowingOpenAIResponsesWebSocketSession(
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-ws-fallback-live-1",
                    modelId: "gpt-5.3-codex",
                    text: "First answer."),
            ],
            WithZeroRetryAfter(new HttpRequestException("WebSocket failed before streaming.")));
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-http-fallback-live-2",
                    modelId: "gpt-5.3-codex",
                    text: "Second answer."),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });
        var firstUserMessage = new AgentConversationMessage(
            AgentConversationRole.User,
            [new AgentMessagePart.Text("First prompt")]);
        var firstRequest = CreateCodexTurnRequest() with
        {
            Conversation = [firstUserMessage],
            CanUseProviderContinuation = true,
        };

        var firstResponse = await executor.ExecuteTurnAsync(
                firstRequest,
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);
        var secondRequest = CreateCodexTurnRequest() with
        {
            CanUseProviderContinuation = true,
            Conversation =
            [
                firstUserMessage,
                firstResponse.AssistantMessage,
                new AgentConversationMessage(
                    AgentConversationRole.User,
                    [new AgentMessagePart.Text("Second prompt")]),
            ],
        };

        _ = await executor.ExecuteTurnAsync(
                secondRequest,
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(7, webSocketSession.Requests.Count);
        Assert.AreEqual("response-ws-fallback-live-1", webSocketSession.Requests[1].PreviousResponseId);
        Assert.AreEqual(1, responsesClient.Requests.Count);
        Assert.IsNull(responsesClient.Requests[0].Options.PreviousResponseId);
        Assert.IsTrue(responsesClient.Requests[0].InputItems.Count > 1);
        StringAssert.Contains(responsesClient.Requests[0].SerializedOptions, "First prompt");
        StringAssert.Contains(responsesClient.Requests[0].SerializedOptions, "First answer.");
        StringAssert.Contains(responsesClient.Requests[0].SerializedOptions, "Second prompt");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexHttpTransportReplaysFullContextWithoutPreviousResponseId()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-live-1",
                    modelId: "gpt-5.3-codex",
                    text: "First answer."),
            ],
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-live-2",
                    modelId: "gpt-5.3-codex",
                    text: "Second answer."),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });
        var firstUserMessage = new AgentConversationMessage(
            AgentConversationRole.User,
            [new AgentMessagePart.Text("First prompt")]);
        var firstRequest = CreateCodexTurnRequest() with
        {
            Conversation = [firstUserMessage],
            CanUseProviderContinuation = true,
        };

        var firstResponse = await executor.ExecuteTurnAsync(
                firstRequest,
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);
        var secondRequest = CreateCodexTurnRequest() with
        {
            CanUseProviderContinuation = true,
            Conversation =
            [
                firstUserMessage,
                firstResponse.AssistantMessage,
                new AgentConversationMessage(
                    AgentConversationRole.User,
                    [new AgentMessagePart.Text("Second prompt")]),
            ],
        };

        _ = await executor.ExecuteTurnAsync(
                secondRequest,
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.Requests.Count);
        Assert.IsNull(responsesClient.Requests[0].Options.PreviousResponseId);
        Assert.IsNull(responsesClient.Requests[1].Options.PreviousResponseId);
        Assert.IsTrue(responsesClient.Requests[1].InputItems.Count > 1);
        StringAssert.Contains(responsesClient.Requests[1].SerializedOptions, "First prompt");
        StringAssert.Contains(responsesClient.Requests[1].SerializedOptions, "First answer.");
        StringAssert.Contains(responsesClient.Requests[1].SerializedOptions, "Second prompt");
    }

    [TestMethod]
    public void CodexSubscriptionDiagnostics_EmitOnlySanitizedRequestShape()
    {
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            BaseUri = new Uri("https://chatgpt.com/backend-api/codex?access_token=secret"),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        };
        var request = CreateCodexTurnRequest() with
        {
            Conversation =
            [
                new AgentConversationMessage(
                    AgentConversationRole.User,
                    [new AgentMessagePart.Text("do not log this prompt")]),
            ],
        };

        var message = OpenAICodexSubscriptionDiagnostics.CreateRequestShape(
            provider,
            request,
            retryCount: 2,
            eventName: "retry",
            responseId: "response-1",
            httpStatus: HttpStatusCode.TooManyRequests,
            errorType: "HttpRequestException");

        StringAssert.Contains(message, "provider=codex");
        StringAssert.Contains(message, "endpoint=chatgpt.com/backend-api/codex");
        StringAssert.Contains(message, "session=session-1");
        StringAssert.Contains(message, "run=run-1");
        StringAssert.Contains(message, "retry=2");
        StringAssert.Contains(message, "response=response-1");
        StringAssert.Contains(message, "http=429");
        StringAssert.Contains(message, "errorType=HttpRequestException");
        Assert.IsFalse(message.Contains("access_token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(message.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(message.Contains("do not log this prompt", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OpenAIResponsesModelProviderRuntime_CodexTokensAreNotStoredInSessionHistory()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await store.SaveAsync(
                "codex",
                new OpenAICodexSubscriptionCredential
                {
                    AccessToken = "access-token-should-not-appear",
                    RefreshToken = "refresh-token-should-not-appear",
                    IdToken = "id-token-should-not-appear",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    AccountId = "acct_123",
                })
            .ConfigureAwait(false);
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-token-history",
                    modelId: "gpt-5.3-codex",
                    text: "Answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: null),
            ],
        ]);

        await using var providerRuntime = new OpenAIResponsesModelProviderRuntime(new OpenAIResponsesModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "codex",
                    IsDefault = true,
                    ResponsesClientFactory = _ => responsesClient,
                    CodexSubscription = new OpenAICodexSubscriptionOptions
                    {
                        Experimental = true,
                        ResponseTransport = "http",
                        ModelDiscovery = "static",
                    },
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo("gpt-5.3-codex", DisplayName: "GPT Codex"),
                    ]),
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-5.3-codex",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        var sessionsRoot = Path.Combine(temp.Path, "sessions");
        var sessionHistory = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.IsFalse(sessionHistory.Contains("access-token-should-not-appear", StringComparison.Ordinal));
        Assert.IsFalse(sessionHistory.Contains("refresh-token-should-not-appear", StringComparison.Ordinal));
        Assert.IsFalse(sessionHistory.Contains("id-token-should-not-appear", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_TranslatesCodexRateLimitErrors()
    {
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => new ThrowingOpenAIResponseClient(
                new HttpRequestException("Too many requests.", null, HttpStatusCode.TooManyRequests)),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });
        var request = CreateTurnRequest() with
        {
            Provider = CreateTurnRequest().Provider with
            {
                ProtocolFamily = "codex",
                ProviderKey = "codex",
                DisplayName = "Codex",
            },
            ModelId = "gpt-5.3-codex",
        };

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Failure.Message, "rate limit");
        StringAssert.Contains(exception.Failure.Message, "Retry");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_TranslatesCodexAuthErrors()
    {
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => new ThrowingOpenAIResponseClient(
                new HttpRequestException("Unauthorized.", null, HttpStatusCode.Unauthorized)),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });
        var request = CreateTurnRequest() with
        {
            Provider = CreateTurnRequest().Provider with
            {
                ProtocolFamily = "codex",
                ProviderKey = "codex",
                DisplayName = "Codex",
            },
            ModelId = "gpt-5.3-codex",
        };

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Failure.Message, "re-authentication");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_TranslatesCodexHttpErrors()
    {
        var cases = new (HttpStatusCode StatusCode, string Expected)[]
        {
            (HttpStatusCode.Forbidden, "account, workspace, plan, or policy"),
            (HttpStatusCode.TooManyRequests, "rate limit"),
            (HttpStatusCode.ServiceUnavailable, "temporarily unavailable"),
            (HttpStatusCode.BadRequest, "request shape"),
        };

        foreach (var testCase in cases)
        {
            var exceptionToThrow = new HttpRequestException(
                testCase.StatusCode.ToString(),
                null,
                testCase.StatusCode);
            exceptionToThrow.Data["Retry-After"] = TimeSpan.Zero;
            var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
            {
                ProviderKey = "codex",
                ResponsesClientFactory = _ => new ThrowingOpenAIResponseClient(exceptionToThrow),
                CodexSubscription = new OpenAICodexSubscriptionOptions
                {
                    Experimental = true,
                    ResponseTransport = "http",
                },
            });

            var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                    () => executor.ExecuteTurnAsync(
                        CreateCodexTurnRequest(),
                        static (_, _) => ValueTask.CompletedTask))
                .ConfigureAwait(false);

            StringAssert.Contains(exception.Failure.Message, testCase.Expected);
        }
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_TranslatesCodexProtocolDriftErrors()
    {
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => new RecordingOpenAIResponseClient([[]]),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    CreateCodexTurnRequest(),
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Failure.Message, "ended prematurely");
        StringAssert.Contains(exception.Failure.Message, "terminal response");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RefreshesCodexCredentialOnceAfterUnauthorized()
    {
        var unauthorized = new HttpRequestException("Unauthorized.", null, HttpStatusCode.Unauthorized);
        var responsesClient = new FlakyOpenAIResponseClient(
            [unauthorized],
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-after-refresh",
                    modelId: "gpt-5.3-codex",
                    text: "Authorized answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: null),
            ]);
        var refreshCount = 0;
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscriptionCredentialRefreshAsync = _ =>
            {
                refreshCount++;
                return ValueTask.CompletedTask;
            },
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var response = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(1, refreshCount);
        Assert.AreEqual(2, responsesClient.RequestCount);
        var text = response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value;
        Assert.AreEqual("Authorized answer.", text);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RefreshesCodexCredentialOnlyOnceAfterUnauthorized()
    {
        var responsesClient = new FlakyOpenAIResponseClient(
            [
                new HttpRequestException("Unauthorized once.", null, HttpStatusCode.Unauthorized),
                new HttpRequestException("Unauthorized twice.", null, HttpStatusCode.Unauthorized),
            ],
            []);
        var refreshCount = 0;
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscriptionCredentialRefreshAsync = _ =>
            {
                refreshCount++;
                return ValueTask.CompletedTask;
            },
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    CreateCodexTurnRequest(),
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        Assert.AreEqual(1, refreshCount);
        Assert.AreEqual(2, responsesClient.RequestCount);
        StringAssert.Contains(exception.Failure.Message, "re-authentication");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RetriesCodexTransientErrorsWithinBudget()
    {
        var transient = new HttpRequestException("Service unavailable.", null, HttpStatusCode.ServiceUnavailable);
        transient.Data["Retry-After"] = TimeSpan.Zero;
        var responsesClient = new FlakyOpenAIResponseClient(
            [transient],
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-retried",
                    modelId: "gpt-5.3-codex",
                    text: "Retried answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: null),
            ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var response = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.RequestCount);
        var text = response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value;
        Assert.AreEqual("Retried answer.", text);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RetriesCodexResponseFailedServerOverloadedBeforeVisibleOutput()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateFailedResponseUpdate("response-overloaded", "gpt-5.3-codex", "server_overloaded", "Server overloaded.")],
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-retried",
                    modelId: "gpt-5.3-codex",
                    text: "Retried answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: null),
            ],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var response = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.Requests.Count);
        var text = response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value;
        Assert.AreEqual("Retried answer.", text);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RetriesCodexUnknownStreamErrorBeforeVisibleOutput()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateErrorResponseUpdate(null, "Please try again in 0ms.")],
            [CreateTextOnlyAssistantResponseUpdate("response-retried", "gpt-5.3-codex", "Retried answer.")],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });
        var sessionUpdates = new List<AgentTurnSessionUpdate>();

        var response = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            static (_, _) => ValueTask.CompletedTask,
            (update, _) =>
            {
                sessionUpdates.Add(update);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.Requests.Count);
        Assert.AreEqual(AgentSessionUpdateKind.Reconnecting, sessionUpdates.Single().Kind);
        Assert.AreEqual("Retried answer.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_DoesNotRetryCodexFatalStreamError()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateErrorResponseUpdate("invalid_prompt", "The prompt is invalid.")],
            [CreateTextOnlyAssistantResponseUpdate("response-should-not-run", "gpt-5.3-codex", "Unexpected.")],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    CreateCodexTurnRequest(),
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        Assert.AreEqual(1, responsesClient.Requests.Count);
        StringAssert.Contains(exception.Failure.Message, "invalid_prompt");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RetriesCodexResponseFailedUnknownErrorBeforeVisibleOutput()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateFailedResponseUpdate("response-unknown", "gpt-5.3-codex", "unknown_error", "Unknown service error. Please try again in 0ms.")],
            [CreateTextOnlyAssistantResponseUpdate("response-retried", "gpt-5.3-codex", "Retried answer.")],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var response = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.Requests.Count);
        Assert.AreEqual("Retried answer.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RetriesCodexIncompleteResponseEvenWithOutputItems()
    {
        var incompleteResponse = new ResponseResult
        {
            Id = "response-incomplete",
            CreatedAt = DateTimeOffset.UtcNow,
            Model = "gpt-5.3-codex",
            Status = ResponseStatus.Incomplete,
        };
        incompleteResponse.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Partial answer.", []));
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateIncompleteUpdate(incompleteResponse)],
            [CreateTextOnlyAssistantResponseUpdate("response-retried", "gpt-5.3-codex", "Retried answer.")],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var response = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.Requests.Count);
        Assert.AreEqual("Retried answer.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_DoesNotRetryCodexUsageLimit429()
    {
        var usageLimit = new ClientResultException(
            new TestPipelineResponse(
                """
                {"error":{"type":"usage_limit_reached","message":"The usage limit has been reached."}}
                """,
                status: 429,
                reasonPhrase: "Too Many Requests"));
        var responsesClient = new FlakyOpenAIResponseClient([usageLimit], []);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    CreateCodexTurnRequest(),
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        Assert.AreEqual(1, responsesClient.RequestCount);
        StringAssert.Contains(exception.Failure.Message, "usage limit");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_DoesNotRetryCodexResponseFailedUsageLimit()
    {
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [CreateFailedResponseUpdate("response-usage-limit", "gpt-5.3-codex", "usage_limit_reached", "The usage limit has been reached.")],
            [CreateTextOnlyAssistantResponseUpdate("response-should-not-run", "gpt-5.3-codex", "Unexpected.")],
        ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var exception = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    CreateCodexTurnRequest(),
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        Assert.AreEqual(1, responsesClient.Requests.Count);
        StringAssert.Contains(exception.Failure.Message, "usage_limit_reached");
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_DoesNotRetryCodexBadRequest()
    {
        var responsesClient = new FlakyOpenAIResponseClient(
            [new HttpRequestException("Bad request.", null, HttpStatusCode.BadRequest)],
            []);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        _ = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    CreateCodexTurnRequest(),
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        Assert.AreEqual(1, responsesClient.RequestCount);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RetriesCodexPrematureEndBeforeVisibleOutput()
    {
        var prematureEnd = CreatePrematureResponseEndException();
        prematureEnd.Data["Retry-After"] = TimeSpan.Zero;
        var responsesClient = new PartiallyFailingThenSuccessOpenAIResponseClient(
            CreateCreatedResponseUpdate(
                responseId: "response-started",
                modelId: "gpt-5.3-codex"),
            prematureEnd,
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-retried",
                    modelId: "gpt-5.3-codex",
                    text: "Retried answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: null),
            ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });
        var deltas = new List<AgentTurnDelta>();
        var sessionUpdates = new List<AgentTurnSessionUpdate>();

        var response = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            },
            (update, _) =>
            {
                sessionUpdates.Add(update);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.RequestCount);
        Assert.AreEqual(0, deltas.Count);
        Assert.AreEqual(1, sessionUpdates.Count);
        Assert.AreEqual(AgentSessionUpdateKind.Reconnecting, sessionUpdates[0].Kind);
        Assert.AreEqual("Reconnecting to ChatGPT/Codex... 1/5", sessionUpdates[0].Message);
        var text = response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value;
        Assert.AreEqual("Retried answer.", text);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RetriesCodexPrematureEndAfterVisibleOutputAndDiscardsDraft()
    {
        var prematureEnd = CreatePrematureResponseEndException();
        prematureEnd.Data["Retry-After"] = TimeSpan.Zero;
        var responsesClient = new PartiallyFailingThenSuccessOpenAIResponseClient(
            CreateOutputTextDeltaUpdate(itemId: "msg_1", delta: "partial"),
            prematureEnd,
            [
                CreateAssistantResponseUpdate(
                    responseId: "response-retried",
                    modelId: "gpt-5.3-codex",
                    text: "Retried answer.",
                    reasoningText: "Thinking.",
                    encryptedReasoning: null),
            ]);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });
        var deltas = new List<AgentTurnDelta>();
        var sessionUpdates = new List<AgentTurnSessionUpdate>();

        var response = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            },
            (update, _) =>
            {
                sessionUpdates.Add(update);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.RequestCount);
        Assert.AreEqual(1, deltas.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(deltas[0].AttemptId));
        var reconnect = sessionUpdates.Single();
        Assert.AreEqual(AgentSessionUpdateKind.Reconnecting, reconnect.Kind);
        Assert.IsTrue(reconnect.Details.HasValue);
        Assert.IsTrue(reconnect.Details.Value.GetProperty("discardDraft").GetBoolean());
        Assert.AreEqual(deltas[0].AttemptId, reconnect.Details.Value.GetProperty("draftAttemptId").GetString());
        Assert.AreEqual("Retried answer.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    private static FileSystemAgentSessionStore CreateSessionStore(string stateRootPath)
        => new(new AgentRuntimePathLayout(stateRootPath));

    private static StreamingResponseUpdate CreateAssistantResponseUpdate(
        string responseId,
        string modelId,
        string text,
        string reasoningText,
        string? encryptedReasoning,
        int inputTokens = 0,
        int outputTokens = 0,
        int? cachedInputTokens = null,
        int? reasoningTokens = null,
        int? totalTokens = null)
    {
        var response = new ResponseResult
        {
            Id = responseId,
            CreatedAt = DateTimeOffset.UtcNow,
            Model = modelId,
        };
        response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem(text, []));
        response.OutputItems.Add(
            new ReasoningResponseItem(reasoningText)
            {
                EncryptedContent = encryptedReasoning,
            });
        return DeserializeStreamingResponseUpdate(
            $$"""
            {
              "type": "response.completed",
              "sequence_number": 0,
              "response": {{SerializeResponseWithUsage(response, inputTokens, outputTokens, cachedInputTokens, reasoningTokens, totalTokens)}}
            }
            """);
    }

    private static StreamingResponseUpdate CreateTextOnlyAssistantResponseUpdate(
        string responseId,
        string modelId,
        string text)
    {
        var response = new ResponseResult
        {
            Id = responseId,
            CreatedAt = DateTimeOffset.UtcNow,
            Model = modelId,
        };
        response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem(text, []));
        return CreateCompletedUpdate(response);
    }

    private static StreamingResponseUpdate CreateOutputTextDeltaUpdate(string itemId, string delta)
        => CreateOutputTextDeltaUpdate(itemId, delta, outputIndex: 0, contentIndex: 0);

    private static StreamingResponseUpdate CreateOutputTextDeltaUpdate(
        string itemId,
        string delta,
        int outputIndex,
        int contentIndex = 0)
        => DeserializeStreamingResponseUpdate(
            $$"""
              {
                "type": "response.output_text.delta",
                "sequence_number": 1,
                "item_id": "{{itemId}}",
                "output_index": {{outputIndex}},
                "content_index": {{contentIndex}},
                "delta": "{{delta}}"
              }
              """);

    private static StreamingResponseUpdate CreateReasoningSummaryTextDeltaUpdate(
        string itemId,
        string delta,
        int summaryIndex)
        => DeserializeStreamingResponseUpdate(
            $$"""
              {
                "type": "response.reasoning_summary_text.delta",
                "sequence_number": 1,
                "item_id": "{{itemId}}",
                "summary_index": {{summaryIndex}},
                "delta": "{{delta}}"
              }
              """);

    private static StreamingResponseUpdate CreateReasoningSummaryTextDoneUpdate(
        string itemId,
        string text,
        int summaryIndex)
        => DeserializeStreamingResponseUpdate(
            $$"""
              {
                "type": "response.reasoning_summary_text.done",
                "sequence_number": 2,
                "item_id": "{{itemId}}",
                "output_index": 1,
                "summary_index": {{summaryIndex}},
                "text": "{{text}}"
              }
              """);

    private static StreamingResponseUpdate CreateReasoningSummaryPartUpdate(
        string type,
        string itemId,
        int summaryIndex)
        => DeserializeStreamingResponseUpdate(
            $$"""
              {
                "type": "{{type}}",
                "sequence_number": 2,
                "item_id": "{{itemId}}",
                "output_index": 1,
                "summary_index": {{summaryIndex}},
                "part": { "type": "summary_text", "text": "Done only" }
              }
              """);

    private static StreamingResponseUpdate CreateReasoningTextDoneUpdate(
        string itemId,
        string text,
        int contentIndex)
        => DeserializeStreamingResponseUpdate(
            $$"""
              {
                "type": "response.reasoning_text.done",
                "sequence_number": 3,
                "item_id": "{{itemId}}",
                "output_index": 1,
                "content_index": {{contentIndex}},
                "text": "{{text}}"
              }
              """);

    private static HttpIOException CreatePrematureResponseEndException()
        => new(
            HttpRequestError.ResponseEnded,
            "The response ended prematurely. (ResponseEnded)",
            innerException: null!);

    private static TaskCanceledException CreateTransportAbortedTaskCanceledException()
        => new(
            "The operation was canceled.",
            new IOException(
                "Unable to read data from the transport connection: The I/O operation has been aborted because of either a session exit or an application request.",
                new SocketException((int)SocketError.OperationAborted)));

    private static TException WithZeroRetryAfter<TException>(TException exception)
        where TException : Exception
    {
        exception.Data["Retry-After"] = TimeSpan.Zero;
        return exception;
    }

    private static StreamingResponseUpdate CreateCreatedResponseUpdate(
        string responseId,
        string modelId)
    {
        var response = new ResponseResult
        {
            Id = responseId,
            CreatedAt = DateTimeOffset.UtcNow,
            Model = modelId,
            Status = ResponseStatus.InProgress,
        };
        return DeserializeStreamingResponseUpdate(
            $$"""
            {
              "type": "response.created",
              "sequence_number": 0,
              "response": {{SerializeModel(response)}}
            }
            """);
    }

    private static OpenAIResponsesTurnExecutor CreateCodexExecutor(ResponsesClient responsesClient)
        => new(new OpenAIProviderOptions
        {
            ProviderKey = "codex",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

    private static StreamingResponseUpdate CreateFailedResponseUpdate(
        string responseId,
        string modelId,
        string code,
        string message)
        => DeserializeStreamingResponseUpdate(
            $$"""
            {
              "type": "response.failed",
              "sequence_number": 0,
              "response": {
                "id": "{{responseId}}",
                "object": "response",
                "created_at": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},
                "model": "{{modelId}}",
                "status": "failed",
                "error": {
                  "code": "{{code}}",
                  "message": "{{message}}"
                },
                "output": []
              }
            }
            """);

    private static StreamingResponseUpdate CreateOutputItemDoneUpdate(
        int outputIndex,
        ResponseItem item)
        => DeserializeStreamingResponseUpdate(
            $$"""
            {
              "type": "response.output_item.done",
              "sequence_number": 0,
              "output_index": {{outputIndex}},
              "item": {{SerializeModel(item)}}
            }
            """);

    private static StreamingResponseUpdate CreateOutputItemAddedUpdate(
        int outputIndex,
        ResponseItem item)
        => DeserializeStreamingResponseUpdate(
            $$"""
            {
              "type": "response.output_item.added",
              "sequence_number": 0,
              "output_index": {{outputIndex}},
              "item": {{SerializeModel(item)}}
            }
            """);

    private static StreamingResponseUpdate CreateErrorResponseUpdate(
        string? code,
        string message,
        string? param = null)
        => DeserializeStreamingResponseUpdate(
            $$"""
            {
              "type": "error",
              "sequence_number": 0,
              "code": {{SerializeJsonStringOrNull(code)}},
              "message": {{SerializeJsonStringOrNull(message)}},
              "param": {{SerializeJsonStringOrNull(param)}}
            }
            """);

    private static StreamingResponseUpdate CreateToolCallResponseUpdate(
        string responseId,
        string modelId,
        string callId,
        string toolName,
        string arguments,
        string summaryText)
    {
        var response = new ResponseResult
        {
            Id = responseId,
            CreatedAt = DateTimeOffset.UtcNow,
            Model = modelId,
        };
        response.OutputItems.Add(new ReasoningResponseItem(summaryText));
        response.OutputItems.Add(ResponseItem.CreateFunctionCallItem(callId, toolName, BinaryData.FromString(arguments)));
        return CreateCompletedUpdate(response);
    }

    private static StreamingResponseUpdate CreateCompletedUpdate(ResponseResult response)
        => DeserializeStreamingResponseUpdate(
            $$"""
            {
              "type": "response.completed",
              "sequence_number": 0,
              "response": {{SerializeModel(response)}}
            }
            """);

    private static StreamingResponseUpdate CreateIncompleteUpdate(ResponseResult response)
        => DeserializeStreamingResponseUpdate(
            $$"""
            {
              "type": "response.incomplete",
              "sequence_number": 0,
              "response": {{SerializeModel(response)}}
            }
            """);

    private static string SerializeResponseWithUsage(
        ResponseResult response,
        int inputTokens,
        int outputTokens,
        int? cachedInputTokens = null,
        int? reasoningTokens = null,
        int? totalTokens = null)
    {
        using var source = JsonDocument.Parse(SerializeModel(response));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in source.RootElement.EnumerateObject())
            {
                if (property.NameEquals("usage"))
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WritePropertyName("usage");
            writer.WriteStartObject();
            writer.WriteNumber("input_tokens", inputTokens);
            writer.WriteNumber("output_tokens", outputTokens);
            writer.WriteNumber("total_tokens", totalTokens ?? inputTokens + outputTokens);
            if (cachedInputTokens is { } cached)
            {
                writer.WritePropertyName("input_tokens_details");
                writer.WriteStartObject();
                writer.WriteNumber("cached_tokens", cached);
                writer.WriteEndObject();
            }

            if (reasoningTokens is { } reasoning)
            {
                writer.WritePropertyName("output_tokens_details");
                writer.WriteStartObject();
                writer.WriteNumber("reasoning_tokens", reasoning);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.GetRawText();
    }

    private static StreamingResponseUpdate DeserializeStreamingResponseUpdate(string json)
    {
        var data = BinaryData.FromString(json);
        using var document = JsonDocument.Parse(data);
        return (StreamingResponseUpdate)DeserializeStreamingResponseUpdateMethod.Invoke(
            obj: null,
            parameters:
            [
                document.RootElement,
                data,
                new ModelReaderWriterOptions("J"),
            ])!;
    }

    private static StreamingChatCompletionUpdate DeserializeStreamingChatCompletionUpdate(string json)
    {
        var data = BinaryData.FromString(json);
        using var document = JsonDocument.Parse(data);
        return (StreamingChatCompletionUpdate)DeserializeStreamingChatCompletionUpdateMethod.Invoke(
            obj: null,
            parameters:
            [
                document.RootElement,
                data,
                new ModelReaderWriterOptions("J"),
            ])!;
    }

    private static string SerializeModel<T>(T model)
        where T : notnull
        => ((IPersistableModel<T>)model).Write(new ModelReaderWriterOptions("J")).ToString();

    private static string SerializeJsonStringOrNull(string? value)
        => value is null ? "null" : JsonSerializer.Serialize(value);

    private static HttpResponseMessage CreateChatStreamingResponse(string completionId, string content)
    {
        var escapedCompletionId = JsonSerializer.Serialize(completionId);
        var escapedContent = JsonSerializer.Serialize(content);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                data: {"id":{{escapedCompletionId}},"object":"chat.completion.chunk","created":1744060800,"model":"gpt-chat-test","choices":[{"index":0,"delta":{"role":"assistant","content":{{escapedContent}}},"finish_reason":null}]}

                data: {"id":{{escapedCompletionId}},"object":"chat.completion.chunk","created":1744060800,"model":"gpt-chat-test","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """,
                Encoding.UTF8,
                "text/event-stream"),
        };
        return response;
    }

    private static AgentTurnRequest CreateTurnRequest()
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = "openai-responses",
                ProviderKey = "openai",
                DisplayName = "OpenAI Responses",
                TransportKind = AgentTransportKind.OpenAIResponses,
            },
            ProviderId = new ModelProviderId("openai-responses"),
            SessionId = "session-1",
            RunId = new AgentRunId("run-1"),
            ModelId = "gpt-test",
            ModelInfo = new AgentModelInfo(
                "gpt-test",
                DisplayName: "GPT Test",
                Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["inputTokenLimit"] = 200000L,
                }),
            Conversation =
            [
                new AgentConversationMessage(
                    AgentConversationRole.User,
                    [new AgentMessagePart.Text("Hello")]),
            ],
            Tools = [],
            State = new AgentSessionState
            {
                SessionId = "session-1",
                ProtocolFamily = "openai-responses",
                ProviderKey = "openai",
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };

    private static AgentTurnRequest CreateChatTurnRequest()
    {
        var request = CreateTurnRequest();
        return request with
        {
            Provider = request.Provider with
            {
                ProtocolFamily = "openai",
                ProviderKey = "openai",
                DisplayName = "OpenAI Chat",
                TransportKind = AgentTransportKind.OpenAIChatCompletions,
            },
            ProviderId = new ModelProviderId("openai"),
            State = request.State with
            {
                ProtocolFamily = "openai",
                ProviderKey = "openai",
            },
        };
    }

    private static AgentTurnRequest CreateCodexTurnRequest()
        => CreateTurnRequest() with
        {
            Provider = CreateTurnRequest().Provider with
            {
                ProtocolFamily = "codex",
                ProviderKey = "codex",
                DisplayName = "Codex",
            },
            ModelId = "gpt-5.3-codex",
        };

    private sealed class RecordingOpenAIResponseClient(IReadOnlyList<IReadOnlyList<StreamingResponseUpdate>> responseBatches)
        : ResponsesClient(new ApiKeyCredential("test-key"))
    {
        public List<ResponseRequestRecord> Requests { get; } = [];

        public override AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CancellationToken cancellationToken = default)
        {
            var clonedOptions = CloneOptions(options);
            Requests.Add(new ResponseRequestRecord(
                clonedOptions.InputItems.ToArray(),
                clonedOptions,
                SerializeModel(options)));
            var requestIndex = Requests.Count - 1;
            var updates = requestIndex < responseBatches.Count
                ? responseBatches[requestIndex]
                : [];
            return new TestAsyncCollectionResult<StreamingResponseUpdate>(updates);
        }

        private static CreateResponseOptions CloneOptions(CreateResponseOptions? options)
        {
            var clone = new CreateResponseOptions
            {
                Model = options?.Model,
                Instructions = options?.Instructions,
                ParallelToolCallsEnabled = options?.ParallelToolCallsEnabled,
                StoredOutputEnabled = options?.StoredOutputEnabled,
                PreviousResponseId = options?.PreviousResponseId,
                MaxOutputTokenCount = options?.MaxOutputTokenCount,
                ToolChoice = options?.ToolChoice,
                StreamingEnabled = options?.StreamingEnabled,
                ReasoningOptions = options?.ReasoningOptions is null
                    ? null
                    : new ResponseReasoningOptions
                    {
                        ReasoningEffortLevel = options.ReasoningOptions.ReasoningEffortLevel,
                        ReasoningSummaryVerbosity = options.ReasoningOptions.ReasoningSummaryVerbosity,
                    },
            };

            if (options is not null)
            {
                foreach (var inputItem in options.InputItems)
                {
                    clone.InputItems.Add(inputItem);
                }

                foreach (var tool in options.Tools)
                {
                    clone.Tools.Add(tool);
                }

                foreach (var includedProperty in options.IncludedProperties)
                {
                    clone.IncludedProperties.Add(includedProperty);
                }

                if (options.Patch.TryGetValue("$.custom_flag"u8, out bool customFlag))
                {
                    clone.Patch.Set("$.custom_flag"u8, customFlag);
                }

                if (options.Patch.TryGetValue("$.customized"u8, out bool customized))
                {
                    clone.Patch.Set("$.customized"u8, customized);
                }

                if (options.Patch.TryGetValue("$.prompt_cache_key"u8, out string? promptCacheKey) &&
                    promptCacheKey is not null)
                {
                    clone.Patch.Set("$.prompt_cache_key"u8, promptCacheKey);
                }

                if (options.Patch.TryGetValue("$.text.verbosity"u8, out string? textVerbosity) &&
                    textVerbosity is not null)
                {
                    clone.Patch.Set("$.text.verbosity"u8, textVerbosity);
                }

                if (options.Patch.TryGetValue("$.client_metadata.x-codex-installation-id"u8, out string? installationId) &&
                    installationId is not null)
                {
                    clone.Patch.Set("$.client_metadata.x-codex-installation-id"u8, installationId);
                }
            }

            return clone;
        }
    }

    private sealed class RecordingOpenAIResponsesWebSocketSession(
        IReadOnlyList<IReadOnlyList<StreamingResponseUpdate>> responseBatches,
        IReadOnlyList<OpenAIResponsesWebSocketSideChannelEvent>? sideChannelEvents = null)
        : IOpenAIResponsesWebSocketSession
    {
        public bool HasOpenConnection => true;

        public Action<OpenAIResponsesWebSocketSideChannelEvent>? SideChannelReceived { get; set; }

        public int RequestCount { get; private set; }

        public int DisposeCount { get; private set; }

        public List<WebSocketRequestRecord> Requests { get; } = [];

        public AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CreateResponseOptions? reconnectOptions = null,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            RequestCount++;
            Requests.Add(new WebSocketRequestRecord(
                options.PreviousResponseId,
                options.InputItems.Count,
                reconnectOptions?.PreviousResponseId,
                reconnectOptions?.InputItems.Count ?? 0));
            foreach (var sideChannelEvent in sideChannelEvents ?? [])
            {
                SideChannelReceived?.Invoke(sideChannelEvent);
            }

            var requestIndex = RequestCount - 1;
            var updates = requestIndex < responseBatches.Count
                ? responseBatches[requestIndex]
                : [];
            return new TestAsyncCollectionResult<StreamingResponseUpdate>(updates);
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class ContextRecordingOpenAIResponsesWebSocketSession(
        IReadOnlyList<IReadOnlyList<StreamingResponseUpdate>> responseBatches) : IOpenAIResponsesWebSocketSession
    {
        public bool HasOpenConnection => true;

        public Action<OpenAIResponsesWebSocketSideChannelEvent>? SideChannelReceived { get; set; }

        public List<CodexSubscriptionRequestContext> Contexts { get; } = [];

        public AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CreateResponseOptions? reconnectOptions = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The context-aware protocol path should be used.");

        public async IAsyncEnumerable<CodexProtocolEvent> CreateProtocolEventsAsync(
            CreateResponseOptions options,
            CreateResponseOptions? reconnectOptions,
            CodexSubscriptionRequestContext requestContext,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = options;
            _ = reconnectOptions;
            cancellationToken.ThrowIfCancellationRequested();
            Contexts.Add(requestContext);
            foreach (var update in responseBatches[Contexts.Count - 1])
            {
                yield return new CodexProtocolEvent(
                    CodexProtocolTransport.WebSocket,
                    update.GetType().Name,
                    update,
                    new CodexResponseMetadata());
                await Task.Yield();
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class ProtocolEventOpenAIResponsesWebSocketSession(IReadOnlyList<CodexProtocolEvent> events)
        : IOpenAIResponsesWebSocketSession
    {
        public bool HasOpenConnection => true;

        public Action<OpenAIResponsesWebSocketSideChannelEvent>? SideChannelReceived { get; set; }

        public AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CreateResponseOptions? reconnectOptions = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The protocol event path should be used.");

        public async IAsyncEnumerable<CodexProtocolEvent> CreateProtocolEventsAsync(
            CreateResponseOptions options,
            CreateResponseOptions? reconnectOptions,
            CodexSubscriptionRequestContext requestContext,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = options;
            _ = reconnectOptions;
            _ = requestContext;
            foreach (var protocolEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return protocolEvent;
                await Task.Yield();
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class SecondRequestThrowingOpenAIResponsesWebSocketSession(
        IReadOnlyList<StreamingResponseUpdate> firstResponseUpdates,
        Exception secondRequestException) : IOpenAIResponsesWebSocketSession
    {
        public bool HasOpenConnection => true;

        public Action<OpenAIResponsesWebSocketSideChannelEvent>? SideChannelReceived { get; set; }

        public List<WebSocketRequestRecord> Requests { get; } = [];

        public AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CreateResponseOptions? reconnectOptions = null,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Requests.Add(new WebSocketRequestRecord(
                options.PreviousResponseId,
                options.InputItems.Count,
                reconnectOptions?.PreviousResponseId,
                reconnectOptions?.InputItems.Count ?? 0));
            if (Requests.Count == 1)
            {
                return new TestAsyncCollectionResult<StreamingResponseUpdate>(firstResponseUpdates);
            }

            throw secondRequestException;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FlakyOpenAIResponsesWebSocketSession(
        IReadOnlyList<Exception> failures,
        IReadOnlyList<StreamingResponseUpdate> successUpdates) : IOpenAIResponsesWebSocketSession
    {
        public bool HasOpenConnection => true;

        public Action<OpenAIResponsesWebSocketSideChannelEvent>? SideChannelReceived { get; set; }

        public int RequestCount { get; private set; }

        public AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CreateResponseOptions? reconnectOptions = null,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            _ = reconnectOptions;
            _ = cancellationToken;
            RequestCount++;
            if (RequestCount <= failures.Count)
            {
                throw failures[RequestCount - 1];
            }

            return new TestAsyncCollectionResult<StreamingResponseUpdate>(successUpdates);
        }

        public void Dispose()
        {
        }
    }

    private sealed class PartiallyFailingOpenAIResponsesWebSocketSession(
        StreamingResponseUpdate firstUpdate,
        Exception failure) : IOpenAIResponsesWebSocketSession
    {
        public bool HasOpenConnection => true;

        public Action<OpenAIResponsesWebSocketSideChannelEvent>? SideChannelReceived { get; set; }

        public int RequestCount { get; private set; }

        public int DisposeCount { get; private set; }

        public AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CreateResponseOptions? reconnectOptions = null,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            _ = reconnectOptions;
            _ = cancellationToken;
            RequestCount++;
            return new FailingAsyncCollectionResult<StreamingResponseUpdate>(firstUpdate, failure);
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class ThrowingOpenAIResponsesWebSocketSession(Exception exception) : IOpenAIResponsesWebSocketSession
    {
        public bool HasOpenConnection => true;

        public Action<OpenAIResponsesWebSocketSideChannelEvent>? SideChannelReceived { get; set; }

        public int RequestCount { get; private set; }

        public AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CreateResponseOptions? reconnectOptions = null,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            _ = reconnectOptions;
            _ = cancellationToken;
            RequestCount++;
            throw exception;
        }

        public void Dispose()
        {
        }
    }

    private sealed record WebSocketRequestRecord(
        string? PreviousResponseId,
        int InputItemCount,
        string? ReconnectPreviousResponseId,
        int ReconnectInputItemCount);

    private sealed class ThrowingOpenAIResponseClient(Exception exception)
        : ResponsesClient(new ApiKeyCredential("test-key"))
    {
        public override AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CancellationToken cancellationToken = default)
            => throw exception;
    }

    private sealed class DelayedOpenAIResponseClient(
        Task release,
        IReadOnlyList<StreamingResponseUpdate> updates)
        : ResponsesClient(new ApiKeyCredential("test-key"))
    {
        public override AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            _ = cancellationToken;
            return new DelayedAsyncCollectionResult<StreamingResponseUpdate>(release, updates);
        }
    }

    private sealed class PendingAfterFirstUpdateResponseClient(StreamingResponseUpdate update)
        : ResponsesClient(new ApiKeyCredential("test-key"))
    {
        public int RequestCount { get; private set; }

        public override AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            RequestCount++;
            return new PendingAfterFirstAsyncCollectionResult<StreamingResponseUpdate>(update, cancellationToken);
        }
    }

    private sealed class SequentialPendingAfterFirstUpdateResponseClient(
        IReadOnlyList<StreamingResponseUpdate> updates)
        : ResponsesClient(new ApiKeyCredential("test-key"))
    {
        public int RequestCount { get; private set; }

        public override AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            var index = RequestCount++;
            return new PendingAfterFirstAsyncCollectionResult<StreamingResponseUpdate>(updates[index], cancellationToken);
        }
    }

    private sealed class FlakyOpenAIResponseClient(
        IReadOnlyList<Exception> failures,
        IReadOnlyList<StreamingResponseUpdate> successUpdates)
        : ResponsesClient(new ApiKeyCredential("test-key"))
    {
        public int RequestCount { get; private set; }

        public override AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            if (RequestCount <= failures.Count)
            {
                throw failures[RequestCount - 1];
            }

            return new TestAsyncCollectionResult<StreamingResponseUpdate>(successUpdates);
        }
    }

    private sealed class PartiallyFailingThenSuccessOpenAIResponseClient(
        StreamingResponseUpdate firstUpdate,
        Exception failure,
        IReadOnlyList<StreamingResponseUpdate> successUpdates)
        : ResponsesClient(new ApiKeyCredential("test-key"))
    {
        public int RequestCount { get; private set; }

        public override AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return RequestCount == 1
                ? new FailingAsyncCollectionResult<StreamingResponseUpdate>(firstUpdate, failure)
                : new TestAsyncCollectionResult<StreamingResponseUpdate>(successUpdates);
        }
    }

    private sealed class RecordingOpenAIChatClient : ChatClient
    {
        private readonly IReadOnlyList<IReadOnlyList<StreamingChatCompletionUpdate>> _responseBatches;

        public RecordingOpenAIChatClient(IReadOnlyList<StreamingChatCompletionUpdate> updates)
            : this([updates])
        {
        }

        private RecordingOpenAIChatClient(IReadOnlyList<IReadOnlyList<StreamingChatCompletionUpdate>> responseBatches)
            : base("test-model", new ApiKeyCredential("test-key"), new OpenAIClientOptions())
        {
            _responseBatches = responseBatches;
        }

        public List<ChatRequestRecord> Requests { get; } = [];

        public static RecordingOpenAIChatClient ForBatches(
            IReadOnlyList<IReadOnlyList<StreamingChatCompletionUpdate>> responseBatches)
            => new(responseBatches);

        public override AsyncCollectionResult<StreamingChatCompletionUpdate> CompleteChatStreamingAsync(
            IEnumerable<ChatMessage> messages,
            ChatCompletionOptions options = default!,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new ChatRequestRecord(messages.ToArray(), CloneOptions(options)));
            var requestIndex = Requests.Count - 1;
            var updates = requestIndex < _responseBatches.Count
                ? _responseBatches[requestIndex]
                : [];
            return new TestAsyncCollectionResult<StreamingChatCompletionUpdate>(updates);
        }

        private static ChatCompletionOptions? CloneOptions(ChatCompletionOptions? options)
        {
            if (options is null)
            {
                return null;
            }

            var clone = new ChatCompletionOptions
            {
                AllowParallelToolCalls = options.AllowParallelToolCalls,
                StoredOutputEnabled = options.StoredOutputEnabled,
                ToolChoice = options.ToolChoice,
                ReasoningEffortLevel = options.ReasoningEffortLevel,
                MaxOutputTokenCount = options.MaxOutputTokenCount,
            };
            foreach (var tool in options.Tools)
            {
                clone.Tools.Add(tool);
            }

            if (options.Patch.TryGetValue("$.reasoning_split"u8, out bool reasoningSplit))
            {
                clone.Patch.Set("$.reasoning_split"u8, reasoningSplit);
            }

            if (options.Patch.TryGetValue("$.max_tokens"u8, out int maxTokens))
            {
                clone.Patch.Set("$.max_tokens"u8, maxTokens);
            }

            return clone;
        }
    }

    private sealed record ResponseRequestRecord(
        IReadOnlyList<ResponseItem> InputItems,
        CreateResponseOptions Options,
        string SerializedOptions);

    private sealed record ChatRequestRecord(
        IReadOnlyList<ChatMessage> Messages,
        ChatCompletionOptions? Options);

    private static readonly MethodInfo DeserializeStreamingResponseUpdateMethod =
        typeof(StreamingResponseUpdate).GetMethod(
            "DeserializeStreamingResponseUpdate",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The OpenAI package no longer exposes the expected response-stream deserializer.");

    private static readonly MethodInfo DeserializeStreamingChatCompletionUpdateMethod =
        typeof(StreamingChatCompletionUpdate).GetMethod(
            "DeserializeStreamingChatCompletionUpdate",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The OpenAI package no longer exposes the expected chat-stream deserializer.");

    private sealed class TestAsyncCollectionResult<T>(IReadOnlyList<T> values) : AsyncCollectionResult<T>
    {
        public override async IAsyncEnumerable<ClientResult> GetRawPagesAsync()
        {
            yield return ClientResult.FromResponse(new TestPipelineResponse());
            await Task.CompletedTask.ConfigureAwait(false);
        }

        protected override async IAsyncEnumerable<T> GetValuesFromPageAsync(ClientResult page)
        {
            foreach (var value in values)
            {
                yield return value;
                await Task.Yield();
            }
        }

        public override ContinuationToken GetContinuationToken(ClientResult page) => default!;
    }

    private sealed class FailingAsyncCollectionResult<T>(T firstValue, Exception failure) : AsyncCollectionResult<T>
    {
        public override async IAsyncEnumerable<ClientResult> GetRawPagesAsync()
        {
            yield return ClientResult.FromResponse(new TestPipelineResponse());
            await Task.CompletedTask.ConfigureAwait(false);
        }

        protected override async IAsyncEnumerable<T> GetValuesFromPageAsync(ClientResult page)
        {
            yield return firstValue;
            await Task.Yield();
            throw failure;
        }

        public override ContinuationToken GetContinuationToken(ClientResult page) => default!;
    }

    private sealed class DelayedAsyncCollectionResult<T>(Task release, IReadOnlyList<T> values) : AsyncCollectionResult<T>
    {
        public override async IAsyncEnumerable<ClientResult> GetRawPagesAsync()
        {
            yield return ClientResult.FromResponse(new TestPipelineResponse());
            await Task.CompletedTask.ConfigureAwait(false);
        }

        protected override async IAsyncEnumerable<T> GetValuesFromPageAsync(ClientResult page)
        {
            await release.ConfigureAwait(false);
            foreach (var value in values)
            {
                yield return value;
                await Task.Yield();
            }
        }

        public override ContinuationToken GetContinuationToken(ClientResult page) => default!;
    }

    private sealed class PendingAfterFirstAsyncCollectionResult<T>(T firstValue, CancellationToken cancellationToken)
        : AsyncCollectionResult<T>
    {
        public override async IAsyncEnumerable<ClientResult> GetRawPagesAsync()
        {
            yield return ClientResult.FromResponse(new TestPipelineResponse());
            await Task.CompletedTask.ConfigureAwait(false);
        }

        protected override async IAsyncEnumerable<T> GetValuesFromPageAsync(ClientResult page)
        {
            yield return firstValue;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        public override ContinuationToken GetContinuationToken(ClientResult page) => default!;
    }

    private sealed class TestPipelineResponse(
        string content = "{}",
        int status = 200,
        string reasonPhrase = "OK") : PipelineResponse
    {
        private readonly PipelineResponseHeaders _headers = new TestPipelineResponseHeaders();
        private readonly BinaryData _content = BinaryData.FromString(content);

        public override int Status => status;

        public override string ReasonPhrase => reasonPhrase;

        protected override PipelineResponseHeaders HeadersCore => _headers;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => _content;

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => _content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_content);

        public override void Dispose()
        {
        }
    }

    private sealed class TestPipelineResponseHeaders : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            yield break;
        }

        public override bool TryGetValue(string name, out string? value)
        {
            value = null;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }

    private sealed class StaticHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri ?? throw new InvalidOperationException("Request URI was not set."));
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return response;
        }
    }

    private sealed class TestTempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TestTempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "openai-raw-api-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestTempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
