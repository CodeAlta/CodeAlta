#pragma warning disable OPENAI001

using System.ClientModel;
#pragma warning disable SCME0001
using System.ClientModel.Primitives;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.OpenAI.CodexSubscription;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace CodeAlta.Tests;

[TestClass]
public sealed class OpenAIRawApiAgentBackendTests
{
    [TestMethod]
    public async Task OpenAIResponsesAgentBackend_UsesLocalReplayAndDoesNotSetPreviousResponseId()
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

        await using var backend = new OpenAIResponsesAgentBackend(new OpenAIResponsesAgentBackendOptions
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
                            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["inputTokenLimit"] = 200000L,
                            }),
                    ]),
                },
            },
        });

        var models = await backend.ListModelsAsync().ConfigureAwait(false);
        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("gpt-test", models[0].Id);

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gpt-test",
            WorkingDirectory = temp.Path,
            SystemMessage = "System instructions",
            DeveloperInstructions = "Developer instructions",
            ReasoningEffort = AgentReasoningEffort.High,
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
        Assert.AreEqual(ResponseReasoningEffortLevel.High, responsesClient.Requests[0].Options.ReasoningOptions!.ReasoningEffortLevel);
        Assert.AreEqual(ResponseReasoningSummaryVerbosity.Detailed, responsesClient.Requests[0].Options.ReasoningOptions.ReasoningSummaryVerbosity);
        Assert.IsTrue(
            responsesClient.Requests[1].InputItems.OfType<FunctionCallOutputResponseItem>()
                .Any(static item => item.CallId == "call-1" && item.FunctionOutput == "README contents"));

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.ToolOutput && e.Content == "README contents"));
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Reasoning && e.Content == "Looked at the file."));
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Assistant && e.Content == "Inspection complete."));
        var usageEvent = history.OfType<AgentSessionUpdateEvent>().Single(static e => e.Kind == AgentSessionUpdateKind.UsageUpdated);
        Assert.IsNotNull(usageEvent.Usage);
        Assert.AreEqual(33L, usageEvent.Usage.CurrentTokens);
        Assert.AreEqual(200000L, usageEvent.Usage.TokenLimit);
        Assert.AreEqual(4, usageEvent.Usage.MessageCount);

        var metadata = (await backend.ListSessionsAsync().ConfigureAwait(false)).Single();
        var details = Assert.IsInstanceOfType<RawApiSessionMetadataDetails>(metadata.Details);
        Assert.AreEqual("response-2", details.ProviderSessionId);
    }

    [TestMethod]
    public async Task OpenAIChatAgentBackend_MergesDeveloperInstructionsWhenProfileDisablesDeveloperRole()
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

        await using var backend = new OpenAIChatAgentBackend(new OpenAIChatAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "compat-provider",
                    IsDefault = true,
                    Profile = new LocalAgentProviderProfile
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

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
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
        Assert.AreEqual(18L, usageEvent.Usage.CurrentTokens);
        Assert.AreEqual(128000L, usageEvent.Usage.TokenLimit);
        Assert.AreEqual(2, usageEvent.Usage.MessageCount);

        var metadata = (await backend.ListSessionsAsync().ConfigureAwait(false)).Single();
        var details = Assert.IsInstanceOfType<RawApiSessionMetadataDetails>(metadata.Details);
        Assert.AreEqual("chatcmpl-1", details.ProviderSessionId);
    }

    [TestMethod]
    public async Task OpenAIChatAgentBackend_MapsRefusalUpdatesToAssistantContent()
    {
        using var temp = TestTempDirectory.Create();
        var chatClient = new RecordingOpenAIChatClient(
        [
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                completionId: "chatcmpl-refusal",
                refusalUpdate: "I can't help with that request.",
                model: "gpt-chat-test"),
        ]);

        await using var backend = new OpenAIChatAgentBackend(new OpenAIChatAgentBackendOptions
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

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
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
    public async Task OpenAIChatAgentBackend_MapsReasoningDeltasFromPatch()
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

        await using var backend = new OpenAIChatAgentBackend(new OpenAIChatAgentBackendOptions
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

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
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
    public async Task OpenAIChatAgentBackend_ReplaysConfiguredReasoningInputFieldForToolCalls()
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

        await using var backend = new OpenAIChatAgentBackend(new OpenAIChatAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "deepseek",
                    IsDefault = true,
                    Profile = new LocalAgentProviderProfile
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

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
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
    }

    [TestMethod]
    public async Task OpenAIChatAgentBackend_AppliesExtraBodyAndParsesCumulativeReasoningDetails()
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

        await using var backend = new OpenAIChatAgentBackend(new OpenAIChatAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "minimax",
                    IsDefault = true,
                    Profile = new LocalAgentProviderProfile
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

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
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

        var text = response.AssistantMessage.Parts.OfType<LocalAgentMessagePart.Text>().Single().Value;
        Assert.AreEqual("Recovered answer.", text);
        Assert.AreEqual("response-recovered", response.ProviderSessionId);
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
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.User,
                    [
                        new LocalAgentMessagePart.Text("Describe this image."),
                        new LocalAgentMessagePart.Data(Convert.ToBase64String(imageBytes), "image/png", "image.png"),
                    ]),
            ],
        };

        var response = await executor.ExecuteTurnAsync(
            request,
            static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var text = response.AssistantMessage.Parts.OfType<LocalAgentMessagePart.Text>().Single().Value;
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

        var toolCall = response.AssistantMessage.Parts.OfType<LocalAgentMessagePart.ToolCall>().Single();
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

        var exception = await Assert.ThrowsExactlyAsync<LocalAgentTurnExecutionException>(
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
                    inputTokens: 33,
                    outputTokens: 7),
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

        Assert.IsNotNull(response.Usage);
        Assert.AreEqual(33L, response.Usage.LastOperation?.InputTokens);
        Assert.AreEqual(7L, response.Usage.LastOperation?.OutputTokens);
        Assert.AreEqual(40L, response.Usage.CurrentTokens);
        Assert.AreEqual(200000L, response.Usage.TokenLimit);
        Assert.AreEqual(AgentUsageScope.CurrentWindow, response.Usage.Scope);
    }

    [TestMethod]
    public async Task OpenAIResponsesAgentBackend_AppliesConfiguredExtraBody()
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

        await using var backend = new OpenAIResponsesAgentBackend(new OpenAIResponsesAgentBackendOptions
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

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
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
            ProviderKey = "codex_subscription",
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
                ProtocolFamily = "openai-codex-subscription",
                ProviderKey = "codex_subscription",
                DisplayName = "Codex (ChatGPT subscription)",
                Profile = new LocalAgentProviderProfile
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
        Assert.IsTrue(document.RootElement.GetProperty("include").EnumerateArray().Any(
            static item => item.GetString() == "reasoning.encrypted_content"));
    }

    [TestMethod]
    public async Task OpenAIResponsesAgentBackend_CodexModelDiscoveryHttpFailureUsesStaticFallback()
    {
        using var temp = TestTempDirectory.Create();
        var credentialStore = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await credentialStore.SaveAsync(
            "codex_subscription",
            new OpenAICodexSubscriptionCredential
            {
                AccessToken = "access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }).ConfigureAwait(false);
        var handler = new StaticHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"detail":"model discovery is temporarily unavailable"}"""),
        });

        await using var backend = new OpenAIResponsesAgentBackend(new OpenAIResponsesAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "codex_subscription",
                    IsDefault = true,
                    BaseUri = new Uri("https://chatgpt.com/backend-api/codex"),
                    SingleModelId = "gpt-5.5",
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

        var models = await backend.ListModelsAsync().ConfigureAwait(false);

        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("gpt-5.5", models[0].Id);
        Assert.AreEqual("codex-static-fallback", models[0].Capabilities?["source"]);
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
            ProviderKey = "codex_subscription",
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
            ProviderKey = "codex_subscription",
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
                ProtocolFamily = "openai-codex-subscription",
                ProviderKey = "codex_subscription",
                DisplayName = "Codex (ChatGPT subscription)",
                Profile = new LocalAgentProviderProfile
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
        Assert.IsFalse(document.RootElement.TryGetProperty("client_metadata", out _));
    }

    [TestMethod]
    public async Task OpenAIResponsesAgentBackend_CodexUsesLocalToolBridgeSequentially()
    {
        using var temp = TestTempDirectory.Create();
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateCreatedResponseUpdate(
                    responseId: "response-tools",
                    modelId: "gpt-5.3-codex"),
                CreateOutputItemDoneUpdate(
                    outputIndex: 0,
                    item: ResponseItem.CreateFunctionCallItem(
                        "call-one",
                        "inspect_one",
                        BinaryData.FromString("""{"path":"README.md"}"""))),
                CreateOutputItemDoneUpdate(
                    outputIndex: 1,
                    item: ResponseItem.CreateFunctionCallItem(
                        "call-two",
                        "inspect_two",
                        BinaryData.FromString("""{"path":"src/CodeAlta.csproj"}"""))),
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

        await using var backend = new OpenAIResponsesAgentBackend(new OpenAIResponsesAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "codex_subscription",
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

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
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
    public async Task OpenAIResponsesAgentBackend_CodexSessionResumeReplaysLocalHistory()
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

        await using var backend = new OpenAIResponsesAgentBackend(new OpenAIResponsesAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "codex_subscription",
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
        await using (var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
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

        await using (var resumed = await backend.ResumeSessionAsync(
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
    public async Task OpenAIResponsesAgentBackend_CodexLiveSessionUsesPreviousResponseId()
    {
        using var temp = TestTempDirectory.Create();
        var responsesClient = new RecordingOpenAIResponseClient(
        [
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-live-backend-1",
                    modelId: "gpt-5.3-codex",
                    text: "First live answer."),
            ],
            [
                CreateTextOnlyAssistantResponseUpdate(
                    responseId: "response-live-backend-2",
                    modelId: "gpt-5.3-codex",
                    text: "Second live answer."),
            ],
        ]);

        await using var backend = new OpenAIResponsesAgentBackend(new OpenAIResponsesAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "codex_subscription",
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

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
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
        Assert.AreEqual("response-live-backend-1", responsesClient.Requests[1].Options.PreviousResponseId);
        Assert.AreEqual(1, responsesClient.Requests[1].InputItems.Count);
        StringAssert.Contains(responsesClient.Requests[1].SerializedOptions, "Second live prompt");
        Assert.IsFalse(responsesClient.Requests[1].SerializedOptions.Contains("First live prompt", StringComparison.Ordinal));
        Assert.IsFalse(responsesClient.Requests[1].SerializedOptions.Contains("First live answer.", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_CodexDefaultsToWebSocketAndFallsBackToHttpBeforeStreaming()
    {
        var webSocketSession = new ThrowingOpenAIResponsesWebSocketSession(
            new HttpRequestException("WebSocket unavailable."));
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
            ProviderKey = "codex_subscription",
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

        Assert.AreEqual(1, webSocketSession.RequestCount);
        Assert.AreEqual(2, responsesClient.Requests.Count);
        Assert.AreEqual("Fallback answer.", response.AssistantMessage.Parts.OfType<LocalAgentMessagePart.Text>().Single().Value);
        Assert.AreEqual("Sticky fallback answer.", secondResponse.AssistantMessage.Parts.OfType<LocalAgentMessagePart.Text>().Single().Value);
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
            ProviderKey = "codex_subscription",
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
            ProviderKey = "codex_subscription",
            ResponsesClientFactory = _ => responsesClient,
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });

        var exception = await Assert.ThrowsExactlyAsync<LocalAgentTurnExecutionException>(
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
            ProviderKey = "codex_subscription",
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
        Assert.AreEqual("Retried over WebSocket.", response.AssistantMessage.Parts.OfType<LocalAgentMessagePart.Text>().Single().Value);
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
            ProviderKey = "codex_subscription",
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
        await ((ILocalAgentProviderSessionCleanup)executor).DisposeProviderSessionAsync("session-1").ConfigureAwait(false);

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
            ProviderKey = "codex_subscription",
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
            ProviderKey = "codex_subscription",
            ResponsesClientFactory = _ => new RecordingOpenAIResponseClient([]),
            ResponsesWebSocketSessionFactory = _ => ValueTask.FromResult<IOpenAIResponsesWebSocketSession>(webSocketSession),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
            },
        });
        var firstUserMessage = new LocalAgentConversationMessage(
            LocalAgentConversationRole.User,
            [new LocalAgentMessagePart.Text("First prompt")]);
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
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.User,
                    [new LocalAgentMessagePart.Text("Second prompt")]),
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
    public async Task OpenAIResponsesTurnExecutor_CodexUsesPreviousResponseIdOnlyForLiveSessionContinuation()
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
            ProviderKey = "codex_subscription",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });
        var firstUserMessage = new LocalAgentConversationMessage(
            LocalAgentConversationRole.User,
            [new LocalAgentMessagePart.Text("First prompt")]);
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
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.User,
                    [new LocalAgentMessagePart.Text("Second prompt")]),
            ],
        };

        _ = await executor.ExecuteTurnAsync(
                secondRequest,
                static (_, _) => ValueTask.CompletedTask)
            .ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.Requests.Count);
        Assert.IsNull(responsesClient.Requests[0].Options.PreviousResponseId);
        Assert.AreEqual("response-live-1", responsesClient.Requests[1].Options.PreviousResponseId);
        Assert.AreEqual(1, responsesClient.Requests[1].InputItems.Count);
        StringAssert.Contains(responsesClient.Requests[1].SerializedOptions, "Second prompt");
        Assert.IsFalse(responsesClient.Requests[1].SerializedOptions.Contains("First prompt", StringComparison.Ordinal));
        Assert.IsFalse(responsesClient.Requests[1].SerializedOptions.Contains("First answer.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodexSubscriptionDiagnostics_EmitOnlySanitizedRequestShape()
    {
        var provider = new OpenAIProviderOptions
        {
            ProviderKey = "codex_subscription",
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
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.User,
                    [new LocalAgentMessagePart.Text("do not log this prompt")]),
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

        StringAssert.Contains(message, "provider=codex_subscription");
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
    public async Task OpenAIResponsesAgentBackend_CodexTokensAreNotStoredInSessionHistory()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileOpenAICodexSubscriptionCredentialStore(temp.Path);
        await store.SaveAsync(
                "codex_subscription",
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

        await using var backend = new OpenAIResponsesAgentBackend(new OpenAIResponsesAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "codex_subscription",
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

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
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
            ProviderKey = "codex_subscription",
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
                ProtocolFamily = "openai-codex-subscription",
                ProviderKey = "codex_subscription",
                DisplayName = "Codex (ChatGPT subscription)",
            },
            ModelId = "gpt-5.3-codex",
        };

        var exception = await Assert.ThrowsExactlyAsync<LocalAgentTurnExecutionException>(
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
            ProviderKey = "codex_subscription",
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
                ProtocolFamily = "openai-codex-subscription",
                ProviderKey = "codex_subscription",
                DisplayName = "Codex (ChatGPT subscription)",
            },
            ModelId = "gpt-5.3-codex",
        };

        var exception = await Assert.ThrowsExactlyAsync<LocalAgentTurnExecutionException>(
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
                ProviderKey = "codex_subscription",
                ResponsesClientFactory = _ => new ThrowingOpenAIResponseClient(exceptionToThrow),
                CodexSubscription = new OpenAICodexSubscriptionOptions
                {
                    Experimental = true,
                    ResponseTransport = "http",
                },
            });

            var exception = await Assert.ThrowsExactlyAsync<LocalAgentTurnExecutionException>(
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
            ProviderKey = "codex_subscription",
            ResponsesClientFactory = _ => new RecordingOpenAIResponseClient([[]]),
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        var exception = await Assert.ThrowsExactlyAsync<LocalAgentTurnExecutionException>(
                () => executor.ExecuteTurnAsync(
                    CreateCodexTurnRequest(),
                    static (_, _) => ValueTask.CompletedTask))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Failure.Message, "expected protocol");
        StringAssert.Contains(exception.Failure.Message, "terminal response payload");
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
            ProviderKey = "codex_subscription",
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
        var text = response.AssistantMessage.Parts.OfType<LocalAgentMessagePart.Text>().Single().Value;
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
            ProviderKey = "codex_subscription",
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

        var exception = await Assert.ThrowsExactlyAsync<LocalAgentTurnExecutionException>(
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
            ProviderKey = "codex_subscription",
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
        var text = response.AssistantMessage.Parts.OfType<LocalAgentMessagePart.Text>().Single().Value;
        Assert.AreEqual("Retried answer.", text);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_DoesNotRetryCodexBadRequest()
    {
        var responsesClient = new FlakyOpenAIResponseClient(
            [new HttpRequestException("Bad request.", null, HttpStatusCode.BadRequest)],
            []);
        var executor = new OpenAIResponsesTurnExecutor(new OpenAIProviderOptions
        {
            ProviderKey = "codex_subscription",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });

        _ = await Assert.ThrowsExactlyAsync<LocalAgentTurnExecutionException>(
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
            ProviderKey = "codex_subscription",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });
        var deltas = new List<LocalAgentTurnDelta>();

        var response = await executor.ExecuteTurnAsync(
            CreateCodexTurnRequest(),
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.RequestCount);
        Assert.AreEqual(0, deltas.Count);
        var text = response.AssistantMessage.Parts.OfType<LocalAgentMessagePart.Text>().Single().Value;
        Assert.AreEqual("Retried answer.", text);
    }

    [TestMethod]
    public async Task OpenAIResponsesTurnExecutor_RetriesCodexPrematureEndAfterVisibleOutput()
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
            ProviderKey = "codex_subscription",
            ResponsesClientFactory = _ => responsesClient,
            CodexSubscription = new OpenAICodexSubscriptionOptions
            {
                Experimental = true,
                ResponseTransport = "http",
            },
        });
        var deltas = new List<LocalAgentTurnDelta>();

        var response = await executor.ExecuteTurnAsync(
                CreateCodexTurnRequest(),
                (delta, _) =>
                {
                    deltas.Add(delta);
                    return ValueTask.CompletedTask;
                })
            .ConfigureAwait(false);

        Assert.AreEqual(2, responsesClient.RequestCount);
        Assert.AreEqual(1, deltas.Count);
        var text = response.AssistantMessage.Parts.OfType<LocalAgentMessagePart.Text>().Single().Value;
        Assert.AreEqual("Retried answer.", text);
    }

    private static StreamingResponseUpdate CreateAssistantResponseUpdate(
        string responseId,
        string modelId,
        string text,
        string reasoningText,
        string? encryptedReasoning,
        int inputTokens = 0,
        int outputTokens = 0)
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
              "response": {{SerializeResponseWithUsage(response, inputTokens, outputTokens)}}
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
        => DeserializeStreamingResponseUpdate(
            $$"""
              {
                "type": "response.output_text.delta",
                "sequence_number": 1,
                "item_id": "{{itemId}}",
                "output_index": 0,
                "content_index": 0,
                "delta": "{{delta}}"
              }
              """);

    private static HttpIOException CreatePrematureResponseEndException()
        => new(
            HttpRequestError.ResponseEnded,
            "The response ended prematurely. (ResponseEnded)",
            innerException: null!);

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

    private static string SerializeResponseWithUsage(ResponseResult response, int inputTokens, int outputTokens)
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
            writer.WriteNumber("total_tokens", inputTokens + outputTokens);
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

    private static LocalAgentTurnRequest CreateTurnRequest()
        => new()
        {
            Provider = new LocalAgentProviderDescriptor
            {
                ProtocolFamily = "openai-responses",
                ProviderKey = "openai",
                DisplayName = "OpenAI Responses",
                BackendId = new AgentBackendId("openai-responses"),
                TransportKind = LocalAgentTransportKind.OpenAIResponses,
            },
            BackendId = new AgentBackendId("openai-responses"),
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
                new LocalAgentConversationMessage(
                    LocalAgentConversationRole.User,
                    [new LocalAgentMessagePart.Text("Hello")]),
            ],
            Tools = [],
            State = new LocalAgentSessionState
            {
                SessionId = "session-1",
                ProtocolFamily = "openai-responses",
                ProviderKey = "openai",
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };

    private static LocalAgentTurnRequest CreateCodexTurnRequest()
        => CreateTurnRequest() with
        {
            Provider = CreateTurnRequest().Provider with
            {
                ProtocolFamily = "openai-codex-subscription",
                ProviderKey = "codex_subscription",
                DisplayName = "Codex (ChatGPT subscription)",
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

    private sealed class RecordingOpenAIResponsesWebSocketSession(IReadOnlyList<IReadOnlyList<StreamingResponseUpdate>> responseBatches) : IOpenAIResponsesWebSocketSession
    {
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

    private sealed class FlakyOpenAIResponsesWebSocketSession(
        IReadOnlyList<Exception> failures,
        IReadOnlyList<StreamingResponseUpdate> successUpdates) : IOpenAIResponsesWebSocketSession
    {
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

    private sealed class ThrowingOpenAIResponsesWebSocketSession(Exception exception) : IOpenAIResponsesWebSocketSession
    {
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
            };
            foreach (var tool in options.Tools)
            {
                clone.Tools.Add(tool);
            }

            if (options.Patch.TryGetValue("$.reasoning_split"u8, out bool reasoningSplit))
            {
                clone.Patch.Set("$.reasoning_split"u8, reasoningSplit);
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

    private sealed class TestPipelineResponse(string content = "{}") : PipelineResponse
    {
        private readonly PipelineResponseHeaders _headers = new TestPipelineResponseHeaders();
        private readonly BinaryData _content = BinaryData.FromString(content);

        public override int Status => 200;

        public override string ReasonPhrase => "OK";

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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri ?? throw new InvalidOperationException("Request URI was not set."));
            return Task.FromResult(response);
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
