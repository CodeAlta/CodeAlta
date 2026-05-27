using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using Microsoft.Extensions.AI;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ChatClientTurnExecutorTests
{
    [TestMethod]
    public async Task ExecuteTurnAsync_MapsStreamingResponseToLocalAssistantMessage()
    {
        var client = new RecordingChatClient
        {
            Updates =
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("plan carefully")])
                {
                    MessageId = "message-1",
                    ResponseId = "response-1",
                    ConversationId = "conversation-1",
                    ModelId = "claude-test",
                },
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")])
                {
                    MessageId = "message-1",
                    ResponseId = "response-1",
                    ConversationId = "conversation-1",
                    ModelId = "claude-test",
                },
                new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("tool-1", "read_file", new Dictionary<string, object?> { ["path"] = "readme.md" })])
                {
                    MessageId = "message-1",
                    ResponseId = "response-1",
                    ConversationId = "conversation-1",
                    ModelId = "claude-test",
                },
                new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails
                {
                    InputTokenCount = 12,
                    OutputTokenCount = 8,
                    CachedInputTokenCount = 3,
                    ReasoningTokenCount = 2,
                    TotalTokenCount = 20,
                })])
                {
                    MessageId = "message-1",
                    ResponseId = "response-1",
                    ConversationId = "conversation-1",
                    ModelId = "claude-test",
                },
            ],
        };
        var executor = new ChatClientTurnExecutor(
            (_, _) => ValueTask.FromResult<IChatClient>(client),
            static (_, _) => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]));
        var deltas = new List<AgentTurnDelta>();

        var response = await executor.ExecuteTurnAsync(
                CreateTurnRequest(),
                (delta, _) =>
                {
                    deltas.Add(delta);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual("conversation-1", response.ProviderSessionId);
        Assert.AreEqual("Done.", Assert.IsInstanceOfType<AgentMessagePart.Text>(response.AssistantMessage.Parts[1]).Value);
        var reasoning = Assert.IsInstanceOfType<AgentMessagePart.Reasoning>(response.AssistantMessage.Parts[0]);
        Assert.AreEqual("plan carefully", reasoning.Value);
        var toolCall = Assert.IsInstanceOfType<AgentMessagePart.ToolCall>(response.AssistantMessage.Parts[2]);
        Assert.AreEqual("tool-1", toolCall.CallId);
        Assert.AreEqual("read_file", toolCall.Name);
        Assert.AreEqual(2, deltas.Count);
        Assert.AreEqual(AgentContentKind.Reasoning, deltas[0].Kind);
        Assert.AreEqual("plan carefully", deltas[0].Text);
        Assert.AreEqual(AgentContentKind.Assistant, deltas[1].Kind);
        Assert.AreEqual("Done.", deltas[1].Text);
        CollectionAssert.AreEqual(
            new string?[] { "message-1", "message-1", null },
            response.AssistantPartContentIds?.ToArray());
        Assert.IsNotNull(response.Usage);
        Assert.AreEqual(12L, response.Usage.LastOperation?.InputTokens);
        Assert.AreEqual(8L, response.Usage.LastOperation?.OutputTokens);
        Assert.AreEqual(20L, response.Usage.CurrentTokens);
        Assert.AreEqual(200000L, response.Usage.TokenLimit);
        Assert.AreEqual(AgentUsageScope.CurrentWindow, response.Usage.Scope);
        Assert.AreEqual(AgentUsageSource.ProviderUsage, response.Usage.Source);
        Assert.IsNotNull(response.ProviderState);
        Assert.AreEqual("response-1", response.ProviderState.Value.GetProperty("responseId").GetString());
        Assert.AreEqual("conversation-1", response.ProviderState.Value.GetProperty("conversationId").GetString());
    }

    [TestMethod]
    public async Task ExecuteTurnAsync_MapsConversationAndOptionsIntoChatClientRequest()
    {
        var client = new RecordingChatClient
        {
            Updates =
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])
                {
                    MessageId = "message-2",
                    ResponseId = "response-2",
                },
            ],
        };
        var executor = new ChatClientTurnExecutor(
            (_, _) => ValueTask.FromResult<IChatClient>(client),
            static (_, _) => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]));
        var request = CreateTurnRequest(
            conversation:
            [
                new AgentConversationMessage(
                    AgentConversationRole.System,
                    [new AgentMessagePart.Text("compacted summary")]),
                new AgentConversationMessage(
                    AgentConversationRole.User,
                    [new AgentMessagePart.Text("inspect the file")]),
                new AgentConversationMessage(
                    AgentConversationRole.Tool,
                    [new AgentMessagePart.ToolResult(
                        "tool-1",
                        new AgentToolResult(true, [new AgentToolResultItem.Text("file contents")]))]),
            ]);

        _ = await executor.ExecuteTurnAsync(
                request,
                static (_, _) => ValueTask.CompletedTask,
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(client.LastMessages);
        Assert.AreEqual(3, client.LastMessages.Count);
        Assert.AreEqual(ChatRole.System, client.LastMessages[0].Role);
        Assert.AreEqual("compacted summary", client.LastMessages[0].Text);
        Assert.AreEqual(ChatRole.User, client.LastMessages[1].Role);
        Assert.AreEqual("inspect the file", client.LastMessages[1].Text);
        Assert.AreEqual(ChatRole.Tool, client.LastMessages[2].Role);
        var functionResult = Assert.IsInstanceOfType<FunctionResultContent>(client.LastMessages[2].Contents.Single());
        Assert.AreEqual("tool-1", functionResult.CallId);
        Assert.AreEqual("file contents", functionResult.Result);
        Assert.IsNotNull(client.LastOptions);
        Assert.AreEqual("claude-test", client.LastOptions.ModelId);
        Assert.AreEqual(ChatToolMode.Auto, client.LastOptions.ToolMode);
        Assert.AreEqual(1, client.LastOptions.Tools?.Count);
        StringAssert.Contains(client.LastOptions.Instructions, "System instructions");
        StringAssert.Contains(client.LastOptions.Instructions, "<developer_instructions>");
        Assert.IsNotNull(client.LastOptions.Reasoning);
    }

    [TestMethod]
    public async Task ExecuteTurnAsync_MapsInlineImageDataWithMediaTypeIntoChatClientRequest()
    {
        var imageBytes = new byte[] { 1, 2, 3 };
        var client = new RecordingChatClient
        {
            Updates =
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])
                {
                    MessageId = "message-3",
                    ResponseId = "response-3",
                },
            ],
        };
        var executor = new ChatClientTurnExecutor(
            (_, _) => ValueTask.FromResult<IChatClient>(client),
            static (_, _) => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]));
        var request = CreateTurnRequest(
            conversation:
            [
                new AgentConversationMessage(
                    AgentConversationRole.User,
                    [
                        new AgentMessagePart.Text("inspect the image"),
                        new AgentMessagePart.Data(Convert.ToBase64String(imageBytes), " image/png ", "image.png"),
                    ]),
            ]);

        _ = await executor.ExecuteTurnAsync(
                request,
                static (_, _) => ValueTask.CompletedTask,
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(client.LastMessages);
        Assert.AreEqual(ChatRole.User, client.LastMessages[0].Role);
        Assert.AreEqual(2, client.LastMessages[0].Contents.Count);
        var dataContent = Assert.IsInstanceOfType<DataContent>(client.LastMessages[0].Contents[1]);
        Assert.AreEqual("image/png", dataContent.MediaType);
        Assert.AreEqual("image.png", dataContent.Name);
        CollectionAssert.AreEqual(imageBytes, dataContent.Data.ToArray());
    }

    [TestMethod]
    public async Task ExecuteTurnAsync_InfersBlankInlineImageMediaTypeFromName()
    {
        var imageBytes = new byte[] { 4, 5, 6 };
        var client = new RecordingChatClient
        {
            Updates =
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])
                {
                    MessageId = "message-4",
                    ResponseId = "response-4",
                },
            ],
        };
        var executor = new ChatClientTurnExecutor(
            (_, _) => ValueTask.FromResult<IChatClient>(client),
            static (_, _) => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]));
        var request = CreateTurnRequest(
            conversation:
            [
                new AgentConversationMessage(
                    AgentConversationRole.User,
                    [new AgentMessagePart.Data(Convert.ToBase64String(imageBytes), " ", "photo.jpeg")]),
            ]);

        _ = await executor.ExecuteTurnAsync(
                request,
                static (_, _) => ValueTask.CompletedTask,
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(client.LastMessages);
        var dataContent = Assert.IsInstanceOfType<DataContent>(client.LastMessages[0].Contents.Single());
        Assert.AreEqual("image/jpeg", dataContent.MediaType);
        CollectionAssert.AreEqual(imageBytes, dataContent.Data.ToArray());
    }

    private static AgentTurnRequest CreateTurnRequest(IReadOnlyList<AgentConversationMessage>? conversation = null)
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = "anthropic-messages",
                ProviderKey = "anthropic",
                DisplayName = "Anthropic",
                TransportKind = AgentTransportKind.AnthropicMessages,
            },
            ProviderId = ModelProviderIds.AnthropicMessages,
            SessionId = "session-1",
            RunId = new AgentRunId("run-1"),
            ModelId = "claude-test",
            ModelInfo = new AgentModelInfo(
                "claude-test",
                DisplayName: "Claude Test",
                Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["maxInputTokens"] = 200000L,
                }),
            SystemMessage = "System instructions",
            DeveloperInstructions = "Developer instructions",
            ReasoningEffort = AgentReasoningEffort.High,
            Conversation = conversation ??
            [
                new AgentConversationMessage(
                    AgentConversationRole.User,
                    [new AgentMessagePart.Text("hello")]),
            ],
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec(
                        "read_file",
                        "Read a file.",
                        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                    (_, _) => Task.FromResult(new AgentToolResult(true, []))),
            ],
            State = new AgentSessionState
            {
                SessionId = "session-1",
                ProtocolFamily = "anthropic-messages",
                ProviderKey = "anthropic",
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };

    private sealed class RecordingChatClient : IChatClient
    {
        public IReadOnlyList<ChatResponseUpdate> Updates { get; init; } = [];

        public List<ChatMessage>? LastMessages { get; private set; }

        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Updates.ToChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages.Select(static message => message.Clone()).ToList();
            LastOptions = options?.Clone();

            foreach (var update in Updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update.Clone();
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
