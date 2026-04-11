#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.OpenAI;
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

    private sealed class RecordingOpenAIResponseClient(IReadOnlyList<IReadOnlyList<StreamingResponseUpdate>> responseBatches)
        : ResponsesClient(new ApiKeyCredential("test-key"), new OpenAIClientOptions())
    {
        public List<ResponseRequestRecord> Requests { get; } = [];

        public override AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
            CreateResponseOptions options,
            CancellationToken cancellationToken = default)
        {
            var clonedOptions = CloneOptions(options);
            Requests.Add(new ResponseRequestRecord(clonedOptions.InputItems.ToArray(), clonedOptions));
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
            }

            return clone;
        }
    }

    private sealed class RecordingOpenAIChatClient(IReadOnlyList<StreamingChatCompletionUpdate> updates)
        : ChatClient("test-model", new ApiKeyCredential("test-key"), new OpenAIClientOptions())
    {
        public List<ChatRequestRecord> Requests { get; } = [];

        public override AsyncCollectionResult<StreamingChatCompletionUpdate> CompleteChatStreamingAsync(
            IEnumerable<ChatMessage> messages,
            ChatCompletionOptions options = default!,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new ChatRequestRecord(messages.ToArray(), CloneOptions(options)));
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

            return clone;
        }
    }

    private sealed record ResponseRequestRecord(
        IReadOnlyList<ResponseItem> InputItems,
        CreateResponseOptions Options);

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

    private sealed class TestPipelineResponse : PipelineResponse
    {
        private readonly PipelineResponseHeaders _headers = new TestPipelineResponseHeaders();
        private BinaryData _content = BinaryData.FromString("{}");

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
